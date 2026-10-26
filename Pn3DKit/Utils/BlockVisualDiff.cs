using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>生成并列红／绿差异图像；相同区域灰显，不依赖 CAD 或图像库。</summary>
public static class BlockVisualDiff
{
    /// <summary>左侧第一组的变化为红色，右侧对照组变化为绿色；双背景变化均参与。</summary>
    public static byte[] Create(VisualSample first, VisualSample other)
    {
        int pixels = first.Frame.Pixels, required = checked(pixels * pixels * 4);
        if (first.Frame != other.Frame || first.LightPixels.Length != required || other.LightPixels.Length != required
            || first.DarkPixels.Length != required || other.DarkPixels.Length != required)
            throw new ArgumentException("差异图需要同一视窗的完整双背景图像。");
        var output = new byte[checked(required * 2)];
        for (int y = 0; y < pixels; y++)
            for (int x = 0; x < pixels; x++)
            {
                int input = (y * pixels + x) * 4, left = (y * pixels * 2 + x) * 4, right = left + pixels * 4;
                bool same = first.LightPixels.AsSpan(input, 4).SequenceEqual(other.LightPixels.AsSpan(input, 4))
                    && first.DarkPixels.AsSpan(input, 4).SequenceEqual(other.DarkPixels.AsSpan(input, 4));
                byte gray = (byte)((first.LightPixels[input] + first.LightPixels[input + 1] + first.LightPixels[input + 2]) / 3);
                if (same) { output[left] = output[left + 1] = output[left + 2] = gray;
                    output[right] = output[right + 1] = output[right + 2] = gray; }
                else { output[left] = 45; output[left + 1] = 45; output[left + 2] = 220;
                    output[right] = 65; output[right + 1] = 170; output[right + 2] = 25; }
                output[left + 3] = output[right + 3] = 255;
            }
        return output;
    }
}
