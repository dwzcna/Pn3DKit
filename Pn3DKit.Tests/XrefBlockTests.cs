using System.Globalization;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证名称统计、来源编号与报告，不保留已经移除的内容比较测试。</summary>
public sealed class XrefBlockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.XrefStatistics", Guid.NewGuid().ToString("N"));

    /// <summary>构造纯内存来源，编号由用例显式指定，不依赖 CAD。</summary>
    private static XrefBlockSource Source(int number, params string[] chains) =>
        new("来源" + number, "图纸" + number + ".dwg", "宿主当前数据", number == 1, chains, DwgNo: number);
    /// <summary>使用实参构造名称清单，保留重复条目以验证来源去重。</summary>
    private static XrefBlockInventory Inventory(params XrefBlockNameEntry[] entries) =>
        new(entries, entries.Select(e => e.Source).DistinctBy(s => s.DwgNo).ToArray(), true);

    /// <summary>用户要求的斜线编号、不同来源数量及单来源名称均完整输出。</summary>
    [Fact]
    public void AllNamesIncludeSingleSourcesAndDistinctDrawingCounts()
    {
        var inventory = Inventory(new XrefBlockNameEntry("xxx", Source(5), false), new("XXX", Source(2), false),
            new("xxx", Source(1), false), new("xxx", Source(5), false), new("Pump", Source(2), false));
        var statistics = XrefBlockNames.Summarize(inventory);
        var row = statistics.Normal.Single(r => r.Name == "xxx");
        Assert.Equal("1/2/5", row.DwgNo); Assert.Equal(3, row.DwgCount);
        Assert.Equal("2", statistics.Normal.Single(r => r.Name == "Pump").DwgNo);
        Assert.Empty(statistics.Anonymous);
    }

    /// <summary>按编号数值排序，名称原文取最小编号来源；多链不增加图纸数量。</summary>
    [Fact]
    public void NumericOrderAndOriginalCasingAreStable()
    {
        var statistics = XrefBlockNames.Summarize(Inventory(new XrefBlockNameEntry("valve", Source(10), false),
            new("VALVE", Source(2, "A → B1", "A → B2"), false), new("Valve", Source(1), false)));
        var row = Assert.Single(statistics.Normal);
        Assert.Equal("Valve", row.Name); Assert.Equal("1/2/10", row.DwgNo); Assert.Equal(3, row.DwgCount);
    }

    /// <summary>匿名身份来自 SDK 标志，同名的两种标志分别统计，不按星号猜测。</summary>
    [Fact]
    public void AnonymousAndNormalNamesUseSdkFlag()
    {
        var statistics = XrefBlockNames.Summarize(Inventory(new XrefBlockNameEntry("*U1", Source(1), true),
            new("*u1", Source(2), true), new("*U1", Source(5), false), new("Odd", Source(1), true)));
        Assert.Equal(2, statistics.Anonymous.Count);
        Assert.Equal("1/2", statistics.Anonymous.Single(r => r.Name == "*U1").DwgNo);
        Assert.Equal("5", Assert.Single(statistics.Normal).DwgNo);
    }

    /// <summary>CAD 名称身份不随当前文化变化，行按名称稳定排列。</summary>
    [Fact]
    public void NamesUseOrdinalCaseInsensitiveIdentity()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var rows = XrefBlockNames.Summarize(Inventory(new XrefBlockNameEntry("z", Source(1), false),
                new("I", Source(1), false), new("i", Source(2), false), new("A", Source(1), false))).Normal;
            Assert.Equal(new[] { "A", "I", "z" }, rows.Select(r => r.Name));
            Assert.Equal("1/2", rows[1].DwgNo);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>没有来源编号的条目不能伪装成有效统计。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidSourceNumberIsRejected(int number) =>
        Assert.Throws<InvalidOperationException>(() => XrefBlockNames.Summarize(Inventory(new XrefBlockNameEntry("Valve", Source(number), false))));

    /// <summary>正常统计定期检查取消，停止后可以无 SDK 地完成部分数据汇总。</summary>
    [Fact]
    public void StatisticsCheckpointCanCancelAndMemoryRetryNeedsNoHost()
    {
        var inventory = Inventory(Enumerable.Range(0, 200).Select(i => new XrefBlockNameEntry("B" + i, Source(1), false)).ToArray());
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => XrefBlockNames.Summarize(inventory, () =>
        { if (++calls == 2) throw new OperationCanceledException(); }));
        Assert.Equal(200, XrefBlockNames.Summarize(inventory).Normal.Count);
    }

    /// <summary>重复名称和匿名名称不构成错误；真实停止、缺失来源及不完整范围默认保留。</summary>
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void RetentionUsesScanCompletion(bool complete, bool interrupted, bool stopped, bool issues)
    {
        var inventory = Inventory(new XrefBlockNameEntry("*U1", Source(1), true), new("*U1", Source(2), true)) with { ScopeComplete = complete };
        var result = new XrefBlockScanResult(inventory, interrupted, stopped ? "原图关闭" : null);
        Assert.Equal(issues, result.HasIssues);
    }

    /// <summary>来源自身报错同样不能把保留默认改成 N。</summary>
    [Fact]
    public void SourceReadErrorRequiresRetention()
    {
        var inventory = new XrefBlockInventory([], [Source(1) with { Error = "SDK ErrorStatus=FileNotFound" }], true);
        Assert.True(new XrefBlockScanResult(inventory).HasIssues);
    }

    /// <summary>零名称仍生成两个 BOM 表头，旧冲突 CSV 不再生成。</summary>
    [Fact]
    public void EmptyScanProducesOnlyNewReportHeaders()
    {
        var files = new RunLogFiles();
        using (var journal = new XrefBlockJournal(_root, "A.dwg", files))
        {
            var inventory = new XrefBlockInventory([], [], true);
            journal.Complete(new(inventory), XrefBlockNames.Summarize(inventory));
        }
        foreach (string name in new[] { "AnonymousBlockStatus.csv", "NormalBlockStatus.csv", "XrefBlockSources.csv" })
        {
            string path = Path.Combine(_root, name);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3));
        }
        Assert.Equal("块名称,dwgNo,dwg计数\r\n", File.ReadAllText(Path.Combine(_root, "NormalBlockStatus.csv")));
        Assert.False(File.Exists(Path.Combine(_root, "XrefBlockCheck.csv")));
        files.Seal(); Assert.Equal(5, files.Delete().Deleted);
    }

    /// <summary>统计输出遵守固定表头、RFC 4180 和 CRLF；只读查看器能立即观察完成记录。</summary>
    [Fact]
    public void StatisticsCsvUsesExactSchemaAndEscaping()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var inventory = Inventory(new XrefBlockNameEntry("中文,\"Valve\"\r\n下一行", Source(1), false),
            new("xxx", Source(5), true), new("xxx", Source(2), true), new("xxx", Source(1), true));
        journal.Complete(new(inventory), XrefBlockNames.Summarize(inventory));
        Assert.Equal("块名称,dwgNo,dwg计数\r\nxxx,1/2/5,3\r\n", XrefNameCsvTests.ReadLive(journal.AnonymousPath));
        Assert.Contains("\"中文,\"\"Valve\"\"\r\n下一行\",1,1\r\n", XrefNameCsvTests.ReadLive(journal.NormalPath));
    }

    /// <summary>源读取失败和覆盖边界保留真实日志，不虚构来源编号或名称。</summary>
    [Fact]
    public void ReadErrorsAndPartialResultsAreExplicit()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        journal.Relation(new("A", "B", "b.dwg", "A → B", true, false, "嵌套覆盖"));
        journal.Source(new("失败0", "lost.dwg", "未读取", false, ["A → lost"], "SDK ErrorStatus=FileNotFound"));
        var inventory = Inventory(new XrefBlockNameEntry("Valve", Source(1), false)) with { ScopeComplete = false };
        journal.Complete(new(inventory, true, "用户取消"), XrefBlockNames.Summarize(inventory));
        string log = XrefNameCsvTests.ReadLive(journal.LogPath);
        Assert.Contains("已中断", log); Assert.Contains("扫描不完整", log);
        Assert.Contains("嵌套覆盖", log); Assert.Contains("SDK ErrorStatus=FileNotFound", log);
        Assert.Contains("Valve,1,1", XrefNameCsvTests.ReadLive(journal.NormalPath));
    }

    /// <summary>五件固定文件冲突不覆盖原文件，也不会创建其他输出。</summary>
    [Theory]
    [InlineData("XrefBlockCheck.log")]
    [InlineData("XrefBlockSources.csv")]
    [InlineData("AnonymousBlockStatus.csv")]
    [InlineData("NormalBlockStatus.csv")]
    [InlineData("Result.csv")]
    public void ExistingReportsAreNeverOverwritten(string name)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, name), "原报告");
        Assert.Throws<IOException>(() => new XrefBlockJournal(_root, "A.dwg", new RunLogFiles()));
        Assert.Equal("原报告", File.ReadAllText(Path.Combine(_root, name)));
        Assert.Single(Directory.GetFiles(_root));
    }

    /// <summary>历史详细报告不再创建或登记，选择清理本批输出时也不能删除历史文件。</summary>
    [Fact]
    public void LegacyComparisonReportIsPreservedAndNotOwned()
    {
        Directory.CreateDirectory(_root);
        string legacy = Path.Combine(_root, "NormalBlockComparison.csv");
        File.WriteAllText(legacy, "历史报告");
        var files = new RunLogFiles();
        using (var journal = new XrefBlockJournal(_root, "A.dwg", files))
            Assert.Equal(Path.Combine(_root, "Result.csv"), journal.ComparisonPath);
        files.Seal(); Assert.Equal(5, files.Delete().Deleted);
        Assert.Equal("历史报告", File.ReadAllText(legacy));
        Assert.Single(Directory.GetFiles(_root));
    }

    /// <summary>统计或主报告写入失败停止后续文件，故障后不能再次宣称输出完成。</summary>
    [Theory]
    [InlineData("AnonymousBlockStatus.csv")]
    [InlineData("XrefBlockCheck.log")]
    public void ReportFailureIsFatalAndPreventsLaterStatistics(string failedFile)
    {
        var streams = new Dictionary<string, XrefFaultStream>();
        var files = new RunLogFiles();
        var journal = new XrefBlockJournal(_root, "A.dwg", files, new XrefTestTimeProvider(),
            createReportFile: path => streams[Path.GetFileName(path)] = new(files.CreateNew(path)));
        streams[failedFile].FailWrites = true;
        try
        {
            var inventory = Inventory(new XrefBlockNameEntry("*U1", Source(1), true), new("Valve", Source(1), false));
            Assert.Throws<XrefBlockLogException>(() => journal.Complete(new(inventory), XrefBlockNames.Summarize(inventory)));
            Assert.True(journal.HasWriteFailure);
            Assert.Equal("块名称,dwgNo,dwg计数\r\n", XrefNameCsvTests.ReadLive(journal.NormalPath));
            Assert.Throws<XrefBlockLogException>(() => journal.Complete(new(inventory), XrefBlockNames.Summarize(inventory)));
        }
        finally { streams[failedFile].FailWrites = false; journal.Dispose(); }
    }

    /// <summary>固定报告关闭失败仍关闭其余输出，并明确标记故障。</summary>
    [Fact]
    public void ReportCloseFailureClosesAllOtherFiles()
    {
        var files = new RunLogFiles(); var streams = new Dictionary<string, XrefFaultStream>();
        var journal = new XrefBlockJournal(_root, "A.dwg", files, new XrefTestTimeProvider(),
            createReportFile: path => streams[Path.GetFileName(path)] = new(files.CreateNew(path)));
        streams["NormalBlockStatus.csv"].FailClose = true;
        Assert.Throws<XrefBlockLogException>(journal.Dispose);
        Assert.True(journal.HasWriteFailure); Assert.All(streams.Values, stream => Assert.True(stream.Closed));
        files.Seal(); Assert.Null(files.ProtectionReason);
    }

    /// <summary>目录取消不创建目录，建议路径仍按统一文档根和序号生成。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DirectoryConfirmationAndCancelCreateNothing(int fileDia)
    {
        string suggested = RunPaths.Suggest(_root, RunLogKind.XrefBlockCheck, new DateTime(2026, 10, 26, 12, 0, 0));
        Assert.Equal(Path.Combine(_root, "Pn3DKit", "XrefBlockCheck", "261026120000"), suggested);
        int dialogs = 0;
        Assert.Equal(suggested, OutputDirectorySelection.Select(fileDia, suggested, _ => { dialogs++; return suggested; },
            _ => "", _ => { }, XrefBlockJournal.Validate));
        Assert.Equal(0, dialogs); Assert.False(Directory.Exists(_root));
        Assert.Null(OutputDirectorySelection.Select(fileDia, suggested, _ => null, _ => null, _ => { }, XrefBlockJournal.Validate));
        if (fileDia == 1)
        {
            Assert.Equal(suggested, OutputDirectorySelection.Select(fileDia, suggested,
                _ => { dialogs++; return suggested; }, _ => "C", _ => { }, XrefBlockJournal.Validate));
            Assert.Equal(1, dialogs);
        }
        Assert.False(Directory.Exists(_root));
        Directory.CreateDirectory(suggested);
        Assert.Equal(suggested + "_01", RunPaths.Suggest(_root, RunLogKind.XrefBlockCheck, new DateTime(2026, 10, 26, 12, 0, 0)));
    }

    /// <summary>只删除本测试建立的临时目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
