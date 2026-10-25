namespace Pn3DKit.Utils;

/// <summary>构造独立 LISP 保存／导出表达式，由 command-s 同步执行并恢复系统变量。</summary>
/// <remarks>独立完成标记与参数均按 Plant 2026 的实际提示顺序提供。</remarks>
public static class CadLispCommands
{
    /// <summary>转义 LISP 字符串中的路径、引号及换行；拒绝空字符。</summary>
    public static string Quote(string text)
    {
        if (text.Contains('\0')) throw new ArgumentException("LISP 参数不能包含空字符。", nameof(text));
        return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    /// <summary>仅执行一次 QSAVE，向独立标记写入 DONE 或 ERROR。</summary>
    public static string Save(string marker) => Wrap(["_.QSAVE"], marker);

    /// <summary>直接按完整格式关键字和输出路径执行 Plant 导出，不插入 Format 菜单或空回车。</summary>
    public static string Export(string output, string marker, string version) =>
        Wrap(ExportPolicy.ExportArguments(output, version).Cast<string>(), marker);

    /// <summary>捕获原生命令错误、恢复变量并在表达式结束前关闭状态文件句柄。</summary>
    private static string Wrap(IEnumerable<string> arguments, string marker) =>
        "((lambda (/ r h fd cd pn) " +
        "(setq fd (getvar \"FILEDIA\") cd (getvar \"CMDDIA\") pn (getvar \"PROXYNOTICE\")) " +
        "(setvar \"FILEDIA\" 0) (setvar \"CMDDIA\" 0) (setvar \"PROXYNOTICE\" 0) " +
        "(setq r (vl-catch-all-apply 'command-s (list " + string.Join(" ", arguments.Select(Quote)) + "))) " +
        "(setvar \"FILEDIA\" fd) (setvar \"CMDDIA\" cd) (setvar \"PROXYNOTICE\" pn) " +
        "(if (setq h (open " + Quote(marker) + " \"w\")) " +
        "(progn (write-line (if (vl-catch-all-error-p r) " +
        "(strcat \"ERROR: \" (vl-catch-all-error-message r)) \"DONE\") h) (close h))) (princ)))";
}
