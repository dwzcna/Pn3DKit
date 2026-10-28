using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>候选只来自完整票据原值，不截断、不修剪、不按显示提示过滤真实字符串。</summary>
public sealed class PnUnifiedValueTests
{
    /// <summary>Ordinal 身份保留大小写、首尾字符及真实提示同文，排除仅空白的候选。</summary>
    [Fact]
    public void OptionsPreserveOrdinalValuesAndExcludeOnlyBlankCandidates()
    {
        var options = PnUnifiedValuePolicy.Options([null, "", " ", "\t", "Z", "a", "A", " A", "A ",
            "(null)", "*(多种)", "(空白)", "null", "A", "a"]);
        Assert.Equal(new[] { " A", "(null)", "(空白)", "*(多种)", "A", "A ", "Z", "a", "null" }, options);
    }

    /// <summary>超过 Tooltip 的十项上限仍提供全部真实候选，重复目标只去重值。</summary>
    [Fact]
    public void MoreThanTenValuesAreNeverTruncated()
    {
        var values = Enumerable.Range(0, 25).Select(i => "值" + i.ToString("D2")).ToArray();
        var options = PnUnifiedValuePolicy.Options(values.Reverse().Concat(values));
        Assert.Equal(25, options.Count); Assert.Equal(values, options);
    }

    /// <summary>全部原值无可列候选时仍允许用户在编辑框手动输入。</summary>
    [Fact]
    public void NoNonblankCandidatesProduceAnEmptyList() =>
        Assert.Empty(PnUnifiedValuePolicy.Options([null, "", " ", "\t", "\r\n"]));
}
