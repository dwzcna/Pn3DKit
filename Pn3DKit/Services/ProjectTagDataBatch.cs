using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>数据模式不进入 DWG 撤销组，已有修改、提交异常或日志失败立即停止。</summary>
public static class ProjectTagDataBatch
{
    /// <summary>只操作预检并确认的行；默认模式由命令入口明确显示为 N。</summary>
    public static ProjectTagBatchResult Run(IReadOnlyList<TagRebuildPlan> plans, IProjectTagDataHost host,
        ITagRebuildJournal journal, Action<int, int> progress)
    {
        var records = plans.Where(x => x.State != TagRebuildState.Ready).Select(TagRebuildPolicy.Preview).ToList();
        var work = plans.Where(x => x.State == TagRebuildState.Ready).Select(x => x.Item)
            .OrderBy(x => x.Check.Subject.Kind == TagTargetKind.LineGroup ? 0 : 1).ToArray();
        string? stop = null;
        bool logFailed = false;
        void Append(TagRebuildRecord record)
        {
            records.Add(record);
            try { journal.Append(record); }
            catch { logFailed = true; throw; }
            if (records.Count % 50 == 0) progress(records.Count, plans.Count);
        }
        try
        {
            host.EnsureContext();
            if (host.HasPendingChanges())
                throw new InvalidOperationException("相关项目数据库已有未提交修改。未写入；请先保存／处理，或重新选择 Y 打开图纸模式。");
            journal.Message("模式 N：只提交项目数据，不打开或保存 DWG；不保证 DWG UNDO 回退，按日志恢复。");
            foreach (var item in work)
            {
                host.EnsureContext();
                if (host.HasPendingChanges()) throw new InvalidOperationException("出现未归属的项目待提交修改，停止后续写入。");
                string? changed = host.Revalidate(item);
                if (changed != null) { Append(new(item, TagRebuildAction.Skipped, changed)); continue; }
                var owners = host.FindOwners(item);
                if (owners.Count != 0) { Append(new(item, TagRebuildAction.Conflict, "写入前占用：" + string.Join("、", owners))); continue; }
                journal.Message("准备提交项目行：" + item.Key + "；" + item.Check.Subject.ExistingTag + " → " + item.Check.ExpectedTag);
                host.EnsureContext();
                try
                {
                    string actual = host.WriteAndCommit(item);
                    if (!string.Equals(actual, item.Check.ExpectedTag, StringComparison.Ordinal))
                    {
                        Append(new(item, TagRebuildAction.Failed, "提交后回读不符，实际 Tag：" + actual));
                        throw new InvalidOperationException("项目提交后回读不符，停止后续写入。");
                    }
                    Append(new(item, TagRebuildAction.Updated, "项目数据已提交，Tag 回读一致；未保存 DWG"));
                }
                catch (Exception ex)
                {
                    if (records.All(x => x.Item.Key != item.Key))
                        Append(new(item, TagRebuildAction.Failed, ex.Message));
                    throw;
                }
            }
        }
        catch (OperationCanceledException ex) { stop = "已中断：" + ex.Message; }
        catch (Exception ex) { stop = "已停止：" + ex.Message; }
        var done = records.Select(x => x.Item.Key).ToHashSet();
        foreach (var item in work.Where(x => !done.Contains(x.Key)))
        {
            var record = new TagRebuildRecord(item, TagRebuildAction.Unexecuted, stop ?? "未执行");
            records.Add(record);
            if (!logFailed)
            {
                try { journal.Append(record); }
                catch (Exception ex) { logFailed = true; stop = "日志写入失败：" + ex.Message; }
            }
        }
        progress(records.Count, plans.Count);
        return new(records, [], stop);
    }
}
