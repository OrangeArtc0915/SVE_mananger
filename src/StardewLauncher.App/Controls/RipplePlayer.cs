using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StardewLauncher.App.Animation;

namespace StardewLauncher.App.Controls;

/// <summary>
/// 点击水波纹。模板里备一层 Canvas 加一个 Ellipse，这里负责把圆从点击点扩散出去。
/// 圆的半径按「离点击点最远的那个角」算，所以任何尺寸的按钮都能一次盖满；
/// 收尾时把椭圆藏起来，自检程序就不会把它当成卡在透明态的元素。
/// </summary>
internal sealed class RipplePlayer
{
    private const double DurationMs = 460;
    private const double StartScale = 0.04;

    private readonly Canvas? _layer;
    private readonly Ellipse? _dot;
    private readonly ScaleTransform? _scale;
    private readonly string _key;

    public RipplePlayer(Canvas? layer, Ellipse? dot, string key)
    {
        _layer = layer;
        _dot = dot;
        _key = key;

        if (_dot is null) return;

        _dot.RenderTransformOrigin = new Point(0.5, 0.5);
        _scale = new ScaleTransform(StartScale, StartScale);
        _dot.RenderTransform = _scale;
    }

    /// <summary>水波纹颜色跟着按钮配色走（实心按钮上用白，浅底按钮上用强调色）。</summary>
    public void UseFill(string resourceKey) => _dot?.SetResourceReference(Shape.FillProperty, resourceKey);

    /// <summary>position 用相对按钮面板的坐标。按钮还没布局出尺寸时直接忽略这一次。</summary>
    public void Play(Point position)
    {
        if (_layer is null || _dot is null || _scale is null) return;
        if (!AnimationEngine.IsEnabled) return;

        var width = _layer.ActualWidth;
        var height = _layer.ActualHeight;
        if (width <= 1 || height <= 1) return;

        var radius = Math.Max(
            Math.Max(position.X, width - position.X),
            Math.Max(position.Y, height - position.Y)) * 1.12;

        _dot.Width = radius * 2;
        _dot.Height = radius * 2;
        _dot.Visibility = Visibility.Visible;
        Canvas.SetLeft(_dot, position.X - radius);
        Canvas.SetTop(_dot, position.Y - radius);

        _scale.ScaleX = StartScale;
        _scale.ScaleY = StartScale;

        AnimationEngine.Start(_key, 0, 1, DurationMs, Ease.OutFluent, v =>
        {
            _dot.Opacity = (1 - v) * 0.3;
            var size = StartScale + (1 - StartScale) * v;
            _scale.ScaleX = size;
            _scale.ScaleY = size;
        }, () =>
        {
            _dot.Opacity = 0;
            _dot.Visibility = Visibility.Collapsed;
        });
    }
}
