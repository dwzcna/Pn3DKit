using Pn3DKit.Models;
using Pn3DKit.Services;

namespace Pn3DKit.Tests;

/// <summary>验证取消后交互及双重故障的异常保全，不访问 CAD。</summary>
public sealed class RepairLifecycleTests
{
    /// <summary>安全收尾不等于正常完成；取消后不再进入保存询问。</summary>
    [Theory]
    [InlineData(true, true, null, true)]
    [InlineData(true, true, "已中断", false)]
    [InlineData(true, false, null, false)]
    [InlineData(false, true, null, false)]
    public void SavePromptRequiresNormalCompletion(bool attempted, bool safe, string? stop, bool allowed) =>
        Assert.Equal(allowed, TagRebuildSave.CanPrompt(new([], attempted, safe, stop)));

    /// <summary>原始取消和释放错误均可追溯，释放失败仍为致命状态。</summary>
    [Fact]
    public void RunPreservesPrimaryAndCleanup()
    {
        var main = new OperationCanceledException("原始取消");
        var close = new IOException("释放失败");
        var result = Assert.Throws<XrefVisualFatalException>(() => XrefVisualCleanup.Run<int>(
            () => throw main, () => throw close, "清理失败"));
        var errors = Assert.IsType<AggregateException>(result.InnerException).Flatten().InnerExceptions;
        Assert.Same(main, errors[0]); Assert.Same(close, errors[1]);
    }

    /// <summary>清理成功时保留原始异常对象。</summary>
    [Fact]
    public void SuccessfulCleanupPreservesOriginal() =>
        Assert.IsType<OperationCanceledException>(Record.Exception(() =>
            XrefVisualCleanup.Run<int>(() => throw new OperationCanceledException(), () => {}, "cleanup")));

    /// <summary>迭代失败和关闭失败均保留；普通资源仍会关闭。</summary>
    [Fact]
    public void IteratorPreservesFailuresAndCloses()
    {
        bool closed = false;
        IEnumerable<int> Broken() { yield return 1; throw new InvalidOperationException("读取失败"); }
        var stream = XrefVisualCleanup.Iterate(Broken(), () => { closed = true; throw new IOException("关闭失败"); }, "释放失败");
        var result = Assert.Throws<XrefVisualFatalException>(() => stream.ToArray());
        Assert.True(closed);
        Assert.Equal(2, Assert.IsType<AggregateException>(result.InnerException).Flatten().InnerExceptions.Count);
    }

    /// <summary>提前停止消费也关闭资源。</summary>
    [Fact]
    public void EarlyEnumerationCloses()
    {
        bool closed = false;
        Assert.Equal(1, XrefVisualCleanup.Iterate(new[] { 1, 2 }, () => closed = true, "cleanup").First());
        Assert.True(closed);
    }
}
