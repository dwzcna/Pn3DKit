using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Interop;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.Commands.XrefBlockCheckCommands))]

namespace Pn3DKit.Commands;

/// <summary>检查 Bind 的 Insert 合并前同名定义，不调用 Plant API、不修改源数据库。</summary>
public sealed class XrefBlockCheckCommands
{
    /// <summary>在原图命令上下文串行读取，目录取消不建日志；切换、关闭和 Esc 保留已完成内容。</summary>
    [CommandMethod("XREFBLOCKCHECK", CommandFlags.Modal)]
    public void CheckXrefBlocks()
    {
        var files = new RunLogFiles();
        IDisposable? lease = null;
        XrefBlockJournal? journal = null;
        Action retention = () => { };
        bool issues = true;
        var results = new List<XrefBlockResult>();
        bool scopeComplete = false, interrupted = false;
        string? stopReason = null;
        int? total = null;
        XrefBlockSelection? selection = null;
        XrefBlockBatchMetrics? metrics = null;
        try
        {
            var document = AcadApp.DocumentManager.MdiActiveDocument;
            if (document == null) { Write("当前没有活动图纸。"); return; }
            lease = BatchRunGate.TryEnter();
            if (lease == null) { Write("已有 Pn3DKit 批次正在运行，请等待结束。"); return; }
            Database database = document.Database;
            ObjectId identity = database.IsDisposed ? ObjectId.Null : database.BlockTableId;
            void EnsureDocument()
            {
                try { TagCheckCommands.EnsureDocument(database, identity); }
                catch (System.Exception ex) { throw new XrefCheckStoppedException(ex.Message, ex); }
            }
            var heartbeat = System.Diagnostics.Stopwatch.StartNew();
            void Ensure()
            {
                EnsureDocument(); TagCheckCommands.CheckBreak();
                if (heartbeat.ElapsedMilliseconds >= 3000)
                { Write("外参块检查处理中，可按 Esc 中断。"); heartbeat.Restart(); }
            }
            retention = EnsureDocument;
            Ensure();
            string suggested = RunPaths.Suggest(RunPaths.Documents(), RunLogKind.XrefBlockCheck, DateTime.Now);
            string? directory = TagCheckCommands.SelectDirectory(document.Editor, suggested, EnsureDocument,
                XrefBlockJournal.Validate, "外参块冲突检查");
            if (directory == null) return;
            Ensure();
            string drawing = string.IsNullOrWhiteSpace(database.Filename) ? document.Name : database.Filename;
            journal = new(directory, drawing, files);
            Write("日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath);
            Write("正在建立外参关系及块名称清单；只深入比较跨来源同名的命名块，不绑定、不重载、不保存。");
            using (var catalog = new XrefBlockCadCatalog(database, drawing, Ensure, journal))
            {
                var stage = System.Diagnostics.Stopwatch.StartNew();
                catalog.Discover((count, source) => Write("名称扫描：" + count + "；" + source));
                var inventory = catalog.ReadNames();
                scopeComplete = inventory.ScopeComplete;
                journal.Notice("名称扫描耗时(ms)：" + stage.Elapsed.TotalMilliseconds.ToString("F1")
                    + "；来源数：" + inventory.Sources.Count + "；名称条目：" + inventory.Entries.Count);
                Ensure();
                stage.Restart();
                selection = XrefBlockBatch.Select(inventory, Ensure);
                total = selection.Candidates.Count;
                journal.Selection(selection);
                foreach (var source in selection.Sources)
                {
                    Ensure();
                    Write(source.Source.Drawing + "：名称 " + source.Names + "；"
                        + (scopeComplete ? "无需比较 " : "已读范围内未发现同名（范围不完整） ")
                        + source.UniqueNames + "；同名命名候选 " + source.NamedCandidates
                        + "；匿名提示 " + source.AnonymousCandidates);
                }
                journal.Notice("名称筛选及汇总耗时(ms)：" + stage.Elapsed.TotalMilliseconds.ToString("F1"));
                Write("候选名称组：" + total + "；唯一名称跳过：" + selection.UniqueNames);
                int requests = 0;
                var batch = XrefBlockBatch.Compare(selection, entry =>
                    {
                        if (requests++ % 50 == 0) Write("读取同名命名块：" + entry.Name + "；" + entry.Source.Drawing);
                        return catalog.ReadDefinition(entry);
                    }, () => catalog.ScopeComplete, Ensure, journal.Append,
                    (done, count) => Write("候选名称组处理进度：" + done + "/" + count));
                results.AddRange(batch.Results);
                metrics = batch.Metrics;
                interrupted = batch.Interrupted; stopReason = batch.StopReason;
                scopeComplete = batch.ScopeComplete;
                try { if (stopReason == null) catalog.VerifySources(); }
                finally { scopeComplete = scopeComplete && catalog.ScopeComplete; }
                journal.Notice("实际根快照读取：" + catalog.RootReads + "；来源读取器：" + catalog.ReadersCreated
                    + "；使用索引来源：" + catalog.UsageIndexes);
            }
            EnsureDocument();
        }
        catch (OperationCanceledException) { interrupted = true; stopReason = "用户取消，已中断。"; }
        catch (System.Exception ex) { stopReason = XrefBlockCadCatalog.Detail(ex); }
        finally
        {
            try
            {
                var batch = new XrefBlockBatchResult(results, scopeComplete, interrupted, stopReason, total, selection, metrics);
                issues = batch.HasIssues;
                if (journal != null)
                {
                    try { journal.Complete(batch); }
                    catch (System.Exception ex) { issues = true; Write("报告写入失败，已停止：" + ex.Message); }
                    finally
                    {
                        try { journal.Dispose(); }
                        catch (System.Exception ex) { issues = true; Write("关闭报告失败：" + ex.Message); }
                    }
                    foreach (var group in results.GroupBy(x => x.StatusText)) Write(group.Key + "：" + group.Count());
                    var conflicts = results.Where(x => x.Status == XrefBlockStatus.Conflict).ToArray();
                    foreach (var item in conflicts.Take(20))
                        Write("冲突块 " + item.Name + "：" + string.Join("；", item.Groups.Select(g =>
                            "定义" + g.Number + " [" + string.Join("、", g.Definitions.Select(d => d.Source.Drawing).Distinct()) + "]"))
                            + (item.Complete ? "" : "；同时存在无法完整比较的来源"));
                    if (conflicts.Length > 20) Write("另有 " + (conflicts.Length - 20) + " 个冲突块，请查看完整报告。");
                    Write(interrupted ? "外参块检查已中断。" : stopReason != null ? "外参块检查已停止。"
                        : batch.CompletedWithAnonymousHints ? "外参块检查完成；存在匿名同名未比较项。"
                        : !scopeComplete || results.Any(x => !x.Complete) ? "外参块检查不完整，请查看具体原因。"
                        : "外参块检查完成。");
                    if (stopReason != null) Write("停止原因：" + stopReason);
                    Write("已完成候选名称组：" + results.Count + "；未完成："
                        + (total.HasValue ? Math.Max(0, total.Value - results.Count).ToString() : "清单未完成，未知"));
                    if (selection != null)
                        Write((scopeComplete ? "无需比较的唯一名称：" : "已读范围内未发现同名（范围不完整）：") + selection.UniqueNames);
                    var anonymous = results.Where(x => x.Status == XrefBlockStatus.AnonymousSkipped).ToArray();
                    foreach (var item in anonymous.Take(20))
                        Write("匿名块同名，未比较 " + item.Name + "："
                            + string.Join("、", (item.Uncompared ?? []).Select(x => x.Source.Drawing).Distinct()));
                    if (anonymous.Length > 20) Write("另有 " + (anonymous.Length - 20) + " 个匿名同名提示，请查看报告。");
                    Write("报告指出定义差异与合并风险，不承诺模拟 Bind → Insert 的最终结果。");
                    Write("日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath);
                }
                else if (stopReason != null) Write(stopReason);
            }
            finally
            {
                try { LogRetentionPrompt.Finish(files, issues, retention); }
                finally { lease?.Dispose(); }
            }
        }
    }

    /// <summary>关闭原图后不向旧 Editor 输出，复用可容错输出路径。</summary>
    private static void Write(string message) => TagCheckCommands.WriteSafely("\n" + message);
}

/// <summary>文档身份变化属于批次停止，不能转换为单个块读取失败后继续。</summary>
internal sealed class XrefCheckStoppedException(string message, System.Exception inner) : InvalidOperationException(message, inner);
