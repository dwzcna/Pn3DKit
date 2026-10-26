using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>目录输入时上下文改变必须直接停止，不能被路径重试或浏览窗口吞掉。</summary>
public sealed class OutputDirectoryContextTests
{
    /// <summary>输入返回时原图纸或项目改变，两个 FILEDIA 分支都不能继续浏览或验证。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ContextChangeDuringInputStopsBeforeBrowseOrValidation(int fileDia)
    {
        bool changed = false;
        Assert.Throws<InvalidOperationException>(() => OutputDirectorySelection.Select(fileDia, "D:\\建议",
            _ => throw new Xunit.Sdk.XunitException("不能继续打开选择器"),
            _ => { changed = true; return fileDia == 0 ? "D:\\输出" : "C"; },
            _ => throw new Xunit.Sdk.XunitException("上下文错误不能作为路径错误重试"),
            _ => throw new Xunit.Sdk.XunitException("不能继续验证"),
            ensureContext: () => { if (changed) throw new InvalidOperationException("原图纸或项目已变化"); }));
    }

    /// <summary>选择器返回后上下文改变，即使异常类型为 IO 也不能降级为目录重试。</summary>
    [Fact]
    public void ContextChangeDuringBrowsePropagatesIoFailure()
    {
        bool changed = false; int prompts = 0;
        Assert.Throws<IOException>(() => OutputDirectorySelection.Select(1, "D:\\建议",
            _ => { changed = true; return "D:\\输出"; }, _ => { prompts++; return "C"; },
            _ => throw new Xunit.Sdk.XunitException("不能重试"),
            _ => throw new Xunit.Sdk.XunitException("不能验证"),
            ensureContext: () => { if (changed) throw new IOException("原数据库已关闭"); }));
        Assert.Equal(1, prompts);
    }

    /// <summary>上下文在路径验证期间变化，验证后检查失败不返回路径或重新发起输入。</summary>
    [Fact]
    public void ContextChangeDuringValidationIsNotAPathError()
    {
        bool changed = false; int prompts = 0;
        Assert.Throws<UnauthorizedAccessException>(() => OutputDirectorySelection.Select(0, "D:\\建议",
            _ => throw new Xunit.Sdk.XunitException("不能弹窗"), _ => { prompts++; return ""; },
            _ => throw new Xunit.Sdk.XunitException("不能重试"),
            path => { changed = true; return path; },
            ensureContext: () => { if (changed) throw new UnauthorizedAccessException("项目状态变化"); }));
        Assert.Equal(1, prompts);
    }

    /// <summary>第一次输入前上下文已经失效时，不能发起任何用户交互。</summary>
    [Fact]
    public void InvalidInitialContextNeverPrompts()
    {
        Assert.Throws<IOException>(() => OutputDirectorySelection.Select(1, "D:\\建议",
            _ => throw new Xunit.Sdk.XunitException("不能弹窗"),
            _ => throw new Xunit.Sdk.XunitException("不能提示"),
            _ => throw new Xunit.Sdk.XunitException("不能重试"),
            ensureContext: () => throw new IOException("原数据库无效")));
    }

    /// <summary>真实路径冲突仍可重试，前后上下文正常时不会改变 FILEDIA 选择方式。</summary>
    [Fact]
    public void RealValidationFailureRetriesAndKeepsContextChecks()
    {
        var inputs = new Queue<string>(["D:\\冲突", "D:\\输出"]);
        int errors = 0; bool inputSeen = false, validated = false;
        string? result = OutputDirectorySelection.Select(0, "D:\\建议",
            _ => throw new Xunit.Sdk.XunitException("不能弹窗"),
            _ => { inputSeen = true; return inputs.Dequeue(); }, _ => errors++,
            path => { if (path.EndsWith("冲突", StringComparison.Ordinal)) throw new IOException("已有日志"); validated = true; return path; },
            ensureContext: () => { if (validated) Assert.True(inputSeen); });
        Assert.Equal("D:\\输出", result); Assert.Equal(1, errors); Assert.Empty(inputs);
    }
}
