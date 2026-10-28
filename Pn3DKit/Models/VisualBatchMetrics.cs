namespace Pn3DKit.Models;

/// <summary>串行视觉批次的实际工作量，不用于替代比较结果。</summary>
public sealed class VisualBatchMetrics
{
    /// <summary>完成摘要计算的可靠来源数。</summary>
    public int Fingerprints { get; internal set; }
    /// <summary>实际无损压缩的新组代表数。</summary>
    public int Representatives { get; internal set; }
    /// <summary>摘要命中后执行的完整像素核对次数。</summary>
    public int PixelComparisons { get; internal set; }
    /// <summary>为了组间差异实际展开的首组代表数。</summary>
    public int ExpandedRepresentatives { get; internal set; }
}
