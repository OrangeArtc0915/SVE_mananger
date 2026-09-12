using System.IO;
using System.IO.Compression;
using Microsoft.Win32;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Nexus;

/// <summary>下载目录里一个待入库的候选压缩包。</summary>
public sealed class PendingModPackage
{
    public string FilePath { get; init; } = "";

    public string FileName { get; init; } = "";

    public long SizeBytes { get; init; }

    public DateTime LastWriteTime { get; init; }

    /// <summary>zip 里含 manifest.json，看着像 Mod 包。</summary>
    public bool LooksLikeMod { get; set; }

    /// <summary>zip 里 manifest.json 的数量（即可能包含的 Mod 个数）。</summary>
    public int ContainedModCount { get; set; }

    /// <summary>无法读取（如 7z/rar）时的说明。</summary>
    public string? InspectError { get; set; }
}

/// <summary>
/// 监控系统下载目录：把浏览器下好的 Mod 压缩包列出来供用户一键入库。
/// 只读扫描，不修改、不移动下载目录里的任何文件。
/// </summary>
public static class DownloadFolderWatcher
{
    /// <summary>认得的压缩包扩展名。tar.gz 必须放在最后，避免被前缀匹配抢走。</summary>
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar.gz"];

    /// <summary>系统默认下载目录（读注册表 Shell Folders，取不到则回退 %USERPROFILE%\Downloads）。</summary>
    public static string DefaultFolder { get; } = ResolveDefaultFolder();

    /// <summary>扫描目录里的压缩包，标出哪些像 Mod 包。</summary>
    public static IReadOnlyList<PendingModPackage> Scan(string folder, IEnumerable<string>? excludeFullPaths = null,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            progress?.Report(1);
            return [];
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludeFullPaths is not null)
        {
            foreach (var path in excludeFullPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                try { excluded.Add(Path.GetFullPath(path)); }
                catch { excluded.Add(path); }
            }
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(folder);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取下载目录失败：{folder}（{ex.Message}）");
            progress?.Report(1);
            return [];
        }

        var result = new List<PendingModPackage>();
        var finished = 0;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                if (excluded.Contains(file)) continue;

                var name = Path.GetFileName(file);
                var extension = MatchExtension(name);
                if (extension is null) continue;

                var info = new FileInfo(file);
                var package = new PendingModPackage
                {
                    FilePath = file,
                    FileName = name,
                    SizeBytes = info.Exists ? info.Length : 0,
                    LastWriteTime = info.Exists ? info.LastWriteTime : DateTime.MinValue
                };

                if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    InspectZip(file, package);
                else
                    package.InspectError = "该格式无法自动识别，请确认后再入库";

                if (package.LooksLikeMod || package.InspectError is not null) result.Add(package);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个文件失败不影响整轮扫描
                Log.Warn($"检查压缩包失败：{file}（{ex.Message}）");
            }
            finally
            {
                finished++;
                progress?.Report((double)finished / files.Length);
            }
        }

        progress?.Report(1);
        return result.OrderByDescending(package => package.LastWriteTime).ToList();
    }

    private static void InspectZip(string path, PendingModPackage package)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            var count = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;

                if (string.Equals(Path.GetFileName(entry.FullName), "manifest.json", StringComparison.OrdinalIgnoreCase))
                    count++;
            }

            package.ContainedModCount = count;
            package.LooksLikeMod = count > 0;
        }
        catch (Exception ex)
        {
            package.InspectError = $"无法读取压缩包：{ex.Message}";
            Log.Warn($"ZIP 检查失败：{path}（{ex.Message}）");
        }
    }

    private static string? MatchExtension(string fileName)
    {
        foreach (var extension in ArchiveExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return extension;
        }

        return null;
    }

    private static string ResolveDefaultFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");

            // {374DE290-...} 是「下载」已知文件夹的 GUID
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string value
                && !string.IsNullOrWhiteSpace(value)
                && Directory.Exists(value))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取系统下载目录失败，改用默认路径：{ex.Message}");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
