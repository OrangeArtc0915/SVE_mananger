using System.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>一次目录扫描的结果。</summary>
public sealed class ModScanResult
{
    public string ModsDirectory { get; init; } = "";

    public bool ModsDirectoryExists { get; init; }

    public IReadOnlyList<ModEntry> Mods { get; init; } = [];

    public int EnabledCount => Mods.Count(m => m.State == ModState.Enabled);

    public int DisabledCount => Mods.Count(m => m.State == ModState.Disabled);

    public int InvalidCount => Mods.Count(m => m.State == ModState.Invalid);

    public string SummaryText => $"共 {Mods.Count} 个，启用 {EnabledCount}，禁用 {DisabledCount}，异常 {InvalidCount}";
}

/// <summary>
/// Mods 目录扫描器。规则贴近 SMAPI：
/// 目录名以 "." 开头视为禁用（其全部子级同样禁用）；一个目录自身有 manifest.json 就是一个 Mod，
/// 只有在没有 manifest.json 时才继续下探子目录，避免把 Mod 内部资源里的 json 误当成 Mod。
/// </summary>
public static class ModScanner
{
    public static ModScanResult Scan(string modsDirectory, IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
        {
            progress?.Report(1);
            return new ModScanResult { ModsDirectory = modsDirectory ?? "", ModsDirectoryExists = false };
        }

        token.ThrowIfCancellationRequested();

        string[] topDirectories;
        try
        {
            topDirectories = Directory.GetDirectories(modsDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"Mods 目录读取失败：{modsDirectory}（{ex.Message}）");
            progress?.Report(1);
            return new ModScanResult { ModsDirectory = modsDirectory, ModsDirectoryExists = true };
        }

        // 固定顺序，保证扫描结果稳定可复现
        Array.Sort(topDirectories, StringComparer.OrdinalIgnoreCase);

        var mods = new List<ModEntry>();
        var processed = 0;

        foreach (var directory in topDirectories)
        {
            token.ThrowIfCancellationRequested();

            var disabled = Path.GetFileName(directory).StartsWith('.');

            try
            {
                Walk(directory, disabled, mods, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个目录出问题不影响整体扫描
                mods.Add(CreateInvalid(directory, disabled, $"扫描失败：{ex.Message}"));
                Log.Warn($"目录扫描失败：{directory}（{ex.Message}）");
            }

            processed++;
            progress?.Report((double)processed / topDirectories.Length);
        }

        progress?.Report(1);
        return new ModScanResult
        {
            ModsDirectory = modsDirectory,
            ModsDirectoryExists = true,
            Mods = mods
        };
    }

    private static void Walk(string directory, bool disabled, List<ModEntry> mods, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var isDisabled = disabled || Path.GetFileName(directory).StartsWith('.');

        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            mods.Add(CreateFromManifest(directory, isDisabled));
            return;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex)
        {
            mods.Add(CreateInvalid(directory, isDisabled, $"目录读取失败：{ex.Message}"));
            return;
        }

        Array.Sort(children, StringComparer.OrdinalIgnoreCase);

        foreach (var child in children)
        {
            Walk(child, isDisabled, mods, token);
        }
    }

    private static ModEntry CreateFromManifest(string directory, bool disabled)
    {
        var entry = Create(directory, disabled);

        var manifest = ManifestParser.Parse(Path.Combine(directory, "manifest.json"), out var error);
        if (manifest is null)
        {
            entry.State = ModState.Invalid;
            entry.ParseError = error ?? "manifest.json 解析失败：未知错误";
            return entry;
        }

        entry.Manifest = manifest;
        entry.State = disabled ? ModState.Disabled : ModState.Enabled;
        return entry;
    }

    private static ModEntry CreateInvalid(string directory, bool disabled, string reason)
    {
        var entry = Create(directory, disabled);
        entry.State = ModState.Invalid;
        entry.ParseError = reason;
        return entry;
    }

    private static ModEntry Create(string directory, bool disabled)
    {
        var folderName = Path.GetFileName(directory);
        return new ModEntry
        {
            FolderPath = directory,
            FolderName = folderName,
            RawFolderName = folderName.TrimStart('.'),
            State = disabled ? ModState.Disabled : ModState.Enabled
        };
    }
}
