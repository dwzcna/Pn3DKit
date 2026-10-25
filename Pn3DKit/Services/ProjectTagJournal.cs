using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>项目级即时日志，保留图纸映射及以项目部分和数据行识别的追加历史。</summary>
public sealed class ProjectTagJournal : ITagRebuildJournal, IDisposable
{
    private readonly FileStream _logFile, _csvFile;
    private readonly StreamWriter _log, _csv;
    /// <summary>文本日志完整路径。</summary>
    public string LogPath { get; }
    /// <summary>UTF-8 BOM CSV 完整路径。</summary>
    public string CsvPath { get; }

    /// <summary>两文件都使用 CreateNew，成功后才写入参数。</summary>
    public ProjectTagJournal(string directory, bool rebuild, string project, IEnumerable<string> classes)
    {
        directory = ProjectTagPolicy.Validate(directory, rebuild);
        Directory.CreateDirectory(directory);
        string name = rebuild ? "ProjectTagRebuild" : "ProjectTagCheck";
        LogPath = Path.Combine(directory, name + ".log");
        CsvPath = Path.Combine(directory, name + ".csv");
        _logFile = new(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try { _csvFile = new(CsvPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch { _logFile.Dispose(); throw; }
        _log = new(_logFile, new UTF8Encoding(false), 4096, true);
        _csv = new(_csvFile, new UTF8Encoding(true), 4096, true) { NewLine = "\r\n" };
        try
        {
            _log.WriteLine("Pn3DKit | " + (rebuild ? "REBUILDPROJECTPNTAGBYCLASS" : "CHECKPROJECTPNTAGBYCLASS"));
            _log.WriteLine("项目：" + project + "；类别：" + string.Join("、", classes));
            _log.WriteLine("开始时间：" + DateTimeOffset.Now.ToString("O"));
            _csv.WriteLine(rebuild
                ? "项目部分,类别,数据行ID,关联图纸,句柄,原Tag,新Tag,动作,原因,差异字段"
                : "项目部分,类别,数据行ID,关联图纸,句柄,现有Tag,期望Tag,状态,差异字段");
            Flush();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>只读检查结果也逐条刷新，不生成最终写入动作。</summary>
    public void AppendCheck(TagCheckResult item)
    {
        var s = item.Subject;
        _log.WriteLine($"[{s.ProjectPart}/{s.ClassName}/{s.Identity}] {item.Message}");
        if (item.Details.Length != 0) _log.WriteLine(item.Details);
        Csv([s.ProjectPart, s.ClassName, s.RowId, string.Join("\n", s.Drawings), s.Handle,
            s.ExistingTag, item.ExpectedTag, item.StatusText, Details(item)]);
    }

    /// <inheritdoc />
    public void Append(TagRebuildRecord record)
    {
        var item = record.Item.Check;
        var s = item.Subject;
        _log.WriteLine($"[{s.ProjectPart}/{s.ClassName}/{s.Identity}] {record.ActionText} | {s.ExistingTag} → {item.ExpectedTag} | {record.Reason}");
        Csv([s.ProjectPart, s.ClassName, s.RowId, string.Join("\n", s.Drawings), s.Handle,
            s.ExistingTag, item.ExpectedTag, record.ActionText, record.Reason, Details(item)]);
    }

    /// <summary>组成员没有自身图形句柄，单独保留关联成员说明。</summary>
    private static string Details(TagCheckResult item) => string.Join("\n",
        new[] { item.Details, item.Subject.RelatedHandles.Length == 0 ? "" : "关联成员：" + item.Subject.RelatedHandles }
            .Where(x => x.Length != 0));

    /// <summary>使用统一的 RFC4180 转义及 CRLF 行尾。</summary>
    private void Csv(string[] cells)
    {
        _csv.WriteLine(string.Join(",", cells.Select(TagCheckJournal.EscapeCsv)));
        Flush();
    }
    /// <inheritdoc />
    public void Message(string message) { _log.WriteLine(message); Flush(); }
    /// <summary>记录结束时间和真实批次状态。</summary>
    public void Complete(string status) => Message("结束时间：" + DateTimeOffset.Now.ToString("O") + "；批次状态：" + status);
    /// <summary>每条记录同步文本和文件流。</summary>
    private void Flush() { _log.Flush(); _csv.Flush(); _logFile.Flush(true); _csvFile.Flush(true); }
    /// <summary>释放所有流，任何释放错误不阻止其他流清理。</summary>
    public void Dispose()
    {
        try { _csv.Dispose(); }
        finally { try { _log.Dispose(); } finally { try { _csvFile.Dispose(); } finally { _logFile.Dispose(); } } }
    }
}
