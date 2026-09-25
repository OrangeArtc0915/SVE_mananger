using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.Core.Diagnostics;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Views;

/// <summary>
/// 环境体检（工具箱里的一个工具）：把启动器、游戏目录、SMAPI、Mods、依赖、冲突、磁盘一次扫一遍，
/// 结果能整段复制，也能连日志一起导成反馈包发给别人。
/// 作为工具箱的子视图存在，进入时由宿主调用 <see cref="Activate"/>。
/// </summary>
public partial class HealthCheckView : UserControl
{
    private HealthReport? _report;

    private bool _busy;

    public HealthCheckView()
    {
        InitializeComponent();
        Log.SetModule("体检");
    }

    /// <summary>宿主切到这个工具时调用：重新体检一次。</summary>
    public void Activate() => _ = InspectAsync();

    private async Task InspectAsync()
    {
        if (_busy) return;

        _busy = true;
        SetBusy(true);
        ShowNotice("正在体检…");

        try
        {
            var report = await Task.Run(() => HealthInspector.Inspect());
            Show(report);
        }
        catch (Exception ex)
        {
            Log.Error("体检失败", ex);
            ShowNotice($"体检没做完：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void Show(HealthReport report)
    {
        _report = report;
        PanItems.ItemsSource = report.Items;

        // 「正在体检…」这条提示的使命结束了，结果由下面的总览条来说
        BarNotice.Visibility = Visibility.Collapsed;

        LabSummaryTitle.Text = report.FailCount > 0
            ? $"有 {report.FailCount} 处问题、{report.WarnCount} 处需要注意"
            : report.WarnCount > 0
                ? $"有 {report.WarnCount} 处需要注意"
                : "没发现明显问题";

        LabSummaryDetail.Text = $"{report.SummaryText}　{report.GeneratedAt:MM-dd HH:mm} 体检";

        IconSummary.Icon = report.HasProblems ? "lucide/triangle-alert" : "lucide/shield-check";

        IconSummary.SetResourceReference(SvgIcon.IconBrushProperty, report.FailCount > 0
            ? "Status.Danger"
            : report.HasProblems
                ? "Status.Warn"
                : "Status.Success");
    }

    // ————— 工具栏 —————

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = InspectAsync();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            ShowNotice("还没体检过，先点「重新体检」。", true);
            return;
        }

        try
        {
            Clipboard.SetText(_report.ToText());
            ShowNotice("体检报告已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            Log.Warn($"复制体检报告失败：{ex.Message}");
            ShowNotice($"复制失败：{ex.Message}", true);
        }
    }

    private async void OnPackageClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出反馈包",
            Filter = "压缩包 (*.zip)|*.zip",
            FileName = $"StardewLauncher-反馈-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        _busy = true;
        SetBusy(true);

        try
        {
            var report = _report;
            var path = dialog.FileName;
            var progress = new Progress<string>(message => ShowNotice(message));

            var result = await Task.Run(() => SupportPackage.Create(path, report, progress));
            ShowNotice(result.Message, !result.Ok);
        }
        catch (Exception ex)
        {
            Log.Error("导出反馈包失败", ex);
            ShowNotice($"导出失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ————— 提示条 —————

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
        BtnCopy.IsEnabled = !busy;
        BtnPackage.IsEnabled = !busy;
    }
}
