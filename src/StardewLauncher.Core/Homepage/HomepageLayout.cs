using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Homepage;

/// <summary>主页小组件布局的导出文件。</summary>
public sealed class HomepageLayoutFile
{
    public int Version { get; set; } = 1;

    public DateTime ExportedAt { get; set; } = DateTime.Now;

    /// <summary>小组件顺序。</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>被收起的小组件。</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>图片挂件用的图片（换机后可能不存在，导入时会提示）。</summary>
    public string ImageFile { get; set; } = string.Empty;
}

/// <summary>导入 / 导出的结果。</summary>
public sealed record HomepageLayoutResult(bool Ok, string Message);

/// <summary>
/// 主页小组件布局的导出与导入：把顺序、显隐与图片挂件的图存成一个 json，
/// 换机器或重装后导回来即可。
/// </summary>
public static class HomepageLayout
{
    /// <summary>全部小组件 id。界面上的内置顺序与这里保持一致。</summary>
    public static readonly string[] WidgetIds = ["calendar", "weather", "quote", "saves", "image", "mods"];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，方便用户手改导出文件
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static HomepageLayoutResult Export(string filePath)
    {
        try
        {
            var settings = SettingsStore.Current;

            var model = new HomepageLayoutFile
            {
                Order = Normalize(settings.HomepageWidgetOrder),
                Hidden = Normalize(settings.HomepageHiddenWidgets),
                ImageFile = settings.HomepageImageFile ?? string.Empty
            };

            File.WriteAllText(filePath, JsonSerializer.Serialize(model, Options));

            Log.Info($"已导出主页布局：{filePath}");
            return new HomepageLayoutResult(true, $"已导出到 {filePath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"导出主页布局失败：{ex.Message}");
            return new HomepageLayoutResult(false, $"导出失败：{ex.Message}");
        }
    }

    public static HomepageLayoutResult Import(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new HomepageLayoutResult(false, "文件不存在");

            var info = new FileInfo(filePath);
            if (info.Length > 1024 * 1024) return new HomepageLayoutResult(false, "这个文件不像是布局文件（太大）");

            var model = JsonSerializer.Deserialize<HomepageLayoutFile>(File.ReadAllText(filePath), Options);
            if (model is null) return new HomepageLayoutResult(false, "文件内容不是有效的布局");

            var settings = SettingsStore.Current;
            settings.HomepageWidgetOrder = Normalize(model.Order);
            settings.HomepageHiddenWidgets = Normalize(model.Hidden);

            var note = string.Empty;

            if (!string.IsNullOrWhiteSpace(model.ImageFile))
            {
                if (File.Exists(model.ImageFile))
                {
                    settings.HomepageImageFile = model.ImageFile;
                }
                else
                {
                    settings.HomepageImageFile = string.Empty;
                    note = "；图片挂件的图在这台机器上不存在，需要重新选一张";
                }
            }

            SettingsStore.Save();

            Log.Info($"已导入主页布局：{filePath}");
            return new HomepageLayoutResult(true, $"布局已导入（顺序 {settings.HomepageWidgetOrder.Count} 项）{note}");
        }
        catch (Exception ex)
        {
            Log.Warn($"导入主页布局失败：{ex.Message}");
            return new HomepageLayoutResult(false, $"导入失败：{ex.Message}");
        }
    }

    /// <summary>只保留认识的 id，去掉重复项；不认识的直接丢掉，避免导入外部文件时把界面搞乱。</summary>
    private static List<string> Normalize(IEnumerable<string>? ids)
    {
        var result = new List<string>();

        foreach (var id in ids ?? [])
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (Array.IndexOf(WidgetIds, id) < 0) continue;
            if (result.Contains(id)) continue;

            result.Add(id);
        }

        return result;
    }
}
