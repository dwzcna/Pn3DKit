using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>使用可推进时钟及真实 CSV 写入器验证进度、逐组结果和提醒，不加载 CAD。</summary>
public sealed class XrefProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.XrefProgress", Guid.NewGuid().ToString("N"));

    /// <summary>心跳跟随当前阶段，切到比较后不能继续声称正在扫描名称。</summary>
    [Fact]
    public void HeartbeatTracksCurrentFileOrBlock()
    {
        var clock = new XrefTestTimeProvider(); var output = new List<string>();
        var progress = new XrefBlockProgress(output.Add, clock);
        progress.SourceFound(1, "D:\\项目\\A.dwg");
        clock.Advance(TimeSpan.FromSeconds(3)); progress.Pulse();
        Assert.Equal(2, output.Count); Assert.Contains("正在发现外参文件", output[^1]);
        progress.Scan(1, 3, "D:\\项目\\A.dwg");
        Assert.Equal("扫描文件 1/3 A.dwg ...", output[^1]);
        clock.Advance(TimeSpan.FromSeconds(2)); progress.Pulse();
        Assert.Equal(3, output.Count);
        progress.Compare(1, 8, "Valve");
        clock.Advance(TimeSpan.FromSeconds(3)); progress.Pulse();
        Assert.Equal("正在对比块 1/8 Valve ...", output[^1]);
        Assert.Equal(output[^2], output[^1]);
    }

    /// <summary>命令行采用符合／不符合，实际三列 CSV 保留匹配／不匹配；输出前已经刷新。</summary>
    [Theory]
    [InlineData(XrefBlockComparisonStatus.Consistent, true, "符合", "匹配")]
    [InlineData(XrefBlockComparisonStatus.Different, true, "不符合", "不匹配")]
    [InlineData(XrefBlockComparisonStatus.Different, false, "不符合", "不匹配")]
    [InlineData(XrefBlockComparisonStatus.Incomplete, false, "未验证", "未验证")]
    [InlineData(XrefBlockComparisonStatus.ReadError, false, "未验证", "未验证")]
    [InlineData(XrefBlockComparisonStatus.NotExecuted, false, "未验证", "未验证")]
    public void ResultIsPrintedOnlyAfterCsvHasBeenFlushed(XrefBlockComparisonStatus status, bool complete,
        string console, string csv)
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var entries = new[] { Entry(1), Entry(2) };
        var result = new XrefBlockComparisonResult("Valve", status, complete, entries, [], [], []);
        var output = new List<string>(); var clock = new XrefTestTimeProvider();
        var progress = new XrefBlockProgress(message =>
        {
            Assert.Equal("块名称,dwgNo,结果\r\nValve,1/2," + csv + "\r\n", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
            output.Add(message);
        }, clock);
        progress.Complete(result, journal.AppendComparison);
        Assert.StartsWith("块 Valve " + console, Assert.Single(output));
        clock.Advance(TimeSpan.FromSeconds(4)); progress.Pulse();
        Assert.Single(output);
    }

    /// <summary>报告写入失败不打印完成行，也不会让调度层登记该候选已完成。</summary>
    [Fact]
    public void OutputFailureDoesNotPrintAResult()
    {
        var output = new List<string>(); var progress = new XrefBlockProgress(output.Add);
        var result = new XrefBlockComparisonResult("Valve", XrefBlockComparisonStatus.Consistent, true, [], [], [], []);
        Assert.Throws<XrefBlockLogException>(() => progress.Complete(result, _ => throw new XrefBlockLogException("磁盘故障")));
        Assert.Empty(output);
    }

    /// <summary>两次日志路径展示均先输出原句，提醒不会被三秒心跳重复。</summary>
    [Fact]
    public void ReminderPrecedesInitialAndFinalPaths()
    {
        var output = new List<string>(); var clock = new XrefTestTimeProvider();
        var progress = new XrefBlockProgress(output.Add, clock);
        progress.Paths("日志：开始", "来源索引：开始");
        progress.Compare(1, 1, "Valve");
        progress.Paths("日志：结束", "比较报告：结束");
        clock.Advance(TimeSpan.FromSeconds(10)); progress.Pulse();
        Assert.Equal(2, output.Count(s => s == XrefBlockProgress.Reminder));
        Assert.Equal("文件较多或块较多时，耗时可能比较长，长按ESC键中断处理过程", output[0]);
        Assert.Equal(XrefBlockProgress.Reminder, output[output.IndexOf("日志：结束") - 1]);
        Assert.Equal("比较报告：结束", output[^1]);
    }

    /// <summary>中断后剩余候选即时显示未验证，完成行只出现一次，不追加 SDK 内容读取。</summary>
    [Fact]
    public void RemainingCandidatesPrintUnverifiedWithoutReading()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var output = new List<string>(); var progress = new XrefBlockProgress(output.Add);
        var a = new XrefBlockCandidate("A", [Entry(1, "A"), Entry(2, "A")]);
        var b = new XrefBlockCandidate("B", [Entry(1, "B"), Entry(2, "B")]);
        bool stop = false; int reads = 0; var completed = new List<XrefBlockComparisonResult>();
        var content = new BlockContentSnapshot("root", new Dictionary<string, BlockContentNode>
        { ["root"] = new("root", "块定义", new Dictionary<string, string>(), []) }, []);
        Assert.Throws<OperationCanceledException>(() => XrefBlockComparisonBatch.Run([a, b],
            entry => { reads++; return new(entry, content); }, () => true,
            () => { if (stop) throw new OperationCanceledException(); },
            result => { progress.Complete(result, journal.AppendComparison); stop = true; }, completed, progress.Compare));
        foreach (var pending in XrefBlockComparisonBatch.Remaining([a, b], completed, "用户取消"))
            progress.Complete(pending, journal.AppendComparison);
        Assert.Equal(2, reads);
        Assert.Equal(1, output.Count(s => s == "块 A 符合"));
        Assert.Contains("块 B 未验证", output);
        Assert.Equal("块名称,dwgNo,结果\r\nA,1/2,匹配\r\nB,1/2,未验证\r\n",
            XrefNameCsvTests.ReadLive(journal.ComparisonPath));
    }

    /// <summary>构造带真实来源编号的离线名称条目。</summary>
    private static XrefBlockNameEntry Entry(int number, string name = "Valve") =>
        new(name, new("来源" + number, "图纸" + number + ".dwg", "替身数据", number == 1, [], DwgNo: number), false);
    /// <summary>只移除本用例创建的临时目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
