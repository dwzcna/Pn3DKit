using System.Diagnostics;
using System.Text.Json;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Xunit.Abstractions;

namespace Pn3DKit.Tests;

/// <summary>验证真实名称预筛选策略和按需读取调用次数；纯快照不加载 Autodesk。</summary>
public sealed class XrefSelectionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.XrefSelection", Guid.NewGuid().ToString("N"));

    /// <summary>不同 Key 代表不同数据库状态；文件名相同也不合并。</summary>
    private static XrefBlockSource Source(string key, bool host = false, string? file = null, string[]? chains = null) =>
        new(key, file ?? key + ".dwg", "离线快照", host, chains ?? [key]);

    /// <summary>轻量条目没有内容，不以空快照表示尚未读取。</summary>
    private static XrefBlockNameEntry Entry(string name, XrefBlockSource source, bool anonymous = false) =>
        new(source.Key + "/" + name, name, source, anonymous);

    /// <summary>仅使用已收集的来源和名称生成候选。</summary>
    private static XrefBlockSelection Select(params XrefBlockNameEntry[] entries) =>
        XrefBlockBatch.Select(new(entries, entries.Select(e => e.Source).DistinctBy(s => s.Key).ToArray(), true), () => { });

    /// <summary>显式构造测试图；内部匿名节点的字段仍属于父定义内容。</summary>
    private static XrefBlockDefinition Definition(XrefBlockNameEntry entry, string value = "10", bool nested = false)
    {
        var nodes = new Dictionary<string, BlockContentNode>
        {
            ["root"] = new("root", "命名块", new Dictionary<string, string>(),
                nested ? [new("内部匿名依赖", "child")] : [new("实体", "child")]),
            ["child"] = new("child", nested ? "匿名子块内容" : "圆",
                new Dictionary<string, string> { ["半径"] = value }, [])
        };
        return new(entry.Name, entry.Source, new("root", nodes, []), 0, false);
    }

    /// <summary>独立来源内名称无论多少均不请求内容，进度为零候选。</summary>
    [Fact]
    public void UniqueNamesNeverCallReader()
    {
        var source = Source("A", true);
        var selection = Select(Enumerable.Range(0, 2000).Select(i => Entry("Unique" + i, source)).ToArray());
        var progress = new List<(int, int)>();
        var result = XrefBlockBatch.Compare(selection, _ => throw new InvalidOperationException("不应读取"),
            () => true, () => { }, _ => throw new InvalidOperationException("不应逐块写日志"),
            (done, total) => progress.Add((done, total)));
        Assert.Empty(result.Results); Assert.Equal(0, result.Total);
        Assert.Equal(2000, selection.UniqueNames); Assert.False(result.HasIssues);
        Assert.Equal(0, result.Metrics!.DefinitionRequests);
        Assert.Contains((0, 0), progress);
    }

    /// <summary>同源多链和重复清单条目不制造跨图纸候选。</summary>
    [Fact]
    public void AliasChainsDoNotCreateDuplicates()
    {
        var source = Source("A", chains: ["主图 → B1", "主图 → B2"]);
        var entry = Entry("Valve", source);
        var selection = Select(entry, entry with { Key = "别名视图" });
        Assert.Empty(selection.Candidates);
        Assert.Equal(1, Assert.Single(selection.Sources).UniqueNames);
    }

    /// <summary>名称按 CAD 大小写不敏感规则匹配，候选及主图代表顺序稳定。</summary>
    [Fact]
    public void CaseInsensitiveCandidatesPreferHostName()
    {
        var main = Entry("Valve", Source("host", true));
        var child = Entry("vALVE", Source("child"));
        var first = Select(child, main); var second = Select(main, child);
        Assert.Equal("Valve", Assert.Single(first.Candidates).Name);
        Assert.Equal(first.Candidates.Select(c => c.Name), second.Candidates.Select(c => c.Name));
        Assert.Equal(first.Candidates[0].Entries.Select(e => e.Key), second.Candidates[0].Entries.Select(e => e.Key));
        Assert.Equal(0, first.UniqueNames);
    }

    /// <summary>同一文件路径的两个宿主状态保留为不同来源。</summary>
    [Fact]
    public void SamePathDifferentDatabaseKeysRemainCandidates()
    {
        var selection = Select(Entry("Valve", Source("memory1", file: "B.dwg")),
            Entry("Valve", Source("memory2", file: "B.dwg")));
        Assert.Equal(2, Assert.Single(selection.Candidates).Entries.Count);
    }

    /// <summary>独立匿名根只报告名称，不能形成内容组、已确认冲突或正常保留默认值。</summary>
    [Fact]
    public void AnonymousDuplicatesAreSeparateUncomparedResults()
    {
        var selection = Select(Entry("*U12", Source("A", true), true),
            Entry("*u12", Source("B", chains: ["A → B", "A → C → B"]), true));
        var batch = XrefBlockBatch.Compare(selection, _ => throw new InvalidOperationException("匿名根不读取"),
            () => true, () => { }, _ => { }, (_, _) => { });
        var result = Assert.Single(batch.Results);
        Assert.Equal(XrefBlockStatus.AnonymousSkipped, result.Status);
        Assert.Equal("匿名块同名，未比较", result.StatusText);
        Assert.Empty(result.Groups); Assert.Equal(2, result.Uncompared!.Count);
        Assert.Contains(result.Uncompared, x => x.Source.Chains.Count == 2);
        Assert.True(batch.HasIssues); Assert.False(result.Complete);
        Assert.True(batch.CompletedWithAnonymousHints);
        Assert.Equal(0, batch.Metrics!.DefinitionRequests);
    }

    /// <summary>匿名名称只在一个来源时同样汇总跳过，没有伪造差异。</summary>
    [Fact]
    public void UniqueAnonymousNameIsOnlyCounted()
    {
        var selection = Select(Entry("*U12", Source("A"), true));
        Assert.Empty(selection.Candidates); Assert.Equal(1, selection.UniqueNames);
    }

    /// <summary>只读少数命名候选；唯一名称和独立匿名提示均不触发回调。</summary>
    [Fact]
    public void ReadsOnlyNamedCandidates()
    {
        var a = Source("A", true); var b = Source("B");
        var selection = Select(Entry("Valve", a), Entry("valve", b), Entry("Unique", a),
            Entry("*U1", a, true), Entry("*U1", b, true));
        var calls = new List<string>();
        var result = XrefBlockBatch.Compare(selection, entry =>
            { calls.Add(entry.Key); return Definition(entry); }, () => true, () => { }, _ => { }, (_, _) => { });
        Assert.Equal(2, calls.Count); Assert.All(calls, key => Assert.Contains("alve", key));
        Assert.Contains(result.Results, r => r.Status == XrefBlockStatus.Consistent);
        Assert.Equal(1, selection.UniqueNames); Assert.Equal(2, result.Total);
    }

    /// <summary>父定义内部匿名节点的差异不会因跳过匿名根而被丢弃。</summary>
    [Fact]
    public void NamedParentsStillCompareAnonymousDependencies()
    {
        var selection = Select(Entry("Valve", Source("A", true)), Entry("Valve", Source("B")));
        var result = XrefBlockBatch.Compare(selection,
            entry => Definition(entry, entry.Source.IsHost ? "10" : "12", true),
            () => true, () => { }, _ => { }, (_, _) => { });
        var item = Assert.Single(result.Results);
        Assert.Equal(XrefBlockStatus.Conflict, item.Status);
        Assert.Contains(item.Differences, text => text.Contains("半径"));
    }

    /// <summary>源范围缺失时零候选不代表全范围正常。</summary>
    [Fact]
    public void IncompleteScopeWithOnlyUniqueNamesRetainsLogs()
    {
        var source = Source("A");
        var selection = XrefBlockBatch.Select(new([Entry("Valve", source)],
            [source, Source("missing") with { Error = "文件缺失" }], false), () => { });
        var result = XrefBlockBatch.Compare(selection, _ => throw new InvalidOperationException(),
            () => false, () => { }, _ => { }, (_, _) => { });
        Assert.Empty(result.Results); Assert.True(result.HasIssues);
        Assert.Equal(0, selection.Sources.Single(x => x.Source.Key == "missing").Names);
    }

    /// <summary>读取期间范围变为不完整，既有内容相同也不能报一致。</summary>
    [Fact]
    public void ScopeInvalidationDuringReadIsNotConsistent()
    {
        bool complete = true;
        var selection = Select(Entry("Valve", Source("A")), Entry("Valve", Source("B")));
        var result = XrefBlockBatch.Compare(selection, entry =>
            { complete = false; return Definition(entry); }, () => complete, () => { }, _ => { }, (_, _) => { });
        Assert.Equal(XrefBlockStatus.Incomplete, Assert.Single(result.Results).Status);
        Assert.False(result.ScopeComplete); Assert.True(result.HasIssues);
    }

    /// <summary>SDK 失败快照保留错误详情，不生成一致结果。</summary>
    [Fact]
    public void ReadErrorSnapshotPreservesSdkDetails()
    {
        var selection = Select(Entry("Valve", Source("A")), Entry("Valve", Source("B")));
        var result = XrefBlockBatch.Compare(selection, entry =>
        {
            var definition = Definition(entry);
            return entry.Source.Key == "B" ? definition with
            { Content = definition.Content with { ReadError = "SDK ErrorStatus=FileNotFound" } } : definition;
        }, () => true, () => { }, _ => { }, (_, _) => { });
        var item = Assert.Single(result.Results);
        Assert.Equal(XrefBlockStatus.ReadError, item.Status);
        Assert.Contains(item.Issues, text => text.Contains("SDK ErrorStatus=FileNotFound"));
    }

    /// <summary>候选读取中取消，不计入未完成的组，并且不继续请求内容。</summary>
    [Fact]
    public void CancelDuringCandidatePreservesCompletedCount()
    {
        int calls = 0;
        var selection = Select(Entry("Valve", Source("A")), Entry("Valve", Source("B")));
        var result = XrefBlockBatch.Compare(selection, entry =>
        {
            if (++calls == 2) throw new OperationCanceledException();
            return Definition(entry);
        }, () => true, () => { }, _ => { }, (_, _) => { });
        Assert.Empty(result.Results); Assert.Equal(2, calls);
        Assert.Equal(1, result.Total); Assert.True(result.Interrupted); Assert.True(result.HasIssues);
    }

    /// <summary>匿名结果写盘失败时停止，不能继续读取后续命名候选。</summary>
    [Fact]
    public void AnonymousReportFailureStopsFurtherReading()
    {
        var a = Source("A"); var b = Source("B");
        var selection = Select(Entry("*U1", a, true), Entry("*U1", b, true), Entry("Valve", a), Entry("Valve", b));
        var result = XrefBlockBatch.Compare(selection, _ => throw new InvalidOperationException("不应读取"),
            () => true, () => { }, _ => throw new IOException("写盘失败"), (_, _) => { });
        Assert.Empty(result.Results); Assert.Equal("写盘失败", result.StopReason);
        Assert.Equal(0, result.Metrics!.DefinitionRequests);
    }

    /// <summary>名称预筛选内部定期检测取消，不等待全部名称收集后再响应。</summary>
    [Fact]
    public void SelectionCanBeCancelled()
    {
        var source = Source("A"); int checks = 0;
        var entries = Enumerable.Range(0, 1000).Select(i => Entry("Unique" + i, source)).ToArray();
        Assert.Throws<OperationCanceledException>(() => XrefBlockBatch.Select(new(entries, [source], true), () =>
        { if (++checks == 100) throw new OperationCanceledException(); }));
    }

    /// <summary>唯一名称不出现在明细 CSV；源范围不完整时汇总采用保守措辞。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UniqueNamesAreOnlySourceSummaries(bool complete)
    {
        var source = Source("A"); var entries = new[] { Entry("SecretUnique", source) };
        var selection = XrefBlockBatch.Select(new(entries, [source], complete), () => { });
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        journal.Selection(selection);
        using var csv = new StreamReader(new FileStream(journal.CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.Equal("块名,状态,定义分组,来源图纸,引用链,使用情况,差异及错误详情\r\n", csv.ReadToEnd());
        using var log = new StreamReader(new FileStream(journal.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string text = log.ReadToEnd();
        Assert.DoesNotContain("SecretUnique", text);
        Assert.Contains(complete ? "无需比较 1" : "已读范围内未发现同名（范围不完整） 1", text);
    }

    /// <summary>匿名提示保留中文、引号及所有引用链，定义分组为空，写入立即可见。</summary>
    [Fact]
    public void AnonymousReportHasNoFabricatedContentGroups()
    {
        var selection = Select(Entry("*U1", Source("A", true), true),
            Entry("*U1", Source("B", file: "中文,\"B\".dwg", chains: ["A → B", "A\n→ C → B"]), true));
        using var journal = new XrefBlockJournal(_root, "A.dwg", new RunLogFiles());
        var batch = XrefBlockBatch.Compare(selection, _ => throw new InvalidOperationException(),
            () => true, () => { }, journal.Append, (_, _) => { });
        journal.Complete(batch);
        using var csv = new StreamReader(new FileStream(journal.CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string text = csv.ReadToEnd();
        Assert.Contains("*U1,匿名块同名，未比较,,", text);
        Assert.Contains("\"中文,\"\"B\"\".dwg\"", text); Assert.Contains("\"A\n→ C → B\"", text);
        using var log = new StreamReader(new FileStream(journal.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string logText = log.ReadToEnd();
        Assert.DoesNotContain("已确认 0 种定义", logText);
        Assert.Contains("已完成（含匿名同名未比较项）", logText);
    }

    /// <summary>记录可复现的纯策略调用数量和阶段耗时，不以耗时阈值替代正确性验证。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void LargeInventoryReadCountsScaleWithCandidates(int sharedNames)
    {
        const int sources = 20, uniquePerSource = 250, anonymousNames = 10;
        var entries = new List<XrefBlockNameEntry>(); var sourceList = new List<XrefBlockSource>();
        for (int index = 0; index < sources; index++)
        {
            var source = Source("来源" + index, index == 0); sourceList.Add(source);
            for (int i = 0; i < uniquePerSource; i++) entries.Add(Entry("Unique" + index + "_" + i, source));
            for (int i = 0; i < anonymousNames; i++) entries.Add(Entry("*U" + i, source, true));
            for (int i = 0; i < sharedNames; i++) entries.Add(Entry("Shared" + i, source));
        }
        var timer = Stopwatch.StartNew();
        var selection = XrefBlockBatch.Select(new(entries, sourceList, true), () => { });
        double selectingMs = timer.Elapsed.TotalMilliseconds;
        var result = XrefBlockBatch.Compare(selection, entry => Definition(entry),
            () => true, () => { }, _ => { }, (_, _) => { });
        Assert.Equal(sources * sharedNames, result.Metrics!.DefinitionRequests);
        Assert.Equal(sources * uniquePerSource, selection.UniqueNames);
        Assert.Equal(sharedNames + anonymousNames, result.Total);
        Assert.Equal(anonymousNames, result.Results.Count(r => r.Status == XrefBlockStatus.AnonymousSkipped));
        output.WriteLine("XREF_SELECTION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            Sources = sources, Names = entries.Count, UniqueNames = selection.UniqueNames,
            NamedCandidateGroups = sharedNames, AnonymousGroups = anonymousNames,
            DefinitionReads = result.Metrics.DefinitionRequests, SelectingMs = selectingMs,
            ReadingMs = result.Metrics.Reading.TotalMilliseconds, ComparingMs = result.Metrics.Comparing.TotalMilliseconds,
            Evidence = "纯名称策略和合成快照，非 CAD 实测"
        }));
    }

    /// <summary>仅清理本测试创建的报告目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
