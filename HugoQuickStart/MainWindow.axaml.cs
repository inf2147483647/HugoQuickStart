using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HugoQuickStart.Models;
using HugoQuickStart.ViewModels;
using HugoQuickStart.Views;

namespace HugoQuickStart;

public partial class MainWindow : Window
{
    private MainViewModel _viewModel = null!;
    private DispatcherTimer? _bottomTimer;
    private int _dialogCount;
    private Size _lastSize = new Size();

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            _bottomTimer?.Stop();
        };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 窗口尺寸变化时立即重新锚定右下角，避免依赖 1.5s 定时器产生的延迟抖动
        LayoutUpdated += OnLayoutUpdated;
        PositionWindowBottomRight();
        StartBottomMostMaintenance();
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
            // 窗口活动或有模态对话框打开时暂停，避免打断交互
            if (!IsActive && _dialogCount == 0)
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
        WindowState = WindowState.Minimized;
    }

    private void EditButton_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleEditModeCommand.Execute(null);
    }

    private async void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        await ShowSettingsDialog();
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

    private async Task ShowSettingsDialog()
    {
        var dialog = new Window
        {
            Title = "设置",
            Width = 340,
            Height = 300,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16
        };

        var title = new TextBlock
        {
            Text = "设置",
            FontSize = 18,
            FontWeight = FontWeight.Bold
        };
        panel.Children.Add(title);

        // Auto-start toggle
        var autoStartPanel = new DockPanel();
        var autoStartText = new TextBlock
        {
            Text = "开机自启",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        DockPanel.SetDock(autoStartText, Dock.Left);
        autoStartPanel.Children.Add(autoStartText);

        var autoStartToggle = new ToggleSwitch
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            IsChecked = _viewModel.AutoStart
        };
        autoStartToggle.IsCheckedChanged += (s, e) =>
        {
            _viewModel.AutoStart = autoStartToggle.IsChecked == true;
        };
        autoStartPanel.Children.Add(autoStartToggle);
        panel.Children.Add(autoStartPanel);

        // Info text
        var infoText = new TextBlock
        {
            Text = "使用提示：\n• 单击应用图标即可启动软件\n• 点击顶部按钮可管理应用\n• 窗口固定在屏幕右下角，始终保持置底\n• 配置文件保存在程序安装目录下的 config.json",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#FF888888")),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(infoText);

        // Close button
        var closeButton = new Button
        {
            Content = "关闭",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0)
        };
        closeButton.Click += (s, e) => dialog.Close();
        panel.Children.Add(closeButton);

        dialog.Content = panel;
        await ShowModalAsync(dialog);
    }
}
