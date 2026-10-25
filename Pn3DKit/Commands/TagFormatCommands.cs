using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Interop;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.Commands.TagFormatCommands))]

namespace Pn3DKit.Commands;

/// <summary>查询单个 Plant 部件的位号格式，并按输入格式计算位号。</summary>
public sealed class TagFormatCommands
{
    /// <summary>在当前文档命令上下文读取格式，所有异常转为命令行提示。</summary>
    /// <remarks>支持 PICKFIRST 预选；交互选择以回车确认，不写入数据库或保存图纸。</remarks>
    [CommandMethod("GETPNTAGFORMAT", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void GetPnTagFormat()
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            BatchExportCommands.Write("请先打开一张图纸，再执行 GETPNTAGFORMAT。");
            return;
        }
        var editor = document.Editor;
        if (BatchExportCommands.IsRunning)
        {
            editor.WriteMessage("\nPn3DKit 批次正在运行，请等待批次结束后查询位号格式。");
            return;
        }
        try
        {
            var objectId = SelectTarget(editor, "GETPNTAGFORMAT");
            if (objectId.HasValue)
                editor.WriteMessage("\n" + PlantTagFormatAccess.Describe(objectId.Value));
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\n无法读取位号格式：" + ex.Message
                + (ex is FileNotFoundException or FileLoadException or TypeLoadException
                    ? "；请在 AutoCAD Plant 3D 2026 中打开项目后运行此命令。" : ""));
        }
    }

    /// <summary>选择单个部件并输入格式，将 SDK 计算的位号输出到命令行。</summary>
    /// <remarks>支持 PICKFIRST 和交互选择；不主动赋号，SDK 求值可能更新序列字段，不保证项目数据不变。</remarks>
    [CommandMethod("EVALUATEPNTAG", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void EvaluatePnTag()
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            BatchExportCommands.Write("请先打开一张图纸，再执行 EVALUATEPNTAG。");
            return;
        }
        var editor = document.Editor;
        if (BatchExportCommands.IsRunning)
        {
            editor.WriteMessage("\nPn3DKit 批次正在运行，请等待批次结束后计算位号。");
            return;
        }
        try
        {
            editor.WriteMessage("\n" + TagEvaluationNotice.Text);
            var objectId = SelectTarget(editor, "EVALUATEPNTAG");
            editor.WriteMessage("\nPn3DKit " + typeof(TagFormatCommands).Assembly.GetName().Version
                + " | EVALUATEPNTAG");
            if (!objectId.HasValue) return;
            var input = editor.GetString(new PromptStringOptions("\n请输入位号格式: ")
            {
                AllowSpaces = true
            });
            if (input.Status == PromptStatus.Cancel) return;
            if (input.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n未输入位号格式。");
                return;
            }
            string format = input.StringResult.Trim();
            if (format.Length >= 2 && format[0] == '"' && format[^1] == '"')
                format = format[1..^1];
            if (string.IsNullOrWhiteSpace(format))
            {
                editor.WriteMessage("\n位号格式不能为空。");
                return;
            }
            editor.WriteMessage("\n" + PlantTagEvaluationAccess.Evaluate(objectId.Value, format));
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\n无法计算位号：" + ex.Message
                + (ex is FileNotFoundException or FileLoadException or TypeLoadException
                    ? "；请在 AutoCAD Plant 3D 2026 中打开项目后运行此命令。" : ""));
        }
    }

    /// <summary>复用预选或交互选择，仅接受一个仍有效的部件候选对象。</summary>
    /// <param name="editor">当前文档编辑器，必须在该文档命令上下文调用。</param>
    /// <param name="commandName">选择数量不符时提示用户重新执行的命令名称。</param>
    /// <returns>有效对象标识；取消、空选或选择数量不符时返回空。</returns>
    internal static ObjectId? SelectTarget(Editor editor, string commandName)
    {
        var selection = editor.SelectImplied();
        if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
        {
            selection = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\n请选择一个 Plant 3D 部件，按回车确认: ",
                MessageForRemoval = "\n请选择要移除的对象: ",
                AllowDuplicates = false
            });
        }
        if (selection.Status == PromptStatus.Cancel) return null;
        if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
        {
            editor.WriteMessage("\n未选择部件。");
            return null;
        }
        if (selection.Value.Count != 1)
        {
            editor.WriteMessage("\n请仅选择一个部件，再执行 " + commandName + "。");
            return null;
        }
        var objectId = selection.Value.GetObjectIds()[0];
        if (objectId.IsNull || !objectId.IsValid || objectId.IsErased)
        {
            editor.WriteMessage("\n所选对象已失效，请重新选择部件。");
            return null;
        }
        return objectId;
    }
}
