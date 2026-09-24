namespace StardewLauncher.Core.Mods;

/// <summary>依赖项表里的一行。</summary>
public sealed record DependencyReportRow(
    string ModName,
    string ModFolderName,
    string UniqueId,
    string IssueText,
    DependencyIssueKind Kind,
    string MissingUniqueId,
    string? RequiredVersion,
    string? FoundVersion);

/// <summary>把扫描结果里的依赖问题汇总成人看的表格，并支持从 Mod 库查找能补齐的前置。</summary>
public static class DependencyReport
{
    /// <summary>汇总所有依赖问题，按 Mod 名排序。</summary>
    public static IReadOnlyList<DependencyReportRow> Build(IReadOnlyList<ModEntry> mods)
    {
        var rows = new List<DependencyReportRow>();
        if (mods is null) return rows;

        foreach (var mod in mods)
        {
            foreach (var issue in mod.Issues)
            {
                rows.Add(new DependencyReportRow(
                    mod.DisplayName,
                    mod.RawFolderName,
                    mod.UniqueId,
                    Describe(issue),
                    issue.Kind,
                    // 循环依赖没有单一缺口，留空表示不该去库里补齐
                    issue.Kind == DependencyIssueKind.Cycle ? string.Empty : issue.UniqueId,
                    issue.RequiredVersion,
                    issue.FoundVersion));
            }
        }

        return [.. rows
            .OrderBy(row => row.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Kind)
            .ThenBy(row => row.MissingUniqueId, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>在 Mod 库里查找能补上某个 UniqueId 的库条目（只读扫描库，不安装）。</summary>
    public static IReadOnlyList<LibraryMod> FindProviders(IEnumerable<LibraryMod> libraryMods, string uniqueId)
    {
        var result = new List<LibraryMod>();

        if (libraryMods is null || string.IsNullOrWhiteSpace(uniqueId)) return result;

        foreach (var mod in libraryMods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.UniqueId)) continue;

            if (string.Equals(mod.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase)) result.Add(mod);
        }

        return result;
    }

    /// <summary>给人看的文案。</summary>
    private static string Describe(DependencyIssue issue) => issue.Kind switch
    {
        DependencyIssueKind.Missing => $"缺少前置：{issue.UniqueId}",
        DependencyIssueKind.VersionTooLow => $"需要 {issue.UniqueId} ≥ {issue.RequiredVersion}，当前 {issue.FoundVersion}",
        _ => "存在循环依赖"
    };
}
