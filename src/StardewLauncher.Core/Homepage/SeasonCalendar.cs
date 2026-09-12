namespace StardewLauncher.Core.Homepage;

/// <summary>一个游戏节日。季节用 0=春 1=夏 2=秋 3=冬 表示，天数为 1..28。</summary>
public sealed record Festival(int SeasonIndex, int Day, string Name);

/// <summary>「今天正好是节日」的结果。</summary>
public sealed record FestivalOnToday(string Name, int SeasonIndex, string SeasonName, int Day);

/// <summary>「下一个节日」的结果。</summary>
public sealed record NextFestival(string Name, int SeasonIndex, string SeasonName, int Day, int DaysAway);

/// <summary>
/// 星露谷季节 / 节日。数据来源：星露谷物语官方节日表（游戏内日历），写死在代码里，不联网抓取。
/// 四季各 28 天，全年 112 天。
/// </summary>
public static class SeasonCalendar
{
    /// <summary>每个季节的天数。</summary>
    public const int DaysPerSeason = 28;

    /// <summary>一年四季的总天数。</summary>
    public const int DaysPerYear = DaysPerSeason * 4;

    /// <summary>趣味映射用的起始日：现实日期每过一天，映射的游戏日期也前进一天。</summary>
    private static readonly DateTime FunEpoch = new(2024, 1, 1);

    /// <summary>星露谷官方节日表（游戏内日历）。</summary>
    private static readonly Festival[] FestivalTable =
    [
        new(0, 13, "蛋蛋节"),
        new(0, 24, "花舞节"),
        new(1, 11, "夏威夷宴会"),
        new(1, 28, "月光水母之舞"),
        new(2, 16, "星露谷展览会"),
        new(2, 27, "万灵节"),
        new(3, 8, "冰雪节"),
        new(3, 25, "冬星节")
    ];

    public static IReadOnlyList<Festival> Festivals => FestivalTable;

    private static readonly string[] SeasonNames = ["春季", "夏季", "秋季", "冬季"];

    private static readonly string[] SeasonEmojis = ["🍃", "🌻", "🍂", "❄"];

    /// <summary>季节中文名。</summary>
    public static string SeasonName(int seasonIndex) => SeasonNames[Normalize(seasonIndex)];

    /// <summary>季节图标（emoji）。</summary>
    public static string SeasonEmoji(int seasonIndex) => SeasonEmojis[Normalize(seasonIndex)];

    /// <summary>把存档里的季节字符串（spring/summer/fall/winter）转成 0..3；无法识别返回 -1。</summary>
    public static int ParseSeason(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "spring" => 0,
        "summer" => 1,
        "fall" => 2,
        "winter" => 3,
        _ => -1
    };

    /// <summary>
    /// 把现实日期映射成游戏日期：以 <see cref="FunEpoch"/> 为起点每天前进一天，落在 112 天的循环里。
    /// 这是趣味显示，与存档中的真实进度无关。
    /// </summary>
    public static (int SeasonIndex, int Day) FunMapping(DateTime date)
    {
        var totalDays = (long)(date.Date - FunEpoch).TotalDays;
        var index = (int)(((totalDays % DaysPerYear) + DaysPerYear) % DaysPerYear);

        return (index / DaysPerSeason, index % DaysPerSeason + 1);
    }

    /// <summary>今天是否正好是节日；不是则返回 null。</summary>
    public static FestivalOnToday? FestivalForToday(int seasonIndex, int day)
    {
        var season = Normalize(seasonIndex);
        foreach (var festival in FestivalTable)
        {
            if (festival.SeasonIndex == season && festival.Day == day)
                return new FestivalOnToday(festival.Name, season, SeasonNames[season], day);
        }

        return null;
    }

    /// <summary>从今天（含）起往后找最近的一个节日，返回还有几天。</summary>
    public static NextFestival? NextFestival(int seasonIndex, int day)
    {
        var season = Normalize(seasonIndex);
        day = Math.Clamp(day, 1, DaysPerSeason);

        var start = season * DaysPerSeason + (day - 1);

        for (var offset = 0; offset < DaysPerYear; offset++)
        {
            var absolute = (start + offset) % DaysPerYear;
            var targetSeason = absolute / DaysPerSeason;
            var targetDay = absolute % DaysPerSeason + 1;

            foreach (var festival in FestivalTable)
            {
                if (festival.SeasonIndex == targetSeason && festival.Day == targetDay)
                    return new NextFestival(festival.Name, targetSeason, SeasonNames[targetSeason], targetDay, offset);
            }
        }

        return null;
    }

    private static int Normalize(int seasonIndex) => ((seasonIndex % 4) + 4) % 4;
}
