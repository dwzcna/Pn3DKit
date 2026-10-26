using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>不依赖 CAD 的名称身份、范围规则及来源去重统计。</summary>
public static class XrefBlockNames
{
    /// <summary>仅删除已确认的本源命名空间，不截取未知名称的最后一段。</summary>
    public static string? RemoveSourceNamespace(string stored, IEnumerable<string> namespaces)
    {
        if (!stored.Contains('|')) return stored;
        foreach (string prefix in namespaces.OrderByDescending(x => x.Length))
            if (stored.StartsWith(prefix + "|", StringComparison.OrdinalIgnoreCase))
                return stored[(prefix.Length + 1)..];
        return null;
    }

    /// <summary>直接外参均纳入；嵌套覆盖不继续向主图传播。</summary>
    public static bool IncludesReference(int parentDepth, bool overlay) => parentDepth == 0 || !overlay;

    /// <summary>按 SDK 匿名标志分表，按 CAD 名称规则去重；回调只用于正常运行时检查取消。</summary>
    /// <remarks>不访问 CAD 或磁盘；停止后的部分统计不传入回调，避免再次查询失效图纸。</remarks>
    public static XrefBlockStatistics Summarize(XrefBlockInventory inventory, Action? checkpoint = null)
    {
        var anonymous = new Dictionary<string, (int First, string Name, HashSet<int> Numbers)>(StringComparer.OrdinalIgnoreCase);
        var normal = new Dictionary<string, (int First, string Name, HashSet<int> Numbers)>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        foreach (var entry in inventory.Entries)
        {
            if (visited++ % 50 == 0) checkpoint?.Invoke();
            int number = entry.Source.DwgNo;
            if (number <= 0) throw new InvalidOperationException("名称条目缺少有效 dwgNo：" + entry.Name);
            var target = entry.IsAnonymous ? anonymous : normal;
            if (!target.TryGetValue(entry.Name, out var group))
                group = (number, entry.Name, []);
            group.Numbers.Add(number);
            if (number < group.First)
            { group.First = number; group.Name = entry.Name; }
            target[entry.Name] = group;
        }
        IReadOnlyList<XrefBlockNameStatus> Rows(Dictionary<string, (int First, string Name, HashSet<int> Numbers)> groups) =>
            groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.Ordinal)
                .Select(g => new XrefBlockNameStatus(g.Name, g.Numbers.Order().ToArray())).ToArray();
        var result = new XrefBlockStatistics(Rows(anonymous), Rows(normal));
        checkpoint?.Invoke();
        return result;
    }
}
