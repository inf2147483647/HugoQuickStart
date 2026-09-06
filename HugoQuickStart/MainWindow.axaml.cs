using System;
using System.Linq;
using System.Runtime.InteropServices;
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
        Closed += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            _bottomTimer?.Stop();
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

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 窗口尺寸变化时立即重新锚定右下角，避免依赖 1.5s 定时器产生的延迟抖动
        LayoutUpdated += OnLayoutUpdated;
        PositionWindowBottomRight();
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
    /// 始终保持窗口置底（位于其它普通窗口之下）。
    /// 通过定时把窗口 Z 序置于底部实现；当窗口处于活动状态时暂停，避免打断点击操作。
    /// </summary>
    private void StartBottomMostMaintenance()
    {
        if (!OperatingSystem.IsWindows())
            return;

        SendToBottom();
        _bottomTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _bottomTimer.Tick += (_, _) =>
        {
            // 窗口活动、有模态对话框或退出确认框打开时暂停，避免打断交互或把遮罩层压到其它窗口之下
            if (!IsActive && _dialogCount == 0 && !_isCloseConfirmShown)
            {
                SendToBottom();
                PositionWindowBottomRight();
            }
        };
        _bottomTimer.Start();
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

    private void SendToBottom()
    {
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;
            // HWND_BOTTOM = (IntPtr)1
            SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
        catch
        {
            // ignore
        }
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

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
