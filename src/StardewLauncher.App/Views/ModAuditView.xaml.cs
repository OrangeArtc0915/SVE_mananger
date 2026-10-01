using System.IO;
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
/// 批量 Mod 信息分析（工具箱里的一个工具）：一次跑完八项检查，逐项给出结论与明细。
/// 与「Mod 冲突检查」分工不同 —— 那个只看磁盘上的装载毛病，
/// 这个会连带读 manifest、比对依赖、检查 SMAPI 装没装。
/// 作为工具箱的子视图存在，进入时由宿主调用 <see cref="Activate"/>。
/// </summary>
public partial class ModAuditView : UserControl
{
    private bool _busy;
    private string? _modsDirectory;

    public ModAuditView()
    {
        InitializeComponent();
        Log.SetModule("批量分析");
    }

    /// <summary>宿主切到这个工具时调用：自动跑一遍。</summary>
    public void Activate() => _ = RunAsync();

    private async Task RunAsync()
    {
        if (_busy) return;

        _busy = true;
        SetBusy(true);

        try
        {
            if (InstanceStore.Current is not { } instance)
            {
                _modsDirectory = null;
                PanChecks.ItemsSource = null;

                ShowSummary("还没有实例",
                    "先到「实例」页新建或选一个，这里才知道该分析哪个 Mods 目录。", false);

                return;
            }

            var directory = instance.ModsDirectory;
            _modsDirectory = directory;

            var report = await Task.Run(() =>
            {
                var scan = ModScanner.Scan(directory);
                if (scan.ModsDirectoryExists) DependencyResolver.Evaluate(scan.Mods);
                return ModAudit.Run(instance, scan);
            });

            PanChecks.ItemsSource = report.Checks;
            ScrollChecks.Visibility = Visibility.Visible;

            var problems = report.FailCount + report.WarnCount;

            ShowSummary(report.Headline,
                $"Mods 目录：{report.ModsDirectory}\n" +
                $"共 {report.ModCount} 个 Mod（启用 {report.EnabledCount}），" +
                $"{report.Checks.Count} 项检查里 {report.FailCount} 项有问题、{report.WarnCount} 项要留意。",
                problems > 0);
        }
        catch (Exception ex)
        {
            Log.Error("批量分析 Mod 失败", ex);
            ShowNotice($"分析失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ————— 工具栏 —————

    private void OnRunClick(object sender, RoutedEventArgs e) => _ = RunAsync();

    private void OnOpenModsDirClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_modsDirectory) || !Directory.Exists(_modsDirectory))
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

        IconSummary.Icon = problems ? "lucide/triangle-alert" : "lucide/list";
        IconSummary.IconBrush = (Brush)FindResource(problems ? "Status.Warn" : "Status.Success");
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
        BtnRun.IsEnabled = !busy;
        BtnOpenDir.IsEnabled = !busy;
    }
}
