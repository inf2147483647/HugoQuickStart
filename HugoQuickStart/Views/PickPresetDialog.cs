using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HugoQuickStart.Models;
using HugoQuickStart.Services;

namespace HugoQuickStart.Views;

/// <summary>
/// "添加预设"选择窗口：分组展示快捷入口预设与希沃应用预设，
/// 点击某一项后经 <see cref="SelectedPreset"/> 返回对应 AppItem 并关闭。
/// </summary>
public partial class PickPresetDialog : Window
{
    /// <summary>用户选中的预设（未选择则为 null）。</summary>
    public AppItem? SelectedPreset { get; private set; }

    public PickPresetDialog()
    {
        Title = "添加预设";
        Width = 460;
        Height = 600;
        MinWidth = 400;
        MinHeight = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 18),
            Spacing = 6
        };
        root.Content = panel;

        panel.Children.Add(BuildSectionTitle("快捷入口预设"));
        foreach (var preset in AppPresets.QuickEntryPresets)
        {
            panel.Children.Add(BuildPresetButton(preset));
        }

        panel.Children.Add(BuildSectionTitle("希沃应用预设"));
        foreach (var preset in AppPresets.XiwoPresets)
        {
            panel.Children.Add(BuildPresetButton(preset));
        }

        Content = root;
    }

    private static TextBlock BuildSectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
        Margin = new Thickness(0, 10, 0, 2)
    };

    private Button BuildPresetButton(AppPreset preset)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 2, 0, 2)
        };

        var icon = AppIconLoader.LoadBuiltinIcon(preset.IconKey);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        if (icon != null)
        {
            grid.Children.Add(new Image
            {
                Source = icon,
                Width = 28,
                Height = 28,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var textPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = preset.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = preset.Path,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);
        button.Content = grid;

        button.Click += (_, _) =>
        {
            SelectedPreset = new AppItem
            {
                Name = preset.Name,
                Path = preset.Path,
                Category = preset.Category,
                IconPath = "",
                IconKey = preset.IconKey,
                Icon = icon
            };
            Close(true);
        };
        return button;
    }
}
