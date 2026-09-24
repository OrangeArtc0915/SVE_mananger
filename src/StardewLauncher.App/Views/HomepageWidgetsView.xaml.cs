using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using StardewLauncher.App.Animation;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Pages;
using StardewLauncher.App.Theme;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Appearance;
using StardewLauncher.Core.Homepage;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Plugins;
using StardewLauncher.Core.Saves;
using StardewLauncher.Core.Smapi;
using StardewLauncher.Core.Weather;

namespace StardewLauncher.App.Views;

/// <summary>
/// 主页小组件：月历（现实日期）、现实天气（Open-Meteo）、每日一言。
/// 天气查询全部走异步，不在 UI 线程等待网络。
/// </summary>
public partial class HomepageWidgetsView : UserControl
{
    private static readonly string[] WeekdayNames =
        ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];

    /// <summary>月历表头，周一为一周起点。</summary>
    private static readonly string[] WeekdayShort = ["一", "二", "三", "四", "五", "六", "日"];

    /// <summary>拖拽时用的私有格式：与窗口级文件拖放（FileDrop）分开，拖卡片不会被当成导入 Mod。</summary>
    private const string WidgetDragFormat = "StardewLauncher.Widget";

    /// <summary>按住后移动超过这个距离才算拖拽，避免把点击、误触当成拖拽。</summary>
    private const double DragThreshold = 6;

    /// <summary>widget id 到卡片的映射，卡片顺序与显隐都按它来找元素。内置卡片与扩展卡片共用这一张表。</summary>
    private readonly Dictionary<string, SurfaceCard> _cards;

    /// <summary>扩展卡片，单独留一份便于增删与刷新。</summary>
    private readonly Dictionary<string, PluginWidgetCard> _pluginCards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>扩展小组件在「自定义」菜单里的勾选项。</summary>
    private readonly List<MenuItem> _pluginMenuItems = [];

    private SurfaceCard? _dragCard;
    private Point _dragOrigin;
    private bool _dragging;

    private bool _weatherRunning;

    public HomepageWidgetsView()
    {
        InitializeComponent();

        _cards = new Dictionary<string, SurfaceCard>
        {
            ["calendar"] = CardCalendar,
            ["weather"] = CardWeather,
            ["quote"] = CardQuote,
            ["saves"] = CardSaves,
            ["image"] = CardImage,
            ["mods"] = CardMods
        };

        SyncPluginCards();
        SyncPluginMenuItems();

        ApplyWidgetLayout();
        BuildWeekdayHeader();
        ApplyArtOpacity();
        ThemeService.ThemeChanged += ApplyArtOpacity;

        RefreshCalendar();
        RefreshQuote();
        RefreshImage();
        _ = RefreshWeatherAsync();
        _ = RefreshSaveAsync();
        _ = RefreshModsAsync();
        RefreshPlugins();
    }

    /// <summary>供页面进入时调用，重新按当前设置刷新月历与天气（天气命中 30 分钟缓存则不会重复请求）。</summary>
    public void Refresh()
    {
        // 回主页时扩展目录可能刚被改过（启用、停用、删掉 dll），先同步一遍
        SyncPluginCards();
        SyncPluginMenuItems();

        // 回到主页时按设置重排一次，但用户收起/排序的结果不会被重置
        ApplyWidgetLayout();
        RefreshCalendar();
        RefreshImage();
        _ = RefreshWeatherAsync();
        _ = RefreshSaveAsync();
        _ = RefreshModsAsync();
        RefreshPlugins();
    }

    // ————— 扩展小组件 —————

    /// <summary>按当前启用的扩展增删卡片。启用状态来自扩展管理窗口。</summary>
    private void SyncPluginCards()
    {
        var wanted = WidgetPluginCatalog.Enabled
            .Select(entry => entry.WidgetId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in _pluginCards.Keys.ToList())
        {
            if (wanted.Contains(id)) continue;

            var card = _pluginCards[id];

            WidgetsPanel.Children.Remove(card);
            _cards.Remove(id);
            _pluginCards.Remove(id);
        }

        foreach (var entry in WidgetPluginCatalog.Enabled)
        {
            if (_pluginCards.ContainsKey(entry.WidgetId)) continue;

            var card = new PluginWidgetCard(entry);

            _pluginCards[entry.WidgetId] = card;
            _cards[entry.WidgetId] = card;
            WidgetsPanel.Children.Add(card);
        }
    }

    /// <summary>把扩展小组件的勾选项插在内置项之后、第一条分隔线之前。</summary>
    private void SyncPluginMenuItems()
    {
        foreach (var item in _pluginMenuItems)
        {
            item.Click -= OnToggleWidgetClick;
            MenuWidgets.Items.Remove(item);
        }

        _pluginMenuItems.Clear();

        var insertAt = IndexOfFirstSeparator();

        foreach (var entry in WidgetPluginCatalog.Enabled)
        {
            var item = new MenuItem
            {
                Header = entry.DisplayName,
                Tag = entry.WidgetId,
                IsCheckable = true,
                StaysOpenOnClick = true
            };

            item.Click += OnToggleWidgetClick;

            MenuWidgets.Items.Insert(insertAt++, item);
            _pluginMenuItems.Add(item);
        }
    }

    private int IndexOfFirstSeparator()
    {
        for (var i = 0; i < MenuWidgets.Items.Count; i++)
            if (MenuWidgets.Items[i] is Separator) return i;

        return MenuWidgets.Items.Count;
    }

    private void RefreshPlugins()
    {
        foreach (var card in _pluginCards.Values) _ = card.RefreshAsync();
    }

    /// <summary>打开扩展管理窗口；关掉后按新的启用状态重排一次。</summary>
    private void OnManagePluginsClick(object sender, RoutedEventArgs e)
    {
        // 不能在菜单项的回调里直接开模态窗口：菜单还按着鼠标捕获，窗口会开不出来。
        // 排到菜单收起之后再做。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OpenPluginManager));
    }

    private void OpenPluginManager()
    {
        var window = new WidgetPluginWindow();

        if (Window.GetWindow(this) is { } owner) window.Owner = owner;

        window.ShowDialog();

        SyncPluginCards();
        SyncPluginMenuItems();
        ApplyWidgetLayout();
        RefreshPlugins();
    }

    // ————— 顺序与显隐 —————

    /// <summary>按设置调整卡片的先后与显隐。瀑布流的落位只认 Children 顺序，所以顺序就是子元素顺序。</summary>
    private void ApplyWidgetLayout()
    {
        var settings = SettingsStore.Current;
        var order = NormalizeOrder(settings.HomepageWidgetOrder ?? []);

        for (var i = 0; i < order.Count; i++)
        {
            var card = _cards[order[i]];
            if (WidgetsPanel.Children.IndexOf(card) == i) continue;

            WidgetsPanel.Children.Remove(card);
            WidgetsPanel.Children.Insert(i, card);
        }

        var hidden = new HashSet<string>(settings.HomepageHiddenWidgets ?? []);
        foreach (var pair in _cards)
            pair.Value.Visibility = hidden.Contains(pair.Key) ? Visibility.Collapsed : Visibility.Visible;

        PanWidgetsEmpty.Visibility = _cards.Values.Any(card => card.Visibility == Visibility.Visible)
            ? Visibility.Collapsed
            : Visibility.Visible;

        WidgetsPanel.InvalidateMeasure();
    }

    /// <summary>
    /// 整理出完整顺序：配置里不认识的 id 丢掉，没提到过的按"内置在前、扩展在后"补到后面
    /// （扩展没有内置顺序可言，只能排在末尾）。
    /// </summary>
    private static List<string> NormalizeOrder(IEnumerable<string> saved)
    {
        var known = HomepageLayout.AllWidgetIds;
        var order = new List<string>(known.Count);

        foreach (var id in saved)
            if (known.Contains(id, StringComparer.OrdinalIgnoreCase) &&
                !order.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(id);
            }

        foreach (var id in known)
            if (!order.Contains(id, StringComparer.OrdinalIgnoreCase)) order.Add(id);

        return order;
    }

    /// <summary>把当前子元素顺序写回设置。落盘失败只记日志，不回滚界面。</summary>
    private void PersistOrder()
    {
        var order = WidgetsPanel.Children.OfType<SurfaceCard>().Select(IdOf).OfType<string>().ToList();
        if (order.Count == 0) return;

        try
        {
            SettingsStore.Current.HomepageWidgetOrder = order;
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"保存小组件顺序失败：{ex.Message}");
        }
    }

    private string? IdOf(DependencyObject card)
    {
        foreach (var pair in _cards)
            if (ReferenceEquals(pair.Value, card)) return pair.Key;

        return null;
    }

    /// <summary>把隐藏集合写回设置并立即重排。</summary>
    private void PersistHidden(IEnumerable<string> hidden)
    {
        var ids = hidden.ToHashSet();

        try
        {
            SettingsStore.Current.HomepageHiddenWidgets =
                HomepageLayout.AllWidgetIds.Where(ids.Contains).ToList();
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"保存小组件显隐失败：{ex.Message}");
        }

        ApplyWidgetLayout();
    }

    // ————— 自定义菜单 —————

    /// <summary>点「自定义」时弹菜单，贴在按钮下方。</summary>
    private void OnCustomizeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } host) return;

        menu.PlacementTarget = host;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>每次弹出都按当前设置回填勾选态，避免两次打开之间状态不一致。</summary>
    private void OnWidgetMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        var hidden = new HashSet<string>(SettingsStore.Current.HomepageHiddenWidgets ?? []);

        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Tag is string id && _cards.ContainsKey(id)) item.IsChecked = !hidden.Contains(id);

        RunMenuIntro(menu);
    }

    /// <summary>菜单淡入 + 轻微下落，和界面其它动效保持一致（模板里的面板在这里才拿得到）。</summary>
    private static void RunMenuIntro(ContextMenu menu)
    {
        if (!AnimationEngine.IsEnabled) return;
        if (menu.Template?.FindName("MenuPanel", menu) is not Border panel) return;

        panel.RenderTransformOrigin = new Point(0.5, 0);

        var drop = panel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        panel.RenderTransform = drop;

        panel.Opacity = 0;

        AnimationEngine.Start($"menu:intro:{menu.GetHashCode()}", 0, 1, 200, Ease.OutFluent, v =>
        {
            panel.Opacity = v;
            drop.Y = -7 * (1 - v);
        });
    }

    private void OnToggleWidgetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string id } || !_cards.ContainsKey(id)) return;

        // 按设置里的现状取反，不依赖菜单勾选态与 Click 的先后顺序
        var hidden = new HashSet<string>(SettingsStore.Current.HomepageHiddenWidgets ?? []);
        if (!hidden.Remove(id)) hidden.Add(id);

        PersistHidden(hidden);
    }

    private void OnResetWidgetOrderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SettingsStore.Current.HomepageWidgetOrder = [];
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"恢复小组件默认顺序失败：{ex.Message}");
        }

        ApplyWidgetLayout();
    }

    private void OnRestoreWidgetsClick(object sender, RoutedEventArgs e) => PersistHidden([]);

    // ————— 存档概览 —————

    /// <summary>取最近保存的那份存档展示；读盘放在后台线程。</summary>
    private async Task RefreshSaveAsync()
    {
        try
        {
            var saves = await Task.Run(SaveScanner.Scan);

            if (saves.Count == 0)
            {
                LabSaveTitle.Text = "还没扫到存档";
                LabSaveLine.Text = SaveScanner.Exists
                    ? "存档目录里没有可识别的存档。"
                    : "没找到游戏存档目录。";
                LabSaveSkills.Text = string.Empty;
                return;
            }

            var save = saves[0];

            LabSaveTitle.Text = $"{save.DisplayName} · {save.FarmText}";
            LabSaveLine.Text = $"{save.DateText} · {save.MoneyText} · 游玩 {save.PlayTimeText}";
            LabSaveSkills.Text = saves.Count > 1
                ? $"{save.SkillsText}　·　共 {saves.Count} 个存档"
                : save.SkillsText;
        }
        catch (Exception ex)
        {
            Log.Warn($"读存档概览失败：{ex.Message}");
            LabSaveTitle.Text = "读不到存档";
            LabSaveLine.Text = ex.Message;
        }
    }

    // ————— 自定义图片 —————

    /// <summary>有图就显示图，没图就显示「选择图片」；图丢了额外给一条提示。</summary>
    private void RefreshImage()
    {
        var file = SettingsStore.Current.HomepageImageFile ?? string.Empty;
        var exists = !string.IsNullOrWhiteSpace(file) && File.Exists(file);

        PanImageEmpty.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        PanImageView.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        LabImageMissing.Visibility = !exists && !string.IsNullOrWhiteSpace(file)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!exists)
        {
            ImgCustom.Source = null;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // 一次读进内存，别让 WPF 一直占着文件——换图时要能把旧文件删掉
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();

            ImgCustom.Source = image;
        }
        catch (Exception ex)
        {
            Log.Warn($"加载主页挂件图片失败：{ex.Message}");
            PanImageEmpty.Visibility = Visibility.Visible;
            PanImageView.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPickImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择主页挂件的图片",
            Filter = "图片 (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        var imported = HomepageImageStore.Import(dialog.FileName, out var error);

        if (imported is null)
        {
            Dialogs.Warn(Window.GetWindow(this)!, $"这张图用不了：{error}", "自定义图片");
            return;
        }

        SettingsStore.Current.HomepageImageFile = imported;
        SettingsStore.Save();

        RefreshImage();
    }

    private void OnClearImageClick(object sender, RoutedEventArgs e)
    {
        HomepageImageStore.Clear();
        SettingsStore.Current.HomepageImageFile = string.Empty;
        SettingsStore.Save();

        RefreshImage();
    }

    // ————— Mod 概览 —————

    private async Task RefreshModsAsync()
    {
        try
        {
            var instance = InstanceStore.Current;

            if (instance is null)
            {
                LabModTitle.Text = "还没选实例";
                LabModCounts.Text = "到「游戏实例」建一个，这里就会显示 Mod 数量。";
                LabModSmapi.Text = string.Empty;
                return;
            }

            LabModTitle.Text = instance.Name;

            var scan = await Task.Run(() => ModScanner.Scan(instance.ModsDirectory));

            LabModCounts.Text = scan.Mods.Count == 0
                ? "Mods 目录里还没有 Mod"
                : $"共 {scan.Mods.Count} 个 · 启用 {scan.EnabledCount} · 禁用 {scan.DisabledCount}"
                  + (scan.InvalidCount > 0 ? $" · 异常 {scan.InvalidCount}" : string.Empty);

            var installed = instance.Install is { HasSmapi: true } install ? install.SmapiVersion : null;

            if (installed is null)
            {
                LabModSmapi.Text = instance.IsVanilla ? "这是原版实例，不加载 Mod" : "当前实例没装 SMAPI";
                return;
            }

            LabModSmapi.Text = $"SMAPI {installed}";

            // 最新版本走 24 小时磁盘缓存；取不到就只显示已装版本
            var latest = await SmapiUpdateChecker.GetLatestAsync();

            if (latest is not null && SemVer.IsNewer(latest.Version, installed))
                LabModSmapi.Text = $"SMAPI {installed}（有新版 {latest.Version}）";
        }
        catch (Exception ex)
        {
            Log.Warn($"读 Mod 概览失败：{ex.Message}");
            LabModTitle.Text = "读不到 Mod 信息";
            LabModCounts.Text = ex.Message;
        }
    }

    // ————— 布局导出 / 导入 —————

    private void OnExportLayoutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出主页布局",
            Filter = "布局文件 (*.json)|*.json",
            FileName = "homepage-layout.json",
            AddExtension = true,
            DefaultExt = ".json"
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        ShowLayoutNotice(HomepageLayout.Export(dialog.FileName));
    }

    private void OnImportLayoutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的布局文件",
            Filter = "布局文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        var result = HomepageLayout.Import(dialog.FileName);
        ShowLayoutNotice(result);

        if (!result.Ok) return;

        ApplyWidgetLayout();
        RefreshImage();
    }

    /// <summary>导出 / 导入的结果用消息框说清楚，别只写日志。</summary>
    private void ShowLayoutNotice(HomepageLayoutResult result)
    {
        if (Window.GetWindow(this) is not { } owner) return;

        if (result.Ok)
            Dialogs.Info(owner, result.Message, "主页布局");
        else
            Dialogs.Warn(owner, result.Message, "主页布局");
    }

    // ————— 拖拽排序 —————

    private void OnWidgetsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCard = null;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        _dragCard = FindCard(e.OriginalSource as DependencyObject);
        _dragOrigin = e.GetPosition(WidgetsPanel);
    }

    private void OnWidgetsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _dragCard is null || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(WidgetsPanel) - _dragOrigin;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        BeginWidgetDrag(_dragCard);
    }

    private void BeginWidgetDrag(SurfaceCard card)
    {
        var id = IdOf(card);
        if (id is null) return;

        _dragging = true;
        card.Opacity = 0.5;

        try
        {
            // 模态循环里不能再动 Children，落点处理一律放在 Drop 里
            DragDrop.DoDragDrop(card, new DataObject(WidgetDragFormat, id), DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Log.Warn($"拖动小组件失败：{ex.Message}");
        }
        finally
        {
            card.Opacity = 1;
            _dragging = false;
            _dragCard = null;
        }
    }

    private void OnWidgetsDragOver(object sender, DragEventArgs e)
    {
        // 不是小组件自己的格式就不插手，留给窗口级的文件拖放
        if (!e.Data.GetDataPresent(WidgetDragFormat)) return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnWidgetsDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(WidgetDragFormat) is not string id) return;
        if (!_cards.TryGetValue(id, out var source)) return;

        e.Handled = true;

        var point = e.GetPosition(WidgetsPanel);
        var target = CardNear(point);
        if (target is null || ReferenceEquals(target, source)) return;

        var sourceIndex = WidgetsPanel.Children.IndexOf(source);
        var targetIndex = WidgetsPanel.Children.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0) return;

        // 落在目标卡片上半部分算前插，下半部分算后插；单列时也成立
        var origin = target.TranslatePoint(new Point(0, 0), WidgetsPanel);
        var after = point.Y > origin.Y + target.ActualHeight / 2;

        WidgetsPanel.Children.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex) targetIndex--;
        WidgetsPanel.Children.Insert(after ? targetIndex + 1 : targetIndex, source);

        // 瀑布流只认 Children 顺序：先失效测量再立刻排一次，落位不会滞后到下一次布局
        WidgetsPanel.InvalidateMeasure();
        WidgetsPanel.UpdateLayout();

        PersistOrder();
    }

    /// <summary>取光标下方那张卡片；落在空白处则取最近的，保证拖到卡片之间也能落位。</summary>
    private SurfaceCard? CardNear(Point point)
    {
        SurfaceCard? nearest = null;
        var nearestDistance = double.MaxValue;

        foreach (var card in WidgetsPanel.Children.OfType<SurfaceCard>())
        {
            if (card.Visibility != Visibility.Visible) continue;

            var origin = card.TranslatePoint(new Point(0, 0), WidgetsPanel);
            var left = origin.X;
            var top = origin.Y;
            var right = left + card.ActualWidth;
            var bottom = top + card.ActualHeight;

            if (point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom) return card;

            var dx = Math.Max(Math.Max(left - point.X, 0), point.X - right);
            var dy = Math.Max(Math.Max(top - point.Y, 0), point.Y - bottom);
            var distance = dx * dx + dy * dy;

            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = card;
        }

        return nearest;
    }

    /// <summary>从原始源往上找瀑布流的直接子卡片，没走到卡片就返回空。</summary>
    private SurfaceCard? FindCard(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is SurfaceCard card && ReferenceEquals(VisualTreeHelper.GetParent(card), WidgetsPanel)) return card;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null;
        }

        return null;
    }

    /// <summary>从卡片上起拖时不要把按钮点击吞掉（与标题栏拖窗口同一套判定）。</summary>
    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null;
        }

        return false;
    }

    // ————— 月历 —————

    private void BuildWeekdayHeader()
    {
        WeekdayHeader.Children.Clear();

        foreach (var name in WeekdayShort)
        {
            var text = new TextBlock
            {
                Text = name,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            WeekdayHeader.Children.Add(text);
        }
    }

    private void RefreshCalendar()
    {
        var today = DateTime.Today;

        LabMonthTitle.Text = $"{today.Year} 年 {today.Month} 月 · {WeekdayNames[(int)today.DayOfWeek]}";

        BuildMonthGrid(today);
    }

    /// <summary>按当月第一天是周几补白，每行 7 格；前后补白显示邻月日期并置灰。</summary>
    private void BuildMonthGrid(DateTime today)
    {
        MonthGrid.Children.Clear();

        var first = new DateTime(today.Year, today.Month, 1);
        var leading = ((int)first.DayOfWeek + 6) % 7;
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var totalCells = (int)Math.Ceiling((leading + daysInMonth) / 7d) * 7;

        for (var i = 0; i < totalCells; i++)
            MonthGrid.Children.Add(CreateDayCell(first.AddDays(i - leading), today));

#if DEBUG
        LogMonthGrid(today, leading, totalCells);
#endif
    }

    private FrameworkElement CreateDayCell(DateTime date, DateTime today)
    {
        var inMonth = date.Month == today.Month;
        var isToday = date.Date == today.Date;

        var text = new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var cell = new Border
        {
            Margin = new Thickness(2),
            MinHeight = 24,
            CornerRadius = TryFindResource("Radius.Small") is CornerRadius radius ? radius : new CornerRadius(6),
            Child = text
        };

        if (isToday)
        {
            text.FontWeight = FontWeights.Bold;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.OnAccent");
            cell.SetResourceReference(Border.BackgroundProperty, "Accent.Bright");
            return cell;
        }

        text.SetResourceReference(TextBlock.ForegroundProperty, inMonth ? "Text.Secondary" : "Text.Disabled");
        return cell;
    }

#if DEBUG
    /// <summary>自检：记录月历格数与今天那一格的背景，便于无人值守确认渲染是否正常。</summary>
    private void LogMonthGrid(DateTime today, int leading, int totalCells)
    {
        try
        {
            var index = leading + today.Day - 1;
            var todayCell = MonthGrid.Children[index] as Border;
            var actual = (todayCell?.Background as SolidColorBrush)?.Color;
            var expected = (TryFindResource("Accent.Bright") as SolidColorBrush)?.Color;

            Log.Info($"月历自检：{today:yyyy-MM} 共 {MonthGrid.Children.Count} 格（前导补白 {leading}，" +
                     $"当月 {DateTime.DaysInMonth(today.Year, today.Month)} 天），今天在第 {index + 1} 格，" +
                     $"背景={Describe(actual)}，Accent.Bright={Describe(expected)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"月历自检失败：{ex.Message}");
        }

        static string Describe(Color? color) => color is { } value ? $"#{value.R:X2}{value.G:X2}{value.B:X2}" : "无";
    }
#endif

    // ————— 每日一言 —————

    private void RefreshQuote() => LabQuote.Text = DailyQuoteService.Pick();

    private void OnNextQuoteClick(object sender, RoutedEventArgs e) => RefreshQuote();

    // ————— 天气 —————

    private void OnGoSetupClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Setup);

    private void OnRefreshWeatherClick(object sender, RoutedEventArgs e) => _ = RefreshWeatherAsync(force: true);

    /// <summary>异步查询现实天气。失败只改文案，不弹窗、不抛异常。</summary>
    private async Task RefreshWeatherAsync(bool force = false)
    {
        if (_weatherRunning) return;
        _weatherRunning = true;

        if (BtnRefreshWeather is not null) BtnRefreshWeather.IsEnabled = false;

        try
        {
            var city = SettingsStore.Current.WeatherCity?.Trim() ?? string.Empty;

            if (city.Length == 0)
            {
                PanNoCity.Visibility = Visibility.Visible;
                PanWeather.Visibility = Visibility.Collapsed;
                return;
            }

            PanNoCity.Visibility = Visibility.Collapsed;
            PanWeather.Visibility = Visibility.Visible;

            LabWeatherCity.Text = city;
            LabWeatherTemp.Text = "…";
            LabWeatherDesc.Text = "正在查询天气…";
            LabWeatherDetail.Text = string.Empty;
            LabWeatherNote.Visibility = Visibility.Collapsed;

            var snapshot = await WeatherService.GetAsync(city, force);

            if (snapshot is null)
            {
                IcoWeatherBig.Icon = WeatherService.IconKey(WeatherKind.Unknown);
                LabWeatherTemp.Text = string.Empty;
                LabWeatherDesc.Text = WeatherService.LastError ?? "天气数据不可用";
                LabWeatherDetail.Text = string.Empty;
                LabWeatherNote.Visibility = Visibility.Collapsed;
                return;
            }

            IcoWeatherBig.Icon = WeatherService.IconKey(snapshot.Kind);

#if DEBUG
            Log.Info($"天气自检：城市={snapshot.City} 温度={snapshot.Temperature:0.#}°C " +
                     $"体感={snapshot.ApparentTemperature:0.#}°C 湿度={snapshot.Humidity:0.#}% " +
                     $"风速={snapshot.WindSpeed:0.#}km/h 类型={snapshot.Kind} 描述={snapshot.Description} " +
                     $"今日={snapshot.TodayHigh:0.#}/{snapshot.TodayLow:0.#}°C 代码={snapshot.WeatherCode} " +
                     $"抓取时间={snapshot.FetchedAt:yyyy-MM-dd HH:mm:ss}");
#endif

            LabWeatherCity.Text = snapshot.City;
            LabWeatherTemp.Text = $"{snapshot.Temperature:0.#}°C";
            LabWeatherDesc.Text = snapshot.Description;

            var parts = new List<string>
            {
                $"体感 {snapshot.ApparentTemperature:0.#}°C",
                $"湿度 {snapshot.Humidity:0.#}%",
                $"风速 {snapshot.WindSpeed:0.#} km/h"
            };

            if (snapshot.TodayHigh is { } high && snapshot.TodayLow is { } low)
                parts.Add($"今日 {high:0.#}/{low:0.#}°C");

            LabWeatherDetail.Text = string.Join(" · ", parts);

            // LastError 仍有值说明这次是退回过期缓存的结果
            if (WeatherService.LastError is { Length: > 0 } reason)
            {
                LabWeatherNote.Text = $"{reason}，显示的是缓存数据";
                LabWeatherNote.Visibility = Visibility.Visible;
            }
            else
            {
                LabWeatherNote.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新天气小组件失败：{ex.Message}");
            LabWeatherDesc.Text = "天气数据不可用";
        }
        finally
        {
            _weatherRunning = false;
            if (BtnRefreshWeather is not null) BtnRefreshWeather.IsEnabled = true;
        }
    }

    // ————— 星露谷素材底纹 —————

    /// <summary>信纸底纹只做点缀：深色主题下再压低透明度，避免影响文字可读性。</summary>
    private void ApplyArtOpacity()
    {
        if (ImgQuotePaper is null) return;
        // 改成 UniformToFill 后底纹比原来实，透明度相应压低
        ImgQuotePaper.Opacity = ThemeService.IsDark ? 0.1 : 0.18;
    }
}
