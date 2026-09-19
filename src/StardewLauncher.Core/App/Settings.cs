namespace StardewLauncher.Core.App;

public enum ThemeMode
{
    Light = 0,
    Dark = 1,
    System = 2
}

public enum AccentTheme
{
    Stardew = 0,
    SkyBlue = 1,
    BerryPink = 2,
    Autumn = 3
}

/// <summary>文件下载使用的源。</summary>
public enum DownloadSource
{
    GitHub = 0,
    Gitee = 1
}

/// <summary>窗口内容区背景的类型。</summary>
public enum BackgroundKind
{
    /// <summary>不设背景，用主题渐变。</summary>
    None = 0,

    Image = 1,

    /// <summary>动图，逐帧播放。</summary>
    Gif = 2,

    /// <summary>视频，循环静音播放。</summary>
    Video = 3
}

/// <summary>背景的铺放方式。</summary>
public enum BackgroundFit
{
    /// <summary>等比铺满，超出部分裁掉（默认，不留空边）。</summary>
    Cover = 0,

    /// <summary>等比完整显示，可能留空边。</summary>
    Contain = 1,

    /// <summary>拉伸填满，可能变形。</summary>
    Fill = 2
}

/// <summary>全局设置。落盘到 Paths.SettingsFile。</summary>
public sealed class Settings
{
    public int Version { get; set; } = 1;

    public string Language { get; set; } = "zh-CN";

    public ThemeMode Theme { get; set; } = ThemeMode.Light;

    public AccentTheme AccentTheme { get; set; } = AccentTheme.Stardew;

    /// <summary>最近一次使用的游戏目录。</summary>
    public string GameDir { get; set; } = string.Empty;

    /// <summary>新建实例时 Mod 库的默认根目录，留空则用 Paths.Instances。</summary>
    public string ModsLibraryRoot { get; set; } = string.Empty;

    public string LastSelectedInstanceId { get; set; } = string.Empty;

    public bool BackupBeforeImport { get; set; } = true;

    public bool DeleteToRecycleBin { get; set; } = true;

    /// <summary>Mod 库目录（存放已解压、待安装的 Mod）。为空时会在启动时尝试自动检测。</summary>
    public string ModLibraryDirectory { get; set; } = string.Empty;

    public bool ModUpdateCheckEnabled { get; set; } = true;

    public bool SmapiUpdateCheckEnabled { get; set; } = true;

    /// <summary>启动时自动检查更新。默认关闭，避免撞 GitHub API 限流。</summary>
    public bool CheckUpdateOnStartup { get; set; }

    /// <summary>
    /// Nexus Mods 个人 API 密钥。
    /// 只保存在本机设置文件里，绝不写进源码、日志或版本库；界面上只显示掩码。
    /// 仅用于查询 Mod 详情与在线下载；查更新可以走 smapi.io 的公开接口，无需此密钥。
    /// </summary>
    public string NexusApiKey { get; set; } = string.Empty;

    /// <summary>监控下载目录（浏览器下载 Mod 后从这里入库）。为空时启动会写入系统默认下载目录。</summary>
    public string DownloadFolder { get; set; } = string.Empty;

    /// <summary>是否已启用 nxm:// 协议关联（用户手动开启）。</summary>
    public bool NxmProtocolEnabled { get; set; }

    /// <summary>下载完成后是否自动解压入库到 Mod 库。默认 true。</summary>
    public bool AutoInstallToLibrary { get; set; } = true;

    /// <summary>文件下载使用的源。国内网络访问 GitHub 较慢时可切到 Gitee。</summary>
    public DownloadSource DownloadSource { get; set; } = DownloadSource.GitHub;

    /// <summary>下载完成后自动安装到当前实例的 Mods 目录（默认关闭，由用户在设置页开启）。</summary>
    public bool AutoInstallToInstance { get; set; }

    /// <summary>天气小组件显示的城市名。留空则不显示天气。</summary>
    public string WeatherCity { get; set; } = string.Empty;

    /// <summary>城市解析出的纬度（由地理编码缓存，不手工填）。</summary>
    public double? WeatherLatitude { get; set; }

    /// <summary>城市解析出的经度。</summary>
    public double? WeatherLongitude { get; set; }

    /// <summary>天气数据缓存时间。</summary>
    public DateTime? WeatherFetchedAt { get; set; }

    public int MaxLogFileCount { get; set; } = 16;

    public long MaxLogFileSize { get; set; } = 8 * 1024 * 1024;

    // ————— 樱花FRP —————

    /// <summary>
    /// 樱花FRP 访问密钥。与 Nexus 密钥一样只保存在本机设置文件里，绝不写进日志或版本库。
    /// 它等价于账号密码，泄露后别人可以拿你的账号开隧道。
    /// </summary>
    public string SakuraAccessKey { get; set; } = string.Empty;

    /// <summary>上次启动过的樱花FRP 隧道 ID。</summary>
    public int SakuraTunnelId { get; set; }

    /// <summary>新建隧道时默认用的名字。</summary>
    public string SakuraTunnelName { get; set; } = "星露谷";

    // ————— 个性化背景 —————

    /// <summary>窗口内容区背景类型。</summary>
    public BackgroundKind BackgroundKind { get; set; } = BackgroundKind.None;

    /// <summary>
    /// 背景文件绝对路径。用户选的文件会被复制到数据目录，避免原文件被移动/删除后背景失效。
    /// </summary>
    public string BackgroundFile { get; set; } = string.Empty;

    public BackgroundFit BackgroundFit { get; set; } = BackgroundFit.Cover;

    /// <summary>
    /// 背景压暗/压淡的百分比（0-80）。深色主题压黑、浅色主题压白，
    /// 保证直接铺在背景上的页面标题仍然看得清。
    /// </summary>
    public int BackgroundDim { get; set; } = 45;
}
