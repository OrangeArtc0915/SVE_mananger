using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Nexus;
using StardewLauncher.Core.Smapi;

namespace StardewLauncher.Core.Updater;

/// <summary>一次启动器更新检查的结果。</summary>
public sealed record LauncherUpdateInfo(
    bool HasUpdate,
    string LatestVersion,
    string Notes,
    string DownloadUrl,
    string AssetName,
    string SourceName,
    string? Error)
{
    /// <summary>能否一键自动安装：只有单文件 exe 资产才能直接替换自身。</summary>
    public bool CanAutoInstall => HasUpdate && !string.IsNullOrWhiteSpace(DownloadUrl);

    /// <summary>发布页地址，供用户手动下载。</summary>
    public static string ReleasePageUrl(DownloadSource source) => source == DownloadSource.Gitee
        ? AppInfo.GiteeUrl + "/releases"
        : AppInfo.GitHubUrl + "/releases";
}

/// <summary>自动更新的执行结果。</summary>
public sealed record UpdateInstallResult(bool Ok, string Message);

/// <summary>
/// 启动器自身的检查更新与自动更新。
///
/// <para>
/// 版本来源按用户选择的下载源尝试，失败自动换另一个源：
/// Gitee 走它自己的 release 接口（含附件列表），GitHub 走 releases/latest 的重定向拿 tag，
/// 再读发布页的资产列表（不用 API，避开 60 次/小时的限流）。
/// </para>
///
/// <para>
/// 自动更新只替换单文件的 <c>StardewLauncher.exe</c>：先在原地下载成 <c>.new</c>，
/// 再交给一个临时的 cmd 脚本 —— 它等本进程退出后替换文件并重新启动，
/// 替换失败会把旧文件换回去。数据目录（设置、实例、存档备份）不动。
/// </para>
/// </summary>
public static class LauncherUpdater
{
    private const string UserAgent = "StardewLauncher";

    /// <summary>可自动安装的资产名必须以它开头（例如 StardewLauncher.exe、StardewLauncher-v1.6.0-win-x64.exe）。</summary>
    private const string ExePrefix = "StardewLauncher";

    /// <summary>下载下来的新版本先落到这个后缀，替换时再改名覆盖。</summary>
    private const string StagedSuffix = ".new";

    /// <summary>替换前的旧文件后缀，脚本会在成功启动后删掉它。</summary>
    private const string BackupSuffix = ".old";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>当前进程的可执行文件路径。单文件发布时就是发布包里的那个 exe。</summary>
    public static string? CurrentExecutable => Environment.ProcessPath;

    // ————— 检查 —————

    /// <summary>检查有没有新版本。取不到任何源时返回带 Error 的结果，不抛异常。</summary>
    public static async Task<LauncherUpdateInfo> CheckAsync(CancellationToken token = default)
    {
        var failures = new List<string>();

        foreach (var source in SourceOrder())
        {
            var info = await TryCheckSourceAsync(source, token).ConfigureAwait(false);

            if (info is not null)
            {
                Log.Info($"启动器更新检查：{info.SourceName} 最新 {info.LatestVersion}，" +
                         $"当前 {AppInfo.Version}，{(info.HasUpdate ? "有新版本" : "已是最新")}");

                return info;
            }

            failures.Add($"{SourceName(source)} 取不到版本信息");
        }

        var detail = string.Join("；", failures);
        Log.Warn($"启动器更新检查失败：{detail}");

        return new LauncherUpdateInfo(false, AppInfo.Version, string.Empty, string.Empty, string.Empty, string.Empty, detail);
    }

    /// <summary>按设置里的下载源优先、另一个兜底。</summary>
    private static IEnumerable<DownloadSource> SourceOrder()
    {
        var preferred = SettingsStore.Current.DownloadSource;

        yield return preferred;
        yield return preferred == DownloadSource.Gitee ? DownloadSource.GitHub : DownloadSource.Gitee;
    }

    private static string SourceName(DownloadSource source) =>
        source == DownloadSource.Gitee ? "Gitee" : "GitHub";

    private static async Task<LauncherUpdateInfo?> TryCheckSourceAsync(DownloadSource source, CancellationToken token)
    {
        try
        {
            var release = source == DownloadSource.Gitee
                ? await ReadGiteeReleaseAsync(token).ConfigureAwait(false)
                : await ReadGitHubReleaseAsync(token).ConfigureAwait(false);

            if (release is null) return null;

            var latest = Normalize(release.Tag);
            if (string.IsNullOrWhiteSpace(latest)) return null;

            var hasUpdate = SemVer.IsNewer(latest, AppInfo.Version);
            var (assetName, url) = hasUpdate ? PickAsset(release.Assets) : (string.Empty, string.Empty);

            return new LauncherUpdateInfo(
                hasUpdate,
                latest,
                release.Notes ?? string.Empty,
                url,
                assetName,
                SourceName(source),
                null);
        }
        catch (Exception ex)
        {
            Log.Warn($"{SourceName(source)} 检查启动器更新异常：{ex.Message}");
            return null;
        }
    }

    private sealed record ReleaseInfo(string Tag, string? Notes, List<(string Name, string Url)> Assets);

    // ————— Gitee —————

    private sealed class GiteeRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("id")] public long Id { get; set; }
    }

    private sealed class GiteeAttachment
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = string.Empty;
    }

    private static async Task<ReleaseInfo?> ReadGiteeReleaseAsync(CancellationToken token)
    {
        if (RepoPathOf(AppInfo.GiteeUrl) is not { } repo) return null;

        var json = await HttpDownloader.GetStringAsync(
            $"https://gitee.com/api/v5/repos/{repo}/releases/latest", UserAgent, token).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json)) return null;

        var release = JsonSerializer.Deserialize<GiteeRelease>(json, Json);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return null;

        var assets = new List<(string, string)>();

        // 发行版附件要单独查一次：releases/latest 里返回的 assets 是自动生成的源码包，不是用户上传的附件
        var attachJson = await HttpDownloader.GetStringAsync(
            $"https://gitee.com/api/v5/repos/{repo}/releases/{release.Id}/attach_files", UserAgent, token)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(attachJson))
        {
            var attachments = JsonSerializer.Deserialize<List<GiteeAttachment>>(attachJson, Json) ?? [];

            foreach (var attachment in attachments)
            {
                if (string.IsNullOrWhiteSpace(attachment.Name) || string.IsNullOrWhiteSpace(attachment.Url)) continue;
                assets.Add((attachment.Name, attachment.Url));
            }
        }

        return new ReleaseInfo(release.TagName, release.Body, assets);
    }

    // ————— GitHub —————

    private static async Task<ReleaseInfo?> ReadGitHubReleaseAsync(CancellationToken token)
    {
        if (RepoPathOf(AppInfo.GitHubUrl) is not { } repo) return null;

        // 用重定向拿 tag：走网页而不是 API，避开未认证请求 60 次/小时的限流
        var location = await HttpDownloader
            .GetRedirectLocationAsync($"{AppInfo.GitHubUrl}/releases/latest", UserAgent, token)
            .ConfigureAwait(false);

        var tag = TagFromLocation(location);
        if (string.IsNullOrWhiteSpace(tag)) return null;

        // 发布页的资产列表由这个片段加载，直接取它就能拿到资产名与下载地址
        var html = await HttpDownloader.GetStringAsync(
            $"https://github.com/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", UserAgent, token)
            .ConfigureAwait(false);

        var assets = new List<(string, string)>();

        if (!string.IsNullOrWhiteSpace(html))
        {
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(html, @"href=""([^""]*/releases/download/[^""]+)"""))
            {
                // 这个片段里的 href 是根相对路径（/owner/repo/releases/download/...），
                // 不补成绝对地址的话下载器会直接报错
                var url = match.Groups[1].Value;
                if (url.StartsWith('/')) url = "https://github.com" + url;

                var name = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);

                if (!string.IsNullOrWhiteSpace(name)) assets.Add((name, url));
            }
        }

        return new ReleaseInfo(tag, null, assets);
    }

    /// <summary>从 releases/tag/v1.4.0 这样的地址里取出 tag。</summary>
    private static string? TagFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;

        var index = location.LastIndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : location[(index + 5)..].Trim('/');
    }

    private static string? RepoPathOf(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri)) return null;

        var path = uri.AbsolutePath.Trim('/');
        return path.Contains('/') ? path : null;
    }

    /// <summary>把 v1.5.0 这类 tag 规范成 1.5.0。</summary>
    private static string Normalize(string tag) => tag.Trim().TrimStart('v', 'V');

    /// <summary>
    /// 挑出可自动安装的资产：名字是 <c>StardewLauncher.exe</c>，或是以它开头、以 .exe 结尾的
    /// （例如 <c>StardewLauncher-v1.6.0-win-x64.exe</c>）。
    ///
    /// <para>
    /// 刻意不认"随便哪个 .exe"：发布页上可能同时有安装器、依赖包之类的其它可执行文件，
    /// 拿它替换自己的结果就是启动器被换成一个无关程序。压缩包也不自动装 —— 那要解压再挑文件，
    /// 出问题更难收拾。
    /// </para>
    /// </summary>
    private static (string Name, string Url) PickAsset(List<(string Name, string Url)> assets)
    {
        foreach (var asset in assets)
        {
            if (!asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!asset.Name.StartsWith(ExePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            return asset;
        }

        return (string.Empty, string.Empty);
    }

    // ————— 下载并安装 —————

    /// <summary>
    /// 下载新版本并安排替换。返回 Ok 时调用方应立即退出进程 —— 脚本会等它退出后再替换并重新启动。
    /// </summary>
    public static async Task<UpdateInstallResult> DownloadAndInstallAsync(
        string url, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var target = CurrentExecutable;

        if (string.IsNullOrWhiteSpace(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return new UpdateInstallResult(false, "当前不是以单文件 exe 运行，无法自动替换，请手动下载新版本。");

        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(directory))
            return new UpdateInstallResult(false, "取不到程序所在目录。");

        var staged = target + StagedSuffix;

        try
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
        catch (Exception ex)
        {
            return new UpdateInstallResult(false, $"清理上次的下载残留失败：{ex.Message}");
        }

        var download = await ResumableDownloader.DownloadAsync(url, staged, progress, token).ConfigureAwait(false);

        if (!download.Success || !File.Exists(staged))
            return new UpdateInstallResult(false, $"下载失败：{download.Message}");

        if (!LooksLikeExecutable(staged, out var reason))
        {
            TryDelete(staged);
            return new UpdateInstallResult(false, $"下载到的文件不像是启动器：{reason}");
        }

        try
        {
            var script = WriteInstallScript(target, staged);
            StartScript(script);

            Log.Info($"更新已就绪，退出后由脚本替换并重启：{target}");

            return new UpdateInstallResult(true, "正在重启以完成更新…");
        }
        catch (Exception ex)
        {
            Log.Error("安排自动更新失败", ex);
            TryDelete(staged);
            return new UpdateInstallResult(false, $"安排替换失败：{ex.Message}");
        }
    }

    /// <summary>粗查一下：能删掉重命名说明没被占用，且文件头是 MZ（可执行文件）。</summary>
    private static bool LooksLikeExecutable(string path, out string reason)
    {
        reason = string.Empty;

        try
        {
            var info = new FileInfo(path);

            // 单文件 exe 自带运行时有 100 MB 以上，明显偏小说明下到的不是它
            if (info.Length < 5 * 1024 * 1024)
            {
                reason = $"文件只有 {info.Length / 1024} KB，太小了";
                return false;
            }

            using var stream = File.OpenRead(path);
            var header = new byte[2];

            if (stream.Read(header, 0, 2) != 2 || header[0] != 'M' || header[1] != 'Z')
            {
                reason = "文件头不是 MZ";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 写一个临时 cmd：等本进程退出 → 旧文件改名备份 → 新文件就位 → 重新启动；
    /// 就位失败就把备份换回去，尽量不留下一个打不开的目录。
    /// </summary>
    private static string WriteInstallScript(string target, string staged)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"stardewlauncher-update-{Environment.ProcessId}.cmd");
        var pid = Environment.ProcessId;

        var lines = new[]
        {
            "@echo off",
            "setlocal",
            $"set \"TARGET={target}\"",
            $"set \"STAGED={staged}\"",
            "set \"BACKUP=%TARGET%.old\"",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
            "if not errorlevel 1 (",
            "  ping -n 2 127.0.0.1 >nul",
            "  goto wait",
            ")",
            "if exist \"%BACKUP%\" del /f /q \"%BACKUP%\" >nul 2>&1",
            "move /y \"%TARGET%\" \"%BACKUP%\" >nul 2>&1",
            "move /y \"%STAGED%\" \"%TARGET%\" >nul 2>&1",
            "if not exist \"%TARGET%\" (",
            "  move /y \"%BACKUP%\" \"%TARGET%\" >nul 2>&1",
            "  exit /b 1",
            ")",
            "start \"\" \"%TARGET%\"",
            "ping -n 3 127.0.0.1 >nul",
            "if exist \"%BACKUP%\" del /f /q \"%BACKUP%\" >nul 2>&1",
            "del /f /q \"%~f0\" >nul 2>&1"
        };

        File.WriteAllLines(scriptPath, lines);
        return scriptPath;
    }

    private static void StartScript(string scriptPath)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(start);
    }

    // ————— 收尾 —————

    /// <summary>
    /// 启动时清理上次更新留下的临时文件：<c>.old</c>（替换成功后的旧版本）与
    /// 半截的 <c>.new</c>（下载中断或被判定不合法）。清理失败只记日志。
    /// </summary>
    public static void CleanupStaleFiles()
    {
        if (CurrentExecutable is not { } target) return;

        foreach (var suffix in new[] { BackupSuffix, StagedSuffix })
        {
            var path = target + suffix;
            if (TryDelete(path)) Log.Info($"已清理上次更新留下的文件：{Path.GetFileName(path)}");
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"删除文件失败 {path}：{ex.Message}");
            return false;
        }
    }
}
