using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Weather;

/// <summary>天气状况大类，用于挑选图标与配色。</summary>
public enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    FreezingRain,
    Snow,
    Thunderstorm,
    Unknown
}

/// <summary>一次现实天气查询的结果（不可变）。</summary>
public sealed record WeatherSnapshot(
    string City,
    double Temperature,
    double ApparentTemperature,
    double Humidity,
    double WindSpeed,
    WeatherKind Kind,
    string Description,
    double? TodayHigh,
    double? TodayLow,
    DateTimeOffset FetchedAt,
    int? WeatherCode);

/// <summary>
/// 现实天气（非游戏内天气）：数据源为 Open-Meteo，免费、无需 API Key。
/// 地理编码：geocoding-api.open-meteo.com；天气预报：api.open-meteo.com。
/// 结果缓存到 <c>Paths.Cache\weather.json</c>，30 分钟内直接复用；城市经纬度缓存进 <see cref="Settings"/>。
/// 任何网络失败都不抛异常：优先退回过期缓存，其次返回 null 并把原因写进 <see cref="LastError"/>。
/// </summary>
public static class WeatherService
{
    /// <summary>缓存有效期。</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly JsonSerializerOptions CacheOptions = new() { WriteIndented = true };

    /// <summary>最近一次失败原因（可读文案，中文）。成功时为 null。</summary>
    public static string? LastError { get; private set; }

    private static string CacheFile
    {
        get
        {
            Paths.Init();
            return Path.Combine(Paths.Cache, "weather.json");
        }
    }

    /// <summary>解析城市名到经纬度（带缓存）。失败返回 null。</summary>
    public static async Task<(double Latitude, double Longitude)?> GeocodeAsync(string cityName,
        CancellationToken token = default)
    {
        var city = (cityName ?? string.Empty).Trim();

        if (city.Length == 0)
        {
            LastError = "未填写城市名";
            return null;
        }

        var settings = SettingsStore.Current;

        // 设置里已缓存同一城市的经纬度时直接复用，不再请求
        if (settings.WeatherLatitude is { } cachedLatitude &&
            settings.WeatherLongitude is { } cachedLongitude &&
            string.Equals(settings.WeatherCity?.Trim(), city, StringComparison.OrdinalIgnoreCase))
        {
            LastError = null;
            return (cachedLatitude, cachedLongitude);
        }

        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                      Uri.EscapeDataString(city) + "&count=1&language=zh&format=json";

            using var document = await GetJsonAsync(url, token);

            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                LastError = $"找不到城市「{city}」";
                return null;
            }

            var item = results[0];
            var latitude = DoubleOf(item, "latitude");
            var longitude = DoubleOf(item, "longitude");

            if (latitude is null || longitude is null)
            {
                LastError = $"城市「{city}」没有返回经纬度";
                return null;
            }

            // 只有当前设置的城市与解析目标一致时才写回，避免误缓存别的城市
            if (string.Equals(settings.WeatherCity?.Trim(), city, StringComparison.OrdinalIgnoreCase))
            {
                settings.WeatherLatitude = latitude;
                settings.WeatherLongitude = longitude;
                SettingsStore.Save();
            }

            LastError = null;
            return (latitude.Value, longitude.Value);
        }
        catch (OperationCanceledException)
        {
            LastError = "解析城市超时";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"解析城市失败：{ex.Message}";
            Log.Warn($"天气地理编码失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>取当前天气。失败返回 null 并把原因写进 <see cref="LastError"/>；有旧缓存时退回过期缓存。</summary>
    public static async Task<WeatherSnapshot?> GetAsync(string cityName, bool forceRefresh = false,
        CancellationToken token = default)
    {
        var city = (cityName ?? string.Empty).Trim();

        if (city.Length == 0)
        {
            LastError = "还没设置城市";
            return null;
        }

        var cache = TryReadCache(city);

        if (!forceRefresh && cache is not null && DateTimeOffset.Now - cache.FetchedAt < CacheTtl)
        {
            LastError = null;
            return cache.ToSnapshot();
        }

        // 优先使用设置里缓存的经纬度，省掉一次地理编码请求
        (double Latitude, double Longitude)? coordinates = null;
        var settings = SettingsStore.Current;

        if (settings.WeatherLatitude is { } latitude &&
            settings.WeatherLongitude is { } longitude &&
            string.Equals(settings.WeatherCity?.Trim(), city, StringComparison.OrdinalIgnoreCase))
        {
            coordinates = (latitude, longitude);
        }

        coordinates ??= await GeocodeAsync(city, token);

        if (coordinates is null)
        {
            if (cache is not null)
            {
                Log.Warn($"天气不可用（{LastError}），退回缓存：{cache.FetchedAt:MM-dd HH:mm}");
                return cache.ToSnapshot();
            }

            Log.Warn($"天气不可用：{LastError}");
            return null;
        }

        var fetched = await FetchAsync(city, coordinates.Value, token);

        if (fetched is null)
        {
            if (cache is not null)
            {
                Log.Warn($"天气不可用（{LastError}），退回缓存：{cache.FetchedAt:MM-dd HH:mm}");
                return cache.ToSnapshot();
            }

            Log.Warn($"天气不可用：{LastError}");
            return null;
        }

        WriteCache(fetched);
        settings.WeatherFetchedAt = fetched.FetchedAt.LocalDateTime;
        SettingsStore.Save();

        LastError = null;
        return fetched;
    }

    /// <summary>WMO weather_code → 状况大类 + 中文描述。</summary>
    public static (WeatherKind Kind, string Description) Classify(int? code) => code switch
    {
        0 => (WeatherKind.Clear, "晴"),
        1 => (WeatherKind.PartlyCloudy, "晴间多云"),
        2 => (WeatherKind.PartlyCloudy, "多云"),
        3 => (WeatherKind.Cloudy, "阴"),
        45 or 48 => (WeatherKind.Fog, "雾"),
        51 or 53 or 55 => (WeatherKind.Drizzle, "毛毛雨"),
        56 or 57 => (WeatherKind.FreezingRain, "冻毛毛雨"),
        61 => (WeatherKind.Rain, "小雨"),
        63 => (WeatherKind.Rain, "中雨"),
        65 => (WeatherKind.Rain, "大雨"),
        66 or 67 => (WeatherKind.FreezingRain, "冻雨"),
        71 => (WeatherKind.Snow, "小雪"),
        73 => (WeatherKind.Snow, "中雪"),
        75 => (WeatherKind.Snow, "大雪"),
        77 => (WeatherKind.Snow, "米雪"),
        80 => (WeatherKind.Rain, "阵雨"),
        81 => (WeatherKind.Rain, "强阵雨"),
        82 => (WeatherKind.Rain, "暴雨"),
        85 => (WeatherKind.Snow, "小阵雪"),
        86 => (WeatherKind.Snow, "大阵雪"),
        95 => (WeatherKind.Thunderstorm, "雷阵雨"),
        96 or 99 => (WeatherKind.Thunderstorm, "雷暴伴冰雹"),
        _ => (WeatherKind.Unknown, "未知")
    };

    /// <summary>状况大类对应自绘图标（Assets/IconPacks/app 下的 SVG 键）。</summary>
    public static string IconKey(WeatherKind kind) => kind switch
    {
        WeatherKind.Clear => "app/weather-clear",
        WeatherKind.PartlyCloudy => "app/weather-partly",
        WeatherKind.Cloudy or WeatherKind.Fog or WeatherKind.Unknown => "app/weather-cloudy",
        WeatherKind.Drizzle or WeatherKind.Rain or WeatherKind.FreezingRain => "app/weather-rain",
        WeatherKind.Snow => "app/weather-snow",
        WeatherKind.Thunderstorm => "app/weather-thunder",
        _ => "app/weather-cloudy"
    };

    // ————— 网络 —————

    private static async Task<WeatherSnapshot?> FetchAsync(string city,
        (double Latitude, double Longitude) coordinates, CancellationToken token)
    {
        var url = "https://api.open-meteo.com/v1/forecast" +
                  $"?latitude={coordinates.Latitude.ToString(CultureInfo.InvariantCulture)}" +
                  $"&longitude={coordinates.Longitude.ToString(CultureInfo.InvariantCulture)}" +
                  "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                  "&timezone=auto&forecast_days=1";

        try
        {
            using var document = await GetJsonAsync(url, token);
            var root = document.RootElement;

            if (!root.TryGetProperty("current", out var current))
            {
                LastError = "天气接口没有返回 current 数据";
                return null;
            }

            var code = IntOf(current, "weather_code");
            var (kind, description) = Classify(code);

            double? high = null;
            double? low = null;

            if (root.TryGetProperty("daily", out var daily))
            {
                high = ElementDouble(daily, "temperature_2m_max", 0);
                low = ElementDouble(daily, "temperature_2m_min", 0);
            }

            return new WeatherSnapshot(
                city,
                DoubleOf(current, "temperature_2m") ?? 0,
                DoubleOf(current, "apparent_temperature") ?? 0,
                DoubleOf(current, "relative_humidity_2m") ?? 0,
                DoubleOf(current, "wind_speed_10m") ?? 0,
                kind,
                description,
                high,
                low,
                DateTimeOffset.Now,
                code);
        }
        catch (OperationCanceledException)
        {
            LastError = "天气请求超时";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"天气请求失败：{ex.Message}";
            Log.Warn($"获取天气失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    // ————— 缓存 —————

    private static WeatherCache? TryReadCache(string city)
    {
        try
        {
            var file = CacheFile;
            if (!File.Exists(file)) return null;

            var cache = JsonSerializer.Deserialize<WeatherCache>(File.ReadAllText(file), CacheOptions);

            return cache is { Version: WeatherCache.CurrentVersion } &&
                   string.Equals(cache.City, city, StringComparison.OrdinalIgnoreCase)
                ? cache
                : null;
        }
        catch (Exception ex)
        {
            Log.Info($"读取天气缓存失败：{ex.Message}");
            return null;
        }
    }

    private static void WriteCache(WeatherSnapshot snapshot)
    {
        try
        {
            var cache = new WeatherCache
            {
                City = snapshot.City,
                Temperature = snapshot.Temperature,
                ApparentTemperature = snapshot.ApparentTemperature,
                Humidity = snapshot.Humidity,
                WindSpeed = snapshot.WindSpeed,
                Kind = snapshot.Kind,
                Description = snapshot.Description,
                TodayHigh = snapshot.TodayHigh,
                TodayLow = snapshot.TodayLow,
                WeatherCode = snapshot.WeatherCode,
                FetchedAt = snapshot.FetchedAt
            };

            IO.AtomicFile.WriteAllText(CacheFile, JsonSerializer.Serialize(cache, CacheOptions));
        }
        catch (Exception ex)
        {
            Log.Info($"写入天气缓存失败：{ex.Message}");
        }
    }

    /// <summary>天气缓存文件结构。</summary>
    private sealed class WeatherCache
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;

        public string City { get; set; } = string.Empty;

        public double Temperature { get; set; }

        public double ApparentTemperature { get; set; }

        public double Humidity { get; set; }

        public double WindSpeed { get; set; }

        public WeatherKind Kind { get; set; }

        public string Description { get; set; } = string.Empty;

        public double? TodayHigh { get; set; }

        public double? TodayLow { get; set; }

        public int? WeatherCode { get; set; }

        public DateTimeOffset FetchedAt { get; set; }

        public WeatherSnapshot ToSnapshot() => new(
            City, Temperature, ApparentTemperature, Humidity, WindSpeed,
            Kind, Description, TodayHigh, TodayLow, FetchedAt, WeatherCode);
    }

    // ————— JSON 取值助手 —————

    private static int? IntOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static double? DoubleOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    private static double? ElementDouble(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        if (index >= array.GetArrayLength()) return null;

        var value = array[index];
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : null;
    }
}
