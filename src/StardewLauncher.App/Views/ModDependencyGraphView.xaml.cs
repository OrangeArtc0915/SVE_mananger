using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.App.Views;

/// <summary>
/// Mod 依赖关系图（工具箱里的一个工具）：把 manifest 里写的前置关系画成分层图，
/// 每往下一层就是「依赖上一层」。缺的前置会画成红色占位节点，一眼能看出缺什么。
/// 作为工具箱的子视图存在，进入时由宿主调用 <see cref="Activate"/>。
/// </summary>
public partial class ModDependencyGraphView : UserControl
{
    private const double NodeWidth = 176;
    private const double NodeHeight = 44;
    private const double HorizontalGap = 20;
    private const double VerticalGap = 46;
    private const double CanvasPadding = 20;

    private DependencyGraph _graph = new();
    private IReadOnlyList<ModEntry> _mods = [];
    private string? _modsDirectory;
    private DependencyNode? _selected;
    private double _zoom = 1;
    private bool _busy;

    public ModDependencyGraphView()
    {
        InitializeComponent();
        Log.SetModule("依赖图");
    }

    /// <summary>宿主切到这个工具时调用：重新扫一遍。</summary>
    public void Activate() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_busy) return;

        _busy = true;
        SetBusy(true);

        try
        {
            if (InstanceStore.Current is not { } instance)
            {
                _graph = new DependencyGraph();
                _mods = [];
                _modsDirectory = null;
                Render();

                ShowSummary("还没有实例",
                    "先到「实例」页新建或选一个，这里才知道该读哪个 Mods 目录。", false);

                return;
            }

            var directory = instance.ModsDirectory;
            _modsDirectory = directory;

            var result = await Task.Run(() =>
            {
                var scan = ModScanner.Scan(directory);
                DependencyResolver.Evaluate(scan.Mods);
                return (Scan: scan, Graph: DependencyGraphBuilder.Build(scan.Mods));
            });

            _mods = result.Scan.Mods;
            _graph = result.Graph;
            _selected = null;
            BarDetail.Visibility = Visibility.Collapsed;

            Render();
            Describe();
        }
        catch (Exception ex)
        {
            Log.Error("生成依赖关系图失败", ex);
            ShowSummary($"生成失败：{ex.Message}", "可以点右上角「重新加载」再试一次。", true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void Describe()
    {
        if (_graph.Nodes.Count == 0)
        {
            ShowSummary("没有可画的依赖关系",
                $"扫了 {_mods.Count} 个 Mod，但没有一个在 manifest 里写了前置。", false);

            return;
        }

        var missing = _graph.Nodes.Count(node => node.IsMissing);
        var disabled = _graph.Nodes.Count(node => node.Mod is { IsEnabled: false });

        var detail = $"共 {_mods.Count} 个 Mod，其中 {_graph.Nodes.Count - _graph.IsolatedCount} 个参与依赖关系，" +
                     $"分 {_graph.LayerCount} 层。层号越小越先加载。";

        if (missing > 0) detail += $" 有 {missing} 个前置没有安装。";
        if (disabled > 0) detail += $" 有 {disabled} 个参与依赖的 Mod 处于禁用状态。";
        if (_graph.IsolatedCount > 0) detail += $" 另有 {_graph.IsolatedCount} 个不参与依赖，默认不画出来。";

        ShowSummary(missing > 0 ? "发现缺失的前置" : "依赖关系已展开", detail, missing > 0);
    }

    // ————— 绘制 —————
    //
    // 布局是「层 = 行」：第 0 层在最上面，往下依次是被依赖方。
    // 层内按显示名横向排开，同层不画连线，连线一律从上层底部拉出一条贝塞尔竖线到下层顶部。

    private void Render()
    {
        Surface.Children.Clear();

        var nodes = ChkOnlyConnected.IsChecked == true
            ? _graph.Nodes.Where(node => !node.Isolated).ToList()
            : [.. _graph.Nodes];

        nodes = [.. nodes.OrderBy(node => node.Layer).ThenBy(node => node.Order)];

        var visible = new HashSet<DependencyNode>(nodes);

        if (nodes.Count == 0)
        {
            Surface.Width = 0;
            Surface.Height = 0;
            LabEmpty.Visibility = Visibility.Visible;
            return;
        }

        LabEmpty.Visibility = Visibility.Collapsed;

        // 先把每个节点的位置算出来，画线时直接取值。
        // 过滤之后同层里可能空出位置，所以按层重新紧凑排一遍，而不是直接用 node.Order。
        var positions = new Dictionary<DependencyNode, Point>();
        var layers = nodes.GroupBy(node => node.Layer).OrderBy(group => group.Key).ToList();

        var widest = 0;

        for (var row = 0; row < layers.Count; row++)
        {
            var column = 0;

            foreach (var node in layers[row].OrderBy(node => node.Order))
            {
                positions[node] = new Point(
                    CanvasPadding + column * (NodeWidth + HorizontalGap),
                    CanvasPadding + row * (NodeHeight + VerticalGap));

                column++;
            }

            widest = Math.Max(widest, column);
        }

        // 连线画在节点下面，从下层节点顶部进、上层节点底部出
        var edgeLayer = new Canvas();
        Surface.Children.Add(edgeLayer);

        foreach (var edge in _graph.Edges)
        {
            if (!visible.Contains(edge.From) || !visible.Contains(edge.To)) continue;
            if (!positions.TryGetValue(edge.From, out var from)) continue;
            if (!positions.TryGetValue(edge.To, out var to)) continue;

            edgeLayer.Children.Add(CreateEdgePath(edge, from, to));
        }

        foreach (var node in nodes)
        {
            Surface.Children.Add(CreateNodeVisual(node, positions[node]));
        }

        Surface.Width = CanvasPadding * 2 + widest * NodeWidth + Math.Max(0, widest - 1) * HorizontalGap;

        Surface.Height = CanvasPadding * 2 + layers.Count * NodeHeight
                         + Math.Max(0, layers.Count - 1) * VerticalGap;

        ApplyZoom();
    }

    private Path CreateEdgePath(DependencyEdge edge, Point from, Point to)
    {
        var startX = from.X + NodeWidth / 2;
        var startY = from.Y + NodeHeight;
        var endX = to.X + NodeWidth / 2;
        var endY = to.Y;

        var bend = Math.Max(18, (endY - startY) / 2);

        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(startX, startY), IsClosed = false };

        figure.Segments.Add(new BezierSegment(
            new Point(startX, startY + bend),
            new Point(endX, endY - bend),
            new Point(endX, endY),
            true));

        geometry.Figures.Add(figure);

        var highlighted = _selected is not null
                          && (ReferenceEquals(_selected, edge.From) || ReferenceEquals(_selected, edge.To));

        var path = new Path
        {
            Data = geometry,
            StrokeThickness = highlighted ? 2.4 : 1.2,
            Opacity = _selected is null ? 0.55 : highlighted ? 1 : 0.18,
            Stroke = (Brush)FindResource(edge.Satisfied ? "Accent.Base" : "Status.Warn")
        };

        if (!edge.Satisfied)
        {
            path.StrokeDashArray = new DoubleCollection { 4, 3 };
            path.Opacity = _selected is null ? 0.9 : path.Opacity;
        }

        return path;
    }

    private Border CreateNodeVisual(DependencyNode node, Point position)
    {
        var selected = ReferenceEquals(_selected, node);

        var border = new Border
        {
            Width = NodeWidth,
            Height = NodeHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = Cursors.Hand,
            Tag = node,
            ToolTip = node.IsMissing
                ? $"{node.MissingId}\n这个前置没有安装"
                : $"{node.Label}\nUniqueID：{node.Mod!.UniqueId}\n{node.Mod.FolderPath}"
        };

        border.SetResourceReference(Border.BackgroundProperty,
            node.IsMissing ? "Status.WarnSoft" : "Surface.Card");

        border.SetResourceReference(Border.BorderBrushProperty,
            selected ? "Accent.Base" : node.IsMissing ? "Status.Warn" : "Border.Strong");

        // 禁用的 Mod 压暗一档，但不要压到看不清名字
        if (node.Mod is { IsEnabled: false }) border.Opacity = 0.62;

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };

        var name = new TextBlock
        {
            Text = node.Label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

        var sub = new TextBlock
        {
            Text = node.SubLabel,
            FontSize = 10.5,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        sub.SetResourceReference(TextBlock.ForegroundProperty,
            node.IsMissing ? "Status.Warn" : "Text.Secondary");

        text.Children.Add(name);
        text.Children.Add(sub);

        border.Child = text;

        Canvas.SetLeft(border, position.X);
        Canvas.SetTop(border, position.Y);

        border.MouseLeftButtonUp += OnNodeClick;

        return border;
    }

    // ————— 交互 —————

    private void OnNodeClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: DependencyNode node }) return;

        _selected = ReferenceEquals(_selected, node) ? null : node;

        Render();
        ShowDetail();
    }

    private void ShowDetail()
    {
        if (_selected is null)
        {
            BarDetail.Visibility = Visibility.Collapsed;
            return;
        }

        var dependencies = _graph.Edges.Where(edge => ReferenceEquals(edge.From, _selected)).ToList();
        var dependents = _graph.Edges.Where(edge => ReferenceEquals(edge.To, _selected)).ToList();

        LabDetailTitle.Text = _selected.IsMissing
            ? $"{_selected.Label}（没有安装）"
            : $"{_selected.Label} · {_selected.SubLabel}";

        LabDetailDeps.Text = dependencies.Count == 0
            ? "不依赖其他 Mod"
            : "依赖：" + string.Join("、", dependencies.Select(edge =>
                edge.Satisfied
                    ? $"{edge.To.Label}{(string.IsNullOrWhiteSpace(edge.RequiredVersion) ? "" : $" ≥ {edge.RequiredVersion}")}"
                    : $"{edge.To.MissingId}（缺失）"));

        LabDetailUsers.Text = dependents.Count == 0
            ? "没有被其他 Mod 依赖"
            : "被依赖：" + string.Join("、", dependents.Select(edge => edge.From.Label));

        BarDetail.Visibility = Visibility.Visible;
    }

    private void OnCloseDetailClick(object sender, RoutedEventArgs e)
    {
        _selected = null;
        BarDetail.Visibility = Visibility.Collapsed;
        Render();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Render();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.15);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.15);

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        if (Surface.Width <= 0 || Surface.Height <= 0) return;

        var width = Scroll.ViewportWidth / Surface.Width;
        var height = Scroll.ViewportHeight / Surface.Height;

        SetZoom(Math.Clamp(Math.Min(width, height), 0.3, 1.6));
    }

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, 0.3, 2.5);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        // 用 LayoutTransform 而不是 RenderTransform：滚动范围会跟着缩放一起算
        Surface.LayoutTransform = Math.Abs(_zoom - 1) < 0.001
            ? Transform.Identity
            : new ScaleTransform(_zoom, _zoom);
    }

    // ————— 摘要与忙闲 —————

    private void ShowSummary(string title, string detail, bool problems)
    {
        LabSummaryTitle.Text = title;
        LabSummaryDetail.Text = detail;

        IconSummary.Icon = problems ? "lucide/triangle-alert" : "lucide/network";
        IconSummary.IconBrush = (Brush)FindResource(problems ? "Status.Warn" : "Status.Success");
    }

    private void SetBusy(bool busy)
    {
        BtnRefresh.IsEnabled = !busy;
        ChkOnlyConnected.IsEnabled = !busy;
    }
}
