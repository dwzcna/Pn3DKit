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

    private sealed class Reader(bool hasProject, Func<bool> isCurrent, bool busy) : IFastPlantProperties
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlantTagFormat?> _formats = new(StringComparer.Ordinal);
        public bool IsCurrent => isCurrent();

        /// <summary>每轮缓存显示名和格式，权限仍逐对象读取，不将获取字段当可写文本。</summary>
        public PnObjectProperties Read(ObjectId id, Document document, Transaction transaction,
            IReadOnlyList<PnPropertySpec> specs, bool single)
        {
            try { return ReadCore(id, document, transaction, specs, single); }
            catch (System.Exception ex)
            {
                string error = PlantProjectTagAccess.Error(ex);
                return new("", "读取失败", specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Error(error),
                    PnEditPermission.ReadOnly(error))).ToArray());
            }
        }

        /// <summary>在可选 SDK 类型内部保留定位阶段的 DataLinks 状态，普通 CAD 不需加载异常类型。</summary>
        private PnObjectProperties ReadCore(ObjectId id, Document document, Transaction transaction,
            IReadOnlyList<PnPropertySpec> specs, bool single)
        {
            if (!hasProject) return Missing(specs, "没有当前 Plant 项目。");
            var context = PlantTagFormatAccess.Locate(id);
            if (context == null) return Missing(specs, "所选对象不是当前项目中的 Plant 部件。", "非 Plant 对象");
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity) return Missing(specs, "目标不是实体。");
            string key = context.PartName + ":" + context.ClassName;
            if (!_names.TryGetValue(key, out string? name))
                _names[key] = name = PlantTagCatalogAccess.ReadDisplayName(context.Links.GetPnPDatabase(), context.ClassName);
            bool locked = true;
            string? stateError = null;
            try { locked = ((LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked; }
            catch (System.Exception ex) { stateError = PlantProjectTagAccess.Error(ex); }
            var cells = specs.Select(spec => ReadCell(context, spec, document, locked, stateError, single)).ToArray();
            return new(key, name, cells, context.PartName + ":" + context.RowId + ":" + context.ClassName);
        }

        /// <summary>不存在与实际读取失败分开；权限查询失败不掩盖已读取的值。</summary>
        private PnPropertyCell ReadCell(PlantTagContext context, PnPropertySpec spec, Document document, bool locked,
            string? stateError, bool single)
        {
            string? path = PnPropertyPolicy.Normalize(spec.Input, out string reason);
            if (path == null) return new(spec, FastPropertyValue.Missing(reason), PnEditPermission.ReadOnly(reason));
            string property = path[13..];
            try
            {
                if (!context.Links.HasProperty(context.RowId, property))
                    return new(spec, FastPropertyValue.Missing("对象不含属性 " + path), PnEditPermission.ReadOnly("属性不存在。"));
                var value = FastPropertyValue.Value(ReadValue(context, property));
                PnEditPermission permission;
                try { permission = GetPermission(context, property, value.Raw, document, locked, stateError, single); }
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
        private PnEditPermission GetPermission(PlantTagContext context, string property, string value, Document document,
            bool locked, string? stateError, bool single)
        {
            if (!single) return PnEditPermission.ReadOnly("多选仅显示共同值，不开放批量编辑。");
            if (stateError != null) return PnEditPermission.ReadOnly("无法确认图层状态：" + stateError);
            var table = context.Links.GetPnPDatabase().Tables[context.ClassName];
            if (!table.Columns.Contains(property)) return PnEditPermission.ReadOnly("字段没有可核对的 SDK 列元数据。");
            var column = table.Columns[property];
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
                Busy: busy, Single: single, SpecialAllowed: true));
        }

        /// <summary>仅 Tag 需要格式及登记；普通 Comment 等字段不依赖此流程。</summary>
        private PnEditPermission TagPermission(PlantTagContext context, string value)
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

        /// <summary>在锁定文档的命令上下文写自身字段；Tag 先校验登记键，写后核对登记和原值。</summary>
        public string Write(ObjectId id, Document document, PnPropertySpec spec, string original, string value, Action<string> validate)
        {
            try { return WriteCore(id, document, spec, original, value, validate); }
            catch (System.Exception ex) { throw new InvalidOperationException(PlantProjectTagAccess.Error(ex), ex); }
        }

        /// <summary>预检和登记失败也保留 SDK 状态；修改后异常另外说明回读结果。</summary>
        private string WriteCore(ObjectId id, Document document, PnPropertySpec spec, string original, string value, Action<string> validate)
        {
            if (!IsCurrent) throw new InvalidOperationException("当前 Plant 项目已变化。");
            string path = PnPropertyPolicy.Normalize(spec.Input, out string reason) ?? throw new InvalidOperationException(reason);
            string property = path[13..];
            var context = PlantTagFormatAccess.Locate(id) ?? throw new InvalidOperationException("Plant 关联已失效。");
            using (var read = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var fresh = Read(id, document, read, [spec], true).Cells[0];
                if (!fresh.Permission.Allowed) throw new InvalidOperationException(fresh.Permission.Reason);
                if (fresh.Value.Kind != FastValueKind.Value || fresh.Value.Raw != original)
                    throw new InvalidOperationException("写入前属性原值已变化。");
            }
            var column = context.Links.GetPnPDatabase().Tables[context.ClassName].Columns[property];
            if (column.ColumnType == typeof(string))
            {
                if (column.Length > 0 && value.Length > column.Length) throw new InvalidOperationException("输入超过字段允许长度。");
            }
            else _ = TypeDescriptor.GetConverter(column.ColumnType).ConvertFromString(null, CultureInfo.CurrentCulture, value);
            bool tag = property.Equals("Tag", StringComparison.OrdinalIgnoreCase);
            string registry = "", candidate = "";
            if (tag)
            {
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith('?')) throw new InvalidOperationException("不能在面板中写入空或临时位号。");
                registry = context.Part.GetTagRegistryName(context.ClassName, "Tag");
                candidate = context.Links.GetCandidateTag(registry, context.ClassName, "Tag", context.RowId, value);
                if (string.IsNullOrEmpty(candidate)) throw new InvalidOperationException("位号注册表没有返回有效候选键。");
                int owner = context.Links.FindRowByTag(registry, candidate);
                if (owner >= 0 && owner != context.RowId) throw new InvalidOperationException("位号冲突：已由数据行 " + owner + " 占用。");
            }
            try
            {
                if (ReadValue(context, property) != original) throw new InvalidOperationException("写入前原值已变化。");
                validate(context.PartName + ":" + context.RowId + ":" + context.ClassName); // 最后一刻复核身份、选择、路径和轮次，拒绝排队期间的旧编辑。
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    context.Links.SetProperties(id, new StringCollection { property }, new StringCollection { value });
                    transaction.Commit();
                }
                string actual = ReadValue(context, property);
                if (actual != value) throw new InvalidOperationException("回读不符，实际值：" + actual);
                if (tag && context.Links.FindRowByTag(registry, candidate) != context.RowId)
                    throw new InvalidOperationException("实际 Tag 已回读，但无法确认位号登记归属。");
                document.Editor.Regen();
                return actual;
            }
            catch (System.Exception ex)
            {
                string actual;
                try { actual = ReadValue(context, property); }
                catch (System.Exception read) { actual = "无法回读：" + PlantProjectTagAccess.Error(read); }
                throw new InvalidOperationException(PlantProjectTagAccess.Error(ex) + "；实际值：" + actual
                    + "；不假定已回滚，请核对图纸和项目数据。", ex);
            }
        }

        /// <summary>直接读取指定自身列；不将 Tag 隐式改为其他列，不展开关联来源。</summary>
        private static string ReadValue(PlantTagContext context, string property)
        {
            var values = context.Links.GetProperties(context.RowId, new StringCollection { property }, true);
            if (values.Count != 1) throw new InvalidOperationException("SDK 未返回属性值：" + property);
            return values[0] ?? "";
        }

        private static PnObjectProperties Missing(IReadOnlyList<PnPropertySpec> specs, string reason, string label = "所选对象") =>
            new("", label, specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Missing(reason), PnEditPermission.ReadOnly(reason))).ToArray());
    }
}
