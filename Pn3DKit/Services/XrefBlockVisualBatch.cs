using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>串行消费绘制结果、压缩代表并按可靠显示效果归组；不依赖 Autodesk。</summary>
public static class XrefBlockVisualBatch
{
    /// <summary>每个候选完成报告刷新后才登记完成；取消和致命设备／输出错误直接传播。</summary>
    public static void Run(IReadOnlyList<XrefBlockCandidate> candidates,
        Func<XrefBlockCandidate, IEnumerable<XrefVisualCapture>> capture, Func<bool> scopeComplete, Action ensure,
        Action<XrefBlockReportResult> append, List<XrefBlockReportResult> completed,
        Action<int, int, string>? progress = null, Action<VisualDifference>? difference = null, VisualBatchMetrics? metrics = null)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            ensure(); var candidate = candidates[i]; progress?.Invoke(i + 1, candidates.Count, candidate.Name);
            var result = Group(candidate, capture(candidate), scopeComplete, ensure, difference, metrics);
            ensure(); append(result); completed.Add(result);
        }
    }

    /// <summary>未知来源不进入内容组；两组可靠结果不同优先报告不匹配，即使另有未知来源。</summary>
    public static XrefBlockReportResult Group(XrefBlockCandidate candidate, IEnumerable<XrefVisualCapture> captures,
        Func<bool> scopeComplete, Action ensure, Action<VisualDifference>? difference = null,
        VisualBatchMetrics? metrics = null, Func<VisualSample, string>? fingerprint = null)
    {
        IEnumerator<XrefVisualCapture>? iterator = null;
        return XrefVisualCleanup.Run(() =>
        {
            iterator = captures.GetEnumerator();
            return GroupCore(candidate, iterator, scopeComplete, ensure, difference, metrics ?? new(), fingerprint ?? CompressedVisualSample.FingerprintOf);
        }, () => iterator?.Dispose(), "消费绘制结果及释放候选资源时失败。");
    }

    /// <summary>不自行关闭迭代器，交由调用方合并消费异常和释放异常。</summary>
    private static XrefBlockReportResult GroupCore(XrefBlockCandidate candidate, IEnumerator<XrefVisualCapture> captures,
        Func<bool> scopeComplete, Action ensure, Action<VisualDifference>? difference, VisualBatchMetrics metrics, Func<VisualSample, string> fingerprint)
    {
        var groups = new List<(CompressedVisualSample Sample, List<XrefBlockNameEntry> Entries)>();
        var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        VisualSample? firstPixels = null;
        var problems = new List<string>(); var differences = new List<string>();
        var received = new HashSet<int>();
        while (captures.MoveNext())
        {
            var capture = captures.Current;
            ensure(); var entry = capture.Entry;
            if (!candidate.Entries.Any(e => e.Source.DwgNo == entry.Source.DwgNo && e.Source.Key == entry.Source.Key)
                || !received.Add(entry.Source.DwgNo))
                throw new InvalidOperationException("绘制结果来源不属于候选或重复提交。");
            var sample = capture.Sample;
            var valid = sample == null ? new VisualPairResult(VisualState.Unverified, capture.Problem ?? "未取得绘制结果。")
                : BlockVisualComparison.Compare(sample, sample);
            if (sample == null || sample.DwgNo != entry.Source.DwgNo || valid.State != VisualState.Matched)
            {
                problems.Add("dwgNo=" + entry.Source.DwgNo + " " + entry.Source.Drawing + "：" + (capture.Problem ?? valid.Reason));
                continue;
            }
            string digest = fingerprint(sample); metrics.Fingerprints++;
            int match = -1;
            if (buckets.TryGetValue(digest, out var bucket))
                foreach (int index in bucket)
                {
                    ensure(); metrics.PixelComparisons++;
                    if (groups[index].Sample.Matches(sample)) { match = index; break; }
                }
            if (match >= 0) { groups[match].Entries.Add(entry); continue; }
            if (groups.Count > 0)
            {
                var first = groups[0];
                if (firstPixels == null) { firstPixels = first.Sample.Read(); metrics.ExpandedRepresentatives++; }
                var original = firstPixels;
                var comparison = BlockVisualComparison.Compare(original, sample);
                if (comparison.State != VisualState.Mismatched)
                {
                    problems.Add("dwgNo=" + entry.Source.DwgNo + "：无法可靠区分内容组：" + comparison.Reason);
                    continue;
                }
                string detail = "G1（dwgNo=" + first.Entries[0].Source.DwgNo + "）→G" + (groups.Count + 1)
                    + "（dwgNo=" + entry.Source.DwgNo + "）：" + comparison.Reason;
                differences.Add(detail);
                ensure(); difference?.Invoke(new(candidate.Name, groups.Count + 1, original, sample, first.Entries[0], entry, detail));
            }
            var compressed = new CompressedVisualSample(sample, digest); metrics.Representatives++;
            if (!buckets.TryGetValue(digest, out var indices)) buckets[digest] = indices = [];
            indices.Add(groups.Count);
            groups.Add((compressed, [entry]));
        }
        foreach (var entry in candidate.Entries.Where(e => !received.Contains(e.Source.DwgNo)))
            problems.Add("dwgNo=" + entry.Source.DwgNo + "：未取得此来源的绘制结果。");
        bool complete = scopeComplete() && problems.Count == 0 && received.Count == candidate.Entries.Count;
        if (!scopeComplete()) problems.Add("外参来源范围不完整，只能描述已读取范围。");
        var status = groups.Count > 1 ? XrefBlockComparisonStatus.Different
            : complete && groups.Count == 1 ? XrefBlockComparisonStatus.Consistent : XrefBlockComparisonStatus.Incomplete;
        return new(candidate.Name, status, complete, candidate.Entries,
            groups.Select((g, i) => new XrefBlockReportGroup(i + 1, g.Entries.OrderBy(e => e.Source.DwgNo).ToArray())).ToArray(),
            differences, problems);
    }

    /// <summary>中断后仅从内存生成剩余名称的未验证行，不触发绘制或旧快照读取。</summary>
    public static IEnumerable<XrefBlockReportResult> Remaining(IReadOnlyList<XrefBlockCandidate> candidates,
        IReadOnlyList<XrefBlockReportResult> completed, string reason)
    {
        var names = completed.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(c => !names.Contains(c.Name)).Select(c => new XrefBlockReportResult(c.Name,
            XrefBlockComparisonStatus.NotExecuted, false, c.Entries, [], [], [reason]));
    }
}

/// <summary>设备状态未知或资源释放失败必须停止批次，不能作为单个来源问题继续。</summary>
public sealed class XrefVisualFatalException(string message, Exception? inner = null) : Exception(message, inner);
