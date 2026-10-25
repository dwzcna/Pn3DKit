using System.Text;
using System.Text.Json;
using Pn3DKit.Models;

namespace Pn3DKit.Services;

/// <summary>逐图持久追加 JSONL，批次结束后一次生成四字段 JSON 报告。</summary>
/// <remarks>仅由宿主主线程使用；日志失败向调用方传播，使批次停止。</remarks>
public sealed class BatchJournal : IDisposable
{
    private readonly StreamWriter _log;
    private readonly StreamWriter _items;
    /// <summary>返回批次最终报告的完整路径。</summary>
    public string ReportPath { get; }

    /// <summary>创建独立日志目录，禁止覆盖已存在的日志。</summary>
    public BatchJournal(string directory)
    {
        string logDirectory = Path.Combine(directory, ".pn3dkit-logs");
        Directory.CreateDirectory(logDirectory);
        ReportPath = Path.Combine(logDirectory, "report.json");
        _log = new(new FileStream(Path.Combine(directory, "Export.log"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            _items = new(new FileStream(Path.Combine(logDirectory, "results.jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch { _log.Dispose(); throw; }
    }

    /// <summary>写入带本地时间的运行消息并即时刷新。</summary>
    public void Log(string message) => _log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>持久追加一个完成项；不反复重写完整报告。</summary>
    public void Append(DrawingResult item) => _items.WriteLine(JsonSerializer.Serialize(item));

    /// <summary>通过同目录临时文件发布最终报告；失败时保留 JSONL 和临时文件。</summary>
    public void Complete(IReadOnlyList<DrawingResult> items)
    {
        string temporary = ReportPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew))
            JsonSerializer.Serialize(stream, items, new JsonSerializerOptions { WriteIndented = true });
        File.Move(temporary, ReportPath, false);
    }

    /// <summary>释放两个日志流；任意流关闭失败仍释放另一个流。</summary>
    public void Dispose()
    {
        try { _items.Dispose(); }
        finally { _log.Dispose(); }
    }
}
