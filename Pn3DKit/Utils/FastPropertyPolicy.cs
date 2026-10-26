using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>八项属性的多选共同值汇总，使用 Ordinal 比较原始键，兼容空值和本地化名称。</summary>
public static class FastPropertyPolicy
{
    /// <summary>未选择、等待或不可用状态清空旧值，避免显示上一张图纸的数据。</summary>
    public static FastPanelSnapshot Empty(string status = "未选择对象", string header = "未选择对象") =>
        new(header, 0, Enum.GetValues<FastProperty>().Select(_ => FastPropertyValue.Missing()).ToArray(), status);

    /// <summary>产生全对象读取失败快照，不丢弃失效对象导致错误统计共同值。</summary>
    public static FastObjectProperties Failed(string reason) =>
        new("", "读取失败", Enum.GetValues<FastProperty>().Select(_ => FastPropertyValue.Error(reason)).ToArray());

    /// <summary>汇总整个选择集；每个对象必须完整提供八项，缺失用状态表达。</summary>
    public static FastPanelSnapshot Merge(IReadOnlyList<FastObjectProperties> objects)
    {
        if (objects.Count == 0) return Empty();
        if (objects.Any(x => x.Values.Count != 8)) throw new ArgumentException("对象属性快照必须包含八项。");
        var values = Enumerable.Range(0, 8).Select(i => MergeValues(objects.Select(x => x.Values[i]).ToArray())).ToArray();
        bool commonType = objects.All(x => string.Equals(x.TypeKey, objects[0].TypeKey, StringComparison.Ordinal));
        string type = commonType ? objects[0].TypeLabel : "多种对象";
        string header = objects.Count == 1 ? type : type + $"（{objects.Count} 个对象）";
        bool error = objects.Any(x => x.Values.Any(v => v.Kind == FastValueKind.Error));
        string status = $"只读 · 已选 {objects.Count} 个对象" + (error ? " · 部分属性读取失败" : "");
        var notes = objects.Select(x => x.Note).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(3).ToArray();
        if (notes.Length != 0) status += "\n" + string.Join("；", notes);
        return new(header, objects.Count, values, status);
    }

    /// <summary>缺失不取其他对象补值；错误不作为普通字符串参与共同值比较。</summary>
    public static FastPropertyValue MergeValues(IReadOnlyList<FastPropertyValue> values)
    {
        if (values.Count == 0) return FastPropertyValue.Missing();
        string details = string.Join("\n", values.Select(v => v.Detail).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(10));
        if (values.Any(v => v.Kind == FastValueKind.Missing))
            return FastPropertyValue.Missing((values.All(v => v.Kind == FastValueKind.Missing) ? "" : "此属性不适用于所有选中对象。\n") + details);
        if (values.Any(v => v.Kind == FastValueKind.Error)) return FastPropertyValue.Error(details);
        if (values.Any(v => v.Kind == FastValueKind.AbsentProperty))
            return FastPropertyValue.AbsentProperty((values.All(v => v.Kind == FastValueKind.AbsentProperty)
                ? "" : "部分选中对象不含此属性。\n") + details);
        if (values.All(v => string.Equals(v.Raw, values[0].Raw, StringComparison.Ordinal))) return values[0];
        return new(FastValueKind.Mixed, Detail: "不同值：\n" + string.Join("\n", values.Select(v => v.Display).Distinct().Take(10)));
    }
}
