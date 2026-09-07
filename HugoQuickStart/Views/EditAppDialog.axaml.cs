using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HugoQuickStart.Models;
using HugoQuickStart.Services;

namespace HugoQuickStart.Views;

public partial class EditAppDialog : Window
{
    public AppItem AppItem { get; private set; }

    private TextBox _nameTextBox = null!;
    private TextBox _pathTextBox = null!;
    private TextBox _argsTextBox = null!;
    private ComboBox _iconModeCombo = null!;
    private ComboBox _presetIconCombo = null!;
    private DockPanel _customIconRow = null!;
    private TextBox _customIconTextBox = null!;
    private StackPanel _fallbackPanel = null!;

    /// <summary>内置预设图标清单（key 对应 Assets/presets/&lt;key&gt;.png）。</summary>
    private static readonly (string Key, string Name)[] PresetIcons =
    {
        ("classisland", "ClassIsland"),
        ("secrandom", "SecRandom"),
        ("easinote5", "希沃白板5"),
        ("easicamera", "希沃视频展台"),
        ("easinote5c", "希沃轻白板"),
        ("vrchat", "VRChat")
    };

    public EditAppDialog(AppItem? appItem = null)
    {
        AppItem = appItem ?? new AppItem();
        Title = appItem == null ? "添加应用" : "编辑应用";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildUI();
        LoadData();
    }

    private void BuildUI()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12
        };

        // Title
        var title = new TextBlock
        {
            Text = Title,
            FontSize = 18,
            FontWeight = FontWeight.Bold
        };
        panel.Children.Add(title);

        // ---- 图标 ----
        var iconPanel = new StackPanel { Spacing = 4 };
        iconPanel.Children.Add(new TextBlock { Text = "图标", FontSize = 12 });

        _iconModeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        _iconModeCombo.Items.Add(new ComboBoxItem { Content = "自动获取" });
        _iconModeCombo.Items.Add(new ComboBoxItem { Content = "预设" });
        _iconModeCombo.Items.Add(new ComboBoxItem { Content = "自定义" });
        _iconModeCombo.SelectionChanged += IconModeCombo_SelectionChanged;
        iconPanel.Children.Add(_iconModeCombo);

        // 预设：内置图标下拉
        _presetIconCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false
        };
        foreach (var (key, name) in PresetIcons)
            _presetIconCombo.Items.Add(new ComboBoxItem { Content = name, Tag = key });
        iconPanel.Children.Add(_presetIconCombo);

        // 自定义：图片文件路径 + 浏览
        _customIconRow = new DockPanel
        {
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false
        };
        var iconBrowseButton = new Button
        {
            Content = "浏览...",
            MinWidth = 72,
            Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(iconBrowseButton, Dock.Right);
        iconBrowseButton.Click += IconBrowseButton_Click;
        _customIconRow.Children.Add(iconBrowseButton);

        _customIconTextBox = new TextBox
        {
            PlaceholderText = "选择图标图片文件（png / jpg / ico 等）"
        };
        _customIconRow.Children.Add(_customIconTextBox);
        iconPanel.Children.Add(_customIconRow);

        panel.Children.Add(iconPanel);

        // ---- 应用名称 ----
        var namePanel = new StackPanel { Spacing = 4 };
        namePanel.Children.Add(new TextBlock { Text = "应用名称", FontSize = 12 });
        _nameTextBox = new TextBox
        {
            PlaceholderText = "请输入应用名称",
            Margin = new Thickness(0, 4, 0, 0)
        };
        namePanel.Children.Add(_nameTextBox);
        panel.Children.Add(namePanel);

        // ---- 文件路径或URI ----
        var pathPanel = new StackPanel { Spacing = 4 };
        pathPanel.Children.Add(new TextBlock { Text = "文件路径或URI", FontSize = 12 });

        var pathRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var browseButton = new Button
        {
            Content = "浏览...",
            MinWidth = 72,
            Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(browseButton, Dock.Right);
        browseButton.Click += BrowseButton_Click;
        pathRow.Children.Add(browseButton);

        _pathTextBox = new TextBox
        {
            PlaceholderText = "例如：C:\\Program Files\\App\\app.exe 或 classisland://app/settings",
            AcceptsReturn = false
        };
        pathRow.Children.Add(_pathTextBox);

        pathPanel.Children.Add(pathRow);
        panel.Children.Add(pathPanel);

        // ---- 备选路径（动态行：文本框 + 浏览 + 删除备选） ----
        var fallbackHeader = new TextBlock
        {
            Text = "文件路径或URI（备选）",
            FontSize = 12
        };
        panel.Children.Add(fallbackHeader);

        _fallbackPanel = new StackPanel { Spacing = 4 };
        panel.Children.Add(_fallbackPanel);

        var addFallbackRowPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var addFallbackButton = new Button { Content = "+ 添加备选" };
        addFallbackButton.Click += (_, _) => AddFallbackRow(new LaunchCandidate());
        addFallbackRowPanel.Children.Add(addFallbackButton);
        addFallbackRowPanel.Children.Add(new TextBlock
        {
            Text = "前一个启动失败时，会自动尝试下一个路径。",
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(addFallbackRowPanel);

        // ---- 启动参数 ----
        var argsPanel = new StackPanel { Spacing = 4 };
        argsPanel.Children.Add(new TextBlock { Text = "启动参数（可选）", FontSize = 12 });
        _argsTextBox = new TextBox
        {
            PlaceholderText = "命令行参数",
            Margin = new Thickness(0, 4, 0, 0)
        };
        argsPanel.Children.Add(_argsTextBox);
        panel.Children.Add(argsPanel);

        // ---- Buttons ----
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var cancelButton = new Button { Content = "取消", MinWidth = 80 };
        cancelButton.Click += (s, e) => Close();
        buttonPanel.Children.Add(cancelButton);

        var saveButton = new Button { Content = "保存", MinWidth = 80 };
        saveButton.Click += SaveButton_Click;
        buttonPanel.Children.Add(saveButton);

        panel.Children.Add(buttonPanel);

        Content = panel;
    }

    private void LoadData()
    {
        _nameTextBox.Text = AppItem.Name;
        _pathTextBox.Text = AppItem.Path;
        _argsTextBox.Text = AppItem.Arguments;

        var mode = Enum.TryParse<IconSourceMode>(AppItem.IconMode, ignoreCase: true, out var parsed)
            ? parsed : IconSourceMode.Auto;
        _iconModeCombo.SelectedIndex = (int)mode;

        if (!string.IsNullOrWhiteSpace(AppItem.IconKey))
        {
            for (var i = 0; i < _presetIconCombo.Items.Count; i++)
            {
                if (((ComboBoxItem)_presetIconCombo.Items[i]!).Tag as string == AppItem.IconKey)
                {
                    _presetIconCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        if (_presetIconCombo.SelectedIndex < 0)
            _presetIconCombo.SelectedIndex = 0;

        _customIconTextBox.Text = AppItem.IconPath;

        foreach (var fallback in AppItem.FallbackPaths)
            AddFallbackRow(fallback);
    }

    private void IconModeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var mode = (IconSourceMode)Math.Max(0, _iconModeCombo.SelectedIndex);
        _presetIconCombo.IsVisible = mode == IconSourceMode.Preset;
        _customIconRow.IsVisible = mode == IconSourceMode.Custom;
    }

    /// <summary>新增一行备选（第一行：路径 + 浏览 + 删除；第二行：该路径专属参数）。</summary>
    private void AddFallbackRow(LaunchCandidate candidate)
    {
        var row = new StackPanel
        {
            Spacing = 2,
            Tag = "fallback"
        };

        // ---- 第一行：路径 + 浏览 + 删除 ----
        var pathLine = new DockPanel();

        var deleteButton = new Button
        {
            Content = "删除备选",
            MinWidth = 72,
            Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(deleteButton, Dock.Right);
        deleteButton.Click += (_, _) => _fallbackPanel.Children.Remove(row);
        pathLine.Children.Add(deleteButton);

        var browseButton = new Button
        {
            Content = "浏览...",
            MinWidth = 72,
            Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(browseButton, Dock.Right);
        pathLine.Children.Add(browseButton);

        var pathBox = new TextBox
        {
            Text = candidate.Path,
            PlaceholderText = "备选文件路径或URI"
        };
        browseButton.Click += async (_, _) =>
        {
            var picked = await PickExeFileAsync();
            if (!string.IsNullOrEmpty(picked))
                pathBox.Text = picked;
        };
        pathLine.Children.Add(pathBox);
        row.Children.Add(pathLine);

        // ---- 第二行：该备选专属启动参数 ----
        row.Children.Add(new TextBox
        {
            Text = candidate.Arguments,
            PlaceholderText = "该备选的启动参数（可选，留空则用下方全局参数）",
            FontSize = 12
        });

        _fallbackPanel.Children.Add(row);
    }

    /// <summary>收集所有路径非空的备选启动项（含各自参数，按路径去重）。</summary>
    private List<LaunchCandidate> CollectFallbackPaths()
    {
        var result = new List<LaunchCandidate>();
        foreach (var child in _fallbackPanel.Children)
        {
            if (child is not StackPanel row || row.Tag is not "fallback" || row.Children.Count == 0)
                continue;

            if (row.Children[0] is not DockPanel pathLine)
                continue;

            // 第一行唯一的 TextBox 即路径框；第二行是参数框
            var pathTb = pathLine.Children.OfType<TextBox>().FirstOrDefault();
            var argsTb = row.Children.OfType<TextBox>().Skip(1).FirstOrDefault();
            if (pathTb == null)
                continue;

            var value = ProcessLauncher.CleanPath(pathTb.Text);
            if (string.IsNullOrWhiteSpace(value) ||
                result.Exists(c => string.Equals(c.Path, value, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(new LaunchCandidate(value, argsTb?.Text?.Trim() ?? string.Empty));
        }
        return result;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
        {
            _ = ShowMessage("提示", "请输入应用名称");
            return;
        }

        AppItem.Name = _nameTextBox.Text?.Trim() ?? string.Empty;
        AppItem.Path = ProcessLauncher.CleanPath(_pathTextBox.Text) ?? string.Empty;
        AppItem.Arguments = _argsTextBox.Text?.Trim() ?? string.Empty;
        AppItem.FallbackPaths = CollectFallbackPaths();

        var mode = (IconSourceMode)Math.Max(0, _iconModeCombo.SelectedIndex);
        AppItem.IconMode = mode.ToString();
        AppItem.IconKey = mode == IconSourceMode.Preset
            ? (_presetIconCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty
            : string.Empty;
        AppItem.IconPath = mode == IconSourceMode.Custom
            ? ProcessLauncher.CleanPath(_customIconTextBox.Text) ?? string.Empty
            : string.Empty;

        // 清空旧图标，保存后由 RefreshIconsAsync 按新模式重新加载
        AppItem.Icon = null;

        Close(true);
    }

    /// <summary>打开系统文件选择器挑选 exe，并将本地路径写入路径框（不做 URL 转码）。</summary>
    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var localPath = await PickExeFileAsync();
        if (!string.IsNullOrEmpty(localPath))
            _pathTextBox.Text = localPath;
    }

    /// <summary>挑选可执行文件，返回未转码的本地路径；取消返回 null。</summary>
    private async System.Threading.Tasks.Task<string?> PickExeFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择应用",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("可执行程序") { Patterns = new[] { "*.exe" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count == 0)
            return null;

        var file = files[0];
        // TryGetLocalPath 是 Avalonia 官方扩展方法，返回未经 URL 转码的本地路径，
        // 能正确保留中文与特殊符号，避免出现 "%E5%BC%A0" 这类编码路径导致 File.Exists 判定失败。
        return file.TryGetLocalPath() ?? file.Path.LocalPath;
    }

    /// <summary>挑选图标图片文件。</summary>
    private async void IconBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图标图片",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片文件") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.ico" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count == 0)
            return;

        var file = files[0];
        _customIconTextBox.Text = file.TryGetLocalPath() ?? file.Path.LocalPath;
    }

    private System.Threading.Tasks.Task ShowMessage(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 300,
            Height = 150,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16
        };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var okButton = new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 80 };
        okButton.Click += (s, e) => dialog.Close();
        panel.Children.Add(okButton);

        dialog.Content = panel;
        return dialog.ShowDialog(this);
    }
}
