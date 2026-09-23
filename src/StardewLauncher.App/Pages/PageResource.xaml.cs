using System.Windows;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Views;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Pages;

/// <summary>
/// 资源中心页：只留两块——安装 SMAPI 与内嵌浏览各 Mod 站点，
/// Mod 管理页只保留一个跳转入口。
/// </summary>
public partial class PageResource : LauncherPage
{
    private readonly OutlineButton[] _tabs;
    private readonly UIElement[] _panels;

    private NexusBrowserView? _browserView;
    private bool _hostHooked;

    public PageResource()
    {
        InitializeComponent();

        _tabs = [BtnTabSmapi, BtnTabBrowse];
        _panels = [ViewSmapi, PanBrowse];

        SwitchTab(0);

        Loaded += OnLoaded;
    }

    // ————— 子标签 —————

    /// <summary>自检用：两个分区里有一个是折叠的，不切出来查不出布局问题。</summary>
    public override int SubViewCount => _panels.Length;

    /// <summary>自检用：不要在这里创建内嵌浏览器，那会额外拉起 WebView2 进程。</summary>
    public override void SelectSubView(int index) => SwitchTab(index, createBrowser: false);

    private void SwitchTab(int index, bool createBrowser = true)
    {
        for (var i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].Tone = i == index ? ButtonTone.Solid : ButtonTone.Outline;
            _panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        if (index == 1 && createBrowser) EnsureBrowserView();
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (int.TryParse(tag, out var index)) SwitchTab(index);
    }

    /// <summary>内嵌浏览器只在第一次切到「在线浏览」时创建并初始化。</summary>
    private void EnsureBrowserView()
    {
        if (_browserView is not null) return;

        _browserView = new NexusBrowserView();
        PanBrowse.Children.Add(_browserView);

        Log.Info("资源中心：已创建内嵌浏览器");
    }

    // ————— 生命周期 —————

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hostHooked) return;

        var host = Window.GetWindow(this);
        if (host is null) return;

        _hostHooked = true;
        host.Closed += OnHostClosed;
    }

    /// <summary>宿主窗口关闭时释放各子视图持有的资源（SMAPI 安装任务、内嵌浏览器）。</summary>
    private void OnHostClosed(object? sender, EventArgs e)
    {
        ViewSmapi.Shutdown();
        _browserView?.Shutdown();
    }
}
