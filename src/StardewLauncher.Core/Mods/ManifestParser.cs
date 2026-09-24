using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// manifest.json 的容错解析。真实 Mod 里的 manifest 写法五花八门，这里集中处理各种坑。
/// </summary>
public static class ManifestParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        // 不转义中文，方便调试输出与日志
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>读取并解析指定 manifest.json。失败时返回 null 并通过 error 给出可读原因。</summary>
    public static Manifest? Parse(string filePath, out string? error)
    {
        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            error = $"manifest.json 读取失败：{ex.Message}";
            return null;
        }

        return ParseJson(json, out error);
    }

    /// <summary>解析 manifest JSON 文本。失败时返回 null 并通过 error 给出可读原因。</summary>
    public static Manifest? ParseJson(string json, out string? error)
    {
        if (TryDeserialize(json, out var manifest, out var exception))
        {
            if (string.IsNullOrWhiteSpace(manifest!.UniqueId))
            {
                error = "缺少 UniqueID";
                return null;
            }

            error = null;
            return manifest;
        }

        // 有些 manifest 里混进了非法控制字符导致整体解析失败，去掉换行后再试一次
        var compact = json.Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (!ReferenceEquals(compact, json) && TryDeserialize(compact, out manifest, out _))
        {
            if (string.IsNullOrWhiteSpace(manifest!.UniqueId))
            {
                error = "缺少 UniqueID";
                return null;
            }

            error = null;
            return manifest;
        }

        error = $"manifest.json 解析失败：{exception?.Message ?? "未知错误"}";
        return null;
    }

    private static bool TryDeserialize(string json, out Manifest? manifest, out Exception? exception)
    {
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(json, Options);
            exception = null;
            return manifest is not null;
        }
        catch (Exception ex)
        {
            manifest = null;
            exception = ex;
            return false;
        }
    }
}
