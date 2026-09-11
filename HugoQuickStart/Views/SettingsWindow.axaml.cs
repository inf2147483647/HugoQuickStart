using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HugoQuickStart.Behaviors;
using HugoQuickStart.Services;
using HugoQuickStart.ViewModels;

namespace HugoQuickStart.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    /// <summary>初始化下拉选中项时抑制 SelectionChanged 回写，避免误触发保存。</summary>
    private bool _suppressThemeChanged;
    /// <summary>程序日志页显示的最近日志行。</summary>
    private readonly ObservableCollection<string> _logLines = new();
    /// <summary>已排队一次日志刷新，用于合并突发写入。</summary>
    private bool _logRefreshQueued;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"版本 {version?.ToString(3) ?? "1.0.0"}";

        AppDirectoryText.Text = AppContext.BaseDirectory;

        // 程序日志页：绑定最近日志，并订阅后续写入以实时刷新
        LogList.ItemsSource = _logLines;
        LogService.LogWritten += OnLogWritten;
        Closed += (_, _) => LogService.LogWritten -= OnLogWritten;

        // 依据当前主题选中下拉项（抑制回写，避免构造期触发保存）
        _suppressThemeChanged = true;
        ThemeCombo.SelectedIndex = ThemeModeToIndex(_viewModel.ThemeMode);
        _suppressThemeChanged = false;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ThemeMode))
            {
                var idx = ThemeModeToIndex(_viewModel.ThemeMode);
                if (ThemeCombo.SelectedIndex != idx)
                {
                    _suppressThemeChanged = true;
                    ThemeCombo.SelectedIndex = idx;
                    _suppressThemeChanged = false;
                }
            }
        };

        // 显式开启三个页面的错峰入场动画：先把子元素立即写入“隐藏态”本地值，
        // 保证各页在首次可见的那一帧就是隐藏状态，之后 Play 触发时才逐行浮现，
        // 避免“先完整显示再重播”的抽动。
        StackPanelIntroAnimationBehavior.Prepare(PageGeneralPanel);
        StackPanelIntroAnimationBehavior.Prepare(PageAppearancePanel);
        StackPanelIntroAnimationBehavior.Prepare(PageInterceptPanel);
        StackPanelIntroAnimationBehavior.Prepare(PageLogPanel);
        StackPanelIntroAnimationBehavior.Prepare(PageAboutPanel);

        // 窗口尚未显示时先布置淡入 + 缩放的初始状态，避免出现首帧闪烁
        WindowIntroAnimationBehavior.Prepare(this);

        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // 窗口级：淡入 + 轻微缩放（参数同 ClassIsland PopupIntroAnimation）
        WindowIntroAnimationBehavior.Play(this);

        // 首次显示后选中“常规设置”，触发 ShowPage 播放内容错峰入场动画
        if (NavList.SelectedItem == null)
        {
            NavList.SelectedIndex = 0;
        }
    }

    /// <summary>主题色下拉：0=跟随系统 1=深色 2=浅色。</summary>
    private void ThemeCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeChanged)
            return;

        _viewModel.ThemeMode = ThemeCombo.SelectedIndex switch
        {
            1 => "Dark",
            2 => "Light",
            _ => "System"
        };
    }

    private static int ThemeModeToIndex(string? mode) => mode switch
    {
        "Dark" => 1,
        "Light" => 2,
        _ => 0
    };

    /// <summary>切换左侧导航时显示对应页面，并对目标页重复播放错峰入场动画。</summary>
    private void NavList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem == NavGeneral)
        {
            ShowPage(PageGeneral, PageGeneralPanel);
        }
        else if (NavList.SelectedItem == NavAppearance)
        {
            ShowPage(PageAppearance, PageAppearancePanel);
        }
        else if (NavList.SelectedItem == NavIntercept)
        {
            ShowPage(PageIntercept, PageInterceptPanel);
        }
        else if (NavList.SelectedItem == NavLog)
        {
            ShowPage(PageLog, PageLogPanel);
            RefreshLog();
        }
        else if (NavList.SelectedItem == NavAbout)
        {
            ShowPage(PageAbout, PageAboutPanel);
        }
    }

    private void ShowPage(Control page, Panel panel)
    {
        // 先让所有页面内容都复位到“未播放”的隐藏态：
        // 目标页在可见前复位，保证切换后首帧即为隐藏态、随后才逐行浮现；
        // 离开页在不可见期间完成动画状态清理，避免切回时闪现完整内容。
        StackPanelIntroAnimationBehavior.Prepare(PageGeneralPanel);
        StackPanelIntroAnimationBehavior.Prepare(PageAppearancePanel);
        StackPanelIntroAnimationBehavior.Prepare(PageInterceptPanel);
        StackPanelIntroAnimationBehavior.Prepare(PageLogPanel);
        StackPanelIntroAnimationBehavior.Prepare(PageAboutPanel);

        PageGeneral.IsVisible = ReferenceEquals(page, PageGeneral);
        PageAppearance.IsVisible = ReferenceEquals(page, PageAppearance);
        PageIntercept.IsVisible = ReferenceEquals(page, PageIntercept);
        PageLog.IsVisible = ReferenceEquals(page, PageLog);
        PageAbout.IsVisible = ReferenceEquals(page, PageAbout);

        // 目标页变为可见后，开始错峰入场动画（ClassIsland 每次导航都会重播）
        StackPanelIntroAnimationBehavior.Play(panel);
    }

    // ================= 程序日志 =================

    /// <summary>日志有新记录时刷新列表（合并突发写入，避免频繁刷新）。</summary>
    private void OnLogWritten()
    {
        if (_logRefreshQueued)
            return;

        _logRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _logRefreshQueued = false;
            if (!IsVisible)
                return;
            RefreshLog();
        }, DispatcherPriority.Background);
    }

    /// <summary>把最近日志同步到列表，并滚动到最后一条。</summary>
    private void RefreshLog()
    {
        _logLines.Clear();
        foreach (var line in LogService.GetRecent())
            _logLines.Add(line);
        LogScroll.ScrollToEnd();
    }

    private void RefreshLog_OnClick(object? sender, RoutedEventArgs e) => RefreshLog();

    private void ClearLog_OnClick(object? sender, RoutedEventArgs e) => LogService.Clear();

    private void OpenLogFile_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogService.LogDirectory);
            if (!File.Exists(LogService.LogFilePath))
                File.WriteAllText(LogService.LogFilePath, string.Empty);
            Process.Start(new ProcessStartInfo
            {
                FileName = LogService.LogFilePath,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>打开配置目录（config.json 所在目录，位于 %APPDATA%，升级不受影响）。</summary>
    private void OpenAppDirectory_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppConfigService.ConfigDirectory;
            Directory.CreateDirectory(dir);
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // ignore
        }
    }
}
