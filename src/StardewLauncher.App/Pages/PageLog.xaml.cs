using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Pages;

/// <summary>
/// 运行日志页。「应用日志」是启动器自己的操作记录（联机、安装等），
/// 「游戏日志」是游戏进程输出与启动 / 退出记录，两者共用 ActivityLog 这一份内存缓冲。
///
/// 因为日志不再属于某个页面，切走再回来仍能看到完整历史；页面未打开时写入的日志也不会丢。
/// </summary>
public partial class PageLog : LauncherPage
{
    /// <summary>页面上最多渲染的行数，与缓冲上限保持一致。</summary>
    private const int MaxLines = ActivityLog.Capacity;

    private readonly OutlineButton[] _tabs;

    private LogSource _source = LogSource.App;
    private bool _subscribed;

    public PageLog()
    {
        InitializeComponent();

        _tabs = [BtnAppLog, BtnGameLog];

        SwitchTab(0);
    }

    // ————— 生命周期 —————

    public override void OnEnter()
    {
        Subscribe();
        Reload();
    }

    public override void OnLeave() => Unsubscribe();

    // ————— 子标签 —————

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (int.TryParse(tag, out var index)) SwitchTab(index);
    }

    private void SwitchTab(int index)
    {
        for (var i = 0; i < _tabs.Length; i++)
            _tabs[i].Tone = i == index ? ButtonTone.Solid : ButtonTone.Outline;

        _source = index == 1 ? LogSource.Game : LogSource.App;

        Reload();
    }

    // ————— 日志 —————

    private void Subscribe()
    {
        if (_subscribed) return;

        _subscribed = true;
        ActivityLog.Appended += OnAppended;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;

        _subscribed = false;
        ActivityLog.Appended -= OnAppended;
    }

    /// <summary>日志可能来自后台线程（如进程输出），必须切回 UI 线程再动控件。</summary>
    private void OnAppended(ActivityLine line)
    {
        if (Dispatcher.CheckAccess()) Append(line);
        else Dispatcher.InvokeAsync(() => Append(line));
    }

    /// <summary>把缓冲里的历史铺一遍，用于进入页面或切换子标签。</summary>
    private void Reload()
    {
        if (PanLines is null) return;

        PanLines.Children.Clear();

        foreach (var line in ActivityLog.Snapshot())
        {
            if (line.Source == _source) Append(line, scroll: false);
        }

        RefreshEmptyHint();
        ScrollLog.ScrollToEnd();
    }

    private void Append(ActivityLine line, bool scroll = true)
    {
        if (PanLines is null || line.Source != _source) return;

        var block = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Text = $"{line.Time:HH:mm:ss}  {line.Text}"
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, BrushKey(line.Level));

        PanLines.Children.Add(block);

        while (PanLines.Children.Count > MaxLines) PanLines.Children.RemoveAt(0);

        RefreshEmptyHint();

        // ScrollToEnd 内部按无限偏移排队，布局完成后会被夹到末尾，无需额外 UpdateLayout
        if (scroll) ScrollLog.ScrollToEnd();
    }

    private void RefreshEmptyHint()
    {
        var hasLines = PanLines.Children.Count > 0;

        LabEmpty.Visibility = hasLines ? Visibility.Collapsed : Visibility.Visible;
        ScrollLog.Visibility = hasLines ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BrushKey(ActivityLevel level) => level switch
    {
        ActivityLevel.Error => "Status.Danger",
        ActivityLevel.Warn => "Status.Warn",
        _ => "Text.Secondary"
    };
}
