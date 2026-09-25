using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.App.Views;

/// <summary>
/// Mod 冲突检查（工具箱里的一个工具）：只看磁盘上已经扫出来的结果，找出三类确定有毛病的装载方式 ——
/// 多个 Mod 共用同一个 ID、压根加载不了的 Mod、Mods 目录里没被识别的东西。
/// 作为工具箱的子视图存在，进入时由宿主调用 <see cref="Activate"/>。
/// </summary>
public partial class ModConflictView : UserControl
{
    private bool _busy;

    private string? _modsDirectory;

    public ModConflictView()
    {
        InitializeComponent();
        Log.SetModule("冲突");
    }

    /// <summary>宿主切到这个工具时调用：重新扫一遍。</summary>
    public void Activate() => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        SetBusy(true);

        try
        {
            if (InstanceStore.Current is not { } instance)
            {
                _modsDirectory = null;
                PanConflicts.ItemsSource = null;

                ShowSummary("还没有实例",
                    "先到「实例」页新建或选一个，这里才知道该检查哪个 Mods 目录。", false);

                return;
            }

            var directory = instance.ModsDirectory;
            _modsDirectory = directory;

            var result = await Task.Run(() =>
            {
                var scan = ModScanner.Scan(directory);

                IReadOnlyList<ModConflict> conflicts = scan.ModsDirectoryExists
                    ? ModConflictDetector.Analyze(scan.Mods, scan.ModsDirectory)
                    : [];

                return (Scan: scan, Conflicts: conflicts);
            });

            if (!result.Scan.ModsDirectoryExists)
            {
                ShowSummary("Mods 目录还不存在", $"{directory}\n装第一个 Mod 时会自动建出来。", false);
                return;
            }

            PanConflicts.ItemsSource = result.Conflicts;

            if (result.Conflicts.Count == 0)
            {
                ShowSummary("没有发现冲突",
                    $"扫了 {result.Scan.SummaryText}，重复 ID、加载不了的 Mod、放错位置的目录都没查到。",
                    false);

                return;
            }

            ShowSummary($"发现 {result.Conflicts.Count} 类冲突",
                $"扫了 {result.Scan.SummaryText}。下面逐条列出涉及的目录，按提示处理完再回来检查一次。",
                true);
        }
        catch (Exception ex)
        {
            Log.Error("检查 Mod 冲突失败", ex);
            ShowNotice($"检查失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ————— 工具栏 —————

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnOpenModsDirClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_modsDirectory) || !System.IO.Directory.Exists(_modsDirectory))
        {
            ShowNotice("Mods 目录还不存在。", true);
            return;
        }

        ShellHelper.OpenFolder(_modsDirectory);
    }

    // ————— 摘要与提示条 —————

    private void ShowSummary(string title, string detail, bool problems)
    {
        LabSummaryTitle.Text = title;
        LabSummaryDetail.Text = detail;

        IconSummary.Icon = problems ? "lucide/triangle-alert" : "lucide/shield-check";
        IconSummary.SetResourceReference(SvgIcon.IconBrushProperty, problems ? "Status.Warn" : "Status.Success");

        ScrollConflicts.Visibility = problems ? Visibility.Visible : Visibility.Collapsed;
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
