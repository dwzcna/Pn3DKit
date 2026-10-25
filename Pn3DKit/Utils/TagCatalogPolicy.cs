using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>保存一次批量读取的实际类别和组成员，不依赖图形包装对象。</summary>
public sealed record TagCatalogRow(int RowId, string ClassName, TagTargetKind Kind, IReadOnlyList<int> Members);
/// <summary>返回范围内的真实数据行和无法归组的诊断。</summary>
public sealed record TagCatalogSelection(IReadOnlyList<TagCatalogRow> Rows, IReadOnlyList<string> Warnings);

/// <summary>以数据行集合限定模型空间或项目范围，管线组通过成员关系纳入。</summary>
public static class TagCatalogPolicy
{
    /// <summary>SDK 的 dbHandle 是整数，必须转换为 CAD 使用的十六进制，不能按十进制匹配。</summary>
    public static string FormatHandle(long handle) => unchecked((ulong)handle).ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>保留具体类别，同组只计一次；缺失组关系不伪造目标或自动创建组。</summary>
    public static TagCatalogSelection Select(IEnumerable<TagCatalogRow> source, IReadOnlySet<int> scope)
    {
        var rows = source.GroupBy(x => x.RowId).Select(g =>
        {
            var first = g.First();
            if (g.Any(x => x.ClassName != first.ClassName || x.Kind != first.Kind))
                throw new InvalidOperationException("同一数据行出现不同实际类别：" + first.RowId);
            return first;
        }).ToArray();
        var direct = rows.Where(x => x.Kind != TagTargetKind.LineGroup && scope.Contains(x.RowId)).ToArray();
        var lineIds = direct.Where(x => x.Kind == TagTargetKind.LineSegment).Select(x => x.RowId).ToHashSet();
        var groups = rows.Where(x => x.Kind == TagTargetKind.LineGroup && x.Members.Any(lineIds.Contains)).ToArray();
        var grouped = groups.SelectMany(x => x.Members).ToHashSet();
        var warnings = lineIds.Where(id => !grouped.Contains(id))
            .Select(id => $"工艺管线数据行 {id} 缺少有效 PipeLineGroupRelationship 关联，未自动创建管线组。").ToArray();
        return new(direct.Concat(groups).OrderBy(x => x.ClassName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RowId).ToArray(), warnings);
    }
}
