using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.Core.Diagnostics;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Views;

/// <summary>
/// SMAPI 日志分析（工具箱里的一个工具）：读最近一份 SMAPI 日志，按来源统计错误与警告，
/// 把「谁在刷屏」直接摆出来，顺带列出被 SMAPI 跳过的 Mod 与关键错误原文。
/// 作为工具箱的子视图存在，进入时由宿主调用 <see cref="Activate"/>。
/// </summary>
public partial class SmapiLogView : UserControl
{
    private SmapiLogReport? _report;

    private bool _busy;

    public SmapiLogView()
    {
        InitializeComponent();
        Log.SetModule("日志分析");
    }

    /// <summary>宿主切到这个工具时调用：重新读一遍日志。</summary>
    public void Activate() => _ = AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        if (_busy) return;

        _busy = true;
        SetBusy(true);
        ShowNotice("正在读日志…");

        try
        {
            var report = await Task.Run(() => SmapiLogReader.Analyze());
            Show(report);
        }
        catch (Exception ex)
        {
            Log.Error("分析 SMAPI 日志失败", ex);
            ShowNotice($"分析失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void Show(SmapiLogReport report)
    {
        _report = report;

        BarNotice.Visibility = Visibility.Collapsed;

        if (!report.Found)
        {
            LabSummaryTitle.Text = "读不到 SMAPI 日志";
            LabSummaryDetail.Text = report.Error ?? "";
            IconSummary.Icon = "lucide/circle-help";
            IconSummary.SetResourceReference(SvgIcon.IconBrushProperty, "Text.Secondary");

            PanSources.ItemsSource = null;
            CardSkipped.Visibility = Visibility.Collapsed;
            CardHighlights.Visibility = Visibility.Collapsed;

            return;
        }

        LabSummaryTitle.Text = report.Clean
            ? "日志里没有错误和警告"
            : $"共 {report.TotalErrors} 条错误、{report.TotalWarnings} 条警告";

        var meta = new List<string>();

        if (report.ModifiedAt is { } modified) meta.Add($"日志时间 {modified:MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(report.SmapiVersion)) meta.Add($"SMAPI {report.SmapiVersion}");
        if (!string.IsNullOrWhiteSpace(report.GameVersion)) meta.Add($"游戏 {report.GameVersion}");

        meta.Add(report.FileName);

        LabSummaryDetail.Text = string.Join("　·　", meta);

        IconSummary.Icon = report.Clean ? "lucide/shield-check" : "lucide/triangle-alert";

        IconSummary.SetResourceReference(SvgIcon.IconBrushProperty,
            report.TotalErrors > 0 ? "Status.Danger" : report.Clean ? "Status.Success" : "Status.Warn");

        PanSources.ItemsSource = report.Sources;

        PanSkipped.ItemsSource = report.Skipped;
        CardSkipped.Visibility = report.Skipped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        PanHighlights.ItemsSource = report.Highlights;
        CardHighlights.Visibility = report.Highlights.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ————— 工具栏 —————

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = AnalyzeAsync();

    private void OnOpenDirClick(object sender, RoutedEventArgs e)
    {
        var directory = _report is null ? null : Path.GetDirectoryName(_report.FilePath);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ShowNotice("日志目录还不存在，用 SMAPI 启动过游戏之后才会生成。", true);
            return;
        }

        ShellHelper.OpenFolder(directory);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            ShowNotice("还没分析过，先点「重新分析」。", true);
            return;
        }

        try
        {
            Clipboard.SetText(_report.ToText());
            ShowNotice("分析报告已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            Log.Warn($"复制分析报告失败：{ex.Message}");
            ShowNotice($"复制失败：{ex.Message}", true);
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
        BtnOpenDir.IsEnabled = !busy;
    }
}
