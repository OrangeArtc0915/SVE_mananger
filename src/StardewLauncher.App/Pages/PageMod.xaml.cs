using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Nexus;

namespace StardewLauncher.App.Pages;

/// <summary>Mod 列表中一条用于界面绑定的数据。包装 ModEntry，补上依赖问题的可读文本。</summary>
public sealed class ModItem
{
    public ModItem(ModEntry entry) => Entry = entry;

    public ModEntry Entry { get; }

    public ModState State => Entry.State;

    public bool IsEnabled => Entry.IsEnabled;

    public string DisplayName => Entry.DisplayName;

    public string DisplayAuthor => Entry.DisplayAuthor;

    public string DisplayVersion => Entry.DisplayVersion;

    public string TypeText => Entry.TypeText;

    public string DisplayDescription => Entry.DisplayDescription;

    public string? ParseError => Entry.ParseError;

    public bool HasIssues => Entry.HasIssues;

    /// <summary>该 Mod 已有的标签胶囊（配色已换算成界面用的画刷）。</summary>
    public List<TagChip> TagChips { get; } = [];

    public bool HasTags => TagChips.Count > 0;

    /// <summary>把该 Mod 的全部依赖问题拼成多行文本，供 ToolTip 使用。</summary>
    public string IssuesText => string.Join(Environment.NewLine, Entry.Issues.Select(DescribeIssue));

    internal static string DescribeIssue(DependencyIssue issue) => issue.Kind switch
    {
        DependencyIssueKind.Missing => string.IsNullOrWhiteSpace(issue.RequiredVersion)
            ? $"缺少前置 {issue.UniqueId}"
            : $"缺少前置 {issue.UniqueId}（需要版本 ≥ {issue.RequiredVersion}）",
        DependencyIssueKind.VersionTooLow =>
            $"需要 {issue.UniqueId} 版本 ≥ {issue.RequiredVersion}，当前 {issue.FoundVersion}",
        _ => $"存在循环依赖：{issue.UniqueId}"
    };
}

/// <summary>依赖问题展开列表里的一行。</summary>
public sealed class ModIssueRow
{
    public ModIssueRow(string modName, string text) => Text = $"{modName} → {text}";

    public string Text { get; }
}

/// <summary>标签胶囊的界面配色。ColorKey → 主题色画刷，软底色由主题色降透明度得到。</summary>
public sealed class TagChip
{
    public TagChip(ModTag tag)
    {
        Id = tag.Id;
        Name = tag.Name;
        (Background, Foreground) = TagPalette.Resolve(tag.ColorKey);
    }

    public string Id { get; }

    public string Name { get; }

    public Brush Background { get; }

    public Brush Foreground { get; }
}

/// <summary>把标签的 ColorKey 映射成主题色画刷。</summary>
internal static class TagPalette
{
    private const byte SoftAlpha = 0x26;

    public static (Brush Background, Brush Foreground) Resolve(string colorKey)
    {
        var resourceKey = colorKey switch
        {
            "Warn" => "Status.Warn",
            "Danger" => "Status.Danger",
            "Success" => "Status.Success",
            _ => "Accent.Base"
        };

        var foreground = Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Transparent;
        var color = (foreground as SolidColorBrush)?.Color ?? Colors.Gray;

        var background = new SolidColorBrush(Color.FromArgb(SoftAlpha, color.R, color.G, color.B));
        background.Freeze();

        return (background, foreground);
    }
}

/// <summary>依赖项表里的一行（界面用）。</summary>
public sealed class DependencyRowItem
{
    public DependencyRowItem(DependencyReportRow row)
    {
        Row = row;
        (KindSoft, KindBrush) = TagPalette.Resolve(row.Kind switch
        {
            DependencyIssueKind.Missing => "Danger",
            DependencyIssueKind.VersionTooLow => "Warn",
            _ => "Warn"
        });
    }

    public DependencyReportRow Row { get; }

    public string ModName => Row.ModName;

    public string IssueText => Row.IssueText;

    public string KindText => Row.Kind switch
    {
        DependencyIssueKind.Missing => "缺失",
        DependencyIssueKind.VersionTooLow => "版本过低",
        _ => "循环"
    };

    /// <summary>循环依赖没有单一缺口，无法从库里补齐。</summary>
    public bool CanFill => Row.Kind != DependencyIssueKind.Cycle && !string.IsNullOrWhiteSpace(Row.MissingUniqueId);

    public string MissingUniqueId => Row.MissingUniqueId;

    public Brush KindSoft { get; }

    public Brush KindBrush { get; }
}

public partial class PageMod : LauncherPage
{
    private readonly List<ModItem> _allItems = [];

    /// <summary>当前选中的标签 Id（多选取并集）。</summary>
    private readonly HashSet<string> _selectedTagIds = new(StringComparer.OrdinalIgnoreCase);

    private ModScanResult? _lastScan;
    private string? _modsDirectory;
    private bool _subscribed;
    private bool _scanning;
    private bool _dirty;
    private bool _busy;
    private bool _depsExpanded;
    private bool _depsView;

    public PageMod()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>进入页面时刷新原版提示并重新扫描当前实例的 Mods 目录。</summary>
    public override void OnEnter()
    {
        UpdateVanillaBar();
        UpdateViewButtons();
        RebuildTagFilter();
        _ = ScanAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;
        _subscribed = true;
        InstanceStore.Changed += OnInstanceChanged;
        ModTagStore.Changed += OnTagsChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        InstanceStore.Changed -= OnInstanceChanged;
        ModTagStore.Changed -= OnTagsChanged;
    }

    /// <summary>标签或关联变化时刷新胶囊与筛选结果。可能在非 UI 线程触发，统一切回 UI 线程。</summary>
    private void OnTagsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnTagsChanged);
            return;
        }

        RebuildTagFilter();
        RefreshTagChips();
        ApplyFilter();
    }

    private void OnInstanceChanged()
    {
        UpdateVanillaBar();
        _ = ScanAsync();
    }

    /// <summary>原版实例不加载 Mod，顶部给一条提示。</summary>
    private void UpdateVanillaBar()
    {
        if (BarVanilla is null) return;

        BarVanilla.Visibility = InstanceStore.Current?.IsVanilla == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ————— 扫描 —————

    /// <summary>
    /// 合并并发扫描请求：扫描期间若又有新请求，只记一个标记，当前扫描结束后补扫一次。
    /// 全部 await 都在 UI 线程恢复，避免跨线程访问界面。
    /// </summary>
    private async Task ScanAsync()
    {
        if (_busy)
        {
            _dirty = true;
            return;
        }

        _busy = true;
        try
        {
            do
            {
                _dirty = false;
                await ScanOnceAsync();
            }
            while (_dirty);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ScanOnceAsync()
    {
        _modsDirectory = InstanceStore.Current?.ModsDirectory;

        if (string.IsNullOrWhiteSpace(_modsDirectory))
        {
            _modsDirectory = null;
            _lastScan = null;
            _allItems.Clear();
            SetScanning(false);
            UpdateSummary();
            UpdateDependencyBar();
            ApplyFilter();
            return;
        }

        SetScanning(true);

        ModScanResult result;
        try
        {
            // 磁盘扫描与依赖解析都放到后台线程，界面只负责渲染结果
            var directory = _modsDirectory;
            result = await Task.Run(() =>
            {
                var scan = ModScanner.Scan(directory);
                DependencyResolver.Evaluate(scan.Mods);
                return scan;
            });
        }
        catch (Exception ex)
        {
            SetScanning(false);
            Log.Error($"Mod 扫描失败：{_modsDirectory}", ex);
            ShowNotice($"Mod 扫描失败：{ex.Message}", true);
            return;
        }

        SetScanning(false);
        _lastScan = result;
        _depsExpanded = false;

        _allItems.Clear();
        foreach (var mod in result.Mods) _allItems.Add(new ModItem(mod));
        RefreshTagChips();

        UpdateSummary();
        UpdateDependencyBar();
        ApplyFilter();
    }

    /// <summary>按标签存储重建每个 Mod 的标签胶囊。</summary>
    private void RefreshTagChips()
    {
        foreach (var item in _allItems)
        {
            item.TagChips.Clear();

            foreach (var tag in ModTagStore.TagsOf(ModTagStore.KeyOf(item.Entry)))
                item.TagChips.Add(new TagChip(tag));
        }
    }

    private void SetScanning(bool scanning)
    {
        _scanning = scanning;

        PanScanning.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        if (!scanning) return;

        PanMods.Visibility = Visibility.Collapsed;
        CardEmpty.Visibility = Visibility.Collapsed;
        ScrollDeps.Visibility = Visibility.Collapsed;
        CardNoDeps.Visibility = Visibility.Collapsed;
    }

    // ————— 界面刷新 —————

    private void UpdateSummary()
    {
        LabSummary.Text = _lastScan?.SummaryText
            ?? (string.IsNullOrWhiteSpace(_modsDirectory) ? "无可用实例" : "尚未扫描");

        LabPath.Text = string.IsNullOrWhiteSpace(_modsDirectory) ? "未选择游戏实例" : _modsDirectory;
        LabPath.ToolTip = LabPath.Text;
    }

    private void UpdateDependencyBar()
    {
        var rows = new List<ModIssueRow>();
        var modCount = 0;

        foreach (var item in _allItems)
        {
            if (!item.HasIssues) continue;

            modCount++;
            foreach (var issue in item.Entry.Issues)
                rows.Add(new ModIssueRow(item.DisplayName, ModItem.DescribeIssue(issue)));
        }

        if (modCount == 0)
        {
            BarDeps.Visibility = Visibility.Collapsed;
            CardDepList.Visibility = Visibility.Collapsed;
            PanDepIssues.ItemsSource = null;
            return;
        }

        LabDeps.Text = _depsExpanded
            ? $"{modCount} 个 Mod 存在依赖问题，点击收起"
            : $"{modCount} 个 Mod 存在依赖问题，点击展开";

        PanDepIssues.ItemsSource = rows;
        BarDeps.Visibility = Visibility.Visible;
        CardDepList.Visibility = _depsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        if (PanMods is null || _scanning) return;

        if (_depsView)
        {
            ShowDependencyView();
            return;
        }

        HideDependencyView();

        var keyword = TxtSearch.Text?.Trim() ?? string.Empty;

        // 过滤只在内存中的 _allItems 上做，绝不重新扫描磁盘；先按搜索词，再按标签（多选取并集）
        var filtered = _allItems
            .Where(item => string.IsNullOrEmpty(keyword) || Matches(item, keyword))
            .Where(MatchesTags)
            .ToList();

        PanMods.ItemsSource = filtered;
        PanMods.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (filtered.Count > 0)
        {
            CardEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        ShowEmptyState();
    }

    /// <summary>选中多个标签时取并集：带任一选中标签即可。</summary>
    private bool MatchesTags(ModItem item)
        => _selectedTagIds.Count == 0 || item.TagChips.Any(chip => _selectedTagIds.Contains(chip.Id));

    private void ShowDependencyView()
    {
        PanMods.Visibility = Visibility.Collapsed;
        CardEmpty.Visibility = Visibility.Collapsed;

        var rows = DependencyReport.Build(_lastScan?.Mods ?? Array.Empty<ModEntry>())
            .Select(row => new DependencyRowItem(row))
            .ToList();

        PanDepRows.ItemsSource = rows;
        ScrollDeps.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CardNoDeps.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HideDependencyView()
    {
        ScrollDeps.Visibility = Visibility.Collapsed;
        CardNoDeps.Visibility = Visibility.Collapsed;
    }

    private static bool Matches(ModItem item, string keyword)
    {
        var entry = item.Entry;

        return Contains(entry.DisplayName)
               || Contains(entry.DisplayAuthor)
               || Contains(entry.UniqueId)
               || Contains(entry.DisplayDescription);

        bool Contains(string text) => text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowEmptyState()
    {
        string title;
        string message;
        var canCreate = false;

        if (string.IsNullOrWhiteSpace(_modsDirectory))
        {
            title = "还没有可用的游戏实例";
            message = "先到「游戏实例」页创建一个实例，Mod 管理会自动读取该实例的 Mods 目录。";
        }
        else if (_lastScan is { ModsDirectoryExists: false })
        {
            title = "没有找到 Mods 文件夹";
            message = $"期望的路径：{_modsDirectory}\n可以点击下面的按钮创建它，然后导入 Mod。";
            canCreate = true;
        }
        else if (_allItems.Count == 0)
        {
            title = "Mods 文件夹是空的";
            message = "还没有安装任何 Mod，点击右上角「导入 Mod」选择一个压缩包（支持 zip / rar / 7z / tar / gz）开始安装。";
        }
        else
        {
            title = "没有匹配的 Mod";
            message = "换个关键词再试试，或清空搜索框查看全部 Mod。";
        }

        CardEmpty.Title = title;
        LabEmpty.Text = message;
        BtnCreateMods.Visibility = canCreate ? Visibility.Visible : Visibility.Collapsed;
        CardEmpty.Visibility = Visibility.Visible;
    }

    private void ShowNotice(string text, bool isError)
    {
        LabNotice.Text = text;
        BarNotice.Visibility = Visibility.Visible;

        BarNotice.SetResourceReference(BackgroundProperty, isError ? "Status.DangerSoft" : "Accent.Faint");
        IconNotice.Icon = isError ? "lucide/triangle-alert" : "lucide/info";
        IconNotice.SetResourceReference(SvgIcon.IconBrushProperty, isError ? "Status.Danger" : "Accent.Base");
    }

    // ————— 工具栏 —————

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        // 占位：本次不联网，仅提示并记日志
        ShowNotice("更新检查将在后续接入", false);
        Log.Info("用户点击了「检查更新」，当前为占位实现");
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e)
        => BarNotice.Visibility = Visibility.Collapsed;

    private void OnDepsToggle(object sender, MouseButtonEventArgs e)
    {
        _depsExpanded = !_depsExpanded;
        UpdateDependencyBar();
    }

    // ————— 视图切换 —————

    private void OnViewAllClick(object sender, RoutedEventArgs e)
    {
        _depsView = false;
        UpdateViewButtons();
        ApplyFilter();
    }

    private void OnViewDepsClick(object sender, RoutedEventArgs e)
    {
        _depsView = true;
        UpdateViewButtons();
        ApplyFilter();
    }

    /// <summary>选中的视图用实心按钮表示。</summary>
    private void UpdateViewButtons()
    {
        BtnViewAll.Tone = _depsView ? ButtonTone.Outline : ButtonTone.Solid;
        BtnViewDeps.Tone = _depsView ? ButtonTone.Solid : ButtonTone.Outline;

        // 一键补齐只在依赖问题视图里出现
        if (BtnFillAllDeps is not null)
            BtnFillAllDeps.Visibility = _depsView ? Visibility.Visible : Visibility.Collapsed;
    }

    // ————— 标签筛选 —————

    /// <summary>按标签列表重建筛选栏；选中的标签用实心胶囊表示。</summary>
    private void RebuildTagFilter()
    {
        PanTagFilter.Children.Clear();

        foreach (var tag in ModTagStore.Tags)
        {
            var selected = _selectedTagIds.Contains(tag.Id);
            var (soft, strong) = TagPalette.Resolve(tag.ColorKey);

            var pill = new Border
            {
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = (CornerRadius)FindResource("Radius.Pill"),
                Background = selected ? strong : soft,
                Cursor = Cursors.Hand,
                Tag = tag,
                ToolTip = "点击筛选该标签（可多选）",
                Child = new TextBlock
                {
                    Text = tag.Name,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = selected ? Brushes.White : strong
                }
            };

            pill.MouseLeftButtonUp += OnTagFilterClick;
            PanTagFilter.Children.Add(pill);
        }

        BtnClearTagFilter.Visibility = _selectedTagIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTagFilterClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModTag tag }) return;

        if (!_selectedTagIds.Remove(tag.Id)) _selectedTagIds.Add(tag.Id);

        RebuildTagFilter();
        ApplyFilter();
    }

    private void OnClearTagFilterClick(object sender, RoutedEventArgs e)
    {
        _selectedTagIds.Clear();
        RebuildTagFilter();
        ApplyFilter();
    }

    /// <summary>打开某个 Mod 的标签管理弹窗。</summary>
    private void OnTagModClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModItem item }) return;

        var dialog = new ModTagWindow(ModTagStore.KeyOf(item.Entry), item.DisplayName)
        {
            Owner = Window.GetWindow(this)
        };

        dialog.ShowDialog();

        // 弹窗里的改动已由 ModTagStore.Changed 触发刷新，这里兜底再刷一次
        RefreshTagChips();
        RebuildTagFilter();
        ApplyFilter();
    }

    // ————— 从 Mod 库补齐依赖 —————

    private async void OnFillDependencyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DependencyRowItem row }) return;

        if (!row.CanFill)
        {
            ShowNotice("循环依赖没有单一的缺失前置，需要手动调整 Mod 组合。", true);
            return;
        }

        var modsDirectory = _modsDirectory;
        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            ShowNotice("还没有可用的 Mods 目录，请先创建游戏实例。", true);
            return;
        }

        var library = SettingsStore.Current.ModLibraryDirectory;
        if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(library))
        {
            ShowNotice("还没有设置 Mod 库目录，请到设置页选择。", true);
            return;
        }

        ShowNotice($"正在扫描 Mod 库查找 {row.MissingUniqueId}…", false);

        IReadOnlyList<LibraryMod> providers;
        try
        {
            var scanProgress = new Progress<double>(value =>
                LabNotice.Text = $"正在扫描 Mod 库… {(int)Math.Round(Math.Clamp(value, 0d, 1d) * 100)}%");

            var target = modsDirectory;
            var scan = await Task.Run(() => ModLibrary.Scan(library, target, scanProgress));
            providers = DependencyReport.FindProviders(scan.Mods, row.MissingUniqueId);
        }
        catch (Exception ex)
        {
            Log.Error($"扫描 Mod 库失败：{library}", ex);
            ShowNotice($"扫描 Mod 库失败：{ex.Message}", true);
            return;
        }

        if (providers.Count == 0)
        {
            ShowNotice($"Mod 库里没有提供 {row.MissingUniqueId} 的 Mod", true);
            ShowMessage($"Mod 库里没有提供 {row.MissingUniqueId} 的 Mod。\n\n库目录：{library}",
                "从 Mod 库补齐", MessageBoxImage.Information);
            return;
        }

        var provider = providers[0];

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"将为「{row.ModName}」补齐前置「{provider.DisplayName}」。\n" +
            $"版本：{(string.IsNullOrWhiteSpace(provider.Version) ? "未知" : provider.Version)}\n" +
            $"安装到：{modsDirectory}\n\n同名文件夹会先备份为 .bak-<时间戳>。确定安装吗？",
            "从 Mod 库补齐", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        ImportResult install;
        try
        {
            var installProgress = new Progress<double>(value =>
                LabNotice.Text = $"正在安装… {(int)Math.Round(Math.Clamp(value, 0d, 1d) * 100)}%");

            var target = modsDirectory;
            install = await Task.Run(() => ModLibrary.Install([provider], target, backupExisting: true, installProgress));
        }
        catch (Exception ex)
        {
            Log.Error($"从 Mod 库补齐失败：{provider.SourcePath}", ex);
            ShowNotice($"安装失败：{ex.Message}", true);
            return;
        }

        ShowNotice($"已安装「{provider.DisplayName}」：成功 {install.InstalledCount} 个，错误 {install.Errors.Count} 条。",
            install.Errors.Count > 0);

        foreach (var error in install.Errors) Log.Warn($"补齐前置时出错：{error}");

        await ScanAsync();
    }

    // ————— 一键补齐缺失依赖 —————

    /// <summary>
    /// 一键补齐：汇总当前依赖问题里所有缺失的 UniqueId，扫描 Mod 库找出能提供它们的条目，
    /// 确认后一次性安装，并列出库里仍然找不到的依赖，给出在线搜索入口。
    /// </summary>
    private async void OnFillAllDependenciesClick(object sender, RoutedEventArgs e)
    {
        var library = SettingsStore.Current.ModLibraryDirectory;
        if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(library))
        {
            var goSetup = MessageBox.Show(
                Window.GetWindow(this)!,
                "还没有设置可用的 Mod 库目录，无法一键补齐。\n\n现在就去设置页选择吗？",
                "一键补齐", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (goSetup == MessageBoxResult.OK)
                (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Setup);

            return;
        }

        var modsDirectory = _modsDirectory;
        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            ShowNotice("还没有可用的 Mods 目录，请先创建游戏实例。", true);
            return;
        }

        var missing = DependencyReport.Build(_lastScan?.Mods ?? Array.Empty<ModEntry>())
            .Where(row => row.Kind != DependencyIssueKind.Cycle && !string.IsNullOrWhiteSpace(row.MissingUniqueId))
            .Select(row => row.MissingUniqueId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            ShowNotice("当前没有可从 Mod 库补齐的缺失依赖。", false);
            return;
        }

        BtnFillAllDeps.IsEnabled = false;
        ShowNotice($"正在扫描 Mod 库，查找 {missing.Count} 个缺失依赖…", false);

        List<LibraryMod> installs;
        List<string> notInLibrary;
        try
        {
            var target = modsDirectory;
            var scanProgress = new Progress<double>(value =>
                LabNotice.Text = $"正在扫描 Mod 库… {(int)Math.Round(Math.Clamp(value, 0d, 1d) * 100)}%");

            var scan = await Task.Run(() => ModLibrary.Scan(library, target, scanProgress));

            installs = [];
            notInLibrary = [];

            foreach (var id in missing)
            {
                var providers = DependencyReport.FindProviders(scan.Mods, id);
                if (providers.Count == 0)
                {
                    notInLibrary.Add(id);
                    continue;
                }

                foreach (var provider in providers)
                {
                    if (installs.Any(item =>
                            string.Equals(item.SourcePath, provider.SourcePath, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    installs.Add(provider);
                }
            }
        }
        catch (Exception ex)
        {
            BtnFillAllDeps.IsEnabled = true;
            Log.Error($"一键补齐：扫描 Mod 库失败：{library}", ex);
            ShowNotice($"扫描 Mod 库失败：{ex.Message}", true);
            return;
        }

        if (installs.Count == 0)
        {
            BtnFillAllDeps.IsEnabled = true;
            ShowNotice($"Mod 库里没有找到能补齐这些依赖的 Mod（共缺失 {missing.Count} 个）。", true);
            OfferOnlineSearch(notInLibrary);
            return;
        }

        var confirm = MessageBox.Show(
            Window.GetWindow(this)!,
            BuildFillConfirm(installs, notInLibrary, modsDirectory),
            "一键补齐", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            BtnFillAllDeps.IsEnabled = true;
            ShowNotice("已取消一键补齐。", false);
            return;
        }

        ImportResult install;
        try
        {
            var target = modsDirectory;
            var installProgress = new Progress<double>(value =>
                LabNotice.Text = $"正在安装… {(int)Math.Round(Math.Clamp(value, 0d, 1d) * 100)}%");

            install = await Task.Run(() =>
                ModLibrary.Install(installs, target, backupExisting: true, installProgress));
        }
        catch (Exception ex)
        {
            BtnFillAllDeps.IsEnabled = true;
            Log.Error("一键补齐：安装失败", ex);
            ShowNotice($"安装失败：{ex.Message}", true);
            return;
        }
        finally
        {
            BtnFillAllDeps.IsEnabled = true;
        }

        foreach (var error in install.Errors) Log.Warn($"一键补齐安装出错：{error}");

        await ScanAsync();

        // 安装后重算，确认哪些依赖仍然缺失
        var stillMissing = DependencyReport.Build(_lastScan?.Mods ?? Array.Empty<ModEntry>())
            .Where(row => row.Kind != DependencyIssueKind.Cycle && !string.IsNullOrWhiteSpace(row.MissingUniqueId))
            .Select(row => row.MissingUniqueId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = $"一键补齐完成：安装 {install.InstalledCount} 个，错误 {install.Errors.Count} 条。";
        if (stillMissing.Count > 0)
            summary += $"\n仍有 {stillMissing.Count} 个依赖缺失：{string.Join("、", stillMissing)}";
        else
            summary += " 依赖已全部满足。";

        ShowNotice(summary, install.Errors.Count > 0);
        OfferOnlineSearch(stillMissing);
    }

    private static string BuildFillConfirm(IReadOnlyList<LibraryMod> installs, IReadOnlyList<string> notInLibrary,
        string modsDirectory)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"将安装以下 {installs.Count} 个 Mod 以补齐依赖：");

        foreach (var mod in installs)
        {
            var version = string.IsNullOrWhiteSpace(mod.Version) ? "" : $" v{mod.Version}";
            builder.AppendLine($"· {mod.DisplayName}{version}");
        }

        if (notInLibrary.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Mod 库里找不到（{notInLibrary.Count} 个，安装后仍会缺失）：");
            foreach (var id in notInLibrary) builder.AppendLine($"· {id}");
        }

        builder.AppendLine();
        builder.AppendLine($"安装到：{modsDirectory}");
        builder.AppendLine("同名文件夹会先备份为 .bak-<时间戳>。");
        builder.Append("确定开始安装吗？");

        return builder.ToString();
    }

    /// <summary>还有依赖缺失时，给出在启动器内打开 Nexus 搜索的入口。</summary>
    private void OfferOnlineSearch(IReadOnlyList<string> missing)
    {
        if (missing.Count == 0) return;

        var preview = string.Join("\n", missing.Take(8).Select(id => "· " + id));
        if (missing.Count > 8) preview += $"\n… 其余 {missing.Count - 8} 个";

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"以下 {missing.Count} 个前置在 Mod 库里没有找到：\n\n{preview}\n\n" +
            "是否在启动器内打开 Nexus 搜索第一个缺失的前置？",
            "在线搜索缺失前置", MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (answer != MessageBoxResult.OK) return;

        var dialog = new NexusBrowserWindow(NexusApi.BuildSearchUrl(missing[0]));
        var owner = Window.GetWindow(this);
        if (owner is not null) dialog.Owner = owner;

        dialog.Show();
    }

    private void OnOpenModsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_modsDirectory))
        {
            ShowNotice("还没有可用的 Mods 目录，请先创建游戏实例。", true);
            return;
        }

        ShellHelper.OpenFolder(_modsDirectory);
    }

    /// <summary>
    /// 跳转到资源中心页：下载 Mod、安装 SMAPI、内嵌浏览 Mod 站点都在那里。
    /// 下载与安装可能改动 Mods 目录，回到本页时 OnEnter 会重新扫描。
    /// </summary>
    private void OnResourceCenterClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Resource);

    private async void OnCreateModsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_modsDirectory)) return;

        try
        {
            Directory.CreateDirectory(_modsDirectory);
            Log.Info($"已创建 Mods 目录：{_modsDirectory}");
        }
        catch (Exception ex)
        {
            ShowNotice($"创建 Mods 目录失败：{ex.Message}", true);
            Log.Error($"创建 Mods 目录失败：{_modsDirectory}", ex);
            return;
        }

        await ScanAsync();
    }

    // ————— 单个 Mod 操作 —————

    /// <summary>按钮生成后按当前状态决定文案与配色。</summary>
    private void OnToggleModLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not OutlineButton button) return;
        if (button.Tag is not ModItem item) return;

        var enabling = !item.IsEnabled;
        button.Content = enabling ? "启用" : "禁用";
        button.Tone = enabling ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private void OnToggleModClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModItem item }) return;

        var enable = !item.IsEnabled;
        if (!ModEnabler.TrySetEnabled(item.Entry, enable, out var error))
        {
            ShowMessage(error ?? "操作失败", "Mod 启停", MessageBoxImage.Warning);
            return;
        }

        Log.Info($"Mod 已{(enable ? "启用" : "禁用")}：{item.DisplayName}");
        _ = ScanAsync();
    }

    private void OnDeleteModClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModItem item }) return;

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"确定删除 Mod「{item.DisplayName}」吗？\n会删除整个 Mod 文件夹：\n{item.Entry.FolderPath}\n删除后可从回收站恢复。",
            "删除 Mod", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        if (!ModEnabler.TryDelete(item.Entry, true, out var error))
        {
            ShowMessage(error ?? "删除失败", "删除 Mod", MessageBoxImage.Warning);
            return;
        }

        _ = ScanAsync();
    }

    // ————— 批量启用 —————

    private async void OnEnableAllClick(object sender, RoutedEventArgs e)
    {
        var targets = _allItems.Where(item => item.State == ModState.Disabled).ToList();

        if (targets.Count == 0)
        {
            ShowNotice("没有需要启用的 Mod。", false);
            return;
        }

        var ok = 0;
        var failed = 0;
        var firstError = string.Empty;

        await Task.Run(() =>
        {
            foreach (var item in targets)
            {
                if (ModEnabler.TrySetEnabled(item.Entry, true, out var error))
                {
                    ok++;
                    continue;
                }

                failed++;
                if (firstError.Length == 0) firstError = $"{item.DisplayName}：{error}";
            }
        });

        var text = $"全部启用完成：成功 {ok} 个，失败 {failed} 个。";
        if (failed > 0) text += $"\n首个失败原因：{firstError}";
        ShowNotice(text, failed > 0);
        Log.Info($"全部启用完成：成功 {ok} 个，失败 {failed} 个");

        await ScanAsync();
    }

    // ————— 从 Mod 库安装 —————

    private void OnInstallFromLibraryClick(object sender, RoutedEventArgs e)
    {
        var library = SettingsStore.Current.ModLibraryDirectory;

        if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(library))
        {
            var answer = MessageBox.Show(
                Window.GetWindow(this)!,
                "还没有设置 Mod 库目录，请到设置页选择。\n\n现在就去设置页吗？",
                "Mod 库", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (answer == MessageBoxResult.OK)
                (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Setup);

            return;
        }

        if (string.IsNullOrWhiteSpace(_modsDirectory))
        {
            ShowNotice("还没有可用的 Mods 目录，请先创建游戏实例。", true);
            return;
        }

        var dialog = new ModLibraryWindow(library, _modsDirectory) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) _ = ScanAsync();
    }

    // ————— 导入 —————

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var target = _modsDirectory;
        if (string.IsNullOrWhiteSpace(target))
        {
            ShowNotice("还没有可用的 Mods 目录，请先创建游戏实例。", true);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的 Mod 压缩包",
            Filter = "Mod 压缩包 (*.zip;*.rar;*.7z;*.tar;*.gz)|*.zip;*.rar;*.7z;*.tar;*.gz|所有文件 (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        var source = dialog.FileName;
        var progress = new Progress<double>(value =>
            LabNotice.Text = $"正在导入… {(int)Math.Round(value * 100)}%");

        ShowNotice("正在导入… 0%", false);

        ImportResult result;
        try
        {
            result = await Task.Run(() => ModImporter.Import(source, target, backupExisting: true, progress));
        }
        catch (Exception ex)
        {
            Log.Error($"导入 Mod 失败：{source}", ex);
            ShowNotice($"导入失败：{ex.Message}", true);
            ShowMessage($"导入失败：{ex.Message}", "导入 Mod", MessageBoxImage.Error);
            return;
        }

        ShowNotice($"导入完成：成功 {result.InstalledCount} 个，警告 {result.Warnings.Count} 条，错误 {result.Errors.Count} 条。",
            result.Errors.Count > 0);

        MessageBox.Show(
            Window.GetWindow(this)!,
            BuildImportMessage(source, result),
            "导入完成",
            MessageBoxButton.OK,
            result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        await ScanAsync();
    }

    private static string BuildImportMessage(string source, ImportResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"来源：{source}");
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

    private void ShowMessage(string message, string caption, MessageBoxImage icon)
        => MessageBox.Show(Window.GetWindow(this)!, message, caption, MessageBoxButton.OK, icon);
}
