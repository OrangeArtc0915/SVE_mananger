using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Smapi;

/// <summary>
/// 查询 SMAPI 最新版本。结果会缓存到磁盘，避免撞上 GitHub 未鉴权的 60 次/小时限流。
/// 网络失败时退回过期缓存，仍无缓存则返回 null 并记 Warn，绝不抛出。
/// </summary>
public static class SmapiUpdateChecker
{
    private const string LatestApiUrl = "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest";

    /// <summary>
    /// 网页版的 releases/latest。它会 302 到 /releases/tag/&lt;版本&gt;，
    /// 走的是 github.com 而不是 api.github.com，因此不受未鉴权 60 次/小时的限流，作为首选来源。
    /// </summary>
    private const string LatestRedirectUrl = "https://github.com/Pathoschild/SMAPI/releases/latest";

    private const string UserAgent = "StardewLauncher";
    private const string ReleaseDownloadRoot = "https://github.com/Pathoschild/SMAPI/releases/download";
    private const string ReleasePageRoot = "https://github.com/Pathoschild/SMAPI/releases/tag";

    /// <summary>缓存有效期 24 小时。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，方便用户直接查看缓存文件
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static string CacheFile => Path.Combine(Paths.Cache, "SmapiRelease.json");

    /// <summary>获取 SMAPI 最新发布信息。forceRefresh 为 true 时忽略缓存强制联网。</summary>
    public static async Task<SmapiRelease?> GetLatestAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        var cached = ReadCache();

        if (!forceRefresh && cached?.Release is not null &&
            DateTimeOffset.Now - cached.FetchedAt < CacheTtl)
        {
            Log.Info($"使用缓存的 SMAPI 版本信息：{cached.Release.Version}");
            return cached.Release;
        }

        if (forceRefresh) HttpDownloader.InvalidateCache(LatestApiUrl);

        var fetched = await FetchAsync(token);
        if (fetched is not null)
        {
            WriteCache(new ReleaseCache { FetchedAt = DateTimeOffset.Now, Release = fetched });
            return fetched;
        }

        if (cached?.Release is not null)
        {
            Log.Warn("SMAPI 版本查询失败，已退回过期缓存");
            return cached.Release;
        }

        Log.Warn("SMAPI 版本查询失败，且本地没有可用缓存");
        return null;
    }

    private static async Task<SmapiRelease?> FetchAsync(CancellationToken token)
    {
        // 先走不限额的网页重定向，失败再退回 API（API 能额外拿到发布时间）
        var byRedirect = await FetchFromRedirectAsync(token);
        if (byRedirect is not null) return byRedirect;

        return await FetchFromApiAsync(token);
    }

    /// <summary>从网页版 releases/latest 的跳转地址里取版本号，不消耗 GitHub API 配额。</summary>
    private static async Task<SmapiRelease?> FetchFromRedirectAsync(CancellationToken token)
    {
        try
        {
            var location = await HttpDownloader.GetRedirectLocationAsync(LatestRedirectUrl, UserAgent, token);
            if (string.IsNullOrWhiteSpace(location)) return null;

            // 形如 https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2
            var slug = location.TrimEnd('/').Split('/').LastOrDefault();
            if (string.IsNullOrWhiteSpace(slug)) return null;

            var version = Uri.UnescapeDataString(slug).Trim().TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(version)) return null;

            Log.Info($"已通过网页重定向获取 SMAPI 最新版本：{version}");
            return new SmapiRelease(
                version,
                $"{ReleaseDownloadRoot}/{version}/SMAPI-{version}-installer.zip",
                $"{ReleasePageRoot}/{version}",
                null);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"SMAPI 版本查询（重定向）失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<SmapiRelease?> FetchFromApiAsync(CancellationToken token)
    {
        try
        {
            var json = await HttpDownloader.GetStringAsync(LatestApiUrl, UserAgent, token);
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var version = GetString(root, "tag_name")?.Trim().TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(version)) return null;

            var pageUrl = GetString(root, "html_url") ?? $"{ReleasePageRoot}/{version}";
            var downloadUrl = FindInstallerAsset(root)
                ?? $"{ReleaseDownloadRoot}/{version}/SMAPI-{version}-installer.zip";

            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(GetString(root, "published_at"), out var published))
                publishedAt = published;

            Log.Info($"已获取 SMAPI 最新版本：{version}");
            return new SmapiRelease(version, downloadUrl, pageUrl, publishedAt);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"SMAPI 版本查询失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>从 assets 里找 installer.zip，找不到返回 null 由调用方按命名规则拼接。</summary>
    private static string? FindInstallerAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            if (string.IsNullOrWhiteSpace(name) ||
                !name.Contains("installer.zip", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = GetString(asset, "browser_download_url");
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ReleaseCache? ReadCache()
    {
        try
        {
            var path = CacheFile;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ReleaseCache>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"SMAPI 缓存读取失败：{ex.Message}");
            return null;
        }
    }

    private static void WriteCache(ReleaseCache cache)
    {
        try
        {
            Directory.CreateDirectory(Paths.Cache);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"SMAPI 缓存写入失败：{ex.Message}");
        }
    }

    /// <summary>磁盘缓存结构，带写入时间用于判断是否过期。</summary>
    private sealed class ReleaseCache
    {
        public DateTimeOffset FetchedAt { get; set; }

        public SmapiRelease? Release { get; set; }
    }
}
