using System.Text;
using StardewLauncher.Core.App;

namespace StardewLauncher.Core.Multiplayer;

/// <summary>分享文本解析结果。字段缺失时为空串 / false。</summary>
public sealed record ShareInfo(
    string Version,
    string Plan,
    string Node,
    string Room,
    string Key,
    bool IsOwner,
    bool ManualIp,
    string ManualIpAddress);

/// <summary>
/// 组网分享文本的生成与解析。格式沿用 HMOL 联机模块的多行文本，方便口头报、微信发、复制粘贴，
/// 只有开头标识与结尾的社群信息换成本项目自己的。
///
/// 文本长这样：
/// <code>
/// 星露谷启动器 组网分享
/// 版本: 1.0.0
/// 方案: EasyTier
/// 节点: udp://39.108.52.138:11010
/// 房间名: 星露谷
/// 密钥: (无)
/// 房主: 否
/// IP: 自动
/// QQ群：1034243331
/// </code>
/// </summary>
public static class ShareText
{
    public const string Header = "星露谷启动器 组网分享";

    /// <summary>分享文本里带的社群信息。</summary>
    public const string CommunityLine = "QQ群：1034243331";

    /// <summary>
    /// 房间名上限。房间名要口头报给队友、也要塞进分享文本，太长不好用，统一卡到 6 个字符：
    /// 界面输入、分享解析、设置载入三处都过 <see cref="NormalizeRoom"/>。
    /// </summary>
    public const int RoomNameLimit = 6;

    public const string PlanEasyTier = "EasyTier";

    /// <summary>按当前配置生成分享文本。「房主: 否」表示拿到文本的人是成员而不是房主。</summary>
    public static string Build(string node, string room, string key, bool manualIp, string manualIpAddress)
    {
        // 房间名在这里也归一化一次：保证分享文本里的房间名和实际组网用的一模一样，
        // 免得调用方忘了截断，导致双方房间名对不上
        var lines = new[]
        {
            Header,
            $"版本: {AppInfo.Version}",
            $"方案: {PlanEasyTier}",
            $"节点: {node}",
            $"房间名: {NormalizeRoom(room)}",
            $"密钥: {(string.IsNullOrWhiteSpace(key) ? "(无)" : key)}",
            "房主: 否",
            $"IP: {(manualIp ? $"手动:{manualIpAddress}" : "自动")}",
            CommunityLine
        };

        return string.Join('\n', lines);
    }

    /// <summary>
    /// 解析分享文本。认不出必需字段（节点 + 房间名）时返回 null。
    /// 兼容 HMOL 的旧标签（「小组」）与没有版本行的文本。
    /// </summary>
    public static ShareInfo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var data = Collect(text);

        var node = Get(data, "节点");
        if (node.Length == 0) return null;

        var room = Get(data, "房间名");
        if (room.Length == 0) room = Get(data, "小组");
        if (room.Length == 0) return null;

        var key = Get(data, "密钥");
        if (key is "(无)" or "(可选)") key = string.Empty;

        var manualIp = false;
        var manualIpAddress = string.Empty;
        var ipText = Get(data, "IP");
        if (ipText.StartsWith("手动", StringComparison.Ordinal))
        {
            manualIp = true;
            var separator = ipText.IndexOf(':');
            manualIpAddress = separator >= 0 ? ipText[(separator + 1)..].Trim() : string.Empty;
        }

        return new ShareInfo(
            Version: Get(data, "版本"),
            Plan: NormalizePlan(Get(data, "方案")),
            Node: node,
            Room: room,
            Key: key,
            IsOwner: Get(data, "房主") == "是",
            ManualIp: manualIp,
            ManualIpAddress: manualIpAddress);
    }

    /// <summary>把任意输入裁成合法房间名。</summary>
    public static string NormalizeRoom(string? room)
    {
        if (string.IsNullOrWhiteSpace(room)) return string.Empty;

        var text = room.Trim();
        return text.Length <= RoomNameLimit ? text : text[..RoomNameLimit];
    }

    /// <summary>方案名归一化：只认 EasyTier，其它方案如实返回，由调用方决定是否拒绝。</summary>
    private static string NormalizePlan(string plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return PlanEasyTier;
        if (plan.Equals(PlanEasyTier, StringComparison.OrdinalIgnoreCase)) return PlanEasyTier;

        return plan;
    }

    /// <summary>逐行取「标签: 值」。注意只认半角冒号，所以结尾的中文社群行不会被当成字段。</summary>
    private static Dictionary<string, string> Collect(string text)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            var label = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (label.Length == 0 || value.Length == 0) continue;

            data[label] = value;
        }

        return data;
    }

    private static string Get(Dictionary<string, string> data, string label)
        => data.TryGetValue(label, out var value) ? value : string.Empty;
}
