using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Pages;

public partial class PageInstance : LauncherPage
{
    private bool _subscribed;
    private bool _gameRunning;

    public PageInstance()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        ApplyFilter();
    }

    public override void OnEnter() => ApplyFilter();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;
        _subscribed = true;
        InstanceStore.Changed += ApplyFilter;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        InstanceStore.Changed -= ApplyFilter;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (PanInstances is null) return;

        _gameRunning = IsGameRunning();

        var keyword = TxtSearch.Text?.Trim() ?? string.Empty;

        // 每次都下发新的列表实例，确保卡片容器重建、当前实例按钮状态跟着刷新
        var result = string.IsNullOrEmpty(keyword)
            ? InstanceStore.All.ToList()
            : InstanceStore.All.Where(i =>
                    i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    i.Note.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    i.GameDir.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();

        PanInstances.ItemsSource = result;

        var empty = result.Count == 0;
        PanInstances.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        CardEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (!empty) return;

        var noInstance = InstanceStore.All.Count == 0;
        CardEmpty.Title = noInstance ? "还没有实例" : "没有匹配的实例";
        LabEmpty.Text = noInstance
            ? "还没有实例。点右上角「新建实例」，选择「原版」或「Mod 端」并手动指定游戏目录。"
            : "换个关键词再试试，或清空搜索框查看全部实例。";
    }

    // ————— 卡片初始化 —————

    private void OnInstanceCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SurfaceCard card || card.DataContext is not Instance instance) return;

        RefreshCard(card, instance);
    }

    /// <summary>把实例的状态刷到卡片上。整张卡片在每次刷新时重建，因此可以整体赋值。</summary>
    private void RefreshCard(SurfaceCard card, Instance instance)
    {
        var switchKind = FindByName<OutlineButton>(card, "BtnSwitchKind");
        if (switchKind is not null)
            switchKind.Content = instance.IsVanilla ? "切换为 Mod 端" : "切换为原版";

        var install = instance.Install;
        var smapiStatus = FindByName<TextBlock>(card, "LabSmapiStatus");
        if (smapiStatus is not null)
        {
            smapiStatus.Text = install is null
                ? "游戏目录不可用"
                : install.HasSmapi
                    ? $"SMAPI {install.SmapiVersion ?? "版本未知"}"
                    : "未安装 SMAPI";
        }

        var runWarning = FindByName<TextBlock>(card, "LabRunWarning");
        if (runWarning is not null)
        {
            runWarning.Visibility = _gameRunning && instance.Install is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    /// <summary>在「原版」与「Mod 端」之间切换实例类型。只改启动方式，不动任何文件。</summary>
    private void OnSwitchKindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Instance instance }) return;

        var toVanilla = !instance.IsVanilla;

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            toVanilla
                ? $"把实例「{instance.Name}」切换为原版？\n\n之后启动会直接运行游戏主程序，不再加载任何 Mod。"
                : $"把实例「{instance.Name}」切换为 Mod 端？\n\n之后启动会通过 SMAPI 加载 Mod。",
            "切换实例类型", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        instance.Kind = toVanilla ? InstanceKind.Vanilla : InstanceKind.Modded;
        InstanceStore.Save(instance);

        Log.Info($"实例「{instance.Name}」类型切换为 {instance.KindText}");
        ApplyFilter();
    }

    // ————— 目录 —————

    private void OnOpenGameDirClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Instance instance }) return;
        ShellHelper.OpenFolder(instance.GameDir);
    }

    private void OnOpenModsDirClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Instance instance }) return;
        ShellHelper.OpenFolder(instance.ModsDirectory);
    }

    private void OnOpenSmapiPageClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenUrl("https://smapi.io/");

    // ————— 进程检测 —————

    private static bool IsGameRunning() => GameProcess.IsRunning();

    // ————— 视觉树查找 —————

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T target && predicate(target)) return target;

            var found = FindDescendant(child, predicate);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>DataTemplate 里的命名元素不会生成字段，只能按名字在卡片内查找。</summary>
    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
        => FindDescendant<T>(root, element => element.Name == name);

    // ————— 通用操作 —————

    /// <summary>卡片生成后按「是否为当前实例」决定按钮的文案与配色。</summary>
    private void OnUseButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not OutlineButton button) return;

        var isCurrent = ReferenceEquals(button.DataContext, InstanceStore.Current);
        button.Content = isCurrent ? "当前使用中" : "使用此实例";
        button.Tone = isCurrent ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private void OnUseInstanceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Instance instance }) return;

        InstanceStore.SetCurrent(instance);
        ApplyFilter();
    }

    private void OnDeleteInstanceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Instance instance }) return;

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"确定删除实例「{instance.Name}」吗？\n只会删除这条实例记录，不会删除游戏目录或 Mod 文件。",
            "删除实例", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        InstanceStore.Delete(instance);
        ApplyFilter();
    }

    private void OnNewInstanceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NewInstanceWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        InstanceStore.Create(dialog.InstanceName, dialog.GameDirectory, dialog.InstanceNote, dialog.Kind);
        ApplyFilter();
    }
}
