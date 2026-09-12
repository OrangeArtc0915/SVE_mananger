using System.IO;
using System.Text;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.Core.Nexus;

/// <summary>一次「下载到 Mod 库」的结果。</summary>
public sealed record LibraryDownloadResult(
    bool Success,
    string FileName,
    string? SavedPath,
    ImportResult? Import,
    string Message);

/// <summary>
/// 把直链下载到本地，若是 zip 就自动解压并入库到 Mod 库。
/// 底层下载统一走 <see cref="ResumableDownloader"/>（支持 Range 续传、退避重试、原子落盘）；
/// 这里只负责「已经拿到直链」的部分，Nexus 会员下载、网页登录等一律不涉及。
/// </summary>
public static class DownloadManager
{
    /// <summary>下载阶段占用的总进度区间 [0, 0.7]，解压入库阶段占用 [0.7, 1]。</summary>
    private const double DownloadShare = 0.7;

    /// <summary>把直链下载到本地，若是 zip 则自动解压并入库到 Mod 库。</summary>
    public static async Task<LibraryDownloadResult> DownloadToLibraryAsync(string url, string libraryDirectory,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        var fileName = "";

        try
        {
            var trimmed = url?.Trim() ?? "";
            if (trimmed.Length == 0
                || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Fail(fileName, "下载链接无效，请输入以 http/https 开头的直链。");
            }

            if (string.IsNullOrWhiteSpace(libraryDirectory))
                return Fail(fileName, "还没有设置 Mod 库目录，请先到设置页选择。");

            Paths.Init();

            var savedPath = MakeUniquePath(Path.Combine(Paths.Downloads, DeriveFileName(uri)));
            fileName = Path.GetFileName(savedPath);

            var downloadProgress = progress is null ? null : new ScaledProgress(progress, 0d, DownloadShare);
            var downloaded = await ResumableDownloader.DownloadAsync(trimmed, savedPath, downloadProgress, token);

            if (!downloaded.Success)
                return Fail(fileName, $"下载失败：{downloaded.Message}（详见日志）");

            if (!savedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"已下载文件（非 zip，未自动入库）：{savedPath}");
                return new LibraryDownloadResult(true, fileName, savedPath, null,
                    $"已下载到 {savedPath}；该格式无法自动解压，请手动解压后放入 Mod 库。");
            }

            var importProgress = progress is null ? null : new ScaledProgress(progress, DownloadShare, 1d);
            var import = await Task.Run(
                () => ModImporter.Import(savedPath, libraryDirectory, backupExisting: true, importProgress, token),
                token);

            progress?.Report(1);

            var message = BuildImportMessage(import);
            Log.Info($"下载入库完成：{savedPath}（{message.Replace(Environment.NewLine, " ")}）");

            return new LibraryDownloadResult(true, fileName, savedPath, import, message);
        }
        catch (OperationCanceledException)
        {
            return Fail(fileName, "已取消下载。");
        }
        catch (Exception ex)
        {
            Log.Error($"下载入库失败：{url}", ex);
            return Fail(fileName, $"下载入库失败：{ex.Message}");
        }
    }

    private static string BuildImportMessage(ImportResult import)
    {
        if (import.InstalledCount > 0)
        {
            var text = $"已入库 {import.InstalledCount} 个 Mod：{string.Join("、", import.InstalledFolders)}";
            if (import.Warnings.Count > 0) text += Environment.NewLine + "警告：" + string.Join("；", import.Warnings);
            if (import.Errors.Count > 0) text += Environment.NewLine + "错误：" + string.Join("；", import.Errors);
            return text;
        }

        IReadOnlyList<string> reasons = import.Errors.Count > 0
            ? import.Errors
            : new List<string> { "压缩包内没有找到 manifest.json" };

        return "下载完成，但没有找到可入库的 Mod：" + string.Join("；", reasons);
    }

    private static LibraryDownloadResult Fail(string fileName, string message)
    {
        Log.Warn($"下载入库未完成：{message}");
        return new LibraryDownloadResult(false, fileName, null, null, message);
    }

    /// <summary>从 URL 推导文件名，取最后一段路径并做路径穿越防护。</summary>
    private static string DeriveFileName(Uri uri)
    {
        string raw;
        try
        {
            raw = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        }
        catch
        {
            raw = Path.GetFileName(uri.AbsolutePath);
        }

        var name = Sanitize(raw);

        // 没有可用的扩展名时给一个带时间戳的 zip 名，避免浏览器式链接（如 download?id=1）落成怪文件
        if (name.Length == 0 || Path.GetExtension(name).Length <= 1)
            name = $"download-{DateTime.Now:yyyyMMdd-HHmmss}.zip";

        return name;
    }

    /// <summary>去掉文件名里的目录分隔符与非法字符，避免写到目标目录之外。</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var ch in name)
        {
            var bad = ch is '\\' or '/' or ':' || Array.IndexOf(invalid, ch) >= 0;
            builder.Append(bad ? '_' : ch);
        }

        return builder.ToString().Trim().TrimEnd('.');
    }

    /// <summary>同名文件已存在时补时间戳，绝不覆盖已有文件。</summary>
    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        var candidate = Path.Combine(directory, $"{stem}-{stamp}{extension}");
        var index = 1;

        while (File.Exists(candidate))
            candidate = Path.Combine(directory, $"{stem}-{stamp}-{index++}{extension}");

        return candidate;
    }

    /// <summary>把 0~1 的进度线性映射到 [from, to] 区间。</summary>
    private sealed class ScaledProgress(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value)
            => inner.Report(from + (to - from) * Math.Clamp(value, 0d, 1d));
    }
}
