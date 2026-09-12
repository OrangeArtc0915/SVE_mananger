using System.Text.Json;
using System.Text.RegularExpressions;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Smapi;

/// <summary>
/// 启动器自建云端文件库里的 SMAPI 安装包查询。
/// 先按下载源列出 GitHub / Gitee 上 resource 目录的内容，取版本号最大的文件；
/// 列目录失败（网络受限或接口变更）时退回到内置的兜底地址。任何异常都不向外抛。
/// </summary>
public static class SmapiCloudRepository
{
    private const string GitHubListApiUrl =
        "https://api.github.com/repos/OrangeArtc0915/SVE_mananger/contents/resource?ref=resource";

    private const string GiteeListApiUrl =
        "https://gitee.com/api/v5/repos/orangearc655743/SVE_mananger_File/contents/resource?ref=master";

    private const string UserAgent = "StardewLauncher";

    /// <summary>兜底版本号，与兜底地址里的文件名保持一致。</summary>
    private const string FallbackVersion = "4.5.2";

    private const string GitHubFallbackUrl =
        "https://raw.githubusercontent.com/OrangeArtc0915/SVE_mananger/resource/resource/SMAPI%204.5.2%20installer.7z";

    private const string GiteeFallbackUrl =
        "https://gitee.com/orangearc655743/SVE_mananger_File/raw/master/resource/SMAPI%204.5.2%20installer.7z";

    /// <summary>从文件名解析版本号：SMAPI 4.5.2 installer.7z → 4.5.2。</summary>
    private static readonly Regex VersionPattern =
        new(@"SMAPI\s+([0-9]+(?:\.[0-9]+)+)\s+installer",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>查询指定下载源上版本号最大的 SMAPI 安装包。</summary>
    public static async Task<CloudSmapiPackage?> GetLatestAsync(DownloadSource source, CancellationToken token = default)
    {
        try
        {
            var apiUrl = source == DownloadSource.Gitee ? GiteeListApiUrl : GitHubListApiUrl;
            var json = await HttpDownloader.GetStringAsync(apiUrl, UserAgent, token);

            if (!string.IsNullOrWhiteSpace(json))
            {
                var latest = Parse(json);
                if (latest is not null)
                {
                    Log.Info($"云端仓库（{DownloadSourceUrls.DisplayName(source)}）最新 SMAPI：{latest.Version}（{latest.FileName}）");
                    return latest;
                }

                Log.Warn($"云端仓库列目录未找到可用的 SMAPI 安装包，改用兜底地址：{apiUrl}");
            }
            else
            {
                Log.Warn($"云端仓库列目录失败，改用兜底地址：{apiUrl}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"云端仓库查询异常，改用兜底地址：{ex.Message}");
        }

        return Fallback(source);
    }

    /// <summary>解析列目录接口返回的 JSON 数组，返回版本号最大的一项。</summary>
    private static CloudSmapiPackage? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        CloudSmapiPackage? best = null;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            var name = GetString(element, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var match = VersionPattern.Match(name);
            if (!match.Success) continue;

            // 优先复用接口给出的 download_url，避免自行拼接时漏掉 URL 编码
            var url = GetString(element, "download_url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            var version = match.Groups[1].Value;
            var size = GetLong(element, "size") ?? 0;

            if (best is null || SemVer.IsNewer(version, best.Version))
                best = new CloudSmapiPackage(version, name, url, size);
        }

        return best;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>GitHub 的 size 是数字，Gitee 为 null，两者都能安全取到。</summary>
    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>列目录不可用时退回内置地址。</summary>
    private static CloudSmapiPackage Fallback(DownloadSource source)
    {
        var fileName = $"SMAPI {FallbackVersion} installer.7z";
        var url = source == DownloadSource.Gitee ? GiteeFallbackUrl : GitHubFallbackUrl;

        Log.Warn($"已退回云端仓库兜底地址：{url}");
        return new CloudSmapiPackage(FallbackVersion, fileName, url, 0);
    }
}
