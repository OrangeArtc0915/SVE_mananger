using System.Text;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Smapi;

namespace StardewLauncher.App.Services;

/// <summary>
/// Mod 扫描结果与维护动作（检查更新 / 一键更新 / 自动修复）的唯一一份状态。
///
/// <para>
/// 「Mod 管理」页与「设置 → Mod 与下载」都从这里拿数据、都调这里的动作：
/// 在设置页更新或修复完，Mod 管理页订阅 <see cref="Changed"/> 就能立刻跟着刷新，
/// 两边不会各扫一份、各修一份。
/// </para>
/// </summary>
internal static class ModMaintenance
{
    private static bool _checking;

    /// <summary>已知的更新结果，键是 <see cref="ModTagStore.KeyOf"/>。跨扫描保留。</summary>
    public static Dictionary<string, ModUpdateInfo> Updates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次扫描的结果；没扫过或原版实例下为 null。</summary>
    public static ModScanResult? LastScan { get; private set; }

    /// <summary>当前实例的 Mods 目录；原版实例或没有实例时为 null。</summary>
    public static string? ModsDirectory { get; private set; }

    /// <summary>扫描结果、更新信息或 Mod 文件变了，订阅方据此刷新界面。</summary>
    public static event Action? Changed;

    /// <summary>有新版本、可以拿去更新的 Mod。</summary>
    public static IReadOnlyList<ModEntry> UpdatableMods()
        => LastScan is null
            ? []
            : [.. LastScan.Mods.Where(mod => !string.IsNullOrWhiteSpace(mod.SuggestedVersion))];

    /// <summary>
    /// 重新扫描当前实例的 Mods 目录（含依赖解析），并顺手查一次更新。
    /// 刻意不触发 <see cref="Changed"/>：调用方拿到返回值自己渲染，避免渲染两次。
    /// </summary>
    public static async Task<ModScanResult?> RescanAsync()
    {
        ModsDirectory = InstanceStore.Current?.ModsDirectory;

        if (string.IsNullOrWhiteSpace(ModsDirectory))
        {
            ModsDirectory = null;
            LastScan = null;
            return null;
        }

        var directory = ModsDirectory;

        // 磁盘扫描与依赖解析都放到后台线程；异常交给调用方去提示，这里不吞
        var result = await Task.Run(() =>
        {
            var scan = ModScanner.Scan(directory);
            DependencyResolver.Evaluate(scan.Mods);
            return scan;
        });

        LastScan = result;
        ApplyKnownUpdates();

        // 按设置在后台补一次更新检查：走缓存，通常不发请求，也不打扰对方接口
        if (SettingsStore.Current.ModUpdateCheckEnabled) _ = CheckUpdatesAsync(force: false);

        return result;
    }

    /// <summary>把已知的更新结果贴回扫描出来的条目上（重新扫描后不会丢）。</summary>
    public static void ApplyKnownUpdates()
    {
        if (LastScan is null) return;

        foreach (var mod in LastScan.Mods)
        {
            mod.SuggestedVersion = null;
            mod.UpdateUrl = null;

            if (!Updates.TryGetValue(ModTagStore.KeyOf(mod), out var info)) continue;

            mod.SuggestedVersion = info.Version;
            mod.UpdateUrl = info.Url;
        }
    }

    /// <summary>
    /// 向 smapi.io 查一次更新。<paramref name="force"/> 为 false 时优先用缓存。
    /// 成功且拿到结果才会触发 <see cref="Changed"/>。
    /// </summary>
    public static async Task<ModUpdateCheckResult?> CheckUpdatesAsync(bool force)
    {
        if (_checking || LastScan is not { } scan) return null;

        _checking = true;

        try
        {
            var install = InstanceStore.Current?.Install;

            var result = await ModUpdateChecker.CheckAsync(scan.Mods, install?.SmapiVersion, install?.GameVersion, force);

            if (!result.Ok) return result;

            foreach (var pair in result.Updates) Updates[pair.Key] = pair.Value;

            ApplyKnownUpdates();
            RaiseChanged();

            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"Mod 更新检查失败：{ex.Message}");
            return null;
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>一键更新：把所有有新版本的 Mod 交出去，来源是 GitHub 的自动下载替换。</summary>
    public static async Task<ModUpdateInstallReport> QuickUpdateAsync(IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var report = await ModUpdateInstaller.RunAsync(UpdatableMods(), ModsDirectory ?? string.Empty, progress, token);

        // 更新过的 Mod 不该再挂着「有新版」，把它们的更新标记撤掉
        foreach (var outcome in report.Items.Where(item => item.Updated))
            Updates.Remove(ModTagStore.KeyOf(outcome.Mod));

        RaiseChanged();
        return report;
    }

    /// <summary>按分析出来的问题清单执行自动修复。</summary>
    public static async Task<ModRepairReport> RepairAsync(IReadOnlyList<ModRepairFinding> findings,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var directory = ModsDirectory;

        var report = await Task.Run(() => ModRepair.Fix(findings, directory, progress, token), token);

        RaiseChanged();
        return report;
    }

    /// <summary>扫出当前所有可以自动修复的问题。</summary>
    public static IReadOnlyList<ModRepairFinding> AnalyzeRepairs()
        => LastScan is null ? [] : ModRepair.Analyze(LastScan.Mods, ModsDirectory);

    /// <summary>一键更新的确认文案。两个页面弹的是同一份，说辞不会走样。</summary>
    public static string BuildUpdateConfirm(IReadOnlyList<ModEntry> targets)
    {
        var auto = targets.Count(ModUpdateInstaller.CanAutoUpdate);

        var builder = new StringBuilder();
        builder.AppendLine($"共 {targets.Count} 个 Mod 有新版本，其中 {auto} 个能从 GitHub 自动更新。");
        builder.AppendLine("更新前会先把旧目录备份为 .bak-<时间戳>，禁用状态也会保留。");
        builder.AppendLine();

        foreach (var mod in targets)
        {
            var mark = ModUpdateInstaller.CanAutoUpdate(mod) ? "自动更新" : "需手动下载";
            builder.AppendLine($"· {mod.DisplayName} → {mod.SuggestedVersion}（{mark}）");
        }

        builder.AppendLine();
        builder.Append($"确定开始吗？还有 {targets.Count - auto} 个需要手动下载。");

        return builder.ToString();
    }

    /// <summary>自动修复的确认文案。</summary>
    public static string BuildRepairConfirm(IReadOnlyList<ModRepairFinding> findings)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"将处理以下 {findings.Count} 个问题（都会先备份，可以反悔）：");
        builder.AppendLine();

        foreach (var finding in findings)
        {
            builder.AppendLine($"· [{finding.KindText}] {finding.Title}");
            builder.AppendLine($"    {finding.Detail}");
        }

        builder.AppendLine();
        builder.Append("确定开始修复吗？");

        return builder.ToString();
    }

    private static void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                // 一个订阅方炸了不该连累另一个
                Log.Warn($"Mod 维护状态刷新失败：{ex.Message}");
            }
        }
    }
}