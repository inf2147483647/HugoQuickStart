using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
    
    [ObservableProperty]
    private bool _autoStart;
    
    [ObservableProperty]
    private bool _showSettings;
    
    [ObservableProperty]
    private string _statusMessage = string.Empty;
    
    [ObservableProperty]
    private bool _hasStatus;

    private DispatcherTimer? _statusTimer;

    public MainViewModel()
    {
        _configService = new AppConfigService();
        LoadConfig();
        _ = RefreshIconsAsync();
    }

    private void LoadConfig()
    {
        var config = _configService.Load();
        QuickEntries = new ObservableCollection<AppItem>(config.QuickEntries);
        XiwoApps = new ObservableCollection<AppItem>(config.XiwoApps);
        AutoStart = config.AutoStart;
    }

    public void SaveConfig()
    {
        var config = new AppConfig
        {
            QuickEntries = new List<AppItem>(QuickEntries),
            XiwoApps = new List<AppItem>(XiwoApps),
            AutoStart = AutoStart
        };
        _configService.Save(config);
        // 配置可能被修改（新增/编辑路径），重新加载缺失的图标
        _ = RefreshIconsAsync();
    }

    /// <summary>
    /// 为所有还没有图标的条目在后台提取 EXE 真实图标。
    /// 提取失败或路径无效的条目保持占位符。
    /// </summary>
    public async Task RefreshIconsAsync()
    {
        var pending = QuickEntries
            .Concat(XiwoApps)
            .Where(item => item.Icon == null && !string.IsNullOrWhiteSpace(item.Path))
            .ToList();

        foreach (var item in pending)
        {
            var icon = await Task.Run(() => AppIconLoader.ExtractForPath(item.Path));
            if (icon != null)
            {
                // 回到 UI 线程更新，触发界面刷新
                await Dispatcher.UIThread.InvokeAsync(() => item.Icon = icon);
            }
        }
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

        // 支持绝对路径与相对安装目录的相对路径
        var launched = ProcessLauncher.Launch(app.Path, app.Arguments);
        if (launched)
        {
            ShowStatus($"正在启动「{app.Name}」...");
        }
        else
        {
            ShowStatus($"启动失败：「{app.Name}」路径无效，点击 ✎ 修改路径");
        }
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
}
