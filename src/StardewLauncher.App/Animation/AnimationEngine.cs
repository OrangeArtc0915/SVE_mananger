using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace StardewLauncher.App.Animation;

/// <summary>
/// 轻量动画引擎。以 CompositionTarget.Rendering 为帧驱动，按 key 管理动画组，
/// 同名 key 会先停掉旧动画。批量构建界面时可用 Suspend() 临时关闭动画。
/// </summary>
public static class AnimationEngine
{
    private sealed class Entry
    {
        public double StartTime;
        public double Duration;
        public double From;
        public double To;
        public required Func<double, double> Easing;
        public required Action<double> Setter;
        public Action? Completed;
        public bool Done;

        /// <summary>往返循环：跑到终点后换向重来，永不停，用于呼吸光、缓慢推拉这类环境动效。</summary>
        public bool PingPong;
    }

    /// <summary>环境动效的 key 前缀。自检时据此把它们与一次性动画区分开：前者本来就永不收尾。</summary>
    public const string AmbientPrefix = "ambient:";

    private static readonly Dictionary<string, Entry> Running = new();

    /// <summary>环境动效的登记表：窗口最小化停掉后，恢复时要照着这张表重放。</summary>
    private static readonly Dictionary<string, (WeakReference<DependencyObject> Owner, Action Restart)> Ambients = new();

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static int _suspendCount;
    private static bool _hooked;
    private static bool _ambientEnabled = true;

    public static bool IsEnabled => _suspendCount == 0;

    /// <summary>环境动效总开关。窗口最小化时置 false，恢复时置 true。</summary>
    public static bool AmbientEnabled => _ambientEnabled;

    /// <summary>帧驱动被调用的次数，用于排查动画是否真的在推进。</summary>
    public static long TickCount { get; private set; }

    /// <summary>当前仍在运行的动画数量。</summary>
    public static int RunningCount => Running.Count;

    /// <summary>当前仍在运行的动画 key，用于排查动画未收尾的问题。</summary>
    public static IReadOnlyCollection<string> RunningKeys => Running.Keys.ToList();

    public static IDisposable Suspend()
    {
        _suspendCount++;
        return new Suspension();
    }

    private sealed class Suspension : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _suspendCount = Math.Max(0, _suspendCount - 1);
            if (_suspendCount == 0) StopAll();
        }
    }

    public static void Start(string key, double from, double to,
        double durationMs, Func<double, double>? easing, Action<double> setter, Action? completed = null,
        double delayMs = 0)
    {
        if (!IsEnabled)
        {
            setter(to);
            completed?.Invoke();
            return;
        }

        if (durationMs <= 0)
        {
            Running.Remove(key);
            setter(to);
            completed?.Invoke();
            return;
        }

        Running[key] = new Entry
        {
            StartTime = Clock.Elapsed.TotalMilliseconds + Math.Max(0, delayMs),
            Duration = durationMs,
            From = from,
            To = to,
            Easing = easing ?? Ease.OutFluent,
            Setter = setter,
            Completed = completed
        };

        EnsureHook();
    }

    public static void Stop(string key)
    {
        if (Running.TryGetValue(key, out var entry)) entry.Done = true;
        Running.Remove(key);
    }

    /// <summary>
    /// 启动一个往返循环动画：跑到终点后原路返回，永不结束。用于呼吸光、缓慢推拉这类环境动效。
    /// 优先用 <see cref="StartAmbient"/>，那样窗口最小化时会被统一叫停。
    /// </summary>
    public static void StartPingPong(string key, double from, double to, double durationMs,
        Func<double, double>? easing, Action<double> setter)
    {
        if (!IsEnabled || durationMs <= 0)
        {
            setter(to);
            return;
        }

        Running[key] = new Entry
        {
            StartTime = Clock.Elapsed.TotalMilliseconds,
            Duration = durationMs,
            From = from,
            To = to,
            Easing = easing ?? Ease.InOutFluent,
            Setter = setter,
            PingPong = true
        };

        EnsureHook();
    }

    /// <summary>
    /// 登记并启动一个环境动效。窗口最小化时统一停掉、恢复时按登记表自动重放；
    /// owner 只持弱引用，页面被丢弃后不会留着回调不放。
    /// from 应当是静止值：环境动效被禁用时界面就停在它上面。
    /// </summary>
    public static void StartAmbient(DependencyObject owner, string key, double from, double to,
        double durationMs, Func<double, double>? easing, Action<double> setter)
    {
        var id = AmbientPrefix + key;

        void Restart() => StartPingPong(id, from, to, durationMs, easing, setter);

        Ambients[id] = (new WeakReference<DependencyObject>(owner), Restart);

        if (_ambientEnabled && IsEnabled) Restart();
        else setter(from);
    }

    /// <summary>
    /// 停掉一个环境动效（比如页面切走时）。登记项会留着，回到页面再调 <see cref="StartAmbient"/> 就接得上。
    /// </summary>
    public static void StopAmbient(string key) => Stop(AmbientPrefix + key);

    /// <summary>叫停 / 恢复所有环境动效。由主窗口按最小化状态驱动。</summary>
    public static void SetAmbientEnabled(bool enabled)
    {
        if (_ambientEnabled == enabled) return;
        _ambientEnabled = enabled;

        if (!enabled)
        {
            StopWhere(key => key.StartsWith(AmbientPrefix, StringComparison.Ordinal));
            return;
        }

        foreach (var (id, entry) in Ambients.ToList())
        {
            // 宿主已不在可视树里就顺手清掉登记项，免得越攒越多
            if (!entry.Owner.TryGetTarget(out var owner) || owner is not FrameworkElement { IsLoaded: true })
            {
                Ambients.Remove(id);
                continue;
            }

            try { entry.Restart(); } catch { /* 元素已销毁时设置属性会抛，忽略 */ }
        }
    }

    public static void StopWhere(Func<string, bool> predicate)
    {
        foreach (var key in Running.Keys.Where(predicate).ToList()) Running.Remove(key);
    }

    public static void StopAll() => Running.Clear();

    private static void EnsureHook()
    {
        if (_hooked) return;
        _hooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (Running.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
            return;
        }

        TickCount++;
        var now = Clock.Elapsed.TotalMilliseconds;
        List<Action>? completedCallbacks = null;

        foreach (var entry in Running.Values.ToList())
        {
            if (entry.Done) continue;

            var progress = Math.Clamp((now - entry.StartTime) / entry.Duration, 0, 1);
            var eased = entry.Easing(progress);

            try { entry.Setter(entry.From + (entry.To - entry.From) * eased); }
            catch { entry.Done = true; continue; }

            if (progress >= 1 && !entry.Done)
            {
                if (entry.PingPong)
                {
                    // 往返：换向重来，不结束。起点按当前帧时间重算，来回一圈的偏差肉眼看不出来
                    (entry.From, entry.To) = (entry.To, entry.From);
                    entry.StartTime = now;
                    continue;
                }

                entry.Done = true;
                if (entry.Completed is not null)
                    (completedCallbacks ??= new List<Action>()).Add(entry.Completed);
            }
        }

        foreach (var pair in Running.Where(p => p.Value.Done).ToList()) Running.Remove(pair.Key);

        if (completedCallbacks is null) return;
        foreach (var callback in completedCallbacks)
        {
            try { callback(); } catch { }
        }
    }

    // ————— 常用属性动画的语法糖 —————

    public static void Opacity(UIElement element, double to, double durationMs = 160,
        Func<double, double>? easing = null, Action? completed = null)
        => Start(Key(element, "Opacity"), element.Opacity, to, durationMs, easing,
            v => element.Opacity = v, completed);

    public static void Double(UIElement element, DependencyProperty property, double to,
        double durationMs = 160, Func<double, double>? easing = null)
        => Start(Key(element, property.Name), Convert.ToDouble(element.GetValue(property)), to,
            durationMs, easing, v => element.SetValue(property, v));

    public static void TranslateX(TranslateTransform transform, double to, double durationMs = 160,
        Func<double, double>? easing = null)
        => Start($"translateX:{RuntimeHelpers.GetHashCode(transform)}", transform.X, to, durationMs,
            easing, v => transform.X = v);

    public static void TranslateY(TranslateTransform transform, double to, double durationMs = 160,
        Func<double, double>? easing = null)
        => Start($"translateY:{RuntimeHelpers.GetHashCode(transform)}", transform.Y, to, durationMs,
            easing, v => transform.Y = v);

    public static void Rotate(RotateTransform transform, double to, double durationMs = 160,
        Func<double, double>? easing = null)
        => Start($"rotate:{RuntimeHelpers.GetHashCode(transform)}", transform.Angle, to, durationMs,
            easing, v => transform.Angle = v);

    public static void ScaleX(ScaleTransform transform, double to, double durationMs = 160,
        Func<double, double>? easing = null)
        => Start($"scaleX:{RuntimeHelpers.GetHashCode(transform)}", transform.ScaleX, to, durationMs,
            easing, v => transform.ScaleX = v);

    public static void ScaleY(ScaleTransform transform, double to, double durationMs = 160,
        Func<double, double>? easing = null)
        => Start($"scaleY:{RuntimeHelpers.GetHashCode(transform)}", transform.ScaleY, to, durationMs,
            easing, v => transform.ScaleY = v);

    /// <summary>等比缩放（横竖两个方向各起一条动画）。</summary>
    public static void Scale(ScaleTransform transform, double to, double durationMs = 160,
        Func<double, double>? easing = null)
    {
        ScaleX(transform, to, durationMs, easing);
        ScaleY(transform, to, durationMs, easing);
    }

    public static void Color(UIElement element, DependencyProperty property, Color to,
        double durationMs = 160, Func<double, double>? easing = null, Action? completed = null)
    {
        var brush = MutableBrush(element, property);
        var from = brush.Color;
        Start(Key(element, property.Name), 0, 1, durationMs, easing, v =>
        {
            brush.Color = Lerp(from, to, v);
        }, completed);
    }

    /// <summary>拿到可动画的画刷。主题资源里的画刷是冻结的，必须先换一份可变副本。</summary>
    public static SolidColorBrush MutableBrush(UIElement element, DependencyProperty property)
    {
        if (element.GetValue(property) is SolidColorBrush existing && !existing.IsFrozen)
            return existing;

        var color = (element.GetValue(property) as SolidColorBrush)?.Color ?? Colors.Transparent;
        var brush = new SolidColorBrush(color);
        element.SetValue(property, brush);
        return brush;
    }

    public static Color Lerp(Color from, Color to, double t) => System.Windows.Media.Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * t),
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));

    private static string Key(UIElement element, string property)
        => $"{property}:{RuntimeHelpers.GetHashCode(element)}";
}
