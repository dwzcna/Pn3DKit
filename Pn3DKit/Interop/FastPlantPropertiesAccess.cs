using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.ProcessPower.DataObjects;
using Autodesk.ProcessPower.PlantInstance;
using Autodesk.ProcessPower.PnPCommonDbx;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Interop;

/// <summary>Plant 自身属性读写适配；不改变获取关系、不生成编号、不提交或保存项目。</summary>
internal static class FastPlantPropertiesAccess
{
    /// <summary>通过非内联入口隔离可选 SDK 的 JIT 和模块加载。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static IFastPlantProperties Capture(bool busy)
    {
        var project = PlantApplication.CurrentProject;
        return new Reader(project != null, () => ReferenceEquals(project, PlantApplication.CurrentProject), busy);
    }

    /// <summary>只定位写入事件关联的数据行，不读取属性值或权限。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string? RelatedDataKey(ObjectId id)
    {
        var context = PlantTagFormatAccess.Locate(id);
        return context == null ? null : context.PartName + ":" + context.RowId + ":" + context.ClassName;
    }

    private sealed class Reader(bool hasProject, Func<bool> isCurrent, bool busy) : IFastPlantProperties
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlantTagFormat?> _formats = new(StringComparer.Ordinal);
        public bool IsCurrent => isCurrent();

        /// <summary>SDK 自身属性键与项目级友好名称配对，包含继承／扩展和只读字段。</summary>
        /// <remarks>仅在主线程文档读锁内调用；不展开关系，不求值格式，不写入或提交项目。</remarks>
        public IReadOnlyList<PnPropertyCatalogEntry> Catalog(ObjectId id)
        {
            if (!hasProject) throw new InvalidOperationException("没有当前 Plant 项目。");
            if (!IsCurrent) throw new InvalidOperationException("当前 Plant 项目已变化。");
            var context = PlantTagFormatAccess.Locate(id)
                ?? throw new InvalidOperationException("所选对象不是当前项目中的 Plant 部件。");
            var table = context.Links.GetPnPDatabase().Tables[context.ClassName];
            var names = context.Links.GetAllProperties(id, true).Select(x => x.Key).ToArray();
            // Project 接口优先查询所属项目／类别的本地化字符串表，再回退列 DisplayName。
            // 不以是否持久化 DisplayName 为门槛，也不按英文内部名硬编码翻译。
            return PnPropertyCatalogPolicy.Build(names,
                name => context.Part.GetColumnDisplayName(table, name), PlantProjectTagAccess.Error);
        }

        /// <summary>每轮缓存显示名和格式，权限仍逐对象读取，不将获取字段当可写文本。</summary>
        public PnObjectProperties Read(ObjectId id, Document document, Transaction transaction,
            IReadOnlyList<PnPropertySpec> specs)
        {
            try { return ReadCore(id, document, transaction, specs); }
            catch (System.Exception ex)
            {
                string error = PlantProjectTagAccess.Error(ex);
                return new("", "读取失败", specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Error(error),
                    PnEditPermission.ReadOnly(error))).ToArray());
            }
        }

        /// <summary>在可选 SDK 类型内部保留定位阶段的 DataLinks 状态，普通 CAD 不需加载异常类型。</summary>
        private PnObjectProperties ReadCore(ObjectId id, Document document, Transaction transaction,
            IReadOnlyList<PnPropertySpec> specs)
        {
            if (!hasProject) return Missing(specs, "没有当前 Plant 项目。");
            var context = PlantTagFormatAccess.Locate(id);
            if (context == null) return Missing(specs, "所选对象不是当前项目中的 Plant 部件。", "非 Plant 对象");
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity) return Missing(specs, "目标不是实体。");
            string key = context.PartName + ":" + context.ClassName;
            if (!_names.TryGetValue(key, out string? name))
                _names[key] = name = PlantTagCatalogAccess.ReadDisplayName(context.Part, context.Links.GetPnPDatabase(), context.ClassName);
            bool locked = true;
            string? stateError = null;
            try { locked = ((LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked; }
            catch (System.Exception ex) { stateError = PlantProjectTagAccess.Error(ex); }
            var cells = specs.Select(spec => ReadCell(context, spec, document, locked, stateError)).ToArray();
            return new(key, name, cells, context.PartName + ":" + context.RowId + ":" + context.ClassName);
        }

        /// <summary>不存在与实际读取失败分开；权限查询失败不掩盖已读取的值。</summary>
        private PnPropertyCell ReadCell(PlantTagContext context, PnPropertySpec spec, Document document, bool locked,
            string? stateError)
        {
            string? path = PnPropertyPolicy.Normalize(spec.Input, out string reason);
            if (path == null) return new(spec, FastPropertyValue.Missing(reason), PnEditPermission.ReadOnly(reason));
            string property = path[13..];
            try
            {
                if (!context.Links.HasProperty(context.RowId, property))
                    return new(spec, FastPropertyValue.AbsentProperty("对象不含属性 " + path), PnEditPermission.ReadOnly("属性不存在。"));
                var value = FastPropertyValue.Value(ReadValue(context, property));
                PnEditPermission permission;
                try { permission = GetPermission(context, property, value.Raw, document, locked, stateError); }
                catch (System.Exception ex) { permission = PnEditPermission.ReadOnly("无法确认写权限：" + PlantProjectTagAccess.Error(ex)); }
                return new(spec, value, permission);
            }
            catch (System.Exception ex)
            {
                string error = PlantProjectTagAccess.Error(ex);
                return new(spec, FastPropertyValue.Error(error), PnEditPermission.ReadOnly(error));
            }
        }

        /// <summary>普通字段只读取自身明确约束；位号格式及登记仅供 Tag 专用流程使用。</summary>
        private PnEditPermission GetPermission(PlantTagContext context, string property, string? value, Document document,
            bool locked, string? stateError)
        {
            if (stateError != null) return PnEditPermission.ReadOnly("无法确认图层状态：" + stateError);
            var table = context.Links.GetPnPDatabase().Tables[context.ClassName];
            var column = FindEffectiveColumn(table, property);
            if (column == null) return PnEditPermission.ReadOnly("字段没有可核对的 SDK 列元数据。");
            var metadata = new PnColumnMetadata(property, column.IsReadOnly, column.IsSystem,
                column.IsAutogenerated, column.IsCounter, column.IsExpression, column.IsExtensionColumn,
                column.IsUnique, column.ColumnType == typeof(string) || column.ColumnType.IsPrimitive || column.ColumnType == typeof(decimal));
            var field = PnPropertyMetadataPolicy.Evaluate(metadata,
                table.GetColumnAttributeNames(property).Cast<string?>(),
                attribute => table.GetColumnAttribute(property, attribute),
                () => context.Part.IsAcquisitionProperty(context.ClassName, property),
                () => context.Part.GetAcquisitionStatus(context.RowId, property)?.ToString(),
                () => TagPermission(context, value), PlantProjectTagAccess.Error);
            if (!field.Allowed) return field;
            return PnPropertyPolicy.Permission(new PnPermissionFacts(
                MetadataKnown: true, DrawingWritable: !document.IsReadOnly, ProjectWritable: !context.Part.ReadOnly,
                Enlisted: context.Links.IsDrawingEnlistedAsReadWrite(document.Database), LayerLocked: locked,
                Busy: busy, SpecialAllowed: true));
        }

        /// <summary>在 CAD 主线程解析完整继承列；调用方仍从实际类别读取有效属性约束。</summary>
        /// <remarks>Columns 仅含本类定义，AllColumns 包含父类及扩展列；缺失返回 null，由调用方拒绝写入。</remarks>
        /// <param name="table">目标的实际类别表。</param>
        /// <param name="property">目标自身字段的内部属性名称。</param>
        /// <returns>本类、父类或扩展列；缺失返回 null，SDK 读取异常交由调用方报告。</returns>
        private static PnPColumn? FindEffectiveColumn(PnPTable table, string property)
        {
            var columns = table.AllColumns;
            return columns.Contains(property) ? columns[property] : null;
        }

        /// <summary>仅 Tag 需要格式及登记；普通 Comment 等字段不依赖此流程。</summary>
        private PnEditPermission TagPermission(PlantTagContext context, string? value)
        {
            var table = context.Links.GetPnPDatabase().Tables[context.ClassName];
            if (context.PartName != "PnId" || table.IsKindOf("PipeLines") || table.IsKindOf("PipeLineGroup")
                || string.IsNullOrWhiteSpace(value) || value.StartsWith('?'))
                return PnEditPermission.ReadOnly("Tag 仅允许有格式及注册表的 PnId 普通元件；管线／组／Piping 或临时位号请使用专用命令。");
            string key = context.PartName + ":" + context.ClassName;
            if (!_formats.TryGetValue(key, out var format)) _formats[key] = format = PlantTagFormatAccess.ReadFormat(context);
            if (format == null || !FormatStringUtils.IsValid(format.Expression))
                return PnEditPermission.ReadOnly("未确认有效位号格式，Tag 保持只读。");
            string registry = context.Part.GetTagRegistryName(context.ClassName, "Tag") ?? "";
            return registry.Length != 0 && context.Links.TagRegistryNames.Contains(registry)
                ? PnEditPermission.Writable : PnEditPermission.ReadOnly("未确认位号注册表，Tag 保持只读。");
        }

        /// <summary>只读检查新值类型、长度及 Tag 登记，不调用 SetProperties。</summary>
        public PnPropertyWritePlan<ObjectId> Prepare(ObjectId id, Document document, PnPropertySpec spec, string? original, string value)
        {
            try { return PrepareCore(id, document, spec, original, value).Plan; }
            catch (System.Exception ex) { throw new InvalidOperationException(PlantProjectTagAccess.Error(ex), ex); }
        }

        /// <summary>预检与实际写入共用完整的逐目标校验，继承列通过 AllColumns 解析。</summary>
        private (PlantTagContext Context, string Property, PnPropertyWritePlan<ObjectId> Plan) PrepareCore(
            ObjectId id, Document document, PnPropertySpec spec, string? original, string value)
        {
            if (!IsCurrent) throw new InvalidOperationException("当前 Plant 项目已变化。");
            string path = PnPropertyPolicy.Normalize(spec.Input, out string reason) ?? throw new InvalidOperationException(reason);
            string property = path[13..];
            var context = PlantTagFormatAccess.Locate(id) ?? throw new InvalidOperationException("Plant 关联已失效。");
            using (var read = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var fresh = Read(id, document, read, [spec]).Cells[0];
                if (!fresh.Permission.Allowed) throw new InvalidOperationException(fresh.Permission.Reason);
                if (fresh.Value.Kind != FastValueKind.Value || fresh.Value.Raw != original)
                    throw new InvalidOperationException("写入前属性原值已变化。");
            }
            var table = context.Links.GetPnPDatabase().Tables[context.ClassName];
            var column = FindEffectiveColumn(table, property)
                ?? throw new InvalidOperationException("字段没有可核对的 SDK 列元数据：" + property);
            if (column.ColumnType == typeof(string))
            {
                if (column.Length > 0 && value.Length > column.Length) throw new InvalidOperationException("输入超过字段允许长度。");
            }
            else _ = TypeDescriptor.GetConverter(column.ColumnType).ConvertFromString(null, CultureInfo.CurrentCulture, value);
            PnPropertyRegistration? registration = null;
            if (property.Equals("Tag", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith('?')) throw new InvalidOperationException("不能在面板中写入空或临时位号。");
                string registry = context.Part.GetTagRegistryName(context.ClassName, "Tag");
                string candidate = context.Links.GetCandidateTag(registry, context.ClassName, "Tag", context.RowId, value);
                if (string.IsNullOrEmpty(candidate)) throw new InvalidOperationException("位号注册表没有返回有效候选键。");
                int owner = context.Links.FindRowByTag(registry, candidate);
                if (owner >= 0 && owner != context.RowId) throw new InvalidOperationException("位号冲突：已由数据行 " + owner + " 占用。");
                registration = new(context.PartName, registry, candidate);
            }
            return (context, property, new(id, context.PartName + ":" + context.RowId + ":" + context.ClassName,
                original, value, registration) { ValueType = column.ColumnType, ValueCulture = CultureInfo.CurrentCulture.Name });
        }

        /// <summary>写入前再次验证，立即回读及核对 Tag 登记；整批结束后由协调器统一 Regen。</summary>
        public string Write(ObjectId id, Document document, PnPropertySpec spec, string? original, string value, Action<string> validate)
        {
            try { return WriteCore(id, document, spec, original, value, validate); }
            catch (System.Exception ex) { throw new InvalidOperationException(PlantProjectTagAccess.Error(ex), ex); }
        }

        /// <summary>修改后异常保留实际回读，不承诺 DWG 事务已回滚 Plant 缓存。</summary>
        private string WriteCore(ObjectId id, Document document, PnPropertySpec spec, string? original, string value, Action<string> validate)
        {
            var prepared = PrepareCore(id, document, spec, original, value);
            var context = prepared.Context;
            string property = prepared.Property;
            try
            {
                if (ReadValue(context, property) != original) throw new InvalidOperationException("写入前原值已变化。");
                validate(prepared.Plan.DataKey);
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    context.Links.SetProperties(id, new StringCollection { property }, new StringCollection { value });
                    transaction.Commit();
                }
                string? actual = ReadValue(context, property);
                if (!PnPropertyValuePolicy.Equivalent(prepared.Plan, actual)) throw new InvalidOperationException("回读不符，实际值：" + FastPropertyValue.Value(actual).Display);
                if (prepared.Plan.Registration is { } registration
                    && context.Links.FindRowByTag(registration.Registry, registration.Candidate) != context.RowId)
                    throw new InvalidOperationException("实际 Tag 已回读，但无法确认位号登记归属。");
                return actual!; // 已按列类型或严格文本核对，保留非 null 的 SDK 实际值。
            }
            catch (System.Exception ex)
            {
                string actual;
                try { actual = FastPropertyValue.Value(ReadValue(context, property)).Display; }
                catch (System.Exception read) { actual = "无法回读：" + PlantProjectTagAccess.Error(read); }
                throw new InvalidOperationException(PlantProjectTagAccess.Error(ex) + "；实际值：" + actual
                    + "；不假定已回滚，请核对图纸和项目数据。", ex);
            }
        }

        /// <summary>读取指定自身列并保留 SDK 返回的 null；不展开关联或把空值改成显示标记。</summary>
        private static string? ReadValue(PlantTagContext context, string property)
        {
            var values = context.Links.GetProperties(context.RowId, new StringCollection { property }, true);
            if (values.Count != 1) throw new InvalidOperationException("SDK 未返回属性值：" + property);
            return values[0];
        }

        private static PnObjectProperties Missing(IReadOnlyList<PnPropertySpec> specs, string reason, string label = "所选对象") =>
            new("", label, specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Missing(reason), PnEditPermission.ReadOnly(reason))).ToArray());
    }
}
