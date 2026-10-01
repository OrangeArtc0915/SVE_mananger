using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Services;
using StardewLauncher.App.Theme;
using StardewLauncher.App.Views;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.Appearance;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Nexus;
using StardewLauncher.Core.Smapi;
using StardewLauncher.Core.Translate;
using StardewLauncher.Core.Updater;
using StardewLauncher.Core.Weather;
using CoreApp = StardewLauncher.Core.App;
using ThemeMode = StardewLauncher.Core.App.ThemeMode;
using AccentTheme = StardewLauncher.Core.App.AccentTheme;
using BackgroundKind = StardewLauncher.Core.App.BackgroundKind;
using BackgroundFit = StardewLauncher.Core.App.BackgroundFit;

namespace StardewLauncher.App.Pages;

public partial class PageSetup : LauncherPage
{
    private bool _smapiQueryRunning;
    private bool _suppressWeatherCityChanged;
    private bool _weatherQueryRunning;
    private bool _suppressAccentSlider;

    public PageSetup()
    {
        InitializeComponent();

        LabDataPath.Text = CoreApp.Paths.Data;
        LabLogPath.Text = CoreApp.Paths.Log;

        RefreshSelection();
        RefreshUpdateToggles();
        RefreshGameInfo();
        RefreshModLibrary();
        RefreshNxmProtocol();
        RefreshAutoInstall();
        RefreshRetention();
        RefreshDownloadSource();
        RefreshUpdateLine();
        RefreshDownloadFolder();
        RefreshWeatherCity();
        RefreshTranslateSettings();

        SwitchCategory(0);
    }

    public override void OnEnter()
    {
        RefreshSelection();
        RefreshUpdateToggles();
        RefreshModLibrary();
        RefreshNxmProtocol();
        RefreshAutoInstall();
        RefreshRetention();
        RefreshDownloadSource();
        RefreshUpdateLine();
        RefreshDownloadFolder();
        RefreshWeatherCity();
        RefreshTranslateSettings();
        _ = RefreshSmapiAsync();
    }

    // ————— Mod 维护（与 Mod 管理页共用 ModMaintenance 里的同一份状态） —————

    /// <summary>「检查更新」：确保扫过之后，向 smapi.io 强制查一遍。</summary>
    private async void OnCheckModUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureModsScannedAsync()) return;

        SetModMaintenanceStatus("正在向 smapi.io 查询更新…");

        var result = await ModMaintenance.CheckUpdatesAsync(force: true);

        if (result is null)
        {
            SetModMaintenanceStatus("更新检查失败，请稍后再试。");
            return;
        }

        if (!result.Ok)
        {
            SetModMaintenanceStatus(result.Message);
            return;
        }

        SetModMaintenanceStatus(result.Updates.Count == 0
            ? $"检查完成：{result.Message}，没有发现新版本。"
            : $"检查完成：{result.Message}，其中 {result.Updates.Count} 个有新版本。");
    }

    /// <summary>「一键更新」：来源是 GitHub 的自动下载替换，其余列出来让人走下载入口。</summary>
    private async void OnQuickUpdateModsClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureModsScannedAsync()) return;

        var targets = ModMaintenance.UpdatableMods();

        if (targets.Count == 0)
        {
            SetModMaintenanceStatus("没有发现可更新的 Mod。先点一次「检查更新」。");
            return;
        }

        if (string.IsNullOrWhiteSpace(ModMaintenance.ModsDirectory))
        {
            SetModMaintenanceStatus("还没有可用的 Mods 目录，请先在「游戏实例」里建一个 Mod 端实例。");
            return;
        }

        if (!Dialogs.Confirm(Window.GetWindow(this)!, ModMaintenance.BuildUpdateConfirm(targets), "一键更新")) return;

        SetModMaintenanceStatus("正在一键更新…");

        ModUpdateInstallReport report;
        try
        {
            report = await ModMaintenance.QuickUpdateAsync(ModProgress());
        }
        catch (Exception ex)
        {
            Log.Error("一键更新失败", ex);
            SetModMaintenanceStatus($"一键更新失败：{ex.Message}");
            return;
        }

        SetModMaintenanceStatus(report.Summary);

        var detail = string.Join(Environment.NewLine,
            report.Items.Select(item => $"{(item.Updated ? "✓" : "·")} {item.ModName}：{item.Message}"));

        if (report.Failed > 0) Dialogs.Warn(Window.GetWindow(this)!, detail, "一键更新结果");
        else Dialogs.Info(Window.GetWindow(this)!, detail, "一键更新结果");
    }

    /// <summary>「自动修复」：先列问题让用户确认，再一次性修完；改文件前都会备份。</summary>
    private async void OnAutoRepairModsClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureModsScannedAsync()) return;

        var findings = ModMaintenance.AnalyzeRepairs();

        if (findings.Count == 0)
        {
            SetModMaintenanceStatus("没有发现可以自动修复的问题。");
            return;
        }

        if (!Dialogs.Confirm(Window.GetWindow(this)!, ModMaintenance.BuildRepairConfirm(findings), "自动修复")) return;

        SetModMaintenanceStatus("正在自动修复…");

        ModRepairReport report;
        try
        {
            report = await ModMaintenance.RepairAsync(findings, ModProgress());
        }
        catch (Exception ex)
        {
            Log.Error("自动修复失败", ex);
            SetModMaintenanceStatus($"自动修复失败：{ex.Message}");
            return;
        }

        SetModMaintenanceStatus(report.Changed
            ? $"自动修复完成：修好 {report.Fixed} 项，未处理 {report.Skipped} 项，失败 {report.Failed} 项。"
            : $"没有改动任何内容：未处理 {report.Skipped} 项，失败 {report.Failed} 项。");

        var detail = string.Join(Environment.NewLine, report.Lines);

        if (report.Failed > 0) Dialogs.Warn(Window.GetWindow(this)!, detail, "自动修复结果");
        else Dialogs.Info(Window.GetWindow(this)!, detail, "自动修复结果");
    }

    /// <summary>共享里没有扫描结果就现扫一次；返回 false 表示当前实例没有可用的 Mods 目录。</summary>
    private async Task<bool> EnsureModsScannedAsync()
    {
        if (ModMaintenance.LastScan is not null) return true;

        SetModMaintenanceStatus("正在扫描 Mod…");

        try
        {
            await ModMaintenance.RescanAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Mod 扫描失败", ex);
            SetModMaintenanceStatus($"Mod 扫描失败：{ex.Message}");
            return false;
        }

        if (ModMaintenance.LastScan is not null) return true;

        SetModMaintenanceStatus("当前实例没有可用的 Mods 目录，请先在「游戏实例」里建一个 Mod 端实例。");
        return false;
    }

    private IProgress<string> ModProgress()
        => new Progress<string>(text =>
        {
            if (!string.IsNullOrEmpty(text)) SetModMaintenanceStatus(text);
        });

    private void SetModMaintenanceStatus(string text)
    {
        if (LabModMaintenanceStatus is null) return;

        LabModMaintenanceStatus.Text = text;
    }

    // ————— 设置分类 —————

    private const int CategoryCount = 7;

    /// <summary>分类数量。只给自检用：折叠的卡片不参与布局，得逐个切出来才查得出重叠。</summary>
    public override int SubViewCount => CategoryCount;

    /// <summary>分类栏挂在窗口主侧栏上，选中态由 MainWindow 统一管，这里转一手。</summary>
    public override void SelectSubView(int index)
        => (Window.GetWindow(this) as MainWindow)?.SelectSetupCategory(index);

    /// <summary>
    /// 七张分类卡片同处一格，只把选中的那张显示出来，折叠的卡片不占布局。
    /// 侧栏的选中态由 MainWindow 负责，这里只管卡片本身。
    /// </summary>
    internal void SwitchCategory(int index)
    {
        if (index is < 0 or >= CategoryCount) index = 0;

        SurfaceCard[] cards =
        [
            CardAppearance, CardBackground, CardNav, CardGame, CardMods, CardNetwork, CardStorage
        ];

        for (var i = 0; i < cards.Length; i++)
            cards[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        ScrollCategory.ScrollToTop();
    }

    /// <summary>左上角返回按钮：回启动页。</summary>
    private void OnBackClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Launch);

    private void OnThemeModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<ThemeMode>(tag, out var mode)) return;

        ThemeService.SetTheme(mode, ThemeService.Accent);
        CoreApp.SettingsStore.Save();
        RefreshSelection();
    }

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<AccentTheme>(tag, out var accent)) return;

        // 点了预设就放弃自定义色，否则预设会被自定义色盖住，看着像没反应
        ThemeService.ClearCustomAccent();
        ThemeService.SetTheme(ThemeService.Mode, accent);
        CoreApp.SettingsStore.Save();
        RefreshSelection();
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(CoreApp.Paths.Data);

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(CoreApp.Paths.Log);

    /// <summary>打开当前实例指向的游戏目录。没有可用实例时给出提示，不弹异常。</summary>
    private void OnOpenGameDirClick(object sender, RoutedEventArgs e)
    {
        var dir = InstanceStore.Current?.GameDir;

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            Dialogs.Info(Window.GetWindow(this)!,
                "当前没有可用的游戏目录。请先到「游戏实例」页新建实例并指定游戏目录。",
                "游戏目录");
            return;
        }

        ShellHelper.OpenFolder(dir);
    }

    /// <summary>老用户也能主动重跑一遍配置向导：探测目录、检查 SMAPI、再建一个新实例。</summary>
    private void OnRerunWizardClick(object sender, RoutedEventArgs e)
        => new ConfigWizardWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OnOpenSmapiPageClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenUrl("https://smapi.io/");

    // ————— Mod 库 —————

    private void OnChooseModLibraryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 Mod 库目录",
            Multiselect = false
        };

        var current = CoreApp.SettingsStore.Current.ModLibraryDirectory;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        CoreApp.SettingsStore.Current.ModLibraryDirectory = dialog.FolderName;
        CoreApp.SettingsStore.Save();
        RefreshModLibrary();
    }

    private void OnDetectModLibraryClick(object sender, RoutedEventArgs e)
    {
        var detected = ModLibrary.DetectDefaultLibraryDirectory();

        if (detected is null)
        {
            Dialogs.Info(Window.GetWindow(this)!,
                "没有找到 Mod 库目录。可以点「选择目录」手动指定。",
                "自动检测");
            return;
        }

        CoreApp.SettingsStore.Current.ModLibraryDirectory = detected;
        CoreApp.SettingsStore.Save();
        RefreshModLibrary();

        Dialogs.Info(Window.GetWindow(this)!,
            $"已找到并设置 Mod 库目录：\n{detected}",
            "自动检测");
    }

    private void OnOpenModLibraryClick(object sender, RoutedEventArgs e)
    {
        var path = CoreApp.SettingsStore.Current.ModLibraryDirectory;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Dialogs.Info(Window.GetWindow(this)!,
                "还没有设置可用的 Mod 库目录。",
                "Mod 库");
            return;
        }

        ShellHelper.OpenFolder(path);
    }

    private void RefreshModLibrary()
    {
        if (LabModLibraryPath is null) return;

        var path = CoreApp.SettingsStore.Current.ModLibraryDirectory;

        if (string.IsNullOrWhiteSpace(path))
        {
            LabModLibraryPath.Text = "未设置";
            LabModLibraryPath.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            return;
        }

        var exists = Directory.Exists(path);
        LabModLibraryPath.Text = exists ? path : $"{path}（目录不存在）";
        LabModLibraryPath.SetResourceReference(TextBlock.ForegroundProperty,
            exists ? "Text.Secondary" : "Status.Danger");
        LabModLibraryPath.ToolTip = path;
    }

    // ————— nxm 协议 —————

    private void OnToggleNxmProtocolClick(object sender, RoutedEventArgs e)
    {
        var settings = CoreApp.SettingsStore.Current;
        var registered = ProtocolRegistrar.IsRegistered();

        if (registered)
        {
            if (ProtocolRegistrar.TryUnregister(out var error))
            {
                settings.NxmProtocolEnabled = false;
                CoreApp.SettingsStore.Save();
            }
            else
            {
                LabNxmProtocolStatus.Text = $"关闭失败：{error}";
                LabNxmProtocolStatus.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
                return;
            }
        }
        else
        {
            if (ProtocolRegistrar.TryRegister(out var error))
            {
                settings.NxmProtocolEnabled = true;
                CoreApp.SettingsStore.Save();
            }
            else
            {
                LabNxmProtocolStatus.Text = $"开启失败：{error}";
                LabNxmProtocolStatus.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
                return;
            }
        }

        RefreshNxmProtocol();
    }

    private void RefreshNxmProtocol()
    {
        if (BtnNxmProtocol is null) return;

        var registered = ProtocolRegistrar.IsRegistered();
        var enabled = CoreApp.SettingsStore.Current.NxmProtocolEnabled;

        BtnNxmProtocol.Content = $"nxm:// 协议关联：{(registered ? "开" : "关")}";
        BtnNxmProtocol.Tone = registered ? ButtonTone.Solid : ButtonTone.Outline;

        if (LabNxmProtocolStatus is null) return;

        if (registered)
        {
            var command = ProtocolRegistrar.RegisteredCommand;
            LabNxmProtocolStatus.Text = string.IsNullOrWhiteSpace(command)
                ? "当前状态：已注册"
                : $"当前状态：已注册 → {command}";
        }
        else
        {
            LabNxmProtocolStatus.Text = enabled
                ? "当前状态：未注册（开关为开，但注册表里没有记录，下次启动会尝试自动重建）"
                : "当前状态：未注册";
        }

        LabNxmProtocolStatus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        LabNxmProtocolStatus.ToolTip = LabNxmProtocolStatus.Text;
    }

    /// <summary>在启动器内嵌浏览器里浏览各 Mod 站点，点 Nexus 网页上的「Mod Manager Download」即可在本程序内下载。</summary>
    private void OnBrowseNexusClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NexusBrowserWindow();
        var owner = Window.GetWindow(this);
        if (owner is not null) dialog.Owner = owner;

        dialog.Show();
    }

    private void OnToggleBackupBeforeImportClick(object sender, RoutedEventArgs e)
    {
        var settings = CoreApp.SettingsStore.Current;
        settings.BackupBeforeImport = !settings.BackupBeforeImport;
        CoreApp.SettingsStore.Save();
        RefreshRetention();
    }

    private void OnToggleSnapshotClick(object sender, RoutedEventArgs e)
    {
        var settings = CoreApp.SettingsStore.Current;
        settings.SnapshotBeforeRestore = !settings.SnapshotBeforeRestore;
        CoreApp.SettingsStore.Save();
        RefreshRetention();
    }

    private void OnLogKeepClick(object sender, RoutedEventArgs e)
        => ApplyRetention(sender, value => CoreApp.SettingsStore.Current.MaxLogFileCount = value);

    private void OnLogSizeClick(object sender, RoutedEventArgs e)
        => ApplyRetention(sender, value => CoreApp.SettingsStore.Current.MaxLogFileSize = value * 1024L * 1024);

    private void OnSaveKeepClick(object sender, RoutedEventArgs e)
        => ApplyRetention(sender, value => CoreApp.SettingsStore.Current.SaveBackupKeepCount = value);

    /// <summary>几个「选一个数字」的按钮共用一条路径：取 Tag 里的数、写进设置、刷新选中态。</summary>
    private void ApplyRetention(object sender, Action<int> apply)
    {
        if (sender is not FrameworkElement { Tag: string text } || !int.TryParse(text, out var value)) return;

        apply(value);
        CoreApp.SettingsStore.Save();
        RefreshRetention();
    }

    /// <summary>刷新备份与日志相关的所有控件。日志上限要重启才生效，所以文案里点明。</summary>
    private void RefreshRetention()
    {
        if (BtnBackupBeforeImport is null) return;

        var settings = CoreApp.SettingsStore.Current;

        BtnBackupBeforeImport.Content = $"覆盖游戏原版文件前先备份：{(settings.BackupBeforeImport ? "开" : "关")}";
        BtnBackupBeforeImport.Tone = settings.BackupBeforeImport ? ButtonTone.Solid : ButtonTone.Outline;

        Highlight(BtnLogKeep8, settings.MaxLogFileCount == 8);
        Highlight(BtnLogKeep16, settings.MaxLogFileCount == 16);
        Highlight(BtnLogKeep32, settings.MaxLogFileCount == 32);

        var sizeMb = settings.MaxLogFileSize / 1024 / 1024;
        Highlight(BtnLogSize4, sizeMb == 4);
        Highlight(BtnLogSize8, sizeMb == 8);
        Highlight(BtnLogSize16, sizeMb == 16);

        LabLogLimit.Text = $"当前：保留 {settings.MaxLogFileCount} 份，单份上限 {sizeMb} MB。" +
                           "改完下次启动生效（日志文件在启动时按这套上限打开）。";

        Highlight(BtnSaveKeep5, settings.SaveBackupKeepCount == 5);
        Highlight(BtnSaveKeep10, settings.SaveBackupKeepCount == 10);
        Highlight(BtnSaveKeep20, settings.SaveBackupKeepCount == 20);
        Highlight(BtnSaveKeep30, settings.SaveBackupKeepCount == 30);

        BtnSnapshotBeforeRestore.Content =
            $"回滚存档前先打快照：{(settings.SnapshotBeforeRestore ? "开" : "关")}";
        BtnSnapshotBeforeRestore.Tone = settings.SnapshotBeforeRestore ? ButtonTone.Solid : ButtonTone.Outline;

        LabSaveRetention.Text = $"当前：每个存档保留 {settings.SaveBackupKeepCount} 份备份。"
                                + (settings.SnapshotBeforeRestore
                                    ? "回滚前会自动给当前存档打一份 -auto 快照，所以回滚本身也能反悔。"
                                    : "回滚前不再自动打快照，回滚之后就没有退路了。");
    }

    private static void Highlight(OutlineButton button, bool selected)
        => button.Tone = selected ? ButtonTone.Solid : ButtonTone.Outline;

    private void OnToggleAutoInstallClick(object sender, RoutedEventArgs e)
    {
        var settings = CoreApp.SettingsStore.Current;
        settings.AutoInstallToLibrary = !settings.AutoInstallToLibrary;
        CoreApp.SettingsStore.Save();
        RefreshAutoInstall();
    }

    private void RefreshAutoInstall()
    {
        if (BtnAutoInstall is null) return;

        var settings = CoreApp.SettingsStore.Current;

        BtnAutoInstall.Content = $"下载完成后自动解压入库：{(settings.AutoInstallToLibrary ? "开" : "关")}";
        BtnAutoInstall.Tone = settings.AutoInstallToLibrary ? ButtonTone.Solid : ButtonTone.Outline;

        BtnAutoInstallInstance.Content = $"下载后自动安装到当前实例：{(settings.AutoInstallToInstance ? "开" : "关")}";
        BtnAutoInstallInstance.Tone = settings.AutoInstallToInstance ? ButtonTone.Solid : ButtonTone.Outline;
    }

    /// <summary>下载入库后再自动装进当前实例的 Mods 目录。默认关闭。</summary>
    private void OnToggleAutoInstallInstanceClick(object sender, RoutedEventArgs e)
    {
        var settings = CoreApp.SettingsStore.Current;
        settings.AutoInstallToInstance = !settings.AutoInstallToInstance;
        CoreApp.SettingsStore.Save();
        RefreshAutoInstall();
    }

    // ————— 下载源 —————

    /// <summary>把当前下载源显示成实心按钮，另一个显示为描边按钮，并刷新下方地址。</summary>
    private void RefreshDownloadSource()
    {
        if (BtnDownloadSourceGitHub is null) return;

        var source = CoreApp.SettingsStore.Current.DownloadSource;

        BtnDownloadSourceGitHub.Tone = source == CoreApp.DownloadSource.GitHub ? ButtonTone.Solid : ButtonTone.Outline;
        BtnDownloadSourceGitee.Tone = source == CoreApp.DownloadSource.Gitee ? ButtonTone.Solid : ButtonTone.Outline;

        if (LabDownloadSourceUrl is null) return;

        var url = CoreApp.DownloadSourceUrls.HomeUrl(source);
        LabDownloadSourceUrl.Text = $"当前源：{CoreApp.DownloadSourceUrls.DisplayName(source)} · {url}";
        LabDownloadSourceUrl.ToolTip = "点击打开该源地址";
    }

    private void OnDownloadSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<CoreApp.DownloadSource>(tag, out var source)) return;

        var settings = CoreApp.SettingsStore.Current;
        if (settings.DownloadSource == source)
        {
            RefreshDownloadSource();
            return;
        }

        settings.DownloadSource = source;

        // 启动器更新线路默认跟着下载源走，用户可以之后单独改
        settings.LauncherUpdateSource = source;

        CoreApp.SettingsStore.Save();
        RefreshDownloadSource();
        RefreshUpdateLine();

        Log.Info($"下载源已切换为 {CoreApp.DownloadSourceUrls.DisplayName(source)}");
    }

    // ————— 启动器更新线路 —————

    private void OnUpdateLineClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<CoreApp.DownloadSource>(tag, out var source)) return;

        var settings = CoreApp.SettingsStore.Current;
        if (settings.LauncherUpdateSource == source)
        {
            RefreshUpdateLine();
            return;
        }

        settings.LauncherUpdateSource = source;
        CoreApp.SettingsStore.Save();
        RefreshUpdateLine();

        // 换了线路，上一次的检查结果与按钮状态就没意义了，退回初始态
        _updateReady = false;
        _updateOpenPage = false;
        _updateUrl = string.Empty;
        if (BtnCheckLauncherUpdate is not null) BtnCheckLauncherUpdate.Content = "检查启动器更新";

        Log.Info($"启动器更新线路已切换为 {CoreApp.DownloadSourceUrls.DisplayName(source)}");
    }

    private void RefreshUpdateLine()
    {
        if (BtnUpdateLineGitHub is null) return;

        var source = CoreApp.SettingsStore.Current.LauncherUpdateSource;

        BtnUpdateLineGitHub.Tone = source == CoreApp.DownloadSource.GitHub ? ButtonTone.Solid : ButtonTone.Outline;
        BtnUpdateLineGitee.Tone = source == CoreApp.DownloadSource.Gitee ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private void OnOpenDownloadSourceClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenUrl(CoreApp.DownloadSourceUrls.HomeUrl(CoreApp.SettingsStore.Current.DownloadSource));

    // ————— 天气与位置 —————

    private void RefreshWeatherCity()
    {
        if (TxtWeatherCity is null) return;

        var settings = CoreApp.SettingsStore.Current;

        _suppressWeatherCityChanged = true;
        TxtWeatherCity.Text = settings.WeatherCity;
        _suppressWeatherCityChanged = false;

        if (LabWeatherStatus is null) return;

        if (string.IsNullOrWhiteSpace(settings.WeatherCity))
        {
            LabWeatherStatus.Text = "未设置城市，主页不会显示天气。";
        }
        else if (settings.WeatherLatitude is { } latitude && settings.WeatherLongitude is { } longitude)
        {
            LabWeatherStatus.Text = $"已定位：{settings.WeatherCity.Trim()}（{latitude:0.####}, {longitude:0.####}）";
        }
        else
        {
            LabWeatherStatus.Text = $"已设置城市：{settings.WeatherCity.Trim()}，点「保存并测试」定位。";
        }

        LabWeatherStatus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
    }

    // ————— Mod 翻译 —————

    /// <summary>当前选中的服务商。改选后要点「保存」才落盘，所以单独留一份未保存的值。</summary>
    private string _translateProvider = "microsoft";

    private void RefreshTranslateSettings()
    {
        if (TxtTranslateKey is null) return;

        var settings = CoreApp.SettingsStore.Current;

        _translateProvider = settings.TranslateProvider;
        TxtTranslateKey.Text = settings.TranslateApiKey;
        TxtTranslateRegion.Text = settings.TranslateRegion;

        SyncTranslateProviderButtons();
        UpdateTranslateStatus();
    }

    /// <summary>选中的那个画成实心、其余描边 —— 与「下载源 / 更新线路」的写法保持一致。</summary>
    private void SyncTranslateProviderButtons()
    {
        (OutlineButton Button, string Key)[] buttons =
        [
            (BtnTranslateMicrosoft, "microsoft"),
            (BtnTranslateBaidu, "baidu"),
            (BtnTranslateDeepl, "deepl"),
            (BtnTranslateNone, "none")
        ];

        foreach (var (button, key) in buttons)
        {
            button.Tone = string.Equals(_translateProvider, key, StringComparison.OrdinalIgnoreCase)
                ? ButtonTone.Solid
                : ButtonTone.Outline;
        }
    }

    private void OnTranslateProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;

        _translateProvider = key;
        SyncTranslateProviderButtons();
        UpdateTranslateStatus(dirty: true);
    }

    private void OnSaveTranslateClick(object sender, RoutedEventArgs e)
    {
        var settings = CoreApp.SettingsStore.Current;

        settings.TranslateProvider = _translateProvider;
        settings.TranslateApiKey = (TxtTranslateKey.Text ?? string.Empty).Trim();
        settings.TranslateRegion = (TxtTranslateRegion.Text ?? string.Empty).Trim();

        CoreApp.SettingsStore.Save();

        UpdateTranslateStatus();

        // 只记「填没填」，绝不把密钥本身写进日志
        Log.Info($"Mod 翻译设置已保存：服务商 {settings.TranslateProvider}，" +
                 $"密钥 {(string.IsNullOrEmpty(settings.TranslateApiKey) ? "未填" : "已填")}");
    }

    private void UpdateTranslateStatus(bool dirty = false)
    {
        if (LabTranslateStatus is null) return;

        var settings = CoreApp.SettingsStore.Current;
        var provider = TranslateService.ProviderDisplayName(_translateProvider);
        var target = TranslateService.TargetDisplayName(settings.TranslateTargetLanguage);

        var configured = _translateProvider != "none"
                         && !string.IsNullOrWhiteSpace(TxtTranslateKey.Text);

        var text = configured
            ? $"当前：{provider} → {target}。密钥已填，点「保存翻译设置」生效。"
            : $"当前：{provider} → {target}。还没填密钥，翻译时会提示到这里来填。";

        LabTranslateStatus.Text = dirty ? text + "（有未保存的改动）" : text;

        LabTranslateStatus.SetResourceReference(TextBlock.ForegroundProperty,
            configured ? "Text.Tertiary" : "Status.Warn");
    }

    private void OnWeatherCityChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressWeatherCityChanged) return;

        if (LabWeatherStatus is null) return;
        LabWeatherStatus.Text = "有未保存的改动，点「保存并测试」后生效。";
        LabWeatherStatus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
    }

    /// <summary>保存城市后立刻做一次定位 + 天气查询，把结果直接显示在卡片里。</summary>
    private async void OnSaveWeatherCityClick(object sender, RoutedEventArgs e)
    {
        var settings = CoreApp.SettingsStore.Current;
        var city = (TxtWeatherCity.Text ?? string.Empty).Trim();

        // 换了城市就丢掉旧的经纬度缓存，强制重新定位
        if (!string.Equals(settings.WeatherCity?.Trim(), city, StringComparison.OrdinalIgnoreCase))
        {
            settings.WeatherLatitude = null;
            settings.WeatherLongitude = null;
            settings.WeatherFetchedAt = null;
        }

        settings.WeatherCity = city;
        CoreApp.SettingsStore.Save();
        RefreshWeatherCity();

        await TestWeatherAsync();
    }

    private void OnRefreshWeatherClick(object sender, RoutedEventArgs e) => _ = TestWeatherAsync();

    /// <summary>定位城市并强制刷新一次天气，结果写进设置页。网络失败不弹窗。</summary>
    private async Task TestWeatherAsync()
    {
        if (_weatherQueryRunning || LabWeatherStatus is null) return;

        _weatherQueryRunning = true;
        if (BtnRefreshWeather is not null) BtnRefreshWeather.IsEnabled = false;
        if (BtnSaveWeatherCity is not null) BtnSaveWeatherCity.IsEnabled = false;

        try
        {
            var city = CoreApp.SettingsStore.Current.WeatherCity?.Trim() ?? string.Empty;

            if (city.Length == 0)
            {
                LabWeatherStatus.Text = "未设置城市。";
                LabWeatherStatus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
                return;
            }

            LabWeatherStatus.Text = "正在定位并查询天气…";
            LabWeatherStatus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

            var coordinates = await WeatherService.GeocodeAsync(city);

            if (coordinates is null)
            {
                LabWeatherStatus.Text = WeatherService.LastError ?? "城市定位失败";
                LabWeatherStatus.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
                return;
            }

            var snapshot = await WeatherService.GetAsync(city, forceRefresh: true);

            if (snapshot is null)
            {
                LabWeatherStatus.Text = WeatherService.LastError ?? "天气数据不可用";
                LabWeatherStatus.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
                return;
            }

            LabWeatherStatus.Text =
                $"已定位：{snapshot.City}（{coordinates.Value.Latitude:0.####}, {coordinates.Value.Longitude:0.####}）" +
                $" · 当前 {snapshot.Temperature:0.#}°C {snapshot.Description}";
            LabWeatherStatus.SetResourceReference(TextBlock.ForegroundProperty, "Status.Success");
        }
        catch (Exception ex)
        {
            LabWeatherStatus.Text = "天气数据不可用（网络受限）";
            LabWeatherStatus.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
            Log.Warn($"设置页天气测试失败：{ex.Message}");
        }
        finally
        {
            _weatherQueryRunning = false;
            if (BtnRefreshWeather is not null) BtnRefreshWeather.IsEnabled = true;
            if (BtnSaveWeatherCity is not null) BtnSaveWeatherCity.IsEnabled = true;
        }
    }

    private void RefreshDownloadFolder()
    {
        if (LabDownloadFolder is null) return;

        var folder = CoreApp.SettingsStore.Current.DownloadFolder;
        if (string.IsNullOrWhiteSpace(folder)) folder = DownloadFolderWatcher.DefaultFolder;

        var exists = Directory.Exists(folder);
        LabDownloadFolder.Text = exists ? folder : $"{folder}（目录不存在）";
        LabDownloadFolder.SetResourceReference(TextBlock.ForegroundProperty,
            exists ? "Text.Secondary" : "Status.Danger");
        LabDownloadFolder.ToolTip = folder;
    }

    private void OnChooseDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要监控的下载目录",
            Multiselect = false
        };

        var current = CoreApp.SettingsStore.Current.DownloadFolder;
        if (string.IsNullOrWhiteSpace(current)) current = DownloadFolderWatcher.DefaultFolder;
        if (Directory.Exists(current)) dialog.InitialDirectory = current;

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        CoreApp.SettingsStore.Current.DownloadFolder = dialog.FolderName;
        CoreApp.SettingsStore.Save();
        RefreshDownloadFolder();
    }

    private void OnResetDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        CoreApp.SettingsStore.Current.DownloadFolder = DownloadFolderWatcher.DefaultFolder;
        CoreApp.SettingsStore.Save();
        RefreshDownloadFolder();
    }

    private void OnOpenDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = CoreApp.SettingsStore.Current.DownloadFolder;
        if (string.IsNullOrWhiteSpace(folder)) folder = DownloadFolderWatcher.DefaultFolder;

        ShellHelper.OpenFolder(folder);
    }

    // ————— 界面主题 —————

    private void RefreshSelection()
    {
        BtnThemeLight.Tone = ThemeService.Mode == ThemeMode.Light ? ButtonTone.Solid : ButtonTone.Outline;
        BtnThemeDark.Tone = ThemeService.Mode == ThemeMode.Dark ? ButtonTone.Solid : ButtonTone.Outline;
        BtnThemeSystem.Tone = ThemeService.Mode == ThemeMode.System ? ButtonTone.Solid : ButtonTone.Outline;

        BtnAccentStardew.Tone = SelectAccent(AccentTheme.Stardew);
        BtnAccentSky.Tone = SelectAccent(AccentTheme.SkyBlue);
        BtnAccentBerry.Tone = SelectAccent(AccentTheme.BerryPink);
        BtnAccentAutumn.Tone = SelectAccent(AccentTheme.Autumn);

        ButtonTone SelectAccent(AccentTheme theme)
            => ThemeService.Accent == theme ? ButtonTone.Solid : ButtonTone.Outline;

        RefreshBackground();
        RefreshAccentControls();
        RefreshNav();
    }

    // ————— 自定义强调色 / 面板不透明度 —————

    /// <summary>把当前主题状态回填到色相条、色值框与档位按钮。</summary>
    private void RefreshAccentControls()
    {
        _suppressAccentSlider = true;
        SldAccentHue.Value = ThemeService.EffectiveHue;
        _suppressAccentSlider = false;

        var baseColor = ThemeService.CurrentBaseColor;
        TxtAccentHex.Text = $"#{baseColor.R:X2}{baseColor.G:X2}{baseColor.B:X2}";

        LabAccentHint.Text = ThemeService.UseCustomAccent
            ? $"当前是自定义色：色相 {ThemeService.CustomHue}°，饱和度 {ThemeService.CustomSaturation}%。继续拖色相条，或在左边填一个色值。"
            : "拖动下面的色相条即改即看（上面的色板跟着变）；不想要自定义色时点「用预设配色」。";

        var opacity = ThemeService.PanelOpacity;

        BtnGlassThin.Tone = opacity < 70 ? ButtonTone.Solid : ButtonTone.Outline;
        BtnGlassNormal.Tone = opacity is >= 70 and < 85 ? ButtonTone.Solid : ButtonTone.Outline;
        BtnGlassThick.Tone = opacity is >= 85 and < 100 ? ButtonTone.Solid : ButtonTone.Outline;
        BtnGlassSolid.Tone = opacity >= 100 ? ButtonTone.Solid : ButtonTone.Outline;
    }

    /// <summary>拖色相条时实时换色（只改内存），松手或按方向键后落盘。</summary>
    private void OnAccentHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressAccentSlider) return;

        ThemeService.SetCustomAccent((int)Math.Round(e.NewValue), ThemeService.EffectiveSaturation);

        var baseColor = ThemeService.CurrentBaseColor;
        TxtAccentHex.Text = $"#{baseColor.R:X2}{baseColor.G:X2}{baseColor.B:X2}";
        LabAccentHint.Text = $"自定义色：色相 {(int)Math.Round(e.NewValue)}°，饱和度 {ThemeService.CustomSaturation}%。";
    }

    /// <summary>拖动结束 / 键盘微调后落盘，避免拖动过程中每格都写一次设置文件。</summary>
    private void OnAccentHueCommitted(object sender, RoutedEventArgs e)
    {
        if (_suppressAccentSlider || !ThemeService.UseCustomAccent) return;

        CoreApp.SettingsStore.Save();
        RefreshSelection();
    }

    private void OnAccentHexClick(object sender, RoutedEventArgs e)
    {
        var text = (TxtAccentHex.Text ?? string.Empty).Trim();
        if (!text.StartsWith('#')) text = "#" + text;

        if (!TryParseColor(text, out var color))
        {
            LabAccentHint.Text = "色值看不懂。写成 #E67E22 这样的六位十六进制就行（也可以写三位缩写）。";
            return;
        }

        var (hue, saturation) = ThemeService.DescribeColor(color);
        ThemeService.SetCustomAccent(hue, saturation);
        CoreApp.SettingsStore.Save();
        RefreshSelection();
    }

    private void OnAccentResetClick(object sender, RoutedEventArgs e)
    {
        ThemeService.ClearCustomAccent();
        CoreApp.SettingsStore.Save();
        RefreshSelection();
    }

    private static bool TryParseColor(string text, out System.Windows.Media.Color color)
    {
        color = default;

        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(text) is System.Windows.Media.Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            // 解析失败走下面的 return false，由调用方给提示
        }

        return false;
    }

    private void OnPanelOpacityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var opacity)) return;

        ThemeService.SetPanelOpacity(opacity);
        CoreApp.SettingsStore.Save();
        RefreshAccentControls();
    }

    // ————— 侧栏导航的顺序与显隐 —————

    /// <summary>按设置重建侧栏导航的行：名称 + 上移 / 下移 / 显示或收起。</summary>
    private void RefreshNav()
    {
        var settings = CoreApp.SettingsStore.Current;
        var order = MainWindow.NormalizeNavOrder(settings.NavOrder);
        var hidden = new HashSet<string>(settings.NavHidden ?? [], StringComparer.OrdinalIgnoreCase);
        var names = MainWindow.NavItemDefs.ToDictionary(def => def.Id, def => def.Name, StringComparer.OrdinalIgnoreCase);

        PanNavItems.Children.Clear();

        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var isHidden = hidden.Contains(id);

            var row = new Grid { Margin = new Thickness(0, 0, 0, i == order.Count - 1 ? 0 : 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = names.TryGetValue(id, out var name) ? name : id,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource(isHidden ? "Text.Disabled" : "Text.Primary")
            };

            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var tools = new StackPanel { Orientation = Orientation.Horizontal };

            var up = new OutlineButton
            {
                Content = "上移",
                Tag = id,
                Tone = ButtonTone.Plain,
                IsEnabled = i > 0
            };
            up.Click += OnNavMoveUpClick;
            tools.Children.Add(up);

            var down = new OutlineButton
            {
                Margin = new Thickness(8, 0, 0, 0),
                Content = "下移",
                Tag = id,
                Tone = ButtonTone.Plain,
                IsEnabled = i < order.Count - 1
            };
            down.Click += OnNavMoveDownClick;
            tools.Children.Add(down);

            var toggle = new OutlineButton
            {
                Margin = new Thickness(8, 0, 0, 0),
                Content = isHidden ? "显示" : "收起",
                Tag = id,
                Tone = isHidden ? ButtonTone.Outline : ButtonTone.Plain
            };
            toggle.Click += OnNavToggleClick;
            tools.Children.Add(toggle);

            Grid.SetColumn(tools, 1);
            row.Children.Add(tools);

            PanNavItems.Children.Add(row);
        }
    }

    private void OnNavMoveUpClick(object sender, RoutedEventArgs e) => MoveNav(sender, -1);

    private void OnNavMoveDownClick(object sender, RoutedEventArgs e) => MoveNav(sender, 1);

    private void MoveNav(object sender, int delta)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;

        var order = MainWindow.NormalizeNavOrder(CoreApp.SettingsStore.Current.NavOrder);
        var index = order.IndexOf(id);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= order.Count) return;

        (order[index], order[target]) = (order[target], order[index]);

        CoreApp.SettingsStore.Current.NavOrder = order;
        CoreApp.SettingsStore.Save();

        ApplyNavLayoutAndRefresh();
    }

    private void OnNavToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;

        var settings = CoreApp.SettingsStore.Current;
        var hidden = new List<string>(settings.NavHidden ?? []);

        var wasHidden = hidden.RemoveAll(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!wasHidden) hidden.Add(id);

        settings.NavHidden = hidden;
        CoreApp.SettingsStore.Save();

        ApplyNavLayoutAndRefresh();
    }

    private void OnNavResetClick(object sender, RoutedEventArgs e)
    {
        var settings = CoreApp.SettingsStore.Current;
        settings.NavOrder = [];
        settings.NavHidden = [];

        CoreApp.SettingsStore.Save();

        ApplyNavLayoutAndRefresh();
    }

    /// <summary>设置改完立刻下发到主窗口，再回填本页的行。</summary>
    private void ApplyNavLayoutAndRefresh()
    {
        (Window.GetWindow(this) as MainWindow)?.ApplyNavLayout();
        RefreshNav();
    }

    // ————— 个性化背景 —————

    /// <summary>从本地选一张图 / 动图 / 视频当背景。</summary>
    private void OnPickBackgroundClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片或视频",
            Filter = "图片与视频 (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.mp4;*.wmv;*.avi)" +
                     "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.mp4;*.wmv;*.avi|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ApplyBackgroundImport(BackgroundService.ImportFile(dialog.FileName));
    }

    private void OnClearBackgroundClick(object sender, RoutedEventArgs e)
    {
        BackgroundService.Clear();

        var settings = CoreApp.SettingsStore.Current;
        settings.BackgroundKind = BackgroundKind.None;
        settings.BackgroundFile = string.Empty;
        CoreApp.SettingsStore.Save();

        RefreshBackground();
        ApplyBackgroundToWindow();
    }

    private void OnBackgroundFitClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<BackgroundFit>(tag, out var fit)) return;

        CoreApp.SettingsStore.Current.BackgroundFit = fit;
        CoreApp.SettingsStore.Save();

        RefreshBackground();
        ApplyBackgroundToWindow();
    }

    private void OnBackgroundDimClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var dim)) return;

        CoreApp.SettingsStore.Current.BackgroundDim = dim;
        CoreApp.SettingsStore.Save();

        RefreshBackground();
        ApplyBackgroundToWindow();
    }

    /// <summary>导入成功就落盘并立刻生效，失败只把原因显示出来、不动当前背景。</summary>
    private void ApplyBackgroundImport(BackgroundImport result)
    {
        if (!result.Ok)
        {
            SetBackgroundStatus(result.Message, warn: true);
            return;
        }

        var settings = CoreApp.SettingsStore.Current;
        settings.BackgroundKind = result.Kind;
        settings.BackgroundFile = result.Path;
        CoreApp.SettingsStore.Save();

        RefreshBackground();
        ApplyBackgroundToWindow();

        SetBackgroundStatus(result.Message, warn: false);
    }

    /// <summary>让主窗口按最新设置重铺背景，不用重启程序。</summary>
    private void ApplyBackgroundToWindow()
        => (Window.GetWindow(this) as MainWindow)?.ApplyBackground();

    private void RefreshBackground()
    {
        var settings = CoreApp.SettingsStore.Current;

        BtnFitCover.Tone = settings.BackgroundFit == BackgroundFit.Cover ? ButtonTone.Solid : ButtonTone.Outline;
        BtnFitContain.Tone = settings.BackgroundFit == BackgroundFit.Contain ? ButtonTone.Solid : ButtonTone.Outline;
        BtnFitFill.Tone = settings.BackgroundFit == BackgroundFit.Fill ? ButtonTone.Solid : ButtonTone.Outline;

        // 档位是区间判断：用户可以手工改配置文件成任意数值，不该出现「一个都没选中」
        BtnDimNone.Tone = settings.BackgroundDim <= 0 ? ButtonTone.Solid : ButtonTone.Outline;
        BtnDimLight.Tone = settings.BackgroundDim is > 0 and <= 35 ? ButtonTone.Solid : ButtonTone.Outline;
        BtnDimMedium.Tone = settings.BackgroundDim is > 35 and <= 55 ? ButtonTone.Solid : ButtonTone.Outline;
        BtnDimHeavy.Tone = settings.BackgroundDim > 55 ? ButtonTone.Solid : ButtonTone.Outline;

        var hasBackground = settings.BackgroundKind != BackgroundKind.None
                            && File.Exists(settings.BackgroundFile);

        if (!hasBackground)
        {
            SetBackgroundStatus("未设置，使用主题渐变。", warn: false);
            return;
        }

        var status = $"当前：{DescribeBackgroundKind(settings.BackgroundKind)}　{Path.GetFileName(settings.BackgroundFile)}";

        // 素材解不开（例如视频缺解码器）时把原因一并说出来，免得用户只看到"背景没了"
        SetBackgroundStatus(
            BackgroundService.LastMediaError is { } error ? $"{status}　（{error}）" : status,
            warn: BackgroundService.LastMediaError is not null);
    }

    private static string DescribeBackgroundKind(BackgroundKind kind) => kind switch
    {
        BackgroundKind.Image => "静态图",
        BackgroundKind.Gif => "动图",
        BackgroundKind.Video => "视频",
        _ => "无"
    };

    private void SetBackgroundStatus(string message, bool warn)
    {
        LabBackground.Text = message;
        LabBackground.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Primary");
    }

    // ————— 更新开关 —————

    private void OnUpdateToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string property }) return;

        var settings = CoreApp.SettingsStore.Current;

        switch (property)
        {
            case nameof(CoreApp.Settings.CheckUpdateOnStartup):
                settings.CheckUpdateOnStartup = !settings.CheckUpdateOnStartup;
                break;
            case nameof(CoreApp.Settings.ModUpdateCheckEnabled):
                settings.ModUpdateCheckEnabled = !settings.ModUpdateCheckEnabled;
                break;
            case nameof(CoreApp.Settings.SmapiUpdateCheckEnabled):
                settings.SmapiUpdateCheckEnabled = !settings.SmapiUpdateCheckEnabled;
                break;
            default:
                return;
        }

        CoreApp.SettingsStore.Save();
        RefreshUpdateToggles();
    }

    private void RefreshUpdateToggles()
    {
        var settings = CoreApp.SettingsStore.Current;

        SetToggle(BtnCheckOnStartup, "启动时自动检查更新", settings.CheckUpdateOnStartup);
        SetToggle(BtnModUpdateCheck, "检查 Mod 更新", settings.ModUpdateCheckEnabled);
        SetToggle(BtnSmapiUpdateCheck, "检查 SMAPI 更新", settings.SmapiUpdateCheckEnabled);
    }

    private static void SetToggle(OutlineButton button, string label, bool on)
    {
        button.Content = $"{label}：{(on ? "开" : "关")}";
        button.Tone = on ? ButtonTone.Solid : ButtonTone.Outline;
    }

    // ————— 启动器自身更新 —————

    /// <summary>检查中，避免连点。</summary>
    private bool _checkingLauncherUpdate;

    /// <summary>已确认有新版本且能自动装：按钮这时是"下载并更新"。</summary>
    private bool _updateReady;

    /// <summary>有新版本但没有可自动安装的单文件 exe：按钮这时是"打开下载页"。</summary>
    private bool _updateOpenPage;

    private string _updateUrl = string.Empty;

    private void OnCheckLauncherUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateReady)
        {
            _ = DownloadAndInstallAsync();
            return;
        }

        if (_updateOpenPage)
        {
            ShellHelper.OpenUrl(LauncherUpdateInfo.ReleasePageUrl(CoreApp.SettingsStore.Current.LauncherUpdateSource));
            return;
        }

        _ = CheckLauncherUpdateAsync();
    }

    private async Task CheckLauncherUpdateAsync()
    {
        if (_checkingLauncherUpdate) return;

        _checkingLauncherUpdate = true;
        SetLauncherUpdateStatus("正在检查新版本…", warn: false);

        try
        {
            ShowLauncherUpdateResult(await LauncherUpdater.CheckAsync());
        }
        catch (Exception ex)
        {
            Log.Warn($"检查启动器更新失败：{ex.Message}");
            BtnCheckLauncherUpdate.Content = "检查启动器更新";
            SetLauncherUpdateStatus($"检查失败：{ex.Message}", warn: true);
        }
        finally
        {
            _checkingLauncherUpdate = false;
        }
    }

    private void ShowLauncherUpdateResult(LauncherUpdateInfo info)
    {
        _updateReady = false;
        _updateOpenPage = false;
        _updateUrl = string.Empty;

        if (info.Error is not null)
        {
            BtnCheckLauncherUpdate.Content = "检查启动器更新";
            SetLauncherUpdateStatus($"检查失败：{info.Error}", warn: true);
            return;
        }

        if (!info.HasUpdate)
        {
            BtnCheckLauncherUpdate.Content = "检查启动器更新";
            SetLauncherUpdateStatus($"当前 {CoreApp.AppInfo.VersionDisplay}，已是最新版本（{info.SourceName}）", warn: false);
            return;
        }

        if (!info.CanAutoInstall)
        {
            _updateOpenPage = true;
            BtnCheckLauncherUpdate.Content = "打开下载页";
            SetLauncherUpdateStatus(
                $"发现新版本 v{info.LatestVersion}，但发布页里没有可直接安装的单文件 exe 或发布包 zip，请手动下载。", warn: true);
            return;
        }

        _updateReady = true;
        _updateUrl = info.DownloadUrl;
        BtnCheckLauncherUpdate.Content = $"下载并更新到 v{info.LatestVersion}";
        SetLauncherUpdateStatus(
            $"发现新版本 v{info.LatestVersion}（{info.SourceName}，{info.AssetName}）。" +
            "点上面的按钮会自动下载、替换并重启；设置、实例与存档备份都不会动。", warn: false);
    }

    private async Task DownloadAndInstallAsync()
    {
        if (_checkingLauncherUpdate || string.IsNullOrWhiteSpace(_updateUrl)) return;

        _checkingLauncherUpdate = true;
        BtnCheckLauncherUpdate.IsEnabled = false;

        try
        {
            var progress = new Progress<double>(value =>
                SetLauncherUpdateStatus($"正在下载新版本… {value:P0}", warn: false));

            var result = await LauncherUpdater.DownloadAndInstallAsync(_updateUrl, progress);

            SetLauncherUpdateStatus(result.Message, warn: !result.Ok);

            if (!result.Ok) return;

            // 交给更新脚本：它等本进程退出后替换文件并重新启动
            await Task.Delay(600);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("自动更新失败", ex);
            SetLauncherUpdateStatus($"自动更新失败：{ex.Message}", warn: true);
        }
        finally
        {
            _checkingLauncherUpdate = false;
            BtnCheckLauncherUpdate.IsEnabled = true;
        }
    }

    private void SetLauncherUpdateStatus(string message, bool warn)
    {
        LabLauncherUpdate.Text = message;
        LabLauncherUpdate.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    /// <summary>
    /// 启动时检查到新版本、用户在弹窗里点了"现在更新"之后调到这里：
    /// 把结果摆到界面上（这一页有进度显示）并立刻开始下载安装。
    /// </summary>
    internal void StartAutoUpdate(LauncherUpdateInfo info)
    {
        ShowLauncherUpdateResult(info);

        if (_updateReady) _ = DownloadAndInstallAsync();
    }

    // ————— 当前实例与 SMAPI —————

    private void RefreshGameInfo()
    {
        if (LabGameInfo is null) return;

        var instance = InstanceStore.Current;

        if (instance is null)
        {
            LabGameInfo.Text = "当前没有可用实例，请先到「游戏实例」页新建。";
            LabGameDir.Text = "未设置";
            LabGameDir.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            LabGameDir.ToolTip = null;
            return;
        }

        LabGameInfo.Text = instance.Install is null
            ? $"「{instance.Name}」· {instance.KindText} · 游戏目录不可用"
            : $"「{instance.Name}」· {instance.KindText} · 游戏 {instance.Install.GameVersion ?? "未知"}";

        var dir = instance.GameDir;
        var exists = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);

        LabGameDir.Text = string.IsNullOrWhiteSpace(dir) ? "未设置" : exists ? dir : $"{dir}（目录不存在）";
        LabGameDir.SetResourceReference(TextBlock.ForegroundProperty,
            exists ? "Text.Secondary" : "Status.Danger");
        LabGameDir.ToolTip = dir;
    }

    /// <summary>查询 SMAPI 最新版本。网络受限时只显示提示，不弹窗、不抛异常。</summary>
    private async Task RefreshSmapiAsync()
    {
        if (_smapiQueryRunning) return;
        _smapiQueryRunning = true;

        try
        {
            RefreshGameInfo();

            var instance = InstanceStore.Current;
            var install = instance?.Install;
            var installed = install is { HasSmapi: true } ? install.SmapiVersion : null;

            LabSmapiInfo.Text = "正在查询 SMAPI 最新版本…";

            var latest = await Task.Run(() => SmapiUpdateChecker.GetLatestAsync());

            if (latest is null)
            {
                LabSmapiInfo.Text = installed is null
                    ? "当前实例未安装 SMAPI；最新版本：无法查询（网络受限）"
                    : $"当前实例 SMAPI {installed}；最新版本：无法查询（网络受限）";
                return;
            }

            LabSmapiInfo.Text = installed is null
                ? $"当前实例未安装 SMAPI；最新版本 {latest.Version}"
                : SemVer.IsNewer(latest.Version, installed)
                    ? $"当前实例 SMAPI {installed}；有新版 {latest.Version} 可用"
                    : $"当前实例 SMAPI {installed}；已是最新版";
        }
        catch (Exception ex)
        {
            LabSmapiInfo.Text = "无法查询（网络受限）";
            Log.Warn($"查询 SMAPI 版本失败：{ex.Message}");
        }
        finally
        {
            _smapiQueryRunning = false;
        }
    }
}
