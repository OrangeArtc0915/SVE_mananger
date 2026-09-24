using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StardewLauncher.App.Animation;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Pages;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App;

/// <summary>
/// 调试用：设置环境变量 SL_CAPTURE 后，程序会把窗口自身渲染成 PNG 再退出。
/// 用 RenderTargetBitmap 直接渲染视觉树，不受窗口层级与遮挡影响。
/// 设置 SL_OVERLAP_SCAN 时会改为逐宽度、逐页面跑重叠检测并退出。
/// </summary>
internal static class DebugCapture
{
    public static void TryCapture(Window window)
    {
        var path = Environment.GetEnvironmentVariable("SL_CAPTURE");
        if (string.IsNullOrWhiteSpace(path)) return;

        // 默认 3 秒后截图；需要先手动操作界面时可用 SL_CAPTURE_DELAY 延长
        var delaySeconds = 3d;
        var delayText = Environment.GetEnvironmentVariable("SL_CAPTURE_DELAY");
        if (double.TryParse(delayText, out var customDelay) && customDelay > 0) delaySeconds = customDelay;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            try
            {
                window.UpdateLayout();

                Log.Info($"动画引擎：TickCount={AnimationEngine.TickCount}，仍在运行的动画={AnimationEngine.RunningCount}");

                if (AnimationEngine.RunningCount > 0)
                    Log.Warn($"未收尾的动画：{string.Join(", ", AnimationEngine.RunningKeys)}");

                LogInvisibleElements(window);

                var width = (int)Math.Ceiling(window.ActualWidth);
                var height = (int)Math.Ceiling(window.ActualHeight);

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using var stream = File.Create(path);
                encoder.Save(stream);

                Log.Info($"已把窗口自身渲染到 {path}（{width}x{height}）");
            }
            catch (Exception ex)
            {
                Log.Warn($"窗口自渲染失败：{ex.Message}");
            }

            Application.Current.Shutdown();
        };

        timer.Start();
    }

    /// <summary>统计视觉树里被动画留在透明状态的元素，用于确认错峰进入动画是否跑完。</summary>
    private static void LogInvisibleElements(DependencyObject root)
    {
        var invisible = new List<string>();

        void Walk(DependencyObject node)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is not FrameworkElement element) continue;

                if (element.Opacity < 0.99 && element.Visibility == Visibility.Visible)
                {
                    invisible.Add($"{element.GetType().Name}(Opacity={element.Opacity:0.00})");
                    continue;
                }

                Walk(child);
            }
        }

        Walk(root);

        if (invisible.Count == 0)
            Log.Info("视觉树中没有被动画留在透明状态的元素");
        else
            Log.Warn($"有 {invisible.Count} 个元素处于透明状态：{string.Join(", ", invisible.Take(12))}");
    }

    // ———————————————— 重叠检测 ————————————————

    /// <summary>
    /// 设置 SL_OVERLAP_SCAN=宽度列表（默认 1180,1060,940）后，依次把窗口调到各宽度、
    /// 逐个页面切换并跑一遍 <see cref="DetectOverlaps"/>，完成后退出。
    /// 结果全部写进日志，供无人值守的重叠回归检测使用。
    /// </summary>
    [Conditional("DEBUG")]
    public static void TryRunOverlapScan(Window window)
    {
        var spec = Environment.GetEnvironmentVariable("SL_OVERLAP_SCAN");
        if (string.IsNullOrWhiteSpace(spec)) return;

        var widths = spec
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 0)
            .Where(w => w > 0)
            .ToList();

        if (widths.Count == 0) widths = [1180, 1060, 940];

        var pages = new[]
        {
            (Index: NavPages.Launch, Name: "启动"),
            (Index: NavPages.Mod, Name: "Mod 管理"),
            (Index: NavPages.Instance, Name: "游戏实例"),
            (Index: NavPages.Resource, Name: "资源中心"),
            (Index: NavPages.Multiplayer, Name: "联机功能"),
            (Index: NavPages.Toolbox, Name: "工具箱"),
            (Index: NavPages.Setup, Name: "设置"),
            (Index: NavPages.Log, Name: "运行日志")
        };

        // 等界面与数据（Mod 扫描等）稳定下来，再开始扫描
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();

            try
            {
                if (window is MainWindow main)
                {
                    var total = 0;

                    using (AnimationEngine.Suspend())
                    {
                        LogScrolledViewports(window, "扫描前");

                        foreach (var width in widths)
                        {
                            window.Width = width;
                            window.UpdateLayout();
                            await Task.Delay(200);

                            Log.Info($"===== 重叠检测开始：窗口宽度 {width:0} =====");

                            foreach (var (index, name) in pages)
                            {
                                main.SwitchToPage(index);
                                window.UpdateLayout();
                                await Task.Delay(500);

                                // 页面内部的子视图（子标签页）默认是折叠的，WPF 不会给它布局，
                                // 所以必须逐个切出来单独检查，否则那一堆面板等于没测
                                var page = main.CurrentPage;
                                var subViewCount = page?.SubViewCount ?? 1;

                                for (var sub = 0; sub < subViewCount; sub++)
                                {
                                    if (sub > 0)
                                    {
                                        page!.SelectSubView(sub);
                                        window.UpdateLayout();
                                        await Task.Delay(350);
                                    }

                                    var label = subViewCount > 1
                                        ? $"{name}#{sub + 1}@{width:0}"
                                        : $"{name}@{width:0}";

                                    DetectOverlaps(window, label);
                                    total += LastOverlapCount;
                                }
                            }
                        }
                    }

                    Log.Info($"===== 重叠检测结束：共检出 {total} 对重叠 =====");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"重叠检测执行失败：{ex.Message}");
            }

            Application.Current.Shutdown();
        };

        timer.Start();
    }

    /// <summary>
    /// 遍历可视树，收集可见的 <see cref="ButtonBase"/>（含 OutlineButton / RoundIconButton）与
    /// <see cref="TextBlock"/>，取各自相对窗口的边界矩形两两比较。
    /// 只报告没有祖孙关系的两个元素；矩形会先按祖先 ScrollViewer / ClipToBounds 的视口裁剪，
    /// 被完全裁掉（滚出视口）的元素不参与比较；交集面积需超过较小者面积的 15%。
    /// 命中项用 Log.Warn 输出，命中数记录在 <see cref="LastOverlapCount"/>。
    /// </summary>
    [Conditional("DEBUG")]
    public static void DetectOverlaps(Window window, string label)
    {
        window.UpdateLayout();

        LogScrolledViewports(window, label);

        var elements = new List<(FrameworkElement Element, Rect Rect)>();
        CollectCandidates(window, window, elements);

        var hits = 0;

        for (var i = 0; i < elements.Count; i++)
        {
            for (var j = i + 1; j < elements.Count; j++)
            {
                var (a, rectA) = elements[i];
                var (b, rectB) = elements[j];

                if (a.IsAncestorOf(b) || b.IsAncestorOf(a)) continue;

                var intersection = Rect.Intersect(rectA, rectB);
                if (intersection.IsEmpty) continue;

                var area = intersection.Width * intersection.Height;
                var smaller = Math.Min(rectA.Width * rectA.Height, rectB.Width * rectB.Height);
                if (smaller <= 0) continue;

                var ratio = area / smaller;
                if (area < 4 || ratio <= 0.15) continue;

                hits++;
                Log.Warn($"[重叠/{label}] {Describe(a)} {Format(rectA)} × {Describe(b)} {Format(rectB)}" +
                         $" → 交集面积 {area:0}（占较小者 {ratio:P0}）");
            }
        }

        if (hits == 0) Log.Info($"[重叠/{label}] 未检出重叠");

        LastOverlapCount = hits;
    }

    /// <summary>最近一次 <see cref="DetectOverlaps"/> 检出的重叠对数。</summary>
    public static int LastOverlapCount { get; private set; }

    /// <summary>若当前页面存在已滚动（VerticalOffset &gt; 0）的 ScrollViewer，记一条日志便于解释布局位置。</summary>
    private static void LogScrolledViewports(DependencyObject root, string label)
    {
        var scrolled = new List<string>();

        void Walk(DependencyObject node)
        {
            if (node is ScrollViewer { VerticalOffset: > 0.5 } viewer)
            {
                var owner = OwnerName(viewer);
                scrolled.Add($"#{viewer.Name}@{owner} offset={viewer.VerticalOffset:0.#} " +
                             $"viewport={viewer.ViewportHeight:0.#} extent={viewer.ExtentHeight:0.#} " +
                             $"scrollable={viewer.ScrollableHeight:0.#}");
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }

        Walk(root);

        if (scrolled.Count > 0)
            Log.Warn($"[重叠/{label}] 检测到已滚动的 ScrollViewer：{string.Join("；", scrolled)}");
    }

    private static string OwnerName(DependencyObject node)
    {
        var current = VisualTreeHelper.GetParent(node);

        while (current is not null)
        {
            if (current is LauncherPage page) return page.GetType().Name;
            if (current is UserControl userControl) return userControl.GetType().Name;

            current = VisualTreeHelper.GetParent(current);
        }

        return "?";
    }

    private static void CollectCandidates(DependencyObject node, Window window,
        List<(FrameworkElement Element, Rect Rect)> result)
    {
        // 折叠的子树不参与布局，但里面的元素会残留上一次排列的坐标。
        // 顺着可视树往下走时遇到折叠节点就整棵跳过，否则会报出「跨面板」的假重叠
        //（比如子标签切走后，旧面板里的按钮和新面板里的标题被判成重叠）。
        if (node is UIElement { Visibility: Visibility.Collapsed }) return;

        if (node is FrameworkElement { Visibility: Visibility.Visible } element &&
            element is ButtonBase or TextBlock &&
            element.ActualWidth > 0.5 && element.ActualHeight > 0.5)
        {
            var rect = VisibleRect(element, window);
            if (rect is { } visible && visible.Width > 1 && visible.Height > 1)
                result.Add((element, visible));
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            CollectCandidates(VisualTreeHelper.GetChild(node, i), window, result);
    }

    /// <summary>
    /// 元素相对窗口的边界矩形，并依次与所有祖先 ScrollViewer 视口、ClipToBounds 祖先求交。
    /// 返回 null 表示元素已被完全裁剪（不在可见区域内），不应参与重叠比较。
    /// </summary>
    private static Rect? VisibleRect(FrameworkElement element, Window window)
    {
        try
        {
            var rect = element.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            DependencyObject? node = VisualTreeHelper.GetParent(element);

            while (node is not null && !ReferenceEquals(node, window))
            {
                var shouldClip = node is ScrollViewer || node is FrameworkElement { ClipToBounds: true };

                if (shouldClip && node is FrameworkElement host && host.ActualWidth > 0 && host.ActualHeight > 0)
                {
                    var hostRect = host.TransformToAncestor(window)
                        .TransformBounds(new Rect(0, 0, host.ActualWidth, host.ActualHeight));

                    rect = Rect.Intersect(rect, hostRect);
                    if (rect.IsEmpty) return null;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return rect;
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(FrameworkElement element)
    {
        var name = string.IsNullOrWhiteSpace(element.Name) ? "" : "#" + element.Name;
        var text = element switch
        {
            TextBlock block => block.Text,
            ContentControl { Content: string content } => content,
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            text = text.ReplaceLineEndings(" ");
            if (text.Length > 24) text = text[..24] + "…";
            text = $"(\"{text}\")";
        }

        return $"{element.GetType().Name}{name}{text}";
    }

    private static string Format(Rect rect) => $"[x={rect.X:0},y={rect.Y:0} {rect.Width:0}x{rect.Height:0}]";
}
