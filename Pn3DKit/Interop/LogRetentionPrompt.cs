using Autodesk.AutoCAD.EditorInput;
using Pn3DKit.Commands;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.Interop;

/// <summary>关闭日志后执行最后一步保留交互；不访问 Plant 数据，也不切换到其他文档。</summary>
internal static class LogRetentionPrompt
{
    /// <summary>会话命令在当前可用文档上下文询问；未知状态或没有文档时保留。</summary>
    internal static async Task FinishAsync(RunLogFiles files, bool hasIssues)
    {
        files.Seal();
        if (!files.HasFiles) return;
        if (files.ProtectionReason != null) { Retain(files, files.ProtectionReason); return; }
        try
        {
            var document = AcadApp.DocumentManager.MdiActiveDocument;
            if (document == null || (!document.Editor.IsQuiescent
                && document.CommandInProgress.ToUpperInvariant() is not
                    ("PNDWGEXPORT" or "REBUILDPNTAGBYCLASS" or "CHECKPROJECTPNTAGBYCLASS" or "REBUILDPROJECTPNTAGBYCLASS")))
            { Retain(files, "当前没有可安全交互的空闲图纸。"); return; }
            var host = new AutoCadHost(() => true);
            await host.InDocumentAsync(host.OriginalDrawing!, current =>
            { Ask(files, hasIssues, current.Editor); return true; });
        }
        catch (System.Exception ex) { Retain(files, "无法完成保留询问：" + ex.Message); }
    }

    /// <summary>已有命令上下文的入口；核对原数据库有效后再询问，不消费旧的中断状态。</summary>
    internal static void Finish(RunLogFiles files, bool hasIssues, Action ensure)
    {
        files.Seal();
        if (!files.HasFiles) return;
        if (files.ProtectionReason != null) { Retain(files, files.ProtectionReason); return; }
        try
        {
            ensure();
            var document = AcadApp.DocumentManager.MdiActiveDocument;
            if (document == null) { Retain(files, "当前没有活动图纸。"); return; }
            Ask(files, hasIssues, document.Editor);
        }
        catch (System.Exception ex) { Retain(files, "无法完成保留询问：" + ex.Message); }
    }

    /// <summary>所有选项完整可点击；Esc 保留，N 删除归属清单中的已结束日志。</summary>
    private static void Ask(RunLogFiles files, bool hasIssues, Editor editor)
    {
        var prompt = new PromptKeywordOptions("\n是否保留本批日志文件？");
        prompt.Keywords.Add("Y", "Y", "是(Y)");
        prompt.Keywords.Add("N", "N", "否(N)");
        prompt.Keywords.Default = hasIssues ? "Y" : "N";
        prompt.AllowNone = true;
        var answer = editor.GetKeywords(prompt);
        string? value = answer.Status == PromptStatus.None ? ""
            : answer.Status == PromptStatus.OK ? answer.StringResult : null;
        if (LogRetentionPolicy.Keep(value, hasIssues)) { Retain(files, value == null ? "已取消保留询问。" : "用户选择保留。"); return; }
        var result = files.Delete();
        Write(result.Remaining.Count == 0 && result.Errors.Count == 0
            ? $"已删除本批日志文件（{result.Deleted} 个）。"
            : $"本批日志部分删除：已删除 {result.Deleted} 个，剩余 {result.Remaining.Count} 个。");
        foreach (string error in result.Errors) Write("清理提示：" + error);
        foreach (string path in result.Remaining) Write("保留：" + path);
    }

    /// <summary>仅报告实际仍在的文件，不重新创建已经关闭的日志。</summary>
    private static void Retain(RunLogFiles files, string reason)
    {
        Write("已保留本批日志文件。" + reason);
        foreach (string path in files.Paths.Where(File.Exists)
            .Where(p => Path.GetExtension(p) is ".log" or ".csv" or ".json" or ".jsonl")) Write(path);
    }
    /// <summary>图纸关闭时输出失败不覆盖原始结果。</summary>
    private static void Write(string value) => TagCheckCommands.WriteSafely("\n" + value);
}
