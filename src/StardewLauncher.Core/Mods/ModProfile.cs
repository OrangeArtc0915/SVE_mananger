namespace StardewLauncher.Core.Mods;

/// <summary>配置档里的一条记录：某个 Mod 当时是启用还是禁用。</summary>
public sealed class ModToggle
{
    /// <summary>Mod 的稳定标识（UniqueId 优先，否则去点前缀的文件夹名）。禁用改名后依然能对上。</summary>
    public string Key { get; set; } = "";

    /// <summary>记录时的显示名，只用于界面展示。</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; }
}

/// <summary>
/// 一套 Mod 启停组合。像"美化包""剧情包"这样成套切换，
/// 只记录启停状态，不动 Mod 文件本身。
/// </summary>
public sealed class ModProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    public string Note { get; set; } = "";

    /// <summary>这份档对应的 Mods 目录，切换实例后只显示同目录的档。</summary>
    public string ModsDirectory { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<ModToggle> Mods { get; set; } = [];

    public int EnabledCount => Mods.Count(item => item.Enabled);

    public string MetaText => $"{Mods.Count} 个 Mod · 启用 {EnabledCount} · 禁用 {Mods.Count - EnabledCount}";

    public string TimeText => $"更新于 {UpdatedAt:yyyy-MM-dd HH:mm}";

    /// <summary>按当前扫描结果快照一份记录。</summary>
    public static List<ModToggle> Snapshot(IEnumerable<ModEntry> mods)
    {
        var list = new List<ModToggle>();

        foreach (var mod in mods)
        {
            var key = ModTagStore.KeyOf(mod);
            if (string.IsNullOrWhiteSpace(key)) continue;

            list.Add(new ModToggle
            {
                Key = key,
                Name = mod.DisplayName,
                Enabled = mod.IsEnabled
            });
        }

        list.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}

/// <summary>应用一份配置档的结果。</summary>
public sealed record ModProfileApplyResult(
    int Changed,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;

    public string Summary
    {
        get
        {
            var text = $"已调整 {Changed} 个 Mod";

            if (Missing.Count > 0) text += $"，{Missing.Count} 个已不在 Mods 目录里";
            if (Errors.Count > 0) text += $"，{Errors.Count} 个失败";

            return text + "。";
        }
    }
}
