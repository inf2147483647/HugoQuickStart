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

    /// <summary>
    /// 希沃悬浮窗最终未能拦截：普通权限连续失败后已自动尝试提权（UAC → 管理员 → SYSTEM），
    /// 若提权后窗口仍可见则置位，界面据此引导用户改用希沃「管家助手显隐」开关从源头关闭。
    /// 仅用于提示，不持久化。
    /// </summary>
    [ObservableProperty]
    private bool _seewoInterceptFailed;

    /// <summary>主题色模式（"System"/"Dark"/"Light"）。变更时立即应用并持久化。</summary>
    [ObservableProperty]
    private string _themeMode = "System";

    /// <summary>界面缩放：主界面总体大小倍率，0.80–1.50，默认 1.00。变更时立即应用并持久化。</summary>
    [ObservableProperty]
    private double _uiScale = 1.0;

    /// <summary>图标悬浮提示开关：悬停显示备注（为空则显示 exe 绝对路径）。默认关闭。</summary>
    [ObservableProperty]
    private bool _showIconToolTips = false;

    /// <summary>隐藏快捷入口：开启后主界面不展示“快捷入口”分组。默认关闭。</summary>
    [ObservableProperty]
    private bool _hideQuickEntries = false;

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
        if (!_isLoadingConfig)
            LogService.Info("设置", $"点击冷却：{(value ? "已启用" : "已禁用")}");
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
        if (!_isLoadingConfig)
            LogService.Info("设置", $"点击冷却时长：{value:F1} 秒");
    }

    /// <summary>同一图标最近一次成功启动的时间（点击冷却判断依据）。</summary>
    private readonly Dictionary<AppItem, DateTime> _lastLaunchTimes = new();

    /// <summary>自动备份周期取值范围（天）。</summary>
    private const int MinBackupIntervalDays = 1;
    private const int MaxBackupIntervalDays = 30;

    /// <summary>自动备份数量上限的取值上限（0 表示无限制，故下限为 0）。</summary>
    private const int MaxBackupCountLimit = 100;

    /// <summary>备份总开关：开启后按周期自动备份配置。默认开启。</summary>
    [ObservableProperty]
    private bool _backupEnabled = true;

    /// <summary>自动备份周期（天），1–30，默认 7。滑条绑定 double，写入时取整并钳制。</summary>
    [ObservableProperty]
    private double _backupIntervalDays = 7;

    /// <summary>自动备份数量上限，0 表示无限制（手动备份不计入），默认 10。</summary>
    [ObservableProperty]
    private double _backupMaxCount = 10;

    /// <summary>备份数量上限的展示文本：0 表示无限制。</summary>
    public string BackupMaxCountText => BackupMaxCount <= 0 ? "无限制" : $"{BackupMaxCount:F0} 份";

    partial void OnBackupEnabledChanged(bool value)
    {
        if (!_suppressThemeSave)
            SaveSettingsOnly();
        if (!_isLoadingConfig)
            LogService.Info("设置", $"配置备份：{(value ? "已启用" : "已禁用")}");
    }

    partial void OnBackupIntervalDaysChanged(double value)
    {
        // 钳制到 1–30 天并取整（滑条 snap 之外的入口兜底）
        var clamped = Math.Round(Math.Clamp(value, MinBackupIntervalDays, MaxBackupIntervalDays));
        if (Math.Abs(clamped - value) > 1e-9)
        {
            BackupIntervalDays = clamped;
            return;
        }

        if (!_suppressThemeSave)
            SaveSettingsOnly();
        if (!_isLoadingConfig)
            LogService.Info("设置", $"备份周期：{value:F0} 天");
    }

    partial void OnBackupMaxCountChanged(double value)
    {
        // 钳制到 0–100 份并取整（0 表示不限制）
        var clamped = Math.Round(Math.Clamp(value, 0, MaxBackupCountLimit));
        if (Math.Abs(clamped - value) > 1e-9)
        {
            BackupMaxCount = clamped;
            return;
        }

        OnPropertyChanged(nameof(BackupMaxCountText));
        if (!_suppressThemeSave)
            SaveSettingsOnly();
        if (!_isLoadingConfig)
            LogService.Info("设置", $"备份数量上限：{BackupMaxCountText}");
    }

    partial void OnShowIconToolTipsChanged(bool value)
    {
        AppItem.ToolTipsEnabled = value;
        foreach (var item in QuickEntries.Concat(XiwoApps))
            item.RefreshToolTip();
        if (!_suppressThemeSave)
            SaveSettingsOnly();
        if (!_isLoadingConfig)
            LogService.Info("设置", $"图标悬浮提示：{(value ? "已启用" : "已禁用")}");
    }

    partial void OnHideQuickEntriesChanged(bool value)
    {
        if (!_suppressThemeSave)
            SaveSettingsOnly();
        if (!_isLoadingConfig)
            LogService.Info("设置", $"隐藏快捷入口：{(value ? "已启用" : "已禁用")}");
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
        if (!_isLoadingConfig)
            LogService.Info("设置", $"界面缩放：{value:F2}");
    }

    /// <summary>主界面窗口宽度 = 基准 370 × 缩放（保证缩放后仍容下一行 5 个图标）。</summary>
    public double ScaledWindowWidth => 370 * UiScale;

    partial void OnThemeModeChanged(string value)
    {
        ThemeService.Apply(ThemeService.Parse(value));
        if (!_suppressThemeSave)
            SaveSettingsOnly();
        if (!_isLoadingConfig)
            LogService.Info("设置", $"主题色：{value}");
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

    /// <summary>加载配置期间为 true：抑制设置项变更日志，避免启动时输出大量"修改设置"记录。</summary>
    private bool _isLoadingConfig;

    private const int ResolveIntervalSeconds = 20;

    public MainViewModel()
    {
        _configService = new AppConfigService();
        LoadConfig();
        NormalizeMatchKeys();
        LogService.Info("初始化",
            $"配置加载完成：快捷入口 {QuickEntries.Count} 项，希沃应用 {XiwoApps.Count} 项");
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
                LogService.Info("初始化", $"自动匹配「{app.Name}」→ {resolved}");
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
        _isLoadingConfig = true;
        var config = _configService.Load();
        QuickEntries = new ObservableCollection<AppItem>(config.QuickEntries);
        XiwoApps = new ObservableCollection<AppItem>(config.XiwoApps);

        // 加载阶段抑制所有属性变更回调的回写：此时其它设置项仍是字段默认值，
        // 若中途触发 SaveConfig() 会用“半默认”状态覆盖磁盘上的用户配置。
        _suppressThemeSave = true;
        AutoStart = config.AutoStart;
        BlockSeewoAssistantWindow = config.BlockSeewoAssistantWindow;
        ThemeMode = string.IsNullOrWhiteSpace(config.ThemeMode) ? "System" : config.ThemeMode;
        UiScale = Math.Clamp(config.UiScale <= 0 ? 1.0 : config.UiScale, 0.80, 1.50);
        ShowIconToolTips = config.ShowIconToolTips;
        HideQuickEntries = config.HideQuickEntries;
        LaunchCooldownEnabled = config.LaunchCooldownEnabled;
        LaunchCooldownSeconds = config.LaunchCooldownSeconds <= 0 ? 1.0 : config.LaunchCooldownSeconds;
        BackupEnabled = config.BackupEnabled;
        BackupIntervalDays = Math.Clamp(config.BackupIntervalDays, MinBackupIntervalDays, MaxBackupIntervalDays);
        BackupMaxCount = Math.Clamp(config.BackupMaxCount, 0, MaxBackupCountLimit);
        _suppressThemeSave = false;
        _isLoadingConfig = false;

        // 全部加载完成后再统一对外“生效”：
        // 1) 以配置文件为权威来源对账自启注册表（加载期回调被抑制，未逐项同步）；
        // 2) 落盘一次，补齐旧版本缺失字段、固化归一化结果，且写入的必是完整状态。
        StartupService.SetAutoStart(AutoStart);
        _configService.Save(BuildConfig());
    }

    /// <summary>把当前内存中的完整状态组装为可持久化的配置对象（单一出口，避免保存时漏字段）。</summary>
    private AppConfig BuildConfig() => new()
    {
        QuickEntries = new List<AppItem>(QuickEntries),
        XiwoApps = new List<AppItem>(XiwoApps),
        AutoStart = AutoStart,
        BlockSeewoAssistantWindow = BlockSeewoAssistantWindow,
        ThemeMode = ThemeMode,
        UiScale = UiScale,
        ShowIconToolTips = ShowIconToolTips,
        HideQuickEntries = HideQuickEntries,
        LaunchCooldownEnabled = LaunchCooldownEnabled,
        LaunchCooldownSeconds = LaunchCooldownSeconds,
        BackupEnabled = BackupEnabled,
        BackupIntervalDays = (int)Math.Round(BackupIntervalDays),
        BackupMaxCount = (int)Math.Round(BackupMaxCount)
    };

    /// <summary>
    /// 应用列表或快捷键可能已变化时触发（保存配置后），由宿主窗口据此重建全局热键注册。
    /// </summary>
    public event Action? HotkeysChanged;

    public void SaveConfig()
    {
        _configService.Save(BuildConfig());
        // 列表/快捷键可能已变化 → 通知宿主重建全局快捷键注册
        HotkeysChanged?.Invoke();
        // 配置可能被修改（新增/编辑路径），重新加载缺失的图标
        _ = RefreshIconsAsync();
    }

    /// <summary>仅持久化设置项（主题/缩放等），不触发图标刷新。供高频变更的 UI 控件使用。</summary>
    private void SaveSettingsOnly() => _configService.Save(BuildConfig());

    /// <summary>
    /// 为所有还没有图标的条目补图，按 IconMode 分流：
    /// Custom=用户选择的图片文件（IconPath）；Preset=内嵌资源图标（IconKey）；
    /// Exe=从用户选择的 exe 文件提取图标（IconExePath）；
    /// Auto=从 EXE 提取真实图标，协议链接回退内置图标。全部失败保持占位符。
    /// </summary>
    public async Task RefreshIconsAsync()
    {
        var pending = QuickEntries
            .Concat(XiwoApps)
            .Where(item => item.Icon == null &&
                           (!string.IsNullOrWhiteSpace(item.Path) ||
                            !string.IsNullOrWhiteSpace(item.IconKey) ||
                            !string.IsNullOrWhiteSpace(item.IconPath) ||
                            !string.IsNullOrWhiteSpace(item.IconExePath)))
            .ToList();

        foreach (var item in pending)
        {
            Bitmap? icon = null;
            var mode = ParseIconMode(item.IconMode);

            if (mode == IconSourceMode.Custom)
            {
                icon = await Task.Run(() => AppIconLoader.LoadFromFile(item.IconPath));
            }
            else if (mode == IconSourceMode.Exe)
            {
                if (!string.IsNullOrWhiteSpace(item.IconExePath))
                    icon = await Task.Run(() => AppIconLoader.ExtractForPath(item.IconExePath));
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
        LogService.Info("点击", $"点击图标「{app.Name}」");
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
            LogService.Info("冷却", $"「{app.Name}」处于冷却中（{LaunchCooldownSeconds:F1} 秒），忽略本次点击");
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
            LogService.Info("启动", $"已启动「{app.Name}」：{candidate.Path}{suffix}");
            ShowStatus(hint ?? $"正在启动「{app.Name}」...{suffix}");
            return;
        }

        LogService.Warn("启动", $"启动失败「{app.Name}」：路径无效");
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
        LogService.Info("设置", "新增快捷入口「新应用」");
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
        LogService.Info("设置", "新增希沃应用「新应用」");
    }

    [RelayCommand]
    private void RemoveApp(AppItem? app)
    {
        if (app == null) return;
        QuickEntries.Remove(app);
        XiwoApps.Remove(app);
        SaveConfig();
        LogService.Info("设置", $"删除应用「{app.Name}」");
    }

    partial void OnAutoStartChanged(bool value)
    {
        // 加载配置期间不回写：此时其它设置项尚未加载完，SaveConfig() 会把半默认状态
        // 覆盖到磁盘。加载完成后由 LoadConfig 统一对账注册表并落盘。
        if (_isLoadingConfig)
            return;

        StartupService.SetAutoStart(value);
        SaveConfig();
        LogService.Info("设置", $"开机自启：{(value ? "已启用" : "已禁用")}");
    }

    partial void OnBlockSeewoAssistantWindowChanged(bool value)
    {
        // 同上：加载期不同步磁盘，避免用半加载状态覆盖用户配置。
        if (_isLoadingConfig)
            return;

        SaveConfig();
        LogService.Info("设置", $"拦截希沃悬浮窗：{(value ? "已启用" : "已禁用")}");
    }
}
