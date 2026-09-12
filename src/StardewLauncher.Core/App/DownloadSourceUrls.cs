namespace StardewLauncher.Core.App;

/// <summary>
/// 下载源对应的地址约定。把 GitHub / Gitee 两种源的基地址与相对路径拼接规则集中在这里，
/// 后续接入启动器自更新与文件下载时，只需按用户选择的 <see cref="DownloadSource"/> 调用
/// <see cref="Resolve"/>，不用在各个下载点重复拼字符串。
/// </summary>
public static class DownloadSourceUrls
{
    /// <summary>该源在界面上的显示名。</summary>
    public static string DisplayName(DownloadSource source) => source switch
    {
        DownloadSource.Gitee => "Gitee",
        _ => "GitHub"
    };

    /// <summary>该源对应的项目主页/文件库地址。</summary>
    public static string HomeUrl(DownloadSource source) => source switch
    {
        DownloadSource.Gitee => AppInfo.GiteeUrl,
        _ => AppInfo.GitHubUrl
    };

    /// <summary>
    /// 该源下的某个相对路径应解析成的完整地址（当前仅做拼接约定，后续接入下载时使用）。
    /// GitHub 走 GitHub Releases 的资产路径 <c>releases/download/&lt;tag&gt;/&lt;file&gt;</c>，
    /// Gitee 走文件库的原始文件路径 <c>raw/&lt;branch&gt;/&lt;path&gt;</c>；
    /// 两者都把 <paramref name="relativePath"/> 原样接在约定前缀之后并按段规范化，
    /// 作为自更新与文件下载的统一入口。
    /// </summary>
    public static string Resolve(DownloadSource source, string relativePath)
    {
        var relative = Normalize(relativePath);

        var prefix = source switch
        {
            DownloadSource.Gitee => HomeUrl(source) + "/raw/",
            _ => HomeUrl(source) + "/releases/download/"
        };

        return prefix + relative;
    }

    /// <summary>把反斜杠统一成斜杠，去掉首尾多余的分隔符，便于拼接。</summary>
    private static string Normalize(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;

        var text = relativePath.Replace('\\', '/').Trim();
        return text.Trim('/');
    }
}
