using System.Runtime.CompilerServices;
using Autodesk.ProcessPower.PlantInstance;
using Autodesk.ProcessPower.ProjectManager;
using Pn3DKit.Models;

namespace Pn3DKit.Interop;

/// <summary>将 Plant API 访问隔离在延迟编译的方法中，避免 NETLOAD 初始化强制加载 Plant 模块。</summary>
internal static class PlantProjectAccess
{
    /// <summary>在 CAD 主线程读取当前项目和两类清单，不打开、保存或关闭任何图纸。</summary>
    /// <returns>固定清单与检查当前项目对象身份的回调。</returns>
    /// <remarks>普通 AutoCAD 缺少 Plant 模块时允许加载异常向命令入口传播并显示提示。</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (ProjectCatalog Catalog, Func<bool> IsCurrent) Read()
    {
        var project = PlantApplication.CurrentProject
            ?? throw new InvalidOperationException("没有当前 Plant 项目，请先在 Plant 3D 中打开项目。");
        IReadOnlyList<string>? ReadPart(string name)
        {
            Project? part;
            try { part = project.ProjectParts[name]; }
            catch (KeyNotFoundException) { return null; }
            return part?.GetPnPDrawingFiles().Select(d => d.ResolvedFilePath).ToArray();
        }
        var catalog = new ProjectCatalog(project.ProjectFolderPath, ReadPart("PnId"), ReadPart("Piping"));
        return (catalog, () => ReferenceEquals(PlantApplication.CurrentProject, project));
    }
}
