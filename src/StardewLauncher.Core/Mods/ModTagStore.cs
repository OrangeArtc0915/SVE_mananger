using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>一个虚拟标签。ColorKey 只允许 Accent / Warn / Danger / Success。</summary>
public sealed class ModTag
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    public string ColorKey { get; set; } = "Accent";
}

/// <summary>
/// 虚拟组 / 标签的存储。
/// 标签按 Mod 的稳定标识关联：优先 UniqueId，没有则退回去掉点前缀的文件夹名（RawFolderName），
/// 因此禁用 / 启用导致文件夹改名后标签不会丢。
/// </summary>
public static class ModTagStore
{
    private const string DefaultColorKey = "Accent";

    /// <summary>允许的颜色键。</summary>
    public static readonly string[] ColorKeys = ["Accent", "Warn", "Danger", "Success"];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，保证用户手工编辑标签文件时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly List<ModTag> Items = [];

    private static readonly Dictionary<string, HashSet<string>> Maps = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ModTag> Tags => Items;

    /// <summary>Mod 的稳定标识 → 标签 Id 集合。</summary>
    public static IReadOnlyDictionary<string, HashSet<string>> Assignments => Maps;

    /// <summary>标签或关联发生变化时触发。</summary>
    public static event Action? Changed;

    public static string FilePath => Path.Combine(Paths.Data, "mod-tags.json");

    public static void Load()
    {
        Paths.Init();

        Items.Clear();
        Maps.Clear();

        try
        {
            var file = FilePath;

            if (File.Exists(file))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file), Options);

                if (model is not null)
                {
                    foreach (var tag in model.Tags)
                    {
                        if (tag is null || string.IsNullOrWhiteSpace(tag.Id)) continue;

                        tag.Name = (tag.Name ?? string.Empty).Trim();
                        tag.ColorKey = NormalizeColor(tag.ColorKey);

                        if (!Items.Any(item => string.Equals(item.Id, tag.Id, StringComparison.OrdinalIgnoreCase)))
                            Items.Add(tag);
                    }

                    foreach (var (key, ids) in model.Assignments)
                    {
                        if (string.IsNullOrWhiteSpace(key) || ids is null) continue;

                        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var id in ids)
                        {
                            if (!string.IsNullOrWhiteSpace(id)) set.Add(id);
                        }

                        if (set.Count > 0) Maps[key] = set;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取标签文件失败", ex);
        }

        Log.Info($"已载入 {Items.Count} 个标签，关联 {Maps.Count} 个 Mod");
        Changed?.Invoke();
    }

    public static ModTag Create(string name, string colorKey)
    {
        var tag = new ModTag
        {
            Name = string.IsNullOrWhiteSpace(name) ? "新标签" : name.Trim(),
            ColorKey = NormalizeColor(colorKey)
        };

        Items.Add(tag);
        Save();

        Log.Info($"已新建标签「{tag.Name}」（{tag.ColorKey}）");
        return tag;
    }

    public static void Rename(ModTag tag, string newName)
    {
        if (tag is null) return;

        var name = (newName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, tag.Name, StringComparison.Ordinal)) return;

        tag.Name = name;
        Save();
        Log.Info($"标签已重命名为「{name}」");
    }

    public static void SetColor(ModTag tag, string colorKey)
    {
        if (tag is null) return;

        var color = NormalizeColor(colorKey);
        if (string.Equals(color, tag.ColorKey, StringComparison.Ordinal)) return;

        tag.ColorKey = color;
        Save();
        Log.Info($"标签「{tag.Name}」颜色改为 {color}");
    }

    public static void Delete(ModTag tag)
    {
        if (tag is null) return;

        Items.RemoveAll(item => string.Equals(item.Id, tag.Id, StringComparison.OrdinalIgnoreCase));

        foreach (var key in Maps.Keys.ToList())
        {
            Maps[key].Remove(tag.Id);
            if (Maps[key].Count == 0) Maps.Remove(key);
        }

        Save();
        Log.Info($"已删除标签「{tag.Name}」");
    }

    /// <summary>某个 Mod 已有的标签，按标签创建顺序返回。</summary>
    public static IReadOnlyList<ModTag> TagsOf(string modKey)
    {
        if (string.IsNullOrWhiteSpace(modKey) || !Maps.TryGetValue(modKey, out var ids) || ids.Count == 0)
            return [];

        return [.. Items.Where(tag => ids.Contains(tag.Id))];
    }

    public static void Assign(string modKey, ModTag tag)
    {
        if (string.IsNullOrWhiteSpace(modKey) || tag is null) return;

        if (!Maps.TryGetValue(modKey, out var ids))
        {
            ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Maps[modKey] = ids;
        }

        if (!ids.Add(tag.Id)) return;

        Save();
    }

    public static void Unassign(string modKey, ModTag tag)
    {
        if (string.IsNullOrWhiteSpace(modKey) || tag is null) return;

        if (!Maps.TryGetValue(modKey, out var ids) || !ids.Remove(tag.Id)) return;

        if (ids.Count == 0) Maps.Remove(modKey);

        Save();
    }

    /// <summary>Mod 的稳定标识：UniqueId 优先，否则 RawFolderName。</summary>
    public static string KeyOf(ModEntry mod)
    {
        if (mod is null) return string.Empty;

        return string.IsNullOrWhiteSpace(mod.UniqueId) ? mod.RawFolderName : mod.UniqueId;
    }

    private static void Save()
    {
        try
        {
            Paths.Init();
            Directory.CreateDirectory(Paths.Data);

            var model = new FileModel
            {
                Tags = [.. Items],
                Assignments = Maps.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase)
            };

            IO.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(model, Options));
        }
        catch (Exception ex)
        {
            Log.Error("标签保存失败", ex);
        }

        Changed?.Invoke();
    }

    private static string NormalizeColor(string? colorKey)
        => Array.IndexOf(ColorKeys, colorKey ?? string.Empty) >= 0 ? colorKey! : DefaultColorKey;

    /// <summary>落盘模型：标签列表 + 关联表。</summary>
    private sealed class FileModel
    {
        public List<ModTag> Tags { get; set; } = [];

        public Dictionary<string, List<string>> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
