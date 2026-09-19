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

    public static bool IsDark { get; private set; }

    public static event Action? ThemeChanged;

    public static void Initialize(ThemeMode mode, AccentTheme accent)
    {
        Mode = mode;
        Accent = accent;
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
        var palette = Accents.TryGetValue(accent, out var found) ? found : Accents[AccentTheme.Stardew];

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
        // 留 22% 的透出量——看得出是壁纸，导航文字也不至于糊在图上。
        brushes["Surface.PanelGlass"] = dark ? ColorOf("#C7241F1A") : ColorOf("#C7F6EDE2");

        // 卡片也半透明，但比侧栏稍实一点（18% 透出）：卡片里文字更密，对比度要留足。
        // 只给 SurfaceCard 模板用；Surface.Card 保持不透明，否则嵌在卡片里的面板和弹窗
        // 会变成「半透明套半透明」，叠出来的通透度不可控。
        brushes["Surface.CardGlass"] = dark ? ColorOf("#D12B2520") : ColorOf("#D1FFFFFF");
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
        var palette = Accents.TryGetValue(accent, out var found) ? found : Accents[AccentTheme.Stardew];

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
        var palette = Accents.TryGetValue(accent, out var found) ? found : Accents[AccentTheme.Stardew];

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
}
