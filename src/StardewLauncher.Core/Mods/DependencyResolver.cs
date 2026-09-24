using System.Globalization;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// 依赖解析：检查缺失/版本过低的依赖、检测循环依赖，并给出建议加载顺序。
/// 唯一标识匹配不区分大小写；同一标识有多份时优先取已启用的那一份。
/// </summary>
public static class DependencyResolver
{
    /// <summary>填充每个 ModEntry 的 Issues。</summary>
    public static void Evaluate(IReadOnlyList<ModEntry> mods)
    {
        var index = BuildIndex(mods);

        foreach (var mod in mods)
        {
            mod.Issues.Clear();

            foreach (var (uniqueId, minimumVersion) in RequiredDependencies(mod))
            {
                if (string.IsNullOrWhiteSpace(uniqueId)) continue;

                if (!index.TryGetValue(uniqueId, out var candidates))
                {
                    mod.Issues.Add(new DependencyIssue(uniqueId, minimumVersion, null, DependencyIssueKind.Missing));
                    continue;
                }

                var target = Pick(candidates);
                var found = target.Manifest?.Version;

                if (!string.IsNullOrWhiteSpace(minimumVersion)
                    && !string.IsNullOrWhiteSpace(found)
                    && CompareVersions(found, minimumVersion) < 0)
                {
                    mod.Issues.Add(new DependencyIssue(uniqueId, minimumVersion, found, DependencyIssueKind.VersionTooLow));
                }
            }
        }

        // 循环依赖统一标记
        foreach (var mod in FindCycle(mods))
        {
            mod.Issues.Add(new DependencyIssue(mod.UniqueId, null, null, DependencyIssueKind.Cycle));
        }
    }

    /// <summary>依赖在前、同层按显示名排序的加载顺序。循环依赖里的条目排在最后，但不会丢失。</summary>
    public static IReadOnlyList<ModEntry> SortByLoadOrder(IReadOnlyList<ModEntry> mods)
    {
        var index = BuildIndex(mods);
        var dependencies = new Dictionary<ModEntry, HashSet<ModEntry>>();
        var dependents = new Dictionary<ModEntry, List<ModEntry>>();

        foreach (var mod in mods)
        {
            dependencies[mod] = [];
            dependents[mod] = [];
        }

        foreach (var mod in mods)
        {
            foreach (var (uniqueId, _) in RequiredDependencies(mod))
            {
                if (string.IsNullOrWhiteSpace(uniqueId) || !index.TryGetValue(uniqueId, out var candidates)) continue;

                var target = Pick(candidates);
                if (ReferenceEquals(target, mod)) continue;

                if (dependencies[mod].Add(target)) dependents[target].Add(mod);
            }
        }

        var remaining = new HashSet<ModEntry>(mods);
        var degrees = mods.ToDictionary(mod => mod, mod => dependencies[mod].Count);
        var result = new List<ModEntry>(mods.Count);

        while (true)
        {
            var next = remaining
                .Where(mod => degrees[mod] == 0)
                .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (next is null) break;

            remaining.Remove(next);
            result.Add(next);

            foreach (var dependent in dependents[next])
            {
                if (remaining.Contains(dependent)) degrees[dependent]--;
            }
        }

        // 剩余的只可能是循环依赖，按名字追加，保证输出包含全部条目
        result.AddRange(remaining
            .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase));

        return result;
    }

    /// <summary>返回参与循环依赖的条目。</summary>
    public static IReadOnlyList<ModEntry> FindCycle(IReadOnlyList<ModEntry> mods)
    {
        var index = BuildIndex(mods);

        // 只有能拿到唯一标识的条目才可能参与依赖关系
        var nodes = mods.Where(mod => !string.IsNullOrWhiteSpace(mod.UniqueId)).ToList();
        var state = new Dictionary<ModEntry, int>();  // 0 未访问 / 1 在递归栈 / 2 已完成
        var stack = new List<ModEntry>();
        var cycle = new HashSet<ModEntry>();

        void Visit(ModEntry current)
        {
            state[current] = 1;
            stack.Add(current);

            foreach (var (uniqueId, _) in RequiredDependencies(current))
            {
                if (string.IsNullOrWhiteSpace(uniqueId) || !index.TryGetValue(uniqueId, out var candidates)) continue;

                var next = Pick(candidates);
                if (!state.TryGetValue(next, out var nextState) || nextState == 0)
                {
                    Visit(next);
                }
                else if (nextState == 1)
                {
                    // next 仍在递归栈上，说明找到了环，把环上的节点全部收集起来
                    var start = stack.IndexOf(next);
                    if (start >= 0)
                    {
                        for (var i = start; i < stack.Count; i++) cycle.Add(stack[i]);
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[current] = 2;
        }

        foreach (var node in nodes)
        {
            if (!state.TryGetValue(node, out var value) || value == 0) Visit(node);
        }

        return [.. cycle
            .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>语义化版本比较：按 "." 拆段，能转数字就按数字比，否则按字符串比；忽略前缀 v。</summary>
    public static int CompareVersions(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);

        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return -1;
        if (b.Length == 0) return 1;

        var partsA = a.Split('.');
        var partsB = b.Split('.');
        var count = Math.Max(partsA.Length, partsB.Length);

        for (var i = 0; i < count; i++)
        {
            var textA = i < partsA.Length ? partsA[i].Trim() : "";
            var textB = i < partsB.Length ? partsB[i].Trim() : "";

            var numberA = int.TryParse(textA, NumberStyles.None, CultureInfo.InvariantCulture, out var valueA);
            var numberB = int.TryParse(textB, NumberStyles.None, CultureInfo.InvariantCulture, out var valueB);

            var compare = numberA && numberB
                ? valueA.CompareTo(valueB)
                : string.Compare(textA, textB, StringComparison.OrdinalIgnoreCase);

            if (compare != 0) return compare < 0 ? -1 : 1;
        }

        return 0;
    }

    private static Dictionary<string, List<ModEntry>> BuildIndex(IReadOnlyList<ModEntry> mods)
    {
        var index = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            var uniqueId = mod.UniqueId;
            if (string.IsNullOrWhiteSpace(uniqueId)) continue;

            if (!index.TryGetValue(uniqueId, out var candidates))
            {
                candidates = [];
                index[uniqueId] = candidates;
            }

            candidates.Add(mod);
        }

        return index;
    }

    private static ModEntry Pick(List<ModEntry> candidates)
        => candidates.FirstOrDefault(candidate => candidate.IsEnabled) ?? candidates[0];

    /// <summary>取出一个 Mod 的全部必需依赖（含内容包指向的框架）。IsRequired 为 false 的会跳过。</summary>
    private static IEnumerable<(string UniqueId, string? MinimumVersion)> RequiredDependencies(ModEntry mod)
    {
        foreach (var dependency in mod.Manifest?.Dependencies ?? [])
        {
            if (dependency is null || !dependency.IsRequired) continue;
            yield return (dependency.UniqueId, dependency.MinimumVersion);
        }

        var contentPackFor = mod.Manifest?.ContentPackFor;
        if (contentPackFor is not null) yield return (contentPackFor.UniqueId, contentPackFor.MinimumVersion);
    }

    private static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";

        var text = version.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text[1..].Trim();
        return text;
    }
}
