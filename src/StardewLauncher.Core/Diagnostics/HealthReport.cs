using StardewLauncher.Core.App;

namespace StardewLauncher.Core.Diagnostics;

/// <summary>体检项的严重程度。Ok 是正常，Info 是知道了就好，Warn 是可能有问题，Fail 是确实有问题。</summary>
public enum HealthLevel
{
    Ok,
    Info,
    Warn,
    Fail
}

/// <summary>体检里的一条结论。Lines 是缩进的明细，可以没有。</summary>
public sealed record HealthItem(string Title, HealthLevel Level, string Detail, IReadOnlyList<string> Lines)
{
    public static HealthItem Ok(string title, string detail, IReadOnlyList<string>? lines = null)
        => new(title, HealthLevel.Ok, detail, lines ?? []);

    public static HealthItem Info(string title, string detail, IReadOnlyList<string>? lines = null)
        => new(title, HealthLevel.Info, detail, lines ?? []);

    public static HealthItem Warn(string title, string detail, IReadOnlyList<string>? lines = null)
        => new(title, HealthLevel.Warn, detail, lines ?? []);

    public static HealthItem Fail(string title, string detail, IReadOnlyList<string>? lines = null)
        => new(title, HealthLevel.Fail, detail, lines ?? []);

    public string LevelText => Level switch
    {
        HealthLevel.Ok => "正常",
        HealthLevel.Info => "提示",
        HealthLevel.Warn => "注意",
        _ => "问题"
    };
}

/// <summary>一次环境体检的结果。</summary>
public sealed class HealthReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;

    public IReadOnlyList<HealthItem> Items { get; init; } = [];

    public int OkCount => Items.Count(item => item.Level == HealthLevel.Ok);

    public int InfoCount => Items.Count(item => item.Level == HealthLevel.Info);

    public int WarnCount => Items.Count(item => item.Level == HealthLevel.Warn);

    public int FailCount => Items.Count(item => item.Level == HealthLevel.Fail);

    /// <summary>有需要用户处理的东西吗。</summary>
    public bool HasProblems => WarnCount + FailCount > 0;

    public string SummaryText
        => $"正常 {OkCount} · 提示 {InfoCount} · 注意 {WarnCount} · 问题 {FailCount}";

    /// <summary>纯文本报告，供复制和打包。</summary>
    public string ToText()
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine($"{AppInfo.Name} 体检报告");
        text.AppendLine($"生成时间：{GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"启动器版本：{AppInfo.VersionDisplay}");
        text.AppendLine();
        text.AppendLine($"汇总：{SummaryText}");
        text.AppendLine();

        foreach (var item in Items)
        {
            text.AppendLine($"[{item.LevelText}] {item.Title}");
            text.AppendLine($"        {item.Detail}");

            foreach (var line in item.Lines) text.AppendLine($"        · {line}");

            text.AppendLine();
        }

        return text.ToString();
    }
}
