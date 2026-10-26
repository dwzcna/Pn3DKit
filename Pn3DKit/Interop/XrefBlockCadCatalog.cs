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
        internal List<(ObjectId Id, string Name, bool Anonymous)> Blocks { get; } = [];
        internal List<ObjectId> Spaces { get; } = [];
        internal Dictionary<ObjectId, XrefBlockDefinition> Definitions { get; } = [];
        internal XrefBlockSnapshotReader? Reader { get; set; }
        internal bool UsageIndexed { get; set; }
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
    private readonly Dictionary<string, (SourceState Source, ObjectId Id, string Name, bool Anonymous)> _names = new(StringComparer.Ordinal);
    private Action<int, string>? _nameProgress;
    private int _nameCount;
    /// <summary>实际根快照读取次数，不含只列名称或匿名提示。</summary>
    internal int RootReads { get; private set; }
    /// <summary>按需创建的来源读取器数。</summary>
    internal int ReadersCreated { get; private set; }
    /// <summary>已建立可达使用索引的来源数。</summary>
    internal int UsageIndexes => _sources.Values.Count(x => x.UsageIndexed);
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
    internal void Discover(Action<int, string>? progress = null)
    {
        _nameProgress = progress;
        Visit(_host, _host.Drawing, 0, []);
        foreach (var source in _sources.Values) _journal.Source(source.Snapshot);
        foreach (var failure in _failures) _journal.Source(failure);
    }

    /// <summary>每个数据库只建立一次名称及外参关系清单，不枚举块实体。</summary>
    private void Index(SourceState source)
    {
        if (source.Indexed) return;
        source.Indexed = true;
        _ensure();
        using var transaction = source.Database.TransactionManager.StartOpenCloseTransaction();
        var table = (BlockTable)transaction.GetObject(source.Database.BlockTableId, OpenMode.ForRead);
        var spaces = source.Spaces;
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
            source.Blocks.Add((id, original, block.IsAnonymous));
            if (++_nameCount % 50 == 0) _nameProgress?.Invoke(_nameCount, source.Drawing);
        }
        _nameProgress?.Invoke(_nameCount, source.Drawing);
        _journal.Notice("来源名称扫描完成：" + source.Key + " | " + source.Drawing
            + " | 名称 " + source.Blocks.Count + "；其中匿名 " + source.Blocks.Count(x => x.Anonymous)
            + "；尚未读取定义内容或统计使用状态。");
    }

    /// <summary>仅为需要深入比较的来源建立可达使用索引；名称扫描不枚举实体。</summary>
    private void IndexUsage(SourceState source)
    {
        if (source.UsageIndexed) return;
        _ensure();
        using var transaction = source.Database.TransactionManager.StartOpenCloseTransaction();
        var pending = new Stack<ObjectId>(source.Spaces);
        var visited = new HashSet<ObjectId>();
        var used = new HashSet<ObjectId>();
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
                used.Add(target);
                if (reference.IsDynamicBlock) used.Add(reference.DynamicBlockTableRecord);
                var child = (BlockTableRecord)transaction.GetObject(target, OpenMode.ForRead);
                if (!child.IsFromExternalReference) pending.Push(target);
            }
        }
        source.Used.UnionWith(used);
        source.UsageIndexed = true;
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

    /// <summary>固定轻量名称身份和最终引用链，不构造快照读取器或统计使用状态。</summary>
    internal XrefBlockInventory ReadNames()
    {
        VerifySources();
        var entries = new List<XrefBlockNameEntry>();
        var sources = _sources.Values.Select(s => s.Snapshot).Concat(_failures).ToArray();
        foreach (var source in _sources.Values)
        {
            if (source.Error != null) continue;
            var snapshot = source.Snapshot;
            for (int index = 0; index < source.Blocks.Count; index++)
            {
                _ensure();
                var (id, name, anonymous) = source.Blocks[index];
                string key = source.Key + "/" + index;
                var entry = new XrefBlockNameEntry(key, name, snapshot, anonymous);
                entries.Add(entry);
                _names[key] = (source, id, name, anonymous);
            }
        }
        return new(entries, sources, ScopeComplete);
    }

    /// <summary>每个命名候选按完整身份惰性读取；保留父块内部所有必要的匿名及唯一名依赖。</summary>
    internal XrefBlockDefinition ReadDefinition(XrefBlockNameEntry entry)
    {
        _ensure();
        if (!_names.TryGetValue(entry.Key, out var target) || target.Source.Key != entry.Source.Key
            || target.Name != entry.Name || target.Anonymous != entry.IsAnonymous)
            throw new InvalidOperationException("块名称条目已失效或不属于本次清单。");
        if (entry.IsAnonymous) throw new InvalidOperationException("匿名根定义按规则不进行独立内容比较。");
        var source = target.Source;
        VerifySource(source);
        if (!target.Id.IsValid || target.Id.IsErased)
            throw new InvalidOperationException("候选块定义已失效或删除：" + entry.Name);
        if (source.Definitions.TryGetValue(target.Id, out var cached)) return cached;
        int references = 0;
        BlockContentSnapshot content;
        try
        {
            IndexUsage(source);
            using (var transaction = source.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var block = (BlockTableRecord)transaction.GetObject(target.Id, OpenMode.ForRead);
                if (block.IsAnonymous || XrefBlockComparison.RemoveSourceNamespace(block.Name, source.Prefixes) != entry.Name)
                    throw new InvalidOperationException("命名块定义状态已变化。");
                references = block.GetBlockReferenceIds(true, false).Count;
                if (block.IsDynamicBlock)
                    foreach (ObjectId anonymous in block.GetAnonymousBlockIds())
                    {
                        _ensure();
                        references += ((BlockTableRecord)transaction.GetObject(anonymous, OpenMode.ForRead))
                            .GetBlockReferenceIds(true, false).Count;
                    }
            }
            if (source.Reader == null)
            {
                source.Reader = new(source.Database, _ensure, source.Prefixes.ToArray());
                ReadersCreated++;
            }
            RootReads++;
            content = source.Reader.Read(target.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Pn3DKit.Commands.XrefCheckStoppedException) { throw; }
        catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        { throw new OperationCanceledException("用户取消。", ex); }
        catch (System.Exception ex) { content = new("root", new Dictionary<string, BlockContentNode>(), [], Detail(ex)); }
        VerifySource(source);
        var result = new XrefBlockDefinition(entry.Name, entry.Source, content, references, source.Used.Contains(target.Id));
        source.Definitions.Add(target.Id, result);
        return result;
    }

    /// <summary>候选处理结束再次确认所有来源；名称唯一或匿名跳过也不能掩盖磁盘变化。</summary>
    internal void VerifySources()
    {
        foreach (var source in _sources.Values)
        {
            _ensure();
            if (source.Error == null) VerifySource(source);
        }
    }

    /// <summary>旁路文件变化时停止并保留报告；先前结果仅反映读取时状态，不冒充当前数据。</summary>
    private void VerifySource(SourceState source)
    {
        _ensure();
        string? problem = null;
        try
        {
            if (source.Database.IsDisposed || !source.Identity.IsValid || source.Identity.IsErased
                || source.Database.BlockTableId != source.Identity)
                problem = "来源数据库已关闭或身份变化：" + source.Drawing;
            else if (source.DiskStamp != null && FileStamp(source.Drawing) != source.DiskStamp)
                problem = "读取期间源文件已变化：" + source.Drawing;
        }
        catch (OperationCanceledException) { throw; }
        catch (Pn3DKit.Commands.XrefCheckStoppedException) { throw; }
        catch (CadException ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        { throw new OperationCanceledException("用户取消。", ex); }
        catch (System.Exception ex) { problem = "无法确认来源状态：" + source.Drawing + "；" + Detail(ex); }
        if (problem == null) return;
        Incomplete(problem + "；已完成报告仅反映读取时状态，请重新检查。");
        throw new IOException(problem);
    }

    /// <summary>从未释放宿主拥有的数据库；失败时仍尝试释放其余旁路数据库。</summary>
    public void Dispose()
    {
        var errors = new List<string>();
        foreach (var source in _sources.Values)
        {
            try { source.Reader?.Dispose(); }
            catch (System.Exception ex) { errors.Add(Detail(ex)); }
            source.Reader = null;
        }
        foreach (var database in _owned)
            try { database.Dispose(); }
            catch (System.Exception ex) { errors.Add(Detail(ex)); }
        _owned.Clear();
        if (errors.Count != 0) throw new InvalidOperationException("关闭只读外参数据库失败：" + string.Join("；", errors));
    }
}
