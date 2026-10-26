using Autodesk.AutoCAD.EditorInput;
using Pn3DKit.Utils;
using Pn3DKit.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.Interop;

/// <summary>六类批次共用的目录提示入口，FILEDIA 只读，所有交互在 CAD 主线程执行。</summary>
internal static class CadOutputDirectoryPrompt
{
    /// <summary>按 FILEDIA 在命令行确认目录，明确选择 C 时才打开宿主拥有的文件夹选择器。</summary>
    /// <param name="editor">本批次原图纸的命令行编辑器。</param>
    /// <param name="suggested">回车采用的建议绝对目录。</param>
    /// <param name="ensureContext">交互及验证前后核对原图纸和项目；失败直接停止，不作为路径错误重试。</param>
    /// <param name="validate">用途专属验证，保持各命令的文件冲突规则。</param>
    /// <param name="purpose">窗口标题中的用途。</param>
    /// <param name="logDirectory">日志目录为 true，导出目录为 false。</param>
    /// <returns>已验证目录；Esc 或选择器取消时为空，不创建日志。</returns>
    internal static string? Select(Editor editor, string suggested, Action ensureContext,
        Func<string, string> validate, string purpose, bool logDirectory = true)
    {
        ensureContext();
        int fileDia = Convert.ToInt32(AcadApp.GetSystemVariable("FILEDIA"));
        string label = logDirectory ? "日志" : "导出";
        return OutputDirectorySelection.Select(fileDia, suggested,
            path => FolderSelectDialogue.Select(path, AcadApp.MainWindow.Handle,
                "Pn3DKit — 选择" + purpose + (logDirectory ? "日志" : "") + "目录"),
            path =>
            {
                if (fileDia != 0)
                {
                    var prompt = new PromptKeywordOptions("\n输出" + label + "目录<" + path + "> 或 [选择..(C)]: ")
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
                var input = editor.GetString(new PromptStringOptions("\n输出" + label + "目录")
                { AllowSpaces = true, DefaultValue = path, UseDefaultValue = true });
                return input.Status switch
                {
                    PromptStatus.OK => input.StringResult,
                    PromptStatus.None => "",
                    _ => null
                };
            }, message => TagCheckCommandsWrite(message), validate,
            "无法使用此" + label + "目录：", ensureContext);
    }

    /// <summary>图纸失效时仍通过现有容错输出报告原因，不更换本批次编辑器发起输入。</summary>
    private static void TagCheckCommandsWrite(string message) =>
        Pn3DKit.Commands.TagCheckCommands.WriteSafely("\n" + message);
}
