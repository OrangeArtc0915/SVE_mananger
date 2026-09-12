using System.Collections.ObjectModel;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Tasks;

/// <summary>
/// 任务中心：统一登记任务、把任务事件桥接成可绑定模型，并负责统一取消与清理。
/// 每个任务持有自己的 CancellationTokenSource，取消时真正中断传入 ExecuteAsync 的 token。
/// </summary>
public static class TaskCenter
{
    /// <summary>已登记的任务（顶层任务；分组任务的子任务挂在对应模型的 Children 上）。</summary>
    public static ObservableCollection<TaskModel> Tasks { get; } = [];

    /// <summary>登记一个任务并返回其界面模型，默认立即开始执行。</summary>
    public static TaskModel Register(ITask task, bool start = true)
    {
        ArgumentNullException.ThrowIfNull(task);

        var model = CreateModel(task);
        Tasks.Add(model);

        if (start) Start(model);
        return model;
    }

    /// <summary>移除所有已结束（成功 / 取消 / 失败）的任务。</summary>
    public static void RemoveFinished()
    {
        for (var i = Tasks.Count - 1; i >= 0; i--)
        {
            var model = Tasks[i];
            if (model.State is TaskState.Waiting or TaskState.Running) continue;

            Tasks.RemoveAt(i);
            model.TokenSource?.Dispose();
        }
    }

    /// <summary>取消所有尚未结束的任务，含分组下的子任务。</summary>
    public static void CancelAll()
    {
        foreach (var model in Tasks) CancelRecursive(model);
    }

    private static void CancelRecursive(TaskModel model)
    {
        if (model.State is TaskState.Waiting or TaskState.Running) model.RequestCancel();

        foreach (var child in model.Children) CancelRecursive(child);
    }

    private static TaskModel CreateModel(ITask task)
    {
        var model = new TaskModel
        {
            Title = task.Title,
            Source = task,
            SupportProgress = task is ITaskProgressive,
            SupportCancel = task is ITaskCancelable,
            SupportPause = false,
            IsGroup = task is ITaskGroup,
            TokenSource = new CancellationTokenSource()
        };

        task.StateChanged += (state, message) =>
        {
            model.StateMessage = message;
            model.State = state;
        };

        if (task is ITaskProgressive progressive)
            progressive.ProgressChanged += value => model.Progress = value;

        if (task is ITaskGroup group)
        {
            group.AddTask += child =>
            {
                var childModel = CreateModel(child);
                model.Children.Add(childModel);
                Start(childModel);
            };

            group.RemoveTask += child =>
            {
                var target = FindChild(model, child);
                if (target is null) return;

                model.Children.Remove(target);
                target.TokenSource?.Dispose();
            };
        }

        return model;
    }

    private static void Start(TaskModel model)
    {
        model.State = TaskState.Running;
        _ = RunAsync(model);
    }

    private static async Task RunAsync(TaskModel model)
    {
        var token = model.TokenSource!.Token;

        try
        {
            // 关键：必须把 token 传进 ExecuteAsync，否则取消不会真正生效
            await model.Source!.ExecuteAsync(token).ConfigureAwait(false);

            if (model.CancelRequested)
            {
                model.State = TaskState.Canceled;
                return;
            }

            model.State = TaskState.Success;
            model.Progress = 1d;
        }
        catch (OperationCanceledException)
        {
            // 取消属于预期路径，静默处理
            model.State = TaskState.Canceled;
        }
        catch (Exception ex)
        {
            model.StateMessage = ex.Message;
            model.State = TaskState.Failed;
            Log.Error($"任务执行失败：{model.Title}", ex);
        }
    }

    private static TaskModel? FindChild(TaskModel parent, ITask source)
    {
        foreach (var child in parent.Children)
        {
            if (ReferenceEquals(child.Source, source)) return child;

            var found = FindChild(child, source);
            if (found is not null) return found;
        }

        return null;
    }
}
