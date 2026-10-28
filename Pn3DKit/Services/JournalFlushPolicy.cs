namespace Pn3DKit.Services;

/// <summary>普通记录逐条可见，主线程按五十条或一秒持久化；写前和收尾立即持久化。</summary>
internal sealed class JournalFlushPolicy(Action buffers, Action persist, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _last = (clock ?? TimeProvider.System).GetTimestamp();
    private int _pending;
    private Exception? _failure;

    /// <summary>刷新文件缓存并检查阈值；失败状态不可由后续成功清除。</summary>
    internal void Record(bool durable = false)
    {
        if (_failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
        try
        {
            buffers(); _pending++;
            if (durable || _pending >= 50 || _clock.GetElapsedTime(_last) >= TimeSpan.FromSeconds(1))
            { persist(); _pending = 0; _last = _clock.GetTimestamp(); }
        }
        catch (Exception ex) { _failure = ex; throw; }
    }

    /// <summary>异常收尾仍尽力持久化；已传播的旧失败仍保持粘性，新的收尾故障独立报告。</summary>
    internal void Finish()
    {
        Exception? final = null;
        try { buffers(); persist(); } catch (Exception ex) { final = ex; }
        if (final != null)
            throw new IOException("日志最终持久化失败或此前输出不完整。",
                new AggregateException(new[] { _failure, final }.OfType<Exception>()));
        _pending = 0; _last = _clock.GetTimestamp();
    }

    /// <summary>逐个持久化文件，保留全部错误；非文件流供离线替身使用。</summary>
    internal static void Persist(params Stream[] streams)
    {
        var errors = new List<Exception>();
        foreach (var stream in streams)
            try { if (stream is FileStream file) file.Flush(true); else stream.Flush(); }
            catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new IOException("日志文件持久化失败。", new AggregateException(errors));
    }

    /// <summary>最终刷新失败也继续关闭全部资源。</summary>
    internal static void Close(Action finish, params IDisposable[] resources)
    {
        var errors = new List<Exception>();
        try { finish(); } catch (Exception ex) { errors.Add(ex); }
        foreach (var resource in resources)
            try { resource.Dispose(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new IOException("日志收尾失败。", new AggregateException(errors));
    }
}
