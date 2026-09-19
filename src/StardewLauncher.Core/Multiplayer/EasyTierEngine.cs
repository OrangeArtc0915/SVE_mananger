using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Multiplayer;

/// <summary>EasyTier 房间里的一个在线成员。</summary>
public sealed record EasyTierPeer(string Ipv4, string Hostname, string Latency, string Tunnel);

/// <summary>本机在 EasyTier 网络里的节点信息，字段缺失时为空字符串。</summary>
public sealed record EasyTierNodeInfo(string Ip, string PeerId, string PublicIpv4, string NatType);

/// <summary>
/// EasyTier 客户端进程管理。对应参考实现 engine_easytier.py：
/// 用 <c>easytier-core</c> 加入房间（网络名 + 网络密钥）组出一张虚拟局域网，
/// 再通过 <c>easytier-cli</c> 的 RPC 查询自己的虚拟 IP 与在线成员。
///
/// 关键参数说明：
/// <list type="bullet">
/// <item><c>--enable-udp-broadcast-relay</c>：把 UDP 广播转发到虚拟网内，游戏内的"局域网房间"才能互相看见。</item>
/// <item><c>--latency-first</c>：优先选低延迟链路。</item>
/// <item><c>--listeners wg:0</c>：不额外监听端口，只作为客户端主动连公共节点，避免占用本机端口。</item>
/// <item>RPC 端口默认动态挑空闲端口：固定端口会被残留进程占用导致新实例起不来。</item>
/// </list>
/// </summary>
public sealed partial class EasyTierEngine
{
    /// <summary>兜底 RPC 端口；正常走动态空闲端口。</summary>
    public const int DefaultRpcPort = 15888;

    /// <summary>
    /// easytier-core 的实例名。残留进程清理按它过滤，
    /// 保证只清理本程序遗留的进程，不会误杀用户自己跑的 EasyTier。
    /// </summary>
    public const string DefaultInstanceName = "stardew-launcher";

    /// <summary>虚拟网卡 MTU。游戏广播包不能因为 MTU 过小被分片。</summary>
    private const int Mtu = 1500;

    /// <summary>EasyTier 内部调试噪音，对用户无意义，不往界面日志里放。</summary>
    private static readonly string[] LogNoise =
    [
        "proto::rpc_impl::bidirect",
        "peer rpc transport read aborted",
        "peers::peer_ospf_route",
        "session id mismatch",
        "connector::manual",
        "reconn_tasks done",
        "peers::foreign_network_client",
        "wintun::log",
        "close tcp connection",
        "bind addr fail",
        // 以下是实测跑出来的噪音：公益公共节点自身超时、组播包找不到 peer、
        // P2P 打洞失败回落到中继、udp 连接参数 dump，都不代表用户这边有问题。
        "peer_center::instance",
        "forward packet error",
        "hole punching task",
        "easytier::tunnel::udp",
        "no peer id for ip"
    ];

    private readonly object _gate = new();
    private readonly List<string> _output = [];
    private readonly List<string> _nodes;
    private readonly Action<string> _log;

    private Process? _process;
    private int _nodeIndex;
    private int _rpcPort;

    public EasyTierEngine(
        string community,
        string key,
        IReadOnlyList<string> nodes,
        string toolDirectory,
        bool useFixedIp = false,
        string fixedIp = "",
        string instanceName = DefaultInstanceName,
        string devName = "et-sdv",
        Action<string>? log = null)
    {
        Community = community;
        Key = key;
        ToolDirectory = toolDirectory;
        UseFixedIp = useFixedIp;
        FixedIp = fixedIp;
        InstanceName = instanceName;
        DevName = devName;
        _log = log ?? (_ => { });
        _nodes = [.. nodes.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())];
        _nodeIndex = 0;
        _rpcPort = FreePort();
    }

    public string Community { get; }

    public string Key { get; }

    public string ToolDirectory { get; }

    public bool UseFixedIp { get; }

    public string FixedIp { get; }

    public string InstanceName { get; }

    public string DevName { get; }

    /// <summary>当前实际连接的公共节点。启动过程中可能切换到备用节点，对外以它为准。</summary>
    public string Node => CurrentNode;

    /// <summary>启动成功后的虚拟 IP（不含掩码）。</summary>
    public string Ip { get; private set; } = string.Empty;

    public bool IsRunning => _process is { HasExited: false };

    private string CoreExe => Path.Combine(ToolDirectory, "easytier-core.exe");

    private string CliExe => Path.Combine(ToolDirectory, "easytier-cli.exe");

    private string CurrentNode => _nodes.Count == 0 ? string.Empty : _nodes[_nodeIndex % _nodes.Count];

    // ————— 启动 / 停止 —————

    /// <summary>
    /// 启动并等待拿到虚拟 IP。失败会自动清理进程，不会留下占用网卡/端口的残骸。
    /// </summary>
    public async Task StartAsync(int timeoutSeconds = 45, bool allowRetry = true, CancellationToken token = default)
    {
        if (IsRunning) throw new InvalidOperationException("EasyTier 已经在运行");

        if (!File.Exists(CoreExe)) throw new FileNotFoundException($"缺少 easytier-core：{CoreExe}");
        if (string.IsNullOrWhiteSpace(Community)) throw new InvalidOperationException("房间名不能为空");
        if (_nodes.Count == 0) throw new InvalidOperationException("至少需要一个公共节点地址");

        _process = null;
        _output.Clear();

        var node = CurrentNode;
        var args = BuildArguments(node);

        LogLine($"启动 easytier-core：节点={node} 房间={Community} " +
                (UseFixedIp ? $"固定IP={FixedIp}" : "IP模式=自动(DHCP)"));
        LogLine("easytier-core " + string.Join(' ', args));

        var psi = new ProcessStartInfo
        {
            FileName = CoreExe,
            WorkingDirectory = ToolDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["RUST_LOG"] = "warn";

        _process = Process.Start(psi) ?? throw new InvalidOperationException("easytier-core 启动失败");
        _ = ReadOutputAsync(_process.StandardOutput);
        _ = ReadOutputAsync(_process.StandardError);

        try
        {
            await WaitReadyAsync(timeoutSeconds, token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            // 端口被残留实例占用：清掉残留并换端口重试一次
            if (allowRetry && PortConflictDetected())
            {
                var killed = await CleanupStaleAsync(InstanceName).ConfigureAwait(false);
                LogLine(killed > 0
                    ? $"检测到 {killed} 个残留 easytier 进程，已清理后重试"
                    : "检测到端口被占用，更换 RPC 端口后重试");

                Stop();
                _rpcPort = FreePort();
                await StartAsync(timeoutSeconds, allowRetry: false, token).ConfigureAwait(false);
                return;
            }

            // 节点不可达：换下一个节点（tcp 超时优先试同主机的 udp 变体）
            if (allowRetry && _nodes.Count > 1)
            {
                var previous = CurrentNode;
                var udpVariant = ToUdpVariant(previous);
                if (udpVariant is not null && !_nodes.Contains(udpVariant))
                    _nodes.Insert((_nodeIndex + 1) % _nodes.Count, udpVariant);

                _nodeIndex = (_nodeIndex + 1) % _nodes.Count;
                LogLine($"节点 {previous} 连接超时，自动切换 {CurrentNode} 重试…");

                Stop();
                _rpcPort = FreePort();
                await Task.Delay(500, token).ConfigureAwait(false);
                await StartAsync(timeoutSeconds, allowRetry: false, token).ConfigureAwait(false);
                return;
            }

            Stop();
            Log.Error($"EasyTier 启动失败：{ex.Message}");
            throw;
        }
    }

    private List<string> BuildArguments(string node)
    {
        var args = new List<string>
        {
            "--network-name", Community
        };

        if (!string.IsNullOrWhiteSpace(Key)) args.AddRange(["--network-secret", Key]);

        args.AddRange(["--peers", node]);

        if (UseFixedIp && !string.IsNullOrWhiteSpace(FixedIp))
        {
            var ip = FixedIp.Contains('/') ? FixedIp : $"{FixedIp}/24";
            args.AddRange(["--ipv4", ip]);
        }
        else
        {
            args.AddRange(["--dhcp", "true"]);
        }

        args.AddRange(["--rpc-portal", $"127.0.0.1:{_rpcPort}"]);
        args.AddRange(["--instance-name", InstanceName]);
        args.AddRange(["--dev-name", DevName]);
        args.AddRange(["--enable-udp-broadcast-relay", "true"]);
        args.AddRange(["--latency-first", "true"]);
        args.AddRange(["--mtu", Mtu.ToString()]);
        args.AddRange(["--multi-thread", "true"]);
        args.AddRange(["--multi-thread-count", "4"]);
        args.AddRange(["--disable-relay-quic", "true"]);
        args.AddRange(["--listeners", "wg:0"]);

        return args;
    }

    /// <summary>轮询 RPC，直到 easytier-core 分配到虚拟 IP。</summary>
    private async Task WaitReadyAsync(int timeoutSeconds, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var lastProgress = 0;

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (_process is null || _process.HasExited)
                throw new InvalidOperationException("easytier-core 进程提前退出");

            var info = await RunCliAsync(["node", "info"], 3, token).ConfigureAwait(false);
            var match = VirtualIpPattern().Match(info.StdOut);
            if (info.ExitCode == 0 && match.Success)
            {
                Ip = match.Groups[1].Value;
                await SetInterfaceMetricAsync(Ip).ConfigureAwait(false);
                LogLine($"easytier-core 已就绪，虚拟 IP = {Ip}");
                return;
            }

            var elapsed = timeoutSeconds - (int)(deadline - DateTime.UtcNow).TotalSeconds;
            if (elapsed - lastProgress >= 15)
            {
                lastProgress = elapsed;
                LogLine($"正在连接节点并等待分配虚拟 IP（{elapsed}s）…");
            }

            await Task.Delay(1000, token).ConfigureAwait(false);
        }

        throw new TimeoutException("easytier-core 启动超时，未获得虚拟 IP");
    }

    /// <summary>根据输出判断启动失败是否为端口被占用。</summary>
    private bool PortConflictDetected()
    {
        lock (_gate)
        {
            return _output.Any(line =>
            {
                var low = line.ToLowerInvariant();
                return low.Contains("failed to listen")
                       || low.Contains("10048")
                       || low.Contains("address already in use");
            });
        }
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        Ip = string.Empty;

        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"结束 easytier-core 失败：{ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    // ————— RPC 查询 —————

    /// <summary>本机节点信息：虚拟 IP、Peer ID、公网 IP、NAT 类型。</summary>
    public async Task<EasyTierNodeInfo> QueryNodeInfoAsync(CancellationToken token = default)
    {
        if (!IsRunning) return new EasyTierNodeInfo("", "", "", "");

        try
        {
            var result = await RunCliAsync(["node", "info"], 5, token).ConfigureAwait(false);
            var text = result.StdOut;

            return new EasyTierNodeInfo(
                Match(text, VirtualIpAnyPattern()),
                Match(text, PeerIdPattern()),
                Match(text, PublicIpv4Pattern()),
                Match(text, NatTypePattern()));
        }
        catch (Exception ex)
        {
            Log.Warn($"查询 EasyTier 节点信息失败：{ex.Message}");
            return new EasyTierNodeInfo("", "", "", "");
        }
    }

    /// <summary>查询在线成员（排除本机）。</summary>
    public async Task<List<EasyTierPeer>> QueryPeersAsync(CancellationToken token = default)
    {
        if (!IsRunning) return [];

        try
        {
            var result = await RunCliAsync(["peer", "list"], 6, token).ConfigureAwait(false);

            // peer list 里自己的 ipv4 带掩码（10.126.126.2/24），而 Ip 是不带掩码的，
            // 必须归一化后再比，否则本机会被当成"在线成员"列出来。
            var own = Ip.Split('/')[0];

            return [.. ParsePeerTable(result.StdOut).Where(p => p.Ipv4.Split('/')[0] != own)];
        }
        catch (Exception ex)
        {
            Log.Warn($"查询 EasyTier 在线成员失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 解析 <c>easytier-cli peer list</c> 的表格。
    /// 实测 2.6.x 的列顺序：1=ipv4 2=hostname 3=cost 4=lat(ms) 5=loss 6=rx 7=tx 8=tunnel 9=NAT 10=version。
    /// 表头行与没有虚拟 IP 的公共节点行会被过滤掉。
    /// </summary>
    public static List<EasyTierPeer> ParsePeerTable(string text)
    {
        var peers = new List<EasyTierPeer>();

        foreach (var line in text.Split('\n'))
        {
            var cells = line.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 11) continue;

            var ipv4 = cells[1];
            var hostname = cells[2];
            if (hostname is "" or "hostname") continue;
            if (!Ipv4Pattern().IsMatch(ipv4)) continue;

            var tunnel = cells[8];
            if (tunnel == "tunnel") continue;

            var latency = cells[4];
            if (latency is "" or "-")
            {
                latency = "";
            }
            else if (double.TryParse(latency, out var value))
            {
                latency = ((int)value).ToString();
            }

            peers.Add(new EasyTierPeer(ipv4, hostname, latency, tunnel));
        }

        return peers;
    }

    public IReadOnlyList<string> RecentOutput(int count = 40)
    {
        lock (_gate) return [.. _output.TakeLast(count)];
    }

    // ————— 子进程工具 —————

    private async Task<(int ExitCode, string StdOut)> RunCliAsync(
        IReadOnlyList<string> args, int timeoutSeconds, CancellationToken token)
    {
        if (!File.Exists(CliExe)) throw new FileNotFoundException($"缺少 easytier-cli：{CliExe}");

        var psi = new ProcessStartInfo
        {
            FileName = CliExe,
            WorkingDirectory = ToolDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add($"127.0.0.1:{_rpcPort}");
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("easytier-cli 启动失败");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            throw new TimeoutException($"easytier-cli {string.Join(' ', args)} 执行超时");
        }

        var text = await stdout.ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);

        return (process.ExitCode, text + errors);
    }

    private async Task ReadOutputAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = line.TrimEnd();
                if (line.Length == 0) continue;

                lock (_gate)
                {
                    _output.Add(line);
                    if (_output.Count > 300) _output.RemoveRange(0, _output.Count - 200);
                }

                var low = line.ToLowerInvariant();
                if (LogNoise.Any(low.Contains)) continue;
                if (!low.Contains("error") && !low.Contains("warn") && !low.Contains("connect")) continue;

                LogLine(line.Trim());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 easytier-core 输出失败：{ex.Message}");
        }
    }

    private void LogLine(string message) => _log($"[EasyTier] {message}");

    private static int FreePort()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch
        {
            return DefaultRpcPort;
        }
    }

    /// <summary><c>tcp://host:port</c> → <c>udp://host:port</c>。公共节点常只放通 UDP。</summary>
    private static string? ToUdpVariant(string node)
        => node.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
            ? string.Concat("udp://", node.AsSpan("tcp://".Length))
            : null;

    private static string Match(string text, Regex pattern)
    {
        var match = pattern.Match(text);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // ————— 系统层辅助 —————

    /// <summary>
    /// 降低持有虚拟 IP 那张网卡的接口跃点数，让游戏的广播优先走虚拟网卡而不是物理网卡。
    /// </summary>
    public static async Task SetInterfaceMetricAsync(string ip, int metric = 1)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;

        var script =
            $"Get-NetIPAddress -IPAddress {ip} -ErrorAction SilentlyContinue | " +
            $"ForEach-Object {{ Set-NetIPInterface -InterfaceIndex $_.InterfaceIndex " +
            $"-InterfaceMetric {metric} -ErrorAction SilentlyContinue }}";

        try
        {
            await RunPowerShellAsync(script, 15).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"设置虚拟网卡跃点数失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理本程序遗留的 easytier-core 进程（按 instance-name 过滤，且只清理超过 maxAgeSeconds 的）。
    /// 残留进程会占着 RPC 端口和虚拟网卡，导致新实例组网失败。
    /// </summary>
    public static async Task<int> CleanupStaleAsync(string instanceName, int maxAgeSeconds = 120)
    {
        var script =
            "Get-CimInstance Win32_Process -Filter \"Name='easytier-core.exe'\" | " +
            $"Where-Object {{ $_.CommandLine -like '*--instance-name {instanceName}*' -and " +
            $"$_.CreationDate -lt (Get-Date).AddSeconds(-{maxAgeSeconds}) }} | " +
            "ForEach-Object { $_.ProcessId }";

        try
        {
            var result = await RunPowerShellAsync(script, 10).ConfigureAwait(false);
            var pids = result.Split(['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Where(p => int.TryParse(p, out _))
                .ToList();

            foreach (var pid in pids)
            {
                try
                {
                    using var process = Process.GetProcessById(int.Parse(pid));
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理残留 easytier-core（PID {pid}）失败：{ex.Message}");
                }
            }

            return pids.Count;
        }
        catch (Exception ex)
        {
            Log.Warn($"清理残留 easytier-core 失败：{ex.Message}");
            return 0;
        }
    }

    private static async Task<string> RunPowerShellAsync(string script, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("powershell 启动失败");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            return string.Empty;
        }

        return await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
    }

    // ————— 正则 —————

    [GeneratedRegex(@"Virtual IP\s*\|\s*([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex VirtualIpPattern();

    [GeneratedRegex(@"Virtual IP\s*\|\s*([0-9.]+/[0-9]+|[0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex VirtualIpAnyPattern();

    [GeneratedRegex(@"Peer ID\s*\|\s*(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex PeerIdPattern();

    [GeneratedRegex(@"Public IPv4\s*\|\s*(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex PublicIpv4Pattern();

    [GeneratedRegex(@"UDP Stun Type\s*\|\s*(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex NatTypePattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+(?:/\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();
}
