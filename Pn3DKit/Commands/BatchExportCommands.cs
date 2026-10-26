using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Interop;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.Commands.BatchExportCommands))]

namespace Pn3DKit.Commands;

/// <summary>提供 NETLOAD 后可直接调用的项目批量导出命令。</summary>
public sealed class BatchExportCommands
{
    /// <summary>供 Ribbon 和单个命令判断所有 Pn3DKit 批次状态。</summary>
    internal static bool IsRunning => BatchRunGate.IsRunning;

    /// <summary>在应用会话上下文协调多文档导出；所有异常在此入口收尾。</summary>
    /// <remarks>async void 是 CAD 命令入口契约；互斥覆盖所有等待和清理。</remarks>
    [CommandMethod("PNDWGEXPORT", CommandFlags.Session)]
    public async void PnDwgExport()
    {
        using var lease = BatchRunGate.TryEnter();
        if (lease == null)
        {
            Write("已有 Pn3DKit 批次正在运行。");
            return;
        }
        var files = new RunLogFiles();
        bool issues = true;
        try
        {
            if (AcadApp.DocumentManager.MdiActiveDocument == null)
            {
                Write("请先打开一张图纸，再执行 PNDWGEXPORT。");
                return;
            }
            var project = PlantProjectAccess.Read();
            var host = new AutoCadHost(project.IsCurrent, files);
            var original = host.OriginalDrawing!;
            BatchOptions? options = await host.InDocumentAsync(original, document =>
            {
                var editor = document.Editor;
                var defaultPart = ExportPolicy.DetectPart(project.Catalog, document.Database.Filename);
                var partPrompt = new PromptKeywordOptions("\n选择图纸类型 ");
                partPrompt.Keywords.Add("P", "P", "P - PnId");
                partPrompt.Keywords.Add("D", "D", "D - Piping");
                partPrompt.Keywords.Default = defaultPart == ProjectPartKind.PnId ? "P" : "D";
                partPrompt.AllowNone = true;
                var partResult = editor.GetKeywords(partPrompt);
                if (partResult.Status is not (PromptStatus.OK or PromptStatus.None)) return null;
                var part = ExportPolicy.ParsePart(partResult.Status == PromptStatus.None
                    ? partPrompt.Keywords.Default : partResult.StringResult);
                var drawings = project.Catalog.Drawings(part);
                if (drawings == null || drawings.Count == 0)
                {
                    editor.WriteMessage($"\n当前项目没有 {part} 图纸，批次未开始。");
                    return null;
                }
                editor.WriteMessage("\nQSAVE 会保存源图；后续失败或取消不会撤销保存。选择 N 将跳过有未保存修改的图纸。");
                var savePrompt = new PromptKeywordOptions("\n导出前保存有修改的图纸？");
                savePrompt.Keywords.Add("Yes");
                savePrompt.Keywords.Add("No");
                savePrompt.Keywords.Default = "Yes";
                savePrompt.AllowNone = true;
                var saveResult = editor.GetKeywords(savePrompt);
                if (saveResult.Status is not (PromptStatus.OK or PromptStatus.None)) return null;
                bool save = saveResult.Status == PromptStatus.None
                    || saveResult.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                    || saveResult.StringResult.Equals("Y", StringComparison.OrdinalIgnoreCase);
                string suggested = ExportPolicy.SuggestDirectory(
                    RunPaths.Documents(), DateTime.Now);
                var directoryIdentity = document.Database.BlockTableId;
                void EnsureDirectoryContext()
                {
                    TagCheckCommands.EnsureDocument(document.Database, directoryIdentity);
                    host.EnsureProject();
                }
                string? output = CadOutputDirectoryPrompt.Select(editor, suggested, EnsureDirectoryContext,
                    ExportPolicy.ValidateOutputDirectory, "导出", logDirectory: false);
                if (output == null) return null;
                editor.WriteMessage("\n所选输出目录：" + output);
                var versionPrompt = new PromptKeywordOptions(DwgVersionSelection.Message)
                {
                    AllowNone = true, AllowArbitraryInput = true, AppendKeywordsToMessage = false
                };
                foreach (var option in DwgVersionSelection.Options)
                    versionPrompt.Keywords.Add(option.Keyword, option.Keyword, option.DisplayName);
                versionPrompt.Keywords.Default = "LT" + DwgVersionSelection.DefaultYear;
                while (true)
                {
                    var versionResult = editor.GetKeywords(versionPrompt);
                    if (versionResult.Status is not (PromptStatus.OK or PromptStatus.None)) return null;
                    if (DwgVersionSelection.TryParse(versionResult.Status == PromptStatus.None
                        ? null : versionResult.StringResult, out string version))
                        return new BatchOptions(part, save, version, output);
                    editor.WriteMessage("\n不支持此文件格式，请点击列出的版本或输入 2007、2010、2013、2018。");
                }
            });
            if (options == null) { Write("批次未开始。"); return; }
            var result = await new BatchExporter().RunAsync(project.Catalog, options, host, Write, files: files);
            issues = LogRetentionPolicy.ExportHasIssues(result);
            Write("报告：" + result.ReportPath);
            if (result.StopReason != null) Write("停止原因：" + result.StopReason);
        }
        catch (System.Exception ex)
        {
            Write("Pn3DKit 未完成：" + ex.Message
                + (ex is FileNotFoundException or FileLoadException or TypeLoadException
                    ? "；请在已加载 Plant 环境的 AutoCAD Plant 3D 2026 中运行此命令。" : ""));
        }
        finally { await LogRetentionPrompt.FinishAsync(files, issues); }
        // 租约覆盖最后的保留询问和文件清理。
    }

    /// <summary>向当前活动文档写入中文消息；文档失效时仅记录调试信息。</summary>
    internal static void Write(string message)
    {
        try { AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + message); }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(message + "；" + ex.Message); }
    }
}
