using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>区分空字符串、真实 null、缺少属性及未知状态，不把占位提示当作真实输入。</summary>
public sealed class FastPropertyValueTests
{
    /// <summary>SDK 缺少属性显示横线，已有空字符串显示占位，未就绪仍不显示值。</summary>
    [Fact]
    public void EmptyAbsentUnknownAndErrorRemainDistinct()
    {
        var empty = FastPropertyValue.Value("");
        var absent = FastPropertyValue.AbsentProperty("对象不含 LoopNumber");
        var unknown = FastPropertyValue.Missing("尚未选择对象");
        var error = FastPropertyValue.Error("SDK ErrorStatus=InvalidInput");
        Assert.Equal("(空白)", empty.Display); Assert.Equal(FastValueKind.Value, empty.Kind);
        Assert.Equal("--", absent.Display); Assert.Equal("", absent.Raw);
        Assert.Equal("", unknown.Display); Assert.NotEqual(empty.Kind, unknown.Kind);
        Assert.Equal("读取失败", error.Display); Assert.Contains("InvalidInput", error.Detail);
    }

    /// <summary>多选共同空值用占位提示；空值和非空值不冒充相同值。</summary>
    [Fact]
    public void CommonEmptyValuesStayEmptyAndDifferentValuesStayMixed()
    {
        var empty = FastPropertyValue.Value("");
        Assert.Equal("(空白)", FastPropertyPolicy.MergeValues([empty, empty]).Display);
        Assert.Equal(FastValueKind.Value, FastPropertyPolicy.MergeValues([empty, empty]).Kind);
        Assert.Equal("*(多种)", FastPropertyPolicy.MergeValues([empty, FastPropertyValue.Value("备注")]).Display);
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
        Assert.Equal("*(多种)", FastPropertyPolicy.MergeValues([FastPropertyValue.Value(""), FastPropertyValue.Value("  ")]).Display);
        Assert.Equal("*(多种)", FastPropertyPolicy.MergeValues([FastPropertyValue.Value(""), FastPropertyValue.Value("--")]).Display);
    }

    /// <summary>真实 null 不压成空字符串；空格、制表符、文字 null 及占位同文仍是普通值。</summary>
    [Theory]
    [InlineData(null, "(null)", true)]
    [InlineData("", "(空白)", true)]
    [InlineData(" ", " ", false)]
    [InlineData("\t", "\t", false)]
    [InlineData("null", "null", false)]
    [InlineData("(null)", "(null)", false)]
    [InlineData("(空白)", "(空白)", false)]
    [InlineData("*(多种)", "*(多种)", false)]
    public void EmptyNullAndLiteralPlaceholdersKeepTheirRawIdentity(string? raw, string display, bool placeholder)
    {
        var value = FastPropertyValue.Value(raw);
        Assert.Equal(raw, value.Raw); Assert.Equal(display, value.Display);
        Assert.Equal(placeholder, value.Placeholder != null);
    }

    /// <summary>共同 null 仍为值，null／空字符串及不同空白字符之间按真实原值合并。</summary>
    [Fact]
    public void NullAndEmptyNeverCollapseDuringSelectionMerge()
    {
        var nil = FastPropertyValue.Value(null); var empty = FastPropertyValue.Value("");
        var common = FastPropertyPolicy.MergeValues([nil, nil]);
        Assert.Equal("(null)", common.Display); Assert.Null(common.Raw); Assert.Equal(FastValueKind.Value, common.Kind);
        Assert.Equal(FastValueKind.Mixed, FastPropertyPolicy.MergeValues([nil, empty]).Kind);
        Assert.Equal(FastValueKind.Mixed, FastPropertyPolicy.MergeValues([nil, FastPropertyValue.Value("null")]).Kind);
        Assert.Equal(FastValueKind.Mixed, FastPropertyPolicy.MergeValues([empty, FastPropertyValue.Value(" ")]).Kind);
    }

    /// <summary>独立显示名称已有内容时不被空原始键的占位提示覆盖。</summary>
    [Fact]
    public void ExplicitDisplayNameDoesNotBecomeAnEmptyPlaceholder()
    {
        var value = FastPropertyValue.Value("", "本地化名称");
        Assert.Equal("本地化名称", value.Display); Assert.Null(value.Placeholder);
        var hiddenName = FastPropertyValue.Value("真实原值", "");
        Assert.Equal("", hiddenName.Display); Assert.Null(hiddenName.Placeholder);
    }

}
