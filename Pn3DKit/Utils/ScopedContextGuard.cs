namespace Pn3DKit.Utils;

/// <summary>主线程上的同步上下文检查作用域；结束时恢复原检查，不把某张图纸的身份留给下一张。</summary>
internal sealed class ScopedContextGuard(Action original)
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private Scope? _active;

    /// <summary>执行当前作用域的检查，未进入作用域时执行原检查；失败原样传播。</summary>
    internal void Ensure()
    {
        CheckThread();
        (_active?.Guard ?? original)();
    }

    /// <summary>临时绑定检查，不执行可能已过时的原检查；作用域内不得等待或切换线程。</summary>
    /// <param name="guard">同步操作目标的身份与取消检查。</param>
    /// <returns>按后进先出顺序释放的作用域；释放后恢复上一检查。</returns>
    internal IDisposable Use(Action guard)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(guard);
        var scope = new Scope(this, _active, guard);
        _active = scope;
        return scope;
    }

    /// <summary>禁止在线程池或异步延续中接管宿主的同步上下文。</summary>
    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("上下文检查离开宿主主线程。");
    }

    /// <summary>按作用域身份恢复，重复释放无副作用，乱序释放拒绝改变当前检查。</summary>
    private sealed class Scope(ScopedContextGuard owner, Scope? previous, Action guard) : IDisposable
    {
        internal Action Guard { get; } = guard;
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            owner.CheckThread();
            if (!ReferenceEquals(owner._active, this))
                throw new InvalidOperationException("上下文检查作用域未按顺序结束。");
            owner._active = previous;
            _disposed = true;
        }
    }
}
