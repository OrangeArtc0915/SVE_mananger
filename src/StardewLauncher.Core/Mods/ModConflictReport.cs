using System.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>冲突的种类。</summary>
public enum ModConflictKind
{
    /// <summary>多个已启用的 Mod 用了同一个 UniqueID，SMAPI 只会加载其中一个。</summary>
    DuplicateUniqueId,

    /// <summary>manifest.json 读不出来，SMAPI 会直接跳过这个 Mod。</summary>
    BrokenMod,

    /// <summary>目录里没有 manifest.json，SMAPI 不会加载，用户却以为装好了。</summary>
    Unrecognized
}

/// <summary>冲突涉及的一项。Note 放这一项自己的状态，例如版本、解析失败原因。</summary>
public sealed record ModConflictTarget(string Name, string Path, string Note);

/// <summary>一条冲突结论。Targets 是涉及到的 Mod 目录或文件。</summary>
public sealed record ModConflict(ModConflictKind Kind, string Title, string Detail,
    IReadOnlyList<ModConflictTarget> Targets)
{
    public string KindText => Kind switch
    {
        ModConflictKind.DuplicateUniqueId => "重复 ID",
        ModConflictKind.BrokenMod => "加载失败",
        _ => "不会被加载"
    };
}

/// <summary>
/// Mod 之间的静态冲突检查：只看磁盘上已经扫出来的结果，不解析内容包的 content.json，
/// 所以覆盖的是三类能确定的毛病 —— 重复 ID、压根加载不了的 Mod、Mods 目录里没被识别的东西。
///
/// <para>
/// 依赖缺失、版本过低、循环依赖由 <see cref="DependencyResolver"/> 负责，这里不重复报。
/// </para>
/// </summary>
public static class ModConflictDetector
{
    /// <summary>单条冲突最多列多少项，避免一次列出几百条把界面撑满。</summary>
    private const int MaxTargets = 50;

    public static IReadOnlyList<ModConflict> Analyze(IReadOnlyList<ModEntry> mods, string? modsDirectory = null)
    {
        var conflicts = new List<ModConflict>();

        AddDuplicateIds(mods, conflicts);
        AddBroken(mods, conflicts);
        AddUnrecognized(modsDirectory, mods, conflicts);

        return conflicts;
    }

    /// <summary>
    /// 同一个 UniqueID 出现在多个**已启用**的目录里才算冲突。
    /// 只启用一份、另一份禁用着是有意的（留个旧版本），SMAPI 也只加载启用的那些，报出来纯属噪音。
    /// </summary>
    private static void AddDuplicateIds(IReadOnlyList<ModEntry> mods, List<ModConflict> conflicts)
    {
        var groups = mods
            .Where(mod => mod.IsEnabled && !string.IsNullOrWhiteSpace(mod.UniqueId))
            .GroupBy(mod => mod.UniqueId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var targets = group
                .Select(mod => new ModConflictTarget(mod.DisplayName, mod.FolderPath,
                    $"{mod.DisplayVersion} · {mod.TypeText}"))
                .ToList();

            conflicts.Add(new ModConflict(
                ModConflictKind.DuplicateUniqueId,
                $"{targets.Count} 个 Mod 共用同一个 ID：{group.Key}",
                "SMAPI 遇到重复 ID 只会加载其中一个，另一个等于白装。留下你要的那份，把多余的禁用或删掉。",
                targets));
        }
    }

    /// <summary>解析失败或目录读不了的 Mod：SMAPI 会跳过，但用户通常以为装好了。</summary>
    private static void AddBroken(IReadOnlyList<ModEntry> mods, List<ModConflict> conflicts)
    {
        var broken = mods.Where(mod => mod.State == ModState.Invalid).ToList();
        if (broken.Count == 0) return;

        var targets = broken
            .Select(mod => new ModConflictTarget(mod.RawFolderName, mod.FolderPath,
                mod.ParseError ?? "原因未知"))
            .ToList();

        conflicts.Add(new ModConflict(
            ModConflictKind.BrokenMod,
            $"{targets.Count} 个 Mod 无法加载",
            WithLimit("这些目录里的 manifest.json 读不出来，SMAPI 会直接跳过，进游戏看不到它们的效果。",
                targets.Count),
            Limit(targets)));
    }

    /// <summary>
    /// Mods 下既没有 manifest.json、又不包含任何已识别 Mod 的目录，以及直接丢在 Mods 根下的文件。
    /// 这一类最容易被忽略：文件确实放进去了，但目录层级不对（多套了一层、或者压缩包没解压），
    /// SMAPI 一格都不会加载。
    /// </summary>
    private static void AddUnrecognized(string? modsDirectory, IReadOnlyList<ModEntry> mods,
        List<ModConflict> conflicts)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory)) return;

        string[] directories;
        string[] files;
        try
        {
            directories = Directory.GetDirectories(modsDirectory);
            files = Directory.GetFiles(modsDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"列 Mods 目录失败：{modsDirectory}（{ex.Message}）");
            return;
        }

        var separator = Path.DirectorySeparatorChar;

        // 已经识别出来的 Mod 根目录：命中的目录自身或其祖先都算已识别
        var roots = mods
            .Select(mod => Trim(mod.FolderPath))
            .ToList();

        var strayDirectories = directories
            .Select(Trim)
            .Where(directory => !roots.Any(root =>
                root.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(directory + separator, StringComparison.OrdinalIgnoreCase)))
            .Select(directory => new ModConflictTarget(Path.GetFileName(directory), directory,
                "整棵目录里没有 manifest.json"))
            .ToList();

        if (strayDirectories.Count > 0)
        {
            conflicts.Add(new ModConflict(
                ModConflictKind.Unrecognized,
                $"{strayDirectories.Count} 个目录不会被加载",
                WithLimit("这些目录里找不到 manifest.json。常见原因是压缩包多套了一层目录，或者装的是要手动覆盖游戏文件的 XNB 包。",
                    strayDirectories.Count),
                Limit(strayDirectories)));
        }

        var strayFiles = files
            .Select(file => new ModConflictTarget(Path.GetFileName(file), file,
                "Mods 目录下直接放文件不会被加载"))
            .ToList();

        if (strayFiles.Count > 0)
        {
            conflicts.Add(new ModConflict(
                ModConflictKind.Unrecognized,
                $"Mods 目录下放着 {strayFiles.Count} 个不会被加载的文件",
                WithLimit("Mod 要以「一个文件夹里放 manifest.json」的形式存在。压缩包要先解压，图片之类的杂项可以删掉。",
                    strayFiles.Count),
                Limit(strayFiles)));
        }
    }

    /// <summary>超过上限时只保留前若干项，具体条数由 <see cref="WithLimit"/> 写进说明里。</summary>
    private static IReadOnlyList<ModConflictTarget> Limit(List<ModConflictTarget> targets)
        => targets.Count <= MaxTargets ? targets : targets.Take(MaxTargets).ToList();

    private static string WithLimit(string detail, int total)
        => total <= MaxTargets ? detail : $"{detail}（只列出前 {MaxTargets} 个，实际共 {total} 个）";

    private static string Trim(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
