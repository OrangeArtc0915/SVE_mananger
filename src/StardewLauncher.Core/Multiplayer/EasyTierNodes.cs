namespace StardewLauncher.Core.Multiplayer;

/// <summary>一个 EasyTier 公共节点：地址 + 给用户看的中文说明。</summary>
public sealed record EasyTierNode(string Address, string Description);

/// <summary>
/// EasyTier 公共节点清单。取自 HMOL 联机模块的内置列表（同一批公益公共节点），
/// 用户自定义的节点另外存在设置里，不混进这里。
///
/// 注意：<c>tcp://39.108.52.138</c> 这类节点的 TCP 端口被防火墙挡掉、只有 UDP 通，
/// 所以不能靠 TCP 连通性判断节点好坏——引擎侧统一用「连不上就自动换下一个节点 +
/// tcp 超时后改试同主机 udp」来容错。
/// </summary>
public static class EasyTierNodes
{
    /// <summary>默认节点：实测连通、延迟约 70ms。</summary>
    public const string DefaultAddress = "udp://39.108.52.138:11010";

    public static IReadOnlyList<EasyTierNode> BuiltIn { get; } =
    [
        new("udp://39.108.52.138:11010", "阿里云广州(UDP)"),
        new("tcp://38.147.105.178:11010", "国内节点"),
        new("tcp://39.108.52.138:11010", "阿里云广州(TCP)"),
        new("udp://38.147.105.178:11010", "国内节点(UDP)"),
        new("tcp://103.224.243.207:11010", "腾讯云节点"),
        new("tcp://47.108.0.143:11010", "阿里云节点"),
        new("tcp://119.23.247.86:11010", "阿里云节点"),
        new("tcp://43.136.62.122:11010", "腾讯云节点")
    ];

    /// <summary>下拉框显示文本：内置节点是「说明 (地址)」，自定义节点原样显示。</summary>
    public static string Label(string address)
    {
        var node = Find(address);
        return node is null ? address : $"{node.Description} ({node.Address})";
    }

    /// <summary>显示文本还原成地址。传入的已经是地址时原样返回。</summary>
    public static string Resolve(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;

        var text = label.Trim();
        foreach (var node in BuiltIn)
            if ($"{node.Description} ({node.Address})" == text) return node.Address;

        return text;
    }

    private static EasyTierNode? Find(string address)
    {
        foreach (var node in BuiltIn)
            if (string.Equals(node.Address, address, StringComparison.OrdinalIgnoreCase)) return node;

        return null;
    }
}
