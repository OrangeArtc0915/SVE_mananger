using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Multiplayer;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.App;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Multiplayer;

namespace StardewLauncher.App.Pages;

/// <summary>房间成员列表里的一行。</summary>
public sealed class MemberRow
{
    public string Hostname { get; init; } = string.Empty;

    public string Ipv4 { get; init; } = string.Empty;

    public string LatencyText { get; init; } = string.Empty;
}

/// <summary>大厅在线玩家列表里的一行。</summary>
public sealed class HallPeerRow
{
    public string Nickname { get; init; } = string.Empty;

    public string OnlineText { get; init; } = string.Empty;
}

/// <summary>大厅公开房间列表里的一行。</summary>
public sealed class HallRoomRow
{
    public HallRoom Source { get; init; } = null!;

    public string Room => Source.Room;

    public string Meta { get; init; } = string.Empty;

    public Visibility LockedVisibility { get; init; }
}

/// <summary>可发送文件的队友列表里的一行。</summary>
public sealed class TeamPeerRow
{
    public PeerEndpoint Source { get; init; } = null!;

    public string Nickname { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;
}

/// <summary>收藏队友列表里的一行。</summary>
public sealed class FriendRow
{
    public Friend Source { get; init; } = null!;

    public string Nickname => Source.Nickname;

    public string Detail { get; init; } = string.Empty;
}

/// <summary>樱花FRP 可选节点列表里的一行。</summary>
public sealed class SakuraNodeRow
{
    public SakuraNode Source { get; init; } = null!;

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public ButtonTone Tone { get; init; }
}

/// <summary>樱花FRP 隧道列表里的一行。</summary>
public sealed class SakuraTunnelRow
{
    public SakuraTunnel Source { get; init; } = null!;

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// 联机大厅。分两个子页：
/// <list type="bullet">
/// <item><b>我的房间</b>：用 EasyTier 组虚拟局域网，把房间名 / 密码 / 虚拟 IP 发给朋友（「分享给队友」生成分享文本，
/// 队友「一键加入」即可；房间名统一卡 6 个字符；节点从内置列表里选，可自定义并测速）。</item>
/// <item><b>大厅广场</b>：通过公共 MQTT 信标看到别人的在线状态与公开房间，并可直接加入。</item>
/// </list>
/// 两者解耦：信标连不上不影响已经建好的房间。开启联机需要管理员权限——虚拟网卡驱动要装到系统里，
/// 所以程序清单声明了 requireAdministrator。
/// </summary>
public partial class PageMultiplayer : LauncherPage
{
    private readonly DispatcherTimer _pollTimer;
    private readonly OutlineButton[] _tabs;
    private readonly UIElement[] _panels;

    private EasyTierEngine? _engine;
    private HallClient? _hall;
    private FileTransferService? _transfer;
    private SakuraFrpApi? _sakura;
    private FrpcRunner? _frpc;
    private CancellationTokenSource? _transferCts;
    private List<PeerEndpoint> _teamPeers = [];
    private List<SakuraNode> _sakuraNodes = [];
    private List<SakuraTunnel> _sakuraTunnels = [];
    private SakuraNode? _sakuraSelectedNode;
    private string _nodeAddress = EasyTierNodes.DefaultAddress;
    private bool _busy;
    private bool _hostHooked;
    private bool _useFixedIp;
    private bool _refreshing;
    private bool _teamRefreshing;
    private bool _clipboardChecked;
    private bool _sakuraAccountLoaded;
    private bool _sakuraBusy;
    private string _tunnelAddress = string.Empty;

    public PageMultiplayer()
    {
        InitializeComponent();

        _tabs = [BtnTabMine, BtnTabSakura, BtnTabTeam, BtnTabHall];
        _panels = [PanMine, PanSakura, PanTeam, PanHall];

        LoadSettings();
        SwitchTab(0);

        BtnHallEnter.Content = "进入大厅";

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();

        Loaded += OnLoaded;
    }

    // ————— 生命周期 —————

    public override void OnEnter()
    {
        EnsurePolling();
        RefreshHall();
        ApplyFriends();
        ApplyTeamPeers();
    }

    public override void OnLeave() => _pollTimer.Stop();

    /// <summary>自检用：樱花FRP / 队友 / 大厅广场都是折叠的，得切出来才能检查布局。</summary>
    public override int SubViewCount => _panels.Length;

    public override void SelectSubView(int index) => SwitchTab(index);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hostHooked)
        {
            var host = Window.GetWindow(this);
            if (host is not null)
            {
                _hostHooked = true;
                host.Closed += OnHostClosed;
            }
        }

        ApplyHallState();
        EnsurePolling();
        ApplyFriends();
        UpdateFrpcState();
        ScheduleClipboardCheck();
    }

    /// <summary>程序退出时收掉 easytier-core 并离开大厅，避免虚拟网卡被一直占着、大厅里留个假在线。</summary>
    private void OnHostClosed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _engine?.Stop();
        _engine = null;

        _transfer?.Stop();
        _transfer = null;

        _frpc?.Dispose();
        _frpc = null;

        _sakura?.Dispose();
        _sakura = null;

        // 不等它完成：大厅客户端设了遗嘱消息，异常退出时由 broker 代发离线
        _ = _hall?.LeaveAsync();
        _hall = null;
    }

    // ————— 子标签 —————

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (int.TryParse(tag, out var index)) SwitchTab(index);
    }

    private void SwitchTab(int index)
    {
        for (var i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].Tone = i == index ? ButtonTone.Solid : ButtonTone.Outline;
            _panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        // 切到「队友」时自动发现一次，省得每次都要手点刷新
        if (index == 2) _ = RefreshTeamAsync();

        // 切到「樱花FRP」时，之前存过访问密钥就自动验证一次
        if (index == 1 && !_sakuraAccountLoaded && TxtSakuraKey.Text.Trim().Length > 0) _ = VerifySakuraAsync();
    }

    // ————— 设置读写 —————

    private void LoadSettings()
    {
        var settings = SettingsStore.Current;

        TxtNickname.Text = settings.MultiplayerNickname;
        TxtRoom.Text = ShareText.NormalizeRoom(settings.MultiplayerRoom);
        TxtKey.Text = settings.MultiplayerKey;
        TxtFixedIp.Text = settings.MultiplayerFixedIp;

        _nodeAddress = EasyTierNodes.Resolve(settings.MultiplayerNode);
        if (_nodeAddress.Length == 0) _nodeAddress = EasyTierNodes.DefaultAddress;
        LabNode.Text = EasyTierNodes.Label(_nodeAddress);

        TxtSakuraKey.Text = settings.SakuraAccessKey;
        TxtTunnelName.Text = settings.SakuraTunnelName;

        _useFixedIp = settings.MultiplayerManualIp;
        UpdateIpModeVisual();
    }

    private void SaveSettings(string room)
    {
        var settings = SettingsStore.Current;

        settings.MultiplayerNickname = TxtNickname.Text.Trim();
        settings.MultiplayerRoom = room;
        settings.MultiplayerKey = TxtKey.Text.Trim();
        settings.MultiplayerNode = _nodeAddress;
        settings.MultiplayerManualIp = _useFixedIp;
        settings.MultiplayerFixedIp = TxtFixedIp.Text.Trim();

        settings.SakuraTunnelName = TxtTunnelName.Text.Trim();
        if (TxtSakuraKey.Text.Trim().Length > 0) settings.SakuraAccessKey = TxtSakuraKey.Text.Trim();

        // 设置文件是人可编辑的，手改出 null 不该让整页崩掉
        var recent = settings.MultiplayerRecentRooms ??= [];
        recent.RemoveAll(r => string.Equals(r, room, StringComparison.OrdinalIgnoreCase));
        if (room.Length > 0)
        {
            recent.Insert(0, room);
            if (recent.Count > 8) recent.RemoveRange(8, recent.Count - 8);
        }

        SettingsStore.Save();
    }

    private void OnIpModeClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _engine is not null) return;

        _useFixedIp = ReferenceEquals(sender, BtnIpFixed);
        UpdateIpModeVisual();
    }

    private void UpdateIpModeVisual()
    {
        BtnIpAuto.Tone = _useFixedIp ? ButtonTone.Outline : ButtonTone.Solid;
        BtnIpFixed.Tone = _useFixedIp ? ButtonTone.Solid : ButtonTone.Outline;
        BoxFixedIp.Visibility = _useFixedIp ? Visibility.Visible : Visibility.Collapsed;
    }

    // ————— 节点选择 —————

    private void OnPickNodeClick(object sender, RoutedEventArgs e)
    {
        if (_engine is not null)
        {
            AppendLog("联机进行中不能换节点，请先点「断开联机」。", warn: true);
            return;
        }

        if (_busy) return;

        var settings = SettingsStore.Current;
        var picker = new NodePickerWindow(_nodeAddress, settings.MultiplayerCustomNodes ?? [])
        {
            Owner = Window.GetWindow(this)
        };

        if (picker.ShowDialog() != true) return;

        _nodeAddress = picker.SelectedNode;
        LabNode.Text = EasyTierNodes.Label(_nodeAddress);

        settings.MultiplayerNode = _nodeAddress;
        settings.MultiplayerCustomNodes = [.. picker.CustomNodes];
        SettingsStore.Save();

        AppendLog($"已选择节点：{_nodeAddress}");
    }

    /// <summary>
    /// 传给引擎的节点列表：选中的排第一，其余内置与自定义节点跟在后面。
    /// 引擎在连不上时会把列表里的下一个节点当作备用，所以顺序不能乱。
    /// </summary>
    private List<string> BuildNodeList()
    {
        var nodes = new List<string> { _nodeAddress };

        foreach (var node in EasyTierNodes.BuiltIn)
            if (!nodes.Contains(node.Address, StringComparer.OrdinalIgnoreCase)) nodes.Add(node.Address);

        foreach (var custom in SettingsStore.Current.MultiplayerCustomNodes ?? [])
            if (!contains(nodes, custom)) nodes.Add(custom);

        return nodes;

        static bool contains(List<string> list, string value)
            => list.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
    }

    // ————— 分享 / 一键加入 —————

    private void OnShareClick(object sender, RoutedEventArgs e)
    {
        var room = ShareText.NormalizeRoom(TxtRoom.Text);
        if (room.Length == 0)
        {
            var owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner, "请先填房间名，再分享给队友。", "分享给队友",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TxtRoom.Text = room;

        var text = ShareText.Build(
            node: _nodeAddress,
            room: room,
            key: TxtKey.Text.Trim(),
            manualIp: _useFixedIp,
            manualIpAddress: TxtFixedIp.Text.Trim());

        AppendLog($"已生成组网分享文本（房间「{room}」· 节点 {_nodeAddress}）");

        var window = new ShareWindow(text) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private async void OnJoinShareClick(object sender, RoutedEventArgs e) => await JoinFromClipboardAsync(true);

    /// <summary>从剪贴板读队友的分享文本并加入。interactive 为 false 时只在能加入时才提示。</summary>
    private async Task JoinFromClipboardAsync(bool interactive)
    {
        if (_busy) return;

        if (_engine is not null)
        {
            if (interactive) AppendLog("已经在一个房间里了，请先点「断开联机」再加入。", warn: true);
            return;
        }

        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            if (interactive) AppendLog($"读取剪贴板失败：{ex.Message}", warn: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            if (interactive) ShowJoinHint("剪贴板里没有内容。\n\n请先复制队友发来的分享文本，再点「一键加入」。");
            return;
        }

        var info = ShareText.Parse(text);
        if (info is null)
        {
            if (interactive)
                ShowJoinHint("剪贴板内容不是有效的组网分享文本。\n\n请完整复制队友发的内容（以「星露谷启动器 组网分享」开头）。");
            return;
        }

        await ApplyAndJoinAsync(info);
    }

    /// <summary>按分享内容填表并直接开始连接（对齐 HMOL：一键加入后会自己连）。</summary>
    private async Task ApplyAndJoinAsync(ShareInfo info)
    {
        if (!ApplyShare(info)) return;

        var room = ShareText.NormalizeRoom(info.Room);
        if (room.Length == 0) return;

        AppendLog("正在按分享内容自动连接…");
        await StartAsync(room);
    }

    /// <summary>把分享内容填进表单。版本或方案不兼容时返回 false。</summary>
    private bool ApplyShare(ShareInfo info)
    {
        var owner = Window.GetWindow(this);

        if (info.Version.Length > 0 && !string.Equals(info.Version, AppInfo.Version, StringComparison.Ordinal))
        {
            if (owner is not null)
                MessageBox.Show(owner,
                    $"该分享来自不同版本的启动器：\n\n分享版本: {info.Version}\n当前版本: {AppInfo.Version}\n\n请双方升级到相同版本后再联机。",
                    "版本不匹配", MessageBoxButton.OK, MessageBoxImage.Warning);

            AppendLog($"拒绝加入：分享版本 {info.Version} 与当前版本 {AppInfo.Version} 不一致", warn: true);
            return false;
        }

        if (!string.Equals(info.Plan, ShareText.PlanEasyTier, StringComparison.OrdinalIgnoreCase))
        {
            if (owner is not null)
                MessageBox.Show(owner,
                    $"该分享用的是「{info.Plan}」方案，本启动器只支持 EasyTier 组网，无法加入。",
                    "方案不支持", MessageBoxButton.OK, MessageBoxImage.Warning);

            AppendLog($"拒绝加入：分享方案 {info.Plan} 不受支持", warn: true);
            return false;
        }

        TxtRoom.Text = ShareText.NormalizeRoom(info.Room);
        TxtKey.Text = info.Key;
        TxtFixedIp.Text = info.ManualIpAddress;

        _useFixedIp = info.ManualIp;
        UpdateIpModeVisual();

        var node = EasyTierNodes.Resolve(info.Node);
        if (node.Length > 0)
        {
            _nodeAddress = node;
            LabNode.Text = EasyTierNodes.Label(node);

            // 队友用的节点可能不在内置列表里，补进自定义节点，下次还能直接选
            var settings = SettingsStore.Current;
            var customs = settings.MultiplayerCustomNodes ??= [];
            var builtIn = EasyTierNodes.BuiltIn.Any(
                n => string.Equals(n.Address, node, StringComparison.OrdinalIgnoreCase));

            if (!builtIn && !customs.Any(c => string.Equals(c, node, StringComparison.OrdinalIgnoreCase)))
                customs.Add(node);
        }

        SaveSettings(TxtRoom.Text);
        SwitchTab(0);

        AppendLog($"已按分享内容填入：房间「{TxtRoom.Text}」· 节点 {_nodeAddress}" +
                  (info.IsOwner ? string.Empty : "（以成员身份加入）"));

        return true;
    }

    private void ShowJoinHint(string message)
    {
        var owner = Window.GetWindow(this);
        if (owner is null) return;

        MessageBox.Show(owner, message, "一键加入", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>打开联机大厅时检查一次剪贴板（对齐 HMOL：启动后延迟触发一次，之后不再打扰）。</summary>
    private void ScheduleClipboardCheck()
    {
        if (_clipboardChecked) return;
        _clipboardChecked = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (_busy || _engine is not null) return;

            string text;
            try
            {
                text = Clipboard.GetText();
            }
            catch
            {
                return;
            }

            var info = ShareText.Parse(text);

            // 版本 / 方案不兼容的分享不打扰用户，等他自己点「一键加入」时再给明确提示
            if (info is null) return;
            if (info.Version.Length > 0 && !string.Equals(info.Version, AppInfo.Version, StringComparison.Ordinal)) return;
            if (!string.Equals(info.Plan, ShareText.PlanEasyTier, StringComparison.OrdinalIgnoreCase)) return;

            var owner = Window.GetWindow(this);
            if (owner is null) return;

            var answer = MessageBox.Show(owner,
                $"剪贴板里检测到队友的组网分享：\n\n房间名: {ShareText.NormalizeRoom(info.Room)}\n节点: {info.Node}\n\n是否一键加入？",
                "检测到队友分享", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes) _ = ApplyAndJoinAsync(info);
        };

        timer.Start();
    }

    // ————— 开启 / 断开联机 —————

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _engine is not null) return;

        var room = ShareText.NormalizeRoom(TxtRoom.Text);
        if (room.Length == 0)
        {
            var owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner, "请先填房间名。\n房间名和密码要用同样的值发给朋友，才能进同一个房间。",
                    "联机大厅", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TxtRoom.Text = room;
        await StartAsync(room);
    }

    private async Task StartAsync(string room)
    {
        SetBusy(true);

        try
        {
            SaveSettings(room);

            AppendLog("正在准备 EasyTier 组件…");
            await Task.Run(EasyTierAssets.EnsureExtracted);

            // 上次异常退出可能留下占用虚拟网卡 / RPC 端口的进程
            var stale = await EasyTierEngine.CleanupStaleAsync(EasyTierEngine.DefaultInstanceName);
            if (stale > 0) AppendLog($"已清理 {stale} 个残留的 easytier 进程");

            var engine = new EasyTierEngine(
                community: room,
                key: TxtKey.Text.Trim(),
                nodes: BuildNodeList(),
                toolDirectory: EasyTierAssets.ToolDirectory,
                useFixedIp: _useFixedIp,
                fixedIp: TxtFixedIp.Text.Trim(),
                log: message => Dispatcher.BeginInvoke(() => AppendLog(message)));

            _engine = engine;

            AppendLog($"开始连接房间「{room}」（节点 {_nodeAddress}）…");
            await engine.StartAsync();

            AppendLog($"联机已开启，虚拟 IP = {engine.Ip}");
            AppendLog("把「分享给队友」生成的文本发给朋友，他们点「一键加入」就能进同一个房间。");

            StartTransferService();

            ApplyConnectedState(true);
            EnsurePolling();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _engine?.Stop();
            _engine = null;
            ApplyConnectedState(false);

            AppendLog($"开启联机失败：{ex.Message}", warn: true);
            Log.Error("开启联机失败", ex);

            var owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner,
                    $"开启联机失败：{ex.Message}\n\n常见原因：\n· 公共节点不可达（点「选择」换一个节点）\n· 防火墙 / 杀软拦截了 easytier-core\n· 房间名或密码里含有特殊字符\n· 双方用的节点不一致",
                    "联机大厅", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        StopEngine();
        AppendLog("已断开联机。");
    }

    private void StopEngine()
    {
        _engine?.Stop();
        _engine = null;

        ApplyConnectedState(false);
        EnsurePolling();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        var connected = _engine is not null;

        BtnStart.IsEnabled = !busy && !connected;
        BtnStop.IsEnabled = !busy && connected;
        BtnCopyIp.IsEnabled = !busy && connected;
        BtnLaunchGame.IsEnabled = !busy && connected;
        BtnShare.IsEnabled = !busy;
        BtnJoinShare.IsEnabled = !busy && !connected;
        BtnRefreshTeam.IsEnabled = !busy;

        var editable = !busy && !connected;
        foreach (var input in new Control[] { TxtRoom, TxtKey, TxtFixedIp, BtnIpAuto, BtnIpFixed, BtnPickNode })
            input.IsEnabled = editable;

        ApplyHallState();
    }

    private void ApplyConnectedState(bool connected)
    {
        SetBusy(_busy);

        if (!connected)
        {
            LabMyIp.Text = "未连接";
            LabMyIpHint.Text = "连接成功后会在这里显示你的虚拟 IP";
            LabNodeInfo.Text = string.Empty;
            ApplyPeers([]);
            _hall?.ClearRoomAnnounce();
            StopTransferService();
            return;
        }

        var ip = _engine?.Ip ?? string.Empty;
        LabMyIp.Text = string.IsNullOrWhiteSpace(ip) ? "获取中…" : ip.Split('/')[0];
        LabMyIpHint.Text = "把这个 IP 发给朋友；他们进同一房间后，游戏里按局域网联机或直接连这个 IP。";

        PublishRoomAnnounce();
    }

    // ————— 我的房间：状态刷新 —————

    private async Task RefreshAsync()
    {
        if (_refreshing) return;

        _refreshing = true;
        try
        {
            await RefreshRoomAsync();
            RefreshHall();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshRoomAsync()
    {
        var engine = _engine;
        if (engine is null || !engine.IsRunning) return;

        try
        {
            var info = await engine.QueryNodeInfoAsync();
            var peers = await engine.QueryPeersAsync();

            // 期间用户可能已断开或重启了引擎，丢弃过期结果
            if (!ReferenceEquals(engine, _engine)) return;

            var ip = string.IsNullOrWhiteSpace(info.Ip) ? engine.Ip : info.Ip.Split('/')[0];
            if (!string.IsNullOrWhiteSpace(ip)) LabMyIp.Text = ip;

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.PeerId)) details.Add($"Peer ID：{info.PeerId}");
            if (!string.IsNullOrWhiteSpace(info.PublicIpv4)) details.Add($"公网 IP：{info.PublicIpv4}");
            if (!string.IsNullOrWhiteSpace(info.NatType)) details.Add($"NAT：{info.NatType}");
            LabNodeInfo.Text = string.Join("　", details);

            ApplyPeers(peers);

            // 虚拟 IP 可能刚拿到，顺手把房间公告刷新一次
            PublishRoomAnnounce();
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新联机状态失败：{ex.Message}");
        }
    }

    private void ApplyPeers(List<EasyTierPeer> peers)
    {
        PanPeers.ItemsSource = peers
            .OrderBy(p => p.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(p => new MemberRow
            {
                Hostname = string.IsNullOrWhiteSpace(p.Hostname) ? "（未命名）" : p.Hostname,
                Ipv4 = p.Ipv4,
                LatencyText = string.IsNullOrWhiteSpace(p.Latency) ? "延迟未知" : $"{p.Latency} ms"
            })
            .ToList();

        var hasPeers = peers.Count > 0;
        ScrollPeers.Visibility = hasPeers ? Visibility.Visible : Visibility.Collapsed;
        LabNoPeers.Visibility = hasPeers ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EnsurePolling()
    {
        if (_engine?.IsRunning == true || _hall is not null) _pollTimer.Start();
        else _pollTimer.Stop();
    }

    // ————— 樱花FRP —————

    private void SetSakuraState(string message, bool warn)
    {
        LabSakuraState.Text = message;
        LabSakuraState.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    private void SetSakuraBusy(bool busy)
    {
        _sakuraBusy = busy;

        BtnSakuraVerify.IsEnabled = !busy;
        BtnPrepareFrpc.IsEnabled = !busy;
        BtnRefreshTunnels.IsEnabled = !busy;
        BtnLoadNodes.IsEnabled = !busy;
        BtnCreateTunnel.IsEnabled = !busy;
        TxtSakuraKey.IsEnabled = !busy;
        TxtTunnelName.IsEnabled = !busy;
    }

    /// <summary>校验访问密钥并把账号信息与可用节点拉下来。失败时把原因写到状态行。</summary>
    private async Task<bool> EnsureSakuraAccountAsync(bool forceReload = false)
    {
        var key = TxtSakuraKey.Text.Trim();
        if (key.Length == 0)
        {
            SetSakuraState("请先填写访问密钥", warn: true);
            return false;
        }

        if (_sakuraAccountLoaded && !forceReload) return true;

        _sakura ??= new SakuraFrpApi();
        SetSakuraState("正在验证…", warn: false);

        var user = await _sakura.GetUserAsync(key);
        if (!user.Ok || user.Value is null)
        {
            SetSakuraState(user.Message, warn: true);
            return false;
        }

        ShowSakuraAccount(user.Value);

        var nodes = await _sakura.GetNodesAsync(key);
        if (nodes.Ok && nodes.Value is not null)
        {
            // 只留能开星露谷隧道的节点：允许建隧道 + 允许 UDP + 没离线
            _sakuraNodes = [.. nodes.Value
                .Where(node => node.Usable)
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)];

            _sakuraSelectedNode = _sakuraNodes.FirstOrDefault();
            ApplySakuraNodes();
        }

        _sakuraAccountLoaded = true;

        SettingsStore.Current.SakuraAccessKey = key;
        SettingsStore.Save();

        SetSakuraState(
            $"已连接账号「{user.Value.Name}」，可用节点 {_sakuraNodes.Count} 个",
            warn: false);

        return true;
    }

    private void ShowSakuraAccount(SakuraUser user)
    {
        if (user.BanReason.Length > 0)
        {
            LabSakuraAccount.Text = user.BanReason;
            LabSakuraAccount.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
            LabSakuraAccount.Visibility = Visibility.Visible;
            return;
        }

        var parts = new List<string>
        {
            $"账号：{user.Name}",
            $"剩余流量：{FormatTraffic(user.TrafficRemaining)}",
            $"隧道上限：{user.TunnelLimit} 条"
        };

        if (user.Speed.Length > 0) parts.Add($"限速：{user.Speed}");

        LabSakuraAccount.Text = string.Join("　·　", parts);
        LabSakuraAccount.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        LabSakuraAccount.Visibility = Visibility.Visible;
    }

    private async void OnSakuraVerifyClick(object sender, RoutedEventArgs e) => await VerifySakuraAsync();

    private async Task VerifySakuraAsync()
    {
        if (_sakuraBusy) return;

        SetSakuraBusy(true);
        try
        {
            _sakuraAccountLoaded = false;
            await EnsureSakuraAccountAsync(forceReload: true);
        }
        finally
        {
            SetSakuraBusy(false);
        }
    }

    private async void OnLoadNodesClick(object sender, RoutedEventArgs e)
    {
        if (_sakuraBusy) return;

        SetSakuraBusy(true);
        try
        {
            await EnsureSakuraAccountAsync(forceReload: true);
        }
        finally
        {
            SetSakuraBusy(false);
        }
    }

    private void OnSakuraNodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SakuraNodeRow row }) return;

        _sakuraSelectedNode = row.Source;
        ApplySakuraNodes();
    }

    private void ApplySakuraNodes()
    {
        PanSakuraNodes.ItemsSource = _sakuraNodes
            .Select(node => new SakuraNodeRow
            {
                Source = node,
                Title = node.Name,
                Detail = $"{node.Host}　{node.Description}",
                Tone = ReferenceEquals(node, _sakuraSelectedNode) ? ButtonTone.Solid : ButtonTone.Outline
            })
            .ToList();

        var hasNodes = _sakuraNodes.Count > 0;
        ScrollSakuraNodes.Visibility = hasNodes ? Visibility.Visible : Visibility.Collapsed;
        LabNoNodes.Visibility = hasNodes ? Visibility.Collapsed : Visibility.Visible;

        if (!hasNodes)
            LabNoNodes.Text = "没有找到可用节点（需要允许建隧道 + 允许 UDP + 在线）。过一会儿再试，或换个时段。";
    }

    private async void OnPrepareFrpcClick(object sender, RoutedEventArgs e)
    {
        if (_sakuraBusy) return;

        SetSakuraBusy(true);
        try
        {
            var runner = EnsureFrpcRunner();
            var result = await runner.EnsureFrpcAsync();

            AppendLog(result.Message, warn: !result.Ok);
            UpdateFrpcState();
        }
        finally
        {
            SetSakuraBusy(false);
        }
    }

    private void UpdateFrpcState()
    {
        if (File.Exists(FrpcRunner.FrpcPath))
        {
            var size = new FileInfo(FrpcRunner.FrpcPath).Length / 1024 / 1024.0;
            LabFrpcState.Text = $"frpc 已就绪（{size:0.0} MB）。可以直接启动隧道了。";
            LabFrpcState.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            BtnPrepareFrpc.Content = "重新准备";
            return;
        }

        LabFrpcState.Text = "还没有准备 frpc 客户端。用它才能把本地端口穿透出去。";
        LabFrpcState.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        BtnPrepareFrpc.Content = "准备 frpc";
    }

    private FrpcRunner EnsureFrpcRunner()
    {
        if (_frpc is not null) return _frpc;

        var runner = new FrpcRunner();

        runner.Log += message => Dispatcher.BeginInvoke(() => AppendLog(message));

        runner.Started += () => Dispatcher.BeginInvoke(() =>
            AppendLog("隧道启动成功，把连接地址发给朋友即可。"));

        runner.AddressFound += address => Dispatcher.BeginInvoke(() =>
        {
            _tunnelAddress = address;
            LabTunnelAddress.Text = address;
            CardTunnelRunning.Visibility = Visibility.Visible;
        });

        _frpc = runner;
        return runner;
    }

    private async void OnRefreshTunnelsClick(object sender, RoutedEventArgs e)
    {
        if (_sakuraBusy) return;

        SetSakuraBusy(true);
        try
        {
            if (!await EnsureSakuraAccountAsync()) return;
            await RefreshTunnelsAsync();
        }
        finally
        {
            SetSakuraBusy(false);
        }
    }

    private async Task RefreshTunnelsAsync()
    {
        if (_sakura is null) return;

        var result = await _sakura.GetTunnelsAsync(TxtSakuraKey.Text.Trim());
        if (!result.Ok || result.Value is null)
        {
            AppendLog($"读取隧道列表失败：{result.Message}", warn: true);
            return;
        }

        // 只有 UDP 隧道能用于星露谷，其它类型的列出来只会让人误点
        _sakuraTunnels = [.. result.Value
            .Where(tunnel => string.Equals(tunnel.Type, "udp", StringComparison.OrdinalIgnoreCase))];

        ApplySakuraTunnels();
        AppendLog($"已读取隧道列表，其中 UDP 隧道 {_sakuraTunnels.Count} 条");
    }

    private void ApplySakuraTunnels()
    {
        var lastUsed = SettingsStore.Current.SakuraTunnelId;

        PanTunnels.ItemsSource = _sakuraTunnels
            .OrderByDescending(tunnel => tunnel.Id == lastUsed)
            .ThenBy(tunnel => tunnel.Name, StringComparer.OrdinalIgnoreCase)
            .Select(tunnel => new SakuraTunnelRow
            {
                Source = tunnel,
                Title = tunnel.Name + (tunnel.Id == lastUsed ? "　（上次用的）" : string.Empty),
                Detail = $"ID {tunnel.Id}　本地 {tunnel.LocalIp}:{tunnel.LocalPort}　远程 {tunnel.Remote}" +
                         (tunnel.Status != 0 ? $"　状态异常：{tunnel.StatusReason}" : string.Empty)
            })
            .ToList();

        var hasTunnels = _sakuraTunnels.Count > 0;

        ScrollTunnels.Visibility = hasTunnels ? Visibility.Visible : Visibility.Collapsed;
        LabNoTunnels.Visibility = hasTunnels ? Visibility.Collapsed : Visibility.Visible;

        if (!hasTunnels)
            LabNoTunnels.Text = "还没有 UDP 隧道。先点「验证并加载」，再到下面新建一条。";
    }

    private async void OnCreateTunnelClick(object sender, RoutedEventArgs e)
    {
        if (_sakuraBusy) return;

        var name = TxtTunnelName.Text.Trim();
        if (name.Length == 0)
        {
            SetSakuraState("请先填隧道名", warn: true);
            return;
        }

        SetSakuraBusy(true);
        try
        {
            if (!await EnsureSakuraAccountAsync()) return;

            if (_sakuraSelectedNode is null || _sakura is null)
            {
                SetSakuraState("请先在下面选一个节点", warn: true);
                return;
            }

            AppendLog($"正在创建 UDP 隧道「{name}」（节点 {_sakuraSelectedNode.Name}，本地端口 {SakuraFrpApi.StardewPort}）…");

            var created = await _sakura.CreateUdpTunnelAsync(
                TxtSakuraKey.Text.Trim(), name, _sakuraSelectedNode.Id);

            if (!created.Ok || created.Value is null)
            {
                AppendLog($"创建隧道失败：{created.Message}", warn: true);
                return;
            }

            AppendLog($"隧道已创建：{created.Value.Name}（ID {created.Value.Id}）");
            await RefreshTunnelsAsync();
        }
        finally
        {
            SetSakuraBusy(false);
        }
    }

    private async void OnStartTunnelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SakuraTunnelRow row }) return;
        if (_sakuraBusy) return;

        if (_frpc?.IsRunning == true)
        {
            AppendLog("已经有一条隧道在运行，先停掉再启动另一条。", warn: true);
            return;
        }

        SetSakuraBusy(true);
        try
        {
            // 需要有节点域名才能拼兜底地址，所以先把账号与节点加载好
            if (!await EnsureSakuraAccountAsync()) return;

            var runner = EnsureFrpcRunner();

            var prepared = await runner.EnsureFrpcAsync();
            if (!prepared.Ok)
            {
                AppendLog(prepared.Message, warn: true);
                return;
            }

            _tunnelAddress = string.Empty;
            LabTunnelAddress.Text = "正在等待连接地址…";
            CardTunnelRunning.Visibility = Visibility.Visible;

            var started = runner.Start(TxtSakuraKey.Text.Trim(), row.Source.Id);
            if (!started.Ok)
            {
                AppendLog(started.Message, warn: true);
                CardTunnelRunning.Visibility = Visibility.Collapsed;
                return;
            }

            // 先用接口信息拼一个兜底地址；frpc 日志里出现真实地址后会覆盖它
            var fallback = SakuraFrpApi.BuildAddress(row.Source, _sakuraNodes);
            if (fallback.Length > 0 && _tunnelAddress.Length == 0)
            {
                _tunnelAddress = fallback;
                LabTunnelAddress.Text = fallback;
            }

            SettingsStore.Current.SakuraTunnelId = row.Source.Id;
            SettingsStore.Save();

            AppendLog($"已启动隧道「{row.Source.Name}」");
        }
        finally
        {
            SetSakuraBusy(false);
        }
    }

    private void OnStopTunnelClick(object sender, RoutedEventArgs e)
    {
        _frpc?.Stop();

        _tunnelAddress = string.Empty;
        CardTunnelRunning.Visibility = Visibility.Collapsed;

        AppendLog("已停止樱花FRP 隧道。");
    }

    private void OnCopyTunnelAddressClick(object sender, RoutedEventArgs e)
    {
        if (_tunnelAddress.Length == 0)
        {
            AppendLog("还没有拿到连接地址，等 frpc 连上节点后再复制。", warn: true);
            return;
        }

        try
        {
            Clipboard.SetText(_tunnelAddress);
            AppendLog($"已复制连接地址：{_tunnelAddress}");
        }
        catch (Exception ex)
        {
            AppendLog($"复制到剪贴板失败：{ex.Message}", warn: true);
        }
    }

    private static string FormatTraffic(long bytes)
    {
        if (bytes <= 0) return "未知";

        const double giga = 1024d * 1024 * 1024;
        return bytes >= giga ? $"{bytes / giga:0.##} GB" : $"{bytes / 1024d / 1024:0.#} MB";
    }

    // ————— 队友：文件传输与收藏 —————

    /// <summary>联机开启后同时启动文件传输：只有在一个房间里才可能收到别人的文件。</summary>
    private void StartTransferService()
    {
        _transfer ??= CreateTransferService();

        if (!_transfer.Start(TxtNickname.Text.Trim()))
        {
            AppendLog("文件传输启动失败，本次只能联机、不能收发文件。", warn: true);
            return;
        }

        AppendLog($"文件传输已就绪，收到的文件放在：{FileTransferService.SaveDirectory}");
    }

    private void StopTransferService()
    {
        _transfer?.Stop();
        _transfer = null;

        _teamPeers = [];
        ApplyTeamPeers();

        CardTransfer.Visibility = Visibility.Collapsed;
    }

    private FileTransferService CreateTransferService()
    {
        var service = new FileTransferService();

        service.Log += message => Dispatcher.BeginInvoke(() => AppendLog(message));

        service.Received += (path, from) => Dispatcher.BeginInvoke(() =>
        {
            var who = string.IsNullOrWhiteSpace(from) ? "队友" : from;

            LabInbox.Text = $"已收到来自「{who}」的文件：\n{path}";
            CardInbox.Visibility = Visibility.Visible;

            AppendLog($"收到文件：{Path.GetFileName(path)}（来自 {who}）");

            // 互相传过文件就算认识，自动收藏
            FriendStore.Remember(who, room: ShareText.NormalizeRoom(TxtRoom.Text));
            ApplyFriends();
        });

        return service;
    }

    private async void OnRefreshTeamClick(object sender, RoutedEventArgs e) => await RefreshTeamAsync();

    private async Task RefreshTeamAsync()
    {
        if (_teamRefreshing) return;

        var transfer = _transfer;
        if (transfer is null || !transfer.IsRunning)
        {
            _teamPeers = [];
            ApplyTeamPeers();
            return;
        }

        _teamRefreshing = true;
        BtnRefreshTeam.IsEnabled = false;

        LabNoTeam.Text = "正在发现队友…";
        LabNoTeam.Visibility = Visibility.Visible;
        ScrollTeam.Visibility = Visibility.Collapsed;

        try
        {
            var found = await transfer.DiscoverAsync(TimeSpan.FromSeconds(2.5));

            // 期间可能已经断开局域网，丢弃过期结果
            if (!ReferenceEquals(transfer, _transfer)) return;

            _teamPeers = [.. found.Where(peer => peer.Port > 0)];
            AppendLog($"发现 {_teamPeers.Count} 个可发送文件的队友");
        }
        catch (Exception ex)
        {
            AppendLog($"发现队友失败：{ex.Message}", warn: true);
        }
        finally
        {
            _teamRefreshing = false;
            BtnRefreshTeam.IsEnabled = !_busy;
            ApplyTeamPeers();
        }
    }

    private void ApplyTeamPeers()
    {
        PanTeamPeers.ItemsSource = _teamPeers
            .OrderBy(peer => peer.Nickname, StringComparer.OrdinalIgnoreCase)
            .Select(peer => new TeamPeerRow
            {
                Source = peer,
                Nickname = string.IsNullOrWhiteSpace(peer.Nickname) ? "（未命名）" : peer.Nickname,
                Detail = $"虚拟 IP {peer.Address}"
            })
            .ToList();

        var hasPeers = _teamPeers.Count > 0;

        ScrollTeam.Visibility = hasPeers ? Visibility.Visible : Visibility.Collapsed;
        LabNoTeam.Visibility = hasPeers ? Visibility.Collapsed : Visibility.Visible;

        if (!hasPeers)
            LabNoTeam.Text = "还没有发现队友。先点「开启联机」，让对方也进同一个房间，再点「刷新」。";
    }

    private async void OnSendFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TeamPeerRow row }) return;

        var transfer = _transfer;
        if (transfer is null || !transfer.IsRunning)
        {
            AppendLog("文件传输没启动，请先点「开启联机」。", warn: true);
            return;
        }

        if (_transferCts is not null)
        {
            AppendLog("已经有一个传输在进行，等它结束或先点「取消」。", warn: true);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"选择要发给「{row.Nickname}」的文件",
            Filter = "所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await SendFileAsync(row.Source, dialog.FileName);
    }

    private async Task SendFileAsync(PeerEndpoint peer, string path)
    {
        var transfer = _transfer;
        if (transfer is null) return;

        var fileName = Path.GetFileName(path);

        _transferCts = new CancellationTokenSource();

        CardTransfer.Visibility = Visibility.Visible;
        BtnCancelTransfer.IsEnabled = true;
        BarTransfer.Value = 0;
        LabTransfer.Text = $"正在发送 {fileName} → {peer.Nickname}";

        var progress = new Progress<TransferProgress>(p =>
        {
            BarTransfer.Value = p.Percent;
            LabTransfer.Text =
                $"正在发送 {fileName} → {peer.Nickname}　{p.Percent}%（{FormatSize(p.Done)} / {FormatSize(p.Total)}）";
        });

        try
        {
            var result = await transfer.SendAsync(peer, path, progress, _transferCts.Token);

            AppendLog(result.Message, warn: !result.Ok);

            if (result.Ok)
            {
                BarTransfer.Value = 100;
                FriendStore.Remember(peer.Nickname, peer.Address, ShareText.NormalizeRoom(TxtRoom.Text));
                ApplyFriends();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"发送失败：{ex.Message}", warn: true);
        }
        finally
        {
            _transferCts?.Dispose();
            _transferCts = null;

            CardTransfer.Visibility = Visibility.Collapsed;
            BtnRefreshTeam.IsEnabled = !_busy;
        }
    }

    private void OnCancelTransferClick(object sender, RoutedEventArgs e)
    {
        _transferCts?.Cancel();
        BtnCancelTransfer.IsEnabled = false;
    }

    private void OnOpenInboxClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(FileTransferService.SaveDirectory);

    private void OnRemoveFriendClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FriendRow row }) return;

        FriendStore.Remove(row.Nickname);
        AppendLog($"已从收藏里移除「{row.Nickname}」");
        ApplyFriends();
    }

    private void ApplyFriends()
    {
        var friends = FriendStore.List();

        PanFriends.ItemsSource = friends
            .Select(friend => new FriendRow { Source = friend, Detail = DescribeFriend(friend) })
            .ToList();

        var hasFriends = friends.Count > 0;

        ScrollFriends.Visibility = hasFriends ? Visibility.Visible : Visibility.Collapsed;
        LabNoFriends.Visibility = hasFriends ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string DescribeFriend(Friend friend)
    {
        var parts = new List<string>();

        if (friend.LastIp.Length > 0) parts.Add($"最近 IP {friend.LastIp}");
        if (friend.LastRoom.Length > 0) parts.Add($"房间「{friend.LastRoom}」");
        if (friend.Times > 0) parts.Add($"一起玩过 {friend.Times} 次");

        parts.Add(friend.LastSeenUtc == default
            ? "还没有一起玩过"
            : $"{DescribeElapsed(DateTime.UtcNow - friend.LastSeenUtc)}见过");

        return string.Join(" · ", parts);
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";

    // ————— 大厅广场 —————

    private async void OnHallEnterClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _hall is not null) return;

        // 昵称是给别人看的，留空的话别人只能看到一串乱码 id
        var nickname = TxtNickname.Text.Trim();
        if (nickname.Length == 0)
        {
            nickname = $"玩家{Random.Shared.Next(1000, 10000)}";
            TxtNickname.Text = nickname;
            SaveSettings(ShareText.NormalizeRoom(TxtRoom.Text));
        }

        SetHallBusy(true);
        AppendLog("正在连接大厅信标…");

        var hall = new HallClient(nickname, message => Dispatcher.BeginInvoke(() => AppendLog(message)));
        var joined = await hall.JoinAsync();

        SetHallBusy(false);

        if (!joined)
        {
            AppendLog("进入大厅失败：所有公共信标都不可用。", warn: true);
            ApplyHallState();
            return;
        }

        _hall = hall;
        ApplyHallState();
        EnsurePolling();
        RefreshHall();
        PublishRoomAnnounce();

        AppendLog($"已进入大厅，昵称「{nickname}」");
    }

    private async void OnHallLeaveClick(object sender, RoutedEventArgs e)
    {
        var hall = _hall;
        _hall = null;

        if (hall is not null) await hall.LeaveAsync();

        ApplyHallState();
        RefreshHall();
        EnsurePolling();
    }

    private void SetHallBusy(bool busy)
    {
        if (busy)
        {
            BtnHallEnter.IsEnabled = false;
            BtnHallLeave.IsEnabled = false;
            return;
        }

        ApplyHallState();
    }

    private void ApplyHallState()
    {
        var connected = _hall?.IsConnected == true;

        LabHallState.Text = connected
            ? $"已进入大厅，昵称「{_hall!.Nickname}」"
            : "未进入大厅";
        LabHallState.SetResourceReference(TextBlock.ForegroundProperty,
            connected ? "Status.Success" : "Text.Primary");

        BtnHallEnter.Content = connected ? "已在大厅" : "进入大厅";
        BtnHallEnter.IsEnabled = !connected && !_busy && _hall is null;
        BtnHallLeave.IsEnabled = connected && !_busy;
    }

    /// <summary>把当前房间公开到大厅。密码只公开"有没有"，不公开内容。</summary>
    private void PublishRoomAnnounce()
    {
        var hall = _hall;
        var engine = _engine;

        if (hall is null || engine is null || !engine.IsRunning) return;

        var room = ShareText.NormalizeRoom(TxtRoom.Text);
        if (room.Length == 0) return;

        hall.Nickname = TxtNickname.Text.Trim();
        hall.SetRoomAnnounce(room, engine.Ip.Split('/')[0], engine.Node, TxtKey.Text.Trim().Length > 0);
    }

    private void RefreshHall()
    {
        if (_hall is null)
        {
            ApplyHallPeers([]);
            ApplyHallRooms([]);
            return;
        }

        ApplyHallPeers(_hall.Peers());
        ApplyHallRooms(_hall.Rooms());
    }

    private void ApplyHallPeers(IReadOnlyList<HallPeer> peers)
    {
        var now = DateTime.UtcNow;

        PanHallPeers.ItemsSource = peers
            .OrderByDescending(p => p.LastSeenUtc)
            .Select(p => new HallPeerRow
            {
                Nickname = p.Nickname,
                OnlineText = $"最后心跳 {DescribeElapsed(now - p.LastSeenUtc)}"
            })
            .ToList();

        var hasPeers = peers.Count > 0;
        CardHallPeers.Title = hasPeers ? $"在线玩家（{peers.Count}）" : "在线玩家";
        ScrollHallPeers.Visibility = hasPeers ? Visibility.Visible : Visibility.Collapsed;
        LabNoHallPeers.Visibility = hasPeers ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyHallRooms(IReadOnlyList<HallRoom> rooms)
    {
        PanHallRooms.ItemsSource = rooms
            .OrderBy(r => r.Room, StringComparer.OrdinalIgnoreCase)
            .Select(r => new HallRoomRow
            {
                Source = r,
                Meta = $"房主 {r.OwnerName} · 虚拟 IP {r.RoomIp}" +
                       (string.IsNullOrWhiteSpace(r.Node) ? string.Empty : $" · 节点 {r.Node}"),
                LockedVisibility = r.Locked ? Visibility.Visible : Visibility.Collapsed
            })
            .ToList();

        var hasRooms = rooms.Count > 0;
        CardHallRooms.Title = hasRooms ? $"公开房间（{rooms.Count}）" : "公开房间";
        ScrollHallRooms.Visibility = hasRooms ? Visibility.Visible : Visibility.Collapsed;
        LabNoHallRooms.Visibility = hasRooms ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>大厅里点「加入」：把房间信息填进「我的房间」并直接开启联机。</summary>
    private async void OnJoinRoomClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HallRoomRow row }) return;
        if (_busy) return;

        if (_engine is not null)
        {
            AppendLog("已经在一个房间里了，请先点「断开联机」再加入别人的房间。", warn: true);
            return;
        }

        var room = row.Source;

        TxtRoom.Text = ShareText.NormalizeRoom(room.Room);
        var node = EasyTierNodes.Resolve(room.Node);
        if (node.Length > 0)
        {
            _nodeAddress = node;
            LabNode.Text = EasyTierNodes.Label(node);
        }

        SwitchTab(0);

        if (room.Locked)
        {
            TxtKey.Focus();
            AppendLog($"房间「{TxtRoom.Text}」设了密码，请填上房主给你的密码，再点「开启联机」。", warn: true);

            var owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner,
                    $"房间「{TxtRoom.Text}」设了密码。\n\n房间名和节点已经帮你填好了，请输入房主给你的密码，再点「开启联机」。",
                    "加入房间", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TxtKey.Text = string.Empty;
        AppendLog($"加入大厅里的公开房间「{TxtRoom.Text}」…");
        await StartAsync(TxtRoom.Text);
    }

    // ————— 工具栏操作 —————

    private void OnCopyIpClick(object sender, RoutedEventArgs e)
    {
        var ip = _engine?.Ip;
        if (string.IsNullOrWhiteSpace(ip)) return;

        var text = ip.Split('/')[0];

        try
        {
            Clipboard.SetText(text);
            AppendLog($"已复制虚拟 IP：{text}");
        }
        catch (Exception ex)
        {
            AppendLog($"复制到剪贴板失败：{ex.Message}", warn: true);
        }
    }

    private void OnLaunchGameClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Launch);

    // ————— 日志 —————

    private void AppendLog(string message, bool warn = false)
    {
        LabLogEmpty.Visibility = Visibility.Collapsed;
        ScrollLog.Visibility = Visibility.Visible;

        PanLogLines.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 1, 0, 1),
            FontSize = 11.5,
            Foreground = (Brush)FindResource(warn ? "Status.Warn" : "Text.Secondary"),
            Text = $"[{DateTime.Now:HH:mm:ss}] {message}",
            TextWrapping = TextWrapping.Wrap
        });

        // 长时间挂机时日志会一直涨，留最近 400 行就够排查了
        while (PanLogLines.Children.Count > 400) PanLogLines.Children.RemoveAt(0);

        ScrollLog.ScrollToEnd();
    }

    private static string DescribeElapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        return seconds < 60 ? $"{seconds} 秒前" : $"{seconds / 60} 分 {seconds % 60} 秒前";
    }
}
