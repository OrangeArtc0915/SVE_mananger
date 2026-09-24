using System.IO;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// 压缩包解压器。支持 zip / rar / 7z / tar / gz，格式靠文件头嗅探而非扩展名判断，
/// 因此把 7z 改名成 .zip 也能正确解压。
/// 解压时对每个条目做路径穿越校验：含 ".."、绝对路径、盘符，或解析后落到目标目录之外的条目一律跳过。
/// </summary>
public static class ArchiveExtractor
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmptyMagic = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] RarMagic = [0x52, 0x61, 0x72, 0x21];
    private static readonly byte[] GzipMagic = [0x1F, 0x8B];
    private static readonly byte[] SevenZipMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>是否是我们支持的压缩包。</summary>
    public static bool IsSupportedArchive(string path) => DetectFormat(path) is not null;

    /// <summary>解压到目标目录（自动识别 zip / rar / 7z / tar / gz）。</summary>
    /// <remarks>C# 不允许必需参数排在可选参数之后，因此 out error 放在可选参数之前。</remarks>
    public static bool TryExtract(string archivePath, string destinationDirectory, out string? error,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                error = $"压缩包不存在：{archivePath}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                error = "目标目录为空";
                return false;
            }

            var format = DetectFormat(archivePath);
            if (format is null)
            {
                error = $"不支持的压缩格式：{Path.GetFileName(archivePath)}";
                return false;
            }

            var destinationRoot = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destinationRoot);

            token.ThrowIfCancellationRequested();

            var archiveName = Path.GetFileName(archivePath);

            using var archive = ArchiveFactory.Open(archivePath);

            // .tar.gz 会被 SharpCompress 当成「一个 gz 条目（内容是一个 tar）」，
            // 这里再往里拆一层：内容是 tar 就用 TarArchive 读，否则按单文件 gzip 处理。
            if (archive.Type == ArchiveType.GZip)
            {
                var nested = archive.Entries.FirstOrDefault(entry => !entry.IsDirectory);

                if (nested is not null)
                {
                    using var decompressed = new MemoryStream();

                    using (var entryStream = nested.OpenEntryStream())
                    {
                        entryStream.CopyTo(decompressed);
                    }

                    decompressed.Position = 0;

                    if (TarArchive.IsTarFile(decompressed))
                    {
                        decompressed.Position = 0;

                        using var tar = TarArchive.Open(decompressed);
                        return ExtractEntries(tar.Entries, destinationRoot, archiveName, format, progress, token);
                    }

                    decompressed.Position = 0;
                    return ExtractSingleStream(decompressed, nested.Key, destinationRoot, archiveName, format, progress);
                }
            }

            return ExtractEntries(archive.Entries, destinationRoot, archiveName, format, progress, token);
        }
        catch (OperationCanceledException)
        {
            error = "操作已取消";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error($"解压失败：{archivePath}", ex);
            return false;
        }
    }

    /// <summary>按条目逐个解压到目标目录。单个条目失败只记 Warn，不影响整体。</summary>
    private static bool ExtractEntries(IEnumerable<IArchiveEntry> entries, string destinationRoot,
        string archiveName, string format, IProgress<double>? progress, CancellationToken token)
    {
        // 目录条目不需要单独创建：解压文件时会按需建目录
        var files = entries.Where(entry => !entry.IsDirectory).ToList();
        var total = files.Count;
        var finished = 0;
        var rejected = 0;

        foreach (var entry in files)
        {
            token.ThrowIfCancellationRequested();

            var key = entry.Key ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (!TryResolveTarget(destinationRoot, key, out var targetPath))
            {
                rejected++;
                Log.Warn($"压缩包条目路径不合法，已跳过：{key}（{archiveName}）");
                continue;
            }

            try
            {
                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using var source = entry.OpenEntryStream();
                using var destination = File.Create(targetPath);
                source.CopyTo(destination);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个条目失败不影响整体
                Log.Warn($"解压条目失败，已跳过：{key}（{ex.Message}）");
            }

            finished++;
            if (total > 0) progress?.Report((double)finished / total);
        }

        progress?.Report(1);

        if (rejected > 0)
            Log.Warn($"解压 {archiveName} 时跳过了 {rejected} 个路径不合法的条目");

        Log.Info($"解压完成：{archiveName}（格式 {format}，文件 {finished} 个）→ {destinationRoot}");
        return true;
    }

    /// <summary>单文件 gzip（非 tar）的解压。</summary>
    private static bool ExtractSingleStream(Stream content, string? key, string destinationRoot,
        string archiveName, string format, IProgress<double>? progress)
    {
        var name = string.IsNullOrWhiteSpace(key) ? "gzip-content" : key;

        if (!TryResolveTarget(destinationRoot, name, out var targetPath))
        {
            Log.Warn($"压缩包条目路径不合法，已跳过：{name}（{archiveName}）");
            progress?.Report(1);
            return true;
        }

        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var destination = File.Create(targetPath);
            content.CopyTo(destination);
        }
        catch (Exception ex)
        {
            Log.Warn($"解压条目失败，已跳过：{name}（{ex.Message}）");
        }

        progress?.Report(1);
        Log.Info($"解压完成：{archiveName}（格式 {format}，单文件 gzip）→ {destinationRoot}");
        return true;
    }

    /// <summary>按文件头嗅探格式；嗅探不出再退回扩展名判断。返回 null 表示不支持。</summary>
    private static string? DetectFormat(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (File.Exists(path))
            {
                var head = new byte[8];
                int read;
                using (var stream = File.OpenRead(path))
                {
                    read = stream.Read(head, 0, head.Length);
                }

                if (read >= 4 && (StartsWith(head, read, ZipMagic) || StartsWith(head, read, ZipEmptyMagic)))
                    return "zip";
                if (read >= 4 && StartsWith(head, read, RarMagic)) return "rar";
                if (read >= 2 && StartsWith(head, read, GzipMagic)) return "gz";
                if (read >= 6 && StartsWith(head, read, SevenZipMagic)) return "7z";
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取压缩包文件头失败，改用扩展名判断：{path}（{ex.Message}）");
        }

        return ExtensionFormat(path);
    }

    private static string? ExtensionFormat(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();

        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
            return "tar.gz";

        return Path.GetExtension(name) switch
        {
            ".zip" => "zip",
            ".rar" => "rar",
            ".7z" => "7z",
            ".tar" => "tar",
            ".gz" => "gz",
            _ => null
        };
    }

    private static bool StartsWith(byte[] buffer, int length, byte[] magic)
    {
        if (length < magic.Length) return false;

        for (var i = 0; i < magic.Length; i++)
        {
            if (buffer[i] != magic[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// 把条目路径解析成目标目录内的绝对路径。
    /// 拒绝绝对路径、UNC、盘符、含 ".." 的路径，以及最终落点不在目标目录内的路径。
    /// </summary>
    private static bool TryResolveTarget(string destinationRoot, string key, out string targetPath)
    {
        targetPath = string.Empty;

        try
        {
            if (Path.IsPathRooted(key)) return false;

            // 统一分隔符，去掉开头的斜杠，避免被当成根路径
            var normalized = key.Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0) return false;

            foreach (var segment in normalized.Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;

                // ".." 用于回溯，盘符（如 "C:"）用于跳出目标目录，一律拒绝
                if (segment == "..") return false;
                if (segment.Contains(':')) return false;
            }

            var relative = normalized.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(destinationRoot, relative));

            if (!IsInside(destinationRoot, full)) return false;

            targetPath = full;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"解析压缩包条目路径失败，已跳过：{key}（{ex.Message}）");
            return false;
        }
    }

    private static bool IsInside(string root, string path)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase)) return true;

        return trimmedPath.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
