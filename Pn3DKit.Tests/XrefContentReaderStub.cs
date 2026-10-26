using Autodesk.AutoCAD.DatabaseServices;
using Pn3DKit.Models;

namespace Pn3DKit.Interop;

/// <summary>仅替代内容 SDK 适配，不加载 Autodesk；目录、缓存及批次代码使用生产源码。</summary>
internal sealed class XrefBlockSnapshotReader : IDisposable
{
    private readonly Database _database;
    private readonly Action _ensure;
    internal static int Created, Disposed;
    internal static readonly List<ObjectId> Reads = [];
    internal static Func<Database, ObjectId, BlockContentSnapshot>? Snapshot;
    internal static Action<Database, ObjectId>? OnRead;
    internal XrefBlockSnapshotReader(Database database, Action ensure, IReadOnlyList<string>? namespaces = null)
    { _database = database; _ensure = ensure; Created++; }
    internal BlockContentSnapshot Read(ObjectId id)
    {
        _ensure(); Reads.Add(id); OnRead?.Invoke(_database, id);
        return Snapshot?.Invoke(_database, id) ?? new("root", new Dictionary<string, BlockContentNode>
        { ["root"] = new("root", "二维块替身", new Dictionary<string, string>(), []) }, []);
    }
    public void Dispose() { Disposed++; }
}
