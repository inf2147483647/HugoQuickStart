using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HugoQuickStart.Models;

namespace HugoQuickStart.Services;

/// <summary>
/// 配置备份：备份内容为"原始配置文件"（即设置项与图标列表所在的 config.json），
/// 整体打包为 zip 保存。
///   · 自动备份：在"距上次自动备份已超过设定周期"后的首次启动时执行；
///   · 手动备份：用户主动触发，不受周期与数量上限约束；
///   · 数量上限只裁剪自动备份，0 表示不限制；
///   · 配置损坏时由 <see cref="TryRestoreFromBackups"/> 按"从近到远"尝试恢复。
/// "上次自动备份时间"由备份文件名中的时间戳推导（不另存字段），
/// 因此即使配置本身丢失或被重置，周期判定依然成立。
/// </summary>
public static class ConfigBackupService
{
    private const string AutoPrefix = "Auto-Backup_";
    private const string ManualPrefix = "Backup_";

    /// <summary>zip 内保存的条目名，即原始配置文件名。</summary>
    private const string ConfigEntryName = "config.json";

    /// <summary>文件名中的时间戳格式。</summary>
    private const string TimestampFormat = "yyyy-MM-dd-HH-mm-ss";

    /// <summary>时间戳字符数（yyyy-MM-dd-HH-mm-ss）。</summary>
    private const int TimestampLength = 19;

    /// <summary>备份目录（%APPDATA%\HugoQuickStart\Backups）。</summary>
    public static string BackupDirectory => Path.Combine(AppConfigService.ConfigDirectory, "Backups");

    /// <summary>当前程序版本（与“关于”页显示一致，取三段式，如 1.0.0）。</summary>
    private static string CurrentVersion =>
        typeof(ConfigBackupService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 生成备份文件名（不含目录）：
    /// 自动备份 <c>Auto-Backup_&lt;YYYY-MM-DD-hh-mm-ss&gt;_&lt;版本&gt;.zip</c>，
    /// 手动备份 <c>Backup_&lt;YYYY-MM-DD-hh-mm-ss&gt;_&lt;版本&gt;.zip</c>。
    /// </summary>
    public static string BuildFileName(bool manual, DateTime time) =>
        $"{(manual ? ManualPrefix : AutoPrefix)}" +
        $"{time.ToString(TimestampFormat, CultureInfo.InvariantCulture)}_{CurrentVersion}.zip";

    /// <summary>
    /// 列出备份文件（最新在前）。<paramref name="autoOnly"/> 为 true 时只返回自动备份。
    /// 只统计符合命名规则的文件，避免把用户放进目录的其它文件当作备份删除。
    /// </summary>
    public static List<FileInfo> GetBackups(bool autoOnly)
    {
        var result = new List<FileInfo>();
        try
        {
            var dir = new DirectoryInfo(BackupDirectory);
            if (!dir.Exists)
                return result;

            foreach (var file in dir.GetFiles("*.zip"))
            {
                var isAuto = file.Name.StartsWith(AutoPrefix, StringComparison.OrdinalIgnoreCase);
                var isManual = file.Name.StartsWith(ManualPrefix, StringComparison.OrdinalIgnoreCase);
                if (autoOnly ? !isAuto : !(isAuto || isManual))
                    continue;
                result.Add(file);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("备份", $"枚举备份目录失败：{ex.GetType().Name}: {ex.Message}");
        }

        return result
            .OrderByDescending(f => ParseTimestamp(f.Name) ?? f.LastWriteTime)
            .ToList();
    }

    /// <summary>最近一次自动备份时间；从未自动备份过返回 null。</summary>
    public static DateTime? GetLastAutoBackupTime()
    {
        var autos = GetBackups(autoOnly: true);
        return autos.Count == 0 ? null : ParseTimestamp(autos[0].Name);
    }

    /// <summary>执行一次备份，返回备份文件路径；失败或源配置不存在时返回 null。</summary>
    public static string? CreateBackup(bool manual)
    {
        try
        {
            var source = AppConfigService.ConfigFilePath;
            if (!File.Exists(source))
            {
                LogService.Warn("备份", "配置文件不存在，已跳过备份");
                return null;
            }

            Directory.CreateDirectory(BackupDirectory);
            var target = MakeUniquePath(
                Path.Combine(BackupDirectory, BuildFileName(manual, DateTime.Now)));

            // 把"原始配置文件"原样打包进 zip（条目名即原文件名 config.json）
            using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(source, ConfigEntryName, CompressionLevel.Optimal);
            }

            LogService.Info("备份", $"已创建{(manual ? "手动" : "自动")}备份：{Path.GetFileName(target)}");
            return target;
        }
        catch (Exception ex)
        {
            LogService.Error("备份", $"创建备份失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 配置损坏时的自动恢复：按"从近到远"遍历全部备份（含手动备份），
    /// 逐个解出其中的 config.json 并校验；首个可用者即写回配置路径并返回。
    /// 全部备份都不可用时返回 null，由调用方回落默认配置。
    /// </summary>
    public static AppConfig? TryRestoreFromBackups()
    {
        var candidates = GetBackups(autoOnly: false);
        if (candidates.Count == 0)
        {
            LogService.Warn("配置", "没有可用备份，无法自动恢复");
            return null;
        }

        foreach (var file in candidates)
        {
            var config = TryReadBackup(file.FullName);
            if (config == null)
            {
                LogService.Warn("配置", $"备份不可用（已跳过并继续尝试更早的备份）：{file.Name}");
                continue;
            }

            if (!AppConfigService.TryAtomicWrite(config))
                LogService.Error("配置", $"已从备份 {file.Name} 恢复配置，但写回配置文件失败，本次运行使用内存中的恢复结果");
            else
                LogService.Warn("配置", $"已从备份恢复配置：{file.Name}");

            return config;
        }

        return null;
    }

    /// <summary>解压备份并解析其中的配置内容；zip 损坏或内容非法时返回 null。</summary>
    private static AppConfig? TryReadBackup(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            // 优先按约定条目名取；个别备份条目名不同则退回第一个条目
            var entry = zip.GetEntry(ConfigEntryName) ?? zip.Entries.FirstOrDefault();
            if (entry == null)
                return null;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return AppConfigService.TryParse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            LogService.Warn("配置", $"读取备份失败（{Path.GetFileName(zipPath)}）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 启动时按需自动备份：距上次自动备份已满 <paramref name="intervalDays"/> 天（含）即备份，
    /// 从未备份过则本次建立首个基线备份；随后按 <paramref name="maxCount"/> 裁剪（0 表示不限制）。
    /// </summary>
    public static void RunStartupBackup(int intervalDays, int maxCount)
    {
        try
        {
            var last = GetLastAutoBackupTime();
            if (last.HasValue && DateTime.Now - last.Value < TimeSpan.FromDays(intervalDays))
                return;   // 未到周期，本次启动不备份

            var path = CreateBackup(manual: false);
            if (path == null)
                return;

            if (last.HasValue)
                LogService.Info("备份", $"距上次自动备份已超过 {intervalDays} 天，已在本次启动完成自动备份");
            else
                LogService.Info("备份", "尚无自动备份，已在本次启动建立首个自动备份");

            PruneAutoBackups(maxCount);
        }
        catch (Exception ex)
        {
            LogService.Error("备份", $"自动备份失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>裁剪自动备份，仅保留最新 <paramref name="maxCount"/> 份；≤0 表示不限制。</summary>
    public static void PruneAutoBackups(int maxCount)
    {
        if (maxCount <= 0)
            return;

        try
        {
            var removed = 0;
            foreach (var file in GetBackups(autoOnly: true).Skip(maxCount))
            {
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (Exception ex)
                {
                    LogService.Warn("备份", $"删除过期自动备份失败（{file.Name}）：{ex.Message}");
                }
            }

            if (removed > 0)
                LogService.Info("备份", $"自动备份超过上限 {maxCount} 份，已清理最旧的 {removed} 份");
        }
        catch (Exception ex)
        {
            LogService.Error("备份", $"裁剪自动备份失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>从备份文件名解析时间戳（取首个下划线之后的部分）；格式不符返回 null。</summary>
    private static DateTime? ParseTimestamp(string fileName)
    {
        var index = fileName.IndexOf('_');
        if (index < 0 || fileName.Length < index + 1 + TimestampLength)
            return null;

        var text = fileName.Substring(index + 1, TimestampLength);
        return DateTime.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value)
            ? value
            : null;
    }

    /// <summary>同名文件已存在时追加 -1 / -2 … 序号，避免覆盖既有备份。</summary>
    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        return path;
    }
}
