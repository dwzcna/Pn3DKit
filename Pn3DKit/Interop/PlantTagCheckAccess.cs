using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.ProcessPower.DataLinks;
using Autodesk.ProcessPower.PlantInstance;
using Autodesk.ProcessPower.PnPCommonDbx;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using CadException = Autodesk.AutoCAD.Runtime.Exception;

namespace Pn3DKit.Interop;



/// <summary>在 CAD 主线程以只读方式检查当前项目的对象位号。</summary>
internal static class PlantTagCheckAccess
{
    /// <summary>对 SDK 异常补充稳定状态码；用户中断必须向批次传播。</summary>
    private static T Sdk<T>(Func<T> read)
    {
        try { return read(); }
        catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        { throw new OperationCanceledException("用户取消。", ex); }
        catch (CadException ex)
        { throw new InvalidOperationException($"SDK ErrorStatus={ex.ErrorStatus}：{ex.Message}", ex); }
        catch (DLException ex)
        { throw new InvalidOperationException($"SDK DataLinks ErrorCode={ex.ErrorCode}：{ex.Message}", ex); }
    }

    /// <summary>单对象选择仍按需定位；批量扫描不通过此入口发现类别。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static TagCheckResult Check(ObjectId objectId)
    {
        try
        {
            if (objectId.IsNull || !objectId.IsValid || objectId.IsErased)
                throw new InvalidOperationException("目标对象已失效或被删除。");
            var context = Sdk(() => PlantTagFormatAccess.Locate(objectId))
                ?? throw new InvalidOperationException("所选对象不是当前 Plant 项目中可识别的 Plant 3D 部件。");
            var table = context.Links.GetPnPDatabase().Tables[context.ClassName];
            var kind = table.IsKindOf("PipeLines") ? TagTargetKind.LineSegment : TagTargetKind.Component;
            return Check(new PlantTagTarget(context, kind, [objectId], [objectId.Database.Filename], "", []));
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Exception ex)
        { return new(new("", "", "", "", ""), "", TagCheckStatus.Error, [], ex.Message); }
    }

    /// <summary>统一检查图形或数据行目标；组读取自身格式，不借用成员 SLINE 格式。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static TagCheckResult Check(PlantTagTarget target, bool refreshFormat = false)
    {
        var subject = target.Subject;
        try
        {
            if (target.LinkError != null) throw new InvalidOperationException(target.LinkError);
            var context = target.Context;
            var row = Sdk(() => context.Links.GetPnPDatabase().GetRow(context.RowId))
                ?? throw new InvalidOperationException("目标数据行已失效。");
            if (row.ClassName != context.ClassName) throw new InvalidOperationException("目标实际类别已变化，请重新扫描。");
            if (!refreshFormat && target.FormatError != null) throw new InvalidOperationException(target.FormatError);
            var format = !refreshFormat && target.FormatKnown ? target.CachedFormat
                : Sdk(() => PlantTagFormatAccess.ReadFormat(context));
            subject = subject with { Format = format?.Expression ?? "" };
            if (format == null) return new(subject, "", TagCheckStatus.NoFormat, []);
            ObjectId id = target.ObjectIds.FirstOrDefault(x => x.IsValid && !x.IsErased);
            if (target.Kind != TagTargetKind.LineGroup && target.ObjectIds.Length != 0 && id.IsNull)
                throw new InvalidOperationException("目标图形已失效或删除。");
            string ReadTag(string name)
            {
                if (!context.Links.HasProperty(context.RowId, name)) return "";
                var names = new StringCollection { name };
                var values = id.IsNull ? context.Links.GetProperties(context.RowId, names, true)
                    : context.Links.GetProperties(id, names, true);
                return values.Count == 0 ? "" : values[0] ?? "";
            }
            string tag = Sdk(() => TagCheckPolicy.ReadExistingTag(context.PartName, ReadTag));
            subject = subject with { ExistingTag = tag };
            var tools = new FormatToolsUtil();
            return TagCheckPolicy.Check(subject,
                () => Sdk(() => id.IsNull ? tools.RebuildTagValue(subject.Format, tag, context.RowId, context.Links)
                    : tools.RebuildTagValue(subject.Format, tag, id)),
                () => Sdk(() => ReadFields(tools, id, context, subject.Format, tag)));
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Exception ex) { return new(subject, "", TagCheckStatus.Error, [], ex.Message); }
    }

    /// <summary>按 SDK 拆出的旧值读取当前属性；只对非自动编号属性片段求值以保留格式规则。</summary>
    /// <remarks>任何整式 Evaluate、自动编号及 AssignProperty 调用均不属于检查流程。</remarks>
    private static IReadOnlyList<TagCheckField> ReadFields(FormatToolsUtil tools, ObjectId objectId,
        PlantTagContext context, string format, string tag)
    {
        if (!FormatStringUtils.IsValid(format))
            throw new InvalidOperationException("位号格式无效：" + format);
        var tokens = Regex.Matches(format, @"#\([^()]*\)");
        string? Evaluate(string token) => objectId.IsNull
            ? FormatStringUtils.Evaluate(token, context.RowId, context.Links)
            : FormatStringUtils.Evaluate(token, objectId);
        var values = (objectId.IsNull
            ? tools.TokenizeValueAgainstFormatString(format, tag, true, context.RowId, context.Links)
            : tools.TokenizeValueAgainstFormatString(format, tag, objectId, true))
            ?? throw new InvalidOperationException("SDK 无法拆分现有 Tag。");
        if (values.Length != tokens.Count)
            values = values.Where(f => f.meSource != FormatSource.None || f.mbFormat || f.mbAutogen).ToArray();
        if (values.Length != tokens.Count)
            throw new InvalidOperationException("SDK 拆分字段数与格式片段数不符。");

        var result = new List<TagCheckField>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i].Value;
            var definitions = tools.Tokenize(token);
            if (definitions == null || definitions.Length != 1)
                throw new InvalidOperationException("无法可靠解析格式片段：" + token);
            var definition = definitions[0];
            var value = values[i];
            if (definition.meSource != value.meSource || definition.mbAutogen != value.mbAutogen
                || !string.Equals(definition.msPropertyName, value.msPropertyName, StringComparison.Ordinal)
                || !string.Equals(definition.msClassOrCategory, value.msClassOrCategory, StringComparison.Ordinal))
                throw new InvalidOperationException("SDK 拆分字段顺序或类别不符：" + token);
            string oldValue = value.msValue ?? "";
            string property = (definition.msClassOrCategory ?? "").TrimStart('+', '?', '=')
                + "." + (definition.msPropertyName ?? "");
            if (definition.mbAutogen || definition.meSource == FormatSource.None)
            {
                result.Add(new(token, definition.meSource == FormatSource.None ? "" : property,
                    oldValue, oldValue, oldValue, definition.mbAutogen));
                continue;
            }

            // RetrieveProperty 只接收 Where.What；必须保留 PipeLines／PipeLineGroup 的 SDK 作用域。
            string propertyPath = token[2..^1].Split('^')[0].TrimStart('+', '?', '=');
            try
            {
                var current = Sdk(() =>
                {
                    try
                    {
                        if (definition.mbUseMate)
                        {
                            string mate = Evaluate(token)
                                ?? throw new InvalidOperationException("SDK 未返回配对字段值。");
                            return (Raw: mate, Expected: mate);
                        }
                        string raw = (objectId.IsNull ? tools.RetrieveProperty(propertyPath, context.RowId, context.Links)
                            : tools.RetrieveProperty(propertyPath, objectId)) ?? "";
                        // 此片段已确认非自动编号；只计算该属性的格式规则，不生成新编号。
                        string expected = Evaluate(token)
                            ?? throw new InvalidOperationException("SDK 未返回字段值。");
                        return (Raw: raw, Expected: expected);
                    }
                    catch (CadException ex) when (definition.mbOptional && ex.ErrorStatus == ErrorStatus.KeyNotFound)
                    { return (Raw: "", Expected: ""); }
                });
                result.Add(new(token, propertyPath, oldValue, current.Raw, current.Expected, false));
            }
            catch (OperationCanceledException) { throw; }
            catch (System.Exception ex)
            { throw new InvalidOperationException($"属性 {propertyPath}（{token}）：{ex.Message}", ex); }
        }
        return result;
    }

    /// <summary>复用一次数据行扫描，实际类别来自 PnPRow.ClassName。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static PlantTagScan Scan(Database database, Action checkBreak) =>
        Sdk(() => PlantTagCatalogAccess.Read(database, checkBreak));
}
