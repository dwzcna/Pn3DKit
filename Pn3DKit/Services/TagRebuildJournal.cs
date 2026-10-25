using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>独占创建重建日志和 CSV；预览及最终结果追加到同一历史，逐条刷新。</summary>
public sealed class TagRebuildJournal : ITagRebuildJournal, IDisposable
{
    private readonly FileStream _logStream;
    private readonly FileStream _csvStream;
    private readonly StreamWriter _log;
    private readonly StreamWriter _csv;
    /// <summary>文本日志完整路径。</summary>
    public string LogPath { get; }
    /// <summary>CSV 完整路径，同句柄的最后一条是最终动作。</summary>
    public string CsvPath { get; }

    /// <summary>两文件均以 CreateNew 打开后才开始检查，不覆盖任何已有日志。</summary>
    public TagRebuildJournal(string directory, string drawing, IEnumerable<string> classes)
    {
        directory = TagRebuildPaths.Validate(directory);
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, "TagRebuild.log");
        CsvPath = Path.Combine(directory, "TagRebuild.csv");
        _logStream = new(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try { _csvStream = new(CsvPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch { _logStream.Dispose(); throw; }
        _log = new(_logStream, new UTF8Encoding(false), 4096, true);
        _csv = new(_csvStream, new UTF8Encoding(true), 4096, true) { NewLine = "\r\n" };
        try
        {
            _log.WriteLine("Pn3DKit | REBUILDPNTAGBYCLASS");
            _log.WriteLine("图纸：" + drawing);
            _log.WriteLine("所选类别：" + string.Join("、", classes));
            _log.WriteLine("开始时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            _log.WriteLine("CSV 为追加历史：同句柄最后一条为最终动作。仅写 PnId.Tag，不处理 Piping 或 PipeLines 及其子类。");
            _csv.WriteLine("类别,句柄,原Tag,新Tag,动作,原因,差异字段");
            Flush();
        }
        catch { Dispose(); throw; }
    }

    /// <inheritdoc />
    public void Append(TagRebuildRecord record)
    {
        var item = record.Item.Check;
        _log.WriteLine($"[{item.Subject.ProjectPart} / {item.Subject.ClassName} / 句柄 {item.Subject.Handle}] {record.ActionText} | {item.Subject.ExistingTag} → {item.ExpectedTag} | {record.Reason}");
        if (item.Details.Length != 0) _log.WriteLine(item.Details);
        _csv.WriteLine(string.Join(",", new[] { item.Subject.ClassName, item.Subject.Handle,
            item.Subject.ExistingTag, item.ExpectedTag, record.ActionText, record.Reason, item.Details }
            .Select(TagCheckJournal.EscapeCsv)));
        Flush();
    }

    /// <inheritdoc />
    public void Message(string message)
    {
        _log.WriteLine(message);
        Flush();
    }

    /// <summary>写入结束状态及全部所选类别的最终动作统计。</summary>
    public void Complete(IReadOnlyList<TagRebuildRecord> records, string status)
    {
        Message("结束时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "；批次状态：" + status);
        foreach (var group in records.GroupBy(x => x.Item.Check.Subject.ClassName))
            Message(group.Key + "：" + TagRebuildPolicy.Summary(group));
        Message("合计：" + TagRebuildPolicy.Summary(records));
    }

    /// <summary>先刷新编码器再同步文件，确保取消时已有记录保留。</summary>
    private void Flush()
    {
        _log.Flush(); _csv.Flush();
        _logStream.Flush(true); _csvStream.Flush(true);
    }

    /// <summary>任一释放失败仍尝试释放其余流。</summary>
    public void Dispose()
    {
        try { _csv.Dispose(); }
        finally
        {
            try { _log.Dispose(); }
            finally { try { _csvStream.Dispose(); } finally { _logStream.Dispose(); } }
        }
    }
}
