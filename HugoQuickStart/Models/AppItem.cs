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

    /// <summary>从目标 exe/快捷方式提取的真实图标（不写入配置文件）。</summary>
    [JsonIgnore]
    public Bitmap? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(nameof(Icon)); }
    }

    [JsonIgnore]
    public bool HasCustomIcon => !string.IsNullOrEmpty(IconPath) && System.IO.File.Exists(IconPath);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
