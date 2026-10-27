using Autodesk.ProcessPower.DataObjects;
using Autodesk.ProcessPower.ProjectManager;

namespace Pn3DKit.Interop;

/// <summary>项目类别名称的只读 SDK 适配；与类别检索／格式／数据身份分离。</summary>
internal static partial class PlantTagCatalogAccess
{
    /// <summary>CAD 主线程优先读取项目本地化名称，再回退表名称和内部类名；普通错误允许回退，取消必须传播。</summary>
    /// <param name="part">类别所属的 PnId 或 Piping 项目部分，不借用其他部分的字符串表。</param>
    /// <param name="database">该项目部分的数据表，仅在项目名称缺少或读取失败时访问。</param>
    /// <param name="className">实际内部类别名；只用作查询和最终回退，不改变分组与写入身份。</param>
    /// <returns>优先项目配置的显示名称；未配置时逐级回退。</returns>
    internal static string ReadDisplayName(Project part, PnPDatabase database, string className)
    {
        string? Read(Func<string?> read)
        {
            try { return read(); }
            catch (OperationCanceledException) { throw; }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
                when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak)
            { throw new OperationCanceledException("用户取消读取类别显示名称。", ex); }
            catch (System.Exception) { return null; }
        }
        string? projectName = Read(() => part.GetClassDisplayName(className));
        if (!string.IsNullOrWhiteSpace(projectName)) return projectName;
        string? tableName = Read(() => database.Tables[className].GetTableAttributeValue(PnPDatabase.DisplayNameAttribute));
        return string.IsNullOrWhiteSpace(tableName) ? className : tableName;
    }
}
