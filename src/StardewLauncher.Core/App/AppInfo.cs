namespace StardewLauncher.Core.App;

/// <summary>
/// 应用自身的元信息。版本号、作者、仓库与 QQ 群只在这里定义一处，
/// 界面与文档统一引用这里的常量，避免多处硬编码导致不一致。
/// </summary>
public static class AppInfo
{
    public const string Name = "星露谷启动器";
    public const string Version = "1.7.0";
    public const string VersionDisplay = "v1.7.0";
    public const string Author = "mmm";
    public const string GitHubUrl = "https://github.com/OrangeArtc0915/SVE_mananger";
    public const string GiteeUrl = "https://gitee.com/orangearc655743/SVE_mananger_File";
    public const string WebsiteUrl = "https://orangeartc0915.github.io/SVE_mananger/";

    /// <summary>官网的使用手册（Wiki）入口。</summary>
    public const string WikiUrl = "https://orangeartc0915.github.io/SVE_mananger/wiki/";

    public const string QqGroup = "1034243331";
}
