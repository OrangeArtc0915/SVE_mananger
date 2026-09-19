using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Multiplayer;

/// <summary>
/// 节点测速。沿用 HMOL 的做法：先试 TCP 建连测往返时间，失败再回退 ICMP PING。
///
/// 之所以不能只看 TCP：像 <c>tcp://39.108.52.138:11010</c> 这类节点的 TCP 端口被防火墙挡了、
/// 只有 UDP 通，TCP 测出来是"不可达"但实际能用。所以这里只把结果当作横向参考，
/// 不能用作"该节点能不能用"的判据。
/// </summary>
public static class NodeSpeedTest
{
    /// <summary>测一个节点的延迟（毫秒）。两种方式都失败返回 null。</summary>
    public static async Task<int?> MeasureAsync(string node, int timeoutMilliseconds = 3000, CancellationToken token = default)
    {
        var (host, port) = SplitHostPort(node);
        if (host.Length == 0) return null;

        var tcp = await TryTcpAsync(host, port, timeoutMilliseconds, token).ConfigureAwait(false);
        if (tcp is not null) return tcp;

        return await TryPingAsync(host, timeoutMilliseconds, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 节点地址拆成 host 与 port。兼容 <c>tcp://1.2.3.4:11010</c> / <c>udp://1.2.3.4:11010</c> / <c>1.2.3.4:11010</c>。
    /// 解析不出端口时 port 为 0，调用方回退成 PING。
    /// </summary>
    public static (string Host, int Port) SplitHostPort(string node)
    {
        var text = (node ?? string.Empty).Trim();
        if (text.Length == 0) return (string.Empty, 0);

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];

        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port) && port > 0)
            return (text[..colon].Trim(), port);

        return (text.Trim(), 0);
    }

    private static async Task<int?> TryTcpAsync(string host, int port, int timeoutMilliseconds, CancellationToken token)
    {
        if (port <= 0) return null;

        var watch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMilliseconds);

            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

            watch.Stop();
            return (int)watch.ElapsedMilliseconds;
        }
        catch (Exception)
        {
            // 建连失败很正常（多数节点只放通 UDP），交给 PING 再试
            return null;
        }
    }

    private static async Task<int?> TryPingAsync(string host, int timeoutMilliseconds, CancellationToken token)
    {
        try
        {
            using var ping = new Ping();

            var reply = await ping.SendPingAsync(host, timeoutMilliseconds).WaitAsync(token).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"PING 节点 {host} 失败：{ex.Message}");
            return null;
        }
    }
}
