using System;
using System.Collections.Generic;
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

    public EditAppDialog(AppItem? appItem = null)
    {
        AppItem = appItem ?? new AppItem();
        Title = appItem == null ? "添加应用" : "编辑应用";
        Width = 400;
        Height = 340;
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
            Spacing = 16
        };

        // Title
        var title = new TextBlock
        {
            Text = Title,
            FontSize = 18,
            FontWeight = FontWeight.Bold
        };
        panel.Children.Add(title);

        // Name field
        var namePanel = new StackPanel { Spacing = 4 };
        namePanel.Children.Add(new TextBlock { Text = "应用名称", FontSize = 12 });
        _nameTextBox = new TextBox
        {
            PlaceholderText = "请输入应用名称",
            Margin = new Thickness(0, 4, 0, 0)
        };
        namePanel.Children.Add(_nameTextBox);
        panel.Children.Add(namePanel);

        // Path field
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

        // Arguments field
        var argsPanel = new StackPanel { Spacing = 4 };
        argsPanel.Children.Add(new TextBlock { Text = "启动参数（可选）", FontSize = 12 });
        _argsTextBox = new TextBox
        {
            PlaceholderText = "命令行参数",
            Margin = new Thickness(0, 4, 0, 0)
        };
        argsPanel.Children.Add(_argsTextBox);
        panel.Children.Add(argsPanel);

        // Buttons
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
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
        {
            await ShowMessage("提示", "请输入应用名称");
            return;
        }

        AppItem.Name = _nameTextBox.Text?.Trim() ?? string.Empty;
        AppItem.Path = ProcessLauncher.CleanPath(_pathTextBox.Text) ?? string.Empty;
        AppItem.Arguments = _argsTextBox.Text?.Trim() ?? string.Empty;

        Close(true);
    }

    /// <summary>打开系统文件选择器挑选 exe，并将本地路径写入路径框（不做 URL 转码）。</summary>
    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
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
            return;

        var file = files[0];
        // TryGetLocalPath 是 Avalonia 官方扩展方法，返回未经 URL 转码的本地路径，
        // 能正确保留中文与特殊符号，避免出现 "%E5%BC%A0" 这类编码路径导致 File.Exists 判定失败。
        var localPath = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(localPath))
        {
            _pathTextBox.Text = localPath;
        }
        else
        {
            _pathTextBox.Text = file.Path.LocalPath;
        }
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
