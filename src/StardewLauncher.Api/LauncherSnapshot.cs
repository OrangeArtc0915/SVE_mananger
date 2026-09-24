namespace StardewLauncher.Api;

/// <summary>
/// 启动器数据的一份只读快照。<see cref="IWidgetContext.GetSnapshot"/> 会重新扫描一次再返回，
/// 不要在高频路径上反复调用。
/// </summary>
/// <param name="LauncherVersion">启动器版本号。</param>
/// <param name="GameRunning">游戏（或 SMAPI）当前是否正在运行。</param>
/// <param name="Instances">全部实例，顺序与「游戏实例」页一致。</param>
/// <param name="CurrentInstance">当前选中的实例，没有则为 <c>null</c>。</param>
/// <param name="Mods">当前实例 Mods 目录扫到的 Mod；没有当前实例或目录不存在时为空。</param>
/// <param name="Saves">游戏存档，按最后保存时间倒序。</param>
public sealed record LauncherSnapshot(
    string LauncherVersion,
    bool GameRunning,
    IReadOnlyList<InstanceSnapshot> Instances,
    InstanceSnapshot? CurrentInstance,
    IReadOnlyList<ModSnapshot> Mods,
    IReadOnlyList<SaveSnapshot> Saves)
{
    /// <summary>当前实例扫到的 Mod 总数。</summary>
    public int TotalModCount => Mods.Count;

    /// <summary>已启用的 Mod 数。</summary>
    public int EnabledModCount => Mods.Count(mod => mod.Valid && mod.Enabled);

    /// <summary>已禁用的 Mod 数。</summary>
    public int DisabledModCount => Mods.Count(mod => mod.Valid && !mod.Enabled);

    /// <summary>目录异常或 manifest.json 解析失败的条目数。</summary>
    public int InvalidModCount => Mods.Count(mod => !mod.Valid);
}

/// <summary>一个游戏实例的只读信息。</summary>
/// <param name="Id">实例 id。</param>
/// <param name="Name">实例名。</param>
/// <param name="IsVanilla">是否原版实例（不经过 SMAPI）。</param>
/// <param name="GameDirectory">游戏本体目录。</param>
/// <param name="ModsDirectory">该实例的 Mods 目录。</param>
/// <param name="GameVersion">游戏版本，取不到为 <c>null</c>。</param>
/// <param name="SmapiVersion">已安装的 SMAPI 版本，未安装为 <c>null</c>。</param>
/// <param name="HasSmapi">游戏目录里是否装了 SMAPI。</param>
/// <param name="TotalPlaySeconds">启动器记录的累计游玩秒数。</param>
/// <param name="LastLaunchAt">启动器最后一次启动它的时间。</param>
public sealed record InstanceSnapshot(
    string Id,
    string Name,
    bool IsVanilla,
    string GameDirectory,
    string ModsDirectory,
    string? GameVersion,
    string? SmapiVersion,
    bool HasSmapi,
    long TotalPlaySeconds,
    DateTime? LastLaunchAt)
{
    /// <summary>累计游玩时长，已格式化成「3 小时 20 分」这样。</summary>
    public string PlayTimeText => TotalPlaySeconds <= 0
        ? "无记录"
        : TimeSpan.FromSeconds(TotalPlaySeconds) is var span && span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
            : $"{span.Minutes} 分钟";
}

/// <summary>一个 Mod 的只读信息。</summary>
/// <param name="Name">manifest 里的显示名，取不到时用文件夹名。</param>
/// <param name="UniqueId">manifest 里的 UniqueId。</param>
/// <param name="Version">版本号。</param>
/// <param name="Author">作者。</param>
/// <param name="Enabled">是否启用（SMAPI 以目录名前的点表示禁用）。</param>
/// <param name="IsContentPack">是否内容包。</param>
/// <param name="Valid">目录与 manifest.json 是否解析成功。</param>
/// <param name="HasIssues">是否有依赖问题。</param>
/// <param name="Directory">Mod 根目录。</param>
public sealed record ModSnapshot(
    string Name,
    string UniqueId,
    string Version,
    string Author,
    bool Enabled,
    bool IsContentPack,
    bool Valid,
    bool HasIssues,
    string Directory);

/// <summary>一个存档的只读摘要。</summary>
/// <param name="Id">存档目录名（游戏内部的存档 id）。</param>
/// <param name="PlayerName">玩家名。</param>
/// <param name="FarmName">农场名。</param>
/// <param name="Directory">存档目录。</param>
/// <param name="Year">游戏内年份。</param>
/// <param name="Season">游戏内季节（0 春、1 夏、2 秋、3 冬）。</param>
/// <param name="DayOfMonth">游戏内日期。</param>
/// <param name="PlayedSeconds">游戏内已游玩秒数。</param>
/// <param name="LastSavedAt">最后一次保存时间。</param>
/// <param name="SizeBytes">存档目录占用字节数。</param>
public sealed record SaveSnapshot(
    string Id,
    string PlayerName,
    string FarmName,
    string Directory,
    int Year,
    int Season,
    int DayOfMonth,
    long PlayedSeconds,
    DateTime? LastSavedAt,
    long SizeBytes)
{
    /// <summary>季节中文名。</summary>
    public string SeasonName => Season switch
    {
        0 => "春",
        1 => "夏",
        2 => "秋",
        3 => "冬",
        _ => "?"
    };

    /// <summary>游戏内日期，例如「第 1 年 春 12 日」。</summary>
    public string GameDateText => $"第 {Year} 年 {SeasonName} {DayOfMonth} 日";

    /// <summary>游戏内游玩时长，累计秒数格式化后的文本。</summary>
    public string PlayTimeText => TimeSpan.FromSeconds(PlayedSeconds) is var span && span.TotalHours >= 1
        ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
        : $"{span.Minutes} 分钟";

    /// <summary>存档目录大小，MB 保留一位小数。</summary>
    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:0.0} MB"
        : $"{SizeBytes / 1024d:0} KB";
}
