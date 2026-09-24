using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// 安装规划的持久化：一个规划一个 JSON 文件，放在数据目录的 plans 下。
/// 首次运行不预置任何规划，由用户或自动推断产生。
/// </summary>
public static class InstallPlanStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，保证用户手工编辑规划时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly List<InstallPlan> Items = [];

    public static IReadOnlyList<InstallPlan> All => Items;

    /// <summary>规划列表发生变化时触发。</summary>
    public static event Action? Changed;

    public static string PlansDirectory => Path.Combine(Paths.Data, "plans");

    /// <summary>读取全部规划。单个文件坏掉只跳过它。</summary>
    public static void Load()
    {
        Items.Clear();

        try
        {
            var directory = PlansDirectory;

            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    var plan = ReadFrom(file, out var error);

                    if (plan is null)
                    {
                        Log.Warn($"安装规划解析失败，已跳过：{Path.GetFileName(file)}（{error}）");
                        continue;
                    }

                    Items.Add(plan);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取安装规划列表失败", ex);
        }

        Log.Info($"已载入 {Items.Count} 个安装规划");
        Changed?.Invoke();
    }

    /// <summary>从指定文件读取一个规划（也用于读取压缩包内自带的 install-plan.json）。</summary>
    public static InstallPlan? ReadFrom(string filePath, out string? error)
    {
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                error = "文件不存在";
                return null;
            }

            var plan = JsonSerializer.Deserialize<InstallPlan>(File.ReadAllText(filePath), Options);
            if (plan is null)
            {
                error = "内容为空";
                return null;
            }

            return plan;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static void Save(InstallPlan plan)
    {
        if (plan is null) return;

        Upsert(plan);
        WriteFile(plan);
        Changed?.Invoke();
    }

    public static void SaveMany(IEnumerable<InstallPlan> plans)
    {
        if (plans is null) return;

        var changed = false;

        foreach (var plan in plans)
        {
            if (plan is null) continue;

            Upsert(plan);
            WriteFile(plan);
            changed = true;
        }

        if (changed) Changed?.Invoke();
    }

    public static void Delete(InstallPlan plan)
    {
        if (plan is null) return;

        Items.RemoveAll(item => string.Equals(item.Id, plan.Id, StringComparison.OrdinalIgnoreCase));

        try
        {
            var file = FileOf(plan);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"安装规划文件删除失败：{plan.Id}（{ex.Message}）");
        }

        Changed?.Invoke();
    }

    private static void Upsert(InstallPlan plan)
    {
        var index = Items.FindIndex(item => string.Equals(item.Id, plan.Id, StringComparison.OrdinalIgnoreCase));

        if (index >= 0) Items[index] = plan;
        else Items.Add(plan);
    }

    private static void WriteFile(InstallPlan plan)
    {
        try
        {
            Directory.CreateDirectory(PlansDirectory);
            IO.AtomicFile.WriteAllText(FileOf(plan), JsonSerializer.Serialize(plan, Options));
        }
        catch (Exception ex)
        {
            Log.Error($"安装规划保存失败：{plan.Name}", ex);
        }
    }

    /// <summary>规划文件名用 Id；Id 不适合做文件名时退回一个随机名。</summary>
    private static string FileOf(InstallPlan plan)
    {
        var id = plan.Id ?? string.Empty;

        if (string.IsNullOrWhiteSpace(id))
            id = Guid.NewGuid().ToString("N")[..12];

        foreach (var ch in Path.GetInvalidFileNameChars()) id = id.Replace(ch, '_');

        return Path.Combine(PlansDirectory, id + ".json");
    }
}
