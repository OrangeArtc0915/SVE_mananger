using System.IO;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Appearance;

/// <summary>背景素材导入结果。</summary>
public sealed record BackgroundImport(bool Ok, string Message, BackgroundKind Kind, string Path);

/// <summary>
/// 个性化背景的素材管理：把用户选的文件收进数据目录、清理旧素材。
/// 只负责文件，界面怎么显示由 App 层的 BackgroundLayer 决定。
///
/// 为什么要把素材复制进数据目录：用户往往从下载目录或 U 盘选图，过几天原文件被删或移走，
/// 背景就变白了。收进自己的数据目录后路径永不失效，也能随程序一起搬走。
/// </summary>
public static class BackgroundService
{
    /// <summary>支持直接当背景的图片（GIF 会被当成动图单独处理）。</summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    private static readonly string[] VideoExtensions = [".mp4", ".wmv", ".avi"];

    /// <summary>背景素材目录。</summary>
    public static string StorageDirectory => Path.Combine(Paths.Data, "Background");

    // ————— 判定 —————

    /// <summary>按扩展名判定背景类型。不支持的类型返回 None。</summary>
    public static BackgroundKind DetectKind(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BackgroundKind.None;

        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".gif") return BackgroundKind.Gif;
        if (VideoExtensions.Contains(extension)) return BackgroundKind.Video;
        if (ImageExtensions.Contains(extension)) return BackgroundKind.Image;

        return BackgroundKind.None;
    }

    /// <summary>这个文件能不能当背景（供文件选择框过滤）。</summary>
    public static bool IsSupportedMedia(string path) => DetectKind(path) != BackgroundKind.None;

    // ————— 导入 —————

    /// <summary>把指定文件设为背景。会把文件复制进数据目录并清掉上一次的素材。</summary>
    public static BackgroundImport ImportFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new BackgroundImport(false, "文件不存在", BackgroundKind.None, string.Empty);

        var kind = DetectKind(sourcePath);
        if (kind == BackgroundKind.None)
        {
            return new BackgroundImport(false,
                "不支持的文件类型。可用的有 jpg / png / bmp / gif / mp4。",
                BackgroundKind.None, string.Empty);
        }

        try
        {
            Directory.CreateDirectory(StorageDirectory);

            // 先清旧的：背景只保留一份，免得数据目录越攒越大
            ClearFiles();

            var target = Path.Combine(StorageDirectory, "background" + Path.GetExtension(sourcePath).ToLowerInvariant());
            File.Copy(sourcePath, target, overwrite: true);

            Log.Info($"背景已导入：{Path.GetFileName(sourcePath)} → {target}（类型 {kind}）");

            return new BackgroundImport(true, $"已设为背景：{Path.GetFileName(sourcePath)}", kind, target);
        }
        catch (Exception ex)
        {
            Log.Error("导入背景失败", ex);
            return new BackgroundImport(false, $"导入背景失败：{ex.Message}", BackgroundKind.None, string.Empty);
        }
    }

    /// <summary>清掉当前背景素材。</summary>
    public static void Clear()
    {
        try
        {
            ClearFiles();

            Log.Info("已清除个性化背景素材");
        }
        catch (Exception ex)
        {
            Log.Warn($"清除背景素材失败：{ex.Message}");
        }
    }

    // ————— 内部 —————

    private static void ClearFiles()
    {
        if (!Directory.Exists(StorageDirectory)) return;

        foreach (var file in Directory.GetFiles(StorageDirectory))
        {
            try { File.Delete(file); } catch (Exception ex) { Log.Warn($"删除旧背景失败：{ex.Message}"); }
        }
    }
}
