using System.Text.RegularExpressions;
using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>自定义 Plant 属性路径、共同值、权限及写前快照的离线策略。</summary>
public static class PnPropertyPolicy
{
    /// <summary>仅接受自身属性，简写补 TargetObject；不支持关系、表达式和通配符。</summary>
    public static string? Normalize(string input, out string reason)
    {
        string value = input.Trim();
        if (value.StartsWith("TargetObject.", StringComparison.OrdinalIgnoreCase)) value = value[13..];
        if (value.Length == 0) { reason = "请输入属性名称，例如 Size 或 TargetObject.Size。"; return null; }
        if (!Regex.IsMatch(value, @"^[\p{L}_][\p{L}\p{Nd}_]*$"))
        { reason = "仅支持属性名称或 TargetObject.属性名称，不支持其他上下文、表达式或通配符。"; return null; }
        reason = ""; return "TargetObject." + value;
    }

    /// <summary>面板文本焦点本身不是 CAD 命令；在读锁外固定此状态，事务中不重复查询。</summary>
    public static bool IsBusy(bool batchRunning, bool commandRunning, bool quiescent, bool panelInputFocused) =>
        batchRunning || commandRunning || (!quiescent && !panelInputFocused);

    /// <summary>所有元数据与对象状态都满足时才允许单对象写入。</summary>
    public static PnEditPermission Permission(PnPermissionFacts facts)
    {
        string? reason = !facts.Single ? "多选仅显示共同值，不开放批量编辑。"
            : facts.Busy ? "CAD 或 Pn3DKit 批次忙碌，暂不可编辑。"
            : !facts.MetadataKnown ? "无法确认写权限：" + facts.UnknownReason
            : facts.FieldReadOnly ? "SDK 字段为只读。"
            : facts.System ? "系统维护字段，不允许直接修改。"
            : facts.Automatic ? "自动生成或计数字段，不生成或改写编号。"
            : facts.Expression ? "表达式或计算字段，不允许直接修改。"
            : facts.Acquisition ? "获取属性：保持获取关系，不改写来源对象。"
            : !facts.DrawingWritable ? "图纸为只读或不可写。"
            : !facts.ProjectWritable ? "项目为只读或不可写。"
            : !facts.Enlisted ? "图纸未以读写方式加入项目。"
            : facts.LayerLocked ? "对象图层已锁定或无法确认锁定状态。"
            : !facts.SpecialAllowed ? (string.IsNullOrEmpty(facts.UnknownReason) ? "该字段的安全写入流程未确认，保持只读。" : facts.UnknownReason)
            : null;
        return reason == null ? PnEditPermission.Writable : PnEditPermission.ReadOnly(reason);
    }

    /// <summary>合并完整选择，属性不适用于所有对象时不冒充共同值。</summary>
    public static PnPanelSnapshot Merge(IReadOnlyList<PnPropertySpec> specs, IReadOnlyList<PnObjectProperties> objects)
    {
        if (objects.Count == 0) return Empty(specs, "只读 · 未选择对象");
        if (objects.Any(x => x.Cells.Count != specs.Count)) throw new ArgumentException("属性行数量不一致。");
        var cells = specs.Select((spec, index) =>
        {
            if (objects.Any(x => x.Cells[index].Spec != spec)) throw new ArgumentException("属性行身份或修订号不一致。");
            var value = FastPropertyPolicy.MergeValues(objects.Select(x => x.Cells[index].Value).ToArray());
            var permission = objects.Count == 1 ? objects[0].Cells[index].Permission
                : PnEditPermission.ReadOnly("多选仅显示共同值，不开放批量编辑。");
            return new PnPropertyCell(spec, value, permission);
        }).ToArray();
        string type = objects.All(x => x.TypeKey == objects[0].TypeKey) ? objects[0].TypeLabel : "多种对象";
        string header = type + (objects.Count == 1 ? "" : $"（{objects.Count} 个对象）");
        return new(header, objects.Count, cells, $"已选 {objects.Count} 个对象 · "
            + (cells.Any(x => x.Permission.Allowed) ? "可编辑项回车或失焦提交" : "只读")
            + (cells.Any(x => x.Value.Kind == FastValueKind.Error) ? " · 部分属性读取失败" : ""));
    }

    /// <summary>空选择／暂停时仍保留属性输入行，值不从旧对象继承。</summary>
    public static PnPanelSnapshot Empty(IReadOnlyList<PnPropertySpec> specs, string status, string header = "未选择对象") =>
        new(header, 0, specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Missing(), PnEditPermission.ReadOnly(status))).ToArray(), status);

    /// <summary>写前全部身份、路径、原值和权限相同才允许继续，返回具体拒绝原因。</summary>
    public static string? Validate<TIdentity, TTarget>(PnEditContext<TIdentity, TTarget> expected, PnEditContext<TIdentity, TTarget> current)
    {
        if (!EqualityComparer<TIdentity>.Default.Equals(expected.Identity, current.Identity)) return "图纸身份已变化，编辑已过期。";
        if (!EqualityComparer<TTarget>.Default.Equals(expected.Target, current.Target) || expected.SelectionCount != 1 || current.SelectionCount != 1) return "选择对象已变化，编辑已过期。";
        if (expected.Epoch != current.Epoch) return "选择或对象状态已变化，请刷新后重试。";
        if (expected.RowId != current.RowId || expected.RowRevision != current.RowRevision || expected.Path != current.Path) return "属性路径已修改，旧值不可提交。";
        if (expected.TargetKey != current.TargetKey) return "Plant 数据关联或类别已变化。";
        if (!string.Equals(expected.Original, current.Original, StringComparison.Ordinal)) return "属性原值已变化，请刷新后重试。";
        if (!expected.Permission.Allowed) return "读取快照未授予写权限。";
        if (!current.Permission.Allowed) return current.Permission.Reason;
        return null;
    }
}
