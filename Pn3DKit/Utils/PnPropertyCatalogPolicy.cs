using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>属性目录的名称回退、路径去重及过期检查，不调用 SDK 或写入数据。</summary>
public static class PnPropertyCatalogPolicy
{
    /// <summary>按内部属性名整理自身完整清单；单项显示名称错误保留该项与具体原因。</summary>
    public static IReadOnlyList<PnPropertyCatalogEntry> Build(IEnumerable<string> names,
        Func<string, string?> readDisplayName, Func<Exception, string>? describe = null) =>
        names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x, StringComparer.Ordinal)
            .Select(name =>
            {
                try
                {
                    string? label = readDisplayName(name);
                    return new PnPropertyCatalogEntry(name, string.IsNullOrWhiteSpace(label) ? name : label);
                }
                catch (Exception ex)
                {
                    return new PnPropertyCatalogEntry(name, name, "显示名称读取失败：" + (describe?.Invoke(ex) ?? ex.Message));
                }
            }).ToArray();

    /// <summary>规范化现有简写与完整路径，保留用户原有行及顺序，只返回缺失项的简写。</summary>
    public static IReadOnlyList<string> MissingPaths(IEnumerable<string> existing, IEnumerable<string> selected)
    {
        var seen = new HashSet<string>(existing.Select(x => PnPropertyPolicy.Normalize(x, out _))
            .OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string input in selected)
        {
            string? path = PnPropertyPolicy.Normalize(input, out _);
            if (path != null && seen.Add(path)) result.Add(path[13..]);
        }
        return result;
    }

    /// <summary>查询和确认追加前分别校验完整身份、轮次、关联及单选，拒绝旧窗口结果。</summary>
    public static string? Validate<TIdentity, TTarget>(PnPropertyCatalogContext<TIdentity, TTarget> expected,
        PnPropertyCatalogContext<TIdentity, TTarget> current, bool projectCurrent, bool busy, bool visible)
    {
        if (!visible) return "面板已关闭或隐藏。";
        if (busy) return "CAD 或 Pn3DKit 批次忙碌，请空闲后重新检索。";
        if (!projectCurrent) return "当前 Plant 项目已变化，请重新检索。";
        if (!EqualityComparer<TIdentity>.Default.Equals(expected.Identity, current.Identity)) return "图纸身份已变化，检索结果已过期。";
        if (expected.SelectionCount != 1 || current.SelectionCount != 1
            || !EqualityComparer<TTarget>.Default.Equals(expected.Target, current.Target)) return "选择对象已变化，请重新检索。";
        if (expected.Epoch != current.Epoch) return "对象或属性行已变化，检索结果已过期。";
        if (expected.TargetKey != current.TargetKey) return "Plant 数据关联已变化，请重新检索。";
        return null;
    }
}
