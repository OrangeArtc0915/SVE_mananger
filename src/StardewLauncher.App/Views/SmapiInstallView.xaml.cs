using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Nexus;
using StardewLauncher.Core.Smapi;

namespace StardewLauncher.App.Views;

/// <summary>
/// 下载并安装 SMAPI 的视图。提供两条线路：官方 GitHub Releases 与启动器自建的云端文件库。
/// 下载、解压、安装全部异步执行，不阻塞界面；静默安装会弹出一个控制台窗口，这是安装器自身的正常行为。
/// </summary>
public partial class SmapiInstallView : UserControl
{
    private readonly CancellationTokenSource _cts = new();

    private SmapiChannel _channel = SmapiChannel.Official;
    private SmapiRelease? _officialRelease;
    private CloudSmapiPackage? _cloudPackage;
    private SmapiInstallerPackage? _installerPackage;
    private string? _extractDirectory;
    private bool _busy;

    public SmapiInstallView()
    {
        InitializeComponent();

        LoadInstances();

        if (string.IsNullOrWhiteSpace(TxtGameDir.Text))
            TxtGameDir.Text = InstanceStore.Current?.GameDir ?? string.Empty;

        UpdateChannelButtons();
        ValidateGameDir();

        Loaded += (_, _) => _ = RefreshInfoAsync();
    }

    // ————— 游戏实例选择 —————

    /// <summary>下拉框里的一项：实例本身 + 展示文案。</summary>
    private sealed record InstanceChoice(Instance Instance, string Text);

    /// <summary>
    /// 列出已有实例供直接选择，默认选中当前实例。
    /// 一个实例都没有时隐藏这一行，退化成手填路径 + 浏览。
    /// </summary>
    private void LoadInstances()
    {
        // 先重新解析一遍游戏目录，列表上的「目录不可用」才不会显示成过期状态
        InstanceStore.RefreshInstalls();

        var choices = InstanceStore.All
            .Select(instance => new InstanceChoice(instance, DescribeInstance(instance)))
            .ToList();

        if (choices.Count == 0)
        {
            PanInstancePicker.Visibility = Visibility.Collapsed;
            LabNoInstance.Visibility = Visibility.Visible;
            return;
        }

        CmbInstance.ItemsSource = choices;
        CmbInstance.SelectedItem =
            choices.FirstOrDefault(choice => ReferenceEquals(choice.Instance, InstanceStore.Current)) ?? choices[0];
    }

    /// <summary>「名字（原版 / Mod 端）— 游戏目录」；目录不可用时直接标出来，避免装错地方。</summary>
    private static string DescribeInstance(Instance instance)
    {
        var kind = instance.Install is null ? "目录不可用" : instance.KindText;
        var directory = string.IsNullOrWhiteSpace(instance.GameDir) ? "未设置游戏目录" : instance.GameDir;

        return $"{instance.Name}（{kind}）— {directory}";
    }

    private void OnInstanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInstance.SelectedItem is not InstanceChoice choice) return;

        TxtGameDir.Text = choice.Instance.GameDir ?? string.Empty;
    }

    // ————— 线路与版本信息 —————

    private void OnChannelOfficialClick(object sender, RoutedEventArgs e) => SetChannel(SmapiChannel.Official);

    private void OnChannelCloudClick(object sender, RoutedEventArgs e) => SetChannel(SmapiChannel.CloudMirror);

    /// <summary>切换云端线路的下载源。写回全局设置，设置页与这里共用同一个选项。</summary>
    private void OnSourceGithubClick(object sender, RoutedEventArgs e) => SetDownloadSource(DownloadSource.GitHub);

    private void OnSourceGiteeClick(object sender, RoutedEventArgs e) => SetDownloadSource(DownloadSource.Gitee);

    private void SetDownloadSource(DownloadSource source)
    {
        if (_busy || SettingsStore.Current.DownloadSource == source) return;

        SettingsStore.Current.DownloadSource = source;
        SettingsStore.Save();

        // 换源等于换下载地址，丢掉上一次拿到的包信息
        _cloudPackage = null;
        _installerPackage = null;
        CleanupTemp();
        LabInstallerPaths.Visibility = Visibility.Collapsed;

        UpdateChannelButtons();
        _ = RefreshInfoAsync();
    }

    private void SetChannel(SmapiChannel channel)
    {
        if (_busy || _channel == channel) return;

        _channel = channel;

        // 换线路等于重新挑安装包：丢掉上一次的解压结果，让后续操作重新下载识别
        _installerPackage = null;
        CleanupTemp();
        LabInstallerPaths.Visibility = Visibility.Collapsed;

        UpdateChannelButtons();
        _ = RefreshInfoAsync();
    }

    /// <summary>选中的线路用实心按钮表示；云端线路额外显示下载源切换。</summary>
    private void UpdateChannelButtons()
    {
        var cloud = _channel == SmapiChannel.CloudMirror;

        BtnOfficial.Tone = cloud ? ButtonTone.Outline : ButtonTone.Solid;
        BtnCloud.Tone = cloud ? ButtonTone.Solid : ButtonTone.Outline;

        if (!cloud)
        {
            PanCloudSource.Visibility = Visibility.Collapsed;
            return;
        }

        var gitee = SettingsStore.Current.DownloadSource == DownloadSource.Gitee;

        BtnSourceGithub.Tone = gitee ? ButtonTone.Outline : ButtonTone.Solid;
        BtnSourceGitee.Tone = gitee ? ButtonTone.Solid : ButtonTone.Outline;

        PanCloudSource.Visibility = Visibility.Visible;
    }

    /// <summary>按当前线路刷新下方的版本信息。查询失败只显示文字，不弹异常。</summary>
    private async Task RefreshInfoAsync()
    {
        try
        {
            LabInfo.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            LabInfo.Text = "正在获取版本信息…";
            LabInfoUrl.Visibility = Visibility.Collapsed;

            if (_channel == SmapiChannel.Official)
            {
                var release = await SmapiUpdateChecker.GetLatestAsync(false, _cts.Token);
                if (_channel != SmapiChannel.Official) return;

                _officialRelease = release;

                if (release is null)
                {
                    ShowInfoFailure("无法获取官方版本信息（可能是网络受限）。仍可尝试下载，或改用启动器云端仓库线路。");
                    return;
                }

                var published = release.PublishedAt is { } at
                    ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "未知";

                LabInfo.Text = $"版本号：{release.Version}\n发布时间：{published}";
                ShowInfoUrl(release.DownloadUrl);
                return;
            }

            var package = await SmapiCloudRepository.GetLatestAsync(SettingsStore.Current.DownloadSource, _cts.Token);
            if (_channel != SmapiChannel.CloudMirror) return;

            _cloudPackage = package;

            if (package is null)
            {
                ShowInfoFailure("无法获取云端仓库版本信息，请稍后重试或改用官方线路。");
                return;
            }

            var size = package.SizeBytes > 0 ? FormatSize(package.SizeBytes) : "未知";
            LabInfo.Text = $"文件名：{package.FileName}\n文件大小：{size}\n版本号：{package.Version}";
            ShowInfoUrl(package.DownloadUrl);
        }
        catch (OperationCanceledException)
        {
            // 窗口已关闭，无需处理
        }
        catch (Exception ex)
        {
            Log.Warn($"获取 SMAPI 版本信息失败：{ex.Message}");
            ShowInfoFailure($"获取版本信息失败：{ex.Message}");
        }
    }

    private void ShowInfoFailure(string message)
    {
        LabInfo.Text = message;
        LabInfo.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
        LabInfoUrl.Visibility = Visibility.Collapsed;
    }

    private void ShowInfoUrl(string url)
    {
        LabInfoUrl.Text = $"下载地址：{url}";
        LabInfoUrl.ToolTip = url;
        LabInfoUrl.Visibility = Visibility.Visible;
    }

    // ————— 安装目录 —————

    private void OnGameDirChanged(object sender, TextChangedEventArgs e) => ValidateGameDir();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择星露谷游戏目录",
            Multiselect = false
        };

        var current = TxtGameDir.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(Window.GetWindow(this)) == true) TxtGameDir.Text = dialog.FolderName;
    }

    /// <summary>用 StardewInstall 校验目录，并把是否有效、是否已装 SMAPI 显示出来。</summary>
    private void ValidateGameDir()
    {
        if (LabDirState is null) return;

        var gameDir = TxtGameDir.Text?.Trim() ?? string.Empty;

        if (gameDir.Length == 0)
        {
            LabDirState.Text = "请选择游戏目录。";
            LabDirState.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            return;
        }

        var install = StardewInstall.TryCreate(gameDir, out var created) ? created : null;

        if (install is null)
        {
            LabDirState.Text = "该目录下没有 Stardew Valley.exe / StardewValley.exe，无法安装。";
            LabDirState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
            return;
        }

        var text = $"已识别为有效星露谷目录（{Path.GetFileName(install.Executable)}）";
        text += install.HasSmapi
            ? $"；已安装 SMAPI {install.SmapiVersion ?? "未知"}，继续安装会覆盖现有文件"
            : "；尚未安装 SMAPI";

        LabDirState.Text = text;
        LabDirState.SetResourceReference(TextBlock.ForegroundProperty,
            install.HasSmapi ? "Status.Warn" : "Status.Success");
    }

    // ————— 下载并安装 —————

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var gameDir = TxtGameDir.Text?.Trim() ?? string.Empty;
        if (!GameLocator.IsGameDirectory(gameDir))
        {
            ShowResult("请先选择一个有效的星露谷游戏目录。", isError: true);
            return;
        }

        SetBusy(true);
        CardResult.Visibility = Visibility.Collapsed;

        try
        {
            var package = await PreparePackageAsync();
            if (package is null) return;

            PanProgress.Visibility = Visibility.Visible;
            BarProgress.IsIndeterminate = true;
            LabProgress.Text = "正在安装（会弹出一个控制台窗口，属正常现象）…";

            var result = await SmapiInstaller.InstallSilentlyAsync(gameDir, package, _cts.Token);

            if (!result.Success)
            {
                ShowResult(result.Message, isError: true);
                return;
            }

            // 安装器不看退出码，这里再用文件系统复核一次并展示结果
            var install = StardewInstall.TryCreate(gameDir, out var created) ? created : null;

            if (install is { HasSmapi: true })
                ShowResult($"SMAPI 安装完成：{install.SmapiVersion ?? "版本未知"}", isError: false);
            else
                ShowResult("安装器已执行，但未能识别到 SMAPI。请检查游戏目录，或改用「手动安装…」。", isError: true);
        }
        catch (OperationCanceledException)
        {
            ShowResult("操作已取消。", isError: true);
        }
        catch (Exception ex)
        {
            Log.Error("SMAPI 安装失败", ex);
            ShowResult($"安装失败：{ex.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnManualClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        CardResult.Visibility = Visibility.Collapsed;

        try
        {
            // 还没下载解压过就先自动完成，用户点一次即可打开脚本
            var package = await PreparePackageAsync();
            if (package is null) return;

            SmapiInstaller.LaunchManualInstaller(package);
            ShowResult($"已打开安装脚本，请在控制台窗口中按提示完成安装：\n{package.InstallScript}", isError: false);
        }
        catch (OperationCanceledException)
        {
            ShowResult("操作已取消。", isError: true);
        }
        catch (Exception ex)
        {
            Log.Error("打开 SMAPI 手动安装脚本失败", ex);
            ShowResult($"打开安装脚本失败：{ex.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 下载并解压安装包，然后定位安装器。已准备好的直接复用，重复操作不会重新下载。
    /// 任一步失败都会展示原因并返回 null。
    /// </summary>
    private async Task<SmapiInstallerPackage?> PreparePackageAsync()
    {
        if (_installerPackage is not null) return _installerPackage;

        PanProgress.Visibility = Visibility.Visible;

        var archivePath = await EnsureDownloadedAsync();
        if (archivePath is null) return null;

        var extractDirectory = CreateExtractDirectory();
        _extractDirectory = extractDirectory;

        BarProgress.IsIndeterminate = false;
        BarProgress.Value = 0;
        LabProgress.Text = "正在解压安装包… 0%";

        var progress = new Progress<double>(value =>
        {
            LabProgress.Text = $"正在解压安装包… {(int)Math.Round(Math.Clamp(value, 0d, 1d) * 100)}%";
            BarProgress.Value = Math.Clamp(value, 0d, 1d);
        });

        string? extractError = null;
        var extracted = await Task.Run(
            () => ArchiveExtractor.TryExtract(archivePath, extractDirectory, out extractError, progress, _cts.Token),
            _cts.Token);

        if (!extracted)
        {
            PanProgress.Visibility = Visibility.Collapsed;
            ShowResult($"解压失败：{extractError ?? "未知原因"}", isError: true);
            return null;
        }

        BarProgress.Value = 1;
        LabProgress.Text = "正在识别安装脚本…";

        if (!SmapiInstaller.TryLocateInstaller(extractDirectory, out var package, out var locateError) ||
            package is null)
        {
            PanProgress.Visibility = Visibility.Collapsed;
            ShowResult($"没有在安装包里找到 SMAPI 安装器：{locateError}", isError: true);
            return null;
        }

        _installerPackage = package;

        // 把识别结果摆到界面上，用户手动安装时能确认打开的是哪个脚本
        LabInstallerPaths.Text = $"已识别安装脚本：{package.InstallScript}\n已识别安装器：{package.InstallerExe}";
        LabInstallerPaths.ToolTip = package.InstallScript;
        LabInstallerPaths.Visibility = Visibility.Visible;

        PanProgress.Visibility = Visibility.Collapsed;
        return package;
    }

    /// <summary>按当前线路把安装包下载到 Paths.Downloads 下的 SMAPI 目录，返回本地文件路径。</summary>
    private async Task<string?> EnsureDownloadedAsync()
    {
        string url;
        string fallbackName;

        if (_channel == SmapiChannel.Official)
        {
            var release = _officialRelease ?? await SmapiUpdateChecker.GetLatestAsync(false, _cts.Token);
            _officialRelease = release;

            if (release is null || string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                PanProgress.Visibility = Visibility.Collapsed;
                ShowResult("无法获取官方下载地址（可能是网络受限），请改用启动器云端仓库线路。", isError: true);
                return null;
            }

            url = release.DownloadUrl;
            fallbackName = $"SMAPI-{release.Version}-installer.zip";
        }
        else
        {
            var package = _cloudPackage ??
                          await SmapiCloudRepository.GetLatestAsync(SettingsStore.Current.DownloadSource, _cts.Token);
            _cloudPackage = package;

            if (package is null || string.IsNullOrWhiteSpace(package.DownloadUrl))
            {
                PanProgress.Visibility = Visibility.Collapsed;
                ShowResult("无法获取云端仓库下载地址，请稍后重试或改用官方线路。", isError: true);
                return null;
            }

            url = package.DownloadUrl;
            fallbackName = package.FileName;
        }

        var destination = Path.Combine(Paths.Downloads, "SMAPI", SanitizeFileName(GuessFileName(url, fallbackName)));

        PanProgress.Visibility = Visibility.Visible;
        BarProgress.IsIndeterminate = false;
        BarProgress.Value = 0;
        LabProgress.Text = "正在下载安装包…";

        var progress = new Progress<double>(value =>
        {
            if (value <= 0)
            {
                LabProgress.Text = "正在下载安装包…";
                return;
            }

            BarProgress.Value = Math.Clamp(value, 0d, 1d);
            LabProgress.Text = $"正在下载安装包… {(int)Math.Round(value * 100)}%";
        });

        var result = await ResumableDownloader.DownloadAsync(url, destination, progress, _cts.Token);

        if (!result.Success || string.IsNullOrWhiteSpace(result.FilePath))
        {
            PanProgress.Visibility = Visibility.Collapsed;
            ShowResult($"下载失败：{result.Message}", isError: true);
            return null;
        }

        return result.FilePath;
    }

    /// <summary>本次安装专用的解压目录，放在 Paths.Temp 下，窗口关闭时清理。</summary>
    private static string CreateExtractDirectory()
    {
        var directory = Path.Combine(Paths.Temp, "SMAPI", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    // ————— 界面状态 —————

    private void ShowResult(string message, bool isError)
    {
        PanProgress.Visibility = Visibility.Collapsed;
        BarProgress.IsIndeterminate = false;

        LabResult.Text = message;
        LabResult.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Status.Danger" : "Text.Primary");

        CardResult.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        BtnInstall.IsEnabled = !busy;
        BtnManual.IsEnabled = !busy;
        BtnClose.IsEnabled = busy;
        BtnOfficial.IsEnabled = !busy;
        BtnCloud.IsEnabled = !busy;
        BtnSourceGithub.IsEnabled = !busy;
        BtnSourceGitee.IsEnabled = !busy;
        CmbInstance.IsEnabled = !busy;
        TxtGameDir.IsEnabled = !busy;

        if (busy) return;

        BarProgress.IsIndeterminate = false;
        PanProgress.Visibility = Visibility.Collapsed;
        LabProgress.Text = string.Empty;
    }

    // ————— 取消与清理 —————

    /// <summary>取消正在进行的下载 / 安装。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_busy) _cts.Cancel();
    }

    /// <summary>宿主销毁时中断仍在进行的安装并清理临时目录。</summary>
    public void Shutdown()
    {
        _cts.Cancel();
        CleanupTemp();
    }

    /// <summary>删除本次解压出来的临时目录，失败只记日志。</summary>
    private void CleanupTemp()
    {
        var directory = _extractDirectory;
        _extractDirectory = null;

        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理 SMAPI 临时解压目录失败：{directory}（{ex.Message}）");
        }
    }

    // ————— 工具 —————

    /// <summary>从下载地址取文件名，取不到时用调用方给的兜底名。</summary>
    private static string GuessFileName(string url, string fallback)
    {
        try
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
            var name = Path.GetFileName(Uri.UnescapeDataString(path));

            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }
        catch
        {
            return fallback;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name) builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var result = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "smapi-installer" : result;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.##} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes} B";
    }
}
