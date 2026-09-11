using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;

namespace HugoQuickStart.Models;

/// <summary>编辑对话框中“图标”来源模式。</summary>
public enum IconSourceMode
{
    /// <summary>自动获取：从目标 exe 提取真实图标（协议链接回退内置图标）。</summary>
    Auto,
    /// <summary>预设：使用内置预设图标（IconKey）。</summary>
    Preset,
    /// <summary>自定义：使用用户选择的图片文件（IconPath）。</summary>
    Custom,
    /// <summary>从 EXE 提取：从用户选择的 exe 文件提取真实图标（IconExePath）。</summary>
    Exe
}

/// <summary>
/// 备选启动项：一个路径/URI + 该路径专属的启动参数。
/// JSON 兼容规则：旧配置为字符串数组（仅路径，参数为空）；
/// 新配置写为 {"path":"...","arguments":"..."} 对象数组。
/// </summary>
[JsonConverter(typeof(LaunchCandidateConverter))]
public class LaunchCandidate
{
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;

    public LaunchCandidate()
    {
    }

    public LaunchCandidate(string path, string arguments = "")
    {
        Path = path;
        Arguments = arguments;
    }
}

internal class LaunchCandidateConverter : JsonConverter<LaunchCandidate>
{
    public override LaunchCandidate? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 旧格式：纯字符串 = 只有路径、无参数
        if (reader.TokenType == JsonTokenType.String)
            return new LaunchCandidate(reader.GetString() ?? string.Empty);

        var candidate = new LaunchCandidate();
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.TryGetProperty("path", out var pathEl))
            candidate.Path = pathEl.GetString() ?? string.Empty;
        if (doc.RootElement.TryGetProperty("arguments", out var argsEl))
            candidate.Arguments = argsEl.GetString() ?? string.Empty;
        return candidate;
    }

    public override void Write(Utf8JsonWriter writer, LaunchCandidate value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("path", value.Path);
        writer.WriteString("arguments", value.Arguments);
        writer.WriteEndObject();
    }
}

public class AppItem : INotifyPropertyChanged
{
    /// <summary>全局开关（来自设置-外观"图标悬浮提示"，默认关闭）。变更时由 MainViewModel 统一刷新各条目的 ToolTipText。</summary>
    public static bool ToolTipsEnabled = false;

    private string _name = string.Empty;
    private string _path = string.Empty;
    private string _iconPath = string.Empty;
    private string _iconExePath = string.Empty;
    private string _category = string.Empty;
    private string _arguments = string.Empty;
    private string _matchKey = string.Empty;
    private string _iconKey = string.Empty;
    private string _remark = string.Empty;
    private string _iconMode = nameof(IconSourceMode.Auto);
    private List<LaunchCandidate> _fallbackPaths = new();
    private Bitmap? _icon;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(nameof(Path)); OnPropertyChanged(nameof(ToolTipText)); }
    }

    public string IconPath
    {
        get => _iconPath;
        set { _iconPath = value; OnPropertyChanged(nameof(IconPath)); }
    }

    /// <summary>
    /// 用于提取图标的 EXE 文件路径（IconMode=Exe 时生效）。
    /// 与启动路径无关，仅用于从该程序提取真实图标。持久化到配置文件。
    /// </summary>
    public string IconExePath
    {
        get => _iconExePath;
        set { _iconExePath = value; OnPropertyChanged(nameof(IconExePath)); }
    }

    public string Category
    {
        get => _category;
        set { _category = value; OnPropertyChanged(nameof(Category)); }
    }

    public string Arguments
    {
        get => _arguments;
        set { _arguments = value; OnPropertyChanged(nameof(Arguments)); }
    }

    /// <summary>
    /// 默认应用的自动解析标识（如 seewo.easinote / vrchat）。
    /// 为空表示用户自定义应用，不参与自动匹配。
    /// </summary>
    public string MatchKey
    {
        get => _matchKey;
        set { _matchKey = value; OnPropertyChanged(nameof(MatchKey)); }
    }

    /// <summary>
    /// 内置预设图标标识（对应嵌入资源 Assets/presets/&lt;key&gt;.png）。
    /// 非空时优先用该内置图标，不依赖本机安装的 exe。持久化到配置文件。
    /// </summary>
    public string IconKey
    {
        get => _iconKey;
        set { _iconKey = value; OnPropertyChanged(nameof(IconKey)); }
    }

    /// <summary>
    /// 图标来源模式：<see cref="IconSourceMode"/> 的名称（Auto/Preset/Custom/Exe）。
    /// 默认 Auto（从目标 exe 自动提取）。持久化到配置文件。
    /// </summary>
    public string IconMode
    {
        get => _iconMode;
        set { _iconMode = value; OnPropertyChanged(nameof(IconMode)); }
    }

    /// <summary>
    /// 备注：鼠标悬停时优先显示的悬浮文本。持久化到配置文件。
    /// </summary>
    public string Remark
    {
        get => _remark;
        set { _remark = value; OnPropertyChanged(nameof(Remark)); OnPropertyChanged(nameof(ToolTipText)); }
    }

    /// <summary>
    /// 备选启动项列表（每项含路径 + 该路径专属参数）。主路径启动失败时按顺序自动尝试，全部失败才提示。
    /// 持久化到配置文件（兼容旧的纯字符串数组格式）。
    /// </summary>
    public List<LaunchCandidate> FallbackPaths
    {
        get => _fallbackPaths;
        set { _fallbackPaths = value ?? new List<LaunchCandidate>(); OnPropertyChanged(nameof(FallbackPaths)); }
    }

    /// <summary>
    /// 悬浮提示文本（仅当设置中开启"图标悬浮提示"时生效）：
    /// 备注非空 → 备注内容；否则 → 解析后的 exe 绝对路径（URI 协议原样显示）。
    /// </summary>
    [JsonIgnore]
    public string? ToolTipText => ToolTipsEnabled ? BuildToolTipText() : null;

    private string? BuildToolTipText()
    {
        if (!string.IsNullOrWhiteSpace(_remark))
            return _remark.Trim();

        var cleaned = Services.ProcessLauncher.CleanPath(_path);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        if (Services.ProcessLauncher.IsUri(cleaned))
            return cleaned;

        return Services.ProcessLauncher.ResolvePath(cleaned) ?? cleaned;
    }

    /// <summary>全局开关变更后调用，通知悬浮文本重新求值。</summary>
    public void RefreshToolTip() => OnPropertyChanged(nameof(ToolTipText));

    /// <summary>从目标 exe/快捷方式提取的真实图标（不写入配置文件）。</summary>
    [JsonIgnore]
    public Bitmap? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(ShowPlaceholder));
        }
    }

    /// <summary>暂无真实图标时显示默认占位符；已有图标时隐藏占位符。</summary>
    [JsonIgnore]
    public bool ShowPlaceholder => Icon == null;

    [JsonIgnore]
    public bool HasCustomIcon => !string.IsNullOrEmpty(IconPath) && System.IO.File.Exists(IconPath);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
