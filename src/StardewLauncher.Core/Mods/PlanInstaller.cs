using System.Diagnostics;
using System.IO;
using System.Text;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>规划里一步的执行结果。</summary>
public sealed record PlanStepResult(PlanStep Step, bool Success, bool Skipped, string Message);

/// <summary>整份规划的执行结果。Success 为 true 表示所有步骤都成功执行（没有被跳过的步骤）。</summary>
public sealed record PlanApplyResult(bool Success, IReadOnlyList<PlanStepResult> Steps, IReadOnlyList<string> Errors);

/// <summary>
/// 安装规划的执行器。所有写操作都会先校验路径落在预期目录内（防路径穿越）；
/// CopyToGame 覆盖已存在文件前会先备份为 .bak-&lt;时间戳&gt;；
/// RunProgram / DeleteFromGame 必须 confirmed 为 true 才会执行，否则跳过并标记需要确认。
/// </summary>
public static class PlanInstaller
{
    private static readonly string[] AllowedProgramExtensions = [".exe", ".bat", ".cmd"];

    private const int ProgramTimeoutMs = 120_000;

    /// <summary>套用规划。confirmed 为 false 时，遇到 RunProgram / DeleteFromGame 步会跳过并标记为需要确认。</summary>
    public static PlanApplyResult Apply(InstallPlan plan, string extractedRoot, string gameDirectory,
        string modsDirectory, bool confirmed, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var results = new List<PlanStepResult>();
        var errors = new List<string>();

        try
        {
            if (plan is null)
            {
                errors.Add("安装规划为空");
                return new PlanApplyResult(false, results, errors);
            }

            if (string.IsNullOrWhiteSpace(extractedRoot) || !Directory.Exists(extractedRoot))
            {
                errors.Add($"来源目录不存在：{extractedRoot}");
                return new PlanApplyResult(false, results, errors);
            }

            var root = Path.GetFullPath(extractedRoot);
            var gameRoot = NormalizeRoot(gameDirectory);
            var modsRoot = NormalizeRoot(modsDirectory);

            var steps = plan.Steps ?? [];
            var total = steps.Count;
            var finished = 0;
            var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Log.Info($"开始套用安装规划「{plan.Name}」（{total} 步，confirmed={confirmed}）");

            foreach (var step in steps)
            {
                token.ThrowIfCancellationRequested();

                PlanStepResult result;

                try
                {
                    result = Execute(step, root, gameRoot, modsRoot, confirmed, backedUp);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = new PlanStepResult(step, false, false, $"步骤执行异常：{ex.Message}");
                }

                results.Add(result);

                if (!result.Success)
                {
                    errors.Add($"{Describe(step)}：{result.Message}");

                    // 必需步骤失败就中止；可选步骤失败继续
                    if (!step.Optional && !result.Skipped) break;
                }

                finished++;
                if (total > 0) progress?.Report((double)finished / total);
            }

            progress?.Report(1);

            var success = results.Count == total && results.All(item => item.Success);
            Log.Info($"安装规划「{plan.Name}」执行完毕：{results.Count}/{total} 步，成功={success}，错误 {errors.Count} 条");

            return new PlanApplyResult(success, results, errors);
        }
        catch (OperationCanceledException)
        {
            errors.Add("操作已取消");
            return new PlanApplyResult(false, results, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            Log.Error($"套用安装规划失败：{plan?.Name}", ex);
            return new PlanApplyResult(false, results, errors);
        }
    }

    private static PlanStepResult Execute(PlanStep step, string root, string gameRoot, string modsRoot,
        bool confirmed, HashSet<string> backedUp)
    {
        return step.Kind switch
        {
            PlanStepKind.CopyToMods => CopyToMods(step, root, modsRoot),
            PlanStepKind.CopyToGame => CopyToGame(step, root, gameRoot, backedUp),
            PlanStepKind.CheckExists => CheckExists(step, gameRoot),
            PlanStepKind.CheckVersion => CheckVersion(step, gameRoot),
            PlanStepKind.RunProgram => RunProgram(step, root, confirmed),
            PlanStepKind.DeleteFromGame => DeleteFromGame(step, gameRoot, confirmed),
            _ => new PlanStepResult(step, false, false, $"未知的步骤类型：{step.Kind}")
        };
    }

    // ————— 复制 —————

    private static PlanStepResult CopyToMods(PlanStep step, string root, string modsRoot)
    {
        if (string.IsNullOrWhiteSpace(modsRoot))
            return Fail(step, "游戏 Mods 目录不可用");

        if (!TryResolveRelative(root, step.Source, out var source))
            return Fail(step, $"来源路径不合法：{step.Source}");

        if (!File.Exists(source) && !Directory.Exists(source))
            return Fail(step, $"来源不存在：{step.Source}");

        if (!TryResolveRelative(modsRoot, step.Target, out var destination))
            return Fail(step, $"目标路径不合法：{step.Target}");

        if (!IsInside(modsRoot, destination))
            return Fail(step, $"目标超出 Mods 目录：{step.Target}");

        // Source 的内容直接放进 Mods 目录（Source=Mods 时，包里的各个 Mod 文件夹原样落到 Mods 下）
        var (copied, _, error) = CopyContents(source, destination, modsRoot, backup: null);
        if (error is not null) return Fail(step, error);

        return Ok(step, $"已复制 {copied} 个文件到 Mods 目录");
    }

    private static PlanStepResult CopyToGame(PlanStep step, string root, string gameRoot, HashSet<string> backedUp)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
            return Fail(step, "游戏根目录不可用");

        if (!TryResolveRelative(root, step.Source, out var source))
            return Fail(step, $"来源路径不合法：{step.Source}");

        if (!File.Exists(source) && !Directory.Exists(source))
            return Fail(step, $"来源不存在：{step.Source}");

        if (!TryResolveRelative(gameRoot, step.Target, out var destination))
            return Fail(step, $"目标路径不合法：{step.Target}");

        if (!IsInside(gameRoot, destination))
            return Fail(step, $"目标超出游戏目录：{step.Target}");

        // 保留 Source 在包内的相对结构：Source=Content → 覆盖到游戏目录下的 Content
        var relativeSource = NormalizeRelative(step.Source);

        var (copied, backups, error) = CopyOverlay(source, relativeSource, destination, gameRoot, backedUp);
        if (error is not null) return Fail(step, error);

        var message = backups > 0
            ? $"已覆盖 {copied} 个文件，备份 {backups} 个"
            : $"已复制 {copied} 个文件";

        return Ok(step, message);
    }

    /// <summary>把 source 的内容复制进 destination 目录；任何落点都必须在 limitRoot 内。</summary>
    private static (int Copied, int Backups, string? Error) CopyContents(string source, string destination,
        string limitRoot, HashSet<string>? backup)
    {
        var copied = 0;
        var backups = 0;

        try
        {
            if (File.Exists(source))
            {
                Directory.CreateDirectory(destination);

                var target = Path.GetFullPath(Path.Combine(destination, Path.GetFileName(source)));
                if (!IsInside(limitRoot, target)) return (0, 0, "目标超出允许的目录");

                if (backup is not null && TryBackup(target, backup)) backups++;

                File.Copy(source, target, overwrite: true);
                return (1, backups, null);
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.GetFullPath(Path.Combine(destination, relative));

                if (!IsInside(limitRoot, target)) return (copied, backups, $"条目超出允许的目录：{relative}");

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                if (backup is not null && TryBackup(target, backup)) backups++;

                File.Copy(file, target, overwrite: true);
                copied++;
            }

            return (copied, backups, null);
        }
        catch (Exception ex)
        {
            return (copied, backups, $"复制失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把 source 按 relativeSource 的相对结构覆盖到 destBase：
    /// Source=Content 时，包里的 Content\Characters\X.xnb 会落到游戏目录的 Content\Characters\X.xnb。
    /// </summary>
    private static (int Copied, int Backups, string? Error) CopyOverlay(string source, string relativeSource,
        string destBase, string limitRoot, HashSet<string>? backup)
    {
        var copied = 0;
        var backups = 0;

        try
        {
            if (File.Exists(source))
            {
                var target = Path.GetFullPath(Path.Combine(destBase, relativeSource));
                if (!IsInside(limitRoot, target)) return (0, 0, "目标超出允许的目录");

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                if (backup is not null && TryBackup(target, backup)) backups++;

                File.Copy(source, target, overwrite: true);
                return (1, backups, null);
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var childRelative = Path.GetRelativePath(source, file);
                var target = Path.GetFullPath(Path.Combine(destBase, relativeSource, childRelative));

                if (!IsInside(limitRoot, target)) return (copied, backups, $"条目超出允许的目录：{childRelative}");

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                if (backup is not null && TryBackup(target, backup)) backups++;

                File.Copy(file, target, overwrite: true);
                copied++;
            }

            return (copied, backups, null);
        }
        catch (Exception ex)
        {
            return (copied, backups, $"复制失败：{ex.Message}");
        }
    }

    /// <summary>覆盖前备份。同一文件在一次 Apply 里只备份一次。</summary>
    private static bool TryBackup(string file, HashSet<string> backedUp)
    {
        if (!File.Exists(file)) return false;
        if (!backedUp.Add(file)) return false;

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var target = $"{file}.bak-{stamp}";

        var suffix = 1;
        while (File.Exists(target)) target = $"{file}.bak-{stamp}-{suffix++}";

        File.Copy(file, target, overwrite: false);
        Log.Info($"安装规划：已备份 {file} → {Path.GetFileName(target)}");

        return true;
    }

    // ————— 检查 —————

    private static PlanStepResult CheckExists(PlanStep step, string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return Fail(step, "游戏根目录不可用");

        if (!TryResolveRelative(gameRoot, step.Target, out var target))
            return Fail(step, $"目标路径不合法：{step.Target}");

        if (File.Exists(target) || Directory.Exists(target)) return Ok(step, $"已找到 {step.Target}");

        return Fail(step, $"缺少 {step.Target}");
    }

    private static PlanStepResult CheckVersion(PlanStep step, string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return Fail(step, "游戏根目录不可用");

        if (!TryResolveRelative(gameRoot, step.Source, out var file))
            return Fail(step, $"文件路径不合法：{step.Source}");

        if (!File.Exists(file)) return Fail(step, $"文件不存在：{step.Source}");

        var found = ReadFileVersion(file);

        if (string.IsNullOrWhiteSpace(step.Condition))
            return Ok(step, found is null ? "文件存在" : $"文件版本 {found}");

        if (found is null) return Fail(step, $"无法读取文件版本：{step.Source}");

        return DependencyResolver.CompareVersions(found, step.Condition) >= 0
            ? Ok(step, $"版本 {found} ≥ {step.Condition}")
            : Fail(step, $"需要版本 ≥ {step.Condition}，当前 {found}");
    }

    private static string? ReadFileVersion(string file)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);

            if (!string.IsNullOrWhiteSpace(info.FileVersion)) return NormalizeVersion(info.FileVersion);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion)) return NormalizeVersion(info.ProductVersion);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取文件版本失败：{file}（{ex.Message}）");
        }

        return null;
    }

    /// <summary>取版本字符串开头的数字与点号，去掉 "(build ...)" 之类的尾巴。</summary>
    private static string NormalizeVersion(string text)
    {
        var builder = new StringBuilder();

        foreach (var ch in text.Trim())
        {
            if (char.IsDigit(ch) || ch == '.') builder.Append(ch);
            else break;
        }

        return builder.Length == 0 ? text.Trim() : builder.ToString();
    }

    // ————— 需要确认的步骤 —————

    private static PlanStepResult RunProgram(PlanStep step, string root, bool confirmed)
    {
        if (!confirmed) return Skipped(step, "需要用户确认");

        if (!TryResolveRelative(root, step.Source, out var program))
            return Fail(step, $"程序路径不合法：{step.Source}");

        if (!File.Exists(program)) return Fail(step, $"程序不存在：{step.Source}");

        var extension = Path.GetExtension(program).ToLowerInvariant();
        if (Array.IndexOf(AllowedProgramExtensions, extension) < 0)
            return Fail(step, $"不允许运行该类型：{extension}");

        try
        {
            var startInfo = new ProcessStartInfo(program)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(program) ?? root
            };

            using var process = Process.Start(startInfo);
            if (process is null) return Fail(step, "启动失败");

            if (!process.WaitForExit(ProgramTimeoutMs)) return Fail(step, "程序运行超时");

            var code = process.ExitCode;
            Log.Info($"安装规划：已运行 {step.Source}，退出码 {code}");

            return code == 0
                ? Ok(step, "已运行（退出码 0）")
                : Fail(step, $"已运行，但退出码为 {code}");
        }
        catch (Exception ex)
        {
            return Fail(step, $"运行失败：{ex.Message}");
        }
    }

    private static PlanStepResult DeleteFromGame(PlanStep step, string gameRoot, bool confirmed)
    {
        if (!confirmed) return Skipped(step, "需要用户确认");

        if (string.IsNullOrWhiteSpace(gameRoot)) return Fail(step, "游戏根目录不可用");

        if (!TryResolveRelative(gameRoot, step.Target, out var target))
            return Fail(step, $"目标路径不合法：{step.Target}");

        if (!IsInside(gameRoot, target) || string.Equals(
                target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(step, "拒绝删除游戏根目录本身");
        }

        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
                return Ok(step, $"已删除文件 {step.Target}");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
                return Ok(step, $"已删除目录 {step.Target}");
            }

            return Ok(step, $"目标不存在，无需删除：{step.Target}");
        }
        catch (Exception ex)
        {
            return Fail(step, $"删除失败：{ex.Message}");
        }
    }

    // ————— 工具 —————

    private static PlanStepResult Ok(PlanStep step, string message) => new(step, true, false, message);

    private static PlanStepResult Fail(PlanStep step, string message)
    {
        Log.Warn($"安装规划步骤失败：{Describe(step)} —— {message}");
        return new PlanStepResult(step, false, false, message);
    }

    private static PlanStepResult Skipped(PlanStep step, string message)
    {
        Log.Info($"安装规划步骤已跳过：{Describe(step)} —— {message}");
        return new PlanStepResult(step, false, true, message);
    }

    private static string Describe(PlanStep step)
        => string.IsNullOrWhiteSpace(step.Description)
            ? $"{step.Kind} {step.Source} → {step.Target}"
            : $"{step.Kind}（{step.Description}）";

    /// <summary>把相对路径规整成不带首尾斜杠的形式；"." 与空串都表示当前目录。</summary>
    private static string NormalizeRelative(string? relative)
    {
        var text = (relative ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        return text == "." ? string.Empty : text;
    }

    private static string NormalizeRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return string.Empty;

        try
        {
            return Path.GetFullPath(directory);
        }
        catch (Exception ex)
        {
            Log.Warn($"路径无法解析：{directory}（{ex.Message}）");
            return string.Empty;
        }
    }

    /// <summary>把相对路径解析成 baseDirectory 下的绝对路径。拒绝绝对路径、".." 与盘符。</summary>
    private static bool TryResolveRelative(string baseDirectory, string? relative, out string full)
    {
        full = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) return false;

            var root = Path.GetFullPath(baseDirectory);
            var text = (relative ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');

            if (text.Length == 0 || text == "." || text == "./")
            {
                full = root;
                return true;
            }

            if (Path.IsPathRooted(relative ?? string.Empty)) return false;

            foreach (var segment in text.Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;

                if (segment == "..") return false;
                if (segment.Contains(':')) return false;
            }

            var combined = Path.GetFullPath(Path.Combine(root, text.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(root, combined)) return false;

            full = combined;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"解析路径失败：{relative}（{ex.Message}）");
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
