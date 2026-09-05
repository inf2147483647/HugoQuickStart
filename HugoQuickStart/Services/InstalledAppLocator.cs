using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HugoQuickStart.Services;

/// <summary>已安装应用的简化描述（来自注册表卸载项）。</summary>
public class InstalledApp
{
    public string DisplayName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string DisplayIcon { get; set; } = string.Empty;
}

/// <summary>
/// 枚举本机已安装的应用（注册表卸载项）与 Steam 游戏库，
/// 供默认应用解析器查找并绑定真实 EXE 路径。
/// </summary>
public static class InstalledAppLocator
{
    // 注册表卸载项根路径（64 位、32 位 WOW6432Node、当前用户）
    private static readonly string[] UninstallRoots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    /// <summary>枚举已安装应用。注册表不可读或单项异常时跳过，尽量返回可用结果。</summary>
    public static List<InstalledApp> GetInstalledApps()
    {
        var result = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in UninstallRoots)
        {
            AddFromRegistry(Registry.LocalMachine, root, result, seen);
        }
        AddFromRegistry(Registry.CurrentUser, UninstallRoots[2], result, seen);

        return result;
    }

    private static void AddFromRegistry(RegistryKey hive, string path,
        List<InstalledApp> result, HashSet<string> seen)
    {
        try
        {
            using var rootKey = hive.OpenSubKey(path);
            if (rootKey == null)
                return;

            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = rootKey.OpenSubKey(subKeyName);
                    if (subKey == null)
                        continue;

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    if (!seen.Add(displayName))
                        continue;

                    var installLocation = subKey.GetValue("InstallLocation") as string ?? string.Empty;
                    var displayIcon = subKey.GetValue("DisplayIcon") as string ?? string.Empty;

                    result.Add(new InstalledApp
                    {
                        DisplayName = displayName.Trim(),
                        InstallLocation = installLocation.Trim(),
                        DisplayIcon = displayIcon.Trim()
                    });
                }
                catch
                {
                    // 单项损坏忽略
                }
            }
        }
        catch
        {
            // 根路径不可读忽略
        }
    }

    /// <summary>根据显示名关键字匹配已安装应用（模糊、忽略大小写）。</summary>
    public static InstalledApp? FindByDisplayName(params string[] keywords)
    {
        var apps = GetInstalledApps();
        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                continue;

            var match = apps.FirstOrDefault(a =>
                a.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                a.InstallLocation.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>把注册表 DisplayIcon（常为 "path.exe,0"）转换成实际程序路径，不能解析时返回 null。</summary>
    public static string? CleanIconToExePath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return null;

        var raw = displayIcon.Trim();

        // 优先取第一段双引号内内容（形如 "C:\x\app.exe",0）
        var q1 = raw.IndexOf('"');
        if (q1 >= 0)
        {
            var q2 = raw.IndexOf('"', q1 + 1);
            if (q2 > q1)
                raw = raw.Substring(q1 + 1, q2 - q1 - 1);
        }

        // 去掉 ",资源索引" 后缀（如 ,0）
        var comma = raw.LastIndexOf(',');
        if (comma > 0 &&
            int.TryParse(raw.Substring(comma + 1), out _) &&
            new[] { ".exe", ".dll", ".ico" }.Any(ext =>
                raw[..comma].EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            raw = raw[..comma];
        }

        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>在安装目录中查找 EXE。优先精确文件名，其次按关键字模糊，最后取根目录第一个。</summary>
    public static string? FindExe(string? installLocation, string? preferredName, params string[] nameTokens)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            return null;

        try
        {
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                var exact = Directory
                    .EnumerateFiles(installLocation, preferredName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (exact != null)
                    return exact;
            }

            if (nameTokens.Length > 0)
            {
                var tokenMatch = Directory
                    .EnumerateFiles(installLocation, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => nameTokens.Any(t =>
                        Path.GetFileNameWithoutExtension(f).Contains(t, StringComparison.OrdinalIgnoreCase)));
                if (tokenMatch != null)
                    return tokenMatch;
            }

            return Directory
                .EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取所有 Steam 游戏库根目录（含默认库与 libraryfolders.vdf 中的扩展库）。</summary>
    public static List<string> GetSteamLibraryPaths()
    {
        var libraries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var steamRoot = GetSteamInstallPath();
        if (string.IsNullOrWhiteSpace(steamRoot))
            return libraries;

        var defaultApps = Path.Combine(steamRoot, "steamapps");
        if (Directory.Exists(defaultApps) && seen.Add(defaultApps))
            libraries.Add(defaultApps);

        // 处理 libraryfolders.vdf（可能包含多个库路径）
        var vdfPath = Path.Combine(defaultApps, "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                var content = File.ReadAllText(vdfPath);
                var regex = new Regex("\"path\"\\s*\"([^\"]+)\"");
                foreach (Match m in regex.Matches(content))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
                        libraries.Add(p);
                }
            }
            catch
            {
                // 忽略 vdf 读取失败
            }
        }

        return libraries;
    }

    /// <summary>解析 Steam 安装路径：优先注册表 SteamPath，回退到默认安装目录。</summary>
    public static string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
                return steamPath.Trim();
        }
        catch
        {
            // 回退
        }

        // 回退：默认安装目录
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidate = Path.Combine(programFiles, "Steam");
        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 在 Steam 游戏库中定位游戏目录下的 EXE（如 VRChat -> steamapps\common\VRChat\VRChat.exe）。
    /// </summary>
    public static string? FindSteamGameExe(string gameFolder, string exeName)
    {
        foreach (var lib in GetSteamLibraryPaths())
        {
            var candidate = Path.Combine(lib, "common", gameFolder, exeName);
            if (File.Exists(candidate))
                return candidate;

            // 兜底：在游戏目录下模糊匹配同名 exe
            var gameDir = Path.Combine(lib, "common", gameFolder);
            if (Directory.Exists(gameDir))
            {
                var found = Directory
                    .EnumerateFiles(gameDir, exeName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null)
                    return found;
            }
        }
        return null;
    }
}
