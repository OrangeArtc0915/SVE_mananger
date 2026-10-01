namespace StardewLauncher.Core.Tasks;

/// <summary>
/// 把一个现成的异步委托包成任务中心认得的任务。
/// 用在那些「本来就是一段 async 方法、不值得为它专门写个类」的耗时操作上，
/// 好处是它们会统一出现在下载监视器里，进度与取消也走同一套。
/// </summary>
public sealed class DelegateTask : ITask, ITaskProgressive
{
    private readonly Func<IProgress<double>, CancellationToken, Task> _work;

    public DelegateTask(string title, Func<IProgress<double>, CancellationToken, Task> work)
    {
        Title = title;
        _work = work;
    }

    public string Title { get; }

    public event TaskStateEvent? StateChanged;

    public event TaskProgressEvent? ProgressChanged;

    public async Task ExecuteAsync(CancellationToken token = default)
    {
        StateChanged?.Invoke(TaskState.Running, "正在处理");

        try
        {
            await _work(new Progress<double>(value => ProgressChanged?.Invoke(value)), token).ConfigureAwait(false);
            StateChanged?.Invoke(TaskState.Success, "完成");
        }
        catch (OperationCanceledException)
        {
            StateChanged?.Invoke(TaskState.Canceled, "已取消");
            throw;
        }
        catch (Exception ex)
        {
            StateChanged?.Invoke(TaskState.Failed, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 登记并执行一个耗时操作，返回它的任务模型。
    /// 任务中心已经把异常记进日志并落成失败状态，所以这里不再往上抛，调用方照常往下走即可。
    /// </summary>
    public static async Task<TaskModel> RunAsync(string title,
        Func<IProgress<double>, CancellationToken, Task> work)
    {
        var model = TaskCenter.Register(new DelegateTask(title, work));

        // Register 已经启动了任务，这里只是等它跑完，让调用方接着做后续（刷新列表、显示结果）
        while (model.State is TaskState.Waiting or TaskState.Running)
        {
            await Task.Delay(80).ConfigureAwait(false);
        }

        return model;
    }
}
