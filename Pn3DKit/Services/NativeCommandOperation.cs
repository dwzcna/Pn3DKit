using Pn3DKit.Models;

namespace Pn3DKit.Services;

/// <summary>原生命令生命周期事件；适配器仅转发本次命令及其内部失败，不访问策略状态。</summary>
public enum NativeCommandEvent { Started, Ended, Failed, Canceled, Unknown }

/// <summary>隔离主线程命令调用、监听及空闲检查，任务返回表示命令上下文已退出。</summary>
public interface INativeCommandHost
{
    /// <summary>当前操作开始后的单调耗时。</summary>
    TimeSpan Elapsed { get; }
    /// <summary>仅在命令上下文退出后核对应用与文档空闲、命令名为空及CMDACTIVE为零。</summary>
    bool IsQuiescent { get; }
    /// <summary>核对主线程、项目及完整数据库身份。</summary>
    void EnsureContext();
    /// <summary>先挂接事件和主线程检查点；部分挂接失败也须清理，释放失败不得忽略。</summary>
    IDisposable Observe(Action<NativeCommandEvent> report, Action checkpoint);
    /// <summary>在命令回调内部捕获异常，退出命令上下文后才返回失败任务。</summary>
    Task ExecuteAsync();
    /// <summary>状态未知时保护日志和环境；此保护不可被后续成功清除。</summary>
    void Protect(string reason);
}

/// <summary>等待原生命令返回、宿主空闲及监听清理，不把超时当作原生操作结束。</summary>
/// <remarks>所有回调和续体均在宿主主线程；不强制中断正在运行的原生命令。</remarks>
public static class NativeCommandOperation
{
    /// <summary>运行一次命令；600秒后禁止继续，但活动调用返回前仍持有监听及批次。</summary>
    /// <param name="host">已固定目标和参数的主线程适配器。</param>
    /// <returns>命令和监听全部退出的任务；状态未知或清理失败时携带保留现场的异常。</returns>
    public static async Task RunAsync(INativeCommandHost host)
    {
        int thread = Environment.CurrentManagedThreadId, started = 0, ended = 0;
        bool canceled = false, failed = false, unknown = false, returned = false, monitoring = true;
        Exception? unsafeError = null, commandError = null, primary = null;
        IDisposable? observer = null;
        var idle = new TaskCompletionSource<bool>();
        void Ensure()
        {
            if (Environment.CurrentManagedThreadId != thread)
                throw new InvalidOperationException("原生命令续体离开 CAD 主线程。");
            host.EnsureContext();
        }
        void Unsafe(Exception error)
        {
            unsafeError ??= error;
            host.Protect(error.Message);
        }
        void Checkpoint()
        {
            if (!monitoring || idle.Task.IsCompleted) return;
            try
            {
                Ensure();
                if (host.Elapsed >= TimeSpan.FromSeconds(600))
                    Unsafe(new TimeoutException("等待原生命令超过600秒；保留现场，命令返回前继续等待，请在 CAD 中检查或按 Esc。"));
                // 正在运行时仅记录未知状态，不结束任务、不移除监听、不恢复变量。
                if (!returned) return;
                bool terminal = ended != 0 || failed || canceled || unknown || commandError != null;
                if (unsafeError != null || terminal && host.IsQuiescent) idle.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Unsafe(ex);
                if (returned) idle.TrySetResult(true);
            }
        }
        void Report(NativeCommandEvent value)
        {
            switch (value)
            {
                case NativeCommandEvent.Started: started++; break;
                case NativeCommandEvent.Ended: ended++; break;
                case NativeCommandEvent.Canceled: canceled = true; break;
                case NativeCommandEvent.Failed: failed = true; break;
                case NativeCommandEvent.Unknown: unknown = true; break;
            }
        }
        try
        {
            Ensure();
            observer = host.Observe(Report, Checkpoint);
            Ensure();
            try { await host.ExecuteAsync(); }
            catch (Exception ex) { commandError = ex; }
            returned = true;
            if (commandError is CadOperationException { StateKnown: false }) Unsafe(commandError);
            Checkpoint();
            await idle.Task;
            if (unsafeError != null)
                throw new CadOperationException("原生命令状态不明确：" + unsafeError.Message, inner:
                    commandError == null ? unsafeError : new AggregateException(commandError, unsafeError));
            Ensure();
            if (canceled || commandError is OperationCanceledException)
                throw new CadOperationException("用户取消了 CAD 命令。", true, true, true, commandError);
            if (unknown) throw new CadOperationException("宿主没有所需命令，已停止批次。", true, inner: commandError);
            if (failed || commandError != null)
                throw new CadOperationException("CAD 命令执行失败：" + commandError?.Message, true, false, inner: commandError);
            if (started != 1 || ended != 1)
                throw new CadOperationException($"CAD 命令未确认单次完成：开始 {started}，结束 {ended}。", true);
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            monitoring = false;
            try { observer?.Dispose(); }
            catch (Exception cleanup)
            {
                primary = new CadOperationException("原生命令监听清理失败：" + cleanup.Message,
                    inner: primary == null ? cleanup : new AggregateException(primary, cleanup));
            }
        }
        if (primary != null)
        {
            if (primary is not CadOperationException)
                primary = new CadOperationException("原生命令上下文失败：" + primary.Message, inner: primary);
            if (primary is CadOperationException { StateKnown: false }) host.Protect(primary.Message);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }
}
