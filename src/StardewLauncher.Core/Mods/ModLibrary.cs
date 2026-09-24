using System.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>Mod 库里的一个 Mod（库中是已解压的文件夹）。</summary>
public sealed class LibraryMod
{
    /// <summary>库里这个 Mod 的根目录。</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>安装到游戏 Mods 时使用的目标文件夹名。</summary>
    public string FolderName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string Author { get; init; } = "";

    public string Version { get; init; } = "";

    public string Description { get; init; } = "";

    public string UniqueId { get; init; } = "";

    /// <summary>「内容包」或「代码 Mod」。</summary>
    public string TypeText { get; init; } = "";

    /// <summary>从文件夹名的方括号前缀里提取，如「作物」「大型拓展」「宠物」；没有则空。</summary>
    public string Category { get; init; } = "";

    /// <summary>库里那一层文件夹名，用于显示。</summary>
    public string SourceFolderName { get; init; } = "";

    /// <summary>游戏 Mods 下已有同名文件夹。</summary>
    public bool IsInstalled { get; set; }

    /// <summary>manifest.json 解析失败时的可读原因。</summary>
    public string? ParseError { get; set; }
}

/// <summary>一次 Mod 库扫描的结果。</summary>
public sealed class ModLibraryScanResult
{
    public string LibraryDirectory { get; init; } = "";

    public bool Exists { get; init; }

    public IReadOnlyList<LibraryMod> Mods { get; init; } = [];
}

/// <summary>
/// Mod 库：一批已解压、待安装的 Mod 文件夹。库里的目录结构并不统一，
/// 有的直接就是 Mod 根目录，有的多包了一层甚至几层，因此扫描时逐层下探直到找到 manifest.json。
/// </summary>
public static class ModLibrary
{
    private const int MaxDescendDepth = 3;

    /// <summary>扫描 Mod 库，并标记每个 Mod 是否已安装到 gameModsDirectory。</summary>
    public static ModLibraryScanResult Scan(string libraryDirectory, string gameModsDirectory,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory) || !Directory.Exists(libraryDirectory))
        {
            progress?.Report(1);
            return new ModLibraryScanResult { LibraryDirectory = libraryDirectory ?? "", Exists = false };
        }

        token.ThrowIfCancellationRequested();

        string[] topDirectories;
        try
        {
            topDirectories = Directory.GetDirectories(libraryDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"Mod 库读取失败：{libraryDirectory}（{ex.Message}）");
            progress?.Report(1);
            return new ModLibraryScanResult { LibraryDirectory = libraryDirectory, Exists = true };
        }

        // 固定顺序，保证扫描结果稳定可复现
        Array.Sort(topDirectories, StringComparer.OrdinalIgnoreCase);

        var mods = new List<LibraryMod>();
        var processed = 0;

        foreach (var topDirectory in topDirectories)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                ScanTopDirectory(topDirectory, gameModsDirectory, mods, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个目录出问题不影响整体扫描
                Log.Warn($"Mod 库目录扫描失败：{topDirectory}（{ex.Message}）");
            }

            processed++;
            progress?.Report((double)processed / topDirectories.Length);
        }

        progress?.Report(1);
        return new ModLibraryScanResult
        {
            LibraryDirectory = libraryDirectory,
            Exists = true,
            Mods = mods
        };
    }

    /// <summary>
    /// 扫描库里的一个直接子目录。它自身有 manifest.json 就是一个 Mod；
    /// 否则向下探最多 <see cref="MaxDescendDepth"/> 层，命中后不再深入，避免把 Mod 内部资源里的 json 当成 Mod。
    /// 安装文件夹名默认取「库里那一层直接子目录名」（即带中文分类前缀的那个），保持用户熟悉的命名；
    /// 同一个直接子目录下若有多个 Mod，则改用「直接子目录名 - 内层文件夹名」，避免互相覆盖。
    /// </summary>
    private static void ScanTopDirectory(string topDirectory, string gameModsDirectory,
        List<LibraryMod> mods, CancellationToken token)
    {
        var roots = new List<string>();
        FindModRoots(topDirectory, 0, roots, token);

        if (roots.Count == 0) return;

        var topName = Path.GetFileName(topDirectory);
        var category = ExtractCategory(topName);
        var multiple = roots.Count > 1;

        // 同一个直接子目录内可能撞名，这里兜底加序号
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();

            var innerName = Path.GetFileName(root);
            var folderName = MakeUnique(multiple ? $"{topName} - {innerName}" : topName, used);
            var sourceFolderName = multiple ? $"{topName}\\{innerName}" : topName;

            mods.Add(CreateEntry(root, folderName, sourceFolderName, category, gameModsDirectory));
        }
    }

    /// <summary>递归找出所有含 manifest.json 的目录；命中后不再深入。</summary>
    private static void FindModRoots(string directory, int depth, List<string> roots, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            roots.Add(directory);
            return;
        }

        if (depth >= MaxDescendDepth) return;

        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex)
        {
            Log.Warn($"Mod 库子目录读取失败：{directory}（{ex.Message}）");
            return;
        }

        Array.Sort(children, StringComparer.OrdinalIgnoreCase);
        foreach (var child in children) FindModRoots(child, depth + 1, roots, token);
    }

    private static LibraryMod CreateEntry(string root, string folderName, string sourceFolderName,
        string category, string gameModsDirectory)
    {
        var manifest = ManifestParser.Parse(Path.Combine(root, "manifest.json"), out var error);

        return new LibraryMod
        {
            SourcePath = root,
            FolderName = folderName,
            DisplayName = string.IsNullOrWhiteSpace(manifest?.Name) ? sourceFolderName : manifest!.Name,
            Author = manifest?.Author ?? "",
            Version = manifest?.Version ?? "",
            Description = manifest?.Description ?? "",
            UniqueId = manifest?.UniqueId ?? "",
            TypeText = manifest is null ? "" : manifest.ContentPackFor is null ? "代码 Mod" : "内容包",
            Category = category,
            SourceFolderName = sourceFolderName,
            IsInstalled = IsAlreadyInstalled(gameModsDirectory, folderName),
            ParseError = manifest is null ? error ?? "manifest.json 解析失败：未知错误" : null
        };
    }

    /// <summary>把选中的 Mod 复制进游戏 Mods 目录。复用 ModImporter 的备份 / 覆盖策略。</summary>
    public static ImportResult Install(IEnumerable<LibraryMod> mods, string gameModsDirectory,
        bool backupExisting = true, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var installed = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(gameModsDirectory))
        {
            errors.Add("目标 Mods 目录为空");
            return new ImportResult(0, installed, warnings, errors);
        }

        var list = mods?.Where(mod => mod is not null).ToList() ?? [];
        if (list.Count == 0)
        {
            progress?.Report(1);
            return new ImportResult(0, installed, warnings, errors);
        }

        try
        {
            Directory.CreateDirectory(gameModsDirectory);
        }
        catch (Exception ex)
        {
            errors.Add($"创建目标 Mods 目录失败：{ex.Message}");
            return new ImportResult(0, installed, warnings, errors);
        }

        var finished = 0;

        foreach (var mod in list)
        {
            token.ThrowIfCancellationRequested();

            var name = mod.FolderName;
            var target = Path.Combine(gameModsDirectory, name);

            try
            {
                if (!Directory.Exists(mod.SourcePath))
                {
                    errors.Add($"「{name}」来源不存在：{mod.SourcePath}");
                }
                else
                {
                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        if (backupExisting)
                        {
                            var backup = ModImporter.MakeBackupName(target);
                            Directory.Move(target, backup);
                            warnings.Add($"「{name}」已存在，旧目录已备份为 {Path.GetFileName(backup)}");
                        }
                        else if (Directory.Exists(target))
                        {
                            Directory.Delete(target, true);
                        }
                        else
                        {
                            File.Delete(target);
                        }
                    }

                    ModImporter.CopyDirectory(mod.SourcePath, target);
                    installed.Add(name);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"「{name}」安装失败：{ex.Message}");
                Log.Warn($"从 Mod 库安装失败：{mod.SourcePath} → {target}（{ex.Message}）");
            }

            finished++;
            progress?.Report((double)finished / list.Count);
        }

        progress?.Report(1);
        Log.Info($"Mod 库安装完成：成功 {installed.Count} 个，错误 {errors.Count} 条");
        return new ImportResult(installed.Count, installed, warnings, errors);
    }

    /// <summary>
    /// 尝试自动定位 Mod 库目录：从程序目录向上最多 6 层，找名为 MODS（大小写不敏感）、
    /// 且其下至少有 2 个含 manifest.json 的目录的文件夹。找不到返回 null。
    /// 开发时 exe 位于 src\...\bin\Debug\net8.0-windows\，借此能自动找到仓库根下的 MODS。
    /// </summary>
    public static string? DetectDefaultLibraryDirectory()
    {
        try
        {
            var directory = AppContext.BaseDirectory;

            for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(directory); depth++)
            {
                var candidate = Path.Combine(directory, "MODS");

                if (Directory.Exists(candidate) && CountManifestDirectories(candidate, limit: 2) >= 2)
                    return candidate;

                var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                directory = Path.GetDirectoryName(trimmed);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"自动检测 Mod 库目录失败：{ex.Message}");
        }

        return null;
    }

    /// <summary>递归统计含 manifest.json 的目录数；达到 limit 即提前返回，避免大目录全量遍历。</summary>
    private static int CountManifestDirectories(string directory, int limit)
    {
        var count = 0;
        Walk(directory);
        return count;

        void Walk(string current)
        {
            if (count >= limit) return;

            if (File.Exists(Path.Combine(current, "manifest.json"))) count++;

            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch
            {
                return;
            }

            foreach (var child in children)
            {
                if (count >= limit) return;
                Walk(child);
            }
        }
    }

    private static bool IsAlreadyInstalled(string gameModsDirectory, string folderName)
    {
        if (string.IsNullOrWhiteSpace(gameModsDirectory)) return false;

        try
        {
            var path = Path.Combine(gameModsDirectory, folderName);
            return Directory.Exists(path) || File.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"检查是否已安装失败：{folderName}（{ex.Message}）");
            return false;
        }
    }

    private static string MakeUnique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{name} ({index++})";
        }
        while (!used.Add(candidate));

        return candidate;
    }

    /// <summary>从「[作物] xxx」「【宠物】xxx」这类文件夹名里取方括号内的分类；没有则为空。</summary>
    private static string ExtractCategory(string folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return "";

        var close = folderName[0] switch
        {
            '[' => ']',
            '【' => '】',
            _ => '\0'
        };

        if (close == '\0') return "";

        var end = folderName.IndexOf(close);
        return end <= 0 ? "" : folderName[1..end].Trim();
    }
}
