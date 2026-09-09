using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HugoQuickStart.Models;
using HugoQuickStart.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HugoQuickStart.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppConfigService _configService;
    
    [ObservableProperty]
    private ObservableCollection<AppItem> _quickEntries = new();
    
    [ObservableProperty]
    private ObservableCollection<AppItem> _xiwoApps = new();
    
    [ObservableProperty]
    private bool _isEditMode;

    /// <summary>标题栏编辑按钮字形：编辑模式显示对勾（表示“完成编辑”），否则显示笔。</summary>
    public string EditButtonGlyph => IsEditMode ? "\uE73E" : "\uE70F";

    /// <summary>标题栏编辑按钮提示：编辑模式为“完成编辑”，否则为“编辑”。</summary>
    public string EditButtonToolTip => IsEditMode ? "完成编辑" : "编辑";

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(EditButtonGlyph));
        OnPropertyChanged(nameof(EditButtonToolTip));
    }
    
    [ObservableProperty]
    private bool _autoStart;

    /// <summary>是否拦截希沃服务助手（SeewoServiceAssistant.exe）的右下角悬浮窗。</summary>
    [ObservableProperty]
    private bool _blockSeewoAssistantWindow = true;

    /// <summary>主题色模式（"System"/"Dark"/"Light"）。变更时立即应用并持久化。</summary>
    [ObservableProperty]
    private string _themeMode = "System";

    /// <summary>界面缩放：主界面总体大小倍率，0.80–1.50，默认 1.00。变更时立即应用并持久化。</summary>
    [ObservableProperty]
    private double _uiScale = 1.0;

    /// <summary>图标悬浮提示开关：悬停显示备注（为空则显示 exe 绝对路径）。默认关闭。</summary>
    [ObservableProperty]
    private bool _showIconToolTips = false;

    /// <summary>点击冷却开关：同一图标在冷却时间内不可重复启动，避免手速过快导致应用多重启动。默认关闭。</summary>
    [ObservableProperty]
    private bool _launchCooldownEnabled = false;

    /// <summary>点击冷却时长（秒），0.1–5.0，默认 1.0。</summary>
    [ObservableProperty]
    private double _launchCooldownSeconds = 1.0;

    partial void OnLaunchCooldownEnabledChanged(bool value)
    {
        if (!_suppressThemeSave)
            SaveSettingsOnly();
    }

    partial void OnLaunchCooldownSecondsChanged(double value)
    {
        // 钳制到合法范围，并按 0.1 步长取整（滑条 snap 之外的入口兜底）
        var clamped = Math.Round(Math.Clamp(value, 0.1, 5.0) / 0.1) * 0.1;
        if (Math.Abs(clamped - value) > 1e-9)
        {
            LaunchCooldownSeconds = clamped;
            return;
        }

        if (!_suppressThemeSave)
            SaveSettingsOnly();
    }

    /// <summary>同一图标最近一次成功启动的时间（点击冷却判断依据）。</summary>
    private readonly Dictionary<AppItem, DateTime> _lastLaunchTimes = new();

    partial void OnShowIconToolTipsChanged(bool value)
    {
        AppItem.ToolTipsEnabled = value;
        foreach (var item in QuickEntries.Concat(XiwoApps))
            item.RefreshToolTip();
        if (!_suppressThemeSave)
            SaveSettingsOnly();
    }

    partial void OnUiScaleChanged(double value)
    {
        // 钳制到合法范围，并按 0.05 步长取整（滑条 snap 之外的入口兜底）
        var clamped = Math.Round(Math.Clamp(value, 0.80, 1.50) / 0.05) * 0.05;
        if (Math.Abs(clamped - value) > 1e-9)
        {
            UiScale = clamped;
            return;
        }

        OnPropertyChanged(nameof(ScaledWindowWidth));
        if (!_suppressThemeSave)
            SaveSettingsOnly();
    }

    /// <summary>主界面窗口宽度 = 基准 370 × 缩放（保证缩放后仍容下一行 5 个图标）。</summary>
    public double ScaledWindowWidth => 370 * UiScale;

    partial void OnThemeModeChanged(string value)
    {
        ThemeService.Apply(ThemeService.Parse(value));
        if (!_suppressThemeSave)
            SaveSettingsOnly();
    }
    
    [ObservableProperty]
    private bool _showSettings;
    
    [ObservableProperty]
    private string _statusMessage = string.Empty;
    
    [ObservableProperty]
    private bool _hasStatus;

    /// <summary>
    /// 宿主窗口注入的协议导航提示回调：返回 true 表示用户确认继续启动。
    /// 用于 classisland://、secrandom:// 等协议快捷入口启动前的提示。
    /// </summary>
    public Func<string, Task<bool>>? UrlNavigationPromptHost { get; set; }

    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _resolveTimer;
    /// <summary>加载配置期间抑制主题变更触发的回写，避免启动即重复保存。</summary>
    private bool _suppressThemeSave;

    private const int ResolveIntervalSeconds = 20;

    public MainViewModel()
    {
        _configService = new AppConfigService();
        LoadConfig();
        NormalizeMatchKeys();
        _ = InitialLoadAsync();
    }

    private async Task InitialLoadAsync()
    {
        await ResolveDefaultsAsync();
        StartResolveTimer();
    }

    /// <summary>为旧配置/历史条目补齐 MatchKey（无则按名称推断），便于自动匹配。</summary>
    private void NormalizeMatchKeys()
    {
        foreach (var app in QuickEntries.Concat(XiwoApps))
        {
            if (string.IsNullOrWhiteSpace(app.MatchKey))
                app.MatchKey = DefaultAppResolver.InferMatchKeyFromName(app.Name);
        }
    }

    private static string GetMatchKey(AppItem app) =>
        !string.IsNullOrWhiteSpace(app.MatchKey)
            ? app.MatchKey
            : DefaultAppResolver.InferMatchKeyFromName(app.Name);

    /// <summary>
    /// 解析默认应用中缺失/失效的路径：枚举已安装应用→匹配→找到对应 EXE→绑定（图标在刷新时映射）。
    /// 当前路径有效（文件存在或为 URI）时不覆盖，尊重用户手动设置的路径。
    /// </summary>
    public async Task ResolveDefaultsAsync()
    {
        var matched = QuickEntries.Concat(XiwoApps)
            .Where(app => !string.IsNullOrWhiteSpace(GetMatchKey(app)))
            .ToList();
        if (matched.Count == 0)
            return;

        // 注册表/Steam 扫描放到后台线程，避免阻塞 UI
        var resolutions = await Task.Run(() =>
        {
            var dict = new Dictionary<AppItem, string?>();
            foreach (var app in matched)
            {
                if (IsValidPath(app.Path))
                    continue;
                dict[app] = DefaultAppResolver.Resolve(GetMatchKey(app));
            }
            return dict;
        });

        var changed = false;
        foreach (var (app, resolved) in resolutions)
        {
            if (!string.IsNullOrWhiteSpace(resolved) &&
                !string.Equals(resolved, app.Path, StringComparison.OrdinalIgnoreCase))
            {
                app.Path = resolved;
                changed = true;
            }
        }

        if (changed)
            SaveConfig();
        await RefreshIconsAsync();
    }

    /// <summary>路径当前是否可用：存在文件，或为可用的 URI 协议。</summary>
    private static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (ProcessLauncher.IsUri(path))
            return true;
        return ProcessLauncher.ResolvePath(path) != null;
    }

    /// <summary>启动周期扫描，用于检测“运行期间安装/卸载应用”导致的列表变化并重新匹配。</summary>
    private void StartResolveTimer()
    {
        _resolveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ResolveIntervalSeconds)
        };
        _resolveTimer.Tick += async (_, _) =>
        {
            await ResolveDefaultsAsync();
        };
        _resolveTimer.Start();
    }

    private void LoadConfig()
    {
        var config = _configService.Load();
        QuickEntries = new ObservableCollection<AppItem>(config.QuickEntries);
        XiwoApps = new ObservableCollection<AppItem>(config.XiwoApps);
        AutoStart = config.AutoStart;
        BlockSeewoAssistantWindow = config.BlockSeewoAssistantWindow;

        // 加载阶段抑制回写；应用已保存的主题色与界面缩放
        _suppressThemeSave = true;
        ThemeMode = string.IsNullOrWhiteSpace(config.ThemeMode) ? "System" : config.ThemeMode;
        UiScale = Math.Clamp(config.UiScale <= 0 ? 1.0 : config.UiScale, 0.80, 1.50);
        ShowIconToolTips = config.ShowIconToolTips;
        LaunchCooldownEnabled = config.LaunchCooldownEnabled;
        LaunchCooldownSeconds = config.LaunchCooldownSeconds <= 0 ? 1.0 : config.LaunchCooldownSeconds;
        _suppressThemeSave = false;
    }

    public void SaveConfig()
    {
        var config = new AppConfig
        {
            QuickEntries = new List<AppItem>(QuickEntries),
            XiwoApps = new List<AppItem>(XiwoApps),
            AutoStart = AutoStart,
            BlockSeewoAssistantWindow = BlockSeewoAssistantWindow,
            ThemeMode = ThemeMode,
            UiScale = UiScale,
            ShowIconToolTips = ShowIconToolTips,
            LaunchCooldownEnabled = LaunchCooldownEnabled,
            LaunchCooldownSeconds = LaunchCooldownSeconds
        };
        _configService.Save(config);
        // 配置可能被修改（新增/编辑路径），重新加载缺失的图标
        _ = RefreshIconsAsync();
    }

    /// <summary>仅持久化设置项（主题/缩放等），不触发图标刷新。供高频变更的 UI 控件使用。</summary>
    private void SaveSettingsOnly()
    {
        var config = new AppConfig
        {
            QuickEntries = new List<AppItem>(QuickEntries),
            XiwoApps = new List<AppItem>(XiwoApps),
            AutoStart = AutoStart,
            BlockSeewoAssistantWindow = BlockSeewoAssistantWindow,
            ThemeMode = ThemeMode,
            UiScale = UiScale,
            ShowIconToolTips = ShowIconToolTips,
            LaunchCooldownEnabled = LaunchCooldownEnabled,
            LaunchCooldownSeconds = LaunchCooldownSeconds
        };
        _configService.Save(config);
    }

    /// <summary>
    /// 为所有还没有图标的条目补图，按 IconMode 分流：
    /// Custom=用户选择的图片文件（IconPath）；Preset=内嵌资源图标（IconKey）；
    /// Auto=从 EXE 提取真实图标，协议链接回退内置图标。全部失败保持占位符。
    /// </summary>
    public async Task RefreshIconsAsync()
    {
        var pending = QuickEntries
            .Concat(XiwoApps)
            .Where(item => item.Icon == null &&
                           (!string.IsNullOrWhiteSpace(item.Path) ||
                            !string.IsNullOrWhiteSpace(item.IconKey) ||
                            !string.IsNullOrWhiteSpace(item.IconPath)))
            .ToList();

        foreach (var item in pending)
        {
            Bitmap? icon = null;
            var mode = ParseIconMode(item.IconMode);

            if (mode == IconSourceMode.Custom)
            {
                icon = await Task.Run(() => AppIconLoader.LoadFromFile(item.IconPath));
            }
            else if (mode == IconSourceMode.Preset)
            {
                if (!string.IsNullOrWhiteSpace(item.IconKey))
                    icon = AppIconLoader.LoadBuiltinIcon(item.IconKey);
            }
            else
            {
                // Auto：优先从 EXE 提取；协议链接（classisland:// 等）回退内置图标
                if (!string.IsNullOrWhiteSpace(item.Path) && !ProcessLauncher.IsUri(ProcessLauncher.CleanPath(item.Path) ?? ""))
                    icon = await Task.Run(() => AppIconLoader.ExtractForPath(item.Path));

                var iconKey = string.IsNullOrWhiteSpace(item.IconKey) ? GuessBuiltinKey(item.Path) : item.IconKey;
                if (icon == null && iconKey != null)
                    icon = AppIconLoader.LoadBuiltinIcon(iconKey);
            }

            if (icon != null)
            {
                // 回到 UI 线程更新，触发界面刷新
                await Dispatcher.UIThread.InvokeAsync(() => item.Icon = icon);
            }
        }
    }

    /// <summary>解析持久化的 IconMode 字符串；无法识别时按 Auto 处理。</summary>
    private static IconSourceMode ParseIconMode(string? mode) =>
        Enum.TryParse<IconSourceMode>(mode, ignoreCase: true, out var parsed) ? parsed : IconSourceMode.Auto;

    /// <summary>
    /// 按链接协议推测内置图标标识：classisland://、secrandom:// 分别对应同名内嵌图标，
    /// 其它返回 null（走 exe 提取或占位符）。
    /// </summary>
    private static string? GuessBuiltinKey(string? path)
    {
        var cleaned = ProcessLauncher.CleanPath(path);
        if (string.IsNullOrEmpty(cleaned) || !ProcessLauncher.IsUri(cleaned))
            return null;

        var scheme = cleaned[..cleaned.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();
        return scheme is "classisland" or "secrandom" ? scheme : null;
    }

    [RelayCommand]
    private void LaunchApp(AppItem? app)
    {
        if (app == null) return;
        if (IsEditMode)
        {
            ShowStatus("编辑模式下点击无效，请先退出编辑模式");
            return;
        }

        if (string.IsNullOrWhiteSpace(app.Path))
        {
            ShowStatus($"「{app.Name}」尚未设置路径，点击 ✎ 编辑后填写");
            return;
        }

        // 点击冷却：同一图标在冷却时间内不可重复启动，避免手速过快导致应用多重启动
        if (LaunchCooldownEnabled && IsInCooldown(app))
        {
            ShowStatus("应用已经在启动了，请耐心等待~");
            return;
        }

        // 支持绝对路径与相对安装目录的相对路径；主路径失败时按顺序尝试备选路径。
        // 参数跟随路径：每个备选用各自的参数；备选参数留空时沿用全局"启动参数"（兼容旧配置行为）。
        var candidates = new List<LaunchCandidate>
        {
            new(app.Path, app.Arguments)
        };
        foreach (var fb in app.FallbackPaths)
        {
            if (string.IsNullOrWhiteSpace(fb.Path))
                continue;
            if (!candidates.Exists(c => string.Equals(c.Path, fb.Path, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(fb);
        }

        foreach (var candidate in candidates)
        {
            var args = string.IsNullOrEmpty(candidate.Arguments) ? app.Arguments : candidate.Arguments;
            if (!ProcessLauncher.Launch(candidate.Path, args))
                continue;

            // 协议快捷入口（classisland:// 等）不弹对话框、直接启动，
            // 用底部黑色状态条提示确认已开启对应协议注册/导航（与普通应用启动提示一致）。
            var hint = GetProtocolHint(candidate.Path);
            var usedFallback = !string.Equals(candidate.Path, app.Path, StringComparison.OrdinalIgnoreCase);
            var suffix = usedFallback ? "（已使用备选路径）" : string.Empty;
            MarkLaunched(app);
            ShowStatus(hint ?? $"正在启动「{app.Name}」...{suffix}");
            return;
        }

        ShowStatus($"启动失败：「{app.Name}」路径无效，点击 ✎ 修改路径");
    }

    /// <summary>判断同一图标是否正处于点击冷却中。</summary>
    private bool IsInCooldown(AppItem app) =>
        _lastLaunchTimes.TryGetValue(app, out var last) &&
        (DateTime.Now - last).TotalSeconds < LaunchCooldownSeconds;

    /// <summary>记录一次成功的启动时间，作为冷却起算点。</summary>
    private void MarkLaunched(AppItem app) => _lastLaunchTimes[app] = DateTime.Now;

    /// <summary>
    /// 需要"协议导航提示"的协议白名单（键为协议 scheme，值为底部黑色状态条提示文案）。
    /// 协议链接点击后直接启动，同时显示该提示提醒确认对应程序已注册协议；
    /// 无论内置默认条目还是用户自定义的同协议链接，均按此提示。
    /// </summary>
    private static readonly Dictionary<string, string> ProtocolHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["classisland"] = "请确保 ClassIsland 已开启 URL 导航",
        ["secrandom"] = "请确保 SecRandom 已开启 URI 导航",
        ["femboy"] = "请确保 FemboyTest 已开启 URI 注册"
    };

    /// <summary>
    /// 按链接的协议 scheme 返回状态条提示文案；非白名单协议返回 null（走默认"正在启动"提示）。
    /// 对自定义条目同样生效（只认协议，不认条目来源）。
    /// </summary>
    private static string? GetProtocolHint(string path)
    {
        // 先清洗可能包裹的外层引号/空白，再提取 scheme
        var cleaned = ProcessLauncher.CleanPath(path);
        if (string.IsNullOrEmpty(cleaned) || !ProcessLauncher.IsUri(cleaned))
            return null;

        var scheme = cleaned[..cleaned.IndexOf("://", StringComparison.Ordinal)];
        return ProtocolHints.TryGetValue(scheme, out var hint) ? hint : null;
    }

    /// <summary>显示一条短暂的状态提示，数秒后自动消失。</summary>
    public void ShowStatus(string message)
    {
        StatusMessage = message;
        HasStatus = true;

        if (_statusTimer == null)
        {
            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                HasStatus = false;
            };
        }

        _statusTimer.Stop();
        _statusTimer.Start();
    }

    [RelayCommand]
    private void ToggleEditMode()
    {
        IsEditMode = !IsEditMode;
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        ShowSettings = !ShowSettings;
    }

    [RelayCommand]
    private void AddQuickEntry()
    {
        QuickEntries.Add(new AppItem
        {
            Name = "新应用",
            Path = "",
            Category = "快捷入口",
            IconPath = ""
        });
        SaveConfig();
    }

    [RelayCommand]
    private void AddXiwoApp()
    {
        XiwoApps.Add(new AppItem
        {
            Name = "新应用",
            Path = "",
            Category = "希沃软件",
            IconPath = ""
        });
        SaveConfig();
    }

    [RelayCommand]
    private void RemoveApp(AppItem? app)
    {
        if (app == null) return;
        QuickEntries.Remove(app);
        XiwoApps.Remove(app);
        SaveConfig();
    }

    partial void OnAutoStartChanged(bool value)
    {
        StartupService.SetAutoStart(value);
        SaveConfig();
    }

    partial void OnBlockSeewoAssistantWindowChanged(bool value)
    {
        SaveConfig();
    }
}
