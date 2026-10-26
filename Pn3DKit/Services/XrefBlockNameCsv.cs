using System.Text;

namespace Pn3DKit.Services;

/// <summary>名称清单及索引的写入故障必须停止批次，不能转为普通来源读取失败后继续。</summary>
public sealed class XrefBlockLogException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>只处理名称文本和文件缓冲；定时器不接触 CAD、项目或主报告。</summary>
/// <remarks>追加、定时刷新和关闭共用同一锁；故障由主线程检查点重新抛出。</remarks>
internal sealed class XrefBlockNameCsv : IDisposable
{
    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly ITimer _timer;
    private Exception? _failure;
    private bool _closed;
    private long _written, _flushed;
    /// <summary>对应清单的完整路径，用于错误追溯。</summary>
    internal string Path { get; }
    /// <summary>完整交给文本写入器的行数；不承诺发生故障的那一行完整落盘。</summary>
    internal long Written { get { lock (_gate) return _written; } }
    /// <summary>最近一次成功刷新已确认的完整行数。</summary>
    internal long Flushed { get { lock (_gate) return _flushed; } }
    /// <summary>创建后立即写出带 BOM 的表头，之后才允许名称扫描开始。</summary>
    internal XrefBlockNameCsv(string path, Stream stream, TimeProvider clock)
    {
        Path = path; _stream = stream;
        try
        {
            _writer = new(stream, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" };
            _writer.WriteLine("块名称,是否匿名");
            _writer.Flush();
            _timer = clock.CreateTimer(_ => FlushFromTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            Exception failure = ex;
            try { stream.Dispose(); }
            catch (Exception close) { failure = new AggregateException(ex, close); }
            throw new XrefBlockLogException("创建来源名称清单失败：" + path + "；" + failure.Message, failure);
        }
    }

    /// <summary>每个有效名称调用一次；50 行刷新，不足 50 行时在首个待刷新名称后安排一秒计时。</summary>
    internal void Append(string name, bool anonymous)
    {
        lock (_gate)
        {
            ThrowFailure();
            if (_closed) throw new XrefBlockLogException("名称清单已经关闭：" + Path);
            try
            {
                _writer.WriteLine(TagCheckJournal.EscapeCsv(name) + "," + (anonymous ? "是" : "否"));
                _written++;
                if (_written - _flushed >= 50) FlushBuffer();
                else if (_written - _flushed == 1
                    && !_timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan))
                    throw new IOException("无法启动来源清单刷新计时器。");
            }
            catch (Exception ex) { _failure ??= ex; ThrowFailure(); }
        }
    }

    /// <summary>主线程扫描检查点发现后台错误后立即停止；后台本身不输出宿主消息。</summary>
    internal void Check()
    {
        lock (_gate) ThrowFailure();
    }

    /// <summary>仅后台文件刷新，异常保存在本实例，不能逃出线程池回调。</summary>
    private void FlushFromTimer()
    {
        lock (_gate)
        {
            if (_closed || _failure != null || _written == _flushed) return;
            try { FlushBuffer(); }
            catch (Exception ex) { _failure ??= ex; }
        }
    }

    /// <summary>只刷新编码器和文件缓冲，不调用强制磁盘刷新。</summary>
    private void FlushBuffer()
    {
        _writer.Flush();
        _flushed = _written;
        if (!_timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan))
            throw new IOException("无法取消来源清单刷新计时器。");
    }

    /// <summary>调用方已持锁，保留首次故障及清单路径。</summary>
    private void ThrowFailure()
    {
        if (_failure != null)
            throw new XrefBlockLogException("来源名称清单写入或关闭失败：" + Path + "；" + _failure.Message, _failure);
    }

    /// <summary>停止计时、最终刷新并关闭，任何一步失败也继续释放其他资源。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            // Dispose 不等待回调；仍在排队的回调取得锁后会因 _closed 直接返回。
            try { _timer.Dispose(); } catch (Exception ex) { _failure ??= ex; }
            try { _writer.Flush(); _flushed = _written; } catch (Exception ex) { _failure ??= ex; }
            try { _writer.Dispose(); } catch (Exception ex) { _failure ??= ex; }
            try { _stream.Dispose(); } catch (Exception ex) { _failure ??= ex; }
            ThrowFailure();
        }
    }
}
