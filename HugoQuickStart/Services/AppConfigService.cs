using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HugoQuickStart.Models;

namespace HugoQuickStart.Services;

public class AppConfigService
{
    private readonly string _configPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppConfigService()
    {
        // 所有运行产生的文件（配置等）均存放在程序安装目录（exe 所在文件夹）
        var baseDir = AppContext.BaseDirectory;
        _configPath = Path.Combine(baseDir, "config.json");
    }

    public AppConfig Load()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (config != null)
                    return config;
            }
            catch
            {
                // If config is corrupted, return default
            }
        }

        var defaultConfig = CreateDefaultConfig();
        Save(defaultConfig);
        return defaultConfig;
    }

    public void Save(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    private AppConfig CreateDefaultConfig()
    {
        return new AppConfig
        {
            QuickEntries = new List<AppItem>
            {
                new()
                {
                    Name = "ClassIsland 设置",
                    Path = "classisland://app/settings",
                    Category = "快捷入口",
                    IconPath = ""
                },
                new()
                {
                    Name = "Seerandom 点名",
                    Path = "secrandom://window/main",
                    Category = "快捷入口",
                    IconPath = ""
                }
            },
            XiwoApps = new List<AppItem>
            {
                new()
                {
                    Name = "希沃白板5",
                    Path = @"C:\Program Files (x86)\Seewo\EasiNote5\EasiNote.exe",
                    Category = "希沃软件",
                    IconPath = ""
                },
                new()
                {
                    Name = "希沃视频展台",
                    Path = @"C:\Program Files (x86)\Seewo\EasiCamera\EasiCamera.exe",
                    Category = "希沃软件",
                    IconPath = ""
                },
                new()
                {
                    Name = "Minecraft",
                    Path = "",
                    Category = "希沃软件",
                    IconPath = ""
                },
                new()
                {
                    Name = "VRChat",
                    Path = "",
                    Category = "希沃软件",
                    IconPath = ""
                }
            },
            AutoStart = false,
            StartMinimized = true
        };
    }
}
