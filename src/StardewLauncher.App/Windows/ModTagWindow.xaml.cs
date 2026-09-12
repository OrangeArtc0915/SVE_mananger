using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 标签管理弹窗：为某个 Mod 勾选 / 取消标签，并新建、重命名、改色、删除标签。
/// 弹窗只改动 ModTagStore，界面刷新由 ModTagStore.Changed 通知调用方。
/// </summary>
public partial class ModTagWindow : Window
{
    /// <summary>颜色下拉项：ColorKey 与显示名。</summary>
    private sealed record ColorOption(string Key, string Name);

    private static readonly IReadOnlyList<ColorOption> Colors =
    [
        new("Accent", "强调"),
        new("Warn", "警告"),
        new("Danger", "危险"),
        new("Success", "成功")
    ];

    private readonly string _modKey;

    public ModTagWindow(string modKey, string modTitle)
    {
        InitializeComponent();

        _modKey = modKey ?? string.Empty;

        LabMod.Text = string.IsNullOrWhiteSpace(_modKey)
            ? modTitle
            : $"{modTitle}（{_modKey}）";
        LabMod.ToolTip = LabMod.Text;

        CmbNewTagColor.ItemsSource = Colors;
        CmbNewTagColor.DisplayMemberPath = nameof(ColorOption.Name);
        CmbNewTagColor.SelectedValuePath = nameof(ColorOption.Key);
        CmbNewTagColor.SelectedIndex = 0;

        BuildTagRows();
    }

    /// <summary>重建标签行（新建 / 删除后需要）。</summary>
    private void BuildTagRows()
    {
        PanTags.Children.Clear();

        if (ModTagStore.Tags.Count == 0)
        {
            PanTags.Children.Add(new TextBlock
            {
                Text = "还没有任何标签，在下面输入名字新建一个。",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text.Secondary")
            });

            return;
        }

        var assigned = ModTagStore.TagsOf(_modKey)
            .Select(tag => tag.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in ModTagStore.Tags)
            PanTags.Children.Add(BuildTagRow(tag, assigned.Contains(tag.Id)));
    }

    private FrameworkElement BuildTagRow(ModTag tag, bool isAssigned)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 勾选 = 打标签
        var check = new CheckBox
        {
            IsChecked = isAssigned,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = tag,
            ToolTip = "勾选 / 取消该标签"
        };
        check.Checked += OnAssignChanged;
        check.Unchecked += OnAssignChanged;
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        // 名字（可直接改名）
        var name = new TextBox
        {
            Text = tag.Name,
            Margin = new Thickness(8, 0, 8, 0),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("Surface.Card"),
            BorderBrush = (Brush)FindResource("Border.Default"),
            CaretBrush = (Brush)FindResource("Text.Primary"),
            Foreground = (Brush)FindResource("Text.Primary"),
            Tag = tag,
            ToolTip = "改完按回车或点到别处生效"
        };
        name.LostFocus += OnTagNameLostFocus;
        name.KeyDown += OnTagNameKeyDown;
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // 颜色
        var color = new ComboBox
        {
            ItemsSource = Colors,
            DisplayMemberPath = nameof(ColorOption.Name),
            SelectedValuePath = nameof(ColorOption.Key),
            Width = 100,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("Surface.Card"),
            BorderBrush = (Brush)FindResource("Border.Default"),
            Foreground = (Brush)FindResource("Text.Primary"),
            Tag = tag,
            ToolTip = "标签颜色"
        };
        color.SelectedValue = tag.ColorKey;
        color.SelectionChanged += OnTagColorChanged;
        Grid.SetColumn(color, 2);
        grid.Children.Add(color);

        // 删除
        var delete = new RoundIconButton
        {
            Icon = "lucide/trash-2",
            IconSize = 14,
            Tone = ButtonTone.Plain,
            Width = 30,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = tag,
            ToolTip = "删除标签"
        };
        delete.Click += OnDeleteTagClick;
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);

        return grid;
    }

    // ————— 事件 —————

    private void OnAssignChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ModTag tag } check) return;

        if (check.IsChecked == true) ModTagStore.Assign(_modKey, tag);
        else ModTagStore.Unassign(_modKey, tag);
    }

    private void OnTagNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (sender is FrameworkElement element)
            element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void OnTagNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: ModTag tag } box) return;
        if (string.Equals(box.Text?.Trim(), tag.Name, StringComparison.Ordinal)) return;

        // 只改名，不重建行：重建会销毁正在失焦的输入框，容易引发二次事件
        ModTagStore.Rename(tag, box.Text ?? string.Empty);
    }

    private void OnTagColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: ModTag tag } combo) return;
        if (combo.SelectedValue is not string colorKey) return;

        // 只改色，不重建行，避免在事件处理中移除触发事件的下拉框
        ModTagStore.SetColor(tag, colorKey);
    }

    private void OnDeleteTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModTag tag }) return;

        var answer = MessageBox.Show(
            this,
            $"确定删除标签「{tag.Name}」吗？\n所有 Mod 上的该标签都会被移除。",
            "删除标签", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        ModTagStore.Delete(tag);
        BuildTagRows();
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var name = TxtNewTagName.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "请先输入标签名字。", "新建标签", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var colorKey = CmbNewTagColor.SelectedValue as string ?? "Accent";
        ModTagStore.Create(name, colorKey);

        TxtNewTagName.Clear();
        BuildTagRows();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
