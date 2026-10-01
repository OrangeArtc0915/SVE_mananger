using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using StardewLauncher.App.Views;
using StardewLauncher.Core.Downloads;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Tasks;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 下载监视器：把任务中心里正在进行的任务收在一处，并把结束的任务记成历史。
/// 进行中列表直接绑 <see cref="TaskCenter.Tasks"/>，已结束的行靠模板触发器折起来；
/// 历史由 <see cref="DownloadHistory"/> 落盘，本窗口只负责在观察到任务结束时补记一条。
/// </summary>
public partial class DownloadMonitorWindow : LauncherWindow
{
    /// <summary>已订阅状态变化的顶层任务，防止重复订阅与关闭时泄漏。</summary>
    private readonly HashSet<TaskModel> _watched = [];

    /// <summary>任务开始时间，用于结算耗时；从集合里退订时一并移除，也充当「是否已记过历史」的判据。</summary>
    private readonly Dictionary<TaskModel, DateTime> _startedAt = [];

    public DownloadMonitorWindow()
    {
        InitializeComponent();
        Log.SetModule("下载");

        PanActive.ItemsSource = TaskCenter.Tasks;
        PanHistory.ItemsSource = DownloadHistory.Entries;

        TaskCenter.Tasks.CollectionChanged += OnTasksChanged;
        DownloadHistory.Changed += OnHistoryChanged;

        // 窗口可能是任务已经跑起来之后才打开的，这里把已有任务补上订阅
        foreach (var task in TaskCenter.Tasks) Watch(task);

        RefreshEmptyStates();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 静态集合挂着窗口的处理器会造成泄漏，关闭时必须逐个退订
        TaskCenter.Tasks.CollectionChanged -= OnTasksChanged;
        DownloadHistory.Changed -= OnHistoryChanged;

        foreach (var task in _watched) task.PropertyChanged -= OnTaskPropertyChanged;

        _watched.Clear();
        _startedAt.Clear();

        base.OnClosed(e);
    }

    // ————— 任务追踪 —————

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TaskModel task in e.OldItems) Unwatch(task);

        if (e.NewItems is not null)
            foreach (TaskModel task in e.NewItems) Watch(task);

        // Reset 拿不到旧项，退而求其次：把已经不在中心里的任务全部退订
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var task in _watched.ToList())
                if (!TaskCenter.Tasks.Contains(task)) Unwatch(task);
        }

        RefreshEmptyStates();
    }

    private void Watch(TaskModel task)
    {
        if (!_watched.Add(task)) return;

        _startedAt[task] = DateTime.Now;
        task.PropertyChanged += OnTaskPropertyChanged;
    }

    private void Unwatch(TaskModel task)
    {
        if (!_watched.Remove(task)) return;

        _startedAt.Remove(task);
        task.PropertyChanged -= OnTaskPropertyChanged;
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TaskModel task) return;
        if (e.PropertyName != nameof(TaskModel.State)) return;

        // 状态多半是在后台线程改的（任务用 ConfigureAwait(false) 跑），落盘与刷新都回到 UI 线程做
        Dispatcher.BeginInvoke(() =>
        {
            RecordIfFinished(task);
            RefreshEmptyStates();
        });
    }

    /// <summary>任务结束时补一条历史。已经记过（不在 _startedAt 里）的直接跳过。</summary>
    private void RecordIfFinished(TaskModel task)
    {
        if (task.State is TaskState.Waiting or TaskState.Running) return;
        if (!_startedAt.Remove(task, out var startedAt)) return;

        var stateText = task.State switch
        {
            TaskState.Success => "完成",
            TaskState.Canceled => "已取消",
            TaskState.Failed => "失败",
            _ => task.State.ToString()
        };

        DownloadHistory.Record(task.Title, stateText, task.StateMessage, startedAt);

        // 这条任务不会再变状态了，顺手退订
        task.PropertyChanged -= OnTaskPropertyChanged;
        _watched.Remove(task);
    }

    // ————— 历史刷新 —————

    private void OnHistoryChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshHistory));
            return;
        }

        RefreshHistory();
    }

    private void RefreshHistory()
    {
        // Entries 是不可变快照式的只读列表，重设一次 ItemsSource 才能看到新内容
        PanHistory.ItemsSource = null;
        PanHistory.ItemsSource = DownloadHistory.Entries;

        RefreshEmptyStates();
    }

    private void RefreshEmptyStates()
    {
        // 集合事件的来源线程不一定是 UI 线程，碰可视元素前再兜一层
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshEmptyStates));
            return;
        }

        LabActiveEmpty.Visibility = TaskCenter.Tasks.Any(t => t.State is TaskState.Waiting or TaskState.Running)
            ? Visibility.Collapsed
            : Visibility.Visible;

        LabHistoryEmpty.Visibility = DownloadHistory.Entries.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ————— 操作 —————

    private void OnCancelAllClick(object sender, RoutedEventArgs e) => TaskCenter.CancelAll();

    private void OnClearFinishedClick(object sender, RoutedEventArgs e) => TaskCenter.RemoveFinished();

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (DownloadHistory.Entries.Count == 0) return;

        if (!Dialogs.Confirm(this, "确定清空全部下载历史吗？清空后无法恢复。", "清空历史")) return;

        DownloadHistory.Clear();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
