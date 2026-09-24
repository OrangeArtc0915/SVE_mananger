using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StardewLauncher.App.Animation;
using StardewLauncher.App.Theme;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Appearance;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Views;

/// <summary>
/// 窗口内容区的个性化背景层：支持静态图、动图（GIF 逐帧）、视频（静音循环）。
///
/// 自己解码 GIF 而不是引第三方库：WPF 的 GifBitmapDecoder 已经能拿到每一帧和帧延时，
/// 配上 DispatcherTimer 就是完整的播放器，不值得为一个背景功能加依赖。
/// 视频用 MediaElement（走系统自带解码器，支持 mp4；WebM 不支持）。
/// </summary>
public partial class BackgroundLayer : UserControl
{
    /// <summary>背景的基础缩放：大于 1 才留出视差平移的余量，平移时不会露出黑边。</summary>
    private const double DriftBaseZoom = 1.04;
    private const double DriftMaxZoom = 1.09;
    private const double DriftHalfCycleMs = 11000;

    /// <summary>鼠标视差的最大位移（像素）。基础缩放要能盖住它。</summary>
    private const double ParallaxMax = 7;

    private const string DriftKey = "background";

    private readonly DispatcherTimer _gifTimer = new();

    private List<(BitmapSource Frame, TimeSpan Delay)> _gifFrames = [];

    private DispatcherTimer? _videoProbe;
    private int _gifIndex;
    private int _loadVersion;
    private bool _paused;
    private bool _hasContent;
    private double _pointerX;
    private double _pointerY;

    public BackgroundLayer()
    {
        InitializeComponent();

        _gifTimer.Tick += OnGifTick;
    }

    /// <summary>
    /// 按设置铺背景。类型为 None、路径为空或文件不存在时，本层完全透明，露出主题渐变。
    /// </summary>
    public void Apply(BackgroundKind kind, string file, BackgroundFit fit, int dimPercent)
    {
        Reset();

        if (kind == BackgroundKind.None || string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Log.Info("背景：使用主题渐变");
            return;
        }

        var stretch = fit switch
        {
            BackgroundFit.Contain => Stretch.Uniform,
            BackgroundFit.Fill => Stretch.Fill,
            _ => Stretch.UniformToFill
        };

        ImgBackground.Stretch = stretch;
        VidBackground.Stretch = stretch;

        // 背景是铺在「页面标题」这类裸文字下面的，所以必须压一层：
        // 深色主题压黑、浅色主题压白，才不会让标题糊在图上。
        // 压暗量做成淡入，换背景时不会"啪"地一下变暗；
        // 淡的是画刷自己的不透明度，元素本身保持不透明——否则自检会把压暗层当成"卡在透明态的元素"。
        var overlay = ThemeService.IsDark ? Colors.Black : Colors.White;
        var dim = Math.Clamp(dimPercent, 0, 80) / 100d;
        var dimBrush = new SolidColorBrush(overlay);

        OverlayDim.Background = dimBrush;

        if (AnimationEngine.IsEnabled)
        {
            dimBrush.Opacity = 0;
            AnimationEngine.Start("background:dim", 0, dim, 420, Ease.OutFluent, v => dimBrush.Opacity = v);
        }
        else
        {
            dimBrush.Opacity = dim;
        }

        _hasContent = true;

        try
        {
            switch (kind)
            {
                case BackgroundKind.Video:
                    // 视频本身就在动，再叠推拉会晕
                    PrepareDrift(drifting: false);
                    StartVideo(file);
                    break;

                case BackgroundKind.Gif:
                    PrepareDrift(drifting: false);
                    LoadGif(file);
                    break;

                default:
                    PrepareDrift(drifting: true);

                    ImgBackground.Source = LoadFrozen(file);
                    ImgBackground.Visibility = Visibility.Visible;

                    Log.Info($"背景：图片 {Path.GetFileName(file)}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"背景加载失败，回退主题渐变：{ex.Message}");
            Reset();
        }
    }

    /// <summary>
    /// 鼠标视差：背景朝指针反方向轻移，做出景深。入参是相对内容区的 -1~1 归一化坐标。
    /// 指针只挪了一点点就跳过，免得白白重起动画。
    /// </summary>
    public void SetPointer(double x, double y)
    {
        if (!_hasContent) return;
        if (Math.Abs(x - _pointerX) < 0.05 && Math.Abs(y - _pointerY) < 0.05) return;

        _pointerX = x;
        _pointerY = y;

        AnimationEngine.TranslateX(DriftShift, -x * ParallaxMax, 220, Ease.OutFluent);
        AnimationEngine.TranslateY(DriftShift, -y * ParallaxMax, 220, Ease.OutFluent);
    }

    /// <summary>
    /// 摆好推拉的初始状态。drifting 为 true 时缓慢往复缩放，否则只固定在基础缩放上
    /// （动图和视频自己就在动，再叠推拉会显得很吵）。
    /// </summary>
    private void PrepareDrift(bool drifting)
    {
        DriftZoom.ScaleX = DriftBaseZoom;
        DriftZoom.ScaleY = DriftBaseZoom;
        DriftShift.X = 0;
        DriftShift.Y = 0;

        if (!drifting)
        {
            AnimationEngine.StopAmbient(DriftKey);
            return;
        }

        AnimationEngine.StartAmbient(PanDrift, DriftKey, DriftBaseZoom, DriftMaxZoom,
            DriftHalfCycleMs, Ease.InOutFluent, v =>
            {
                DriftZoom.ScaleX = v;
                DriftZoom.ScaleY = v;
            });
    }

    /// <summary>窗口最小化时停掉动图与视频，别白烧 CPU。</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;

        if (VidBackground.Visibility == Visibility.Visible)
        {
            if (paused) VidBackground.Pause();
            else VidBackground.Play();
        }

        if (paused)
        {
            _gifTimer.Stop();
        }
        else
        {
            ScheduleNextGifFrame();
        }
    }

    // ————— 动图 —————

    /// <summary>解码放后台线程，几十帧的图在主线程解会卡一下。</summary>
    private async void LoadGif(string file)
    {
        var version = ++_loadVersion;

        try
        {
            var frames = await Task.Run(() => DecodeGif(file));

            // 期间用户可能又换了背景，丢弃过期结果
            if (version != _loadVersion || frames.Count == 0) return;

            _gifFrames = frames;
            _gifIndex = 0;

            ImgBackground.Source = frames[0].Frame;
            ImgBackground.Visibility = Visibility.Visible;

            ScheduleNextGifFrame();

            Log.Info($"背景：动图 {Path.GetFileName(file)}（{frames.Count} 帧）");
        }
        catch (Exception ex)
        {
            Log.Warn($"动图背景加载失败：{ex.Message}");

            if (version == _loadVersion) Reset();
        }
    }

    private static List<(BitmapSource Frame, TimeSpan Delay)> DecodeGif(string file)
    {
        var decoder = new GifBitmapDecoder(
            new Uri(file),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var frames = new List<(BitmapSource Frame, TimeSpan Delay)>();

        foreach (var frame in decoder.Frames)
        {
            // 帧要冻结才能跨线程给界面用
            if (frame.CanFreeze) frame.Freeze();

            frames.Add((frame, ReadFrameDelay(frame)));
        }

        return frames;
    }

    private static TimeSpan ReadFrameDelay(BitmapFrame frame)
    {
        // GIF 的帧延时存在 Graphic Control Extension 里，单位是 1/100 秒
        const int fallback = 100;

        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay"))
            {
                var raw = metadata.GetQuery("/grctlext/Delay");

                // 0 和 1（<20ms）在绝大多数浏览器里都被当成「太快了」，按 100ms 处理
                if (raw is ushort hundredths && hundredths >= 2)
                    return TimeSpan.FromMilliseconds(hundredths * 10);
            }
        }
        catch
        {
            // 元数据读不到就用默认值，不值得为此报错
        }

        return TimeSpan.FromMilliseconds(fallback);
    }

    private void ScheduleNextGifFrame()
    {
        if (_paused || _gifFrames.Count <= 1) return;

        _gifTimer.Interval = _gifFrames[_gifIndex].Delay;
        _gifTimer.Start();
    }

    private void OnGifTick(object? sender, EventArgs e)
    {
        _gifTimer.Stop();

        if (_paused || _gifFrames.Count == 0) return;

        _gifIndex = (_gifIndex + 1) % _gifFrames.Count;
        ImgBackground.Source = _gifFrames[_gifIndex].Frame;

        ScheduleNextGifFrame();
    }

    // ————— 视频 —————

    /// <summary>
    /// 起播。这里刻意<b>不</b>设置 Position、也<b>不</b>调 Play：
    /// LoadedBehavior 是 Manual，媒体还没 Open 时调 Play 会被丢掉，画面就一直黑着。
    /// 真正的播放放到 MediaOpened 里做。
    /// </summary>
    private void StartVideo(string file)
    {
        VidBackground.Visibility = Visibility.Visible;
        VidBackground.Source = new Uri(file);

        Log.Info($"背景：视频 {Path.GetFileName(file)}，等待解码…");
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        Log.Info($"背景视频已解码：{VidBackground.NaturalVideoWidth}x{VidBackground.NaturalVideoHeight}，" +
                 $"时长 {VidBackground.NaturalDuration.TimeSpan.TotalSeconds:0.0} 秒");

        try
        {
            VidBackground.Volume = 0;
            VidBackground.Position = TimeSpan.Zero;
            VidBackground.Play();
        }
        catch (Exception ex)
        {
            Log.Warn($"背景视频起播失败：{ex.Message}");
            return;
        }

        // 起播几秒后回看一眼位置有没有推进：解码成功但位置不动，说明画面其实没出来
        _videoProbe ??= CreateVideoProbe();
        _videoProbe.Stop();
        _videoProbe.Start();
    }

    private DispatcherTimer CreateVideoProbe()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (VidBackground.Visibility != Visibility.Visible) return;

            Log.Info($"背景视频状态：播放位置 {VidBackground.Position.TotalSeconds:0.00}s / " +
                     $"{VidBackground.NaturalDuration.TimeSpan.TotalSeconds:0.00}s（位置一直不推进就是只有黑屏）");
        };

        return timer;
    }

    /// <summary>
    /// 解不开就把视频层收掉：宁可露出主题渐变，也不要给用户留一块黑屏。
    /// </summary>
    private void OnVideoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // 没设视频背景时 MediaElement 也会报一次"解不开"（此时它根本没有源），
        // 那不是问题，记一条普通日志就行，别让它污染日志、也别让设置页显示假警告。
        if (VidBackground.Source is null)
        {
            Log.Info($"背景视频层未启用（{e.ErrorException?.Message ?? "没有媒体源"}）");
            return;
        }

        Log.Warn($"背景视频无法解码（{e.ErrorException?.Message ?? "未知原因"}），已回退为无背景。" +
                 "常见原因是系统不支持该视频编码（HEVC / AV1 需要额外装解码器）");

        // 让设置页能把原因告诉用户，不然他只看到"背景没了"
        BackgroundService.ReportMediaError("这个视频系统解不开（HEVC / AV1 需要额外安装解码器），已退回主题渐变");

        _videoProbe?.Stop();

        VidBackground.Visibility = Visibility.Collapsed;
        VidBackground.Source = null;

        OverlayDim.Background = Brushes.Transparent;
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        // 背景视频要无缝循环，播完回到开头
        try
        {
            VidBackground.Position = TimeSpan.Zero;
            VidBackground.Play();
        }
        catch (Exception ex)
        {
            Log.Warn($"背景视频循环播放失败：{ex.Message}");
        }
    }

    // ————— 内部 —————

    /// <summary>解码成冻结的位图：避免 WPF 一直占着原文件句柄，用户也就删得掉自己的原图。</summary>
    private static BitmapSource LoadFrozen(string file)
    {
        var image = new BitmapImage();

        image.BeginInit();
        image.UriSource = new Uri(file);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.EndInit();
        image.Freeze();

        return image;
    }

    private void Reset()
    {
        _loadVersion++;
        _gifFrames = [];
        _gifIndex = 0;
        _gifTimer.Stop();
        _videoProbe?.Stop();

        // 每次重新铺背景都从"没有错误"开始；真的解不开时由 OnVideoFailed 再记上
        BackgroundService.ReportMediaError(null);

        // 没有背景了，推拉与视差一并收掉，别留着每帧渲染
        _hasContent = false;
        _pointerX = 0;
        _pointerY = 0;

        AnimationEngine.StopAmbient(DriftKey);
        DriftZoom.ScaleX = 1;
        DriftZoom.ScaleY = 1;
        DriftShift.X = 0;
        DriftShift.Y = 0;

        ImgBackground.Source = null;
        ImgBackground.Visibility = Visibility.Collapsed;

        try
        {
            VidBackground.Stop();
        }
        catch
        {
            // 没加载过媒体时 Stop 可能抛异常，忽略
        }

        VidBackground.Source = null;
        VidBackground.Visibility = Visibility.Collapsed;

        OverlayDim.Background = Brushes.Transparent;
    }
}
