using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.ProcessPower.PlantInstance;
using Autodesk.ProcessPower.ProjectManager;

namespace Pn3DKit.Interop;

/// <summary>通过项目数据关联只读查询部件类别及其当前位号格式。</summary>
internal static class PlantTagFormatAccess
{
    /// <summary>在 CAD 主线程查询 PnId／Piping，返回格式说明或不适用原因。</summary>
    /// <param name="objectId">当前文档中已确认有效的单个对象标识。</param>
    /// <returns>项目部分、实际类别、格式名称及表达式，或未关联／未配置提示。</returns>
    /// <remarks>延迟编译隔离 Plant 依赖；异常由命令入口捕获，不修改对象或项目配置。</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string Describe(ObjectId objectId)
    {
        var project = PlantApplication.CurrentProject;
        if (project == null)
            return "没有当前 Plant 项目，无法读取部件位号格式。";

        foreach (string partName in new[] { "PnId", "Piping" })
        {
            Project? part;
            try { part = project.ProjectParts[partName]; }
            catch (KeyNotFoundException) { continue; }
            if (part == null) continue;

            var links = part.DataLinksManager;
            if (links == null || !links.HasLinks(objectId)) continue;
            var rowId = links.FindAcPpRowId(objectId);
            if (rowId < 0) continue;
            string className = links.GetObjectClassname(rowId);
            if (string.IsNullOrWhiteSpace(className))
                return "所选 Plant 部件没有可识别的类别，无法读取位号格式。";

            string noFormat = $"所选 Plant 部件不含位号格式。项目部分：{partName}；类别：{className}。";
            var database = links.GetPnPDatabase();
            if (database == null || !database.Tables.Contains(className)) return noFormat;
            var table = database.Tables[className];
            // 非 Local 接口包含父类属性，优先采用实际类别的覆盖配置。
            if (table == null || !table.GetTableAttributeNames().Contains("TagFormatName")) return noFormat;
            string formatName = table.GetTableAttributeValue("TagFormatName");
            if (string.IsNullOrWhiteSpace(formatName)
                || !part.GetTagFormat(formatName, out string expression, out string definedForClass)
                || string.IsNullOrWhiteSpace(expression))
                return noFormat;

            return $"项目部分：{partName}；部件类别：{className}"
                + $"\n位号格式名称：{formatName}"
                + $"\n格式定义类别：{definedForClass}"
                + $"\n位号格式：{expression}";
        }

        return "所选对象不是当前 Plant 项目中可识别的 Plant 3D 部件。";
    }
}
