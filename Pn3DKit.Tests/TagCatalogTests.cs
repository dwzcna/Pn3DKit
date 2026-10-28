using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证数据行类别发现、无图形组、SLINE 部分位号和组优先的写入策略。</summary>
public sealed class TagCatalogTests
{
    /// <summary>真实反馈的十六进制句柄必须与整数 SDK 关联正确匹配。</summary>
    [Theory]
    [InlineData(0x30836L, "30836")]
    [InlineData(0x30855L, "30855")]
    [InlineData(0xABCDEFL, "ABCDEF")]
    [InlineData(0x10L, "10")]
    public void SdkHandleMatchesCadHexadecimal(long value, string expected)
    {
        Assert.Equal(expected, TagCatalogPolicy.FormatHandle(value));
        Assert.NotEqual(value.ToString(), TagCatalogPolicy.FormatHandle(value));
    }

    /// <summary>实际派生类别不折叠，组无独立图形也通过成员纳入，同组不重复计数。</summary>
    [Fact]
    public void ConcreteClassesAndSharedGroupArePreserved()
    {
        TagCatalogRow[] rows =
        [
            new(1, "Primary", TagTargetKind.LineSegment, []),
            new(2, "Secondary", TagTargetKind.LineSegment, []),
            new(3, "BallValve", TagTargetKind.Component, []),
            new(10, "PipeLineGroup", TagTargetKind.LineGroup, [1, 2]),
            new(10, "PipeLineGroup", TagTargetKind.LineGroup, [1, 2]),
            new(11, "PipeLineGroup", TagTargetKind.LineGroup, [99]),
            new(12, "PipeLineGroup", TagTargetKind.LineGroup, [])
        ];
        var result = TagCatalogPolicy.Select(rows, new HashSet<int> { 1, 2, 3 });
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(new[] { "BallValve", "PipeLineGroup", "Primary", "Secondary" }, result.Rows.Select(x => x.ClassName));
        Assert.Empty(result.Warnings);
        Assert.Single(result.Rows, x => x.Kind == TagTargetKind.LineGroup);
    }

    /// <summary>非当前模型范围的管线不进入列表；失去组关系的管线明确报告。</summary>
    [Fact]
    public void ScopeAndMissingRelationshipAreExplicit()
    {
        TagCatalogRow[] rows =
        [
            new(1, "Primary", TagTargetKind.LineSegment, []),
            new(2, "Secondary", TagTargetKind.LineSegment, []),
            new(10, "PipeLineGroup", TagTargetKind.LineGroup, [2])
        ];
        var result = TagCatalogPolicy.Select(rows, new HashSet<int> { 1 });
        Assert.Equal(1, Assert.Single(result.Rows).RowId);
        Assert.Contains("数据行 1", Assert.Single(result.Warnings));
        Assert.DoesNotContain(result.Rows, x => x.Kind == TagTargetKind.LineGroup);
    }

    /// <summary>同一行类别不一致是数据错误，不能静默计数或猜测类别。</summary>
    [Fact]
    public void InconsistentRowIdentityIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => TagCatalogPolicy.Select(
            [new(1, "Primary", TagTargetKind.LineSegment, []), new(1, "Secondary", TagTargetKind.LineSegment, [])],
            new HashSet<int> { 1 }));

    /// <summary>空图纸不从整个项目带入无关组。</summary>
    [Fact]
    public void EmptyDrawingHasNoInventedGroups()
    {
        var result = TagCatalogPolicy.Select([new(10, "PipeLineGroup", TagTargetKind.LineGroup, [1])], new HashSet<int>());
        Assert.Empty(result.Rows);
        Assert.Empty(result.Warnings);
    }

    /// <summary>关联另一图纸的共享组仍只统计一次，保留完整成员范围。</summary>
    [Fact]
    public void SharedGroupRetainsMembersOutsideCurrentDrawing()
    {
        var result = TagCatalogPolicy.Select(
            [new(1, "Primary", TagTargetKind.LineSegment, []), new(10, "PipeLineGroup", TagTargetKind.LineGroup, [1, 200])],
            new HashSet<int> { 1 });
        Assert.Equal(new[] { 1, 200 }, Assert.Single(result.Rows, x => x.Kind == TagTargetKind.LineGroup).Members);
    }

    /// <summary>SLINE 和组允许保留可靠结果中的问号，普通元件仍阻止部分位号。</summary>
    [Theory]
    [InlineData(TagTargetKind.LineSegment, null)]
    [InlineData(TagTargetKind.LineGroup, null)]
    [InlineData(TagTargetKind.Component, "期望 Tag 含 ?，有属性为空")]
    public void PartialTagPermissionDependsOnRealTargetKind(TagTargetKind kind, string? reason) =>
        Assert.Equal(reason, TagRebuildPolicy.UnavailableReason(Candidate("1", kind)));

    /// <summary>部分许可不放开无格式、无 Tag、临时 Tag、空结果或 SDK 错误。</summary>
    [Theory]
    [InlineData(TagCheckStatus.NoFormat)]
    [InlineData(TagCheckStatus.NoTag)]
    [InlineData(TagCheckStatus.TemporaryTag)]
    [InlineData(TagCheckStatus.Error)]
    public void PartialPermissionDoesNotBypassOtherGuards(TagCheckStatus status)
    {
        var item = Candidate("1", TagTargetKind.LineGroup);
        Assert.NotNull(TagRebuildPolicy.UnavailableReason(item with { Check = item.Check with { Status = status } }));
        Assert.NotNull(TagRebuildPolicy.UnavailableReason(item with { Check = item.Check with { ExpectedTag = "" } }));
    }

    /// <summary>复现用户 SWR／SWS 空尺寸案例，保留自动编号、问号及百分号字面量。</summary>
    [Theory]
    [InlineData("SWR")]
    [InlineData("SWS")]
    public void EmptyNominalSizeUsesVerifiedFieldFallback(string service)
    {
        const string format = "#(PipeLineGroup.Service)-#(PipeLineGroup.LineNumber^@NNNNN)#(PipeLineGroup.Area)-#(PipeLineGroup.NominalSize)-不编号%%";
        string old = service + "-12514?-?-不编号%%";
        TagCheckField[] fields =
        [
            new("#(PipeLineGroup.Service)", "PipeLineGroup.Service", service, service, service, false),
            new("#(PipeLineGroup.LineNumber^@NNNNN)", "PipeLineGroup.LineNumber", "12514", "99999", "99999", true),
            new("#(PipeLineGroup.Area)", "PipeLineGroup.Area", "?", "", "?", false),
            new("#(PipeLineGroup.NominalSize)", "PipeLineGroup.NominalSize", "?", "", ".", false)
        ];
        var subject = new TagCheckSubject("PnId", "Secondary", "30836", format, old)
        { RowId = "300", Kind = TagTargetKind.LineSegment };
        var result = TagCheckPolicy.Check(subject, () => old, () => fields);
        Assert.Equal(service + "-12514?-.-不编号%%", result.ExpectedTag);
        Assert.Contains("逐字段", result.Note);
        Assert.Equal("", Assert.Single(result.Differences).CurrentValue);
        Assert.Null(TagRebuildPolicy.UnavailableReason(new("PnId:300", result)));
    }

    /// <summary>组先于成员处理；组写后成员快照变化则不盲写旧期望值。</summary>
    [Fact]
    public void GroupIsWrittenFirstAndChangedMemberIsSkipped()
    {
        var group = Candidate("10", TagTargetKind.LineGroup);
        var line = Candidate("1", TagTargetKind.LineSegment);
        line = line with { Check = line.Check with { ExpectedTag = "LINE?" } };
        var plans = TagRebuildPolicy.Prepare([line, group], _ => []);
        var host = new GroupHost();
        var result = TagRebuildBatch.Run(plans, host, new MemoryJournal(), (_, _) => { });
        Assert.Equal(new[] { "PnId:10" }, host.Writes);
        Assert.Equal(TagRebuildAction.Updated, result.Records[0].Action);
        Assert.Equal(TagRebuildAction.Skipped, result.Records[1].Action);
    }

    /// <summary>没有图形句柄的组与其他行按数据行身份独立，不合并为空句柄。</summary>
    [Fact]
    public void EmptyGroupHandlesDoNotMergeDifferentRows()
    {
        var a = Candidate("10", TagTargetKind.LineGroup);
        var b = Candidate("20", TagTargetKind.LineGroup) with
        { Check = Candidate("20", TagTargetKind.LineGroup).Check with { ExpectedTag = "OTHER?" } };
        Assert.Equal(2, TagRebuildPolicy.Prepare([a, b], _ => []).Count);
        var duplicate = TagRebuildPolicy.Prepare([a, Candidate("30", TagTargetKind.LineGroup)], _ => []);
        Assert.All(duplicate, x => Assert.Equal(TagRebuildState.Conflict, x.State));
    }

    /// <summary>构造可重建的实际行目标。</summary>
    private static TagRebuildCandidate Candidate(string id, TagTargetKind kind) =>
        new("PnId:" + id, new(new("PnId", kind == TagTargetKind.LineGroup ? "PipeLineGroup" : "Secondary",
            kind == TagTargetKind.LineGroup ? "" : "A", "#(TargetObject.Code)", "OLD")
        { RowId = id, Kind = kind }, "NEW?", TagCheckStatus.Mismatch, []));

    /// <summary>模拟组更新影响成员，不实际调用 CAD。</summary>
    private sealed class GroupHost : ITagRebuildHost
    {
        internal List<string> Writes { get; } = [];
        public void EnsureContext() { }
        public void BeginUndo() { }
        public void EndUndo() { }
        public void Regen() { }
        public string? Revalidate(TagRebuildCandidate item) =>
            item.Check.Subject.Kind == TagTargetKind.LineSegment && Writes.Count != 0 ? "组更新后期望值变化" : null;
        public IReadOnlyList<string> FindOwners(TagRebuildCandidate item) => [];
        public string WriteAndRead(TagRebuildCandidate item) { Writes.Add(item.Key); return item.Check.ExpectedTag; }
    }

    /// <summary>接收离线写入历史。</summary>
    private sealed class MemoryJournal : ITagRebuildJournal
    {
        public void BeforeWrite(TagRebuildCandidate item) => Message(TagWriteRecovery.Format(item));
        public void Append(TagRebuildRecord record) { }
        public void Message(string message) { }
    }
}
