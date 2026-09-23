namespace StardewLauncher.Core.Saves;

/// <summary>
/// 一个存档的摘要。全部字段来自存档目录里的 SaveGameInfo（小文件），
/// 不读主存档，避免几十到几百 MB 的文件被整块读进内存。
/// </summary>
public sealed class SaveSummary
{
    private static readonly string[] SeasonNames = ["春", "夏", "秋", "冬"];

    /// <summary>存档目录名，也是游戏内部的 SaveId。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>存档目录绝对路径。</summary>
    public string Directory { get; init; } = string.Empty;

    public string PlayerName { get; init; } = string.Empty;

    public string FarmName { get; init; } = string.Empty;

    public int Money { get; init; }

    public int TotalMoneyEarned { get; init; }

    public int DayOfMonth { get; init; }

    /// <summary>0 春 / 1 夏 / 2 秋 / 3 冬。</summary>
    public int Season { get; init; }

    public int Year { get; init; }

    public long MillisecondsPlayed { get; init; }

    public int DeepestMineLevel { get; init; }

    public int FarmingLevel { get; init; }

    public int MiningLevel { get; init; }

    public int ForagingLevel { get; init; }

    public int FishingLevel { get; init; }

    public int CombatLevel { get; init; }

    /// <summary>主存档文件最后的写入时间。</summary>
    public DateTime? LastSavedAt { get; init; }

    /// <summary>存档目录总大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>主存档文件是否在位。缺了说明存档不完整。</summary>
    public bool HasMainFile { get; init; }

    /// <summary>已有多少份备份（由页面按需填充）。</summary>
    public int BackupCount { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(PlayerName) ? Id : PlayerName;

    public string FarmText => string.IsNullOrWhiteSpace(FarmName) ? "（未命名农场）" : FarmName;

    public string SeasonText => Season >= 0 && Season < SeasonNames.Length ? SeasonNames[Season] : "未知";

    public string DateText => Year <= 0
        ? "日期未知"
        : $"第 {Year} 年 {SeasonText} {DayOfMonth} 日";

    public string MoneyText => $"{Money:N0}g";

    public string PlayTimeText
    {
        get
        {
            var span = TimeSpan.FromMilliseconds(MillisecondsPlayed);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
                : $"{span.Minutes} 分";
        }
    }

    public string SkillsText =>
        $"耕种 {FarmingLevel} · 采矿 {MiningLevel} · 觅食 {ForagingLevel} · 钓鱼 {FishingLevel} · 战斗 {CombatLevel}";

    /// <summary>五项技能等级之和，用于卡片上的进度展示（满级 50）。</summary>
    public int TotalSkillLevels => FarmingLevel + MiningLevel + ForagingLevel + FishingLevel + CombatLevel;

    public string LastSavedText => LastSavedAt is null
        ? "未知"
        : LastSavedAt.Value.ToString("yyyy-MM-dd HH:mm");

    public string SizeText => SizeBytes >= 1024L * 1024
        ? $"{SizeBytes / 1024.0 / 1024.0:F1} MB"
        : $"{SizeBytes / 1024.0:F0} KB";

    public string BackupText => BackupCount <= 0 ? "无备份" : $"{BackupCount} 份备份";

    public string MetaText => $"最后保存 {LastSavedText} · {SizeText}";

    public string Summary => $"第 {Year} 年 {SeasonText} {DayOfMonth} 日 · {MoneyText} · 游玩 {PlayTimeText}";
}
