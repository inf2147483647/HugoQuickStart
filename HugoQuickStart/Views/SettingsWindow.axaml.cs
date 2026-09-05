using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HugoQuickStart.Behaviors;
using HugoQuickStart.ViewModels;

namespace HugoQuickStart.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"版本 {version?.ToString(3) ?? "1.0.0"}";

        AppDirectoryText.Text = AppContext.BaseDirectory;

        // 显式开启三个页面的错峰入场动画：先把子元素立即写入“隐藏态”本地值，
        // 保证各页在首次可见的那一帧就是隐藏状态，之后 Play 触发时才逐行浮现，
        // 避免“先完整显示再重播”的抽动。
        StackPanelIntroAnimationBehavior.Prepare(PageGeneralPanel);
        StackPanelIntroAnimationBehavior.Prepare(PageInterceptPanel);
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

    /// <summary>切换左侧导航时显示对应页面，并对目标页重复播放错峰入场动画。</summary>
    private void NavList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem == NavGeneral)
        {
            ShowPage(PageGeneral, PageGeneralPanel);
        }
        else if (NavList.SelectedItem == NavIntercept)
        {
            ShowPage(PageIntercept, PageInterceptPanel);
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
        StackPanelIntroAnimationBehavior.Prepare(PageInterceptPanel);
        StackPanelIntroAnimationBehavior.Prepare(PageAboutPanel);

        PageGeneral.IsVisible = ReferenceEquals(page, PageGeneral);
        PageIntercept.IsVisible = ReferenceEquals(page, PageIntercept);
        PageAbout.IsVisible = ReferenceEquals(page, PageAbout);

        // 目标页变为可见后，开始错峰入场动画（ClassIsland 每次导航都会重播）
        StackPanelIntroAnimationBehavior.Play(panel);
    }

    /// <summary>打开程序安装目录（配置文件 config.json 所在目录）。</summary>
    private void OpenAppDirectory_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
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
