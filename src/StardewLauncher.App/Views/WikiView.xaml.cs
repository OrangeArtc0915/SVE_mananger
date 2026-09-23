using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using StardewLauncher.Core.App;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Views;

/// <summary>
/// 官网 Wiki 的内嵌视图。
///
/// 与樱花面板一样用独立用户数据目录（Data\WebView2-Wiki）：同一份目录不能被两个 WebView2 内核同时占用。
/// 官网在 GitHub Pages 上，国内偶尔抽风，所以右上角保留「用浏览器打开」这条退路。
/// </summary>
public partial class WikiView : UserControl
{
    private bool _initializing;
    private bool _initialized;

    public WikiView()
    {
        InitializeComponent();

        LabAddress.Text = AppInfo.WikiUrl;
        LabAddress.ToolTip = AppInfo.WikiUrl;

        Loaded += OnLoaded;
    }

    /// <summary>首次显示时才创建 WebView2 内核（它很重）；可重复调用，只会真正初始化一次。</summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initializing || _initialized) return;

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
            Log.Warn($"释放 Wiki 视图失败：{ex.Message}");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await EnsureInitializedAsync();
    }

    private async Task InitializeAsync()
    {
        var userDataFolder = Path.Combine(Paths.Data, "WebView2-Wiki");

        try
        {
            Directory.CreateDirectory(userDataFolder);

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
            PanError.Visibility = Visibility.Collapsed;

            core.Navigate(AppInfo.WikiUrl);
            Log.Info($"官网 Wiki 已在内嵌浏览器中打开：{AppInfo.WikiUrl}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    // ————— 导航 —————


    /// <summary>Wiki 里的外链都在本视图内打开，不弹系统浏览器。</summary>
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
        var uri = Browser.Source?.ToString() ?? AppInfo.WikiUrl;

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
            _ = EnsureInitializedAsync();
            return;
        }

        core.Reload();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        PanError.Visibility = Visibility.Collapsed;
        _initialized = false;
        _ = EnsureInitializedAsync();
    }

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenUrl(AppInfo.WikiUrl);

    private void ShowError(Exception ex)
    {
        LabError.Text = $"无法创建内嵌浏览器：{ex.Message}";
        PanError.Visibility = Visibility.Visible;

        Log.Warn($"Wiki 视图初始化失败：{ex.Message}");
    }
}
