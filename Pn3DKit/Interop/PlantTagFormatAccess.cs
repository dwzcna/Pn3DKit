using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.ProcessPower.DataLinks;
using Autodesk.ProcessPower.PlantInstance;
using Autodesk.ProcessPower.ProjectManager;

namespace Pn3DKit.Interop;

/// <summary>保存当前对象所属的项目部分及数据关联，只在 CAD 主线程短期使用。</summary>
internal sealed record PlantTagContext(string PartName, Project Part, DataLinksManager Links,
    int RowId, string ClassName);

/// <summary>保存类别继承后的有效位号格式。</summary>
internal sealed record PlantTagFormat(string Name, string Expression, string DefinedForClass);

/// <summary>为查询、单对象检查与扫描提供一致的只读格式解析。</summary>
internal static class PlantTagFormatAccess
{
    /// <summary>按 PnId、Piping 顺序定位对象；非 Plant 对象返回空，无项目或关联损坏抛出异常。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static PlantTagContext? Locate(ObjectId objectId)
    {
        var project = PlantApplication.CurrentProject
            ?? throw new InvalidOperationException("没有当前 Plant 项目，请打开项目后重试。");
        foreach (string partName in new[] { "PnId", "Piping" })
        {
            Project? part;
            try { part = project.ProjectParts[partName]; }
            catch (KeyNotFoundException) { continue; }
            if (part == null) continue;
            var links = part.DataLinksManager;
            if (links == null || !links.HasLinks(objectId)) continue;
            int rowId = links.FindAcPpRowId(objectId);
            if (rowId < 0) throw new InvalidOperationException("Plant 对象有关联但无法定位项目数据行。");
            string className = links.GetObjectClassname(rowId);
            if (string.IsNullOrWhiteSpace(className))
                throw new InvalidOperationException("所选 Plant 部件没有可识别的类别。");
            return new(partName, part, links, rowId, className);
        }
        return null;
    }

    /// <summary>读取实际类别和父类的 TagFormatName；未配置返回空，已配置但无法读取时报告错误。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static PlantTagFormat? ReadFormat(PlantTagContext context)
    {
        var database = context.Links.GetPnPDatabase();
        if (database == null || !database.Tables.Contains(context.ClassName))
            throw new InvalidOperationException("无法读取 Plant 类别表：" + context.ClassName);
        var table = database.Tables[context.ClassName]
            ?? throw new InvalidOperationException("Plant 类别表为空：" + context.ClassName);
        // 非 Local 接口包含继承属性，并采用实际类别的覆盖配置。
        if (!table.GetTableAttributeNames().Contains("TagFormatName")) return null;
        string name = table.GetTableAttributeValue("TagFormatName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!context.Part.GetTagFormat(name, out string expression, out string definedForClass)
            || string.IsNullOrWhiteSpace(expression))
            throw new InvalidOperationException("无法读取类别已配置的位号格式：" + name);
        return new(name, expression, definedForClass);
    }

    /// <summary>在 CAD 主线程返回位号格式说明；此方法不修改对象或项目配置。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string Describe(ObjectId objectId)
    {
        var target = Locate(objectId);
        if (target == null) return "所选对象不是当前 Plant 项目中可识别的 Plant 3D 部件。";
        var format = ReadFormat(target);
        if (format == null)
            return $"所选 Plant 部件不含位号格式。项目部分：{target.PartName}；类别：{target.ClassName}。";
        return $"项目部分：{target.PartName}；部件类别：{target.ClassName}"
            + $"\n位号格式名称：{format.Name}"
            + $"\n格式定义类别：{format.DefinedForClass}"
            + $"\n位号格式：{format.Expression}";
    }
}
