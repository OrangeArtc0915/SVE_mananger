using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.App.Views;

/// <summary>
/// 原版文件还原（工具箱里的一个工具）：把被 Mod 覆盖过的游戏原版文件从覆盖前备份里救回来。
/// 备份是启动器在覆盖前就地留下的（<c>{文件}.bak-时间戳</c>），这里按索引判断哪些能确定是原版。
/// 作为工具箱的子视图存在，进入时由宿主调用 <see cref="Activate"/>。
/// </summary>
public partial class GameFileRestoreView : UserControl
{
    private string? _gameDirectory;

    private bool _busy;

    public GameFileRestoreView()
    {
        InitializeComponent();
        Log.SetModule("原版还原");
    }

    /// <summary>宿主切到这个工具时调用：重新扫一遍备份。</summary>
    public void Activate() => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        SetBusy(true);
        ShowNotice("正在扫描覆盖前备份…");

        try
        {
            var directory = InstanceStore.Current?.Install?.Directory;

            if (string.IsNullOrWhiteSpace(directory))
            {
                _gameDirectory = null;
                PanItems.ItemsSource = null;

                ShowSummary("游戏目录不可用",
                    "先到「实例」页选一个目录可用的实例，这里才知道要去哪里找备份。", false);

                return;
            }

            _gameDirectory = directory;

            var items = await Task.Run(() => GameFileBackups.List(directory));
            PanItems.ItemsSource = items;

            if (items.Count == 0)
            {
                ShowSummary("没有可还原的原版文件",
                    "启动器只在覆盖游戏原版文件前备份：还没发生过覆盖，或者已经在「设置 → Mod 与下载」里关掉了「覆盖前备份」。",
                    false);
            }
            else
            {
                var registered = items.Count(item => item.Registered);
                var unregistered = items.Count - registered;

                ShowSummary($"{items.Count} 个文件可以还原",
                    $"{registered} 个来自登记过的备份，能确定是原版；{unregistered} 个是索引之前留下的，无法确定。",
                    unregistered > 0);
            }

            if (GameProcess.IsRunning())
                ShowNotice("游戏正在运行：还原正在使用的文件可能失败，建议先退出游戏。", true);
        }
        catch (Exception ex)
        {
            Log.Error("扫描覆盖前备份失败", ex);
            ShowNotice($"扫描失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ————— 工具栏 —————

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnOpenGameDirClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gameDirectory) || !System.IO.Directory.Exists(_gameDirectory))
        {
            ShowNotice("游戏目录不可用。", true);
            return;
        }

        ShellHelper.OpenFolder(_gameDirectory);
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: GameFileRestoreItem item }) return;

        var running = GameProcess.IsRunning();

        var message = $"用备份把 {item.RelativePath} 还原成原版？\n\n"
                      + $"还原前会把当前文件另存为 {item.FileName}.before-restore-时间戳，"
                      + "备份本身也留着，所以这一步随时能反悔。\n\n"
                      + $"备份时间：{item.CreatedText}　{item.SizeText}";

        if (!item.Registered)
            message += "\n\n注意：这份备份没有登记过，无法确定它是原版文件，还是上一个 Mod 覆盖后的版本。";

        if (running) message += "\n\n游戏正在运行，建议先退出游戏再还原。";

        if (!Dialogs.Confirm(Window.GetWindow(this)!, message, "还原原版文件", "还原", "取消")) return;

        GameFileRestoreResult result;

        _busy = true;
        SetBusy(true);

        try
        {
            result = await Task.Run(() => GameFileBackups.Restore(item));
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }

        ShowNotice(result.Message, !result.Ok);

        if (result.Ok) _ = ScanAsync();
    }

    // ————— 摘要与提示条 —————

    private void ShowSummary(string title, string detail, bool warn)
    {
        // 「正在扫描…」这条提示的使命结束了，结论由摘要条来说
        BarNotice.Visibility = Visibility.Collapsed;

        LabSummaryTitle.Text = title;
        LabSummaryDetail.Text = detail;

        IconSummary.Icon = warn ? "lucide/triangle-alert" : "lucide/rotate-ccw";
        IconSummary.SetResourceReference(SvgIcon.IconBrushProperty, warn ? "Status.Warn" : "Accent.Base");
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

    private void SetBusy(bool busy)
    {
        BtnRefresh.IsEnabled = !busy;
        BtnOpenDir.IsEnabled = !busy;
    }
}
