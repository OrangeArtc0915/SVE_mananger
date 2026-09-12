using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Nexus;

/// <summary>
/// 一条 <c>nxm://</c> 链接。真实格式示例：
/// <c>nxm://stardewvalley/mods/1234/files/5678?key=abc123&amp;expires=1735689600</c>。
/// 这类链接由 Nexus 网页在用户点击「Mod Manager Download」时生成，
/// 其中 key / expires 是 Nexus 官方给第三方 Mod 管理器使用的临时下载凭证，
/// 非会员据此也能调用 download_link 接口（与 Vortex / MO2 的做法一致）。
/// </summary>
public sealed class NxmLink
{
    /// <summary>游戏域名，例如 stardewvalley。</summary>
    public string GameDomain { get; init; } = "";

    public int ModId { get; init; }

    public int FileId { get; init; }

    /// <summary>下载凭证，可能为空（用户直接复制的链接往往不带）。</summary>
    public string? Key { get; init; }

    /// <summary>凭证过期时间戳（Unix 秒，字符串形式），可能为空。</summary>
    public string? Expires { get; init; }

    /// <summary>原始链接文本。</summary>
    public string OriginalUri { get; init; } = "";

    /// <summary>该链接是否携带 key/expires（决定非会员能否直接下载）。</summary>
    public bool HasDownloadToken => !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Expires);

    /// <summary>是否指向星露谷（domain 为 stardewvalley）。</summary>
    public bool IsStardewValley
        => string.Equals(GameDomain, "stardewvalley", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mod 页面地址（没有下载凭证时用它引导用户去网页下载）。</summary>
    public string ModPageUrl => $"https://www.nexusmods.com/{GameDomain}/mods/{ModId}";

    /// <summary>不含查询串的安全文本，用于日志与界面显示（避免把下载凭证写进日志）。</summary>
    public string SafeText => $"nxm://{GameDomain}/mods/{ModId}/files/{FileId}";

    /// <summary>解析 nxm:// 链接；格式不合法返回 null，任何异常都不向外抛。</summary>
    public static NxmLink? Parse(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            Log.Warn("nxm 链接为空，已忽略");
            return null;
        }

        var text = uri.Trim();

        try
        {
            if (!text.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"不是 nxm 链接，已忽略：{Truncate(text)}");
                return null;
            }

            // 手工拆解，避免未知 scheme 在各版本 Uri 解析器上的差异
            var rest = text[6..];

            var hashIndex = rest.IndexOf('#');
            if (hashIndex >= 0) rest = rest[..hashIndex];

            var queryIndex = rest.IndexOf('?');
            var query = queryIndex >= 0 ? rest[(queryIndex + 1)..] : "";
            var authorityAndPath = queryIndex >= 0 ? rest[..queryIndex] : rest;

            var slashIndex = authorityAndPath.IndexOf('/');
            var domain = slashIndex >= 0 ? authorityAndPath[..slashIndex] : authorityAndPath;
            var path = slashIndex >= 0 ? authorityAndPath[slashIndex..] : "";

            if (string.IsNullOrWhiteSpace(domain))
            {
                Log.Warn($"nxm 链接缺少游戏域名，已忽略：{Truncate(text)}");
                return null;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            int? modId = null;
            int? fileId = null;

            for (var i = 0; i + 1 < segments.Length; i++)
            {
                var label = segments[i];
                var value = segments[i + 1];

                if (modId is null && label.Equals("mods", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var parsedMod) && parsedMod > 0) modId = parsedMod;
                }
                else if (modId is not null && fileId is null && label.Equals("files", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var parsedFile) && parsedFile > 0) fileId = parsedFile;
                }
            }

            if (modId is null)
            {
                Log.Warn($"nxm 链接缺少有效的 mods/<数字> 段，已忽略：{Truncate(text)}");
                return null;
            }

            if (fileId is null)
            {
                // 只到 /mods/{id} 的链接无法直接下载，明确记下原因
                Log.Warn($"nxm 链接缺少 files/<数字> 段，无法直接下载：{Truncate(text)}");
                return null;
            }

            ParseQuery(query, out var key, out var expires);

            return new NxmLink
            {
                GameDomain = domain,
                ModId = modId.Value,
                FileId = fileId.Value,
                Key = key,
                Expires = expires,
                OriginalUri = text
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"nxm 链接解析异常，已忽略：{Truncate(text)}（{ex.Message}）");
            return null;
        }
    }

    private static void ParseQuery(string query, out string? key, out string? expires)
    {
        key = null;
        expires = null;

        if (string.IsNullOrEmpty(query)) return;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;

            var name = pair[..separator];
            var value = Decode(pair[(separator + 1)..]);

            if (name.Equals("key", StringComparison.OrdinalIgnoreCase)) key = value;
            else if (name.Equals("expires", StringComparison.OrdinalIgnoreCase)) expires = value;
        }
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }

    /// <summary>日志里不打印完整链接：先去掉查询串（内含下载凭证），再截断。</summary>
    private static string Truncate(string text)
    {
        var cut = text.IndexOf('?');
        var safe = cut >= 0 ? text[..cut] + "?…" : text;
        return safe.Length <= 160 ? safe : safe[..160] + "…";
    }
}
