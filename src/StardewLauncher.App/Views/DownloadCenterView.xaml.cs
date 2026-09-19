using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.App;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Nexus;

namespace StardewLauncher.App.Views;

/// <summary>下载目录里一条待入库项，带可勾选的选中状态。</summary>
public sealed class PendingPackageItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public PendingPackageItem(PendingModPackage package)
    {
        Package = package;

        var megabytes = package.SizeBytes / 1024d / 1024d;
        var time = package.LastWriteTime == DateTime.MinValue
            ? "未知时间"
            : package.LastWriteTime.ToString("yyyy-MM-dd HH:mm");

        MetaText = $"{megabytes:0.0} MB · {time}";
    }

    public PendingModPackage Package { get; }

    public string FileName => Package.FileName;

    public string MetaText { get; }

    public bool HasLooksLikeMod => Package.LooksLikeMod;

    public string ModBadgeText => $"含 {Package.ContainedModCount} 个 Mod";

    public bool HasInspectError => !string.IsNullOrWhiteSpace(Package.InspectError);

    public string InspectError => Package.InspectError ?? "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 下载中心视图：Nexus 查询（只查详情，下载走网页）、直链下载入库、下载目录监控入库、游戏本体官方入口。
/// 所有网络与磁盘操作都在后台线程，失败一律给出可读提示，不抛异常。
/// </summary>
public partial class DownloadCenterView : UserControl
{
    private readonly OutlineButton[] _tabs;
    private readonly UIElement[] _panels;

    private readonly List<PendingPackageItem> _packages = [];

    /// <summary>本轮窗口生命周期内已成功入库的文件，扫描时排除，避免重复入库。</summary>
    private readonly HashSet<string> _processed = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _downloadCts;
    private string _watchFolder = "";
    private string _currentModUrl = "";
    private bool _scanning;
    private bool _importing;

    public DownloadCenterView()
    {
        InitializeComponent();

        _tabs = [BtnTabNexus, BtnTabUrl, BtnTabFolder, BtnTabStore];
        _panels = [PanNexus, PanUrl, PanFolder, PanStore];

        PanStoreLinks.ItemsSource = GameStoreLinks.All;

        SwitchTab(0);
        RefreshKeyState();
        RefreshWatchFolder();
        ApplyPackages();
    }

    /// <summary>宿主销毁时取消仍在进行的下载。</summary>
    public void Shutdown()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    // ————— 分区切换 —————

    private void SwitchTab(int index)
    {
        for (var i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].Tone = i == index ? ButtonTone.Solid : ButtonTone.Outline;
            _panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (int.TryParse(tag, out var index)) SwitchTab(index);
    }

    // ————— 分区 1：Nexus 查询 —————

    private void RefreshKeyState()
        => BarNexusNoKey.Visibility = NexusApi.HasApiKey ? Visibility.Collapsed : Visibility.Visible;

    private void ShowNexusNotice(string text, bool isError)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            LabNexusNotice.Visibility = Visibility.Collapsed;
            return;
        }

        LabNexusNotice.Text = text;
        LabNexusNotice.SetResourceReference(ForegroundProperty, isError ? "Status.Danger" : "Text.Secondary");
        LabNexusNotice.Visibility = Visibility.Visible;
    }

    private async void OnNexusQueryClick(object sender, RoutedEventArgs e)
    {
        var input = TxtModInput.Text?.Trim() ?? "";

        if (input.Length == 0)
        {
            ShowNexusNotice("请输入 Mod ID 或 N 网页面链接。", true);
            return;
        }

        // 不是 ID/链接就当成关键词：Nexus 没有搜索接口，只能开网页搜索
        if (!NexusApi.TryParseModId(input, out var modId))
        {
            ShellHelper.OpenUrl(NexusApi.BuildSearchUrl(input));
            ShowNexusNotice("Nexus 没有搜索接口，已为你打开网页搜索。", false);
            CardNexusResult.Visibility = Visibility.Collapsed;
            return;
        }

        if (!NexusApi.HasApiKey)
        {
            RefreshKeyState();
            ShowNexusNotice("还没有配置 Nexus API Key，请到设置页填写。", true);
            CardNexusResult.Visibility = Visibility.Collapsed;
            return;
        }

        BtnNexusQuery.IsEnabled = false;
        BtnNexusQuery.Content = "查询中…";
        ShowNexusNotice("正在查询…", false);

        try
        {
            var info = await NexusApi.GetModAsync(modId);

            if (info is null)
            {
                CardNexusResult.Visibility = Visibility.Collapsed;
                ShowNexusNotice($"未查到 Mod {modId}：可能是 ID 不存在、API Key 无效、被限流或网络受限（详见日志）。", true);
                return;
            }

            ShowNexusResult(info);
        }
        catch (Exception ex)
        {
            Log.Error($"Nexus 查询界面异常：mods/{modId}", ex);
            CardNexusResult.Visibility = Visibility.Collapsed;
            ShowNexusNotice($"查询失败：{ex.Message}", true);
        }
        finally
        {
            BtnNexusQuery.IsEnabled = true;
            BtnNexusQuery.Content = "查询";
        }
    }

    private void ShowNexusResult(NexusModInfo info)
    {
        _currentModUrl = info.ModPageUrl;

        LabModName.Text = info.Name;
        LabModMeta.Text = $"作者：{(string.IsNullOrWhiteSpace(info.Author) ? "未知" : info.Author)}" +
                          $" · 版本：{(string.IsNullOrWhiteSpace(info.Version) ? "未知" : info.Version)}";
        LabModSummary.Text = string.IsNullOrWhiteSpace(info.Summary) ? "（该 Mod 没有填写简介）" : info.Summary;

        var stats = $"点赞 {info.EndorsementCount}";
        if (info.UpdatedAt is not null) stats += $" · 更新于 {info.UpdatedAt.Value:yyyy-MM-dd}";
        LabModStats.Text = stats;

        PillNewVersion.Visibility = info.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        CardNexusResult.Visibility = Visibility.Visible;
        ShowNexusNotice("", false);
    }

    private void OnOpenModPageClick(object sender, RoutedEventArgs e) => ShellHelper.OpenUrl(_currentModUrl);

    private void OnCopyModLinkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentModUrl)) return;

        try
        {
            Clipboard.SetText(_currentModUrl);
            ShowNexusNotice("已复制链接到剪贴板。", false);
        }
        catch (Exception ex)
        {
            Log.Warn($"复制链接失败：{ex.Message}");
            ShowNexusNotice("复制失败，可以手动选中链接复制。", true);
        }
    }

    // ————— 分区 2：直链下载 —————

    private async void OnStartDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_downloadCts is not null) return;

        var url = TxtUrl.Text?.Trim() ?? "";
        if (url.Length == 0)
        {
            LabUrlResult.Text = "请输入下载直链。";
            return;
        }

        var library = SettingsStore.Current.ModLibraryDirectory;
        if (string.IsNullOrWhiteSpace(library))
        {
            LabUrlResult.Text = "还没有设置 Mod 库目录，请先到设置页选择。";
            return;
        }

        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;

        BtnStartUrl.IsEnabled = false;
        PanUrlProgress.Visibility = Visibility.Visible;
        SetUrlProgress(0);
        LabUrlResult.Text = "正在下载…";

        var progress = new Progress<double>(SetUrlProgress);

        try
        {
            var result = await Task.Run(
                () => DownloadManager.DownloadToLibraryAsync(url, library, progress, token), token);

            LabUrlResult.Text = result.Message;
        }
        catch (Exception ex)
        {
            Log.Error($"直链下载界面异常：{url}", ex);
            LabUrlResult.Text = $"下载失败：{ex.Message}";
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;

            BtnStartUrl.IsEnabled = true;
            PanUrlProgress.Visibility = Visibility.Collapsed;
            SetUrlProgress(0);
        }
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_downloadCts is null) return;

        LabUrlResult.Text = "正在取消…";
        _downloadCts.Cancel();
    }

    private void SetUrlProgress(double value)
    {
        var percent = (int)Math.Round(Math.Clamp(value, 0d, 1d) * 100);
        PrgUrl.Value = percent;
        LabUrlProgress.Text = $"{percent}%";
    }

    // ————— 分区 3：下载目录监控 —————

    private void RefreshWatchFolder()
    {
        var folder = SettingsStore.Current.DownloadFolder;
        if (string.IsNullOrWhiteSpace(folder)) folder = DownloadFolderWatcher.DefaultFolder;

        _watchFolder = folder;
        LabWatchFolder.Text = folder;
        LabWatchFolder.ToolTip = folder;
    }

    private void OnBrowseWatchFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要监控的下载目录",
            Multiselect = false
        };

        if (Directory.Exists(_watchFolder)) dialog.InitialDirectory = _watchFolder;

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        SettingsStore.Current.DownloadFolder = dialog.FolderName;
        SettingsStore.Save();
        RefreshWatchFolder();

        // 换了目录，之前的状态与已处理集合都不再适用
        _packages.Clear();
        _processed.Clear();
        LabPackagesResult.Visibility = Visibility.Collapsed;
        ApplyPackages();
    }

    private async void OnScanPackagesClick(object sender, RoutedEventArgs e)
    {
        if (_scanning || _importing) return;
        await ScanPackagesAsync();
    }

    private async Task ScanPackagesAsync()
    {
        if (_scanning || _importing) return;

        _scanning = true;
        BtnScanPackages.IsEnabled = false;
        PanPackages.Visibility = Visibility.Collapsed;
        LabPackagesEmpty.Visibility = Visibility.Collapsed;
        PrgPackages.Visibility = Visibility.Visible;
        PrgPackages.Value = 0;

        var folder = _watchFolder;
        var excluded = _processed.ToList();
        var progress = new Progress<double>(value => PrgPackages.Value = Math.Clamp(value, 0d, 1d) * 100);

        try
        {
            var found = await Task.Run(() => DownloadFolderWatcher.Scan(folder, excluded, progress));

            _packages.Clear();
            foreach (var package in found) _packages.Add(new PendingPackageItem(package));

            Log.Info($"下载目录扫描完成：{folder}，命中 {_packages.Count} 个候选压缩包");
        }
        catch (Exception ex)
        {
            Log.Error($"下载目录扫描失败：{folder}", ex);
            LabPackagesResult.Text = $"扫描失败：{ex.Message}";
            LabPackagesResult.Visibility = Visibility.Visible;
        }
        finally
        {
            _scanning = false;
            BtnScanPackages.IsEnabled = true;
            PrgPackages.Visibility = Visibility.Collapsed;
            ApplyPackages();
        }
    }

    private void ApplyPackages()
    {
        PanPackages.ItemsSource = _packages.ToList();
        PanPackages.Visibility = _packages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_packages.Count == 0)
        {
            LabPackagesEmpty.Text = string.IsNullOrWhiteSpace(_watchFolder)
                ? "还没有可用的下载目录。"
                : $"在 {_watchFolder} 里没有发现新的 Mod 压缩包。\n下载好 Mod 后点「扫描」即可入库。";
            LabPackagesEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            LabPackagesEmpty.Visibility = Visibility.Collapsed;
        }

        UpdateSelection();
    }

    private void OnPackageClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PendingPackageItem item }) return;

        item.IsSelected = !item.IsSelected;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = _packages.Count(item => item.IsSelected);
        LabSelectedPackages.Text = $"已选 {count} 个";
        BtnImportPackages.IsEnabled = count > 0 && !_importing && !_scanning;
    }

    private async void OnImportPackagesClick(object sender, RoutedEventArgs e)
    {
        if (_importing) return;

        var selected = _packages.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0) return;

        var library = SettingsStore.Current.ModLibraryDirectory;
        if (string.IsNullOrWhiteSpace(library))
        {
            LabPackagesResult.Text = "还没有设置 Mod 库目录，请先到设置页选择。";
            LabPackagesResult.Visibility = Visibility.Visible;
            return;
        }

        _importing = true;
        UpdateSelection();
        BtnImportPackages.Content = "入库中…";

        var installed = new List<string>();
        var problems = new List<string>();
        var succeeded = new List<string>();

        try
        {
            await Task.Run(() =>
            {
                foreach (var item in selected)
                {
                    if (!item.Package.FilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{item.FileName}：不是 zip，无法自动解压");
                        continue;
                    }

                    var result = ModImporter.Import(item.Package.FilePath, library, backupExisting: true);

                    if (result.InstalledCount > 0)
                    {
                        installed.AddRange(result.InstalledFolders);
                        succeeded.Add(item.Package.FilePath);
                    }

                    problems.AddRange(result.Errors.Select(error => $"{item.FileName}：{error}"));
                }
            });

            // 入库成功过的文件记入已处理集合，本轮不再重复列出
            foreach (var path in succeeded) _processed.Add(path);
        }
        catch (Exception ex)
        {
            Log.Error("批量入库失败", ex);
            problems.Add($"入库过程异常：{ex.Message}");
        }
        finally
        {
            _importing = false;
            BtnImportPackages.Content = "入库选中";
        }

        var builder = new StringBuilder();
        builder.Append($"入库完成：成功 {installed.Count} 个");
        if (installed.Count > 0) builder.Append("（" + string.Join("、", installed) + "）");
        builder.Append('。');

        if (problems.Count > 0)
        {
            builder.Append(Environment.NewLine);
            builder.Append("未完成：" + string.Join("；", problems));
        }

        LabPackagesResult.Text = builder.ToString();
        LabPackagesResult.Visibility = Visibility.Visible;

        Log.Info($"下载目录批量入库：成功 {installed.Count} 个，问题 {problems.Count} 条");

        await ScanPackagesAsync();
    }

    // ————— 分区 4：获取游戏本体 —————

    private void OnOpenStoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StoreLink link }) return;
        ShellHelper.OpenUrl(link.Url);
    }
}
