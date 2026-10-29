using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>确定性验证生产原生命令策略的回调、超时、取消和清理；不替代真实CAD原型。</summary>
public sealed class NativeCommandTests
{
    /// <summary>同步替身将所有事件限制在测试线程，显式控制原生任务返回和空闲检查点。</summary>
    private sealed class Host : INativeCommandHost
    {
        internal Action<NativeCommandEvent>? Report;
        internal Action? Checkpoint;
        internal Func<Task>? Execute;
        internal Exception? ContextError, DisposeError, AttachError;
        internal int Disposals, Executions, Ensures;
        internal bool Idle = true;
        internal readonly RunLogFiles Files = new();
        public TimeSpan Elapsed { get; set; }
        public bool IsQuiescent => Idle;
        public void EnsureContext() { Ensures++; if (ContextError != null) throw ContextError; }
        public void Protect(string reason) => Files.Protect(reason);
        public IDisposable Observe(Action<NativeCommandEvent> report, Action checkpoint)
        {
            if (AttachError != null) throw AttachError;
            Report = report; Checkpoint = checkpoint;
            return new Release(() => { Disposals++; if (DisposeError != null) throw DisposeError; });
        }
        public Task ExecuteAsync()
        {
            Executions++;
            if (Execute != null) return Execute();
            Report!(NativeCommandEvent.Started); Report(NativeCommandEvent.Ended);
            return Task.CompletedTask;
        }
        private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
    }

    /// <summary>替身续体由手工完成源同步执行，避免测试框架调度到其他线程影响主线程断言。</summary>
    private static void OnOneThread(Action test)
    {
        var original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { test(); } finally { SynchronizationContext.SetSynchronizationContext(original); }
    }

    /// <summary>仅一次开始／结束并空闲才成功，完成前后监听恰好释放一次。</summary>
    [Fact]
    public void SuccessRequiresNativeReturnIdleAndCleanup() => OnOneThread(() =>
    {
        var host = new Host();
        NativeCommandOperation.RunAsync(host).GetAwaiter().GetResult();
        Assert.Equal(1, host.Executions); Assert.Equal(1, host.Disposals);
        Assert.Null(host.Files.ProtectionReason);
    });

    /// <summary>没有开始或重复执行不能冒充一次成功。</summary>
    [Theory] [InlineData(2, 2)] [InlineData(0, 1)]
    public void InvalidCompletionCountsAreRejected(int starts, int ends) => OnOneThread(() =>
    {
        var host = new Host();
        host.Execute = () =>
        {
            for (int i = 0; i < starts; i++) host.Report!(NativeCommandEvent.Started);
            for (int i = 0; i < ends; i++) host.Report!(NativeCommandEvent.Ended);
            return Task.CompletedTask;
        };
        var error = Assert.Throws<CadOperationException>(() => NativeCommandOperation.RunAsync(host).GetAwaiter().GetResult());
        Assert.True(error.StateKnown); Assert.True(error.StopBatch); Assert.Equal(1, host.Disposals);
    });

    /// <summary>取消、未知命令及普通失败采用不同的停止语义，均等待清理。</summary>
    [Theory] [InlineData(NativeCommandEvent.Canceled, true, true)]
    [InlineData(NativeCommandEvent.Unknown, false, true)] [InlineData(NativeCommandEvent.Failed, false, false)]
    public void EventsKeepKnownStateAndFailureMeaning(NativeCommandEvent value, bool canceled, bool stop) => OnOneThread(() =>
    {
        var host = new Host();
        host.Execute = () => { host.Report!(value); return Task.CompletedTask; };
        var error = Assert.Throws<CadOperationException>(() => NativeCommandOperation.RunAsync(host).GetAwaiter().GetResult());
        Assert.True(error.StateKnown); Assert.Equal(canceled, error.Canceled); Assert.Equal(stop, error.StopBatch);
        Assert.Equal(1, host.Disposals); Assert.Null(host.Files.ProtectionReason);
    });

    /// <summary>命令回调错误在上下文返回后保留原始异常，空闲已确认时允许策略判定后续。</summary>
    [Fact]
    public void CommandExceptionIsPreservedAfterCallbackReturns() => OnOneThread(() =>
    {
        var host = new Host { Execute = () => Task.FromException(new IOException("native error")) };
        var error = Assert.Throws<CadOperationException>(() => NativeCommandOperation.RunAsync(host).GetAwaiter().GetResult());
        Assert.True(error.StateKnown); Assert.IsType<IOException>(error.InnerException); Assert.Equal(1, host.Disposals);
    });

    /// <summary>真实QSAVE可能在Task返回及IsQuiescent为真之后才送达CommandEnded；此前不能判成功或拆监听。</summary>
    [Fact]
    public void QuiescentDoesNotReplaceDelayedCommandEnded() => OnOneThread(() =>
    {
        var host = new Host();
        host.Execute = () => { host.Report!(NativeCommandEvent.Started); return Task.CompletedTask; };
        var running = NativeCommandOperation.RunAsync(host);
        Assert.False(running.IsCompleted); Assert.Equal(0, host.Disposals);
        host.Report!(NativeCommandEvent.Ended); host.Checkpoint!();
        running.GetAwaiter().GetResult(); Assert.Equal(1, host.Disposals);
    });

    /// <summary>没有可靠终止事件的空闲状态也不能冒充完成，超时保留未知状态。</summary>
    [Theory] [InlineData(0)] [InlineData(1)]
    public void MissingTerminalEventTimesOut(int starts) => OnOneThread(() =>
    {
        var host = new Host();
        host.Execute = () => { for (int i = 0; i < starts; i++) host.Report!(NativeCommandEvent.Started); return Task.CompletedTask; };
        var running = NativeCommandOperation.RunAsync(host);
        Assert.False(running.IsCompleted);
        host.Elapsed = TimeSpan.FromSeconds(600); host.Checkpoint!();
        Assert.False(Assert.Throws<CadOperationException>(() => running.GetAwaiter().GetResult()).StateKnown);
        Assert.Equal(1, host.Disposals);
    });

    /// <summary>命令已返回但宿主未空闲时不完成、不拆监听。</summary>
    [Fact]
    public void ReturnedCommandWaitsForIdle() => OnOneThread(() =>
    {
        var host = new Host { Idle = false };
        var running = NativeCommandOperation.RunAsync(host);
        Assert.False(running.IsCompleted); Assert.Equal(0, host.Disposals);
        host.Idle = true; host.Checkpoint!();
        running.GetAwaiter().GetResult(); Assert.Equal(1, host.Disposals);
    });

    /// <summary>600秒超时不把仍运行的原生调用当作结束，环境保持关闭直到明确收尾。</summary>
    [Fact]
    public void TimeoutWhileNativeRunsDoesNotReleaseOrRestore() => OnOneThread(() =>
    {
        var native = new TaskCompletionSource<bool>();
        var host = new Host { Execute = () => native.Task };
        int value = 1;
        var scope = new ExportAutoCheckScope(() => value, item => value = Convert.ToInt32(item));
        scope.Disable();
        var running = NativeCommandOperation.RunAsync(host);
        host.Elapsed = TimeSpan.FromSeconds(600); host.Checkpoint!();
        Assert.False(running.IsCompleted); Assert.Equal(0, host.Disposals); Assert.Equal(0, value);
        Assert.NotNull(host.Files.ProtectionReason);
        native.SetResult(true);
        var error = Assert.Throws<CadOperationException>(() => running.GetAwaiter().GetResult());
        Assert.False(error.StateKnown); Assert.True(error.StopBatch); Assert.Equal(1, host.Disposals);
        Assert.True(scope.Finish(host.Files.ProtectionReason).HasIssue); Assert.Equal(0, value);
    });

    /// <summary>原生调用返回后仍未空闲，超时返回未知状态并保护日志。</summary>
    [Fact]
    public void IdleTimeoutStopsWithoutClaimingSuccess() => OnOneThread(() =>
    {
        var host = new Host { Idle = false };
        var running = NativeCommandOperation.RunAsync(host);
        host.Elapsed = TimeSpan.FromSeconds(601); host.Checkpoint!();
        var error = Assert.Throws<CadOperationException>(() => running.GetAwaiter().GetResult());
        Assert.False(error.StateKnown); Assert.Equal(1, host.Disposals); Assert.NotNull(host.Files.ProtectionReason);
    });

    /// <summary>图纸或项目变化时等待正在运行的调用返回，随后停止且不恢复环境。</summary>
    [Fact]
    public void IdentityChangeRetainsActiveNativeCall() => OnOneThread(() =>
    {
        var native = new TaskCompletionSource<bool>();
        var host = new Host { Execute = () => native.Task };
        var running = NativeCommandOperation.RunAsync(host);
        host.ContextError = new InvalidOperationException("BlockTableId changed"); host.Checkpoint!();
        Assert.False(running.IsCompleted); Assert.Equal(0, host.Disposals);
        native.SetResult(true);
        var error = Assert.Throws<CadOperationException>(() => running.GetAwaiter().GetResult());
        Assert.False(error.StateKnown); Assert.Contains("BlockTableId", error.ToString()); Assert.Equal(1, host.Disposals);
    });

    /// <summary>主故障和清理故障同时保留；清理失败即使命令完成仍禁止继续。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CleanupFailureKeepsPrimaryAndProtects(bool commandFailed) => OnOneThread(() =>
    {
        var host = new Host { DisposeError = new IOException("cleanup error") };
        if (commandFailed) host.Execute = () => Task.FromException(new InvalidOperationException("command error"));
        var error = Assert.Throws<CadOperationException>(() => NativeCommandOperation.RunAsync(host).GetAwaiter().GetResult());
        Assert.False(error.StateKnown); Assert.Contains("cleanup error", error.ToString());
        if (commandFailed) Assert.Contains("command error", error.ToString());
        Assert.Equal(1, host.Disposals); Assert.NotNull(host.Files.ProtectionReason);
    });

    /// <summary>挂接或前置身份检查失败不得启动原生命令。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void PreflightFailureStartsNoCommand(bool attach) => OnOneThread(() =>
    {
        var host = new Host();
        if (attach) host.AttachError = new IOException("listen error"); else host.ContextError = new IOException("context error");
        Assert.Throws<CadOperationException>(() => NativeCommandOperation.RunAsync(host).GetAwaiter().GetResult());
        Assert.Equal(0, host.Executions); Assert.NotNull(host.Files.ProtectionReason);
    });

    /// <summary>Plant命令的缺省值和显式值，按真实API状态解释。</summary>
    [Theory] [InlineData(-5001, "", 1)] [InlineData(5100, "", 1)]
    [InlineData(5100, "0", 0)] [InlineData(5100, "1", 1)]
    public void NativeAutoCheckValuesAreParsed(int status, string raw, int expected) =>
        Assert.Equal(expected, PlantAutoCheckValue.Parse(status, raw));

    /// <summary>SDK读取失败或错误内容不自动当作关闭或可恢复的正常值。</summary>
    [Theory] [InlineData(-5001, "0")] [InlineData(5100, "2")] [InlineData(5100, " 0")]
    [InlineData(5100, "false")] [InlineData(-1, "")] [InlineData(0, "0")]
    public void NativeAutoCheckFailuresRejectExport(int status, string raw) =>
        Assert.Throws<InvalidOperationException>(() => PlantAutoCheckValue.Parse(status, raw));
}
