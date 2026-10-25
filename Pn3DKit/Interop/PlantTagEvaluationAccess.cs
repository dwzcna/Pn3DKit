using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.ProcessPower.DataLinks;
using Autodesk.ProcessPower.PlantInstance;
using Autodesk.ProcessPower.PnPCommonDbx;
using Autodesk.ProcessPower.ProjectManager;
using Pn3DKit.Utils;
using CadException = Autodesk.AutoCAD.Runtime.Exception;

namespace Pn3DKit.Interop;

/// <summary>按 SDK 格式上下文读取目标、安装管线及管线组字段后计算位号。</summary>
internal static class PlantTagEvaluationAccess
{
    /// <summary>在 CAD 文档命令上下文检查格式并求值，仅对已确认缺失的属性提前提示。</summary>
    /// <param name="objectId">当前文档中已确认有效的单个对象标识。</param>
    /// <param name="tagFormat">已去除命令行外层引号的非空格式字符串。</param>
    /// <returns>SDK 求值结果、明确缺失的原始片段或带上下文的 SDK 失败信息。</returns>
    /// <remarks>延迟访问 Plant；不调用位号赋值、属性写回或保存命令，外部异常由命令入口处理。</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string Evaluate(ObjectId objectId, string tagFormat)
    {
        var project = PlantApplication.CurrentProject;
        if (project == null) return "没有当前 Plant 项目，无法计算部件位号。";

        foreach (string partName in new[] { "PnId", "Piping" })
        {
            Project? part;
            try { part = project.ProjectParts[partName]; }
            catch (KeyNotFoundException) { continue; }
            if (part == null) continue;
            var links = part.DataLinksManager;
            if (links == null || !links.HasLinks(objectId)) continue;
            int targetRowId = links.FindAcPpRowId(objectId);
            if (targetRowId < 0) continue;
            if (!FormatStringUtils.IsValid(tagFormat)) return "位号格式无效，请检查格式表达式。";

            var formatTools = new FormatToolsUtil();
            var database = links.GetPnPDatabase();
            if (database == null) return "无法读取部件所属的 Plant 项目数据库。";
            var scopedFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            TagPropertyAvailability ReadScopedProperty(string scope, string property)
            {
                string path = scope + "." + property;
                try
                {
                    // 必须保留 Where.What 上下文；空字符串也是一次成功读取。
                    _ = formatTools.RetrieveProperty(path, objectId);
                    return TagPropertyAvailability.Present;
                }
                catch (CadException ex)
                {
                    scopedFailures[path] = ex.ErrorStatus + ": " + ex.Message;
                }

                // SDK 单字段读取失败后，仅使用能明确定位的类别／官方关系检查定义。
                string actualClass = links.GetObjectClassname(targetRowId);
                if (!string.IsNullOrWhiteSpace(actualClass) && database.Tables.Contains(actualClass))
                {
                    var targetTable = database.Tables[actualClass];
                    if (targetTable != null && (actualClass.Equals(scope, StringComparison.OrdinalIgnoreCase)
                        || (database.Tables.Contains(scope) && targetTable.IsKindOf(scope))))
                        return links.HasProperty(targetRowId, property)
                            ? TagPropertyAvailability.Present : TagPropertyAvailability.Missing;
                }

                string? relationship = null, lineRole = null, groupRole = null;
                if (scope.Equals("PipeLineGroup", StringComparison.OrdinalIgnoreCase))
                    (relationship, lineRole, groupRole) = ("PipeLineGroupRelationship", "PipeLine", "PipeLineGroup");
                else if (scope.Equals("SignalLineGroup", StringComparison.OrdinalIgnoreCase))
                    (relationship, lineRole, groupRole) = ("SignalLineGroupRelationship", "SignalLine", "SignalLineGroup");
                if (relationship != null)
                {
                    try
                    {
                        var groupRows = links.GetRelatedRowIds(relationship, lineRole!, targetRowId, groupRole!);
                        bool foundGroup = false;
                        foreach (int groupId in groupRows)
                        {
                            string groupClass = links.GetObjectClassname(groupId);
                            if (string.IsNullOrWhiteSpace(groupClass) || !database.Tables.Contains(groupClass))
                                continue;
                            var groupTable = database.Tables[groupClass];
                            if (groupTable == null) continue;
                            foundGroup = true;
                            if (groupTable.AllColumns.Contains(property) || links.HasProperty(groupId, property))
                                return TagPropertyAvailability.Present;
                        }
                        if (foundGroup) return TagPropertyAvailability.Missing;
                    }
                    catch (DLException ex)
                    {
                        scopedFailures[path] += "；关系查询：" + ex.ErrorCode;
                    }
                }
                // 未定位不等于不存在；阀门安装管线等上下文继续交给完整求值器。
                return TagPropertyAvailability.Unresolved;
            }

            var missing = new List<string>();
            var unresolved = new List<string>();
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match token in Regex.Matches(tagFormat, @"#\([^()]*\)"))
            {
                var fields = formatTools.Tokenize(token.Value);
                if (fields == null) return "无法解析位号格式片段：" + token.Value;
                foreach (var field in fields)
                {
                    if (field.meSource != FormatSource.Class || string.IsNullOrWhiteSpace(field.msPropertyName))
                        continue;
                    var availability = TagPropertyScope.Check(field.msClassOrCategory, field.msPropertyName,
                        name => links.HasProperty(targetRowId, name), ReadScopedProperty);
                    if (availability == TagPropertyAvailability.Missing && reported.Add(token.Value))
                        missing.Add("所选目标不含属性 " + token.Value);
                    else if (availability == TagPropertyAvailability.Unresolved)
                    {
                        string path = field.msClassOrCategory.TrimStart('+', '?', '=') + "." + field.msPropertyName;
                        string detail = scopedFailures.GetValueOrDefault(path, "单字段上下文未定位");
                        unresolved.Add("无法单独确认格式属性 " + token.Value + "（" + path + "；" + detail + "）");
                    }
                }
            }
            if (missing.Count != 0) return string.Join("\n", missing);

            try
            {
                string result = FormatStringUtils.Evaluate(tagFormat, objectId);
                if (!string.IsNullOrWhiteSpace(result)) return "位号：" + result;
                return "SDK 位号计算未返回有效结果。"
                    + (unresolved.Count == 0 ? "" : "\n" + string.Join("\n", unresolved.Distinct()));
            }
            catch (CadException ex)
            {
                return "SDK 无法计算位号（" + ex.ErrorStatus + "）：" + ex.Message
                    + (unresolved.Count == 0 ? "" : "\n" + string.Join("\n", unresolved.Distinct()));
            }
        }
        return "所选对象不是当前 Plant 项目中可识别的 Plant 3D 部件。";
    }
}
