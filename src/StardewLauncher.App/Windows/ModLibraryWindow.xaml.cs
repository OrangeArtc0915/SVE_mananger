using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.App.Windows;

/// <summary>Mod 库列表里的一条，带可勾选的选中状态。</summary>
public sealed class LibraryModItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public LibraryModItem(LibraryMod mod) => Mod = mod;

    public LibraryMod Mod { get; }

    public string DisplayName => Mod.DisplayName;

    public string Category => Mod.Category;

    public bool HasCategory => !string.IsNullOrWhiteSpace(Mod.Category);

    public string MetaText
    {
        get
        {
            var author = string.IsNullOrWhiteSpace(Mod.Author) ? "未知作者" : Mod.Author;
            var version = string.IsNullOrWhiteSpace(Mod.Version) ? "未知版本" : Mod.Version;
            var type = string.IsNullOrWhiteSpace(Mod.TypeText) ? "" : $" · {Mod.TypeText}";
            return $"{author} · {version}{type}";
        }
    }

    public string? ParseError => Mod.ParseError;

    public bool HasParseError => !string.IsNullOrWhiteSpace(Mod.ParseError);

    public bool IsInstalled => Mod.IsInstalled;

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
/// 「从 Mod 库安装」对话框：浏览库里已解压的 Mod，勾选后批量复制到游戏 Mods 目录。
/// </summary>
public partial class ModLibraryWindow : Window
{
    private readonly string _libraryDirectory;
    private readonly string _gameModsDirectory;

    private readonly List<LibraryModItem> _allItems = [];
    private bool _scanning;
    private bool _onlyUninstalled;

    public ModLibraryWindow(string libraryDirectory, string gameModsDirectory)
    {
        InitializeComponent();

        _libraryDirectory = libraryDirectory;
        _gameModsDirectory = gameModsDirectory;

        LabLibraryPath.Text = libraryDirectory;
        LabLibraryPath.ToolTip = libraryDirectory;

        UpdateOnlyUninstalledButton();
        _ = ScanAsync();
    }

    // ————— 扫描 —————

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;

        PanScanning.Visibility = Visibility.Visible;
        PanMods.Visibility = Visibility.Collapsed;
        LabEmpty.Visibility = Visibility.Collapsed;
        BtnInstall.IsEnabled = false;

        // 重新扫描时保留已勾选的项
        var previousSelection = _allItems
            .Where(item => item.IsSelected)
            .Select(item => item.Mod.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var library = _libraryDirectory;
            var gameMods = _gameModsDirectory;

            var scan = await Task.Run(() => ModLibrary.Scan(library, gameMods));

            foreach (var item in _allItems) item.PropertyChanged -= OnItemPropertyChanged;
            _allItems.Clear();

            foreach (var mod in scan.Mods)
            {
                var item = new LibraryModItem(mod)
                {
                    IsSelected = previousSelection.Contains(mod.SourcePath)
                };

                item.PropertyChanged += OnItemPropertyChanged;
                _allItems.Add(item);
            }

            Log.Info($"Mod 库扫描完成：{_allItems.Count} 个 Mod（{library}）");
        }
        catch (Exception ex)
        {
            Log.Error($"Mod 库扫描失败：{_libraryDirectory}", ex);
        }
        finally
        {
            _scanning = false;
            PanScanning.Visibility = Visibility.Collapsed;
            ApplyFilter();
        }
    }

    // ————— 过滤与选中 —————

    private void ApplyFilter()
    {
        if (PanMods is null) return;

        var keyword = TxtSearch.Text?.Trim() ?? string.Empty;

        var filtered = _allItems
            .Where(item => (!_onlyUninstalled || !item.IsInstalled) && Matches(item, keyword))
            .ToList();

        PanMods.ItemsSource = filtered;
        PanMods.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LabEmpty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (filtered.Count == 0)
        {
            LabEmpty.Text = _allItems.Count == 0
                ? $"没有在 Mod 库里找到任何 Mod。\n库目录：{_libraryDirectory}"
                : "没有匹配的 Mod，换个关键词或关闭「只显示未安装」。";
        }

        UpdateSelectedState();
    }

    private static bool Matches(LibraryModItem item, string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return true;

        var mod = item.Mod;

        return Contains(mod.DisplayName)
               || Contains(mod.Author)
               || Contains(mod.Category)
               || Contains(mod.UniqueId)
               || Contains(mod.SourceFolderName);

        bool Contains(string text)
            => !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryModItem.IsSelected)) UpdateSelectedState();
    }

    private void UpdateSelectedState()
    {
        var count = _allItems.Count(item => item.IsSelected);
        LabSelected.Text = $"已选 {count} 个";
        BtnInstall.IsEnabled = count > 0 && !_scanning;
    }

    private void SetVisibleSelection(bool selected)
    {
        if (PanMods.ItemsSource is not IEnumerable<LibraryModItem> items) return;

        foreach (var item in items.ToList()) item.IsSelected = selected;
        UpdateSelectedState();
    }

    // ————— 工具栏 —————

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRescanClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SetVisibleSelection(true);

    private void OnSelectNoneClick(object sender, RoutedEventArgs e) => SetVisibleSelection(false);

    private void OnToggleOnlyUninstalledClick(object sender, RoutedEventArgs e)
    {
        _onlyUninstalled = !_onlyUninstalled;
        UpdateOnlyUninstalledButton();
        ApplyFilter();
    }

    private void UpdateOnlyUninstalledButton()
        => BtnOnlyUninstalled.Content = $"只显示未安装：{(_onlyUninstalled ? "开" : "关")}";

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    // ————— 安装 —————

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var selected = _allItems.Where(item => item.IsSelected).Select(item => item.Mod).ToList();
        if (selected.Count == 0) return;

        var answer = MessageBox.Show(
            this,
            $"将把选中的 {selected.Count} 个 Mod 复制到游戏 Mods 目录：\n{_gameModsDirectory}\n\n" +
            "同名文件夹会先备份为 .bak-<时间戳>。确定继续吗？",
            "安装 Mod", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        BtnInstall.IsEnabled = false;

        var progress = new Progress<double>(value =>
        {
            var percent = (int)Math.Round(Math.Clamp(value, 0d, 1d) * 100);
            BtnInstall.Content = $"安装中… {percent}%";
        });

        ImportResult result;
        try
        {
            var target = _gameModsDirectory;
            result = await Task.Run(() => ModLibrary.Install(selected, target, backupExisting: true, progress: progress));
        }
        catch (Exception ex)
        {
            BtnInstall.Content = "安装选中";
            BtnInstall.IsEnabled = true;
            Log.Error("从 Mod 库安装失败", ex);
            MessageBox.Show(this, $"安装失败：{ex.Message}", "安装 Mod", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        BtnInstall.Content = "安装选中";
        Log.Info($"Mod 库安装结果：成功 {result.InstalledCount} 个，警告 {result.Warnings.Count} 条，错误 {result.Errors.Count} 条");

        MessageBox.Show(this, BuildResultMessage(result), "安装完成", MessageBoxButton.OK,
            result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        DialogResult = true;
    }

    private static string BuildResultMessage(ImportResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"成功安装 {result.InstalledCount} 个 Mod。");

        if (result.InstalledFolders.Count > 0)
            builder.AppendLine("已安装：" + string.Join("、", result.InstalledFolders));

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"警告（{result.Warnings.Count} 条）：");
            foreach (var warning in result.Warnings) builder.AppendLine("· " + warning);
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"错误（{result.Errors.Count} 条）：");
            foreach (var error in result.Errors) builder.AppendLine("· " + error);
        }

        return builder.ToString().TrimEnd();
    }
}
