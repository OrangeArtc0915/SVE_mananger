using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// 写入 Mods 目录下的 SMAPI-config.json，用来覆盖加载顺序与更新检查清单。
/// 只动这个文件，绝不碰游戏目录里 smapi-internal\config.json（那个会被 SMAPI 升级重置）。
/// </summary>
public static class SmapiConfigWriter
{
    /// <summary>配置文件名，固定放在 Mods 目录下。</summary>
    public const string FileName = "SMAPI-config.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        // 不转义中文
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>写入配置。空数组的键不写，只写需要覆盖的键。</summary>
    public static void Write(string modsDirectory, IEnumerable<string> loadEarly, IEnumerable<string> loadLate,
        IEnumerable<string> suppressUpdateChecks)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory)) return;

        var payload = new Dictionary<string, string[]>();
        AddIfAny(payload, "ModsToLoadEarly", loadEarly);
        AddIfAny(payload, "ModsToLoadLate", loadLate);
        AddIfAny(payload, "SuppressUpdateChecks", suppressUpdateChecks);

        try
        {
            Directory.CreateDirectory(modsDirectory);
            IO.AtomicFile.WriteAllText(Path.Combine(modsDirectory, FileName), JsonSerializer.Serialize(payload, Options));
            Log.Info($"已写入 {FileName}：{string.Join("、", payload.Keys)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"写入 {FileName} 失败：{ex.Message}");
        }
    }

    /// <summary>删除配置文件。文件不存在时什么也不做。</summary>
    public static void Clear(string modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory)) return;

        try
        {
            var path = Path.Combine(modsDirectory, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除 {FileName} 失败：{ex.Message}");
        }
    }

    private static void AddIfAny(Dictionary<string, string[]> payload, string key, IEnumerable<string>? values)
    {
        var clean = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        if (clean.Length > 0) payload[key] = clean;
    }
}
