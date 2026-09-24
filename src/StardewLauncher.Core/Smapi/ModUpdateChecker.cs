using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.Core.Smapi;

/// <summary>一个 Mod 的更新检查结果。</summary>
public sealed record ModUpdateInfo(string Version, string Url);

/// <summary>一次更新检查的结果。Updates 以 ModTagStore.KeyOf 的稳定键为索引。</summary>
public sealed record ModUpdateCheckResult(bool Ok, string Message, IReadOnlyDictionary<string, ModUpdateInfo> Updates);

/// <summary>
/// Mod 更新检查：把 manifest.json 里的 UpdateKeys 交给 smapi.io 的公开接口交叉查询，
/// 由它判断有没有新版本（Nexus / GitHub / CurseForge / ModDrop / wiki 都覆盖）。
///
/// <para>
/// 接口形状取自官方文档 docs/technical/web.md（本机实测过）：POST /api/{版本}/mods，
/// 请求体 <c>{ mods: [{ id, updateKeys, installedVersion }], apiVersion, gameVersion, platform }</c>，
/// 响应 <c>[{ id, suggestedUpdate: { version, url } | null, errors }]</c>。
/// 其中 apiVersion（当前装的 SMAPI 版本）不传的话对方不会给任何更新建议，所以必须带上。
/// </para>
///
/// <para>
/// 结果按 6 小时缓存到磁盘：进页面时用缓存，用户主动点「检查更新」才强制重查，
/// 免得反复打扰对方接口。
/// </para>
/// </summary>
public static class ModUpdateChecker
{
    private const string ApiUrl = "https://smapi.io/api/v4.0.0/mods";
    private const string UserAgent = "StardewLauncher";

    /// <summary>磁盘缓存有效期。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly IReadOnlyDictionary<string, ModUpdateInfo> Empty =
        new Dictionary<string, ModUpdateInfo>();

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static Dictionary<string, ModUpdateInfo>? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;

    private static string CacheFile => Path.Combine(Paths.Cache, "mod-updates.json");

    /// <summary>
    /// 检查一批 Mod 的更新。<paramref name="force"/> 为 false 且缓存新鲜时直接用缓存，不发请求。
    /// </summary>
    public static async Task<ModUpdateCheckResult> CheckAsync(IReadOnlyList<ModEntry> mods, string? smapiVersion,
        string? gameVersion, bool force, CancellationToken token = default)
    {
        // 只有写了 UpdateKeys、并且能读到版本号的 Mod 才查得到
        var targets = mods
            .Where(mod => mod.UpdateKeys.Length > 0 && !string.IsNullOrWhiteSpace(mod.Manifest?.Version))
            .ToList();

        if (targets.Count == 0)
        {
            return new ModUpdateCheckResult(false,
                "没有可检查的 Mod：manifest.json 里既要有 UpdateKeys，也要有版本号。", Empty);
        }

        if (!force && TryReadCache(out var fresh, ignoreExpiry: false))
            return new ModUpdateCheckResult(true, "用的是几小时内查过的结果", fresh);

        var requestIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var request = BuildRequest(targets, requestIds, smapiVersion, gameVersion);

        var text = await HttpDownloader.PostJsonAsync(ApiUrl, request, UserAgent, token);

        if (string.IsNullOrWhiteSpace(text))
        {
            // 查不到就退回缓存（哪怕过期），总比什么都没有强
            if (TryReadCache(out var stale, ignoreExpiry: true))
                return new ModUpdateCheckResult(true, "查询失败，显示的是上次的结果", stale);

            return new ModUpdateCheckResult(false, "查询失败：连不上 smapi.io，或对方返回了错误。", Empty);
        }

        var updates = Parse(text, requestIds);

        lock (Gate)
        {
            // 合并进缓存：这次没查到的 Mod 保留上次的结果
            _cached = Merge(_cached, updates);
            _cachedAt = DateTime.UtcNow;

            WriteCache(_cached);
        }

        Log.Info($"Mod 更新检查完成：查询 {requestIds.Count} 个，有新版 {updates.Count} 个");

        return new ModUpdateCheckResult(true, $"已检查 {requestIds.Count} 个 Mod", updates);
    }

    /// <summary>
    /// 组装请求体，并把「接口用的 id → 调用方的稳定键」记进 <paramref name="requestIds"/>，
    /// 响应回来时按它还原成稳定键（这样重新扫描后也能对上）。
    /// </summary>
    private static string BuildRequest(IReadOnlyList<ModEntry> targets,
        Dictionary<string, string> requestIds, string? smapiVersion, string? gameVersion)
    {
        var payload = new List<object>();

        foreach (var mod in targets)
        {
            // 有 UniqueId 就直接用它当 id；只有 UpdateKeys 的用假的 id（官方文档推荐的写法）
            var id = string.IsNullOrWhiteSpace(mod.UniqueId)
                ? $"FAKE.{mod.UpdateKeys[0].Replace(':', '.').Replace('/', '.')}"
                : mod.UniqueId;

            // 两个文件夹装了同一个 Mod 时只发一次
            if (!requestIds.TryAdd(id, ModTagStore.KeyOf(mod))) continue;

            payload.Add(new
            {
                id,
                updateKeys = mod.UpdateKeys,
                installedVersion = mod.Manifest?.Version
            });
        }

        return JsonSerializer.Serialize(new
        {
            mods = payload,
            apiVersion = smapiVersion,
            gameVersion,
            platform = "Windows",
            includeExtendedMetadata = false
        }, Options);
    }

    private sealed class ResponseItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

        [JsonPropertyName("suggestedUpdate")] public SuggestedUpdate? SuggestedUpdate { get; set; }

        [JsonPropertyName("errors")] public List<string>? Errors { get; set; }
    }

    private sealed class SuggestedUpdate
    {
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    }

    private static Dictionary<string, ModUpdateInfo> Parse(string text, Dictionary<string, string> requestIds)
    {
        var result = new Dictionary<string, ModUpdateInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var items = JsonSerializer.Deserialize<List<ResponseItem>>(text, Options) ?? [];

            foreach (var item in items)
            {
                if (item.Errors is { Count: > 0 })
                    Log.Warn($"更新查询报错（{item.Id}）：{string.Join("；", item.Errors)}");

                if (item.SuggestedUpdate is not { } update) continue;
                if (string.IsNullOrWhiteSpace(update.Version)) continue;
                if (!requestIds.TryGetValue(item.Id, out var key)) continue;

                result[key] = new ModUpdateInfo(update.Version, update.Url);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析更新查询结果失败：{ex.Message}");
        }

        return result;
    }

    private sealed class CacheModel
    {
        public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

        public Dictionary<string, ModUpdateInfo> Updates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, ModUpdateInfo> Merge(
        Dictionary<string, ModUpdateInfo>? previous, Dictionary<string, ModUpdateInfo> current)
    {
        var merged = previous is null
            ? new Dictionary<string, ModUpdateInfo>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ModUpdateInfo>(previous, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in current) merged[pair.Key] = pair.Value;

        return merged;
    }

    private static bool TryReadCache(out IReadOnlyDictionary<string, ModUpdateInfo> updates, bool ignoreExpiry)
    {
        lock (Gate)
        {
            if (_cached is not null && (ignoreExpiry || DateTime.UtcNow - _cachedAt < CacheTtl))
            {
                updates = _cached;
                return true;
            }

            updates = Empty;

            try
            {
                if (!File.Exists(CacheFile)) return false;

                var model = JsonSerializer.Deserialize<CacheModel>(File.ReadAllText(CacheFile), Options);
                if (model is null) return false;
                if (!ignoreExpiry && DateTime.UtcNow - model.FetchedAt >= CacheTtl) return false;

                _cached = model.Updates;
                _cachedAt = model.FetchedAt;

                updates = _cached;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"读取 Mod 更新缓存失败：{ex.Message}");
                return false;
            }
        }
    }

    private static void WriteCache(Dictionary<string, ModUpdateInfo> updates)
    {
        try
        {
            Directory.CreateDirectory(Paths.Cache);

            var model = new CacheModel { FetchedAt = DateTime.UtcNow, Updates = updates };
            IO.AtomicFile.WriteAllText(CacheFile, JsonSerializer.Serialize(model, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"写入 Mod 更新缓存失败：{ex.Message}");
        }
    }
}
