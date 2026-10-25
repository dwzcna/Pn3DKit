using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证安装管线及管线组字段交给 SDK 上下文读取，并防止不确定状态被误报为缺失。</summary>
public sealed class TagPropertyScopeTests
{
    /// <summary>用户报告的管线组字段与阀门安装管线 Size 均不得在目标自身查询。</summary>
    [Theory]
    [InlineData("PipeLineGroup", "Service")]
    [InlineData("PipeLineGroup", "Area")]
    [InlineData("PipeLineGroup", "LineNumber")]
    [InlineData("PipeLineGroup", "NominalSize")]
    [InlineData("PipeLineGroup", "NominalSpec")]
    [InlineData("PipeLineGroup", "Insulation")]
    [InlineData("PipeLines", "Size")]
    public void ExplicitClassUsesSdkContext(string scope, string property)
    {
        Assert.Equal(TagPropertyAvailability.Present, TagPropertyScope.Check(scope, property,
            _ => throw new InvalidOperationException("不能在部件自身检查此字段"),
            (actualScope, actualProperty) =>
            {
                Assert.Equal(scope, actualScope);
                Assert.Equal(property, actualProperty);
                return TagPropertyAvailability.Present;
            }));
    }

    /// <summary>同一格式的 TargetObject.Size 必须独立检查阀门自身。</summary>
    [Fact]
    public void ValveSizeUsesTargetWithoutBorrowingLineSize()
    {
        Assert.Equal(TagPropertyAvailability.Missing, TagPropertyScope.Check("TargetObject", "Size",
            name => { Assert.Equal("Size", name); return false; },
            (_, _) => throw new InvalidOperationException("不能借用安装管线字段")));
    }

    /// <summary>单字段定位失败时保留不确定状态，让完整格式继续 SDK 求值。</summary>
    [Fact]
    public void UnresolvedPipelineContextIsNotReportedMissing()
    {
        Assert.Equal(TagPropertyAvailability.Unresolved, TagPropertyScope.Check("PipeLineGroup", "Service",
            _ => false, (_, _) => TagPropertyAvailability.Unresolved));
    }

    /// <summary>明确确认安装管线缺少字段时仍能显示缺失提示。</summary>
    [Fact]
    public void ConfirmedMissingScopedPropertyRemainsMissing()
    {
        Assert.Equal(TagPropertyAvailability.Missing, TagPropertyScope.Check("PipeLines", "Absent",
            _ => true, (_, _) => TagPropertyAvailability.Missing));
    }

    /// <summary>可选字段前缀不应进入传给 SDK 的类别名。</summary>
    [Fact]
    public void OptionalClassPrefixIsRemoved()
    {
        Assert.Equal(TagPropertyAvailability.Present, TagPropertyScope.Check("+PipeLineGroup", "Service",
            _ => false, (scope, _) =>
            {
                Assert.Equal("PipeLineGroup", scope);
                return TagPropertyAvailability.Present;
            }));
    }

    /// <summary>属性存在且空值时，存在性由 SDK 成功读取确定，不按值非空判断。</summary>
    [Fact]
    public void PresentPropertyIsNotConfusedWithEmptyValue()
    {
        Assert.Equal(TagPropertyAvailability.Present, TagPropertyScope.Check("TargetObject", "Area",
            _ => true, (_, _) => throw new InvalidOperationException()));
    }
}
