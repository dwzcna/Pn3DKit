using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Interop;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.UI;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.Commands.TagCheckCommands))]

namespace Pn3DKit.Commands;

/// <summary>在当前文档命令上下文提供单个及按类别的只读位号检查。</summary>
public sealed class TagCheckCommands
{
    /// <summary>复用预选或单选，输出现有 Tag、期望 Tag 及逐字段差异。</summary>
    [CommandMethod("CHECKPNTAG", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void CheckPnTag()
    {
        var document = Ready("CHECKPNTAG");
        if (document == null) return;
        var editor = document.Editor;
        try
        {
            var id = TagFormatCommands.SelectTarget(editor, "CHECKPNTAG");
            if (!id.HasValue) return;
            var result = PlantTagCheckAccess.Check(id.Value);
            if (result.Subject.ClassName.Length != 0)
                editor.WriteMessage($"\n项目部分：{result.Subject.ProjectPart}；部件类别：{result.Subject.ClassName}；句柄：{result.Subject.Handle}");
            editor.WriteMessage("\n" + result.Message);
            if (result.Details.Length != 0 && result.Status != TagCheckStatus.Error)
                editor.WriteMessage("\n" + result.Details);
        }
        catch (OperationCanceledException) { editor.WriteMessage("\n已取消检查。"); }
        catch (System.Exception ex) { ReportFailure(editor, ex); }
    }

    /// <summary>扫描当前图纸模型空间，按所选类别逐个检查并保存即时日志。</summary>
    /// <remarks>全程在 CAD 主线程执行，不切换、打开、关闭或保存文档；Esc 保留已写记录。</remarks>
    [CommandMethod("CHECKPNTAGBYCLASS", CommandFlags.Modal)]
    public void CheckPnTagByClass()
    {
        var document = Ready("CHECKPNTAGBYCLASS");
        if (document == null) return;
        var editor = document.Editor;
        string? output = null;
        try
        {
            var database = document.Database;
            var scan = PlantTagCheckAccess.Scan(database, () =>
            {
                EnsureDocument(document, database);
                CheckBreak();
            });
            var available = scan.Groups.Where(g => g.HasFormat || g.FormatError != null).ToArray();
            var skipped = scan.Groups.Where(g => !g.HasFormat && g.FormatError == null).ToArray();
            string skippedText = skipped.Length == 0 ? ""
                : "未配置位号格式的类别已跳过："
                    + string.Join("、", skipped.Select(g => g.ClassName).Distinct())
                    + $"（共 {skipped.Select(g => g.ClassName).Distinct().Count()} 类，{skipped.Sum(g => g.ObjectIds.Count)} 个对象）";
            if (skippedText.Length != 0) editor.WriteMessage("\n" + skippedText);
            foreach (string warning in scan.Warnings.Take(20))
                editor.WriteMessage("\n" + warning);
            if (scan.Warnings.Count > 20)
                editor.WriteMessage($"\n另有 {scan.Warnings.Count - 20} 条识别警告，执行检查时将写入日志。");
            if (available.Length == 0)
            {
                editor.WriteMessage("\n当前图纸模型空间没有可检查的位号类别。");
                return;
            }
            for (int i = 0; i < available.Length; i++)
            {
                var group = available[i];
                string part = available.Count(g => g.ClassName == group.ClassName) > 1
                    ? " [" + group.PartName + "]" : "";
                editor.WriteMessage($"\n{i + 1} - {group.ClassName}{part} ({group.ObjectIds.Count})"
                    + (group.FormatError == null ? "" : "（格式读取失败，检查结果将记录原因）"));
            }

            int[] numbers;
            while (true)
            {
                var input = editor.GetString(new PromptStringOptions(
                    "\n请选择类别编号 [如 1,3,5-7；*=全部] <0>: ") { AllowSpaces = true });
                if (input.Status == PromptStatus.Cancel) return;
                if (input.Status is not (PromptStatus.OK or PromptStatus.None)) return;
                try
                {
                    numbers = TagCheckPolicy.ParseClasses(
                        input.Status == PromptStatus.None ? "" : input.StringResult, available.Length);
                    break;
                }
                catch (ArgumentException ex) { editor.WriteMessage("\n" + ex.Message); }
            }
            if (numbers.Length == 0)
            {
                editor.WriteMessage("\n未选择类别，未执行检查");
                return;
            }
            var selected = numbers.Select(number => available[number - 1]).ToArray();
            var objects = selected.SelectMany(g => g.ObjectIds.Select(id =>
                (Id: id, PartName: g.PartName, ClassName: g.ClassName))).DistinctBy(item => item.Id).ToArray();
            string[] classes = selected.Select(g => g.ClassName).Distinct().ToArray();
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            string suggested = TagCheckPaths.Suggest(documents, DateTime.Now);
            output = SelectDirectory(editor, suggested);
            if (output == null) return;

            void EnsureContext()
            {
                EnsureDocument(document, database);
                if (!scan.IsCurrent())
                    throw new InvalidOperationException("当前 Plant 项目已变化，检查已停止。");
                CheckBreak();
            }
            EnsureContext();
            string diagnostics = string.Join("\n", new[] { skippedText }
                .Concat(scan.Warnings)
                .Concat(selected.Where(g => g.FormatError != null)
                    .Select(g => g.ClassName + "：扫描时格式读取失败：" + g.FormatError))
                .Where(s => s.Length != 0));
            string drawing = string.IsNullOrWhiteSpace(database.Filename) ? document.Name : database.Filename;
            using var journal = new TagCheckJournal(output, drawing, classes, diagnostics);
            editor.WriteMessage("\n日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath);
            try
            {
                var result = TagCheckBatch.Run(objects, item =>
                {
                    var checkedItem = PlantTagCheckAccess.Check(item.Id);
                    return TagCheckPolicy.BindToScannedClass(checkedItem, item.PartName, item.ClassName);
                }, EnsureContext, journal,
                    (done, total) => editor.WriteMessage($"\n位号检查进度：{done}/{total}"));
                foreach (string className in classes)
                    editor.WriteMessage("\n" + className + "：" + TagCheckPolicy.Summary(
                        result.Items.Where(x => x.Subject.ClassName == className)));
                var mismatches = result.Items.Where(x => x.Status == TagCheckStatus.Mismatch).ToArray();
                foreach (var item in mismatches.Take(20))
                    editor.WriteMessage($"\n不符：{item.Subject.ExistingTag}；句柄 {item.Subject.Handle}；期望 {item.ExpectedTag}");
                if (mismatches.Length > 20)
                    editor.WriteMessage($"\n另有 {mismatches.Length - 20} 条不符，请查看完整日志。");
                editor.WriteMessage($"\n{(result.Interrupted ? "已中断" : result.StopReason == null ? "检查完成" : "检查已停止")}；已处理 {result.Items.Count}，未处理 {result.Remaining}。");
                if (result.StopReason != null) editor.WriteMessage("\n停止原因：" + result.StopReason);
            }
            finally { editor.WriteMessage("\n日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath); }
        }
        catch (OperationCanceledException) { editor.WriteMessage("\n已取消检查。"); }
        catch (System.Exception ex)
        {
            ReportFailure(editor, ex);
            if (output != null) editor.WriteMessage("\n已写内容保留于：" + output);
        }
    }

    /// <summary>复用 FILEDIA 分流和系统文件夹选择器，只替换用途提示及路径验证。</summary>
    private static string? SelectDirectory(Editor editor, string suggested)
    {
        int fileDia = Convert.ToInt32(AcadApp.GetSystemVariable("FILEDIA"));
        return OutputDirectorySelection.Select(fileDia, suggested,
            path => FolderSelectDialogue.Select(path, AcadApp.MainWindow.Handle, "Pn3DKit — 选择位号检查日志目录"),
            path =>
            {
                if (fileDia != 0)
                {
                    var prompt = new PromptKeywordOptions("\n输出日志目录<" + path + "> 或 [选择..(C)]: ")
                    { AllowNone = true, AppendKeywordsToMessage = false };
                    prompt.Keywords.Add("C", "C", "选择..(C)");
                    var result = editor.GetKeywords(prompt);
                    return result.Status switch
                    {
                        PromptStatus.OK => result.StringResult,
                        PromptStatus.None => "",
                        _ => null
                    };
                }
                var input = editor.GetString(new PromptStringOptions("\n输出日志目录")
                { AllowSpaces = true, DefaultValue = path, UseDefaultValue = true });
                return input.Status switch
                {
                    PromptStatus.OK => input.StringResult,
                    PromptStatus.None => "",
                    _ => null
                };
            }, message => editor.WriteMessage("\n" + message), TagCheckPaths.Validate, "无法使用此日志目录：");
    }

    /// <summary>在进入 Plant API 前检查当前文档及现有导出批次。</summary>
    private static Document? Ready(string command)
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document == null) { BatchExportCommands.Write("请先打开一张图纸，再执行 " + command + "。"); return null; }
        if (BatchExportCommands.IsRunning)
        {
            document.Editor.WriteMessage("\nPn3DKit 导出批次正在运行，请等待结束后检查位号。");
            return null;
        }
        return document;
    }

    /// <summary>拒绝在扫描或提示期间发生的文档切换，不取得其他文档的操作权。</summary>
    private static void EnsureDocument(Document document, Database database)
    {
        if (!ReferenceEquals(AcadApp.DocumentManager.MdiActiveDocument, document)
            || !ReferenceEquals(document.Database, database))
            throw new InvalidOperationException("活动图纸已变化，检查已停止。");
    }

    /// <summary>使用宿主提供的中断检查，避免消息泵造成其他 CAD 命令重入。</summary>
    private static void CheckBreak()
    {
        if (HostApplicationServices.Current.UserBreak())
            throw new OperationCanceledException("用户取消。");
    }

    /// <summary>将普通异常和缺失 Plant 环境转换为明确命令行提示。</summary>
    private static void ReportFailure(Editor editor, System.Exception ex) =>
        editor.WriteMessage("\n无法完成位号检查：" + ex.Message
            + (ex is FileNotFoundException or FileLoadException or TypeLoadException
                ? "；请在 AutoCAD Plant 3D 2026 中打开项目后运行此命令。" : ""));
}
