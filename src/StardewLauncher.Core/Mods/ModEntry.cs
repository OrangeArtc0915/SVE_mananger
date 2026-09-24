namespace StardewLauncher.Core.Mods;

/// <summary>Mod 的启用状态。SMAPI 通过目录名前缀的点来判断是否加载。</summary>
public enum ModState
{
    Enabled,
    Disabled,
    Invalid
}

/// <summary>依赖问题的种类。</summary>
public enum DependencyIssueKind
{
    Missing,
    VersionTooLow,
    Cycle
}

/// <summary>一条依赖问题。RequiredVersion / FoundVersion 在无法确定时为空。</summary>
public sealed record DependencyIssue(string UniqueId, string? RequiredVersion, string? FoundVersion, DependencyIssueKind Kind);

/// <summary>扫描到的一个 Mod 条目。</summary>
public sealed class ModEntry
{
    /// <summary>Mod 根目录（即含 manifest.json 的那一级目录）完整路径。</summary>
    public string FolderPath { get; set; } = "";

    /// <summary>真实文件夹名，可能以 "." 开头（表示已禁用）。</summary>
    public string FolderName { get; set; } = "";

    /// <summary>去掉开头 "." 的文件夹名。</summary>
    public string RawFolderName { get; set; } = "";

    public ModState State { get; set; } = ModState.Enabled;

    public Manifest? Manifest { get; set; }

    /// <summary>解析失败时的可读原因。</summary>
    public string? ParseError { get; set; }

    public string DisplayName
        => string.IsNullOrWhiteSpace(Manifest?.Name) ? RawFolderName : Manifest!.Name;

    public string DisplayAuthor
        => string.IsNullOrWhiteSpace(Manifest?.Author) ? "未知作者" : Manifest!.Author;

    public string DisplayVersion
        => string.IsNullOrWhiteSpace(Manifest?.Version) ? "未知版本" : Manifest!.Version;

    public string DisplayDescription
        => Manifest?.Description ?? "";

    public string UniqueId => Manifest?.UniqueId ?? "";

    /// <summary>内容包还是代码 Mod。</summary>
    public string TypeText => Manifest?.ContentPackFor is null ? "代码 Mod" : "内容包";

    public bool IsEnabled => State == ModState.Enabled;

    /// <summary>依赖解析结果，由 DependencyResolver 填充。</summary>
    public List<DependencyIssue> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    /// <summary>更新检查发现的新版本号，由 ModUpdateChecker 填充；没有新版本时为空。</summary>
    public string? SuggestedVersion { get; set; }

    /// <summary>新版本的页面地址，由 ModUpdateChecker 填充，点更新标记时用它打开。</summary>
    public string? UpdateUrl { get; set; }

    public string[] UpdateKeys => Manifest?.UpdateKeys ?? [];

    public override string ToString() => $"{DisplayName} [{UniqueId}] ({State})";
}
