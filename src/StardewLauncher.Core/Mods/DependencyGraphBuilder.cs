namespace StardewLauncher.Core.Mods;

/// <summary>依赖图里的一个节点。可能是真实 Mod，也可能是「没装的前置」占位。</summary>
public sealed class DependencyNode
{
    /// <summary>真实 Mod；占位节点为 null。</summary>
    public ModEntry? Mod { get; init; }

    /// <summary>占位节点表示的那个 UniqueID。</summary>
    public string MissingId { get; init; } = "";

    /// <summary>依赖链上越靠前的层号越小，加载顺序按层号从小到大。</summary>
    public int Layer { get; set; }

    /// <summary>同层内的次序，按显示名排。</summary>
    public int Order { get; set; }

    /// <summary>没人依赖、也不依赖别人的孤立节点。</summary>
    public bool Isolated { get; set; }

    /// <summary>被别的 Mod 依赖。</summary>
    public bool HasDependents { get; set; }

    public bool IsMissing => Mod is null;

    public string Label => Mod?.DisplayName ?? MissingId;

    public string SubLabel => Mod is null
        ? "未安装"
        : Mod.IsEnabled ? Mod.DisplayVersion : "已禁用";
}

/// <summary>一条依赖边：From 依赖 To。</summary>
public sealed record DependencyEdge(
    DependencyNode From,
    DependencyNode To,
    bool Satisfied,
    string? RequiredVersion,
    string? FoundVersion);

/// <summary>布好局的依赖图。</summary>
public sealed class DependencyGraph
{
    public IReadOnlyList<DependencyNode> Nodes { get; init; } = [];

    public IReadOnlyList<DependencyEdge> Edges { get; init; } = [];

    public int LayerCount => Nodes.Count == 0 ? 0 : Nodes.Max(node => node.Layer) + 1;

    /// <summary>孤立节点（不参与任何依赖关系）的数量。</summary>
    public int IsolatedCount => Nodes.Count(node => node.Isolated);
}

/// <summary>
/// 把扫描结果整理成一张分层依赖图。层的含义是加载顺序：
/// 前置在第 0 层，依赖它的往下排；环会被就地切断，不会让布局卡死。
/// </summary>
public static class DependencyGraphBuilder
{
    public static DependencyGraph Build(IReadOnlyList<ModEntry> mods)
    {
        var byId = mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.UniqueId))
            .GroupBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                // 同一 ID 有多份时，优先拿启用的那份来展示
                group => group.FirstOrDefault(mod => mod.IsEnabled) ?? group.First(),
                StringComparer.OrdinalIgnoreCase);

        var nodes = new Dictionary<string, DependencyNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<DependencyEdge>();

        DependencyNode NodeOf(ModEntry mod)
        {
            var key = string.IsNullOrWhiteSpace(mod.UniqueId) ? mod.FolderPath : mod.UniqueId;

            if (nodes.TryGetValue(key, out var existing)) return existing;

            var created = new DependencyNode { Mod = mod };
            nodes[key] = created;
            return created;
        }

        DependencyNode MissingNodeOf(string uniqueId)
        {
            if (nodes.TryGetValue(uniqueId, out var existing)) return existing;

            var created = new DependencyNode { MissingId = uniqueId };
            nodes[uniqueId] = created;
            return created;
        }

        foreach (var mod in mods)
        {
            var from = NodeOf(mod);

            foreach (var (uniqueId, minimumVersion) in DependencyResolver.RequiredDependencies(mod))
            {
                if (string.IsNullOrWhiteSpace(uniqueId)) continue;

                if (byId.TryGetValue(uniqueId, out var target))
                {
                    var to = NodeOf(target);
                    if (ReferenceEquals(to, from)) continue;   // 自依赖没有画的意义

                    edges.Add(new DependencyEdge(from, to, true, minimumVersion, target.Manifest?.Version));
                    continue;
                }

                var missing = MissingNodeOf(uniqueId);
                edges.Add(new DependencyEdge(from, missing, false, minimumVersion, null));
            }
        }

        AssignLayers(nodes.Values, edges);
        MarkIsolated(nodes.Values, edges);
        OrderWithinLayers(nodes.Values);

        return new DependencyGraph
        {
            Nodes = [.. nodes.Values],
            Edges = edges
        };
    }

    /// <summary>
    /// 层号 = 所有前置层号的最大值 + 1。递归遇到当前栈上的节点就直接返回 0 把环切断，
    /// 保证一定收敛；结果对环里的节点来说只是「排得靠前一点」，不影响其余部分的正确性。
    /// </summary>
    private static void AssignLayers(IEnumerable<DependencyNode> nodes, IReadOnlyList<DependencyEdge> edges)
    {
        var incoming = edges
            .GroupBy(edge => edge.From)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.To).ToList());

        var memo = new Dictionary<DependencyNode, int>();
        var visiting = new HashSet<DependencyNode>();

        int LayerOf(DependencyNode node)
        {
            if (memo.TryGetValue(node, out var cached)) return cached;
            if (!visiting.Add(node)) return 0;

            var layer = 0;

            if (incoming.TryGetValue(node, out var dependencies))
            {
                foreach (var dependency in dependencies)
                {
                    layer = Math.Max(layer, LayerOf(dependency) + 1);
                }
            }

            visiting.Remove(node);
            memo[node] = layer;
            return layer;
        }

        foreach (var node in nodes) node.Layer = LayerOf(node);
    }

    private static void MarkIsolated(IEnumerable<DependencyNode> nodes, IReadOnlyList<DependencyEdge> edges)
    {
        var connected = new HashSet<DependencyNode>();

        foreach (var edge in edges)
        {
            connected.Add(edge.From);
            connected.Add(edge.To);
            edge.To.HasDependents = true;
        }

        foreach (var node in nodes) node.Isolated = !connected.Contains(node);
    }

    private static void OrderWithinLayers(IEnumerable<DependencyNode> nodes)
    {
        foreach (var layer in nodes.GroupBy(node => node.Layer))
        {
            var index = 0;

            foreach (var node in layer
                         .OrderBy(node => node.Label, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(node => node.MissingId, StringComparer.OrdinalIgnoreCase))
            {
                node.Order = index++;
            }
        }
    }
}
