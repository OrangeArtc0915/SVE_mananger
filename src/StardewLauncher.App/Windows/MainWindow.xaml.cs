using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using StardewLauncher.App.Animation;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Interop;
using StardewLauncher.App.Pages;
using StardewLauncher.App.Theme;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using CoreApp = StardewLauncher.Core.App;

namespace StardewLauncher.App.Windows;

public partial class MainWindow : Window
{
    /// <summary>进入页面时每个内容块的错峰间隔与最大延迟。</summary>
    private const double StaggerStepMs = 22;
    private const double StaggerMaxDelayMs = 220;
    private const double EnterOffsetY = 16;

    /// <summary>页面缓存：同一页面在会话内只创建一次。</summary>
    private readonly Dictionary<int, LauncherPage> _pages = new();

    private readonly bool _ready;
    private int _currentPage = -1;
    private bool _glassEnabled;
    private bool _suppressNavCheck;

    public MainWindow()
    {
        InitializeComponent();
        _ready = true;

        // 图标是资源引用，写错只会静默不显示；这里记一条自检日志，便于确认真的加载到了
        Log.Info($"主窗口图标：{(Icon is null ? "未设置" : $"{Icon.Width:0}×{Icon.Height:0}")}");

        PanBack.SizeChanged += (_, e) => RectForm.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        Loaded += (_, _) =>
        {
            SwitchToPage(NavPages.Launch);
            ApplyBackground();

            // 主题变了要重铺一次：压暗层在深色主题用黑、浅色主题用白
            ThemeService.ThemeChanged += ApplyBackground;
#if DEBUG
            DebugCapture.TryRunOverlapScan(this);
            DebugCapture.TryCapture(this);
#endif
        };

        Closed += (_, _) => ThemeService.ThemeChanged -= ApplyBackground;

        // 最小化时停掉视频与动图，别白烧 CPU
        StateChanged += (_, _) => BackgroundView.SetPaused(WindowState == WindowState.Minimized);
    }

    /// <summary>按设置铺内容区背景。设置页改完直接调这里，不用重启。</summary>
    internal void ApplyBackground()
    {
        var settings = CoreApp.SettingsStore.Current;

        BackgroundView.Apply(
            settings.BackgroundKind,
            settings.BackgroundFile,
            settings.BackgroundFit,
            settings.BackgroundDim);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        _glassEnabled = WindowInterop.TryExtendFrameIntoClientArea(handle);

        if (_glassEnabled)
        {
            // 渲染层允许 Alpha 通道通过，外圈留白才能透出桌面并显示自绘投影
            var source = HwndSource.FromHwnd(handle);
            if (source is not null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Log.Info("窗口已启用玻璃化，边缘投影正常显示");
        }
        else
        {
            // 无法玻璃化时退化为纯色窗口并去掉留白，避免出现黑边
            PanBack.Margin = new Thickness(0);
            RootGrid.SetResourceReference(Panel.BackgroundProperty, "Surface.Window");
            Log.Warn("系统未启用 DWM 合成，窗口退化为纯色模式");
        }
    }

    // ————— 导航 —————

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressNavCheck) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var page)) return;

        SwitchToPage(page);
    }

    /// <summary>切换到指定页面。页面会缓存，切换时做淡出 + 错峰进入动画。</summary>
    public void SwitchToPage(int page)
    {
        if (_currentPage == page) return;

        var target = GetPage(page);
        var previous = PanContent.Child as LauncherPage;

        void Swap()
        {
            previous?.OnLeave();
            PanContent.Child = target;

            // 页面容器本身必须设为不透明，淡入交给下面的错峰动画逐块完成
            target.Opacity = 1;
            target.OnEnter();
            RunEnterAnimation(target);
        }

        _currentPage = page;
        SyncNavSelection(page);
        Log.Info($"切换到页面 {page}");

        if (previous is null || ReferenceEquals(previous, target) || !AnimationEngine.IsEnabled)
        {
            Swap();
            return;
        }

        AnimationEngine.Opacity(previous, 0, 90, Ease.OutFluent, Swap);
    }

    /// <summary>当前显示的页面。给自检用。</summary>
    internal LauncherPage? CurrentPage => PanContent.Child as LauncherPage;

    private LauncherPage GetPage(int page)
    {
        if (_pages.TryGetValue(page, out var cached)) return cached;

        LauncherPage created = page switch
        {
            NavPages.Mod => new PageMod(),
            NavPages.Instance => new PageInstance(),
            NavPages.Resource => new PageResource(),
            NavPages.Multiplayer => new PageMultiplayer(),
            NavPages.Setup => new PageSetup(),
            NavPages.Log => new PageLog(),
            NavPages.Toolbox => new PageToolbox(),
            _ => new PageLaunch()
        };

        _pages[page] = created;
        return created;
    }

    /// <summary>把侧栏选中态同步到当前页面。</summary>
    private void SyncNavSelection(int page)
    {
        _suppressNavCheck = true;

        NavLaunch.IsChecked = page == NavPages.Launch;
        NavMod.IsChecked = page == NavPages.Mod;
        NavInstance.IsChecked = page == NavPages.Instance;
        NavResource.IsChecked = page == NavPages.Resource;
        NavMultiplayer.IsChecked = page == NavPages.Multiplayer;
        NavToolbox.IsChecked = page == NavPages.Toolbox;
        NavSetup.IsChecked = page == NavPages.Setup;
        NavLog.IsChecked = page == NavPages.Log;

        _suppressNavCheck = false;
    }

    // ————— 页面进入动画 —————

    /// <summary>页面内容错峰进入：逐块淡入并轻微上移。</summary>
    private static void RunEnterAnimation(LauncherPage page)
    {
        var host = FindStaggerHost(page.Content);
        var targets = host is not null
            ? host.Children.OfType<UIElement>().ToList()
            : new List<UIElement> { page };

        if (!AnimationEngine.IsEnabled)
        {
            foreach (var element in targets) element.Opacity = 1;
            return;
        }

        var index = 0;
        foreach (var element in targets)
        {
            if (element is not FrameworkElement target) continue;

            var offset = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
            target.RenderTransform = offset;
            target.Opacity = 0;

            var delay = Math.Min(index * StaggerStepMs, StaggerMaxDelayMs);

            AnimationEngine.Start($"enter:{target.GetHashCode()}", 0, 1, 280, Ease.OutFluent, v =>
            {
                target.Opacity = v;
                offset.Y = EnterOffsetY * (1 - v);
            }, delayMs: delay);

            index++;
        }
    }

    /// <summary>沿单一子级链路向下找到第一个真正承载多个内容块的面板。</summary>
    private static Panel? FindStaggerHost(object? content)
    {
        while (content is not null)
        {
            if (content is Panel panel) return panel;

            content = content switch
            {
                ScrollViewer scrollViewer => scrollViewer.Content,
                Border border => border.Child,
                ContentControl contentControl => contentControl.Content,
                Decorator decorator => decorator.Child,
                _ => null
            };
        }

        return null;
    }

    // ————— 标题栏按钮 —————

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        try { DragMove(); }
        catch { /* 鼠标已释放时 DragMove 会抛异常，忽略即可 */ }
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null;
        }

        return false;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ————— 拖拽安装 —————
    // 挂在 PanForm 上：窗口里只有这一层铺满且带背景（Background 为 null 的元素不参与命中测试，
    // 挂到 RootGrid / PanContent 上会收不到 Drop）。事件冒泡，子元素不处理就落到这里。

    private void OnGlobalDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnGlobalDrop(object sender, DragEventArgs e)
    {
        var paths = TryGetDroppedPaths(e);
        if (paths.Count == 0) return;

        e.Handled = true;

        // 切到 Mod 页，导入的格式校验、进度与结果汇总都由那一页负责，这里不重复实现
        SwitchToPage(NavPages.Mod);

        if (GetPage(NavPages.Mod) is PageMod page)
        {
            Log.Info($"拖入 {paths.Count} 个文件，交给 Mod 页导入");
            _ = page.ImportArchivesAsync(paths);
        }
    }

    private static IReadOnlyList<string> TryGetDroppedPaths(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return [];
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return [];

        return paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>标题栏问号按钮：在默认浏览器里打开项目 GitHub 仓库。</summary>
    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        Log.Info($"打开 GitHub 仓库：{CoreApp.AppInfo.GitHubUrl}");
        ShellHelper.OpenUrl(CoreApp.AppInfo.GitHubUrl);
    }

    /// <summary>
    /// 左侧导航「关于」：打开独立窗口。
    /// 该项单独分组，不影响主页面选中态；点击后主动取消自身选中，避免留下高亮。
    /// </summary>
    private void OnAboutNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is NavItem item) item.IsChecked = false;

        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    /// <summary>侧栏「使用手册」：在启动器内嵌的浏览器里打开官网 Wiki。</summary>
    private void OnWikiNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is NavItem item) item.IsChecked = false;

        var wiki = new WikiWindow { Owner = this };
        wiki.Show();
    }
}
