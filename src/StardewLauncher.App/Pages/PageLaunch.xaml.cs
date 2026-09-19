using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Theme;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Launch;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Pages;

public partial class PageLaunch : LauncherPage
{
    /// <summary>日志面板最多保留的行数，超出时丢弃最早的行。</summary>
    private const int MaxLogLines = 400;

    private bool _subscribed;
    private bool _sessionSubscribed;
    private bool _logHasContent;

    private GameProcessSession? _session;
    private DateTime _launchStartedAt;

    public PageLaunch()
    {
        InitializeComponent();

        PanLinks.ItemsSource = SiteLinks.All;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        RefreshGreeting();
        RefreshCurrentInstance();
    }

    public override void OnEnter()
    {
        RefreshGreeting();
        ApplyArtOpacity();

        // 页面在外期间游戏可能已经退出，回到页面时补上收尾
        if (_session is { IsRunning: false }) HandleExit(_session.ExitCode);

        RefreshCurrentInstance();

        // 设置页可能改过城市，回到启动页时刷新一次（天气命中缓存则不会重复请求）
        PanWidgets?.Refresh();

        SubscribeSession();
    }

    /// <summary>离开页面时只退订事件，不结束游戏进程——用户可能关掉启动器继续玩。</summary>
    public override void OnLeave() => UnsubscribeSession();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            InstanceStore.Changed += RefreshCurrentInstance;
        }

        SubscribeSession();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            _subscribed = false;
            InstanceStore.Changed -= RefreshCurrentInstance;
        }

        UnsubscribeSession();
    }

    // ————— 界面刷新 —————

    /// <summary>按时段给出问候语。</summary>
    private void RefreshGreeting()
    {
        var hour = DateTime.Now.Hour;
        LabGreeting.Text = hour switch
        {
            >= 5 and < 11 => "早上好呀～",
            >= 11 and < 13 => "中午好呀～",
            >= 13 and < 18 => "下午好呀～",
            >= 18 and < 23 => "晚上好呀～",
            _ => "夜深了，早点休息～"
        };
    }

    /// <summary>
    /// 全景图现在是「完整显示的一张图」，不是铺底的纹理，所以不能压得太淡——
    /// 压狠了会变成一块看不出内容的灰块。深色主题稍微压一点，免得亮图在暗底上过于跳。
    /// </summary>
    private void ApplyArtOpacity()
    {
        if (BrushPanorama is null) return;
        BrushPanorama.Opacity = ThemeService.IsDark ? 0.85 : 0.95;
    }

    /// <summary>把当前实例信息与按钮状态同步到界面。</summary>
    private void RefreshCurrentInstance()
    {
        if (LabInstanceName is null) return;

        var instance = InstanceStore.Current;
        var running = _session is { IsRunning: true };

        if (instance?.Install is null)
        {
            LabInstanceName.Text = "还没有可用实例";
            LabInstanceSummary.Text = "先选择或新建一个实例";
            BtnSelectInstance.Content = "新建实例";
            PanSmapiWarn.Visibility = Visibility.Collapsed;
            BorKind.Visibility = Visibility.Collapsed;

            if (!running) BtnLaunch.IsEnabled = false;
            return;
        }

        LabInstanceName.Text = instance.Name;
        LabInstanceSummary.Text = instance.IsVanilla
            ? "原版实例：直接启动游戏主程序，不加载任何 Mod"
            : $"{instance.Summary} · Mods 目录：{instance.ModsDirectory}";
        BtnSelectInstance.Content = "选择实例";

        LabKind.Text = instance.KindText;
        BorKind.Visibility = Visibility.Visible;
        BorKind.SetResourceReference(Border.BackgroundProperty,
            instance.IsVanilla ? "Surface.Sunken" : "Accent.Faint");
        LabKind.SetResourceReference(TextBlock.ForegroundProperty,
            instance.IsVanilla ? "Text.Secondary" : "Accent.Base");

        if (!running)
        {
            BtnLaunch.IsEnabled = true;
            BtnLaunch.Content = "▶  启动游戏";
            BtnLaunch.Tone = ButtonTone.Solid;
        }

        // 原版实例不经过 SMAPI，不需要提示安装
        PanSmapiWarn.Visibility = !instance.IsVanilla && !instance.Install.HasSmapi
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ————— 启动 / 停止 —————

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_session is { IsRunning: true })
        {
            StopGame();
            return;
        }

        StartGame();
    }

    private void StartGame()
    {
        var instance = InstanceStore.Current;
        if (instance?.Install is not { } install)
        {
            AppendLine(GameLogLevel.Warn, "没有可用实例，无法启动");
            return;
        }

        // 原版实例直接启动游戏主程序，不经过 SMAPI
        if (instance.IsVanilla)
        {
            Launch(instance, install, new LaunchRequest(install.Directory, UseSmapi: false, null));
            return;
        }

        // Mod 端必须有 SMAPI，缺了就提示，不启动
        if (!install.HasSmapi)
        {
            AppendLine(GameLogLevel.Warn, "该实例是 Mod 端但游戏目录未安装 SMAPI，请先自行安装 SMAPI（实例页有下载页入口）");
            Log.Warn($"实例「{instance.Name}」是 Mod 端，但游戏目录未安装 SMAPI，已取消启动");
            return;
        }

        // Mod 一律使用游戏目录下的 Mods，不做任何重定向
        Launch(instance, install, new LaunchRequest(install.Directory, UseSmapi: true));
    }

    /// <summary>按请求启动进程，成功后就绪会话与界面状态。</summary>
    private void Launch(Instance instance, StardewInstall install, LaunchRequest request)
    {
        if (!GameLauncher.TryLaunch(request, out var session, out var error) || session is null)
        {
            AppendLine(GameLogLevel.Error, error ?? "启动失败：未知原因");
            Log.Warn($"启动游戏失败：{error}");
            return;
        }

        _session = session;
        _launchStartedAt = DateTime.Now;
        SubscribeSession();

        instance.LastLaunchAt = DateTime.Now;
        InstanceStore.Save(instance);

        BtnLaunch.Content = "■  停止游戏";
        BtnLaunch.Tone = ButtonTone.Danger;
        BtnLaunch.IsEnabled = true;

        AppendLine(GameLogLevel.Info, $"已启动「{instance.Name}」（{install.Directory}）");
        Log.Info($"界面：已启动实例「{instance.Name}」");
    }

    private void StopGame()
    {
        var session = _session;
        if (session is null) return;

        try
        {
            session.Kill();
        }
        catch (Exception ex)
        {
            Log.Warn($"结束游戏进程失败：{ex.Message}");
        }

        AppendLine(GameLogLevel.Info, "正在结束游戏进程…");
    }

    private void SubscribeSession()
    {
        if (_session is null || _sessionSubscribed) return;

        _sessionSubscribed = true;
        _session.LineReceived += OnSessionLine;
        _session.Exited += OnSessionExited;
    }

    private void UnsubscribeSession()
    {
        if (_session is null || !_sessionSubscribed) return;

        _sessionSubscribed = false;
        _session.LineReceived -= OnSessionLine;
        _session.Exited -= OnSessionExited;
    }

    /// <summary>游戏输出可能来自后台线程，必须切回 UI 线程再动控件。</summary>
    private void OnSessionLine(GameLogLine line)
    {
        if (Dispatcher.CheckAccess()) AppendLine(line);
        else Dispatcher.InvokeAsync(() => AppendLine(line));
    }

    /// <summary>游戏退出可能在后台线程触发，同样切回 UI 线程。</summary>
    private void OnSessionExited(int? code)
    {
        if (Dispatcher.CheckAccess()) HandleExit(code);
        else Dispatcher.InvokeAsync(() => HandleExit(code));
    }

    /// <summary>游戏退出后的收尾：复位按钮、累计本次运行时长。</summary>
    private void HandleExit(int? code)
    {
        var session = _session;
        if (session is null) return;

        UnsubscribeSession();
        _session = null;
        session.Dispose();

        if (code is null) AppendLine(GameLogLevel.Info, "游戏已停止");
        else AppendLine(code == 0 ? GameLogLevel.Info : GameLogLevel.Warn, $"游戏已退出（退出码 {code}）");

        var instance = InstanceStore.Current;
        if (instance is not null && _launchStartedAt != default)
        {
            var seconds = (long)Math.Max(0, (DateTime.Now - _launchStartedAt).TotalSeconds);
            instance.TotalPlaySeconds += seconds;
            InstanceStore.Save(instance);
            AppendLine(GameLogLevel.Info, $"本次运行 {FormatDuration(seconds)}，累计 {instance.PlayTimeText}");
        }

        _launchStartedAt = default;
        RefreshCurrentInstance();
        Log.Info("游戏进程已退出，启动页按钮已复位");
    }

    // ————— 日志面板 —————

    private void AppendLine(GameLogLevel level, string text)
        => AppendLine(new GameLogLine(DateTime.Now, level, text));

    private void AppendLine(GameLogLine line)
    {
        if (PanLogLines is null) return;

        if (!_logHasContent)
        {
            _logHasContent = true;
            PanLogLines.Children.Clear();
            LabLogEmpty.Visibility = Visibility.Collapsed;
            ScrollLog.Visibility = Visibility.Visible;
        }

        // 只保留最近 400 行：先移除最早的，再加入新行
        while (PanLogLines.Children.Count >= MaxLogLines) PanLogLines.Children.RemoveAt(0);

        var block = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Text = $"{line.Time:HH:mm:ss}  {line.Text}"
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, LogBrushKey(line.Level));

        PanLogLines.Children.Add(block);

        // ScrollToEnd 内部按无限偏移排队，布局完成后会被夹到末尾，无需额外 UpdateLayout
        ScrollLog.ScrollToEnd();
    }

    private static string LogBrushKey(GameLogLevel level) => level switch
    {
        GameLogLevel.Fatal or GameLogLevel.Error => "Status.Danger",
        GameLogLevel.Warn => "Status.Warn",
        GameLogLevel.Info => "Text.Secondary",
        _ => "Text.Tertiary"
    };

    private static string FormatDuration(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);

        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1) return $"{span.Minutes} 分 {span.Seconds} 秒";

        return $"{span.Seconds} 秒";
    }

    // ————— 导航与链接 —————

    private void OnSelectInstanceClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Instance);

    private void OnGoSetupClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Setup);

    private void OnLinkClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;
        ShellHelper.OpenUrl(url);
    }
}
