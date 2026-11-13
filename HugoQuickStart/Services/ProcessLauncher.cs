using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace HugoQuickStart.Services;

/// <summary>启动结果：成功 / 路径无效 / 用户取消了管理员授权（UAC）。</summary>
public enum LaunchResult
{
    Ok,
    PathInvalid,
    ElevationDenied
}

public static class ProcessLauncher
{
    /// <summary>用户取消 UAC 授权时 ShellExecute 返回的错误码。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// 异步启动应用。支持三种方式：
    /// 1. URI 协议（如 classisland://app/settings）——交给系统按注册的协议处理程序启动；
    /// 2. 绝对路径——直接启动；
    /// 3. 相对路径——若不存在，优先在程序安装目录（exe 所在目录）下解析。
    /// 路径解析（File.Exists）与 ShellExecute 都可能在高负载时阻塞，故整体放到线程池执行，
    /// 避免点击图标时卡住 UI 线程。
    /// <paramref name="asAdministrator"/> 为 true 时以管理员身份启动（弹 UAC）；URI 协议忽略该选项。
    /// </summary>
    public static Task<LaunchResult> LaunchAsync(string path, string arguments = "", bool asAdministrator = false) =>
        Task.Run(() => LaunchCore(path, arguments, asAdministrator));

    private static LaunchResult LaunchCore(string path, string arguments, bool asAdministrator)
    {
        if (string.IsNullOrWhiteSpace(path))
            return LaunchResult.PathInvalid;

        var cleaned = CleanPath(path);
        if (string.IsNullOrEmpty(cleaned))
            return LaunchResult.PathInvalid;

        try
        {
            // URI 协议（如 classisland://app/settings）：交给系统按注册的协议处理程序启动
            // 协议链接由系统已有的处理程序打开，不为其请求管理员权限。
            if (IsUri(cleaned))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = cleaned,
                    Arguments = arguments,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                return LaunchResult.Ok;
            }

            var resolved = ResolvePath(cleaned);
            if (string.IsNullOrEmpty(resolved))
                return LaunchResult.PathInvalid;

            var fileStartInfo = new ProcessStartInfo
            {
                FileName = resolved,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(resolved) ?? string.Empty
            };
            if (asAdministrator)
                fileStartInfo.Verb = "runas";

            Process.Start(fileStartInfo);
            return LaunchResult.Ok;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // 用户在 UAC 对话框中选择"否"
            return LaunchResult.ElevationDenied;
        }
        catch (Exception)
        {
            return LaunchResult.PathInvalid;
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
