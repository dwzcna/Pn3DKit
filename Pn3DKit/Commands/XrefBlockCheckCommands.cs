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
            Write("正在建立外参关系及命名块清单；本命令不绑定、不重载、不保存图纸。");
            using (var catalog = new XrefBlockCadCatalog(database, drawing, Ensure, journal))
            {
                catalog.Discover();
                Ensure();
                var definitions = catalog.ReadDefinitions((count, source) =>
                    Write("读取定义：" + count + "；" + source));
                Ensure();
                scopeComplete = catalog.ScopeComplete;
                var batch = XrefBlockBatch.Compare(definitions, scopeComplete, Ensure, journal.Append,
                    (done, total) => Write("同名块比较进度：" + done + "/" + total));
                results.AddRange(batch.Results);
                interrupted = batch.Interrupted; stopReason = batch.StopReason;
                scopeComplete = batch.ScopeComplete; total = batch.Total;
            }
            EnsureDocument();
        }
        catch (OperationCanceledException) { interrupted = true; stopReason = "用户取消，已中断。"; }
        catch (System.Exception ex) { stopReason = XrefBlockCadCatalog.Detail(ex); }
        finally
        {
            try
            {
                var batch = new XrefBlockBatchResult(results, scopeComplete, interrupted, stopReason, total);
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
                        : !scopeComplete || results.Any(x => !x.Complete) ? "外参块检查不完整，请查看具体原因。"
                        : "外参块检查完成。");
                    if (stopReason != null) Write("停止原因：" + stopReason);
                    Write("已完成块名：" + results.Count + "；未完成："
                        + (total.HasValue ? Math.Max(0, total.Value - results.Count).ToString() : "清单未完成，未知"));
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
