namespace Pn3DKit.Services;

/// <summary>导出、检查与重建共用的批次互斥；租约覆盖异步保存和清理。</summary>
public static class BatchRunGate
{
    private static int _running;
    /// <summary>当前是否有批次持有租约。</summary>
    public static bool IsRunning => Volatile.Read(ref _running) != 0;
    /// <summary>原子申请租约，已有批次时返回空。</summary>
    public static IDisposable? TryEnter() =>
        Interlocked.CompareExchange(ref _running, 1, 0) == 0 ? new Lease() : null;

    /// <summary>仅释放本次持有的租约，重复释放不会解锁后来者。</summary>
    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Exchange(ref _running, 0);
        }
    }
}
