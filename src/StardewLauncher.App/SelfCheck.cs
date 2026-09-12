using System.Collections;
using System.IO;
using System.Resources;
using System.Text.Json;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App;

/// <summary>
/// 启动自检（仅 Debug 构建）。
/// 图标以字符串形式在 XAML 中引用，写错名字不会报错、只会静默不显示；
/// 游戏目录探测依赖外部环境，失败原因也需要看得见。这些都在启动时扫一遍并写进日志。
/// </summary>
internal static class SelfCheck
{
    private const string ResourcePathMarker = "assets/iconpacks/";
    private const string AssemblyResourceName = "StardewLauncher.g.resources";

    public static void Run()
    {
        CheckIcons();
        CheckGameDetection();
    }

    private static void CheckIcons()
    {
        try
        {
            var names = EnumerateIconKeys();
            if (names.Count == 0)
            {
                Log.Warn("自检：未能枚举到任何图标资源");
                return;
            }

            var failed = new List<string>();
            foreach (var name in names)
            {
                if (SvgIconLoader.Get(name) is null) failed.Add(name);
            }

            if (failed.Count == 0)
                Log.Info($"自检：{names.Count} 个图标全部解析成功");
            else
                Log.Warn($"自检：{names.Count} 个图标中有 {failed.Count} 个解析失败 → {string.Join(", ", failed)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"自检（图标）执行失败：{ex.Message}");
        }
    }

    private static void CheckGameDetection()
    {
        try
        {
            var candidates = GameLocator.FindAll(SettingsStore.Current.GameDir);
            if (candidates.Count == 0)
            {
                Log.Warn("自检：没有找到星露谷安装目录（Steam / GOG / Xbox / 常见路径都试过了）");
                return;
            }

            Log.Info($"自检：找到 {candidates.Count} 个游戏目录候选 → " +
                     string.Join("；", candidates.Select(c => $"{c.Directory}（{c.Source}）")));

            if (!StardewInstall.TryCreate(candidates[0].Directory, out var install) || install is null)
            {
                Log.Warn("自检：候选目录校验失败");
                return;
            }

            Log.Info($"自检：主程序 = {Path.GetFileName(install.Executable)}；" +
                     $"游戏版本 = {install.GameVersion ?? "未知"}；" +
                     $"SMAPI = {(install.HasSmapi ? install.SmapiVersion ?? "版本未知" : "未安装")}");

            CheckModsDirectory(install);
        }
        catch (Exception ex)
        {
            Log.Warn($"自检（游戏探测）执行失败：{ex.Message}");
        }
    }

    /// <summary>统计 Mods 目录并试解析一份真实 manifest，验证扫描深度与 JSON 容错。</summary>
    private static void CheckModsDirectory(StardewInstall install)
    {
        var modsDirectory = install.ModsDirectory;
        if (!Directory.Exists(modsDirectory))
        {
            Log.Warn($"自检：游戏目录下没有 Mods 文件夹（{modsDirectory}）");
            return;
        }

        var topLevel = Directory.GetDirectories(modsDirectory).Length;
        var disabled = Directory.GetDirectories(modsDirectory)
            .Count(path => Path.GetFileName(path).StartsWith('.'));

        var manifests = Directory.EnumerateFiles(modsDirectory, "manifest.json", SearchOption.AllDirectories).ToList();
        Log.Info($"自检：Mods 下顶层目录 {topLevel} 个（其中点开头视为禁用 {disabled} 个），" +
                 $"递归找到 manifest.json {manifests.Count} 个");

        if (manifests.Count == 0) return;

        // 真实 manifest 里存在尾随逗号，这里验证解析选项是否够宽容
        var parsed = 0;
        var failed = 0;
        foreach (var file in manifests.Take(200))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                if (document.RootElement.TryGetProperty("UniqueID", out _)) parsed++;
                else failed++;
            }
            catch
            {
                failed++;
            }
        }

        Log.Info($"自检：抽样解析 manifest {Math.Min(200, manifests.Count)} 份，成功 {parsed} 份，失败 {failed} 份");
    }

    private static List<string> EnumerateIconKeys()
    {
        var result = new List<string>();
        var assembly = typeof(SelfCheck).Assembly;

        using var stream = assembly.GetManifestResourceStream(AssemblyResourceName);
        if (stream is null) return result;

        using var reader = new ResourceReader(stream);
        foreach (DictionaryEntry entry in reader)
        {
            if (entry.Key is not string key) continue;

            var normalized = key.Replace('\\', '/').ToLowerInvariant();
            var index = normalized.IndexOf(ResourcePathMarker, StringComparison.Ordinal);
            if (index < 0 || !normalized.EndsWith(".svg", StringComparison.Ordinal)) continue;

            result.Add(normalized[(index + ResourcePathMarker.Length)..^4]);
        }

        return result;
    }
}
