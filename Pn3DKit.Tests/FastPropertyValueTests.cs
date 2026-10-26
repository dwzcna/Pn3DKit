using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>0.1.26.6 将已有空值、确认缺少属性及未知状态分别显示，不改变真实输入。</summary>
public sealed class FastPropertyValueTests
{
    /// <summary>只有 SDK 确认缺少属性时显示双横线，未就绪和已有空值均为空白。</summary>
    [Fact]
    public void EmptyAbsentUnknownAndErrorRemainDistinct()
    {
        var empty = FastPropertyValue.Value("");
        var absent = FastPropertyValue.AbsentProperty("对象不含 LoopNumber");
        var unknown = FastPropertyValue.Missing("尚未选择对象");
        var error = FastPropertyValue.Error("SDK ErrorStatus=InvalidInput");
        Assert.Equal("", empty.Display); Assert.Equal(FastValueKind.Value, empty.Kind);
        Assert.Equal("--", absent.Display); Assert.Equal("", absent.Raw);
        Assert.Equal("", unknown.Display); Assert.NotEqual(empty.Kind, unknown.Kind);
        Assert.Equal("读取失败", error.Display); Assert.Contains("InvalidInput", error.Detail);
    }

    /// <summary>多选所有值为空仍为空；空值和非空值不冒充相同值。</summary>
    [Fact]
    public void CommonEmptyValuesStayEmptyAndDifferentValuesStayMixed()
    {
        var empty = FastPropertyValue.Value("");
        Assert.Equal("", FastPropertyPolicy.MergeValues([empty, empty]).Display);
        Assert.Equal(FastValueKind.Value, FastPropertyPolicy.MergeValues([empty, empty]).Kind);
        Assert.Equal("多种值", FastPropertyPolicy.MergeValues([empty, FastPropertyValue.Value("备注")]).Display);
    }

    /// <summary>部分对象缺少字段时保持缺少标记；未知和错误不据此推断所有对象的适用性。</summary>
    [Fact]
    public void AbsentSelectionKeepsReasonWithoutMaskingUnknownOrErrors()
    {
        var absent = FastPropertyValue.AbsentProperty("阀门不含 LoopNumber");
        var partial = FastPropertyPolicy.MergeValues([FastPropertyValue.Value("101"), absent]);
        Assert.Equal("--", partial.Display); Assert.Contains("部分选中对象", partial.Detail);
        Assert.Equal("--", FastPropertyPolicy.MergeValues([absent, absent]).Display);
        Assert.Equal("", FastPropertyPolicy.MergeValues([absent, FastPropertyValue.Missing("非 Plant")]).Display);
        Assert.Equal("读取失败", FastPropertyPolicy.MergeValues([absent, FastPropertyValue.Error("SDK 失败")]).Display);
        Assert.Equal("", FastPropertyPolicy.MergeValues([]).Display);
    }

    /// <summary>原始空格及真实横线值不修剪、不变成缺少属性状态，也不合并成空值。</summary>
    [Fact]
    public void LiteralWhitespaceAndDashesRemainActualValues()
    {
        Assert.Equal("  ", FastPropertyValue.Value("  ").Display);
        Assert.Equal("--", FastPropertyValue.Value("--").Display);
        Assert.Equal(FastValueKind.Value, FastPropertyValue.Value("--").Kind);
        Assert.Equal("多种值", FastPropertyPolicy.MergeValues([FastPropertyValue.Value(""), FastPropertyValue.Value("  ")]).Display);
        Assert.Equal("多种值", FastPropertyPolicy.MergeValues([FastPropertyValue.Value(""), FastPropertyValue.Value("--")]).Display);
    }
}
