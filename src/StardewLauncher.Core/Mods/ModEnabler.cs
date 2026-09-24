using System.IO;
using Microsoft.VisualBasic.FileIO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// Mod 的启停与删除。启停完全依赖 SMAPI 的原生机制：目录名以 "." 开头即被跳过，
/// 因此这里只重命名文件夹，不搬移任何文件。
/// </summary>
public static class ModEnabler
{
    /// <summary>启用或禁用一个 Mod。成功后会同步更新传入条目的目录字段与状态。</summary>
    public static bool TrySetEnabled(ModEntry mod, bool enabled, out string? error)
    {
        error = null;

        var source = mod.FolderPath;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            error = $"Mod 目录不存在：{source}";
            return false;
        }

        var parent = Path.GetDirectoryName(source);
        if (string.IsNullOrWhiteSpace(parent))
        {
            error = $"无法确定上级目录：{source}";
            return false;
        }

        var currentName = Path.GetFileName(source);
        var currentEnabled = !currentName.StartsWith('.');

        if (currentEnabled == enabled)
        {
            Sync(mod, source, currentName);
            return true;
        }

        var rawName = string.IsNullOrWhiteSpace(mod.RawFolderName) ? currentName.TrimStart('.') : mod.RawFolderName;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            error = "目录名不合法，无法启停";
            return false;
        }

        var targetName = enabled ? rawName : "." + rawName;
        var target = Path.Combine(parent, targetName);

        if (Directory.Exists(target) || File.Exists(target))
        {
            error = $"目标目录已存在：{targetName}";
            return false;
        }

        try
        {
            Directory.Move(source, target);
        }
        catch (Exception ex)
        {
            error = $"重命名失败：{ex.Message}";
            Log.Warn($"Mod 启停失败：{source} → {targetName}（{ex.Message}）");
            return false;
        }

        Sync(mod, target, targetName);
        Log.Info($"Mod 已{(enabled ? "启用" : "禁用")}：{mod.DisplayName}");
        return true;
    }

    /// <summary>删除整个 Mod 文件夹。可按设置送进回收站或直接删除。</summary>
    public static bool TryDelete(ModEntry mod, bool toRecycleBin, out string? error)
    {
        error = null;

        var path = mod.FolderPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            error = $"Mod 目录不存在：{path}";
            return false;
        }

        try
        {
            if (toRecycleBin)
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            else
            {
                Directory.Delete(path, true);
            }

            Log.Info($"已删除 Mod：{mod.DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"删除失败：{ex.Message}";
            Log.Warn($"Mod 删除失败：{path}（{ex.Message}）");
            return false;
        }
    }

    private static void Sync(ModEntry mod, string path, string folderName)
    {
        var disabled = folderName.StartsWith('.');

        mod.FolderPath = path;
        mod.FolderName = folderName;
        mod.RawFolderName = folderName.TrimStart('.');

        // manifest 本身不合法时保持 Invalid，避免把坏 Mod 误标成正常启用
        mod.State = disabled
            ? ModState.Disabled
            : mod.Manifest is null ? ModState.Invalid : ModState.Enabled;
    }
}
