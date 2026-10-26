using Autodesk.AutoCAD.DatabaseServices;
using Pn3DKit.Interop;
using Pn3DKit.Models;
using Xunit.Abstractions;
using Pn3DKit.Services;

namespace Pn3DKit.Tests;

/// <summary>链接真实来源检索代码，以最小 SDK 替身验证读取和资源生命周期，不打开真实 DWG。</summary>
public sealed class XrefCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.XrefCatalog", Guid.NewGuid().ToString("N"));
    private readonly RunLogFiles _files = new();
    private readonly XrefBlockJournal _journal;
    private readonly ITestOutputHelper _output;
    /// <summary>重置本类使用的只读 SDK 替身，并准备独立报告目录。</summary>
    public XrefCatalogTests(ITestOutputHelper output)
    {
        _output = output;
        Database.Files.Clear(); Database.Reads.Clear(); Database.Failures.Clear(); Database.Created.Clear(); Database.ReadModes.Clear();
        XrefBlockSnapshotReader.OnRead = null; XrefBlockSnapshotReader.NamespaceReads.Clear();
        XrefBlockSnapshotReader.RootsRead.Clear(); XrefBlockSnapshotReader.CreatedCount = 0;
        XrefBlockSnapshotReader.DisposedCount = 0; XrefBlockSnapshotReader.CreateSnapshot = null;
        _journal = new(_root, "A.dwg", _files);
    }

    /// <summary>只生成标明为替身数据的临时文件，用于路径和变更检查，不是真实图纸。</summary>
    private Database Source(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "仅用于 SDK 替身测试，不是真实 DWG");
        var source = new Database(false, true) { Filename = path, Fixture = true };
        source.Block("*Model_Space", true);
        Database.Files[path] = source; return source;
    }

    /// <summary>同一个磁盘来源只读取一次，多个引用链和命名定义均保留。</summary>
    [Fact]
    public void SharedDiskSourceIsReadOnceAndRetainsChains()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); child.Block("Valve");
        main.Reference("B1", null, child.Filename);
        main.Reference("B2", null, Path.Combine(_root, ".", "b.dwg"));
        using (var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal))
        {
            catalog.Discover(); var definitions = catalog.ReadNames().Entries;
            var item = Assert.Single(definitions);
            Assert.Equal(2, item.Source.Chains.Count);
            Assert.Equal(1, Database.Reads[child.Filename]);
            Assert.Equal("磁盘只读数据", item.Source.DataSource);
            Assert.True(catalog.ScopeComplete);
        }
        Assert.All(Database.Created.Where(db => !db.Fixture), db => Assert.True(db.IsDisposed));
        Assert.False(main.IsDisposed); Assert.False(child.IsDisposed);
        Assert.All(Database.ReadModes, mode =>
        { Assert.Equal(FileOpenMode.OpenForReadAndAllShare, mode.Mode); Assert.Equal("", mode.Password); });
    }

    /// <summary>已加载外参使用宿主当前数据库，不重新读取磁盘文件。</summary>
    [Fact]
    public void LoadedSourceUsesHostData()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); child.Block("LiveOnly");
        main.Reference("B", child, child.Filename);
        main.GraphDatabases.Add(child);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var item = Assert.Single(catalog.ReadNames().Entries);
        Assert.Equal("LiveOnly", item.Name);
        Assert.Equal("宿主当前数据", item.Source.DataSource); Assert.Empty(Database.Reads);
    }

    /// <summary>同路径不同数据库状态分别保留，不按文件名误合并。</summary>
    [Fact]
    public void SamePathDifferentLiveStatesAreNotMerged()
    {
        var main = Source("A.dwg"); var first = Source("b.dwg"); first.Block("Valve");
        var second = Source("b.dwg"); second.Block("Valve");
        main.Reference("B1", first, first.Filename); main.Reference("B2", second, second.Filename);
        main.GraphDatabases.AddRange([first, second]);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var definitions = catalog.ReadNames().Entries;
        Assert.Equal(2, definitions.Count);
        Assert.Equal(2, definitions.Select(d => d.Source.Key).Distinct().Count());
        Assert.Empty(Database.Reads);
    }

    /// <summary>无法确定同路径多个宿主状态时，不回退磁盘冒充当前数据。</summary>
    [Fact]
    public void AmbiguousLiveStateIsIncomplete()
    {
        var main = Source("A.dwg"); var first = Source("b.dwg"); var second = Source("b.dwg");
        main.GraphDatabases.AddRange([first, second]); main.Reference("B", null, first.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        Assert.False(catalog.ScopeComplete); Assert.Empty(Database.Reads);
    }

    /// <summary>根覆盖纳入，子覆盖排除，子附着继续传播。</summary>
    [Fact]
    public void DirectOverlayAndNestedAttachmentFollowBoundary()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); var attached = Source("c.dwg"); var overlay = Source("d.dwg");
        child.Block("B"); attached.Block("C"); overlay.Block("D");
        main.Reference("B", child, child.Filename, true);
        child.Reference("C", attached, attached.Filename);
        child.Reference("D", overlay, overlay.Filename, true);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        Assert.Equal(new[] { "B", "C" }, catalog.ReadNames().Entries.Select(d => d.Name).Order());
        Assert.True(catalog.ScopeComplete); Assert.Empty(Database.Reads);
    }

    /// <summary>循环关系报告不完整，避免无限遍历或重复读源。</summary>
    [Fact]
    public void CircularReferencesStopExpansionAndKeepKnownSources()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); child.Block("Valve");
        main.Reference("B", child, child.Filename); child.Reference("A", main, main.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        Assert.False(catalog.ScopeComplete);
        Assert.Single(catalog.ReadNames().Entries);
    }

    /// <summary>读取失败也缓存，同源不同引用链不重复触发 SDK 读取。</summary>
    [Fact]
    public void FailedDiskReadIsCachedAndOwnedDatabaseDisposed()
    {
        var main = Source("A.dwg"); var child = Source("lost.dwg");
        Database.Failures[child.Filename] = Autodesk.AutoCAD.Runtime.ErrorStatus.FileNotFound;
        main.Reference("B1", null, child.Filename); main.Reference("B2", null, child.Filename);
        using (var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal))
        { catalog.Discover(); Assert.False(catalog.ScopeComplete); }
        Assert.Equal(1, Database.Reads[child.Filename]);
        Assert.All(Database.Created.Where(db => !db.Fixture), db => Assert.True(db.IsDisposed));
    }

    /// <summary>未插入命名块仍列出；仅被未使用父块引用不算从模型／布局实际可达。</summary>
    [Fact]
    public void UnusedDefinitionsAndReferenceReachabilityRemainSeparate()
    {
        var main = Source("A.dwg");
        var unused = main.Block("Unused"); var used = main.Block("Used");
        var model = (BlockTableRecord)main.Objects[main.Table.Items[0]];
        model.Items.Add(main.Add(new BlockReference { BlockTableRecord = used.Id }));
        unused.Items.Add(main.Add(new BlockReference { BlockTableRecord = unused.Id }));
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var definitions = catalog.ReadNames().Entries;
        Assert.All(main.Objects.Values.OfType<BlockTableRecord>(), b => Assert.Equal(0, b.EntityEnumerations));
        Assert.False(catalog.ReadDefinition(definitions.Single(d => d.Name == "Unused")).Used);
        Assert.True(catalog.ReadDefinition(definitions.Single(d => d.Name == "Used")).Used);
        Assert.Equal(2, definitions.Count);
    }

    /// <summary>文档身份变化不能吞成来源错误后继续。</summary>
    [Fact]
    public void ContextChangePropagatesOutOfDiscovery()
    {
        var main = Source("A.dwg"); main.Block("Valve");
        int calls = 0; bool enabled = false;
        void Ensure()
        {
            if (enabled && ++calls == 4) throw new Pn3DKit.Commands.XrefCheckStoppedException("切换了原图");
        }
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, Ensure, _journal);
        enabled = true;
        Assert.Throws<Pn3DKit.Commands.XrefCheckStoppedException>(() => catalog.Discover());
    }

    /// <summary>旁路懒读取期间文件变化，已读快照也必须改为不完整。</summary>
    [Fact]
    public void FileChangeInvalidatesDiskSnapshots()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); child.Block("Valve");
        main.Reference("B", null, child.Filename);
        XrefBlockSnapshotReader.OnRead = db =>
        { if (db.Filename == child.Filename) File.AppendAllText(child.Filename, "已变化"); };
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var item = Assert.Single(catalog.ReadNames().Entries);
        var error = Assert.Throws<IOException>(() => catalog.ReadDefinition(item));
        Assert.False(catalog.ScopeComplete);
        Assert.Contains("源文件已变化", error.Message);
    }

    /// <summary>SDK UserBreak 作为中断传播，仍释放工具数据库。</summary>
    [Fact]
    public void SdkUserBreakCancelsRatherThanBecomingReadError()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        Database.Failures[child.Filename] = Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak;
        main.Reference("B", null, child.Filename);
        using (var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal))
            Assert.Throws<OperationCanceledException>(() => catalog.Discover());
        Assert.All(Database.Created.Where(db => !db.Fixture), db => Assert.True(db.IsDisposed));
    }

    /// <summary>读取已确认来源命名空间中的转发名称，不把任意最后一段当原名。</summary>
    [Fact]
    public void LoadedNamespaceRestoresKnownOriginalName()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        child.Block("b|Valve").IsDependent = true;
        main.Reference("B", child, child.Filename); main.GraphDatabases.Add(child);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        var item = Assert.Single(catalog.ReadNames().Entries);
        Assert.Equal("Valve", item.Name);
        catalog.ReadDefinition(item);
        Assert.Contains(XrefBlockSnapshotReader.NamespaceReads, names => names.Contains("b"));
    }

    /// <summary>不能还原原名的外参容器不能静默排除后报告范围完整。</summary>
    [Fact]
    public void UnknownReferenceNamespaceMakesScopeIncomplete()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); var nested = Source("c.dwg");
        main.Reference("B", child, child.Filename); main.GraphDatabases.Add(child);
        child.Reference("Unknown|C", nested, nested.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); Assert.False(catalog.ScopeComplete);
    }

    /// <summary>运行真实目录的名称筛选及按需回调；SDK 内容由显式替身提供。</summary>
    private XrefBlockBatchResult RunCatalog(XrefBlockCadCatalog catalog, Action<XrefBlockResult>? append = null)
    {
        var selection = XrefBlockBatch.Select(catalog.ReadNames(), () => { });
        _journal.Selection(selection);
        var result = XrefBlockBatch.Compare(selection, catalog.ReadDefinition, () => catalog.ScopeComplete,
            () => { }, append ?? _journal.Append, (_, _) => { });
        if (result.StopReason == null) catalog.VerifySources();
        return result;
    }

    /// <summary>上千唯一名称及独立匿名项不创建读取器，不枚举实体；少量命名候选才读取。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void LargeCatalogOnlyReadsNamedCandidates(int sharedNames)
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); var other = Source("c.dwg");
        var sources = new[] { main, child, other };
        for (int index = 0; index < sources.Length; index++)
        {
            for (int i = 0; i < 500; i++) sources[index].Block("Unique" + index + "_" + i);
            for (int i = 0; i < 3; i++) sources[index].Block("*U" + i).IsAnonymous = true;
            for (int i = 0; i < sharedNames; i++) sources[index].Block("Shared" + i);
        }
        main.Reference("B", child, child.Filename); main.Reference("C", other, other.Filename);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        var inventory = catalog.ReadNames();
        double scanningMs = timer.Elapsed.TotalMilliseconds;
        Assert.All(sources.SelectMany(db => db.Objects.Values.OfType<BlockTableRecord>()), block =>
        { Assert.Equal(0, block.EntityEnumerations); Assert.Equal(0, block.ReferenceReads); });
        Assert.Equal(0, XrefBlockSnapshotReader.CreatedCount);
        var batch = RunCatalog(catalog);
        Assert.Equal(sharedNames * sources.Length, catalog.RootReads);
        Assert.Equal(sharedNames * sources.Length, XrefBlockSnapshotReader.RootsRead.Count);
        Assert.Equal(sharedNames == 0 ? 0 : sources.Length, catalog.ReadersCreated);
        Assert.Equal(sharedNames == 0 ? 0 : sources.Length, catalog.UsageIndexes);
        Assert.Equal(1500, batch.Selection!.UniqueNames);
        Assert.Equal(sharedNames, batch.Results.Count(x => x.Status == XrefBlockStatus.Consistent));
        Assert.Equal(3, batch.Results.Count(x => x.Status == XrefBlockStatus.AnonymousSkipped));
        foreach (var block in sources.SelectMany(db => db.Objects.Values.OfType<BlockTableRecord>())
            .Where(b => b.Name.StartsWith("Unique") || b.IsAnonymous))
            Assert.Equal(0, block.ReferenceReads);
        _output.WriteLine("XREF_CATALOG_EVIDENCE=" + System.Text.Json.JsonSerializer.Serialize(new
        {
            Sources = sources.Length, Names = inventory.Entries.Count, UniqueNames = batch.Selection.UniqueNames,
            NamedCandidateGroups = sharedNames, AnonymousGroups = 3, SnapshotRootReads = catalog.RootReads,
            ReaderCreations = catalog.ReadersCreated, UsageIndexes = catalog.UsageIndexes, ScanningMs = scanningMs,
            ReadingMs = batch.Metrics!.Reading.TotalMilliseconds, ComparingMs = batch.Metrics.Comparing.TotalMilliseconds,
            Evidence = "真实目录源码与最小 SDK 替身，非 DWG 或 CAD 实测"
        }));
    }

    /// <summary>来源没有命名候选时，不为其建立实体可达索引。</summary>
    [Fact]
    public void UniqueOnlySourceDoesNotBuildUsageIndex()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); var other = Source("c.dwg");
        main.Block("Valve"); child.Block("Valve"); other.Block("Unique");
        main.Reference("B", child, child.Filename); main.Reference("C", other, other.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var result = RunCatalog(catalog);
        Assert.Single(result.Results); Assert.Equal(2, catalog.RootReads); Assert.Equal(2, catalog.UsageIndexes);
        Assert.All(other.Objects.Values.OfType<BlockTableRecord>(), b => Assert.Equal(0, b.EntityEnumerations));
    }

    /// <summary>一个来源复用读取器，同根复用快照和引用统计；结束时先释放读取器。</summary>
    [Fact]
    public void LazyReadersAndRootsAreCachedAndDisposed()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("Valve"); main.Block("Pump"); child.Block("Valve"); child.Block("Pump");
        main.Reference("B", child, child.Filename);
        using (var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal))
        {
            catalog.Discover(); var inventory = catalog.ReadNames();
            Assert.Equal(0, catalog.ReadersCreated);
            foreach (var entry in inventory.Entries)
            {
                var first = catalog.ReadDefinition(entry);
                Assert.Same(first, catalog.ReadDefinition(entry));
            }
            Assert.Equal(4, catalog.RootReads); Assert.Equal(2, catalog.ReadersCreated);
            Assert.Equal(2, catalog.UsageIndexes);
            Assert.All(new[] { main, child }.SelectMany(db => db.Objects.Values.OfType<BlockTableRecord>())
                .Where(b => b.Name is "Valve" or "Pump"), b => Assert.Equal(1, b.ReferenceReads));
        }
        Assert.Equal(2, XrefBlockSnapshotReader.DisposedCount);
        Assert.False(main.IsDisposed); Assert.False(child.IsDisposed);
    }

    /// <summary>SDK 内容读取失败保留 ErrorStatus 并缓存，不重试同根或误判一致。</summary>
    [Fact]
    public void SnapshotFailureKeepsSdkStatus()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("Valve"); child.Block("Valve"); main.Reference("B", child, child.Filename);
        XrefBlockSnapshotReader.OnRead = db =>
        { if (db.Filename == child.Filename) throw new Autodesk.AutoCAD.Runtime.Exception(Autodesk.AutoCAD.Runtime.ErrorStatus.FileNotFound); };
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var result = Assert.Single(RunCatalog(catalog).Results);
        Assert.Equal(XrefBlockStatus.ReadError, result.Status);
        Assert.Contains(result.Issues, text => text.Contains("SDK ErrorStatus=FileNotFound"));
        Assert.Equal(2, catalog.RootReads);
    }

    /// <summary>来源失效后禁止读取缓存或创建新读取器，保留具体停止原因。</summary>
    [Fact]
    public void ClosedSourceStopsBeforeSnapshot()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        child.Block("Valve"); main.Reference("B", child, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var entry = Assert.Single(catalog.ReadNames().Entries);
        child.Dispose();
        Assert.Contains("数据库已关闭或身份变化", Assert.Throws<IOException>(() => catalog.ReadDefinition(entry)).Message);
        Assert.False(catalog.ScopeComplete); Assert.Equal(0, catalog.RootReads);
    }

    /// <summary>仅含唯一名称的磁盘源也要在结束前核对文件状态，不因跳过快照而漏报变化。</summary>
    [Fact]
    public void UniqueDiskSourceChangeIsStillDetected()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        child.Block("Unique"); main.Reference("B", null, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); Assert.Single(catalog.ReadNames().Entries);
        File.AppendAllText(child.Filename, "源文件变化");
        Assert.Contains("源文件已变化", Assert.Throws<IOException>(catalog.VerifySources).Message);
        Assert.False(catalog.ScopeComplete); Assert.Equal(0, catalog.RootReads);
    }

    /// <summary>完整比较仍使用父块快照中的匿名依赖内容，独立匿名根只输出提示。</summary>
    [Fact]
    public void NamedSnapshotRetainsAnonymousDependencyDifferences()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("Valve"); child.Block("Valve");
        main.Block("*U1").IsAnonymous = true; child.Block("*U1").IsAnonymous = true;
        main.Reference("B", child, child.Filename);
        XrefBlockSnapshotReader.CreateSnapshot = (db, id) =>
        {
            Assert.Equal("Valve", ((BlockTableRecord)db.Objects[id]).Name);
            return new("root", new Dictionary<string, BlockContentNode>
            {
                ["root"] = new("root", "命名父块", new Dictionary<string, string>(), [new("匿名依赖", "child")]),
                ["child"] = new("child", "匿名内容", new Dictionary<string, string>
                    { ["半径"] = db.Filename == main.Filename ? "10" : "12" }, [])
            }, []);
        };
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var result = RunCatalog(catalog);
        Assert.Contains(result.Results, x => x.Status == XrefBlockStatus.Conflict);
        Assert.Contains(result.Results, x => x.Status == XrefBlockStatus.AnonymousSkipped);
        Assert.Equal(2, catalog.RootReads);
    }

    /// <summary>第一组落盘后文档变化立即停止，已完成结果及后续未执行数量保留。</summary>
    [Fact]
    public void ContextChangeBetweenCandidatesPreservesCompletedGroup()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("A"); main.Block("B"); child.Block("A"); child.Block("B");
        main.Reference("X", child, child.Filename);
        bool changed = false;
        void Ensure() { if (changed) throw new Pn3DKit.Commands.XrefCheckStoppedException("原图已切换"); }
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, Ensure, _journal);
        catalog.Discover(); var selection = XrefBlockBatch.Select(catalog.ReadNames(), Ensure);
        var result = XrefBlockBatch.Compare(selection, catalog.ReadDefinition, () => catalog.ScopeComplete,
            Ensure, item => { _journal.Append(item); changed = true; }, (_, _) => { });
        Assert.Single(result.Results); Assert.Equal(2, result.Total);
        Assert.Equal(2, catalog.RootReads); Assert.Equal("原图已切换", result.StopReason);
        Assert.True(result.HasIssues);
    }

    /// <summary>只清理本测试创建的替身文件和日志。</summary>
    public void Dispose()
    {
        XrefBlockSnapshotReader.OnRead = null; _journal.Dispose();
        foreach (var database in Database.Created) database.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
