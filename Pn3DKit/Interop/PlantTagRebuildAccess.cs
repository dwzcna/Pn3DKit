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

/// <summary>在当前文档命令上下文实现 PnId.Tag 预检、项目冲突查询和受控写入。</summary>
internal sealed class PlantTagRebuildAccess : ITagRebuildHost
{
    private readonly Document _document;
    private readonly Database _database;
    private readonly ObjectId _identity;
    private readonly Func<bool> _isCurrent;
    private readonly Dictionary<string, ObjectId[]> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _drawingTags = new(StringComparer.OrdinalIgnoreCase);
    private bool _undoOwned;

    /// <summary>固定原图纸和项目身份，不打开其他图纸。</summary>
    internal PlantTagRebuildAccess(Document document, Func<bool> isCurrent)
    {
        _document = document;
        _database = document.Database;
        _identity = _database.BlockTableId;
        _isCurrent = isCurrent;
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

    /// <summary>检查原图纸和项目；撤销收尾不消费 Esc，避免中断导致撤销组悬挂。</summary>
    internal void EnsureStable()
    {
        TagCheckCommands.EnsureDocument(_database, _identity);
        if (!_isCurrent()) throw new InvalidOperationException("当前 Plant 项目已变化，重建已停止。");
    }

    /// <inheritdoc />
    public void EnsureContext()
    {
        EnsureStable();
        EnsureWritable(_document);
        TagCheckCommands.CheckBreak();
    }

    /// <summary>将全部扫描对象按真实数据行合并，包含没有位号格式的对象以检查占用。</summary>
    internal void IndexTargets(PlantTagScan scan)
    {
        var rows = new Dictionary<string, List<ObjectId>>(StringComparer.Ordinal);
        foreach (var id in scan.Groups.SelectMany(g => g.ObjectIds).Distinct())
        {
            EnsureContext();
            var target = PlantTagFormatAccess.Locate(id)
                ?? throw new InvalidOperationException("扫描对象已失去 Plant 数据关联，停止预检。");
            string key = Key(target);
            if (!rows.TryGetValue(key, out var ids)) rows[key] = ids = [];
            ids.Add(id);
        }
        foreach (var row in rows) _targets.Add(row.Key, row.Value.ToArray());
        RefreshDrawingIndex();
    }

    /// <summary>取得所选数据行的检查快照，保留同一行的全部图形句柄。</summary>
    internal TagRebuildCandidate ReadCandidate(ObjectId id)
    {
        EnsureContext();
        var context = PlantTagFormatAccess.Locate(id)
            ?? throw new InvalidOperationException("对象已失去 Plant 数据关联。");
        string key = Key(context);
        var check = PlantTagCheckAccess.Check(id);
        if (_targets.TryGetValue(key, out var aliases))
            check = check with { Subject = check.Subject with
            { Handle = string.Join("、", aliases.Select(x => x.Handle.ToString())) } };
        string? exclusion = null;
        string registry = "", candidate = "";
        try
        {
            if (context.PartName != "PnId") exclusion = "管线号需按管线组处理；本版只写 PnId.Tag";
            else
            {
                var db = context.Links.GetPnPDatabase();
                var table = db.Tables[context.ClassName]
                    ?? throw new InvalidOperationException("无法读取实际类别表。");
                if (table.IsKindOf("PipeLines")) exclusion = "PipeLines 及其子类别由管线组决定，第一版跳过";
                else if (!context.Links.HasProperty(context.RowId, "Tag")) exclusion = "对象没有可写的 Tag 列";
                else if (!context.Links.IsDrawingEnlistedAsReadWrite(_database)) exclusion = "项目部分未以读写方式登记图纸";
            }
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
        return new(key, check, registry, candidate, exclusion);
    }

    /// <summary>读 Tag 或 Piping 的管线号，仅供占用索引；属性错误不伪装为空值。</summary>
    private static string ReadTag(PlantTagContext context, ObjectId id) =>
        TagCheckPolicy.ReadExistingTag(context.PartName, name =>
        {
            if (!context.Links.HasProperty(context.RowId, name)) return "";
            var values = context.Links.GetProperties(id, new StringCollection { name }, true);
            return values.Count == 0 ? "" : values[0] ?? "";
        });

    /// <summary>固定数据行键，避免同一行的图形别名被误认为位号冲突。</summary>
    private static string Key(PlantTagContext context) => context.PartName + ":" + context.RowId;

    /// <summary>确认后重新扫描当前模型空间占用，覆盖未选类别、无格式对象和预览期间新增对象。</summary>
    internal void RefreshDrawingIndex()
    {
        EnsureContext();
        _drawingTags.Clear();
        ObjectId[] objects;
        using (var transaction = _database.TransactionManager.StartOpenCloseTransaction())
        {
            var table = (BlockTable)transaction.GetObject(_database.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            objects = model.Cast<ObjectId>().ToArray();
        }
        foreach (var id in objects)
        {
            EnsureContext();
            if (!id.IsValid || id.IsErased) continue;
            var context = PlantTagFormatAccess.Locate(id);
            if (context == null) continue;
            string tag = ReadTag(context, id);
            if (tag.Length == 0) continue;
            string index = context.PartName + "\n" + tag;
            if (!_drawingTags.TryGetValue(index, out var owners)) _drawingTags[index] = owners = [];
            if (!owners.Contains(Key(context))) owners.Add(Key(context));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FindOwners(TagRebuildCandidate item)
    {
        EnsureContext();
        var context = Current(item);
        if (item.Registry.Length != 0)
        {
            try
            {
                // 注册表只接受转换后的内部键，不能直接使用显示 Tag。
                string registry = context.Part.GetTagRegistryName(context.ClassName, "Tag");
                if (!string.Equals(registry, item.Registry, StringComparison.Ordinal))
                    throw new InvalidOperationException("位号注册表配置已变化，请重新预检。");
                string candidate = context.Links.GetCandidateTag(registry, context.ClassName, "Tag",
                    context.RowId, item.Check.ExpectedTag);
                if (!string.Equals(candidate, item.CandidateTag, StringComparison.Ordinal))
                    throw new InvalidOperationException("候选位号注册键已变化，请重新预检。");
                int row = context.Links.FindRowByTag(registry, candidate);
                return row >= 0 && row != context.RowId ? [context.PartName + ":" + row] : [];
            }
            catch (OperationCanceledException) { throw; }
            catch (CadException ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak)
            { throw new OperationCanceledException(Error(ex), ex); }
            catch (System.Exception ex) { throw new InvalidOperationException(Error(ex), ex); }
        }
        // 明确缺失注册表才回退；其他 SDK 异常交给预检记录为不可重建。
        string index = context.PartName + "\n" + item.Check.ExpectedTag;
        return _drawingTags.TryGetValue(index, out var owners)
            ? owners.Where(key => key != item.Key).ToArray() : [];
    }

    /// <summary>每次重新定位图形，确认数据行和项目部分仍与预检相同。</summary>
    private PlantTagContext Current(TagRebuildCandidate item)
    {
        var id = Id(item);
        if (!id.IsValid || id.IsErased) throw new InvalidOperationException("目标对象已失效或删除。");
        var current = PlantTagFormatAccess.Locate(id)
            ?? throw new InvalidOperationException("目标不再具有 Plant 数据关联。");
        if (Key(current) != item.Key) throw new InvalidOperationException("目标数据行已变化。");
        return current;
    }

    /// <summary>选取仍有效的同数据行图形，避免持有打开的数据库对象。</summary>
    private ObjectId Id(TagRebuildCandidate item) => _targets[item.Key].FirstOrDefault(id => id.IsValid && !id.IsErased);

    /// <inheritdoc />
    public string? Revalidate(TagRebuildCandidate item)
    {
        EnsureContext();
        try
        {
            _ = Current(item);
            var current = ReadCandidate(Id(item));
            if (current.Exclusion != null) return "写入前不可重建：" + current.Exclusion;
            if (!string.Equals(current.Registry, item.Registry, StringComparison.Ordinal)
                || !string.Equals(current.CandidateTag, item.CandidateTag, StringComparison.Ordinal))
                return "位号注册表或候选键已变化，请重新预检";
            if (current.Check.Status != TagCheckStatus.Mismatch
                || !string.Equals(current.Check.Subject.ExistingTag, item.Check.Subject.ExistingTag, StringComparison.Ordinal)
                || !string.Equals(current.Check.ExpectedTag, item.Check.ExpectedTag, StringComparison.Ordinal)
                || !string.Equals(current.Check.Subject.Format, item.Check.Subject.Format, StringComparison.Ordinal))
                return "预检后 Tag、属性或格式已变化，请重新预检";
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Exception ex) { return "写入前核对失败：" + Error(ex); }
    }

    /// <inheritdoc />
    public string WriteAndRead(TagRebuildCandidate item)
    {
        var context = Current(item);
        var id = Id(item);
        try
        {
            using (var transaction = _database.TransactionManager.StartTransaction())
            {
                context.Links.SetProperties(id, new StringCollection { "Tag" },
                    new StringCollection { item.Check.ExpectedTag });
                transaction.Commit();
            }
            string actual = ReadTag(context, id);
            // 更新本图退回索引；包括回读不符的真实值，不假定事务能回滚 Plant 缓存。
            UpdateIndex(item.Key, context.PartName, actual);
            return actual;
        }
        catch (System.Exception ex)
        {
            string actual;
            bool stateKnown = true;
            try { actual = ReadTag(context, id); UpdateIndex(item.Key, context.PartName, actual); }
            catch (System.Exception read) { stateKnown = false; actual = "无法回读：" + Error(read); }
            string message = Error(ex) + "；异常后实际 Tag：" + actual + "；不假定数据已回滚";
            if (stateKnown && ex is CadException cad && cad.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak)
                throw new OperationCanceledException(message, ex);
            throw new TagRebuildWriteException(message, stateKnown, ex);
        }
    }

    /// <summary>按已回读的实际值更新回退索引，包括写入异常后的真实状态。</summary>
    private void UpdateIndex(string key, string part, string actual)
    {
        foreach (var owners in _drawingTags.Values) owners.Remove(key);
        string index = part + "\n" + actual;
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
