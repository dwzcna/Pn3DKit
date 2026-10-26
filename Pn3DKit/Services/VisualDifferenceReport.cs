using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;

namespace Pn3DKit.Services;

/// <summary>同步创建受日志清单管理的无损差异 PNG；任何故障作为批次输出故障传播。</summary>
public static class VisualDifferenceReport
{
    /// <summary>仅保存不同组与第一组代表的差异；文件使用完整 GUID 且绝不覆盖已有文件。</summary>
    public static string Write(string directory, RunLogFiles files, VisualDifference difference)
    {
        string name = "VisualDiff__" + Guid.NewGuid().ToString("N") + "__G1-G" + difference.OtherGroup + ".png";
        string path = Path.Combine(directory, name);
        try
        {
            int size = difference.First.Frame.Pixels, width = checked(size * 2);
            var pixels = BlockVisualDiff.Create(difference.First, difference.Other);
            var bitmap = BitmapSource.Create(width, size, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            var visual = new DrawingVisual();
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, size + 120));
                draw.DrawImage(bitmap, new Rect(0, 60, width, size));
                var typeface = new Typeface("Microsoft YaHei UI");
                void Text(string text, double y, double font) => draw.DrawText(new FormattedText(text,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, font, Brushes.Black, 1)
                    { MaxTextWidth = width - 24, MaxTextHeight = 50, Trimming = TextTrimming.CharacterEllipsis }, new Point(12, y));
                Text(difference.Name + " | G1 dwgNo=" + difference.FirstSource.Source.DwgNo + "（左：红）"
                    + " / G" + difference.OtherGroup + " dwgNo=" + difference.OtherSource.Source.DwgNo + "（右：绿）", 10, 22);
                Text("相同区域：灰色；两侧变化：红／绿。 " + difference.Reason, size + 66, 17);
            }
            var target = new RenderTargetBitmap(width, size + 120, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var output = files.CreateImageReport(path);
            encoder.Save(output); output.Flush(true);
            return path;
        }
        catch (Exception ex) { throw new XrefBlockLogException("创建视觉差异图失败：" + path + "；" + ex.Message, ex); }
    }
}
