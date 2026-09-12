using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace StardewLauncher.Core.Tasks;

/// <summary>
/// 任务在界面上的可绑定模型。
/// 手写 INotifyPropertyChanged 与 ICommand，避免为 Core 引入任何 UI 框架依赖。
/// </summary>
public sealed class TaskModel : INotifyPropertyChanged
{
    private readonly SimpleCommand _cancelCommand;
    private readonly SimpleCommand _pauseCommand;

    private string _title = string.Empty;
    private TaskState _state = TaskState.Waiting;
    private string _stateMessage = string.Empty;
    private double _progress;
    private bool _supportProgress;
    private bool _supportCancel;
    private bool _supportPause;

    public TaskModel()
    {
        _cancelCommand = new SimpleCommand(
            RequestCancel,
            () => SupportCancel && State is TaskState.Waiting or TaskState.Running);
        _pauseCommand = new SimpleCommand(Noop, () => SupportPause && State is TaskState.Running);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>任务本体。由中心保留强引用，便于取消与统计；也是唯一实现来源。</summary>
    public ITask? Source { get; init; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public TaskState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            _cancelCommand.RaiseCanExecuteChanged();
            _pauseCommand.RaiseCanExecuteChanged();
        }
    }

    public string StateMessage
    {
        get => _stateMessage;
        set => Set(ref _stateMessage, value);
    }

    /// <summary>进度，取值 0..1，越界自动收敛。</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, Math.Clamp(value, 0d, 1d));
    }

    public bool SupportProgress
    {
        get => _supportProgress;
        set => Set(ref _supportProgress, value);
    }

    public bool SupportCancel
    {
        get => _supportCancel;
        set
        {
            if (Set(ref _supportCancel, value)) _cancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool SupportPause
    {
        get => _supportPause;
        set
        {
            if (Set(ref _supportPause, value)) _pauseCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>是否为包含子任务的分组任务。</summary>
    public bool IsGroup { get; init; }

    /// <summary>子任务模型，仅分组任务使用。</summary>
    public ObservableCollection<TaskModel> Children { get; } = [];

    public ICommand CancelCommand => _cancelCommand;

    public ICommand PauseCommand => _pauseCommand;

    /// <summary>该任务独占的取消源，由中心注入并传入 ExecuteAsync。</summary>
    internal CancellationTokenSource? TokenSource { get; set; }

    /// <summary>是否已请求取消。任务不理会 token 直接返回时，中心据此仍判为已取消。</summary>
    internal bool CancelRequested { get; private set; }

    /// <summary>真正的取消入口：中断 token、通知任务本体、并立即更新状态。</summary>
    internal void RequestCancel()
    {
        CancelRequested = true;

        try { TokenSource?.Cancel(); }
        catch (ObjectDisposedException) { /* 任务刚结束并已释放，忽略 */ }

        (Source as ITaskCancelable)?.Cancel();
        State = TaskState.Canceled;
    }

    // 暂停的具体语义由上层实现方决定，这里只保证命令在不支持时不可用。
    private static void Noop() { }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    /// <summary>不依赖任何命令框架的极简 ICommand 实现。</summary>
    private sealed class SimpleCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public SimpleCommand(Action execute, Func<bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute();

        public void Execute(object? parameter)
        {
            if (!_canExecute()) return;
            _execute();
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
