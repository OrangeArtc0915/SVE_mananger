using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Multiplayer;

/// <summary>大厅里的一个在线玩家。</summary>
public sealed record HallPeer(string SessionId, string Nickname, DateTime LastSeenUtc);

/// <summary>大厅里公开出来的一间房。带密码的房间不会出现在这里（密码不公开）。</summary>
public sealed record HallRoom(
    string SessionId,
    string OwnerName,
    string Room,
    string RoomIp,
    string Node,
    bool Locked,
    DateTime LastSeenUtc);

/// <summary>
/// 大厅广场：用公共 MQTT broker 做信标，让所有装了本启动器的人互相发现。
///
/// 设计要点：
/// <list type="bullet">
/// <item>与联机房间彻底解耦——信标挂了不影响已经建好的虚拟局域网。</item>
/// <item>只发布两类信息：在线心跳（昵称）与公开房间（房间名 + 虚拟 IP + 节点）。</item>
/// <item>不做聊天，也不发布任何密码；带密码的房间只公开"存在"，join 时仍需密码。</item>
/// <item>公共 broker 上的数据所有人可见，因此这里不发送公网 IP 之类的敏感信息。</item>
/// </list>
/// </summary>
public sealed class HallClient
{
    /// <summary>主题空间。用启动器自己的前缀，避免和别的工具互相干扰。</summary>
    private const string TopicPrefix = "sdv-launcher/hall/v1";

    private const string TopicPresence = TopicPrefix + "/presence";
    private const string TopicRoom = TopicPrefix + "/room";

    /// <summary>公共 broker，按顺序尝试。都是无鉴权可直连的测试用 broker。</summary>
    private static readonly (string Host, int Port)[] Brokers =
    [
        ("broker.emqx.io", 1883),
        ("test.mosquitto.org", 1883),
        ("broker.hivemq.com", 1883)
    ];

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RoomTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, HallPeer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HallRoom> _rooms = new(StringComparer.Ordinal);
    private readonly Action<string> _log;

    private IMqttClient? _client;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private RoomAnnounce? _announce;
    private volatile bool _connected;
    private string _nickname;

    public HallClient(string nickname, Action<string>? log = null)
    {
        _nickname = Sanitize(nickname, 32);
        _log = log ?? (_ => { });
        SessionId = "p" + Guid.NewGuid().ToString("n")[..11];
    }

    /// <summary>本机在大厅里的会话标识。别人靠它把消息发给特定的人。</summary>
    public string SessionId { get; }

    public bool IsConnected => _connected;

    public string Nickname
    {
        get => _nickname;
        set => _nickname = Sanitize(value, 32);
    }

    // ————— 进出大厅 —————

    /// <summary>连接信标并加入大厅。所有 broker 都不可用时返回 false。</summary>
    public async Task<bool> JoinAsync(CancellationToken token = default)
    {
        if (_connected) return true;

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        foreach (var (host, port) in Brokers)
        {
            if (!await TryConnectAsync(host, port, _loopCts.Token).ConfigureAwait(false)) continue;

            LogLine($"已连接大厅信标 {host}");
            await PublishPresenceAsync().ConfigureAwait(false);

            _loop = Task.Run(() => LoopAsync(_loopCts.Token), CancellationToken.None);
            return true;
        }

        _loopCts.Dispose();
        _loopCts = null;
        LogLine("所有大厅信标都不可用，进入大厅失败");
        return false;
    }

    /// <summary>离开大厅：先广播离线，再断开。异常退出由 broker 的遗嘱消息兜底。</summary>
    public async Task LeaveAsync()
    {
        if (_connected)
        {
            await SafePublishAsync(TopicPresence, new HallMessage { T = "g", Id = SessionId })
                .ConfigureAwait(false);
        }

        _connected = false;

        var cts = _loopCts;
        _loopCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { /* 忽略 */ }
        }

        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* 退出中的异常无需上报 */ }
        }

        if (cts is not null) cts.Dispose();

        var client = _client;
        _client = null;
        if (client is not null)
        {
            try
            {
                if (client.IsConnected) await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"断开大厅信标失败：{ex.Message}");
            }

            try { client.Dispose(); } catch { /* 忽略 */ }
        }

        lock (_gate)
        {
            _peers.Clear();
            _rooms.Clear();
        }

        _announce = null;
        LogLine("已离开大厅");
    }

    private async Task<bool> TryConnectAsync(string host, int port, CancellationToken token)
    {
        var client = CreateClient();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ConnectTimeout);

            await client.ConnectAsync(BuildOptions(host, port), timeout.Token).ConfigureAwait(false);

            var subscribe = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(TopicPresence)
                .WithTopicFilter(TopicRoom)
                .Build();

            await client.SubscribeAsync(subscribe, token).ConfigureAwait(false);

            _client = client;
            _connected = true;
            return true;
        }
        catch (Exception ex)
        {
            LogLine($"信标 {host}:{port} 连接失败：{ex.Message}");
            try { client.Dispose(); } catch { /* 忽略 */ }
            return false;
        }
    }

    private IMqttClient CreateClient()
    {
        var client = new MqttFactory().CreateMqttClient();

        client.ApplicationMessageReceivedAsync += OnMessageAsync;
        client.DisconnectedAsync += args => OnDisconnectedAsync(client, args);

        return client;
    }

    private MqttClientOptions BuildOptions(string host, int port)
        => new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("sdvlauncher_" + SessionId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithCleanSession()
            // 异常退出（崩溃/断网）时由 broker 代发离线消息
            .WithWillTopic(TopicPresence)
            .WithWillPayload(Encode(new HallMessage { T = "g", Id = SessionId }))
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

    // ————— 心跳与重连 —————

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_connected) await TryReconnectAsync(token).ConfigureAwait(false);

                if (_connected)
                {
                    await PublishPresenceAsync().ConfigureAwait(false);
                    await PublishRoomAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"大厅心跳异常：{ex.Message}");
            }

            try
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TryReconnectAsync(CancellationToken token)
    {
        LogLine("大厅信标已断开，正在重连…");

        foreach (var (host, port) in Brokers)
        {
            if (token.IsCancellationRequested) return;
            if (!await TryConnectAsync(host, port, token).ConfigureAwait(false)) continue;

            LogLine($"已重新连接大厅信标 {host}");
            return;
        }
    }

    /// <summary>只有当前生效的那个 client 断开才改状态：换连接时旧 client 的断开事件要忽略。</summary>
    private Task OnDisconnectedAsync(IMqttClient sender, MqttClientDisconnectedEventArgs args)
    {
        if (ReferenceEquals(sender, _client))
        {
            _connected = false;
            LogLine("大厅信标连接断开，等待自动重连");
        }

        return Task.CompletedTask;
    }

    // ————— 对外发布 —————

    /// <summary>设置本机要公开的房间信息，心跳时周期性广播。密码不会发布出去。</summary>
    public void SetRoomAnnounce(string room, string roomIp, string node, bool locked)
        => _announce = new RoomAnnounce(room.Trim(), roomIp.Trim(), node.Trim(), locked);

    public void ClearRoomAnnounce() => _announce = null;

    private Task PublishPresenceAsync()
        => SafePublishAsync(TopicPresence, new HallMessage { T = "p", Id = SessionId, N = _nickname });

    private Task PublishRoomAsync()
    {
        var announce = _announce;
        if (announce is null) return Task.CompletedTask;

        return SafePublishAsync(TopicRoom, new HallMessage
        {
            T = "r",
            Id = SessionId,
            N = _nickname,
            Room = announce.Room,
            RoomIp = announce.RoomIp,
            Node = announce.Node,
            Locked = announce.Locked
        });
    }

    private async Task SafePublishAsync(string topic, HallMessage message)
    {
        var client = _client;
        if (!_connected || client is null) return;

        try
        {
            var payload = Encode(message);

            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"发布大厅消息失败：{ex.Message}");
        }
    }

    // ————— 接收 —————

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var payload = args.ApplicationMessage.PayloadSegment.ToArray();
            if (payload.Length == 0) return Task.CompletedTask;

            var message = JsonSerializer.Deserialize<HallMessage>(payload, Json);
            if (message is null || string.IsNullOrEmpty(message.Id)) return Task.CompletedTask;

            // MQTT 会把自己的消息回发给订阅者，必须忽略自己的回环
            if (message.Id == SessionId) return Task.CompletedTask;

            var now = DateTime.UtcNow;

            switch (message.T)
            {
                case "p":
                    ApplyPeer(message, now);
                    break;
                case "g":
                    RemovePeer(message.Id);
                    break;
                case "r":
                    ApplyRoom(message, now);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"处理大厅消息失败：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    private void ApplyPeer(HallMessage message, DateTime now)
    {
        var nickname = Sanitize(message.N, 32);
        if (nickname.Length == 0) nickname = "匿名玩家";

        lock (_gate)
        {
            _peers[message.Id] = new HallPeer(message.Id, nickname, now);
        }
    }

    private void RemovePeer(string sessionId)
    {
        lock (_gate)
        {
            _peers.Remove(sessionId);
            _rooms.Remove(sessionId);
        }
    }

    private void ApplyRoom(HallMessage message, DateTime now)
    {
        var room = Sanitize(message.Room, 32);
        var roomIp = Sanitize(message.RoomIp, 45);
        if (room.Length == 0 || roomIp.Length == 0) return;

        var owner = Sanitize(message.N, 32);
        if (owner.Length == 0) owner = "匿名玩家";

        lock (_gate)
        {
            _rooms[message.Id] = new HallRoom(
                message.Id,
                owner,
                room,
                roomIp,
                Sanitize(message.Node, 128),
                message.Locked,
                now);
        }
    }

    // ————— 查询 —————

    /// <summary>在线玩家（不含自己），顺带清理超时未心跳的。</summary>
    public IReadOnlyList<HallPeer> Peers()
    {
        var cutoff = DateTime.UtcNow - PeerTimeout;

        lock (_gate)
        {
            foreach (var id in _peers.Where(p => p.Value.LastSeenUtc < cutoff).Select(p => p.Key).ToList())
                _peers.Remove(id);

            return [.. _peers.Values];
        }
    }

    /// <summary>大厅里的公开房间（不含自己），顺带清理停止广播的。</summary>
    public IReadOnlyList<HallRoom> Rooms()
    {
        var cutoff = DateTime.UtcNow - RoomTimeout;

        lock (_gate)
        {
            foreach (var id in _rooms.Where(r => r.Value.LastSeenUtc < cutoff).Select(r => r.Key).ToList())
                _rooms.Remove(id);

            return [.. _rooms.Values];
        }
    }

    // ————— 工具 —————

    private static byte[] Encode(HallMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    /// <summary>清掉控制字符并截断，入站文本一律过这一道。</summary>
    private static string Sanitize(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) continue;
            builder.Append(ch);
            if (builder.Length >= maxLength) break;
        }

        return builder.ToString().Trim();
    }

    private void LogLine(string message) => _log($"[大厅] {message}");

    private sealed record RoomAnnounce(string Room, string RoomIp, string Node, bool Locked);

    /// <summary>大厅消息信封。字段名走 camelCase，只保留必要字段。</summary>
    private sealed class HallMessage
    {
        /// <summary>p = 在线心跳，g = 离线，r = 房间公告。</summary>
        public string T { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        /// <summary>昵称。</summary>
        public string? N { get; set; }

        /// <summary>房间名。</summary>
        public string? Room { get; set; }

        /// <summary>房主的虚拟 IP。</summary>
        public string? RoomIp { get; set; }

        /// <summary>房间用的 EasyTier 公共节点。</summary>
        public string? Node { get; set; }

        /// <summary>房间是否设了密码（只公开"有"，不公开密码本身）。</summary>
        public bool Locked { get; set; }
    }
}
