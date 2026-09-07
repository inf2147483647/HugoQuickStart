using System.Collections.Generic;

namespace HugoQuickStart.Models;

public class AppConfig
{
    public List<AppItem> QuickEntries { get; set; } = new();
    public List<AppItem> XiwoApps { get; set; } = new();
    public bool AutoStart { get; set; } = false;
    public bool StartMinimized { get; set; } = true;

    /// <summary>是否拦截希沃服务助手（SeewoServiceAssistant.exe）的右下角悬浮窗。</summary>
    public bool BlockSeewoAssistantWindow { get; set; } = true;

    /// <summary>主题色：跟随系统 / 深色 / 浅色（对应 ThemeMode 枚举名）。持久化到配置文件。</summary>
    public string ThemeMode { get; set; } = "System";

    /// <summary>界面缩放：控制主界面总体大小，0.80–1.50，默认 1.00。持久化到配置文件。</summary>
    public double UiScale { get; set; } = 1.0;
    public double WindowWidth { get; set; } = 380;
    public double WindowHeight { get; set; } = 500;
    public int MarginRight { get; set; } = 20;
    public int MarginBottom { get; set; } = 60;
}
