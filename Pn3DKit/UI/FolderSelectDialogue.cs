using Forms = System.Windows.Forms;

namespace Pn3DKit.UI;

/// <summary>由命令行 C 入口直接打开系统文件夹选择器，不显示自定义目录窗口。</summary>
/// <remarks>必须在 CAD 的 STA 主线程调用；不创建批次文件，不取得 CAD 窗口所有权。</remarks>
internal static class FolderSelectDialogue
{
    /// <summary>从建议路径最近的已有父目录开始浏览，返回用户选中的文件夹。</summary>
    /// <param name="suggested">自动建议目录，允许尚未创建。</param>
    /// <param name="ownerHandle">作为窗口所有者的 CAD 主窗口句柄。</param>
    /// <param name="description">选择器标题，默认保持导出用途。</param>
    /// <returns>用户选定的目录；取消时为空，由调用方统一验证路径。</returns>
    internal static string? Select(string suggested, IntPtr ownerHandle, string description = "Pn3DKit — 选择导出目录")
    {
        string? initial = suggested;
        while (initial != null && !Directory.Exists(initial)) initial = Path.GetDirectoryName(initial);
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = description, UseDescriptionForTitle = true,
            AutoUpgradeEnabled = true, ShowNewFolderButton = true,
            InitialDirectory = initial ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        return picker.ShowDialog(new WindowOwner(ownerHandle)) == Forms.DialogResult.OK
            ? picker.SelectedPath : null;
    }

    /// <summary>只包装 CAD 主窗口句柄，选择器关闭时不释放宿主窗口。</summary>
    private sealed record WindowOwner(IntPtr Handle) : Forms.IWin32Window;
}
