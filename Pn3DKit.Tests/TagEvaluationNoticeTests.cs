using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>离线验证四类日志在开始即写明求值副作用，逐条记录不重复提示或改变 CSV。</summary>
public sealed class TagEvaluationNoticeTests
{
    /// <summary>检查、重建及其项目级日志各记录一次说明，写入后即可共享读取。</summary>
    [Theory]
    [InlineData("check", 8)]
    [InlineData("rebuild", 9)]
    [InlineData("project-check", 9)]
    [InlineData("project-rebuild", 10)]
    public void NoticeIsFlushedOnceAndCsvShapeIsUnchanged(string kind, int columns)
    {
        string root = Path.Combine(Path.GetTempPath(), "Pn3DKit.Notice", Guid.NewGuid().ToString("N"));
        var subject = new TagCheckSubject("PnId", "Valve", "A1", "#(+TargetObject.Number^NN)", "V-01") { RowId = "1" };
        var result = new TagCheckResult(subject, "V-02", TagCheckStatus.Mismatch, []);
        var record = new TagRebuildRecord(new("PnId:1", result), TagRebuildAction.Pending, "预检");
        IDisposable? journal = null;
        try
        {
            string logPath, csvPath;
            Action append;
            if (kind == "check")
            {
                var log = new TagCheckJournal(root, "a.dwg", ["Valve"], "");
                journal = log; logPath = log.LogPath; csvPath = log.CsvPath; append = () => log.Append(result);
            }
            else if (kind == "rebuild")
            {
                var log = new TagRebuildJournal(root, "a.dwg", ["Valve"]);
                journal = log; logPath = log.LogPath; csvPath = log.CsvPath; append = () => log.Append(record);
            }
            else
            {
                bool rebuild = kind == "project-rebuild";
                var log = new ProjectTagJournal(root, rebuild, "project", ["Valve"]);
                journal = log; logPath = log.LogPath; csvPath = log.CsvPath;
                append = () => { if (rebuild) log.Append(record); else log.AppendCheck(result); };
            }
            Assert.Contains(TagEvaluationNotice.Text, ReadShared(logPath));
            append(); append();
            Assert.Equal(1, ReadShared(logPath).Split(TagEvaluationNotice.Text, StringSplitOptions.None).Length - 1);
            string csv = ReadShared(csvPath);
            Assert.DoesNotContain(TagEvaluationNotice.Text, csv);
            Assert.Equal(columns, csv.Split("\r\n")[0].Split(',').Length);
            Assert.Equal(3, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally
        {
            journal?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>日志仍打开时共享读取，确认提示不依赖 Dispose 才落盘。</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
