using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>核对同一绘制配置的完整彩色像素和有效外观，不设置像素比例容差。</summary>
public static class BlockVisualComparison
{
    /// <summary>严格比较双背景图像；不完整、无效或多线宽位置未知时不能报告匹配。</summary>
    public static VisualPairResult Compare(VisualSample a, VisualSample b)
    {
        if (a.Frame != b.Frame) return Unknown("取景或绘制配置不一致。");
        var frame = a.Frame;
        if (frame.Pixels <= 0 || frame.Pixels > 4096 || !double.IsFinite(frame.CenterX)
            || !double.IsFinite(frame.CenterY) || !double.IsFinite(frame.Span) || frame.Span <= 0
            || string.IsNullOrWhiteSpace(frame.ProfileId))
            return Unknown("绘制范围无效。");
        int required = checked(frame.Pixels * frame.Pixels * 4);
        bool Valid(byte[]? pixels) => pixels != null && pixels.Length == required;
        if (!a.RasterComplete || !b.RasterComplete || !Valid(a.LightPixels) || !Valid(a.DarkPixels)
            || !Valid(b.LightPixels) || !Valid(b.DarkPixels))
            return Unknown(a.Problem ?? b.Problem ?? "图像未完整取得。");
        if (!a.LightPixels.AsSpan().SequenceEqual(b.LightPixels) || !a.DarkPixels.AsSpan().SequenceEqual(b.DarkPixels))
            return Different("彩色显示结果不同。");
        if (!a.AppearanceComplete || !b.AppearanceComplete || a.EffectiveColors == null || b.EffectiveColors == null
            || a.EffectiveLineWeights == null || b.EffectiveLineWeights == null)
            return Unknown(a.Problem ?? b.Problem ?? "未能完整核对有效颜色和线宽。");
        if (!Sorted(a.EffectiveColors) || !Sorted(b.EffectiveColors)
            || !Sorted(a.EffectiveLineWeights) || !Sorted(b.EffectiveLineWeights))
            return Unknown("有效外观未按数值排序去重。");
        if (!a.EffectiveColors.AsSpan().SequenceEqual(b.EffectiveColors))
            return Different("有效颜色或透明度不同：" + string.Join("/", a.EffectiveColors.Select(c => c.ToString("X8")))
                + " → " + string.Join("/", b.EffectiveColors.Select(c => c.ToString("X8"))) + "。");
        if (!a.EffectiveLineWeights.AsSpan().SequenceEqual(b.EffectiveLineWeights))
            return Different("有效线宽不同：" + string.Join("/", a.EffectiveLineWeights)
                + " → " + string.Join("/", b.EffectiveLineWeights) + "（百分之一毫米）。");
        if (a.EffectiveLineWeights.Length > 1)
        {
            if (!a.WeightCodeComplete || !b.WeightCodeComplete || !Valid(a.WeightCodePixels) || !Valid(b.WeightCodePixels))
                return Unknown("未能确认多种线宽的分配位置。");
            if (!a.WeightCodePixels!.AsSpan().SequenceEqual(b.WeightCodePixels!))
                return Different("线宽在图形中的分配不同。");
        }
        return new(VisualState.Matched, "显示及有效外观核对相同。");
    }

    /// <summary>核对同一帧的视图／设备独立完整读回，不使用摘要或差异比例阈值。</summary>
    public static bool CompleteReadback(VisualFrame frame, byte[]? view, byte[]? device)
    {
        if (frame.Pixels <= 0 || frame.Pixels > 4096 || !double.IsFinite(frame.Span) || frame.Span <= 0)
            return false;
        int required = checked(frame.Pixels * frame.Pixels * 4);
        return view != null && device != null && view.Length == required && device.Length == required
            && view.AsSpan().SequenceEqual(device);
    }

    /// <summary>输入集合必须规范化，避免重复或排列差异造成错误结论。</summary>
    private static bool Sorted<T>(T[] values) where T : IComparable<T>
    {
        for (int i = 1; i < values.Length; i++) if (values[i - 1].CompareTo(values[i]) >= 0) return false;
        return true;
    }

    /// <summary>已可靠取得的差异。</summary>
    private static VisualPairResult Different(string reason) => new(VisualState.Mismatched, reason);
    /// <summary>不能证明匹配的输入。</summary>
    private static VisualPairResult Unknown(string reason) => new(VisualState.Unverified, reason);

    /// <summary>从可靠来源范围计算公共正方形视窗；不能逐来源独立缩放。</summary>
    public static VisualFrame Frame(IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> ranges,
        int pixels = 2048, string profile = "Pn3DKit-WCS-Top-2048-v1")
    {
        var bounds = ranges.ToArray();
        if (bounds.Any(b => !double.IsFinite(b.MinX) || !double.IsFinite(b.MinY) || !double.IsFinite(b.MaxX)
            || !double.IsFinite(b.MaxY) || b.MinX > b.MaxX || b.MinY > b.MaxY))
            throw new ArgumentException("来源范围无效，不能建立公共视窗。");
        if (bounds.Length == 0) return new(0, 0, 1, pixels, profile);
        double minX = bounds.Min(b => b.MinX), minY = bounds.Min(b => b.MinY);
        double maxX = bounds.Max(b => b.MaxX), maxY = bounds.Max(b => b.MaxY);
        double span = Math.Max(maxX - minX, maxY - minY);
        span = span == 0 ? 1 : span * 1.15;
        double x = minX / 2 + maxX / 2, y = minY / 2 + maxY / 2;
        if (!double.IsFinite(span) || !double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentException("公共视窗超出可绘制的数值范围。");
        return new(x, y, span, pixels, profile);
    }
}
