namespace StardewLauncher.Core.Smapi;

/// <summary>SMAPI 的一个发布版本信息。</summary>
public sealed record SmapiRelease(string Version, string DownloadUrl, string ReleasePageUrl, DateTimeOffset? PublishedAt);
