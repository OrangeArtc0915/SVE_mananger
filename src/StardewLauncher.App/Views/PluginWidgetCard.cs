using System.Windows;
using System.Windows.Controls;
using StardewLauncher.Api;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Plugins;

namespace StardewLauncher.App.Views;

/// <summary>
/// 扩展提供的主页卡片。外壳、间距、配色、进度条全部由启动器按现有卡片风格画，
/// 扩展只提供数据与内容描述 —— 卡片上没有任何可点击的行，扩展也不能借它唤起别的程序。
/// </summary>
internal sealed class PluginWidgetCard : SurfaceCard
{
    /// <summary>单次刷新的上限。扩展卡死不能把主页拖住。</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(15);

    /// <summary>一张卡片最多渲染多少行、每行最多多少字，防止坏扩展把主页撑爆。</summary>
    private const int MaxItems = 40;
    private const int MaxTextLength = 600;

    private readonly WidgetPluginEntry _entry;
    private readonly WidgetContext _context;
    private readonly StackPanel _body = new();

    private bool _refreshing;

    public PluginWidgetCard(WidgetPluginEntry entry)
    {
        _entry = entry;
        _context = new WidgetContext();

        Title = entry.Instance?.Title ?? entry.Key;
        Padding = new Thickness(0);
        MinHeight = 110;
        Content = _body;

        ShowMessage("加载中…");
    }

    /// <summary>该卡片对应的 widget id，与布局设置里的 id 一致。</summary>
    public string WidgetId => _entry.WidgetId;

    /// <summary>取一次内容。扩展抛异常、超时都只显示在卡片里，不影响启动器。</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;

        if (_entry.Instance is not { } plugin)
        {
            ShowMessage("扩展未加载");
            return;
        }

        _refreshing = true;

        try
        {
            using var source = new CancellationTokenSource(RefreshTimeout);

            // 丢到线程池：扩展里若有同步阻塞的代码，也只堵住那个线程，不堵界面
            var content = await Task.Run(() => plugin.RefreshAsync(_context, source.Token), source.Token);

            Render(content);
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"扩展「{Title}」刷新超时");
            ShowMessage($"刷新超时（超过 {RefreshTimeout.TotalSeconds:0} 秒）");
        }
        catch (Exception ex)
        {
            Log.Warn($"扩展「{Title}」刷新失败：{ex.Message}");
            ShowMessage($"扩展出错：{ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ————— 渲染 —————

    private void Render(WidgetContent? content)
    {
        _body.Children.Clear();

        if (content is null)
        {
            AddMessage("扩展没有返回内容");
            return;
        }

        _body.Children.Add(BuidHeader(content.Subtitle));

        var items = content.Items ?? [];

        if (items.Count == 0)
        {
            AddMessage(string.IsNullOrWhiteSpace(content.EmptyText) ? "暂时没有内容" : content.EmptyText);
        }
        else
        {
            foreach (var item in items.Take(MaxItems)) _body.Children.Add(BuildRow(item));

            if (items.Count > MaxItems)
                Log.Warn($"扩展「{Title}」返回了 {items.Count} 行，只显示前 {MaxItems} 行");
        }

        if (!string.IsNullOrWhiteSpace(content.Footnote))
        {
            var footnote = new TextBlock
            {
                Text = Shorten(content.Footnote),
                FontSize = 11,
                Margin = new Thickness(16, 8, 16, 0),
                TextWrapping = TextWrapping.Wrap
            };
            footnote.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            _body.Children.Add(footnote);
        }

        _body.Children.Add(new Border { Height = 10 });
    }

    /// <summary>副标题 + 刷新按钮。</summary>
    private FrameworkElement BuidHeader(string? subtitle)
    {
        var row = new Grid { Margin = new Thickness(16, 12, 10, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var label = new TextBlock
            {
                Text = Shorten(subtitle),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            row.Children.Add(label);
        }

        var refresh = new RoundIconButton
        {
            Icon = "lucide/refresh-cw",
            IconSize = 12,
            Width = 24,
            Height = 24,
            Tone = ButtonTone.Plain,
            ToolTip = "刷新这个扩展"
        };
        refresh.Click += (_, _) => _ = RefreshAsync();
        Grid.SetColumn(refresh, 1);
        row.Children.Add(refresh);

        return row;
    }

    private FrameworkElement BuildRow(WidgetItem item) => item.Kind switch
    {
        WidgetItemKind.Divider => BuildDivider(),
        WidgetItemKind.KeyValue => BuildKeyValue(item),
        WidgetItemKind.Progress => BuildProgress(item),
        _ => BuildText(item)
    };

    private FrameworkElement BuildText(WidgetItem item)
    {
        var text = new TextBlock
        {
            Text = Shorten(item.Value),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        return Padded(text);
    }

    private FrameworkElement BuildKeyValue(WidgetItem item)
    {
        var row = new Grid();

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = Shorten(item.Label),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        row.Children.Add(label);

        var value = new TextBlock
        {
            Text = Shorten(item.Value),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        Grid.SetColumn(value, 1);
        row.Children.Add(value);

        return Padded(row);
    }

    private FrameworkElement BuildProgress(WidgetItem item)
    {
        var ratio = Math.Clamp(item.Ratio, 0, 1);

        var caption = new Grid();
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = Shorten(item.Label),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        caption.Children.Add(label);

        var value = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Value) ? $"{ratio:P0}" : Shorten(item.Value),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 0)
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        Grid.SetColumn(value, 1);
        caption.Children.Add(value);

        var bar = new Grid { Height = 6, Margin = new Thickness(0, 5, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });

        var fill = new Border { CornerRadius = new CornerRadius(3) };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent.Bright");

        var rest = new Border { CornerRadius = new CornerRadius(3) };
        rest.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");

        Grid.SetColumn(fill, 0);
        Grid.SetColumn(rest, 1);
        bar.Children.Add(fill);
        bar.Children.Add(rest);

        var stack = new StackPanel();
        stack.Children.Add(caption);
        stack.Children.Add(bar);

        return Padded(stack);
    }

    private FrameworkElement BuildDivider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(16, 6, 16, 6) };
        line.SetResourceReference(Border.BackgroundProperty, "Border.Default");

        return line;
    }

    private static FrameworkElement Padded(FrameworkElement content)
    {
        content.Margin = new Thickness(16, 3, 16, 3);

        return content;
    }

    private void ShowMessage(string message)
    {
        _body.Children.Clear();
        AddMessage(message);
    }

    private void AddMessage(string message)
    {
        var text = new TextBlock
        {
            Text = Shorten(message),
            FontSize = 12,
            Margin = new Thickness(16, 12, 16, 0),
            TextWrapping = TextWrapping.Wrap
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");

        _body.Children.Add(text);
    }

    /// <summary>过长的文本截断一下，防止坏扩展把卡片撑爆。</summary>
    private static string Shorten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "…";
    }
}
