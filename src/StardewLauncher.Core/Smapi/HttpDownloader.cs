using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Smapi;

/// <summary>
/// 统一的 HTTP 下载与文本获取。静态复用 HttpClient，失败自动重试，任何异常都不向外抛。
/// </summary>
public static class HttpDownloader
{
    private const string DefaultUserAgent = "StardewLauncher";
    private const string AcceptHeader = "application/vnd.github+json";

    /// <summary>重试等待时间：最多重试 2 次，间隔递增。</summary>
    private static readonly int[] RetryDelaysMs = [800, 2000];

    /// <summary>GetStringAsync 的内存缓存有效期，避免同一进程内对同一地址反复请求。</summary>
    private static readonly TimeSpan TextCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        // 必须声明支持的压缩方式，这是 GitHub 发布页"资产列表"片段的前提：
        // 不带 Accept-Encoding 的请求，它直接回 500（实测同一个地址，带上就是 200）。
        // 顺带也让所有文本 / JSON 请求走压缩传输，省流量、更快。
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate |
                                 DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(100)
    };

    /// <summary>
    /// 专用于探测重定向的客户端。必须关掉自动跳转，否则请求会一路跟到最终响应，
    /// 拿到的是 200 而 Headers.Location 为空，就取不到跳转目标了。
    /// </summary>
    private static readonly HttpClient RedirectProbeClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate |
                                 DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Dictionary<string, (string Text, DateTimeOffset FetchedAt)> TextCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object TextCacheLock = new();

    /// <summary>GET 一个文本资源（带缓存与重试）。失败返回 null，不抛出。</summary>
    public static async Task<string?> GetStringAsync(string url, string? userAgent = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var now = DateTimeOffset.Now;
        lock (TextCacheLock)
        {
            if (TextCache.TryGetValue(url, out var cached) && now - cached.FetchedAt < TextCacheTtl)
                return cached.Text;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = CreateRequest(url, userAgent);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);

                var status = (int)response.StatusCode;

                // 4xx 是对方的明确答复（文件不存在、没权限），再问几次也是同样结果，
                // 直接返回可以让调用方把时间留给别的地址。408 / 429 例外，属于等一下可能就好。
                if (status is >= 400 and < 500 and not (408 or 429))
                {
                    Log.Warn($"请求被拒绝：{url}（{status} {response.ReasonPhrase}）");
                    return null;
                }

                response.EnsureSuccessStatusCode();

                var text = await response.Content.ReadAsStringAsync(token);

                lock (TextCacheLock)
                {
                    TextCache[url] = (text, DateTimeOffset.Now);
                }

                return text;
            }
            catch (OperationCanceledException)
            {
                Log.Warn($"请求已取消：{url}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn($"请求失败（第 {attempt + 1} 次）：{url}（{ex.Message}）");

                if (attempt >= RetryDelaysMs.Length) return null;

                try
                {
                    await Task.Delay(RetryDelaysMs[attempt], token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// POST 一段 JSON 并返回响应文本（与 GET 一样带重试，失败返回 null，不抛异常）。
    /// 给只接受 POST 的公开接口用，例如 smapi.io 的 Mod 更新查询。
    /// </summary>
    public static async Task<string?> PostJsonAsync(string url, string json, string? userAgent = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                request.Headers.TryAddWithoutValidation("User-Agent",
                    string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);

                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);

                var status = (int)response.StatusCode;

                // 4xx 是对方的明确答复，重试也是同样结果；408 / 429 除外
                if (status is >= 400 and < 500 and not (408 or 429))
                {
                    Log.Warn($"请求被拒绝：{url}（{status} {response.ReasonPhrase}）");
                    return null;
                }

                response.EnsureSuccessStatusCode();

                return await response.Content.ReadAsStringAsync(token);
            }
            catch (OperationCanceledException)
            {
                Log.Warn($"请求已取消：{url}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn($"请求失败（第 {attempt + 1} 次）：{url}（{ex.Message}）");

                if (attempt >= RetryDelaysMs.Length) return null;

                try
                {
                    await Task.Delay(RetryDelaysMs[attempt], token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// 发一次不跟随重定向的 GET，返回 Location 头。
    /// 用于从 GitHub 网页版的 releases/latest 跳转地址里取版本号：它不经过 API，因此不受 60 次/小时的限流。
    /// </summary>
    public static async Task<string?> GetRedirectLocationAsync(string url, string? userAgent = null,
        CancellationToken token = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent",
                string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);

            using var response = await RedirectProbeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var location = response.Headers.Location?.ToString();
            return string.IsNullOrWhiteSpace(location) ? null : location;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"探测重定向失败：{url}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>丢弃指定地址的内存缓存，供需要强制走网络的调用方使用。</summary>
    internal static void InvalidateCache(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (TextCacheLock) TextCache.Remove(url);
    }

    private static HttpRequestMessage CreateRequest(string url, string? userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        request.Headers.TryAddWithoutValidation("Accept", AcceptHeader);
        return request;
    }
}
