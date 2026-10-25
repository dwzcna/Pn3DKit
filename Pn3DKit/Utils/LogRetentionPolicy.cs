using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>根据结构化最终结果决定日志保留默认值；取消询问永远保留。</summary>
public static class LogRetentionPolicy
{
    /// <summary>只有明确 N 或在默认 N 时回车才删除；null 表示取消或无法交互。</summary>
    public static bool Keep(string? input, bool hasIssues) => input == null ? true
        : input.Length == 0 ? hasIssues : !input.Equals("N", StringComparison.OrdinalIgnoreCase);
    /// <summary>六种检查状态中，只有一致属于无异常。</summary>
    public static bool CheckHasIssues(IEnumerable<TagCheckResult> items, int total, bool interrupted = false) =>
        interrupted || items.Count() != total || items.Any(x => x.Status != TagCheckStatus.Consistent);
    /// <summary>重建成功或因原值一致而正常跳过为无异常；选择不保存本身不算失败。</summary>
    public static bool RebuildHasIssues(IEnumerable<TagRebuildRecord> records, int total, bool stopped = false)
    {
        var values = records.ToArray();
        return stopped || values.Length != total || values.Any(x => x.Action != TagRebuildAction.Updated
            && !(x.Action == TagRebuildAction.Skipped && x.Item.Check.Status == TagCheckStatus.Consistent
                && x.Item.Exclusion == null));
    }
    /// <summary>导出必须全部成功，跳过或未完成都建议保留报告。</summary>
    public static bool ExportHasIssues(BatchResult result) => result.Canceled || result.StopReason != null
        || result.Items.Count != result.Total || result.Items.Any(x => x.Status != "Exported");
}
