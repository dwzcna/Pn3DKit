using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>串行处理已筛选候选；检查点及报告故障直接传播，已完成清单由调用方保留。</summary>
public static class XrefBlockComparisonBatch
{
    /// <summary>每个候选来源仅读一次根，完成归组且落盘后才登记为完成。</summary>
    public static void Run(IReadOnlyList<XrefBlockCandidate> candidates, Func<XrefBlockNameEntry, XrefComparedDefinition> read,
        Func<bool> scopeComplete, Action ensure, Action<XrefBlockComparisonResult> append,
        List<XrefBlockComparisonResult> completed, Action<int, int, string>? progress = null)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            ensure();
            var candidate = candidates[index];
            progress?.Invoke(index + 1, candidates.Count, candidate.Name);
            var definitions = new List<XrefComparedDefinition>();
            foreach (var entry in candidate.Entries) { ensure(); definitions.Add(read(entry)); }
            var result = XrefBlockComparison.Group(candidate, definitions, scopeComplete(), ensure);
            ensure();
            append(result);
            completed.Add(result);
        }
    }

    /// <summary>中断后只用内存生成未执行项，不重新查询数据库或修改已完成结果。</summary>
    public static IEnumerable<XrefBlockComparisonResult> Remaining(IReadOnlyList<XrefBlockCandidate> candidates,
        IReadOnlyList<XrefBlockComparisonResult> completed, string reason)
    {
        var names = completed.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(c => !names.Contains(c.Name)).Select(c => XrefBlockComparison.NotExecuted(c, reason));
    }
}
