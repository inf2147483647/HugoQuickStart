using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HugoQuickStart.Models;
using HugoQuickStart.Services;
using HugoQuickStart.ViewModels;
using HugoQuickStart.Views;

namespace HugoQuickStart;

public partial class MainWindow : Window
{
    private MainViewModel _viewModel = null!;
    private DispatcherTimer? _bottomTimer;
    /// <summary>最近一次"机会式置底"的时间戳（Environment.TickCount64），用于限制抢占频率。</summary>
    private long _lastOpportunisticPushTick;
    /// <summary>置底竞争中已让出：不再由兜底定时器抢占 Z 序，只保留事件驱动的必要修正。</summary>
    private bool _bottomContentionYielded;
    /// <summary>窗口期内的机会式置底时间戳，用于判定是否存在置底竞争。</summary>
    private readonly Queue<long> _opportunisticPushTicks = new();
    private int _dialogCount;
    private bool _allowClose;
    private bool _isCloseConfirmShown;
    private Size _lastSize = new Size();
    /// <summary>当前进程 ID，用于在 Z 序扫描中跳过自己拥有的窗口（悬浮球、对话框等）。</summary>
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    /// <summary>EVENT_SYSTEM_FOREGROUND 全局钩子句柄与回调委托（委托须持有防 GC）。</summary>
    private IntPtr _foregroundEventHook;
    private WinEventDelegate? _foregroundEventProc;
    /// <summary>WndProc 子类化（参考 AdvancedTimeIsland Mode 0）：保存旧过程与委托引用防 GC。</summary>
    private IntPtr _oldWndProc;
    private IntPtr _hookedHwnd;
    private WndProcDelegate? _wndProcDelegate;
    /// <summary>SendToBottom 重入计数：防止"自己 SetWindowPos → WM_WINDOWPOSCHANGED → 钩子再压回"死循环。</summary>
    private int _inSendToBottom;
    private SeewoAssistantWindowBlocker? _seewoBlocker;
    private TrayIcon? _trayIcon;
    private FloatBallWindow? _floatBallWindow;
    /// <summary>快捷应用全局快捷键（注册/注销与 WM_HOTKEY 分发）。</summary>
    private readonly GlobalHotkeyService _hotkeyService = new();

    // ---- 自定义图标悬浮提示（静止 1s 弹出，0.25s 淡入 / 0.25s 淡出） ----
    private DispatcherTimer? _tipIdleTimer;
    private DispatcherTimer? _tipHideTimer;
    private Control? _tipTarget;
    private string? _tipPendingText;
    private Point _tipLastPos;
    private const int TipIdleMs = 1000;
    private const int TipFadeMs = 250;
    private const double TipStillThreshold = 4.0;

    /// <summary>
    /// 置底维护定时器间隔（毫秒）。仅作"事件驱动全部失效"时的低频兜底探测。
    /// 不能用高频轮询（曾用 1ms）：多个主动置底的窗口同时存在时，每个窗口都会探测到
    /// "自己下方还有别的普通窗口"而反复调用 SetWindowPos(HWND_BOTTOM)，形成 Z 序争夺战，
    /// 表现为高频闪烁（Rainmeter 官方文档明确记载同层窗口互相主动抢占会 flicker）。
    /// </summary>
    private const int BottomMaintenanceIntervalMs = 1000;

    /// <summary>两次"机会式置底"之间的最小间隔（毫秒），限制争夺战频率。</summary>
    private const int OpportunisticBottomMinIntervalMs = 1500;

    /// <summary>置底竞争判定：窗口期内机会式置底达到该次数，即认定存在多个置底窗口在互相抢占。</summary>
    private const int BottomContentionPushThreshold = 4;

    /// <summary>置底竞争的统计窗口。</summary>
    private static readonly TimeSpan BottomContentionWindow = TimeSpan.FromSeconds(10);

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Closing += OnClosing;
        Loaded += OnLoaded;
        // 界面缩放：内容整体 LayoutTransform + 窗口宽度按比例（Width 绑定 ScaledWindowWidth）。
        // SizeToContent=Height 会跟随变换后的测量高度自动调整。
        ApplyUiScale(_viewModel.UiScale);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.UiScale))
                ApplyUiScale(_viewModel.UiScale);
        };
        Closed += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            _bottomTimer?.Stop();
            UnregisterForegroundHook();
            DetachWndProcHook();
            _viewModel.HotkeysChanged -= RebindHotkeys;
            _hotkeyService.Dispose();
            _seewoBlocker?.Dispose();
            _seewoBlocker = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
            _floatBallWindow?.Close();
            _floatBallWindow = null;
        };

        InitializeTrayIcon();

        // 设置窗口内切换“拦截希沃悬浮窗”开关时，实时同步拦截器启停
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.BlockSeewoAssistantWindow))
            {
                // 重新启用时清除上一次的"拦截失效"提示，允许重新尝试（拦截器同时会清空尝试记录）
                _viewModel.SeewoInterceptFailed = false;

                if (_seewoBlocker != null)
                    _seewoBlocker.Enabled = _viewModel.BlockSeewoAssistantWindow;
            }
        };
    }

    /// <summary>
    /// 应用界面缩放：根元素外包 LayoutTransformControl，其 LayoutTransform 参与测量，
    /// 窗口高度随缩放后的内容自动适配（SizeToContent=Height），宽度由 ScaledWindowWidth 绑定按比例。
    /// 1.00 时清除变换，保持原始渲染精度。
    /// </summary>
    private void ApplyUiScale(double scale)
    {
        if (RootScale == null)
            return;

        RootScale.LayoutTransform = Math.Abs(scale - 1.0) < 1e-9
            ? null
            : new ScaleTransform(scale, scale);
    }

    // ================= 图标悬浮提示 =================

    /// <summary>进入图标：开始 1s 静止计时；切换到其它图标一律重新计时（不立即复用）。</summary>
    private void AppIcon_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control)
            return;

        var item = control.DataContext as AppItem;
        var text = item?.ToolTipText;
        if (string.IsNullOrEmpty(text))
            return;

        _tipLastPos = e.GetCurrentPoint(null).Position;
        _tipPendingText = text;

        // 同一图标边缘抖动重新进入且提示仍显示中：取消淡出直接保留，不重新计时
        if (AppTipPopup.IsOpen && ReferenceEquals(_tipTarget, control) && AppTipBorder.Opacity > 0.9)
        {
            _tipHideTimer?.Stop();
            _tipIdleTimer?.Stop();
            return;
        }

        _tipTarget = control;

        // 切换图标：旧提示保持淡出流程，新提示重新计 1s
        _tipIdleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TipIdleMs) };
        _tipIdleTimer.Stop();
        _tipIdleTimer.Tick -= TipIdleTimer_Tick;
        _tipIdleTimer.Tick += TipIdleTimer_Tick;
        _tipIdleTimer.Start();
    }

    private void TipIdleTimer_Tick(object? sender, EventArgs e)
    {
        _tipIdleTimer?.Stop();
        if (_tipTarget == null || string.IsNullOrEmpty(_tipPendingText))
            return;

        // 文本与锚定在弹出时刻才应用，避免淡出途中被提前替换
        AppTipText.Text = _tipPendingText;
        AppTipPopup.PlacementTarget = _tipTarget;
        AppTipPopup.IsOpen = true;
        AppTipBorder.Opacity = 1; // OpacityTransition 0.25s 淡入
    }

    /// <summary>移动超过阈值视为"未静止"：弹出前重置 1s 计时；已弹出则保持显示（静止仅约束弹出时机）。</summary>
    private void AppIcon_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || !ReferenceEquals(control, _tipTarget))
            return;

        var pos = e.GetCurrentPoint(null).Position;
        if (Math.Abs(pos.X - _tipLastPos.X) <= TipStillThreshold &&
            Math.Abs(pos.Y - _tipLastPos.Y) <= TipStillThreshold)
            return;

        _tipLastPos = pos;

        if (!AppTipPopup.IsOpen)
        {
            _tipIdleTimer?.Stop();
            _tipIdleTimer?.Start();
        }
    }

    private void AppIcon_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(sender as Control, _tipTarget))
            return;

        _tipIdleTimer?.Stop();
        HideAppTip();
    }

    /// <summary>点击启动应用时立即收起提示（指针可能仍停留在图标上，不会触发 Exited）。</summary>
    private void AppIcon_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(sender as Control, _tipTarget))
            return;

        _tipIdleTimer?.Stop();
        if (AppTipPopup.IsOpen)
            HideAppTip();
    }

    /// <summary>淡出 0.25s 后再真正关闭 Popup（Popup 不支持淡出，动画结束后才收起窗口）。</summary>
    private void HideAppTip()
    {
        AppTipBorder.Opacity = 0; // OpacityTransition 0.25s 淡出

        _tipHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TipFadeMs) };
        _tipHideTimer.Stop();
        _tipHideTimer.Tick -= TipHideTimer_Tick;
        _tipHideTimer.Tick += TipHideTimer_Tick;
        _tipHideTimer.Start();
    }

    private void TipHideTimer_Tick(object? sender, EventArgs e)
    {
        _tipHideTimer?.Stop();
        AppTipPopup.IsOpen = false;
        // 若已切到新图标且正在重新计时（淡出与 1s 计时并行），保留目标不清空
        if (_tipIdleTimer?.IsEnabled != true)
            _tipTarget = null;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 窗口尺寸变化时立即重新锚定右下角，避免依赖定时器产生的延迟抖动
        LayoutUpdated += OnLayoutUpdated;
        PositionWindowBottomRight();

        // 事件驱动置底：失焦时立即置底；被本窗主动激活时不打断交互。
        // 外部无焦点事件的抬层由 EVENT_SYSTEM_FOREGROUND 钩子 + 定时器真实 Z 序探测兜底。
        Deactivated += (_, _) =>
        {
            // 失焦时收起悬浮提示，避免 Popup 残留在屏幕上
            _tipIdleTimer?.Stop();
            if (AppTipPopup.IsOpen)
                HideAppTip();

            if (_dialogCount == 0 && !_isCloseConfirmShown)
                SendToBottom();
        };

        StartBottomMostMaintenance();
        StartSeewoBlocker();
        InitializeHotkeys();
        RunStartupBackupIfEnabled();
        LogService.Info("初始化", "主界面已加载并显示");
    }

    /// <summary>
    /// 启动时按需自动备份配置（只备份 config.json：设置项与图标列表）。
    /// 仅当备份总开关开启时执行；距上次自动备份已满设定周期（或从未备份过）才创建，
    /// 随后按数量上限清理最旧的自动备份（0 表示不限制）。手动备份不在此路径。
    /// </summary>
    private void RunStartupBackupIfEnabled()
    {
        if (!_viewModel.BackupEnabled)
            return;

        ConfigBackupService.RunStartupBackup(
            (int)Math.Round(_viewModel.BackupIntervalDays),
            (int)Math.Round(_viewModel.BackupMaxCount));
    }

    /// <summary>
    /// 初始化快捷应用全局快捷键：绑定窗口句柄与触发回调、按当前配置注册，
    /// 并订阅配置保存事件以便列表/快捷键变更后自动重建注册。
    /// </summary>
    private void InitializeHotkeys()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            LogService.Warn("快捷键", "未取得窗口句柄，全局快捷键未注册");
            return;
        }

        // 触发统一走图标点击的启动路径（含冷却、备选路径、协议提示、状态条反馈）
        _hotkeyService.Attach(hwnd, app => _viewModel.LaunchAppCommand.Execute(app));
        _viewModel.HotkeysChanged += RebindHotkeys;
        RebindHotkeys();
    }

    /// <summary>
    /// 重建全部全局热键注册。注册失败（组合被占用、权限不足等）时给出提示，
    /// 但不影响其余热键（例如 Ctrl+F1 常被驱动或其它常驻软件占用）。
    /// </summary>
    private void RebindHotkeys()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var failures = _hotkeyService.Rebind(_viewModel.QuickEntries.Concat(_viewModel.XiwoApps));

        if (failures.Count > 0)
        {
            LogService.Warn("快捷键", $"{failures.Count} 项快捷键未生效：{string.Join("；", failures)}");
            _viewModel.ShowStatus(failures.Count == 1
                ? failures[0]
                : $"{failures[0]}（另有 {failures.Count - 1} 项快捷键未生效，详见日志）");
        }
    }

    /// <summary>
    /// 拦截窗口关闭（任务栏“关闭窗口”、Alt+F4 等）：弹出全屏 ContentDialog 确认，
    /// 用户选择“退出”才真正关闭；选择“取消”则留在右下角继续运行。
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        // 默认先阻止本次关闭，等待用户确认
        e.Cancel = true;

        // 已有模态子窗口（如设置窗口）在交互，或确认框已在显示：忽略本次关闭请求
        if (_dialogCount > 0 || _isCloseConfirmShown)
            return;

        _isCloseConfirmShown = true;
        try
        {
            var dialog = new FAContentDialog
            {
                Title = "退出快捷启动？",
                Content = "关闭后快捷启动将停止运行，不再驻留屏幕右下角。确定要退出程序吗？",
                PrimaryButtonText = "退出",
                CloseButtonText = "取消",
                DefaultButton = FAContentDialogButton.Close
            };

            var result = await dialog.ShowAsync(this);
            if (result == FAContentDialogResult.Primary)
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _isCloseConfirmShown = false;
        }
    }

    /// <summary>按配置启动“拦截希沃服务助手右下角悬浮窗”。</summary>
    private void StartSeewoBlocker()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (_seewoBlocker == null)
        {
            _seewoBlocker = new SeewoAssistantWindowBlocker(_viewModel.BlockSeewoAssistantWindow);
            // 普通权限连续失败达阈值后，拦截器上报目标句柄 → 走提权链路（UAC → 管理员 → SYSTEM）关闭。
            _seewoBlocker.EscalationRequired += hwnd => _ = EscalateHideAsync(hwnd);
        }
        else
            _seewoBlocker.Enabled = _viewModel.BlockSeewoAssistantWindow;
    }

    /// <summary>
    /// 提权关闭希沃管家悬浮窗（普通权限连续失败后触发）。
    /// 用 ShellExecute 的 runas 启动本程序的管理员实例（弹一次 UAC 确认），
    /// 该实例再复制 SYSTEM 令牌、以 SYSTEM 身份启动工作进程完成隐藏。
    /// 因 ShellExecute 拿不到子进程退出码，最终以"窗口是否真的不可见"为准确认结果。
    /// </summary>
    private async Task EscalateHideAsync(IntPtr hwnd)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                LogService.Warn("提权拦截", "无法获取当前程序路径，提权取消");
                return;
            }

            LogService.Warn("提权拦截",
                $"普通权限隐藏失败，正在请求提权（将弹出 UAC 确认）关闭窗口 hwnd=0x{hwnd.ToInt64():X}");

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--hide-window {hwnd.ToInt64()} --elevate",
                UseShellExecute = true,   // runas 需要 ShellExecute
                Verb = "runas"            // 触发 UAC 提权
            };

            try
            {
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                // 用户在 UAC 对话框选择"否"时会抛 Win32Exception(1223)
                LogService.Warn("提权拦截", $"提权请求未完成：{ex.GetType().Name}: {ex.Message}");
                SetSeewoInterceptFailed(true);
                return;
            }

            var hidden = await WaitUntilWindowHiddenAsync(hwnd, TimeSpan.FromSeconds(20));
            if (hidden)
            {
                LogService.Info("提权拦截", $"已通过提权（SYSTEM）关闭希沃悬浮窗 hwnd=0x{hwnd.ToInt64():X}");
                SetSeewoInterceptFailed(false);
            }
            else
            {
                LogService.Error("提权拦截",
                    $"提权后窗口仍可见（hwnd=0x{hwnd.ToInt64():X}）；可直接改用希沃“管家助手显隐”开关从源头关闭。");
                SetSeewoInterceptFailed(true);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("提权拦截", $"提权关闭窗口异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>轮询等待目标窗口变为不可见（提权工作进程完成后生效）。</summary>
    private static async Task<bool> WaitUntilWindowHiddenAsync(IntPtr hwnd, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsWindowVisible(hwnd))
                return true;
            await Task.Delay(250);
        }

        return !IsWindowVisible(hwnd);
    }

    /// <summary>在 UI 线程上更新"拦截失效"提示（提权后仍未能关闭时提示改用希沃官方开关）。</summary>
    private void SetSeewoInterceptFailed(bool failed) =>
        Dispatcher.UIThread.Post(() => _viewModel.SeewoInterceptFailed = failed);

    /// <summary>
    /// SizeToContent 下内容高度变化（如编辑模式切换、增删应用）会引起窗口尺寸变化，
    /// 在此立即校准位置，使窗口稳定锚定在屏幕右下角而非随高度向下方扩展。
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        if (Bounds.Width != _lastSize.Width || Bounds.Height != _lastSize.Height)
        {
            _lastSize = new Size(Bounds.Width, Bounds.Height);
            PositionWindowBottomRight();
        }
    }

    /// <summary>将窗口定位到屏幕工作区右下角（固定位置，不可移动）。</summary>
    private void PositionWindowBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;

        var workingArea = screen.WorkingArea;
        var scale = screen.Scaling;

        // 使用实际渲染尺寸（SizeToContent 后以 Bounds 为准），避免固定 Height 导致的偏移
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;

        var x = workingArea.X + workingArea.Width - (int)(width * scale) - (int)(20 * scale);
        var y = workingArea.Y + workingArea.Height - (int)(height * scale) - (int)(20 * scale);

        // 幂等：位置未变化则不写。维护 Tick 频率很高（1ms），若每 Tick 都赋 Position，
        // 等价于持续 SetWindowPos 移动窗口 → 与 DWM 合成争抢，造成闪烁/抖动。
        var target = new PixelPoint(x, y);
        if (Position == target)
            return;

        Position = target;
    }

    /// <summary>
    /// 保持窗口置底（位于其它普通窗口之下）。移植 AdvancedTimeIsland 的四层防线，
    /// 每层都"先探测真实 Z 序、确认被抬起才置底"，因此静止期间零 SetWindowPos、不闪烁：
    ///  1) WS_EX_NOACTIVATE（<see cref="ApplyNoActivateStyle"/>）：从源头阻断"激活 → 被系统强制提升"；
    ///  2) WndProc 子类化（<see cref="AttachWndProcHook"/>）：本窗 Z 序被改时即时压回，
    ///     可捕获不产生前台/焦点事件的重排；
    ///  3) EVENT_SYSTEM_FOREGROUND 钩子（<see cref="RegisterForegroundHook"/>）：任意窗口切前台时即时压回；
    ///  4) 低频兜底定时器：仅在前三类事件都没覆盖到时兜底（<see cref="SendToBottomOpportunistically"/>
    ///     带频率限制与"置底竞争"让出，避免与其它置底窗口互相抢占 Z 序而闪烁）。
    /// 不信任"已置底"布尔标志——它会在无焦点事件的外部抬层后永久失真，导致窗口赖在前面不回落。
    /// </summary>
    private void StartBottomMostMaintenance()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // 0) 扩展样式自愈：置底窗口置 WS_EX_NOACTIVATE，阻断"激活 → 系统强制提升 z-order"这一
        //    置底失效主因（参考 AdvancedTimeIsland ApplyWindowLayer 第一步）。
        ApplyNoActivateStyle();

        SendToBottom();
        RegisterForegroundHook();
        // 消息驱动层（参考 AdvancedTimeIsland Mode 0）：子类化 WndProc 拦截 WM_WINDOWPOSCHANGED，
        // 捕获"不产生前台/焦点事件"的外部抬层（Shell 重排、其它窗口最小化/还原、Alt+Tab 等）。
        // 此前该方法只有定义与解挂、从未挂载，导致唯一能即时发现此类抬层的路径全程失效——
        // 只剩 2s 定时器兜底，"置底失效"由此而来。
        AttachWndProcHook();

        _bottomTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(BottomMaintenanceIntervalMs)
        };
        _bottomTimer.Tick += (_, _) =>
        {
            // 窗口活动、有模态对话框或退出确认框打开时暂停，避免打断交互或把遮罩层压到其它窗口之下
            if (IsActive || _dialogCount > 0 || _isCloseConfirmShown)
                return;

            // 窗口不可见（最小化到托盘）时无 Z 序可维护：直接跳过。
            // 否则隐藏窗口的探测恒为"被抬起"，会在高频 Tick 下持续做无谓的 SetWindowPos。
            if (!IsVisible)
                return;

            // 样式自愈：Avalonia 在 Show/属性变更时会重写 GWL_EXSTYLE 抹掉 NOACTIVATE，
            // 需持续补写（值相同不写，不触发 DWM 重评估，故高频 Tick 下无额外开销）。
            ApplyNoActivateStyle();

            // 真实探测：只有确实被抬起来才重设 Z 序。这里走"机会式置底"——
            // 带最小间隔与置底竞争让出，避免与其它置底窗口互相反复抢占造成闪烁。
            if (IsRaisedAboveForeignWindow())
                SendToBottomOpportunistically();

            // 位置校准：常态由 LayoutUpdated 在窗口尺寸变化时立即重锚，
            // 这里兜底"工作区/DPI 变化"（任务栏增删、分辨率切换）；幂等，未变化不写。
            PositionWindowBottomRight();
        };
        _bottomTimer.Start();
    }

    /// <summary>
    /// 沿 Z 序向下扫描本窗口之下的窗口（GetWindow GW_HWNDNEXT）。
    /// 若存在"可见、非本进程、非置顶（WS_EX_TOPMOST）"的窗口，说明本窗被抬到了它们之上，
    /// 即已不在最底层。置顶窗口永远在普通层之上、不会出现在本窗之下，故跳过即可。
    /// 已在最底层时本窗之下没有普通窗口，返回 false，定时器因此不会调用 SetWindowPos（无闪烁）。
    /// 注意：正确置底时本窗之下仍有桌面 Shell 窗口（Progman/WorkerW，可见且非置顶），
    /// 它们是 Z 序链最底部的特殊存在，必须按类名排除，否则永远判定"被抬起"→ 周期性重设 → 闪烁。
    /// 同理必须排除"不可激活窗口（WS_EX_NOACTIVATE）"——它们是与本窗同类的桌面面板/覆盖层
    /// （其它置底悬浮窗即属此类），本窗位于其上方无害；若把它们当作"被抬起"的依据，
    /// 多个置底窗口会互相判定、反复压底，形成 Z 序争夺战而持续闪烁。
    /// </summary>
    private bool IsRaisedAboveForeignWindow()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return false;

        try
        {
            var below = GetWindow(hwnd, GW_HWNDNEXT);
            while (below != IntPtr.Zero)
            {
                if (IsWindowVisible(below))
                {
                    GetWindowThreadProcessId(below, out var pid);
                    if (pid != _ownPid && !IsDesktopShellWindow(below))
                    {
                        var exStyle = (int)(long)GetWindowLongPtr(below, GWL_EXSTYLE);
                        // 普通可交互窗口位于本窗之下 → 本窗确实被抬起来了
                        if ((exStyle & WS_EX_TOPMOST) == 0 && (exStyle & WS_EX_NOACTIVATE) == 0)
                            return true;
                    }
                }
                below = GetWindow(below, GW_HWNDNEXT);
            }
            return false;
        }
        catch
        {
            // 探测失败按"需要重新置底"处理，宁可多压一次也不让窗口赖在前面
            return true;
        }
    }

    /// <summary>
    /// 机会式置底：仅由低频兜底定时器调用，带最小间隔与"置底竞争"让出机制。
    /// 多个都主动置底的窗口同时存在时，每个窗口都可能探测到"自己下方还有别的普通窗口"，
    /// 若各自无条件反复压底就会形成 Z 序争夺战（Rainmeter 官方文档明确记载同层窗口
    /// 互相主动抢占会 flicker）。因此这里：①限制抢占频率；②短期内反复抢占即认定存在
    /// 竞争，永久让出周期抢占，只保留事件驱动路径（前台切换 / 本窗 Z 序变化 / 失焦）
    /// 的必要修正——那些路径由真实转换触发，不会自激。
    /// </summary>
    private void SendToBottomOpportunistically()
    {
        if (_bottomContentionYielded)
            return;

        var now = Environment.TickCount64;
        if (now - _lastOpportunisticPushTick < OpportunisticBottomMinIntervalMs)
            return;

        _lastOpportunisticPushTick = now;
        SendToBottom();

        // 统计窗口期内的抢占次数，超出阈值即认定正在与其它置底窗口互相抢占
        _opportunisticPushTicks.Enqueue(now);
        var windowMs = (long)BottomContentionWindow.TotalMilliseconds;
        while (_opportunisticPushTicks.Count > 0 && now - _opportunisticPushTicks.Peek() > windowMs)
            _opportunisticPushTicks.Dequeue();

        if (_opportunisticPushTicks.Count >= BottomContentionPushThreshold)
        {
            _bottomContentionYielded = true;
            _opportunisticPushTicks.Clear();
            LogService.Warn("置底",
                "检测到多个窗口同时在抢占桌面底层（置底竞争），已停止周期性置底，改由事件驱动维护以避免闪烁");
        }
    }

    /// <summary>桌面 Shell 窗口类名：始终位于 Z 序链最底部，不代表"本窗被抬起"。</summary>
    private static bool IsDesktopShellWindow(IntPtr hwnd)
    {
        var buffer = new char[64];
        var len = GetClassName(hwnd, buffer, buffer.Length);
        if (len <= 0)
            return false;
        var cls = new string(buffer, 0, len);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>以模态方式弹出对话框并返回结果（跟踪打开数量，期间暂停置底）。</summary>
    private async Task<TResult?> ShowModalAsync<TResult>(Window dialog) where TResult : struct
    {
        _dialogCount++;
        try
        {
            return await dialog.ShowDialog<TResult?>(this);
        }
        finally
        {
            _dialogCount--;
        }
    }

    /// <summary>以模态方式弹出无返回值对话框。</summary>
    private async Task ShowModalAsync(Window dialog)
    {
        _dialogCount++;
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            _dialogCount--;
        }
    }

    /// <summary>
    /// 把窗口 Z 序压到最底层。调用方（定时器 tick / 前台钩子 / WndProc 钩子）只在探测到
    /// "确实被抬起"或焦点切换时才调用，因此不会周期性重设层级造成闪烁。
    /// 移植 AdvancedTimeIsland FloatingScheduleService.ApplyWindowLayer 的"彻底置底"三要素：
    ///  1) WS_EX_NOACTIVATE：点击不激活 → Windows 永不因激活强制提升本窗（被拉上来的主因）。
    ///     仅当值变化时写 GWL_EXSTYLE，避免 DWM 反复重评估合成分层造成闪烁。
    ///  2) 完整 SWP 标志（对齐 ClassIsland Bottommost）：NOSENDCHANGING/NOOWNERZORDER/NOREPOSITION。
    ///  3) _inSendToBottom 重入计数：WndProc 钩子据此区分"自己压底引发的 WM_WINDOWPOSCHANGED"
    ///     与"外部抬层"，防止 自己Apply→消息→再Apply 死循环。
    /// </summary>
    private void SendToBottom()
    {
        Interlocked.Increment(ref _inSendToBottom);
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;

            // 1) 幂等添加 WS_EX_NOACTIVATE（值未变化则不写，防 DWM 重合成闪烁）
            ApplyNoActivateStyle();

            // HWND_BOTTOM = (IntPtr)1
            SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
                | SWP_NOSENDCHANGING | SWP_NOOWNERZORDER | SWP_NOREPOSITION);
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Decrement(ref _inSendToBottom);
        }
    }

    /// <summary>
    /// 幂等地给窗口补上 WS_EX_NOACTIVATE 扩展样式（值未变化则不写 GWL_EXSTYLE）。
    /// 置底窗口一旦被激活，Windows 会强制提升其 z-order，即使高频重设 Z 序也压不住；
    /// 置此位后点击/悬停不激活本窗，从源头杜绝"被激活 → 被提升"。
    /// 与"是否需要置底"解耦、单独调用：Avalonia 在 Show 流程/窗口属性变更时会重写
    /// GWL_EXSTYLE 抹掉该位，因此需由维护 Tick 持续补写实现自愈；
    /// 因"值相同不写"，高频调用不会触发 DWM 重评估合成分层，故不产生闪烁。
    /// </summary>
    private void ApplyNoActivateStyle()
    {
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;

            var exStyle = (int)(long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            var target = exStyle | WS_EX_NOACTIVATE;
            if (target != exStyle)
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)target);
        }
        catch
        {
            // ignore
        }
    }

    #region 置底相关 Win32 互操作

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    // 注意：SWP_NOREPOSITION 与 SWP_NOOWNERZORDER 同值 0x0200（0x0100 是 SWP_NOCOPYBITS）。
    // 参考 AdvancedTimeIsland FloatingScheduleService 注释，两处对齐 ClassIsland Bottommost 实现。
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOREPOSITION = 0x0200;
    private const uint SWP_NOSENDCHANGING = 0x0400;

    private const int GWL_EXSTYLE = -20;
    private const int GWLP_WNDPROC = -4;
    private const int WS_EX_TOPMOST = 0x00000008;
    /// <summary>★ 彻底置底关键位：点击/悬停不激活窗口 → Windows 不会因激活而强制提升其 Z 序
    /// （激活提升是"置底后又被拉上来"的主因，即使高频重设也压不住）。</summary>
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint GW_HWNDNEXT = 2;

    private const uint WM_WINDOWPOSCHANGED = 0x0047;

    /// <summary>已注册的全局热键被按下（wParam = 热键 ID）。</summary>
    private const uint WM_HOTKEY = 0x0312;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd,
        uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// WndProc 子类化（移植 AdvancedTimeIsland AttachTopmostRefreshAv Mode 0）：
    /// 拦截本窗的 WM_WINDOWPOSCHANGED，当 Z 序被"外部"改变（flags 不含 SWP_NOZORDER
    /// 且非自己 SendToBottom 引发）时，反应式压回底层——这是对"无焦点事件的抬层"
    /// （Shell 重排、其它窗口最小化/还原连带调整）最直接的检测路径。
    /// </summary>
    private void AttachWndProcHook()
    {
        if (_oldWndProc != IntPtr.Zero)
            return;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            _wndProcDelegate = WndProcHook;   // 存字段防 GC
            _oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
            _hookedHwnd = hwnd;
        }
        catch
        {
            _wndProcDelegate = null;
            _oldWndProc = IntPtr.Zero;
            _hookedHwnd = IntPtr.Zero;
        }
    }

    private void DetachWndProcHook()
    {
        if (_oldWndProc != IntPtr.Zero && _hookedHwnd != IntPtr.Zero)
        {
            try
            {
                SetWindowLongPtr(_hookedHwnd, GWLP_WNDPROC, _oldWndProc);
            }
            catch
            {
                // ignore
            }
        }
        _oldWndProc = IntPtr.Zero;
        _hookedHwnd = IntPtr.Zero;
        _wndProcDelegate = null;
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 防御：任何异常都不能吞掉旧 WndProc 调用，否则窗口失去消息泵（卡死）
        try
        {
            if (msg == WM_WINDOWPOSCHANGED && lParam != IntPtr.Zero)
            {
                // WINDOWPOS { hwnd, hwndInsertAfter, x, y, cx, cy, flags }
                // flags 偏移 = IntPtr.Size*2 + sizeof(int)*4
                var flagsOffset = IntPtr.Size * 2 + 16;
                var flags = (uint)Marshal.ReadInt32(lParam, flagsOffset);

                // 含 Z 序变化（非 SWP_NOZORDER）且不是自己 SendToBottom 引发的 → 外部抬层
                if ((flags & SWP_NOZORDER) == 0 && Volatile.Read(ref _inSendToBottom) == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsActive || _dialogCount > 0 || _isCloseConfirmShown)
                            return;
                        if (IsRaisedAboveForeignWindow())
                            SendToBottom();
                    }, DispatcherPriority.Background);
                }
            }
            else if (msg == WM_HOTKEY)
            {
                // 有模态对话框（如在编辑对话框中录入快捷键）或退出确认框时不触发，
                // 否则用户录入按键组合的瞬间就会把对应应用启动起来。
                if (_dialogCount == 0 && !_isCloseConfirmShown)
                    _hotkeyService.HandleMessage(wParam.ToInt32());
            }
        }
        catch
        {
            // ignore
        }

        if (_oldWndProc != IntPtr.Zero)
            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 注册 EVENT_SYSTEM_FOREGROUND 全局钩子：任意窗口切换到前台时，
    /// 若本窗此刻被抬起来了就立即反应式置底（对齐 ClassIsland 的
    /// RegisterForegroundWindowChangedEvent 与 Windhawk keep-rainmeter-always-bottom 方案）。
    /// 回调在 UI 线程消息循环上派发（WINEVENT_OUTOFCONTEXT），可直接操作窗口。
    /// </summary>
    private void RegisterForegroundHook()
    {
        if (_foregroundEventHook != IntPtr.Zero)
            return;

        _foregroundEventProc = OnWinEvent;
        _foregroundEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundEventProc,
            0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // 交互期（本窗活动 / 有模态框 / 确认框）不打断用户
        if (IsActive || _dialogCount > 0 || _isCloseConfirmShown)
            return;

        // 前台切换到自己拥有的窗口时无需处理
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid)
            return;

        // 已在最底层则不动，避免每次 alt-tab 都无谓地重设 Z 序
        if (IsRaisedAboveForeignWindow())
            SendToBottom();
    }

    private void UnregisterForegroundHook()
    {
        if (_foregroundEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundEventHook);
            _foregroundEventHook = IntPtr.Zero;
        }
        _foregroundEventProc = null;
    }

    #endregion

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        HideWindowToTray();
    }

    /// <summary>标题栏“关机”按钮：退出程序（复用统一退出流程，含确认对话框）。</summary>
    private void ShutdownButton_Click(object? sender, RoutedEventArgs e)
    {
        ExitProgram();
    }

    /// <summary>初始化系统托盘图标：窗口隐藏时仍在任务栏通知区驻留，左键点击还原主界面。</summary>
    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://HugoQuickStart/Assets/app.ico"))),
                ToolTipText = "快捷启动",
                IsVisible = true
            };

            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("显示主界面");
            showItem.Click += (_, _) => RestoreMainWindow();
            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += (_, _) => ExitProgram();
            menu.Items.Add(showItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);
            _trayIcon.Menu = menu;

            // 左键单击托盘图标同样还原主界面
            _trayIcon.Clicked += (_, _) => RestoreMainWindow();

            if (Application.Current is not null)
                TrayIcon.SetIcons(Application.Current, new TrayIcons { _trayIcon });
        }
        catch
        {
            // 托盘初始化失败不阻塞主界面正常运行
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }

    /// <summary>
    /// 还原并显示主界面，隐藏悬浮球。
    /// 不调用 Activate()：本窗是"始终置底"的桌面面板，激活会把它抬到其它窗口之上，
    /// 且活动期间置底守卫会暂停维护，导致窗口停在高层级。Show() 本身也会临时抬层，
    /// 因此显示后立即压回底层，保持与正常运行一致的 Z 序。
    /// </summary>
    private void RestoreMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        PositionWindowBottomRight();
        HideFloatBall();
        SendToBottom();
    }

    /// <summary>最小化到托盘：隐藏主窗口并显示右下角悬浮球。</summary>
    private void HideWindowToTray()
    {
        Hide();
        ShowFloatBall();
    }

    /// <summary>显示右下角悬浮球并锚定位置。</summary>
    private void ShowFloatBall()
    {
        if (_floatBallWindow == null)
        {
            _floatBallWindow = new FloatBallWindow();
            _floatBallWindow.Clicked += RestoreMainWindow;
            _floatBallWindow.Closed += (_, _) => _floatBallWindow = null;
        }

        PositionFloatBall();
        _floatBallWindow.Show();
        _floatBallWindow.Activate();
    }

    /// <summary>隐藏悬浮球（还原主界面或退出时调用）。</summary>
    private void HideFloatBall()
    {
        _floatBallWindow?.Hide();
    }

    /// <summary>将悬浮球锚定到屏幕工作区右下角。</summary>
    private void PositionFloatBall()
    {
        if (_floatBallWindow == null)
            return;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null)
            return;

        var workingArea = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = _floatBallWindow.Width;
        var height = _floatBallWindow.Height;

        var x = workingArea.X + workingArea.Width - (int)(width * scale) - (int)(20 * scale);
        var y = workingArea.Y + workingArea.Height - (int)(height * scale) - (int)(20 * scale);

        _floatBallWindow.Position = new PixelPoint(x, y);
    }

    /// <summary>统一退出路径：若主窗被隐藏则先还原以让确认对话框有可见宿主，再走关闭确认流程。</summary>
    private void ExitProgram()
    {
        if (!IsVisible)
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            PositionWindowBottomRight();
        }

        HideFloatBall();
        Close();
    }

    private void EditButton_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleEditModeCommand.Execute(null);
    }

    private SettingsWindow? _settingsWindow;

    /// <summary>编辑模式下弹出"添加预设"选择窗口，把选中的内置预设加入对应列表。</summary>
    private async void AddPreset_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new PickPresetDialog();
        var ok = await ShowModalAsync<bool>(dialog);
        if (ok != true)
            return;
        if (dialog.SelectedPreset is not AppItem preset)
            return;

        var target = preset.Category == AppPresets.CategoryXiwo
            ? _viewModel.XiwoApps
            : _viewModel.QuickEntries;

        if (target.Any(x => string.Equals(x.Path, preset.Path, StringComparison.OrdinalIgnoreCase)))
        {
            _viewModel.ShowStatus($"「{preset.Name}」已存在，未重复添加");
            return;
        }

        target.Add(preset);
        _viewModel.SaveConfig();
        _viewModel.ShowStatus($"已添加「{preset.Name}」");
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        // 非模态打开设置窗口：主界面仍可点击，不被阻塞
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_viewModel);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        // 若被最小化则先还原，再前置窗口
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }
        _settingsWindow.Activate();
    }

    private async void QuickEntryEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is AppItem appItem)
        {
            var result = await ShowModalAsync<bool>(new EditAppDialog(appItem));
            if (result == true)
            {
                _viewModel.SaveConfig();
                LogService.Info("设置", $"修改快捷入口「{appItem.Name}」");
            }
        }
    }

    private async void XiwoAppEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is AppItem appItem)
        {
            var result = await ShowModalAsync<bool>(new EditAppDialog(appItem));
            if (result == true)
            {
                _viewModel.SaveConfig();
                LogService.Info("设置", $"修改希沃应用「{appItem.Name}」");
            }
        }
    }

    private async void AddQuickEntry_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new EditAppDialog();
        var result = await ShowModalAsync<bool>(dialog);
        if (result == true)
        {
            var newApp = dialog.AppItem;
            newApp.Category = "快捷入口";
            _viewModel.QuickEntries.Add(newApp);
            _viewModel.SaveConfig();
            LogService.Info("设置", $"新增快捷入口「{newApp.Name}」");
        }
    }

    private async void AddXiwoApp_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new EditAppDialog();
        var result = await ShowModalAsync<bool>(dialog);
        if (result == true)
        {
            var newApp = dialog.AppItem;
            newApp.Category = "希沃软件";
            _viewModel.XiwoApps.Add(newApp);
            _viewModel.SaveConfig();
            LogService.Info("设置", $"新增希沃应用「{newApp.Name}」");
        }
    }
}
