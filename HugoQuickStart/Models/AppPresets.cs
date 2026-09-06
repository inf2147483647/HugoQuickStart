using System.Collections.Generic;

namespace HugoQuickStart.Models;

/// <summary>
/// 一条可被一键添加到列表的内置预设。
/// <paramref name="IconKey"/> 为内置图标标识（Assets/presets/&lt;key&gt;.png）。
/// </summary>
public sealed record AppPreset(string Name, string Path, string Category, string IconKey);

/// <summary>"添加预设"功能提供的内置预设清单（快捷入口 + 希沃软件）。</summary>
public static class AppPresets
{
    public const string CategoryQuickEntry = "快捷入口";
    public const string CategoryXiwo = "希沃软件";

    /// <summary>快捷入口预设（ClassIsland / SecRandom 的协议链接，图标为内置资源）。</summary>
    public static IReadOnlyList<AppPreset> QuickEntryPresets { get; } = new List<AppPreset>
    {
        new("ClassIsland档案", "classisland://app/profile/", CategoryQuickEntry, "classisland"),
        new("ClassIsland换课", "classisland://app/class-swap", CategoryQuickEntry, "classisland"),
        new("ClassIsland设置", "classisland://app/settings/", CategoryQuickEntry, "classisland"),
        new("SecRandom点名", "secrandom://window/main?action=show&page=roll", CategoryQuickEntry, "secrandom"),
        new("SecRandom设置", "secrandom://window/settings", CategoryQuickEntry, "secrandom"),
        new("SecRandom闪抽", "secrandom://roll_call/quick_draw", CategoryQuickEntry, "secrandom"),
        new("SecRandom抽奖", "secrandom://window/main?action=show&page=lottery", CategoryQuickEntry, "secrandom")
    };

    /// <summary>希沃应用预设（固定安装路径的 exe，图标为内置资源）。</summary>
    public static IReadOnlyList<AppPreset> XiwoPresets { get; } = new List<AppPreset>
    {
        new("希沃白板5", @"C:\Program Files (x86)\Seewo\EasiNote5\swenlauncher\swenlauncher.exe", CategoryXiwo, "easinote5"),
        new("希沃视频展台", @"C:\Program Files (x86)\Seewo\EasiCamera\sweclauncher\sweclauncher.exe", CategoryXiwo, "easicamera"),
        new("VRChat", @"C:\Program Files (x86)\Steam\steamapps\common\VRChat\launch.exe", CategoryXiwo, "vrchat"),
        new("希沃轻白板", @"C:\Program Files (x86)\Seewo\EasiNote5C\swenlauncher\swenlauncher.exe", CategoryXiwo, "easinote5c")
    };
}
