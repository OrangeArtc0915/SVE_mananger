using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Pages;
using StardewLauncher.App.Theme;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Homepage;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Weather;

namespace StardewLauncher.App.Views;

/// <summary>
/// 主页小组件：月历（现实日期）、现实天气（Open-Meteo）、每日一言。
/// 天气查询全部走异步，不在 UI 线程等待网络。
/// </summary>
public partial class HomepageWidgetsView : UserControl
{
    private static readonly string[] WeekdayNames =
        ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];

    /// <summary>月历表头，周一为一周起点。</summary>
    private static readonly string[] WeekdayShort = ["一", "二", "三", "四", "五", "六", "日"];

    private bool _weatherRunning;

    public HomepageWidgetsView()
    {
        InitializeComponent();

        BuildWeekdayHeader();
        ApplyArtOpacity();
        ThemeService.ThemeChanged += ApplyArtOpacity;

        RefreshCalendar();
        RefreshQuote();
        _ = RefreshWeatherAsync();
    }

    /// <summary>供页面进入时调用，重新按当前设置刷新月历与天气（天气命中 30 分钟缓存则不会重复请求）。</summary>
    public void Refresh()
    {
        RefreshCalendar();
        _ = RefreshWeatherAsync();
    }

    // ————— 月历 —————

    private void BuildWeekdayHeader()
    {
        WeekdayHeader.Children.Clear();

        foreach (var name in WeekdayShort)
        {
            var text = new TextBlock
            {
                Text = name,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            WeekdayHeader.Children.Add(text);
        }
    }

    private void RefreshCalendar()
    {
        var today = DateTime.Today;

        LabMonthTitle.Text = $"{today.Year} 年 {today.Month} 月 · {WeekdayNames[(int)today.DayOfWeek]}";

        BuildMonthGrid(today);
    }

    /// <summary>按当月第一天是周几补白，每行 7 格；前后补白显示邻月日期并置灰。</summary>
    private void BuildMonthGrid(DateTime today)
    {
        MonthGrid.Children.Clear();

        var first = new DateTime(today.Year, today.Month, 1);
        var leading = ((int)first.DayOfWeek + 6) % 7;
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var totalCells = (int)Math.Ceiling((leading + daysInMonth) / 7d) * 7;

        for (var i = 0; i < totalCells; i++)
            MonthGrid.Children.Add(CreateDayCell(first.AddDays(i - leading), today));

#if DEBUG
        LogMonthGrid(today, leading, totalCells);
#endif
    }

    private FrameworkElement CreateDayCell(DateTime date, DateTime today)
    {
        var inMonth = date.Month == today.Month;
        var isToday = date.Date == today.Date;

        var text = new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var cell = new Border
        {
            Margin = new Thickness(2),
            MinHeight = 24,
            CornerRadius = TryFindResource("Radius.Small") is CornerRadius radius ? radius : new CornerRadius(6),
            Child = text
        };

        if (isToday)
        {
            text.FontWeight = FontWeights.Bold;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.OnAccent");
            cell.SetResourceReference(Border.BackgroundProperty, "Accent.Bright");
            return cell;
        }

        text.SetResourceReference(TextBlock.ForegroundProperty, inMonth ? "Text.Secondary" : "Text.Disabled");
        return cell;
    }

#if DEBUG
    /// <summary>自检：记录月历格数与今天那一格的背景，便于无人值守确认渲染是否正常。</summary>
    private void LogMonthGrid(DateTime today, int leading, int totalCells)
    {
        try
        {
            var index = leading + today.Day - 1;
            var todayCell = MonthGrid.Children[index] as Border;
            var actual = (todayCell?.Background as SolidColorBrush)?.Color;
            var expected = (TryFindResource("Accent.Bright") as SolidColorBrush)?.Color;

            Log.Info($"月历自检：{today:yyyy-MM} 共 {MonthGrid.Children.Count} 格（前导补白 {leading}，" +
                     $"当月 {DateTime.DaysInMonth(today.Year, today.Month)} 天），今天在第 {index + 1} 格，" +
                     $"背景={Describe(actual)}，Accent.Bright={Describe(expected)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"月历自检失败：{ex.Message}");
        }

        static string Describe(Color? color) => color is { } value ? $"#{value.R:X2}{value.G:X2}{value.B:X2}" : "无";
    }
#endif

    // ————— 每日一言 —————

    private void RefreshQuote() => LabQuote.Text = DailyQuoteService.Pick();

    private void OnNextQuoteClick(object sender, RoutedEventArgs e) => RefreshQuote();

    // ————— 天气 —————

    private void OnGoSetupClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Setup);

    private void OnRefreshWeatherClick(object sender, RoutedEventArgs e) => _ = RefreshWeatherAsync(force: true);

    /// <summary>异步查询现实天气。失败只改文案，不弹窗、不抛异常。</summary>
    private async Task RefreshWeatherAsync(bool force = false)
    {
        if (_weatherRunning) return;
        _weatherRunning = true;

        if (BtnRefreshWeather is not null) BtnRefreshWeather.IsEnabled = false;

        try
        {
            var city = SettingsStore.Current.WeatherCity?.Trim() ?? string.Empty;

            if (city.Length == 0)
            {
                PanNoCity.Visibility = Visibility.Visible;
                PanWeather.Visibility = Visibility.Collapsed;
                return;
            }

            PanNoCity.Visibility = Visibility.Collapsed;
            PanWeather.Visibility = Visibility.Visible;

            LabWeatherCity.Text = city;
            LabWeatherTemp.Text = "…";
            LabWeatherDesc.Text = "正在查询天气…";
            LabWeatherDetail.Text = string.Empty;
            LabWeatherNote.Visibility = Visibility.Collapsed;

            var snapshot = await WeatherService.GetAsync(city, force);

            if (snapshot is null)
            {
                IcoWeatherBig.Icon = WeatherService.IconKey(WeatherKind.Unknown);
                LabWeatherTemp.Text = string.Empty;
                LabWeatherDesc.Text = WeatherService.LastError ?? "天气数据不可用";
                LabWeatherDetail.Text = string.Empty;
                LabWeatherNote.Visibility = Visibility.Collapsed;
                return;
            }

            IcoWeatherBig.Icon = WeatherService.IconKey(snapshot.Kind);

#if DEBUG
            Log.Info($"天气自检：城市={snapshot.City} 温度={snapshot.Temperature:0.#}°C " +
                     $"体感={snapshot.ApparentTemperature:0.#}°C 湿度={snapshot.Humidity:0.#}% " +
                     $"风速={snapshot.WindSpeed:0.#}km/h 类型={snapshot.Kind} 描述={snapshot.Description} " +
                     $"今日={snapshot.TodayHigh:0.#}/{snapshot.TodayLow:0.#}°C 代码={snapshot.WeatherCode} " +
                     $"抓取时间={snapshot.FetchedAt:yyyy-MM-dd HH:mm:ss}");
#endif

            LabWeatherCity.Text = snapshot.City;
            LabWeatherTemp.Text = $"{snapshot.Temperature:0.#}°C";
            LabWeatherDesc.Text = snapshot.Description;

            var parts = new List<string>
            {
                $"体感 {snapshot.ApparentTemperature:0.#}°C",
                $"湿度 {snapshot.Humidity:0.#}%",
                $"风速 {snapshot.WindSpeed:0.#} km/h"
            };

            if (snapshot.TodayHigh is { } high && snapshot.TodayLow is { } low)
                parts.Add($"今日 {high:0.#}/{low:0.#}°C");

            LabWeatherDetail.Text = string.Join(" · ", parts);

            // LastError 仍有值说明这次是退回过期缓存的结果
            if (WeatherService.LastError is { Length: > 0 } reason)
            {
                LabWeatherNote.Text = $"{reason}，显示的是缓存数据";
                LabWeatherNote.Visibility = Visibility.Visible;
            }
            else
            {
                LabWeatherNote.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新天气小组件失败：{ex.Message}");
            LabWeatherDesc.Text = "天气数据不可用";
        }
        finally
        {
            _weatherRunning = false;
            if (BtnRefreshWeather is not null) BtnRefreshWeather.IsEnabled = true;
        }
    }

    // ————— 星露谷素材底纹 —————

    /// <summary>信纸底纹只做点缀：深色主题下再压低透明度，避免影响文字可读性。</summary>
    private void ApplyArtOpacity()
    {
        if (ImgQuotePaper is null) return;
        // 改成 UniformToFill 后底纹比原来实，透明度相应压低
        ImgQuotePaper.Opacity = ThemeService.IsDark ? 0.1 : 0.18;
    }
}
