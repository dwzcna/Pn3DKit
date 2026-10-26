using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using CadColor = Autodesk.AutoCAD.Colors.Color;
using LineWeight = Autodesk.AutoCAD.DatabaseServices.LineWeight;

namespace Pn3DKit.Interop;

/// <summary>一个候选来源的隔离副本；只修改自建数据库，复制定义及资源后逐实例解析继承外观。</summary>
internal sealed class XrefBlockVisualPreparation : IDisposable
{
    private readonly Action _ensure;
    private readonly Database _source;
    private readonly Transaction _transaction;
    private readonly HashSet<ObjectId> _active = [];
    private readonly HashSet<ObjectId> _fonts = [];
    private readonly List<string> _issues = [];
    private readonly HashSet<uint> _colors = [];
    private readonly HashSet<int> _weights = [];
    private readonly Dictionary<ObjectId, int> _leafWeights = [];
    private readonly int _defaultWeight;
    private bool _disposed;
    private int _visible;
    /// <summary>外观继承上下文，颜色使用可保留 ACI7 自适应语义的独立值。</summary>
    private sealed record Appearance(short? Aci, byte Red, byte Green, byte Blue, byte Alpha, int Weight,
        ObjectId Layer, ObjectId Linetype);

    /// <summary>当前来源的标准化临时数据库，不注册为 CAD 文档。</summary>
    internal Database Database { get; }
    /// <summary>位于原点且无单位换算的标准引用。</summary>
    internal ObjectId Reference { get; }
    /// <summary>仅用于公共取景的可靠范围；真实空定义可没有范围。</summary>
    internal (double MinX, double MinY, double MaxX, double MaxY)? Bounds { get; }
    /// <summary>不支持类型或资源的具体原因。</summary>
    internal string? Problem => _issues.Count == 0 ? null : string.Join("；", _issues.Distinct(StringComparer.Ordinal));
    /// <summary>可见有效颜色及透明度集合。</summary>
    internal uint[] Colors => _colors.OrderBy(c => c).ToArray();
    /// <summary>可见有效线宽集合，单位百分之一毫米。</summary>
    internal int[] Weights => _weights.OrderBy(w => w).ToArray();
    /// <summary>是否存在应绘制的可见内容，用于拒绝意外的全背景图像。</summary>
    internal bool HasVisibleContent => _visible > 0;

    /// <summary>来源只读；克隆、实例化和外观改写在独立副本与同一 CAD 主线程完成。</summary>
    internal XrefBlockVisualPreparation(Database source, ObjectId definition, int defaultWeight, Action ensure)
    {
        _source = source; _defaultWeight = defaultWeight; _ensure = ensure;
        ensure();
        using (var check = source.TransactionManager.StartOpenCloseTransaction())
        {
            var root = (BlockTableRecord)check.GetObject(definition, OpenMode.ForRead);
            if (root.IsDynamicBlock) throw new NotSupportedException("动态块定义未验证，不克隆或求值：" + root.Name);
        }
        Database = new Database(true, true);
        Transaction? transaction = null;
        var previous = HostApplicationServices.WorkingDatabase;
        try
        {
            // 每来源使用独立库；Replace 明确替换目标默认同名资源，避免静默使用错误的 0 层／Standard。
            using var mapping = new IdMapping();
            source.WblockCloneObjects(new ObjectIdCollection([definition]), Database.BlockTableId,
                mapping, DuplicateRecordCloning.Replace, false);
            ObjectId cloned = mapping[definition].Value;
            if (cloned.IsNull || !cloned.IsValid) throw new InvalidOperationException("块定义未可靠克隆。");
            HostApplicationServices.WorkingDatabase = Database;
            Database.LineWeightDisplay = true;
            Database.Ltscale = source.Ltscale; Database.MsLtScale = false;
            Database.Fillmode = true; Database.Attmode = 1;
            Database.SortEnts = 127; Database.DrawOrderCtl = 3;
            Database.Pdmode = source.Pdmode; Database.Pdsize = source.Pdsize;
            transaction = Database.TransactionManager.StartTransaction();
            _transaction = transaction;
            // WblockCloneObjects 不保证复制 SORTENTS；按原数据库的映射逐定义恢复有效绘制顺序。
            using (var original = source.TransactionManager.StartOpenCloseTransaction())
            foreach (IdPair pair in mapping)
            {
                ensure();
                if (!pair.IsCloned || pair.Value.IsNull || pair.Key.ObjectClass.Name != "AcDbBlockTableRecord") continue;
                var sourceBlock = (BlockTableRecord)original.GetObject(pair.Key, OpenMode.ForRead);
                if (sourceBlock.DrawOrderTableId.IsNull) continue;
                var order = (DrawOrderTable)original.GetObject(sourceBlock.DrawOrderTableId, OpenMode.ForRead);
                var desired = order.GetFullDrawOrder(0).Cast<ObjectId>().Where(id => mapping.Contains(id))
                    .Select(id => mapping[id].Value).Where(id => !id.IsNull).ToArray();
                if (desired.Length == 0) continue;
                var targetBlock = (BlockTableRecord)transaction.GetObject(pair.Value, OpenMode.ForWrite);
                var targetOrder = (DrawOrderTable)transaction.GetObject(targetBlock.DrawOrderTableId, OpenMode.ForWrite);
                foreach (var entityId in desired) targetOrder.MoveToTop(new ObjectIdCollection([entityId]));
            }
            var layers = (LayerTable)transaction.GetObject(Database.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId layerId in layers)
            {
                ensure();
                var layer = (LayerTableRecord)transaction.GetObject(layerId, OpenMode.ForWrite);
                // 只调整实际不同的状态，避免对保留图层的冗余设置触发 eInvalidLayer。
                if (layer.IsOff) layer.IsOff = false;
                if (layer.IsFrozen) layer.IsFrozen = false;
                if (layer.IsLocked) layer.IsLocked = false;
            }
            var zero = (LayerTableRecord)transaction.GetObject(Database.LayerZero, OpenMode.ForRead);
            var normal = new Appearance(7, 255, 255, 255, 255, defaultWeight, zero.ObjectId, Database.ContinuousLinetype);
            ObjectId root = Normalize(cloned, normal, true);
            var table = (BlockTable)transaction.GetObject(Database.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var insert = new BlockReference(Point3d.Origin, root);
            insert.SetDatabaseDefaults(Database);
            insert.LayerId = Database.LayerZero; insert.ColorIndex = 7; insert.LineWeight = (LineWeight)defaultWeight;
            insert.Transparency = new Transparency(255);
            insert.LinetypeId = Database.ContinuousLinetype;
            Reference = model.AppendEntity(insert); transaction.AddNewlyCreatedDBObject(insert, true);
            var block = (BlockTableRecord)transaction.GetObject(root, OpenMode.ForRead);
            foreach (ObjectId id in block)
            {
                ensure();
                if (transaction.GetObject(id, OpenMode.ForRead) is not AttributeDefinition { Constant: false } attribute) continue;
                var value = new AttributeReference();
                value.SetAttributeFromBlock(attribute, insert.BlockTransform); value.TextString = attribute.TextString;
                insert.AttributeCollection.AppendAttribute(value); transaction.AddNewlyCreatedDBObject(value, true);
                value.AdjustAlignment(Database);
                if (_leafWeights.TryGetValue(id, out int weight)) _leafWeights[value.ObjectId] = weight;
            }
            if (HasVisibleContent)
            {
                try
                {
                    Extents3d? combined = null;
                    try { combined = insert.GeometricExtents; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.NullExtents
                        && insert.AttributeCollection.Count > 0) { }
                    foreach (ObjectId attributeId in insert.AttributeCollection)
                    {
                        var attribute = (AttributeReference)transaction.GetObject(attributeId, OpenMode.ForRead);
                        if (attribute.Invisible || !attribute.Visible) continue;
                        var extent = attribute.GeometricExtents;
                        if (combined is {} current) { current.AddExtents(extent); combined = current; }
                        else combined = extent;
                    }
                    if (combined is not {} all) throw new InvalidOperationException("可见内容没有可靠范围。");
                    Bounds = (all.MinPoint.X, all.MinPoint.Y, all.MaxPoint.X, all.MaxPoint.Y);
                    _ = BlockVisualComparison.Frame([Bounds.Value]);
                }
                catch (System.Exception ex) when (Recoverable(ex)) { _issues.Add("无法取得可靠取景范围：" + XrefBlockCadCatalog.Detail(ex)); }
            }
            CheckOcclusion(insert);
            transaction.Commit();
            transaction.Dispose();
            transaction = null;
            ensure();
        }
        catch (System.Exception original)
        {
            var failures = new List<System.Exception> { original };
            try { transaction?.Dispose(); } catch (System.Exception ex) { failures.Add(ex); }
            try { HostApplicationServices.WorkingDatabase = previous; } catch (System.Exception ex) { failures.Add(ex); }
            try { Database.Dispose(); } catch (System.Exception ex) { failures.Add(ex); }
            if (failures.Count > 1) throw new XrefVisualFatalException("候选准备失败且资源清理未完整完成。", new AggregateException(failures));
            throw;
        }
        finally
        {
            try { HostApplicationServices.WorkingDatabase = previous; }
            catch (System.Exception ex) { throw new XrefVisualFatalException("恢复原 WorkingDatabase 失败。", ex); }
        }
    }

    /// <summary>逐嵌套实例复制定义，避免同一子定义在不同 ByBlock 上下文中被错误共用。</summary>
    private ObjectId Normalize(ObjectId original, Appearance parent, bool top = false)
    {
        _ensure();
        if (!_active.Add(original)) { _issues.Add("循环嵌套依赖无法可靠绘制。"); return original; }
        try
        {
            var source = (BlockTableRecord)_transaction.GetObject(original, OpenMode.ForRead);
            if (source.IsDynamicBlock) { _issues.Add("动态块定义未验证：" + source.Name); return original; }
            if (source.IsFromExternalReference) { _issues.Add("候选内部包含外参容器：" + source.Name); return original; }
            if (source.Annotative == AnnotativeStates.True)
                _issues.Add("注释性块需要对象上下文，不能确认独立副本显示：" + source.Name);
            var table = (BlockTable)_transaction.GetObject(Database.BlockTableId, OpenMode.ForWrite);
            var target = new BlockTableRecord { Name = "PNVISUAL_" + Guid.NewGuid().ToString("N"),
                Origin = source.Origin, Units = source.Units, Explodable = source.Explodable, BlockScaling = source.BlockScaling };
            table.Add(target); _transaction.AddNewlyCreatedDBObject(target, true);
            var ids = source.Cast<ObjectId>().ToArray();
            if (ids.Length > 0)
            {
                using var map = new IdMapping();
                Database.DeepCloneObjects(new ObjectIdCollection(ids), target.ObjectId, map, false);
                if (!source.DrawOrderTableId.IsNull)
                {
                    var order = (DrawOrderTable)_transaction.GetObject(source.DrawOrderTableId, OpenMode.ForRead);
                    var desired = order.GetFullDrawOrder(0).Cast<ObjectId>().Where(id => map.Contains(id))
                        .Select(id => map[id].Value).ToArray();
                    if (desired.Length > 0)
                    {
                        var draw = (DrawOrderTable)_transaction.GetObject(target.DrawOrderTableId, OpenMode.ForWrite);
                        foreach (var entityId in desired) draw.MoveToTop(new ObjectIdCollection([entityId]));
                    }
                }
            }
            foreach (ObjectId id in target)
            {
                _ensure();
                if (_transaction.GetObject(id, OpenMode.ForWrite) is not Entity entity)
                { _issues.Add("非实体的块内容无法绘制：" + id.ObjectClass.Name); continue; }
                if (!entity.Visible || entity is AttributeDefinition { Invisible: true } or AttributeReference { Invisible: true }) continue;
                string type = entity.GetRXClass().Name;
                if (!XrefBlockContentScope.SupportsEntity(type) || entity is ProxyEntity or MInsertBlock or Ray or Xline or MLeader)
                { _issues.Add("本轮二维普通块不支持：" + type); continue; }
                if (entity is BlockReference { IsDynamicBlock: true })
                { _issues.Add("动态块引用未验证：" + type); continue; }
                Appearance appearance = Resolve(entity, parent);
                Apply(entity, appearance);
                if (entity is BlockReference nested)
                {
                    if (nested.Normal.X != 0 || nested.Normal.Y != 0)
                    { _issues.Add("三维嵌套引用方向未验证：" + type); continue; }
                    if (!nested.ExtensionDictionary.IsNull)
                        CheckClip(nested);
                    nested.BlockTableRecord = Normalize(nested.BlockTableRecord, appearance);
                    foreach (ObjectId attributeId in nested.AttributeCollection)
                    {
                        var attribute = (AttributeReference)_transaction.GetObject(attributeId, OpenMode.ForWrite);
                        if (!attribute.Visible || attribute.Invisible) continue;
                        Appearance a = Resolve(attribute, appearance); Apply(attribute, a); Leaf(attribute, a);
                    }
                    continue;
                }
                if (entity is AttributeDefinition { Constant: false } && !top) continue;
                if (entity is Dimension dimension)
                {
                    if (dimension.Normal.X != 0 || dimension.Normal.Y != 0 || dimension.HasFields)
                        _issues.Add("非二维标注或包含字段，不执行求值：" + type);
                    CheckFont(dimension.TextStyleId);
                    if (dimension.DynamicDimension || dimension.DimBlockId.IsNull)
                    { _issues.Add("标注缺少可靠缓存图形或为动态标注：" + type); continue; }
                    dimension.DimBlockId = Normalize(dimension.DimBlockId, appearance);
                    continue;
                }
                Leaf(entity, appearance);
            }
            return target.ObjectId;
        }
        finally { _active.Remove(original); }
    }

    /// <summary>解析 ByLayer／ByBlock／默认线宽；颜色保持 ACI7 的双背景自适应语义。</summary>
    private Appearance Resolve(Entity entity, Appearance parent)
    {
        var stored = (LayerTableRecord)_transaction.GetObject(entity.LayerId, OpenMode.ForRead);
        ObjectId layerId = entity.LayerId == Database.LayerZero ? parent.Layer : entity.LayerId;
        var layer = (LayerTableRecord)_transaction.GetObject(layerId, OpenMode.ForRead);
        using var original = entity.Color;
        using var layerColor = layer.Color;
        var color = original.IsByLayer ? layerColor : original;
        short? aci; byte red, green, blue;
        if (color.IsByBlock) { aci = parent.Aci; red = parent.Red; green = parent.Green; blue = parent.Blue; }
        else
        {
            if (!color.IsByAci && !color.IsByColor)
                _issues.Add("有效颜色方法无法确认：" + color.ColorMethod);
            aci = color.IsByAci ? color.ColorIndex : null;
            var rgb = color.ColorValue; red = rgb.R; green = rgb.G; blue = rgb.B;
        }
        var transparency = entity.Transparency.IsByLayer ? layer.Transparency : entity.Transparency;
        byte alpha = transparency.IsByBlock ? parent.Alpha : transparency.IsByAlpha ? transparency.Alpha : (byte)255;
        if (transparency.IsInvalid) _issues.Add("透明度状态无效。");
        int weight = (int)entity.LineWeight;
        if (weight == (int)LineWeight.ByLayer) weight = (int)layer.LineWeight;
        if (weight == (int)LineWeight.ByBlock) weight = parent.Weight;
        if (weight == (int)LineWeight.ByLineWeightDefault) weight = _defaultWeight;
        if (weight < 0) _issues.Add("有效线宽无法解析：" + weight);
        ObjectId linetype = entity.LinetypeId;
        if (linetype == Database.ByLayerLinetype) linetype = layer.LinetypeObjectId;
        if (linetype == Database.ByBlockLinetype) linetype = parent.Linetype;
        CheckLinetype(linetype);
        return new(aci, red, green, blue, alpha, weight, layerId, linetype);
    }

    /// <summary>只在副本中显式落实继承外观，原图层名不作为比较身份。</summary>
    private static void Apply(Entity entity, Appearance appearance)
    {
        using var color = appearance.Aci is short aci
            ? CadColor.FromColorIndex(ColorMethod.ByAci, aci) : CadColor.FromRgb(appearance.Red, appearance.Green, appearance.Blue);
        entity.Color = color; entity.Transparency = new Transparency(appearance.Alpha);
        entity.LineWeight = (LineWeight)Math.Max(0, appearance.Weight);
        entity.LayerId = appearance.Layer; entity.LinetypeId = appearance.Linetype;
    }

    /// <summary>仅统计可见叶实体；检查字体、复合颜色及平面范围，不触发字段或动态求值。</summary>
    private void Leaf(Entity entity, Appearance appearance)
    {
        _ensure();
        if (appearance.Alpha == 0) return;
        if (entity.Annotative == AnnotativeStates.True) _issues.Add("注释性实体需要显示上下文：" + entity.GetRXClass().Name);
        if (entity is DBText text)
        {
            CheckFont(text.TextStyleId);
            if (text.Thickness != 0 || text.Normal.X != 0 || text.Normal.Y != 0) _issues.Add("三维文字或厚度未验证。");
            if (text.HasFields) _issues.Add("文字字段缓存及自动更新状态无法确认，不执行字段求值。");
        }
        if (entity is MText mtext)
        {
            CheckFont(mtext.TextStyleId);
            if (Regex.IsMatch(mtext.Contents, @"\\[cCfF]")) _issues.Add("内嵌文字颜色／字体无法完整解析。");
            if (mtext.Normal.X != 0 || mtext.Normal.Y != 0) _issues.Add("非二维多行文字。");
            if (mtext.HasFields) _issues.Add("多行文字包含字段，不执行求值。");
            if (mtext.BackgroundFill) _issues.Add("多行文字背景遮罩尚不能可靠验证。");
            if (mtext.BackgroundFill && !mtext.UseBackgroundColor)
            {
                using var bg = mtext.BackgroundFillColor; var rgb = bg.ColorValue;
                byte alpha = mtext.BackgroundTransparency.IsByAlpha ? mtext.BackgroundTransparency.Alpha : (byte)255;
                _colors.Add(((uint)alpha << 24) | ((uint)rgb.R << 16) | ((uint)rgb.G << 8) | rgb.B);
            }
        }
        if (entity is AttributeDefinition { IsMTextAttributeDefinition: true } or AttributeReference { IsMTextAttribute: true })
            _issues.Add("多行属性的内嵌格式尚不能完整核对。");
        if (entity is Hatch { IsGradient: true }) _issues.Add("渐变填充的复合颜色及编码通道未完整支持。");
        // 真实 2026 原型未能可靠保留遮罩顺序；不能拿缺少遮罩的图像证明匹配。
        if (entity is Wipeout) { _issues.Add("当前离屏后端无法可靠验证 Wipeout 遮罩及顺序。"); return; }
        // 非恒定定义本身可没有显示范围；顶层实例默认值创建后再核对实际属性引用。
        if (entity is not AttributeDefinition { Constant: false })
        {
            try
            {
                var extent = entity.GeometricExtents;
                if (extent.MinPoint.Z != extent.MaxPoint.Z) _issues.Add("包含非平面或有厚度的内容：" + entity.GetRXClass().Name);
            }
            catch (System.Exception ex) when (Recoverable(ex)) { _issues.Add("实体范围读取失败：" + entity.GetRXClass().Name + "；" + XrefBlockCadCatalog.Detail(ex)); }
        }
        _colors.Add(((uint)appearance.Alpha << 24) | ((uint)appearance.Red << 16) | ((uint)appearance.Green << 8) | appearance.Blue);
        _weights.Add(appearance.Weight); _leafWeights[entity.ObjectId] = appearance.Weight; _visible++;
    }

    /// <summary>原生测试尚不能确认重叠异色内容的排序；只用临时副本范围保守拒绝证明匹配。</summary>
    private void CheckOcclusion(BlockReference root)
    {
        if (_issues.Count != 0 || (_colors.Count <= 1 && _weights.Count <= 1)) return;
        var bounds = new List<(Extents3d Box, uint Color, int Weight)>();
        void LeafBox(Entity entity, Matrix3d transform)
        {
            if (!entity.Visible || entity is AttributeDefinition { Constant: false } or AttributeDefinition { Invisible: true }
                or AttributeReference { Invisible: true }) return;
            if (entity is BlockReference reference)
            {
                Walk(reference.BlockTableRecord, transform * reference.BlockTransform);
                foreach (ObjectId attributeId in reference.AttributeCollection)
                    LeafBox((Entity)_transaction.GetObject(attributeId, OpenMode.ForRead), transform);
                return;
            }
            using var color = entity.Color;
            var rgb = color.ColorValue;
            byte alpha = entity.Transparency.IsByAlpha ? entity.Transparency.Alpha : (byte)255;
            if (alpha == 0) return;
            var box = entity.GeometricExtents; box.TransformBy(transform);
            bounds.Add((box, ((uint)alpha << 24) | ((uint)rgb.R << 16) | ((uint)rgb.G << 8) | rgb.B, (int)entity.LineWeight));
        }
        void Walk(ObjectId id, Matrix3d transform)
        {
            _ensure();
            var block = (BlockTableRecord)_transaction.GetObject(id, OpenMode.ForRead);
            foreach (ObjectId entityId in block)
                if (_transaction.GetObject(entityId, OpenMode.ForRead) is Entity entity) LeafBox(entity, transform);
        }
        try
        {
            LeafBox(root, Matrix3d.Identity);
            // 扩展可能的像素覆盖范围，包含交叉细线的排序影响；范围不是一致性证据。
            double margin = Bounds is {} box ? Math.Max(box.MaxX-box.MinX, box.MaxY-box.MinY)/1024 : 0;
            var ordered = bounds.OrderBy(b => b.Box.MinPoint.X).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                _ensure();
                for (int j = i+1; j < ordered.Length; j++)
                {
                    if (ordered[j].Box.MinPoint.X > ordered[i].Box.MaxPoint.X + margin) break;
                    if (ordered[i].Color == ordered[j].Color && ordered[i].Weight == ordered[j].Weight) continue;
                    if (ordered[j].Box.MinPoint.Y <= ordered[i].Box.MaxPoint.Y + margin
                        && ordered[j].Box.MaxPoint.Y >= ordered[i].Box.MinPoint.Y - margin)
                    { _issues.Add("重叠不同颜色或线宽内容的绘制顺序不能由当前离屏后端可靠验证。"); return; }
                }
            }
        }
        catch (System.Exception ex) when (Recoverable(ex))
        { _issues.Add("无法确认遮挡范围：" + XrefBlockCadCatalog.Detail(ex)); }
    }

    /// <summary>图形裁剪依赖必须保留且可识别；未知扩展过滤器不能静默忽略。</summary>
    private void CheckClip(BlockReference reference)
    {
        var dictionary = (DBDictionary)_transaction.GetObject(reference.ExtensionDictionary, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary)
            if (entry.Key.Contains("FILTER", StringComparison.OrdinalIgnoreCase))
                _issues.Add("嵌套裁剪过滤器尚未通过可靠绘制验证：" + entry.Key);
    }

    /// <summary>复杂线型的文字／形文件属于必要资源，缺少资源即未验证。</summary>
    private void CheckLinetype(ObjectId id)
    {
        if (id.IsNull) { _issues.Add("线型资源为空。"); return; }
        var line = (LinetypeTableRecord)_transaction.GetObject(id, OpenMode.ForRead);
        if (line.NumDashes > 0) _issues.Add("当前离屏后端无法可靠验证非连续线型：" + line.Name);
        for (int i = 0; i < line.NumDashes; i++)
        {
            ObjectId style = line.ShapeStyleAt(i);
            if (!style.IsNull) CheckFont(style);
        }
    }

    /// <summary>字体按原请求核对，不能把替代字体当作可靠原字体。</summary>
    private void CheckFont(ObjectId id)
    {
        if (id.IsNull || !_fonts.Add(id)) return;
        var style = (TextStyleTableRecord)_transaction.GetObject(id, OpenMode.ForRead);
        bool configured = false;
        foreach (var resource in new[] { (File: style.FileName, Big: false), (File: style.BigFontFileName, Big: true) })
        {
            string file = resource.File;
            if (string.IsNullOrWhiteSpace(file)) continue;
            configured = true;
            try
            {
                string path = Path.IsPathFullyQualified(file) ? file : HostApplicationServices.Current.FindFile(file, _source, FindFileHint.FontFile);
                if (!File.Exists(path) || !string.Equals(Path.GetFileNameWithoutExtension(file),
                    Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("字体缺失或发生替代：" + file + " → " + path);
                using var readable = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // 只写临时样式，固定已核实的原字体；无扩展名的 txt 也交由 SDK 字体规则解析。
                if (!style.IsWriteEnabled) style.UpgradeOpen();
                if (resource.Big) style.BigFontFileName = path; else style.FileName = path;
            }
            catch (System.Exception ex) when (Recoverable(ex)) { _issues.Add("字体不可确认：" + file + "；" + XrefBlockCadCatalog.Detail(ex)); }
        }
        if (!configured)
        {
            var font = style.Font;
            if (string.IsNullOrWhiteSpace(font.TypeFace) || !System.Windows.Media.Fonts.SystemFontFamilies.Any(f =>
                f.FamilyNames.Values.Any(n => string.Equals(n, font.TypeFace, StringComparison.OrdinalIgnoreCase))))
                _issues.Add("未找到原字体族：" + font.TypeFace);
        }
    }

    /// <summary>取消、文档变化、报告和资源状态故障不能转换为普通字段读取问题。</summary>
    private static bool Recoverable(System.Exception ex) => ex is not OperationCanceledException
        and not XrefVisualFatalException and not XrefBlockLogException and not Pn3DKit.Commands.XrefCheckStoppedException
        && ex is not Autodesk.AutoCAD.Runtime.Exception { ErrorStatus: Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak };

    /// <summary>用候选组共用编码色表改写临时叶实体，只用于线宽位置辅助绘制。</summary>
    internal void EncodeWeights(IReadOnlyDictionary<int, uint> palette)
    {
        _ensure();
        using var transaction = Database.TransactionManager.StartTransaction();
        foreach (var pair in _leafWeights)
        {
            _ensure();
            var entity = (Entity)transaction.GetObject(pair.Key, OpenMode.ForWrite);
            uint rgb = palette[pair.Value];
            using var color = CadColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            entity.Color = color; entity.Transparency = new Transparency(255);
        }
        transaction.Commit();
    }

    /// <summary>释放自建事务和数据库；释放失败意味着会话状态未知，必须停止批次。</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        var errors = new List<System.Exception>();
        try { Database.Dispose(); } catch (System.Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new XrefVisualFatalException("临时视觉数据库释放失败。", new AggregateException(errors));
    }
}
