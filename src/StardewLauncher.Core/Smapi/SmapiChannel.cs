namespace StardewLauncher.Core.Smapi;

/// <summary>SMAPI 安装线路。</summary>
public enum SmapiChannel
{
    /// <summary>官方线路：从 SMAPI 的 GitHub Releases 获取安装包。</summary>
    Official = 0,

    /// <summary>云端线路：从启动器自建的 GitHub / Gitee 文件库获取安装包。</summary>
    CloudMirror = 1
}
