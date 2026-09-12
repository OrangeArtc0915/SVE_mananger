namespace StardewLauncher.Core.Tasks;

/// <summary>任务状态。</summary>
public enum TaskState
{
    /// <summary>尚未开始执行。</summary>
    Waiting,

    /// <summary>正在执行。</summary>
    Running,

    /// <summary>执行成功结束。</summary>
    Success,

    /// <summary>被取消（含用户主动取消与中心统一取消）。</summary>
    Canceled,

    /// <summary>执行过程中抛出异常。</summary>
    Failed
}

/// <summary>任务状态变化通知，message 供界面直接展示。</summary>
public delegate void TaskStateEvent(TaskState state, string message);

/// <summary>任务进度变化通知，取值 0..1。</summary>
public delegate void TaskProgressEvent(double progress);

/// <summary>可被任务中心调度的任务。</summary>
public interface ITask
{
    string Title { get; }

    Task ExecuteAsync(CancellationToken token = default);

    event TaskStateEvent? StateChanged;
}

/// <summary>支持上报进度的任务。</summary>
public interface ITaskProgressive : ITask
{
    event TaskProgressEvent? ProgressChanged;
}

/// <summary>支持主动取消的任务。</summary>
public interface ITaskCancelable : ITask
{
    void Cancel();
}

/// <summary>包含子任务的任务，子任务在运行期间动态加入或移除。</summary>
public interface ITaskGroup : ITask
{
    event Action<ITask>? AddTask;

    event Action<ITask>? RemoveTask;
}
