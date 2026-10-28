using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>项目级提交、共享数据行、逐图生命周期与日志的离线回归，不加载 Autodesk。</summary>
public sealed class ProjectTagTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.ProjectTests", Guid.NewGuid().ToString("N"));
    /// <summary>测试文件限定在本用例目录。</summary>
    public ProjectTagTests() => Directory.CreateDirectory(_root);
    /// <summary>释放临时测试目录。</summary>
    public void Dispose() => Directory.Delete(_root, true);
    private string Dwg(string name) { string path = Path.Combine(_root, name + ".dwg"); File.WriteAllText(path, "fake"); return path; }
    private static TagRebuildPlan Plan(int id, params string[] drawings) => new(
        new("PnId:" + id, new(new("PnId", "Instrument", id.ToString("X"), "#(TargetObject.Number)", "PT-" + id)
        { RowId = id.ToString(), Drawings = drawings }, "PT-" + (id + 100), TagCheckStatus.Mismatch, [])),
        TagRebuildState.Ready, "");
    private sealed class Journal : ITagRebuildJournal
    {
        internal bool FailBeforeWrite;
        public void BeforeWrite(TagRebuildCandidate item)
        {
            if (FailBeforeWrite) throw new IOException("写前记录持久化失败");
            Message(TagWriteRecovery.Format(item));
        }
        internal List<TagRebuildRecord> Records = [];
        internal List<string> Messages = [];
        internal bool Fail;
        public void Append(TagRebuildRecord record) { if (Fail) throw new IOException("disk full"); Records.Add(record); }
        public void Message(string value) { if (Fail) throw new IOException("disk full"); Messages.Add(value); }
    }
    private sealed class Data : IProjectTagDataHost
    {
        internal bool Pending, Fail, Wrong;
        internal int Writes, Checks;
        internal Action? OnEnsure;
        internal string? Changed;
        internal IReadOnlyList<string> Owners = [];
        public void EnsureContext() { Checks++; OnEnsure?.Invoke(); }
        public bool HasPendingChanges() => Pending;
        public string? Revalidate(TagRebuildCandidate item) => Changed;
        public IReadOnlyList<string> FindOwners(TagRebuildCandidate item) => Owners;
        public string WriteAndCommit(TagRebuildCandidate item)
        { Writes++; if (Fail) throw new IOException("commit failed"); return Wrong ? "unexpected" : item.Check.ExpectedTag; }
    }
    private sealed class Drawing(string path) : ICadDrawing
    { internal string Path = path; internal readonly Guid Identity = Guid.NewGuid(); internal bool Modified, ReadOnly, Closed; }
    private sealed class Host : IProjectTagDrawingHost
    {
        internal Dictionary<string, Drawing> Open = new(StringComparer.OrdinalIgnoreCase);
        internal Drawing Original = new("original");
        internal int Writes, Saves, Closes, Syncs, Restores;
        internal bool Pending, Throw, WrongSave, FailClose;
        internal readonly HashSet<string> ReadOnlyPaths = new(StringComparer.OrdinalIgnoreCase);
        internal readonly ScopedContextGuard Guard;
        private Drawing _active;
        internal Host() { _active = Original; Guard = new(() => EnsureDrawing(Original)); }
        private void EnsureDrawing(Drawing drawing)
        { if (drawing.Closed || _active.Identity != drawing.Identity) throw new InvalidOperationException("图纸身份不符或已关闭"); }
        private IDisposable UseDrawing(Drawing drawing) => Guard.Use(() => EnsureDrawing(drawing));
        internal Action? OnContext;
        public ICadDrawing? OriginalDrawing => Original;
        public void CheckContext() => OnContext?.Invoke();
        public void EnsureProject() => CheckContext();
        public ICadDrawing? FindOpen(string source) => Open.GetValueOrDefault(source);
        public Task<ICadDrawing> OpenAsync(string source)
        { var d = new Drawing(source) { ReadOnly = ReadOnlyPaths.Contains(source) }; Open[source] = d; _active = d; return Task.FromResult<ICadDrawing>(d); }
        public Task ActivateAsync(ICadDrawing drawing) { _active = (Drawing)drawing; EnsureDrawing(_active); if (drawing == Original) Restores++; return Task.CompletedTask; }
        public Task<DrawingState> ReadStateAsync(ICadDrawing drawing)
        { var d = (Drawing)drawing; return Task.FromResult(new DrawingState(d.Modified, d.ReadOnly)); }
        public Task SaveAsync(ICadDrawing drawing, string marker)
        { Saves++; ((Drawing)drawing).Modified = WrongSave; return Task.CompletedTask; }
        public Task ExportAsync(ICadDrawing drawing, string temporary, string version, string marker) => throw new NotSupportedException();
        public Task CloseAsync(ICadDrawing drawing)
        { EnsureDrawing((Drawing)drawing); if (FailClose) throw new IOException("真实关闭失败"); Closes++; ((Drawing)drawing).Closed = true; Open.Remove(((Drawing)drawing).Path); return Task.CompletedTask; }
        public Task<bool> HasPendingDataAsync(ICadDrawing drawing)
        { using var scope = UseDrawing((Drawing)drawing); Guard.Ensure(); return Task.FromResult(Pending); }
        public Task<TagRebuildBatchResult> RebuildAsync(ICadDrawing drawing, IReadOnlyList<TagRebuildPlan> work,
            IReadOnlyList<TagRebuildRecord> synchronize, ITagRebuildJournal journal)
        {
            using var scope = UseDrawing((Drawing)drawing); Guard.Ensure();
            if (Throw) throw new InvalidOperationException("unknown callback");
            var result = work.Select(p => new TagRebuildRecord(p.Item, TagRebuildAction.Updated, "read back")).ToArray();
            foreach (var r in result) { Writes++; journal.Append(r); }
            Syncs += synchronize.Count;
            ((Drawing)drawing).Modified = work.Count != 0 || synchronize.Count != 0;
            return Task.FromResult(new TagRebuildBatchResult(result, work.Count != 0, true, null));
        }
    }

    /// <summary>回车明确选择数据模式，不隐式取消。</summary>
    [Theory]
    [InlineData(null, ProjectTagMode.Data)]
    [InlineData("", ProjectTagMode.Data)]
    [InlineData("n", ProjectTagMode.Data)]
    [InlineData("Y", ProjectTagMode.Drawings)]
    [InlineData("c", ProjectTagMode.Cancel)]
    public void ModeSelection(string? value, ProjectTagMode mode) => Assert.Equal(mode, ProjectTagPolicy.ParseMode(value));
    /// <summary>未知模式不产生写入选择。</summary>
    [Fact] public void InvalidMode() => Assert.Throws<ArgumentException>(() => ProjectTagPolicy.ParseMode("*"));
    /// <summary>已有项目修改阻止提交，全部目标保留未执行。</summary>
    [Fact] public void PendingBaselineStops()
    { var h = new Data { Pending = true }; var r = ProjectTagDataBatch.Run([Plan(1), Plan(2)], h, new Journal(), (_, _) => { });
      Assert.Equal(0, h.Writes); Assert.All(r.Records, x => Assert.Equal(TagRebuildAction.Unexecuted, x.Action)); }
    /// <summary>专用写前记录持久化失败时，项目提交接口不得执行。</summary>
    [Fact]
    public void DurableBeforeWriteFailureStopsData()
    {
        var host = new Data();
        var result = ProjectTagDataBatch.Run([Plan(1), Plan(2)], host,
            new Journal { FailBeforeWrite = true }, (_, _) => {});
        Assert.Equal(0, host.Writes); Assert.NotNull(result.StopReason);
        Assert.All(result.Records, x => Assert.Equal(TagRebuildAction.Unexecuted, x.Action));
    }

    /// <summary>数据模式每行提交回读，接口不提供 DWG 操作。</summary>
    [Fact] public void DataWritesAll()
    { var h = new Data(); var r = ProjectTagDataBatch.Run([Plan(1), Plan(2)], h, new Journal(), (_, _) => { });
      Assert.Null(r.StopReason); Assert.Equal(2, h.Writes); Assert.All(r.Records, x => Assert.Equal(TagRebuildAction.Updated, x.Action)); }
    /// <summary>提交失败或回读不符均停止后续行。</summary>
    [Theory] [InlineData(true, false)] [InlineData(false, true)]
    public void CommitOrReadFailureStops(bool fail, bool wrong)
    { var h = new Data { Fail = fail, Wrong = wrong }; var r = ProjectTagDataBatch.Run([Plan(1), Plan(2)], h, new Journal(), (_, _) => { });
      Assert.Equal(1, h.Writes); Assert.Equal(TagRebuildAction.Failed, r.Records[0].Action);
      Assert.Equal(TagRebuildAction.Unexecuted, r.Records[1].Action); Assert.NotNull(r.StopReason); }
    /// <summary>预检后属性变化或项目新占用禁止该行写入。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void RevalidationAndConflict(bool changed)
    { var h = new Data { Changed = changed ? "attribute changed" : null, Owners = changed ? [] : ["PnId:99"] };
      var r = ProjectTagDataBatch.Run([Plan(1)], h, new Journal(), (_, _) => { });
      Assert.Equal(0, h.Writes); Assert.Equal(changed ? TagRebuildAction.Skipped : TagRebuildAction.Conflict, r.Records[0].Action); }
    /// <summary>取消与日志故障在任何写入前中止。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void CancelOrLogStopsData(bool cancel)
    { var h = new Data { OnEnsure = cancel ? () => throw new OperationCanceledException("Esc") : null };
      var r = ProjectTagDataBatch.Run([Plan(1)], h, new Journal { Fail = !cancel }, (_, _) => { });
      Assert.Equal(0, h.Writes); Assert.NotNull(r.StopReason); Assert.Single(r.Records); }
    /// <summary>部分完成后取消，保留提交记录及未执行记录。</summary>
    [Fact] public void CancelAfterFirstCommit()
    { var h = new Data(); h.OnEnsure = () => { if (h.Writes == 1) throw new OperationCanceledException(); };
      var r = ProjectTagDataBatch.Run([Plan(1), Plan(2)], h, new Journal(), (_, _) => { });
      Assert.Equal(TagRebuildAction.Updated, r.Records[0].Action); Assert.Equal(TagRebuildAction.Unexecuted, r.Records[1].Action); }
    /// <summary>跨图纸共享行去重写入，每张关联图都刷新，不保存时全部保留。</summary>
    [Fact] public async Task SharedRowWrittenOnce()
    { var a = Dwg("a"); var b = Dwg("b"); var h = new Host();
      var r = await ProjectTagDrawingBatch.RunAsync([Plan(1, a, b)], false, _root, h, new Journal(), (_, _) => { });
      Assert.Null(r.StopReason); Assert.Equal(1, h.Writes); Assert.Equal(1, h.Syncs);
      Assert.Equal(2, h.Open.Count); Assert.Equal(0, h.Closes); Assert.Equal(1, h.Restores); Assert.Single(r.Records); }
    /// <summary>不保存时不采用导出工具的三张保留上限。</summary>
    [Fact] public async Task AllModifiedDrawingsRemainOpen()
    { var h = new Host(); var plans = Enumerable.Range(1, 5).Select(i => Plan(i, Dwg(i.ToString()))).ToArray();
      var r = await ProjectTagDrawingBatch.RunAsync(plans, false, _root, h, new Journal(), (_, _) => { });
      Assert.Null(r.StopReason); Assert.Equal(5, h.Open.Count); Assert.Equal(5, h.Writes); Assert.Equal(0, h.Saves); }
    /// <summary>统一保存后只关闭工具打开且干净的文档，预先打开的文档保持打开。</summary>
    [Fact] public async Task SaveOnlyClosesOwnedCleanDrawings()
    { var a = Dwg("a"); var b = Dwg("b"); var h = new Host(); h.Open[a] = new Drawing(a) { Modified = true };
      var r = await ProjectTagDrawingBatch.RunAsync([Plan(1, a), Plan(2, b)], true, _root, h, new Journal(), (_, _) => { });
      Assert.Null(r.StopReason); Assert.Equal(2, h.Saves); Assert.Equal(1, h.Closes); Assert.True(h.Open.ContainsKey(a)); }
    /// <summary>已打开的只读图纸不写入，但同一共享行可在可写图纸处理。</summary>
    [Fact] public async Task ReadOnlyAliasIsSkipped()
    { var a = Dwg("a"); var b = Dwg("b"); var h = new Host(); h.Open[a] = new Drawing(a) { ReadOnly = true };
      var r = await ProjectTagDrawingBatch.RunAsync([Plan(1, a, b)], false, _root, h, new Journal(), (_, _) => { });
      Assert.Null(r.StopReason); Assert.Equal(1, h.Writes); Assert.Equal("跳过", r.Drawings[0].Status); }
    /// <summary>首图只读或上一图已关闭时，关闭前独立绑定当前图 Guard，跳过后继续处理。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ToolOpenedReadOnlyDoesNotAbortAfterAnotherDrawing(bool first)
    {
        var a = Dwg("a"); var b = Dwg("b"); var c = Dwg("c"); var h = new Host();
        h.ReadOnlyPaths.Add(first ? a : b);
        var result = await ProjectTagDrawingBatch.RunAsync([Plan(1, a), Plan(2, b), Plan(3, c)],
            true, _root, h, new Journal(), (_, _) => { });
        Assert.Null(result.StopReason); Assert.Equal(2, h.Writes); Assert.Equal(2, h.Saves);
        Assert.Equal(3, h.Closes); Assert.Empty(h.Open); Assert.Equal(1, h.Restores);
        var skipped = Assert.Single(result.Drawings, x => x.Status == "跳过");
        Assert.False(skipped.Retained); Assert.Contains("只读", skipped.Reason);
        h.Guard.Ensure();
    }

    /// <summary>实际缓存待提交时保留工具打开的只读图纸，仍继续后续可写图纸。</summary>
    [Fact] public async Task ReadOnlyWithPendingCacheRetainsAndContinues()
    {
        var a = Dwg("a"); var b = Dwg("b"); var h = new Host { Pending = true };
        h.ReadOnlyPaths.Add(a);
        var result = await ProjectTagDrawingBatch.RunAsync([Plan(1, a), Plan(2, b)],
            true, _root, h, new Journal(), (_, _) => { });
        Assert.Null(result.StopReason); Assert.Equal(1, h.Writes); Assert.Equal(1, h.Saves);
        Assert.True(result.Drawings[0].Retained); Assert.Equal(0, h.Closes); Assert.Equal(2, h.Open.Count);
    }

    /// <summary>真正关闭失败仍停止，并保留下一张图纸未执行记录。</summary>
    [Fact] public async Task RealCloseFailureStillStops()
    {
        var h = new Host { FailClose = true };
        var result = await ProjectTagDrawingBatch.RunAsync([Plan(1, Dwg("a")), Plan(2, Dwg("b"))],
            true, _root, h, new Journal(), (_, _) => { });
        Assert.Contains("关闭", result.StopReason); Assert.Equal(1, h.Writes);
        Assert.Single(h.Open); Assert.Contains(result.Records, x => x.Action == TagRebuildAction.Unexecuted);
    }

    /// <summary>DBMOD 清零但项目缓存未提交时仍不关闭。</summary>
    [Fact] public async Task PendingCacheRetains()
    { var h = new Host { Pending = true };
      await ProjectTagDrawingBatch.RunAsync([Plan(1, Dwg("a"))], true, _root, h, new Journal(), (_, _) => { });
      Assert.Equal(0, h.Closes); Assert.Single(h.Open); }
    /// <summary>未知回调故障保留现场，不把可能已写的行记作未执行。</summary>
    [Fact] public async Task UnknownCallbackStops()
    { var h = new Host { Throw = true };
      var r = await ProjectTagDrawingBatch.RunAsync([Plan(1, Dwg("a")), Plan(2, Dwg("b"))], false, _root, h, new Journal(), (_, _) => { });
      Assert.NotNull(r.StopReason); Assert.Equal(0, h.Restores); Assert.Equal(TagRebuildAction.Failed, r.Records[0].Action);
      Assert.Equal(TagRebuildAction.Unexecuted, r.Records[1].Action); }
    /// <summary>目录日志故障立刻停止，不继续打开其他图纸。</summary>
    [Fact] public async Task LogFailureStopsBeforeOpen()
    { var h = new Host();
      var r = await ProjectTagDrawingBatch.RunAsync([Plan(1, Dwg("a")), Plan(2, Dwg("b"))], false, _root, h, new Journal { Fail = true }, (_, _) => { });
      Assert.Equal(0, h.Writes); Assert.Empty(h.Open); Assert.NotNull(r.StopReason); Assert.Equal(2, r.Records.Count); }
    /// <summary>原关联图纸缺失时共享行可以在另一有效关联图纸处理。</summary>
    [Fact] public async Task MissingAliasDoesNotLoseRow()
    { var h = new Host();
      var r = await ProjectTagDrawingBatch.RunAsync([Plan(1, Path.Combine(_root, "a-missing.dwg"), Dwg("b"))], false, _root, h, new Journal(), (_, _) => { });
      Assert.Null(r.StopReason); Assert.Equal(1, h.Writes); Assert.Equal(TagRebuildAction.Updated, Assert.Single(r.Records).Action); }
    /// <summary>保存后仍脏时不关闭，已更新数据行结果仍然保留。</summary>
    [Fact] public async Task SaveRemainsDirty()
    { var h = new Host { WrongSave = true };
      var r = await ProjectTagDrawingBatch.RunAsync([Plan(1, Dwg("a"))], true, _root, h, new Journal(), (_, _) => { });
      Assert.Single(h.Open); Assert.Equal(0, h.Closes); Assert.Equal(TagRebuildAction.Updated, Assert.Single(r.Records).Action); }
    /// <summary>项目／Esc 停止不自动恢复或丢弃现场。</summary>
    [Fact] public async Task ContextChangeStops()
    { var h = new Host { OnContext = () => throw new OperationCanceledException("Esc") };
      var r = await ProjectTagDrawingBatch.RunAsync([Plan(1, Dwg("a"))], false, _root, h, new Journal(), (_, _) => { });
      Assert.Equal(0, h.Writes); Assert.Equal(0, h.Restores); Assert.NotNull(r.StopReason); }
    /// <summary>日志建议目录惰性创建，冲突追加序号，选中目录冲突不覆盖。</summary>
    [Fact] public void PathsDoNotCreateOrOverwrite()
    { var stamp = new DateTime(2026, 10, 25, 1, 2, 3);
      string a = ProjectTagPolicy.Suggest(_root, stamp, false); Assert.False(Directory.Exists(a));
      Directory.CreateDirectory(a); string b = ProjectTagPolicy.Suggest(_root, stamp, false); Assert.EndsWith("_01", b);
      File.WriteAllText(Path.Combine(a, "ProjectTagCheck.log"), "original");
      Assert.Throws<IOException>(() => ProjectTagPolicy.Validate(a, false)); }
    /// <summary>日志追加保留预检和结果，带 BOM 与图纸／行身份，文件可在结束前读取。</summary>
    [Fact] public void JournalFlushesHistoryAndEscapes()
    { var p = Plan(1, "中文,\"test\"\n.dwg");
      using (var log = new ProjectTagJournal(_root, true, "project", ["Instrument"]))
      {
          log.Append(TagRebuildPolicy.Preview(p)); log.Append(new(p.Item, TagRebuildAction.Updated, "原因,\n\"内容\""));
          string csv;
          using (var input = new StreamReader(new FileStream(log.CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
              csv = input.ReadToEnd();
          Assert.Contains("待重建", csv); Assert.Contains("已更新", csv); Assert.Contains("\"\"test\"\"", csv);
          using (var bytes = new FileStream(log.CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
          { Assert.Equal(0xEF, bytes.ReadByte()); Assert.Equal(0xBB, bytes.ReadByte()); Assert.Equal(0xBF, bytes.ReadByte()); }
          Assert.Throws<IOException>(() => new ProjectTagJournal(_root, true, "p", []));
          log.Complete("已中断");
          using var text = new StreamReader(new FileStream(log.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
          Assert.Contains("已中断", text.ReadToEnd());
      }
    }
}
