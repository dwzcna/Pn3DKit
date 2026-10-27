// 仅为链接的类别名称适配提供最小只读 SDK 替身，不加载 Autodesk，不模拟真实项目返回的中文。
namespace Autodesk.ProcessPower.ProjectManager
{
    /// <summary>记录共享适配实际请求的内部类别名称及读取次数。</summary>
    internal sealed class Project(Func<string, string?> read)
    {
        internal List<string> Reads { get; } = [];
        internal string? GetClassDisplayName(string className) { Reads.Add(className); return read(className); }
    }
}
namespace Autodesk.ProcessPower.DataObjects
{
    /// <summary>延迟访问表集合，验证项目名称有效时不碰表回退。</summary>
    internal sealed class PnPDatabase(Func<IReadOnlyDictionary<string, PnPTable>> tables)
    {
        internal const string DisplayNameAttribute = "DisplayName";
        internal int Reads { get; private set; }
        internal IReadOnlyDictionary<string, PnPTable> Tables { get { Reads++; return tables(); } }
    }
    /// <summary>表名称回调保留属性标识，支持确定性读取错误与取消。</summary>
    internal sealed class PnPTable(Func<string, string?> read)
    {
        internal List<string> Reads { get; } = [];
        internal string? GetTableAttributeValue(string name) { Reads.Add(name); return read(name); }
    }
}
