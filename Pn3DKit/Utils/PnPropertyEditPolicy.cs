using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>完整选择集合的写前校验；不以选择顺序、文件名或汇总文本识别对象及原值。</summary>
public static class PnPropertyEditPolicy
{
    /// <summary>每个目标都必须保留身份、轮次、行、路径、数据关联、原值和许可。</summary>
    public static string? ValidateSelection<TIdentity, TTarget>(
        IReadOnlyList<PnEditContext<TIdentity, TTarget>> expected,
        IReadOnlyList<PnEditContext<TIdentity, TTarget>> current) where TTarget : notnull
    {
        if (expected.Count == 0 || current.Count != expected.Count) return "选择对象数量已变化，编辑已过期。";
        if (expected.Any(x => x.SelectionCount != expected.Count) || current.Any(x => x.SelectionCount != current.Count))
            return "完整选择快照不一致。";
        if (expected.Select(x => x.Target).Distinct().Count() != expected.Count
            || current.Select(x => x.Target).Distinct().Count() != current.Count) return "选择快照含有重复对象。";
        var indexed = current.ToDictionary(x => x.Target);
        var first = expected[0];
        foreach (var target in expected)
        {
            if (!EqualityComparer<TIdentity>.Default.Equals(target.Identity, first.Identity) || target.Epoch != first.Epoch
                || target.RowId != first.RowId || target.RowRevision != first.RowRevision || target.Path != first.Path)
                return "编辑票据的图纸、轮次或属性行不一致。";
            if (!indexed.TryGetValue(target.Target, out var actual)) return "选择对象已变化，编辑已过期。";
            if (string.IsNullOrEmpty(target.TargetKey) || string.IsNullOrEmpty(actual.TargetKey))
                return "没有可核对的 Plant 数据关联。";
            string? reason = PnPropertyPolicy.Validate(target, actual);
            if (reason != null) return reason;
        }
        return null;
    }
}
