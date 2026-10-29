using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证位号查询表达式、继承去重和批次格式缓存边界。</summary>
public sealed class ProjectTagLookupTests
{
    /// <summary>引号、通配符和中文均按字面量传入固定 Tag 列。</summary>
    [Theory]
    [InlineData("V'1", "Tag = 'V''1'")]
    [InlineData("阀%_[1]", "Tag = '阀%_[1]'")]
    [InlineData("", "Tag = ''")]
    public void FilterEscapesLiteral(string value, string filter) => Assert.Equal(filter, ProjectTagLookup.Filter(value));

    /// <summary>非ASCII大小写字符扩大候选，通配符、引号及反斜杠保持字面量。</summary>
    [Theory]
    [InlineData("Ä1", "Tag LIKE '_1' ESCAPE '\\'")]
    [InlineData("Ι1", "Tag LIKE '_1' ESCAPE '\\'")]
    [InlineData("𐐀1", "Tag LIKE '_1' ESCAPE '\\'")]
    [InlineData("Ä%_[1]'\\x", "Tag LIKE '_\\%\\_[1]''\\\\x' ESCAPE '\\'")]
    public void UnicodeCaseCandidatesAreWidened(string tag, string filter) =>
        Assert.Equal(filter, ProjectTagLookup.Filter(tag));

    /// <summary>扩大结果仍严格按OrdinalIgnoreCase去重并排除自身，不能把通配命中当占用。</summary>
    [Fact]
    public void UnicodeCandidateResultsAreRechecked()
    {
        var rows = new[] { (1, "Ä1"), (2, "ä1"), (2, "Ä1"), (3, "B1"), (4, "ä2") };
        Assert.Equal(new[] { 2 }, ProjectTagLookup.Owners("Ä1", 1, _ => rows));
    }

    /// <summary>非法UTF16和NUL在调用SDK前拒绝，不使用替代字符查询。</summary>
    [Fact]
    public void InvalidUnicodeNeverReachesSdk()
    {
        foreach (string tag in new[] { "V\0", "V\ud800", "\udc00" })
            Assert.Throws<ArgumentException>(() => ProjectTagLookup.Owners(tag, 0,
                _ => throw new InvalidOperationException("不得查询SDK")));
    }

    /// <summary>SDK 查询结果必须再核对 Tag、去掉自身和继承表重复。</summary>
    [Fact]
    public void OwnersAreCurrentFilteredAndDeduplicated()
    {
        int calls = 0;
        var owners = ProjectTagLookup.Owners("Valve", 1, filter =>
        {
            calls++; Assert.Equal("Tag = 'Valve'", filter);
            return new[] { (1, "Valve"), (10, "VALVE"), (2, "Valve"), (2, "Valve"), (8, "Pump") };
        });
        Assert.Equal(1, calls); Assert.Equal(new[] {2,10}, owners);
    }

    /// <summary>每次占用检查访问当前 SDK 视图，不用旧缓存放行。</summary>
    [Fact]
    public void ExternalOwnerAppearingIsObserved()
    {
        var rows = new List<(int, string)> { (1, "V") };
        Assert.Empty(ProjectTagLookup.Owners("V", 1, _ => rows));
        rows.Add((2, "V"));
        Assert.Equal(new[] {2}, ProjectTagLookup.Owners("V", 1, _ => rows));
    }

    /// <summary>查询读取错误完整传播，不能返回已经发现的部分占用。</summary>
    [Fact]
    public void QueryFailureDoesNotReturnPartial()
    {
        IEnumerable<(int,string)> Broken() { yield return (2,"V"); throw new IOException("SDK read"); }
        Assert.Throws<IOException>(() => ProjectTagLookup.Owners("V", 1, _ => Broken()));
    }

    /// <summary>相同批次同类型只解析一次，租约结束及新批次重新解析。</summary>
    [Fact]
    public void FormatCacheIsLimitedToLease()
    {
        int calls = 0;
        using (BatchRunGate.TryEnter())
        {
            Assert.Equal(1, TagFormatCache.Get("fixture", () => ++calls));
            Assert.Equal(1, TagFormatCache.Get("fixture", () => ++calls));
        }
        Assert.Equal(2, TagFormatCache.Get("fixture", () => ++calls));
        using (BatchRunGate.TryEnter()) Assert.Equal(3, TagFormatCache.Get("fixture", () => ++calls));
    }

    /// <summary>纯结构缓存保持字段相邻边界，未缓存任何属性值。</summary>
    [Fact]
    public void AdjacentFieldsRetainOffsets()
    {
        var tokens = TagFormatCache.Tokens("I-#(LoopNumber^NNN)#(Feature)-#(Number)");
        Assert.Equal(3, tokens.Count);
        Assert.Equal("#(LoopNumber^NNN)", tokens[0].Value);
        Assert.Equal(tokens[0].Index + tokens[0].Length, tokens[1].Index);
    }
}
