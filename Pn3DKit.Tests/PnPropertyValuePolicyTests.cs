using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>按类型核对值，避免显示文本、空值或区域差异带来误判。</summary>
public sealed class PnPropertyValuePolicyTests
{
    /// <summary>数值精确等值通过，字符串仍严格，null不与任何文本合并。</summary>
    [Theory]
    [InlineData("01", "1", typeof(int), true)]
    [InlineData("01", "1", typeof(string), false)]
    [InlineData("1.0", "1", typeof(decimal), true)]
    [InlineData("1.00000001", "1", typeof(double), false)]
    [InlineData("True", "true", typeof(bool), true)]
    [InlineData("", null, typeof(string), false)]
    [InlineData("1", null, typeof(int), false)]
    [InlineData("not numeric", "1", typeof(int), false)]
    public void TypedEquality(string requested, string? actual, Type type, bool expected)
    {
        var plan = new PnPropertyWritePlan<int>(1, "row", "", requested) { ValueType = type, ValueCulture = "en-US" };
        Assert.Equal(expected, PnPropertyValuePolicy.Equivalent(plan, actual));
    }
}
