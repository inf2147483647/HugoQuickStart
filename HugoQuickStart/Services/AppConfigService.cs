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
        // 首次运行：目录下无配置时写入默认配置，保证后续必有稳定读写入口。
        if (!File.Exists(_configPath))
        {
            var fresh = CreateDefaultConfig();
            AtomicWrite(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config != null)
                return config;
        }
        catch
        {
            // 读取/反序列化失败视为配置损坏，走下方恢复逻辑
        }

        // 配置损坏：先备份原始文件再重建，绝不直接覆盖导致用户设置被静默抹掉。
        BackupCorruptConfig();
        var defaults = CreateDefaultConfig();
        AtomicWrite(defaults);
        return defaults;
    }

    public void Save(AppConfig config) => AtomicWrite(config);

    /// <summary>原子写入：先写临时文件再整体替换，避免写入中途（程序崩溃/断电）产生截断的损坏配置。</summary>
    private void AtomicWrite(AppConfig config)
    {
        var tmp = _configPath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tmp, json);
            // 同目录内重命名替换，NTFS 上为原子操作，目标存在与否均可。
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略清理失败 */ }
        }
    }

    /// <summary>把损坏的配置复制为带时间戳的备份，供人工恢复；保留原文件以便排查。</summary>
    private void BackupCorruptConfig()
    {
        try
        {
            var backup =
                _configPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
            File.Copy(_configPath, backup, overwrite: false);
        }
        catch
        {
            // 备份失败不致命：仍按默认配置继续
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
