using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Interop;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.Commands.XrefBlockCheckCommands))]

namespace Pn3DKit.Commands;

/// <summary>先统计名称，再比较跨来源同名普通块的二维内容，不修改源数据库。</summary>
public sealed class XrefBlockCheckCommands
{
    /// <summary>串行名称扫描；目录取消不建日志，切换、关闭和 Esc 保留已确认的部分清单。</summary>
    [CommandMethod("XREFBLOCKCHECK", CommandFlags.Modal)]
    public void CheckXrefBlocks()
    {
        var files = new RunLogFiles();
        IDisposable? lease = null;
        XrefBlockJournal? journal = null;
        XrefBlockCadCatalog? catalog = null;
        XrefBlockInventory inventory = new([], [], false);
        XrefBlockStatistics? statistics = null;
        IReadOnlyList<XrefBlockCandidate> candidates = [];
        var comparisons = new List<XrefBlockComparisonResult>();
        int rootReads = 0, readers = 0;
        Action retention = () => { };
        bool interrupted = false, issues = true;
        string? stopReason = null;
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
                { Write("外参块名称扫描处理中，可按 Esc 中断。"); heartbeat.Restart(); }
            }
            retention = EnsureDocument;
            Ensure();
            string suggested = RunPaths.Suggest(RunPaths.Documents(), RunLogKind.XrefBlockCheck, DateTime.Now);
            string? directory = TagCheckCommands.SelectDirectory(document.Editor, suggested, EnsureDocument,
                XrefBlockJournal.Validate, "外参块名称统计");
            if (directory == null) return;
            Ensure();
            string drawing = string.IsNullOrWhiteSpace(database.Filename) ? document.Name : database.Filename;
            journal = new(directory, drawing, files);
            Write("日志：" + journal.LogPath + "\n来源索引：" + journal.SourcesPath);
            Write("正在扫描外参关系及块名称；名称统计落盘后仅比较重复普通块的二维内容。");
            catalog = new(database, drawing, Ensure, journal);
            var stage = System.Diagnostics.Stopwatch.StartNew();
            catalog.Discover((count, source) => Write("名称扫描：" + count + "；" + source));
            inventory = catalog.ReadNames();
            journal.Notice("名称扫描耗时(ms)：" + stage.Elapsed.TotalMilliseconds.ToString("F1")
                + "；来源数：" + inventory.Sources.Count(s => s.DwgNo > 0)
                + "；名称条目：" + inventory.Entries.Count);
            Write("正在按匿名标志汇总块名称及 dwgNo。");
            statistics = XrefBlockNames.Summarize(inventory, Ensure);
            journal.WriteStatistics(inventory, statistics);
            candidates = XrefBlockComparison.Candidates(inventory, statistics);
            Write("重复普通块候选名称组：" + candidates.Count + "；比较报告：" + journal.ComparisonPath);
            stage.Restart();
            XrefBlockComparisonBatch.Run(candidates, catalog.ReadDefinition, () => catalog.ScopeComplete, Ensure,
                journal.AppendComparison, comparisons,
                (index, count, name) => Write("比较名称组：" + index + "/" + count + "；" + name));
            catalog.VerifySources();
            journal.Notice("候选比较耗时(ms)：" + stage.Elapsed.TotalMilliseconds.ToString("F1")
                + "；实际根读取：" + catalog.RootReads + "；来源内容读取器：" + catalog.ReadersCreated);
            Ensure();
        }
        catch (OperationCanceledException) { interrupted = true; stopReason = "用户取消，已中断。"; }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        { interrupted = true; stopReason = "用户取消，已中断。"; }
        catch (System.Exception ex) { stopReason = XrefBlockCadCatalog.Detail(ex); }
        finally
        {
            try
            {
                if (catalog != null)
                {
                    // 结束路径只复制内存；原图已关闭时不能再次查询 SDK 或 Editor。
                    inventory = catalog.CaptureNames();
                    rootReads = catalog.RootReads; readers = catalog.ReadersCreated;
                    try { catalog.Dispose(); }
                    catch (System.Exception ex)
                    { stopReason = (stopReason == null ? "" : stopReason + "；") + XrefBlockCadCatalog.Detail(ex); }
                }
                var result = new XrefBlockScanResult(inventory, interrupted, stopReason);
                issues = result.HasIssues;
                if (journal != null)
                {
                    bool outputComplete = false;
                    try
                    {
                        if (journal.HasWriteFailure)
                            throw new XrefBlockLogException("输出已失败，停止生成后续名称统计；保留已写出文件。");
                        statistics ??= XrefBlockNames.Summarize(inventory);
                        journal.WriteStatistics(inventory, statistics);
                        if (candidates.Count == 0) candidates = XrefBlockComparison.Candidates(inventory, statistics);
                        foreach (var pending in XrefBlockComparisonBatch.Remaining(candidates, comparisons,
                            stopReason ?? "扫描或比较未完成，未读取此候选。").ToArray())
                        { journal.AppendComparison(pending); comparisons.Add(pending); }
                        issues |= comparisons.Any(c => c.HasIssues);
                        journal.Complete(result, statistics, comparisons, rootReads, readers);
                        outputComplete = true;
                    }
                    catch (System.Exception ex) { issues = true; Write("报告写入失败，已停止：" + ex.Message); }
                    finally
                    {
                        try { journal.Dispose(); }
                        catch (System.Exception ex) { issues = true; outputComplete = false; Write("关闭报告失败：" + ex.Message); }
                    }
                    Write("外参块名称统计：" + result.State + (outputComplete ? "" : "；报告输出未完成"));
                    if (stopReason != null) Write("停止原因：" + stopReason);
                    Write("已进入扫描的来源：" + inventory.Sources.Count(s => s.DwgNo > 0)
                        + "；已确认名称条目：" + inventory.Entries.Count);
                    if (outputComplete && statistics != null)
                        Write("匿名块名称：" + statistics.Anonymous.Count + "；非匿名块名称：" + statistics.Normal.Count);
                    if (result.HasIssues) Write("统计仅含已确认名称，不能代表全部外参范围。");
                    foreach (var group in comparisons.GroupBy(c => c.ResultText)) Write("比较" + group.Key + "：" + group.Count());
                    var different = comparisons.Where(c => c.Status == XrefBlockComparisonStatus.Different).ToArray();
                    foreach (var item in different.Take(20))
                        Write("不匹配块 " + item.Name + "：" + string.Join("；", item.Groups.Select(g => "内容组" + g.Number
                            + " [dwgNo=" + string.Join("/", g.Definitions.Select(d => d.Entry.Source.DwgNo)) + "]"))
                            + (item.Complete ? "" : "；同时存在未完整比较来源"));
                    if (different.Length > 20) Write("另有 " + (different.Length - 20) + " 个不匹配名称，请查看报告。");
                    Write("二维比较范围外的内容明确标记不完整；报告不模拟最终绑定结果。");
                    Write("日志：" + journal.LogPath + "\n来源索引：" + journal.SourcesPath
                        + "\n匿名统计：" + journal.AnonymousPath + "\n非匿名统计：" + journal.NormalPath
                        + "\n比较报告：" + journal.ComparisonPath);
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

/// <summary>文档身份变化属于批次停止，不能转换为普通来源读取失败后继续。</summary>
internal sealed class XrefCheckStoppedException(string message, System.Exception inner) : InvalidOperationException(message, inner);
