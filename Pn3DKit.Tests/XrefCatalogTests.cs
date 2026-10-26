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
        Database.OnGetObject = null;
        XrefBlockSnapshotReader.Created = 0; XrefBlockSnapshotReader.Disposed = 0;
        XrefBlockSnapshotReader.Reads.Clear(); XrefBlockSnapshotReader.Snapshot = null; XrefBlockSnapshotReader.OnRead = null;
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
            Assert.Equal(2, item.Source.DwgNo);
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
        Assert.Equal(new[] { 2, 3 }, definitions.Select(d => d.Source.DwgNo).Order());
        Assert.Equal("2/3", Assert.Single(Pn3DKit.Utils.XrefBlockNames.Summarize(catalog.CaptureNames()).Normal).DwgNo);
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
        Assert.False(catalog.ScopeComplete);
    }

    /// <summary>关系预遍历不创建名称清单；名称阶段第一次读取块表前表头已经可见。</summary>
    [Fact]
    public void SourceHeaderExistsBeforeBlockTableRead()
    {
        var main = Source("A.dwg"); main.Block("Valve");
        int checkedHeader = 0, censusReads = 0; bool naming = false;
        Database.OnGetObject = (database, id) =>
        {
            if (database != main || id != main.BlockTableId) return;
            if (!naming) { Assert.Empty(Directory.GetFiles(_root, "A__*.csv")); censusReads++; return; }
            string path = Assert.Single(Directory.GetFiles(_root, "A__*.csv"));
            Assert.Equal("块名称,是否匿名\r\n", XrefNameCsvTests.ReadLive(path));
            checkedHeader++;
        };
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(scanStarted: (_, _, _) => naming = true);
        Assert.Equal(1, censusReads); Assert.Equal(1, checkedHeader);
        Assert.Equal("块名称,是否匿名\r\nValve,否\r\n",
            File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "A__*.csv"))));

    }

    /// <summary>名称循环到第 50 行时已可读，子外参尚未进入名称扫描且没有调用 ReadNames。</summary>
    [Fact]
    public void NamesAreVisibleInsideIdentificationLoop()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        for (int i = 0; i < 61; i++) main.Block("Unique" + i);
        main.Reference("B", child, child.Filename); child.Block("Valve");
        bool observed = false;
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover((count, source) =>
        {
            if (count != 50 || source != main.Filename) return;
            observed = true;
            string path = Assert.Single(Directory.GetFiles(_root, "A__*.csv"));
            string text = XrefNameCsvTests.ReadLive(path);
            Assert.Equal(51, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("Unique49,否", text);
            Assert.Empty(Directory.GetFiles(_root, "b__*.csv"));

        });
        Assert.True(observed);
        Assert.Equal(62, File.ReadAllLines(Assert.Single(Directory.GetFiles(_root, "A__*.csv"))).Length);
    }

    /// <summary>空来源也立即生成表头并正常关闭，模型空间和外参容器不进入清单。</summary>
    [Fact]
    public void EmptySourceCreatesHeaderOnlyInventory()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Reference("B", child, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        foreach (string pattern in new[] { "A__*.csv", "b__*.csv" })
        {
            string path = Assert.Single(Directory.GetFiles(_root, pattern));
            Assert.Equal("块名称,是否匿名\r\n", File.ReadAllText(path));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Assert.Contains(",完成,0,0,", XrefNameCsvTests.ReadLive(_journal.SourcesPath));
    }

    /// <summary>同源多链在预遍历中确认，名称扫描时只生成一份清单并带同一个 GUID 记录全部链。</summary>
    [Fact]
    public void SharedSourceCsvAndLateChainsUseSameGuid()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        child.Block("Unique"); child.Block("*U1").IsAnonymous = true;
        main.Reference("B1", child, child.Filename); main.Reference("B2", child, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        string path = Assert.Single(Directory.GetFiles(_root, "b__*.csv"));
        string id = Path.GetFileNameWithoutExtension(path).Split("__")[^1];
        Assert.True(Guid.TryParseExact(id, "N", out _));
        string log = XrefNameCsvTests.ReadLive(_journal.LogPath);
        Assert.Contains("来源引用链：" + id + " | 来源2 | dwgNo=2 | " + main.Filename + " → B2", log);
        Assert.Contains("Unique,否", File.ReadAllText(path));
        Assert.Contains("*U1,是", File.ReadAllText(path));
        Assert.Equal(2, catalog.ReadNames().Entries.Count);
    }

    /// <summary>同路径的两个宿主数据库产生不同清单，索引保留相同路径及独立来源编号。</summary>
    [Fact]
    public void DifferentLiveStatesHaveDistinctNameInventories()
    {
        var main = Source("A.dwg"); var first = Source("b.dwg"); var second = Source("b.dwg");
        first.Block("FirstOnly"); second.Block("SecondOnly");
        main.Reference("B1", first, first.Filename); main.Reference("B2", second, second.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        var paths = Directory.GetFiles(_root, "b__*.csv");
        Assert.Equal(2, paths.Length);
        Assert.Contains(paths, p => File.ReadAllText(p).Contains("FirstOnly"));
        Assert.Contains(paths, p => File.ReadAllText(p).Contains("SecondOnly"));
        foreach (string path in paths)
            Assert.Contains(Path.GetFileName(path), XrefNameCsvTests.ReadLive(_journal.SourcesPath));
    }

    /// <summary>取消、图纸变化或 SDK 读取失败后最终刷新尾部，结束索引记录真实状态。</summary>
    [Theory]
    [InlineData("cancel", "已中断")]
    [InlineData("context", "已停止")]
    [InlineData("sdk", "读取失败")]
    public void PartialNameScanClosesAndRecordsReason(string failure, string status)
    {
        var main = Source("A.dwg");
        for (int i = 0; i < 10; i++) main.Block("Unique" + i);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        void Progress(int count, string source)
        {
            if (count != 10 || source != main.Filename) return;
            if (failure == "cancel") throw new OperationCanceledException("模拟 Esc");
            if (failure == "context") throw new Pn3DKit.Commands.XrefCheckStoppedException("原图已关闭");
            throw new Autodesk.AutoCAD.Runtime.Exception(Autodesk.AutoCAD.Runtime.ErrorStatus.FileNotFound);
        }
        if (failure == "cancel") Assert.Throws<OperationCanceledException>(() => catalog.Discover(Progress));
        else if (failure == "context") Assert.Throws<Pn3DKit.Commands.XrefCheckStoppedException>(() => catalog.Discover(Progress));
        else { catalog.Discover(Progress); Assert.False(catalog.ScopeComplete); }
        string path = Assert.Single(Directory.GetFiles(_root, "A__*.csv"));
        Assert.Equal(11, File.ReadAllLines(path).Length);
        Assert.Contains("," + status + ",10,10,", XrefNameCsvTests.ReadLive(_journal.SourcesPath));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        main.Dispose();
        Database.OnGetObject = (_, _) => throw new InvalidOperationException("停止后不能访问 CAD");
        var partial = catalog.CaptureNames();
        Assert.False(partial.ScopeComplete); Assert.Equal(10, partial.Entries.Count);
        var statistics = Pn3DKit.Utils.XrefBlockNames.Summarize(partial);
        _journal.Complete(new(partial, failure == "cancel", failure == "sdk" ? null : "已停止"), statistics);
        Assert.Equal(11, XrefNameCsvTests.ReadLive(_journal.NormalPath).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("扫描不完整", XrefNameCsvTests.ReadLive(_journal.LogPath));
    }

    /// <summary>清单写入、关闭及创建失败都越过普通来源错误处理，阻止扫描后续外参。</summary>
    [Theory]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("close")]
    public void InventoryIoFailureStopsRemainingSources(string failure)
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); child.Block("Later");
        for (int i = 0; i < 51; i++) main.Block("Unique" + i);
        main.Reference("B", child, child.Filename);
        int created = 0; XrefFaultStream? current = null;
        using var journal = new XrefBlockJournal(Path.Combine(_root, "stream-failure"), main.Filename, new RunLogFiles(),
            new XrefTestTimeProvider(), path =>
            {
                created++;
                if (failure == "create") throw new IOException("模拟创建失败");
                current = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    { FailClose = failure == "close" };
                return current;
            });
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, journal);
        // 名称阶段表头成功写出后再注入故障，关系预遍历不能访问尚未创建的清单。
        Assert.Throws<XrefBlockLogException>(() => catalog.Discover(scanStarted: (_, _, _) =>
        { if (failure == "write") current!.FailWrites = true; }));
        Assert.Equal(1, created);
        Assert.Contains(",写入失败,", XrefNameCsvTests.ReadLive(journal.SourcesPath));
        if (current != null) Assert.True(current.Closed);
    }


    /// <summary>无论同名比例如何，全部名称只读取块表元数据；未插入和匿名块均统计。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void LargeCatalogNeverReadsEntitiesOrReferenceCounts(int sharedNames)
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
        var inventory = catalog.ReadNames(); double scanningMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var statistics = Pn3DKit.Utils.XrefBlockNames.Summarize(inventory);
        _journal.Complete(new(inventory), statistics);
        Assert.Equal(1500 + sharedNames, statistics.Normal.Count);
        Assert.Equal(3, statistics.Anonymous.Count);
        Assert.All(statistics.Anonymous, row => Assert.Equal("1/2/3", row.DwgNo));
        Assert.Equal(new[] { 1, 2, 3 }, inventory.Sources.Select(s => s.DwgNo).Order());
        Assert.All(sources.SelectMany(db => db.Objects.Values.OfType<BlockTableRecord>()), b => Assert.Equal(0, b.EntityEnumerations));
        Assert.False(File.Exists(Path.Combine(_root, "XrefBlockCheck.csv")));
        _output.WriteLine("XREF_NAMES_EVIDENCE=" + System.Text.Json.JsonSerializer.Serialize(new
        {
            Sources = sources.Length, Names = inventory.Entries.Count, SharedNames = sharedNames,
            NormalNames = statistics.Normal.Count, AnonymousNames = statistics.Anonymous.Count,
            EntityEnumerations = 0, ScanningMs = scanningMs, StatisticsMs = timer.Elapsed.TotalMilliseconds,
            Evidence = "真实名称扫描源码与最小 SDK 替身；不读取实体，不是实际 DWG 性能"
        }));
    }

    /// <summary>来源失效时停止核对，但已确认名称可纯内存捕获和报告。</summary>
    [Fact]
    public void ClosedSourceStopsVerificationButPreservesNames()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        child.Block("Valve"); main.Reference("B", child, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); child.Dispose();
        Assert.Contains("数据库已关闭或身份变化", Assert.Throws<IOException>(catalog.VerifySources).Message);
        Assert.False(catalog.ScopeComplete);
        Assert.Equal("Valve", Assert.Single(catalog.CaptureNames().Entries).Name);
    }

    /// <summary>同源多链只计一图，空来源仍占用连续编号；主图从 1 开始。</summary>
    [Fact]
    public void DrawingNumbersAreAllocatedPerSourceNotReferenceChain()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); var other = Source("c.dwg");
        main.Block("Valve"); child.Block("Valve"); other.Block("Valve");
        main.Reference("B1", child, child.Filename); main.Reference("B2", child, child.Filename);
        main.Reference("C", other, other.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var inventory = catalog.ReadNames();
        var row = Assert.Single(Pn3DKit.Utils.XrefBlockNames.Summarize(inventory).Normal);
        Assert.Equal("1/2/3", row.DwgNo); Assert.Equal(3, row.DwgCount);
        Assert.Equal(3, inventory.Sources.Count);
        string index = XrefNameCsvTests.ReadLive(_journal.SourcesPath);
        Assert.StartsWith("dwgNo,来源ID,", index);
        foreach (var group in index.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).GroupBy(s => s.Split(',')[0]))
        {
            Assert.Equal(2, group.Count());
            Assert.Single(group.Select(s => s.Split(',')[1]).Distinct());
        }
    }

    /// <summary>主 LOG 或索引刷新失败均提升为批次故障，不能继续扫描后续外参。</summary>
    [Theory]
    [InlineData("XrefBlockCheck.log")]
    [InlineData("XrefBlockSources.csv")]
    public void MainReportIoFailureStopsRemainingSources(string failedFile)
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("Valve"); child.Block("Later"); main.Reference("B", child, child.Filename);
        var files = new RunLogFiles(); var streams = new Dictionary<string, XrefFaultStream>();
        var directory = Path.Combine(_root, "report-failure");
        var journal = new XrefBlockJournal(directory, main.Filename, files, new XrefTestTimeProvider(),
            createReportFile: path => streams[Path.GetFileName(path)] = new(files.CreateNew(path)));
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, journal);
        Database.OnGetObject = (database, id) =>
        { if (database == main && id == main.BlockTableId) streams[failedFile].FailWrites = true; };
        try
        {
            Assert.Throws<XrefBlockLogException>(() => catalog.Discover());
            Assert.True(journal.HasWriteFailure);
            Assert.Empty(Directory.GetFiles(directory, "b__*.csv"));
            Assert.Equal("块名称,dwgNo,dwg计数\r\n", XrefNameCsvTests.ReadLive(journal.NormalPath));
        }
        finally
        {
            Database.OnGetObject = null;
            streams[failedFile].FailWrites = false;
            journal.Dispose();
        }
    }

    /// <summary>统计已经刷新后才读取候选；无候选时没有内容读取器或根读取。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void OnlyRepeatedNormalNamesReachContentReaders(int shared)
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); var other = Source("c.dwg");
        var sources = new[] { main, child, other };
        for (int index = 0; index < sources.Length; index++)
        {
            for (int i = 0; i < 200; i++) sources[index].Block("Unique" + index + "_" + i);
            sources[index].Block("*U1").IsAnonymous = true;
            for (int i = 0; i < shared; i++) sources[index].Block("Shared" + i);
        }
        main.Reference("B", child, child.Filename); main.Reference("C", other, other.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var inventory = catalog.ReadNames();
        var statistics = Pn3DKit.Utils.XrefBlockNames.Summarize(inventory);
        _journal.WriteStatistics(inventory, statistics);
        XrefBlockSnapshotReader.OnRead = (database, id) =>
        {
            Assert.Contains("Unique0_0,1,1", XrefNameCsvTests.ReadLive(_journal.NormalPath));
            Assert.Contains("*U1,1/2/3,3", XrefNameCsvTests.ReadLive(_journal.AnonymousPath));
            Assert.False(database.IsDisposed);
            Assert.StartsWith("Shared", ((BlockTableRecord)database.Objects[id]).Name);
        };
        var candidates = Pn3DKit.Utils.XrefBlockComparison.Candidates(inventory, statistics);
        var results = new List<XrefBlockComparisonResult>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        XrefBlockComparisonBatch.Run(candidates, catalog.ReadDefinition, () => catalog.ScopeComplete, () => { },
            _journal.AppendComparison, results);
        _journal.Complete(new(inventory), statistics, results, catalog.RootReads, catalog.ReadersCreated);
        Assert.Equal(shared, candidates.Count);
        Assert.Equal(shared * 3, catalog.RootReads);
        Assert.Equal(shared == 0 ? 0 : 3, catalog.ReadersCreated);
        Assert.Equal(shared * 3, XrefBlockSnapshotReader.Reads.Count);
        Assert.All(results, r => Assert.Equal(XrefBlockComparisonStatus.Consistent, r.Status));
        Assert.Equal(0, XrefBlockSnapshotReader.Disposed);
        _output.WriteLine("XREF_COMPARISON_EVIDENCE=" + System.Text.Json.JsonSerializer.Serialize(new
        {
            Sources = 3, UniqueNames = 600, AnonymousNames = 1, CandidateNames = candidates.Count,
            RootsRead = catalog.RootReads, ContentReaders = catalog.ReadersCreated,
            ComparisonMs = timer.Elapsed.TotalMilliseconds, Evidence = "生产目录／候选／报告源码加内容替身，非 DWG 实测"
        }));
    }

    /// <summary>同一根复用快照，读取器先于工具数据库释放；宿主数据库保持打开。</summary>
    [Fact]
    public void DefinitionCacheAndReaderLifetimeAreScopedToSource()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("Valve"); child.Block("Valve"); main.Reference("B", null, child.Filename);
        using (var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal))
        {
            catalog.Discover(); var entries = catalog.ReadNames().Entries;
            foreach (var entry in entries)
            {
                var first = catalog.ReadDefinition(entry);
                Assert.Same(first.Content, catalog.ReadDefinition(entry).Content);
            }
            Assert.Equal(2, catalog.RootReads); Assert.Equal(2, catalog.ReadersCreated);
            catalog.Dispose(); catalog.Dispose();
        }
        Assert.Equal(2, XrefBlockSnapshotReader.Disposed);
        Assert.False(main.IsDisposed);
        Assert.All(Database.Created.Where(db => !db.Fixture), db => Assert.True(db.IsDisposed));
    }

    /// <summary>普通 SDK 读取错误只使对应内容未知，UserBreak 和文档停止必须传播。</summary>
    [Theory]
    [InlineData("sdk")]
    [InlineData("cancel")]
    [InlineData("context")]
    public void ContentReadFailuresKeepTheirBatchMeaning(string failure)
    {
        var main = Source("A.dwg"); main.Block("Valve");
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var entry = Assert.Single(catalog.ReadNames().Entries);
        XrefBlockSnapshotReader.OnRead = (_, _) =>
        {
            if (failure == "context") throw new Pn3DKit.Commands.XrefCheckStoppedException("原图关闭");
            throw new Autodesk.AutoCAD.Runtime.Exception(failure == "cancel"
                ? Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak : Autodesk.AutoCAD.Runtime.ErrorStatus.FileNotFound);
        };
        if (failure == "context") Assert.Throws<Pn3DKit.Commands.XrefCheckStoppedException>(() => catalog.ReadDefinition(entry));
        else if (failure == "cancel") Assert.Throws<OperationCanceledException>(() => catalog.ReadDefinition(entry));
        else Assert.Contains("SDK ErrorStatus=FileNotFound", catalog.ReadDefinition(entry).Content.ReadError);
    }

    /// <summary>先完整发现来源再扫描名称，分母固定、主图优先，多链不重复加载，块表最多两阶段各一次。</summary>
    [Fact]
    public void CensusFreezesSourceTotalBeforeFirstNameInventory()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); var nested = Source("c.dwg");
        main.Block("Main"); child.Block("Child"); nested.Block("Nested");
        main.Reference("B1", null, child.Filename); main.Reference("B2", null, child.Filename);
        child.Reference("C", null, nested.Filename);
        var tableReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Database.OnGetObject = (db, id) =>
        {
            if (id == db.BlockTableId) tableReads[db.Filename] = tableReads.GetValueOrDefault(db.Filename) + 1;
        };
        var found = new List<int>(); var scans = new List<(int Index, int Total, string Name)>();
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(sourceFound: (count, _) =>
        {
            found.Add(count); Assert.Empty(Directory.GetFiles(_root, "*__*.csv"));
        }, scanStarted: (index, total, path) =>
        {
            scans.Add((index, total, Path.GetFileName(path)));
            if (index == 1)
            {
                Assert.Equal(3, tableReads.Count);
                Assert.All(tableReads.Values, value => Assert.Equal(1, value));
                Assert.Single(Directory.GetFiles(_root, "*__*.csv"));
            }
        });
        Assert.Equal(new[] { 1, 2, 3 }, found);
        Assert.Equal(new[] { (1, 3, "A.dwg"), (2, 3, "b.dwg"), (3, 3, "c.dwg") }, scans);
        Assert.All(tableReads.Values, value => Assert.Equal(2, value));
        Assert.Equal(1, Database.Reads[child.Filename]); Assert.Equal(1, Database.Reads[nested.Filename]);
        Assert.Equal(0, catalog.RootReads); Assert.Equal(0, catalog.ReadersCreated);
        var inventory = catalog.ReadNames();
        Assert.Equal(new[] { 1, 2, 3 }, inventory.Sources.Select(s => s.DwgNo));
        Assert.Equal(2, inventory.Sources.Single(s => s.DwgNo == 2).Chains.Count);
        _output.WriteLine("XREF_CENSUS_EVIDENCE=" + System.Text.Json.JsonSerializer.Serialize(new
        { Sources = scans.Count, Totals = scans.Select(s => s.Total), TableReads = tableReads,
          DiskLoads = Database.Reads, RootReads = catalog.RootReads, Readers = catalog.ReadersCreated,
          Evidence = "生产检索源码与 SDK 替身，不是实际 DWG 耗时" }));
    }

    /// <summary>同路径不同内存状态分别计数，不因同名物理文件合并分母。</summary>
    [Fact]
    public void CensusCountsDistinctHostStatesAtSamePath()
    {
        var main = Source("A.dwg"); var child = Source("shared.dwg");
        var other = new Database(false, true) { Filename = child.Filename, Fixture = true };
        child.Block("First"); other.Block("Second");
        main.Reference("B1", child, child.Filename); main.Reference("B2", other, child.Filename);
        var totals = new List<int>();
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(scanStarted: (_, total, _) => totals.Add(total));
        Assert.Equal(new[] { 3, 3, 3 }, totals); Assert.Empty(Database.Reads);
        Assert.Equal(3, catalog.ReadNames().Sources.Select(s => s.DwgNo).Distinct().Count());
    }

    /// <summary>不可读来源单独记录，总数只覆盖已确认数据库，不能宣告范围完整。</summary>
    [Fact]
    public void CensusMissingSourceKeepsKnownTotalAndIncompleteScope()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Reference("B", child, child.Filename);
        main.Reference("Missing", null, Path.Combine(_root, "lost.dwg"));
        var totals = new List<int>();
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(scanStarted: (_, total, _) => totals.Add(total));
        Assert.Equal(new[] { 2, 2 }, totals);
        Assert.False(catalog.ReadNames().ScopeComplete);
        Assert.Contains("总数仅覆盖已确认来源", XrefNameCsvTests.ReadLive(_journal.LogPath));
    }

    /// <summary>发现阶段中断不创建名称文件；已发现但未扫描的来源仍可纯内存追溯。</summary>
    [Fact]
    public void CensusCancelKeepsRelationsWithoutNameFiles()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); main.Reference("B", null, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        Assert.Throws<OperationCanceledException>(() => catalog.Discover(sourceFound: (count, _) =>
        { if (count == 2) throw new OperationCanceledException("来源发现取消"); }));
        Assert.Empty(Directory.GetFiles(_root, "*__*.csv"));
        var inventory = catalog.CaptureNames();
        Assert.False(inventory.ScopeComplete); Assert.Empty(inventory.Entries);
        Assert.Equal(2, inventory.Sources.Count); Assert.All(inventory.Sources, s => Assert.Equal(0, s.DwgNo));
        _journal.Complete(new(inventory, true, "来源发现取消"), Pn3DKit.Utils.XrefBlockNames.Summarize(inventory));
        Assert.Contains("未扫描来源", XrefNameCsvTests.ReadLive(_journal.LogPath));
    }

    /// <summary>名称阶段中断保留已确认行及固定总数，未扫描来源不冒充空图纸。</summary>
    [Fact]
    public void NamingCancelPreservesFixedTotalAndUnscannedSources()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("First"); child.Block("Later"); main.Reference("B", child, child.Filename);
        var totals = new List<int>();
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        Assert.Throws<OperationCanceledException>(() => catalog.Discover(
            progress: (_, source) => { if (source == main.Filename) throw new OperationCanceledException(); },
            scanStarted: (_, total, _) => totals.Add(total)));
        Assert.Equal(new[] { 2 }, totals); Assert.Empty(Directory.GetFiles(_root, "b__*.csv"));
        var inventory = catalog.CaptureNames();
        Assert.Single(inventory.Entries); Assert.False(inventory.ScopeComplete);
        Assert.Equal(0, inventory.Sources.Single(s => s.Drawing == child.Filename).DwgNo);
    }

    /// <summary>冻结后外参新增、删除、目标状态或覆盖规则变化均停止，不扩大总数继续扫描。</summary>
    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("retarget")]
    [InlineData("overlay")]
    public void ReferenceChangeBetweenStagesStopsInsteadOfChangingTotal(string change)
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); var other = Source("c.dwg");
        main.Reference("B", child, child.Filename);
        var reference = main.Objects.Values.OfType<BlockTableRecord>().Single(b => b.IsFromExternalReference);
        var totals = new List<int>();
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        var error = Assert.Throws<Pn3DKit.Commands.XrefCheckStoppedException>(() => catalog.Discover(
            scanStarted: (_, total, _) =>
            {
                totals.Add(total);
                if (change == "add") main.Reference("C", other, other.Filename);
                else if (change == "remove") main.Table.Items.Remove(reference.Id);
                else if (change == "retarget") reference.Loaded = other;
                else reference.IsFromOverlayReference = true;
            }));
        Assert.Contains("外参关系已变化", error.Message); Assert.Equal(new[] { 2 }, totals);
        Assert.False(catalog.ScopeComplete); Assert.Empty(catalog.CaptureNames().Entries);
        Assert.Empty(Directory.GetFiles(_root, "b__*.csv"));
    }

    /// <summary>来源文件在预遍历后变化时在该图名称扫描之前停止，已完成图纸仍保留。</summary>
    [Fact]
    public void DiskChangeBetweenStagesStopsBeforeSourceNames()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("First"); child.Block("Later"); main.Reference("B", null, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        Assert.Throws<IOException>(() => catalog.Discover(scanStarted: (index, _, _) =>
        { if (index == 1) File.AppendAllText(child.Filename, "来源变化"); }));
        Assert.Single(catalog.CaptureNames().Entries);
        Assert.Empty(Directory.GetFiles(_root, "b__*.csv")); Assert.False(catalog.ScopeComplete);
        Assert.Equal(1, Database.Reads[child.Filename]);
    }

    /// <summary>视觉候选只定位定义，不读取旧快照、使用索引或独立匿名根。</summary>
    [Fact]
    public void VisualLocationDoesNotReadContentSnapshots()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg");
        main.Block("Valve"); main.Block("Unique"); child.Block("Valve"); child.Block("*U1").IsAnonymous = true;
        main.Reference("B", child, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover();
        var entries = catalog.ReadNames().Entries.Where(e => e.Name == "Valve").ToArray();
        foreach (var entry in entries)
        {
            var location = catalog.LocateVisual(entry);
            catalog.VerifyVisual(entry, location.Definition);
            Assert.True(location.Definition.IsValid);
        }
        Assert.Equal(2, catalog.VisualPrepares); Assert.Equal(0, catalog.RootReads);
        Assert.Equal(0, catalog.ReadersCreated); Assert.Equal(0, XrefBlockSnapshotReader.Created);
    }

    /// <summary>视觉准备前改名属于来源身份变化，不能降为普通未验证后继续。</summary>
    [Fact]
    public void VisualRenamedTargetStops()
    {
        var main = Source("A.dwg"); main.Block("Valve");
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var entry = catalog.ReadNames().Entries.Single();
        main.Objects.Values.OfType<BlockTableRecord>().Single(b => b.Name == "Valve").Name = "Other";
        Assert.Throws<Pn3DKit.Commands.XrefCheckStoppedException>(() => catalog.LocateVisual(entry));
        Assert.Equal(0, catalog.VisualPrepares);
    }

    /// <summary>视觉绘制前源文件变化必须停止，不能继续使用过期的旁路内容。</summary>
    [Fact]
    public void VisualDiskChangeStops()
    {
        var main = Source("A.dwg"); var child = Source("b.dwg"); child.Block("Valve");
        main.Reference("B", null, child.Filename);
        using var catalog = new XrefBlockCadCatalog(main, main.Filename, () => { }, _journal);
        catalog.Discover(); var entry = catalog.ReadNames().Entries.Single();
        File.AppendAllText(child.Filename, "change");
        Assert.Throws<Pn3DKit.Commands.XrefCheckStoppedException>(() => catalog.LocateVisual(entry));
        Assert.False(catalog.ScopeComplete);
    }

    /// <summary>只清理本测试创建的替身文件和日志。</summary>
    public void Dispose()
    {
        Database.OnGetObject = null; _journal.Dispose();
        foreach (var database in Database.Created) database.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
