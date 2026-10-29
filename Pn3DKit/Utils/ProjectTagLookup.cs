using System.Buffers;
using System.Text;

namespace Pn3DKit.Utils;

/// <summary>生成 SDK 位号筛选并核对返回结果；身份使用项目部分及实际数据行。</summary>
public static class ProjectTagLookup
{
    /// <summary>普通值使用等号；Unicode大小写字符扩大SQL候选范围，返回后必须按OrdinalIgnoreCase复核。</summary>
    /// <remarks>SQLite等SDK引擎的大小写折叠可能仅覆盖ASCII；通配符均转义，非法UTF16拒绝查询。</remarks>
    public static string Filter(string tag)
    {
        if (tag.IndexOf('\0') >= 0) throw new ArgumentException("位号含空字符。", nameof(tag));
        bool widened = false;
        var pattern = new StringBuilder();
        for (int offset = 0; offset < tag.Length;)
        {
            if (Rune.DecodeFromUtf16(tag.AsSpan(offset), out var rune, out int consumed) != OperationStatus.Done)
                throw new ArgumentException("位号含无效Unicode字符。", nameof(tag));
            offset += consumed;
            if (rune.Value > 127 && Rune.ToUpperInvariant(rune) != Rune.ToLowerInvariant(rune))
            {
                widened = true;
                pattern.Append('_');
            }
            else
            {
                if (rune.Value is '%' or '_' or '\\') pattern.Append('\\');
                pattern.Append(rune.ToString());
            }
        }
        return widened ? "Tag LIKE '" + pattern.ToString().Replace("'", "''") + "' ESCAPE '\\'"
            : "Tag = '" + tag.Replace("'", "''") + "'";
    }

    /// <summary>过滤继承表重复、大小写及自身行；枚举错误直接传播，禁止使用部分结果。</summary>
    public static IReadOnlyList<int> Owners(string tag, int self,
        Func<string, IEnumerable<(int Id, string Tag)>> select) =>
        select(Filter(tag)).Where(row => row.Id != self && string.Equals(row.Tag, tag, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Id).Distinct().OrderBy(id => id).ToArray();
}
