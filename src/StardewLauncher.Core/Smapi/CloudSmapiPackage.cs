namespace StardewLauncher.Core.Smapi;

/// <summary>云端文件库里的一份 SMAPI 安装包。<see cref="SizeBytes"/> 为 0 表示该来源未提供文件大小。</summary>
public sealed record CloudSmapiPackage(string Version, string FileName, string DownloadUrl, long SizeBytes);
