using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// Mod 配置档的存储：一份档一个 JSON，放在 Data\profiles 下。
/// 一档一文件的好处是"导出 / 导入"天然就是复制一个文件。
/// </summary>
public static class ModProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，方便用户手工编辑配置档
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly List<ModProfile> Items = [];

    public static IReadOnlyList<ModProfile> All => Items;

    /// <summary>配置档增删改时触发。</summary>
    public static event Action? Changed;

    /// <summary>配置档目录。</summary>
    public static string ProfilesDirectory => Path.Combine(Paths.Data, "profiles");

    public static void Load()
    {
        Paths.Init();

        Items.Clear();

        try
        {
            if (System.IO.Directory.Exists(ProfilesDirectory))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
                {
                    var profile = ReadFile(file);
                    if (profile is null) continue;

                    if (Items.Any(item => string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    Items.Add(profile);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取配置档失败", ex);
        }

        Items.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
        Log.Info($"已载入 {Items.Count} 个配置档");
        Changed?.Invoke();
    }

    /// <summary>某个 Mods 目录下的配置档，最近更新的在前。</summary>
    public static IReadOnlyList<ModProfile> ForDirectory(string? modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory)) return [];

        return
        [
            .. Items.Where(item =>
                string.Equals(Normalize(item.ModsDirectory), Normalize(modsDirectory), StringComparison.OrdinalIgnoreCase))
        ];
    }

    /// <summary>用当前 Mod 列表新建一份配置档。</summary>
    public static ModProfile Create(string name, string note, string modsDirectory, IEnumerable<ModEntry> mods)
    {
        var profile = new ModProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "新配置档" : name.Trim(),
            Note = (note ?? string.Empty).Trim(),
            ModsDirectory = modsDirectory ?? string.Empty,
            Mods = ModProfile.Snapshot(mods)
        };

        Items.Add(profile);
        Save(profile);

        Log.Info($"已新建配置档「{profile.Name}」（{profile.MetaText}）");
        return profile;
    }

    /// <summary>用当前 Mod 列表覆盖一份已有配置档。</summary>
    public static void Overwrite(ModProfile profile, IEnumerable<ModEntry> mods)
    {
        if (profile is null) return;

        profile.Mods = ModProfile.Snapshot(mods);
        profile.UpdatedAt = DateTime.Now;
        Save(profile);

        Log.Info($"配置档「{profile.Name}」已更新（{profile.MetaText}）");
    }

    public static void Rename(ModProfile profile, string name, string note)
    {
        if (profile is null) return;

        var trimmed = (name ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmed)) profile.Name = trimmed;

        profile.Note = (note ?? string.Empty).Trim();
        profile.UpdatedAt = DateTime.Now;

        Save(profile);
        Log.Info($"配置档已改名为「{profile.Name}」");
    }

    public static void Delete(ModProfile profile)
    {
        if (profile is null) return;

        Items.RemoveAll(item => string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase));

        try
        {
            var file = FileOf(profile.Id);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除配置档文件失败：{ex.Message}");
        }

        Log.Info($"已删除配置档「{profile.Name}」");
        Changed?.Invoke();
    }

    /// <summary>把一份档另存为文件。</summary>
    public static bool Export(ModProfile profile, string filePath, out string? error)
    {
        error = null;

        try
        {
            IO.AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(profile, Options));
            Log.Info($"已导出配置档「{profile.Name}」→ {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"导出失败：{ex.Message}";
            Log.Warn($"导出配置档失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>从文件导入一份档。Id 会重新生成，不会覆盖已有的同名档。</summary>
    public static ModProfile? Import(string filePath, string modsDirectory, out string? error)
    {
        error = null;

        var profile = ReadFile(filePath);
        if (profile is null)
        {
            error = "这个文件不是有效的配置档";
            return null;
        }

        profile.Id = Guid.NewGuid().ToString("N")[..12];
        profile.ModsDirectory = modsDirectory ?? profile.ModsDirectory;
        profile.UpdatedAt = DateTime.Now;

        if (Items.Any(item =>
                string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(item.ModsDirectory), Normalize(profile.ModsDirectory), StringComparison.OrdinalIgnoreCase)))
        {
            profile.Name += "（导入）";
        }

        Items.Add(profile);
        Save(profile);

        Log.Info($"已导入配置档「{profile.Name}」（{profile.MetaText}）");
        return profile;
    }

    /// <summary>
    /// 把配置档应用到当前 Mod 列表。只动状态不一致的 Mod，逐条收集错误不中断，
    /// 因为单个 Mod 改名冲突不该让整份档应用失败。
    /// </summary>
    public static ModProfileApplyResult Apply(ModProfile profile, IReadOnlyList<ModEntry> currentMods)
    {
        var changed = 0;
        var missing = new List<string>();
        var errors = new List<string>();

        var index = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in currentMods ?? [])
        {
            var key = ModTagStore.KeyOf(mod);
            if (!string.IsNullOrWhiteSpace(key)) index[key] = mod;
        }

        foreach (var toggle in profile?.Mods ?? [])
        {
            if (!index.TryGetValue(toggle.Key, out var mod))
            {
                missing.Add(string.IsNullOrWhiteSpace(toggle.Name) ? toggle.Key : toggle.Name);
                continue;
            }

            if (mod.IsEnabled == toggle.Enabled) continue;

            if (ModEnabler.TrySetEnabled(mod, toggle.Enabled, out var error)) changed++;
            else errors.Add($"{mod.DisplayName}：{error}");
        }

        Log.Info($"应用配置档「{profile?.Name}」：调整 {changed} 个，缺失 {missing.Count} 个，失败 {errors.Count} 个");
        ActivityLog.Write(LogSource.App, $"已应用配置档「{profile?.Name}」：调整 {changed} 个 Mod");

        return new ModProfileApplyResult(changed, missing, errors);
    }

    private static ModProfile? ReadFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            var info = new FileInfo(filePath);
            if (info.Length > 8 * 1024 * 1024)
            {
                Log.Warn($"配置档过大，已跳过：{filePath}");
                return null;
            }

            var profile = JsonSerializer.Deserialize<ModProfile>(File.ReadAllText(filePath), Options);
            if (profile is null) return null;

            if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N")[..12];
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "未命名配置档";
            profile.Mods ??= [];
            profile.Mods.RemoveAll(item => string.IsNullOrWhiteSpace(item.Key));

            return profile;
        }
        catch (Exception ex)
        {
            Log.Warn($"解析配置档失败 {filePath}：{ex.Message}");
            return null;
        }
    }

    private static void Save(ModProfile profile)
    {
        try
        {
            Paths.Init();
            System.IO.Directory.CreateDirectory(ProfilesDirectory);
            IO.AtomicFile.WriteAllText(FileOf(profile.Id), JsonSerializer.Serialize(profile, Options));
        }
        catch (Exception ex)
        {
            Log.Error($"保存配置档失败（{profile.Name}）", ex);
        }

        Changed?.Invoke();
    }

    public static string FileOf(string id) => Path.Combine(ProfilesDirectory, id + ".json");

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return path.Trim(); }
    }
}
