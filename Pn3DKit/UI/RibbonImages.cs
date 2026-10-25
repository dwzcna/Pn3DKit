using System.Windows;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;

namespace Pn3DKit.UI;

/// <summary>绘制叠放图纸和导出箭头的 WPF 矢量，支持 Ribbon 大小图标。</summary>
/// <remarks>不依赖磁盘图像或额外 DLL；图像冻结后可供宿主在不同 DPI 下复用。</remarks>
internal static class RibbonImages
{
    /// <summary>供大型 Ribbon 按钮使用的 32 DIP 图标。</summary>
    internal static ImageSource Large { get; } = Create(32);
    /// <summary>供紧凑布局使用的 16 DIP 图标。</summary>
    internal static ImageSource Small { get; } = Create(16);

    /// <summary>按现有 SVG 坐标创建指定 DIP 大小的透明矢量图标。</summary>
    /// <param name="size">图标边长，单位为 DIP。</param>
    /// <returns>已冻结的绘图图像。</returns>
    private static DrawingImage Create(double size)
    {
        var drawing = new DrawingGroup { Transform = new ScaleTransform(size / 256, size / 256) };
        SolidColorBrush Brush(string color) =>
            new((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(color));
        using (var context = drawing.Open())
        {
            // 透明边界固定 SVG 视口，避免图纸叠放和箭头引起大小图标对齐偏移。
            context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, 256, 256));
            context.DrawRoundedRectangle(Brush("#153A66"), null, new Rect(25, 51, 145, 174), 15, 15);
            context.DrawRoundedRectangle(Brush("#2367A8"), null, new Rect(46, 31, 145, 174), 15, 15);
            context.DrawRoundedRectangle(Brush("#F0F7FF"), new MediaPen(Brush("#A7D4FF"), 5),
                new Rect(67, 11, 145, 174), 15, 15);
            context.DrawGeometry(null, new MediaPen(Brush("#2367A8"), 7),
                Geometry.Parse("M92,49 L186,49 M88,116 L118,80 L144,105 L168,76 L191,116"));
            context.DrawGeometry(null, new MediaPen(Brush("#6B9BBF"), 6),
                Geometry.Parse("M90,140 L173,140"));
            context.DrawGeometry(Brush("#59D6B3"), new MediaPen(Brush("#146B5C"), 5),
                Geometry.Parse("M118,166 L174,166 L174,143 L237,194 L174,245 L174,220 L118,220 Z"));
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
