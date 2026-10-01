using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.App.Views;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.App.Windows;

/// <summary>
/// Mod 合集：把一整套 Mod 存成组合，之后一键切回来。
/// 套用只做启停（改目录名的点前缀），不删不改 Mod 本身，所以随时能反悔。
/// </summary>
public partial class ModCollectionWindow : LauncherWindow
{
    private bool _busy;

    /// <summary>本次打开里有没有真的套用过合集。调用方据此决定要不要重扫。</summary>
    public bool Applied { get; private set; }

    public ModCollectionWindow()
    {
        InitializeComponent();
        Log.SetModule("合集");

        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var items = ModCollectionStore.All.OrderByDescending(item => item.CreatedAt).ToList();

        PanCollections.ItemsSource = items;
        LabEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ————— 保存 —————

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (InstanceStore.Current is not { } instance)
        {
            ShowNotice("还没有实例，先到「实例」页选一个。", true);
            return;
        }

        var name = TxtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowNotice("先给合集起个名字。", true);
            return;
        }

        try
        {
            _busy = true;

            var directory = instance.ModsDirectory;
            var scan = ModScanner.Scan(directory);

            if (!scan.ModsDirectoryExists)
            {
                ShowNotice($"Mods 目录还不存在：{directory}", true);
                return;
            }

            var collection = ModCollectionStore.Capture(name, TxtNote.Text, scan.Mods);

            if (collection.ModKeys.Count == 0)
            {
                ModCollectionStore.Delete(collection);
                ShowNotice("当前一个 Mod 都没启用，存下来会是空合集，已取消。", true);
                return;
            }

            TxtName.Text = string.Empty;
            TxtNote.Text = string.Empty;

            Refresh();
            ShowNotice($"已保存合集「{collection.Name}」，收录 {collection.ModKeys.Count} 个 Mod。");
        }
        catch (Exception ex)
        {
            Log.Error("保存 Mod 合集失败", ex);
            ShowNotice($"保存失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
        }
    }

    // ————— 套用 —————

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: ModCollection collection }) return;

        if (InstanceStore.Current is not { } instance)
        {
            ShowNotice("还没有实例，先到「实例」页选一个。", true);
            return;
        }

        try
        {
            _busy = true;

            var directory = instance.ModsDirectory;
            var scan = ModScanner.Scan(directory);

            if (!scan.ModsDirectoryExists)
            {
                ShowNotice($"Mods 目录还不存在：{directory}", true);
                return;
            }

            var plan = ModCollectionStore.Plan(collection, scan.Mods);

            if (plan.ToEnable.Count == 0 && plan.ToDisable.Count == 0)
            {
                ShowNotice($"「{collection.Name}」和现在的状态一致，不需要改动。");
                return;
            }

            // 把要发生的事一次说清楚再动手：套用会关闭不在合集里的 Mod，这一步必须用户点头
            var lines = new List<string> { $"要把 Mods 目录切换到合集「{collection.Name}」：" };

            if (plan.ToEnable.Count > 0) lines.Add($"\n· 启用 {plan.ToEnable.Count} 个：{Sample(plan.ToEnable, mod => mod.DisplayName)}");
            if (plan.ToDisable.Count > 0) lines.Add($"\n· 禁用 {plan.ToDisable.Count} 个：{Sample(plan.ToDisable, mod => mod.DisplayName)}");
            if (plan.Missing.Count > 0) lines.Add($"\n· 合集里有 {plan.Missing.Count} 个在磁盘上找不到，会跳过：{Sample(plan.Missing, key => key)}");

            lines.Add("\n\n只改目录名的点前缀，不删除任何文件，之后可以再套用别的合集切回来。");

            if (!Dialogs.Confirm(this, string.Join("", lines), "套用 Mod 合集", "开始切换", "算了")) return;

            var changed = 0;
            var failed = new List<string>();

            foreach (var mod in plan.ToEnable)
            {
                if (ModEnabler.TrySetEnabled(mod, true, out var error)) changed++;
                else failed.Add($"{mod.DisplayName}（{error}）");
            }

            foreach (var mod in plan.ToDisable)
            {
                if (ModEnabler.TrySetEnabled(mod, false, out var error)) changed++;
                else failed.Add($"{mod.DisplayName}（{error}）");
            }

            Applied = true;

            if (failed.Count == 0)
            {
                ShowNotice($"已套用「{collection.Name}」，改动了 {changed} 个 Mod。");
            }
            else
            {
                ShowNotice($"已改动 {changed} 个，另有 {failed.Count} 个没成功：{string.Join("、", failed)}", true);
            }

            InstanceStore.RefreshInstalls();
        }
        catch (Exception ex)
        {
            Log.Error("套用 Mod 合集失败", ex);
            ShowNotice($"套用失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModCollection collection }) return;

        if (!Dialogs.Confirm(this, $"删除合集「{collection.Name}」？\n\n只删这份存档，磁盘上的 Mod 一个都不会动。",
                "删除合集", "删除", "算了"))
            return;

        ModCollectionStore.Delete(collection);
        Refresh();
        ShowNotice($"已删除合集「{collection.Name}」。");
    }

    private static string Sample<T>(IReadOnlyList<T> items, Func<T, string>? label = null)
    {
        const int max = 5;
        label ??= item => item?.ToString() ?? "";

        var head = string.Join("、", items.Take(max).Select(label));
        return items.Count <= max ? head : $"{head} 等";
    }

    // ————— 其它 —————

    private void OnOpenModsDirClick(object sender, RoutedEventArgs e)
    {
        if (InstanceStore.Current is not { } instance)
        {
            ShowNotice("还没有实例。", true);
            return;
        }

        ShellHelper.OpenFolder(instance.ModsDirectory);
    }

    private void ShowNotice(string text, bool warn = false)
    {
        LabNotice.Text = text;
        BarNotice.Background = (Brush)FindResource(warn ? "Status.WarnSoft" : "Accent.Faint");
        IconNotice.IconBrush = (Brush)FindResource(warn ? "Status.Warn" : "Accent.Base");
        IconNotice.Icon = warn ? "lucide/triangle-alert" : "lucide/info";
        BarNotice.Visibility = Visibility.Visible;
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e)
        => BarNotice.Visibility = Visibility.Collapsed;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
