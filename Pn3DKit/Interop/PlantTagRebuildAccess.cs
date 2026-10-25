using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.ProcessPower.DataLinks;
using Autodesk.ProcessPower.PlantInstance;
using Pn3DKit.Commands;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadException = Autodesk.AutoCAD.Runtime.Exception;

namespace Pn3DKit.Interop;

/// <summary>在目标文档命令上下文实现元件、SLINE 与管线组的受控 Tag 写入。</summary>
internal sealed class PlantTagRebuildAccess : ITagRebuildHost
{
    private readonly Document _document;
    private readonly Database _database;
    private readonly ObjectId _identity;
    private readonly Func<bool> _isCurrent;
    private readonly Dictionary<string, PlantTagTarget> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _drawingTags = new(StringComparer.OrdinalIgnoreCase);
    private bool _undoOwned;
    private bool _fallbackComplete = true;

    /// <summary>固定原图纸和项目身份，不打开其他图纸。</summary>
    internal PlantTagRebuildAccess(Document document, Func<bool> isCurrent)
    {
        _document = document; _database = document.Database;
        _identity = _database.BlockTableId; _isCurrent = isCurrent;
    }

    /// <summary>检查图纸只读状态，通过当前项目部分的管理器核对读写登记。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void EnsureWritable(Document document)
    {
        var project = PlantApplication.CurrentProject
            ?? throw new InvalidOperationException("没有当前 Plant 项目，请打开项目后重试。");
        if (document.IsReadOnly) throw new InvalidOperationException("当前图纸为只读，不能重建位号。");
        // 本地 GetManager 只表示图纸关联；是否加入项目读写必须询问项目管理器。
        foreach (string name in new[] { "PnId", "Piping" })
        {
            Autodesk.ProcessPower.ProjectManager.Project? part;
            try { part = project.ProjectParts[name]; }
            catch (KeyNotFoundException) { continue; }
            if (part?.DataLinksManager?.IsDrawingEnlistedAsReadWrite(document.Database) == true) return;
        }
        throw new InvalidOperationException("当前图纸未以读写方式加入 Plant 项目，不能重建位号。");
    }

    /// <summary>核对原文档和项目；收尾不消费 Esc。</summary>
    internal void EnsureStable()
    {
        TagCheckCommands.EnsureDocument(_database, _identity);
        if (!_isCurrent()) throw new InvalidOperationException("当前 Plant 项目已变化，重建已停止。");
    }
    /// <inheritdoc />
    public void EnsureContext() { EnsureStable(); EnsureWritable(_document); TagCheckCommands.CheckBreak(); }

    /// <summary>缓存本次扫描的行目标，包括组；存在识别警告时禁用不完整的回退索引。</summary>
    internal void IndexTargets(PlantTagScan scan)
    {
        foreach (var target in scan.Targets) _targets[target.Key] = target;
        _fallbackComplete = scan.Warnings.Count == 0;
        RefreshDrawingIndex();
    }

    /// <summary>取得本次目标快照，仅对有确定 Tag 列和配置的目标产生写入候选。</summary>
    internal TagRebuildCandidate ReadCandidate(PlantTagTarget target, bool refreshFormat = false)
    {
        EnsureContext();
        var check = PlantTagCheckAccess.Check(target, refreshFormat);
        string? exclusion = target.LinkError;
        string registry = "", candidate = "";
        var context = target.Context;
        try
        {
            if (context.PartName != "PnId") exclusion = "管线号需按管线组处理；本版只写 PnId.Tag";
            else if (!context.Links.HasProperty(context.RowId, "Tag")) exclusion = "对象没有可写的 Tag 列";
            else if (!context.Links.IsDrawingEnlistedAsReadWrite(_database)) exclusion = "项目部分未以读写方式登记图纸";
            if (exclusion == null && check.Status == TagCheckStatus.Mismatch)
            {
                registry = context.Part.GetTagRegistryName(context.ClassName, "Tag") ?? "";
                if (registry.Length != 0 && !context.Links.TagRegistryNames.Contains(registry)) registry = "";
                if (registry.Length != 0)
                    candidate = context.Links.GetCandidateTag(registry, context.ClassName, "Tag", context.RowId, check.ExpectedTag);
                if (registry.Length != 0 && string.IsNullOrEmpty(candidate))
                    throw new InvalidOperationException("注册表未返回有效的候选位号键。");
            }
        }
        catch (CadException ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak)
        { throw new OperationCanceledException(Error(ex), ex); }
        catch (DLException ex) when (ex.ErrorCode == DLStatus.TagRegistryDoesNotExist)
        { registry = ""; candidate = ""; }
        catch (System.Exception ex) { exclusion = Error(ex); }
        if (registry.Length == 0 && !_fallbackComplete) exclusion ??= "占用索引存在关联读取错误，无法可靠排除冲突";
        return new(target.Key, check, registry, candidate, exclusion);
    }

    /// <summary>读取数据行当前值；不会把属性读取异常伪装为空值。</summary>
    internal static string ReadTag(PlantTagContext context) =>
        TagCheckPolicy.ReadExistingTag(context.PartName, name =>
        {
            if (!context.Links.HasProperty(context.RowId, name)) return "";
            var values = context.Links.GetProperties(context.RowId, new StringCollection { name }, true);
            return values.Count == 0 ? "" : values[0] ?? "";
        });

    /// <summary>刷新本图关联行的占用，不重复遍历模型空间；新关联行也纳入保守索引。</summary>
    internal void RefreshDrawingIndex()
    {
        EnsureContext();
        _drawingTags.Clear();
        foreach (var target in _targets.Values)
        {
            EnsureContext();
            UpdateIndex(target.Key, target.Context.PartName, ReadTag(target.Context));
        }
        foreach (var part in _targets.Values.Select(x => x.Context).DistinctBy(x => x.PartName))
        {
            var rowIds = part.Links.SelectAcPpRowIds(_database);
            foreach (int id in rowIds)
            {
                EnsureContext();
                string key = part.PartName + ":" + id;
                if (_targets.ContainsKey(key)) continue;
                var row = part.Links.GetPnPDatabase().GetRow(id);
                if (row == null) throw new InvalidOperationException("新增关联行无法读取：" + key);
                var context = part with { RowId = id, ClassName = row.ClassName };
                UpdateIndex(key, part.PartName, ReadTag(context));
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FindOwners(TagRebuildCandidate item)
    {
        EnsureContext();
        var context = Current(item).Context;
        try
        {
            if (item.Registry.Length != 0)
            {
                string registry = context.Part.GetTagRegistryName(context.ClassName, "Tag");
                if (registry != item.Registry) throw new InvalidOperationException("位号注册表已变化，请重新预检。");
                string candidate = context.Links.GetCandidateTag(registry, context.ClassName, "Tag", context.RowId, item.Check.ExpectedTag);
                if (candidate != item.CandidateTag) throw new InvalidOperationException("候选位号注册键已变化，请重新预检。");
                int row = context.Links.FindRowByTag(registry, candidate);
                return row >= 0 && row != context.RowId ? [context.PartName + ":" + row] : [];
            }
            if (!_fallbackComplete) throw new InvalidOperationException("占用索引不完整。");
            return _drawingTags.TryGetValue(context.PartName + "\n" + item.Check.ExpectedTag, out var owners)
                ? owners.Where(key => key != item.Key).ToArray() : [];
        }
        catch (CadException ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak)
        { throw new OperationCanceledException(Error(ex), ex); }
        catch (System.Exception ex) { throw new InvalidOperationException(Error(ex), ex); }
    }

    /// <summary>重新核对真实数据行、类别及图形；组还需保持原成员关系。</summary>
    private PlantTagTarget Current(TagRebuildCandidate item)
    {
        var target = _targets[item.Key];
        var context = target.Context;
        var row = context.Links.GetPnPDatabase().GetRow(context.RowId)
            ?? throw new InvalidOperationException("目标数据行已失效。");
        if (row.ClassName != context.ClassName) throw new InvalidOperationException("目标实际类别已变化。");
        if (target.Kind == TagTargetKind.LineGroup)
        {
            var members = context.Links.GetRelatedRowIds("PipeLineGroupRelationship", "PipeLineGroup", context.RowId, "PipeLine").Cast<int>();
            if (!members.ToHashSet().SetEquals(target.Members))
                throw new InvalidOperationException("管线组成员关系已变化，请重新预检。");
        }
        else
        {
            ObjectId id = target.ObjectIds.FirstOrDefault(x => x.IsValid && !x.IsErased);
            if (id.IsNull) throw new InvalidOperationException("目标图形已失效。");
            var current = PlantTagFormatAccess.Locate(id);
            if (current == null || current.PartName != context.PartName || current.RowId != context.RowId)
                throw new InvalidOperationException("目标数据关联已变化。");
        }
        return target;
    }

    /// <inheritdoc />
    public string? Revalidate(TagRebuildCandidate item)
    {
        EnsureContext();
        try
        {
            var fresh = ReadCandidate(Current(item), true);
            if (fresh.Exclusion != null) return "写入前不可重建：" + fresh.Exclusion;
            if (fresh.Registry != item.Registry || fresh.CandidateTag != item.CandidateTag)
                return "位号注册表或候选键已变化，请重新预检";
            if (fresh.Check.Status != TagCheckStatus.Mismatch
                || fresh.Check.Subject.ExistingTag != item.Check.Subject.ExistingTag
                || fresh.Check.ExpectedTag != item.Check.ExpectedTag
                || fresh.Check.Subject.Format != item.Check.Subject.Format)
                return "预检后 Tag、属性或格式已变化，请重新预检";
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Exception ex) { return "写入前核对失败：" + Error(ex); }
    }

    /// <inheritdoc />
    public string WriteAndRead(TagRebuildCandidate item)
    {
        var target = Current(item);
        var context = target.Context;
        try
        {
            using (var transaction = _database.TransactionManager.StartTransaction())
            {
                var names = new StringCollection { "Tag" };
                var values = new StringCollection { item.Check.ExpectedTag };
                if (target.Kind == TagTargetKind.LineGroup) context.Links.SetProperties(context.RowId, names, values);
                else context.Links.SetProperties(target.ObjectIds.First(x => x.IsValid && !x.IsErased), names, values);
                transaction.Commit();
            }
            string actual = ReadTag(context);
            UpdateIndex(item.Key, context.PartName, actual);
            return actual;
        }
        catch (System.Exception ex)
        {
            string actual;
            bool known = true;
            try { actual = ReadTag(context); UpdateIndex(item.Key, context.PartName, actual); }
            catch (System.Exception read) { known = false; actual = "无法回读：" + Error(read); }
            string message = Error(ex) + "；异常后实际 Tag：" + actual + "；不假定数据已回滚";
            if (known && ex is CadException cad && cad.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak)
                throw new OperationCanceledException(message, ex);
            throw new TagRebuildWriteException(message, known, ex);
        }
    }

    /// <summary>按已回读的实际值维护占用索引。</summary>
    private void UpdateIndex(string key, string part, string tag)
    {
        foreach (var owners in _drawingTags.Values) owners.Remove(key);
        if (tag.Length == 0) return;
        string index = part + "\n" + tag;
        if (!_drawingTags.TryGetValue(index, out var values)) _drawingTags[index] = values = [];
        values.Add(key);
    }

    /// <summary>保留 CAD 和 DataLinks 错误码，重复位号等错误不被吞掉。</summary>
    private static string Error(System.Exception ex) => ex switch
    {
        CadException cad => $"SDK ErrorStatus={cad.ErrorStatus}：{cad.Message}",
        DLException dl => $"SDK DataLinks ErrorCode={dl.ErrorCode}：{dl.Message}",
        _ => ex.Message
    };

    /// <inheritdoc />
    public void BeginUndo()
    {
        EnsureContext();
        int control = Convert.ToInt32(AcadApp.GetSystemVariable("UNDOCTL"));
        if ((control & 1) == 0 || (control & 2) != 0 || (control & 8) != 0)
            throw new InvalidOperationException("撤销已关闭、处于单步模式或已有撤销组，未执行重建。");
        try
        {
            _document.Editor.Command("_.UNDO", "_Begin");
            if ((Convert.ToInt32(AcadApp.GetSystemVariable("UNDOCTL")) & 8) == 0)
                throw new InvalidOperationException("无法确认 UNDO BEGIN，未执行重建。");
            _undoOwned = true;
        }
        catch
        {
            // 起始没有撤销组，BEGIN 后才出现的组由本次创建，失败时先尝试关闭。
            if ((Convert.ToInt32(AcadApp.GetSystemVariable("UNDOCTL")) & 8) != 0)
            {
                _undoOwned = true;
                EndUndo();
            }
            throw;
        }
    }

    /// <inheritdoc />
    public void EndUndo()
    {
        EnsureStable();
        if (!_undoOwned) return;
        if ((Convert.ToInt32(AcadApp.GetSystemVariable("UNDOCTL")) & 8) == 0)
            throw new InvalidOperationException("本次撤销组已意外结束，保留现场。");
        _document.Editor.Command("_.UNDO", "_End");
        if ((Convert.ToInt32(AcadApp.GetSystemVariable("UNDOCTL")) & 8) != 0)
            throw new InvalidOperationException("无法确认 UNDO END，保留现场。");
        _undoOwned = false;
    }

    /// <inheritdoc />
    public void Regen() { EnsureStable(); _document.Editor.Regen(); }
}
