// 本文件仅属于离线测试程序集：模拟 SDK 读取接口，不引用、不加载 Autodesk，也不解释真实 DWG。
using System.Collections;

namespace Autodesk.AutoCAD.Runtime
{
    /// <summary>离线宿主模拟可见 SDK 状态，不替代真实 CAD 验收。</summary>
    internal enum ErrorStatus { OK, UserBreak, FileNotFound }
    /// <summary>供真实来源检索代码的状态分支运行。</summary>
    internal sealed class Exception(ErrorStatus status) : System.Exception(status.ToString())
    {
        public ErrorStatus ErrorStatus { get; } = status;
    }
}
namespace Autodesk.AutoCAD.DatabaseServices
{
    /// <summary>测试宿主仅暴露读模式，来源适配若尝试写入则无法编译。</summary>
    internal enum OpenMode { ForRead }
    /// <summary>模拟文件共享读取模式。</summary>
    internal enum FileOpenMode { OpenForReadAndAllShare }
    /// <summary>模拟来源检索提示。</summary>
    internal enum FindFileHint { XRefDrawing }
    /// <summary>模拟外参加载状态。</summary>
    internal enum XrefStatus { Resolved, Unloaded, Unresolved }
    /// <summary>含数据库身份的测试对象标识。</summary>
    internal readonly record struct ObjectId(Database? Database, int Value)
    {
        public bool IsNull => Value == 0;
        public bool IsValid => !IsNull && Database is { IsDisposed: false };
        public bool IsErased => false;
    }
    /// <summary>模拟只读事务中的对象。</summary>
    internal class DBObject { internal ObjectId Id; }
    /// <summary>保留块表的实际枚举。</summary>
    internal sealed class BlockTable : DBObject, IEnumerable<ObjectId>
    {
        internal readonly List<ObjectId> Items = [];
        public IEnumerator<ObjectId> GetEnumerator() => Items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    /// <summary>模拟标准块、匿名块和外参记录。</summary>
    internal sealed class BlockTableRecord(string name) : DBObject, IEnumerable<ObjectId>
    {
        internal readonly List<ObjectId> Items = [];
        internal Database? Loaded;
        internal int EntityEnumerations;
        public string Name { get; set; } = name;
        public bool IsLayout { get; set; }
        public bool IsFromExternalReference { get; set; }
        public bool IsDependent { get; set; }
        public bool IsAnonymous { get; set; }
        public bool IsUnloaded { get; set; }
        public bool IsFromOverlayReference { get; set; }
        public XrefStatus XrefStatus { get; set; } = XrefStatus.Resolved;
        public string PathName { get; set; } = "";
        public Database? GetXrefDatabase(bool incrementUnresolved) => Loaded;
        public IEnumerator<ObjectId> GetEnumerator() { EntityEnumerations++; return Items.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    /// <summary>模拟数据库，只读文件装载由测试预登记数据提供。</summary>
    internal sealed class Database : IDisposable
    {
        internal static readonly Dictionary<string, Database> Files = new(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<string, int> Reads = new(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<string, Runtime.ErrorStatus> Failures = new(StringComparer.OrdinalIgnoreCase);
        internal static readonly List<Database> Created = [];
        internal static readonly List<(FileOpenMode Mode, string Password)> ReadModes = [];
        internal static Action<Database, ObjectId>? OnGetObject;
        internal Dictionary<ObjectId, DBObject> Objects = [];
        internal readonly List<Database> GraphDatabases = [];
        internal bool Fixture;
        internal BlockTable Table;
        public Database(bool defaults, bool noDocument)
        {
            Created.Add(this);
            Table = new BlockTable();
            BlockTableId = Add(Table);
        }
        public ObjectId BlockTableId { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Filename { get; set; } = "";
        public TransactionManager TransactionManager => new(this);
        internal ObjectId Add(DBObject obj)
        {
            var id = new ObjectId(this, Objects.Count + 1); obj.Id = id; Objects.Add(id, obj); return id;
        }
        internal BlockTableRecord Block(string name, bool layout = false)
        {
            var block = new BlockTableRecord(name) { IsLayout = layout };
            Table.Items.Add(Add(block)); return block;
        }
        internal void Reference(string alias, Database? target, string path, bool overlay = false)
        {
            var block = Block(alias);
            block.IsFromExternalReference = true; block.Loaded = target; block.PathName = path;
            block.IsUnloaded = target == null; block.XrefStatus = target == null ? XrefStatus.Unloaded : XrefStatus.Resolved;
            block.IsFromOverlayReference = overlay;
        }
        public XrefGraph GetHostDwgXrefGraph(bool includeGhosts) => new(new[] { this }.Concat(GraphDatabases).ToArray());
        public void ReadDwgFile(string path, FileOpenMode mode, bool allowConversion, string password)
        {
            Reads[path] = Reads.GetValueOrDefault(path) + 1; ReadModes.Add((mode, password));
            if (Failures.TryGetValue(path, out var failure)) throw new Runtime.Exception(failure);
            if (!Files.TryGetValue(path, out var source)) throw new Runtime.Exception(Runtime.ErrorStatus.FileNotFound);
            Filename = path; Objects = source.Objects; Table = source.Table; BlockTableId = source.BlockTableId;
        }
        public void Dispose() => IsDisposed = true;
    }
    /// <summary>模拟只读事务管理。</summary>
    internal sealed class TransactionManager(Database database)
    {
        public Transaction StartOpenCloseTransaction() => new(database);
    }
    /// <summary>读取预登记对象，不支持写入和提交。</summary>
    internal sealed class Transaction(Database database) : IDisposable
    {
        public DBObject GetObject(ObjectId id, OpenMode mode)
        {
            Database.OnGetObject?.Invoke(database, id);
            return database.Objects.TryGetValue(id, out var obj) ? obj : id.Database!.Objects[id];
        }
        public void Dispose() { }
    }
    /// <summary>独立关系图快照，不拥有数据库。</summary>
    internal sealed class XrefGraph(Database[] databases) : IDisposable
    {
        public int NumNodes => databases.Length;
        public XrefGraphNode GetXrefNode(int index) => new(databases[index]);
        public void Dispose() { }
    }
    /// <summary>模拟已加载外参的图节点。</summary>
    internal sealed class XrefGraphNode(Database database)
    {
        public Database Database => database;
        public XrefStatus XrefStatus => XrefStatus.Resolved;
        public string Name => Path.GetFileNameWithoutExtension(database.Filename);
    }
    /// <summary>按调用方所属图纸位置定位，避免依赖进程当前目录。</summary>
    internal sealed class HostApplicationServices
    {
        public static HostApplicationServices Current { get; } = new();
        public string FindFile(string path, Database database, FindFileHint hint)
        {
            string result = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(database.Filename)!, path));
            if (!File.Exists(result)) throw new Runtime.Exception(Runtime.ErrorStatus.FileNotFound);
            return result;
        }
    }
}
namespace Pn3DKit.Commands
{
    /// <summary>模拟同类型的批次身份停止异常；不模拟真实文档管理器。</summary>
    internal sealed class XrefCheckStoppedException(string message, System.Exception? inner = null) : InvalidOperationException(message, inner);
}
