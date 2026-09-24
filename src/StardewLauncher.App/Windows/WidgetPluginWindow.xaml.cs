using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Views;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Plugins;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 主页扩展管理：列出扩展目录里扫到的扩展，由用户手动启用或停用。
/// 扫描只读文件信息，未启用的扩展不会被执行任何代码。
/// </summary>
public partial class WidgetPluginWindow : LauncherWindow
{
    public WidgetPluginWindow()
    {
        InitializeComponent();

        Reload();
    }

    private void Reload()
    {
        LabDirectory.Text = WidgetPluginCatalog.WidgetsDirectory;

        // 每次进来都重新扫一遍：用户可能刚在资源管理器里放进去或删掉 dll
        WidgetPluginCatalog.Refresh();

        BuildRows();
    }

    private void BuildRows()
    {
        PanPlugins.Children.Clear();

        var entries = WidgetPluginCatalog.All;

        PanEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in entries) PanPlugins.Children.Add(BuildRow(entry));
    }

    private FrameworkElement BuildRow(WidgetPluginEntry entry)
    {
        var card = new SurfaceCard { HasHoverEffect = false, Margin = new Thickness(0, 0, 0, 8) };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleRow.Children.Add(BuildBadge(entry));
        info.Children.Add(titleRow);

        var detail = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Text = Describe(entry)
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty,
            entry.Error is null ? "Text.Tertiary" : "Status.Danger");
        info.Children.Add(detail);

        grid.Children.Add(info);

        var toggle = new OutlineButton
        {
            Width = 84,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Content = entry.Enabled ? "停用" : "启用",
            Tone = entry.Enabled ? ButtonTone.Outline : ButtonTone.Solid
        };
        toggle.Click += (_, _) => Toggle(entry);

        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        card.Content = grid;

        return card;
    }

    private static FrameworkElement BuildBadge(WidgetPluginEntry entry)
    {
        var (text, backgroundKey, foregroundKey) = entry switch
        {
            { Error: not null } => ("加载失败", "Status.DangerSoft", "Status.Danger"),
            { Enabled: true } => ("已启用", "Accent.Faint", "Accent.Base"),
            _ => ("未启用", "Surface.Sunken", "Text.Secondary")
        };

        var label = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);

        var badge = new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(1000),
            Child = label
        };
        badge.SetResourceReference(Border.BackgroundProperty, backgroundKey);

        return badge;
    }

    private static string Describe(WidgetPluginEntry entry)
    {
        if (entry.Error is not null) return $"加载失败：{entry.Error}";

        var file = Path.GetFileName(entry.AssemblyPath);
        var folder = Path.GetFileName(entry.Folder);

        var size = "未知大小";
        try
        {
            size = $"{new FileInfo(entry.AssemblyPath).Length / 1024d:0} KB";
        }
        catch (Exception ex)
        {
            Log.Warn($"读取扩展大小失败：{ex.Message}");
        }

        if (entry.Enabled) return $"已启用 · {file}（{size}） · 小组件 id：{entry.WidgetId}";

        // 未启用时不去加载它，所以只有文件名可看
        var location = string.Equals(folder, "Widgets", StringComparison.OrdinalIgnoreCase) ? file : $"{folder}\\{file}";
        return $"未启用 · {location}（{size}）";
    }

    private void Toggle(WidgetPluginEntry entry)
    {
        if (entry.Enabled)
        {
            WidgetPluginCatalog.Disable(entry);

            Log.Info($"已停用扩展 {entry.Key}");
            BuildRows();
            return;
        }

        if (WidgetPluginCatalog.Enable(entry, out var error))
        {
            Log.Info($"已启用扩展 {entry.Key}");
        }
        else
        {
            Dialogs.Warn(this, error ?? "未知原因", "无法启用这个扩展");
        }

        BuildRows();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        WidgetPluginCatalog.EnsureDirectory();
        ShellHelper.OpenFolder(WidgetPluginCatalog.WidgetsDirectory);
    }

    private void OnRescanClick(object sender, RoutedEventArgs e) => Reload();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
