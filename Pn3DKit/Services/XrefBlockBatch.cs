using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>比较固定的命名定义清单；中断和报告失败均停止，保留已成功写入的结果。</summary>
public static class XrefBlockBatch
{
    /// <summary>按 CAD 名称规则归并同名候选，内容分组继续严格核对，不受输入顺序影响。</summary>
    public static XrefBlockBatchResult Compare(IReadOnlyList<XrefBlockDefinition> definitions, bool scopeComplete,
        Action ensure, Action<XrefBlockResult> append, Action<int, int> progress)
    {
        var groups = definitions.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new List<XrefBlockResult>();
        bool interrupted = false;
        string? reason = null;
        try
        {
            foreach (var group in groups)
            {
                ensure();
                var result = XrefBlockComparison.Compare(group.Key, group.ToArray(), scopeComplete, ensure);
                append(result);
                results.Add(result);
                if (results.Count % 50 == 0) progress(results.Count, groups.Length);
            }
        }
        catch (OperationCanceledException) { interrupted = true; reason = "用户取消，已中断。"; }
        catch (Exception ex) { reason = ex.Message; }
        try { if (results.Count % 50 != 0 || groups.Length == 0) progress(results.Count, groups.Length); }
        catch (OperationCanceledException) { interrupted = true; reason ??= "用户取消，已中断。"; }
        catch (Exception ex) { reason ??= ex.Message; }
        return new(results, scopeComplete, interrupted, reason, groups.Length);
    }
}
