using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using CadException = Autodesk.AutoCAD.Runtime.Exception;

namespace Pn3DKit.Interop;

/// <summary>读取当前数据库和外参关系；只持有并释放本工具创建的旁路数据库。</summary>
internal sealed class XrefBlockCadCatalog : IDisposable
{
    /// <summary>单个真实数据库的缓存，多个引用链共享定义读取结果。</summary>
    internal sealed class SourceState(Database database, string key, string drawing, string mode, bool host)
    {
        internal Database Database { get; } = database;
        internal ObjectId Identity { get; } = database.BlockTableId;
        internal string Key { get; } = key;
        internal string Drawing { get; } = drawing;
        internal string Mode { get; } = mode;
        internal bool Host { get; } = host;
        internal HashSet<string> Chains { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Prefixes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<(ObjectId Id, string Name)> Blocks { get; } = [];
        internal List<(ObjectId Id, string Alias, string Path, bool Overlay, Database? Loaded, string Status)> References { get; } = [];
        internal HashSet<ObjectId> Used { get; } = [];
        internal bool Indexed { get; set; }
        internal string? Error { get; set; }
        internal string? DiskStamp { get; set; }
        internal XrefBlockSource Snapshot => new(Key, Drawing, Mode, Host, Chains.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Error);
    }

    private readonly Action _ensure;
    private readonly XrefBlockJournal _journal;
    private readonly Dictionary<ObjectId, SourceState> _sources = [];
    private readonly Dictionary<string, List<Database>> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ObjectId, HashSet<string>> _livePrefixes = [];
    private readonly Dictionary<string, SourceState> _disk = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _diskFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Database> _owned = [];
    private readonly List<XrefBlockSource> _failures = [];
    private readonly SourceState _host;
    /// <summary>未知来源、循环及索引失败均使整体范围不完整。</summary>
    internal bool ScopeComplete { get; private set; } = true;

    /// <summary>仅查询宿主关系图，不解析或重载外参。</summary>
    internal XrefBlockCadCatalog(Database host, string drawing, Action ensure, XrefBlockJournal journal)
    {
        _ensure = ensure; _journal = journal;
        _host = Register(host, drawing, "宿主当前数据", true);
        try
        {
            using var graph = host.GetHostDwgXrefGraph(true);
            for (int i = 0; i < graph.NumNodes; i++)
            {
                _ensure();
                var node = graph.GetXrefNode(i);
                var database = node.Database;
                if (database == null || database.IsDisposed || node.XrefStatus != XrefStatus.Resolved) continue;
                string path = Canonical(database.Filename);
                if (path.Length == 0) continue;
                if (database.BlockTableId != host.BlockTableId && !string.IsNullOrWhiteSpace(node.Name))
                {
                    if (!_livePrefixes.TryGetValue(database.BlockTableId, out var prefixes))
                        _livePrefixes.Add(database.BlockTableId, prefixes = new(StringComparer.OrdinalIgnoreCase));
                    prefixes.Add(node.Name);
                }
                if (!_live.TryGetValue(path, out var variants)) _live.Add(path, variants = []);
                if (!variants.Any(db => db.BlockTableId == database.BlockTableId)) variants.Add(database);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Pn3DKit.Commands.XrefCheckStoppedException) { throw; }
        catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak) { throw new OperationCanceledException("用户取消。", ex); }
        catch (System.Exception ex) { Incomplete("无法完整读取宿主外参关系图：" + Detail(ex)); }
    }

    /// <summary>规范化实际路径供本批缓存使用；路径不参与块内容比较。</summary>
    private static string Canonical(string? path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);

    /// <summary>完整块表标识区分同路径的不同宿主内存数据库。</summary>
    private SourceState Register(Database database, string drawing, string mode, bool host = false)
    {
        ObjectId identity = database.BlockTableId;
        if (_sources.TryGetValue(identity, out var existing)) return existing;
        var source = new SourceState(database, "来源" + (_sources.Count + 1), drawing, mode, host);
        if (!host && _livePrefixes.TryGetValue(identity, out var prefixes)) source.Prefixes.UnionWith(prefixes);
        _sources.Add(identity, source);
        return source;
    }

    /// <summary>警告即时落盘；调用方不能用正常结束抹掉范围不完整。</summary>
    private void Incomplete(string detail) { ScopeComplete = false; _journal.Notice("无法完整比较：" + detail); }

    /// <summary>SDK 错误保留状态码，普通异常保留类型及原始消息。</summary>
    internal static string Detail(System.Exception ex) => ex is CadException cad
        ? "SDK ErrorStatus=" + cad.ErrorStatus + "：" + cad.Message : ex.GetType().Name + "：" + ex.Message;

    /// <summary>建立全部引用链后再固定来源清单，命名定义包括未插入块。</summary>
    internal void Discover()
    {
        Visit(_host, _host.Drawing, 0, []);
        foreach (var source in _sources.Values) _journal.Source(source.Snapshot);
        foreach (var failure in _failures) _journal.Source(failure);
    }

    /// <summary>每个数据库只建立一次块表、直接外参和可达使用索引。</summary>
    private void Index(SourceState source)
    {
        if (source.Indexed) return;
        source.Indexed = true;
        _ensure();
        using var transaction = source.Database.TransactionManager.StartOpenCloseTransaction();
        var table = (BlockTable)transaction.GetObject(source.Database.BlockTableId, OpenMode.ForRead);
        var spaces = new List<ObjectId>();
        var records = new List<(ObjectId Id, BlockTableRecord Block)>();
        foreach (ObjectId id in table)
        {
            _ensure();
            var block = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (block.IsLayout) { spaces.Add(id); continue; }
            records.Add((id, block));
        }
        var unmappedReferences = new List<string>();
        // 先取得本源的直接外参名，再判别子命名空间，不能依赖块表创建顺序。
        foreach (var (id, block) in records.Where(x => x.Block.IsFromExternalReference))
        {
            _ensure();
            if (source.Host && block.IsDependent) continue;
            string? original = XrefBlockComparison.RemoveSourceNamespace(block.Name, source.Prefixes);
            if (original == null || original.Contains('|')) { unmappedReferences.Add(original ?? block.Name); continue; }
            Database? loaded = null;
            if (!block.IsUnloaded && block.XrefStatus == XrefStatus.Resolved) loaded = block.GetXrefDatabase(false);
            source.References.Add((id, original, block.PathName, block.IsFromOverlayReference, loaded, block.XrefStatus.ToString()));
        }
        foreach (string stored in unmappedReferences)
            if (!source.References.Any(r => stored.StartsWith(r.Alias + "|", StringComparison.OrdinalIgnoreCase)))
                Incomplete(source.Drawing + "：外参容器的原始名称无法确认，关系范围不完整：" + stored);
        foreach (var (id, block) in records.Where(x => !x.Block.IsFromExternalReference))
        {
            _ensure();
            if (source.Host && block.IsDependent) continue;
            string? original = XrefBlockComparison.RemoveSourceNamespace(block.Name, source.Prefixes);
            bool childNamespace = source.References.Any(r => block.Name.StartsWith(r.Alias + "|", StringComparison.OrdinalIgnoreCase)
                || original != null && original.StartsWith(r.Alias + "|", StringComparison.OrdinalIgnoreCase));
            if (childNamespace)
            {
                if (original != null && !original.Contains('|') && source.Prefixes.Any(prefix =>
                    source.References.Any(r => string.Equals(r.Alias, prefix, StringComparison.OrdinalIgnoreCase))))
                    Incomplete(source.Drawing + "：本源与子参照别名重合，无法唯一恢复原名：" + block.Name);
                continue;
            }
            if (original == null || original.Contains('|'))
            {
                Incomplete(source.Drawing + "：无法可靠确认原始命名空间：" + block.Name);
                continue;
            }
            if (!block.IsAnonymous) source.Blocks.Add((id, original));
        }
        var pending = new Stack<ObjectId>(spaces);
        var visited = new HashSet<ObjectId>();
        while (pending.Count != 0)
        {
            _ensure();
            var id = pending.Pop();
            if (!visited.Add(id)) continue;
            var block = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            foreach (ObjectId entityId in block)
            {
                _ensure();
                if (transaction.GetObject(entityId, OpenMode.ForRead) is not BlockReference reference) continue;
                ObjectId target = reference.BlockTableRecord;
                source.Used.Add(target);
                if (reference.IsDynamicBlock) source.Used.Add(reference.DynamicBlockTableRecord);
                var child = (BlockTableRecord)transaction.GetObject(target, OpenMode.ForRead);
                if (!child.IsFromExternalReference) pending.Push(target);
            }
        }
    }

    /// <summary>遍历引用链而不是全局剪枝，保留共享文件的每条到达路径。</summary>
    private void Visit(SourceState source, string chain, int depth, HashSet<ObjectId> ancestors)
    {
        _ensure();
        source.Chains.Add(chain);
        if (!ancestors.Add(source.Identity)) { Incomplete("循环外参关系：" + chain); return; }
        try
        {
            Index(source);
            foreach (var reference in source.References)
            {
                _ensure();
                string next = chain + " → " + reference.Alias;
                bool included = XrefBlockComparison.IncludesReference(depth, reference.Overlay);
                _journal.Relation(new(source.Drawing, reference.Alias, reference.Path, next, reference.Overlay,
                    included, included ? reference.Status : "嵌套覆盖不向主图传播"));
                if (!included) continue;
                try
                {
                    SourceState child;
                    if (reference.Loaded is { IsDisposed: false } loaded)
                        child = Register(loaded, string.IsNullOrWhiteSpace(loaded.Filename)
                            ? reference.Path : loaded.Filename, "宿主当前数据");
                    else
                    {
                        string path = Resolve(reference.Path, source.Database);
                        if (_live.TryGetValue(path, out var variants) && variants.Count == 1)
                            child = Register(variants[0], path, "宿主当前数据（同源已加载）");
                        else if (_live.TryGetValue(path, out variants) && variants.Count > 1)
                            throw new InvalidOperationException("同路径存在多个宿主数据状态，无法确定当前引用对应的数据。");
                        else if (!_disk.TryGetValue(path, out child!))
                        {
                            if (_diskFailures.TryGetValue(path, out var previous))
                                throw new IOException("同源文件本批读取已失败，不重复读取：" + previous);
                            try
                            {
                                string stamp = FileStamp(path);
                                var database = new Database(false, true);
                                _owned.Add(database);
                                database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                                if (FileStamp(path) != stamp) throw new IOException("读取期间源文件已变化：" + path);
                                child = Register(database, path, "磁盘只读数据");
                                child.DiskStamp = stamp;
                                _disk.Add(path, child);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Pn3DKit.Commands.XrefCheckStoppedException) { throw; }
                            catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
                            { throw new OperationCanceledException("用户取消。", ex); }
                            catch (System.Exception ex) { _diskFailures[path] = Detail(ex); throw; }
                        }
                    }
                    Visit(child, next, depth + 1, new HashSet<ObjectId>(ancestors));
                }
                catch (OperationCanceledException) { throw; }
                catch (Pn3DKit.Commands.XrefCheckStoppedException) { throw; }
                catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak) { throw new OperationCanceledException("用户取消。", ex); }
                catch (System.Exception ex)
                {
                    string reason = Detail(ex);
                    Incomplete(next + "：" + reason);
                    _failures.Add(new("失败" + _failures.Count, reference.Path, "未读取", false, [next], reason));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Pn3DKit.Commands.XrefCheckStoppedException) { throw; }
        catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak) { throw new OperationCanceledException("用户取消。", ex); }
        catch (System.Exception ex) { source.Error = Detail(ex); Incomplete(source.Drawing + "：" + source.Error); }
        finally { ancestors.Remove(source.Identity); }
    }

    /// <summary>先使用宿主的搜索规则，再按所属图纸目录解析；不依赖进程当前目录猜测相对路径。</summary>
    private static string Resolve(string path, Database owner)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new FileNotFoundException("外参未提供源文件路径。");
        if (Path.IsPathFullyQualified(path)) return Canonical(path);
        try
        {
            string found = HostApplicationServices.Current.FindFile(path, owner, FindFileHint.XRefDrawing);
            if (!string.IsNullOrWhiteSpace(found)) return Canonical(found);
        }
        catch (CadException ex) when (ex.ErrorStatus != ErrorStatus.UserBreak) { /* 继续按所属图纸位置核对。 */ }
        if (string.IsNullOrWhiteSpace(owner.Filename))
            throw new FileNotFoundException("所属图纸未保存，无法可靠解析相对外参路径：" + path);
        string candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(owner.Filename)!, path));
        if (!File.Exists(candidate)) throw new FileNotFoundException("外参源文件缺失：" + candidate, candidate);
        return candidate;
    }

    /// <summary>旁路懒读取允许其他程序写文件，因此在读取前后核对文件状态。</summary>
    private static string FileStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Length + "/" + file.LastWriteTimeUtc.Ticks;
    }

    /// <summary>依来源串行读取定义；每条清单立即记录，取消不丢失已扫描记录。</summary>
    internal IReadOnlyList<XrefBlockDefinition> ReadDefinitions(Action<int, string> progress)
    {
        var result = new List<XrefBlockDefinition>();
        int count = 0;
        foreach (var source in _sources.Values)
        {
            _ensure();
            if (source.Error != null) continue;
            int sourceStart = result.Count;
            using var reader = new XrefBlockSnapshotReader(source.Database, _ensure, source.Prefixes.ToArray());
            foreach (var (id, name) in source.Blocks)
            {
                _ensure();
                int references = 0;
                BlockContentSnapshot content;
                try
                {
                    using var transaction = source.Database.TransactionManager.StartOpenCloseTransaction();
                    var block = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    references = block.GetBlockReferenceIds(true, false).Count;
                    if (block.IsDynamicBlock)
                        foreach (ObjectId anonymous in block.GetAnonymousBlockIds())
                            references += ((BlockTableRecord)transaction.GetObject(anonymous, OpenMode.ForRead))
                                .GetBlockReferenceIds(true, false).Count;
                    content = reader.Read(id);
                }
                catch (OperationCanceledException) { throw; }
                catch (Pn3DKit.Commands.XrefCheckStoppedException) { throw; }
                catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak) { throw new OperationCanceledException("用户取消。", ex); }
                catch (System.Exception ex) { content = new("root", new Dictionary<string, BlockContentNode>(), [], Detail(ex)); }
                var definition = new XrefBlockDefinition(name, source.Snapshot, content, references, source.Used.Contains(id));
                result.Add(definition);
                _journal.Notice("定义清单：" + source.Drawing + " | " + name + " | " + definition.Usage
                    + (content.Complete ? "" : " | 无法完整比较：" + string.Join("；", content.Issues.Append(content.ReadError ?? ""))));
                if (++count % 50 == 0) progress(count, source.Drawing);
            }
            if (source.DiskStamp != null)
            {
                string? changed = null;
                try { if (FileStamp(source.Drawing) != source.DiskStamp) changed = "读取期间源文件已变化：" + source.Drawing; }
                catch (System.Exception ex) { changed = "无法确认源文件状态：" + Detail(ex); }
                if (changed != null)
                {
                    Incomplete(changed);
                    for (int i = sourceStart; i < result.Count; i++)
                        result[i] = result[i] with { Content = result[i].Content with
                            { Issues = result[i].Content.Issues.Append(changed).ToArray() } };
                }
            }
        }
        progress(count, "读取完成");
        return result;
    }

    /// <summary>从未释放宿主拥有的数据库；失败时仍尝试释放其余旁路数据库。</summary>
    public void Dispose()
    {
        var errors = new List<string>();
        foreach (var database in _owned)
            try { database.Dispose(); }
            catch (System.Exception ex) { errors.Add(Detail(ex)); }
        _owned.Clear();
        if (errors.Count != 0) throw new InvalidOperationException("关闭只读外参数据库失败：" + string.Join("；", errors));
    }
}
