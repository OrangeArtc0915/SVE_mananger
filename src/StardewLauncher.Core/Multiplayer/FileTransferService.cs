using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;
using CoreLog = StardewLauncher.Core.Logging.Log;

namespace StardewLauncher.Core.Multiplayer;

/// <summary>发现到的一个可接收文件的队友。</summary>
public sealed record PeerEndpoint(string Address, int Port, string Nickname);

/// <summary>发送进度。Done 已发字节数，Total 总字节数。</summary>
public sealed record TransferProgress(long Done, long Total)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Done * 100 / Total, 0, 100);
}

/// <summary>一次发送的结果。</summary>
public sealed record TransferResult(bool Ok, string Message);

/// <summary>
/// 房间内点对点文件传输。跑在 EasyTier 的虚拟局域网上，不经任何中转服务器。
///
/// 与参考实现（HMOL）的差异：那边用固定 TCP 端口 + 无校验；这里改成
/// <list type="bullet">
/// <item>UDP 广播发现（固定 17800）拿到对方的 TCP 端口，TCP 端口本身在 17901-17999 里随机挑，
/// 免得固定端口被别的程序占了就整个功能不可用。广播能过是因为 EasyTier 开了 UDP 广播中继。</item>
/// <item>首帧就带上整文件 SHA-256：接收方靠它判断能否续传，收完也靠它确认数据没坏。</item>
/// <item>断点：接收方把半成品存成 <c>&lt;文件名&gt;.part</c> 并留一份边车 meta，同一文件重连时从断点继续。</item>
/// </list>
/// 收下的文件统一存到数据目录下的「收到的文件」，重名自动加 (1)。
/// </summary>
public sealed class FileTransferService : IDisposable
{
    /// <summary>发现通道端口。固定，双方必须先能在这个端口上互发广播。</summary>
    public const int DiscoveryPort = 17800;

    private const int TransferPortMin = 17901;
    private const int TransferPortMax = 17999;
    private const long MaxFileSize = 500L * 1024 * 1024;
    private const int ChunkSize = 1024 * 1024;
    private const int MaxHeaderBytes = 8192;
    private const string FirewallRulePrefix = "StardewLauncher 文件传输";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, PeerEndpoint> _discovered = new(StringComparer.Ordinal);
    private readonly string _pid = "f" + Guid.NewGuid().ToString("n")[..11];

    private UdpClient? _discovery;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _discoveryLoop;
    private Task? _acceptLoop;
    private DateTime _lastReplyAt = DateTime.MinValue;
    private string _nickname = string.Empty;

    /// <summary>收到的文件存放目录。</summary>
    public static string SaveDirectory => Path.Combine(Paths.Downloads, "收到的文件");

    /// <summary>本机监听的 TCP 端口。未启动时为 0。</summary>
    public int TransferPort { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>收到并校验通过的文件。参数：文件绝对路径、发送者昵称。</summary>
    public event Action<string, string>? Received;

    public event Action<string>? Log;

    // ————— 启停 —————

    /// <summary>开始监听发现通道与传输端口，并放行防火墙。</summary>
    public bool Start(string nickname)
    {
        if (IsRunning) return true;

        _nickname = string.IsNullOrWhiteSpace(nickname) ? "玩家" : nickname.Trim();
        _cts = new CancellationTokenSource();

        try
        {
            System.IO.Directory.CreateDirectory(SaveDirectory);

            _discovery = new UdpClient
            {
                EnableBroadcast = true,
                ExclusiveAddressUse = false
            };
            _discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            _listener = new TcpListener(IPAddress.Any, PickTransferPort());
            _listener.Start();
            TransferPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (Exception ex)
        {
            LogLine($"文件传输启动失败：{ex.Message}");
            Stop();
            return false;
        }

        AllowThroughFirewall();

        _discoveryLoop = Task.Run(() => DiscoveryLoopAsync(_cts.Token));
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        LogLine($"文件传输已就绪（发现端口 {DiscoveryPort}，传输端口 {TransferPort}）");
        return true;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        _cts?.Dispose();
        _cts = null;

        // 先关套接字让阻塞中的收发立刻退出，再等循环收尾
        try { _discovery?.Dispose(); } catch { /* 忽略 */ }
        _discovery = null;

        try { _listener?.Stop(); } catch { /* 忽略 */ }
        _listener = null;
        TransferPort = 0;

        foreach (var loop in new[] { _discoveryLoop, _acceptLoop })
        {
            if (loop is null) continue;
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* 退出中的异常无需上报 */ }
        }

        _discoveryLoop = null;
        _acceptLoop = null;
    }

    public void Dispose() => Stop();

    /// <summary>从 17901-17999 里挑一个能绑上的端口，避开固定端口被占用导致的整体不可用。</summary>
    private static int PickTransferPort()
    {
        var start = Random.Shared.Next(TransferPortMin, TransferPortMax + 1);

        for (var i = 0; i <= TransferPortMax - TransferPortMin; i++)
        {
            var port = TransferPortMin + (start - TransferPortMin + i) % (TransferPortMax - TransferPortMin + 1);

            try
            {
                var probe = new TcpListener(IPAddress.Any, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 被占用，继续试下一个
            }
        }

        throw new InvalidOperationException($"没有可用端口（{TransferPortMin}-{TransferPortMax}）");
    }

    // ————— 发现 —————

    /// <summary>广播探测并收集回复，拿到房间里可接收文件的队友。</summary>
    public async Task<List<PeerEndpoint>> DiscoverAsync(TimeSpan timeout, CancellationToken token = default)
    {
        var discovery = _discovery;
        if (discovery is null) return [];

        lock (_gate) _discovered.Clear();

        var payload = Encode(new WireMessage { T = "probe", Pid = _pid, Nick = _nickname });

        try
        {
            // 发两轮：广播包丢一个也不至于发现不到人
            for (var round = 0; round < 2; round++)
            {
                await discovery.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), token)
                    .ConfigureAwait(false);
                await Task.Delay(350, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogLine($"广播探测失败：{ex.Message}");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                // 收到第一份回复后再等 300ms 静默，避免漏掉回得慢的人
                if (_discovered.Count > 0 && DateTime.UtcNow - _lastReplyAt > TimeSpan.FromMilliseconds(300)) break;
            }

            await Task.Delay(120, token).ConfigureAwait(false);
        }

        lock (_gate) return [.. _discovered.Values];
    }

    /// <summary>
    /// 唯一的发现通道接收循环，同时干两件事：应答别人的 probe、收下别人的 here。
    /// 不能拆成两个循环——同一个 socket 上两个 ReceiveAsync 会互相抢包。
    /// </summary>
    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _discovery!.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogLine($"发现通道接收异常：{ex.Message}");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            try
            {
                var message = Decode(result.Buffer);
                if (message is null || message.Pid == _pid) continue; // 自己的广播，跳过

                switch (message.T)
                {
                    case "probe":
                        await ReplyHereAsync(result.RemoteEndPoint, token).ConfigureAwait(false);
                        break;

                    case "here" when message.Port > 0:
                        var address = result.RemoteEndPoint.Address.ToString();
                        lock (_gate)
                        {
                            _discovered[address] = new PeerEndpoint(address, message.Port, message.Nick ?? string.Empty);
                            _lastReplyAt = DateTime.UtcNow;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                LogLine($"处理发现消息失败：{ex.Message}");
            }
        }
    }

    private async Task ReplyHereAsync(IPEndPoint target, CancellationToken token)
    {
        var port = TransferPort;
        if (port <= 0) return;

        try
        {
            var payload = Encode(new WireMessage { T = "here", Pid = _pid, Nick = _nickname, Port = port });
            await _discovery!.SendAsync(payload, target, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogLine($"应答探测失败：{ex.Message}");
        }
    }

    // ————— 接收 —————

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) break;
                LogLine($"接受连接失败：{ex.Message}");
                continue;
            }

            // 单个连接处理失败不能影响监听循环
            _ = Task.Run(() => HandleIncomingAsync(client, token), token);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            string? partPath = null;
            string? metaPath = null;

            try
            {
                var stream = client.GetStream();
                var offer = await ReadMessageAsync(stream, token).ConfigureAwait(false);

                if (offer?.T != "offer" || string.IsNullOrWhiteSpace(offer.Name) || offer.Size <= 0)
                {
                    await WriteMessageAsync(stream, new WireMessage { T = "reject", Reason = "传输请求无效" }, token).ConfigureAwait(false);
                    return;
                }

                if (offer.Size > MaxFileSize)
                {
                    await WriteMessageAsync(stream, new WireMessage
                    {
                        T = "reject",
                        Reason = $"文件超过 {MaxFileSize / 1024 / 1024} MB 上限"
                    }, token).ConfigureAwait(false);

                    LogLine($"拒绝接收 {offer.Name}：超过 {MaxFileSize / 1024 / 1024} MB 上限（来自 {offer.Nick ?? peer}）");
                    return;
                }

                var safeName = SanitizeName(offer.Name);
                var finalPath = UniquePath(Path.Combine(SaveDirectory, safeName));
                partPath = finalPath + ".part";
                metaPath = partPath + ".meta";

                var offset = ResolveResumeOffset(partPath, metaPath, offer);
                if (offset > 0) LogLine($"续传 {safeName}：从 {FormatSize(offset)} 处继续（来自 {offer.Nick ?? peer}）");
                else WriteMeta(metaPath, offer, safeName);

                await WriteMessageAsync(stream, new WireMessage { T = "accept", Offset = offset }, token).ConfigureAwait(false);

                var received = await ReceiveBodyAsync(stream, partPath, offset, offer.Size, token).ConfigureAwait(false);

                if (received != offer.Size)
                {
                    // 保留 .part 与 meta，下次同一文件可以接着传
                    LogLine($"接收中断：{safeName} 已收 {FormatSize(received)}/{FormatSize(offer.Size)}，已保留断点");
                    return;
                }

                var actual = await ComputeHashAsync(partPath, token).ConfigureAwait(false);
                if (!string.Equals(actual, offer.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteMessageAsync(stream, new WireMessage
                    {
                        T = "done",
                        Ok = false,
                        Reason = "校验不匹配，数据可能损坏"
                    }, token).ConfigureAwait(false);

                    CleanupPart(partPath, metaPath);
                    LogLine($"校验失败：{safeName} 与发送方提供的 SHA-256 不一致，已丢弃");
                    return;
                }

                File.Move(partPath, finalPath, overwrite: true);
                File.Delete(metaPath);

                await WriteMessageAsync(stream, new WireMessage { T = "done", Ok = true }, token).ConfigureAwait(false);

                LogLine($"已接收 {Path.GetFileName(finalPath)}（{FormatSize(offer.Size)}）");
                Received?.Invoke(finalPath, offer.Nick ?? string.Empty);
            }
            catch (OperationCanceledException)
            {
                // 程序退出，保留断点即可
            }
            catch (Exception ex)
            {
                LogLine($"接收失败：{ex.Message}");

                // 中断时保留 .part 供续传，只有明确失败才清理
                if (partPath is not null && metaPath is not null && !File.Exists(partPath))
                    CleanupPart(partPath, metaPath);
            }
        }
    }

    /// <summary>判断能否续传：边车 meta 里的名字/大小/哈希都对得上就用已有 .part 的长度。</summary>
    private static long ResolveResumeOffset(string partPath, string metaPath, WireMessage offer)
    {
        try
        {
            if (!File.Exists(partPath) || !File.Exists(metaPath)) return 0;

            var meta = Decode(File.ReadAllBytes(metaPath));
            if (meta is null) return 0;
            if (meta.Size != offer.Size) return 0;
            if (!string.Equals(meta.Hash, offer.Hash, StringComparison.OrdinalIgnoreCase)) return 0;

            var length = new FileInfo(partPath).Length;
            return length > 0 && length <= offer.Size ? length : 0;
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"读取断点信息失败：{ex.Message}");
            return 0;
        }
    }

    private static void WriteMeta(string metaPath, WireMessage offer, string safeName)
    {
        try
        {
            File.WriteAllBytes(metaPath, Encode(new WireMessage { Name = safeName, Size = offer.Size, Hash = offer.Hash }));
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"写入断点信息失败：{ex.Message}");
        }
    }

    private static void CleanupPart(string partPath, string metaPath)
    {
        try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* 忽略 */ }
        try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { /* 忽略 */ }
    }

    private static async Task<long> ReceiveBodyAsync(
        NetworkStream stream, string partPath, long offset, long total, CancellationToken token)
    {
        using var file = new FileStream(
            partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, ChunkSize, useAsync: true);

        if (offset > 0)
        {
            file.SetLength(offset);
            file.Seek(offset, SeekOrigin.Begin);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var received = offset;

        try
        {
            while (received < total)
            {
                var want = (int)Math.Min(buffer.Length, total - received);
                var read = await stream.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);
                if (read <= 0) break;

                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
            }

            return received;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ————— 发送 —————

    /// <summary>把单个文件发给指定队友。返回结果说明，不抛异常。</summary>
    public async Task<TransferResult> SendAsync(
        PeerEndpoint peer,
        string path,
        IProgress<TransferProgress>? progress = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(peer.Address) || peer.Port <= 0) return new TransferResult(false, "目标地址无效");
        if (!File.Exists(path)) return new TransferResult(false, "文件不存在");

        var info = new FileInfo(path);
        if (info.Length <= 0) return new TransferResult(false, "空文件无法发送");
        if (info.Length > MaxFileSize)
            return new TransferResult(false, $"文件超过 {MaxFileSize / 1024 / 1024} MB 上限，请压缩后再试");

        try
        {
            LogLine($"正在计算校验值：{info.Name}");
            var hash = await ComputeHashAsync(path, token).ConfigureAwait(false);

            using var client = new TcpClient();

            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(peer.Address, peer.Port, connectTimeout.Token).ConfigureAwait(false);
            }

            using var stream = client.GetStream();

            await WriteMessageAsync(stream, new WireMessage
            {
                T = "offer",
                Pid = _pid,
                Nick = _nickname,
                Name = info.Name,
                Size = info.Length,
                Hash = hash
            }, token).ConfigureAwait(false);

            var reply = await ReadMessageAsync(stream, token).ConfigureAwait(false);

            if (reply is null) return new TransferResult(false, "对方没有响应");
            if (reply.T == "reject") return new TransferResult(false, $"对方拒绝接收：{reply.Reason}");
            if (reply.T != "accept") return new TransferResult(false, $"对方响应异常（{reply.T}）");

            var offset = Math.Clamp(reply.Offset, 0, info.Length);
            var resumed = offset > 0;

            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true))
            {
                if (offset > 0)
                {
                    file.Seek(offset, SeekOrigin.Begin);
                    LogLine($"对方已有 {FormatSize(offset)}，从断点继续发送");
                }

                var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
                var sent = offset;
                progress?.Report(new TransferProgress(sent, info.Length));

                try
                {
                    while (sent < info.Length)
                    {
                        var read = await file.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (read <= 0) break;

                        await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        sent += read;
                        progress?.Report(new TransferProgress(sent, info.Length));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var done = await ReadMessageAsync(stream, token).ConfigureAwait(false);

            if (done?.T == "done" && done.Ok == true)
                return new TransferResult(true, $"{(resumed ? "续传" : "发送")}完成：{info.Name}（{FormatSize(info.Length)}）");

            return new TransferResult(false, done?.Reason ?? "对方未确认接收完成");
        }
        catch (OperationCanceledException)
        {
            return new TransferResult(false, "已取消发送");
        }
        catch (Exception ex)
        {
            return new TransferResult(false,
                $"发送失败：{ex.Message}\n请确认对方在线、和你在同一个房间，且防火墙已放行");
        }
    }

    // ————— 行协议 —————

    /// <summary>一行 UTF-8 JSON，以 \n 结尾。逐字节读到换行，避免把后面的二进制流读进头部。</summary>
    private static async Task<WireMessage?> ReadMessageAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), token).ConfigureAwait(false);
            if (read <= 0) return null;

            if (buffer[length] == (byte)'\n') break;
            length++;
        }

        return length == 0 ? null : Decode(buffer.AsSpan(0, length));
    }

    private static Task WriteMessageAsync(NetworkStream stream, WireMessage message, CancellationToken token)
    {
        var payload = Encode(message);
        var frame = new byte[payload.Length + 1];
        payload.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';

        return stream.WriteAsync(frame, token).AsTask();
    }

    private static byte[] Encode(WireMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    private static WireMessage? Decode(byte[] payload) => Decode(payload.AsSpan());

    private static WireMessage? Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            return payload.Length == 0 ? null : JsonSerializer.Deserialize<WireMessage>(payload, Json);
        }
        catch
        {
            return null;
        }
    }

    // ————— 工具 —————

    private static async Task<string> ComputeHashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>文件名消毒：只留最后一段，去掉路径分隔与控制字符，最长 200 字符。</summary>
    private static string SanitizeName(string name)
    {
        var text = (name ?? string.Empty).Replace('\\', '/');
        var slash = text.LastIndexOf('/');
        if (slash >= 0) text = text[(slash + 1)..];

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch) || Path.GetInvalidFileNameChars().Contains(ch)) builder.Append('_');
            else builder.Append(ch);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length == 0) result = "未命名文件";
        if (result.Length > 200) result = result[..200];

        return result;
    }

    /// <summary>重名时加 (1)(2)…，不覆盖已有文件。</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{name} ({Guid.NewGuid():n}){extension}");
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024:0.0} MB"
        : $"{bytes / 1024} KB";

    /// <summary>放行防火墙：发现通道走 UDP，传输端口走一小段 TCP 范围。</summary>
    private static void AllowThroughFirewall()
    {
        AddFirewallRule($"{FirewallRulePrefix}（发现）", "UDP", DiscoveryPort.ToString());
        AddFirewallRule($"{FirewallRulePrefix}（传输）", "TCP", $"{TransferPortMin}-{TransferPortMax}");
    }

    /// <summary>
    /// 加一条入站放行规则。netsh 的同名规则不会覆盖，
    /// 每次启动直接 add 会越堆越多，所以先删一次再加。
    /// </summary>
    private static void AddFirewallRule(string name, string protocol, string ports)
    {
        RunNetsh("advfirewall", "firewall", "delete", "rule", $"name={name}");
        RunNetsh("advfirewall", "firewall", "add", "rule",
            $"name={name}", "dir=in", "action=allow", $"protocol={protocol}", $"localport={ports}");
    }

    private static void RunNetsh(params string[] arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments) psi.ArgumentList.Add(argument);

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            // 没有管理员权限时加不了规则，但局域网内通常已经通了，只记日志
            CoreLog.Warn($"执行 netsh 失败（{string.Join(' ', arguments)}）：{ex.Message}");
        }
    }

    private void LogLine(string message) => Log?.Invoke($"[文件传输] {message}");

    /// <summary>线上一帧。所有帧共用同一个结构，靠 T 区分，省得多套类型。</summary>
    private sealed class WireMessage
    {
        /// <summary>协议版本。</summary>
        public int V { get; set; } = 1;

        /// <summary>probe / here / offer / accept / reject / done。</summary>
        public string T { get; set; } = string.Empty;

        /// <summary>发送方实例标识，用来忽略自己发出的广播。</summary>
        public string? Pid { get; set; }

        /// <summary>昵称。</summary>
        public string? Nick { get; set; }

        /// <summary>here 帧里是对方的 TCP 传输端口。</summary>
        public int Port { get; set; }

        /// <summary>文件名。</summary>
        public string? Name { get; set; }

        /// <summary>文件总字节数。</summary>
        public long Size { get; set; }

        /// <summary>整文件 SHA-256（小写 hex）。</summary>
        public string? Hash { get; set; }

        /// <summary>accept 帧里是接收方已有的字节数，用于续传。</summary>
        public long Offset { get; set; }

        /// <summary>reject / done 里的原因说明。</summary>
        public string? Reason { get; set; }

        /// <summary>done 帧里表示校验是否通过。</summary>
        public bool? Ok { get; set; }
    }
}
