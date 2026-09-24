using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using StardewLauncher.App.Animation;
using StardewLauncher.App.Theme;

namespace StardewLauncher.App.Controls;

public enum ButtonTone
{
    /// <summary>描边按钮，用于次要操作。</summary>
    Outline = 0,

    /// <summary>实心强调色按钮，用于主要操作。</summary>
    Solid = 1,

    /// <summary>危险操作。</summary>
    Danger = 2,

    /// <summary>无边框文字按钮。</summary>
    Plain = 3
}

/// <summary>
/// 按钮。Tone 决定配色：Outline 为描边、Solid 为焦糖实心、Danger 为警示、Plain 为纯文字。
/// 静止态的配色一律用资源引用，主题切换后自动跟随。
/// </summary>
public class OutlineButton : Button
{
    /// <summary>呼吸外发光的强弱两端与单程时长。</summary>
    private const double GlowIdleOpacity = 0.22;
    private const double GlowPeakOpacity = 0.62;
    private const double GlowHalfCycleMs = 900;

    private Border? _face;
    private ScaleTransform? _scale;
    private RipplePlayer? _ripple;

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(ButtonTone), typeof(OutlineButton),
        new PropertyMetadata(ButtonTone.Outline, OnToneChanged));

    public static readonly DependencyProperty GlowPulseProperty = DependencyProperty.Register(
        nameof(GlowPulse), typeof(bool), typeof(OutlineButton),
        new PropertyMetadata(false, OnGlowPulseChanged));

    public ButtonTone Tone
    {
        get => (ButtonTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>
    /// 持续呼吸的外发光。只给各页面里的「主操作」按钮用（比如启动页的启动游戏），
    /// 每个按钮都发光会失去重点。窗口最小化时由动画引擎统一叫停。
    /// </summary>
    public bool GlowPulse
    {
        get => (bool)GetValue(GlowPulseProperty);
        set => SetValue(GlowPulseProperty, value);
    }

    private static void OnToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (OutlineButton)d;
        button.ApplyRestingState();
        button.SyncRippleFill();
        button.SyncGlow();
    }

    private static void OnGlowPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((OutlineButton)d).SyncGlow();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _face = GetTemplateChild("Face") as Border;
        if (_face is not null)
        {
            _face.RenderTransformOrigin = new Point(0.5, 0.5);
            _scale = new ScaleTransform(1, 1);
            _face.RenderTransform = _scale;
        }

        _ripple = new RipplePlayer(GetTemplateChild("RippleLayer") as Canvas,
            GetTemplateChild("Ripple") as Ellipse, $"ripple:{GetHashCode()}");

        ApplyRestingState();
        SyncRippleFill();
        SyncGlow();

        // 主题换了要重新取一次发光颜色：Effect 的 Color 不是资源引用，不会自己跟着变。
        // 同时把呼吸动效的起停绑到可视树：页面切走后看不见了，没必要还占着每帧渲染。
        Loaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.ThemeChanged += OnThemeChanged;

            if (GlowPulse) SyncGlow();
        };

        Unloaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            AnimationEngine.StopAmbient($"glow:{GetHashCode()}");
        };

        // 禁用状态（比如还没有实例时的启动按钮）不该闪着光
        IsEnabledChanged += (_, _) =>
        {
            if (IsEnabled)
            {
                if (GlowPulse) SyncGlow();
                return;
            }

            AnimationEngine.StopAmbient($"glow:{GetHashCode()}");
        };
    }

    private void OnThemeChanged()
    {
        if (GlowPulse) SyncGlow();
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_face is null || !IsEnabled || Tone == ButtonTone.Plain) return;

        var (background, border, foreground) = HoverColors();
        AnimationEngine.Color(_face, Border.BackgroundProperty, background, 150, Ease.OutFluent);
        AnimationEngine.Color(_face, Border.BorderBrushProperty, border, 150, Ease.OutFluent);
        AnimationEngine.Color(this, ForegroundProperty, foreground, 150, Ease.OutFluent);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        RestoreRestingState();
    }

    protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!IsEnabled) return;

        if (_face is not null) _ripple?.Play(e.GetPosition(_face));

        if (_scale is null) return;

        AnimationEngine.ScaleX(_scale, 0.975, 80, t => Ease.OutFluent(t, 5));
        AnimationEngine.ScaleY(_scale, 0.975, 80, t => Ease.OutFluent(t, 5));
    }

    protected override void OnPreviewMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_scale is null || !IsEnabled) return;

        AnimationEngine.ScaleX(_scale, 1, 300, Ease.OutBack);
        AnimationEngine.ScaleY(_scale, 1, 300, Ease.OutBack);
    }

    private void ApplyRestingState()
    {
        if (_face is null) return;

        var (backgroundKey, borderKey, foregroundKey) = RestingKeys();
        _face.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        _face.SetResourceReference(Border.BorderBrushProperty, borderKey);
        _face.BorderThickness = Tone == ButtonTone.Plain ? new Thickness(0) : new Thickness(1);
        SetResourceReference(ForegroundProperty, foregroundKey);
    }

    private void RestoreRestingState()
    {
        if (_face is null || Tone == ButtonTone.Plain) return;

        var (backgroundKey, borderKey, foregroundKey) = RestingKeys();
        var (background, border, foreground) = RestingColors();

        AnimationEngine.Color(_face, Border.BackgroundProperty, background, 200, Ease.OutFluent,
            () => _face?.SetResourceReference(Border.BackgroundProperty, backgroundKey));
        AnimationEngine.Color(_face, Border.BorderBrushProperty, border, 200, Ease.OutFluent,
            () => _face?.SetResourceReference(Border.BorderBrushProperty, borderKey));
        AnimationEngine.Color(this, ForegroundProperty, foreground, 200, Ease.OutFluent,
            () => SetResourceReference(ForegroundProperty, foregroundKey));
    }

    /// <summary>
    /// 水波纹颜色跟着按钮配色走。填色用不透明色，淡出全靠波纹元素自己的不透明度——
    /// 颜色再带一层透明度的话，两层一乘就淡到看不见了。
    /// </summary>
    private void SyncRippleFill() => _ripple?.UseFill(
        Tone is ButtonTone.Solid or ButtonTone.Danger ? "Text.OnAccent" : "Accent.Base");

    /// <summary>
    /// 呼吸外发光。模糊半径固定、只动不透明度：逐帧改模糊半径要重算整张效果图，贵得多。
    /// </summary>
    private void SyncGlow()
    {
        if (_face is null) return;

        if (!GlowPulse || !IsEnabled)
        {
            if (_face.Effect is DropShadowEffect) _face.Effect = null;
            return;
        }

        if (_face.Effect is not DropShadowEffect glow)
        {
            glow = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Direction = 270,
                Opacity = GlowIdleOpacity
            };

            _face.Effect = glow;
        }

        // 颜色每次都重新取：Tone 会变（启动按钮跑起来时变成危险色），主题也会变
        glow.Color = Tone is ButtonTone.Danger ? ThemeColors.Danger(this) : ThemeColors.AccentBright(this);

        AnimationEngine.StartAmbient(_face, $"glow:{GetHashCode()}",
            GlowIdleOpacity, GlowPeakOpacity, GlowHalfCycleMs, Ease.InOutFluent, v => glow.Opacity = v);
    }

    private (string Background, string Border, string Foreground) RestingKeys() => Tone switch
    {
        ButtonTone.Solid => ("Accent.Bright", "Accent.Bright", "Text.OnAccent"),
        ButtonTone.Danger => ("Status.Danger", "Status.Danger", "Text.OnAccent"),
        ButtonTone.Plain => ("Common.Transparent", "Common.Transparent", "Accent.Base"),
        _ => ("Surface.Card", "Border.Strong", "Text.Primary")
    };

    private (Color Background, Color Border, Color Foreground) RestingColors() => Tone switch
    {
        ButtonTone.Solid => (ThemeColors.AccentBright(this), ThemeColors.AccentBright(this), ThemeColors.OnAccent(this)),
        ButtonTone.Danger => (ThemeColors.Danger(this), ThemeColors.Danger(this), ThemeColors.OnAccent(this)),
        ButtonTone.Plain => (ThemeColors.Transparent(this), ThemeColors.Transparent(this), ThemeColors.Accent(this)),
        _ => (ThemeColors.Surface(this), ThemeColors.BorderStrong(this), ThemeColors.Text(this))
    };

    private (Color Background, Color Border, Color Foreground) HoverColors() => Tone switch
    {
        ButtonTone.Solid => (ThemeColors.AccentHover(this), ThemeColors.AccentHover(this), ThemeColors.OnAccent(this)),
        ButtonTone.Danger => (ThemeColors.DangerHover(this), ThemeColors.DangerHover(this), ThemeColors.OnAccent(this)),
        ButtonTone.Plain => (ThemeColors.Transparent(this), ThemeColors.Transparent(this), ThemeColors.AccentBright(this)),
        _ => (ThemeColors.SurfaceHover(this), ThemeColors.Accent(this), ThemeColors.Accent(this))
    };
}
