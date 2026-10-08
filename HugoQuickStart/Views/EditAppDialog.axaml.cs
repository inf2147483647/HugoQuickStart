using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
    private TextBox _remarkTextBox = null!;
    private ComboBox _iconModeCombo = null!;
    private ComboBox _presetIconCombo = null!;
    private DockPanel _customIconRow = null!;
    private TextBox _customIconTextBox = null!;
    private DockPanel _exeIconRow = null!;
    private TextBox _exeIconTextBox = null!;
    private StackPanel _fallbackPanel = null!;
    private TextBox _hotkeyTextBox = null!;
    private TextBlock _hotkeyHintText = null!;

    /// <summary>快捷键提示的默认文案；按下不支持的按键时临时替换为告警文案。</summary>
    private const string HotkeyHintDefault =
        "支持 Ctrl / Alt / Shift 与字母、数字、F1–F12 组合；单独按字母或数字会自动转为 Ctrl+Alt+字母/数字。";

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
        // 限制最大高度：内容过长时由内部滚动容器承接，避免底部“保存/取消”被顶出屏幕。
        MaxHeight = ResolveMaxHeight();

        BuildUI();
        LoadData();
    }

    /// <summary>
    /// 计算对话框最大高度：屏幕工作区逻辑高度的 88%（不低于 320）。
    /// 构造期若尚未取得屏幕信息（无平台句柄），退回保守默认值：
    /// 即便估值偏小也只是更早出现滚动条，按钮始终可见。
    /// </summary>
    private double ResolveMaxHeight()
    {
        try
        {
            var screen = Screens.Primary;
            if (screen != null && screen.Scaling > 0)
                return Math.Max(320, screen.WorkingArea.Height / screen.Scaling * 0.88);
        }
        catch
        {
            // 忽略：使用下方默认值
        }

        return 600;
    }

    private void BuildUI()
    {
        // 字段区（不含底部按钮）：由外层滚动容器承载，左右与上方留 20 边距
        var panel = new StackPanel
        {
            Margin = new Thickness(20, 20, 20, 8),
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
        _iconModeCombo.Items.Add(new ComboBoxItem { Content = "从 EXE 提取" });
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

        // 从 EXE 提取：exe 文件路径 + 浏览
        _exeIconRow = new DockPanel
        {
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false
        };
        var exeBrowseButton = new Button
        {
            Content = "浏览...",
            MinWidth = 72,
            Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(exeBrowseButton, Dock.Right);
        exeBrowseButton.Click += ExeIconBrowseButton_Click;
        _exeIconRow.Children.Add(exeBrowseButton);

        _exeIconTextBox = new TextBox
        {
            PlaceholderText = "选择用于提取图标的程序文件（.exe）"
        };
        _exeIconRow.Children.Add(_exeIconTextBox);
        iconPanel.Children.Add(_exeIconRow);

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

        // ---- 备注 ----
        var remarkPanel = new StackPanel { Spacing = 4 };
        remarkPanel.Children.Add(new TextBlock { Text = "备注（可选）", FontSize = 12 });
        _remarkTextBox = new TextBox
        {
            PlaceholderText = "悬停提示内容；留空则悬停显示程序绝对路径（需在设置中开启图标悬浮提示）",
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 0
        };
        remarkPanel.Children.Add(_remarkTextBox);
        panel.Children.Add(remarkPanel);

        // ---- 快捷键 ----
        var hotkeyPanel = new StackPanel { Spacing = 4 };
        hotkeyPanel.Children.Add(new TextBlock { Text = "快捷键（可选）", FontSize = 12 });

        var hotkeyRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var clearHotkeyButton = new Button
        {
            Content = "清除",
            MinWidth = 72,
            Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(clearHotkeyButton, Dock.Right);
        clearHotkeyButton.Click += (_, _) =>
        {
            _hotkeyTextBox.Text = string.Empty;
            ResetHotkeyHint();
        };
        hotkeyRow.Children.Add(clearHotkeyButton);

        // 只读文本框仅作"按键捕获"显示区：KeyDown 里自行解析组合键，不接受文本输入
        _hotkeyTextBox = new TextBox
        {
            IsReadOnly = true,
            PlaceholderText = "点此按下组合键，例如 Ctrl+Alt+A"
        };
        _hotkeyTextBox.KeyDown += HotkeyTextBox_KeyDown;
        hotkeyRow.Children.Add(_hotkeyTextBox);
        hotkeyPanel.Children.Add(hotkeyRow);

        _hotkeyHintText = new TextBlock
        {
            Text = HotkeyHintDefault,
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        };
        hotkeyPanel.Children.Add(_hotkeyHintText);
        panel.Children.Add(hotkeyPanel);

        // ---- Buttons ----
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            // 按钮行固定在窗口底部（不随字段区滚动），左右下与字段区对齐
            Margin = new Thickness(20, 4, 20, 20)
        };

        var cancelButton = new Button { Content = "取消", MinWidth = 80 };
        cancelButton.Click += (s, e) => Close();
        buttonPanel.Children.Add(cancelButton);

        var saveButton = new Button { Content = "保存", MinWidth = 80 };
        saveButton.Click += SaveButton_Click;
        buttonPanel.Children.Add(saveButton);

        // 按钮行 dock 在底部并始终可见；字段区放入滚动容器，内容超长时滚动而不挤压按钮
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        root.Children.Add(buttonPanel);
        root.Children.Add(scroll);

        Content = root;
    }

    private void LoadData()
    {
        _nameTextBox.Text = AppItem.Name;
        _pathTextBox.Text = AppItem.Path;
        _argsTextBox.Text = AppItem.Arguments;
        _remarkTextBox.Text = AppItem.Remark;
        _hotkeyTextBox.Text = AppItem.Hotkey;

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
        _exeIconTextBox.Text = AppItem.IconExePath;

        foreach (var fallback in AppItem.FallbackPaths)
            AddFallbackRow(fallback);
    }

    private void IconModeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var mode = (IconSourceMode)Math.Max(0, _iconModeCombo.SelectedIndex);
        _presetIconCombo.IsVisible = mode == IconSourceMode.Preset;
        _customIconRow.IsVisible = mode == IconSourceMode.Custom;
        _exeIconRow.IsVisible = mode == IconSourceMode.Exe;
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
        AppItem.Remark = _remarkTextBox.Text?.Trim() ?? string.Empty;
        AppItem.Hotkey = _hotkeyTextBox.Text?.Trim() ?? string.Empty;
        AppItem.FallbackPaths = CollectFallbackPaths();

        var mode = (IconSourceMode)Math.Max(0, _iconModeCombo.SelectedIndex);
        AppItem.IconMode = mode.ToString();
        AppItem.IconKey = mode == IconSourceMode.Preset
            ? (_presetIconCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty
            : string.Empty;
        AppItem.IconPath = mode == IconSourceMode.Custom
            ? ProcessLauncher.CleanPath(_customIconTextBox.Text) ?? string.Empty
            : string.Empty;
        AppItem.IconExePath = mode == IconSourceMode.Exe
            ? ProcessLauncher.CleanPath(_exeIconTextBox.Text) ?? string.Empty
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
    private async System.Threading.Tasks.Task<string?> PickExeFileAsync(string title = "选择应用")
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
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

    /// <summary>挑选用于提取图标的 exe 文件。</summary>
    private async void ExeIconBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var localPath = await PickExeFileAsync("选择用于提取图标的程序");
        if (!string.IsNullOrEmpty(localPath))
            _exeIconTextBox.Text = localPath;
    }

    // ================= 快捷键录入 =================

    /// <summary>
    /// 捕获快捷键：按下"修饰键 + 主键"的组合即写入显示框。
    /// 仅按下修饰键时不生效（继续等待主键）；Backspace / Delete 清除已设快捷键。
    /// </summary>
    private void HotkeyTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // 该文本框只读，此处完全接管按键，避免光标移动/选择等默认行为
        e.Handled = true;

        if (IsModifierKey(e.Key))
            return;

        if (e.Key is Key.Back or Key.Delete)
        {
            _hotkeyTextBox.Text = string.Empty;
            ResetHotkeyHint();
            return;
        }

        var keyToken = KeyToToken(e.Key);
        if (keyToken == null)
        {
            ShowHotkeyHint("该按键不支持：请使用字母、数字或 F1–F12 作为主键。");
            return;
        }

        var modifiers = e.KeyModifiers;
        var text = GlobalHotkeyService.BuildText(
            ctrl: modifiers.HasFlag(KeyModifiers.Control),
            alt: modifiers.HasFlag(KeyModifiers.Alt),
            shift: modifiers.HasFlag(KeyModifiers.Shift),
            keyToken: keyToken);

        if (string.IsNullOrEmpty(text))
        {
            ShowHotkeyHint("该按键不支持：请使用字母、数字或 F1–F12 作为主键。");
            return;
        }

        _hotkeyTextBox.Text = text;
        ResetHotkeyHint();
    }

    /// <summary>仅修饰键判定：它们自身不能构成快捷键，需与主键组合。</summary>
    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin
        or Key.System or Key.None;

    /// <summary>
    /// Avalonia 按键 → 快捷键 token：字母（A–Z）、数字（0–9，含小键盘）、功能键（F1–F24）。
    /// 按枚举成员名解析，不依赖枚举取值的连续性；不支持的主键返回 null。
    /// </summary>
    private static string? KeyToToken(Key key)
    {
        var name = key.ToString();

        // 字母：Key.A → "A"
        if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z')
            return name;

        // 主键盘数字：Key.D0 → "0"
        if (name.Length == 2 && name[0] == 'D' && name[1] >= '0' && name[1] <= '9')
            return name[1].ToString();

        // 小键盘数字：Key.NumPad0 → "0"（与主键盘数字同义）
        if (name.StartsWith("NumPad", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan(6), out var pad) && pad is >= 0 and <= 9)
            return pad.ToString();

        // 功能键：Key.F1 → "F1"
        if (name.Length is >= 2 and <= 3 && name[0] == 'F' &&
            int.TryParse(name.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
            return "F" + fn;

        return null;
    }

    private void ResetHotkeyHint()
    {
        _hotkeyHintText.Text = HotkeyHintDefault;
        _hotkeyHintText.Opacity = 0.7;
    }

    private void ShowHotkeyHint(string message)
    {
        _hotkeyHintText.Text = message;
        _hotkeyHintText.Opacity = 1;
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
