using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Translate;

/// <summary>缓存里的一条翻译结果。</summary>
public sealed class TranslateCacheEntry
{
    public string Text { get; set; } = "";

    public DateTime FetchedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 翻译结果的落盘缓存。键由「源文本 + 目标语言 + 服务商」拼成：
/// 换服务商或换目标语言会各自存一份，免得把上一个语言的结果命中给下一个。
/// 超过设置里的 <see cref="Settings.TranslateCacheHours"/> 小时即视为过期。
/// </summary>
public static class TranslateCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，方便用户直接看缓存文件里翻了些什么
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly Dictionary<string, TranslateCacheEntry> Items = new(StringComparer.Ordinal);

    private static bool _loaded;

    public static string FilePath => Path.Combine(Paths.Data, "translate-cache.json");

    public static int Count => Items.Count;

    /// <summary>拼缓存键。用控制字符分隔，避免源文本里恰好出现分隔符造成串键。</summary>
    public static string KeyOf(string source, string target, string provider)
        => $"{provider}\u0001{target}\u0001{source}";

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
            Log.Error("读取翻译缓存失败", ex);
        }

        CleanupExpired();
        Log.Info($"已载入 {Items.Count} 条翻译缓存");
    }

    /// <summary>取缓存。命中且没过期才返回 true；过期的条目顺手清掉。</summary>
    public static bool TryGet(string key, out string text)
    {
        EnsureLoaded();

        text = string.Empty;

        if (string.IsNullOrWhiteSpace(key) || !Items.TryGetValue(key, out var entry)) return false;

        if (IsExpired(entry))
        {
            Items.Remove(key);
            return false;
        }

        text = entry.Text;
        return true;
    }

    public static void Put(string key, string text)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(text)) return;

        EnsureLoaded();
        Items[key] = new TranslateCacheEntry { Text = text, FetchedAt = DateTime.Now };
    }

    /// <summary>清掉过期条目。命中判断里也会清，这里用于启动时与定期整理。</summary>
    public static void CleanupExpired()
    {
        EnsureLoaded();

        if (Items.Count == 0) return;

        var expired = Items.Where(pair => IsExpired(pair.Value)).Select(pair => pair.Key).ToList();
        foreach (var key in expired) Items.Remove(key);
    }

    public static void Save()
    {
        try
        {
            Paths.Init();
            Directory.CreateDirectory(Paths.Data);

            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(new FileModel { Items = Items }, Options));
        }
        catch (Exception ex)
        {
            Log.Error("翻译缓存保存失败", ex);
        }
    }

    private static bool IsExpired(TranslateCacheEntry entry)
    {
        // 至少留 1 小时，避免用户把设置填成 0 时缓存立刻全失效
        var hours = Math.Max(1, SettingsStore.Current.TranslateCacheHours);
        return DateTime.Now - entry.FetchedAt > TimeSpan.FromHours(hours);
    }

    private static void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    private sealed class FileModel
    {
        public Dictionary<string, TranslateCacheEntry> Items { get; set; } = new(StringComparer.Ordinal);
    }
}
