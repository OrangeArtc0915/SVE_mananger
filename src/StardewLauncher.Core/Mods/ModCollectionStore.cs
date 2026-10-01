using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>一整套 Mod 的组合。只记 Mod 的稳定标识，不复制任何文件。</summary>
public sealed class ModCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    public string Note { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>收录的 Mod 标识（UniqueID 优先，否则去掉点前缀的文件夹名）。</summary>
    public List<string> ModKeys { get; set; } = [];

    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd");

    public string CountText => $"收录 {ModKeys.Count} 个 Mod";
}

/// <summary>套用合集时要做的改动。只描述差异，不直接动手。</summary>
public sealed record CollectionPlan(
    IReadOnlyList<ModEntry> ToEnable,
    IReadOnlyList<ModEntry> ToDisable,
    IReadOnlyList<string> Missing,
    IReadOnlyList<ModEntry> AlreadyOk);

/// <summary>
/// Mod 合集的存储与比对。合集记的是 Mod 的稳定标识（同 <see cref="ModTagStore.KeyOf"/>），
/// 所以禁用 / 启用导致文件夹改名、甚至换一份同 ID 的安装，合集都还能对上。
/// </summary>
public static class ModCollectionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，保证用户手工编辑合集文件时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly List<ModCollection> Items = [];

    public static IReadOnlyList<ModCollection> All => Items;

    public static event Action? Changed;

    public static string FilePath => Path.Combine(Paths.Data, "mod-collections.json");

    public static void Load()
    {
        Paths.Init();

        Items.Clear();

        try
        {
            var file = FilePath;

            if (File.Exists(file))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file), Options);

                foreach (var collection in model?.Collections ?? [])
                {
                    if (collection is null || string.IsNullOrWhiteSpace(collection.Id)) continue;

                    collection.Name = (collection.Name ?? string.Empty).Trim();
                    collection.ModKeys = [.. collection.ModKeys.Where(key => !string.IsNullOrWhiteSpace(key))];

                    if (!Items.Any(item => string.Equals(item.Id, collection.Id, StringComparison.OrdinalIgnoreCase)))
                        Items.Add(collection);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取 Mod 合集文件失败", ex);
        }

        Log.Info($"已载入 {Items.Count} 个 Mod 合集");
        Changed?.Invoke();
    }

    /// <summary>把当前启用着的 Mod 存成一个新合集。</summary>
    public static ModCollection Capture(string name, string note, IReadOnlyList<ModEntry> mods)
    {
        var collection = new ModCollection
        {
            Name = string.IsNullOrWhiteSpace(name) ? "新合集" : name.Trim(),
            Note = (note ?? string.Empty).Trim(),
            ModKeys = [.. mods
                .Where(mod => mod.IsEnabled)
                .Select(ModTagStore.KeyOf)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)]
        };

        Items.Add(collection);
        Save();

        Log.Info($"已保存合集「{collection.Name}」，收录 {collection.ModKeys.Count} 个 Mod");
        return collection;
    }

    public static void Rename(ModCollection collection, string newName, string? newNote = null)
    {
        if (collection is null) return;

        var name = (newName ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(name)) collection.Name = name;

        collection.Note = (newNote ?? collection.Note ?? string.Empty).Trim();

        Save();
        Log.Info($"合集已更新：{collection.Name}");
    }

    public static void Delete(ModCollection collection)
    {
        if (collection is null) return;

        Items.RemoveAll(item => string.Equals(item.Id, collection.Id, StringComparison.OrdinalIgnoreCase));
        Save();

        Log.Info($"已删除合集「{collection.Name}」");
    }

    /// <summary>
    /// 比对合集与磁盘现状：要开哪些、要关哪些、合集里有哪些已经不在磁盘上了。
    /// 只做计算，不改磁盘 —— 真正动手在界面确认之后。
    /// </summary>
    public static CollectionPlan Plan(ModCollection collection, IReadOnlyList<ModEntry> mods)
    {
        var wanted = new HashSet<string>(collection.ModKeys, StringComparer.OrdinalIgnoreCase);

        var byKey = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            var key = ModTagStore.KeyOf(mod);
            if (!string.IsNullOrWhiteSpace(key)) byKey.TryAdd(key, mod);
        }

        var toEnable = new List<ModEntry>();
        var toDisable = new List<ModEntry>();
        var alreadyOk = new List<ModEntry>();
        var missing = new List<string>();

        foreach (var key in collection.ModKeys)
        {
            if (byKey.TryGetValue(key, out var mod))
            {
                if (mod.IsEnabled) alreadyOk.Add(mod);
                else toEnable.Add(mod);
                continue;
            }

            missing.Add(key);
        }

        foreach (var mod in mods)
        {
            if (!mod.IsEnabled) continue;

            var key = ModTagStore.KeyOf(mod);
            if (string.IsNullOrWhiteSpace(key) || wanted.Contains(key)) continue;

            toDisable.Add(mod);
        }

        return new CollectionPlan(toEnable, toDisable, missing, alreadyOk);
    }

    private static void Save()
    {
        try
        {
            Paths.Init();
            Directory.CreateDirectory(Paths.Data);

            IO.AtomicFile.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(new FileModel { Collections = [.. Items] }, Options));
        }
        catch (Exception ex)
        {
            Log.Error("Mod 合集保存失败", ex);
        }

        Changed?.Invoke();
    }

    private sealed class FileModel
    {
        public List<ModCollection> Collections { get; set; } = [];
    }
}
