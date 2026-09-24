using System.Windows;
using System.Windows.Controls;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Windows;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Saves;

namespace StardewLauncher.App.Views;

/// <summary>
/// 存档管理（工具箱里的一个工具）：列出游戏存档的摘要，并提供备份与回滚。
/// 作为工具箱的子视图存在，进入时由宿主调用 <see cref="Activate"/>。
/// </summary>
public partial class SaveManagerView : UserControl
{
    private bool _busy;

    public SaveManagerView()
    {
        InitializeComponent();
        Log.SetModule("存档");

        LabPaths.Text = $"存档目录：{SaveScanner.SavesDirectory}　　备份目录：{SaveBackupService.Root}";
    }

    /// <summary>宿主切到这个工具时调用：重新扫描存档。</summary>
    public void Activate() => _ = ScanAsync();

    // ————— 扫描 —————

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var saves = await Task.Run(() =>
            {
                var list = SaveScanner.Scan();
                foreach (var save in list) save.BackupCount = SaveBackupService.CountOf(save.Id);
                return list;
            });

            PanSaves.ItemsSource = saves;

            var empty = saves.Count == 0;
            PanSaves.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            CardEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

            if (empty)
            {
                LabEmpty.Text = SaveScanner.Exists
                    ? "存档目录存在，但里面没有可识别的存档（每个存档都需要一个 SaveGameInfo）。"
                    : $"没有找到 {SaveScanner.SavesDirectory}。如果游戏装了没玩过，或者存档被挪到别处，这里就会是空的。";
            }

            RefreshRunningWarning();
        }
        catch (Exception ex)
        {
            Log.Error("扫描存档失败", ex);
            ShowNotice($"扫描存档失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void RefreshRunningWarning()
    {
        if (GameProcess.IsRunning())
        {
            ShowNotice("游戏正在运行：可以备份，但此时备份的是上次保存的状态；回滚需要先退出游戏。", true);
        }
    }

    // ————— 工具栏 —————

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnOpenSavesDirClick(object sender, RoutedEventArgs e)
    {
        if (!SaveScanner.Exists)
        {
            ShowNotice($"存档目录还不存在：{SaveScanner.SavesDirectory}", true);
            return;
        }

        ShellHelper.OpenFolder(SaveScanner.SavesDirectory);
    }

    private async void OnBackupAllClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (PanSaves.ItemsSource is not IReadOnlyList<SaveSummary> saves || saves.Count == 0)
        {
            ShowNotice("没有可备份的存档。", true);
            return;
        }

        var running = GameProcess.IsRunning();
        var message = $"给 {saves.Count} 个存档各备份一份？"
                      + $"\n\n备份会放在：{SaveBackupService.Root}"
                      + $"\n每个存档最多保留 {SettingsStore.Current.SaveBackupKeepCount} 份，超出后自动删掉最旧的。";

        if (running)
        {
            message += "\n\n注意：游戏正在运行，此时备份的是上次保存的状态。";
        }

        var answer = Dialogs.Confirm(Window.GetWindow(this)!, message, "全部备份");
        if (!answer) return;

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var ok = 0;
            var failed = 0;

            foreach (var save in saves)
            {
                var created = await Task.Run(() => SaveBackupService.Create(save, out _));
                if (created is null) failed++;
                else
                {
                    ok++;
                    SaveBackupService.Prune(save.Id, SettingsStore.Current.SaveBackupKeepCount);
                }

                ShowNotice($"正在备份… {ok + failed}/{saves.Count}", failed > 0);
            }

            ShowNotice(failed == 0
                ? $"已备份 {ok} 个存档。"
                : $"备份完成：成功 {ok} 个，失败 {failed} 个，详见运行日志。", failed > 0);

            await ScanAsync();
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    // ————— 卡片操作 —————

    private async void OnBackupClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: SaveSummary save }) return;

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var backup = await Task.Run(() => SaveBackupService.Create(save, out _));

            if (backup is null)
            {
                ShowNotice($"「{save.DisplayName}」备份失败，详见运行日志。", true);
                return;
            }

            SaveBackupService.Prune(save.Id, SettingsStore.Current.SaveBackupKeepCount);
            ShowNotice($"已备份「{save.DisplayName}」：{backup.FolderName}（{backup.SizeText}）");

            await ScanAsync();
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void OnManageBackupsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SaveSummary save }) return;

        var window = new SaveBackupWindow(save) { Owner = Window.GetWindow(this) };
        window.ShowDialog();

        _ = ScanAsync();
    }

    private void OnOpenSaveDirClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SaveSummary save }) return;
        ShellHelper.OpenFolder(save.Directory);
    }

    // ————— 提示条 —————

    private void ShowNotice(string text, bool warn = false)
    {
        LabNotice.Text = text;
        BarNotice.Background = (System.Windows.Media.Brush)FindResource(warn ? "Status.WarnSoft" : "Accent.Faint");
        IconNotice.IconBrush = (System.Windows.Media.Brush)FindResource(warn ? "Status.Warn" : "Accent.Base");
        IconNotice.Icon = warn ? "lucide/triangle-alert" : "lucide/info";
        BarNotice.Visibility = Visibility.Visible;
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e)
        => BarNotice.Visibility = Visibility.Collapsed;

    private void SetButtonsEnabled(bool enabled)
    {
        BtnRefresh.IsEnabled = enabled;
        BtnBackupAll.IsEnabled = enabled;
        BtnOpenDir.IsEnabled = enabled;
    }
}
