using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Views;

/// <summary>
/// 樱花FRP 网页版面板的内嵌视图。
///
/// 面板要登录，所以用户数据目录单独一份（Data\WebView2-Sakura）：登录状态留在本机、
/// 可随程序一起搬走，也不会和 Mod 站点浏览器的 WebView2 抢同一个目录（同一份目录不能被两个内核同时占用）。
/// </summary>
public partial class SakuraPanelView : UserControl
{
    /// <summary>官方管理面板的隧道列表地址。</summary>
    public const string PanelUrl = "https://www.natfrp.com/tunnel/";

    private bool _initializing;
    private bool _initialized;
    private bool _failed;

    public SakuraPanelView()
    {
        InitializeComponent();

        LabAddress.Text = PanelUrl;
        LabAddress.ToolTip = PanelUrl;

        Loaded += OnLoaded;
    }

    /// <summary>首次显示时才创建 WebView2 内核（它很重）；可重复调用，只会真正初始化一次。</summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initializing || _initialized || _failed) return;

        _initializing = true;
        try
        {
            await InitializeAsync();
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>宿主窗口关闭时释放内核。</summary>
    public void Shutdown()
    {
        try
        {
            Browser.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"释放樱花FRP 面板失败：{ex.Message}");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await EnsureInitializedAsync();
    }

    private async Task InitializeAsync()
    {
        var userDataFolder = Path.Combine(Paths.Data, "WebView2-Sakura");

        try
        {
            Directory.CreateDirectory(userDataFolder);
            Log.Info($"樱花FRP 面板用户数据目录：{userDataFolder}");

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);

            var core = Browser.CoreWebView2;
            if (core is null)
            {
                ShowError(new InvalidOperationException("WebView2 内核未创建成功"));
                return;
            }

            core.SourceChanged += OnSourceChanged;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;

            BtnBack.IsEnabled = true;
            BtnForward.IsEnabled = true;
            BtnRefresh.IsEnabled = true;

            _initialized = true;

            core.Navigate(PanelUrl);
            Log.Info("樱花FRP 面板已在内嵌浏览器中打开");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    // ————— 导航 —————

    /// <summary>面板里的外链（帮助文档、下载页等）都在本视图内打开，不弹系统浏览器。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (!string.IsNullOrWhiteSpace(e.Uri)) Browser.CoreWebView2?.Navigate(e.Uri);
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e) => RefreshAddress();

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        RefreshAddress();
        RefreshNavState();
    }

    private void RefreshAddress()
    {
        var uri = Browser.Source?.ToString() ?? PanelUrl;

        LabAddress.Text = uri;
        LabAddress.ToolTip = uri;
    }

    private void RefreshNavState()
    {
        var core = Browser.CoreWebView2;
        if (core is null) return;

        BtnBack.IsEnabled = core.CanGoBack;
        BtnForward.IsEnabled = core.CanGoForward;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack();
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var core = Browser.CoreWebView2;
        if (core is null)
        {
            _failed = false;
            _ = EnsureInitializedAsync();
            return;
        }

        core.Reload();
    }

    private void ShowError(Exception ex)
    {
        _failed = true;

        LabError.Text = $"无法创建内嵌浏览器：{ex.Message}";
        PanError.Visibility = Visibility.Visible;

        Log.Warn($"樱花FRP 面板初始化失败：{ex.Message}");
    }
}
