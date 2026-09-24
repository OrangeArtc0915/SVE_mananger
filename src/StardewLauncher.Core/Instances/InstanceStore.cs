using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Instances;

/// <summary>
/// 实例的读写与增删改查。每个实例一个 JSON 文件，放在数据目录的 instances 下。
/// </summary>
public static class InstanceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，保证用户手工编辑配置文件时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly List<Instance> Items = [];

    public static IReadOnlyList<Instance> All => Items;

    public static Instance? Current { get; private set; }

    /// <summary>列表或当前实例发生变化时触发。</summary>
    public static event Action? Changed;

    public static void Load()
    {
        Items.Clear();
        Current = null;

        try
        {
            if (Directory.Exists(Paths.Instances))
            {
                foreach (var file in Directory.EnumerateFiles(Paths.Instances, "*.json"))
                {
                    try
                    {
                        var instance = JsonSerializer.Deserialize<Instance>(File.ReadAllText(file), Options);
                        if (instance is not null && !string.IsNullOrWhiteSpace(instance.Id)) Items.Add(instance);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"实例文件解析失败，已跳过：{Path.GetFileName(file)}（{ex.Message}）");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取实例列表失败", ex);
        }

        RefreshInstalls();

        var lastId = SettingsStore.Current.LastSelectedInstanceId;
        Current = Items.FirstOrDefault(item => item.Id == lastId) ?? Items.FirstOrDefault();

        Log.Info($"已载入 {Items.Count} 个实例，当前实例：{Current?.Name ?? "无"}");
        Changed?.Invoke();
    }

    /// <summary>重新解析每个实例指向的游戏目录，标记不可用的实例。</summary>
    public static void RefreshInstalls()
    {
        foreach (var instance in Items)
        {
            instance.Install = StardewInstall.TryCreate(instance.GameDir, out var install) ? install : null;
        }
    }

    /// <summary>
    /// 保存对已有实例的修改（改名 / 换游戏目录 / 换类型 / 改备注）。
    /// 会重新解析一次游戏目录，并通知界面刷新。
    /// </summary>
    public static void Update(Instance instance)
    {
        instance.Install = StardewInstall.TryCreate(instance.GameDir, out var install) ? install : null;

        Save(instance);
        Changed?.Invoke();

        Log.Info($"已更新实例「{instance.Name}」（{instance.KindText}），游戏目录：{instance.GameDir}");
    }

    public static Instance Create(string name, string gameDir, string note = "",
        InstanceKind kind = InstanceKind.Modded)
    {
        var instance = new Instance
        {
            Name = string.IsNullOrWhiteSpace(name) ? "新实例" : name.Trim(),
            GameDir = GameLocator.NormalizePath(gameDir),
            Note = note.Trim(),
            Kind = kind
        };

        instance.Install = StardewInstall.TryCreate(instance.GameDir, out var install) ? install : null;

        Items.Add(instance);
        Save(instance);

        Current ??= instance;
        if (ReferenceEquals(Current, instance)) SettingsStore.Current.LastSelectedInstanceId = instance.Id;

        SettingsStore.Save();
        Changed?.Invoke();
        Log.Info($"已创建实例「{instance.Name}」（{instance.KindText}），游戏目录：{instance.GameDir}");

        return instance;
    }

    public static void Save(Instance instance)
    {
        try
        {
            Directory.CreateDirectory(Paths.Instances);
            IO.AtomicFile.WriteAllText(Paths.InstanceFile(instance.Id), JsonSerializer.Serialize(instance, Options));
        }
        catch (Exception ex)
        {
            Log.Error($"实例保存失败：{instance.Name}", ex);
        }
    }

    public static void Delete(Instance instance)
    {
        Items.Remove(instance);

        try
        {
            var file = Paths.InstanceFile(instance.Id);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"实例文件删除失败：{instance.Name}（{ex.Message}）");
        }

        if (ReferenceEquals(Current, instance))
            SetCurrent(Items.FirstOrDefault());
        else
            Changed?.Invoke();

        Log.Info($"已删除实例「{instance.Name}」");
    }

    public static void SetCurrent(Instance? instance)
    {
        if (ReferenceEquals(Current, instance)) return;

        Current = instance;
        SettingsStore.Current.LastSelectedInstanceId = instance?.Id ?? string.Empty;
        SettingsStore.Save();

        Changed?.Invoke();
        Log.Info($"当前实例切换为：{instance?.Name ?? "无"}");
    }
}
