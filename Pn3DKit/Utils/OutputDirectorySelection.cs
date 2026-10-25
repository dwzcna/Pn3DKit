namespace Pn3DKit.Utils;

/// <summary>按 FILEDIA 选择一次批次目录，隔离可离线验证的命令行流程。</summary>
public static class OutputDirectorySelection
{
    /// <summary>先在命令行确认目录；FILEDIA 非零且明确选择 C 时才打开系统文件夹选择器。</summary>
    /// <remarks>所有回调在调用线程同步执行；由 CAD 主线程调用。命令行验证不创建目录。</remarks>
    /// <param name="fileDia">宿主当前 FILEDIA 值，本方法不更改该变量。</param>
    /// <param name="suggested">回车采用的自动建议绝对路径。</param>
    /// <param name="showDialog">显示系统文件夹选择器；取消返回空值，返回路径统一验证。</param>
    /// <param name="readLine">零值时读取目录文本，非零时读取 C 关键字或回车；取消返回空值。</param>
    /// <param name="notify">输出路径无效或冲突的原因，之后重新提示。</param>
    /// <returns>确认后的完整目录；取消时为空。</returns>
    public static string? Select(int fileDia, string suggested, Func<string, string?> showDialog,
        Func<string, string?> readLine, Action<string> notify)
    {
        while (true)
        {
            string? input = readLine(suggested);
            if (input == null) return null;
            if (fileDia != 0 && input.Equals("C", StringComparison.OrdinalIgnoreCase))
            {
                input = showDialog(suggested);
                if (input == null) return null;
            }
            string path = input.Trim();
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
            try { return ExportPolicy.ValidateOutputDirectory(path.Length == 0 ? suggested : path); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                notify("无法使用此导出目录：" + ex.Message);
            }
        }
    }
}
