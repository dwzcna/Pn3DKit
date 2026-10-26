using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Services;

namespace Pn3DKit.Tests;

/// <summary>用可推进时钟和可注入文件故障验证名称清单，不等待真实一秒、不加载 CAD。</summary>
public sealed class XrefNameCsvTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.XrefNames", Guid.NewGuid().ToString("N"));

    /// <summary>活动报告允许只读查看器观察已刷新数据。</summary>
    internal static string ReadLive(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

    /// <summary>来源原名不参与文件名安全判断，完整文本保留在索引中。</summary>
    private static XrefBlockSource Source(string key = "来源1", string drawing = @"D:\项目\A.dwg") =>
        new(key, drawing, "宿主当前数据", false, ["主图 → " + key]);

    /// <summary>构造时立刻写出 BOM 表头，每个名称立即进入缓冲，恰好 50 行时刷新。</summary>
    [Fact]
    public void HeaderAndFiftyRowFlushAreImmediate()
    {
        var memory = new MemoryStream(); var stream = new XrefFaultStream(memory);
        var clock = new XrefTestTimeProvider();
        using var csv = new XrefBlockNameCsv("A.csv", stream, clock);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, memory.ToArray().Take(3));
        Assert.Equal("块名称,是否匿名\r\n", Encoding.UTF8.GetString(memory.ToArray()[3..]));
        int initial = stream.FlushCalls;
        for (int i = 0; i < 49; i++) csv.Append("Valve" + i, false);
        Assert.Equal(49, csv.Written); Assert.Equal(0, csv.Flushed); Assert.Equal(initial, stream.FlushCalls);
        csv.Append("*U1", true);
        Assert.Equal(50, csv.Flushed); Assert.Equal(initial + 1, stream.FlushCalls);
        Assert.Contains("*U1,是\r\n", Encoding.UTF8.GetString(memory.ToArray()));
    }

    /// <summary>不足 50 行时一秒到期刷新；空缓冲不周期刷盘。</summary>
    [Fact]
    public void OneSecondFlushAndIdleDoNotNeedMoreNames()
    {
        var stream = new XrefFaultStream(new MemoryStream()); var clock = new XrefTestTimeProvider();
        using var csv = new XrefBlockNameCsv("A.csv", stream, clock);
        csv.Append("Valve", false); clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(0, csv.Flushed);
        clock.Advance(TimeSpan.FromMilliseconds(1)); Assert.Equal(1, csv.Flushed);
        int after = stream.FlushCalls; clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(after, stream.FlushCalls);
        csv.Append("Pump", false); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, csv.Flushed);
    }

    /// <summary>达到 50 行后撤销旧计时，下一批由第一个待刷新名称重新计时。</summary>
    [Fact]
    public void RowThresholdResetsTimerForNextBuffer()
    {
        var clock = new XrefTestTimeProvider();
        using var csv = new XrefBlockNameCsv("A.csv", new MemoryStream(), clock);
        for (int i = 0; i < 49; i++) csv.Append("Valve" + i, false);
        clock.Advance(TimeSpan.FromMilliseconds(900)); csv.Append("Pump", false);
        Assert.Equal(50, csv.Flushed);
        clock.Advance(TimeSpan.FromSeconds(5)); csv.Append("Tail", false);
        clock.Advance(TimeSpan.FromMilliseconds(999)); Assert.Equal(50, csv.Flushed);
        clock.Advance(TimeSpan.FromMilliseconds(1)); Assert.Equal(51, csv.Flushed);
    }

    /// <summary>关闭时刷新不足 50 行尾部并释放流和计时器，重复关闭不重复操作。</summary>
    [Fact]
    public void CloseFlushesTailAndCancelsTimer()
    {
        var clock = new XrefTestTimeProvider(); var stream = new XrefFaultStream(new MemoryStream());
        var csv = new XrefBlockNameCsv("A.csv", stream, clock);
        csv.Append("Valve", false); csv.Dispose();
        Assert.Equal(1, csv.Flushed); Assert.True(stream.Closed);
        int flushes = stream.FlushCalls;
        clock.Advance(TimeSpan.FromMinutes(1)); csv.Dispose();
        Assert.Equal(flushes, stream.FlushCalls);
        Assert.Throws<XrefBlockLogException>(() => csv.Append("Later", false));
    }

    /// <summary>名称中的中文、引号、逗号和换行按 RFC 4180 转义。</summary>
    [Fact]
    public void NamesUseCsvEscaping()
    {
        var memory = new MemoryStream();
        using (var csv = new XrefBlockNameCsv("A.csv", memory, new XrefTestTimeProvider()))
            csv.Append("中文,\"Valve\"\r\n下一行", false);
        Assert.Contains("\"中文,\"\"Valve\"\"\r\n下一行\",否\r\n", Encoding.UTF8.GetString(memory.ToArray()));
    }

    /// <summary>后台失败不逃出回调；主线程检查和后续追加立即拒绝，保留已确认数量。</summary>
    [Fact]
    public void TimerFailureStopsAtMainThreadCheckpoint()
    {
        var clock = new XrefTestTimeProvider(); var stream = new XrefFaultStream(new MemoryStream());
        var csv = new XrefBlockNameCsv("A.csv", stream, clock);
        csv.Append("Valve", false); stream.FailFlush = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, csv.Written); Assert.Equal(0, csv.Flushed);
        Assert.Contains("模拟刷新失败", Assert.Throws<XrefBlockLogException>(csv.Check).ToString());
        Assert.Throws<XrefBlockLogException>(() => csv.Append("Pump", false));
        Assert.Throws<XrefBlockLogException>(csv.Dispose); Assert.True(stream.Closed);
    }

    /// <summary>写入及关闭故障都保留具体原因，其他资源仍尝试释放。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteAndCloseFailuresAreExplicit(bool write)
    {
        var stream = new XrefFaultStream(new MemoryStream());
        var csv = new XrefBlockNameCsv("A.csv", stream, new XrefTestTimeProvider());
        if (write)
        {
            stream.FailWrites = true;
            Assert.Throws<XrefBlockLogException>(() => csv.Append(new string('V', 4000), false));
        }
        else { csv.Append("Valve", false); stream.FailClose = true; }
        Assert.Throws<XrefBlockLogException>(csv.Dispose);
        Assert.True(stream.Closed);
    }

    /// <summary>表头或计时器创建失败时也释放已创建文件。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConstructorFailureClosesFile(bool header)
    {
        var stream = new XrefFaultStream(new MemoryStream()) { FailFlush = header };
        var clock = new XrefTestTimeProvider { FailCreate = !header };
        Assert.Throws<XrefBlockLogException>(() => new XrefBlockNameCsv("A.csv", stream, clock));
        Assert.True(stream.Closed);
    }

    /// <summary>定时刷新可以在其他线程执行，主报告和索引只在显式主线程调用时变化。</summary>
    [Fact]
    public async Task TimerDoesNotWriteMainReports()
    {
        var clock = new XrefTestTimeProvider();
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles(), clock);
        var scan = journal.BeginNames(Source()); scan.Append("Valve", false);
        string log = ReadLive(journal.LogPath), index = ReadLive(journal.SourcesPath);
        await Task.Run(() => clock.Advance(TimeSpan.FromSeconds(1)));
        Assert.Contains("Valve,否", ReadLive(scan.Path));
        Assert.Equal(log, ReadLive(journal.LogPath)); Assert.Equal(index, ReadLive(journal.SourcesPath));
        journal.EndNames(scan, "完成", "");
    }

    /// <summary>GUID 与原始路径可反向追溯；同文件名和同路径不同宿主状态都保留独立记录。</summary>
    [Fact]
    public void SourceIndexPreservesIdentityAndOriginalNames()
    {
        var clock = new XrefTestTimeProvider(); var files = new RunLogFiles();
        using var journal = new XrefBlockJournal(_root, "A.dwg", files, clock);
        var first = journal.BeginNames(Source("来源1", @"D:\甲\A.dwg")); first.Append("Valve", false);
        clock.Advance(TimeSpan.FromMilliseconds(25)); journal.EndNames(first, "完成", "");
        var second = journal.BeginNames(Source("来源2", @"D:\乙\A.dwg")); journal.EndNames(second, "完成", "");
        var third = journal.BeginNames(Source("来源3", @"D:\甲\A.dwg")); journal.EndNames(third, "完成", "");
        Assert.Equal(3, new[] { first.Id, second.Id, third.Id }.Distinct().Count());
        Assert.Equal(new[] { 1, 2, 3 }, new[] { first.DwgNo, second.DwgNo, third.DwgNo });
        Assert.StartsWith("dwgNo,来源ID,", ReadLive(journal.SourcesPath));
        foreach (var scan in new[] { first, second, third })
        {
            Assert.EndsWith("__" + scan.Id.ToString("N") + ".csv", scan.Path);
            Assert.Contains(scan.Source.Drawing, ReadLive(journal.SourcesPath));
            Assert.Contains(Path.GetFileName(scan.Path), ReadLive(journal.SourcesPath));
        }
        Assert.Contains(",完成,1,1,25.0,", ReadLive(journal.SourcesPath));
        Assert.Equal(8, files.Paths.Count);
    }

    /// <summary>名称前缀合法化不会丢失包含特殊字符的原始来源标识。</summary>
    [Fact]
    public void SanitizedNameIsRecoverableFromIndex()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles(), new XrefTestTimeProvider());
        var source = Source(drawing: "原始,\"图纸\"\n名称?.dwg");
        var scan = journal.BeginNames(source); journal.EndNames(scan, "完成", "");
        Assert.DoesNotContain("?", Path.GetFileName(scan.Path)); Assert.DoesNotContain("\"", Path.GetFileName(scan.Path));
        Assert.Contains("\"原始,\"\"图纸\"\"\n名称?.dwg\"", ReadLive(journal.SourcesPath));
    }

    /// <summary>GUID 文件已存在时重新分配，原有文件不登记为本批所有。</summary>
    [Fact]
    public void GuidCollisionNeverOverwritesOrOwnsExistingFile()
    {
        var ids = new Queue<Guid>([Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222")]);
        Directory.CreateDirectory(_root);
        string existing = Path.Combine(_root, "A__11111111111111111111111111111111.csv");
        File.WriteAllText(existing, "原有文件");
        var files = new RunLogFiles();
        using (var journal = new XrefBlockJournal(_root, "A.dwg", files, new XrefTestTimeProvider(), newSourceId: ids.Dequeue))
        {
            var scan = journal.BeginNames(Source()); journal.EndNames(scan, "完成", "");
            Assert.Contains("22222222222222222222222222222222", scan.Path);
        }
        files.Seal(); files.Delete();
        Assert.Equal("原有文件", File.ReadAllText(existing)); Assert.DoesNotContain(existing, files.Paths);
    }

    /// <summary>清单创建失败也写明路径和失败状态，不将无法确认的数据说成完整输出。</summary>
    [Fact]
    public void CreationFailureIsRecordedAndFatal()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles(), new XrefTestTimeProvider(),
            _ => throw new IOException("模拟创建失败"));
        Assert.Throws<XrefBlockLogException>(() => journal.BeginNames(Source()));
        Assert.Contains("写入失败,0,0,", ReadLive(journal.SourcesPath));
        Assert.Contains("模拟创建失败", ReadLive(journal.LogPath));
    }

    /// <summary>后台故障由日志检查点传播，结束索引保留输出和刷新两个不同计数。</summary>
    [Fact]
    public void JournalReportsConfirmedCountsAfterTimerFailure()
    {
        var clock = new XrefTestTimeProvider(); var stream = new XrefFaultStream(new MemoryStream());
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles(), clock, _ => stream);
        var scan = journal.BeginNames(Source()); scan.Append("Valve", false);
        stream.FailFlush = true; clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<XrefBlockLogException>(journal.CheckNameWrites);
        Assert.Throws<XrefBlockLogException>(() => journal.EndNames(scan, "完成", ""));
        Assert.Contains(",写入失败,1,0,", ReadLive(journal.SourcesPath));
        Assert.True(stream.Closed);
    }

    /// <summary>清单及索引都进入统一归属清理，用户文件保留。</summary>
    [Fact]
    public void AllOwnedReportsCloseBeforeCleanup()
    {
        var files = new RunLogFiles(); Directory.CreateDirectory(_root);
        string user = Path.Combine(_root, "user.csv"); File.WriteAllText(user, "保留");
        using (var journal = new XrefBlockJournal(_root, "A.dwg", files))
        {
            var first = journal.BeginNames(Source()); first.Append("Valve", false); journal.EndNames(first, "完成", "");
            var unfinished = journal.BeginNames(Source("来源2", @"D:\B.dwg")); unfinished.Append("Pump", false);
        }
        files.Seal(); Assert.Null(files.ProtectionReason);
        var cleanup = files.Delete();
        Assert.Equal(7, cleanup.Deleted); Assert.Empty(cleanup.Errors);
        Assert.Equal("保留", File.ReadAllText(user)); Assert.Single(Directory.GetFiles(_root));
    }

    /// <summary>GUID 即使文件名前缀不同也不能重复，索引始终按来源唯一追溯。</summary>
    [Fact]
    public void GuidCollisionAcrossDifferentPrefixesIsReallocated()
    {
        Guid firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var ids = new Queue<Guid>([firstId, firstId, secondId]);
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles(), new XrefTestTimeProvider(), newSourceId: ids.Dequeue);
        var first = journal.BeginNames(Source()); journal.EndNames(first, "完成", "");
        var second = journal.BeginNames(Source("来源2", @"D:\B.dwg")); journal.EndNames(second, "完成", "");
        Assert.Equal(firstId, first.Id); Assert.Equal(secondId, second.Id);
    }

    /// <summary>计时器拒绝调度不能被当作成功，否则无法兑现一秒刷新。</summary>
    [Fact]
    public void RejectedTimerChangeIsFatal()
    {
        var clock = new XrefTestTimeProvider();
        var csv = new XrefBlockNameCsv("A.csv", new MemoryStream(), clock);
        clock.FailChange = true;
        Assert.Throws<XrefBlockLogException>(() => csv.Append("Valve", false));
        Assert.Throws<XrefBlockLogException>(csv.Dispose);
    }

    /// <summary>只删除本例创建的临时目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

/// <summary>可推进单次计时器；回调在时钟锁外执行，避免与写入器锁相互等待。</summary>
internal sealed class XrefTestTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TestTimer> _timers = [];
    private long _ticks;
    internal bool FailCreate { get; init; }
    internal bool FailChange;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (FailCreate) throw new IOException("模拟计时器创建失败");
        var timer = new TestTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period); return timer;
    }
    /// <summary>不睡眠地触发到期回调；只在当前推进方顺序调用。</summary>
    internal void Advance(TimeSpan elapsed)
    {
        long target = GetTimestamp() + elapsed.Ticks;
        while (true)
        {
            TestTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => !t.Closed && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next == null) { _ticks = target; return; }
                _ticks = next.Due; next.Due = long.MaxValue;
            }
            next.Callback(next.State);
        }
    }
    /// <summary>仅实现生产使用的单次调度，关闭后 Change 返回 false。</summary>
    private sealed class TestTimer(XrefTestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal bool Closed;
        internal long Due = long.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (Closed || owner.FailChange) return false;
                if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("本替身只允许单次计时");
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (owner._gate) { Closed = true; Due = long.MaxValue; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>注入名称清单的写入／刷新／关闭故障；不影响真实 CAD 或主报告。</summary>
internal sealed class XrefFaultStream(Stream inner) : Stream
{
    internal bool FailFlush, FailWrites, FailClose, Closed;
    internal int FlushCalls;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { FlushCalls++; if (FailFlush) throw new IOException("模拟刷新失败"); inner.Flush(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count)
    { if (FailWrites) throw new IOException("模拟写入失败"); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer)
    { if (FailWrites) throw new IOException("模拟写入失败"); inner.Write(buffer); }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Closed = true; inner.Dispose();
            if (FailClose) throw new IOException("模拟关闭失败");
        }
        base.Dispose(disposing);
    }
}
