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

    /// <summary>PN快速特性大图标，内存绘制的属性面板与两列字段。</summary>
    internal static ImageSource FastPanelLarge { get; } = CreateFastPanel(32);
    /// <summary>PN快速特性小图标，供 Ribbon 缩放使用。</summary>
    internal static ImageSource FastPanelSmall { get; } = CreateFastPanel(16);

    /// <summary>绘制固定边界的可停靠属性面板矢量，无外部图片依赖。</summary>
    private static DrawingImage CreateFastPanel(double size)
    {
        var drawing = new DrawingGroup { Transform = new ScaleTransform(size / 32, size / 32) };
        var blue = new SolidColorBrush(MediaColor.FromRgb(40, 111, 180));
        var mint = new SolidColorBrush(MediaColor.FromRgb(72, 188, 157));
        using (var context = drawing.Open())
        {
            context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, 32, 32));
            context.DrawRoundedRectangle(new SolidColorBrush(MediaColor.FromRgb(237, 246, 255)),
                new MediaPen(blue, 1.5), new Rect(3, 3, 26, 26), 2, 2);
            context.DrawRectangle(blue, null, new Rect(4, 4, 24, 5));
            context.DrawRectangle(mint, null, new Rect(21, 4, 7, 24));
            context.DrawGeometry(null, new MediaPen(blue, 1.5),
                Geometry.Parse("M7,13 L17,13 M7,18 L17,18 M7,23 L17,23 M12,11 L12,26"));
            context.DrawGeometry(null, new MediaPen(System.Windows.Media.Brushes.White, 1.5),
                Geometry.Parse("M23,12 L26,12 M23,17 L26,17 M23,22 L26,22"));
        }
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }

    /// <summary>七个位号图标在内存中绘制并冻结，保持 16 DIP 透明边界。</summary>
    private static readonly IReadOnlyDictionary<string, ImageSource> TagImages =
        new Dictionary<string, ImageSource>
        {
            ["CHECKPNTAG"] = CreateTag("check", false, false),
            ["CHECKPNTAGBYCLASS"] = CreateTag("check", true, false),
            ["CHECKPROJECTPNTAGBYCLASS"] = CreateTag("check", true, true),
            ["EVALUATEPNTAG"] = CreateTag("evaluate", false, false),
            ["GETPNTAGFORMAT"] = CreateTag("format", false, false),
            ["REBUILDPNTAGBYCLASS"] = CreateTag("rebuild", true, false),
            ["REBUILDPROJECTPNTAGBYCLASS"] = CreateTag("rebuild", true, true)
        };

    /// <summary>取得已冻结的图标，不访问外部图片或 SDK。</summary>
    internal static ImageSource ForTagCommand(string command) => TagImages[command];

    /// <summary>纸张、叠页、项目建筑分别表示单对象、批量和项目范围，前景符号区分功能。</summary>
    private static DrawingImage CreateTag(string kind, bool multiple, bool project)
    {
        var group = new DrawingGroup();
        var blue = new SolidColorBrush(MediaColor.FromRgb(44, 108, 171));
        var foreground = new SolidColorBrush(kind == "rebuild" ? MediaColor.FromRgb(220, 133, 35)
            : kind == "evaluate" ? MediaColor.FromRgb(140, 79, 190) : MediaColor.FromRgb(23, 156, 124));
        using (var context = group.Open())
        {
            context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, 16, 16));
            if (project)
            {
                context.DrawGeometry(new SolidColorBrush(MediaColor.FromRgb(219, 237, 252)), new MediaPen(blue, 1),
                    Geometry.Parse("M1,14 L1,5 L5,5 L5,1 L11,1 L11,14 Z"));
                context.DrawGeometry(null, new MediaPen(blue, 1), Geometry.Parse("M7,3 L9,3 M7,5 L9,5 M3,7 L4,7 M3,9 L4,9"));
            }
            else
            {
                if (multiple) context.DrawRoundedRectangle(null, new MediaPen(blue, 1), new Rect(1, 3, 9, 12), 1, 1);
                context.DrawRoundedRectangle(new SolidColorBrush(MediaColor.FromRgb(239, 247, 255)),
                    new MediaPen(blue, 1), new Rect(3, 1, 10, 12), 1, 1);
                context.DrawGeometry(null, new MediaPen(blue, 1), Geometry.Parse("M5,4 L10,4 M5,6 L9,6"));
            }
            string path = kind switch
            {
                "check" => "M6,10 L9,13 L15,7",
                "evaluate" => "M11,6 L7,11 L11,11 L9,15 L15,9 L11,9 Z",
                "format" => "M7,8 L14,8 M6,12 L13,12 M9,6 L7,15 M13,6 L11,15",
                _ => "M7,10 A4,4 0 1 1 10,15 M7,7 L7,11 L11,11"
            };
            context.DrawGeometry(kind == "evaluate" ? foreground : null, new MediaPen(foreground, 1.8), Geometry.Parse(path));
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

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
