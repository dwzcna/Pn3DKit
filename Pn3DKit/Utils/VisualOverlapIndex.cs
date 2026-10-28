namespace Pn3DKit.Utils;

/// <summary>只用于筛选可能有绘制顺序影响的二维范围，不作为图形一致性证据。</summary>
public readonly record struct VisualDisplayBox(double MinX, double MinY, double MaxX, double MaxY, uint Color, int Weight);

/// <summary>空间筛选实际工作量，供离线与CAD验证记录。</summary>
public sealed class VisualOverlapMetrics
{
    /// <summary>访问的树节点数量。</summary>
    public long Nodes { get; internal set; }
    /// <summary>实际执行的叶级范围核对数量。</summary>
    public long Pairs { get; internal set; }
}

/// <summary>二维平衡范围层次筛选；保持严格范围和外观规则，不设置几何容差。</summary>
public static class VisualOverlapIndex
{
    private readonly record struct Entry(int Index, VisualDisplayBox Box);
    private sealed class Node
    {
        internal VisualDisplayBox Box;
        internal int Start, Count, MaxIndex;
        internal bool Uniform;
        internal Node? Left, Right;
    }

    /// <summary>发现异色或异线宽范围重叠即返回；margin沿用原后端的像素覆盖边界。</summary>
    public static bool HasOverlap(IReadOnlyList<VisualDisplayBox> boxes, double margin, Action ensure,
        VisualOverlapMetrics? metrics = null)
    {
        if (!double.IsFinite(margin) || margin < 0) throw new ArgumentException("遮挡边界无效。", nameof(margin));
        metrics ??= new();
        var entries = boxes.Select((box, index) => new Entry(index, box)).ToArray();
        foreach (var entry in entries)
        {
            var b = entry.Box;
            if (!double.IsFinite(b.MinX) || !double.IsFinite(b.MinY) || !double.IsFinite(b.MaxX)
                || !double.IsFinite(b.MaxY) || b.MinX > b.MaxX || b.MinY > b.MaxY)
                throw new ArgumentException("实体显示范围无效。", nameof(boxes));
        }
        if (entries.Length < 2) return false;
        bool Appearance(VisualDisplayBox a, VisualDisplayBox b) => a.Color == b.Color && a.Weight == b.Weight;
        bool Touch(VisualDisplayBox a, VisualDisplayBox b) =>
            b.MinX <= a.MaxX + margin && b.MaxX >= a.MinX - margin
            && b.MinY <= a.MaxY + margin && b.MaxY >= a.MinY - margin;

        Node Build(int start, int count)
        {
            ensure();
            var first = entries[start].Box;
            var node = new Node { Box = first, Start = start, Count = count, MaxIndex = -1, Uniform = true };
            for (int i = start; i < start + count; i++)
            {
                if ((i & 255) == 0) ensure();
                var entry = entries[i]; var b = entry.Box;
                node.MaxIndex = Math.Max(node.MaxIndex, entry.Index);
                node.Uniform &= Appearance(first, b);
                node.Box = node.Box with { MinX = Math.Min(node.Box.MinX,b.MinX), MinY = Math.Min(node.Box.MinY,b.MinY),
                    MaxX = Math.Max(node.Box.MaxX,b.MaxX), MaxY = Math.Max(node.Box.MaxY,b.MaxY) };
            }
            if (count <= 8) return node;
            bool x = node.Box.MaxX - node.Box.MinX >= node.Box.MaxY - node.Box.MinY;
            Array.Sort(entries, start, count, Comparer<Entry>.Create((a,b) =>
            {
                double ac = x ? a.Box.MinX/2+a.Box.MaxX/2 : a.Box.MinY/2+a.Box.MaxY/2;
                double bc = x ? b.Box.MinX/2+b.Box.MaxX/2 : b.Box.MinY/2+b.Box.MaxY/2;
                int order = ac.CompareTo(bc); return order != 0 ? order : a.Index.CompareTo(b.Index);
            }));
            int half = count/2;
            node.Left = Build(start,half); node.Right = Build(start+half,count-half);
            return node;
        }

        bool Search(Node node, Entry current)
        {
            if ((++metrics.Nodes & 255) == 0) ensure();
            if (node.MaxIndex <= current.Index || !Touch(current.Box,node.Box)
                || node.Uniform && Appearance(current.Box,node.Box)) return false;
            if (node.Left != null) return Search(node.Left,current) || Search(node.Right!,current);
            for (int j = node.Start; j < node.Start+node.Count; j++)
            {
                var other = entries[j];
                if (other.Index <= current.Index || Appearance(current.Box,other.Box)) continue;
                metrics.Pairs++;
                if (Touch(current.Box,other.Box)) return true;
            }
            return false;
        }

        var root = Build(0,entries.Length);
        foreach (var entry in entries) { ensure(); if (Search(root,entry)) return true; }
        return false;
    }
}
