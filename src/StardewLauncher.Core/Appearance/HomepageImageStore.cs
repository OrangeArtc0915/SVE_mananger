using System.IO;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Appearance;

/// <summary>
/// 主页「自定义图片」小组件用的素材。
/// 与窗口背景分开存（背景是单槽、导入时会清空自己的目录），互不覆盖。
/// </summary>
public static class HomepageImageStore
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif"];

    private const string FileStem = "widget";

    public static string StorageDirectory => Path.Combine(Paths.Data, "Homepage");

    public static bool IsSupported(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>把选中的图片复制一份进数据目录，返回副本路径。</summary>
    public static string? Import(string sourcePath, out string? error)
    {
        error = null;

        if (!File.Exists(sourcePath))
        {
            error = "文件不存在";
            return null;
        }

        if (!IsSupported(sourcePath))
        {
            error = "只支持 jpg / png / bmp / gif 图片";
            return null;
        }

        try
        {
            Directory.CreateDirectory(StorageDirectory);

            // 换图时先清掉上一张，避免目录里堆一堆用不上的文件
            foreach (var old in Directory.GetFiles(StorageDirectory, FileStem + ".*")) TryDelete(old);

            var target = Path.Combine(StorageDirectory, FileStem + Path.GetExtension(sourcePath).ToLowerInvariant());
            File.Copy(sourcePath, target, overwrite: true);

            Log.Info($"主页图片挂件素材已导入：{target}");
            return target;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"导入主页图片挂件素材失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>删掉已导入的素材。</summary>
    public static void Clear()
    {
        try
        {
            if (!Directory.Exists(StorageDirectory)) return;

            foreach (var file in Directory.GetFiles(StorageDirectory, FileStem + ".*")) TryDelete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理主页图片挂件素材失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Warn($"删除素材失败 {path}：{ex.Message}"); }
    }
}
