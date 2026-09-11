using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HugoQuickStart.Services;

/// <summary>
/// 程序日志：记录应用初始化、点击图标、点击冷却、修改应用设置等事件。
/// 日志文件写入用户配置目录（%APPDATA%\HugoQuickStart\app.log），
/// 与安装目录分离，覆盖安装/升级不会丢失。
/// </summary>
public static class LogService
{
    private static readonly object Gate = new();

    /// <summary>日志文件大小上限（1 MB），超出后轮转为 app.log.old，避免长期运行无限增长。</summary>
    private const long MaxLogBytes = 1024 * 1024;

    /// <summary>内存中保留的最近日志条数，供设置窗口实时查看，无需反复读取文件。</summary>
    private const int RecentCapacity = 500;

    private static readonly List<string> RecentBuffer = new();

    /// <summary>日志目录（%APPDATA%\HugoQuickStart）。</summary>
    public static string LogDirectory => AppConfigService.ConfigDirectory;

    /// <summary>日志文件完整路径。</summary>
    public static string LogFilePath => Path.Combine(LogDirectory, "app.log");

    /// <summary>有新日志写入时触发（用于设置窗口实时刷新列表）。</summary>
    public static event Action? LogWritten;

    public static void Info(string category, string message) => Write("INFO", category, message);

    public static void Warn(string category, string message) => Write("WARN", category, message);

    public static void Error(string category, string message) => Write("ERROR", category, message);

    /// <summary>返回最近日志行快照（时间从早到晚）。</summary>
    public static IReadOnlyList<string> GetRecent()
    {
        lock (Gate)
            return RecentBuffer.ToArray();
    }

    /// <summary>清空内存缓冲与日志文件。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            RecentBuffer.Clear();
            try
            {
                File.WriteAllText(LogFilePath, string.Empty, Encoding.UTF8);
            }
            catch
            {
                // 清空失败不致命
            }
        }
        LogWritten?.Invoke();
    }

    private static void Write(string level, string category, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}";

        lock (Gate)
        {
            RecentBuffer.Add(line);
            if (RecentBuffer.Count > RecentCapacity)
                RecentBuffer.RemoveRange(0, RecentBuffer.Count - RecentCapacity);

            try
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志写入失败不应影响主流程
            }
        }

        LogWritten?.Invoke();
    }

    /// <summary>超过大小上限时把当前文件轮转为 app.log.old（仅保留一份历史）。</summary>
    private static void RotateIfNeeded()
    {
        var info = new FileInfo(LogFilePath);
        if (!info.Exists || info.Length < MaxLogBytes)
            return;

        var old = LogFilePath + ".old";
        try
        {
            if (File.Exists(old))
                File.Delete(old);
            File.Move(LogFilePath, old);
        }
        catch
        {
            // 轮转失败则继续追加，不影响记录
        }
    }
}
