using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>串行写入已确认的快照；一次撤销组覆盖全部写入，Esc 保留已经完成的修改。</summary>
public static class TagRebuildBatch
{
    /// <summary>执行期间任何日志故障立即停止写入；始终尝试结束自己创建的撤销组。</summary>
    public static TagRebuildBatchResult Run(IReadOnlyList<TagRebuildPlan> plans, ITagRebuildHost host,
        ITagRebuildJournal journal, Action<int, int> progress)
    {
        var records = plans.Where(x => x.State != TagRebuildState.Ready).Select(TagRebuildPolicy.Preview).ToList();
        var pending = plans.Where(x => x.State == TagRebuildState.Ready).Select(x => x.Item)
            .OrderBy(x => x.Check.Subject.Kind == TagTargetKind.LineGroup ? 0 : 1).ToArray();
        bool undo = false, attempted = false, safe = true, logFailed = false;
        string? stop = null;
        void Record(TagRebuildRecord record)
        {
            records.Add(record);
            try { journal.Append(record); }
            catch { logFailed = true; throw; }
            if (records.Count % 50 == 0) progress(records.Count, plans.Count);
        }
        if (pending.Length == 0) return new(records, false, true, null);
        try
        {
            host.EnsureContext();
            host.BeginUndo();
            undo = true;
            journal.Message("UNDO BEGIN 已建立；Plant 数据缓存的撤销效果待实机确认，请保留日志，必要时请用日志恢复。");
            foreach (var item in pending)
            {
                host.EnsureContext();
                string? changed = host.Revalidate(item);
                if (changed != null) { Record(new(item, TagRebuildAction.Skipped, changed)); continue; }
                var owners = host.FindOwners(item);
                if (owners.Count != 0)
                { Record(new(item, TagRebuildAction.Conflict, "写入前发现占用：" + string.Join("、", owners))); continue; }
                journal.BeforeWrite(item);
                host.EnsureContext();
                attempted = true;
                TagRebuildRecord result;
                try
                {
                    string actual = host.WriteAndRead(item);
                    result = string.Equals(actual, item.Check.ExpectedTag, StringComparison.Ordinal)
                        ? new(item, TagRebuildAction.Updated, "Tag 回读一致")
                        : new(item, TagRebuildAction.Failed, "回读不符，实际 Tag：" + actual + "；不假定数据已回滚");
                }
                catch (OperationCanceledException ex)
                {
                    Record(new(item, TagRebuildAction.Failed, "写入被中断：" + ex.Message));
                    throw;
                }
                catch (TagRebuildWriteException ex) when (!ex.StateKnown)
                {
                    Record(new(item, TagRebuildAction.Failed, ex.Message));
                    throw;
                }
                catch (Exception ex) { result = new(item, TagRebuildAction.Failed, ex.Message); }
                Record(result);
            }
        }
        catch (OperationCanceledException ex) { stop = "已中断：" + ex.Message; }
        catch (Exception ex) { stop = "已停止：" + ex.Message; safe = false; }
        finally
        {
            if (undo)
            {
                try { host.EndUndo(); }
                catch (Exception ex) { safe = false; stop = (stop == null ? "" : stop + "；") + "撤销组收尾失败：" + ex.Message; }
            }
        }
        if (safe && attempted)
        {
            try { host.Regen(); }
            catch (Exception ex) { safe = false; stop = (stop == null ? "" : stop + "；") + "刷新失败：" + ex.Message; }
        }
        var done = records.Select(x => x.Item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var item in pending.Where(x => !done.Contains(x.Key)))
        {
            var record = new TagRebuildRecord(item, TagRebuildAction.Unexecuted, stop ?? "未执行");
            records.Add(record);
            if (!logFailed)
            {
                try { journal.Append(record); }
                catch (Exception ex) { logFailed = true; safe = false; stop = "日志写入失败：" + ex.Message; }
            }
        }
        if (!logFailed)
        {
            try
            {
                journal.Message("UNDO 收尾：" + (undo && safe ? "已完成" : undo ? "请检查上述停止原因" : "未建立，未写入"));
                journal.Message(stop ?? "写入循环完成");
                journal.Message(TagRebuildPolicy.Summary(records));
            }
            catch (Exception ex) { safe = false; stop = "日志写入失败：" + ex.Message; }
        }
        progress(records.Count, plans.Count);
        return new(records, attempted, safe && !logFailed, stop);
    }
}
