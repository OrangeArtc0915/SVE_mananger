using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.App;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Nexus;
using StardewLauncher.App.Windows;

namespace StardewLauncher.App.Views;

/// <summary>
/// 内嵌的 Mod 站点浏览器（默认 Nexus Mods，站点清单见 <see cref="ModSiteCatalog"/>），
/// 由资源中心页与 <see cref="NexusBrowserWindow"/> 共同复用。
/// 关键点：用户点网页上的「Mod Manager Download」时，Nexus 会让浏览器导航到一条 <c>nxm://</c> 链接，
/// 这里在 <see cref="CoreWebView2.NavigationStarting"/> 里取消该导航，改为在本进程内解析并交给
/// <see cref="NxmDownloadWindow"/> 处理。这样既不依赖 nxm:// 协议注册，也不需要 Nexus 会员。
/// 其它站点不使用 nxm://，因此不受影响，正常浏览。
/// WebView2 Runtime 缺失时优雅降级：显示提示卡，引导改用系统浏览器。
/// </summary>
public partial class NexusBrowserView : UserControl
{
    private readonly List<(ModSite Site, OutlineButton Button)> _siteButtons = [];

    private ModSite _activeSite = ModSiteCatalog.Default;

    private string _address = ModSiteCatalog.Default.Url;

    /// <summary>不指定初始地址时打开默认站点（Nexus Mods）；指定时直接导航过去（例如站内搜索页）。</summary>
    public NexusBrowserView(string? initialUrl = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(initialUrl)) _address = initialUrl.Trim();

        BuildSiteBar();
        ActivateSite(ModSiteCatalog.Default);

        LabAddress.Text = _address;
        LabAddress.ToolTip = _address;
        RefreshKeyState();

        Loaded += OnLoaded;
    }

    // ————— 站点栏 —————

    /// <summary>按 <see cref="ModSiteCatalog"/> 生成站点按钮；站点说明挂在按钮的 ToolTip 上。</summary>
    private void BuildSiteBar()
    {
        PanSites.Children.Clear();
        _siteButtons.Clear();

        foreach (var site in ModSiteCatalog.All)
        {
            var button = new OutlineButton
            {
                Content = site.Name,
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 4),
                Tone = ButtonTone.Outline,
                ToolTip = site.Note,
                Tag = site
            };

            AutomationProperties.SetAutomationId(button, $"Site_{site.Id}");
            button.Click += OnSiteClick;

            PanSites.Children.Add(button);
            _siteButtons.Add((site, button));
        }
    }

    /// <summary>把某个站点设为当前站点：当前站点用实心按钮高亮，其余为描边。</summary>
    private void ActivateSite(ModSite site)
    {
        _activeSite = site;

        foreach (var (candidate, button) in _siteButtons)
            button.Tone = candidate.Id == site.Id ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private void OnSiteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModSite site }) return;

        ActivateSite(site);
        NavigateTo(site.Url);
    }

    /// <summary>地址以某站点 Url 开头时高亮该站点；站内翻页不强制切回站点按钮。</summary>
    private void HighlightMatchingSite(string uri)
    {
        foreach (var (site, _) in _siteButtons)
        {
            if (!uri.StartsWith(site.Url, StringComparison.OrdinalIgnoreCase)) continue;

            if (_activeSite.Id != site.Id) ActivateSite(site);
            return;
        }
    }

    private void NavigateTo(string url)
    {
        _address = url;
        LabAddress.Text = url;
        LabAddress.ToolTip = url;

        var core = Browser.CoreWebView2;
        if (core is null)
        {
            Log.Info($"内嵌浏览器尚未就绪，已记录待打开地址：{url}");
            return;
        }

        Log.Info($"在线下载窗口切换到站点：{url}");
        core.Navigate(url);
    }

    // ————— 初始化 —————

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await InitializeBrowserAsync();
    }

    /// <summary>
    /// 显式指定用户数据目录（跟随程序数据目录），保证登录 Cookie 便携、可随程序一起搬走。
    /// 任何失败都不向外抛：转为界面提示。
    /// </summary>
    private async Task InitializeBrowserAsync()
    {
        var userDataFolder = Path.Combine(Paths.Data, "WebView2");

        try
        {
            Directory.CreateDirectory(userDataFolder);
            Log.Info($"内嵌浏览器用户数据目录：{userDataFolder}");

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);

            var core = Browser.CoreWebView2;
            if (core is null)
            {
                ShowError(new InvalidOperationException("WebView2 内核未创建成功"));
                return;
            }

            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.SourceChanged += OnSourceChanged;
            core.NavigationCompleted += OnNavigationCompleted;

            BtnBack.IsEnabled = true;
            BtnForward.IsEnabled = true;
            BtnRefresh.IsEnabled = true;

            Log.Info($"内嵌浏览器初始化完成，WebView2 Runtime 版本：{DescribeRuntime()}");
            core.Navigate(_address);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>初始化失败时的兜底：不崩溃，改用系统浏览器。</summary>
    private void ShowError(Exception ex)
    {
        Log.Warn($"内嵌浏览器初始化失败：{ex.Message}");

        LabError.Text = $"内嵌浏览器不可用：{ex.Message}";
        PanError.Visibility = Visibility.Visible;
        Browser.Visibility = Visibility.Collapsed;
    }

    private static string DescribeRuntime()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "未知";
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 WebView2 Runtime 版本失败：{ex.Message}");
            return "未知";
        }
    }

    private void RefreshKeyState()
    {
        if (NexusApi.HasApiKey)
        {
            LabKeyState.Text = "已配置 Key";
            LabKeyState.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            return;
        }

        LabKeyState.Text = "未配置 Key";
        LabKeyState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Warn");
    }

    // ————— 导航拦截 —————

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // nxm:// 只有 Nexus 会发；其它站点即便遇到同形链接也按普通浏览处理
        if (!ShouldTakeOverNxm() || !IsNxm(e.Uri)) return;

        // 关键：取消 WebView2 内的这次导航，改由本进程处理
        e.Cancel = true;

        var uri = e.Uri;
        Dispatcher.BeginInvoke(() => HandleNxmUri(uri, "内部拦截"));
    }

    /// <summary>把新窗口请求改为在本窗口内打开，避免弹出无法控制的窗口。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        // 个别情况下 nxm 链接会走 target=_blank，仅 Nexus 上下文下一并接管
        if (ShouldTakeOverNxm() && IsNxm(e.Uri))
        {
            var uri = e.Uri;
            Dispatcher.BeginInvoke(() => HandleNxmUri(uri, "新窗口拦截"));
            return;
        }

        Browser.CoreWebView2?.Navigate(e.Uri);
    }

    /// <summary>
    /// nxm:// 拦截只对 Nexus 生效：当前站点的下载可被本程序接管（即 Nexus），
    /// 或当前页面的 host 属于 nexusmods.com。其它站点一律保持普通浏览。
    /// </summary>
    private bool ShouldTakeOverNxm()
        => _activeSite.SupportsDirectDownload || ModSiteCatalog.IsNexusHost(Browser.Source?.Host);

    /// <summary>解析 nxm 链接并转交下载确认窗口。始终在 UI 线程执行。</summary>
    private void HandleNxmUri(string uri, string source)
    {
        var link = NxmLink.Parse(uri);

        if (link is null)
        {
            Log.Warn($"内嵌浏览器收到无法解析的 nxm 链接（{source}）");
            var owner = Window.GetWindow(this);
            if (owner is not null)
                Dialogs.Warn(owner, "无法解析该下载链接。", "在线下载 Mod");
            return;
        }

        if (!link.IsStardewValley)
        {
            Log.Info($"内嵌浏览器收到的 nxm 链接不属于星露谷：{link.SafeText}");
            var owner = Window.GetWindow(this);
            if (owner is not null)
                Dialogs.Info(owner, "该链接不是星露谷物语的 Mod。", "在线下载 Mod");
            return;
        }

        // 日志只记不带下载凭证的安全文本
        Log.Info($"内嵌浏览器收到 nxm 链接（{source}）：{link.SafeText}（下载凭证：{(link.HasDownloadToken ? "有" : "无")}）");

        var dialog = new NxmDownloadWindow(link);
        var host = Window.GetWindow(this);
        if (host is not null) dialog.Owner = host;

        dialog.ShowDialog();
    }

    /// <summary>大小写不敏感地判断是否为 nxm:// 链接。</summary>
    private static bool IsNxm(string? uri)
        => !string.IsNullOrWhiteSpace(uri) && uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase);

    // ————— 地址与导航按钮 —————

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        => UpdateAddress(Browser.Source?.ToString());

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        => UpdateAddress(Browser.Source?.ToString());

    private void UpdateAddress(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (IsNxm(uri)) return;

        var changed = !string.Equals(_address, uri, StringComparison.OrdinalIgnoreCase);

        _address = uri;
        LabAddress.Text = uri;
        LabAddress.ToolTip = uri;

        HighlightMatchingSite(uri);

        if (changed) Log.Info($"在线下载窗口当前地址：{uri}");
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not null && Browser.CanGoBack) Browser.GoBack();
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not null && Browser.CanGoForward) Browser.GoForward();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    // ————— 工具栏右侧 —————

    private void OnOpenSystemBrowserClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenUrl(string.IsNullOrWhiteSpace(_address) ? _activeSite.Url : _address);

    private void OnOpenLibraryClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(SettingsStore.Current.ModLibraryDirectory);

    // ————— 释放 —————

    /// <summary>宿主窗口关闭时由宿主调用，释放内嵌浏览器。</summary>
    public void Shutdown()
    {
        try
        {
            Browser.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"释放内嵌浏览器失败：{ex.Message}");
        }
    }
}
