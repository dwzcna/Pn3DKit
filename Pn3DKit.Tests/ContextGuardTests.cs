using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证生产上下文作用域的身份、异常恢复及线程约束，不加载 Autodesk。</summary>
public sealed class ContextGuardTests
{
    /// <summary>即使原图或前一图身份已不适用，当前同步操作使用自己的检查，结束后恢复。</summary>
    [Fact]
    public void NewDrawingDoesNotInvokeStaleOriginalGuard()
    {
        bool originalInvalid = true;
        var guard = new ScopedContextGuard(() => { if (originalInvalid) throw new InvalidOperationException("旧图已关闭"); });
        int current = 0;
        using (guard.Use(() => current++)) guard.Ensure();
        Assert.Equal(1, current);
        Assert.Throws<InvalidOperationException>(guard.Ensure);
        originalInvalid = false; guard.Ensure();
    }

    /// <summary>检查或回调抛错时，using 仍恢复上一检查，不把失败图的 Guard 泄漏到下一图。</summary>
    [Fact]
    public void FailedDrawingRestoresPreviousGuard()
    {
        int root = 0;
        var guard = new ScopedContextGuard(() => root++);
        Assert.Throws<IOException>(() =>
        {
            using var scope = guard.Use(() => throw new IOException("当前图状态不可读"));
            guard.Ensure();
        });
        guard.Ensure(); Assert.Equal(1, root);
        int next = 0;
        using (guard.Use(() => next++)) guard.Ensure();
        Assert.Equal(1, next);
    }

    /// <summary>嵌套恢复按作用域身份判断，乱序释放不会破坏当前检查，重复释放安全。</summary>
    [Fact]
    public void NestedScopesRejectOutOfOrderDisposal()
    {
        int value = 0;
        var guard = new ScopedContextGuard(() => value = 0);
        var first = guard.Use(() => value = 1);
        var second = guard.Use(() => value = 2);
        Assert.Throws<InvalidOperationException>(first.Dispose);
        guard.Ensure(); Assert.Equal(2, value);
        second.Dispose(); guard.Ensure(); Assert.Equal(1, value);
        first.Dispose(); first.Dispose(); guard.Ensure(); Assert.Equal(0, value);
    }

    /// <summary>不允许另一线程切换或调用正在使用的宿主检查。</summary>
    [Fact]
    public void OtherThreadCannotUseOrExecuteGuard()
    {
        var guard = new ScopedContextGuard(() => { });
        using var scope = guard.Use(() => { });
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Assert.Throws<InvalidOperationException>(guard.Ensure);
                Assert.Throws<InvalidOperationException>(() => guard.Use(() => { }));
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "跨线程检查超时");
        Assert.Null(failure);
        guard.Ensure();
    }
}
