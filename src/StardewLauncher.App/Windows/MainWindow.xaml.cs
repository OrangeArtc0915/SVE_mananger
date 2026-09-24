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
    private const double EnterOffsetY = 14;

    /// <summary>内容块入场时由略小放大到实际大小，收尾要有"浮上来"的感觉。</summary>
    private const double EnterScaleFrom = 0.985;
    private const double EnterDurationMs = 300;

    /// <summary>切页时新页从侧面滑入的距离（旧页往相反方向退出，形成方向感）。</summary>
    private const double PageSlideX = 22;

    /// <summary>开场动画：整个面板从略小放大到实际大小，同时侧栏逐项滑入。</summary>
    private const double OpenScaleFrom = 0.965;
    private const double OpenDurationMs = 360;
    private const double OpenSlideMs = 320;
    private const double NavIntroStepMs = 30;
    private const double NavIntroDelayMs = 70;

    /// <summary>
    /// 侧栏导航项：id 用于读写设置，顺序即默认顺序。
    /// 设置页的「侧栏导航」卡片也读这张表，加导航项时这里和 XAML 一起加。
    /// </summary>
    internal static readonly (string Id, string Name)[] NavItemDefs =
    [
        ("launch", "启动"),
        ("mod", "Mod 管理"),
        ("instance", "游戏实例"),
        ("resource", "资源中心"),
        ("multiplayer", "联机功能"),
        ("toolbox", "工具箱"),
        ("setup", "设置"),
        ("log", "运行日志")
    ];

    /// <summary>页面缓存：同一页面在会话内只创建一次。</summary>
    private readonly Dictionary<int, LauncherPage> _pages = new();

    private readonly bool _ready;
    private int _currentPage = -1;
    private bool _glassEnabled;
    private bool _suppressNavCheck;

    /// <summary>页面错峰动画的整体延迟。开场那一下用它把内容入场推到面板放大之后。</summary>
    private double _staggerDelayOffsetMs;

    public MainWindow()
    {
        InitializeComponent();
        _ready = true;

        // 图标是资源引用，写错只会静默不显示；这里记一条自检日志，便于确认真的加载到了
        Log.Info($"主窗口图标：{(Icon is null ? "未设置" : $"{Icon.Width:0}×{Icon.Height:0}")}");

        PanBack.SizeChanged += (_, e) => RectForm.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        Loaded += (_, _) =>
        {
            ApplyNavLayout();

            // 开场：面板由小放大淡入、侧栏逐项滑入；内容错峰整体推迟到开场之后，
            // 两层叠在一起会糊成一团，看不出层次
            RunOpenAnimation();

            _staggerDelayOffsetMs = OpenDurationMs * 0.45;
            SwitchToPage(NavPages.Launch);
            _staggerDelayOffsetMs = 0;

            ApplyBackground();

            // 主题变了要重铺一次：压暗层在深色主题用黑、浅色主题用白
            ThemeService.ThemeChanged += ApplyBackground;
#if DEBUG
            DebugCapture.TryRunOverlapScan(this);
            DebugCapture.TryCapture(this);
#endif
        };

        Closed += (_, _) => ThemeService.ThemeChanged -= ApplyBackground;

        // 最小化时停掉视频与动图，别白烧 CPU；环境动效（呼吸光、缓慢推拉）一并叫停
        StateChanged += (_, _) =>
        {
            var minimized = WindowState == WindowState.Minimized;
            BackgroundView.SetPaused(minimized);
            AnimationEngine.SetAmbientEnabled(!minimized);
        };
    }

    /// <summary>
    /// 开场动画：整个面板从略小放大到实际大小并淡入，标题栏从上方落下，侧栏导航逐项滑入。
    /// 缩放刻意不过冲——窗口外圈是自绘投影的留白，过冲会闪出桌面。
    /// </summary>
    private void RunOpenAnimation()
    {
        if (!AnimationEngine.IsEnabled)
        {
            PanBack.Opacity = 1;
            return;
        }

        PanBack.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(OpenScaleFrom, OpenScaleFrom);
        PanBack.RenderTransform = scale;

        AnimationEngine.Start("open:opacity", 0, 1, OpenDurationMs * 0.7, Ease.OutFluent,
            v => PanBack.Opacity = v);

        AnimationEngine.Start("open:scale", OpenScaleFrom, 1, OpenDurationMs, t => Ease.OutFluent(t, 4),
            v =>
            {
                scale.ScaleX = v;
                scale.ScaleY = v;
            });

        var titleOffset = new TranslateTransform();
        PanTitle.RenderTransform = titleOffset;
        AnimationEngine.TranslateY(titleOffset, 0, OpenSlideMs, Ease.OutBack);

        RunNavIntro();
    }

    /// <summary>侧栏导航逐项从左侧滑入。只在开场跑一次，设置页重排导航时不重复播。</summary>
    private void RunNavIntro()
    {
        var index = 0;

        foreach (var child in PanNav.Children)
        {
            if (child is not NavItem item) continue;

            var delay = NavIntroDelayMs + index * NavIntroStepMs;
            var offset = new TranslateTransform(-16, 0);

            item.RenderTransform = offset;
            item.Opacity = 0.2;

            AnimationEngine.Start($"navIntro:{item.GetHashCode()}", 0, 1, OpenSlideMs, Ease.OutBack, v =>
            {
                offset.X = -16 * (1 - v);
                item.Opacity = 0.2 + 0.8 * Math.Min(1, v);
            }, delayMs: delay);

            index++;
        }
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

    /// <summary>切换到指定页面。页面会缓存，切换时做带方向感的交接 + 内容错峰进入。</summary>
    public void SwitchToPage(int page)
    {
        if (_currentPage == page) return;

        var target = GetPage(page);
        var previous = PanContent.Child as LauncherPage;

        // 侧栏里更靠下的页面从右边进、更靠上的从左边进，方向与点导航的手感一致
        var direction = TransitionDirection(_currentPage, page);

        void Swap()
        {
            previous?.OnLeave();
            PanContent.Child = target;

            // 页面容器本身必须设为不透明，淡入交给下面的错峰动画逐块完成
            target.Opacity = 1;
            target.OnEnter();

            // 页面整体从侧面滑到位；块级错峰只负责上移与淡入，两者分工不重叠
            var slide = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
            target.RenderTransform = slide;
            slide.X = PageSlideX * direction;
            AnimationEngine.TranslateX(slide, 0, 320, Ease.OutFluent);

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

        // 旧页往相反方向退出，和进来的新页形成一次左右交接
        var offset = previous.RenderTransform as TranslateTransform ?? new TranslateTransform();
        previous.RenderTransform = offset;

        AnimationEngine.Opacity(previous, 0, 130, Ease.OutFluent, Swap);
        AnimationEngine.TranslateX(offset, -PageSlideX * direction, 150, Ease.OutFluent);
    }

    /// <summary>方向感：1 表示新页在侧栏里更靠下（从右侧进），-1 表示更靠上。</summary>
    private int TransitionDirection(int fromPage, int toPage)
    {
        var from = VisualRailIndex(fromPage);
        var to = VisualRailIndex(toPage);

        if (from < 0 || to < 0 || from == to) return 1;

        return to > from ? 1 : -1;
    }

    /// <summary>页面在侧栏里的实际位置（顺序与显隐都能在设置页改）。找不到返回 -1。</summary>
    private int VisualRailIndex(int page)
    {
        for (var i = 0; i < PanNav.Children.Count; i++)
        {
            if (PanNav.Children[i] is NavItem { Tag: string tag } &&
                int.TryParse(tag, out var value) && value == page)
            {
                return i;
            }
        }

        return -1;
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

    // ————— 侧栏导航的顺序与显隐 —————

    /// <summary>按设置重排 / 隐藏侧栏导航项。设置页改完会调它。</summary>
    internal void ApplyNavLayout()
    {
        var settings = CoreApp.SettingsStore.Current;
        var order = NormalizeNavOrder(settings.NavOrder);
        var hidden = new HashSet<string>(settings.NavHidden ?? [], StringComparer.OrdinalIgnoreCase);

        var items = new List<NavItem>(order.Count);

        foreach (var id in order)
        {
            if (NavElementOf(id) is not { } item) continue;

            item.Visibility = hidden.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
            items.Add(item);
        }

        // 导航项在 XAML 里声明、字段持有引用，这里只负责重新挂载与显隐
        PanNav.Children.Clear();
        foreach (var item in items) PanNav.Children.Add(item);

        Log.Info($"侧栏导航已排布：显示 {items.Count(item => item.Visibility == Visibility.Visible)}/{items.Count} 项");
    }

    /// <summary>补全顺序：不认识的 id 丢掉，没提到的按默认顺序接在后面。</summary>
    internal static List<string> NormalizeNavOrder(IEnumerable<string>? saved)
    {
        var defaults = NavItemDefs.Select(def => def.Id).ToList();
        var order = new List<string>(defaults.Count);

        foreach (var id in saved ?? [])
        {
            if (!defaults.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            if (order.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;

            order.Add(id);
        }

        foreach (var id in defaults)
        {
            if (!order.Contains(id, StringComparer.OrdinalIgnoreCase)) order.Add(id);
        }

        return order;
    }

    private NavItem? NavElementOf(string id) => id switch
    {
        "launch" => NavLaunch,
        "mod" => NavMod,
        "instance" => NavInstance,
        "resource" => NavResource,
        "multiplayer" => NavMultiplayer,
        "toolbox" => NavToolbox,
        "setup" => NavSetup,
        "log" => NavLog,
        _ => null
    };

    // ————— 页面进入动画 —————

    /// <summary>页面内容错峰进入：逐块淡入、从下方浮起并轻微放大，拉开层次。</summary>
    private void RunEnterAnimation(LauncherPage page)
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

            // 页面内的块都归这里管，模板里没有别处给它设过变换，所以直接换成新的变换组
            var offset = new TranslateTransform(0, EnterOffsetY);
            var scale = new ScaleTransform(EnterScaleFrom, EnterScaleFrom);

            target.RenderTransformOrigin = new Point(0.5, 0.5);
            target.RenderTransform = new TransformGroup { Children = { scale, offset } };
            target.Opacity = 0;

            var delay = _staggerDelayOffsetMs + Math.Min(index * StaggerStepMs, StaggerMaxDelayMs);

            AnimationEngine.Start($"enter:{target.GetHashCode()}", 0, 1, EnterDurationMs, Ease.OutFluent, v =>
            {
                target.Opacity = Math.Min(1, v);
                offset.Y = EnterOffsetY * (1 - v);

                var size = EnterScaleFrom + (1 - EnterScaleFrom) * v;
                scale.ScaleX = size;
                scale.ScaleY = size;
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

    // ————— 背景视差 —————
    // 挂在 PanForm 上：它铺满整块窗口并带背景，鼠标在整个窗口里移动都能收到。

    private void OnPointerMoved(object sender, MouseEventArgs e)
    {
        var width = PanForm.ActualWidth;
        var height = PanForm.ActualHeight;
        if (width <= 1 || height <= 1) return;

        // 归一化到 -1~1，窗口中心是 0；背景朝反方向移动
        var point = e.GetPosition(PanForm);
        BackgroundView.SetPointer((point.X / width - 0.5) * 2, (point.Y / height - 0.5) * 2);
    }

    private void OnPointerLeft(object sender, MouseEventArgs e) => BackgroundView.SetPointer(0, 0);

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
