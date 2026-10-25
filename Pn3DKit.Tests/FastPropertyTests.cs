using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证面板的共同值语义与选择轮次隔离，不加载 Autodesk 模块。</summary>
public sealed class FastPropertyTests
{
    /// <summary>构造真实字段排列的离线元件快照，内部类别键和显示名称分开。</summary>
    private static FastObjectProperties Plant(string tag = "PT-101", string classKey = "PnId:Valve", string label = "阀门") =>
        new(classKey, label, [
            FastPropertyValue.Value("ACPPASSET"), FastPropertyValue.Value("设备"),
            FastPropertyValue.Value("ByLayer"), FastPropertyValue.Value("ByLayer"),
            FastPropertyValue.Value("PnId"), FastPropertyValue.Value(classKey, label),
            FastPropertyValue.Value(tag), FastPropertyValue.Value("#(+TargetObject.Type)-#(TargetObject.Number^NN)")
        ]);

    /// <summary>替换单字段，模拟部分错误和普通 CAD 不适用。</summary>
    private static FastObjectProperties With(FastObjectProperties source, FastProperty field, FastPropertyValue value)
    {
        var values = source.Values.ToArray(); values[(int)field] = value;
        return source with { Values = values };
    }

    /// <summary>单选八项完整，长格式和只读数量不丢失。</summary>
    [Fact]
    public void SingleSelectionShowsAllEightValues()
    {
        var item = Plant();
        var result = FastPropertyPolicy.Merge([item]);
        Assert.Equal(1, result.Count);
        Assert.Equal("阀门", result.Header);
        Assert.Equal(item.Values, result.Values);
        Assert.Contains("只读", result.Status);
        Assert.Contains("1 个对象", result.Status);
        Assert.Equal("#(+TargetObject.Type)-#(TargetObject.Number^NN)", result.Values[7].Display);
    }

    /// <summary>多选相同原始值按字段保留共同值，顶部保留总量。</summary>
    [Fact]
    public void EqualMultiSelectionShowsCommonValues()
    {
        var result = FastPropertyPolicy.Merge([Plant(), Plant(), Plant()]);
        Assert.Equal(3, result.Count);
        Assert.Equal("阀门（3 个对象）", result.Header);
        Assert.All(result.Values, v => Assert.Equal(FastValueKind.Value, v.Kind));
        Assert.Equal("PT-101", result.Values[6].Display);
    }

    /// <summary>仅变化字段显示多种值，其他共同字段不受影响。</summary>
    [Theory]
    [InlineData(FastProperty.Layer, "仪表")]
    [InlineData(FastProperty.Color, "ACI 2")]
    [InlineData(FastProperty.Linetype, "CONTINUOUS")]
    [InlineData(FastProperty.Tag, "PT-102")]
    public void DifferentFieldOnlyBecomesMixed(FastProperty field, string different)
    {
        var result = FastPropertyPolicy.Merge([Plant(), With(Plant(), field, FastPropertyValue.Value(different))]);
        Assert.Equal("多种值", result.Values[(int)field].Display);
        Assert.Equal(FastValueKind.Mixed, result.Values[(int)field].Kind);
        Assert.Contains(different, result.Values[(int)field].Detail);
        Assert.Equal("PnId", result.Values[4].Display);
    }

    /// <summary>不同类名使用相同本地化显示名时，仍是不同类别和不同对象。</summary>
    [Fact]
    public void EqualDisplayLabelsDoNotMergeDifferentClassIdentities()
    {
        var result = FastPropertyPolicy.Merge([Plant(), Plant(classKey: "PnId:CheckValve")]);
        Assert.Equal("多种对象（2 个对象）", result.Header);
        Assert.Equal("多种值", result.Values[5].Display);
    }

    /// <summary>原始字符串采用 Ordinal 比较，大小写不同的位号不冒充共同值。</summary>
    [Fact]
    public void TagComparisonIsOrdinal()
    {
        var result = FastPropertyPolicy.Merge([Plant("pt-101"), Plant("PT-101")]);
        Assert.Equal(FastValueKind.Mixed, result.Values[6].Kind);
    }

    /// <summary>普通 CAD 和 Plant 混选时 Plant 四项不适用，不显示单个元件的属性。</summary>
    [Fact]
    public void MixedCadAndPlantKeepCommonGeneralProperties()
    {
        var cad = Plant() with { TypeKey = "CAD:LINE", TypeLabel = "LINE" };
        for (int i = 4; i < 8; i++) cad = With(cad, (FastProperty)i, FastPropertyValue.Missing("普通 CAD 对象。"));
        cad = With(cad, FastProperty.Type, FastPropertyValue.Value("LINE"));
        var result = FastPropertyPolicy.Merge([Plant(), cad]);
        Assert.Equal("多种对象（2 个对象）", result.Header);
        Assert.Equal("多种值", result.Values[0].Display);
        Assert.Equal("设备", result.Values[1].Display);
        Assert.All(result.Values.Skip(4), v => Assert.Equal(FastValueKind.Missing, v.Kind));
        Assert.Contains("不适用于所有", result.Values[6].Detail);
    }

    /// <summary>空值、缺失和占位字符使用不同结构状态，禁止把显示文本当比较键。</summary>
    [Fact]
    public void EmptyMissingAndLiteralPlaceholderAreDistinct()
    {
        var empty = With(Plant(), FastProperty.Tag, FastPropertyValue.Value(""));
        var missing = With(Plant(), FastProperty.Tag, FastPropertyValue.Missing("没有 Tag 列"));
        var literal = With(Plant(), FastProperty.Tag, FastPropertyValue.Value("—"));
        Assert.Equal("—", FastPropertyPolicy.Merge([empty]).Values[6].Display);
        Assert.Equal(FastValueKind.Value, FastPropertyPolicy.Merge([empty, empty]).Values[6].Kind);
        Assert.Equal(FastValueKind.Mixed, FastPropertyPolicy.Merge([empty, literal]).Values[6].Kind);
        Assert.Equal(FastValueKind.Missing, FastPropertyPolicy.Merge([missing, literal]).Values[6].Kind);
    }

    /// <summary>读取失败显示失败和 SDK 详情，不把其他字段清空。</summary>
    [Fact]
    public void PartialFailurePreservesSuccessfulFieldsAndDetails()
    {
        var error = With(Plant(), FastProperty.Tag, FastPropertyValue.Error("SDK ErrorStatus=InvalidInput"));
        var result = FastPropertyPolicy.Merge([Plant(), error]);
        Assert.Equal("读取失败", result.Values[6].Display);
        Assert.Contains("InvalidInput", result.Values[6].Detail);
        Assert.Equal("ByLayer", result.Values[2].Display);
        Assert.Contains("部分属性读取失败", result.Status);
    }

    /// <summary>缺失优先表达适用性，同时在详情和底部保留实际错误。</summary>
    [Fact]
    public void NotApplicableDoesNotEraseFailureDetails()
    {
        var missing = With(Plant(), FastProperty.Tag, FastPropertyValue.Missing("没有属性"));
        var error = With(Plant(), FastProperty.Tag, FastPropertyValue.Error("数据库读取失败"));
        var result = FastPropertyPolicy.Merge([missing, error]);
        Assert.Equal("—", result.Values[6].Display);
        Assert.Contains("数据库读取失败", result.Values[6].Detail);
        Assert.Contains("读取失败", result.Status);
    }

    /// <summary>失败对象不从统计中丢弃，也不把失败文本当普通值比较。</summary>
    [Fact]
    public void FailedObjectRemainsPartOfSelection()
    {
        var result = FastPropertyPolicy.Merge([Plant(), FastPropertyPolicy.Failed("对象已删除")]);
        Assert.Equal(2, result.Count);
        Assert.All(result.Values, v => Assert.Equal(FastValueKind.Error, v.Kind));
        Assert.Contains("对象已删除", result.Values[0].Detail);
    }

    /// <summary>没有选择时八项均为空，不显示上一轮值。</summary>
    [Fact]
    public void EmptySelectionClearsSnapshot()
    {
        var result = FastPropertyPolicy.Merge([]);
        Assert.Equal(0, result.Count);
        Assert.Equal("未选择对象", result.Header);
        Assert.Equal(8, result.Values.Count);
        Assert.All(result.Values, v => Assert.Equal(FastValueKind.Missing, v.Kind));
    }

    /// <summary>没有 Plant 环境仍保留常规值及可解释的降级提示。</summary>
    [Fact]
    public void MissingPlantEnvironmentKeepsGeneralValues()
    {
        var cad = Plant() with { Note = "当前 Plant 环境不可用，显示常规属性。" };
        for (int i = 4; i < 8; i++) cad = With(cad, (FastProperty)i, FastPropertyValue.Missing());
        var result = FastPropertyPolicy.Merge([cad]);
        Assert.Equal("设备", result.Values[1].Display);
        Assert.All(result.Values.Skip(4), v => Assert.Equal("—", v.Display));
        Assert.Contains("常规属性", result.Status);
        Assert.DoesNotContain("读取失败", result.Status);
    }

    /// <summary>不完整的单对象快照必须被拒绝，防止索引错位。</summary>
    [Fact]
    public void IncompleteFieldSetsAreRejected() =>
        Assert.Throws<ArgumentException>(() => FastPropertyPolicy.Merge([new("LINE", "LINE", [])]));

    /// <summary>离线身份模拟完整 ObjectId 的数据库与对象两部分，不使用文件名。</summary>
    private readonly record struct Identity(Guid Database, int BlockTable);

    /// <summary>大选择按批完成后才发布，包含最后一项，不截断为前 32 项。</summary>
    [Fact]
    public void LargeSelectionIsCompleteAndNotPublishedPartially()
    {
        var batch = new FastPropertyBatch<Identity, int>();
        var identity = new Identity(Guid.NewGuid(), 1);
        var targets = Enumerable.Range(0, 105).ToArray();
        batch.Begin(identity, targets);
        var read = new List<int>();
        FastObjectProperties Read(int id) { read.Add(id); return Plant(id == 104 ? "PT-999" : "PT-101"); }
        Assert.Null(batch.Step(identity, Read));
        Assert.Equal(32, batch.Processed);
        Assert.Null(batch.Step(identity, Read));
        Assert.Null(batch.Step(identity, Read));
        var result = batch.Step(identity, Read);
        Assert.NotNull(result);
        Assert.Equal(105, result.Count);
        Assert.Equal("多种值", result.Values[6].Display);
        Assert.Equal(targets, read);
        Assert.False(batch.HasWork);
    }

    /// <summary>在读取中选择变化会使旧结果失效，旧回调不能发布。</summary>
    [Fact]
    public void SelectionChangeDuringReadDiscardsOldResult()
    {
        var batch = new FastPropertyBatch<int, int>();
        batch.Begin(1, [1, 2]);
        Assert.Null(batch.Step(1, id => { batch.Invalidate(); return Plant(); }));
        Assert.False(batch.HasWork);
        batch.Begin(1, [3]);
        Assert.Equal("PT-103", batch.Step(1, _ => Plant("PT-103"))?.Values[6].Display);
    }

    /// <summary>重入开始新选择后，旧轮次不得清空或污染新轮次。</summary>
    [Fact]
    public void RestartDuringReadKeepsNewSelection()
    {
        var batch = new FastPropertyBatch<int, int>();
        batch.Begin(1, [1, 2]);
        Assert.Null(batch.Step(1, _ => { batch.Begin(1, [9]); return Plant("OLD"); }));
        Assert.True(batch.HasWork);
        var result = batch.Step(1, id => Plant("NEW-" + id));
        Assert.Equal(1, result?.Count);
        Assert.Equal("NEW-9", result?.Values[6].Display);
    }

    /// <summary>关闭后重开同名图纸即使表句柄相同，完整身份不同也不能继续旧批次。</summary>
    [Fact]
    public void DifferentDatabaseIdentityDiscardsPendingValues()
    {
        var batch = new FastPropertyBatch<Identity, int>();
        var first = new Identity(Guid.NewGuid(), 1);
        var reopened = new Identity(Guid.NewGuid(), 1);
        batch.Begin(first, [1, 2]);
        Assert.Null(batch.Step(first, _ => Plant(), 1));
        int reads = 0;
        Assert.Null(batch.Step(reopened, _ => { reads++; return Plant(); }));
        Assert.Equal(0, reads);
        Assert.False(batch.HasWork);
    }

    /// <summary>Begin 固定清单，不被调用方随后改变选择数组污染。</summary>
    [Fact]
    public void TargetListIsSnapshotted()
    {
        var batch = new FastPropertyBatch<int, int>();
        int[] ids = [1, 2];
        batch.Begin(1, ids); ids[1] = 9;
        var seen = new List<int>();
        var result = batch.Step(1, id => { seen.Add(id); return Plant(); });
        Assert.Equal([1, 2], seen);
        Assert.Equal(2, result?.Count);
    }

    /// <summary>空清单不用读取也能发布清空结果。</summary>
    [Fact]
    public void EmptyBatchPublishesEmptyWithoutReading()
    {
        var batch = new FastPropertyBatch<int, int>();
        batch.Begin(1, []);
        Assert.Equal(0, batch.Step(1, _ => throw new InvalidOperationException())?.Count);
        Assert.False(batch.HasWork);
    }

    /// <summary>批预算必须大于零，避免永不完成的刷新。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidBudgetIsRejected(int budget)
    {
        var batch = new FastPropertyBatch<int, int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Step(1, _ => Plant(), budget));
    }
}
