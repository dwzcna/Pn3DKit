using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>离线检查内容归组、严格图等价、外参范围和报告；不加载 Autodesk 或操作 DWG。</summary>
public sealed class XrefBlockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.XrefBlockTests", Guid.NewGuid().ToString("N"));

    /// <summary>构造无身份依赖的命名定义，默认包含一个圆及属性定义。</summary>
    private static XrefBlockDefinition Definition(string file, string radius = "10", string value = "V",
        bool host = false, string name = "Valve", string[]? issues = null, bool used = true, string? error = null,
        string[]? chains = null)
    {
        var graph = Graph("root",
            Node("root", "块", [("基点", "0,0,0"), ("单位", "Millimeters")], [new("实体", "circle"), new("实体", "att")]),
            Node("circle", "圆", [("半径", radius)]),
            Node("att", "属性定义", [("属性名", "TAG"), ("默认值", value)]));
        return new(name, new(file, file, host ? "宿主当前数据" : "磁盘只读数据", host, chains ?? [file]),
            graph with { Issues = issues ?? [], ReadError = error }, 1, used);
    }

    /// <summary>构造精确字段，重复边不去重。</summary>
    private static BlockContentNode Node(string id, string kind, (string, string)[]? fields = null, BlockContentEdge[]? edges = null) =>
        new(id, kind, (fields ?? []).ToDictionary(x => x.Item1, x => x.Item2), edges ?? []);

    /// <summary>图中 Id 只用于关联，便于验证重编号、循环和共享关系。</summary>
    private static BlockContentSnapshot Graph(string root, params BlockContentNode[] nodes) =>
        new(root, nodes.ToDictionary(n => n.Id), []);

    /// <summary>A／a1／a2 三来源按两种内容分组，主图和 a2 相同，a1 不同。</summary>
    [Fact]
    public void ThreeDrawingCaseGroupsEqualDefinitionsAndNamesDifferences()
    {
        var result = XrefBlockComparison.Compare("Valve",
            [Definition("a1.dwg", "12", "VALVE"), Definition("a2.dwg"), Definition("A.dwg", host: true)], true);
        Assert.Equal(XrefBlockStatus.Conflict, result.Status);
        Assert.True(result.Complete);
        Assert.Equal(new[] { "A.dwg", "a2.dwg" }, result.Groups[0].Definitions.Select(d => d.Source.Drawing));
        Assert.Equal("a1.dwg", Assert.Single(result.Groups[1].Definitions).Source.Drawing);
        Assert.Contains(result.Differences, d => d.Contains("半径") && d.Contains("10") && d.Contains("12"));
        Assert.Contains(result.Differences, d => d.Contains("默认值") && d.Contains("VALVE"));
        Assert.Contains("复用主图定义", result.Risk);
    }

    /// <summary>主图没有该块，外参冲突也必须显示，提示绑定顺序风险。</summary>
    [Fact]
    public void ReferencesConflictWithoutHostDefinition()
    {
        var result = XrefBlockComparison.Compare("Valve", [Definition("a1.dwg"), Definition("a2.dwg", "12")], true);
        Assert.Equal(XrefBlockStatus.Conflict, result.Status);
        Assert.Contains("绑定顺序", result.Risk);
    }

    /// <summary>未插入的主图定义仍参与冲突，使用状态不改变内容。</summary>
    [Fact]
    public void UnusedHostDefinitionStillParticipates()
    {
        var unused = Definition("A.dwg", host: true, used: false) with { DirectReferences = 0 };
        var result = XrefBlockComparison.Compare("Valve", [unused, Definition("a1.dwg", value: "DIFFERENT")], true);
        Assert.Equal(XrefBlockStatus.Conflict, result.Status);
        Assert.Contains("未使用定义", result.Groups[0].Definitions[0].Usage);
    }

    /// <summary>只有一个完整来源属于无需比较，多个完整相同来源属于一致。</summary>
    [Theory]
    [InlineData(1, XrefBlockStatus.Unique)]
    [InlineData(3, XrefBlockStatus.Consistent)]
    public void UniqueAndConsistentAreDistinct(int count, XrefBlockStatus status)
    {
        var result = XrefBlockComparison.Compare("Valve", Enumerable.Range(0, count).Select(i => Definition(i + ".dwg")).ToArray(), true);
        Assert.Equal(status, result.Status); Assert.True(result.Complete);
        Assert.Single(result.Groups);
    }

    /// <summary>已知冲突优先，同时保留第三来源未完整的事实，不能归入一致组。</summary>
    [Fact]
    public void KnownConflictAndUnknownSourceRemainIndependent()
    {
        var result = XrefBlockComparison.Compare("Valve",
            [Definition("A.dwg", host: true), Definition("a1.dwg", "12"), Definition("proxy.dwg", issues: ["代理 AcDbProxyEntity"])], true);
        Assert.Equal(XrefBlockStatus.Conflict, result.Status);
        Assert.False(result.Complete); Assert.Equal(3, result.Groups.Count);
        Assert.StartsWith("未完整", result.Groups[^1].Number);
        Assert.Contains(result.Issues, s => s.Contains("代理"));
    }

    /// <summary>不完整、读取错误和范围缺失都不能判为一致或无需比较。</summary>
    [Theory]
    [InlineData(false, false, false, XrefBlockStatus.Incomplete)]
    [InlineData(true, true, false, XrefBlockStatus.Incomplete)]
    [InlineData(true, false, true, XrefBlockStatus.ReadError)]
    public void UnknownDoesNotMeanEqual(bool scope, bool issue, bool error, XrefBlockStatus status)
    {
        var item = Definition("a.dwg", issues: issue ? ["动态节点参数未解释"] : [], error: error ? "SDK ErrorStatus=FileNotFound" : null);
        var result = XrefBlockComparison.Compare("Valve", [item], scope);
        Assert.Equal(status, result.Status); Assert.False(result.Complete);
    }

    /// <summary>主图与外参的文件身份、节点编号和实体创建顺序均不构成内容差异。</summary>
    [Fact]
    public void NodeIdsAndCreationOrderDoNotAffectEquality()
    {
        var a = Definition("A.dwg").Content;
        var b = Graph("b", Node("p", "属性定义", [("属性名", "TAG"), ("默认值", "V")]),
            Node("c", "圆", [("半径", "10")]),
            Node("b", "块", [("单位", "Millimeters"), ("基点", "0,0,0")], [new("实体", "p"), new("实体", "c")]));
        Assert.True(XrefBlockComparison.Equivalent(a, b));
        Assert.Equal(XrefBlockComparison.Fingerprint(a), XrefBlockComparison.Fingerprint(b));
    }

    /// <summary>即使哈希碰撞，内容不同仍会分成两个组。</summary>
    [Fact]
    public void FingerprintCollisionNeverProvesEquality()
    {
        var result = XrefBlockComparison.Compare("Valve",
            [Definition("A.dwg"), Definition("b.dwg", "12")], true, fingerprint: _ => "故意碰撞");
        Assert.Equal(XrefBlockStatus.Conflict, result.Status);
        Assert.Equal(2, result.Groups.Count);
    }

    /// <summary>重复实体数量必须保留，不能把多重集当集合。</summary>
    [Fact]
    public void DuplicateEntitiesRemainSignificant()
    {
        var a = Graph("r", Node("r", "块", edges: [new("实体", "a"), new("实体", "a")]), Node("a", "圆"));
        var b = Graph("r", Node("r", "块", edges: [new("实体", "a")]), Node("a", "圆"));
        Assert.False(XrefBlockComparison.Equivalent(a, b));
    }

    /// <summary>父块名称和元数据相同，嵌套子块、资源或属性的任意变化都能传到根定义。</summary>
    [Theory]
    [InlineData("嵌套定义")]
    [InlineData("图层")]
    [InlineData("文字样式")]
    [InlineData("动态参数")]
    public void DependencyContentChangesReachParent(string relation)
    {
        var a = Graph("r", Node("r", "块", edges: [new(relation, "x")]), Node("x", relation, [("值", "1")]));
        var b = Graph("s", Node("s", "块", edges: [new(relation, "y")]), Node("y", relation, [("值", "2")]));
        Assert.False(XrefBlockComparison.Equivalent(a, b));
        Assert.Contains(XrefBlockComparison.Differences(a, b), s => s.Contains("值"));
    }

    /// <summary>动态动作、约束及三维拓扑的关联编号可变化，连接方式和精确参数必须相同。</summary>
    [Theory]
    [InlineData("动态动作")]
    [InlineData("约束节点")]
    [InlineData("三维边")]
    public void CyclicGraphsIgnoreInternalNumberingButKeepConnectivity(string kind)
    {
        var a = Graph("r", Node("r", "块", edges: [new("节点", "1"), new("节点", "2")]),
            Node("1", kind, [("参数", "H")], [new("连接", "2")]), Node("2", kind, [("参数", "V")], [new("连接", "1")]));
        var b = Graph("root", Node("root", "块", edges: [new("节点", "20"), new("节点", "10")]),
            Node("10", kind, [("参数", "H")], [new("连接", "20")]), Node("20", kind, [("参数", "V")], [new("连接", "10")]));
        Assert.True(XrefBlockComparison.Equivalent(a, b));
        var changed = b with { Nodes = b.Nodes.ToDictionary(p => p.Key,
            p => p.Key == "20" ? p.Value with { Edges = [new("连接", "20")] } : p.Value) };
        Assert.False(XrefBlockComparison.Equivalent(a, changed));
    }

    /// <summary>相同节点度数和哈希仍不能把两个三角环误判为一个六边环。</summary>
    [Fact]
    public void EqualLocalSignaturesDoNotHideDifferentTopology()
    {
        BlockContentSnapshot Cycle(bool split)
        {
            var nodes = new List<BlockContentNode> { Node("r", "块", edges: Enumerable.Range(0, 6).Select(i => new BlockContentEdge("节点", i.ToString())).ToArray()) };
            for (int i = 0; i < 6; i++)
                nodes.Add(Node(i.ToString(), "边", edges: [new("下一条", (split ? i / 3 * 3 + (i + 1) % 3 : (i + 1) % 6).ToString())]));
            return Graph("r", nodes.ToArray());
        }
        Assert.Equal(XrefBlockComparison.Fingerprint(Cycle(false)), XrefBlockComparison.Fingerprint(Cycle(true)));
        Assert.False(XrefBlockComparison.Equivalent(Cycle(false), Cycle(true)));
    }

    /// <summary>显式绘制顺序使用有序边，不能与普通实体集合一起排序抹掉。</summary>
    [Fact]
    public void DrawOrderRemainsSignificant()
    {
        var a = Graph("r", Node("r", "块", edges: [new("绘制顺序/0", "a"), new("绘制顺序/1", "b")]),
            Node("a", "实体", [("颜色", "Red")]), Node("b", "实体", [("颜色", "Blue")]));
        var b = Graph("r", Node("r", "块", edges: [new("绘制顺序/1", "a"), new("绘制顺序/0", "b")]),
            Node("a", "实体", [("颜色", "Red")]), Node("b", "实体", [("颜色", "Blue")]));
        Assert.False(XrefBlockComparison.Equivalent(a, b));
    }

    /// <summary>严格比较保留极小差异、文字大小写和文化无关的数字；零符号不影响数值。</summary>
    [Fact]
    public void GeometryIsStrictAndTextIsOrdinal()
    {
        Assert.NotEqual(XrefBlockComparison.Number(10), XrefBlockComparison.Number(Math.BitIncrement(10)));
        Assert.Equal("0", XrefBlockComparison.Number(-0.0));
        Assert.Throws<InvalidOperationException>(() => XrefBlockComparison.Number(double.NaN));
        Assert.False(XrefBlockComparison.Equivalent(Definition("a", value: "V").Content, Definition("b", value: "v").Content));
    }

    /// <summary>直接附着与覆盖均检查，嵌套仅沿附着传播。</summary>
    [Theory]
    [InlineData(0, true, true)]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(3, true, false)]
    public void OverlayBoundaryIsExplicit(int depth, bool overlay, bool expected) =>
        Assert.Equal(expected, XrefBlockComparison.IncludesReference(depth, overlay));

    /// <summary>名称分组忽略大小写，文本内容不使用文化忽略规则；同源多条链不增加定义种类。</summary>
    [Fact]
    public void NamesGroupByCadRuleAndPreserveAllChains()
    {
        var a = Definition("A.dwg", host: true, name: "Valve");
        var b = Definition("b.dwg", name: "vALVE", chains: ["A → B", "A → C → B"]);
        var results = new List<XrefBlockResult>();
        var batch = XrefBlockBatch.Compare([a, b], true, () => { }, results.Add, (_, _) => { });
        Assert.Single(batch.Results); Assert.False(batch.HasIssues);
        Assert.Equal(2, batch.Results[0].Groups[0].Definitions[1].Source.Chains.Count);
    }

    /// <summary>图中断链必须变为未完整结果，不能利用相同哈希跳过验证。</summary>
    [Fact]
    public void DanglingGraphIsIncomplete()
    {
        var broken = Definition("a") with { Content = Graph("root", Node("root", "块", edges: [new("实体", "missing")])) };
        var result = XrefBlockComparison.Compare("Valve", [broken], true);
        Assert.Equal(XrefBlockStatus.Incomplete, result.Status);
        Assert.Contains(result.Issues, s => s.Contains("未完整读取"));
    }

    /// <summary>取消和输出故障立即停止，只有成功落盘项计入已处理结果。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationAndWriteFailurePreserveCompletedResults(bool cancel)
    {
        int writes = 0;
        var items = new[] { Definition("a", name: "A"), Definition("b", name: "B"), Definition("c", name: "C") };
        var result = XrefBlockBatch.Compare(items, true, () => { }, _ =>
        {
            if (++writes == 2) { if (cancel) throw new OperationCanceledException(); throw new IOException("写盘失败"); }
        }, (_, _) => { });
        Assert.Single(result.Results); Assert.True(result.HasIssues);
        Assert.Equal(cancel, result.Interrupted); Assert.NotNull(result.StopReason); Assert.Equal(2, writes);
    }

    /// <summary>比较过程内部也响应取消，不等待整个大图完成。</summary>
    [Fact]
    public void GraphSearchPropagatesCancellation()
    {
        Assert.Throws<OperationCanceledException>(() => XrefBlockComparison.Equivalent(Definition("a").Content, Definition("b").Content,
            () => throw new OperationCanceledException()));
    }

    /// <summary>目录取消前建议路径不创建目录，重名自动递增。</summary>
    [Fact]
    public void PathsUseUnifiedDocumentsRootAndCollisionSuffix()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0);
        string suggested = RunPaths.Suggest(_root, RunLogKind.XrefBlockCheck, now);
        Assert.Equal(Path.Combine(_root, "Pn3DKit", "XrefBlockCheck", "261026120000"), suggested);
        Assert.False(Directory.Exists(suggested));
        Directory.CreateDirectory(suggested);
        Assert.Equal(suggested + "_01", RunPaths.Suggest(_root, RunLogKind.XrefBlockCheck, now));
    }

    /// <summary>CreateNew 冲突不得覆盖原文件，也不创建另一件日志。</summary>
    [Theory]
    [InlineData("XrefBlockCheck.log")]
    [InlineData("XrefBlockCheck.csv")]
    public void ExistingReportsAreNeverOverwritten(string name)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, name), "原报告");
        Assert.Throws<IOException>(() => new XrefBlockJournal(_root, "A.dwg", new RunLogFiles()));
        Assert.Equal("原报告", File.ReadAllText(Path.Combine(_root, name)));
        Assert.Single(Directory.GetFiles(_root));
    }

    /// <summary>CSV 带 BOM，来源多链逐行、中文／引号／逗号／换行按 RFC 4180 转义，完成前即可读取。</summary>
    [Fact]
    public void JournalFlushesGroupsAndErrorsWithUtf8Bom()
    {
        var files = new RunLogFiles();
        using (var journal = new XrefBlockJournal(_root, "A.dwg", files))
        {
            var definition = Definition("中文,\"a\".dwg", host: true, chains: ["A → B", "A\n→ C"]);
            journal.Relation(new("A", "B", "b.dwg", "A → B", true, false, "嵌套覆盖"));
            journal.Source(new("missing", "lost.dwg", "未读取", false, ["A → lost"], "SDK ErrorStatus=FileNotFound"));
            journal.Append(XrefBlockComparison.Compare("Valve", [definition], true));
            using var reader = new StreamReader(new FileStream(journal.CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string csv = reader.ReadToEnd();
            Assert.Contains("\"中文,\"\"a\"\".dwg\"", csv);
            Assert.Contains("\"A\n→ C\"", csv);
            Assert.Contains("SDK ErrorStatus=FileNotFound", csv);
            using var logReader = new StreamReader(new FileStream(journal.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            Assert.Contains("嵌套覆盖", logReader.ReadToEnd());
            journal.Complete(new([], false, true, "已中断"));
        }
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(Path.Combine(_root, "XrefBlockCheck.csv")).Take(3));
        Assert.Contains("已中断", File.ReadAllText(Path.Combine(_root, "XrefBlockCheck.log")));
        files.Seal();
        Assert.Equal(2, files.Delete().Deleted);
    }

    /// <summary>大型定义使用显式回溯栈，不按实体数静默截断或消耗同等深度的调用栈。</summary>
    [Fact]
    public void LargeDefinitionKeepsAllEntities()
    {
        const int count = 1800;
        BlockContentSnapshot Many(string prefix, bool reverse)
        {
            var ids = Enumerable.Range(0, count).Select(i => prefix + i).ToArray();
            var nodes = new List<BlockContentNode>
            {
                Node(prefix, "块", edges: (reverse ? ids.Reverse() : ids).Select(id => new BlockContentEdge("实体", id)).ToArray())
            };
            nodes.AddRange(Enumerable.Range(0, count).Select(i => Node(prefix + i, "圆", [("半径", i.ToString())])));
            return Graph(prefix, nodes.ToArray());
        }
        Assert.True(XrefBlockComparison.Equivalent(Many("a", false), Many("b", true)));
    }

    /// <summary>外部资源保留完整内容，不能仅凭文件名或摘要决定相等。</summary>
    [Fact]
    public void ResourcePayloadUsesExactBytes()
    {
        var a = Graph("r", Node("r", "字体") with { Payloads = new Dictionary<string, byte[]> { ["内容"] = [1, 2, 3] } });
        var b = Graph("x", Node("x", "字体") with { Payloads = new Dictionary<string, byte[]> { ["内容"] = [1, 2, 4] } });
        Assert.False(XrefBlockComparison.Equivalent(a, b));
        Assert.Contains(XrefBlockComparison.Differences(a, b), s => s.Contains("资源文件内容不同"));
    }

    /// <summary>只按已确认的命名空间恢复原名，未知前缀及嵌套关系不会被截成最后一段。</summary>
    [Theory]
    [InlineData("Valve", "X", "Valve")]
    [InlineData("X|Valve", "X", "Valve")]
    [InlineData("X|Child|Valve", "X", "Child|Valve")]
    [InlineData("Unknown|Valve", "X", null)]
    public void OriginalNameRequiresConfirmedNamespace(string stored, string source, string? expected) =>
        Assert.Equal(expected, XrefBlockComparison.RemoveSourceNamespace(stored, [source]));

    /// <summary>FILEDIA 两种流程都只在确认后使用目录，取消不创建任何文件。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DirectoryConfirmationAndCancelCreateNothing(int fileDia)
    {
        string suggested = Path.Combine(_root, "suggested");
        int dialogs = 0;
        string? result = OutputDirectorySelection.Select(fileDia, suggested,
            _ => { dialogs++; return suggested; }, _ => "", _ => { }, XrefBlockJournal.Validate);
        Assert.Equal(suggested, result); Assert.Equal(0, dialogs); Assert.False(Directory.Exists(_root));
        Assert.Null(OutputDirectorySelection.Select(fileDia, suggested, _ => throw new InvalidOperationException(),
            _ => null, _ => { }, XrefBlockJournal.Validate));
        Assert.False(Directory.Exists(_root));
        if (fileDia == 1)
        {
            Assert.Equal(suggested, OutputDirectorySelection.Select(fileDia, suggested,
                _ => { dialogs++; return suggested; }, _ => "C", _ => { }, XrefBlockJournal.Validate));
            Assert.Equal(1, dialogs);
        }
    }

    /// <summary>临时目录仅属于本测试实例。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
