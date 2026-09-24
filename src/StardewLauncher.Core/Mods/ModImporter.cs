using System.IO;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>一次导入的结果。单个 Mod 失败只记入 Errors，不影响其余 Mod。</summary>
public sealed record ImportResult(
    int InstalledCount,
    IReadOnlyList<string> InstalledFolders,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

/// <summary>
/// 从 zip 或文件夹导入 Mod。压缩包常常多包一层甚至多层文件夹，这里会自动下探找到真正的 Mod 根目录；
/// 一个包里可能有多个 Mod，会逐个安装。
/// </summary>
public static class ModImporter
{
    private const int MaxDescendDepth = 3;

    /// <summary>包内自带安装规划时使用的文件名。</summary>
    private const string InstallPlanFile = "install-plan.json";

    public static ImportResult Import(string sourcePath, string modsDirectory, bool backupExisting = true,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        Paths.Init();

        var installed = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(sourcePath) || (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)))
        {
            errors.Add($"来源不存在：{sourcePath}");
            return new ImportResult(0, installed, warnings, errors);
        }

        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            errors.Add("目标 Mods 目录为空");
            return new ImportResult(0, installed, warnings, errors);
        }

        var isArchive = File.Exists(sourcePath);
        var temp = Path.Combine(Paths.Temp, "import-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(temp);
            progress?.Report(0.05);

            string extractionRoot;
            if (isArchive)
            {
                var extractProgress = progress is null
                    ? null
                    : new Progress<double>(value => progress.Report(0.05 + 0.15 * Math.Clamp(value, 0d, 1d)));

                if (!ArchiveExtractor.TryExtract(sourcePath, temp, out var extractError, extractProgress, token))
                {
                    errors.Add($"解压失败：{extractError}");
                    return new ImportResult(0, installed, warnings, errors);
                }

                extractionRoot = temp;
            }
            else
            {
                extractionRoot = Path.GetFullPath(sourcePath);
            }

            progress?.Report(0.2);

            var gameDirectory = GameDirectoryOf(modsDirectory);

            // 先在解压根目录判定安装规划：包内自带 install-plan.json 优先，其次按结构自动推断。
            // 必须在下探之前判断，否则「Content 覆盖型」包会被误当成多包了一层而下探进 Content 目录。
            var plan = ResolvePlan(extractionRoot, gameDirectory, out var fromPackage);
            var planRoot = extractionRoot;

            // 解压结果本身没有 manifest.json 且只有一个子目录时，说明多包了一层，继续下探。
            // 一旦遇到 manifest.json 或 install-plan.json 就停下，避免越过真正的包根目录。
            var searchRoot = extractionRoot;
            if (plan is null && isArchive)
            {
                for (var depth = 0; depth < MaxDescendDepth; depth++)
                {
                    if (File.Exists(Path.Combine(searchRoot, "manifest.json"))) break;
                    if (File.Exists(Path.Combine(searchRoot, InstallPlanFile))) break;

                    var children = Directory.GetDirectories(searchRoot);
                    if (children.Length != 1) break;

                    searchRoot = children[0];
                }
            }

            // 下探到的目录里也可能带规划（例如压缩包外面又套了一层同名文件夹）
            if (plan is null && !string.Equals(searchRoot, extractionRoot, StringComparison.OrdinalIgnoreCase))
            {
                plan = ResolvePlan(searchRoot, gameDirectory, out fromPackage);
                planRoot = searchRoot;
            }

            // 非标准结构的包（文件替换 / XNB 替换 / Content 覆盖 / Reshade）按安装规划处理
            if (plan is not null)
            {
                return ApplyPlanForImport(plan, fromPackage, planRoot, modsDirectory, gameDirectory,
                    token, progress, installed, warnings, errors);
            }

            var modRoots = FindModRoots(searchRoot, token);
            if (modRoots.Count == 0)
            {
                errors.Add("没有找到任何含 manifest.json 的 Mod");
                return new ImportResult(0, installed, warnings, errors);
            }

            Directory.CreateDirectory(modsDirectory);

            var fallbackName = isArchive
                ? Path.GetFileNameWithoutExtension(sourcePath)
                : new DirectoryInfo(extractionRoot).Name;

            var finished = 0;

            foreach (var root in modRoots)
            {
                token.ThrowIfCancellationRequested();

                var name = string.Equals(root, extractionRoot, StringComparison.OrdinalIgnoreCase)
                    ? fallbackName
                    : Path.GetFileName(root);
                name = Sanitize(name);
                if (string.IsNullOrWhiteSpace(name)) name = "Mod-" + Guid.NewGuid().ToString("N")[..6];

                var target = Path.Combine(modsDirectory, name);

                try
                {
                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        if (backupExisting)
                        {
                            var backup = MakeBackupName(target);
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

                    CopyDirectory(root, target);
                    installed.Add(name);
                }
                catch (Exception ex)
                {
                    errors.Add($"「{name}」安装失败：{ex.Message}");
                    Log.Warn($"导入 Mod 失败：{root} → {target}（{ex.Message}）");
                }

                finished++;
                progress?.Report(0.2 + 0.8 * finished / modRoots.Count);
            }
        }
        finally
        {
            TryDeleteDirectory(temp);
        }

        progress?.Report(1);
        Log.Info($"Mod 导入完成：成功 {installed.Count} 个，错误 {errors.Count} 条");
        return new ImportResult(installed.Count, installed, warnings, errors);
    }

    /// <summary>
    /// 在指定目录判定安装规划：优先读取包内自带的 install-plan.json，其次尝试自动推断。
    /// fromPackage 表示规划来自包内文件（而不是推断出来的）。返回 null 表示没有规划适用。
    /// </summary>
    private static InstallPlan? ResolvePlan(string root, string gameDirectory, out bool fromPackage)
    {
        fromPackage = false;

        var planFile = Path.Combine(root, InstallPlanFile);

        if (File.Exists(planFile))
        {
            var plan = InstallPlanStore.ReadFrom(planFile, out var error);

            if (plan is not null)
            {
                fromPackage = true;
                Log.Info($"使用包内自带的安装规划「{plan.Name}」（{root}）");
                return plan;
            }

            Log.Warn($"包内 install-plan.json 解析失败：{error}");
        }

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            Log.Warn("游戏根目录不可用，跳过安装规划推断");
            return null;
        }

        return PlanInference.Infer(root, gameDirectory);
    }

    /// <summary>套用安装规划并组装 ImportResult。</summary>
    private static ImportResult ApplyPlanForImport(InstallPlan plan, bool fromPackage, string packageRoot,
        string modsDirectory, string gameDirectory, CancellationToken token, IProgress<double>? progress,
        List<string> installed, List<string> warnings, List<string> errors)
    {
        var planProgress = progress is null
            ? null
            : new Progress<double>(value => progress.Report(0.2 + 0.75 * Math.Clamp(value, 0d, 1d)));

        // 导入是无人值守流程，RunProgram / DeleteFromGame 一律要求用户确认，因此这里传 false
        var apply = PlanInstaller.Apply(plan, packageRoot, gameDirectory, modsDirectory,
            confirmed: false, planProgress, token);

        foreach (var error in apply.Errors) errors.Add(error);

        foreach (var step in apply.Steps.Where(item => item.Skipped))
            warnings.Add($"安装规划中需要确认的步骤已跳过：{step.Message}（{step.Step.Description}）");

        var applied = apply.Steps.Count(item => item.Success);

        warnings.Add(fromPackage
            ? $"已按包内安装规划「{plan.Name}」处理（覆盖游戏目录的文件已备份）"
            : "该包为非标准结构，已按推断的安装规划处理（覆盖游戏目录的文件已备份）");

        if (applied > 0) installed.Add(plan.Name);
        else errors.Add("安装规划没有成功执行任何步骤");

        Log.Info($"安装规划「{plan.Name}」处理完成：成功 {applied}/{apply.Steps.Count} 步，错误 {apply.Errors.Count} 条");

        progress?.Report(1);
        return new ImportResult(installed.Count, installed, warnings, errors);
    }

    /// <summary>从 Mods 目录推出游戏根目录（Mods 的上一级）。</summary>
    private static string GameDirectoryOf(string modsDirectory)
    {
        try
        {
            var trimmed = (modsDirectory ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return trimmed.Length == 0 ? string.Empty : Path.GetDirectoryName(trimmed) ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warn($"推导游戏根目录失败：{modsDirectory}（{ex.Message}）");
            return string.Empty;
        }
    }

    /// <summary>递归找出所有含 manifest.json 的目录；命中后不再深入，避免把 Mod 内部资源里的 json 当成 Mod。</summary>
    private static List<string> FindModRoots(string root, CancellationToken token)
    {
        var result = new List<string>();

        Walk(root);
        return result;

        void Walk(string directory)
        {
            token.ThrowIfCancellationRequested();

            if (File.Exists(Path.Combine(directory, "manifest.json")))
            {
                result.Add(directory);
                return;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch
            {
                return;
            }

            Array.Sort(children, StringComparer.OrdinalIgnoreCase);
            foreach (var child in children) Walk(child);
        }
    }

    /// <summary>递归复制目录（供导入与 Mod 库安装共用）。</summary>
    internal static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>为已存在的目标生成不冲突的备份名（供导入与 Mod 库安装共用）。</summary>
    internal static string MakeBackupName(string target)
    {
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var backup = $"{target}.bak-{stamp}";

        var suffix = 1;
        while (Directory.Exists(backup) || File.Exists(backup))
        {
            backup = $"{target}.bak-{stamp}-{suffix++}";
        }

        return backup;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name) builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return builder.ToString().Trim().TrimEnd('.');
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"临时目录清理失败：{path}（{ex.Message}）");
        }
    }
}
