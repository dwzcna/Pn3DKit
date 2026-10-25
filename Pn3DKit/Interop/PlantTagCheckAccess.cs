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

/// <summary>缓存同一项目部分和类别的模型空间对象，不持有打开的数据库对象。</summary>
internal sealed record PlantTagClassGroup(string PartName, string ClassName, List<ObjectId> ObjectIds,
    bool HasFormat, string? FormatError);

/// <summary>保存模型空间扫描结果及原项目身份验证回调。</summary>
internal sealed record PlantTagScan(IReadOnlyList<PlantTagClassGroup> Groups,
    IReadOnlyList<string> Warnings, Func<bool> IsCurrent);

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

    /// <summary>提供单个命令和批量命令共用的检查；不调用赋号、属性写回或保存接口。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static TagCheckResult Check(ObjectId objectId)
    {
        var subject = new TagCheckSubject("", "", "", "", "");
        try
        {
            if (objectId.IsNull || !objectId.IsValid || objectId.IsErased)
                throw new InvalidOperationException("目标对象已失效或被删除。");
            subject = subject with { Handle = objectId.Handle.ToString() };
            var target = Sdk(() => PlantTagFormatAccess.Locate(objectId));
            if (target == null)
                throw new InvalidOperationException("所选对象不是当前 Plant 项目中可识别的 Plant 3D 部件。");
            subject = subject with { ProjectPart = target.PartName, ClassName = target.ClassName };
            var format = Sdk(() => PlantTagFormatAccess.ReadFormat(target));
            subject = subject with { Format = format?.Expression ?? "" };
            if (format == null) return new(subject, "", TagCheckStatus.NoFormat, []);

            string ReadTag(string name)
            {
                if (!target.Links.HasProperty(target.RowId, name)) return "";
                var names = new StringCollection { name };
                var values = target.Links.GetProperties(objectId, names, true);
                return values.Count == 0 ? "" : values[0] ?? "";
            }
            string tag = Sdk(() => TagCheckPolicy.ReadExistingTag(target.PartName, ReadTag));
            subject = subject with { ExistingTag = tag };
            var tools = new FormatToolsUtil();
            return TagCheckPolicy.Check(subject,
                () => Sdk(() => tools.RebuildTagValue(subject.Format, tag, objectId)),
                () => Sdk(() => ReadFields(tools, objectId, subject.Format, tag)));
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Exception ex)
        { return new(subject, "", TagCheckStatus.Error, [], ex.Message); }
    }

    /// <summary>按 SDK 拆出的旧值读取当前属性；只对非自动编号属性片段求值以保留格式规则。</summary>
    /// <remarks>任何整式 Evaluate、自动编号及 AssignProperty 调用均不属于检查流程。</remarks>
    private static IReadOnlyList<TagCheckField> ReadFields(FormatToolsUtil tools, ObjectId objectId,
        string format, string tag)
    {
        if (!FormatStringUtils.IsValid(format))
            throw new InvalidOperationException("位号格式无效：" + format);
        var tokens = Regex.Matches(format, @"#\([^()]*\)");
        var values = tools.TokenizeValueAgainstFormatString(format, tag, objectId, true)
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
                            string mate = FormatStringUtils.Evaluate(token, objectId)
                                ?? throw new InvalidOperationException("SDK 未返回配对字段值。");
                            return (Raw: mate, Expected: mate);
                        }
                        string raw = tools.RetrieveProperty(propertyPath, objectId) ?? "";
                        // 此片段已确认非自动编号；只计算该属性的格式规则，不生成新编号。
                        string expected = FormatStringUtils.Evaluate(token, objectId)
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

    /// <summary>只扫描当前数据库模型空间直接对象，缓存类别格式，识别失败单独保留诊断。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static PlantTagScan Scan(Database database, Action checkBreak)
    {
        var project = PlantApplication.CurrentProject
            ?? throw new InvalidOperationException("没有当前 Plant 项目，请打开项目后重试。");
        var groups = new Dictionary<string, PlantTagClassGroup>(StringComparer.Ordinal);
        var warnings = new List<string>();
        ObjectId[] ids;
        using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
        {
            var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            ids = space.Cast<ObjectId>().ToArray();
        }
        foreach (ObjectId id in ids)
        {
            checkBreak();
            if (!ReferenceEquals(project, PlantApplication.CurrentProject))
                throw new InvalidOperationException("当前 Plant 项目已变化，扫描已停止。");
            if (!id.IsValid || id.IsErased) continue;
            try
            {
                var target = Sdk(() => PlantTagFormatAccess.Locate(id));
                if (target == null) continue;
                string key = target.PartName + "\n" + target.ClassName;
                if (!groups.TryGetValue(key, out var group))
                {
                    bool hasFormat = false;
                    string? error = null;
                    try { hasFormat = Sdk(() => PlantTagFormatAccess.ReadFormat(target)) != null; }
                    catch (OperationCanceledException) { throw; }
                    catch (System.Exception ex) { error = ex.Message; }
                    group = new(target.PartName, target.ClassName, [], hasFormat, error);
                    groups.Add(key, group);
                }
                group.ObjectIds.Add(id);
            }
            catch (OperationCanceledException) { throw; }
            catch (System.Exception ex)
            { warnings.Add($"句柄 {id.Handle}：识别失败：{ex.Message}"); }
        }
        return new(groups.Values.OrderBy(g => g.ClassName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.PartName, StringComparer.Ordinal).ToArray(), warnings,
            () => ReferenceEquals(project, PlantApplication.CurrentProject));
    }
}
