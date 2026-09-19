using System.Windows;
using System.Windows.Controls;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.Multiplayer;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 「选择节点」窗口。把 HMOL 联机模块里的三块功能合成一个窗口：内置节点下拉、节点测速、自定义节点增删。
/// 房间内所有人必须用同一个节点，所以这里把延迟与地址都摆出来，方便房主统一指定。
/// </summary>
public partial class NodePickerWindow : Window
{
    private readonly List<string> _customNodes;
    private readonly List<NodeRow> _rows = [];

    private string _selected;

    public NodePickerWindow(string currentAddress, IEnumerable<string> customNodes)
    {
        InitializeComponent();

        _selected = EasyTierNodes.Resolve(currentAddress);
        _customNodes = [.. customNodes
            .Where(node => !string.IsNullOrWhiteSpace(node))
            .Select(node => node.Trim())];

        BuildRows();

        Loaded += (_, _) => _ = TestAllAsync();
    }

    /// <summary>「应用」时选中的节点地址。</summary>
    public string SelectedNode => _selected;

    /// <summary>窗口内可能被改过的自定义节点列表，由调用方决定是否持久化。</summary>
    public IReadOnlyList<string> CustomNodes => _customNodes;

    /// <summary>列表里的一行：地址 + 用来显示延迟的文本块。</summary>
    private sealed record NodeRow(string Address, TextBlock Latency);

    // ————— 列表 —————

    private void BuildRows()
    {
        PanNodes.Children.Clear();
        _rows.Clear();

        var addresses = new List<string>();

        foreach (var node in EasyTierNodes.BuiltIn) addresses.Add(node.Address);

        foreach (var custom in _customNodes)
            if (!addresses.Contains(custom, StringComparer.OrdinalIgnoreCase)) addresses.Add(custom);

        // 设置文件里存过、但既不是内置也不在自定义列表里的地址（例如手改过配置），
        // 补进自定义列表，用户才能看见并把它删掉
        if (_selected.Length > 0 && !addresses.Contains(_selected, StringComparer.OrdinalIgnoreCase))
        {
            _customNodes.Add(_selected);
            addresses.Add(_selected);
        }

        foreach (var address in addresses) PanNodes.Children.Add(BuildRow(address));

        ApplySelectionVisual();
    }

    private UIElement BuildRow(string address)
    {
        var builtIn = EasyTierNodes.BuiltIn.FirstOrDefault(
            node => string.Equals(node.Address, address, StringComparison.OrdinalIgnoreCase));

        var description = new TextBlock
        {
            FontSize = 13,
            Text = builtIn?.Description ?? "自定义节点"
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

        var addressText = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 11,
            Text = address
        };
        addressText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");

        var left = new StackPanel();
        left.Children.Add(description);
        left.Children.Add(addressText);

        var latency = new TextBlock
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11.5,
            Text = "未测速"
        };
        latency.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(latency, 1);
        content.Children.Add(left);
        content.Children.Add(latency);

        var button = new OutlineButton
        {
            Content = content,
            Tag = address,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tone = ButtonTone.Outline
        };
        button.Click += OnNodeClick;

        _rows.Add(new NodeRow(address, latency));
        return button;
    }

    private void ApplySelectionVisual()
    {
        foreach (var button in PanNodes.Children.OfType<OutlineButton>())
        {
            var address = button.Tag as string ?? string.Empty;
            button.Tone = string.Equals(address, _selected, StringComparison.OrdinalIgnoreCase)
                ? ButtonTone.Solid
                : ButtonTone.Outline;
        }
    }

    private void OnNodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string address }) return;

        _selected = address;
        ApplySelectionVisual();
    }

    // ————— 测速 —————

    private void OnRetestClick(object sender, RoutedEventArgs e) => _ = TestAllAsync();

    /// <summary>并发测所有节点。单节点最坏 2.5 秒 TCP + 2.5 秒 PING，串行会让用户等太久。</summary>
    private async Task TestAllAsync()
    {
        LabTestState.Text = "测速中…";

        var tasks = _rows.Select(async row =>
        {
            row.Latency.Text = "测速中…";

            var milliseconds = await NodeSpeedTest.MeasureAsync(row.Address, 2500);

            row.Latency.Text = milliseconds is null ? "不可达" : $"{milliseconds} ms";
            row.Latency.SetResourceReference(TextBlock.ForegroundProperty,
                milliseconds is null ? "Text.Disabled" : "Text.Secondary");
        });

        await Task.WhenAll(tasks);

        LabTestState.Text = "测速完成（延迟仅供横向比较）";
    }

    // ————— 自定义节点 —————

    private void OnAddCustomClick(object sender, RoutedEventArgs e)
    {
        var address = TxtCustom.Text.Trim();
        if (address.Length == 0) return;

        if (!_customNodes.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            _customNodes.Add(address);
            _selected = address;
        }

        TxtCustom.Text = string.Empty;
        BuildRows();
        _ = TestAllAsync();
    }

    private void OnRemoveCustomClick(object sender, RoutedEventArgs e)
    {
        var removed = _customNodes.RemoveAll(
            node => string.Equals(node, _selected, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            MessageBox.Show(this, "选中的是内置节点，内置节点不能删除。\n先选中一个自定义节点再删。",
                "选择节点", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selected = EasyTierNodes.DefaultAddress;
        BuildRows();
    }

    // ————— 应用 —————

    private void OnApplyClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
