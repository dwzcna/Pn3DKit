using System.Collections;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.BoundaryRepresentation;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Pn3DKit.Models;
using CadException = Autodesk.AutoCAD.Runtime.Exception;
using Node = Pn3DKit.Interop.XrefBlockSnapshotReader.Node;

namespace Pn3DKit.Interop;

/// <summary>读取精确 BRep 拓扑和已支持的解析／NURBS 几何，不使用网格近似或质量属性。</summary>
internal static class XrefBrepReader
{
    /// <summary>官方 SDK 公开几何的明确读取集合；不枚举任意属性或调用转换／求值方法。</summary>
    private static readonly IReadOnlyDictionary<string, string[]> GeometryProperties = new Dictionary<string, string[]>
    {
        ["Plane"] = ["Coefficients", "PointOnPlane", "Normal"],
        ["BoundedPlane"] = ["Coefficients", "PointOnPlane", "Normal"],
        ["Cylinder"] = ["Origin", "Radius", "AxisOfSymmetry", "ReferenceAxis", "Height", "IsOuterNormal"],
        ["Cone"] = ["BaseCenter", "BaseRadius", "AxisOfSymmetry", "ReferenceAxis", "HalfAngle", "Height", "IsOuterNormal"],
        ["Sphere"] = ["Center", "Radius", "NorthAxis", "ReferenceAxis", "SouthPole", "NorthPole", "IsOuterNormal"],
        ["Torus"] = ["Center", "MajorRadius", "MinorRadius", "AxisOfSymmetry", "ReferenceAxis", "IsOuterNormal"],
        ["Line3d"] = ["PointOnLine", "Direction"],
        ["LineSegment3d"] = ["StartPoint", "EndPoint"],
        ["Ray3d"] = ["PointOnLine", "Direction"],
        ["CircularArc3d"] = ["Center", "Radius", "Normal", "ReferenceVector", "StartAngle", "EndAngle"],
        ["EllipticalArc3d"] = ["Center", "MajorAxis", "MinorAxis", "MajorRadius", "MinorRadius", "StartAngle", "EndAngle"],
        ["ConstrainedPoint"] = ["Point"],
        ["ConstrainedImplicitPoint"] = ["Point", "PointType", "PointIndex"],
        ["ConstrainedLine"] = ["PointOnLine", "Direction"],
        ["ConstrainedBoundedLine"] = ["PointOnLine", "Direction", "StartPoint", "EndPoint", "IsRay"],
        ["ConstrainedCircle"] = ["CenterPoint", "Radius"],
        ["ConstrainedArc"] = ["CenterPoint", "Radius", "StartPoint", "EndPoint", "MidPoint"],
        ["ConstrainedEllipse"] = ["CenterPoint", "MajorRadius", "MinorRadius", "Direction"],
        ["ConstrainedSpline"] = ["NurbSpline"],
        ["NurbCurve3d"] = ["DefinitionData"],
        ["NurbCurve3dData"] = ["Degree", "Rational", "Periodic", "ControlPoints", "Knots", "Weights"],
        ["NurbSurface"] = ["DegreeInU", "DegreeInV", "NumControlPointsInU", "NumControlPointsInV",
            "ControlPoints", "UKnots", "VKnots", "Weights", "IsRationalInU", "IsRationalInV", "IsPeriodicInU", "IsPeriodicInV"]
    };

    /// <summary>将拓扑对象的临时身份转换成关联图，保持共享顶点、边、环、面和壳关系。</summary>
    internal static void Capture(Entity entity, Node root, Func<string, Node> create,
        Action<Node, string, ObjectId> link, Action ensure)
    {
        using var brep = new Brep(entity);
        var registry = new Dictionary<BrepEntity, Node>();
        var owned = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        Node Vertex(Autodesk.AutoCAD.BoundaryRepresentation.Vertex vertex)
        {
            owned.Add(vertex);
            if (registry.TryGetValue(vertex, out var cached)) return cached;
            var node = create("三维顶点"); registry.Add(vertex, node);
            node.Fields["坐标"] = XrefBlockSnapshotReader.Scalar(vertex.Point);
            return node;
        }
        Node Edge(Autodesk.AutoCAD.BoundaryRepresentation.Edge edge)
        {
            ensure(); owned.Add(edge);
            if (registry.TryGetValue(edge, out var cached)) return cached;
            var node = create("三维边"); registry.Add(edge, node);
            node.Fields["方向"] = edge.IsOrientToCurve.ToString();
            node.Edges.Add(new("起点", Vertex(edge.Vertex1).Id));
            node.Edges.Add(new("终点", Vertex(edge.Vertex2).Id));
            using var curve = edge.Curve;
            Geometry(curve, node, "曲线", ensure);
            return node;
        }
        try
        {
            foreach (Autodesk.AutoCAD.BoundaryRepresentation.Vertex vertex in brep.Vertices)
            { ensure(); root.Edges.Add(new("三维顶点", Vertex(vertex).Id)); }
            foreach (Autodesk.AutoCAD.BoundaryRepresentation.Edge edge in brep.Edges)
            { ensure(); root.Edges.Add(new("三维边", Edge(edge).Id)); }
            foreach (Autodesk.AutoCAD.BoundaryRepresentation.Face face in brep.Faces)
            {
                ensure(); owned.Add(face);
                var node = create("三维面"); registry.Add(face, node); root.Edges.Add(new("三维面", node.Id));
                node.Fields["方向"] = face.IsOrientToSurface.ToString();
                if (entity is Solid3d solid)
                {
                    var subentity = face.SubentityPath.SubentId;
                    using var color = solid.GetSubentityColor(subentity);
                    node.Fields["面颜色"] = XrefBlockSnapshotReader.ColorValue(color);
                    link(node, "面材质", solid.GetSubentityMaterial(subentity));
                    using var mapper = solid.GetSubentityMaterialMapper(subentity);
                    foreach (string property in new[] { "Transform", "Projection", "AutoTransform", "UTiling", "VTiling" })
                    {
                        var member = mapper.GetType().GetProperty(property)
                            ?? throw new NotSupportedException("无法读取面材质映射：" + property);
                        node.Fields["材质映射/" + property] = XrefBlockSnapshotReader.Scalar(member.GetValue(mapper));
                    }
                }
                else if (entity is not Autodesk.AutoCAD.DatabaseServices.Region)
                    node.Issues.Add("Surface 的子实体外观尚未完整解析。");
                using var surface = face.Surface;
                Geometry(surface, node, "曲面", ensure);
                var shell = face.Shell; owned.Add(shell);
                if (!registry.TryGetValue(shell, out var shellNode))
                {
                    shellNode = create("三维壳"); registry.Add(shell, shellNode);
                    shellNode.Fields["类型"] = shell.ShellType.ToString();
                    root.Edges.Add(new("三维壳", shellNode.Id));
                }
                shellNode.Edges.Add(new("面", node.Id));
                var complex = shell.Complex; owned.Add(complex);
                if (!registry.TryGetValue(complex, out var complexNode))
                {
                    complexNode = create("三维连通体"); registry.Add(complex, complexNode);
                    root.Edges.Add(new("三维连通体", complexNode.Id));
                }
                complexNode.Edges.Add(new("壳", shellNode.Id));
                foreach (BoundaryLoop loop in face.Loops)
                {
                    ensure(); owned.Add(loop);
                    var boundary = create("三维边界环");
                    boundary.Fields["环类型"] = loop.LoopType.ToString();
                    node.Edges.Add(new("边界环", boundary.Id));
                    foreach (Autodesk.AutoCAD.BoundaryRepresentation.Edge edge in loop.Edges)
                        boundary.Edges.Add(new("边", Edge(edge).Id));
                    foreach (Autodesk.AutoCAD.BoundaryRepresentation.Vertex vertex in loop.Vertices)
                        boundary.Edges.Add(new("顶点", Vertex(vertex).Id));
                }
            }
        }
        finally
        {
            var errors = new List<string>();
            foreach (var item in owned)
                try { item.Dispose(); } catch (System.Exception ex) { errors.Add(ex.Message); }
            if (errors.Count != 0) root.Issues.Add("BRep 资源释放异常：" + string.Join("；", errors));
        }
    }

    /// <summary>保留精确解析参数或控制点／节点／权重；不支持的几何表示明确说明。</summary>
    internal static void Geometry(object value, Node node, string path, Action ensure)
    {
        ensure();
        string type = value.GetType().Name;
        if (type is "ExternalCurve3d" or "ExternalSurface" or "ExternalBoundedSurface")
        {
            string property = type == "ExternalCurve3d" ? "NativeCurve" : type == "ExternalSurface" ? "nativeSurface" : "BaseSurface";
            object? native = value.GetType().GetProperty(property)?.GetValue(value);
            if (native == null) { node.Issues.Add(path + " 无法读取原生几何：" + type); return; }
            try
            {
                Geometry(native, node, path, ensure);
                if (type == "ExternalBoundedSurface")
                {
                    object? count = value.GetType().GetProperty("NumContours")?.GetValue(value);
                    if (count == null) node.Issues.Add(path + " 无法确认曲面修剪边界。");
                    else if (Convert.ToInt32(count, CultureInfo.InvariantCulture) > 0
                        && native.GetType().Name is not ("Plane" or "BoundedPlane"))
                        node.Issues.Add(path + " 曲面含参数域修剪，三维边界已读取但 UV 修剪尚未完整解释。");
                }
            }
            finally { if (native is IDisposable disposable) disposable.Dispose(); }
            return;
        }
        node.Fields[path + "/类型"] = type;
        if (!GeometryProperties.TryGetValue(type, out var properties))
        { node.Issues.Add(path + " 尚不支持的精确几何类型：" + type); return; }
        foreach (string property in properties)
        {
            ensure();
            try
            {
                var member = value.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new NotSupportedException(type + " 缺少公开参数 " + property);
                object? field = member.GetValue(value);
                Values(field, node, path + "/" + property, ensure);
            }
            catch (System.Exception ex)
            {
                var actual = XrefBlockSnapshotReader.Unwrap(ex);
                if (actual is OperationCanceledException or Pn3DKit.Commands.XrefCheckStoppedException) throw actual;
                if (actual is CadException { ErrorStatus: Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak })
                    throw new OperationCanceledException("用户取消。", actual);
                node.Issues.Add(path + "/" + property + "：" + XrefBlockCadCatalog.Detail(actual));
            }
        }
    }

    /// <summary>集合保留参数顺序，拓扑对象则由图关系另行处理。</summary>
    private static void Values(object? value, Node node, string path, Action ensure)
    {
        if (value != null && GeometryProperties.ContainsKey(value.GetType().Name))
        { Geometry(value, node, path, ensure); return; }
        if (value is Interval interval)
        {
            node.Fields[path + "/下界"] = interval.IsBoundedBelow ? XrefBlockSnapshotReader.Scalar(interval.LowerBound) : "负无界";
            node.Fields[path + "/上界"] = interval.IsBoundedAbove ? XrefBlockSnapshotReader.Scalar(interval.UpperBound) : "正无界";
            return;
        }
        if (value is IEnumerable list and not string)
        {
            int index = 0;
            foreach (object? item in list)
            { ensure(); Values(item, node, path + "/" + (index++).ToString("D8", CultureInfo.InvariantCulture), ensure); }
            node.Fields[path + "/数量"] = index.ToString(CultureInfo.InvariantCulture);
        }
        else node.Fields[path] = XrefBlockSnapshotReader.Scalar(value);
    }
}
