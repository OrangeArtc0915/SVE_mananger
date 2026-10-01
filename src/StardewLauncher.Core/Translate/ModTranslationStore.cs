using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Translate;

/// <summary>某个 Mod 已落盘的翻译结果。描述可能为空（Mod 本身没写描述）。</summary>
public sealed class ModTranslation
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public DateTime TranslatedAt { get; set; } = DateTime.Now;

    public string TranslatedAtText => TranslatedAt.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// Mod 翻译结果的存储。键沿用 <see cref="Mods.ModTagStore.KeyOf"/>（UniqueId 优先，否则去掉点前缀的文件夹名），
/// 因此 Mod 被禁用 / 启用改了文件夹名后，翻译结果不会丢。
/// 这里只记启动器里显示用的译文，绝不改动 Mod 自己的任何文件。
/// </summary>
public static class ModTranslationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，方便用户直接看文件里翻成了什么
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly Dictionary<string, ModTranslation> Items = new(StringComparer.OrdinalIgnoreCase);

    private static bool _loaded;

    public static string FilePath => Path.Combine(Paths.Data, "mod-translations.json");

    public static int Count => Items.Count;

    public static IReadOnlyDictionary<string, ModTranslation> All => Items;

    public static void Load()
    {
        Paths.Init();

        Items.Clear();
        _loaded = true;

        try
        {
            var file = FilePath;

            if (File.Exists(file))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file), Options);

                foreach (var (key, entry) in model?.Items ?? [])
                {
                    if (string.IsNullOrWhiteSpace(key) || entry is null) continue;
                    Items[key] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取 Mod 翻译文件失败", ex);
        }

        Log.Info($"已载入 {Items.Count} 个 Mod 的翻译");
    }

    public static bool TryGet(string key, [MaybeNullWhen(false)] out ModTranslation entry)
    {
        EnsureLoaded();

        if (string.IsNullOrWhiteSpace(key))
        {
            entry = null!;
            return false;
        }

        return Items.TryGetValue(key, out entry);
    }

    public static void Put(string key, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        EnsureLoaded();

        Items[key] = new ModTranslation
        {
            Name = name ?? string.Empty,
            Description = description ?? string.Empty,
            TranslatedAt = DateTime.Now
        };

        Save();
    }

    public static void Clear()
    {
        EnsureLoaded();

        Items.Clear();
        Save();

        Log.Info("已清空所有 Mod 翻译");
    }

    private static void Save()
    {
        try
        {
            Paths.Init();
            Directory.CreateDirectory(Paths.Data);

            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(new FileModel { Items = Items }, Options));
        }
        catch (Exception ex)
        {
            Log.Error("Mod 翻译保存失败", ex);
        }
    }

    private static void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    private sealed class FileModel
    {
        public Dictionary<string, ModTranslation> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
