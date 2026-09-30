using System.IO;
using System.Text.Json;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.Core.Smapi;

/// <summary>单个 Mod 的一键更新结果。</summary>
public sealed record ModUpdateOutcome(ModEntry Mod, bool Updated, string Message)
{
    public string ModName => Mod.DisplayName;
}

/// <summary>一次一键更新的汇总。</summary>
public sealed record ModUpdateInstallReport(int Updated, int Skipped, int Failed,
    IReadOnlyList<ModUpdateOutcome> Items)
{
    public string Summary => $"一键更新完成：成功 {Updated} 个，跳过 {Skipped} 个，失败 {Failed} 个。";
}

/// <summary>
/// 一键更新：只自动处理 UpdateKeys 里带 GitHub 的 Mod —— 调 GitHub 接口取最新 release 里的 zip，
/// 下载后交给 <see cref="ModImporter"/> 安装（同名旧目录自动备份为 .bak-&lt;时间戳&gt;）。
///
/// <para>
/// Nexus / CurseForge / ModDrop 这些没有免登录直链的来源一律跳过，让用户走页面上已有的下载入口；
/// smapi.io 给的只是「新版本页面地址」，不是文件直链，所以更新走 GitHub 自己的发布资产。
/// </para>
/// </summary>
public static class ModUpdateInstaller
{
    private const string UserAgent = "StardewLauncher";
    private const string GitHubKeyPrefix = "GitHub:";

    /// <summary>这个 Mod 能不能自动更新（UpdateKeys 里有 GitHub 键）。</summary>
    public static bool CanAutoUpdate(ModEntry mod) => FindGitHubRepo(mod) is not null;

    public static async Task<ModUpdateInstallReport> RunAsync(IReadOnlyList<ModEntry> targets, string modsDirectory,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var items = new List<ModUpdateOutcome>();
        int updated = 0, skipped = 0, failed = 0;

        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            foreach (var mod in targets)
            {
                skipped++;
                items.Add(new ModUpdateOutcome(mod, false, "没有可用的 Mods 目录"));
            }

            return new ModUpdateInstallReport(0, skipped, 0, items);
        }

        foreach (var mod in targets)
        {
            token.ThrowIfCancellationRequested();

            var name = mod.DisplayName;

            if (FindGitHubRepo(mod) is not { } repo)
            {
                skipped++;
                items.Add(new ModUpdateOutcome(mod, false, "不是 GitHub 来源，请到新版本页面手动下载"));
                continue;
            }

            try
            {
                progress?.Report($"正在查询「{name}」的发布页…");

                var zipUrl = await FindAssetUrlAsync(repo, token);
                if (zipUrl is null)
                {
                    skipped++;
                    items.Add(new ModUpdateOutcome(mod, false, "GitHub 发布页里没有可用的 zip 包"));
                    continue;
                }

                Paths.Init();

                var file = Path.Combine(Paths.Downloads, $"update-{Guid.NewGuid().ToString("N")[..8]}.zip");

                progress?.Report($"正在下载「{name}」…");
                if (!await HttpDownloader.DownloadFileAsync(zipUrl, file, null, token))
                {
                    failed++;
                    items.Add(new ModUpdateOutcome(mod, false, "下载失败"));
                    continue;
                }

                progress?.Report($"正在安装「{name}」…");

                var result = await Task.Run(
                    () => ModImporter.Import(file, modsDirectory, backupExisting: true, null, token), token);

                TryDelete(file);

                if (result.InstalledCount == 0)
                {
                    failed++;
                    items.Add(new ModUpdateOutcome(mod, false,
                        result.Errors.Count > 0 ? result.Errors[0] : "包里没有找到 Mod"));
                    continue;
                }

                var note = TidyUp(mod, modsDirectory, result.InstalledFolders);

                updated++;
                items.Add(new ModUpdateOutcome(mod, true, $"已更新到 {mod.SuggestedVersion}{note}"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"一键更新失败：{name}（{ex.Message}）");
                items.Add(new ModUpdateOutcome(mod, false, ex.Message));
            }
        }

        progress?.Report(string.Empty);
        Log.Info($"一键更新结束：成功 {updated} 个，跳过 {skipped} 个，失败 {failed} 个");

        return new ModUpdateInstallReport(updated, skipped, failed, items);
    }

    /// <summary>从 UpdateKeys 里取出 GitHub 的 owner/repo；没有就返回 null。</summary>
    private static string? FindGitHubRepo(ModEntry mod)
    {
        foreach (var key in mod.UpdateKeys)
        {
            if (!key.StartsWith(GitHubKeyPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var parts = key[GitHubKeyPrefix.Length..].Trim().Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2) return $"{parts[0]}/{parts[1]}";
        }

        return null;
    }

    /// <summary>取最新 release 里的第一个 zip 资产。GitHub 自己生成的源码包不在 assets 里，不会误选。</summary>
    private static async Task<string?> FindAssetUrlAsync(string repo, CancellationToken token)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/latest";

        // 走一次强制刷新：发布页刚发的版本不该被 5 分钟的内存缓存挡住
        HttpDownloader.InvalidateCache(url);

        var text = await HttpDownloader.GetStringAsync(url, UserAgent, token);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var document = JsonDocument.Parse(text);

            if (!document.RootElement.TryGetProperty("assets", out var assets)) return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var name)) continue;
                if (!asset.TryGetProperty("browser_download_url", out var download)) continue;

                var fileName = name.GetString() ?? string.Empty;
                if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                var link = download.GetString();
                if (!string.IsNullOrWhiteSpace(link)) return link;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 GitHub 发布页失败：{repo}（{ex.Message}）");
        }

        return null;
    }

    /// <summary>
    /// 安装完的收尾：包里文件夹名和原来不一样时，把旧目录改成备份名，别让同一份 Mod 留两份；
    /// 原来禁用的，把新目录重新加上点前缀，保持用户的启停状态。
    /// </summary>
    private static string TidyUp(ModEntry mod, string modsDirectory, IReadOnlyList<string> installed)
    {
        var notes = new List<string>();

        foreach (var name in installed)
        {
            var folder = Path.Combine(modsDirectory, name);
            if (!Directory.Exists(folder)) continue;

            var manifest = ManifestParser.Parse(Path.Combine(folder, "manifest.json"), out _);

            // 认不出是同一个 Mod 就别动，宁可留着让用户自己看
            if (manifest is null || string.IsNullOrWhiteSpace(mod.UniqueId)) continue;
            if (!string.Equals(manifest.UniqueId, mod.UniqueId, StringComparison.OrdinalIgnoreCase)) continue;

            if (!SamePath(folder, mod.FolderPath) && Directory.Exists(mod.FolderPath))
            {
                try
                {
                    Directory.Move(mod.FolderPath, ModImporter.MakeBackupName(mod.FolderPath));
                    notes.Add("旧目录已备份");
                }
                catch (Exception ex)
                {
                    Log.Warn($"旧目录备份失败：{mod.FolderPath}（{ex.Message}）");
                }
            }

            if (!mod.IsEnabled && !Path.GetFileName(folder).StartsWith('.'))
            {
                try
                {
                    var disabled = Path.Combine(modsDirectory, "." + name);
                    if (!Directory.Exists(disabled)) Directory.Move(folder, disabled);

                    notes.Add("保持禁用状态");
                }
                catch (Exception ex)
                {
                    Log.Warn($"禁用新目录失败：{folder}（{ex.Message}）");
                }
            }
        }

        return notes.Count == 0 ? string.Empty : $"（{string.Join("，", notes)}）";
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"更新包清理失败：{path}（{ex.Message}）");
        }
    }
}