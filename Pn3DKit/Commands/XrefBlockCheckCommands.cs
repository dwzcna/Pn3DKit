using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Interop;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.Commands.XrefBlockCheckCommands))]

namespace Pn3DKit.Commands;

/// <summary>先统计名称，再按公共视窗比较跨来源同名普通块的二维显示效果，不修改源数据库。</summary>
public sealed class XrefBlockCheckCommands
{
    /// <summary>串行名称扫描；目录取消不建日志，切换、关闭和 Esc 保留已确认的部分清单。</summary>
    [CommandMethod("XREFBLOCKCHECK", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void CheckXrefBlocks()
    {
        var files = new RunLogFiles();
        IDisposable? lease = null;
        XrefBlockJournal? journal = null;
        XrefBlockCadCatalog? catalog = null;
        XrefBlockVisualSession? visual = null;
        XrefBlockInventory inventory = new([], [], false);
        XrefBlockStatistics? statistics = null;
        IReadOnlyList<XrefBlockCandidate> candidates = [];
        var comparisons = new List<XrefBlockReportResult>();
        var progress = new XrefBlockProgress(Write);
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
            void Ensure()
            {
                EnsureDocument(); TagCheckCommands.CheckBreak();
                progress.Pulse();
            }
            retention = EnsureDocument;
            Ensure();
            string suggested = RunPaths.Suggest(RunPaths.Documents(), RunLogKind.XrefBlockCheck, DateTime.Now);
            string? directory = CadOutputDirectoryPrompt.Select(document.Editor, suggested, EnsureDocument,
                XrefBlockJournal.Validate, "外参块名称统计");
            if (directory == null) return;
            Ensure();
            string drawing = string.IsNullOrWhiteSpace(database.Filename) ? document.Name : database.Filename;
            journal = new(directory, drawing, files);
            progress.Paths("日志：" + journal.LogPath, "来源索引：" + journal.SourcesPath);
            progress.Stage("正在发现外参文件……");
            catalog = new(database, drawing, Ensure, journal);
            var stage = System.Diagnostics.Stopwatch.StartNew();
            catalog.Discover(sourceFound: progress.SourceFound, scanStarted: progress.Scan);
            progress.Stage("正在核对名称扫描来源状态。");
            inventory = catalog.ReadNames();
            journal.Notice("来源发现及名称扫描耗时(ms)：" + stage.Elapsed.TotalMilliseconds.ToString("F1")
                + "；来源数：" + inventory.Sources.Count(s => s.DwgNo > 0)
                + "；名称条目：" + inventory.Entries.Count);
            progress.Stage("正在按匿名标志汇总块名称及 dwgNo。");
            statistics = XrefBlockNames.Summarize(inventory, Ensure);
            journal.WriteStatistics(inventory, statistics);
            candidates = XrefBlockComparison.Candidates(inventory, statistics);
            Write("重复普通块候选名称组：" + candidates.Count + "；比较报告：" + journal.ComparisonPath);
            stage.Restart();
            journal.Notice("视觉比较：WCS俯视／正交／公共视窗／2048×2048／双背景；"
                + "有效颜色、透明度及线宽核对，多线宽共用编码色表；匹配不代表无限精度几何等价。");
            journal.Notice("当前原生后端不能可靠确认非连续线型、遮罩及重叠异色内容的显示顺序；"
                + "这些情况返回未验证，不以不完整图像证明匹配。");
            if (candidates.Count > 0)
            {
                visual = new(document, catalog, Ensure, progress.Stage);
                XrefBlockVisualBatch.Run(candidates, visual.Capture, () => catalog.ScopeComplete, Ensure,
                    result => progress.Complete(result, journal.AppendComparison), comparisons, progress.Compare,
                    journal.AppendVisualDifference);
            }
            progress.Stage("正在核对比较来源状态。");
            catalog.VerifySources();
            journal.Notice("候选比较耗时(ms)：" + stage.Elapsed.TotalMilliseconds.ToString("F1")
                + "；实际候选来源准备：" + catalog.VisualPrepares + "；专用设备：" + (visual?.DevicesCreated ?? 0)
                + "；原生绘制调用：" + (visual?.RasterCalls ?? 0) + "；完整读回替代完成标志：" + (visual?.LegacyInvalidFlags ?? 0)
                + "；旧字段快照读取：" + catalog.RootReads);
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
                if (visual != null)
                {
                    readers = visual.DevicesCreated;
                    try { visual.Dispose(); }
                    catch (System.Exception ex)
                    { stopReason = (stopReason == null ? "" : stopReason + "；") + XrefBlockCadCatalog.Detail(ex); }
                }
                if (catalog != null)
                {
                    // 结束路径只复制内存；原图已关闭时不能再次查询 SDK 或 Editor。
                    inventory = catalog.CaptureNames();
                    rootReads = catalog.VisualPrepares;
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
                        foreach (var pending in XrefBlockVisualBatch.Remaining(candidates, comparisons,
                            stopReason ?? "扫描或比较未完成，未读取此候选。").ToArray())
                        { progress.Complete(pending, journal.AppendComparison); comparisons.Add(pending); }
                        issues |= comparisons.Any(c => c.HasIssues);
                        journal.CompleteVisual(result, statistics, comparisons, rootReads, readers);
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
                    foreach (var group in comparisons.GroupBy(XrefBlockProgress.ResultLabel)) Write("比较" + group.Key + "：" + group.Count());
                    var different = comparisons.Where(c => c.Status == XrefBlockComparisonStatus.Different).ToArray();
                    foreach (var item in different.Take(20))
                        Write("不符合块 " + item.Name + "：" + string.Join("；", item.Groups.Select(g => "内容组" + g.Number
                            + " [dwgNo=" + string.Join("/", g.Entries.Select(e => e.Source.DwgNo)) + "]"))
                            + (item.Complete ? "" : "；同时存在未完整比较来源"));
                    if (different.Length > 20) Write("另有 " + (different.Length - 20) + " 个不符合名称，请查看报告。");
                    Write("匹配仅代表规定俯视、显示条件及分辨率下通过核对；范围外内容未验证，报告不模拟最终绑定结果。");
                    progress.Paths("日志：" + journal.LogPath, "来源索引：" + journal.SourcesPath,
                        "匿名统计：" + journal.AnonymousPath, "非匿名统计：" + journal.NormalPath,
                        "比较报告：" + journal.ComparisonPath);
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
