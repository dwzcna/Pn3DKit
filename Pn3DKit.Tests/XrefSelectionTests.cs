using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>名称扫描仍沿用已确认的范围和原名规则。</summary>
public sealed class XrefSelectionTests
{
    /// <summary>根层附着／覆盖均纳入，嵌套覆盖停止传播。</summary>
    [Theory]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(3, true, false)]
    public void ReferenceBoundaryIsPreserved(int depth, bool overlay, bool expected) =>
        Assert.Equal(expected, XrefBlockNames.IncludesReference(depth, overlay));

    /// <summary>不能通过截取最后一个竖线猜测原始名称。</summary>
    [Theory]
    [InlineData("Valve", "X", "Valve")]
    [InlineData("X|Valve", "X", "Valve")]
    [InlineData("X|Child|Valve", "X", "Child|Valve")]
    [InlineData("Unknown|Valve", "X", null)]
    public void OriginalNameRequiresConfirmedNamespace(string stored, string source, string? expected) =>
        Assert.Equal(expected, XrefBlockNames.RemoveSourceNamespace(stored, [source]));

    /// <summary>已确认前缀重叠时使用最完整前缀，按 CAD 大小写规则匹配。</summary>
    [Fact]
    public void LongestConfirmedNamespaceWins() =>
        Assert.Equal("Valve", XrefBlockNames.RemoveSourceNamespace("x|Child|Valve", ["X", "X|Child"]));
}
