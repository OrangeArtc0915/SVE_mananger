using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
    /// <summary>能否一键自动安装：发布页里有单文件 exe 或整包 zip 才行。</summary>
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
///
/// <para>
/// 资产优先用裸的单文件 exe；只有发布包里没有 exe 时才用整包 zip，先解压出 exe 再替换。
/// 之所以要支持 zip：Gitee 的单个附件上限是 100 MB，而这个自包含单文件 exe 有一百四十多兆，
/// 那边只传得上 zip（约 65 MB）。
/// </para>
/// </summary>
public static class LauncherUpdater
{
    private const string UserAgent = "StardewLauncher";

    /// <summary>可自动安装的资产名必须以它开头（例如 StardewLauncher.exe、StardewLauncher-v1.6.0-win-x64.zip）。</summary>
    private const string ExePrefix = "StardewLauncher";

    /// <summary>下载下来的新版本先落到这个后缀，替换时再改名覆盖。</summary>
    private const string StagedSuffix = ".new";

    /// <summary>替换前的旧文件后缀，脚本会在成功启动后删掉它。</summary>
    private const string BackupSuffix = ".old";

    /// <summary>整包 zip 下载时的临时后缀（解出 exe 后立刻删掉）。</summary>
    private const string ArchiveSuffix = ".zip";

    /// <summary>
    /// 替换失败时由脚本留下的说明文件（紧挨着 exe），下次启动时读给用户看。
    /// 没有它的话，替换失败只会表现为"更新完还是旧版本"，用户完全不知道发生了什么。
    /// </summary>
    private const string FailureNoteSuffix = ".update-failed.txt";

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

    /// <summary>按设置里的「启动器更新线路」优先、另一个兜底。</summary>
    private static IEnumerable<DownloadSource> SourceOrder()
    {
        var preferred = SettingsStore.Current.LauncherUpdateSource;

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
    /// 挑出可自动安装的资产：优先名字是 <c>StardewLauncher.exe</c>，或是以它开头、以 .exe 结尾的
    /// （例如 <c>StardewLauncher-v1.6.0-win-x64.exe</c>）。
    ///
    /// <para>
    /// 没有裸 exe 时退一步用整包 zip（<c>StardewLauncher-v1.6.0-win-x64.zip</c>）——
    /// Gitee 的单个附件上限 100 MB，一百四十多兆的单文件 exe 传不上去，那边只会是 zip。
    /// </para>
    ///
    /// <para>
    /// 刻意不认"随便哪个 .exe / .zip"：发布页上可能同时有安装器、依赖包之类的无关文件，
    /// 拿它替换自己的结果就是启动器被换成一个别的程序。
    /// </para>
    /// </summary>
    private static (string Name, string Url) PickAsset(List<(string Name, string Url)> assets)
    {
        foreach (var suffix in new[] { ".exe", ".zip" })
        {
            foreach (var asset in assets)
            {
                if (!asset.Name.StartsWith(ExePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                return asset;
            }
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
        var fromArchive = IsArchiveUrl(url);

        // 还有别的实例在跑同一个 exe 时，替换一定会失败（Windows 不让覆盖正在运行的 exe，
        // 旧文件也删不掉），先拦下来，别让用户白等一百多兆的下载
        if (OtherInstanceRunning(target) is { } other)
        {
            return new UpdateInstallResult(false,
                $"还有另一个启动器在运行（进程 {other}），现在替换会失败。请先把它关掉再更新。");
        }

        var backup = PickBackupPath(target);
        var downloadPath = fromArchive ? staged + ArchiveSuffix : staged;

        try
        {
            foreach (var leftover in new[] { staged, staged + ArchiveSuffix })
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
        }
        catch (Exception ex)
        {
            return new UpdateInstallResult(false, $"清理上次的下载残留失败：{ex.Message}");
        }

        var download = await ResumableDownloader.DownloadAsync(url, downloadPath, progress, token).ConfigureAwait(false);

        if (!download.Success || !File.Exists(downloadPath))
            return new UpdateInstallResult(false, $"下载失败：{download.Message}");

        string reason;

        if (fromArchive)
        {
            if (!TryExtractExecutable(downloadPath, staged, out reason))
            {
                TryDelete(downloadPath);
                TryDelete(staged);
                return new UpdateInstallResult(false, $"从发布包里取出启动器失败：{reason}");
            }

            TryDelete(downloadPath);
            Log.Info($"已从发布包里解出启动器（{Path.GetFileName(downloadPath)}）");
        }

        if (!LooksLikeExecutable(staged, out reason))
        {
            TryDelete(staged);
            return new UpdateInstallResult(false, $"下载到的文件不像是启动器：{reason}");
        }

        try
        {
            var script = WriteInstallScript(target, staged, backup);
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

    /// <summary>
    /// 下载地址指向的是整包 zip 吗。两个源的下载地址都以 <c>.zip</c> 结尾
    /// （Gitee 是 <c>…/attach_files/…/download/xxx.zip</c>，GitHub 是 <c>…/releases/download/tag/xxx.zip</c>），
    /// 所以看扩展名就够，查询串要先去掉。
    /// </summary>
    private static bool IsArchiveUrl(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;

        return path.EndsWith(ArchiveSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从发布包 zip 里取出 <c>StardewLauncher.exe</c> 写到指定路径。</summary>
    private static bool TryExtractExecutable(string archivePath, string destination, out string reason)
    {
        reason = string.Empty;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            // 发布包里的 exe 在顶层；按文件名找，以后就算套进一层目录也照样能找到
            var entry = archive.Entries.FirstOrDefault(candidate =>
                candidate.Name.StartsWith(ExePrefix, StringComparison.OrdinalIgnoreCase) &&
                candidate.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                reason = $"发布包里没有 {ExePrefix}.exe";
                return false;
            }

            using (var source = entry.Open())
            using (var output = File.Create(destination))
            {
                source.CopyTo(output);
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
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
    /// 写一个临时 cmd：等本进程退出 → 旧文件改名备份 → 新文件就位 → 重新启动。
    ///
    /// <para>
    /// 每一步都校验结果并重试若干次（文件常被安全软件或残留进程短暂占用）：
    /// 新文件没就位就把备份换回去，失败时留一份说明给下次启动的启动器显示 ——
    /// 之前有过"替换失败但静默重启旧版本，用户以为更新没生效"的情况，所以失败必须留痕。
    /// </para>
    /// </summary>
    private static string WriteInstallScript(string target, string staged, string backup)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"stardewlauncher-update-{Environment.ProcessId}.cmd");
        var pid = Environment.ProcessId;
        var note = target + FailureNoteSuffix;

        var lines = new[]
        {
            "@echo off",
            "setlocal enabledelayedexpansion",
            $"set \"TARGET={target}\"",
            $"set \"STAGED={staged}\"",
            $"set \"BACKUP={backup}\"",
            $"set \"NOTE={note}\"",
            "set /a WAIT=0",

            // 等本进程真正退出。tasklist 还列着它就说明没退干净（此时文件也还锁着），每秒问一次
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
            "if errorlevel 1 goto ready",
            "set /a WAIT+=1",
            "if !WAIT! GEQ 90 goto timeout",
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",

            ":timeout",
            "> \"%NOTE%\" echo timeout",
            "del /f /q \"%~f0\" >nul 2>&1",
            "exit /b 2",

            ":ready",
            "set /a TRY=0",

            ":swap",
            "set /a TRY+=1",
            "attrib -r -h -s \"%TARGET%\" >nul 2>&1",
            "attrib -r -h -s \"%STAGED%\" >nul 2>&1",
            "attrib -r -h -s \"%BACKUP%\" >nul 2>&1",
            "del /f /q \"%BACKUP%\" >nul 2>&1",
            "if exist \"%BACKUP%\" goto retry",
            "move /y \"%TARGET%\" \"%BACKUP%\" >nul 2>&1",
            "if exist \"%TARGET%\" goto retry",
            "move /y \"%STAGED%\" \"%TARGET%\" >nul 2>&1",

            // 以"待安装的那份有没有被搬走"为准：搬走了才算就位
            "if exist \"%STAGED%\" goto rollback",
            "goto done",

            ":rollback",
            "if exist \"%TARGET%\" goto retry",
            "move /y \"%BACKUP%\" \"%TARGET%\" >nul 2>&1",
            "if not exist \"%TARGET%\" goto broken",
            "goto retry",

            ":retry",
            "if !TRY! LSS 5 (",
            "  ping -n 2 127.0.0.1 >nul",
            "  goto swap",
            ")",

            // 重试用尽：把旧版本重新打开，并留下失败标记（只写 ASCII 标记，
            // 中文由启动器那边翻译 —— cmd 的 echo 会按控制台代码页写，直接写中文可能变乱码）
            "> \"%NOTE%\" echo locked",
            "if exist \"%TARGET%\" start \"\" \"%TARGET%\"",
            "del /f /q \"%~f0\" >nul 2>&1",
            "exit /b 1",

            ":broken",
            "> \"%NOTE%\" echo broken",
            "del /f /q \"%~f0\" >nul 2>&1",
            "exit /b 3",

            // 换好了：重新打开（就是新版本），再清掉备份与脚本自己
            ":done",
            "start \"\" \"%TARGET%\"",
            "ping -n 4 127.0.0.1 >nul",
            "del /f /q \"%BACKUP%\" >nul 2>&1",
            "del /f /q \"%~f0\" >nul 2>&1",
            "exit /b 0"
        };

        File.WriteAllLines(scriptPath, lines);
        return scriptPath;
    }

    /// <summary>
    /// 挑一个能用的备份文件名。默认是 <c>.old</c>；那个名字被占用且删不掉时换个后缀，
    /// 否则脚本第一步就会卡住，整个替换都做不成。
    /// </summary>
    private static string PickBackupPath(string target)
    {
        for (var index = 0; index < 5; index++)
        {
            var candidate = index == 0 ? target + BackupSuffix : $"{target}{BackupSuffix}{index}";

            if (!File.Exists(candidate)) return candidate;
            if (TryDelete(candidate)) return candidate;
        }

        return $"{target}{BackupSuffix}{Guid.NewGuid():N}";
    }

    /// <summary>有没有别的进程在跑同一个 exe。读不到某个进程的信息就跳过它，不因此拦住用户。</summary>
    private static int? OtherInstanceRunning(string target)
    {
        var name = Path.GetFileNameWithoutExtension(target);

        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;

                try
                {
                    var image = process.MainModule?.FileName;
                    if (string.Equals(image, target, StringComparison.OrdinalIgnoreCase)) return process.Id;
                }
                catch
                {
                    // 权限不够或进程已退出，当作没占用
                }
            }
        }

        return null;
    }

    /// <summary>上次更新是否留有失败标记。只查不取，供体检之类的地方看一眼。</summary>
    public static bool HasPendingFailureNote()
        => CurrentExecutable is { } target && File.Exists(target + FailureNoteSuffix);

    /// <summary>
    /// 取走上次更新失败留下的标记（如果有），并把它删掉，返回给用户看的话。
    /// 调用方负责显示出来 —— 更新失败必须让人看见，不能悄悄停回旧版本。
    /// </summary>
    public static string? TakePendingFailureNote()
    {
        if (CurrentExecutable is not { } target) return null;

        var path = target + FailureNoteSuffix;

        try
        {
            if (!File.Exists(path)) return null;

            var raw = File.ReadAllText(path).Trim();
            File.Delete(path);

            Log.Warn($"上次自动更新未完成（标记：{raw}）");

            return raw.ToLowerInvariant() switch
            {
                "timeout" => "上次自动更新没有完成：启动器进程一直没退出，所以没有改动任何文件。可以再试一次。",
                "broken" => "上次自动更新失败：新旧文件都没能就位，请到发布页手动下载安装。",
                "" => "上次自动更新没能完成。",
                _ => "上次自动更新没能完成：新版本的文件一直被占用，替换失败。\n\n"
                     + "常见原因是还有另一个启动器实例在运行，或者安全软件正在扫描刚下载的文件。"
                     + "可以重启一次电脑后重试，或直接到发布页手动下载。"
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"读取上次更新失败的标记失败：{ex.Message}");
            return null;
        }
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
    /// 启动时清理上次更新留下的临时文件：<c>.old</c> 与它的编号变体（替换成功后的旧版本）、
    /// 半截的 <c>.new</c>（下载中断或被判定不合法）与 <c>.new.zip</c>（整包还没解完）。
    /// 清理失败只记日志 —— 那些文件常常被安全软件占着，过几次启动就清掉了。
    /// </summary>
    public static void CleanupStaleFiles()
    {
        if (CurrentExecutable is not { } target) return;

        var candidates = new List<string> { StagedSuffix, StagedSuffix + ArchiveSuffix };

        for (var index = 0; index < 5; index++)
        {
            candidates.Add(index == 0 ? BackupSuffix : $"{BackupSuffix}{index}");
        }

        foreach (var suffix in candidates)
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
