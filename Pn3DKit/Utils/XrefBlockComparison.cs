using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>按名称定位跨来源普通块候选，内容由视觉流程判定。</summary>
public static class XrefBlockComparison
{
    /// <summary>只从本次内存统计筛选重复普通名称，不回读 CSV。</summary>
    public static IReadOnlyList<XrefBlockCandidate> Candidates(XrefBlockInventory inventory, XrefBlockStatistics statistics)
    {
        var byName = inventory.Entries.Where(e => !e.IsAnonymous).GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.DistinctBy(e => e.Source.DwgNo).OrderBy(e => e.Source.DwgNo).ToArray(), StringComparer.OrdinalIgnoreCase);
        return statistics.Normal.Where(r => r.DwgCount > 1).Select(r =>
        {
            var entries = byName[r.Name];
            if (!entries.Select(e => e.Source.DwgNo).SequenceEqual(r.DwgNumbers))
                throw new InvalidOperationException("候选来源与名称统计不一致：" + r.Name);
            return new XrefBlockCandidate(r.Name, entries);
        }).ToArray();
    }

}
