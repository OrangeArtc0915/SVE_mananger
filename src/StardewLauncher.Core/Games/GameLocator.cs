using Microsoft.Win32;

namespace StardewLauncher.Core.Games;

/// <summary>一个找到的游戏安装候选，附带来源说明，便于在界面上告诉用户是从哪找到的。</summary>
public sealed record GameCandidate(string Directory, string Source);

/// <summary>
/// 游戏安装目录定位。依次尝试：设置里已记录的目录 → Steam 库 → GOG 注册表 → Xbox → 常见默认路径。
/// </summary>
public static class GameLocator
{
    /// <summary>星露谷在 Steam 上的 AppID。</summary>
    public const int StardewAppId = 413150;

    /// <summary>游戏目录下可能出现的主程序名，两种写法都要认。</summary>
    private static readonly string[] ExecutableNames =
    [
        "Stardew Valley.exe",
        "StardewValley.exe"
    ];

    public static IReadOnlyList<string> SupportedExecutableNames => ExecutableNames;

    /// <summary>列出所有能找到的候选，按可信度排序（越靠前越可信）。</summary>
    public static IReadOnlyList<GameCandidate> FindAll(string? rememberedDirectory = null)
    {
        var found = new List<GameCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? directory, string source)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            if (!IsGameDirectory(directory)) return;

            var full = NormalizePath(directory);
            if (!seen.Add(full)) return;

            found.Add(new GameCandidate(full, source));
        }

        TryAdd(rememberedDirectory, "上次使用");

        foreach (var candidate in FindInSteamLibraries())
            TryAdd(candidate, "Steam");

        TryAdd(FindInGogRegistry(), "GOG");
        TryAdd(@"C:\XboxGames\Stardew Valley", "Xbox");

        foreach (var candidate in CommonPaths())
            TryAdd(candidate, "常见路径");

        return found;
    }

    /// <summary>返回可信度最高的候选。</summary>
    public static GameCandidate? FindBest(string? rememberedDirectory = null)
        => FindAll(rememberedDirectory).FirstOrDefault();

    /// <summary>判断一个目录是否为星露谷安装目录：存在主程序即可。</summary>
    public static bool IsGameDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;

        return ExecutableNames.Any(name =>
            File.Exists(Path.Combine(directory, name)));
    }

    /// <summary>按优先顺序返回主程序路径。</summary>
    public static string? FindExecutable(string directory)
    {
        foreach (var name in ExecutableNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>把路径统一为带反斜杠的完整路径（Steam 注册表里写的是正斜杠）。</summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        try
        {
            return Path.GetFullPath(normalized);
        }
        catch
        {
            return normalized;
        }
    }

    private static IEnumerable<string> FindInSteamLibraries()
    {
        var steamRoot = FindSteamRoot();
        if (steamRoot is null) yield break;

        // 新版库配置在 steamapps 下，旧版在 config 下，两处都读一遍
        var libraryRoots = new List<string>();
        var appLibrary = (string?)null;

        foreach (var configPath in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf")
                 })
        {
            var parsed = VdfReader.ParseFile(configPath);
            if (parsed is null) continue;

            CollectLibraries(parsed, libraryRoots, ref appLibrary, steamRoot);
        }

        // 主安装目录本身也算一个库
        libraryRoots.Insert(0, steamRoot);

        // 列出 AppID 的库优先，其余随后
        var ordered = libraryRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(root => appLibrary is not null &&
                                       root.Equals(appLibrary, StringComparison.OrdinalIgnoreCase));

        foreach (var root in ordered)
            yield return Path.Combine(root, "steamapps", "common", "Stardew Valley");
    }

    private static void CollectLibraries(Dictionary<string, object> parsed,
        List<string> libraryRoots, ref string? appLibrary, string steamRoot)
    {
        var container = parsed.TryGetValue("libraryfolders", out var value) &&
                        value is Dictionary<string, object> inner
            ? inner
            : parsed;

        foreach (var entry in container.Values)
        {
            // 老格式：库路径直接作为值
            if (entry is string rawPath)
            {
                var legacyPath = NormalizePath(rawPath);
                if (legacyPath.Length > 2) libraryRoots.Add(legacyPath);
                continue;
            }

            if (entry is not Dictionary<string, object> library) continue;

            var path = library.TryGetValue("path", out var pathValue) && pathValue is string pathText
                ? NormalizePath(pathText)
                : null;

            if (path is not null && path.Length > 2) libraryRoots.Add(path);

            // 新版会把各 AppID 列在 apps 下，据此可以确认哪个库装着游戏
            if (path is null) continue;
            if (!library.TryGetValue("apps", out var appsValue) ||
                appsValue is not Dictionary<string, object> apps) continue;

            if (apps.ContainsKey(StardewAppId.ToString()))
                appLibrary ??= path;
        }
    }

    private static string? FindSteamRoot()
    {
        foreach (var (hive, subKey, valueName) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.CurrentUser, @"Software\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key?.GetValue(valueName) is not string raw || string.IsNullOrWhiteSpace(raw)) continue;

                var path = NormalizePath(raw);
                if (Directory.Exists(path)) return path;
            }
            catch
            {
                // 单个来源不可用时继续尝试下一个
            }
        }

        return null;
    }

    private static string? FindInGogRegistry()
    {
        foreach (var (hive, subKey) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games"),
                     (Registry.LocalMachine, @"SOFTWARE\GOG.com\Games")
                 })
        {
            try
            {
                using var games = hive.OpenSubKey(subKey);
                if (games is null) continue;

                foreach (var id in games.GetSubKeyNames())
                {
                    using var game = games.OpenSubKey(id);
                    if (game?.GetValue("path") is not string path) continue;

                    var normalized = NormalizePath(path);
                    if (IsGameDirectory(normalized)) return normalized;
                }
            }
            catch
            {
                // 忽略并继续下一个来源
            }
        }

        return null;
    }

    private static IEnumerable<string> CommonPaths()
    {
        yield return @"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley";
        yield return @"C:\Program Files\Steam\steamapps\common\Stardew Valley";
        yield return @"C:\GOG Games\Stardew Valley";
        yield return @"C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley";
        yield return @"D:\Steam\steamapps\common\Stardew Valley";
        yield return @"D:\SteamLibrary\steamapps\common\Stardew Valley";
        yield return @"E:\SteamLibrary\steamapps\common\Stardew Valley";
        yield return @"F:\Steam\steamapps\common\Stardew Valley";
    }
}
