using System.Collections.Generic;

namespace HugoQuickStart.Models;

public class AppConfig
{
    public List<AppItem> QuickEntries { get; set; } = new();
    public List<AppItem> XiwoApps { get; set; } = new();
    public bool AutoStart { get; set; } = false;
    public bool StartMinimized { get; set; } = true;
    public double WindowWidth { get; set; } = 380;
    public double WindowHeight { get; set; } = 500;
    public int MarginRight { get; set; } = 20;
    public int MarginBottom { get; set; } = 60;
}
