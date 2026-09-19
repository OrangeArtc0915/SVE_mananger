using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Multiplayer;

namespace StardewLauncher.App.Pages;

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
/// 联机页：只有樱花FRP 一条路。
/// 流程是「填访问密钥 → 在可用节点上建一条 UDP 24642 隧道 → 启动隧道 → 把连接地址发给朋友」，
/// 朋友在游戏里按局域网联机输入地址即可。隧道由樱花FRP 官方 frpc 客户端维持，
/// 首次使用时下载到数据目录；本页不装任何驱动，也不需要管理员权限。
/// </summary>
public partial class PageMultiplayer : LauncherPage
{
    private SakuraFrpApi? _sakura;
    private FrpcRunner? _frpc;
    private List<SakuraNode> _sakuraNodes = [];
    private List<SakuraTunnel> _sakuraTunnels = [];
    private SakuraNode? _sakuraSelectedNode;
    private bool _sakuraAccountLoaded;
    private bool _sakuraBusy;
    private bool _hostHooked;
    private bool _autoVerified;
    private string _tunnelAddress = string.Empty;

    public PageMultiplayer()
    {
        InitializeComponent();

        LoadSettings();

        Loaded += OnLoaded;
    }

    // ————— 生命周期 —————

    public override void OnEnter() => AutoVerifyOnce();

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

        UpdateFrpcState();
        AutoVerifyOnce();
    }

    /// <summary>存过访问密钥就自动验证一次，省得每次进页面都要手点。</summary>
    private void AutoVerifyOnce()
    {
        if (_autoVerified) return;
        if (TxtSakuraKey.Text.Trim().Length == 0) return;

        _autoVerified = true;
        _ = VerifySakuraAsync();
    }

    /// <summary>程序退出时收掉 frpc，别把隧道留在后台。</summary>
    private void OnHostClosed(object? sender, EventArgs e)
    {
        _frpc?.Dispose();
        _frpc = null;

        _sakura?.Dispose();
        _sakura = null;
    }

    // ————— 设置读写 —————

    private void LoadSettings()
    {
        var settings = SettingsStore.Current;

        TxtSakuraKey.Text = settings.SakuraAccessKey;
        TxtTunnelName.Text = settings.SakuraTunnelName;
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

            SettingsStore.Current.SakuraTunnelName = name;
            SettingsStore.Save();

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

    // ————— 工具栏与日志 —————

    private void OnLaunchGameClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Launch);

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
}
