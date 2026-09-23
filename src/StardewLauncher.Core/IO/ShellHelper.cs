using System.Diagnostics;
using System.IO;
using Microsoft.VisualBasic.FileIO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.IO;

/// <summary>调用系统外壳打开网址或目录。</summary>
public static class ShellHelper
{
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败 {url}：{ex.Message}");
        }
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!Directory.Exists(path))
            {
                Log.Warn($"目录不存在，无法打开：{path}");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败 {path}：{ex.Message}");
        }
    }

    /// <summary>删除整个目录。toRecycleBin 为真时送进回收站，用户还能捞回来。</summary>
    public static bool TryDeleteDirectory(string path, bool toRecycleBin, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            error = $"目录不存在：{path}";
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

            return true;
        }
        catch (Exception ex)
        {
            error = $"删除失败：{ex.Message}";
            Log.Warn($"删除目录失败 {path}：{ex.Message}");
            return false;
        }
    }
}
