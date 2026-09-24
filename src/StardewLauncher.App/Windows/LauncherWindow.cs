using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Interop;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 二级窗口的统一外壳，做法与主窗口一致：
/// 用 <see cref="WindowChrome"/> 把系统标题栏去掉（CaptionHeight=0、不画系统按钮），
/// 再把 DWM 框架延伸到整个客户区，于是窗口边缘那圈留白能透出桌面，
/// 我们就在留白里自绘投影，做出「浮起来的圆角卡片」。
///
/// <para>
/// 窗口自己的内容会被自动包进这个外壳，XAML 不用改结构；顶部的细条是拖动区，
/// 可缩放的窗口还会带上最小化 / 最大化 / 关闭按钮。
/// </para>
///
/// <para>系统未启用 DWM 合成时会退化为不带留白的纯色窗口，不会出现黑边。</para>
/// </summary>
public class LauncherWindow : Window
{
    /// <summary>卡片外围留给投影的宽度。</summary>
    private const double ShellMargin = 8;

    private const double StripHeight = 34;

    private Border _card = new();
    private bool _shellApplied;

    protected LauncherWindow()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        AllowsTransparency = false;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        if (Application.Current?.TryFindResource("AppFont") is FontFamily font) FontFamily = font;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        // XAML 里的 Background 是在 Content 之前应用的，所以在这里覆盖才有效
        if (_shellApplied || newContent is not UIElement content) return;

        _shellApplied = true;
        ApplyShell(content);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;

        if (WindowInterop.TryExtendFrameIntoClientArea(handle))
        {
            // 渲染层允许 Alpha 通过，外圈留白才能透出桌面
            var source = HwndSource.FromHwnd(handle);
            if (source is not null) source.CompositionTarget.BackgroundColor = Colors.Transparent;

            return;
        }

        // 无法玻璃化：去掉留白与投影，退化成纯色窗口
        _card.Margin = new Thickness(0);
        _card.Effect = null;
    }

    private void ApplyShell(UIElement content)
    {
        // 先摘出来再包，避免赋值时又进一次 OnContentChanged（已由 _shellApplied 挡住，这里只是顺序需要）
        Content = null;

        Background = null;

        var radius = Application.Current?.TryFindResource("Radius.Card") is CornerRadius cardRadius
            ? cardRadius
            : new CornerRadius(12);

        // 卡片本体：负责圆角底色与投影
        _card = new Border
        {
            Margin = new Thickness(ShellMargin),
            CornerRadius = radius,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                Direction = 270,
                ShadowDepth = 0,
                Opacity = 0.22,
                Color = Colors.Black
            }
        };
        _card.SetResourceReference(Border.BackgroundProperty, "Surface.Window");

        // 内容再套一层做圆角裁剪，否则内容会画到圆角外面
        var clipHost = new Border { CornerRadius = radius, Background = Brushes.Transparent };
        var clip = new RectangleGeometry { RadiusX = radius.TopLeft, RadiusY = radius.TopLeft };
        clipHost.Clip = clip;
        clipHost.SizeChanged += (_, args) => clip.Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var strip = BuildStrip();
        Grid.SetRow(strip, 0);
        layout.Children.Add(strip);
        Grid.SetRow(content, 1);
        layout.Children.Add(content);

        clipHost.Child = layout;
        _card.Child = clipHost;
        Content = _card;
    }

    /// <summary>顶部拖动条：按住空白处可拖窗口；可缩放的窗口带最小化 / 最大化 / 关闭。</summary>
    private Grid BuildStrip()
    {
        var strip = new Grid { Height = StripHeight, Background = Brushes.Transparent };
        strip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (e.ClickCount == 2 && ResizeMode != ResizeMode.NoResize) ToggleMaximize();
            else DragMove();
        };
        strip.MouseRightButtonUp += (_, _) => ShowSystemMenu();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5, 6, 0)
        };

        if (ResizeMode != ResizeMode.NoResize)
        {
            // 图标包里没有最大化用的方框字形，所以只给最小化 + 关闭，
            // 最大化沿用「双击标题栏」这个通用习惯
            buttons.Children.Add(BuildStripButton("lucide/minus", "最小化", () => WindowState = WindowState.Minimized));
        }

        buttons.Children.Add(BuildStripButton("lucide/x", "关闭", Close));

        strip.Children.Add(buttons);
        return strip;
    }

    private static RoundIconButton BuildStripButton(string icon, string tip, Action action)
    {
        var button = new RoundIconButton
        {
            Icon = icon,
            IconSize = 12,
            Width = 28,
            Height = 28,
            Tone = ButtonTone.Plain,
            ToolTip = tip
        };

        // 图标按钮没有文字，读屏软件会把 Name 兜成空，这里补上
        AutomationProperties.SetName(button, tip);

        button.Click += (_, _) => action();
        return button;
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>右键标题栏弹系统菜单（和原生窗口一致的习惯）。</summary>
    private void ShowSystemMenu()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return;

        SystemCommands.ShowSystemMenu(this, PointToScreen(Mouse.GetPosition(this)));
    }
}
