using System;
using System.IO;

namespace HugoQuickStart.Services;

/// <summary>
/// 默认应用解析器：把默认快捷入口的匹配键（MatchKey）解析为本机真实 EXE 路径。
/// 不再硬编码路径，而是：枚举已安装应用（注册表）→ 匹配 → 解析 EXE → 绑定图标。
/// VRChat 等 Steam 游戏额外通过 Steam 游戏库定位。
/// </summary>
public static class DefaultAppResolver
{
    // 匹配键常量
    public const string KeyEasiNote = "seewo.easinote";    // 希沃白板5
    public const string KeyEasiCamera = "seewo.easicamera"; // 希沃视频展台
    public const string KeyMinecraft = "minecraft";         // Minecraft
    public const string KeyVrchat = "vrchat";               // VRChat (Steam)

    /// <summary>按匹配键解析 EXE 路径；无法解析返回 null。</summary>
    public static string? Resolve(string? matchKey)
    {
        if (string.IsNullOrWhiteSpace(matchKey))
            return null;

        switch (matchKey)
        {
            case KeyEasiNote:
                return ResolveSeewo("希沃白板", "EasiNote", "EasiNote.exe");
            case KeyEasiCamera:
                return ResolveSeewo("希沃视频展台", "EasiCamera", "EasiCamera.exe");
            case KeyMinecraft:
                return ResolveByDisplayName("Minecraft");
            case KeyVrchat:
                return ResolveSteamGame("VRChat", "VRChat.exe");
            default:
                return null;
        }
    }

    /// <summary>
    /// 从应用名称推断匹配键（用于旧配置/无 MatchKey 的条目迁移）。
    /// 无法识别返回空字符串。
    /// </summary>
    public static string InferMatchKeyFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        if (Contains(name, "希沃白板"))
            return KeyEasiNote;
        if (Contains(name, "希沃视频展台") || Contains(name, "视频展台"))
            return KeyEasiCamera;
        if (Contains(name, "Minecraft"))
            return KeyMinecraft;
        if (Contains(name, "VRChat"))
            return KeyVrchat;

        return string.Empty;
    }

    /// <summary>希沃软件类应用：从已安装应用匹配注册表条目，再解析出 EXE。</summary>
    private static string? ResolveSeewo(string displayKeyword, string installKeyword, string exeName)
    {
        var app = InstalledAppLocator.FindByDisplayName(displayKeyword, installKeyword);
        if (app == null)
            return null;

        // 优先 DisplayIcon 指向的实际程序
        var iconPath = InstalledAppLocator.CleanIconToExePath(app.DisplayIcon);
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            return iconPath;

        // 其次安装目录中的目标 exe
        return InstalledAppLocator.FindExe(app.InstallLocation, exeName, installKeyword, displayKeyword);
    }

    /// <summary>按显示名关键字匹配已安装应用，再解析 EXE。</summary>
    private static string? ResolveByDisplayName(string keyword)
    {
        var app = InstalledAppLocator.FindByDisplayName(keyword);
        if (app == null)
            return null;

        var iconPath = InstalledAppLocator.CleanIconToExePath(app.DisplayIcon);
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            return iconPath;

        return InstalledAppLocator.FindExe(app.InstallLocation, null, keyword);
    }

    /// <summary>通过 Steam 游戏库定位游戏 EXE（如 VRChat）。</summary>
    private static string? ResolveSteamGame(string gameFolder, string exeName)
    {
        return InstalledAppLocator.FindSteamGameExe(gameFolder, exeName);
    }

    private static bool Contains(string source, string part) =>
        source.Contains(part, StringComparison.OrdinalIgnoreCase);
}
