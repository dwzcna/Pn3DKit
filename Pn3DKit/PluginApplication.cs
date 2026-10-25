using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Commands;
using Pn3DKit.UI;

[assembly: ExtensionApplication(typeof(Pn3DKit.PluginApplication))]

namespace Pn3DKit;

/// <summary>初始化 NETLOAD 插件入口；加载时不访问 Plant 项目或用户图纸。</summary>
public sealed class PluginApplication : IExtensionApplication
{
    private RibbonController? _ribbon;
    /// <summary>安装可延迟创建的 Ribbon，并输出命令入口。</summary>
    public void Initialize()
    {
        _ribbon = new RibbonController();
        _ribbon.Start();
        BatchExportCommands.Write($"Pn3DKit {typeof(PluginApplication).Assembly.GetName().Version} 已加载。"
            + "命令：PNDWGEXPORT、GETPNTAGFORMAT、EVALUATEPNTAG。");
    }

    /// <summary>在 CAD 退出时解除 Ribbon 和应用事件，不关闭或保存图纸。</summary>
    public void Terminate()
    {
        _ribbon?.Dispose();
        _ribbon = null;
    }
}
