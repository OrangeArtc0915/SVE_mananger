using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Theme;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Nexus;
using StardewLauncher.Core.Smapi;
using StardewLauncher.Core.Weather;
using CoreApp = StardewLauncher.Core.App;
using ThemeMode = StardewLauncher.Core.App.ThemeMode;
using AccentTheme = StardewLauncher.Core.App.AccentTheme;

namespace StardewLauncher.App.Pages;

public partial class PageSetup : LauncherPage
{
    private bool _smapiQueryRunning;
    private bool _suppressNexusKeyChanged;
    private bool _nexusKeyDirty;
    private bool _suppressWeatherCityChanged;
    private bool _weatherQueryRunning;

    public PageSetup()
    {
        InitializeComponent();

        LabDataPath.Text = CoreApp.Paths.Data;
        LabLogPath.Text = CoreApp.Paths.Log;

        RefreshSelection();
        RefreshUpdateToggles();
        RefreshGameInfo();
        RefreshModLibrary();
        RefreshNexusKey();
        RefreshNexusAccount();
        RefreshNxmProtocol();
        RefreshAutoInstall();
        RefreshNexusQuota();
        RefreshDownloadSource();
        RefreshDownloadFolder();
        RefreshWeatherCity();
    }

    public override void OnEnter()
    {
        RefreshSelection();
        RefreshUpdateToggles();
        RefreshModLibrary();
        RefreshNexusKey();
        RefreshNexusAccount();
        RefreshNxmProtocol();
        RefreshAutoInstall();
        RefreshNexusQuota();
        RefreshDownloadSource();
        RefreshDownloadFolder();
        RefreshWeatherCity();
        _ = RefreshSmapiAsync();
    }

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
            MessageBox.Show(Window.GetWindow(this)!,
                "当前没有可用的游戏目录。请先到「游戏实例」页新建实例并指定游戏目录。",
                "游戏目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShellHelper.OpenFolder(dir);
    }

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
            MessageBox.Show(Window.GetWindow(this)!,
                "没有找到 Mod 库目录。可以点「选择目录」手动指定。",
                "自动检测", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CoreApp.SettingsStore.Current.ModLibraryDirectory = detected;
        CoreApp.SettingsStore.Save();
        RefreshModLibrary();

        MessageBox.Show(Window.GetWindow(this)!,
            $"已找到并设置 Mod 库目录：\n{detected}",
            "自动检测", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOpenModLibraryClick(object sender, RoutedEventArgs e)
    {
        var path = CoreApp.SettingsStore.Current.ModLibraryDirectory;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "还没有设置可用的 Mod 库目录。",
                "Mod 库", MessageBoxButton.OK, MessageBoxImage.Information);
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

    // ————— Nexus API Key 与下载目录 —————

    /// <summary>显示 Nexus API Key 的掩码：只保留前 6 位与后 6 位，中间用 ... 代替。</summary>
    private static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";

        var text = key.Trim();
        if (text.Length > 12) return $"{text[..6]}...{text[^6..]}";

        return text[..Math.Min(6, text.Length)] + "...";
    }

    private void RefreshNexusKey()
    {
        if (TxtNexusKey is null) return;

        var key = CoreApp.SettingsStore.Current.NexusApiKey;

        _suppressNexusKeyChanged = true;
        TxtNexusKey.Text = MaskKey(key);
        _suppressNexusKeyChanged = false;
        _nexusKeyDirty = false;

        LabNexusKeyHint.Text = string.IsNullOrWhiteSpace(key)
            ? "未配置。用于查询 Mod 详情，不填也能用其它下载功能。填入后点「保存」。"
            : "已配置，界面上只显示掩码。要更换请直接输入新 Key 后点「保存」。";
    }

    private void OnNexusKeyChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressNexusKeyChanged) return;
        _nexusKeyDirty = true;
    }

    private void OnSaveNexusKeyClick(object sender, RoutedEventArgs e)
    {
        if (!_nexusKeyDirty)
        {
            LabNexusKeyHint.Text = "没有改动，无需保存。";
            return;
        }

        // 只写设置文件，绝不把 Key 记进日志
        CoreApp.SettingsStore.Current.NexusApiKey = (TxtNexusKey.Text ?? "").Trim();
        CoreApp.SettingsStore.Save();

        RefreshNexusKey();
        LabNexusKeyHint.Text = "已保存。";

        Log.Info("已更新 Nexus API Key 设置");
    }

    // ————— Nexus 账号、nxm 协议与配额 —————

    private void RefreshNexusAccount()
    {
        if (LabNexusAccount is null) return;

        if (string.IsNullOrWhiteSpace(LabNexusAccount.Text))
        {
            LabNexusAccount.Text = "尚未验证账号。点「验证账号」检查 API Key 是否可用。";
            LabNexusAccount.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        }
    }

    private async void OnVerifyNexusClick(object sender, RoutedEventArgs e)
    {
        BtnVerifyNexus.IsEnabled = false;
        LabNexusAccount.Text = "正在验证…";
        LabNexusAccount.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        try
        {
            var account = await NexusApi.ValidateAsync();

            if (account is null)
            {
                LabNexusAccount.Text = "验证失败：" + (NexusApi.LastError ?? "未知原因");
                LabNexusAccount.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
            }
            else
            {
                var tag = account.IsPremium ? "会员" : "非会员";
                var supporter = account.IsSupporter ? " · 赞助者" : "";
                LabNexusAccount.Text = $"{account.UserName}（ID {account.UserId}）· {tag}{supporter}";
                LabNexusAccount.SetResourceReference(TextBlock.ForegroundProperty, "Status.Success");
            }
        }
        catch (Exception ex)
        {
            LabNexusAccount.Text = "验证失败：" + ex.Message;
            LabNexusAccount.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
            Log.Warn($"验证 Nexus 账号失败：{ex.Message}");
        }
        finally
        {
            BtnVerifyNexus.IsEnabled = true;
            RefreshNexusQuota();
        }
    }

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
        CoreApp.SettingsStore.Save();
        RefreshDownloadSource();

        Log.Info($"下载源已切换为 {CoreApp.DownloadSourceUrls.DisplayName(source)}");
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

    private void RefreshNexusQuota()
    {
        if (LabNexusQuota is null) return;

        var quota = NexusApi.LastQuota;

        var parts = new List<string>();
        if (quota.HourlyRemaining is { } hourly) parts.Add($"每小时剩余 {hourly}");
        if (quota.DailyRemaining is { } daily) parts.Add($"每日剩余 {daily}");
        if (quota.HourlyReset is { } reset) parts.Add($"每小时额度重置于 {reset.ToLocalTime():HH:mm}");

        LabNexusQuota.Text = parts.Count == 0 ? "未获取" : string.Join(" · ", parts);
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
