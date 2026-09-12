using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Nexus;

/// <summary>当前 API Key 对应的 Nexus 账号。</summary>
public sealed record NexusAccount(string UserName, int UserId, bool IsPremium, bool IsSupporter);

/// <summary>Nexus 限流配额（取自响应头，可能拿不到）。</summary>
public sealed record NexusQuota(int? HourlyRemaining, int? DailyRemaining, DateTimeOffset? HourlyReset);

/// <summary>Nexus 上的一个文件。</summary>
public sealed record NexusModFile(int FileId, string Name, string? Version, string? Category,
    long SizeInBytes, DateTimeOffset? UploadedAt);

/// <summary>
/// Nexus 上一个 Mod 的公开详情。字段是「查询用的精简信息」与「nxm 下载窗口用的完整信息」的并集，
/// 只由 <see cref="NexusApi.GetModAsync(string, int, CancellationToken)"/> 构造。
/// </summary>
public sealed record NexusModInfo(
    int ModId,
    string Name,
    string? Author,
    string? Summary,
    string? Version,
    string ModPageUrl,
    bool AdultContent,
    bool HasUpdate,
    int EndorsementCount,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// Nexus Mods REST v1 客户端。所有请求都带用户的 <c>apikey</c> 头。
/// download_link 接口默认仅限会员，但携带 nxm 链接带来的 key/expires 时非会员同样可用
/// （这是 Nexus 官方给第三方 Mod 管理器的标准路径，不做任何鉴权绕过）。
/// 所有方法都不抛异常：失败返回 null / 空集合，并把可读原因写进 <see cref="LastError"/>。
/// </summary>
public static class NexusApi
{
    private const string BaseUrl = "https://api.nexusmods.com/v1/";
    private const string UserAgent = "StardewLauncher/0.1";

    /// <summary>默认游戏域名：星露谷。</summary>
    private const string DefaultGameDomain = "stardewvalley";

    private const string SiteBase = "https://www.nexusmods.com/" + DefaultGameDomain;

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Regex ModIdPattern = new(@"/mods/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>最近一次响应里的配额信息，供设置页显示。</summary>
    public static NexusQuota LastQuota { get; private set; } = new(null, null, null);

    /// <summary>最近一次调用的错误说明，供界面显示可读原因。</summary>
    public static string? LastError { get; private set; }

    /// <summary>是否已在设置里填了 Nexus API Key。</summary>
    public static bool HasApiKey => !string.IsNullOrWhiteSpace(SettingsStore.Current.NexusApiKey);

    /// <summary>校验 API Key。失败（含网络失败）返回 null，不抛异常。</summary>
    public static async Task<NexusAccount?> ValidateAsync(string? apiKey = null, CancellationToken token = default)
    {
        LastError = null;

        var key = string.IsNullOrWhiteSpace(apiKey) ? SettingsStore.Current.NexusApiKey : apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            LastError = "未配置 Nexus API Key";
            Log.Warn("未配置 Nexus API Key，跳过账号校验");
            return null;
        }

        try
        {
            using var response = await SendAsync(BaseUrl + "users/validate.json", key, token);
            if (response is null) return null;
            if (await DescribeFailureAsync(response, "users/validate", token) is not null) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new NexusAccount(
                StringOf(root, "name")?.Trim() ?? "",
                IntOf(root, "user_id") ?? 0,
                BoolOf(root, "is_premium"),
                BoolOf(root, "is_supporter"));
        }
        catch (Exception ex)
        {
            LastError = $"解析账号信息失败：{ex.Message}";
            Log.Warn($"校验 Nexus 账号失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>按 Mod ID 查星露谷 Mod 详情（默认域名的便捷重载）。</summary>
    public static Task<NexusModInfo?> GetModAsync(int modId, CancellationToken token = default)
        => GetModAsync(DefaultGameDomain, modId, token);

    /// <summary>查询 Mod 基本信息。失败返回 null，不抛异常。</summary>
    public static async Task<NexusModInfo?> GetModAsync(string gameDomain, int modId,
        CancellationToken token = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(gameDomain) || modId <= 0)
        {
            LastError = "Mod 参数无效";
            Log.Warn($"Nexus Mod 参数无效：game={gameDomain} modId={modId}");
            return null;
        }

        var url = $"{BaseUrl}games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}.json";

        try
        {
            using var response = await SendAsync(url, null, token);
            if (response is null) return null;
            if (await DescribeFailureAsync(response, $"mods/{modId}", token) is not null) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            DateTimeOffset? updatedAt = null;
            var updatedText = StringOf(root, "updated_time");
            if (!string.IsNullOrWhiteSpace(updatedText) &&
                DateTimeOffset.TryParse(updatedText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                updatedAt = parsed;
            }

            return new NexusModInfo(
                modId,
                StringOf(root, "name")?.Trim() ?? $"Mod {modId}",
                StringOf(root, "author")?.Trim(),
                StringOf(root, "summary")?.Trim(),
                StringOf(root, "version")?.Trim(),
                BuildModPageUrl(gameDomain, modId),
                BoolOf(root, "adult_content"),
                BoolOf(root, "has_update"),
                IntOf(root, "endorsement_count") ?? 0,
                updatedAt);
        }
        catch (Exception ex)
        {
            LastError = $"解析 Mod 信息失败：{ex.Message}";
            Log.Warn($"查询 Nexus Mod 信息失败：mods/{modId}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>列出 Mod 的文件列表。</summary>
    public static async Task<IReadOnlyList<NexusModFile>> GetFilesAsync(string gameDomain, int modId,
        CancellationToken token = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(gameDomain) || modId <= 0)
        {
            LastError = "Mod 参数无效";
            return [];
        }

        var url = $"{BaseUrl}games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files.json";
        var files = new List<NexusModFile>();

        try
        {
            using var response = await SendAsync(url, null, token);
            if (response is null) return files;
            if (await DescribeFailureAsync(response, $"mods/{modId}/files", token) is not null) return files;

            var json = await response.Content.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("files", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return files;
            }

            foreach (var item in array.EnumerateArray())
            {
                var fileId = IntOf(item, "file_id") ?? 0;
                if (fileId <= 0) continue;

                // Nexus 的 size_in_bytes 单位是 KB
                var sizeKb = LongOf(item, "size_in_bytes") ?? 0;
                var sizeBytes = sizeKb > 0 ? sizeKb * 1024 : 0;

                DateTimeOffset? uploadedAt = null;
                if (LongOf(item, "uploaded_timestamp") is { } stamp && stamp > 0)
                {
                    try { uploadedAt = DateTimeOffset.FromUnixTimeSeconds(stamp); }
                    catch { uploadedAt = null; }
                }

                files.Add(new NexusModFile(
                    fileId,
                    StringOf(item, "name")?.Trim() ?? $"文件 {fileId}",
                    StringOf(item, "version")?.Trim(),
                    StringOf(item, "category_name")?.Trim(),
                    sizeBytes,
                    uploadedAt));
            }

            return files;
        }
        catch (Exception ex)
        {
            LastError = $"解析文件列表失败：{ex.Message}";
            Log.Warn($"查询 Nexus 文件列表失败：mods/{modId}（{ex.Message}）");
            return files;
        }
    }

    /// <summary>
    /// 取下载直链。带 key/expires 时非会员也可用。
    /// 返回的是 Nexus 生成的临时直链，拿到后即可用普通 GET 下载。
    /// </summary>
    public static async Task<string?> GetDownloadUrlAsync(string gameDomain, int modId, int fileId,
        string? key = null, string? expires = null, CancellationToken token = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(gameDomain) || modId <= 0 || fileId <= 0)
        {
            LastError = "下载参数无效";
            return null;
        }

        var url = $"{BaseUrl}games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files/{fileId}/download_link.json";
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(expires))
        {
            url += $"?key={Uri.EscapeDataString(key)}&expires={Uri.EscapeDataString(expires)}";
        }

        try
        {
            using var response = await SendAsync(url, null, token);
            if (response is null) return null;
            if (await DescribeFailureAsync(response, $"download_link {modId}/{fileId}", token) is not null) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                LastError = "下载接口返回的不是链接数组";
                Log.Warn($"下载接口返回格式异常：mods/{modId}/files/{fileId}");
                return null;
            }

            // 数组里可能有多条（不同 CDN），取第一条即可
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var link = StringOf(item, "URI") ?? StringOf(item, "uri");
                if (!string.IsNullOrWhiteSpace(link)) return link.Trim();
            }

            LastError = "下载接口未返回可用直链";
            Log.Warn($"下载接口未返回直链：mods/{modId}/files/{fileId}");
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"解析下载直链失败：{ex.Message}";
            Log.Warn($"获取下载直链失败：mods/{modId}/files/{fileId}（{ex.Message}）");
            return null;
        }
    }

    // ————— 地址解析与拼接 —————

    /// <summary>从「2400」或「https://www.nexusmods.com/stardewvalley/mods/2400?tab=files」里解析出 Mod ID。</summary>
    public static bool TryParseModId(string? input, out int modId)
    {
        modId = 0;

        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = input.Trim();

        if (int.TryParse(text, out var plain))
        {
            if (plain <= 0) return false;
            modId = plain;
            return true;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // 只认路径里的 /mods/<数字>，查询串与片段一律忽略
        var match = ModIdPattern.Match(uri.AbsolutePath);
        if (!match.Success) return false;

        return int.TryParse(match.Groups[1].Value, out modId) && modId > 0;
    }

    /// <summary>拼 Mod 页面地址。</summary>
    public static string BuildModPageUrl(string gameDomain, int modId)
        => $"https://www.nexusmods.com/{gameDomain}/mods/{modId}";

    /// <summary>拼站内搜索地址（Nexus API 没有搜索接口，搜名字只能开网页）。</summary>
    public static string BuildSearchUrl(string keyword)
        => $"{SiteBase}/search?gsearch={Uri.EscapeDataString(keyword ?? string.Empty)}";

    // ————— HTTP 与错误处理 —————

    private static async Task<HttpResponseMessage?> SendAsync(string url, string? apiKeyOverride,
        CancellationToken token)
    {
        try
        {
            var key = string.IsNullOrWhiteSpace(apiKeyOverride)
                ? SettingsStore.Current.NexusApiKey
                : apiKeyOverride.Trim();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 个人 API Key 只放在请求头里，绝不写进日志
            if (!string.IsNullOrWhiteSpace(key))
                request.Headers.TryAddWithoutValidation("apikey", key.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            ReadQuota(response);
            return response;
        }
        catch (OperationCanceledException)
        {
            LastError = "请求超时或已取消";
            Log.Warn($"Nexus 请求超时或已取消：{url}");
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"网络请求失败：{ex.Message}";
            Log.Warn($"Nexus 请求失败：{url}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>非成功状态码时把响应体与限流信息写进 LastError，返回可读说明；成功返回 null。</summary>
    private static async Task<string?> DescribeFailureAsync(HttpResponseMessage response, string what,
        CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return null;

        var code = (int)response.StatusCode;

        var body = "";
        try { body = (await response.Content.ReadAsStringAsync(token)).Trim(); }
        catch { /* 读不到响应体不影响错误说明 */ }

        if (body.Length > 300) body = body[..300] + "…";

        var hint = code switch
        {
            429 => DescribeRetryAfter(response),
            403 => "（无权限：非会员需要 nxm 链接携带 key/expires 才能取直链）",
            401 => "（API Key 无效或已失效）",
            _ => ""
        };

        var message = $"HTTP {code} {response.ReasonPhrase}{hint}" +
                      (string.IsNullOrWhiteSpace(body) ? "" : "：" + body);

        LastError = message;
        Log.Warn($"Nexus 接口调用失败（{what}）：{message}");
        return message;
    }

    private static string DescribeRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } delta)
            return $"（已被限流，建议 {delta.TotalSeconds:0} 秒后重试）";

        if (retryAfter?.Date is { } date)
            return $"（已被限流，可重试时间 {date:HH:mm:ss}）";

        return "（请求过于频繁，已被限流）";
    }

    /// <summary>每次响应后读取 Nexus 的限流配额响应头。</summary>
    private static void ReadQuota(HttpResponseMessage response)
    {
        try
        {
            var hourly = HeaderInt(response, "X-RL-Hourly-Remaining");
            var daily = HeaderInt(response, "X-RL-Daily-Remaining");
            var reset = HeaderDate(response, "X-RL-Hourly-Reset");

            if (hourly is not null || daily is not null || reset is not null)
                LastQuota = new NexusQuota(hourly, daily, reset);
        }
        catch
        {
            // 配额只是展示用，读不到就保持原值
        }
    }

    private static int? HeaderInt(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;

        foreach (var value in values)
        {
            if (int.TryParse(value?.Trim(), out var parsed)) return parsed;
        }

        return null;
    }

    private static DateTimeOffset? HeaderDate(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;

        foreach (var value in values)
        {
            if (DateTimeOffset.TryParse(value?.Trim(), out var parsed)) return parsed;
        }

        return null;
    }

    // ————— JsonElement 取值助手 —————

    private static string? StringOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool BoolOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) &&
           value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
           value.GetBoolean();

    private static int? IntOf(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static long? LongOf(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;

        if (value.TryGetInt64(out var parsed)) return parsed;
        return value.TryGetDouble(out var floating) ? (long)floating : null;
    }
}
