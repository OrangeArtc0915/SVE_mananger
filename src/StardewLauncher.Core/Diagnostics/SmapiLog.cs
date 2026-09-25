using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Diagnostics;

/// <summary>日志里某个来源（SMAPI、游戏本体或某个 Mod）的错误与警告。</summary>
public sealed record LogSourceIssues(string Source, int Errors, int Warnings, IReadOnlyList<string> Samples)
{
    public bool HasErrors => Errors > 0;

    public int Total => Errors + Warnings;

    public string CountText => Errors > 0 ? $"{Errors} 条错误、{Warnings} 条警告" : $"{Warnings} 条警告";
}

/// <summary>日志里识别到的一个已加载 Mod。</summary>
public sealed record LoggedMod(string Name, string UniqueId);

/// <summary>一份 SMAPI 日志的分析结果。</summary>
public sealed class SmapiLogReport
{
    /// <summary>有没有找到日志文件。没有时 Error 说明原因。</summary>
    public bool Found { get; init; }

    public string? Error { get; init; }

    public string FilePath { get; init; } = "";

    public DateTime? ModifiedAt { get; init; }

    public string? GameVersion { get; init; }

    public string? SmapiVersion { get; init; }

    public string? ModsPath { get; init; }

    /// <summary>日志开始时间，已换算成本机时区。</summary>
    public DateTime? StartedAt { get; init; }

    public int LineCount { get; init; }

    public IReadOnlyList<LoggedMod> Mods { get; init; } = [];

    /// <summary>SMAPI 明确跳过的 Mod（不兼容之类），原文一行一条。</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>按错误数从多到少排的来源。</summary>
    public IReadOnlyList<LogSourceIssues> Sources { get; init; } = [];

    /// <summary>去重后的关键错误原文。</summary>
    public IReadOnlyList<string> Highlights { get; init; } = [];

    public int TotalErrors => Sources.Sum(item => item.Errors);

    public int TotalWarnings => Sources.Sum(item => item.Warnings);

    public bool Clean => TotalErrors == 0 && TotalWarnings == 0;

    public string FileName => string.IsNullOrWhiteSpace(FilePath) ? "" : Path.GetFileName(FilePath);

    public string SummaryText
    {
        get
        {
            if (!Found) return Error ?? "读不到日志";

            var head = Clean
                ? "整份日志里没有错误和警告"
                : $"共 {TotalErrors} 条错误、{TotalWarnings} 条警告";

            return $"{head}　{LineCount} 行，日志里加载了 {Mods.Count} 个 Mod";
        }
    }

    /// <summary>纯文本报告，供复制或贴给别人看。</summary>
    public string ToText()
    {
        var text = new StringBuilder();

        text.AppendLine("SMAPI 日志分析");
        text.AppendLine($"日志文件：{FilePath}");

        if (!Found)
        {
            text.AppendLine($"结果：{Error}");
            return text.ToString();
        }

        text.AppendLine($"文件修改时间：{ModifiedAt:yyyy-MM-dd HH:mm:ss}");
        if (StartedAt is { } started) text.AppendLine($"日志开始时间：{started:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(GameVersion)) text.AppendLine($"游戏版本：{GameVersion}");
        if (!string.IsNullOrWhiteSpace(SmapiVersion)) text.AppendLine($"SMAPI 版本：{SmapiVersion}");
        if (!string.IsNullOrWhiteSpace(ModsPath)) text.AppendLine($"Mods 目录：{ModsPath}");
        text.AppendLine(SummaryText);
        text.AppendLine();

        if (Sources.Count > 0)
        {
            text.AppendLine("———— 按来源 ————");

            foreach (var source in Sources)
            {
                text.AppendLine($"{source.Source}：{source.CountText}");

                foreach (var sample in source.Samples) text.AppendLine($"    {sample}");
            }

            text.AppendLine();
        }

        if (Skipped.Count > 0)
        {
            text.AppendLine("———— 被跳过的 Mod ————");

            foreach (var line in Skipped) text.AppendLine(line);

            text.AppendLine();
        }

        if (Highlights.Count > 0)
        {
            text.AppendLine("———— 关键错误 ————");

            foreach (var line in Highlights) text.AppendLine(line);

            text.AppendLine();
        }

        if (Mods.Count > 0)
        {
            text.AppendLine("———— 日志里加载的 Mod ————");

            foreach (var mod in Mods) text.AppendLine($"{mod.Name}　{mod.UniqueId}");
        }

        return text.ToString();
    }
}

/// <summary>
/// SMAPI 日志的定位与解析。
///
/// <para>
/// 日志位置按 SMAPI 官方的说法找（Steam / GOG 与 Xbox 版各一处），
/// 文件名是 <c>SMAPI-crash.txt</c> 与 <c>SMAPI-latest.txt</c>，取最近改动的那一份。
/// 行格式是 <c>[时间 级别 来源] 消息</c>，来源就是「谁在报错」的依据 —— 这也是 smapi.io/log
/// 的做法：SMAPI、游戏本体与每个 Mod 各自一个来源。
/// </para>
/// </summary>
public static class SmapiLogReader
{
    private const int MaxSamples = 3;

    private const int MaxHighlights = 8;

    private const int MaxMessageLength = 200;

    /// <summary>Xbox 版把数据放在包容器里，路径与 Steam / GOG 不同。</summary>
    private const string XboxPackageFolder = "ConcernedApe.StardewValleyPC_0c8vynj4cqe4e";

    private static readonly Regex LinePattern = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2})\s+(?<level>[A-Z]+)\s+(?<source>[^\]]+)\]\s?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HeaderPattern = new(
        @"^SMAPI (?<smapi>\S+) with Stardew Valley (?<game>\S+)(?: build (?<build>\S+))? on (?<os>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModPattern = new(
        @"^(?<name>.+?) \(from .+?, ID: (?<id>[^,)]+)(?:, assembly version: [^)]*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>找到最近的一份 SMAPI 日志。找不到返回 null。</summary>
    public static string? FindLogFile()
    {
        FileInfo? newest = null;

        foreach (var directory in LogDirectories())
        {
            foreach (var name in new[] { "SMAPI-crash.txt", "SMAPI-latest.txt" })
            {
                try
                {
                    var info = new FileInfo(Path.Combine(directory, name));
                    if (!info.Exists) continue;

                    if (newest is null || info.LastWriteTime > newest.LastWriteTime) newest = info;
                }
                catch (Exception ex)
                {
                    Log.Warn($"查看 SMAPI 日志失败：{name}（{ex.Message}）");
                }
            }
        }

        return newest?.FullName;
    }

    private static IEnumerable<string> LogDirectories()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
            yield return Path.Combine(roaming, "StardewValley", "ErrorLogs");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "Packages", XboxPackageFolder, "LocalCache", "Roaming",
                "StardewValley", "ErrorLogs");
    }

    /// <summary>读并分析一份日志。path 为空时自己找最近的一份。</summary>
    public static SmapiLogReport Analyze(string? path = null, CancellationToken token = default)
    {
        path ??= FindLogFile();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new SmapiLogReport
            {
                Found = false,
                Error = "没有找到 SMAPI 日志。用 SMAPI 启动过游戏之后，日志才会生成。"
            };
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 SMAPI 日志失败：{path}（{ex.Message}）");

            return new SmapiLogReport
            {
                Found = false,
                FilePath = path,
                Error = $"读不了这个日志文件：{ex.Message}"
            };
        }

        return Parse(path, lines, token);
    }

    private static SmapiLogReport Parse(string path, string[] lines, CancellationToken token)
    {
        var sources = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        var mods = new List<LoggedMod>();
        var skipped = new List<string>();
        var highlights = new List<string>();
        var seenErrors = new HashSet<string>(StringComparer.Ordinal);

        string? gameVersion = null;
        string? smapiVersion = null;
        string? modsPath = null;
        DateTime? startedAt = null;

        var inSkippedSection = false;
        string? currentSource = null;

        foreach (var raw in lines)
        {
            token.ThrowIfCancellationRequested();

            // 有的 Mod 会往日志里写空字符，直接照单全收会让后面的文本处理出错
            var line = raw.Replace("\0", string.Empty).TrimEnd();
            if (line.Length == 0) continue;

            var match = LinePattern.Match(line);

            if (!match.Success)
            {
                // 异常堆栈这类多行消息，续行没有 [时间 级别 来源] 前缀。
                // 它属于上一条消息本身，只并进样例，不能再算一条 ——
                // 堆栈动辄几十行，算进去错误数就被吹大了，按来源排名也就失去意义
                if (currentSource is not null) AppendContinuation(sources, currentSource, line.Trim());

                continue;
            }

            var level = match.Groups["level"].Value;
            var source = match.Groups["source"].Value.Trim();
            var message = match.Groups["message"].Value.Trim();

            currentSource = source;

            if (inSkippedSection && IsProblem(level) && source.Equals("SMAPI", StringComparison.OrdinalIgnoreCase))
                skipped.Add(message);
            else
                inSkippedSection = false;

            if (message.StartsWith("Skipped ", StringComparison.Ordinal) &&
                message.Contains(" mods:", StringComparison.Ordinal))
            {
                inSkippedSection = true;
            }

            if (message.Contains("because it's no longer compatible", StringComparison.OrdinalIgnoreCase) &&
                !skipped.Contains(message))
            {
                skipped.Add(message);
            }

            if (HeaderPattern.Match(message) is { Success: true } header)
            {
                smapiVersion = header.Groups["smapi"].Value;
                gameVersion = header.Groups["game"].Value;
            }

            if (message.StartsWith("Mods go here: ", StringComparison.Ordinal))
                modsPath = message["Mods go here: ".Length..].Trim();

            if (message.StartsWith("Log started at ", StringComparison.Ordinal))
                startedAt = ParseStartedAt(message["Log started at ".Length..]);

            if (message.Contains("(from ", StringComparison.Ordinal) &&
                ModPattern.Match(message) is { Success: true } mod)
            {
                mods.Add(new LoggedMod(mod.Groups["name"].Value.Trim(), mod.Groups["id"].Value.Trim()));
            }

            if (!IsProblem(level)) continue;

            Accumulate(sources, source, level, message);

            if (level is "ERROR" or "ALERT" && seenErrors.Add(message) && highlights.Count < MaxHighlights)
                highlights.Add($"[{level}] {source}：{Truncate(message)}");
        }

        var ordered = sources
            .Select(pair => new LogSourceIssues(pair.Key, pair.Value.Errors, pair.Value.Warnings, pair.Value.Samples))
            .OrderByDescending(item => item.Errors)
            .ThenByDescending(item => item.Warnings)
            .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DateTime? modified = null;
        try { modified = File.GetLastWriteTime(path); }
        catch (Exception ex) { Log.Warn($"读取日志修改时间失败：{ex.Message}"); }

        return new SmapiLogReport
        {
            Found = true,
            FilePath = path,
            ModifiedAt = modified,
            GameVersion = gameVersion,
            SmapiVersion = smapiVersion,
            ModsPath = modsPath,
            StartedAt = startedAt,
            LineCount = lines.Length,
            Mods = mods,
            Skipped = skipped,
            Sources = ordered,
            Highlights = highlights
        };
    }

    /// <summary>错误、警告与 SMAPI 的 ALERT 都算「有问题」，TRACE / DEBUG / INFO 不算。</summary>
    private static bool IsProblem(string level) => level is "ERROR" or "ALERT" or "WARN";

    private static void Accumulate(Dictionary<string, Accumulator> sources, string source, string level,
        string message)
    {
        if (!sources.TryGetValue(source, out var accumulator))
        {
            accumulator = new Accumulator();
            sources[source] = accumulator;
        }

        if (level == "WARN") accumulator.Warnings++;
        else accumulator.Errors++;

        // 同一条消息重复刷屏时只留一份样例，不然三行样例会被同一句话占满
        var sample = Truncate(message);
        if (sample.Length > 0 && accumulator.Samples.Count < MaxSamples && !accumulator.Samples.Contains(sample))
            accumulator.Samples.Add(sample);
    }

    /// <summary>把续行并进该来源最后一条样例里。</summary>
    private static void AppendContinuation(Dictionary<string, Accumulator> sources, string source, string line)
    {
        if (line.Length == 0) return;
        if (!sources.TryGetValue(source, out var accumulator)) return;
        if (accumulator.Samples.Count == 0) return;

        var last = accumulator.Samples[^1];
        if (last.Length >= MaxMessageLength) return;

        accumulator.Samples[^1] = Truncate($"{last} {line}");
    }

    private static DateTime? ParseStartedAt(string text)
    {
        var value = text.Trim();

        // SMAPI 写的是 "2026-02-01T02:49:58 UTC"。DateTime.TryParse 不认这个 "UTC" 后缀，
        // 得先摘掉再按 UTC 解释
        if (value.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase)) value = value[..^4].Trim();

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            Log.Warn($"SMAPI 日志里的开始时间无法解析：{text}");
            return null;
        }

        return utc.ToLocalTime();
    }

    private static string Truncate(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();

        return single.Length <= MaxMessageLength ? single : single[..MaxMessageLength] + "…";
    }

    private sealed class Accumulator
    {
        public int Errors { get; set; }

        public int Warnings { get; set; }

        public List<string> Samples { get; } = [];
    }
}
