namespace StardewLauncher.App;

/// <summary>主页「常去小站」的站点链接。</summary>
internal static class SiteLinks
{
    public static IReadOnlyList<SiteLink> All { get; } =
    [
        new("Nexus 星露谷板块", "https://www.nexusmods.com/stardewvalley", "lucide/globe"),
        new("SMAPI 官网", "https://smapi.io/", "lucide/wrench"),
        new("Mod 兼容性列表", "https://smapi.io/mods", "lucide/shield-check"),
        new("星露谷 Wiki 模组区", "https://stardewvalleywiki.com/Modding:Index", "lucide/book-marked")
    ];
}

public sealed record SiteLink(string Title, string Url, string Icon);
