using System.Text.Json;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>通过真实临时文件和离线宿主验证插件策略，不加载 Autodesk 或连接 CAD。</summary>
public sealed class Pn3DKitTests
{
    /// <summary>类型识别使用清单路径，默认值与显式关键字均符合命令契约。</summary>
    [Fact]
    public void DetectPartUsesProjectListsAndDefaultsToPnId()
    {
        using var fixture = new Fixture();
        string pnid = fixture.Add("PnId/a.dwg"), piping = fixture.Add("Models/b.dwg");
        var catalog = new ProjectCatalog(fixture.Project, [pnid], [piping]);
        Assert.Equal(ProjectPartKind.PnId, ExportPolicy.DetectPart(catalog, pnid.ToUpperInvariant()));
        Assert.Equal(ProjectPartKind.Piping, ExportPolicy.DetectPart(catalog, piping));
        Assert.Equal(ProjectPartKind.PnId, ExportPolicy.DetectPart(catalog, null));
        Assert.Equal(ProjectPartKind.PnId, ExportPolicy.DetectPart(catalog, Path.Combine(fixture.Root, "outside.dwg")));
        Assert.Equal(ProjectPartKind.PnId, ExportPolicy.ParsePart("p"));
        Assert.Equal(ProjectPartKind.Piping, ExportPolicy.ParsePart("D"));
        Assert.Throws<ArgumentException>(() => ExportPolicy.ParsePart("Piping"));
    }

    /// <summary>SAVEAS 式点击关键字、直接年份和回车均解析为导出格式。</summary>
    [Theory]
    [InlineData(null, "2013")]
    [InlineData("", "2013")]
    [InlineData("  ", "2013")]
    [InlineData("2007", "2007")]
    [InlineData("LT2010", "2010")]
    [InlineData("lt2013", "2013")]
    [InlineData("_LT2018", "2018")]
    [InlineData("  2013(LT2013)  ", "2013")]
    public void SaveAsSelectionAcceptsKeywordsYearsAndDefault(string? input, string expected)
    {
        Assert.True(DwgVersionSelection.TryParse(input, out string year));
        Assert.Equal(expected, year);
    }

    /// <summary>SAVEAS 的其他格式不能绕过 Plant 导出能力或被静默当成默认版本。</summary>
    [Theory]
    [InlineData("R14")]
    [InlineData("2000")]
    [InlineData("2004")]
    [InlineData("2026")]
    [InlineData("DXF")]
    [InlineData("_")]
    [InlineData("2013(LT2018)")]
    public void SaveAsSelectionRejectsUnsupportedOrMismatchedFormats(string input)
    {
        Assert.False(DwgVersionSelection.TryParse(input, out string year));
        Assert.Empty(year);
    }

    /// <summary>DWG 格式年份与文件头代码映射保持一致。</summary>
    [Theory]
    [InlineData("2000", "AC1015")]
    [InlineData("2004", "AC1018")]
    [InlineData("2007", "AC1021")]
    [InlineData("2010", "AC1024")]
    [InlineData("2013", "AC1027")]
    [InlineData("2018", "AC1032")]
    public void VersionMappingMatchesDwgHeader(string year, string code) =>
        Assert.Equal(code, ExportPolicy.Versions[year]);

    /// <summary>Plant 格式使用资源中的完整全局关键字，拒绝命令未提供的早期格式。</summary>
    [Theory]
    [InlineData("2007", "_2007(LT2007)")]
    [InlineData("2010", "_2010(LT2010)")]
    [InlineData("2013", "_2013(LT2013)")]
    [InlineData("2018", "_2018(LT2018)")]
    public void CommandArgumentsUseCompleteGlobalKeywords(string year, string keyword)
    {
        Assert.Equal(new object[] { "_.-EXPORTTOAUTOCAD", keyword, "output.dwg" },
            ExportPolicy.ExportArguments("output.dwg", year));
        Assert.Throws<ArgumentException>(() => ExportPolicy.ExportArguments("output.dwg", "2000"));
    }

    /// <summary>LISP 使用三项 Plant 命令参数，正确保留中文与空格路径。</summary>
    [Theory]
    [InlineData("2013", "_2013(LT2013)")]
    [InlineData("2018", "_2018(LT2018)")]
    public void LispExportUsesPlantPromptOrderAndRestoresVariables(string version, string keyword)
    {
        string command = CadLispCommands.Export(@"C:\输出\PID DWG\a.dwg", @"C:\日志\done.txt", version);
        Assert.Contains("(vl-catch-all-apply 'command-s (list \"_.-EXPORTTOAUTOCAD\" \"" + keyword + "\" \"C:\\\\输出\\\\PID DWG\\\\a.dwg\"))", command);
        Assert.DoesNotContain("\"_F\"", command);
        Assert.DoesNotContain("\"\"", command);
        Assert.Contains("(setvar \"FILEDIA\" fd)", command);
        Assert.Contains("(setvar \"CMDDIA\" cd)", command);
        Assert.Contains("(setvar \"PROXYNOTICE\" pn)", command);
        Assert.Contains("\"DONE\"", command);
        Assert.Contains("\"ERROR: \"", command);
        Assert.Contains("(close h)", command);
    }

    /// <summary>独立保存表达式不混入导出，并正确转义路径引号及拒绝空字符。</summary>
    [Fact]
    public void LispSaveIsIndependentAndEscapesQuotedPaths()
    {
        string command = CadLispCommands.Save(@"C:\logs\save.status.txt");
        Assert.Contains("(list \"_.QSAVE\")", command);
        Assert.DoesNotContain("EXPORTTOAUTOCAD", command);
        Assert.Equal("\"a\\\"b\\\\c\"", CadLispCommands.Quote("a\"b\\c"));
        Assert.Throws<ArgumentException>(() => CadLispCommands.Quote("bad\0path"));
    }

    /// <summary>实际 DWG 版本与选择不符时保留临时输出，不发布正式文件。</summary>
    [Fact]
    public async Task WrongDwgHeaderNeverPublishes()
    {
        using var fixture = new Fixture();
        fixture.Add("source.dwg");
        fixture.Host.ExportContent = "AC1032converted";
        var result = await fixture.Run();
        Assert.Equal("Failed", result.Items[0].Status);
        Assert.Contains("AC1027", result.Items[0].Message);
        Assert.False(File.Exists(result.Items[0].Destination));
        Assert.True(File.Exists(fixture.Host.LastTemporary));
    }

    /// <summary>目录建议不创建文件夹，同秒建议避开历史目录，确认后不擅自改名。</summary>
    [Fact]
    public void BatchDirectoryContainsMinutesAndReservesUniqueNames()
    {
        using var fixture = new Fixture();
        string path = ExportPolicy.SuggestDirectory(fixture.Root, new DateTime(2026, 10, 24, 15, 30, 45));
        Assert.EndsWith(Path.Combine("Pn3DKit.Export", "261024153045"), path);
        Assert.False(Directory.Exists(path));
        Assert.Equal(path, ExportPolicy.ReserveDirectory(path));
        File.WriteAllText(Path.Combine(path, "keep.txt"), "keep");
        Assert.Throws<IOException>(() => ExportPolicy.ReserveDirectory(path));
        Assert.Equal(path + "_01", ExportPolicy.SuggestDirectory(fixture.Root, new DateTime(2026, 10, 24, 15, 30, 45)));
        Assert.False(Directory.Exists(path + "_01"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "keep.txt")));
    }

    /// <summary>选择已有目录时直接导出到其中，保留已有输出并在相同目录写入日志。</summary>
    [Fact]
    public async Task SelectedExistingDirectoryIsUsedExactlyAndExistingDrawingIsSkipped()
    {
        using var fixture = new Fixture();
        fixture.Add("area/keep.dwg", modified: true);
        fixture.Add("area/new.dwg");
        string selected = Path.Combine(fixture.Root, "用户选择的目录");
        Directory.CreateDirectory(Path.Combine(selected, "area"));
        string existing = Path.Combine(selected, "area", "keep.dwg");
        File.WriteAllText(existing, "existing-output");
        var options = fixture.Options() with { OutputDirectory = selected };
        var result = await new BatchExporter().RunAsync(new(fixture.Project, fixture.Sources, null),
            options, fixture.Host, _ => { });
        Assert.Equal(["Skipped", "Exported"], result.Items.Select(x => x.Status));
        Assert.Equal("existing-output", File.ReadAllText(existing));
        Assert.True(File.Exists(Path.Combine(selected, "area", "new.dwg")));
        Assert.True(File.Exists(Path.Combine(selected, "Export.log")));
        Assert.Equal(Path.Combine(selected, ".pn3dkit-logs", "report.json"), result.ReportPath);
        Assert.False(Directory.Exists(selected + "_01"));
        Assert.Single(fixture.Host.Opened);
        Assert.Equal(0, fixture.Host.Saves);
    }

    /// <summary>已存在的日志或标记在打开源图之前阻止批次，不能覆盖或偷偷换目录。</summary>
    [Theory]
    [InlineData("Export.log")]
    [InlineData(".pn3dkit-run")]
    [InlineData(".pn3dkit-logs")]
    public async Task SelectedDirectoryWithBatchMetadataStopsBeforeCad(string name)
    {
        using var fixture = new Fixture();
        fixture.Add("source.dwg", modified: true);
        string selected = Path.Combine(fixture.Root, "selected");
        Directory.CreateDirectory(selected);
        string existing = Path.Combine(selected, name);
        File.WriteAllText(existing, "keep");
        var options = fixture.Options() with { OutputDirectory = selected };
        await Assert.ThrowsAsync<IOException>(() => new BatchExporter().RunAsync(
            new(fixture.Project, fixture.Sources, null), options, fixture.Host, _ => { }));
        Assert.Empty(fixture.Host.Opened);
        Assert.Equal(0, fixture.Host.Saves);
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.False(Directory.Exists(selected + "_01"));
    }

    /// <summary>预检以及开始前取消不创建所选目录，也不接触源图。</summary>
    [Fact]
    public async Task DirectoryValidationAndPreBatchCancellationDoNotCreateFiles()
    {
        using var fixture = new Fixture();
        fixture.Add("source.dwg");
        string selected = Path.Combine(fixture.Root, "not-created");
        Assert.Equal(selected, ExportPolicy.ValidateOutputDirectory(selected));
        Assert.False(Directory.Exists(selected));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = fixture.Options() with { OutputDirectory = selected };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BatchExporter().RunAsync(
            new(fixture.Project, fixture.Sources, null), options, fixture.Host, _ => { }, cancellation.Token));
        Assert.False(Directory.Exists(selected));
        Assert.Empty(fixture.Host.Opened);
    }

    /// <summary>路径映射去重、保留目录并隔离外部图纸，输出不能覆盖源图。</summary>
    [Fact]
    public void JobsPreserveDirectoriesDeduplicateAndIsolateExternalFiles()
    {
        using var fixture = new Fixture();
        string first = fixture.Add("one/same.dwg"), second = fixture.Add("two/same.dwg");
        string external = Path.Combine(fixture.Root, "outside.dwg");
        var catalog = new ProjectCatalog(fixture.Project, [first, first.ToUpperInvariant(), second, external], null);
        var options = fixture.Options();
        var jobs = ExportPolicy.CreateJobs(catalog, options);
        Assert.Equal(3, jobs.Count);
        Assert.EndsWith(Path.Combine("one", "same.dwg"), jobs[0].Destination);
        Assert.EndsWith(Path.Combine("two", "same.dwg"), jobs[1].Destination);
        Assert.Contains(Path.DirectorySeparatorChar + "_External" + Path.DirectorySeparatorChar, jobs[2].Destination);
        Assert.All(jobs, j => Assert.True(ExportPolicy.Inside(j.Destination, options.OutputDirectory)));
        Assert.Throws<IOException>(() => ExportPolicy.CreateJobs(catalog, options with { OutputDirectory = fixture.Project }));
        Assert.Throws<ArgumentException>(() => ExportPolicy.CreateJobs(catalog, options with { Version = "2026" }));
        Assert.Throws<InvalidOperationException>(() => ExportPolicy.CreateJobs(catalog, options with { Part = ProjectPartKind.Piping }));
    }

    /// <summary>项目内部路径与外部映射发生重名时编号消解。</summary>
    [Fact]
    public void JobDestinationCollisionsReceiveSuffix()
    {
        using var fixture = new Fixture();
        string external = Path.Combine(fixture.Root, "outside.dwg");
        var single = new ProjectCatalog(fixture.Project, [external], null);
        string mapped = ExportPolicy.CreateJobs(single, fixture.Options())[0].Destination;
        string internalSource = fixture.Add(Path.GetRelativePath(fixture.Options().OutputDirectory, mapped));
        var jobs = ExportPolicy.CreateJobs(single with { PnIdDrawings = [external, internalSource] }, fixture.Options());
        Assert.NotEqual(jobs[0].Destination, jobs[1].Destination);
        Assert.EndsWith("outside_01.dwg", jobs[1].Destination);
    }

    /// <summary>N 跳过有修改图纸且继续导出其他图，已打开图纸保持打开。</summary>
    [Fact]
    public async Task NoSkipsDirtyDrawingAndContinuesWithoutSaving()
    {
        using var fixture = new Fixture();
        string dirty = fixture.Add("dirty.dwg", modified: true, alreadyOpen: true);
        string clean = fixture.Add("clean.dwg");
        var result = await fixture.Run(save: false);
        Assert.Equal(["Skipped", "Exported"], result.Items.Select(x => x.Status));
        Assert.Equal(0, fixture.Host.Saves);
        Assert.Equal(1, fixture.Host.Exports);
        Assert.DoesNotContain(dirty, fixture.Host.Closed);
        Assert.Contains(clean, fixture.Host.Closed);
        Assert.Same(fixture.Host.OriginalDrawing, fixture.Host.Active);
    }

    /// <summary>Y 仅保存有修改的图纸一次，已保存的只读图纸可以直接导出。</summary>
    [Fact]
    public async Task YesSavesDirtyOnceAndExportsSavedReadOnlyDrawing()
    {
        using var fixture = new Fixture();
        fixture.Add("dirty.dwg", modified: true);
        string readOnly = fixture.Add("read-only.dwg", readOnly: true);
        var result = await fixture.Run();
        Assert.All(result.Items, x => Assert.Equal("Exported", x.Status));
        Assert.Equal(1, fixture.Host.Saves);
        Assert.Equal(2, fixture.Host.Exports);
        Assert.Equal("original", File.ReadAllText(readOnly));
    }

    /// <summary>只读且有修改的图纸失败，但不影响其余图纸。</summary>
    [Fact]
    public async Task DirtyReadOnlyDrawingIsNeverSavedOrExported()
    {
        using var fixture = new Fixture();
        fixture.Add("dirty-read-only.dwg", modified: true, readOnly: true);
        fixture.Add("clean.dwg");
        var result = await fixture.Run();
        Assert.Equal(["Failed", "Exported"], result.Items.Select(x => x.Status));
        Assert.Equal(0, fixture.Host.Saves);
        Assert.Equal(1, fixture.Host.Exports);
    }

    /// <summary>保存失败或保存后仍有修改都禁止导出该图，且不重试保存。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveDoesNotExportAndNeverRetries(bool throws)
    {
        using var fixture = new Fixture();
        string dirty = fixture.Add("dirty.dwg", modified: true);
        fixture.Host.SaveClearsDirty = false;
        if (throws) fixture.Host.SaveFailure = new CadOperationException("保存失败", true, false);
        fixture.Add("clean.dwg");
        var result = await fixture.Run();
        Assert.Equal(["Failed", "Exported"], result.Items.Select(x => x.Status));
        Assert.Equal(1, fixture.Host.Saves);
        Assert.Equal(1, fixture.Host.Exports);
        Assert.DoesNotContain(dirty, fixture.Host.Closed);
    }

    /// <summary>导出到非空临时文件后才发布，并输出根目录 Export.log 与四字段报告。</summary>
    [Fact]
    public async Task PublishesNonEmptyFileAndWritesRequestedLogAndReport()
    {
        using var fixture = new Fixture();
        string source = fixture.Add("area/a.dwg", alreadyOpen: true);
        var result = await fixture.Run();
        Assert.Equal("AC1027converted", File.ReadAllText(result.Items[0].Destination));
        Assert.Equal("original", File.ReadAllText(source));
        Assert.Empty(fixture.Host.Closed);
        Assert.False(File.Exists(fixture.Host.LastTemporary));
        string batchDirectory = Directory.GetParent(Path.GetDirectoryName(result.ReportPath)!)!.FullName;
        Assert.Contains("成功 1", File.ReadAllText(Path.Combine(batchDirectory, "Export.log")));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(["Source", "Destination", "Status", "Message"],
            report.RootElement[0].EnumerateObject().Select(p => p.Name));
        Assert.Single(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(result.ReportPath)!, "results.jsonl")));
    }

    /// <summary>缺失或空输出不发布正式文件；临时文件保留供检查。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task EmptyOrMissingOutputFailsWithoutPublishing(string? content)
    {
        using var fixture = new Fixture();
        fixture.Add("source.dwg");
        fixture.Host.ExportContent = content;
        var result = await fixture.Run();
        Assert.Equal("Failed", result.Items[0].Status);
        Assert.False(File.Exists(result.Items[0].Destination));
        Assert.Equal(content != null, File.Exists(fixture.Host.LastTemporary));
        Assert.Single(fixture.Host.Closed);
    }

    /// <summary>导出期间有人创建正式目标时，保留既有目标和本次临时输出。</summary>
    [Fact]
    public async Task PublicationRaceNeverOverwritesExistingOutput()
    {
        using var fixture = new Fixture();
        fixture.Add("source.dwg");
        fixture.Host.AfterExport = () =>
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Host.LastTemporary)!, "source.dwg"), "keep");
        var result = await fixture.Run();
        Assert.Equal("Failed", result.Items[0].Status);
        Assert.Equal("keep", File.ReadAllText(result.Items[0].Destination));
        Assert.Equal("AC1027converted", File.ReadAllText(fixture.Host.LastTemporary));
    }

    /// <summary>已知导出失败关闭安全图纸并继续；状态不明或未知命令立即停止。</summary>
    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    public async Task CommandFailureRespectsStateAndStopFlags(bool known, bool stop, int expectedItems)
    {
        using var fixture = new Fixture();
        string first = fixture.Add("one.dwg");
        fixture.Add("two.dwg");
        fixture.Host.ExportFailure = new CadOperationException("导出失败", known, stop);
        var result = await fixture.Run();
        Assert.Equal(expectedItems, result.Items.Count);
        Assert.All(result.Items, x => Assert.Equal("Failed", x.Status));
        Assert.Equal(known && !stop, fixture.Host.Closed.Contains(first));
        Assert.Equal(expectedItems == 1, result.StopReason != null);
    }

    /// <summary>导出返回后取消不发布输出、不关闭图纸且不恢复活动图。</summary>
    [Fact]
    public async Task CancellationAfterCommandPreservesDrawingAndTemporaryFile()
    {
        using var fixture = new Fixture();
        fixture.Add("one.dwg");
        fixture.Add("two.dwg");
        using var cancellation = new CancellationTokenSource();
        fixture.Host.AfterExport = cancellation.Cancel;
        var result = await fixture.Run(token: cancellation.Token);
        Assert.True(result.Canceled);
        Assert.Single(result.Items);
        Assert.Equal("Canceled", result.Items[0].Status);
        Assert.Empty(fixture.Host.Closed);
        Assert.True(File.Exists(fixture.Host.LastTemporary));
        Assert.False(File.Exists(result.Items[0].Destination));
        Assert.NotSame(fixture.Host.OriginalDrawing, fixture.Host.Active);
    }

    /// <summary>正式文件已发布后才取消时，保留成功结果并停止后续关闭和导出。</summary>
    [Fact]
    public async Task CancellationAfterPublicationKeepsSuccessfulResult()
    {
        using var fixture = new Fixture();
        fixture.Add("one.dwg");
        fixture.Add("two.dwg");
        using var cancellation = new CancellationTokenSource();
        fixture.Host.AfterStateRead = count => { if (count == 2) cancellation.Cancel(); };
        var result = await fixture.Run(token: cancellation.Token);
        Assert.True(result.Canceled);
        Assert.Single(result.Items);
        Assert.Equal("Exported", result.Items[0].Status);
        Assert.True(File.Exists(result.Items[0].Destination));
        Assert.Empty(fixture.Host.Closed);
    }

    /// <summary>原生命令取消通过状态标记停止整个批次。</summary>
    [Fact]
    public async Task CadCancellationStopsRemainingDrawings()
    {
        using var fixture = new Fixture();
        fixture.Add("one.dwg");
        fixture.Add("two.dwg");
        fixture.Host.ExportFailure = new CadOperationException("用户取消", true, true, true);
        var result = await fixture.Run();
        Assert.True(result.Canceled);
        Assert.Single(result.Items);
        Assert.Empty(fixture.Host.Closed);
    }

    /// <summary>项目在命令期间改变时，禁止发布或继续关闭图纸。</summary>
    [Fact]
    public async Task ProjectChangePreservesUnpublishedOutputAndStops()
    {
        using var fixture = new Fixture();
        fixture.Add("one.dwg");
        fixture.Add("two.dwg");
        fixture.Host.AfterExport = () => fixture.Host.ProjectChanged = true;
        var result = await fixture.Run();
        Assert.Single(result.Items);
        Assert.Equal("Failed", result.Items[0].Status);
        Assert.Empty(fixture.Host.Closed);
        Assert.False(File.Exists(result.Items[0].Destination));
        Assert.True(File.Exists(fixture.Host.LastTemporary));
    }

    /// <summary>打开状态不明与关闭失败都立即停止，不误关闭其他文档。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenOrCloseFailureStopsBatch(bool duringOpen)
    {
        using var fixture = new Fixture();
        fixture.Add("one.dwg");
        fixture.Add("two.dwg");
        if (duringOpen) fixture.Host.OpenFailure = new IOException("打开失败");
        else fixture.Host.CloseFailure = new IOException("关闭失败");
        var result = await fixture.Run();
        Assert.Single(result.Items);
        Assert.NotNull(result.StopReason);
        Assert.Empty(fixture.Host.Closed);
    }

    /// <summary>导出后新增修改不得被丢弃，累计保留三张工具打开图纸时停止。</summary>
    [Fact]
    public async Task ThreeRetainedModifiedDrawingsStopTheBatch()
    {
        using var fixture = new Fixture();
        for (int i = 0; i < 4; i++) fixture.Add(i + ".dwg");
        fixture.Host.ModifyAfterExport = true;
        var result = await fixture.Run();
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, x => Assert.Equal("Exported", x.Status));
        Assert.Empty(fixture.Host.Closed);
        Assert.Contains("三张", result.StopReason);
    }

    /// <summary>缺失图纸按单图失败记录，其余图纸照常导出。</summary>
    [Fact]
    public async Task MissingSourceDoesNotOpenCadAndContinues()
    {
        using var fixture = new Fixture();
        string missing = fixture.Add("missing.dwg");
        File.Delete(missing);
        fixture.Add("valid.dwg");
        var result = await fixture.Run();
        Assert.Equal(["Failed", "Exported"], result.Items.Select(x => x.Status));
        Assert.Single(fixture.Host.Opened);
    }

    /// <summary>创建本测试独享的临时项目，结束时清理，不接触实际用户目录。</summary>
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Pn3DKit.Tests", Guid.NewGuid().ToString("N"));
        internal string Project => Path.Combine(Root, "project");
        internal List<string> Sources { get; } = [];
        internal FakeHost Host { get; } = new();
        internal Fixture() => Directory.CreateDirectory(Project);
        internal string Add(string relative, bool modified = false, bool readOnly = false, bool alreadyOpen = false)
        {
            string source = Path.GetFullPath(Path.Combine(Project, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "original");
            Sources.Add(source);
            var drawing = new FakeDrawing(source) { Modified = modified, ReadOnly = readOnly };
            Host.Drawings[source] = drawing;
            if (alreadyOpen) Host.Existing.Add(source);
            return source;
        }
        internal BatchOptions Options(bool save = true) => new(ProjectPartKind.PnId, save, "2013",
            ExportPolicy.SuggestDirectory(Root, new DateTime(2026, 10, 24, 15, 30, 45)));
        internal Task<BatchResult> Run(bool save = true, CancellationToken token = default) =>
            new BatchExporter().RunAsync(new(Project, Sources.ToArray(), null), Options(save), Host, _ => { }, token);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    /// <summary>保存离线文档状态，模拟宿主对文档的所有权。</summary>
    private sealed class FakeDrawing(string path) : ICadDrawing
    {
        internal string Path { get; } = path;
        internal bool Modified { get; set; }
        internal bool ReadOnly { get; set; }
    }

    /// <summary>模拟同步 CAD 操作完成后的任务结果，不使用真实 CAD 或后台线程。</summary>
    private sealed class FakeHost : ICadHost
    {
        internal Dictionary<string, FakeDrawing> Drawings { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Opened { get; } = [];
        internal List<string> Closed { get; } = [];
        public ICadDrawing? OriginalDrawing { get; } = new FakeDrawing("original");
        internal ICadDrawing? Active { get; private set; }
        internal int Saves { get; private set; }
        internal int Exports { get; private set; }
        internal bool SaveClearsDirty { get; set; } = true;
        internal bool ModifyAfterExport { get; set; }
        internal bool ProjectChanged { get; set; }
        internal string? ExportContent { get; set; } = "AC1027converted";
        internal string LastTemporary { get; private set; } = "";
        internal Exception? SaveFailure { get; set; }
        internal Exception? ExportFailure { get; set; }
        internal Exception? OpenFailure { get; set; }
        internal Exception? CloseFailure { get; set; }
        internal Action? AfterExport { get; set; }
        internal Action<int>? AfterStateRead { get; set; }
        private int _stateReads;
        public void EnsureProject()
        {
            if (ProjectChanged) throw new CadOperationException("项目已改变");
        }
        public ICadDrawing? FindOpen(string source) => Existing.Contains(source) ? Drawings[source] : null;
        public Task<ICadDrawing> OpenAsync(string source)
        {
            if (OpenFailure != null) throw OpenFailure;
            Opened.Add(source);
            return Task.FromResult<ICadDrawing>(Drawings[source]);
        }
        public Task ActivateAsync(ICadDrawing drawing) { Active = drawing; return Task.CompletedTask; }
        public Task<DrawingState> ReadStateAsync(ICadDrawing drawing)
        {
            var fake = (FakeDrawing)drawing;
            AfterStateRead?.Invoke(++_stateReads);
            return Task.FromResult(new DrawingState(fake.Modified, fake.ReadOnly));
        }
        public Task SaveAsync(ICadDrawing drawing, string marker)
        {
            Saves++;
            if (SaveFailure != null) throw SaveFailure;
            if (SaveClearsDirty) ((FakeDrawing)drawing).Modified = false;
            return Task.CompletedTask;
        }
        public Task ExportAsync(ICadDrawing drawing, string temporary, string version, string marker)
        {
            Exports++;
            LastTemporary = temporary;
            if (ExportContent != null) File.WriteAllText(temporary, ExportContent);
            if (ExportFailure != null) throw ExportFailure;
            if (ModifyAfterExport) ((FakeDrawing)drawing).Modified = true;
            AfterExport?.Invoke();
            return Task.CompletedTask;
        }
        public Task CloseAsync(ICadDrawing drawing)
        {
            if (CloseFailure != null) throw CloseFailure;
            Closed.Add(((FakeDrawing)drawing).Path);
            return Task.CompletedTask;
        }
    }
}
