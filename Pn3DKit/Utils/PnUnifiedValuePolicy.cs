namespace Pn3DKit.Utils;

/// <summary>只从完整编辑票据的真实原值建立候选，不解析显示文本或截断后的 Tooltip。</summary>
public static class PnUnifiedValuePolicy
{
    /// <summary>排除 null、空及纯空白后按 Ordinal 全量去重排序，保留原始字符。</summary>
    /// <param name="originals">有效票据内每个目标的真实 Original；调用方先核对上下文。</param>
    /// <returns>不截断、不修剪的候选列表；真实占位同文仍保留。</returns>
    public static IReadOnlyList<string> Options(IEnumerable<string?> originals) => originals
        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
}
