namespace Pn3DKit.Models;

/// <summary>一个候选名称组共用的 WCS 俯视正交取景；像素及配置也是比较条件。</summary>
public sealed record VisualFrame(double CenterX, double CenterY, double Span, int Pixels, string ProfileId);

/// <summary>无行填充、从上到下的 BGRA32 图像；有效外观数组须排序去重。</summary>
public sealed record VisualSample(int DwgNo, VisualFrame Frame, byte[] LightPixels, byte[] DarkPixels,
    uint[] EffectiveColors, int[] EffectiveLineWeights, bool RasterComplete, bool AppearanceComplete,
    byte[]? WeightCodePixels, bool WeightCodeComplete, string? Problem);

/// <summary>视觉比较只提供匹配、不匹配及未验证。</summary>
public enum VisualState { Matched, Mismatched, Unverified }

/// <summary>比较结论和可审计的原因，不包含 CAD 对象。</summary>
public sealed record VisualPairResult(VisualState State, string Reason)
{
    /// <summary>Result.csv 使用的固定结果文字。</summary>
    public string ResultText => State switch
    {
        VisualState.Matched => "匹配", VisualState.Mismatched => "不匹配", _ => "未验证"
    };
}

/// <summary>来源的一次绘制交付；错误来源不使用空图像冒充成功。</summary>
public sealed record XrefVisualCapture(XrefBlockNameEntry Entry, VisualSample? Sample, string? Problem = null);

/// <summary>与具体比较算法无关的来源分组，未知来源不能进入一致组。</summary>
public sealed record XrefBlockReportGroup(int Number, IReadOnlyList<XrefBlockNameEntry> Entries);

/// <summary>报告 DTO 独立于旧字段快照，保留范围完整性及所有来源。</summary>
public sealed record XrefBlockReportResult(string Name, XrefBlockComparisonStatus Status, bool Complete,
    IReadOnlyList<XrefBlockNameEntry> Entries, IReadOnlyList<XrefBlockReportGroup> Groups,
    IReadOnlyList<string> Differences, IReadOnlyList<string> Problems)
{
    /// <summary>已确认差异优先；未知内容绝不证明匹配。</summary>
    public string ResultText => Status == XrefBlockComparisonStatus.Different ? "不匹配"
        : Complete && Status == XrefBlockComparisonStatus.Consistent ? "匹配" : "未验证";
    /// <summary>详细状态只用于日志。</summary>
    public string StatusText => Status switch
    {
        XrefBlockComparisonStatus.Consistent => "一致",
        XrefBlockComparisonStatus.Different => "不一致",
        XrefBlockComparisonStatus.ReadError => "读取错误",
        XrefBlockComparisonStatus.NotExecuted => "未执行",
        _ => "无法完整比较"
    };
    /// <summary>任何差异或不完整情况默认保留报告。</summary>
    public bool HasIssues => !Complete || Status != XrefBlockComparisonStatus.Consistent;
}

/// <summary>新发现的内容组与第一组代表的差异，调用方同步处理后即可释放原始像素。</summary>
public sealed record VisualDifference(string Name, int OtherGroup, VisualSample First, VisualSample Other,
    XrefBlockNameEntry FirstSource, XrefBlockNameEntry OtherSource, string Reason);
