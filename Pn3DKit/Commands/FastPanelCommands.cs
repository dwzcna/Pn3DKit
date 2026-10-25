using Autodesk.AutoCAD.Runtime;
using Pn3DKit.UI;

[assembly: CommandClass(typeof(Pn3DKit.Commands.FastPanelCommands))]

namespace Pn3DKit.Commands;

/// <summary>打开 PN快速特性 Plant 属性面板，保留预选，不发起选择提示或产生日志。</summary>
public sealed class FastPanelCommands
{
    /// <summary>命令入口捕获界面创建异常；不依赖 Plant 项目，不创建批次任务。</summary>
    [CommandMethod("PNFASTPANEL", CommandFlags.Session | CommandFlags.UsePickSet | CommandFlags.Redraw)]
    public void ShowFastPanel()
    {
        if (BatchExportCommands.IsRunning) { BatchExportCommands.Write("Pn3DKit 批次正在运行，请等待结束后打开面板。"); return; }
        try { FastPanelController.Show(); }
        catch (System.Exception ex) { BatchExportCommands.Write("无法打开 PN快速特性：" + ex.Message); }
    }
}
