using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证 FILEDIA 分流、命令行路径及取消，任何测试都不加载 CAD。</summary>
public sealed class OutputDirectorySelectionTests
{
    /// <summary>FILEDIA=0 接受含中文与空格的路径及成对引号，绝不调用窗口。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileDiaZeroUsesCommandLineAndKeepsExactPath(bool quoted)
    {
        using var fixture = new Fixture();
        string? result = OutputDirectorySelection.Select(0, fixture.Suggested, NoDialog,
            suggested =>
            {
                Assert.Equal(fixture.Suggested, suggested);
                return quoted ? "\"" + fixture.Selected + "\"" : fixture.Selected;
            }, NoError);
        Assert.Equal(fixture.Selected, result);
        Assert.False(Directory.Exists(fixture.Selected));
        Assert.False(Directory.Exists(fixture.Suggested));
    }

    /// <summary>命令行直接回车采用建议目录，不创建任何批次文件。</summary>
    [Fact]
    public void CommandLineEnterUsesSuggestedDirectory()
    {
        using var fixture = new Fixture();
        Assert.Equal(fixture.Suggested, OutputDirectorySelection.Select(0, fixture.Suggested,
            NoDialog, _ => "", NoError));
        Assert.False(Directory.Exists(fixture.Suggested));
    }

    /// <summary>命令行取消立即返回，不改为弹窗或采用默认值。</summary>
    [Fact]
    public void CommandLineCancelDoesNotFallBackToDialog()
    {
        using var fixture = new Fixture();
        Assert.Null(OutputDirectorySelection.Select(0, fixture.Suggested, NoDialog, _ => null, NoError));
        Assert.False(Directory.Exists(fixture.Suggested));
    }

    /// <summary>相对路径与已有批次日志均在命令行重新提示，不弹出错误或目录窗口。</summary>
    [Fact]
    public void InvalidPathsRetryOnlyOnCommandLine()
    {
        using var fixture = new Fixture();
        string occupied = Path.Combine(fixture.Root, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "Export.log"), "keep");
        var inputs = new Queue<string>(["relative", occupied, fixture.Selected]);
        var errors = new List<string>();
        string? result = OutputDirectorySelection.Select(0, fixture.Suggested, NoDialog,
            _ => inputs.Dequeue(), errors.Add);
        Assert.Equal(fixture.Selected, result);
        Assert.Empty(inputs);
        Assert.Equal(2, errors.Count);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(occupied, "Export.log")));
        Assert.False(Directory.Exists(fixture.Selected));
    }

    /// <summary>FILEDIA=1 先提示命令行，输入或点击 C 后仅打开一次系统文件夹选择器。</summary>
    [Theory]
    [InlineData("C")]
    [InlineData("c")]
    public void FileDiaOneUsesDialogOnlyAfterBrowseChoice(string choice)
    {
        using var fixture = new Fixture();
        int calls = 0, prompts = 0;
        string? result = OutputDirectorySelection.Select(1, fixture.Suggested, suggested =>
        {
            Assert.Equal(1, prompts);
            calls++;
            Assert.Equal(fixture.Suggested, suggested);
            return fixture.Selected;
        }, suggested =>
        {
            Assert.Equal(fixture.Suggested, suggested);
            prompts++;
            return choice;
        }, NoError);
        Assert.Equal(fixture.Selected, result);
        Assert.Equal(1, calls);
        Assert.Equal(1, prompts);
        Assert.False(Directory.Exists(fixture.Selected));
    }

    /// <summary>选择器取消结束批次，不再提示命令行或静默采用建议目录。</summary>
    [Fact]
    public void DialogCancelDoesNotFallBackToCommandLine()
    {
        using var fixture = new Fixture();
        int prompts = 0;
        Assert.Null(OutputDirectorySelection.Select(1, fixture.Suggested, _ => null,
            _ => { Assert.Equal(0, prompts++); return "C"; }, NoError));
        Assert.Equal(1, prompts);
        Assert.False(Directory.Exists(fixture.Suggested));
    }

    /// <summary>FILEDIA=1 回车直接采用建议目录，不弹出任何选择器。</summary>
    [Fact]
    public void FileDiaOneEnterKeepsSuggestedDirectoryWithoutDialog()
    {
        using var fixture = new Fixture();
        Assert.Equal(fixture.Suggested, OutputDirectorySelection.Select(1, fixture.Suggested,
            NoDialog, _ => "", NoError));
        Assert.False(Directory.Exists(fixture.Suggested));
    }

    /// <summary>FILEDIA=1 在 C 提示处取消不会弹窗或创建目录。</summary>
    [Fact]
    public void FileDiaOnePromptCancelDoesNotOpenDialog()
    {
        using var fixture = new Fixture();
        Assert.Null(OutputDirectorySelection.Select(1, fixture.Suggested, NoDialog, _ => null, NoError));
        Assert.False(Directory.Exists(fixture.Suggested));
    }

    /// <summary>选择已有日志的目录后回到命令行，必须再次明确选 C 才重新打开选择器。</summary>
    [Fact]
    public void OccupiedPickedDirectoryReturnsToCommandPrompt()
    {
        using var fixture = new Fixture();
        string occupied = Path.Combine(fixture.Root, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "Export.log"), "keep");
        int prompts = 0, dialogs = 0;
        var errors = new List<string>();
        string? result = OutputDirectorySelection.Select(1, fixture.Suggested, _ =>
        {
            Assert.Equal(dialogs + 1, prompts);
            return ++dialogs == 1 ? occupied : fixture.Selected;
        }, _ => { prompts++; return "C"; }, errors.Add);
        Assert.Equal(fixture.Selected, result);
        Assert.Equal(2, prompts);
        Assert.Equal(2, dialogs);
        Assert.Single(errors);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(occupied, "Export.log")));
        Assert.False(Directory.Exists(fixture.Selected));
    }

    /// <summary>FILEDIA=0 的 C 仍作为无效目录重新提示，不能触发新增浏览行为。</summary>
    [Fact]
    public void FileDiaZeroDoesNotTreatCAsBrowseKeyword()
    {
        using var fixture = new Fixture();
        var inputs = new Queue<string>(["C", fixture.Selected]);
        var errors = new List<string>();
        Assert.Equal(fixture.Selected, OutputDirectorySelection.Select(0, fixture.Suggested, NoDialog,
            _ => inputs.Dequeue(), errors.Add));
        Assert.Single(errors);
        Assert.Empty(inputs);
        Assert.False(Directory.Exists(fixture.Selected));
    }

    /// <summary>没有明确选择 C 的场景不得调用选择器。</summary>
    private static string? NoDialog(string _) => throw new InvalidOperationException("未选择 C，不得弹窗。");
    /// <summary>成功路径不应报告错误。</summary>
    private static void NoError(string message) => throw new InvalidOperationException(message);

    /// <summary>只持有本测试独享的临时目录，不接触用户项目。</summary>
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Pn3DKit.FileDia", Guid.NewGuid().ToString("N"));
        internal string Suggested => Path.Combine(Root, "suggested");
        internal string Selected => Path.Combine(Root, "用户选择 export 目录");
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
