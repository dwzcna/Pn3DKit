using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>逐图同步已确认的项目行；数据行只写一次，所有关联图纸都验证刷新。</summary>
public static class ProjectTagDrawingBatch
{
    /// <summary>保存策略全批统一；不保存时保留全部修改图纸，不设置数量上限。</summary>
    public static async Task<ProjectTagBatchResult> RunAsync(IReadOnlyList<TagRebuildPlan> plans,
        bool saveChanges, string directory, IProjectTagDrawingHost host, ITagRebuildJournal journal,
        Action<int, int> progress, RunLogFiles? files = null)
    {
        var records = plans.Where(x => x.State != TagRebuildState.Ready)
            .Select(TagRebuildPolicy.Preview).ToDictionary(x => x.Item.Key);
        var selected = plans.Where(x => x.State == TagRebuildState.Ready).ToDictionary(x => x.Item.Key);
        var jobs = ProjectTagPolicy.DrawingJobs(plans);
        var drawings = new List<ProjectTagDrawingResult>();
        var deferred = new Dictionary<string, string>();
        string? stop = null;
        bool logFailed = false;
        void Message(string value)
        {
            try { journal.Message(value); }
            catch { logFailed = true; throw; }
        }
        foreach (var job in jobs)
        {
            ICadDrawing? drawing = null;
            bool opened = false, known = true, retained = false, checkingContext = false;
            void CheckContext()
            {
                checkingContext = true;
                host.CheckContext();
                checkingContext = false;
            }
            string state = "已处理", reason = "";
            bool hasIssue = false;
            var work = job.Keys.Where(k => !records.ContainsKey(k)).Select(k => selected[k]).ToArray();
            var sync = job.Keys.Where(k => records.TryGetValue(k, out var r) && r.Action == TagRebuildAction.Updated)
                .Select(k => records[k]).ToArray();
            try
            {
                CheckContext();
                Message($"图纸 [{drawings.Count + 1}/{jobs.Count}] {job.Path}；新行 {work.Length}，共享刷新 {sync.Length}");
                drawing = host.FindOpen(job.Path);
                if (drawing == null)
                {
                    ExportPolicy.RejectLinks(job.Path);
                    if (!File.Exists(job.Path)) throw new FileNotFoundException("项目图纸不存在。", job.Path);
                    known = false;
                    drawing = await host.OpenAsync(job.Path);
                    opened = true;
                    known = true;
                }
                known = false;
                await host.ActivateAsync(drawing);
                var before = await host.ReadStateAsync(drawing);
                known = true;
                if (before.ReadOnly)
                {
                    hasIssue = true;
                    state = "跳过";
                    reason = "图纸为只读，未写入或保存。";
                    foreach (var plan in work) deferred[plan.Item.Key] = reason;
                }
                else if (work.Length != 0 || sync.Length != 0)
                {
                    known = false;
                    var result = await host.RebuildAsync(drawing, work, sync, journal);
                    known = result.SafeToSave;
                    foreach (var record in result.Records) records[record.Item.Key] = record;
                    if (result.StopReason != null || !result.SafeToSave)
                    {
                        hasIssue = true;
                        stop = result.StopReason ?? "目标文档状态不明确。";
                        state = "已停止"; reason = stop;
                    }
                    else if (saveChanges && (result.Attempted || sync.Length != 0))
                    {
                        CheckContext();
                        string internalDirectory = Path.Combine(directory, "_internal");
                        if (files != null) files.EnsureDirectory(internalDirectory);
                        else Directory.CreateDirectory(internalDirectory);
                        Message("统一保存选择 Y，QSAVE 包括本图原有修改：" + job.Path);
                        known = false;
                        await host.SaveAsync(drawing, Path.Combine(internalDirectory, Guid.NewGuid().ToString("N") + ".done"));
                        var afterSave = await host.ReadStateAsync(drawing);
                        known = true;
                        if (afterSave.Modified) throw new CadOperationException("QSAVE 后图纸仍有修改，保留打开状态。", true);
                        Message("QSAVE 完成：" + job.Path);
                        state = "已保存";
                    }
                    else reason = "统一选择不保存；当前修改保留。";
                }
            }
            catch (OperationCanceledException ex) { hasIssue = true; stop = "已中断：" + ex.Message; state = "已中断"; reason = stop; }
            catch (Exception ex)
            {
                hasIssue = true;
                state = "失败"; reason = ex.Message;
                if (ex is CadOperationException cad) { known = cad.StateKnown; if (cad.StopBatch) stop = ex.Message; }
                if (logFailed) stop = "日志写入失败：" + ex.Message;
                if (checkingContext) stop = "项目状态检查失败：" + ex.Message;
                if (!known)
                {
                    stop = "文档操作状态不明确：" + ex.Message;
                    files?.Protect(stop);
                    foreach (var plan in work.Where(x => !records.ContainsKey(x.Item.Key)))
                    {
                        var record = new TagRebuildRecord(plan.Item, TagRebuildAction.Failed,
                            "操作状态不明确，可能已修改；请查看逐行日志：" + ex.Message);
                        records[record.Item.Key] = record;
                        try { journal.Append(record); } catch { /* 原始日志故障由停止状态保留。 */ }
                    }
                }
                else foreach (var plan in work) deferred[plan.Item.Key] = ex.Message;
            }
            finally
            {
                if (opened && drawing != null)
                {
                    retained = true;
                    if (known && stop == null)
                    {
                        try
                        {
                            var stateNow = await host.ReadStateAsync(drawing);
                            bool pending = await host.HasPendingDataAsync(drawing);
                            if (!stateNow.Modified && !pending)
                            { await host.CloseAsync(drawing); retained = false; }
                            else reason += "；保留工具打开的图纸（仍有图纸修改或项目缓存待提交）。";
                        }
                        catch (Exception ex) { hasIssue = true; stop = "关闭前检查或关闭失败：" + ex.Message; reason += "；" + stop; }
                    }
                }
                drawings.Add(new(job.Path, state, reason, retained) { HasIssue = hasIssue });
                try { journal.Message($"图纸结果：{job.Path}；{state}；{reason}；保留={retained}"); }
                catch (Exception ex) { stop = "日志写入失败：" + ex.Message; }
                progress(drawings.Count, jobs.Count);
            }
            if (stop != null) break;
        }
        foreach (var plan in selected.Values.Where(x => !records.ContainsKey(x.Item.Key)))
        {
            var record = new TagRebuildRecord(plan.Item,
                stop != null ? TagRebuildAction.Unexecuted : TagRebuildAction.Skipped,
                stop ?? deferred.GetValueOrDefault(plan.Item.Key, "没有可处理的关联图纸"));
            records[plan.Item.Key] = record;
            if (!logFailed)
            {
                try { journal.Append(record); }
                catch (Exception ex) { logFailed = true; stop = "日志写入失败：" + ex.Message; }
            }
        }
        if (stop == null && host.OriginalDrawing is { } original)
        {
            try { host.EnsureProject(); await host.ActivateAsync(original); }
            catch (Exception ex) { stop = "恢复原活动图纸失败：" + ex.Message; }
        }
        return new(records.Values.ToArray(), drawings, stop);
    }
}
