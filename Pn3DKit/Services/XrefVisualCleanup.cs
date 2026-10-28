namespace Pn3DKit.Services;

/// <summary>合并绘制主异常和释放异常；不把清理故障降为普通未验证。</summary>
internal static class XrefVisualCleanup
{
    /// <summary>同步执行并清理；无释放错误时完整保留原始异常及堆栈。</summary>
    internal static T Run<T>(Func<T> action, Action cleanup, string message)
    {
        Exception? primary = null;
        try { return action(); }
        catch (Exception ex) { primary = ex; throw; }
        finally
        {
            try { cleanup(); }
            catch (Exception ex) { ThrowIfFailed(primary, [ex], message); }
        }
    }

    /// <summary>惰性消费来源，异常和提前关闭时均释放自身迭代器及资源。</summary>
    internal static IEnumerable<T> Iterate<T>(IEnumerable<T> source, Action cleanup, string message)
    {
        IEnumerator<T>? iterator = null;
        Exception? primary = null;
        try
        {
            while (true)
            {
                bool more;
                T current = default!;
                try
                {
                    iterator ??= source.GetEnumerator();
                    more = iterator.MoveNext();
                    if (more) current = iterator.Current;
                }
                catch (Exception ex) { primary = ex; throw; }
                if (!more) yield break;
                yield return current;
            }
        }
        finally
        {
            var errors = new List<Exception>();
            try { iterator?.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            try { cleanup(); } catch (Exception ex) { errors.Add(ex); }
            ThrowIfFailed(primary, errors, message);
        }
    }

    /// <summary>仅存在释放故障时构造致命异常，原始异常排在汇总首位。</summary>
    internal static void ThrowIfFailed(Exception? primary, IReadOnlyList<Exception> errors, string message)
    {
        if (errors.Count == 0) return;
        throw new XrefVisualFatalException(message, new AggregateException(
            primary == null ? errors : new[] { primary }.Concat(errors)));
    }
}
