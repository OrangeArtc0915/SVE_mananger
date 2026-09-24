using System.Windows;
using System.Windows.Threading;
using StardewLauncher.App.Nexus;
using StardewLauncher.App.Theme;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Nexus;
using StardewLauncher.Core.Smapi;

namespace StardewLauncher.App;

public partial class App : Application
{
    /// <summary>持有单实例互斥量：整个进程存活期间不释放，进程退出时由系统回收。</summary>
    private Mutex? _singleInstanceMutex;

    public static Logger? Logger { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例判断必须最先做：nxm:// 被点击时系统会再拉起一个进程，
        // 第二个进程只把链接转发给已在运行的实例，然后立刻退出，不显示任何窗口。
        _singleInstanceMutex = new Mutex(true, NxmLinkRelay.MutexName, out var createdNew);

        var incomingLink = FindNxmArgument(e.Args);

        if (!createdNew)
        {
            InitializeLogging();

            if (string.IsNullOrWhiteSpace(incomingLink))
            {
                Log.Info("检测到已有实例在运行，请求激活其主窗口后退出");
            }
            else
            {
                Log.Info("检测到已有实例在运行，转发 nxm 链接后退出");
            }

            NxmLinkRelay.TryForward(incomingLink);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        InitializeLogging();

        // 门锁要在主窗口创建之前过：StartupUri 的窗口是在 OnStartup 返回后才建的，
        // 这里判定不通过就直接退出，用户不会看到半截界面。
        if (!EnsureSurvive())
        {
            StartupUri = null;
            Shutdown(1);
            return;
        }

        DetectModLibrary();
        EnsureDownloadFolder();

        InstanceStore.Load();
        ModTagStore.Load();
        InstallPlanStore.Load();
        ModProfileStore.Load();
        NotifyNoInstance();

        ThemeService.Initialize(SettingsStore.Current.Theme, SettingsStore.Current.AccentTheme);

        // 监听来自后启动进程的 nxm 链接（管道回调在后台线程，需切回 UI 线程）
        NxmLinkRelay.StartListening(OnRelayMessage);

        EnsureProtocolRegistration();

        if (!string.IsNullOrWhiteSpace(incomingLink))
            Dispatcher.BeginInvoke(() => OpenNxmDownload(incomingLink));

        WarmUpSmapiRelease();

#if DEBUG
        SelfCheck.Run();
#endif

        DispatcherUnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// 远端门锁：survive 分支上的 SVE_M_survive 为 false，或三个源都读不到有效配置时，
    /// 提示并终止启动。这里刻意选了"拿不到就不放行"，所以断网时启动器同样打不开。
    /// </summary>
    private static bool EnsureSurvive()
    {
        // 每次启动都要联网确认，整轮最多等这么久（三个源平分）。
        // 给得偏宽是因为冷启动那次 HTTPS 握手本身就要 1 秒多，掐太紧会把能通的源误判成不通，
        // 而误判的结果是用户完全打不开启动器。正常情况下第一个源 2 秒内就有结果，等不到这个上限。
        var budget = TimeSpan.FromMilliseconds(9000);

        SurviveResult result;

        try
        {
            // 必须丢到后台线程再等：直接在这等会把 UI 线程占住，
            // 而 HttpDownloader 里的 await 要回 UI 线程才能继续 —— 死锁。
            result = Task.Run(() => SurviveGate.CheckAsync(budget)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("门锁校验异常", ex);
            result = new SurviveResult(SurviveState.Unreachable, ex.Message);
        }

        if (result.Allowed)
        {
            Log.Info($"门锁通过：{result.Detail}");
            return true;
        }

        Log.Warn($"门锁拦截：{result.Code}（{result.Detail}）");
        ShowSurviveBlocked(result);
        return false;
    }

    private static void ShowSurviveBlocked(SurviveResult result)
    {
        var message = result.State == SurviveState.Denied
            ? "此版本星露谷启动器暂停支持！请联系域管理员！"
              + $"\n\n错误代码：{result.Code}"
            : "无法校验启动权限，启动器已停止运行。"
              + $"\n\n{result.Detail}"
              + $"\n\n错误代码：{result.Code}"
              + "\n请联系域管理员。";

        MessageBox.Show(message, $"{AppInfo.Name} {AppInfo.VersionDisplay}",
            MessageBoxButton.OK, MessageBoxImage.Stop);
    }

    private static void InitializeLogging()
    {
        Paths.Init();
        SettingsStore.Load();

        Logger = new Logger(Paths.Log, SettingsStore.Current.MaxLogFileSize,
            SettingsStore.Current.MaxLogFileCount, LogLevel.Debug);
        Log.Init(Logger);

        Log.Info($"星露谷启动器启动，数据目录：{Paths.Data}");
    }

    // ————— nxm:// 协议关联 —————

    /// <summary>从命令行参数里找出 nxm:// 链接（没有则返回 null）。</summary>
    private static string? FindNxmArgument(IReadOnlyList<string>? args)
    {
        if (args is null) return null;

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            var text = arg.Trim();
            if (text.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) return text;
        }

        return null;
    }

    /// <summary>管道消息回调（后台线程）。空消息表示只激活主窗口。</summary>
    private void OnRelayMessage(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                Log.Info("收到已有实例的激活请求，已激活主窗口");
                ActivateMainWindow();
                return;
            }

            OpenNxmDownload(message);
        });
    }

    /// <summary>弹出下载确认窗口。必须在 UI 线程调用。</summary>
    private void OpenNxmDownload(string uri)
    {
        var link = Core.Nexus.NxmLink.Parse(uri);

        if (link is null)
        {
            Log.Warn("收到无法解析的 nxm 链接，已忽略");

            const string message = "收到一个无法识别的 nxm 链接，已忽略。";
            if (MainWindow is { } ownerWindow) MessageBox.Show(ownerWindow, message, "从 Nexus 下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(message, "从 Nexus 下载", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        Log.Info($"收到 nxm 链接：{link.SafeText}（下载凭证：{(link.HasDownloadToken ? "有" : "无")}）");

        ActivateMainWindow();

        var owner = MainWindow;
        var window = new NxmDownloadWindow(link);
        if (owner is not null && owner.IsLoaded) window.Owner = owner;

        window.ShowDialog();

        ActivateMainWindow();
    }

    private static void ActivateMainWindow()
    {
        var window = Current?.MainWindow;
        if (window is null) return;

        try
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"激活主窗口失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 用户开过这个开关、但注册表里已经没有关联（例如换了 exe 路径）时，重新注册一次。
    /// </summary>
    private static void EnsureProtocolRegistration()
    {
        if (!SettingsStore.Current.NxmProtocolEnabled) return;
        if (ProtocolRegistrar.IsRegistered()) return;

        if (ProtocolRegistrar.TryRegister(out var error))
            Log.Info("nxm:// 协议关联缺失或已失效，已自动重新注册");
        else
            Log.Warn($"重新注册 nxm:// 协议失败：{error}");
    }

    /// <summary>
    /// 首次启动时尝试自动定位 Mod 库目录（仓库根 / 程序目录附近的 MODS），
    /// 找不到就只记日志，由用户在设置页手动指定。
    /// </summary>
    private static void DetectModLibrary()
    {
        if (!string.IsNullOrWhiteSpace(SettingsStore.Current.ModLibraryDirectory)) return;

        try
        {
            var detected = ModLibrary.DetectDefaultLibraryDirectory();

            if (detected is null)
            {
                Log.Info("未能自动检测到 Mod 库目录，可在设置页手动选择");
                return;
            }

            SettingsStore.Current.ModLibraryDirectory = detected;
            SettingsStore.Save();
            Log.Info($"已自动检测到 Mod 库目录：{detected}");
        }
        catch (Exception ex)
        {
            Log.Warn($"自动检测 Mod 库目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 首次启动时把系统默认下载目录写进设置，供下载中心的「下载目录」分区使用。
    /// 只记日志，不弹窗；用户之后可在设置页改。
    /// </summary>
    private static void EnsureDownloadFolder()
    {
        if (!string.IsNullOrWhiteSpace(SettingsStore.Current.DownloadFolder)) return;

        try
        {
            var folder = DownloadFolderWatcher.DefaultFolder;
            SettingsStore.Current.DownloadFolder = folder;
            SettingsStore.Save();
            Log.Info($"已写入默认下载监控目录：{folder}");
        }
        catch (Exception ex)
        {
            Log.Warn($"写入默认下载监控目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 后台预热一次 SMAPI 版本查询，把结果写进磁盘缓存供设置页 / 实例页直接读取。
    /// 必须在后台线程、且不阻塞启动；失败不弹窗，只记日志。
    /// </summary>
    private static void WarmUpSmapiRelease()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var release = await SmapiUpdateChecker.GetLatestAsync();
                Log.Info($"启动预热 SMAPI 版本查询完成：{release?.Version ?? "无结果"}");
            }
            catch (Exception ex)
            {
                Log.Warn($"启动预热 SMAPI 版本查询失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 还没有任何实例时只记一条日志，不自动创建。
    /// 游戏目录一律由用户在「新建实例」时手动指定，工具不预设。
    /// </summary>
    private static void NotifyNoInstance()
    {
        if (InstanceStore.All.Count > 0) return;

        var candidates = GameLocator.FindAll(null);
        Log.Info(candidates.Count == 0
            ? "还没有实例，并且没有检测到游戏目录，等待用户手动新建实例"
            : $"还没有实例；检测到 {candidates.Count} 个候选目录，等待用户在新建实例时选择");
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal("界面线程未处理异常", e.Exception);
        MessageBox.Show(e.Exception.Message, "星露谷启动器遇到了一个问题",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsStore.Save();
        ThemeService.Shutdown();
        Log.Info("星露谷启动器退出");
        Logger?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
