using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Pn3DKit.UI;

/// <summary>命令行C入口的原生文件夹选择器；仅在CAD的STA主线程调用。</summary>
internal static class FolderSelectDialogue
{
    /// <summary>取得当前CAD UI线程的活动窗口，供WPF原生对话框确定所有者。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
    /// <summary>仅切换当前线程活动窗口，退出时恢复，不创建或释放宿主窗口。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr window);

    /// <summary>从建议路径最近的已有父目录浏览；取消返回空，不创建或释放宿主窗口。</summary>
    /// <param name="suggested">允许尚未存在的建议目录。</param>
    /// <param name="ownerHandle">CAD主窗口所有者句柄。</param>
    /// <param name="description">用途专属窗口标题。</param>
    /// <returns>已选目录，后续由统一策略验证。</returns>
    internal static string? Select(string suggested, IntPtr ownerHandle, string description = "Pn3DKit — 选择导出目录")
    {
        string? initial = suggested;
        while (initial != null && !Directory.Exists(initial)) initial = Path.GetDirectoryName(initial);
        var picker = new OpenFolderDialog
        {
            Title = description, Multiselect = false,
            InitialDirectory = initial ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        // .NET 8 的 WPF 公共重载只接收 Window；无所有者重载使用当前线程的活动 HWND。
        // 用公开 Windows API 明确选定CAD窗口，不创建虚假WPF所有者或反射私有接口。
        if (ownerHandle == IntPtr.Zero) throw new InvalidOperationException("CAD主窗口已失效。");
        var previous = GetActiveWindow();
        try
        {
            if (previous != ownerHandle)
            {
                SetActiveWindow(ownerHandle);
                if (GetActiveWindow() != ownerHandle) throw new InvalidOperationException("无法确认目录选择器的CAD窗口所有者。");
            }
            return picker.ShowDialog() == true ? picker.FolderName : null;
        }
        finally { if (previous != IntPtr.Zero && previous != ownerHandle) SetActiveWindow(previous); }
    }
}
