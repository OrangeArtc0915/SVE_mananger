using System.IO;
using System.Text.Json.Serialization;
using StardewLauncher.Core.Games;

namespace StardewLauncher.Core.Instances;

/// <summary>实例类型：原版直接跑游戏主程序，Mod 端通过 SMAPI 启动并加载 Mod。</summary>
public enum InstanceKind
{
    /// <summary>原版：不经过 SMAPI、不加载任何 Mod，也不改动 Mods 目录。</summary>
    Vanilla = 0,

    /// <summary>Mod 端：通过 SMAPI 启动，可加载 Mod。</summary>
    Modded = 1
}

/// <summary>
/// 一个游戏实例。实例是「用哪个游戏本体 + 哪种启动方式」的组合，不复制游戏本体；
/// Mod 一律放在游戏目录下的 Mods 里，由 Mod 管理页直接操作。
/// </summary>
public sealed class Instance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    /// <summary>实例类型，决定启动时是否经过 SMAPI。</summary>
    public InstanceKind Kind { get; set; } = InstanceKind.Modded;

    /// <summary>指向的游戏本体目录。</summary>
    public string GameDir { get; set; } = string.Empty;

    public DateTime? LastLaunchAt { get; set; }

    public long TotalPlaySeconds { get; set; }

    /// <summary>是否在列表中隐藏。</summary>
    public bool Hidden { get; set; }

    // ————— 以下为运行时派生，不写入配置文件 —————

    [JsonIgnore]
    public StardewInstall? Install { get; set; }

    [JsonIgnore]
    public bool IsValid => Install is not null;

    [JsonIgnore]
    public bool IsVanilla => Kind == InstanceKind.Vanilla;

    /// <summary>启动时是否经过 SMAPI。原版实例一律直启主程序。</summary>
    [JsonIgnore]
    public bool UseSmapi => Kind == InstanceKind.Modded;

    [JsonIgnore]
    public string KindText => Kind == InstanceKind.Vanilla ? "原版" : "Mod 端";

    /// <summary>该实例要管理的 Mods 目录：游戏目录下的 Mods。</summary>
    [JsonIgnore]
    public string ModsDirectory => Install?.ModsDirectory ?? Path.Combine(GameDir, "Mods");

    [JsonIgnore]
    public string LastLaunchText => LastLaunchAt is null
        ? "尚未启动"
        : $"上次启动 {LastLaunchAt.Value:yyyy-MM-dd HH:mm}";

    [JsonIgnore]
    public string PlayTimeText => TotalPlaySeconds <= 0
        ? "无记录"
        : TimeSpan.FromSeconds(TotalPlaySeconds) is var span && span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
            : $"{span.Minutes} 分钟";

    [JsonIgnore]
    public string Summary => Install is null
        ? "游戏目录不可用"
        : Install.HasSmapi
            ? $"SMAPI {Install.SmapiVersion ?? "未知"} · 游戏 {Install.GameVersion ?? "未知"}"
            : "未安装 SMAPI";
}
