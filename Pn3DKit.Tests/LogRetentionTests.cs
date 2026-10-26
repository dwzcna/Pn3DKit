using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证默认选择与破坏性清理的文件归属、关闭要求、现场保护及失败保留。</summary>
public sealed class LogRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.Retention", Guid.NewGuid().ToString("N"));
    /// <summary>每例隔离临时目录。</summary>
    public LogRetentionTests() => Directory.CreateDirectory(_root);
    /// <summary>只删除本例临时目录。</summary>
    public void Dispose() => Directory.Delete(_root, true);
    private string FilePath(string name) => Path.Combine(_root, name);
    private static TagCheckResult Check(TagCheckStatus status) => new(new("PnId", "Valve", "1", "#(TargetObject.Tag)", "V-1"), "V-2", status, []);

    /// <summary>询问取消永远保留；回车遵循动态默认，只有 N 表示删除。</summary>
    [Theory]
    [InlineData(null, false, true)] [InlineData(null, true, true)]
    [InlineData("", false, false)] [InlineData("", true, true)]
    [InlineData("N", true, false)] [InlineData("n", false, false)]
    [InlineData("Y", false, true)]
    public void PromptSelection(string? input, bool issues, bool keep) => Assert.Equal(keep, LogRetentionPolicy.Keep(input, issues));

    /// <summary>检查六种状态中仅一致默认不保留。</summary>
    [Theory]
    [InlineData(TagCheckStatus.Consistent, false)] [InlineData(TagCheckStatus.Mismatch, true)]
    [InlineData(TagCheckStatus.NoFormat, true)] [InlineData(TagCheckStatus.NoTag, true)]
    [InlineData(TagCheckStatus.TemporaryTag, true)] [InlineData(TagCheckStatus.Error, true)]
    public void CheckStatuses(TagCheckStatus status, bool issues) =>
        Assert.Equal(issues, LogRetentionPolicy.CheckHasIssues([Check(status)], 1));

    /// <summary>未完成和中断即使已处理项一致也建议保留。</summary>
    [Fact] public void PartialCheckKeeps()
    {
        Assert.True(LogRetentionPolicy.CheckHasIssues([Check(TagCheckStatus.Consistent)], 2));
        Assert.True(LogRetentionPolicy.CheckHasIssues([Check(TagCheckStatus.Consistent)], 1, true));
    }

    /// <summary>重建最终动作决定默认值，普通一致跳过与实际写入成功均正常。</summary>
    [Theory]
    [InlineData(TagRebuildAction.Updated, TagCheckStatus.Mismatch, false)]
    [InlineData(TagRebuildAction.Skipped, TagCheckStatus.Consistent, false)]
    [InlineData(TagRebuildAction.Skipped, TagCheckStatus.Mismatch, true)]
    [InlineData(TagRebuildAction.Conflict, TagCheckStatus.Mismatch, true)]
    [InlineData(TagRebuildAction.Failed, TagCheckStatus.Mismatch, true)]
    [InlineData(TagRebuildAction.Unexecuted, TagCheckStatus.Mismatch, true)]
    public void RebuildOutcomes(TagRebuildAction action, TagCheckStatus status, bool issues) =>
        Assert.Equal(issues, LogRetentionPolicy.RebuildHasIssues([new(new("PnId:1", Check(status)), action, "")], 1));

    /// <summary>一致但由于范围排除的跳过并不是成功处理。</summary>
    [Fact] public void ExcludedConsistentRowKeeps() =>
        Assert.True(LogRetentionPolicy.RebuildHasIssues([new(new("PnId:1", Check(TagCheckStatus.Consistent), Exclusion: "unsupported"),
            TagRebuildAction.Skipped, "unsupported")], 1));

    /// <summary>导出必须全部成功；跳过、失败、未处理及停止均保留。</summary>
    [Theory]
    [InlineData("Exported", false)] [InlineData("Skipped", true)] [InlineData("Failed", true)]
    public void ExportOutcomes(string state, bool expected) => Assert.Equal(expected,
        LogRetentionPolicy.ExportHasIssues(new(1, [new("a", "b", state, "")], false, null, "")));

    /// <summary>批次不完整不得误判为正常。</summary>
    [Fact] public void StoppedAndIncompleteKeep()
    {
        Assert.True(LogRetentionPolicy.ExportHasIssues(new(1, [], false, null, "")));
        Assert.True(LogRetentionPolicy.ExportHasIssues(new(0, [], true, "cancel", "")));
        Assert.True(LogRetentionPolicy.RebuildHasIssues([], 1));
        Assert.True(LogRetentionPolicy.RebuildHasIssues([], 0, true));
    }

    /// <summary>自选目录中的用户文件、DWG 及临时 DWG 都不属于清理清单。</summary>
    [Fact] public void OnlyOwnedFilesAreDeleted()
    {
        var files = new RunLogFiles();
        string run = FilePath("run"); files.EnsureDirectory(run);
        using (files.CreateNew(Path.Combine(run, "run.log"))) { }
        foreach (string name in new[] { "source.dwg", ".__pn3d_a.dwg", "user.csv", "notes.txt" })
            System.IO.File.WriteAllText(Path.Combine(run, name), "keep");
        files.Seal(); var result = files.Delete();
        Assert.Equal(1, result.Deleted); Assert.Empty(result.Errors); Assert.Equal(4, Directory.GetFiles(run).Length);
        Assert.Throws<ArgumentException>(() => files.CreateNew(Path.Combine(run, "forbidden.dwg")));
    }

    /// <summary>先关闭流才能清理，防止交互发生在日志释放之前。</summary>
    [Fact] public void OpenStreamProtects()
    {
        var files = new RunLogFiles();
        using var stream = files.CreateNew(FilePath("open.log"));
        files.Seal(); Assert.NotNull(files.ProtectionReason);
        Assert.Equal(0, files.Delete().Deleted); Assert.True(System.IO.File.Exists(FilePath("open.log")));
    }

    /// <summary>未经封存即使文件已经关闭也不得删除。</summary>
    [Fact] public void UnsealedCannotDelete()
    {
        var files = new RunLogFiles();
        using (files.CreateNew(FilePath("closed.log"))) { }
        Assert.Equal(0, files.Delete().Deleted);
    }

    /// <summary>CreateNew 冲突不认领原有文件，部分创建仍准确登记。</summary>
    [Fact] public void ExistingFilesAreNotOwned()
    {
        string existing = FilePath("existing.csv"); System.IO.File.WriteAllText(existing, "original");
        var files = new RunLogFiles();
        using (files.CreateNew(FilePath("partial.log"))) { }
        Assert.Throws<IOException>(() => files.CreateNew(existing));
        files.Seal(); files.Delete();
        Assert.Equal("original", System.IO.File.ReadAllText(existing));
        Assert.Single(files.Paths);
    }

    /// <summary>一个文件被占用时仍清理其他本批文件，并明确记录剩余路径。</summary>
    [Fact] public void DeleteFailurePreservesRemaining()
    {
        var files = new RunLogFiles();
        string a = FilePath("a.log"), b = FilePath("b.csv");
        using (files.CreateNew(a)) { } using (files.CreateNew(b)) { }
        files.Seal();
        using var locked = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = files.Delete();
        Assert.Equal(1, result.Deleted); Assert.Contains(a, result.Remaining);
        Assert.NotEmpty(result.Errors); Assert.False(System.IO.File.Exists(b));
    }

    /// <summary>未知原生操作即使文件流已关闭也不可清理现场。</summary>
    [Fact] public void UnknownNativeOperationIsProtected()
    {
        var files = new RunLogFiles(); using (files.CreateNew(FilePath("run.log"))) { }
        files.ExpectNativeFile(FilePath("command.done"));
        files.Protect("still running"); files.Seal();
        Assert.Equal(0, files.Delete().Deleted);
    }

    /// <summary>完成标记与 LISP 只按登记路径清理，原生同名标记冲突不会被认领。</summary>
    [Fact] public void CompletedNativeFilesAreOwned()
    {
        var files = new RunLogFiles(); string marker = FilePath("qsave.done");
        files.ExpectNativeFile(marker); System.IO.File.WriteAllText(marker, "DONE");
        using (files.CreateNew(FilePath("qsave.command.lsp"))) { }
        Assert.Throws<IOException>(() => files.ExpectNativeFile(marker));
        files.Seal(); Assert.Equal(2, files.Delete().Deleted);
    }

    /// <summary>报告发布失败不删除已有正式报告。</summary>
    [Fact] public void PublishCollisionPreservesExisting()
    {
        var files = new RunLogFiles(); string tmp = FilePath("report.json.tmp"), report = FilePath("report.json");
        using (files.CreateNew(tmp)) { }
        System.IO.File.WriteAllText(report, "original");
        Assert.Throws<IOException>(() => files.Publish(tmp, report));
        files.Seal(); files.Delete(); Assert.Equal("original", System.IO.File.ReadAllText(report));
    }

    /// <summary>四种日志实现都在 Dispose 后可统一清理，保持预检与正式结果共用归属。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void JournalsCloseBeforeCleanup(int kind)
    {
        var files = new RunLogFiles(); string run = FilePath("run");
        using (IDisposable journal = kind switch
        {
            0 => new TagCheckJournal(run, "drawing", [], "", files),
            1 => new TagRebuildJournal(run, "drawing", [], files),
            2 => new ProjectTagJournal(run, false, "project", [], files),
            _ => new BatchJournal(run, files)
        }) { }
        files.Seal(); Assert.Null(files.ProtectionReason);
        var result = files.Delete(); Assert.Empty(result.Errors); Assert.Empty(result.Remaining);
        Assert.False(Directory.Exists(run));
    }

    /// <summary>原有空目录不属于批次，不因删除日志而删除。</summary>
    [Fact] public void ExistingDirectoryStays()
    {
        var files = new RunLogFiles(); files.EnsureDirectory(_root);
        using (files.CreateNew(FilePath("run.log"))) { }
        files.Seal(); files.Delete(); Assert.True(Directory.Exists(_root));
    }

    /// <summary>五类新路径同秒追加序号，建议生成不创建目录。</summary>
    [Theory]
    [InlineData(RunLogKind.Export)] [InlineData(RunLogKind.TagCheck)] [InlineData(RunLogKind.TagRebuild)]
    [InlineData(RunLogKind.ProjectTagCheck)] [InlineData(RunLogKind.ProjectTagRebuild)] [InlineData(RunLogKind.XrefBlockCheck)]
    public void UnifiedDirectories(RunLogKind kind)
    {
        DateTime time = new(2026, 10, 25, 1, 2, 3);
        string path = RunPaths.Suggest(_root, kind, time);
        Assert.Equal(Path.Combine(_root, "Pn3DKit", kind.ToString(), "261025010203"), path);
        Assert.False(Directory.Exists(path)); Directory.CreateDirectory(path);
        Assert.Equal(path + "_01", RunPaths.Suggest(_root, kind, time));
    }
}
