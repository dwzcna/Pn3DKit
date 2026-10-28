using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>独占创建位号日志与带 BOM 的 CSV，每条完成记录刷新文件缓存并按阈值持久化。</summary>
/// <remarks>仅由 CAD 主线程或离线测试串行使用；写入失败向调用方传播并停止批次。</remarks>
public sealed class TagCheckJournal : IDisposable
{
    private readonly RunLogFiles _files;
    private readonly JournalFlushPolicy _flush;
    private bool _disposed;
    private readonly FileStream _logStream;
    private readonly FileStream _csvStream;
    private readonly StreamWriter _log;
    private readonly StreamWriter _csv;
    private readonly string[] _classes;

    /// <summary>返回文本日志的完整路径。</summary>
    public string LogPath { get; }
    /// <summary>返回 CSV 的完整路径。</summary>
    public string CsvPath { get; }

    /// <summary>两件日志均成功打开后才写入运行参数，不覆盖既有文件。</summary>
    public TagCheckJournal(string directory, string drawing, IReadOnlyList<string> classes, string skipped, RunLogFiles? files = null)
    {
        _files = files ?? new RunLogFiles();
        directory = TagCheckPaths.Validate(directory);
        _files.EnsureDirectory(directory);
        LogPath = Path.Combine(directory, "TagCheck.log");
        CsvPath = Path.Combine(directory, "TagCheck.csv");
        _classes = classes.ToArray();
        _logStream = _files.CreateNew(LogPath);
        try { _csvStream = _files.CreateNew(CsvPath); }
        catch { _logStream.Dispose(); throw; }
        _log = new StreamWriter(_logStream, new UTF8Encoding(false), 4096, leaveOpen: true);
        _csv = new StreamWriter(_csvStream, new UTF8Encoding(true), 4096, leaveOpen: true) { NewLine = "\r\n" };
        _flush = new(() => { _log.Flush(); _csv.Flush(); }, () => JournalFlushPolicy.Persist(_logStream, _csvStream));
        try
        {
            _log.WriteLine("Pn3DKit | CHECKPNTAGBYCLASS");
            _log.WriteLine("图纸：" + drawing);
            _log.WriteLine("所选类别：" + string.Join("、", _classes));
            _log.WriteLine("开始时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            _log.WriteLine(TagEvaluationNotice.Text);
            if (skipped.Length != 0) _log.WriteLine(skipped);
            _csv.WriteLine("项目部分,类别,数据行ID,句柄,现有Tag,期望Tag,状态,差异字段");
            Flush();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>按 RFC 4180 转义字段，保留换行、引号、空格和中文。</summary>
    public static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>将同一对象结果追加到两个文件；错误或中断时已刷新记录仍可读取。</summary>
    public void Append(TagCheckResult item)
    {
        var s = item.Subject;
        _log.WriteLine($"[{s.ProjectPart} / {s.ClassName} / {s.Identity}] {item.StatusText} | {item.Message}");
        _log.WriteLine("现有Tag：" + s.ExistingTag + "；期望Tag：" + item.ExpectedTag);
        if (s.Format.Length != 0) _log.WriteLine("格式：" + s.Format);
        if (item.Details.Length != 0) _log.WriteLine(item.Details);
        _csv.WriteLine(string.Join(",", new[] { s.ProjectPart, s.ClassName, s.RowId, s.Handle, s.ExistingTag,
            item.ExpectedTag, item.StatusText, item.Details }.Select(EscapeCsv)));
        Flush();
    }

    /// <summary>写入结束状态及所有所选类别的统计，包括没有执行到的类别。</summary>
    public void Complete(TagCheckBatchResult result)
    {
        _log.WriteLine("结束时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        _log.WriteLine("批次状态：" + (result.Interrupted ? "已中断"
            : result.StopReason == null ? "已完成" : "已停止"));
        if (result.StopReason != null) _log.WriteLine("停止原因：" + result.StopReason);
        _log.WriteLine($"总计 {result.Total}；已处理 {result.Items.Count}；未处理 {result.Remaining}");
        foreach (string className in _classes)
            _log.WriteLine(className + "：" + TagCheckPolicy.Summary(
                result.Items.Where(x => x.Subject.ClassName == className)));
        _flush.Record(durable: true);
    }

    /// <summary>普通记录刷新文件缓存，按五十条或一秒检查点强制持久化。</summary>
    private void Flush() => _flush.Record();

    /// <summary>任一释放失败时仍尝试释放其余资源，不持有宿主文件句柄。</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        JournalFlushPolicy.Close(_flush.Finish, _csv, _log, _csvStream, _logStream);
    }
}
