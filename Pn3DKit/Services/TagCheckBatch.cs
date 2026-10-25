using Pn3DKit.Models;

namespace Pn3DKit.Services;

/// <summary>串行执行已固定的对象清单，在对象之间处理取消并保留已完成日志。</summary>
public static class TagCheckBatch
{
    /// <summary>在调用线程执行检查；CAD 实现负责在 beforeEach 中检测 Esc 和上下文。</summary>
    /// <param name="items">已固定的所选类别对象清单。</param>
    /// <param name="check">与单个命令相同的逐对象检查；其 SDK 求值可能更新序列字段。</param>
    /// <param name="beforeEach">检查取消及项目／文档身份；取消抛出 OperationCanceledException。</param>
    /// <param name="journal">已独占打开两件文件的日志。</param>
    /// <param name="progress">每五十条及结束时输出已处理与总数。</param>
    /// <returns>仅包含已检查并写入两个日志的结果。</returns>
    public static TagCheckBatchResult Run<T>(IReadOnlyList<T> items, Func<T, TagCheckResult> check,
        Action beforeEach, TagCheckJournal journal, Action<int, int> progress)
    {
        var results = new List<TagCheckResult>();
        bool interrupted = false;
        string? stopReason = null;
        try
        {
            foreach (T item in items)
            {
                beforeEach();
                var result = check(item);
                journal.Append(result);
                results.Add(result);
                if (results.Count % 50 == 0) progress(results.Count, items.Count);
            }
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            stopReason = "用户取消，已中断。";
        }
        catch (Exception ex) { stopReason = ex.Message; }
        var batch = new TagCheckBatchResult(items.Count, results, interrupted, stopReason);
        journal.Complete(batch);
        if (results.Count == 0 || results.Count % 50 != 0) progress(results.Count, items.Count);
        return batch;
    }
}
