using System.IO;
using System.Runtime.InteropServices;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Saves;
using StardewLauncher.Core.Smapi;
using StardewLauncher.Core.Updater;

namespace StardewLauncher.Core.Diagnostics;

/// <summary>
/// 环境体检：把「出问题时需要挨个问一圈」的信息一次性收集起来，
/// 按固定顺序给出结论 —— 启动器自身、游戏目录、SMAPI、Mods、依赖、版本要求、冲突、磁盘、日志。
///
/// <para>
/// 全部只读，不改任何文件（唯一的写入是数据目录里的一句探测文本，写完立刻删）。
/// 结论既能直接显示，也能整段复制或打进反馈包。
/// </para>
/// </summary>
public static class HealthInspector
{
    /// <summary>低于这个剩余空间就提醒用户。</summary>
    private const long LowDiskBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>同一类问题最多列多少条，避免刷屏。</summary>
    private const int MaxListed = 10;

    public static HealthReport Inspect(IProgress<double>? progress = null, CancellationToken token = default)
    {
        var items = new List<HealthItem>();

        AddLauncher(items);
        progress?.Report(0.1);

        var instance = AddInstance(items);
        progress?.Report(0.2);

        var scan = AddMods(items, instance, progress, token);
        progress?.Report(0.6);

        if (scan is not null)
        {
            token.ThrowIfCancellationRequested();

            if (instance?.Install is { } install) AddVersionRequirements(items, scan, install);

            AddConflicts(items, scan);
        }

        progress?.Report(0.75);

        AddStorage(items, instance?.Install);
        AddLogs(items);
        AddDataUsage(items);
        AddPendingUpdate(items);

        progress?.Report(1);
        return new HealthReport { Items = items };
    }

    // ————— 启动器自身 —————

    private static void AddLauncher(List<HealthItem> items)
    {
        items.Add(HealthItem.Info("启动器版本",
            $"{AppInfo.Name} {AppInfo.VersionDisplay}",
            [
                $"运行时：{RuntimeInformation.FrameworkDescription}",
                $"系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.ProcessArchitecture}）"
            ]));

        // 设置、实例、备份都在数据目录里。程序放在 Program Files 之类受保护的位置时，写入会失败，
        // 而失败原因往往要到用户改设置时才浮出来，这里提前问一句
        var probe = Path.Combine(Paths.Data, $"write-test-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(probe, "ok");
        }
        catch (Exception ex)
        {
            items.Add(HealthItem.Fail("数据目录写不进去",
                $"位置：{Paths.Data}",
                [
                    $"原因：{ex.Message}",
                    "设置、实例与备份都存这里。把整个程序文件夹换到不受保护的位置（例如 D 盘）再运行。"
                ]));

            return;
        }

        try { File.Delete(probe); }
        catch (Exception ex) { Log.Warn($"删除数据目录探测文件失败：{ex.Message}"); }

        items.Add(HealthItem.Ok("数据目录", $"可以正常读写：{Paths.Data}"));
    }

    // ————— 游戏目录与 SMAPI —————

    private static Instance? AddInstance(List<HealthItem> items)
    {
        if (InstanceStore.Current is not { } instance)
        {
            items.Add(HealthItem.Warn("还没有实例",
                "没有选中任何实例，下面的 Mod 相关检查会跳过。先到「实例」页新建或选一个。"));

            return null;
        }

        if (instance.Install is not { } install)
        {
            items.Add(HealthItem.Fail("游戏目录不可用",
                $"实例「{instance.Name}」指向的目录里找不到游戏主程序。",
                [
                    $"记录的位置：{instance.GameDir}",
                    "游戏可能被移动、改名，或者盘符变了。到「实例」页把目录改对。"
                ]));

            return instance;
        }

        items.Add(HealthItem.Ok("游戏目录",
            $"实例「{instance.Name}」（{instance.KindText}）",
            [
                $"位置：{install.Directory}",
                $"游戏版本：{install.GameVersion ?? "读不到"}"
            ]));

        if (install.HasSmapi)
        {
            items.Add(HealthItem.Ok("SMAPI", $"已安装 {install.SmapiVersion ?? "（版本读不到）"}"));
        }
        else
        {
            items.Add(HealthItem.Warn("SMAPI 未安装",
                "这个游戏目录里没有 StardewModdingAPI.exe，Mod 一个都不会被加载。",
                ["到「资源中心」可以一键安装。"]));
        }

        if (instance.IsVanilla)
        {
            items.Add(HealthItem.Info("实例类型是「原版」",
                "启动时直接跑游戏主程序，Mods 目录里的 Mod 不会加载。想用 Mod 就换成 Mod 端实例。"));
        }

        return instance;
    }

    // ————— Mods —————

    private static ModScanResult? AddMods(List<HealthItem> items, Instance? instance,
        IProgress<double>? progress, CancellationToken token)
    {
        if (instance is null) return null;

        var directory = instance.ModsDirectory;
        var result = ModScanner.Scan(directory, progress, token);

        if (!result.ModsDirectoryExists)
        {
            items.Add(HealthItem.Info("Mods 目录还不存在",
                $"位置：{directory}",
                ["装第一个 Mod 时会自动建出来，暂时不用管。"]));

            return null;
        }

        DependencyResolver.Evaluate(result.Mods);
        items.Add(HealthItem.Ok("Mods 目录", result.SummaryText, [$"位置：{result.ModsDirectory}"]));

        AddDependencies(items, result);
        return result;
    }

    private static void AddDependencies(List<HealthItem> items, ModScanResult scan)
    {
        var missing = new List<string>();
        var tooLow = new List<string>();

        foreach (var mod in scan.Mods)
        {
            foreach (var issue in mod.Issues)
            {
                switch (issue.Kind)
                {
                    case DependencyIssueKind.Missing:
                        missing.Add($"「{mod.DisplayName}」缺少前置 {issue.UniqueId}");
                        break;

                    case DependencyIssueKind.VersionTooLow:
                        tooLow.Add($"「{mod.DisplayName}」需要 {issue.UniqueId} ≥ {issue.RequiredVersion}，" +
                                   $"现在装的是 {issue.FoundVersion}");
                        break;
                }
            }
        }

        var cycle = DependencyResolver.FindCycle(scan.Mods);

        if (missing.Count == 0 && tooLow.Count == 0 && cycle.Count == 0)
        {
            items.Add(HealthItem.Ok("依赖关系", "没有缺失的前置，也没有版本过低或循环依赖。"));
            return;
        }

        var lines = new List<string>();
        lines.AddRange(Take(missing));
        lines.AddRange(Take(tooLow));

        if (cycle.Count > 0)
            lines.Add($"循环依赖：{string.Join("、", cycle.Select(mod => mod.DisplayName).Take(MaxListed))}");

        var total = missing.Count + tooLow.Count + cycle.Count;

        items.Add(HealthItem.Warn($"依赖有 {total} 处问题",
            "缺失前置与版本过低会让 Mod 加载失败。到「MOD」页顶部那条提示里可以一键补齐。",
            lines));
    }

    // ————— Mod 与环境的版本要求 —————

    /// <summary>
    /// Mod 的 manifest 里能声明「至少要什么版本的 SMAPI / 游戏」，装低了 SMAPI 会直接拒绝加载，
    /// 而报错信息只在游戏控制台里出现，用户很难对上号，所以这里主动比一遍。
    /// </summary>
    private static void AddVersionRequirements(List<HealthItem> items, ModScanResult scan, StardewInstall install)
    {
        if (install.HasSmapi && !string.IsNullOrWhiteSpace(install.SmapiVersion))
        {
            var version = install.SmapiVersion;

            ReportRequirement(items, scan.Mods, mod => mod.Manifest?.MinimumApiVersion, version,
                new RequirementText(
                    Title: "要求更高的 SMAPI 版本",
                    OkTitle: "SMAPI 版本要求",
                    Detail: $"当前 SMAPI 是 {version}，这些 Mod 会因为版本不够而拒绝加载。",
                    OkDetail: $"现有 SMAPI {version} 满足所有 Mod 的最低要求。",
                    Line: required => $"要求 SMAPI ≥ {required}"));
        }

        if (!string.IsNullOrWhiteSpace(install.GameVersion))
        {
            var version = install.GameVersion;

            ReportRequirement(items, scan.Mods, mod => mod.Manifest?.MinimumGameVersion, version,
                new RequirementText(
                    Title: "要求更高的游戏版本",
                    OkTitle: "游戏版本要求",
                    Detail: $"当前游戏是 {version}，这些 Mod 会因为版本不够而拒绝加载。",
                    OkDetail: $"现有游戏 {version} 满足所有 Mod 的最低要求。",
                    Line: required => $"要求游戏 ≥ {required}"));
        }
    }

    /// <summary>一类版本要求的文案。中英混排容易缺空格，所以整套文案由调用方给出。</summary>
    private sealed record RequirementText(string Title, string OkTitle, string Detail, string OkDetail,
        Func<string, string> Line);

    private static void ReportRequirement(List<HealthItem> items, IReadOnlyList<ModEntry> mods,
        Func<ModEntry, string?> requirement, string installed, RequirementText text)
    {
        var tooLow = new List<string>();

        foreach (var mod in mods)
        {
            if (!mod.IsEnabled) continue;

            var required = requirement(mod);
            if (string.IsNullOrWhiteSpace(required)) continue;
            if (SemVer.Compare(installed, required) >= 0) continue;

            tooLow.Add($"「{mod.DisplayName}」{text.Line(required)}");
        }

        if (tooLow.Count == 0)
        {
            items.Add(HealthItem.Ok(text.OkTitle, text.OkDetail));
            return;
        }

        items.Add(HealthItem.Warn($"{tooLow.Count} 个 Mod {text.Title}", text.Detail, Take(tooLow)));
    }

    // ————— Mod 之间的冲突 —————

    private static void AddConflicts(List<HealthItem> items, ModScanResult scan)
    {
        var conflicts = ModConflictDetector.Analyze(scan.Mods, scan.ModsDirectory);

        if (conflicts.Count == 0)
        {
            items.Add(HealthItem.Ok("Mod 之间没有发现冲突",
                "重复 ID、加载不了的 Mod、放错位置的目录都没查到。"));
            return;
        }

        items.Add(HealthItem.Warn($"发现 {conflicts.Count} 类冲突",
            "细节见「工具箱 → Mod 冲突检查」，那里能逐项看到是哪些目录。",
            conflicts.Select(conflict => conflict.Title).ToList()));
    }

    // ————— 磁盘、日志、数据目录 —————

    private static void AddStorage(List<HealthItem> items, StardewInstall? install)
    {
        var lines = new List<string>();
        var low = new List<string>();

        void Inspect(string label, string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrWhiteSpace(root))
                {
                    lines.Add($"{label}：读不到所在分区");
                    return;
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    lines.Add($"{label}：分区未就绪（{root}）");
                    return;
                }

                lines.Add($"{label}：剩余 {FormatSize(drive.AvailableFreeSpace)} / 共 {FormatSize(drive.TotalSize)}（{root}）");

                if (drive.AvailableFreeSpace < LowDiskBytes) low.Add(root);
            }
            catch (Exception ex)
            {
                lines.Add($"{label}：读不到磁盘信息（{ex.Message}）");
            }
        }

        if (install is not null) Inspect("游戏所在盘", install.Directory);
        Inspect("数据所在盘", Paths.Data);

        if (low.Count == 0)
        {
            items.Add(HealthItem.Ok("磁盘空间", "剩余空间够用。", lines));
            return;
        }

        items.Add(HealthItem.Warn("磁盘空间不多了",
            $"这些分区剩余不到 {FormatSize(LowDiskBytes)}：{string.Join("、", low.Distinct())}",
            lines));
    }

    private static void AddLogs(List<HealthItem> items)
    {
        var (files, bytes) = Measure(Paths.Log);

        if (files == 0)
        {
            items.Add(HealthItem.Info("运行日志", "还没有日志文件。", [$"位置：{Paths.Log}"]));
            return;
        }

        var lines = new List<string> { $"位置：{Paths.Log}" };

        try
        {
            var latest = Directory.EnumerateFiles(Paths.Log, "*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTime)
                .FirstOrDefault();

            if (latest is not null)
                lines.Add($"最近一份：{latest.Name}（{latest.LastWriteTime:MM-dd HH:mm}）");
        }
        catch (Exception ex)
        {
            Log.Warn($"列日志文件失败：{ex.Message}");
        }

        items.Add(HealthItem.Info("运行日志", $"{files} 份，共 {FormatSize(bytes)}", lines));
    }

    private static void AddDataUsage(List<HealthItem> items)
    {
        var (files, bytes) = Measure(Paths.Data);

        items.Add(HealthItem.Info("数据目录占用",
            $"{FormatSize(bytes)}（{files} 个文件）",
            [
                $"Cache：{FormatSize(Measure(Paths.Cache).Bytes)}",
                $"Log：{FormatSize(Measure(Paths.Log).Bytes)}",
                $"存档备份：{FormatSize(Measure(SaveBackupService.Root).Bytes)}",
                $"位置：{Paths.Data}"
            ]));
    }

    private static void AddPendingUpdate(List<HealthItem> items)
    {
        if (!LauncherUpdater.HasPendingFailureNote()) return;

        items.Add(HealthItem.Warn("上次自动更新没有完成",
            "启动器上次想更新自己，但替换文件失败，现在跑的还是旧版本。",
            ["到「设置」里可以手动检查更新；重启一次电脑再试通常就好了。"]));
    }

    // ————— 小工具 —————

    /// <summary>列表太长时只保留前若干条，剩下的折成一句话。</summary>
    private static IReadOnlyList<string> Take(List<string> lines)
    {
        if (lines.Count <= MaxListed) return lines;

        var kept = lines.Take(MaxListed).ToList();
        kept.Add($"……还有 {lines.Count - MaxListed} 条未列出");

        return kept;
    }

    private static (int Files, long Bytes) Measure(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return (0, 0);

        var files = 0;
        long bytes = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                files++;

                try { bytes += new FileInfo(file).Length; }
                catch { /* 被占用或刚被删的文件跳过，不影响总数 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"统计目录体积失败：{directory}（{ex.Message}）");
        }

        return (files, bytes);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}
