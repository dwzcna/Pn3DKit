using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using CadException = Autodesk.AutoCAD.Runtime.Exception;

namespace Pn3DKit.Interop;

/// <summary>只读提取块和关联资源的内容图；未知原生数据明确保留不完整原因。</summary>
internal sealed class XrefBlockSnapshotReader(Database database, Action ensure, IReadOnlyList<string>? sourceNamespaces = null) : IDisposable
{
    /// <summary>读取期间的图节点，冻结后交给不依赖 SDK 的比较层。</summary>
    internal sealed class Node(string id, string kind)
    {
        internal string Id { get; } = id;
        internal string Kind { get; } = kind;
        internal Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
        internal List<BlockContentEdge> Edges { get; } = [];
        internal List<string> Issues { get; } = [];
        internal Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<ObjectId, Node> _objects = [];
    private readonly Dictionary<string, Node> _all = new(StringComparer.Ordinal);
    private readonly Transaction _transaction = database.TransactionManager.StartOpenCloseTransaction();
    private static readonly HashSet<string> KnownEntities = new(StringComparer.Ordinal)
    {
        "AcDbLine", "AcDbRay", "AcDbXline", "AcDbArc", "AcDbCircle", "AcDbEllipse", "AcDbPolyline",
        "AcDb2dPolyline", "AcDb3dPolyline", "AcDb2dVertex", "AcDb3dPolylineVertex", "AcDbSpline",
        "AcDbText", "AcDbMText", "AcDbAttributeDefinition", "AcDbAttribute", "AcDbBlockReference",
        "AcDbMInsertBlock", "AcDbHatch", "AcDbSolid", "AcDbTrace", "AcDbFace", "AcDbPoint",
        "AcDbLeader", "AcDbMLeader", "AcDbAlignedDimension", "AcDbRotatedDimension",
        "AcDbRadialDimension", "AcDbDiametricDimension", "AcDb2LineAngularDimension",
        "AcDb3PointAngularDimension", "AcDbOrdinateDimension", "AcDbRadialDimensionLarge",
        "AcDbArcDimension", "AcDbPolyFaceMesh", "AcDbPolyFaceMeshVertex", "AcDbFaceRecord",
        "AcDbPolygonMesh", "AcDbPolygonMeshVertex", "AcDbSubDMesh", "AcDbWipeout"
    };
    private static readonly HashSet<string> KnownResources = new(StringComparer.Ordinal)
    {
        "AcDbLayerTableRecord", "AcDbLinetypeTableRecord", "AcDbTextStyleTableRecord",
        "AcDbDimStyleTableRecord", "AcDbRegAppTableRecord", "AcDbMLeaderStyle", "AcDbVisualStyle",
        "AcDbMaterial", "AcDbPlaceHolder"
    };

    /// <summary>冻结根可达内容；未知子对象的原因向定义传播，不把部分图判定为完整。</summary>
    internal BlockContentSnapshot Read(ObjectId id)
    {
        ensure();
        Node root = Visit(id);
        var pending = new Stack<string>(); pending.Push(root.Id);
        var nodes = new Dictionary<string, BlockContentNode>(StringComparer.Ordinal);
        var issues = new List<string>();
        while (pending.Count != 0)
        {
            ensure();
            string key = pending.Pop();
            if (nodes.ContainsKey(key)) continue;
            Node node = _all[key];
            nodes.Add(key, new(key, node.Kind, new Dictionary<string, string>(node.Fields),
                node.Edges.ToArray(), new Dictionary<string, byte[]>(node.Payloads)));
            issues.AddRange(node.Issues.Select(s => node.Kind + "：" + s));
            foreach (var edge in node.Edges) pending.Push(edge.Target);
        }
        return new(root.Id, nodes, issues.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>只在 ForRead 事务中定位，先登记再递归以保留环和共享资源。</summary>
    private Node Visit(ObjectId id)
    {
        ensure();
        if (id.IsNull || !id.IsValid || id.IsErased) throw new InvalidOperationException("内容关联已失效或删除。");
        if (_objects.TryGetValue(id, out var found)) return found;
        var item = _transaction.GetObject(id, OpenMode.ForRead);
        string type = item.GetRXClass().Name;
        var node = New(type); _objects.Add(id, node);
        try
        {
            if (item is ProxyEntity or ProxyObject)
            {
                string original = item is ProxyEntity proxyEntity ? proxyEntity.OriginalClassName : ((ProxyObject)item).OriginalClassName;
                node.Issues.Add("代理对象无法完整分析：" + type + "；原类：" + original);
                return node;
            }
            if (item is BlockTableRecord block) CaptureBlock(node, block);
            else if (item is DBDictionary dictionary)
            {
                foreach (DBDictionaryEntry entry in dictionary)
                { ensure(); Link(node, "字典/" + entry.Key, entry.Value); }
            }
            else if (item is Xrecord record) CaptureXrecord(node, record);
            else
            {
                if (item is Entity entity) CaptureDisplay(node, entity);
                if (item is SymbolTableRecord symbolContent) CaptureSymbol(node, symbolContent);
                else if (item is Solid3d or Autodesk.AutoCAD.DatabaseServices.Region or Autodesk.AutoCAD.DatabaseServices.Surface)
                    XrefBrepReader.Capture((Entity)item, node, New, Link, ensure);
                else if (item is Assoc2dConstraintGroup constraint) CaptureConstraints(node, constraint);
                else if (item is SubDMesh subDMesh) CaptureMesh(node, subDMesh);
                else
                {
                    if (!KnownEntities.Contains(type) && !KnownResources.Contains(type))
                        node.Issues.Add("尚未确认此原生类型的全部字段语义：" + type
                            + (type.Contains("Block", StringComparison.Ordinal) || type.Contains("Eval", StringComparison.Ordinal)
                                ? "；动态节点内部编号未冒充内容，需 SDK 语义支持。" : ""));
                    CaptureFiler(node, item);
                }
                if (item is SymbolTableRecord symbol)
                {
                    node.Fields["资源名"] = SymbolName(symbol, node);
                    if (symbol is TextStyleTableRecord text) CaptureFont(node, text);
                }
                if (item is BlockReference reference) CaptureReference(node, reference);
                if (item is Polyline2d polyline)
                    foreach (ObjectId vertex in polyline) Link(node, "顶点/" + node.Edges.Count.ToString("D8", CultureInfo.InvariantCulture), vertex);
                if (item is Polyline3d spatial)
                    foreach (ObjectId vertex in spatial) Link(node, "顶点/" + node.Edges.Count.ToString("D8", CultureInfo.InvariantCulture), vertex);
                if (item is PolyFaceMesh or PolygonMesh)
                    node.Issues.Add("旧式网格的有序记录已读取，内部索引和子实体外观尚未完整规范化。");
                if (item is PolyFaceMesh mesh)
                    foreach (ObjectId vertex in mesh) Link(node, "网格记录/" + node.Edges.Count.ToString("D8", CultureInfo.InvariantCulture), vertex);
                if (item is PolygonMesh polygon)
                    foreach (ObjectId vertex in polygon) Link(node, "网格记录/" + node.Edges.Count.ToString("D8", CultureInfo.InvariantCulture), vertex);
                if (item is Dimension dimension && !dimension.DimBlockId.IsNull)
                    Link(node, "标注图形", dimension.DimBlockId);
                if (item is not SymbolTableRecord && !item.OwnerId.IsNull
                    && _transaction.GetObject(item.OwnerId, OpenMode.ForRead) is DBDictionary ownerDictionary)
                {
                    string stored = ownerDictionary.NameAt(item.ObjectId);
                    string? original = XrefBlockComparison.RemoveSourceNamespace(stored, sourceNamespaces ?? []);
                    if (original == null || original.Contains('|')) node.Issues.Add("资源键的原始命名空间无法可靠还原：" + stored);
                    node.Fields["资源键"] = (original ?? stored).ToUpperInvariant();
                }
                CaptureReadableFields(node, item);
            }
            if (!item.ExtensionDictionary.IsNull) Link(node, "扩展字典", item.ExtensionDictionary);
            CaptureXdata(node, item);
        }
        catch (OperationCanceledException) { throw; }
        catch (Pn3DKit.Commands.XrefCheckStoppedException) { throw; }
        catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak) { throw new OperationCanceledException("用户取消。", ex); }
        catch (System.Exception ex) { node.Issues.Add(XrefBlockCadCatalog.Detail(Unwrap(ex))); }
        return node;
    }

    /// <summary>建立身份无关的本地图节点编号，编号不进入比较字段。</summary>
    internal Node New(string kind)
    {
        var node = new Node("n" + _all.Count.ToString(CultureInfo.InvariantCulture), kind);
        _all.Add(node.Id, node); return node;
    }

    /// <summary>资源名称使用符号名称规则；不能可靠恢复的依赖命名空间不猜测。</summary>
    private string SymbolName(SymbolTableRecord symbol, Node node)
    {
        string? original = XrefBlockComparison.RemoveSourceNamespace(symbol.Name, sourceNamespaces ?? []);
        if (original == null || original.Contains('|'))
        {
            node.Issues.Add("资源原始命名空间无法可靠还原：" + symbol.Name);
            return symbol.Name.ToUpperInvariant();
        }
        return original.ToUpperInvariant();
    }

    /// <summary>连接实际关联节点，不把句柄、ObjectId 或文件路径放入内容。</summary>
    private void Link(Node node, string label, ObjectId id)
    {
        if (!id.IsNull) node.Edges.Add(new(label, Visit(id).Id));
    }

    /// <summary>块元数据及实体集合不包含顶层插入变换；绘制顺序另行记录。</summary>
    private void CaptureBlock(Node node, BlockTableRecord block)
    {
        if (block.IsFromExternalReference)
        {
            node.Issues.Add("定义内引用了外参容器，最终嵌套解析不能仅由本源定义证明。");
            return;
        }
        node.Fields["原始块名"] = block.IsAnonymous ? "〈所属匿名定义〉" : SymbolName(block, node);
        node.Fields["基点"] = Scalar(block.Origin);
        node.Fields["单位"] = block.Units.ToString();
        node.Fields["允许分解"] = block.Explodable.ToString();
        node.Fields["缩放规则"] = block.BlockScaling.ToString();
        node.Fields["注释性"] = block.Annotative.ToString();
        node.Fields["动态定义"] = block.IsDynamicBlock.ToString();
        if (block.IsDynamicBlock && block.ExtensionDictionary.IsNull)
            node.Issues.Add("动态定义缺少可读取的参数／动作关联数据，不能仅凭可见几何判定一致。");
        var entities = new List<(ObjectId Id, Entity Entity)>();
        foreach (ObjectId id in block)
        {
            ensure();
            if (_transaction.GetObject(id, OpenMode.ForRead) is Entity entity)
            { entities.Add((id, entity)); Link(node, "实体", id); }
        }
        DrawOrderTable? order = block.DrawOrderTableId.IsNull ? null
            : (DrawOrderTable)_transaction.GetObject(block.DrawOrderTableId, OpenMode.ForRead);
        var explicitlySorted = order == null ? new HashSet<ObjectId>() : entities.Where(e =>
            { var sort = order.GetSortHandle(e.Id); return sort.Value != 0 && sort != e.Id.Handle; }).Select(e => e.Id).ToHashSet();
        var ranks = order == null ? new Dictionary<ObjectId, int>() : order.GetFullDrawOrder(0).Cast<ObjectId>()
            .Select((id, rank) => (id, rank)).ToDictionary(x => x.id, x => x.rank);
        var paints = new Dictionary<ObjectId, string>();
        var obscures = new Dictionary<ObjectId, bool>();
        foreach (var entity in entities)
        {
            ensure();
            using var color = entity.Entity.Color;
            var alpha = entity.Entity.Transparency;
            paints[entity.Id] = ColorValue(color) + "/" + entity.Entity.LineWeight
                + "/" + (alpha.IsByLayer ? "ByLayer" : alpha.IsByBlock ? "ByBlock" : alpha.Alpha.ToString(CultureInfo.InvariantCulture));
            obscures[entity.Id] = entity.Entity is Hatch or Wipeout or Solid or MText
                || !alpha.IsByLayer && !alpha.IsByBlock && alpha.Alpha < 255;
        }
        bool unknownOrder = false;
        for (int i = 0; i < entities.Count; i++)
        for (int j = i + 1; j < entities.Count; j++)
        {
            ensure();
            var first = entities[i]; var second = entities[j];
            bool explicitPair = explicitlySorted.Contains(first.Id) || explicitlySorted.Contains(second.Id);
            bool samePaint = first.Entity.LayerId == second.Entity.LayerId && first.Entity.LinetypeId == second.Entity.LinetypeId
                && paints[first.Id] == paints[second.Id];
            if (!explicitPair && !obscures[first.Id] && !obscures[second.Id] && samePaint) continue;
            // 包围框仅排除不相交的显示依赖；不能用它证明实体或定义相同。
            var a = first.Entity.Bounds; var b = second.Entity.Bounds;
            if (a.HasValue && b.HasValue && (a.Value.MinPoint.X > b.Value.MaxPoint.X || b.Value.MinPoint.X > a.Value.MaxPoint.X
                || a.Value.MinPoint.Y > b.Value.MaxPoint.Y || b.Value.MinPoint.Y > a.Value.MaxPoint.Y
                || a.Value.MinPoint.Z > b.Value.MaxPoint.Z || b.Value.MinPoint.Z > a.Value.MaxPoint.Z)) continue;
            if (explicitPair && ranks.TryGetValue(first.Id, out int one) && ranks.TryGetValue(second.Id, out int two))
            {
                var relation = New("绘制顺序约束");
                relation.Edges.Add(new("先绘制", Visit(one < two ? first.Id : second.Id).Id));
                relation.Edges.Add(new("后绘制", Visit(one < two ? second.Id : first.Id).Id));
                node.Edges.Add(new("绘制关系", relation.Id));
            }
            else if (!unknownOrder)
            {
                node.Issues.Add("默认绘制次序可能影响遮挡或不同显示属性；无法可靠排除其显示影响。");
                unknownOrder = true;
            }
        }
        // 未参与显式显示约束的实体仍是无序多重集，不混入 SORTENTS 或原创建序列。
    }

    /// <summary>符号资源通过公开语义属性读取，排除外参依赖、解析、使用缓存等身份标志。</summary>
    private void CaptureSymbol(Node node, SymbolTableRecord symbol)
    {
        if (symbol is LayerTableRecord layer)
        {
            Properties(node, layer, ["Color", "LineWeight", "LinetypeObjectId", "IsFrozen", "IsOff",
                "IsLocked", "IsPlottable", "Transparency", "MaterialId", "PlotStyleNameId", "ViewportVisibilityDefault"]);
            if (layer.HasOverrides) node.Issues.Add("图层含视口属性覆盖，尚未完整展开各视口的有效资源。");
        }
        else if (symbol is TextStyleTableRecord style)
        {
            Properties(node, style, ["TextSize", "XScale", "ObliquingAngle", "IsVertical", "IsShapeFile", "FlagBits"]);
            var font = style.Font;
            node.Fields["字体族"] = font.TypeFace ?? "";
            node.Fields["粗体"] = font.Bold.ToString(); node.Fields["斜体"] = font.Italic.ToString();
            node.Fields["字符集"] = Scalar(font.CharacterSet); node.Fields["字距族"] = Scalar(font.PitchAndFamily);
        }
        else if (symbol is LinetypeTableRecord line)
        {
            node.Fields["总长度"] = Scalar(line.PatternLength);
            node.Fields["匹配缩放"] = line.IsScaledToFit.ToString();
            for (int index = 0; index < line.NumDashes; index++)
            {
                ensure();
                string prefix = "线型段/" + index.ToString("D8", CultureInfo.InvariantCulture);
                node.Fields[prefix + "/长度"] = Scalar(line.DashLengthAt(index));
                ObjectId glyphStyle = line.ShapeStyleAt(index);
                if (glyphStyle.IsNull) continue;
                Link(node, prefix + "/字形样式", glyphStyle);
                node.Fields[prefix + "/字形"] = Scalar(line.ShapeNumberAt(index));
                node.Fields[prefix + "/偏移"] = Scalar(line.ShapeOffsetAt(index));
                node.Fields[prefix + "/比例"] = Scalar(line.ShapeScaleAt(index));
                node.Fields[prefix + "/UCS方向"] = line.ShapeIsUcsOrientedAt(index).ToString();
                node.Fields[prefix + "/正向"] = line.ShapeIsUprightAt(index).ToString();
                node.Fields[prefix + "/旋转"] = Scalar(line.ShapeRotationAt(index));
                node.Fields[prefix + "/文字"] = line.TextAt(index) ?? "";
            }
        }
        else if (symbol is DimStyleTableRecord dim)
            Properties(node, dim, dim.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0
                    && property.Name.StartsWith("Dim", StringComparison.Ordinal)).Select(property => property.Name).ToArray());
        else if (symbol is not RegAppTableRecord)
            node.Issues.Add("尚未完整解释的符号资源：" + symbol.GetRXClass().Name);
    }

    /// <summary>仅调用明确选定的只读属性；必要属性缺失或异常仍保持不完整。</summary>
    private void Properties(Node node, object value, IReadOnlyList<string> names)
    {
        foreach (string name in names)
        {
            ensure();
            try
            {
                var property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new NotSupportedException("没有公开资源属性：" + name);
                object? field = property.GetValue(value);
                switch (field)
                {
                    case ObjectId id: Link(node, name, id); break;
                    case Autodesk.AutoCAD.Colors.Color color:
                        using (color) node.Fields[name] = ColorValue(color);
                        break;
                    case Autodesk.AutoCAD.Colors.Transparency transparency:
                        node.Fields[name] = transparency.IsByLayer ? "ByLayer" : transparency.IsByBlock ? "ByBlock" : Scalar(transparency.Alpha);
                        break;
                    case PlotStyleDescriptor plot:
                        node.Fields[name] = plot.Type.ToString(); Link(node, name + "/定义", plot.Id); break;
                    default: node.Fields[name] = Scalar(field); break;
                }
            }
            catch (System.Exception ex)
            {
                var actual = Unwrap(ex);
                if (actual is OperationCanceledException or Pn3DKit.Commands.XrefCheckStoppedException) throw actual;
                if (actual is CadException { ErrorStatus: ErrorStatus.UserBreak }) throw new OperationCanceledException("用户取消。", actual);
                node.Issues.Add(name + "：" + XrefBlockCadCatalog.Detail(actual));
            }
        }
    }

    /// <summary>三维语义读取同样保留实体显示属性及资源。</summary>
    private void CaptureDisplay(Node node, Entity entity)
    {
        using var color = entity.Color;
        node.Fields["颜色"] = ColorValue(color);
        node.Fields["线型比例"] = Scalar(entity.LinetypeScale);
        node.Fields["线宽"] = entity.LineWeight.ToString();
        node.Fields["可见"] = entity.Visible.ToString();
        var transparency = entity.Transparency;
        node.Fields["透明度"] = transparency.IsByLayer ? "ByLayer" : transparency.IsByBlock ? "ByBlock" : transparency.Alpha.ToString(CultureInfo.InvariantCulture);
        Link(node, "图层", entity.LayerId); Link(node, "线型", entity.LinetypeId); Link(node, "材质", entity.MaterialId);
        node.Fields["打印样式类型"] = entity.PlotStyleNameId.Type.ToString();
        if (!entity.PlotStyleNameId.Id.IsNull) Link(node, "打印样式", entity.PlotStyleNameId.Id);
    }

    /// <summary>保留定义内部的引用名称、变换和属性，动态引用不通过重置或求值重建。</summary>
    private void CaptureReference(Node node, BlockReference reference)
    {
        ObjectId definition = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
        var block = (BlockTableRecord)_transaction.GetObject(definition, OpenMode.ForRead);
        node.Fields["引用块名"] = block.IsAnonymous ? "〈所属匿名定义〉" : SymbolName(block, node);
        node.Fields["变换"] = Scalar(reference.BlockTransform);
        Link(node, "引用定义", definition);
        foreach (ObjectId attribute in reference.AttributeCollection) Link(node, "实例属性", attribute);
        if (reference.IsDynamicBlock)
        {
            foreach (DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
            {
                ensure();
                node.Fields["动态属性/" + property.PropertyName] = Scalar(property.Value);
            }
            if (!reference.AnonymousBlockTableRecord.IsNull)
                Link(node, "内部动态显示定义", reference.AnonymousBlockTableRecord);
        }
    }

    /// <summary>网格索引转换为有向角环和顶点关联，内部顶点编号不冒充几何内容。</summary>
    private void CaptureMesh(Node node, SubDMesh mesh)
    {
        using var vertices = mesh.Vertices;
        var normals = mesh.VertexNormalArray;
        using var textures = mesh.VertexTextureArray;
        var colors = mesh.VertexColorArray;
        var nodes = new List<Node>();
        for (int i = 0; i < vertices.Count; i++)
        {
            ensure();
            var vertex = New("网格顶点");
            vertex.Fields["坐标"] = Scalar(vertices[i]);
            if (normals.Count == vertices.Count) vertex.Fields["法线"] = Scalar(normals[i]);
            if (textures.Count == vertices.Count) vertex.Fields["纹理坐标"] = Scalar(textures[i]);
            if (colors.Length == vertices.Count) vertex.Fields["颜色"] = Scalar(colors[i]);
            nodes.Add(vertex); node.Edges.Add(new("网格顶点", vertex.Id));
        }
        var faces = mesh.FaceArray;
        int cursor = 0;
        while (cursor < faces.Count)
        {
            ensure();
            int count = faces[cursor++];
            if (count < 3 || count > faces.Count - cursor) throw new InvalidOperationException("网格面索引数量无效。");
            var face = New("网格面"); node.Edges.Add(new("网格面", face.Id));
            var corners = new List<Node>();
            for (int i = 0; i < count; i++)
            {
                int index = faces[cursor++];
                if (index < 0 || index >= nodes.Count) throw new InvalidOperationException("网格顶点索引越界。");
                var corner = New("网格面角"); corners.Add(corner);
                face.Edges.Add(new("面角", corner.Id)); corner.Edges.Add(new("顶点", nodes[index].Id));
            }
            for (int i = 0; i < corners.Count; i++)
                corners[i].Edges.Add(new("下一角", corners[(i + 1) % corners.Count].Id));
        }
        node.Fields["细分级别"] = Scalar(mesh.SmoothLevel);
        node.Issues.Add("网格顶点、面连接及可读逐顶点数据已读取；子实体棱折／材质映射尚未完整规范化。");
    }

    /// <summary>保留颜色方法和可读取色册信息；ByLayer／ByBlock 不依赖未定义的 RGB 缓存。</summary>
    internal static string ColorValue(Autodesk.AutoCAD.Colors.Color color)
    {
        string result = color.ColorMethod + "/" + color.ColorIndex;
        if (color.IsByColor) result += "/" + color.Red + "," + color.Green + "," + color.Blue;
        if (color.HasColorName) result += "/名称:" + color.ColorName;
        if (color.HasBookName) result += "/色册:" + color.BookName;
        return result;
    }

    /// <summary>类型化内存写出补全公开属性没有覆盖的字段；未知二进制不能证明完整。</summary>
    private void CaptureFiler(Node node, DBObject item)
    {
        int position = 0;
        string? resourceName = item is Entity ? null : item.GetType().GetProperty("Name")?.GetValue(item) as string;
        using var filer = new XrefContentFiler((kind, value) =>
        {
            ensure();
            string key = "字段/" + (position++).ToString("D8", CultureInfo.InvariantCulture) + "/" + kind;
            if (value is ObjectId id)
            {
                if (id == item.ObjectId || id == item.OwnerId) return; // 自身及容器身份，不是资源内容。
                Link(node, key, id);
            }
            else if (value is Handle handle)
            {
                if (handle == item.Handle || handle.Value == 0) return;
                Link(node, key, database.GetObjectId(false, handle, 0));
            }
            else if (value is IntPtr)
                node.Issues.Add("写出包含无法解释的内存地址字段：" + key);
            else if (value is byte[] bytes)
            {
                if (bytes.Length != 0) node.Issues.Add("写出包含未解释的二进制数据：" + key + "，" + bytes.Length + " 字节");
            }
            else
            {
                string scalar = Scalar(value);
                if (item is SymbolTableRecord symbol && value is string text && text == symbol.Name)
                    scalar = SymbolName(symbol, node);
                if (resourceName != null && value is string named && named == resourceName)
                {
                    string? original = XrefBlockComparison.RemoveSourceNamespace(named, sourceNamespaces ?? []);
                    if (original == null || original.Contains('|')) node.Issues.Add("资源名称无法可靠还原：" + named);
                    scalar = (original ?? named).ToUpperInvariant();
                }
                if (item is TextStyleTableRecord style && value is string file
                    && !string.IsNullOrWhiteSpace(file) && (file == style.FileName || file == style.BigFontFileName))
                    scalar = file == style.FileName ? "〈字体资源内容〉" : "〈大字体资源内容〉";
                if (item is Material && value is string asset && !string.IsNullOrWhiteSpace(asset)
                    && (Path.IsPathFullyQualified(asset) || new[] { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".dds", ".hdr", ".exr", ".ies" }
                        .Contains(Path.GetExtension(asset), StringComparer.OrdinalIgnoreCase)))
                {
                    try
                    {
                        string path = Path.IsPathFullyQualified(asset) ? asset
                            : HostApplicationServices.Current.FindFile(asset, database, FindFileHint.Default);
                        node.Payloads[key] = File.ReadAllBytes(path);
                        scalar = "〈材质资源内容〉";
                    }
                    catch (System.Exception ex) when (ex is not CadException { ErrorStatus: ErrorStatus.UserBreak })
                    { node.Issues.Add("材质资源无法读取：" + XrefBlockCadCatalog.Detail(ex)); scalar = "〈未读材质资源〉"; }
                }
                node.Fields[key] = scalar;
            }
        });
        item.DwgOut(filer);
        if (filer.FilerStatus != ErrorStatus.OK)
            throw new InvalidOperationException("SDK ErrorStatus=" + filer.FilerStatus);
    }

    /// <summary>增加可读差异标签，核心完整性仍来自完整类型化内容和关联。</summary>
    private static void CaptureReadableFields(Node node, DBObject item)
    {
        if (item is Circle circle) node.Fields["半径"] = Scalar(circle.Radius);
        if (item is Arc arc) node.Fields["半径"] = Scalar(arc.Radius);
        if (item is DBText text) node.Fields["文字"] = text.TextString;
        if (item is MText mtext) node.Fields["文字"] = mtext.Contents;
        if (item is AttributeDefinition attribute)
        {
            node.Fields["属性名"] = attribute.Tag;
            node.Fields["提示"] = attribute.Prompt;
            node.Fields["默认值"] = attribute.TextString;
        }
    }

    /// <summary>读取自定义记录的所有类型化项，不能把数据里的关联句柄当普通字符串。</summary>
    private void CaptureXrecord(Node node, Xrecord record)
    {
        using var data = record.Data;
        if (data == null) return;
        CaptureTyped(node, data.AsArray(), "记录");
    }

    /// <summary>有意义的扩展数据参与比较，无法解释类型时记录具体字段。</summary>
    private void CaptureXdata(Node node, DBObject item)
    {
        using var data = item.XData;
        if (data != null) CaptureTyped(node, data.AsArray(), "扩展数据");
    }

    /// <summary>按类型保留数值、文字、点及关联；XData 句柄解析失败时不能忽略。</summary>
    private void CaptureTyped(Node node, TypedValue[] data, string prefix)
    {
        for (int i = 0; i < data.Length; i++)
        {
            ensure(); var item = data[i];
            string key = prefix + "/" + i.ToString("D8", CultureInfo.InvariantCulture) + "/" + item.TypeCode;
            if (item.Value is ObjectId id) Link(node, key, id);
            else if (item.TypeCode == 1005 && item.Value is string handle)
                Link(node, key, database.GetObjectId(false, new Handle(long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0));
            else if (item.Value is byte[]) node.Issues.Add("未解释的扩展二进制：" + key);
            else node.Fields[key] = Scalar(item.Value);
        }
    }

    /// <summary>字体文件路径只是定位；可读取内容以完整 SHA-256 参与资源比较。</summary>
    private void CaptureFont(Node node, TextStyleTableRecord style)
    {
        foreach (var (key, file) in new[] { ("字体内容", style.FileName), ("大字体内容", style.BigFontFileName) })
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            try
            {
                string path = Path.IsPathFullyQualified(file) ? file
                    : HostApplicationServices.Current.FindFile(file, database, FindFileHint.Default);
                node.Payloads[key] = File.ReadAllBytes(path);
                node.Fields[key] = "〈资源内容，逐字节比较〉";
            }
            catch (System.Exception ex) when (ex is not OperationCanceledException
                && ex is not CadException { ErrorStatus: ErrorStatus.UserBreak })
            { node.Issues.Add("必要字体无法读取：" + file + "；" + XrefBlockCadCatalog.Detail(ex)); }
        }
    }

    /// <summary>通过公开约束图读取关联，节点 ID 仅用于连接，不进入比较字段。</summary>
    private void CaptureConstraints(Node node, Assoc2dConstraintGroup group)
    {
        using var workPlane = group.WorkPlane;
        XrefBrepReader.Geometry(workPlane, node, "约束平面", ensure);
        var nodes = new Dictionary<uint, Node>();
        foreach (var geometry in group.ConstrainedGeometries)
        {
            ensure();
            var target = New(geometry.GetType().Name); nodes.Add(geometry.NodeId, target);
            node.Edges.Add(new("约束几何", target.Id));
            Link(target, "关联几何", geometry.GeomDependencyId);
            XrefBrepReader.Geometry(geometry, target, "几何参数", ensure);
            // 子实体路径的临时索引不是稳定几何语义；未能完整解释时不判为一致。
            target.Issues.Add("约束子实体路径尚未完整规范化，已读取几何关联。");
        }
        foreach (var constraint in group.Constraints)
        {
            ensure();
            var target = New(constraint.GetType().Name);
            node.Edges.Add(new("约束", target.Id));
            target.Fields["启用"] = constraint.IsEnabled.ToString();
            target.Fields["内部"] = constraint.IsInternal.ToString();
            target.Fields["隐含"] = constraint.IsImplied.ToString();
            target.Fields["激活"] = constraint.IsActive.ToString();
            foreach (var geometry in constraint.ConnectedGeometries)
                if (nodes.TryGetValue(geometry.NodeId, out var related)) target.Edges.Add(new("连接几何", related.Id));
                else target.Issues.Add("约束连接到了未读取的节点。");
            target.Issues.Add("约束特有参数未完整解释：" + constraint.GetType().Name);
        }
        foreach (ObjectId dependency in group.GetDependencies(true, true)) Link(node, "约束依赖", dependency);
    }

    /// <summary>严格的标量序列化，不用 SDK ToString 的显示精度代替几何。</summary>
    internal static string Scalar(object? value) => value switch
    {
        null => "〈空〉",
        double n => XrefBlockComparison.Number(n),
        float n => XrefBlockComparison.Number(n),
        string text => text,
        bool flag => flag ? "True" : "False",
        Point3d p => Join(p.X, p.Y, p.Z),
        Point2d p => Join(p.X, p.Y),
        Vector3d v => Join(v.X, v.Y, v.Z),
        Vector2d v => Join(v.X, v.Y),
        Scale3d scale => Join(scale.X, scale.Y, scale.Z),
        Matrix3d matrix => Join(matrix.ToArray()),
        Autodesk.AutoCAD.Colors.EntityColor color => color.ColorMethod + "/" + color.ColorIndex
            + (color.IsByColor ? "/" + color.Red + "," + color.Green + "," + color.Blue : ""),
        Enum enumeration => enumeration.GetType().Name + ":" + enumeration,
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw new NotSupportedException("尚未解释的数值类型：" + value.GetType().FullName)
    };
    /// <summary>几何组件使用明确分隔符，所有数值严格往返格式化。</summary>
    private static string Join(params double[] values) => string.Join(",", values.Select(XrefBlockComparison.Number));
    /// <summary>反射读取几何属性时保留真正的 SDK 错误。</summary>
    internal static System.Exception Unwrap(System.Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
    /// <summary>仅释放本读取器创建的只读事务。</summary>
    public void Dispose() => _transaction.Dispose();
}
