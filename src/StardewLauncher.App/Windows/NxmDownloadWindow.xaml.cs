using System.IO;
using System.Text;
using System.Windows;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Nexus;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 收到 nxm:// 链接后的下载确认窗口：展示 Mod 信息、选择落盘目标、显示下载与入库结果。
/// 整个下载在后台线程执行，界面不阻塞；失败原因原样展示给用户。
/// </summary>
public partial class NxmDownloadWindow : Window
{
    private static readonly string[] KnownArchiveExtensions = [".zip", ".7z", ".rar", ".tar", ".gz"];

    private readonly NxmLink _link;

    private CancellationTokenSource? _cts;
    private bool _downloading;
    private NexusModFile? _file;
    private string? _lastDirectory;

    public NxmDownloadWindow(NxmLink link)
    {
        InitializeComponent();

        _link = link;

        LabLink.Text = link.SafeText + (link.HasDownloadToken ? "（含下载凭证）" : "（未携带下载凭证）");
        LabLink.ToolTip = link.SafeText;

        PanBadges.Visibility = link.IsStardewValley ? Visibility.Visible : Visibility.Collapsed;

        RefreshTargets();
        ApplyTokenState();

        Loaded += (_, _) => _ = LoadInfoAsync();
    }

    // ————— 初始化 —————

    private void ApplyTokenState()
    {
        if (_link.HasDownloadToken) return;

        LabNotice.Text = "该链接未携带下载凭证，无法直接下载。请到 Mod 页面选择文件手动下载。";
        LabNotice.Visibility = Visibility.Visible;

        CardTarget.Visibility = Visibility.Collapsed;
        BtnDownload.Visibility = Visibility.Collapsed;
        BtnOpenPage.Tone = ButtonTone.Solid;
    }

    private void RefreshTargets()
    {
        var library = SettingsStore.Current.ModLibraryDirectory;
        LabLibraryPath.Text = string.IsNullOrWhiteSpace(library) ? "未设置 Mod 库目录" : library;
        LabLibraryPath.ToolTip = library;

        var instance = InstanceStore.Current;

        if (instance is null)
        {
            RadInstance.IsEnabled = false;
            LabInstancePath.Text = "当前没有实例，无法安装到游戏目录";
        }
        else
        {
            LabInstancePath.Text = $"实例「{instance.Name}」→ {instance.ModsDirectory}";
            LabInstancePath.ToolTip = instance.ModsDirectory;
        }
    }

    private async Task LoadInfoAsync()
    {
        try
        {
            var info = await NexusApi.GetModAsync(_link.GameDomain, _link.ModId);
            var files = await NexusApi.GetFilesAsync(_link.GameDomain, _link.ModId);

            if (info is null)
            {
                LabModName.Text = "无法获取 Mod 信息（可能是网络受限），仍可继续下载";
                LabModMeta.Text = NexusApi.LastError ?? "";
            }
            else
            {
                LabModName.Text = string.IsNullOrWhiteSpace(info.Name) ? $"Mod {_link.ModId}" : info.Name;

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(info.Author)) parts.Add($"作者：{info.Author}");
                if (!string.IsNullOrWhiteSpace(info.Version)) parts.Add($"版本：{info.Version}");
                if (info.AdultContent) parts.Add("成人内容");
                LabModMeta.Text = string.Join(" · ", parts);

                LabModSummary.Text = info.Summary ?? "";
            }

            _file = files.FirstOrDefault(item => item.FileId == _link.FileId);

            if (_file is not null)
            {
                var version = string.IsNullOrWhiteSpace(_file.Version) ? "" : $"（{_file.Version}）";
                var size = _file.SizeInBytes > 0 ? $" · {FormatSize(_file.SizeInBytes)}" : "";
                LabFileInfo.Text = $"本次文件：{_file.Name}{version}{size}";
            }
            else if (_link.HasDownloadToken)
            {
                LabFileInfo.Text = $"本次文件 ID：{_link.FileId}";
            }
        }
        catch (Exception ex)
        {
            LabModName.Text = "无法获取 Mod 信息（可能是网络受限），仍可继续下载";
            LabModMeta.Text = ex.Message;
            Log.Warn($"获取 Nexus Mod 信息失败：{ex.Message}");
        }
    }

    // ————— 下载 —————

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;

        var installToInstance = RadInstance.IsChecked == true;
        var instance = InstanceStore.Current;

        var targetDirectory = installToInstance
            ? instance?.ModsDirectory ?? ""
            : SettingsStore.Current.ModLibraryDirectory;

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            MessageBox.Show(this,
                installToInstance ? "当前没有可用实例。" : "还没有设置 Mod 库目录，请先到设置页指定。",
                "从 Nexus 下载", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetDownloading(true);
        CardResult.Visibility = Visibility.Collapsed;

        try
        {
            LabProgress.Text = "正在获取下载直链…";
            BarProgress.IsIndeterminate = true;

            var url = await NexusApi.GetDownloadUrlAsync(_link.GameDomain, _link.ModId, _link.FileId,
                _link.Key, _link.Expires, _cts.Token);

            if (string.IsNullOrWhiteSpace(url))
            {
                ShowFailure("无法获取下载直链。" + (NexusApi.LastError ?? ""));
                return;
            }

            // 只下载到 Mod 库时压缩包直接落在库里；安装到实例时先放临时下载目录
            var saveDirectory = installToInstance ? Paths.Downloads : targetDirectory;
            var destination = Path.Combine(saveDirectory, BuildFileName(url));

            BarProgress.IsIndeterminate = false;
            LabProgress.Text = "正在下载…";

            var progress = new Progress<double>(value =>
            {
                if (value <= 0)
                {
                    LabProgress.Text = "正在下载…";
                    return;
                }

                BarProgress.Value = value;
                LabProgress.Text = $"正在下载… {Math.Round(value * 100)}%";
            });

            var result = await ResumableDownloader.DownloadAsync(url, destination, progress, _cts.Token);

            if (!result.Success)
            {
                ShowFailure(result.Message);
                return;
            }

            BarProgress.Value = 1;
            _lastDirectory = Path.GetDirectoryName(result.FilePath) ?? saveDirectory;

            ImportResult? import = null;

            if (installToInstance)
            {
                LabProgress.Text = "正在安装到当前实例…";
                import = await RunImportAsync(result.FilePath!, instance!.ModsDirectory);
                ShowSuccess(result, import, $"已安装到实例「{instance.Name}」");
            }
            else if (SettingsStore.Current.AutoInstallToLibrary)
            {
                LabProgress.Text = "正在解压入库…";
                import = await RunImportAsync(result.FilePath!, SettingsStore.Current.ModLibraryDirectory);

                // 入库成功后再按开关自动装进当前实例；自动安装失败不影响已完成的入库
                if (SettingsStore.Current.AutoInstallToInstance)
                {
                    if (InstanceStore.Current is { } autoInstance)
                    {
                        LabProgress.Text = $"正在自动安装到实例「{autoInstance.Name}」…";
                        var autoImport = await RunImportAsync(result.FilePath!, autoInstance.ModsDirectory);
                        ShowSuccess(result, import, "已解压到 Mod 库", autoImport, autoInstance.Name);
                    }
                    else
                    {
                        ShowSuccess(result, import, "已解压到 Mod 库", null, null,
                            "未自动安装（当前没有可用的游戏实例），Mod 已入库到 Mod 库");
                    }
                }
                else
                {
                    ShowSuccess(result, import, "已解压到 Mod 库");
                }
            }
            else
            {
                ShowSuccess(result, null, "已下载到 Mod 库");
            }
        }
        catch (OperationCanceledException)
        {
            ShowFailure("下载已取消");
        }
        catch (Exception ex)
        {
            Log.Error("nxm 下载失败", ex);
            ShowFailure(ex.Message);
        }
        finally
        {
            SetDownloading(false);
        }
    }

    private static async Task<ImportResult> RunImportAsync(string archivePath, string modsDirectory)
    {
        try
        {
            return await Task.Run(() => ModImporter.Import(archivePath, modsDirectory, backupExisting: true));
        }
        catch (Exception ex)
        {
            Log.Warn($"解压入库失败：{ex.Message}");
            return new ImportResult(0, [], [], [$"解压入库失败：{ex.Message}"]);
        }
    }

    // ————— 结果展示 —————

    /// <summary>
    /// 展示下载结果。<paramref name="autoInstall"/> 为「下载后自动安装到当前实例」的结果；
    /// 传入 <paramref name="autoInstallNote"/> 时显示一条说明（例如没有可用实例）。
    /// 自动安装失败只影响提示文案，不会把整个下载流程判为失败。
    /// </summary>
    private void ShowSuccess(DownloadResult result, ImportResult? import, string targetText,
        ImportResult? autoInstall = null, string? autoInstallInstanceName = null, string? autoInstallNote = null)
    {
        PanProgress.Visibility = Visibility.Collapsed;
        CardResult.Visibility = Visibility.Visible;
        BtnOpenFolder.Visibility = Visibility.Visible;

        var builder = new StringBuilder();
        builder.AppendLine($"下载完成：{Path.GetFileName(result.FilePath)}");
        builder.AppendLine($"大小：{FormatSize(result.BytesReceived)}");
        builder.AppendLine($"位置：{result.FilePath}");
        builder.Append($"目标：{targetText}");

        LabResult.Text = builder.ToString();

        if (import is not null)
        {
            var importText = new StringBuilder();
            importText.Append($"解压入库：成功 {import.InstalledCount} 个");

            if (import.InstalledFolders.Count > 0)
                importText.Append("（" + string.Join("、", import.InstalledFolders) + "）");

            foreach (var warning in import.Warnings)
            {
                importText.AppendLine();
                importText.Append("警告：" + warning);
            }

            LabImport.Text = importText.ToString();
            LabImport.Visibility = Visibility.Visible;

            if (import.Errors.Count > 0)
            {
                LabResultError.Text = "错误：" + string.Join("；", import.Errors);
                LabResultError.Visibility = Visibility.Visible;
            }
        }

        ShowAutoInstall(autoInstall, autoInstallInstanceName, autoInstallNote);
    }

    /// <summary>显示自动安装到实例的结果；没有配置该行为时保持隐藏。</summary>
    private void ShowAutoInstall(ImportResult? autoInstall, string? instanceName, string? note)
    {
        if (autoInstall is null)
        {
            if (string.IsNullOrWhiteSpace(note)) return;

            LabAutoInstall.Text = note;
            LabAutoInstall.Visibility = Visibility.Visible;
            return;
        }

        var text = new StringBuilder();
        text.Append($"已自动安装到实例「{instanceName}」：成功 {autoInstall.InstalledCount} 个");

        if (autoInstall.InstalledFolders.Count > 0)
            text.Append("（" + string.Join("、", autoInstall.InstalledFolders) + "）");

        foreach (var warning in autoInstall.Warnings)
        {
            text.AppendLine();
            text.Append("警告：" + warning);
        }

        foreach (var error in autoInstall.Errors)
        {
            text.AppendLine();
            text.Append("失败：" + error);
        }

        LabAutoInstall.Text = text.ToString();
        LabAutoInstall.Visibility = Visibility.Visible;
    }

    private void ShowFailure(string message)
    {
        PanProgress.Visibility = Visibility.Collapsed;
        CardResult.Visibility = Visibility.Visible;

        LabResult.Text = "下载未完成";
        LabResultError.Text = string.IsNullOrWhiteSpace(message) ? "原因未知" : message;
        LabResultError.Visibility = Visibility.Visible;

        if (_lastDirectory is not null) BtnOpenFolder.Visibility = Visibility.Visible;
    }

    private void SetDownloading(bool downloading)
    {
        _downloading = downloading;

        PanProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        if (!downloading) BarProgress.IsIndeterminate = false;

        BtnDownload.IsEnabled = !downloading;
        BtnOpenPage.IsEnabled = !downloading;
        RadLibrary.IsEnabled = !downloading;
        RadInstance.IsEnabled = !downloading && InstanceStore.Current is not null;

        if (!downloading) LabProgress.Text = "";
    }

    // ————— 交互 —————

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            _cts?.Cancel();
            return;
        }

        Close();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_downloading) _cts?.Cancel();
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e) => ShellHelper.OpenUrl(_link.ModPageUrl);

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = _lastDirectory;
        if (string.IsNullOrWhiteSpace(folder)) folder = SettingsStore.Current.ModLibraryDirectory;
        if (string.IsNullOrWhiteSpace(folder)) folder = Paths.Downloads;

        ShellHelper.OpenFolder(folder);
    }

    // ————— 工具 —————

    private string BuildFileName(string downloadUrl)
    {
        var suggested = _file?.Name;
        if (string.IsNullOrWhiteSpace(suggested)) suggested = $"mod-{_link.ModId}-{_link.FileId}";

        var version = _file?.Version;
        if (!string.IsNullOrWhiteSpace(version) &&
            !suggested.Contains(version, StringComparison.OrdinalIgnoreCase))
        {
            suggested = $"{suggested} {version}";
        }

        return Sanitize(suggested) + GuessExtension(downloadUrl);
    }

    /// <summary>从直链路径猜压缩包后缀；猜不到按 .zip 处理。</summary>
    private static string GuessExtension(string url)
    {
        try
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
            var extension = Path.GetExtension(path);

            if (!string.IsNullOrWhiteSpace(extension) &&
                Array.Exists(KnownArchiveExtensions,
                    item => item.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            {
                return extension.ToLowerInvariant();
            }
        }
        catch
        {
            // 猜不到就用默认后缀
        }

        return ".zip";
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name) builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length > 120) result = result[..120].Trim();

        return string.IsNullOrWhiteSpace(result) ? "mod" : result;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.##} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes} B";
    }
}
