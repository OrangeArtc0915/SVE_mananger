namespace StardewLauncher.Core.Smapi;

/// <summary>已定位好的 SMAPI 安装器。<see cref="PackageRoot"/> 是安装包解压后的根目录。</summary>
public sealed record SmapiInstallerPackage(string PackageRoot, string InstallerExe, string InstallScript);
