using System;
using System.Diagnostics;
using System.IO;

namespace HugoQuickStart.Services;

public static class ProcessLauncher
{
    /// <summary>
    /// 启动应用。支持三种方式：
    /// 1. URI 协议（如 classisland://app/settings）——交给系统按注册的协议处理程序启动；
    /// 2. 绝对路径——直接启动；
    /// 3. 相对路径——若不存在，优先在程序安装目录（exe 所在目录）下解析。
    /// </summary>
    public static bool Launch(string path, string arguments = "")
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var cleaned = CleanPath(path);
        if (string.IsNullOrEmpty(cleaned))
            return false;

        try
        {
            // URI 协议（如 classisland://app/settings）：交给系统按注册的协议处理程序启动
            if (IsUri(cleaned))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = cleaned,
                    Arguments = arguments,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                return true;
            }

            var resolved = ResolvePath(cleaned);
            if (string.IsNullOrEmpty(resolved))
                return false;

            var fileStartInfo = new ProcessStartInfo
            {
                FileName = resolved,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(resolved) ?? string.Empty
            };
            Process.Start(fileStartInfo);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>判断字符串是否为带协议的 URI（形如 scheme://...），以区分文件路径。</summary>
    public static bool IsUri(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var index = path.IndexOf("://", StringComparison.Ordinal);
        if (index <= 0)
            return false;

        var scheme = path[..index];
        foreach (var c in scheme)
        {
            if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return false;
        }
        return true;
    }

    /// <summary>
    /// 将应用路径解析为绝对路径。
    /// 规则：绝对路径存在则直接使用；否则尝试“安装目录\该路径”。
    /// </summary>
    public static string? ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        path = CleanPath(path) ?? string.Empty;
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            if (Path.IsPathRooted(path))
            {
                return File.Exists(path) ? path : null;
            }

            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, path);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 清洗路径字符串：去除首尾空白，并去掉成对包裹的外层双引号/单引号。
    /// 用户习惯从资源管理器复制带引号的路径（例如 "D:\...\App.exe"），
    /// 若不处理会导致 File.Exists 因引号而判定失败。
    /// </summary>
    public static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var trimmed = path.Trim();
        if (trimmed.Length >= 2)
        {
            var first = trimmed[0];
            var last = trimmed[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                trimmed = trimmed[1..^1];
        }
        return trimmed;
    }
}
