using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>串行协调项目图纸的保存、临时导出、发布与文档恢复。</summary>
/// <remarks>CAD 适配器负责主线程和上下文；本类可通过离线宿主验证全部生命周期。</remarks>
public sealed class BatchExporter
{
    /// <summary>执行已确认的批次；取消或状态不明时保留现场，报告清理完成后才返回。</summary>
    /// <param name="catalog">当前项目的固定清单。</param>
    /// <param name="options">命令行确认的选项。</param>
    /// <param name="host">CAD 上下文与文档操作适配器。</param>
    /// <param name="notify">主线程进度回调。</param>
    /// <param name="token">仅阻止后续操作，不强行中断同步 CAD 调用。</param>
    /// <returns>逐图真实结果、停止原因和报告路径。</returns>
    public async Task<BatchResult> RunAsync(ProjectCatalog catalog, BatchOptions options,
        ICadHost host, Action<string> notify, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        host.EnsureProject();
        var jobs = ExportPolicy.CreateJobs(catalog, options);
        string directory = ExportPolicy.ReserveDirectory(options.OutputDirectory);
        using var journal = new BatchJournal(directory);
        var results = new List<DrawingResult>(jobs.Count);
        bool canceled = false, needsCheck = false;
        string? stopReason = null;
        int retained = 0;

        void Report(string message) { journal.Log(message); notify(message); }

        try
        {
            Report("输出目录：" + directory);
            Report("执行方式：LISP command-s；等待完成标记、LISP 结束及 CAD 空闲后处理输出。");
            Report($"图纸类型：{options.Part}，格式：{options.Version}，共 {jobs.Count} 张。");
            foreach (var job in jobs)
            {
                token.ThrowIfCancellationRequested();
                host.EnsureProject();
                ICadDrawing? document = null;
                bool opened = false, safe = true, stop = false;
                string status = "Failed", message = "", temporary = "";
                string marker = Path.Combine(directory, ".pn3dkit-logs", $"{results.Count + 1:D5}.status.txt");
                string saveMarker = Path.Combine(directory, ".pn3dkit-logs", $"{results.Count + 1:D5}.qsave.status.txt");
                try
                {
                    Report($"[{results.Count + 1}/{jobs.Count}] {job.Source}");
                    ExportPolicy.RejectLinks(job.Source);
                    ExportPolicy.RejectLinks(Path.GetDirectoryName(job.Destination)!);
                    ExportPolicy.ValidateCadPath(job.Source);
                    ExportPolicy.ValidateCadPath(job.Destination);
                    ExportPolicy.ValidateCadPath(marker);
                    ExportPolicy.ValidateCadPath(saveMarker);
                    if (!File.Exists(job.Source)) throw new FileNotFoundException("源图不存在。", job.Source);
                    if (File.Exists(job.Destination) || Directory.Exists(job.Destination))
                    {
                        status = "Skipped";
                        message = "目标已存在，未打开或保存源图。";
                    }
                    else
                    {
                        string outputDirectory = Path.GetDirectoryName(job.Destination)!;
                        Directory.CreateDirectory(outputDirectory);
                        do { temporary = Path.Combine(outputDirectory, ".__pn3d_" + Guid.NewGuid().ToString("N")[..8] + ".dwg"); }
                        while (File.Exists(temporary) || Directory.Exists(temporary));
                        ExportPolicy.ValidateCadPath(temporary);
                        token.ThrowIfCancellationRequested();
                        document = host.FindOpen(job.Source);
                        if (document == null)
                        {
                            safe = false;
                            document = await host.OpenAsync(job.Source);
                            opened = true;
                            safe = true;
                        }
                        token.ThrowIfCancellationRequested();
                        host.EnsureProject();
                        safe = false;
                        await host.ActivateAsync(document);
                        var state = await host.ReadStateAsync(document);
                        safe = true;
                        if (state.Modified && !options.SaveChanges)
                        {
                            status = "Skipped";
                            message = "选择 N：源图有未保存修改，未保存也未导出。";
                        }
                        else
                        {
                            if (state.Modified)
                            {
                                if (state.ReadOnly) throw new IOException("源图只读且有修改，不能保存或导出。");
                                token.ThrowIfCancellationRequested();
                                host.EnsureProject();
                                Report("执行一次 QSAVE：" + job.Source);
                                safe = false;
                                await host.SaveAsync(document, saveMarker);
                                state = await host.ReadStateAsync(document);
                                safe = true;
                                if (state.Modified) throw new IOException("QSAVE 后仍有未保存修改，未导出。");
                            }
                            token.ThrowIfCancellationRequested();
                            host.EnsureProject();
                            safe = false;
                            await host.ExportAsync(document, temporary, options.Version, marker);
                            safe = true;
                            token.ThrowIfCancellationRequested();
                            host.EnsureProject();
                            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                                throw new IOException("导出未生成非空临时 DWG。");
                            ExportPolicy.RejectLinks(temporary);
                            ExportPolicy.RejectLinks(outputDirectory);
                            ExportPolicy.ValidateOutput(temporary, options.Version);
                            File.Move(temporary, job.Destination, false);
                            status = "Exported";
                            message = "已生成非空 DWG；图形完整性需实际验收。";
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    canceled = stop = true;
                    status = "Canceled";
                    message = "批次已取消；保留当前文档及临时输出。";
                }
                catch (Exception ex)
                {
                    if (ex is CadOperationException cad)
                    {
                        safe = cad.StateKnown;
                        canceled = cad.Canceled;
                        stop = cad.StopBatch || !safe || canceled;
                        if (canceled) status = "Canceled";
                    }
                    else stop = !safe;
                    message = ex.Message;
                    needsCheck |= !safe || stop;
                }
                finally
                {
                    if (opened && document != null)
                    {
                        bool closed = false;
                        if (safe && !canceled && !stop && !token.IsCancellationRequested)
                        {
                            try
                            {
                                host.EnsureProject();
                                safe = false;
                                var state = await host.ReadStateAsync(document);
                                if (!state.Modified)
                                {
                                    token.ThrowIfCancellationRequested();
                                    await host.CloseAsync(document);
                                    closed = true;
                                }
                                safe = true;
                            }
                            catch (Exception ex)
                            {
                                stop = needsCheck = true;
                                canceled |= ex is OperationCanceledException;
                                if (!canceled || status != "Exported")
                                    status = canceled ? "Canceled" : "Failed";
                                message += "；关闭前检查或关闭失败：" + ex.Message;
                            }
                        }
                        if (!closed)
                        {
                            message += "；保留工具打开的图纸。";
                            if (++retained >= 3)
                            {
                                stop = needsCheck = true;
                                message += "已累计保留三张图纸，请检查 CAD。";
                            }
                        }
                    }
                    if (!safe) needsCheck = stop = true;
                    if (token.IsCancellationRequested) canceled = stop = true;
                    if (canceled && status != "Exported") status = "Canceled";
                    if (temporary.Length > 0 && File.Exists(temporary))
                        message += "；临时输出：" + temporary;
                    var item = new DrawingResult(job.Source, job.Destination, status, message);
                    results.Add(item);
                    journal.Append(item);
                    Report($"{status}：{message}");
                }
                if (stop || canceled) { stopReason = message; break; }
            }
        }
        catch (OperationCanceledException) { canceled = true; stopReason = "已取消后续操作。"; }
        catch (Exception ex) { needsCheck = true; stopReason = ex.Message; }
        finally
        {
            if (!needsCheck && !canceled && !token.IsCancellationRequested && host.OriginalDrawing is { } original)
            {
                try { host.EnsureProject(); await host.ActivateAsync(original); }
                catch (Exception ex) { stopReason = "恢复原活动图纸失败：" + ex.Message; }
            }
            journal.Complete(results);
        }
        var result = new BatchResult(jobs.Count, results.ToArray(), canceled || token.IsCancellationRequested,
            stopReason, journal.ReportPath);
        if (result.StopReason != null) Report("停止原因：" + result.StopReason);
        Report(result.Summary);
        return result;
    }
}
