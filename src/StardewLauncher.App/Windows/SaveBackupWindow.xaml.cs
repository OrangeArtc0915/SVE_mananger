using System.Windows;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Views;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Saves;

namespace StardewLauncher.App.Windows;

/// <summary>单个存档的备份管理：新建、回滚、删除。回滚前会自动给当前存档打一份快照。</summary>
public partial class SaveBackupWindow : Window
{
    private readonly SaveSummary _save;
    private bool _busy;

    public SaveBackupWindow(SaveSummary save)
    {
        InitializeComponent();

        _save = save;

        Title = $"备份与回滚 · {save.DisplayName}";
        LabTitle.Text = $"「{save.DisplayName}」的备份";
        LabPath.Text = $"存档：{save.Directory}　　备份：{SaveBackupService.DirectoryOf(save.Id)}";

        Loaded += (_, _) => _ = RefreshAsync();

        RefreshHint();
    }

    // ————— 列表 —————

    private async Task RefreshAsync()
    {
        if (_busy) return;

        var backups = await Task.Run(() => SaveBackupService.List(_save.Id));
        PanBackups.ItemsSource = backups;

        var empty = backups.Count == 0;
        LabEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LabEmpty.Text = "这个存档还没有备份。\n点右上角「新建备份」会把当前存档整目录复制一份到这里。";

        RefreshHint();
    }

    private void RefreshHint()
    {
        var keep = SettingsStore.Current.SaveBackupKeepCount;
        var snapshot = SettingsStore.Current.SnapshotBeforeRestore ? "恢复前会自动给当前存档打一份快照" : "恢复前不会自动快照（已在设置里关掉）";
        LabHint.Text = $"每份备份是整目录复制；最多保留 {keep} 份，超出后自动删最旧的。{snapshot}。";
    }

    // ————— 操作 —————

    private async void OnNewBackupClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var backup = await Task.Run(() => SaveBackupService.Create(_save, out _));

            if (backup is null)
            {
                ShowNotice("备份失败，详见运行日志。", true);
                return;
            }

            SaveBackupService.Prune(_save.Id, SettingsStore.Current.SaveBackupKeepCount);
            ShowNotice($"已备份：{backup.FolderName}（{backup.SizeText}）");

            await RefreshAsync();
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: SaveBackup backup }) return;

        if (GameProcess.IsRunning())
        {
            Dialogs.Warn(this,
                "游戏正在运行，先退出游戏再回滚。\n\n游戏中保存会把回滚的结果覆盖掉，而且存档文件可能被占用。",
                "回滚已取消");
            return;
        }

        var snapshotLine = SettingsStore.Current.SnapshotBeforeRestore
            ? "\n当前存档会先自动快照一份，之后还能退回来。"
            : "\n注意：设置里关掉了「恢复前自动快照」，这次回滚不可撤销。";

        var answer = Dialogs.Confirm(this,
            $"用 {backup.TimeText} 的备份（{backup.KindText}，{backup.SizeText}）覆盖存档「{_save.DisplayName}」？"
            + snapshotLine
            + "\n\n已经打开的游戏不会感知这次改动；改完再进游戏即可。",
            "确认回滚");

        if (!answer) return;

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var ok = await Task.Run(() => SaveBackupService.Restore(backup, _save, out _));

            ShowNotice(ok
                ? $"已回滚到 {backup.TimeText} 的备份。"
                : "回滚失败，详见运行日志。", !ok);

            await RefreshAsync();
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: SaveBackup backup }) return;

        var toRecycleBin = SettingsStore.Current.DeleteToRecycleBin;
        var target = toRecycleBin ? "回收站" : "永久删除";

        var answer = Dialogs.Confirm(this,
            $"删除 {backup.TimeText} 的备份（{backup.SizeText}）？\n\n会放进{target}。",
            "删除备份");

        if (!answer) return;

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var ok = await Task.Run(() => SaveBackupService.Delete(backup, toRecycleBin, out _));
            ShowNotice(ok ? "已删除该备份。" : "删除失败，详见运行日志。", !ok);

            await RefreshAsync();
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void OnOpenDirClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(SaveBackupService.DirectoryOf(_save.Id));

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ————— 提示条 —————

    private void ShowNotice(string text, bool warn = false)
    {
        LabNotice.Text = text;
        BarNotice.Background = (System.Windows.Media.Brush)FindResource(warn ? "Status.WarnSoft" : "Accent.Faint");
        IconNotice.IconBrush = (System.Windows.Media.Brush)FindResource(warn ? "Status.Warn" : "Accent.Base");
        IconNotice.Icon = warn ? "lucide/triangle-alert" : "lucide/info";
        BarNotice.Visibility = Visibility.Visible;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        BtnNewBackup.IsEnabled = enabled;
        BtnOpenDir.IsEnabled = enabled;
    }
}
