using System.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// 安装规划推断。给定一个已解压的包目录，若它没有 manifest.json（或结构明显不标准），
/// 就按里面出现的特征推断出一份安装规划。
/// 推断刻意保守：只要看来像标准 SMAPI Mod，或识别不出任何特征，就返回 null，交回原有标准流程。
/// </summary>
public static class PlanInference
{
    private const int MaxScanDepth = 4;
    private const int MaxScanFiles = 400;

    /// <summary>若运行库文件，说明是 Reshade 渲染包。</summary>
    private static readonly string[] ReshadeMarkerFiles =
        ["dxgi.dll", "d3d11.dll", "d3d9.dll", "opengl32.dll", "reshade.ini", "reshadepreset.ini"];

    /// <summary>
    /// 推断安装规划。识别不出特征时返回 null。
    /// </summary>
    /// <param name="extractedRoot">已解压的包根目录。</param>
    /// <param name="gameDirectory">游戏根目录，用于判断哪些文件是「游戏里已有的同名文件」。</param>
    public static InstallPlan? Infer(string extractedRoot, string gameDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(extractedRoot) || !Directory.Exists(extractedRoot)) return null;
            if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) return null;

            var root = Path.GetFullPath(extractedRoot);
            var game = Path.GetFullPath(gameDirectory);

            // 根目录有 manifest.json 就是标准 SMAPI Mod，不推断
            if (File.Exists(Path.Combine(root, "manifest.json"))) return null;

            // 特殊目录之外还有 manifest.json，说明是「多包一层 / 一包多 Mod」的标准结构，不推断
            if (HasNestedManifest(root)) return null;

            var contentDirectory = Path.Combine(root, "Content");
            var modsDirectory = Path.Combine(root, "Mods");

            var hasContent = Directory.Exists(contentDirectory);
            var hasMods = Directory.Exists(modsDirectory);
            var reshadeItems = CollectReshadeItems(root);
            var rootReplacements = CollectRootReplacements(root, game);

            var steps = new List<PlanStep>();
            var reasons = new List<string>();

            if (hasContent)
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.CopyToGame,
                    Source = "Content",
                    Target = ".",
                    Description = "覆盖游戏 Content 目录（贴图/数据替换型）"
                });
                reasons.Add("Content 覆盖");
            }

            if (hasMods)
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.CopyToMods,
                    Source = "Mods",
                    Target = "",
                    Description = "把包内 Mods 目录的内容放入游戏 Mods 目录"
                });
                reasons.Add("Mods 目录");
            }

            foreach (var item in reshadeItems)
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.CopyToGame,
                    Source = item,
                    Target = ".",
                    Description = "覆盖游戏根目录（Reshade 渲染）"
                });
            }

            if (reshadeItems.Count > 0) reasons.Add("Reshade 渲染");

            foreach (var item in rootReplacements)
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.CopyToGame,
                    Source = item,
                    Target = ".",
                    Description = "覆盖游戏根目录中的同名文件"
                });
            }

            if (rootReplacements.Count > 0) reasons.Add("游戏文件替换");

            // 纯文件替换：整包只有与游戏目录同结构的文件时，逐文件生成 CopyToGame
            if (steps.Count == 0)
            {
                var nested = CollectNestedReplacements(root, game);

                foreach (var relative in nested)
                {
                    steps.Add(new PlanStep
                    {
                        Kind = PlanStepKind.CopyToGame,
                        Source = relative,
                        Target = ".",
                        Optional = true,
                        Description = "覆盖游戏目录中的同名文件"
                    });
                }

                if (nested.Count > 0) reasons.Add("游戏文件替换");
            }

            if (steps.Count == 0) return null;

            var deduped = Deduplicate(steps);

            return new InstallPlan
            {
                Name = string.Join(" + ", reasons) + "安装",
                Description = "该包不是标准 SMAPI Mod 目录结构，已推断出安装步骤。" +
                              "覆盖游戏目录的文件在覆盖前会自动备份为 .bak-<时间戳>。",
                Match = null,
                RequiresConfirmation = true,
                Steps = deduped
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"推断安装规划失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>根目录下的直接子目录（排除 Content / Mods）里若还有 manifest.json，说明是标准多 Mod 结构。</summary>
    private static bool HasNestedManifest(string root)
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(directory);

                if (string.Equals(name, "Content", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "Mods", StringComparison.OrdinalIgnoreCase)) continue;

                if (File.Exists(Path.Combine(directory, "manifest.json"))) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"检查嵌套 manifest 失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>找出根目录下与 Reshade 相关的文件或目录（返回相对根目录的名字）。</summary>
    private static List<string> CollectReshadeItems(string root)
    {
        var result = new List<string>();

        try
        {
            foreach (var file in Directory.GetFiles(root))
            {
                var name = Path.GetFileName(file);
                var lower = name.ToLowerInvariant();

                if (lower.StartsWith("reshade", StringComparison.Ordinal)
                    || Array.IndexOf(ReshadeMarkerFiles, lower) >= 0)
                {
                    result.Add(name);
                }
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(directory);

                if (name.Contains("reshade", StringComparison.OrdinalIgnoreCase))
                    result.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"识别 Reshade 文件失败：{ex.Message}");
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>找出根目录下与游戏根目录同名的文件，以及所有 .xnb 文件。</summary>
    private static List<string> CollectRootReplacements(string root, string game)
    {
        var result = new List<string>();

        try
        {
            foreach (var file in Directory.GetFiles(root))
            {
                var name = Path.GetFileName(file);

                var isXnb = name.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase);
                var sameAsGame = File.Exists(Path.Combine(game, name));

                if (isXnb || sameAsGame) result.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"识别游戏文件替换失败：{ex.Message}");
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>递归找出与游戏目录同相对路径的文件（纯文件替换型包）。</summary>
    private static List<string> CollectNestedReplacements(string root, string game)
    {
        var result = new List<string>();
        var scanned = 0;

        Walk(root, 0);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;

        void Walk(string directory, int depth)
        {
            if (depth > MaxScanDepth || scanned >= MaxScanFiles) return;

            string[] files;
            string[] children;

            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex)
            {
                Log.Warn($"扫描包目录失败：{directory}（{ex.Message}）");
                return;
            }

            foreach (var file in files)
            {
                if (scanned++ >= MaxScanFiles) return;

                string relative;

                try
                {
                    relative = Path.GetRelativePath(root, file);
                }
                catch
                {
                    continue;
                }

                if (relative.StartsWith('.')) continue;
                if (File.Exists(Path.Combine(game, relative))) result.Add(relative);
            }

            foreach (var child in children)
            {
                if (scanned >= MaxScanFiles) return;
                Walk(child, depth + 1);
            }
        }
    }

    private static List<PlanStep> Deduplicate(List<PlanStep> steps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PlanStep>();

        foreach (var step in steps)
        {
            var key = $"{step.Kind}|{step.Source}|{step.Target}";
            if (seen.Add(key)) result.Add(step);
        }

        return result;
    }
}
