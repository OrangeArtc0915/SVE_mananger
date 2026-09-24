namespace StardewLauncher.Core.Mods;

/// <summary>一个可浏览的 Mod 站点。<see cref="SupportsDirectDownload"/> 为 true 表示该站点的下载可被本程序接管（nxm://）。</summary>
public sealed record ModSite(string Id, string Name, string Url, string Note, bool SupportsDirectDownload);

/// <summary>
/// 按官方 Wiki「寻找 Mod」一节整理的站点清单。
/// 地址与定位说明以 Wiki 为准，界面只读取这里的数据，避免多处硬编码。
/// </summary>
public static class ModSiteCatalog
{
    /// <summary>Nexus Mods 的站点 Id。只有该站点会发 nxm:// 链接，也正因如此只有它支持「一键下载」。</summary>
    public const string NexusId = "nexus";

    /// <summary>按官方 Wiki「寻找 Mod」一节整理的站点清单（顺序即界面上的排列顺序）。</summary>
    public static IReadOnlyList<ModSite> All { get; } =
    [
        new(NexusId, "Nexus Mods", "https://www.nexusmods.com/stardewvalley/mods/",
            "绝大多数 Mod 发布于此；页面上的「Mod Manager Download」可直接在启动器内下载", true),

        new("curseforge", "CurseForge", "https://www.curseforge.com/stardewvalley/search?class=mods&sortBy=popularity",
            "站上约有 1783 个星露谷 Mod，此地址已按热度排序", false),

        new("forums", "官方论坛", "https://forums.stardewvalley.net/index.php?resources/",
            "官方论坛的资源区，部分 Mod 只在此发布", false),

        new("chucklefish", "Chucklefish Mods", "https://community.playstarbound.com/resources/categories/stardew-valley.22/",
            "以较老的 Mod 为主，多数也已转到 Nexus", false),

        new("moddrop", "ModDrop", "https://www.moddrop.com/stardew-valley",
            "官方 Wiki 不推荐：审核缺失、存在盗搬 Mod，建议优先用 Nexus", false),

        new("smapi", "SMAPI 兼容性列表", "https://smapi.io/mods",
            "查某个 Mod 是否兼容当前游戏与 SMAPI 版本", false),

        new("wiki", "星露谷 Wiki 模组索引", "https://stardewvalleywiki.com/Modding:Index",
            "模组相关文档总索引", false),

        new("recommend", "推荐 Mod 列表", "https://gist.github.com/Pathoschild/b608892d3e60bd25d0eea71ca7584649",
            "SMAPI 作者 Pathoschild 整理的推荐清单", false)
    ];

    /// <summary>按 Id 查找站点；找不到返回 null。</summary>
    public static ModSite? ById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var site in All)
        {
            if (string.Equals(site.Id, id, StringComparison.OrdinalIgnoreCase)) return site;
        }

        return null;
    }

    /// <summary>host 是否属于 nexusmods.com（含子域）。用于判断 nxm:// 是否该由本程序接管。</summary>
    public static bool IsNexusHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        return host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>默认站点：Nexus Mods。</summary>
    public static ModSite Default => All[0];
}
