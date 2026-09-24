namespace StardewLauncher.Api;

/// <summary>卡片里一行的样式。</summary>
public enum WidgetItemKind
{
    /// <summary>一段普通文字，自动换行。</summary>
    Text = 0,

    /// <summary>左侧标签 + 右侧取值，标签与取值会分别用次要色与主要色显示。</summary>
    KeyValue = 1,

    /// <summary>进度条，用 <see cref="WidgetItem.Ratio"/> 表示 0~1 的进度。</summary>
    Progress = 2,

    /// <summary>可点击的外部链接，用系统默认浏览器打开 <see cref="WidgetItem.Url"/>。</summary>
    Link = 3,

    /// <summary>一条分隔线，用于分组。</summary>
    Divider = 4
}

/// <summary>卡片里的一行。</summary>
public sealed class WidgetItem
{
    /// <summary>这一行的样式。</summary>
    public WidgetItemKind Kind { get; init; }

    /// <summary>标签文字（<see cref="WidgetItemKind.KeyValue"/>、<see cref="WidgetItemKind.Progress"/> 用）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>取值或正文（<see cref="WidgetItemKind.Text"/> 用这里）。</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>进度，0~1，超出范围会被夹住。</summary>
    public double Ratio { get; init; }

    /// <summary>链接地址。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// 行首图标名。可用的是启动器内置图标，例如 <c>lucide/clock</c>、<c>lucide/package</c>；
    /// 留空则不显示图标。写错只是不显示，不会报错。
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>一段文字，可多行。</summary>
    public static WidgetItem Text(string text) => new() { Kind = WidgetItemKind.Text, Value = text };

    /// <summary>标签 + 取值。</summary>
    public static WidgetItem KeyValue(string label, string value) =>
        new() { Kind = WidgetItemKind.KeyValue, Label = label, Value = value };

    /// <summary>带进度条的一行。value 留空时右侧只显示百分比。</summary>
    public static WidgetItem Progress(string label, double ratio, string? value = null) => new()
    {
        Kind = WidgetItemKind.Progress,
        Label = label,
        Ratio = Math.Clamp(ratio, 0, 1),
        Value = value ?? string.Empty
    };

    /// <summary>可点击的外部链接。</summary>
    public static WidgetItem Link(string label, string url, string? icon = "lucide/external-link") =>
        new() { Kind = WidgetItemKind.Link, Label = label, Url = url, Icon = icon };

    /// <summary>分隔线。</summary>
    public static WidgetItem Divider() => new() { Kind = WidgetItemKind.Divider };
}

/// <summary>
/// 插件返回的卡片内容，由启动器按现有卡片风格渲染。
/// 最简用法：<c>new WidgetContent { Items = [WidgetItem.Text("今天天气不错")] }</c>。
/// </summary>
public sealed class WidgetContent
{
    /// <summary>标题右侧的小字，可以是状态说明。</summary>
    public string? Subtitle { get; init; }

    /// <summary>卡片正文，按顺序渲染。留空时显示 <see cref="EmptyText"/>。</summary>
    public IReadOnlyList<WidgetItem> Items { get; init; } = [];

    /// <summary>正文下方的小字备注。</summary>
    public string? Footnote { get; init; }

    /// <summary>没有内容时显示的话。</summary>
    public string? EmptyText { get; init; }

    /// <summary>只有一句提示的卡片。</summary>
    public static WidgetContent Empty(string text) => new() { EmptyText = text };

    /// <summary>只有一行文字的卡片。</summary>
    public static WidgetContent Text(string text) => new() { Items = [WidgetItem.Text(text)] };
}
