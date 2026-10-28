using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>独立于内容读取方式验证三列报告、分组、来源和支持边界。</summary>
public sealed class XrefReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.Report", Guid.NewGuid().ToString("N"));
    private static XrefBlockNameEntry Entry(int no) =>
        new("Valve", new("source" + no, "图纸" + no + ".dwg", "fixture", no == 1, [], DwgNo: no), false);
    private static XrefBlockReportResult Result(XrefBlockComparisonStatus status, bool complete = true, string name = "Valve",
        IReadOnlyList<XrefBlockNameEntry>? entries = null) => new(name, status, complete, entries ?? [Entry(1), Entry(2)],
            [new(1, [Entry(1)]), new(2, [Entry(2)])], ["颜色：红 → 绿"], complete ? [] : ["来源3无法绘制"]);

    /// <summary>每块一行，编号去重数值排序，名称统计与详细报告保持独立。</summary>
    [Fact]
    public void ReportKeepsSchemaBomGroupingAndOwnership()
    {
        var files = new RunLogFiles();
        using (var journal = new XrefBlockJournal(_root, "A.dwg", files))
        {
            var result = Result(XrefBlockComparisonStatus.Different, entries: [Entry(10),Entry(2),Entry(1),Entry(2)]);
            var inventory = new XrefBlockInventory(result.Entries, result.Entries.Select(e => e.Source).ToArray(), true);
            var statistics = XrefBlockNames.Summarize(inventory);
            journal.WriteStatistics(inventory, statistics);
            string names = XrefNameCsvTests.ReadLive(journal.NormalPath);
            journal.AppendComparison(result); journal.CompleteVisual(new(inventory), statistics, [result]);
            Assert.Equal(names, XrefNameCsvTests.ReadLive(journal.NormalPath));
            Assert.Equal("块名称,dwgNo,结果\r\nValve,1/2/10,不匹配\r\n", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
            string log = XrefNameCsvTests.ReadLive(journal.LogPath);
            Assert.Contains("颜色", log); Assert.Contains("内容组2", log); Assert.Contains("图纸2.dwg", log);
            Assert.False(File.Exists(Path.Combine(_root, "NormalBlockComparison.csv")));
        }
        Assert.Equal(new byte[] {0xEF,0xBB,0xBF}, File.ReadAllBytes(Path.Combine(_root, "Result.csv")).Take(3));
        files.Seal(); Assert.Equal(5, files.Delete().Deleted);
    }

    /// <summary>未知来源不掩盖已确认差异，未知详情只保留在LOG中。</summary>
    [Fact]
    public void KnownDifferenceAndUnknownSourcePreserveMismatch()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var result = Result(XrefBlockComparisonStatus.Different, false);
        journal.AppendComparison(result);
        Assert.Contains("Valve,1/2,不匹配", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
        Assert.Contains("来源3无法绘制", XrefNameCsvTests.ReadLive(journal.LogPath));
        Assert.True(result.HasIssues);
    }

    /// <summary>名称中的逗号、引号和换行按RFC4180保留。</summary>
    [Fact]
    public void CsvEscapesNames()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        journal.AppendComparison(Result(XrefBlockComparisonStatus.Incomplete, false, "阀,\"A\"\nB"));
        Assert.Equal("块名称,dwgNo,结果\r\n\"阀,\"\"A\"\"\nB\",1/2,未验证\r\n", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
    }

    /// <summary>详细状态只能映射成三种最终结果。</summary>
    [Theory]
    [InlineData(XrefBlockComparisonStatus.Consistent, true, "匹配")]
    [InlineData(XrefBlockComparisonStatus.Consistent, false, "未验证")]
    [InlineData(XrefBlockComparisonStatus.Different, true, "不匹配")]
    [InlineData(XrefBlockComparisonStatus.Different, false, "不匹配")]
    [InlineData(XrefBlockComparisonStatus.Incomplete, false, "未验证")]
    [InlineData(XrefBlockComparisonStatus.ReadError, false, "未验证")]
    [InlineData(XrefBlockComparisonStatus.NotExecuted, false, "未验证")]
    public void OnlyThreeLabels(XrefBlockComparisonStatus state, bool complete, string text) =>
        Assert.Equal(text, Result(state, complete).ResultText);

    /// <summary>结果通道失败立即阻止后续候选，已完成登记不得伪造。</summary>
    [Fact]
    public void ResultOutputFailureIsFatal()
    {
        var streams = new Dictionary<string, XrefFaultStream>();
        var files = new RunLogFiles();
        var journal = new XrefBlockJournal(_root, "A.dwg", files, new XrefTestTimeProvider(),
            createReportFile: path => streams[Path.GetFileName(path)] = new(files.CreateNew(path)));
        streams["Result.csv"].FailWrites = true;
        try
        {
            Assert.Throws<XrefBlockLogException>(() => journal.AppendComparison(Result(XrefBlockComparisonStatus.Different)));
            Assert.True(journal.HasWriteFailure);
            Assert.Throws<XrefBlockLogException>(() => journal.AppendComparison(Result(XrefBlockComparisonStatus.Consistent)));
        }
        finally { streams["Result.csv"].FailWrites = false; journal.Dispose(); }
    }

    /// <summary>无候选时只有表头，不产生伪造结果。</summary>
    [Fact]
    public void NoCandidateHasOnlyHeader()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        Assert.Equal("块名称,dwgNo,结果\r\n", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
    }

    /// <summary>实际适配层调用同一范围策略，二维类可尝试读取，三维、代理和未知类拒绝完整判定。</summary>
    [Theory]
    [InlineData("AcDbLine", true)]
    [InlineData("AcDbCircle", true)]
    [InlineData("AcDbPolyline", true)]
    [InlineData("AcDb2dPolyline", true)]
    [InlineData("AcDbSpline", true)]
    [InlineData("AcDbText", true)]
    [InlineData("AcDbMText", true)]
    [InlineData("AcDbAttributeDefinition", true)]
    [InlineData("AcDbHatch", true)]
    [InlineData("AcDbAlignedDimension", true)]
    [InlineData("AcDbBlockReference", true)]
    [InlineData("AcDb3dSolid", false)]
    [InlineData("AcDbRegion", false)]
    [InlineData("AcDbSurface", false)]
    [InlineData("AcDb3dPolyline", false)]
    [InlineData("AcDbSubDMesh", false)]
    [InlineData("AcDbProxyEntity", false)]
    [InlineData("UnknownCustomEntity", false)]
    public void EntityScopeUsesActualSdkClassName(string rxClass, bool supported) =>
        Assert.Equal(supported, XrefBlockContentScope.SupportsEntity(rxClass));


    /// <summary>只清理本测试创建的目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
