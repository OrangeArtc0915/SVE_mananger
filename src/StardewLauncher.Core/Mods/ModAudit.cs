using System.IO;
using StardewLauncher.Core.Instances;

namespace StardewLauncher.Core.Mods;

/// <summary>一项检查的结论。</summary>
public enum AuditStatus
{
    /// <summary>没问题。</summary>
    Pass = 0,

    /// <summary>不算错，但值得看一眼（例如被依赖的 Mod 被禁用了）。</summary>
    Warn = 1,

    /// <summary>确定有问题，会导致 Mod 加载不了或行为异常。</summary>
    Fail = 2,

    /// <summary>没条件检查（例如原版实例不用管 SMAPI）。</summary>
    Skipped = 3
}

/// <summary>一条具体发现，指向某个 Mod 或某个目录。</summary>
public sealed record AuditFinding(string Title, string Detail, string? Path);

/// <summary>一项检查的完整结果。</summary>
public sealed class AuditCheck
{
    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public AuditStatus Status { get; set; } = AuditStatus.Pass;

    /// <summary>一句话结论，列表上直接显示。</summary>
    public string Summary { get; set; } = "";

    public IReadOnlyList<AuditFinding> Findings { get; set; } = [];

    /// <summary>给界面用的状态文字。</summary>
    public string StatusText => Status switch
    {
        AuditStatus.Pass => "通过",
        AuditStatus.Warn => "注意",
        AuditStatus.Fail => "有问题",
        _ => "跳过"
    };
}

/// <summary>一次批量分析的完整结果。</summary>
public sealed class ModAuditReport
{
    public string ModsDirectory { get; init; } = "";

    public int ModCount { get; init; }

    public int EnabledCount { get; init; }

    public IReadOnlyList<AuditCheck> Checks { get; init; } = [];

    public int FailCount => Checks.Count(check => check.Status == AuditStatus.Fail);

    public int WarnCount => Checks.Count(check => check.Status == AuditStatus.Warn);

    public string Headline => FailCount > 0
        ? $"{FailCount} 项检查没通过"
        : WarnCount > 0
            ? $"全部通过，但有 {WarnCount} 项要留意"
            : "全部检查通过";
}

/// <summary>
/// 批量 Mod 信息分析：把「这类问题该怎么查」的规则集中在一处，一次跑完给出清单。
/// 与工具箱里的「Mod 冲突检查」分工不同 —— 那边只看磁盘上有哪些装载毛病，
/// 这边是逐项体检：SMAPI 装没装、manifest 能不能解析、前置齐不齐、版本够不够、
/// 有没有环、目录层级对不对、被依赖的 Mod 有没有被禁用。
/// </summary>
public static class ModAudit
{
    public static ModAuditReport Run(Instance? instance, ModScanResult scan)
    {
        var mods = scan.Mods;
        var checks = new List<AuditCheck>
        {
            CheckSmapi(instance),
            CheckManifest(scan, mods),
            CheckDuplicateIds(mods),
            CheckMissingDependencies(mods),
            CheckVersionTooLow(mods),
            CheckCycles(mods),
            CheckFolderLayout(scan, mods),
            CheckDisabledDependencies(mods)
        };

        return new ModAuditReport
        {
            ModsDirectory = scan.ModsDirectory,
            ModCount = mods.Count,
            EnabledCount = scan.EnabledCount,
            Checks = checks
        };
    }

    // ————— 各项检查 —————

    private static AuditCheck CheckSmapi(Instance? instance)
    {
        const string name = "SMAPI 已安装";

        if (instance is null)
        {
            return Skip(name, "没有正在使用的实例，不知道要检查哪个游戏目录。");
        }

        if (instance.IsVanilla)
        {
            return Skip(name, "当前是原版实例，不经过 SMAPI，这一项不适用。");
        }

        var install = instance.Install;

        if (install is null)
        {
            return Fail(name, "实例指向的游戏目录不可用。",
                [new AuditFinding(instance.Name, $"目录不存在或不是星露谷：{instance.GameDir}", instance.GameDir)]);
        }

        if (!install.HasSmapi)
        {
            return Fail(name, "游戏目录里没有装 SMAPI，Mod 端实例启动会失败。",
                [new AuditFinding("缺少 SMAPI", $"游戏目录：{install.Directory}", install.Directory)]);
        }

        return Pass(name, $"SMAPI {install.SmapiVersion ?? "版本未知"}，游戏 {install.GameVersion ?? "版本未知"}。");
    }

    private static AuditCheck CheckManifest(ModScanResult scan, IReadOnlyList<ModEntry> mods)
    {
        const string name = "manifest.json 能解析";

        if (!scan.ModsDirectoryExists)
            return Skip(name, "Mods 目录还不存在。");

        var broken = mods.Where(mod => mod.State == ModState.Invalid).ToList();

        if (broken.Count == 0) return Pass(name, $"{mods.Count} 个 Mod 的 manifest 都能正常读出。");

        return Fail(name, $"{broken.Count} 个目录读不出 manifest，SMAPI 会直接跳过它们。",
            broken.Select(mod => new AuditFinding(mod.FolderName, mod.ParseError ?? "解析失败", mod.FolderPath)).ToList());
    }

    private static AuditCheck CheckDuplicateIds(IReadOnlyList<ModEntry> mods)
    {
        const string name = "没有重复的 UniqueID";

        var duplicates = mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.UniqueId))
            .GroupBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        if (duplicates.Count == 0) return Pass(name, "每个 UniqueID 都只有一个 Mod 在用。");

        return Fail(name, $"{duplicates.Count} 个 UniqueID 被多个 Mod 共用，SMAPI 只会加载其中一个。",
            duplicates.Select(group => new AuditFinding(
                group.Key,
                $"{group.Count()} 个：{string.Join("、", group.Select(mod => mod.FolderName))}",
                group.First().FolderPath)).ToList());
    }

    private static AuditCheck CheckMissingDependencies(IReadOnlyList<ModEntry> mods)
    {
        const string name = "必需的前置都在";

        var rows = mods
            .SelectMany(mod => mod.Issues
                .Where(issue => issue.Kind == DependencyIssueKind.Missing)
                .Select(issue => new AuditFinding(
                    mod.DisplayName, $"缺少前置 {issue.UniqueId}", mod.FolderPath)))
            .ToList();

        if (rows.Count == 0) return Pass(name, "所有 Mod 的必需前置都能在 Mods 目录里找到。");

        return Fail(name, $"{rows.Count} 处缺少前置，这些 Mod 启动时会报红字。", rows);
    }

    private static AuditCheck CheckVersionTooLow(IReadOnlyList<ModEntry> mods)
    {
        const string name = "前置版本够新";

        var rows = mods
            .SelectMany(mod => mod.Issues
                .Where(issue => issue.Kind == DependencyIssueKind.VersionTooLow)
                .Select(issue => new AuditFinding(
                    mod.DisplayName,
                    $"需要 {issue.UniqueId} ≥ {issue.RequiredVersion}，实际是 {issue.FoundVersion}",
                    mod.FolderPath)))
            .ToList();

        if (rows.Count == 0) return Pass(name, "没有版本过低的前置。");

        return Fail(name, $"{rows.Count} 处前置版本过低。", rows);
    }

    private static AuditCheck CheckCycles(IReadOnlyList<ModEntry> mods)
    {
        const string name = "没有循环依赖";

        var cycle = DependencyResolver.FindCycle(mods);

        if (cycle.Count == 0) return Pass(name, "依赖关系里没有发现环。");

        return Fail(name, $"{cycle.Count} 个 Mod 卷进了循环依赖，加载顺序无法确定。",
            cycle.Select(mod => new AuditFinding(mod.DisplayName, $"UniqueID：{mod.UniqueId}", mod.FolderPath)).ToList());
    }

    private static AuditCheck CheckFolderLayout(ModScanResult scan, IReadOnlyList<ModEntry> mods)
    {
        const string name = "Mods 目录结构正常";

        if (!scan.ModsDirectoryExists) return Skip(name, "Mods 目录还不存在。");

        List<string> topLevel;
        try
        {
            topLevel = [.. Directory.GetDirectories(scan.ModsDirectory)];
        }
        catch (Exception ex)
        {
            return Skip(name, $"读不到 Mods 目录：{ex.Message}");
        }

        if (topLevel.Count == 0) return Pass(name, "Mods 目录下还没有东西。");

        // 一个顶层目录只要在它底下（含自身）扫出过 Mod，就算放对了
        var covered = mods
            .Select(mod => mod.FolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var orphans = topLevel
            .Where(dir => !covered.Any(path =>
                path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, dir, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (orphans.Count == 0) return Pass(name, $"Mods 下 {topLevel.Count} 个顶层目录都找得到 manifest.json。");

        return Fail(name, $"{orphans.Count} 个顶层目录里没有任何 manifest.json，SMAPI 看不见它们。",
            orphans.Select(dir => new AuditFinding(
                Path.GetFileName(dir),
                "多半是解压时多套了一层，或只放了解压出来的子目录",
                dir)).ToList());
    }

    private static AuditCheck CheckDisabledDependencies(IReadOnlyList<ModEntry> mods)
    {
        const string name = "被依赖的 Mod 没被禁用";

        var byId = mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.UniqueId))
            .GroupBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<AuditFinding>();

        foreach (var mod in mods.Where(entry => entry.IsEnabled))
        {
            foreach (var (uniqueId, _) in DependencyResolver.RequiredDependencies(mod))
            {
                if (string.IsNullOrWhiteSpace(uniqueId)) continue;
                if (!byId.TryGetValue(uniqueId, out var targets)) continue;

                // 只要还有一份启用着就不算问题
                if (targets.Any(target => target.IsEnabled)) continue;

                rows.Add(new AuditFinding(
                    mod.DisplayName,
                    $"依赖的 {uniqueId} 处于禁用状态（{targets[0].FolderName}）",
                    targets[0].FolderPath));
            }
        }

        if (rows.Count == 0) return Pass(name, "启用中的 Mod 所依赖的目标也都是启用状态。");

        return Warn(name, $"{rows.Count} 处依赖的目标被禁用了，对应 Mod 会因为缺前置报错。", rows);
    }

    // ————— 构造结果的三个小助手 —————

    private static AuditCheck Pass(string name, string summary) => new()
    {
        Name = name,
        Description = DescriptionOf(name),
        Status = AuditStatus.Pass,
        Summary = summary
    };

    private static AuditCheck Skip(string name, string summary) => new()
    {
        Name = name,
        Description = DescriptionOf(name),
        Status = AuditStatus.Skipped,
        Summary = summary
    };

    private static AuditCheck Fail(string name, string summary, IReadOnlyList<AuditFinding> findings) => new()
    {
        Name = name,
        Description = DescriptionOf(name),
        Status = AuditStatus.Fail,
        Summary = summary,
        Findings = findings
    };

    private static AuditCheck Warn(string name, string summary, IReadOnlyList<AuditFinding> findings) => new()
    {
        Name = name,
        Description = DescriptionOf(name),
        Status = AuditStatus.Warn,
        Summary = summary,
        Findings = findings
    };

    private static string DescriptionOf(string name) => name switch
    {
        "SMAPI 已安装" => "Mod 端实例靠 SMAPI 启动，没装它一个 Mod 都加载不了。",
        "manifest.json 能解析" => "每个 Mod 目录根下都得有一份能读的 manifest.json。",
        "没有重复的 UniqueID" => "同一个 UniqueID 出现多份时，SMAPI 只认其中一份，另一份静默失效。",
        "必需的前置都在" => "manifest 里 Required 为 true 的依赖，以及内容包指向的框架，都必须装。",
        "前置版本够新" => "前置装了但版本低于 MinimumVersion，同样会被判定为不满足。",
        "没有循环依赖" => "互相依赖成环时，加载顺序没有合法解，只能靠运气。",
        "Mods 目录结构正常" => "只有含 manifest.json 的那一级才是 Mod，多套一层 SMAPI 就找不到。",
        "被依赖的 Mod 没被禁用" => "禁用一个还被别人依赖的 Mod，等于把依赖它的那些一起弄坏。",
        _ => ""
    };
}
