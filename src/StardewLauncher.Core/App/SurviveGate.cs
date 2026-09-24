using System.Diagnostics;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Smapi;

namespace StardewLauncher.Core.App;

/// <summary>门锁的判定结果。</summary>
public enum SurviveState
{
    /// <summary>允许启动。</summary>
    Allowed,

    /// <summary>远端明确写了 false：停止支持。</summary>
    Denied,

    /// <summary>三个源都拿不到有效配置。</summary>
    Unreachable
}

/// <summary>门锁的判定结果与给用户看的错误代码。</summary>
public sealed record SurviveResult(SurviveState State, string Detail)
{
    public bool Allowed => State == SurviveState.Allowed;

    /// <summary>错误代码：出问题时让用户报这个就行。</summary>
    public string Code => State switch
    {
        SurviveState.Denied => DeniedCode,
        SurviveState.Unreachable => UnreachableCode,
        _ => OkCode
    };

    public const string OkCode = "SVE-M-LOCK-0000";

    public const string DeniedCode = "SVE-M-LOCK-0001";

    public const string UnreachableCode = "SVE-M-LOCK-0002";
}

/// <summary>
/// 远端门锁：读 survive 分支上的配置文件里的 <c>SVE_M_survive</c>。
/// 多个源依次尝试（按国内可达性排序），整轮最多等 budget；
/// 拿不到有效配置一律按拦截处理——这是刻意选的策略，宁可不放行也不误放。
/// </summary>
public static class SurviveGate
{
    /// <summary>配置字段名。</summary>
    public const string FlagName = "SVE_M_survive";

    private const string UserAgent = "StardewLauncher";

    /// <summary>单个源至少要有这么多预算才去尝试，太少的话连 TCP 握手都完不成。</summary>
    private static readonly TimeSpan MinSlice = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// 读取顺序：Gitee 国内最快 → jsDelivr → GitHub raw 兜底。
    /// 换仓库或换分支时这里要一起改。
    /// </summary>
    public static readonly string[] SourceUrls =
    [
        "https://gitee.com/orangearc655743/SVE_mananger_File/raw/survive/survive.txt",
        "https://cdn.jsdelivr.net/gh/OrangeArtc0915/SVE_mananger@survive/survive.txt",
        "https://raw.githubusercontent.com/OrangeArtc0915/SVE_mananger/survive/survive.txt"
    ];

    /// <summary>
    /// 校验是否允许启动。任何情况下都不向外抛异常，失败一律给 Unreachable。
    /// </summary>
    public static async Task<SurviveResult> CheckAsync(TimeSpan budget, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        var failures = new List<string>();

        for (var i = 0; i < SourceUrls.Length; i++)
        {
            var url = SourceUrls[i];
            var remaining = budget - watch.Elapsed;

            // 把剩余预算平分给还没试过的源。整轮预算一起用的话，一个卡住或 404 重试的源
            // 就能把后面的源全部饿死，而各源的可达性其实是互相独立的。
            var slice = TimeSpan.FromMilliseconds(remaining.TotalMilliseconds / (SourceUrls.Length - i));

            if (slice < MinSlice)
            {
                failures.Add($"{HostOf(url)} 未在时限内尝试");
                continue;
            }

            var text = await FetchAsync(url, slice, token);

            if (text is null)
            {
                failures.Add($"{HostOf(url)} 无响应");
                continue;
            }

            var allowed = TryParse(text, out var found, out var raw);

            if (!found)
            {
                failures.Add($"{HostOf(url)} 配置里没有 {FlagName}");
                continue;
            }

            if (allowed is null)
            {
                failures.Add($"{HostOf(url)} 的值不认识：{raw}");
                continue;
            }

            Log.Info($"门锁：{HostOf(url)} 返回 {FlagName}: {raw}");

            return allowed.Value
                ? new SurviveResult(SurviveState.Allowed, $"{HostOf(url)} · {FlagName}: {raw}")
                : new SurviveResult(SurviveState.Denied, $"{HostOf(url)} · {FlagName}: {raw}");
        }

        var detail = failures.Count == 0 ? "没有任何可用来源" : string.Join("；", failures);

        Log.Warn($"门锁：取不到有效配置（{detail}）");
        return new SurviveResult(SurviveState.Unreachable, detail);
    }

    /// <summary>
    /// 解析配置文本。支持 <c>SVE_M_survive: true</c> 这种写法（键不分大小写，
    /// true/false 也认 1/0、yes/no、on/off、是/否）。找不到字段时 found 为 false。
    /// </summary>
    public static bool? TryParse(string? text, out bool found, out string raw)
    {
        found = false;
        raw = string.Empty;

        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var separator = trimmed.IndexOf(':');
            if (separator <= 0) continue;

            var key = trimmed[..separator].Trim().Trim('"', '\'');
            if (!string.Equals(key, FlagName, StringComparison.OrdinalIgnoreCase)) continue;

            var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'').TrimEnd(',');

            found = true;
            raw = value;

            return value.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" or "是" or "开" => true,
                "false" or "0" or "no" or "off" or "否" or "关" => false,
                _ => null
            };
        }

        return null;
    }

    private static async Task<string?> FetchAsync(string url, TimeSpan timeout, CancellationToken token)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(timeout);

        try
        {
            return await HttpDownloader.GetStringAsync(url, UserAgent, source.Token);
        }
        catch (Exception ex)
        {
            Log.Warn($"门锁：读取 {HostOf(url)} 异常（{ex.Message}）");
            return null;
        }
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
