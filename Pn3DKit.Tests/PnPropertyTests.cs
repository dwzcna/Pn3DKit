using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>可配置 Plant 属性表的路径、元数据权限、写前快照和分批失效离线回归。</summary>
public sealed class PnPropertyTests
{
    private static readonly PnPropertySpec[] Specs = [
        new(Guid.NewGuid(), 1, "Size"), new(Guid.NewGuid(), 1, "Tag"), new(Guid.NewGuid(), 1, "LoopNumber")];

    /// <summary>简写与完整路径一致，限定自身属性而非来源对象。</summary>
    [Theory]
    [InlineData("Size", "TargetObject.Size")]
    [InlineData("Tag", "TargetObject.Tag")]
    [InlineData("LoopNumber", "TargetObject.LoopNumber")]
    [InlineData(" TargetObject.Size ", "TargetObject.Size")]
    [InlineData("targetobject.编号", "TargetObject.编号")]
    [InlineData("_Custom1", "TargetObject._Custom1")]
    public void ShorthandNormalizesToOwnProperty(string input, string expected)
    {
        Assert.Equal(expected, PnPropertyPolicy.Normalize(input, out string reason));
        Assert.Equal("", reason);
    }

    /// <summary>不解析关系、表达式、通配符或不完整路径，避免意外改写其他对象。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("TargetObject.")]
    [InlineData("PipeLines.Size")]
    [InlineData("PipeLineGroup.Tag")]
    [InlineData("#(TargetObject.Size)")]
    [InlineData("TargetObject.Size.More")]
    [InlineData("*")]
    [InlineData("Size\nTag")]
    public void UnsupportedPathIsExplained(string input)
    {
        Assert.Null(PnPropertyPolicy.Normalize(input, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    private static PnPermissionFacts Allowed() => new(MetadataKnown: true, DrawingWritable: true,
        ProjectWritable: true, Enlisted: true, LayerLocked: false, Busy: false, SpecialAllowed: true);

    /// <summary>仅在所有事实已知且安全时允许编辑。</summary>
    [Fact]
    public void KnownWritableSelfPropertyIsAllowed() => Assert.True(PnPropertyPolicy.Permission(Allowed()).Allowed);

    /// <summary>字段标志之外的每种对象状态都能独立阻止写入。</summary>
    [Theory]
    [InlineData("column", "只读")]
    [InlineData("system", "系统")]
    [InlineData("auto", "编号")]
    [InlineData("expression", "表达式")]
    [InlineData("acquire", "获取")]
    [InlineData("drawing", "图纸")]
    [InlineData("project", "项目")]
    [InlineData("enlist", "读写")]
    [InlineData("layer", "图层")]
    [InlineData("busy", "忙碌")]

    [InlineData("special", "安全写入")]
    [InlineData("unknown", "未知规则X")]
    public void EachRestrictionIndependentlyDeniesWrite(string restriction, string reason)
    {
        var facts = Allowed();
        facts = restriction switch
        {
            "column" => facts with { FieldReadOnly = true },
            "system" => facts with { System = true },
            "auto" => facts with { Automatic = true },
            "expression" => facts with { Expression = true },
            "acquire" => facts with { Acquisition = true },
            "drawing" => facts with { DrawingWritable = false },
            "project" => facts with { ProjectWritable = false },
            "enlist" => facts with { Enlisted = false },
            "layer" => facts with { LayerLocked = true },
            "busy" => facts with { Busy = true },

            "special" => facts with { SpecialAllowed = false },
            _ => facts with { MetadataKnown = false, UnknownReason = "未知规则X" }
        };
        var permission = PnPropertyPolicy.Permission(facts);
        Assert.False(permission.Allowed);
        Assert.Contains(reason, permission.Reason);
    }

    /// <summary>没有充分权限事实时默认只读；不以 IsReadOnly=false 单独放行。</summary>
    [Fact]
    public void ColumnFlagAloneNeverGrantsPermission()
    {
        Assert.False(PnPropertyPolicy.Permission(new(FieldReadOnly: false)).Allowed);
        Assert.False(PnPropertyPolicy.Permission(Allowed() with { MetadataKnown = false }).Allowed);
    }

    /// <summary>阀门 Size 为获取属性时，即使图纸和字段本身可写也不开放。</summary>
    [Fact]
    public void AcquiredValveSizeNeverBecomesAnEditableSourceProxy()
    {
        var permission = PnPropertyPolicy.Permission(Allowed() with { Acquisition = true });
        Assert.False(permission.Allowed);
        Assert.Contains("来源对象", permission.Reason);
    }

    private static PnObjectProperties Object(string tag = "HV-1001", PnEditPermission? permission = null) =>
        new("PnId:Valve", "手动阀门", Specs.Select((s, i) => new PnPropertyCell(s,
            FastPropertyValue.Value(i == 0 ? "DN50" : i == 1 ? tag : "101"),
            permission ?? PnEditPermission.Writable)).ToArray(), "PnId:12:Valve");

    /// <summary>单选显示全部自定义字段，行身份与输入顺序保持。</summary>
    [Fact]
    public void SingleSelectionPreservesConfiguredRows()
    {
        var result = PnPropertyPolicy.Merge(Specs, [Object()]);
        Assert.Equal(3, result.Cells.Count);
        Assert.Equal("DN50", result.Cells[0].Value.Display);
        Assert.Equal(Specs[1], result.Cells[1].Spec);
        Assert.True(result.Cells[0].Permission.Allowed);
    }

    /// <summary>全部对象许可时，共同值和多种值都允许统一输入，不能依赖类别相同。</summary>
    [Fact]
    public void MultiSelectionGrantsEditingOnlyWhenEveryObjectAllowsIt()
    {
        var result = PnPropertyPolicy.Merge(Specs, [Object(), Object("HV-1002")]);
        Assert.Equal(2, result.Count);
        Assert.Equal("DN50", result.Cells[0].Value.Display);
        Assert.Equal("多种值", result.Cells[1].Value.Display);
        Assert.All(result.Cells, c => Assert.True(c.Permission.Allowed));
    }

    /// <summary>混选不适用对象不能拿一个 Plant 元件的值冒充共同属性。</summary>
    [Fact]
    public void MixedSelectionUsesMissingForInapplicableProperties()
    {
        var ordinary = new PnObjectProperties("CAD", "非 Plant 对象", Specs.Select(s =>
            new PnPropertyCell(s, FastPropertyValue.Missing(), PnEditPermission.ReadOnly("非 Plant"))).ToArray());
        var result = PnPropertyPolicy.Merge(Specs, [Object(), ordinary]);
        Assert.All(result.Cells, c => Assert.Equal("", c.Value.Display));
        Assert.Contains("多种对象", result.Header);
    }

    /// <summary>缺失与 SDK 失败分别显示，详情可供 Tooltip 使用。</summary>
    [Fact]
    public void MissingAndSdkFailureRemainDifferentStates()
    {
        var cells = Object().Cells.ToArray();
        cells[0] = cells[0] with { Value = FastPropertyValue.AbsentProperty("不含 Size"), Permission = PnEditPermission.ReadOnly("缺失") };
        cells[2] = cells[2] with { Value = FastPropertyValue.Error("SDK ErrorStatus=InvalidInput"), Permission = PnEditPermission.ReadOnly("失败") };
        var result = PnPropertyPolicy.Merge(Specs, [Object() with { Cells = cells }]);
        Assert.Equal("--", result.Cells[0].Value.Display);
        Assert.Equal("读取失败", result.Cells[2].Value.Display);
        Assert.Contains("InvalidInput", result.Cells[2].Value.Detail);
        Assert.Equal("HV-1001", result.Cells[1].Value.Display);
    }

    /// <summary>无选择仍保留所有输入行，值清空且不可编辑。</summary>
    [Fact]
    public void NoSelectionKeepsPropertyInputs()
    {
        var result = PnPropertyPolicy.Merge(Specs, []);
        Assert.Equal(0, result.Count); Assert.Equal(3, result.Cells.Count);
        Assert.Equal(Specs, result.Cells.Select(c => c.Spec));
        Assert.All(result.Cells, c => { Assert.Equal("", c.Value.Display); Assert.False(c.Permission.Allowed); });
    }

    /// <summary>行数、身份或修订不一致不能组合成新表。</summary>
    [Fact]
    public void MismatchedRowsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => PnPropertyPolicy.Merge(Specs, [new("x", "x", [])]));
        var specs = Specs.Select(s => s with { Revision = s.Revision + 1 }).ToArray();
        Assert.Throws<ArgumentException>(() => PnPropertyPolicy.Merge(specs, [Object()]));
    }

    private static PnEditContext<(Guid Database, int BlockTable), int> EditContext() =>
        new((Guid.NewGuid(), 1), 42, 7, Specs[0].Id, 1, "TargetObject.Size", "PnId:12:Valve", "DN50", 1, PnEditPermission.Writable);

    /// <summary>全部快照一致才允许提交。</summary>
    [Fact]
    public void FreshEditPassesAllChecks()
    {
        var expected = EditContext();
        Assert.Null(PnPropertyPolicy.Validate(expected, expected));
    }

    /// <summary>身份、选择、路径、值或权限变化独立使旧编辑失效。</summary>
    [Theory]
    [InlineData("database", "图纸身份")]
    [InlineData("target", "选择对象")]
    [InlineData("multi", "选择对象")]
    [InlineData("epoch", "已变化")]
    [InlineData("row", "路径")]
    [InlineData("revision", "路径")]
    [InlineData("path", "路径")]
    [InlineData("link", "关联")]
    [InlineData("old", "原值")]
    [InlineData("permission", "图层锁定")]
    public void StaleEditIsRejectedBeforeWrite(string change, string reason)
    {
        var expected = EditContext();
        var current = change switch
        {
            "database" => expected with { Identity = (Guid.NewGuid(), 1) },
            "target" => expected with { Target = 43 },
            "multi" => expected with { SelectionCount = 2 },
            "epoch" => expected with { Epoch = 8 },
            "row" => expected with { RowId = Guid.NewGuid() },
            "revision" => expected with { RowRevision = 2 },
            "path" => expected with { Path = "TargetObject.Tag" },
            "link" => expected with { TargetKey = "PnId:13:Valve" },
            "old" => expected with { Original = "DN80" },
            _ => expected with { Permission = PnEditPermission.ReadOnly("图层锁定") }
        };
        bool wrote = false;
        string? rejection = PnPropertyPolicy.Validate(expected, current);
        if (rejection == null) wrote = true;
        Assert.False(wrote); Assert.Contains(reason, rejection);
    }

    /// <summary>未授予的权限不可由后续读取凭空补成有效旧票据。</summary>
    [Fact]
    public void ReadOnlySnapshotCannotBecomeAValidEdit()
    {
        var current = EditContext();
        var expected = current with { Permission = PnEditPermission.ReadOnly("获取属性") };
        Assert.NotNull(PnPropertyPolicy.Validate(expected, current));
    }

    /// <summary>超过一个分批的选择全部完成后发布，最后一个值影响共同值。</summary>
    [Fact]
    public void CompleteSelectionIsReadAcrossChunks()
    {
        var batch = new PnPropertyBatch<int, int>(); batch.Begin(1, Enumerable.Range(0, 70).ToArray(), Specs);
        var seen = new List<int>();
        PnObjectProperties Read(int id) { seen.Add(id); return Object(id == 69 ? "LAST" : "FIRST"); }
        Assert.Null(batch.Step(1, Read)); Assert.Null(batch.Step(1, Read));
        var result = batch.Step(1, Read);
        Assert.Equal(70, result?.Count); Assert.Equal(70, seen.Count);
        Assert.Equal("多种值", result?.Cells[1].Value.Display);
    }

    /// <summary>输入路径时立即废弃旧结果，新轮次只显示新修订的属性。</summary>
    [Fact]
    public void PathChangeDiscardsOldBatch()
    {
        var batch = new PnPropertyBatch<int, int>(); batch.Begin(1, [1, 2], Specs);
        Assert.Null(batch.Step(1, _ => { batch.Invalidate(); return Object(); }));
        var newSpecs = new[] { Specs[0] with { Revision = 2, Input = "LoopNumber" } };
        batch.Begin(1, [1], newSpecs);
        var result = batch.Step(1, _ => new("x", "x", [new(newSpecs[0], FastPropertyValue.Value("101"), PnEditPermission.ReadOnly("测试"))]));
        Assert.Equal("LoopNumber", result?.Cells[0].Spec.Input);
        Assert.Equal("101", result?.Cells[0].Value.Display);
    }

    /// <summary>读取中切换图纸、关闭同名重开，旧数据库身份不得发布。</summary>
    [Fact]
    public void DatabaseChangeDiscardsPendingBatchWithoutReading()
    {
        var batch = new PnPropertyBatch<Guid, int>(); batch.Begin(Guid.NewGuid(), [1], Specs);
        Assert.Null(batch.Step(Guid.NewGuid(), _ => throw new InvalidOperationException()));
        Assert.False(batch.HasWork);
    }

    /// <summary>开始新选择的重入回调不能被旧读取清空。</summary>
    [Fact]
    public void NewSelectionSurvivesOldCallback()
    {
        var batch = new PnPropertyBatch<int, int>(); batch.Begin(1, [1, 2], Specs);
        Assert.Null(batch.Step(1, _ => { batch.Begin(1, [9], Specs); return Object("OLD"); }));
        Assert.Equal("NEW", batch.Step(1, _ => Object("NEW"))?.Cells[1].Value.Display);
    }
}
