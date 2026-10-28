using System.Text.RegularExpressions;

namespace Pn3DKit.Utils;

/// <summary>仅缓存批次内纯格式结构；不缓存属性、权限、求值及序列字段。</summary>
internal static class TagFormatCache
{
    private sealed class State
    {
        internal readonly Dictionary<(Type, string), object> Values = [];
    }
    private static State? _batch;

    /// <summary>由唯一批次租约开启；锁仅保护纯解析缓存。</summary>
    internal static void Begin() => Volatile.Write(ref _batch, new State());
    /// <summary>批次结束丢弃全部格式数据，不遗留跨项目值。</summary>
    internal static void End() => Volatile.Write(ref _batch, null);
    /// <summary>相同格式和结果类型复用只读解析结果；非批次调用即时解析。</summary>
    internal static T Get<T>(string format, Func<T> parse) where T : notnull
    {
        var batch = Volatile.Read(ref _batch);
        if (batch == null) return parse();
        lock (batch.Values)
        {
            var key = (typeof(T), format);
            if (batch.Values.TryGetValue(key, out var result)) return (T)result;
            var value = parse();
            batch.Values.Add(key, value);
            return value;
        }
    }

    /// <summary>固定结构的格式片段，索引和长度继续用于逐字符回拼。</summary>
    internal sealed record Token(int Index, int Length, string Value);
    /// <summary>同一批次的检查与重建共用格式边界，结果不含 SDK 对象。</summary>
    internal static IReadOnlyList<Token> Tokens(string format) => Get<IReadOnlyList<Token>>(format, () =>
        Regex.Matches(format, @"#\([^()]*\)").Select(m => new Token(m.Index, m.Length, m.Value)).ToArray());
}
