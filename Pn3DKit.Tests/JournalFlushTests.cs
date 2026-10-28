using Pn3DKit.Services;

namespace Pn3DKit.Tests;

/// <summary>通过可控时钟与调用计数验证日志持久化及失败收尾。</summary>
public sealed class JournalFlushTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    /// <summary>每行刷新缓存，五十行才持久化一次。</summary>
    [Fact]
    public void FiftyRecordsPersistOnce()
    {
        int visible = 0, durable = 0;
        var flush = new JournalFlushPolicy(() => visible++, () => durable++, new Clock());
        for (int i = 0; i < 49; i++) flush.Record();
        Assert.Equal(49, visible); Assert.Equal(0, durable);
        flush.Record(); Assert.Equal(50, visible); Assert.Equal(1, durable);
        flush.Finish(); Assert.Equal(2, durable);
    }

    /// <summary>一秒阈值由主线程下个检查点处理。</summary>
    [Fact]
    public void OneSecondPersistsAtCheckpoint()
    {
        var clock = new Clock(); int durable = 0;
        var flush = new JournalFlushPolicy(() => {}, () => durable++, clock);
        flush.Record(); clock.Now = 999; flush.Record(); Assert.Equal(0, durable);
        clock.Now = 1000; flush.Record(); Assert.Equal(1, durable);
    }

    /// <summary>写前记录持久化成功后才允许进入修改。</summary>
    [Fact]
    public void BeforeWriteIsDurable()
    {
        var events = new List<string>();
        var flush = new JournalFlushPolicy(() => events.Add("buffer"), () => events.Add("durable"), new Clock());
        flush.Record(true); events.Add("SDK write");
        Assert.Equal(new[] { "buffer", "durable", "SDK write" }, events);
    }

    /// <summary>持久化失败停止修改，并继续关闭所有资源。</summary>
    [Fact]
    public void DurableFailureStopsAndClosesAll()
    {
        bool write = false, closed = false;
        var flush = new JournalFlushPolicy(() => {}, () => throw new IOException("disk full"));
        Assert.Throws<IOException>(() => { flush.Record(true); write = true; });
        Assert.False(write); Assert.Throws<IOException>(() => flush.Record());
        Assert.Throws<IOException>(() => JournalFlushPolicy.Close(flush.Finish, new CloseAction(() => closed = true)));
        Assert.True(closed);
    }

    /// <summary>恢复文件通道允许关闭，但不能清除已传播的故障并继续批次。</summary>
    [Fact]
    public void RecoveredChannelCanCloseButCannotResume()
    {
        bool fail = true;
        var flush = new JournalFlushPolicy(() => {}, () => { if (fail) throw new IOException("disk"); });
        var original = Assert.Throws<IOException>(() => flush.Record(true));
        fail = false; flush.Finish();
        Assert.Same(original, Assert.Throws<IOException>(() => flush.Record()));
    }

    /// <summary>空文件及不足阈值的尾部仍最终持久化。</summary>
    [Theory] [InlineData(0)] [InlineData(7)]
    public void FinishPersistsTail(int records)
    {
        int durable = 0;
        var flush = new JournalFlushPolicy(() => {}, () => durable++, new Clock());
        for (int i = 0; i < records; i++) flush.Record();
        flush.Finish(); Assert.Equal(1, durable);
    }
    private sealed class CloseAction(Action close) : IDisposable { public void Dispose() => close(); }
}
