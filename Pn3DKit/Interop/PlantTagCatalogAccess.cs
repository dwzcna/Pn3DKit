using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.ProcessPower.DataLinks;
using Autodesk.ProcessPower.DataObjects;
using Autodesk.ProcessPower.PlantInstance;
using Autodesk.ProcessPower.ProjectManager;
using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Interop;

/// <summary>保存真实数据行及可选图形；管线组不借用成员 ObjectId 充当自身身份。</summary>
internal sealed record PlantTagTarget(PlantTagContext Context, TagTargetKind Kind, ObjectId[] ObjectIds,
    string[] Drawings, string RelatedHandles, int[] Members, string? LinkError = null)
{
    internal PlantTagFormat? CachedFormat { get; init; }
    internal bool FormatKnown { get; init; }
    internal string? FormatError { get; init; }
    internal string Key => Context.PartName + ":" + Context.RowId;
    internal string RemoteHandles { get; init; } = "";
    internal string Handle => Kind == TagTargetKind.LineGroup ? "" : ObjectIds.Length == 0
        ? RemoteHandles : string.Join("、", ObjectIds.Select(id => id.Handle.ToString()));
    internal TagCheckSubject Subject => new(Context.PartName, Context.ClassName, Handle, "", "")
    { RowId = Context.RowId.ToString(), Kind = Kind, Drawings = Drawings, RelatedHandles = RelatedHandles };
}

/// <summary>类别清单按真实数据行计数，缓存继承后的格式和关联诊断。</summary>
internal sealed record PlantTagClassGroup(string PartName, string ClassName, List<PlantTagTarget> Targets,
    bool HasFormat, string? FormatError)
{
    /// <summary>本次扫描缓存的项目类别显示名称；缺少名称时使用实际类名。</summary>
    internal string DisplayName { get; init; } = ClassName;
    internal string Unit => Targets.All(x => x.Kind == TagTargetKind.LineGroup) ? "组" : "个";
}

/// <summary>保存批量扫描结果及原项目身份核对，不持有打开的数据库对象。</summary>
internal sealed record PlantTagScan(IReadOnlyList<PlantTagClassGroup> Groups,
    IReadOnlyList<string> Warnings, Func<bool> IsCurrent)
{
    internal IReadOnlyList<string> Diagnostics { get; init; } = [];
    internal IEnumerable<PlantTagTarget> Targets => Groups.SelectMany(x => x.Targets);
}

/// <summary>从批量数据行取得实际类别，模型空间只用于限定直接图形范围。</summary>
internal static partial class PlantTagCatalogAccess
{
    /// <summary>图纸范围或整个项目范围的统一扫描；类别发现不调用 GetObjectClassname。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static PlantTagScan Read(Database? drawing, Action ensure)
    {
        var project = PlantApplication.CurrentProject
            ?? throw new InvalidOperationException("没有当前 Plant 项目，请打开项目后重试。");
        var groups = new List<PlantTagClassGroup>();
        var warnings = new List<string>();
        var diagnostics = new List<string> { "Pn3DKit " + typeof(PlantTagCatalogAccess).Assembly.GetName().Version };
        var model = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        if (drawing != null)
        {
            using var tr = drawing.TransactionManager.StartOpenCloseTransaction();
            var blocks = (BlockTable)tr.GetObject(drawing.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in space)
                if (id.IsValid && !id.IsErased) model[id.Handle.ToString()] = id;
        }
        diagnostics.Add("当前图纸：" + (drawing?.Filename ?? "项目范围") + "；模型空间直接图形数：" + model.Count);
        void Check()
        {
            ensure();
            if (!ReferenceEquals(project, PlantApplication.CurrentProject))
                throw new InvalidOperationException("当前 Plant 项目已变化，扫描已停止。");
        }
        foreach (string partName in new[] { "PnId", "Piping" })
        {
            Check();
            Project? part;
            try { part = project.ProjectParts[partName]; }
            catch (KeyNotFoundException) { continue; }
            if (part == null) continue;
            var links = part.DataLinksManager;
            var db = links.GetPnPDatabase();
            var paths = new Dictionary<int, string>();
            foreach (var file in part.GetPnPDrawingFiles())
            {
                Check();
                // 使用管理器解析后的图纸 ID，避免混用项目项行 ID 与数据关联图纸 ID。
                int fileId = links.GetDrawingId(file.Guid);
                if (fileId < 0) fileId = links.GetDrawingId(file.RowGuid.ToString());
                if (fileId >= 0) paths[fileId] = file.ResolvedFilePath;
                else warnings.Add($"[{partName}] 无法解析项目图纸的数据关联 ID：{file.ResolvedFilePath}");
            }
            int drawingId = drawing == null ? -1 : links.GetDrawingId(drawing);
            if (drawing != null)
            {
                int registered = paths.Where(x => string.Equals(x.Value, drawing.Filename, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Key).DefaultIfEmpty(-1).First();
                if (drawingId < 0) drawingId = registered;
                else if (registered >= 0 && registered != drawingId)
                    throw new InvalidOperationException($"[{partName}] 活动图纸 ID {drawingId} 与项目登记 ID {registered} 不一致，停止扫描。");
            }
            diagnostics.Add($"[{partName}] 项目图纸数 {paths.Count}；当前图纸行ID {drawingId}");
            if (drawing != null && !paths.ContainsKey(drawingId)) continue;
            var scope = new HashSet<int>();
            if (drawing == null)
            {
                foreach (int id in paths.Keys)
                    foreach (int row in links.SelectAcPpRowIds(id)) scope.Add(row);
            }
            else foreach (int row in links.SelectAcPpRowIds(drawingId)) scope.Add(row);
            diagnostics.Add($"[{partName}] 图纸关联数据行数 {scope.Count}");
            if (scope.Count == 0) continue;
            var rowIds = new PnPRowIdArray(scope.ToList());
            var drawingIds = new PnPRowIdArray(drawing == null ? paths.Keys.ToList() : new List<int> { drawingId });
            var classNames = links.GetClassesWithContent(drawingIds, true).Keys;
            var rows = classNames.Where(name => db.Tables.Contains(name))
                .SelectMany(name => db.Tables[name].Select(null, rowIds, true, false, false))
                .GroupBy(row => Convert.ToInt32(row["PnPID"])).Select(g => g.First()).ToArray();
            diagnostics.Add($"[{partName}] SDK 有内容类别数 {classNames.Count}；批量读到数据行 {rows.Length}");
            if (rows.Length == 0)
                throw new InvalidOperationException($"[{partName}] 已有 {scope.Count} 个关联行，但类别批量查询没有返回数据，请核对项目数据关联。");
            var rowMap = new Dictionary<int, TagCatalogRow>();
            var targets = new Dictionary<int, PlantTagTarget>();
            var directScope = new HashSet<int>();
            var kinds = new Dictionary<string, TagTargetKind>(StringComparer.Ordinal);
            var associationsCache = new Dictionary<int, (ObjectId[] Ids, string[] Paths, string Handles)>();

            (ObjectId[] Ids, string[] Paths, string Handles) Associations(int rowId)
            {
                if (associationsCache.TryGetValue(rowId, out var cached)) return cached;
                var ids = new List<ObjectId>();
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var handles = new List<string>();
                foreach (PpObjectId pp in links.FindAcPpObjectIds(rowId, false))
                {
                    if (!paths.TryGetValue(pp.DwgId, out string? path))
                        throw new InvalidOperationException("数据行关联未登记的图纸 ID：" + pp.DwgId);
                    files.Add(path);
                    string handle = TagCatalogPolicy.FormatHandle(pp.dbHandle);
                    handles.Add(path + ":" + handle);
                    if (drawing != null && pp.DwgId == drawingId
                        && model.TryGetValue(handle, out var id)) ids.Add(id);
                }
                var result = (ids.Distinct().ToArray(), files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    string.Join("、", handles.Distinct()));
                associationsCache[rowId] = result;
                return result;
            }

            foreach (var row in rows)
            {
                Check();
                int id = Convert.ToInt32(row["PnPID"]);
                string name = row.ClassName;
                if (!kinds.TryGetValue(name, out var kind))
                {
                    var table = db.Tables[name];
                    kind = table.IsKindOf("PipeLineGroup") ? TagTargetKind.LineGroup
                        : table.IsKindOf("PipeLines") ? TagTargetKind.LineSegment : TagTargetKind.Component;
                    kinds[name] = kind;
                }
                if (kind == TagTargetKind.LineGroup) continue;
                try
                {
                    var association = Associations(id);
                    if (drawing != null && association.Ids.Length == 0) continue;
                    if (association.Paths.Length == 0)
                    { warnings.Add($"[{partName}] 数据行 {id} 没有有效图纸关联。"); continue; }
                    directScope.Add(id);
                    rowMap[id] = new(id, name, kind, []);
                    targets[id] = new(new(partName, part, links, id, name), kind, association.Ids,
                        association.Paths, drawing == null ? association.Handles : "", [])
                    {
                        RemoteHandles = string.Join("、", association.Handles.Split('、', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x[(x.LastIndexOf(':') + 1)..]).Distinct())
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (System.Exception ex) { warnings.Add($"[{partName}] 数据行 {id} 关联读取失败：{ex.Message}"); }
            }
            if (partName == "PnId" && db.Tables.Contains("PipeLineGroup"))
            {
                foreach (var row in db.Tables["PipeLineGroup"].Select())
                {
                    Check();
                    int id = Convert.ToInt32(row["PnPID"]);
                    try
                    {
                        int[] members = links.GetRelatedRowIds("PipeLineGroupRelationship", "PipeLineGroup", id, "PipeLine")
                            .Cast<int>().Distinct().ToArray();
                        rowMap[id] = new(id, row.ClassName, TagTargetKind.LineGroup, members);
                        if (!members.Any(directScope.Contains)) continue;
                        var associations = members.Select(Associations).ToArray();
                        targets[id] = new(new(partName, part, links, id, row.ClassName), TagTargetKind.LineGroup, [],
                            associations.SelectMany(x => x.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            string.Join("、", associations.Select(x => x.Handles).Where(x => x.Length != 0)), members);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (System.Exception ex) { warnings.Add($"管线组数据行 {id} 关联读取失败：{ex.Message}"); }
                }
            }
            var selection = TagCatalogPolicy.Select(rowMap.Values, directScope);
            diagnostics.Add($"[{partName}] 范围内图形行 {directScope.Count}；管线组 {selection.Rows.Count(x => x.Kind == TagTargetKind.LineGroup)}；全部目标 {selection.Rows.Count}");
            warnings.AddRange(selection.Warnings.Select(x => "[" + partName + "] " + x));
            foreach (var category in selection.Rows.Where(x => targets.ContainsKey(x.RowId))
                .GroupBy(x => x.ClassName).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                Check();
                var values = category.Select(x => targets[x.RowId]).ToList();
                PlantTagFormat? format = null;
                string? error = null;
                try { format = PlantTagFormatAccess.ReadFormat(values[0].Context); }
                catch (OperationCanceledException) { throw; }
                catch (System.Exception ex) { error = ex.Message; }
                values = values.Select(x => x with { CachedFormat = format, FormatKnown = true, FormatError = error }).ToList();
                // 每个项目部分的实际类别只读取一次；后续列表及跳过提示使用缓存。
                groups.Add(new(partName, category.Key, values, format != null, error)
                { DisplayName = ReadDisplayName(part, db, category.Key) });
            }
        }
        // 显示顺序遵循当前区域规则；同名类别再按内部身份稳定排序，不合并目标。
        return new(groups.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.ClassName, StringComparer.Ordinal)
            .ThenBy(x => x.PartName, StringComparer.Ordinal).ToArray(), warnings,
            () => ReferenceEquals(project, PlantApplication.CurrentProject))
        { Diagnostics = diagnostics };
    }
}
