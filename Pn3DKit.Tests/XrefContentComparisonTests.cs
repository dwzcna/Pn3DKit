using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>使用类型化快照验证二维内容规范化、按需批次与独立报告，不冒充 CAD 实测。</summary>
public sealed class XrefContentComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.XrefComparison", Guid.NewGuid().ToString("N"));

    /// <summary>本地节点编号与语义分离，便于验证不同身份和创建顺序。</summary>
    private static BlockContentNode Node(string id, string kind = "AcDbCircle", string field = "半径", string value = "10",
        params BlockContentEdge[] edges) => new(id, kind, new Dictionary<string, string> { [field] = value }, edges);
    /// <summary>构造一个根及二维实体多重集，编号前缀仅代表本快照身份。</summary>
    private static BlockContentSnapshot Shape(string prefix = "a", string radius = "10", bool reverse = false, int count = 1)
    {
        var nodes = Enumerable.Range(0, count).Select(i => Node(prefix + i, value: radius)).ToArray();
        var root = Node(prefix, "块定义", "单位", "毫米", (reverse ? nodes.Reverse() : nodes)
            .Select(n => new BlockContentEdge("实体", n.Id)).ToArray());
        return new(prefix, nodes.Prepend(root).ToDictionary(n => n.Id), []);
    }
    /// <summary>候选的来源编号与内存名称统计使用同一套身份。</summary>
    private static XrefBlockNameEntry Entry(int number, string name = "Valve") =>
        new(name, new("来源" + number, "图纸" + number + ".dwg", "宿主当前数据", number == 1, [], DwgNo: number), false);
    /// <summary>以输入快照创建完整候选，默认使用不同来源。</summary>
    private static XrefBlockComparisonResult Compare(params BlockContentSnapshot[] snapshots)
    {
        var entries = snapshots.Select((_, i) => Entry(i + 1)).ToArray();
        return XrefBlockComparison.Group(new("Valve", entries),
            snapshots.Select((s, i) => new XrefComparedDefinition(entries[i], s)).ToArray(), true);
    }

    /// <summary>A／a1／a2 三图归组，最小来源先作为基准，避免输出全部两两比较。</summary>
    [Fact]
    public void ThreeDrawingsGroupByContent()
    {
        var result = Compare(Shape("a"), Shape("b", "12"), Shape("c"));
        Assert.Equal(XrefBlockComparisonStatus.Different, result.Status); Assert.True(result.Complete);
        Assert.Equal(new[] { 1, 3 }, result.Groups[0].Definitions.Select(d => d.Entry.Source.DwgNo));
        Assert.Equal(2, Assert.Single(result.Groups[1].Definitions).Entry.Source.DwgNo);
        Assert.Contains(result.Differences, d => d.Contains("半径") && d.Contains("10") && d.Contains("12"));
    }

    /// <summary>编号与创建顺序不参与内容；精确数值相同才可一致。</summary>
    [Fact]
    public void IdentityAndCreationOrderDoNotAffectContent()
    {
        var left = Shape("left", count: 100, reverse: false);
        var right = Shape("right", count: 100, reverse: true);
        Assert.Equal(XrefBlockComparison.Fingerprint(left), XrefBlockComparison.Fingerprint(right));
        Assert.True(XrefBlockComparison.Equivalent(left, right));
        Assert.Equal(XrefBlockComparisonStatus.Consistent, Compare(left, right).Status);
    }

    /// <summary>几何、文字、属性默认值、变换和资源等真实字段均参与比较。</summary>
    [Theory]
    [InlineData("AcDbCircle", "半径", "10", "12")]
    [InlineData("AcDbText", "文字", "V", "VALVE")]
    [InlineData("AcDbAttributeDefinition", "默认值", "V", "VALVE")]
    [InlineData("AcDbBlockReference", "变换", "0,0,0", "10,0,0")]
    [InlineData("AcDbLayerTableRecord", "颜色", "ACI1", "ACI2")]
    [InlineData("AcDbLinetypeTableRecord", "线型段长度", "1", "2")]
    [InlineData("AcDbDimStyleTableRecord", "Dimtxt", "2.5", "3")]
    public void ConcreteSemanticFieldDifferencesAreReported(string kind, string field, string old, string current)
    {
        BlockContentSnapshot Content(string id, string value) => new(id, new Dictionary<string, BlockContentNode>
            { [id] = Node(id, kind, field, value) }, []);
        var result = Compare(Content("a", old), Content("b", current));
        Assert.Equal(XrefBlockComparisonStatus.Different, result.Status);
        Assert.Contains(result.Differences, d => d.Contains(field) && d.Contains(old) && d.Contains(current));
    }

    /// <summary>不同显示名称的资源内容不能仅按资源名判相同，原始文字区分大小写。</summary>
    [Fact]
    public void SameResourceNameAndTextCaseStillRequireExactContent()
    {
        var a = Shape(); var b = Shape("b");
        var resourceA = Node("styleA", "文字样式", "资源名", "STANDARD") with { Payloads = new Dictionary<string, byte[]> { ["字体"] = [1, 2] } };
        var resourceB = Node("styleB", "文字样式", "资源名", "STANDARD") with { Payloads = new Dictionary<string, byte[]> { ["字体"] = [1, 3] } };
        a = a with { Nodes = a.Nodes.Concat(new[] { new KeyValuePair<string, BlockContentNode>(resourceA.Id, resourceA) })
            .ToDictionary(x => x.Key, x => x.Key == a.Root ? x.Value with { Edges = [new("样式", resourceA.Id)] } : x.Value) };
        b = b with { Nodes = b.Nodes.Concat(new[] { new KeyValuePair<string, BlockContentNode>(resourceB.Id, resourceB) })
            .ToDictionary(x => x.Key, x => x.Key == b.Root ? x.Value with { Edges = [new("样式", resourceB.Id)] } : x.Value) };
        Assert.False(XrefBlockComparison.Equivalent(a, b));
        Assert.Contains(Compare(a, b).Differences, d => d.Contains("资源文件内容不同"));
        Assert.Equal(XrefBlockComparisonStatus.Different, Compare(Shape(radius: "V"), Shape("b", "v")).Status);
    }

    /// <summary>重复实体数量不能在规范化中被集合去重。</summary>
    [Fact]
    public void DuplicateEntitiesRemainAMultiset()
    {
        var result = Compare(Shape(count: 1), Shape("b", count: 2));
        Assert.Equal(XrefBlockComparisonStatus.Different, result.Status);
        Assert.Contains(result.Differences, d => d.Contains("数量") && d.Contains("1") && d.Contains("2"));
    }

    /// <summary>相邻双精度值严格区分，不以显示精度或容差掩盖差异。</summary>
    [Fact]
    public void TinyGeometryDifferenceIsStrict()
    {
        string one = XrefBlockComparison.Number(10), two = XrefBlockComparison.Number(Math.BitIncrement(10));
        Assert.NotEqual(one, two);
        Assert.Equal(XrefBlockComparisonStatus.Different, Compare(Shape(radius: one), Shape("b", two)).Status);
        Assert.Throws<InvalidOperationException>(() => XrefBlockComparison.Number(double.NaN));
    }

    /// <summary>父块必须包含必要匿名子定义的实际内容；匿名编号本身不参与比较。</summary>
    [Fact]
    public void AnonymousDependencyDifferenceChangesNamedParent()
    {
        BlockContentSnapshot Nested(string prefix, string radius) => new(prefix, new Dictionary<string, BlockContentNode>
        {
            [prefix] = Node(prefix, "块定义", edges: [new("实体", prefix + "ref")]),
            [prefix + "ref"] = Node(prefix + "ref", "AcDbBlockReference", "变换", "Identity", new BlockContentEdge("引用定义", prefix + "child")),
            [prefix + "child"] = Node(prefix + "child", "匿名定义", "名称", "〈所属匿名定义〉", new BlockContentEdge("实体", prefix + "circle")),
            [prefix + "circle"] = Node(prefix + "circle", value: radius)
        }, []);
        Assert.True(XrefBlockComparison.Equivalent(Nested("A", "10"), Nested("B", "10")));
        Assert.Equal(XrefBlockComparisonStatus.Different, Compare(Nested("A", "10"), Nested("B", "12")).Status);
    }

    /// <summary>共享或复制的同内容依赖不因存储身份差异误报。</summary>
    [Fact]
    public void EquivalentDependencyAliasesAreNormalized()
    {
        var left = new BlockContentSnapshot("root", new Dictionary<string, BlockContentNode>
        {
            ["root"] = Node("root", "块", edges: [new("实体", "same"), new("实体", "same")]),
            ["same"] = Node("same")
        }, []);
        Assert.True(XrefBlockComparison.Equivalent(left, Shape("other", count: 2) with
        { Nodes = Shape("other", count: 2).Nodes.ToDictionary(p => p.Key, p => p.Key == "other" ? p.Value with
            { Kind = "块", Fields = new Dictionary<string, string> { ["半径"] = "10" } } : p.Value) }));
    }

    /// <summary>同实体的显式先后关系改变仍须判为不同。</summary>
    [Fact]
    public void DrawOrderConstraintsParticipate()
    {
        BlockContentSnapshot Ordered(string prefix, bool reverse) => new(prefix, new Dictionary<string, BlockContentNode>
        {
            [prefix] = Node(prefix, "块", edges: [new("实体", prefix+"a"), new("实体", prefix+"b"), new("绘制关系", prefix+"order")]),
            [prefix+"a"] = Node(prefix+"a", value: "10"), [prefix+"b"] = Node(prefix+"b", value: "12"),
            [prefix+"order"] = Node(prefix+"order", "绘制约束", edges:
                [new("先绘制", prefix+(reverse ? "b" : "a")), new("后绘制", prefix+(reverse ? "a" : "b"))])
        }, []);
        Assert.Equal(XrefBlockComparisonStatus.Different, Compare(Ordered("x", false), Ordered("y", true)).Status);
    }

    /// <summary>摘要相同只能筛选候选，不能让不同内容合并。</summary>
    [Fact]
    public void ForcedHashCollisionStillChecksFullContents()
    {
        var entries = new[] { Entry(1), Entry(2) };
        var result = XrefBlockComparison.Group(new("Valve", entries), [new(entries[0], Shape()), new(entries[1], Shape("b", "12"))],
            true, fingerprint: _ => "强制相同");
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(XrefBlockComparisonStatus.Different, result.Status);
    }

    /// <summary>动态／三维／代理／未解释类型没有完整快照，不能进入一致内容组。</summary>
    [Theory]
    [InlineData("动态块")]
    [InlineData("三维实体")]
    [InlineData("代理对象")]
    [InlineData("未解释字段")]
    public void UnsupportedContentsAreIncomplete(string reason)
    {
        var result = Compare(Shape(), Shape("b") with { Issues = [reason] });
        Assert.Equal(XrefBlockComparisonStatus.Incomplete, result.Status); Assert.False(result.Complete);
        Assert.Single(result.Groups); Assert.Contains(result.Problems, p => p.Contains(reason));
    }

    /// <summary>已确认两种内容时仍显示不一致，同时保留第三来源读取失败。</summary>
    [Fact]
    public void KnownDifferenceSurvivesUnknownSource()
    {
        var error = Shape("c") with { ReadError = "SDK ErrorStatus=FileNotFound" };
        var result = Compare(Shape(), Shape("b", "12"), error);
        Assert.Equal(XrefBlockComparisonStatus.Different, result.Status); Assert.False(result.Complete);
        Assert.Equal(2, result.Groups.Count);
        Assert.Contains(result.Problems, p => p.Contains("dwgNo=3") && p.Contains("SDK"));
        Assert.Equal(XrefBlockComparisonStatus.ReadError, Compare(Shape(), error).Status);
    }

    /// <summary>范围缺失时只能说明已读来源相同，不能宣告完整范围一致。</summary>
    [Fact]
    public void MissingScopePreventsConsistencyClaim()
    {
        var entries = new[] { Entry(1), Entry(2) };
        var result = XrefBlockComparison.Group(new("Valve", entries), [new(entries[0], Shape()), new(entries[1], Shape("b"))], false);
        Assert.Equal(XrefBlockComparisonStatus.Incomplete, result.Status); Assert.True(result.HasIssues);
    }

    /// <summary>循环和缺失依赖明确为无法规范化，不通过同哈希判一致。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CyclicOrMissingDependencyIsIncomplete(bool cycle)
    {
        var broken = new BlockContentSnapshot("r", new Dictionary<string, BlockContentNode>
        { ["r"] = Node("r", edges: [new("依赖", cycle ? "r" : "missing")]) }, []);
        var result = Compare(Shape(), broken);
        Assert.Equal(XrefBlockComparisonStatus.Incomplete, result.Status);
        Assert.Contains(result.Problems, p => p.Contains(cycle ? "循环" : "缺失"));
    }

    /// <summary>选择来源顺序不影响分组，内部最小 dwgNo 优先。</summary>
    [Fact]
    public void GroupNumbersFollowLowestDrawingNumber()
    {
        var entries = new[] { Entry(5), Entry(2), Entry(1) };
        var result = XrefBlockComparison.Group(new("Valve", entries),
            [new(entries[0], Shape("five")), new(entries[1], Shape("two", "12")), new(entries[2], Shape("one"))], true);
        Assert.Equal(new[] { 1, 5 }, result.Groups[0].Definitions.Select(d => d.Entry.Source.DwgNo));
        Assert.Equal(2, Assert.Single(result.Groups[1].Definitions).Entry.Source.DwgNo);
    }

    /// <summary>不吞掉主线程取消、上下文变化或报告故障。</summary>
    [Fact]
    public void CheckpointErrorsPropagateUnchanged()
    {
        var entries = new[] { Entry(1), Entry(2) };
        Assert.Throws<InvalidOperationException>(() => XrefBlockComparison.Group(new("Valve", entries),
            [new(entries[0], Shape()), new(entries[1], Shape("b"))], true, () => throw new InvalidOperationException("原图已切换")));
        Assert.Throws<OperationCanceledException>(() => XrefBlockComparison.Equivalent(Shape(), Shape("b"),
            () => throw new OperationCanceledException()));
    }

    /// <summary>首组输出后中断，已经完成的组保留，剩余只用内存记为未执行。</summary>
    [Fact]
    public void InterruptedBatchPreservesCompletedAndMarksRemaining()
    {
        var a = new XrefBlockCandidate("A", [Entry(1, "A"), Entry(2, "A")]);
        var b = new XrefBlockCandidate("B", [Entry(1, "B"), Entry(2, "B")]);
        bool stop = false; int reads = 0; var completed = new List<XrefBlockComparisonResult>();
        Assert.Throws<OperationCanceledException>(() => XrefBlockComparisonBatch.Run([a, b],
            e => { reads++; return new(e, Shape()); }, () => true,
            () => { if (stop) throw new OperationCanceledException(); }, _ => stop = true, completed));
        Assert.Single(completed); Assert.Equal(2, reads);
        Assert.Equal("B", Assert.Single(XrefBlockComparisonBatch.Remaining([a, b], completed, "已取消")).Name);
        Assert.Equal(XrefBlockComparisonStatus.NotExecuted,
            Assert.Single(XrefBlockComparisonBatch.Remaining([a, b], completed, "已取消")).Status);
    }

    /// <summary>输出故障立即停止后续候选，尚未成功落盘的组不登记为完成。</summary>
    [Fact]
    public void ReportFailureStopsBatch()
    {
        var candidate = new XrefBlockCandidate("Valve", [Entry(1), Entry(2)]);
        int reads = 0; var completed = new List<XrefBlockComparisonResult>();
        Assert.Throws<XrefBlockLogException>(() => XrefBlockComparisonBatch.Run([candidate, candidate],
            e => { reads++; return new(e, Shape()); }, () => true, () => { },
            _ => throw new XrefBlockLogException("磁盘写满"), completed));
        Assert.Equal(2, reads); Assert.Empty(completed);
    }

    /// <summary>三列报告每块一行且完成前可读取；名称统计和详细 LOG 保持独立。</summary>
    [Fact]
    public void ReportIsIndependentAndKeepsExactCsvSchema()
    {
        var files = new RunLogFiles();
        using (var journal = new XrefBlockJournal(_root, "A.dwg", files))
        {
            var result = Compare(Shape(), Shape("b", "12"));
            var inventory = new XrefBlockInventory(result.Entries, result.Entries.Select(e => e.Source).ToArray(), true);
            var statistics = XrefBlockNames.Summarize(inventory);
            journal.WriteStatistics(inventory, statistics);
            string names = XrefNameCsvTests.ReadLive(journal.NormalPath);
            journal.AppendComparison(result);
            journal.Complete(new(inventory), statistics, [result]);
            Assert.Equal(names, XrefNameCsvTests.ReadLive(journal.NormalPath));
            string csv = XrefNameCsvTests.ReadLive(journal.ComparisonPath);
            Assert.Equal("块名称,dwgNo,结果\r\nValve,1/2,不匹配\r\n", csv);
            Assert.Equal(Path.Combine(_root, "Result.csv"), journal.ComparisonPath);
            string log = XrefNameCsvTests.ReadLive(journal.LogPath);
            Assert.Contains("半径", log); Assert.Contains("内容组2", log); Assert.Contains("图纸2.dwg", log);
            Assert.False(File.Exists(Path.Combine(_root, "NormalBlockComparison.csv")));
        }
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(Path.Combine(_root, "Result.csv")).Take(3));
        files.Seal(); Assert.Equal(5, files.Delete().Deleted);
    }

    /// <summary>名称中的换行、引号和逗号正确转义；未知详情仅写 LOG，不污染三列结果。</summary>
    [Fact]
    public void ReportEscapesNamesAndKeepsUnknownDetailsInLog()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var entries = new[] { Entry(1, "V,\"A\"\r\n下一行"), Entry(2, "V,\"A\"\r\n下一行") };
        var result = XrefBlockComparison.Group(new(entries[0].Name, entries),
            [new(entries[0], Shape()), new(entries[1], Shape("b") with { Issues = ["未知\n字段"] })], true);
        journal.AppendComparison(result);
        var csv = XrefNameCsvTests.ReadLive(journal.ComparisonPath);
        Assert.Equal("块名称,dwgNo,结果\r\n\"V,\"\"A\"\"\r\n下一行\",1/2,未验证\r\n", csv);
        Assert.DoesNotContain("未知", csv);
        Assert.Contains("未知\n字段", XrefNameCsvTests.ReadLive(journal.LogPath));
    }

    /// <summary>内部五态只映射为三种报告文字；不完整的一致状态也不能伪报匹配。</summary>
    [Theory]
    [InlineData(XrefBlockComparisonStatus.Consistent, true, "匹配")]
    [InlineData(XrefBlockComparisonStatus.Consistent, false, "未验证")]
    [InlineData(XrefBlockComparisonStatus.Different, true, "不匹配")]
    [InlineData(XrefBlockComparisonStatus.Different, false, "不匹配")]
    [InlineData(XrefBlockComparisonStatus.Incomplete, false, "未验证")]
    [InlineData(XrefBlockComparisonStatus.ReadError, false, "未验证")]
    [InlineData(XrefBlockComparisonStatus.NotExecuted, false, "未验证")]
    public void ResultUsesOnlyThreeVerifiedLabels(XrefBlockComparisonStatus status, bool complete, string expected)
    {
        var result = new XrefBlockComparisonResult("Valve", status, complete, [], [], [], []);
        Assert.Equal(expected, result.ResultText);
    }

    /// <summary>一个名称的全部来源在单行中数字排序并去重，不能按文字得到 1/10/2。</summary>
    [Fact]
    public void ResultCombinesUniqueDrawingNumbersInNumericOrder()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var entries = new[] { Entry(10), Entry(2), Entry(1) };
        var result = XrefBlockComparison.Group(new("Valve", entries),
            entries.Select(entry => new XrefComparedDefinition(entry, Shape("s" + entry.Source.DwgNo))).ToArray(), true);
        journal.AppendComparison(result with { Entries = entries.Append(entries[1]).ToArray() });
        Assert.Equal("块名称,dwgNo,结果\r\nValve,1/2/10,匹配\r\n", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
    }

    /// <summary>已确认差异仍报告不匹配，另一个来源的读取失败及具体差异完整保留在 LOG。</summary>
    [Fact]
    public void ResultKeepsConfirmedMismatchAlongsideUnknownSource()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var result = Compare(Shape(), Shape("b", "12"), Shape("c") with { ReadError = "SDK ErrorStatus=FileNotFound" });
        journal.AppendComparison(result);
        Assert.False(result.Complete);
        Assert.Equal("块名称,dwgNo,结果\r\nValve,1/2/3,不匹配\r\n", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
        string log = XrefNameCsvTests.ReadLive(journal.LogPath);
        Assert.Contains("不完整", log); Assert.Contains("半径", log);
        Assert.Contains("dwgNo=3", log); Assert.Contains("SDK ErrorStatus=FileNotFound", log);
    }

    /// <summary>中断保留已落盘的匹配行，剩余候选只用内存追加未验证，不再读取内容。</summary>
    [Fact]
    public void InterruptedResultPreservesCompletedAndMarksUnverified()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var a = new XrefBlockCandidate("A", [Entry(1, "A"), Entry(2, "A")]);
        var b = new XrefBlockCandidate("B", [Entry(1, "B"), Entry(2, "B")]);
        bool stop = false; int reads = 0; var completed = new List<XrefBlockComparisonResult>();
        Assert.Throws<OperationCanceledException>(() => XrefBlockComparisonBatch.Run([a, b],
            entry => { reads++; return new(entry, Shape("s" + entry.Source.DwgNo)); }, () => true,
            () => { if (stop) throw new OperationCanceledException(); },
            result => { journal.AppendComparison(result); stop = true; }, completed));
        foreach (var pending in XrefBlockComparisonBatch.Remaining([a, b], completed, "用户取消"))
            journal.AppendComparison(pending);
        Assert.Equal(2, reads);
        Assert.Equal("块名称,dwgNo,结果\r\nA,1/2,匹配\r\nB,1/2,未验证\r\n", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
        Assert.Contains("用户取消", XrefNameCsvTests.ReadLive(journal.LogPath));
    }

    /// <summary>无跨图普通块候选时保留仅有表头的结果文件，不伪造匹配行。</summary>
    [Fact]
    public void NoCandidateResultContainsOnlyHeader()
    {
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var inventory = new XrefBlockInventory([], [], true);
        journal.Complete(new(inventory), XrefBlockNames.Summarize(inventory), []);
        Assert.Equal("块名称,dwgNo,结果\r\n", XrefNameCsvTests.ReadLive(journal.ComparisonPath));
    }

    /// <summary>比较文件写入失败阻止后续行，归属清单仍能在关闭后统一清理。</summary>
    [Fact]
    public void ComparisonStreamFailureIsFatal()
    {
        var files = new RunLogFiles(); var streams = new Dictionary<string, XrefFaultStream>();
        var journal = new XrefBlockJournal(_root, "A.dwg", files, new XrefTestTimeProvider(),
            createReportFile: path => streams[Path.GetFileName(path)] = new(files.CreateNew(path)));
        streams["Result.csv"].FailWrites = true;
        try
        {
            Assert.Throws<XrefBlockLogException>(() => journal.AppendComparison(Compare(Shape(), Shape("b", "12"))));
            Assert.True(journal.HasWriteFailure);
        }
        finally { streams["Result.csv"].FailWrites = false; journal.Dispose(); }
        files.Seal(); Assert.Null(files.ProtectionReason);
    }

    /// <summary>大量相同实体按内容规范化，不进行图同构的组合回溯。</summary>
    [Fact]
    public void ManyDuplicateEntitiesDoNotNeedBacktracking()
    {
        Assert.True(XrefBlockComparison.Equivalent(Shape("a", count: 2500), Shape("b", reverse: true, count: 2500)));
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

    /// <summary>不同内容实体的创建顺序变化也不能改变定义判定。</summary>
    [Fact]
    public void DistinctEntitiesCanBeCreatedInAnyOrder()
    {
        BlockContentSnapshot Content(string prefix, bool reverse)
        {
            var items = Enumerable.Range(1, 50).Select(i => Node(prefix + i, value: i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            var root = Node(prefix, "块", edges: (reverse ? items.Reverse() : items).Select(item => new BlockContentEdge("实体", item.Id)).ToArray());
            return new(prefix, items.Prepend(root).ToDictionary(item => item.Id), []);
        }
        var a = Content("a", false); var b = Content("b", true);
        Assert.Equal(XrefBlockComparison.Fingerprint(a), XrefBlockComparison.Fingerprint(b));
        Assert.True(XrefBlockComparison.Equivalent(a, b));
    }

    /// <summary>只清理本用例创建的临时目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
