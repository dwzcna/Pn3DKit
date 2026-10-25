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

/// <summary>在当前文档命令上下文对比单个及按类别的位号；SDK 求值可能更新序列字段。</summary>
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
            editor.WriteMessage("\n" + TagEvaluationNotice.Text);
            var id = TagFormatCommands.SelectTarget(editor, "CHECKPNTAG");
            if (!id.HasValue) return;
            var result = PlantTagCheckAccess.Check(id.Value);
            if (result.Subject.ClassName.Length != 0)
                editor.WriteMessage($"\n项目部分：{result.Subject.ProjectPart}；部件类别：{result.Subject.ClassName}；句柄：{result.Subject.Handle}");
            editor.WriteMessage("\n" + result.Message);
            if (result.Details.Length != 0 && result.Status != TagCheckStatus.Error)
                editor.WriteMessage("\n" + result.Details);
        }
        catch (OperationCanceledException) { WriteSafely("\n已取消检查。"); }
        catch (System.Exception ex) { ReportFailure(ex); }
    }

    /// <summary>扫描当前图纸模型空间，按所选类别逐个检查并保存即时日志。</summary>
    /// <remarks>全程在 CAD 主线程执行，不切换、打开、关闭或保存文档；Esc 保留已写记录。</remarks>
    [CommandMethod("CHECKPNTAGBYCLASS", CommandFlags.Modal)]
    public void CheckPnTagByClass()
    {
        string? output = null;
        var files = new RunLogFiles();
        IDisposable? lease = null;
        bool issues = true;
        Action ensureRetention = () => { };
        try
        {
            var document = Ready("CHECKPNTAGBYCLASS");
            if (document == null) return;
            lease = BatchRunGate.TryEnter();
            if (lease == null) { WriteSafely("\n已有 Pn3DKit 批次正在运行。"); return; }
            Database database;
            ObjectId databaseIdentity;
            try
            {
                database = document.Database;
                databaseIdentity = database.IsDisposed ? ObjectId.Null : database.BlockTableId;
            }
            catch (System.Exception ex)
            { throw new InvalidOperationException("无法读取数据库状态：" + ex.Message, ex); }
            EnsureDocument(database, databaseIdentity);
            ensureRetention = () => EnsureDocument(database, databaseIdentity);
            var editor = document.Editor;
            editor.WriteMessage("\n" + TagEvaluationNotice.Text);
            var scan = PlantTagCheckAccess.Scan(database, () =>
            {
                EnsureDocument(database, databaseIdentity);
                CheckBreak();
            });
            EnsureDocument(database, databaseIdentity);
            var available = scan.Groups.Where(g => g.HasFormat || g.FormatError != null).ToArray();
            var skipped = scan.Groups.Where(g => !g.HasFormat && g.FormatError == null).ToArray();
            string skippedText = skipped.Length == 0 ? ""
                : "未配置位号格式的类别已跳过："
                    + string.Join("、", skipped.Select(g => g.DisplayName + " " + g.Targets.Count + g.Unit))
                    + $"（共 {skipped.Select(g => g.ClassName).Distinct().Count()} 类，{skipped.Sum(g => g.Targets.Count)} 个目标）";
            if (skippedText.Length != 0) WriteSafely("\n" + skippedText);
            foreach (string warning in scan.Warnings.Take(20))
                WriteSafely("\n" + warning);
            if (scan.Warnings.Count > 20)
                WriteSafely($"\n另有 {scan.Warnings.Count - 20} 条识别警告，执行检查时将写入日志。");
            if (available.Length == 0)
            {
                foreach (string diagnostic in scan.Diagnostics) WriteSafely("\n扫描诊断：" + diagnostic);
                WriteSafely("\n当前图纸模型空间没有可检查的位号类别。");
                return;
            }
            for (int i = 0; i < available.Length; i++)
            {
                var group = available[i];
                string part = available.Count(g => g.ClassName == group.ClassName) > 1
                    ? " [" + group.PartName + "]" : "";
                WriteSafely($"\n{i + 1} - {group.DisplayName}{part} ({group.Targets.Count}{group.Unit})"
                    + (group.FormatError == null ? "" : "（格式读取失败，检查结果将记录原因）"));
            }

            int[] numbers;
            while (true)
            {
                EnsureDocument(database, databaseIdentity);
                var input = editor.GetString(new PromptStringOptions(
                    "\n请选择类别编号 [如 1,3,5-7；*=全部] <0>: ") { AllowSpaces = true });
                EnsureDocument(database, databaseIdentity);
                if (input.Status == PromptStatus.Cancel) return;
                if (input.Status is not (PromptStatus.OK or PromptStatus.None)) return;
                try
                {
                    numbers = TagCheckPolicy.ParseClasses(
                        input.Status == PromptStatus.None ? "" : input.StringResult, available.Length);
                    break;
                }
                catch (ArgumentException ex) { WriteSafely("\n" + ex.Message); }
            }
            if (numbers.Length == 0)
            {
                WriteSafely("\n未选择类别，未执行检查");
                return;
            }
            var selected = numbers.Select(number => available[number - 1]).ToArray();
            var objects = selected.SelectMany(g => g.Targets).DistinctBy(item => item.Key).ToArray();
            string[] classes = selected.Select(g => g.ClassName).Distinct().ToArray();
            string documents = RunPaths.Documents();
            string suggested = TagCheckPaths.Suggest(documents, DateTime.Now);
            output = SelectDirectory(editor, suggested, () => EnsureDocument(database, databaseIdentity));
            if (output == null) return;

            void EnsureContext()
            {
                EnsureDocument(database, databaseIdentity);
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
            using var journal = new TagCheckJournal(output, drawing, classes, diagnostics, files);
            WriteSafely("\n日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath);
            try
            {
                var result = TagCheckBatch.Run(objects, item =>
                {
                    var checkedItem = PlantTagCheckAccess.Check(item);
                    return TagCheckPolicy.BindToScannedClass(checkedItem, item.Context.PartName, item.Context.ClassName);
                }, EnsureContext, journal,
                    (done, total) => WriteSafely($"\n位号检查进度：{done}/{total}"));
                issues = scan.Warnings.Count != 0 || result.StopReason != null
                    || LogRetentionPolicy.CheckHasIssues(result.Items, result.Total, result.Interrupted);
                foreach (string className in classes)
                    WriteSafely("\n" + className + "：" + TagCheckPolicy.Summary(
                        result.Items.Where(x => x.Subject.ClassName == className)));
                var mismatches = result.Items.Where(x => x.Status == TagCheckStatus.Mismatch).ToArray();
                foreach (var item in mismatches.Take(20))
                    WriteSafely($"\n不符：{item.Subject.ExistingTag}；句柄 {item.Subject.Handle}；期望 {item.ExpectedTag}");
                if (mismatches.Length > 20)
                    WriteSafely($"\n另有 {mismatches.Length - 20} 条不符，请查看完整日志。");
                WriteSafely($"\n{(result.Interrupted ? "已中断" : result.StopReason == null ? "检查完成" : "检查已停止")}；已处理 {result.Items.Count}，未处理 {result.Remaining}。");
                if (result.StopReason != null) WriteSafely("\n停止原因：" + result.StopReason);
            }
            finally { WriteSafely("\n日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath); }
        }
        catch (OperationCanceledException) { WriteSafely("\n已取消检查。"); }
        catch (System.Exception ex)
        {
            ReportFailure(ex);
            if (output != null) WriteSafely("\n日志目录：" + output);
            issues = true;
        }
        finally
        {
            try { LogRetentionPrompt.Finish(files, issues, ensureRetention); }
            finally { lease?.Dispose(); }
        }
    }

    /// <summary>复用 FILEDIA 分流和系统文件夹选择器，在交互前后核对数据库身份。</summary>
    /// <param name="editor">本批次开始时取得的命令行编辑器。</param>
    /// <param name="suggested">回车采用的建议日志目录。</param>
    /// <param name="ensureDocument">在输入及选择器返回时检查原数据库仍为活动图纸。</param>
    internal static string? SelectDirectory(Editor editor, string suggested, Action ensureDocument,
        Func<string, string>? validate = null, string title = "位号检查")
    {
        int fileDia = Convert.ToInt32(AcadApp.GetSystemVariable("FILEDIA"));
        return OutputDirectorySelection.Select(fileDia, suggested,
            path =>
            {
                ensureDocument();
                string? selected = FolderSelectDialogue.Select(path, AcadApp.MainWindow.Handle, "Pn3DKit — 选择" + title + "日志目录");
                ensureDocument();
                return selected;
            },
            path =>
            {
                ensureDocument();
                if (fileDia != 0)
                {
                    var prompt = new PromptKeywordOptions("\n输出日志目录<" + path + "> 或 [选择..(C)]: ")
                    { AllowNone = true, AppendKeywordsToMessage = false };
                    prompt.Keywords.Add("C", "C", "选择..(C)");
                    var result = editor.GetKeywords(prompt);
                    ensureDocument();
                    return result.Status switch
                    {
                        PromptStatus.OK => result.StringResult,
                        PromptStatus.None => "",
                        _ => null
                    };
                }
                var input = editor.GetString(new PromptStringOptions("\n输出日志目录")
                { AllowSpaces = true, DefaultValue = path, UseDefaultValue = true });
                ensureDocument();
                return input.Status switch
                {
                    PromptStatus.OK => input.StringResult,
                    PromptStatus.None => "",
                    _ => null
                };
            }, message => WriteSafely("\n" + message), validate ?? TagCheckPaths.Validate, "无法使用此日志目录：");
    }

    /// <summary>在进入 Plant API 前检查当前文档及现有导出批次。</summary>
    private static Document? Ready(string command)
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document == null) { WriteSafely("\n当前没有活动图纸。"); return null; }
        if (BatchExportCommands.IsRunning)
        {
            WriteSafely("\nPn3DKit 批次正在运行，请等待结束后检查位号。");
            return null;
        }
        return document;
    }

    /// <summary>以开始时的完整块表 ObjectId 核对底层数据库，拒绝真实图纸切换。</summary>
    /// <param name="originalDatabase">仅用于检查原数据库是否已释放，不比较托管引用。</param>
    /// <param name="databaseIdentity">命令开始时记录的完整 BlockTableId，不转换为句柄或文件名。</param>
    /// <remarks>先核对原数据库和标识有效性，再读取活动数据库；宿主状态异常附原始信息。</remarks>
    internal static void EnsureDocument(Database originalDatabase, ObjectId databaseIdentity)
    {
        string? stopReason = null;
        try
        {
            if (originalDatabase.IsDisposed || databaseIdentity.IsNull
                || !databaseIdentity.IsValid || databaseIdentity.IsErased)
                stopReason = "原图纸数据库已失效或关闭。";
            else
            {
                var active = AcadApp.DocumentManager.MdiActiveDocument;
                if (active == null)
                    stopReason = "当前没有活动图纸。";
                else
                {
                    var currentDatabase = active.Database;
                    if (currentDatabase.IsDisposed)
                        stopReason = "无法读取数据库状态：当前活动图纸数据库已释放。";
                    else
                    {
                        var currentIdentity = currentDatabase.BlockTableId;
                        if (currentIdentity.IsNull || !currentIdentity.IsValid || currentIdentity.IsErased)
                            stopReason = "无法读取数据库状态：当前活动图纸的块表标识无效。";
                        else if (currentIdentity != databaseIdentity)
                            stopReason = "当前活动图纸与开始检查时不同。";
                    }
                }
            }
        }
        catch (System.Exception ex)
        { throw new InvalidOperationException("无法读取数据库状态：" + ex.Message, ex); }
        if (stopReason != null) throw new InvalidOperationException(stopReason);
    }

    /// <summary>向当前可用的编辑器输出；图纸关闭或输出失败时降级到跟踪，不再次访问旧 Editor。</summary>
    /// <param name="message">包含所需换行的完整提示或日志路径。</param>
    internal static void WriteSafely(string message)
    {
        System.Exception? failure = null;
        try
        {
            var current = AcadApp.DocumentManager.MdiActiveDocument;
            if (current != null)
            {
                current.Editor.WriteMessage(message);
                return;
            }
        }
        catch (System.Exception ex) { failure = ex; }
        try
        {
            System.Diagnostics.Trace.WriteLine("Pn3DKit：" + message
                + (failure == null ? "" : "；输出失败：" + failure.Message));
        }
        catch (System.Exception) { /* 跟踪监听器失效时不让提示覆盖原始停止原因。 */ }
    }

    /// <summary>使用宿主提供的中断检查，避免消息泵造成其他 CAD 命令重入。</summary>
    internal static void CheckBreak()
    {
        if (HostApplicationServices.Current.UserBreak())
            throw new OperationCanceledException("用户取消。");
    }

    /// <summary>将普通异常和缺失 Plant 环境转换为明确命令行提示。</summary>
    private static void ReportFailure(System.Exception ex) =>
        WriteSafely("\n无法完成位号检查：" + ex.Message
            + (ex is FileNotFoundException or FileLoadException or TypeLoadException
                ? "；请在 AutoCAD Plant 3D 2026 中打开项目后运行此命令。" : ""));
}
