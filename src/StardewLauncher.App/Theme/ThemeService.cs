using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StardewLauncher.Core.App;

namespace StardewLauncher.App.Theme;

/// <summary>一套强调色的色阶。Base 用于图标与点睛，Bright 用于主要按钮与胶囊。</summary>
internal sealed record AccentPalette(
    Color Deep,
    Color Base,
    Color Bright,
    Color Hover,
    Color Soft,
    Color Faint);

/// <summary>
/// 主题服务：把配色写入 Application.Resources，界面通过 DynamicResource 自动响应。
/// 资源键采用语义化命名（Surface / Border / Text / Accent / Status / Nav）。
/// 默认色板为暖焦糖系：白底 + 深焦糖点睛。
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<AccentTheme, AccentPalette> Accents = new()
    {
        [AccentTheme.Stardew] = new(
            ColorOf("#7A3E22"), ColorOf("#A0522D"), ColorOf("#E67E22"),
            ColorOf("#D9741D"), ColorOf("#F3DFD0"), ColorOf("#FBF0E4")),
        [AccentTheme.SkyBlue] = new(
            ColorOf("#1F4A6B"), ColorOf("#2E6E9E"), ColorOf("#3F8FD0"),
            ColorOf("#4489BC"), ColorOf("#C7DEEE"), ColorOf("#EBF4FA")),
        [AccentTheme.BerryPink] = new(
            ColorOf("#7A2A4E"), ColorOf("#A83C6B"), ColorOf("#D2548A"),
            ColorOf("#C25C88"), ColorOf("#EED2DE"), ColorOf("#FAEDF2")),
        [AccentTheme.Autumn] = new(
            ColorOf("#23491F"), ColorOf("#356B2E"), ColorOf("#4E9A43"),
            ColorOf("#427F38"), ColorOf("#CFE5C6"), ColorOf("#EDF5E9"))
    };

    private static DispatcherTimer? _systemWatcher;
    private static bool _systemWasDark;

    public static ThemeMode Mode { get; private set; } = ThemeMode.Light;

    public static AccentTheme Accent { get; private set; } = AccentTheme.Stardew;

    /// <summary>自定义强调色的色相（0-359）；-1 表示使用上面四套预设。</summary>
    public static int CustomHue { get; private set; } = -1;

    /// <summary>自定义强调色的饱和度（8-100）。</summary>
    public static int CustomSaturation { get; private set; } = 68;

    /// <summary>半透明面板的不透明度（55-100）。默认 78，与原来的玻璃面板一致。</summary>
    public static int PanelOpacity { get; private set; } = 78;

    public static bool UseCustomAccent => CustomHue >= 0;

    /// <summary>当前生效的强调色（自定义优先，否则取预设），取自 Base 一档。</summary>
    public static Color CurrentBaseColor => ResolvePalette(Accent).Base;

    /// <summary>当前生效的色相：自定义优先，否则取预设 Base 色折算出来的色相。</summary>
    public static int EffectiveHue => UseCustomAccent ? CustomHue : DescribeColor(CurrentBaseColor).Hue;

    /// <summary>当前生效的饱和度。</summary>
    public static int EffectiveSaturation
        => UseCustomAccent ? CustomSaturation : DescribeColor(CurrentBaseColor).Saturation;

    /// <summary>把颜色折算成色相与饱和度，供设置页的「按色值」入口使用。</summary>
    public static (int Hue, int Saturation) DescribeColor(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        var hue = 0d;

        if (delta > 0)
        {
            if (Math.Abs(max - r) < double.Epsilon) hue = 60 * (((g - b) / delta) % 6);
            else if (Math.Abs(max - g) < double.Epsilon) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }

        if (hue < 0) hue += 360;

        var saturation = max <= 0 ? 0 : delta / max;

        return ((int)Math.Round(hue), (int)Math.Round(saturation * 100));
    }

    public static bool IsDark { get; private set; }

    public static event Action? ThemeChanged;

    public static void Initialize(ThemeMode mode, AccentTheme accent)
    {
        Mode = mode;
        Accent = accent;

        var settings = SettingsStore.Current;
        CustomHue = settings.AccentHue is >= 0 and < 360 ? settings.AccentHue : -1;
        CustomSaturation = Math.Clamp(settings.AccentSaturation, 8, 100);
        PanelOpacity = Math.Clamp(settings.PanelOpacity, 55, 100);

        _systemWasDark = IsSystemInDarkMode();
        Apply();
        EnsureSystemWatcher();
    }

    public static void SetTheme(ThemeMode mode, AccentTheme accent)
    {
        Mode = mode;
        Accent = accent;
        SettingsStore.Current.Theme = mode;
        SettingsStore.Current.AccentTheme = accent;
        Apply();
    }

    /// <summary>
    /// 用色相 + 饱和度生成整套强调色。只改内存与设置对象，落盘由调用方决定
    /// （拖动滑块时每一格都调一次，不该每次都写文件）。
    /// </summary>
    public static void SetCustomAccent(int hue, int saturation)
    {
        CustomHue = ((hue % 360) + 360) % 360;
        CustomSaturation = Math.Clamp(saturation, 8, 100);

        SettingsStore.Current.AccentHue = CustomHue;
        SettingsStore.Current.AccentSaturation = CustomSaturation;

        Apply();
    }

    /// <summary>放弃自定义色，回到预设四套配色。</summary>
    public static void ClearCustomAccent()
    {
        CustomHue = -1;
        SettingsStore.Current.AccentHue = -1;
        Apply();
    }

    /// <summary>调整半透明面板的透明度（数值越大越实）。</summary>
    public static void SetPanelOpacity(int percent)
    {
        PanelOpacity = Math.Clamp(percent, 55, 100);
        SettingsStore.Current.PanelOpacity = PanelOpacity;
        Apply();
    }

    public static void Apply()
    {
        IsDark = Mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemInDarkMode()
        };

        var resources = Application.Current?.Resources;
        if (resources is null) return;

        foreach (var (key, color) in BuildBrushes(IsDark, Accent))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        resources["Shadow.Tint"] = IsDark ? ColorOf("#000000") : ColorOf("#4A3A22");
        resources["Brush.WindowBackground"] = BuildWindowGradient(IsDark, Accent);
        resources["Brush.CardCover"] = BuildCardCover(IsDark, Accent);

        ThemeChanged?.Invoke();
    }

    public static void Shutdown()
    {
        _systemWatcher?.Stop();
        _systemWatcher = null;
    }

    public static bool IsSystemInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureSystemWatcher()
    {
        if (_systemWatcher is not null) return;

        _systemWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _systemWatcher.Tick += (_, _) =>
        {
            if (Mode != ThemeMode.System) return;
            var dark = IsSystemInDarkMode();
            if (dark == _systemWasDark) return;
            _systemWasDark = dark;
            Apply();
        };
        _systemWatcher.Start();
    }

    private static Dictionary<string, Color> BuildBrushes(bool dark, AccentTheme accent)
    {
        var palette = ResolvePalette(accent);

        var brushes = new Dictionary<string, Color>(40);

        // 强调色：Base 用于图标与点睛，Bright 用于主要按钮与胶囊
        brushes["Accent.Deep"] = dark ? Mix(palette.Deep, Colors.Black, 0.15) : palette.Deep;
        brushes["Accent.Base"] = dark ? Lighten(palette.Base, 0.18) : palette.Base;
        brushes["Accent.Bright"] = palette.Bright;
        brushes["Accent.Hover"] = dark ? Lighten(palette.Bright, 0.08) : palette.Hover;
        brushes["Accent.Soft"] = dark ? Mix(palette.Base, ColorOf("#2B2520"), 0.42) : palette.Soft;
        brushes["Accent.Faint"] = dark ? Mix(palette.Base, ColorOf("#2B2520"), 0.20) : palette.Faint;

        // 文字
        brushes["Text.Primary"] = dark ? ColorOf("#F3EBE0") : ColorOf("#46331F");
        brushes["Text.Secondary"] = dark ? ColorOf("#C6B8A6") : ColorOf("#8A6D5B");
        brushes["Text.Tertiary"] = dark ? ColorOf("#93866F") : ColorOf("#A8927E");
        brushes["Text.Disabled"] = dark ? ColorOf("#6E6252") : ColorOf("#C4B4A2");
        brushes["Text.OnAccent"] = Colors.White;

        // 承载面：白底 + 暖色侧栏
        brushes["Surface.Window"] = dark ? ColorOf("#1C1815") : ColorOf("#FAF6F0");
        brushes["Surface.Panel"] = dark ? ColorOf("#241F1A") : ColorOf("#F6EDE2");

        // 侧栏与标题栏用半透明版本：个性化背景要能透到整个窗口，不能只铺内容区。
        // 透明度由设置里的「面板不透明度」决定，默认 78%——看得出是壁纸，导航文字也不至于糊在图上。
        var panelAlpha = AlphaOf(PanelOpacity);

        brushes["Surface.PanelGlass"] = dark
            ? WithAlpha(ColorOf("#241F1A"), panelAlpha)
            : WithAlpha(ColorOf("#F6EDE2"), panelAlpha);

        // 卡片也半透明，但比侧栏稍实一点：卡片里文字更密，对比度要留足。
        // 只给 SurfaceCard 模板用；Surface.Card 保持不透明，否则嵌在卡片里的面板和弹窗
        // 会变成「半透明套半透明」，叠出来的通透度不可控。
        var cardAlpha = AlphaOf(Math.Min(100, PanelOpacity + 4));
        brushes["Surface.CardGlass"] = dark
            ? WithAlpha(ColorOf("#2B2520"), cardAlpha)
            : WithAlpha(Colors.White, cardAlpha);
        brushes["Surface.Card"] = dark ? ColorOf("#2B2520") : Colors.White;
        brushes["Surface.CardHover"] = dark ? ColorOf("#342D26") : ColorOf("#FDF6EE");
        brushes["Surface.Sunken"] = dark ? ColorOf("#211C18") : ColorOf("#F7F0E7");
        brushes["Surface.Overlay"] = dark ? ColorOf("#B3000000") : ColorOf("#59000000");

        // 描边
        brushes["Border.Default"] = dark ? ColorOf("#3D342B") : ColorOf("#EADCCC");
        brushes["Border.Strong"] = dark ? ColorOf("#52463A") : ColorOf("#D9C3AA");

        // 状态
        brushes["Status.Warn"] = dark ? ColorOf("#E0A94A") : ColorOf("#C97A16");
        brushes["Status.WarnSoft"] = dark ? ColorOf("#3A2F1B") : ColorOf("#FBF0E4");
        brushes["Status.Danger"] = dark ? ColorOf("#E8735F") : ColorOf("#B23A2E");
        brushes["Status.DangerSoft"] = dark ? ColorOf("#3A211C") : ColorOf("#FBE7E2");
        brushes["Status.Success"] = dark ? ColorOf("#6FAF63") : ColorOf("#4E8A3F");

        // 侧栏导航
        brushes["Nav.ItemHover"] = dark ? ColorOf("#2F2822") : ColorOf("#F0E4D6");
        brushes["Nav.ItemActive"] = dark ? ColorOf("#3A2E22") : ColorOf("#F6E3D2");
        brushes["Nav.Indicator"] = palette.Bright;
        brushes["Nav.Text"] = dark ? ColorOf("#B8A894") : ColorOf("#7A6551");
        brushes["Nav.TextActive"] = dark ? ColorOf("#F0C9A8") : ColorOf("#A0522D");

        return brushes;
    }

    /// <summary>窗口整体底纹：极淡的暖色斜向渐变，卡片浮起来时不至于贴在一块死板上。</summary>
    private static LinearGradientBrush BuildWindowGradient(bool dark, AccentTheme accent)
    {
        var palette = ResolvePalette(accent);

        var edge = dark
            ? Mix(ColorOf("#1C1815"), palette.Base, 0.16)
            : Mix(palette.Soft, ColorOf("#FFFDF9"), 0.52);
        var middle = dark ? Mix(ColorOf("#1C1815"), palette.Base, 0.05) : ColorOf("#FBF7F1");

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.88, 0),
            EndPoint = new Point(0.12, 1)
        };
        brush.GradientStops.Add(new GradientStop(edge, 0));
        brush.GradientStops.Add(new GradientStop(middle, 0.42));
        brush.GradientStops.Add(new GradientStop(edge, 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush BuildCardCover(bool dark, AccentTheme accent)
    {
        var palette = ResolvePalette(accent);

        var from = dark ? Mix(palette.Deep, Colors.Black, 0.35) : palette.Deep;
        var to = dark ? Mix(palette.Base, Colors.Black, 0.25) : palette.Bright;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(from, 0));
        brush.GradientStops.Add(new GradientStop(to, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color from, Color to, double t) => Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * t),
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));

    private static Color Lighten(Color color, double amount) => Mix(color, Colors.White, amount);

    private static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>有自定义色相就用自定义，否则取预设；预设值坏了也不至于崩。</summary>
    private static AccentPalette ResolvePalette(AccentTheme accent)
        => UseCustomAccent
            ? BuildCustomPalette(CustomHue, CustomSaturation)
            : Accents.TryGetValue(accent, out var found) ? found : Accents[AccentTheme.Stardew];

    /// <summary>
    /// 从一个色相推出一套色阶，深浅分工与预设保持一致：
    /// Base 点睛、Bright 主按钮、Deep 深色底、Hover 悬停、Soft/Faint 两级浅底。
    /// </summary>
    private static AccentPalette BuildCustomPalette(int hue, int saturation)
    {
        var s = Math.Clamp(saturation, 8, 100) / 100.0;

        return new AccentPalette(
            Hsv(hue, s, 0.46),
            Hsv(hue, s, 0.62),
            Hsv(hue, s, 0.88),
            Hsv(hue, s, 0.80),
            Hsv(hue, Math.Min(1, s * 0.22), 0.945),
            Hsv(hue, Math.Min(1, s * 0.12), 0.975));
    }

    private static Color Hsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static byte AlphaOf(int percent)
        => (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0);

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);
}
