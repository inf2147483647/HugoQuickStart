using System;
using System.Linq;
using Avalonia;
using Avalonia.Styling;
using FluentAvalonia.Styling;

namespace HugoQuickStart.Services;

/// <summary>主题色模式：跟随系统 / 深色 / 浅色。</summary>
public enum ThemeMode
{
    System,
    Dark,
    Light
}

/// <summary>
/// 应用主题切换：同时设置 Application.RequestedThemeVariant 与
/// FluentAvaloniaTheme.PreferSystemTheme，保证 FluentAvalonia 资源正确响应。
/// </summary>
public static class ThemeService
{
    public static void Apply(ThemeMode mode)
    {
        if (Application.Current is not Application app)
            return;

        // 让 FluentAvaloniaTheme 决定是否跟随系统
        var fluentTheme = app.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (fluentTheme != null)
            fluentTheme.PreferSystemTheme = mode == ThemeMode.System;

        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    /// <summary>解析持久化字符串；无法识别时按 System 处理。</summary>
    public static ThemeMode Parse(string? value) =>
        Enum.TryParse<ThemeMode>(value, ignoreCase: true, out var m) ? m : ThemeMode.System;
}
