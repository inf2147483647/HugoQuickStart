using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 窗口尺寸变化时立即重新锚定右下角，避免依赖定时器产生的延迟抖动
        LayoutUpdated += OnLayoutUpdated;
        PositionWindowBottomRight();

        // 事件驱动置底：失焦时立即置底；被本窗主动激活时不打断交互。
        // 外部无焦点事件的抬层由 EVENT_SYSTEM_FOREGROUND 钩子 + 定时器真实 Z 序探测兜底。
        Deactivated += (_, _) =>
        {
            if (_dialogCount == 0 && !_isCloseConfirmShown)
                SendToBottom();
        };

        StartBottomMostMaintenance();
        StartSeewoBlocker();
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
            _seewoBlocker = new SeewoAssistantWindowBlocker(_viewModel.BlockSeewoAssistantWindow);
        else
            _seewoBlocker.Enabled = _viewModel.BlockSeewoAssistantWindow;
    }

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

        Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// 保持窗口置底（位于其它普通窗口之下）。
    /// 不再信任"已置底"布尔标志——外部行为（Alt+Tab、其它窗口最小化/还原、
    /// 新窗口插入）可能在没有 Activated 事件的情况下把本窗抬起来，标志会永久失真。
    /// 改为：定时器 tick 时通过 GetWindow(GW_HWNDPREV) 向上扫描**真实 Z 序**，
    /// 仅当确实被抬到其它进程的非置顶窗口之上时才重新置底（无变化则零 SetWindowPos 调用，不闪烁）。
    /// 另注册 EVENT_SYSTEM_FOREGROUND 全局钩子，在任意窗口切换到前台时即时反应式置底。
    /// </summary>
    private void StartBottomMostMaintenance()
    {
        if (!OperatingSystem.IsWindows())
            return;

        SendToBottom();
        RegisterForegroundHook();

        _bottomTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _bottomTimer.Tick += (_, _) =>
        {
            // 窗口活动、有模态对话框或退出确认框打开时暂停，避免打断交互或把遮罩层压到其它窗口之下
            if (IsActive || _dialogCount > 0 || _isCloseConfirmShown)
                return;

            // 真实探测：只有确实被抬起来才重设 Z 序；否则仅校准位置
            if (IsRaisedAboveForeignWindow())
                SendToBottom();
            else
                PositionWindowBottomRight();
        };
        _bottomTimer.Start();
    }

    /// <summary>
    /// 沿 Z 序向下扫描本窗口之下的窗口（GetWindow GW_HWNDNEXT）。
    /// 若存在"可见、非本进程、且非置顶（WS_EX_TOPMOST）"的窗口，说明本窗被抬到了它们之上，
    /// 即已不在最底层。置顶窗口永远在普通层之上、不会出现在本窗之下，故跳过即可。
    /// 已在最底层时本窗之下没有普通窗口，返回 false，定时器因此不会调用 SetWindowPos（无闪烁）。
    /// 注意：正确置底时本窗之下仍有桌面 Shell 窗口（Progman/WorkerW，可见且非置顶），
    /// 它们是 Z 序链最底部的特殊存在，必须按类名排除，否则永远判定"被抬起"→ 周期性重设 → 闪烁。
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
                        if ((exStyle & WS_EX_TOPMOST) == 0)
                            return true;    // 本窗之下还有其它进程的普通窗口 → 本窗不在底层
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
            try
            {
                var exStyle = (int)(long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                var target = exStyle | WS_EX_NOACTIVATE;
                if (target != exStyle)
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)target);
            }
            catch
            {
                // ignore
            }

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

    /// <summary>还原并前置主界面，隐藏悬浮球。</summary>
    private void RestoreMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        PositionWindowBottomRight();
        HideFloatBall();
        Activate();
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
        }
    }
}
