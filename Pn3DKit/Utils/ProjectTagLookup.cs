namespace Pn3DKit.Utils;

/// <summary>生成 SDK 位号筛选并核对返回结果；身份使用项目部分及实际数据行。</summary>
public static class ProjectTagLookup
{
    /// <summary>Tag 是固定字段名；值按 SQL 字符串字面量转义，不拼接用户字段名。</summary>
    public static string Filter(string tag)
    {
        if (tag.IndexOf('\0') >= 0) throw new ArgumentException("位号含空字符。", nameof(tag));
        return "Tag = '" + tag.Replace("'", "''") + "'";
    }

    /// <summary>过滤继承表重复、大小写及自身行；枚举错误直接传播，禁止使用部分结果。</summary>
    public static IReadOnlyList<int> Owners(string tag, int self,
        Func<string, IEnumerable<(int Id, string Tag)>> select) =>
        select(Filter(tag)).Where(row => row.Id != self && string.Equals(row.Tag, tag, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Id).Distinct().OrderBy(id => id).ToArray();
}
