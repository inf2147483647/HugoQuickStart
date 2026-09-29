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
        _configPath = ConfigFilePath;
        MigrateLegacyConfig();
    }

    /// <summary>用户配置目录（%APPDATA%\HugoQuickStart）。</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HugoQuickStart");

    /// <summary>配置文件完整路径（%APPDATA%\HugoQuickStart\config.json）。</summary>
    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// 旧版本把 config.json 放在安装目录（exe 旁）。若用户目录尚无配置则迁移过来；
    /// 若用户目录已有配置（权威来源），则清除安装目录的残留旧文件——
    /// 避免升级安装时安装包误覆盖/误读，也避免用户误以为配置仍在那里。
    /// </summary>
    private static void MigrateLegacyConfig()
    {
        try
        {
            var newPath = Path.Combine(ConfigDirectory, "config.json");
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "config.json");

            if (File.Exists(newPath))
            {
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                    LogService.Info("配置", $"已删除安装目录的残留旧配置：{legacyPath}（以 %APPDATA% 下的配置为准）");
                }
                return;
            }

            if (File.Exists(legacyPath))
            {
                File.Copy(legacyPath, newPath, overwrite: false);
                File.Delete(legacyPath);
                LogService.Info("配置", $"已将安装目录旧配置迁移到用户目录：{newPath}");
            }
        }
        catch (Exception ex)
        {
            // 迁移失败不致命：按全新配置继续，但必须留下排查痕迹
            LogService.Error("配置", $"旧配置迁移失败（不影响本次启动）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 尝试把 JSON 文本解析为配置对象；格式非法或结果为空时返回 null（不抛异常）。
    /// 供备份恢复校验备份内容是否可用。
    /// </summary>
    public static AppConfig? TryParse(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config == null)
                return null;

            config.QuickEntries ??= new List<AppItem>();
            config.XiwoApps ??= new List<AppItem>();
            return config;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把配置原子写回配置文件路径（供备份恢复使用，无需实例）；成功返回 true。</summary>
    public static bool TryAtomicWrite(AppConfig config) => AtomicWriteTo(ConfigFilePath, config);

    public AppConfig Load()
    {
        // 首次运行：目录下无配置时写入默认配置，保证后续必有稳定读写入口。
        if (!File.Exists(_configPath))
        {
            var fresh = CreateDefaultConfig();
            AtomicWrite(fresh);
            LogService.Info("配置", $"未发现配置文件，已创建默认配置：{_configPath}");
            return fresh;
        }

        try
        {
            var config = TryParse(File.ReadAllText(_configPath));
            if (config != null)
                return config;

            LogService.Error("配置", $"配置文件无法解析，按损坏处理：{_configPath}");
        }
        catch (Exception ex)
        {
            // 读取/反序列化失败视为配置损坏，走下方恢复逻辑
            LogService.Error("配置", $"读取配置失败，按损坏处理：{ex.GetType().Name}: {ex.Message}（文件：{_configPath}）");
        }

        // 配置损坏：先备份原始文件保留现场，再按"从近到远"用备份尝试恢复；
        // 全部备份都无法恢复时才回落默认配置——绝不静默抹掉用户设置。
        BackupCorruptConfig();

        var restored = ConfigBackupService.TryRestoreFromBackups();
        if (restored != null)
            return restored;

        var defaults = CreateDefaultConfig();
        AtomicWrite(defaults);
        LogService.Warn("配置", "所有备份均无法恢复，已重建默认配置");
        return defaults;
    }

    public void Save(AppConfig config) => AtomicWrite(config);

    /// <summary>原子写入：先写临时文件再整体替换，避免写入中途（程序崩溃/断电）产生截断的损坏配置。</summary>
    private void AtomicWrite(AppConfig config) => AtomicWriteTo(_configPath, config);

    /// <summary>原子写入指定路径的配置文件；成功返回 true，失败写日志并返回 false。</summary>
    private static bool AtomicWriteTo(string path, AppConfig config)
    {
        var tmp = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tmp, json);
            // 同目录内重命名替换，NTFS 上为原子操作，目标存在与否均可。
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // 保存失败绝不能静默：用户会以为设置已生效，重启后却发现丢失
            // （常见于杀软占用 .tmp、磁盘满、漫游配置文件同步失败）。
            LogService.Error("配置", $"配置保存失败，本次修改可能未落盘：{ex.GetType().Name}: {ex.Message}（目标：{path}）");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略清理失败 */ }
            return false;
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
            LogService.Warn("配置", $"原配置已备份至 {backup}，现重建默认配置，可人工对照备份恢复");
        }
        catch (Exception ex)
        {
            // 备份失败不致命：仍按默认配置继续，但必须记录
            LogService.Error("配置", $"损坏配置备份失败：{ex.GetType().Name}: {ex.Message}");
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
