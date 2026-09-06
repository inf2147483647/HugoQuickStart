using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HugoQuickStart;

/// <summary>
/// 悬浮球：程序最小化到托盘后，显示在屏幕右下角的圆形程序图标。点击后还原主界面。
/// </summary>
public partial class FloatBallWindow : Window
{
    /// <summary>点击悬浮球时触发，由主窗口订阅以还原主界面。</summary>
    public event Action? Clicked;

    public FloatBallWindow()
    {
        InitializeComponent();
    }

    private void Ball_Click(object? sender, RoutedEventArgs e)
    {
        Clicked?.Invoke();
    }
}
