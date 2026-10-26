using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>离线验证数字字段调用路由、相邻字段边界及失败保护；SDK 数字输出由回调模拟。</summary>
public sealed class TagNumericFormattingTests
{
    private const string Format = "#(TargetObject.Type)-#(TargetObject.LoopNumber^NNN)#(TargetObject.Feature)-#(TargetObject.Number^@NNN)";
    private const string NumberToken = "#(TargetObject.LoopNumber^NNN)";

    /// <summary>构造带相邻 H 和原自动编号 102 的输入，数字格式化回调必须收到当前原值。</summary>
    private static TagCheckResult Check(string raw, string? formatted, string sdkResult = "I-H-102",
        string existing = "I-302H-102", string oldNumber = "302")
    {
        var subject = new TagCheckSubject("PnId", "Instrument", "2A3F", Format, existing);
        return TagCheckPolicy.Check(subject, () => sdkResult, () =>
        {
            string expected = TagCheckPolicy.FormatProperty("TargetObject.LoopNumber", raw, "NNN",
                (characters, input) =>
                {
                    Assert.Equal("NNN", characters);
                    Assert.Equal(raw, input);
                    return formatted;
                }, () => throw new InvalidOperationException("数字字段不应调用片段 Evaluate。"));
            return
            [
                new("#(TargetObject.Type)", "TargetObject.Type", "I", "I", "I", false),
                new(NumberToken, "TargetObject.LoopNumber", oldNumber, raw, expected, false),
                new("#(TargetObject.Feature)", "TargetObject.Feature", "H", "H", "H", false),
                new("#(TargetObject.Number^@NNN)", "TargetObject.Number", "102", "999", "", true)
            ];
        });
    }

    /// <summary>当前 302 保持一致；303 正确变化；2 使用 SDK 补零结果，始终保留相邻 H 和自动编号 102。</summary>
    [Theory]
    [InlineData("302", "302", "I-302H-102", TagCheckStatus.Consistent)]
    [InlineData("303", "303", "I-303H-102", TagCheckStatus.Mismatch)]
    [InlineData("2", "002", "I-002H-102", TagCheckStatus.Mismatch)]
    public void NumericFieldsPreserveAdjacentFeatureAndOriginalAutogen(string raw, string formatted,
        string expectedTag, TagCheckStatus status)
    {
        var result = Check(raw, formatted);
        Assert.Equal(status, result.Status);
        Assert.Equal(expectedTag, result.ExpectedTag);
        Assert.DoesNotContain(result.Differences, x => x.Property == "TargetObject.Feature"
            || x.Property == "TargetObject.Number");
        if (status == TagCheckStatus.Consistent) Assert.Empty(result.Differences);
        else
        {
            var difference = Assert.Single(result.Differences);
            Assert.Equal("TargetObject.LoopNumber", difference.Property);
            Assert.Equal("302", difference.TagValue);
            Assert.Equal(raw, difference.CurrentValue);
        }
    }

    /// <summary>既有 002 与原值 2 经 SDK 补零后相同，不能把补零当成不符。</summary>
    [Fact]
    public void PaddedCurrentNumberMatchesExistingTag() =>
        Assert.Equal(TagCheckStatus.Consistent,
            Check("2", "002", existing: "I-002H-102", oldNumber: "002").Status);

    /// <summary>N／NN／NNN 都把完整路径、格式和原值交给 SDK，不使用 Evaluate 或自行替代 SDK 格式化。</summary>
    [Theory]
    [InlineData("TargetObject.LoopNumber", "N", "302", "302")]
    [InlineData("TargetObject.Number", "NN", "2", "02")]
    [InlineData("PipeLineGroup.LineNumber", "NNN", "2", "002")]
    [InlineData("Project.CustomNumber", "N", "41", "SDK格式值")]
    public void NumeralRoutingIsGenericAndUsesRawProperty(string property, string format, string raw, string sdkValue)
    {
        int formatted = 0;
        string actual = TagCheckPolicy.FormatProperty(property, raw, format, (characters, input) =>
        {
            formatted++;
            Assert.Equal(format, characters);
            Assert.Equal(raw, input);
            return sdkValue;
        }, () => throw new InvalidOperationException("不能对数字片段求值。"));
        Assert.Equal(1, formatted);
        Assert.Equal(sdkValue, actual);
    }

    /// <summary>其他格式继续原 SDK 片段求值，不将字母、小写或带额外规则的格式误归为本轮数字格式。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA")]
    [InlineData("nn")]
    [InlineData("NNNN")]
    [InlineData("NNN$Project.Last")]
    public void OtherFormatsKeepOriginalEvaluation(string? format)
    {
        int evaluated = 0;
        string actual = TagCheckPolicy.FormatProperty("TargetObject.Feature", "H", format,
            (_, _) => throw new InvalidOperationException("不应调用数字格式化。"), () =>
            {
                evaluated++;
                return "H";
            });
        Assert.Equal("H", actual);
        Assert.Equal(1, evaluated);
    }

    /// <summary>数字非空却格式化为空或 null，错误保留路径、原值和格式，不采用 SDK 的缺号重建结果。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyNumeralResultCannotProduceIncompleteTag(string? formatted)
    {
        var result = Check("302", formatted);
        Assert.Equal(TagCheckStatus.Error, result.Status);
        Assert.Empty(result.ExpectedTag);
        Assert.Contains("字段格式化失败", result.Error);
        Assert.Contains("TargetObject.LoopNumber", result.Error);
        Assert.Contains("302", result.Error);
        Assert.Contains("NNN", result.Error);
        Assert.Contains("未提供 ErrorStatus", result.Error);
    }

    /// <summary>空属性可沿用 SDK 的空结果，但不能把非空数字格式化为空伪装成空属性。</summary>
    [Fact]
    public void TrulyEmptyRawPropertyMayRemainEmpty() =>
        Assert.Equal("", TagCheckPolicy.FormatProperty("TargetObject.LoopNumber", "", "NNN",
            (_, raw) => raw, () => throw new InvalidOperationException("不得求值。")));

    /// <summary>SDK 格式化异常保留状态与内部异常，包括可选字段也不能静默吞掉的 KeyNotFound。</summary>
    [Theory]
    [InlineData("SDK ErrorStatus=InvalidInput")]
    [InlineData("SDK ErrorStatus=KeyNotFound")]
    [InlineData("SDK DataLinks ErrorCode=DLM_INVALID_ROWID")]
    public void NumeralFailureRetainsSdkDiagnostic(string diagnostic)
    {
        var sdkError = new InvalidOperationException(diagnostic);
        var error = Assert.Throws<InvalidOperationException>(() =>
            TagCheckPolicy.FormatProperty("TargetObject.LoopNumber", "302", "NNN",
                (_, _) => throw sdkError, () => "incorrect"));
        Assert.Contains("字段格式化失败", error.Message);
        Assert.Contains("TargetObject.LoopNumber", error.Message);
        Assert.Contains("302", error.Message);
        Assert.Contains("NNN", error.Message);
        Assert.Contains(diagnostic, error.Message);
        Assert.Same(sdkError, error.InnerException);
    }

    /// <summary>格式化取消继续向批次传播，不能变成对象字段错误。</summary>
    [Fact]
    public void NumeralCancellationContinuesToBatch() =>
        Assert.Throws<OperationCanceledException>(() =>
            TagCheckPolicy.FormatProperty("TargetObject.LoopNumber", "302", "NNN",
                (_, _) => throw new OperationCanceledException(), () => "incorrect"));

    /// <summary>共享检查成功供重建使用；数字格式失败禁止生成候选，避免写入缺号位号。</summary>
    [Theory]
    [InlineData("302", TagRebuildState.Consistent)]
    [InlineData("303", TagRebuildState.Ready)]
    [InlineData("", TagRebuildState.Unavailable)]
    [InlineData(null, TagRebuildState.Unavailable)]
    public void RebuildPrecheckUsesValidatedNumericResult(string? formatted, TagRebuildState expected)
    {
        var check = Check(formatted == "303" ? "303" : "302", formatted);
        var candidate = new TagRebuildCandidate("PnId:42", check);
        var plan = Assert.Single(TagRebuildPolicy.Prepare([candidate], _ => []));
        Assert.Equal(expected, plan.State);
        if (expected == TagRebuildState.Unavailable)
        {
            Assert.Contains("字段格式化失败", plan.Reason);
            Assert.Empty(plan.Item.Check.ExpectedTag);
        }
        else Assert.Equal("I-" + formatted + "H-102", plan.Item.Check.ExpectedTag);
    }
}
