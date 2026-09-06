using System.ComponentModel;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;

namespace HugoQuickStart.Models;

public class AppItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _path = string.Empty;
    private string _iconPath = string.Empty;
    private string _category = string.Empty;
    private string _arguments = string.Empty;
    private string _matchKey = string.Empty;
    private string _iconKey = string.Empty;
    private Bitmap? _icon;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(nameof(Path)); }
    }

    public string IconPath
    {
        get => _iconPath;
        set { _iconPath = value; OnPropertyChanged(nameof(IconPath)); }
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
