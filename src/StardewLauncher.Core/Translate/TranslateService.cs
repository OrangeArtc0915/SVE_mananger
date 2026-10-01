using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Translate;

/// <summary>一次翻译请求的结果状态。</summary>
public enum TranslateStatus
{
    /// <summary>成功，返回的文本与请求顺序一一对应。</summary>
    Ok,

    /// <summary>用户在设置里关掉了翻译（provider = none），不发请求。</summary>
    Disabled,

    /// <summary>选了服务商但密钥没配好，界面据此提示去设置页补。</summary>
    NotConfigured,

    /// <summary>请求失败（网络、鉴权、限流等），<see cref="TranslateResult.Error"/> 里有可读原因。</summary>
    Failed
}

/// <summary>翻译结果。失败时 Texts 为空，绝不向界面抛异常。</summary>
public sealed record TranslateResult(TranslateStatus Status, IReadOnlyList<string> Texts, string? Error = null)
{
    public bool IsOk => Status == TranslateStatus.Ok;

    public string FirstOrEmpty => Texts.Count > 0 ? Texts[0] : string.Empty;
}

/// <summary>
/// 翻译服务。按设置里的服务商直接调官方 REST 接口，一次可翻多条以省请求。
/// 统一的约定：无论哪种失败都不抛异常，而是返回带状态的 <see cref="TranslateResult"/>，
/// 密钥缺失一律归为 <see cref="TranslateStatus.NotConfigured"/>，让界面能区分「该去填密钥」和「网络出问题」。
/// </summary>
public static class TranslateService
{
    /// <summary>单次请求超时。翻译接口通常很快，太久说明网络不通，早点让用户知道。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient Client = new()
    {
        Timeout = Timeout
    };

    /// <summary>服务商在界面上的中文名。</summary>
    public static string ProviderDisplayName(string? provider) => (provider ?? "").Trim().ToLowerInvariant() switch
    {
        "microsoft" => "微软翻译",
        "baidu" => "百度翻译",
        "deepl" => "DeepL 翻译",
        "none" => "未启用",
        _ => "未启用"
    };

    /// <summary>目标语言在界面上的中文名。</summary>
    public static string TargetDisplayName(string? target) => (target ?? "").Trim().ToLowerInvariant() switch
    {
        "zh-hans" or "zh" or "zh-cn" or "zh_cn" => "简体中文",
        "zh-hant" or "cht" or "zh-tw" => "繁体中文",
        "en" or "en-us" => "英语",
        "ja" or "ja-jp" => "日语",
        "" => "简体中文",
        _ => target ?? ""
    };

    /// <summary>文本里是否含中文（用于判断某个名字是不是已经不用翻了）。</summary>
    public static bool HasChinese(string? text)
        => !string.IsNullOrEmpty(text) && text.Any(c => c >= '\u4e00' && c <= '\u9fff');

    /// <summary>
    /// 批量翻译。返回的文本顺序与传入顺序一致；家用的 Mod 列表规模很小，不做并发。
    /// </summary>
    public static async Task<TranslateResult> TranslateAsync(IReadOnlyList<string> texts,
        CancellationToken token = default)
    {
        if (texts is null || texts.Count == 0) return new TranslateResult(TranslateStatus.Ok, []);

        var settings = SettingsStore.Current;
        var provider = (settings.TranslateProvider ?? "").Trim().ToLowerInvariant();

        if (provider is "" or "none")
            return new TranslateResult(TranslateStatus.Disabled, [], "翻译已在设置里关闭");

        var key = settings.TranslateApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return new TranslateResult(TranslateStatus.NotConfigured, [], "还没填写翻译密钥");

        var target = string.IsNullOrWhiteSpace(settings.TranslateTargetLanguage)
            ? "zh-Hans"
            : settings.TranslateTargetLanguage.Trim();

        try
        {
            return provider switch
            {
                "microsoft" => await TranslateMicrosoftAsync(texts, key, settings.TranslateRegion, target, token),
                "baidu" => await TranslateBaiduAsync(texts, key, target, token),
                "deepl" => await TranslateDeepLAsync(texts, key, target, token),
                _ => new TranslateResult(TranslateStatus.Failed, [], $"不认识的服务商：{provider}")
            };
        }
        catch (OperationCanceledException)
        {
            Log.Warn("翻译请求已取消");
            return new TranslateResult(TranslateStatus.Failed, [], "翻译请求已取消");
        }
        catch (Exception ex)
        {
            // 网络异常到此为止：只记日志 + 交给界面提示，不让它冒泡到 UI 线程
            Log.Warn($"翻译请求失败：{ex.Message}");
            return new TranslateResult(TranslateStatus.Failed, [], ex.Message);
        }
    }

    /// <summary>微软（Azure）翻译。区域为空时不带 Region 头，兼容全球版资源。</summary>
    private static async Task<TranslateResult> TranslateMicrosoftAsync(IReadOnlyList<string> texts, string key,
        string? region, string target, CancellationToken token)
    {
        var url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" +
                  Uri.EscapeDataString(MapTarget("microsoft", target));

        // 微软要求请求体是对象数组，响应也按同一顺序返回，正好和批量入参对应
        var payload = JsonSerializer.Serialize(texts.Select(text => new { Text = text }));

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
        if (!string.IsNullOrWhiteSpace(region))
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region.Trim());

        using var response = await Client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
            return new TranslateResult(TranslateStatus.Failed, [], DescribeHttpFailure("微软翻译", response, body));

        var parsed = ParseMicrosoft(body, texts.Count);
        return parsed is null
            ? new TranslateResult(TranslateStatus.Failed, [], "微软翻译返回的内容无法解析")
            : new TranslateResult(TranslateStatus.Ok, parsed);
    }

    /// <summary>
    /// 百度翻译。鉴权就是「请求内签名」：appid + salt + sign=MD5(appid+q+salt+密钥)，
    /// 没有单独的换 token 接口，所以这里一次 POST 把参数与签名一起发出去。
    /// 密钥字段按「APPID:密钥」填写，因为设置里只有一个字符串位置。
    /// </summary>
    private static async Task<TranslateResult> TranslateBaiduAsync(IReadOnlyList<string> texts, string key,
        string target, CancellationToken token)
    {
        var (appId, secret) = SplitBaiduKey(key);
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
            return new TranslateResult(TranslateStatus.NotConfigured, [], "百度翻译的密钥请按「APPID:密钥」填写");

        // 百度用换行分隔多段，返回的 trans_result 按行对应；
        // 先把文本里的换行压成空格，免得一段描述被拆成多条、和入参数量对不上
        var query = string.Join("\n", texts.Select(text => text.ReplaceLineEndings(" ")));
        var salt = Random.Shared.Next(100000, 999999).ToString();
        var sign = Md5(appId + query + salt + secret);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://fanyi-api.baidu.com/api/trans/vip/translate")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = query,
                ["from"] = "auto",
                ["to"] = MapTarget("baidu", target),
                ["appid"] = appId,
                ["salt"] = salt,
                ["sign"] = sign
            })
        };

        using var response = await Client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
            return new TranslateResult(TranslateStatus.Failed, [], DescribeHttpFailure("百度翻译", response, body));

        return ParseBaidu(body, texts.Count);
    }

    /// <summary>DeepL 翻译（免费版接口）。多段文本用重复的 text 表单字段。</summary>
    private static async Task<TranslateResult> TranslateDeepLAsync(IReadOnlyList<string> texts, string key,
        string target, CancellationToken token)
    {
        var form = new List<KeyValuePair<string, string>>();
        foreach (var text in texts) form.Add(new KeyValuePair<string, string>("text", text));
        form.Add(new KeyValuePair<string, string>("target_lang", MapTarget("deepl", target)));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api-free.deepl.com/v2/translate")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {key}");

        using var response = await Client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
            return new TranslateResult(TranslateStatus.Failed, [], DescribeHttpFailure("DeepL 翻译", response, body));

        var parsed = ParseDeepL(body, texts.Count);
        return parsed is null
            ? new TranslateResult(TranslateStatus.Failed, [], "DeepL 返回的内容无法解析")
            : new TranslateResult(TranslateStatus.Ok, parsed);
    }

    /// <summary>
    /// 各服务商对目标语言码的要求不一样，这里统一映射一次，调用方只用「设置里那一套」：
    /// 微软认 zh-Hans 这种 BCP-47，DeepL 认大写 ZH-HANS，百度只认 zh / cht / en 这种短码。
    /// </summary>
    private static string MapTarget(string provider, string target)
    {
        var normalized = target.Trim();

        switch (provider)
        {
            case "deepl":
                return normalized.ToUpperInvariant().Replace('_', '-');

            case "baidu":
                var lower = normalized.ToLowerInvariant();
                if (lower is "zh-hant" or "zh-tw" or "cht") return "cht";
                if (lower.StartsWith("zh", StringComparison.Ordinal)) return "zh";
                return lower;

            default:
                return normalized;
        }
    }

    /// <summary>百度密钥形如「APPID:密钥」，两个值用第一个冒号切开。</summary>
    private static (string AppId, string Secret) SplitBaiduKey(string key)
    {
        var colon = key.IndexOf(':');
        if (colon <= 0 || colon >= key.Length - 1) return (string.Empty, string.Empty);

        return (key[..colon].Trim(), key[(colon + 1)..].Trim());
    }

    private static string Md5(string text)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string DescribeHttpFailure(string provider, HttpResponseMessage response, string body)
    {
        var reason = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();

        // 只截一小段响应体：既是可读的失败原因，也不会把整页错误页写进日志
        var snippet = string.IsNullOrWhiteSpace(body) ? "" : body.Trim();
        if (snippet.Length > 200) snippet = snippet[..200];

        return string.IsNullOrEmpty(snippet) ? $"{provider}请求失败：{reason}" : $"{provider}请求失败：{reason}（{snippet}）";
    }

    private static List<string>? ParseMicrosoft(string body, int expected)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var result = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("translations", out var translations) ||
                    translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
                    return null;

                result.Add(translations[0].TryGetProperty("text", out var text) ? text.GetString() ?? "" : "");
            }

            return result.Count == expected ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string>? ParseDeepL(string body, int expected)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("translations", out var translations) ||
                translations.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<string>();
            foreach (var item in translations.EnumerateArray())
                result.Add(item.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "");

            return result.Count == expected ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>百度响应：正常时 trans_result 是 [{src,dst}]，出错时带 error_code / error_msg。</summary>
    private static TranslateResult ParseBaidu(string body, int expected)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var errorCode = root.TryGetProperty("error_code", out var code) ? code.GetString() : null;
            if (!string.IsNullOrEmpty(errorCode) && errorCode != "0")
            {
                var message = root.TryGetProperty("error_msg", out var msg) ? msg.GetString() : errorCode;
                return new TranslateResult(TranslateStatus.Failed, [], $"百度翻译：{message}");
            }

            if (!root.TryGetProperty("trans_result", out var items) || items.ValueKind != JsonValueKind.Array)
                return new TranslateResult(TranslateStatus.Failed, [], "百度翻译返回的内容无法解析");

            var result = new List<string>();
            foreach (var item in items.EnumerateArray())
                result.Add(item.TryGetProperty("dst", out var dst) ? dst.GetString() ?? "" : "");

            return result.Count == expected
                ? new TranslateResult(TranslateStatus.Ok, result)
                : new TranslateResult(TranslateStatus.Failed, [], "百度翻译返回的条目数和请求对不上");
        }
        catch (JsonException)
        {
            return new TranslateResult(TranslateStatus.Failed, [], "百度翻译返回的内容无法解析");
        }
    }
}
