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
        // 配置存放于 %APPDATA%\HugoQuickStart，与安装目录分离，
        // 避免覆盖安装/升级时 config.json 被安装包覆盖导致用户设置丢失。
        var dir = ConfigDirectory;
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");
        MigrateLegacyConfig();
    }

    /// <summary>用户配置目录（%APPDATA%\HugoQuickStart）。</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HugoQuickStart");

    /// <summary>旧版本把 config.json 放在安装目录（exe 旁），首次运行时自动迁移。</summary>
    private static void MigrateLegacyConfig()
    {
        try
        {
            var newPath = Path.Combine(ConfigDirectory, "config.json");
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!File.Exists(newPath) && File.Exists(legacyPath))
            {
                File.Copy(legacyPath, newPath, overwrite: false);
                File.Delete(legacyPath);
            }
        }
        catch
        {
            // 迁移失败不致命：按全新配置继续
        }
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
                    Name = "SecRandom 点名",
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
                    Path = "",
                    MatchKey = DefaultAppResolver.KeyEasiNote,
                    Category = "希沃软件",
                    IconPath = "",
                    IconMode = nameof(IconSourceMode.Preset),
                    IconKey = "easinote5"
                },
                new()
                {
                    Name = "希沃视频展台",
                    Path = "",
                    MatchKey = DefaultAppResolver.KeyEasiCamera,
                    Category = "希沃软件",
                    IconPath = "",
                    IconMode = nameof(IconSourceMode.Preset),
                    IconKey = "easicamera"
                },
                new()
                {
                    Name = "希沃轻白板",
                    Path = "",
                    MatchKey = DefaultAppResolver.KeyEasiNote5C,
                    Category = "希沃软件",
                    IconPath = "",
                    IconMode = nameof(IconSourceMode.Preset),
                    IconKey = "easinote5c"
                },
                new()
                {
                    Name = "VRChat",
                    Path = "",
                    MatchKey = DefaultAppResolver.KeyVrchat,
                    Category = "希沃软件",
                    IconPath = "",
                    IconMode = nameof(IconSourceMode.Preset),
                    IconKey = "vrchat"
                }
            },
            AutoStart = false,
            StartMinimized = true
        };
    }
}
