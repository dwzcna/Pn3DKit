using System.Collections.Specialized;
using Autodesk.ProcessPower.DataLinks;
using Autodesk.ProcessPower.DataObjects;
using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Interop;

/// <summary>主线程上的全项目数据行检查、注册键冲突索引与受控提交，不直接访问项目文件数据库。</summary>
internal sealed class PlantProjectTagAccess : IProjectTagDataHost
{
    private readonly PlantTagScan _scan;
    private readonly Dictionary<string, PlantTagTarget> _targets;
    private readonly Dictionary<string, string[]> _associations;
    private readonly Dictionary<string, List<string>> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _complete;
    private readonly List<string> _indexErrors = [];
    private readonly Action<string>? _reportIndexIssue;
    private readonly ScopedContextGuard _guard;
    internal IEnumerable<PlantTagTarget> Targets => _targets.Values;

    /// <summary>固定预检行与全部图纸关联；缓存完整项目占用，包括无格式及未选择对象。</summary>
    internal PlantProjectTagAccess(PlantTagScan scan, Action guard, Action<string>? reportIndexIssue = null)
    {
        _scan = scan; _guard = new ScopedContextGuard(guard); _reportIndexIssue = reportIndexIssue;
        _targets = scan.Targets.ToDictionary(x => x.Key);
        _associations = _targets.ToDictionary(x => x.Key, x => Associations(x.Value));
        RefreshIndex();
    }

    /// <inheritdoc />
    public void EnsureContext()
    {
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("项目调用离开 CAD 主线程。");
        if (!_scan.IsCurrent()) throw new InvalidOperationException("当前 Plant 项目已变化，停止批次。");
        _guard.Ensure();
    }

    /// <summary>为同步图纸回调临时绑定完整数据库身份；结束后恢复命令原检查。</summary>
    /// <param name="guard">当前图纸身份及取消检查，不允许跨异步等待使用。</param>
    /// <returns>必须在回调返回前释放的上下文作用域。</returns>
    internal IDisposable UseGuard(Action guard) => _guard.Use(guard);

    /// <summary>只有相关 PnId 库参与写入，不代为提交其他项目部分。</summary>
    public bool HasPendingChanges()
    {
        EnsureContext();
        return _targets.Values.Where(x => x.Context.PartName == "PnId").DistinctBy(x => x.Context.PartName)
            .Any(x => x.Context.Links.GetPnPDatabase().HasChanges());
    }

    /// <summary>全项目表索引不依赖所选类别、格式或图形是否已打开；读不全时禁止回退写入。</summary>
    internal void RefreshIndex()
    {
        EnsureContext(); _tags.Clear(); _indexErrors.Clear(); _complete = _scan.Warnings.Count == 0;
        foreach (var part in _targets.Values.Select(x => x.Context).DistinctBy(x => x.PartName))
        {
            string tableName = "(数据库)", rowId = "(未定位)";
            try
            {
                var db = part.Links.GetPnPDatabase();
                var seen = new HashSet<int>();
                foreach (PnPTable table in db.Tables)
                {
                    tableName = table.Name; rowId = "(枚举行)";
                    if (!table.Columns.Contains("Tag")) continue;
                    foreach (PnPRow row in table.Select())
                    {
                        EnsureContext();
                        rowId = Convert.ToString(row["PnPID"]) ?? "(null)";
                        int id = Convert.ToInt32(row["PnPID"]);
                        if (!seen.Add(id)) continue;
                        var context = part with { RowId = id, ClassName = row.ClassName };
                        string tag = PlantTagRebuildAccess.ReadTag(context);
                        if (tag.Length == 0) continue;
                        string index = part.PartName + "\n" + tag;
                        if (!_tags.TryGetValue(index, out var owners)) _tags[index] = owners = [];
                        owners.Add(part.PartName + ":" + id);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (System.Exception ex)
            {
                _complete = false;
                string detail = $"项目索引读取失败：部分 {part.PartName}；表 {tableName}；行 {rowId}；{Error(ex)}";
                _indexErrors.Add(detail);
                _reportIndexIssue?.Invoke(detail);
            }
        }
    }

    /// <summary>每个关联包含图纸数据行ID及完整十六进制句柄；组记录全部成员关联。</summary>
    private static string[] Associations(PlantTagTarget target)
    {
        var ids = target.Kind == TagTargetKind.LineGroup ? target.Members : [target.Context.RowId];
        return ids.SelectMany(row => target.Context.Links.FindAcPpObjectIds(row, false).Cast<PpObjectId>()
            .Select(pp => row + ":" + pp.DwgId + ":" + pp.dbHandle.ToString("X")))
            .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    /// <summary>重新核对实际类别、成员和所有关联，不接受同文件名的新目标。</summary>
    internal PlantTagTarget Current(string key)
    {
        EnsureContext();
        var target = _targets[key];
        var c = target.Context;
        var row = c.Links.GetPnPDatabase().GetRow(c.RowId);
        if (row == null || row.ClassName != c.ClassName) throw new InvalidOperationException("数据行或实际类别已变化。");
        if (target.Kind == TagTargetKind.LineGroup
            && !c.Links.GetRelatedRowIds("PipeLineGroupRelationship", "PipeLineGroup", c.RowId, "PipeLine")
                .Cast<int>().ToHashSet().SetEquals(target.Members))
            throw new InvalidOperationException("管线组成员已变化，请重新预检。");
        if (!Associations(target).SequenceEqual(_associations[key]))
            throw new InvalidOperationException("目标图纸或图形关联已变化，请重新预检。");
        return target;
    }

    /// <summary>项目预检采用数据行重载；只形成 PnId.Tag 的写入候选。</summary>
    internal TagRebuildCandidate Candidate(PlantTagTarget target)
    {
        EnsureContext();
        var check = PlantTagCheckAccess.Check(target, true);
        var c = target.Context;
        string registry = "", candidate = "";
        string? exclusion = target.LinkError;
        try
        {
            if (c.PartName != "PnId") exclusion = "管线号需按管线组处理；本版只写 PnId.Tag";
            else if (!c.Links.HasProperty(c.RowId, "Tag")) exclusion = "对象没有可写的 Tag 列";
            if (exclusion == null && check.Status == TagCheckStatus.Mismatch)
            {
                registry = c.Part.GetTagRegistryName(c.ClassName, "Tag") ?? "";
                if (registry.Length != 0 && !c.Links.TagRegistryNames.Contains(registry)) registry = "";
                if (registry.Length != 0)
                {
                    candidate = c.Links.GetCandidateTag(registry, c.ClassName, "Tag", c.RowId, check.ExpectedTag);
                    if (string.IsNullOrEmpty(candidate)) throw new InvalidOperationException("SDK 未返回候选注册键。");
                }
            }
        }
        catch (DLException ex) when (ex.ErrorCode == DLStatus.TagRegistryDoesNotExist) { registry = ""; candidate = ""; }
        catch (System.Exception ex) { exclusion = Error(ex); }
        if (registry.Length == 0 && !_complete) exclusion ??= "全项目占用索引不完整，禁止无注册表的相关写入";
        return new(target.Key, check, registry, candidate, exclusion);
    }

    /// <inheritdoc />
    public string? Revalidate(TagRebuildCandidate item)
    {
        try
        {
            var fresh = Candidate(Current(item.Key));
            if (fresh.Exclusion != null) return fresh.Exclusion;
            if (fresh.Check.Status != TagCheckStatus.Mismatch || fresh.Check.ExpectedTag != item.Check.ExpectedTag
                || fresh.Check.Subject.ExistingTag != item.Check.Subject.ExistingTag
                || fresh.Check.Subject.Format != item.Check.Subject.Format
                || fresh.Registry != item.Registry || fresh.CandidateTag != item.CandidateTag)
                return "预检后 Tag、属性、格式或注册键已变化；留待重新预检";
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Exception ex) { return Error(ex); }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FindOwners(TagRebuildCandidate item)
    {
        var c = Current(item.Key).Context;
        try
        {
            if (item.Registry.Length != 0)
            {
                if (c.Part.GetTagRegistryName(c.ClassName, "Tag") != item.Registry
                    || c.Links.GetCandidateTag(item.Registry, c.ClassName, "Tag", c.RowId, item.Check.ExpectedTag) != item.CandidateTag)
                    throw new InvalidOperationException("注册键已变化，请重新预检。");
                int row = c.Links.FindRowByTag(item.Registry, item.CandidateTag);
                return row >= 0 && row != c.RowId ? [c.PartName + ":" + row] : [];
            }
            if (!_complete) throw new InvalidOperationException("完整项目索引不可用。" + string.Join("；", _indexErrors));
            var ids = ProjectTagLookup.Owners(item.Check.ExpectedTag, c.RowId, filter =>
            {
                var found = new List<(int Id, string Tag)>();
                foreach (PnPTable table in c.Links.GetPnPDatabase().Tables)
                {
                    EnsureContext();
                    if (!table.Columns.Contains("Tag")) continue;
                    foreach (PnPRow row in table.Select(filter))
                    {
                        EnsureContext();
                        int id = Convert.ToInt32(row["PnPID"]);
                        var context = c with { RowId = id, ClassName = row.ClassName };
                        found.Add((id, PlantTagRebuildAccess.ReadTag(context)));
                    }
                }
                return found;
            });
            return ids.Select(id => c.PartName + ":" + id).ToArray();
        }
        catch (System.Exception ex) { throw new InvalidOperationException(Error(ex), ex); }
    }

    /// <inheritdoc />
    public string WriteAndCommit(TagRebuildCandidate item)
    {
        EnsureContext();
        var target = Current(item.Key);
        if (target.Context.PartName != "PnId") throw new InvalidOperationException("仅允许写 PnId.Tag。");
        if (HasPendingChanges()) throw new InvalidOperationException("项目已有待提交修改，未写入。");
        try
        {
            var c = target.Context;
            c.Links.SetProperties(c.RowId, new StringCollection { "Tag" }, new StringCollection { item.Check.ExpectedTag });
            // 主线程同步提交本次操作及 SDK 维护的注册信息；不拒绝其他修改。
            c.Links.AcceptChanges();
            string actual = PlantTagRebuildAccess.ReadTag(c);
            if (HasPendingChanges()) throw new InvalidOperationException("提交后仍有未知待提交修改，停止并保留现场。");
            RecordWrite(item, actual);
            return actual;
        }
        catch (System.Exception ex)
        {
            string actual;
            try { actual = PlantTagRebuildAccess.ReadTag(target.Context); }
            catch (System.Exception read) { actual = "无法回读：" + Error(read); }
            throw new InvalidOperationException(Error(ex) + "；实际 Tag：" + actual + "；不自动 Reject 或回滚", ex);
        }
    }

    /// <summary>成功回读后只更新本行缓存；写前占用仍以 SDK 当前查询为准。</summary>
    internal void RecordWrite(TagRebuildCandidate item, string actual)
    {
        EnsureContext();
        var context = Current(item.Key).Context;
        string oldKey = context.PartName + "\n" + item.Check.Subject.ExistingTag;
        if (_tags.TryGetValue(oldKey, out var previous))
        {
            previous.RemoveAll(owner => owner == item.Key);
            if (previous.Count == 0) _tags.Remove(oldKey);
        }
        if (actual.Length == 0) return;
        string newKey = context.PartName + "\n" + actual;
        if (!_tags.TryGetValue(newKey, out var owners)) _tags[newKey] = owners = [];
        if (!owners.Contains(item.Key)) owners.Add(item.Key);
    }

    /// <summary>保留 CAD 与 DataLinks 状态码。</summary>
    internal static string Error(System.Exception ex) => ex switch
    {
        Autodesk.AutoCAD.Runtime.Exception cad => $"SDK ErrorStatus={cad.ErrorStatus}：{cad.Message}",
        DLException dl => $"SDK DataLinks ErrorCode={dl.ErrorCode}：{dl.Message}",
        _ => ex.Message
    };
}
