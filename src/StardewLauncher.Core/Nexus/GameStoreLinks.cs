namespace StardewLauncher.Core.Nexus;

/// <summary>游戏本体的官方获取入口。</summary>
public sealed record StoreLink(string Name, string Url, string Note);

/// <summary>
/// 游戏本体的官方渠道入口。本工具只提供入口，绝不分发游戏本体。
/// </summary>
public static class GameStoreLinks
{
    public static IReadOnlyList<StoreLink> All { get; } =
    [
        new("Steam",
            "https://store.steampowered.com/app/413150/Stardew_Valley/",
            "最常用的渠道，支持 Steam 创意工坊以外的 Mod（本工具管理）"),
        new("GOG",
            "https://www.gog.com/game/stardew_valley",
            "无 DRM，与 SMAPI 兼容良好"),
        new("官方网站",
            "https://www.stardewvalley.net/",
            "开发者官网，含各平台购买入口"),
        new("Xbox / Microsoft Store",
            "https://www.xbox.com/games/store/stardew-valley/9nblggh52xj9",
            "主机版，Mod 支持有限")
    ];
}
