using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>管理名称统计、来源索引及流式清单；主线程写报告，定时器只刷新名称文件。</summary>
public sealed class XrefBlockJournal : IDisposable
{
    private readonly Stream _logStream, _sourceStream, _anonymousStream, _normalStream, _comparisonStream;
    private readonly StreamWriter _log, _sources, _anonymous, _normal, _comparison;
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly Func<string, Stream> _createNameFile;
    private readonly Func<Guid> _newSourceId;
    private readonly Dictionary<string, NameScan> _scans = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _sourceIds = [];
    private NameScan? _activeScan;
    private bool _disposed;
    private int _nextDwgNo;
    private bool _statisticsWritten;
    /// <summary>任何输出故障后禁止生成后续统计，保留已经写出的内容。</summary>
    public bool HasWriteFailure { get; private set; }

    /// <summary>一次来源扫描的日志身份；引用链可以在名称清单关闭后继续补入主 LOG。</summary>
    internal sealed class NameScan(Guid id, XrefBlockSource source, string path, long started)
    {
        internal Guid Id { get; } = id;
        internal int DwgNo => Source.DwgNo;
        internal string State { get; set; } = "扫描中";
        internal XrefBlockSource Source { get; } = source;
        internal string Path { get; } = path;
        internal long Started { get; } = started;
        internal XrefBlockNameCsv? Csv { get; set; }
        internal bool Finished { get; set; }
        /// <summary>名称确认循环中逐条追加，不读取实体或回读 CSV。</summary>
        internal void Append(string name, bool anonymous) =>
            (Csv ?? throw new XrefBlockLogException("来源清单尚未创建：" + Path)).Append(name, anonymous);
    }
    /// <summary>文本报告路径。</summary>
    public string LogPath { get; }
    /// <summary>匿名块名称统计路径。</summary>
    public string AnonymousPath { get; }
    /// <summary>非匿名块名称统计路径。</summary>
    public string NormalPath { get; }
    /// <summary>跨来源同名普通块的三列汇总报告，每个候选名称一行。</summary>
    public string ComparisonPath { get; }
    /// <summary>来源 GUID、原始路径和名称清单之间的可追溯索引。</summary>
    public string SourcesPath { get; }

    /// <summary>五件固定报告通过 CreateNew 创建；来源清单在各来源首次进入扫描时再创建。</summary>
    public XrefBlockJournal(string directory, string drawing, RunLogFiles files)
        : this(directory, drawing, files, TimeProvider.System) { }

    /// <summary>离线测试可注入时钟和文件故障；生产仍由归属清单独占创建真实文件。</summary>
    internal XrefBlockJournal(string directory, string drawing, RunLogFiles files, TimeProvider clock,
        Func<string, Stream>? createNameFile = null, Func<Guid>? newSourceId = null,
        Func<string, Stream>? createReportFile = null)
    {
        directory = Validate(directory);
        _directory = directory; _clock = clock;
        _createNameFile = createNameFile ?? files.CreateNew;
        _newSourceId = newSourceId ?? Guid.NewGuid;
        files.EnsureDirectory(directory);
        LogPath = Path.Combine(directory, "XrefBlockCheck.log");
        AnonymousPath = Path.Combine(directory, "AnonymousBlockStatus.csv");
        NormalPath = Path.Combine(directory, "NormalBlockStatus.csv");
        SourcesPath = Path.Combine(directory, "XrefBlockSources.csv");
        ComparisonPath = Path.Combine(directory, "Result.csv");
        var opened = new List<IDisposable>();
        Func<string, Stream> createReport = createReportFile ?? files.CreateNew;
        try
        {
            _logStream = createReport(LogPath); opened.Add(_logStream);
            _sourceStream = createReport(SourcesPath); opened.Add(_sourceStream);
            _anonymousStream = createReport(AnonymousPath); opened.Add(_anonymousStream);
            _normalStream = createReport(NormalPath); opened.Add(_normalStream);
            _comparisonStream = createReport(ComparisonPath); opened.Add(_comparisonStream);
            _log = new(_logStream, new UTF8Encoding(false), leaveOpen: true); opened.Add(_log);
            _sources = CsvWriter(_sourceStream); opened.Add(_sources);
            _anonymous = CsvWriter(_anonymousStream); opened.Add(_anonymous);
            _normal = CsvWriter(_normalStream); opened.Add(_normal);
            _comparison = CsvWriter(_comparisonStream); opened.Add(_comparison);
            _log.WriteLine("Pn3DKit | XREFBLOCKCHECK | 块名称统计");
            _log.WriteLine("主图：" + drawing);
            _log.WriteLine("开始时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            _log.WriteLine("先输出名称统计，再比较 dwg计数大于1的普通块二维内容；不绑定、重载或保存。");
            _log.WriteLine("来源索引：" + SourcesPath);
            _log.WriteLine("匿名统计：" + AnonymousPath + "\n普通块统计：" + NormalPath);
            _log.WriteLine("比较结果：" + ComparisonPath);
            _sources.WriteLine("dwgNo,来源ID,来源编号,来源图纸,数据来源,名称清单文件,扫描状态,已输出数量,已刷新数量,耗时毫秒,详情");
            _anonymous.WriteLine("块名称,dwgNo,dwg计数");
            _normal.WriteLine("块名称,dwgNo,dwg计数");
            _comparison.WriteLine("块名称,dwgNo,结果");
            _comparison.Flush(); _comparisonStream.Flush();
            _anonymous.Flush(); _anonymousStream.Flush();
            _normal.Flush(); _normalStream.Flush();
            Flush();
        }
        catch (Exception ex)
        {
            var errors = new List<Exception> { ex };
            foreach (var item in opened.AsEnumerable().Reverse())
                try { item.Dispose(); } catch (Exception close) { errors.Add(close); }
            throw new XrefBlockLogException("创建外参检查报告失败：" + directory + "；" + ex.Message,
                errors.Count == 1 ? ex : new AggregateException(errors));
        }
    }

    /// <summary>只验证目录及本命令的五个固定日志名，不创建文件。</summary>
    public static string Validate(string path)
    {
        string directory = ExportPolicy.Normalize(path);
        ExportPolicy.RejectLinks(directory);
        if (File.Exists(directory)) throw new IOException("所选路径是文件，请选择文件夹。");
        foreach (string name in new[] { "XrefBlockCheck.log", "XrefBlockSources.csv", "AnonymousBlockStatus.csv", "NormalBlockStatus.csv", "Result.csv" })
            if (File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name)))
                throw new IOException("所选目录已有同名日志，请选择其他文件夹：" + Path.Combine(directory, name));
        return directory;
    }

    /// <summary>先创建并刷新来源表头，再允许目录代码读取块表；同名文件从不覆盖。</summary>
    internal NameScan BeginNames(XrefBlockSource source)
    {
        if (_activeScan != null || _scans.ContainsKey(source.Key))
            throw new XrefBlockLogException("来源名称扫描重复或尚有未关闭的来源：" + source.Key);
        source = source with { DwgNo = checked(++_nextDwgNo) };
        NameScan? scan = null;
        Stream? stream = null;
        try
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                Guid id = _newSourceId();
                if (!_sourceIds.Add(id)) continue;
                string path = Path.Combine(_directory, NameFile(source.Drawing, id));
                scan = new(id, source, path, _clock.GetTimestamp());
                if (File.Exists(path) || Directory.Exists(path)) continue;
                try { stream = _createNameFile(path); break; }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) is 80 or 183
                    && (File.Exists(path) || Directory.Exists(path))) { }
            }
            if (scan == null || stream == null) throw new IOException("来源清单 GUID 多次重名，未覆盖已有文件。");
            _scans.Add(source.Key, scan);
            _activeScan = scan;
            scan.Csv = new(scan.Path, stream, _clock);
            RecordNames(scan, "扫描中", "");
            foreach (string chain in source.Chains) SourceChain(source.Key, chain);
            return scan;
        }
        catch (Exception ex)
        {
            HasWriteFailure = true;
            Exception failure = ex;
            if (scan != null)
            {
                _scans.TryAdd(source.Key, scan);
                try { EndNames(scan, "写入失败", ex.Message); }
                catch (Exception close) { failure = new AggregateException(ex, close); }
            }
            throw new XrefBlockLogException("开始来源名称清单失败：" + source.Drawing + "；" + failure.Message, failure);
        }
    }

    /// <summary>源文件名只用作便于辨认的前缀；完整路径和来源身份保存在索引中。</summary>
    private static string NameFile(string drawing, Guid id)
    {
        string name;
        try { name = Path.GetFileNameWithoutExtension(drawing); }
        catch (ArgumentException) { name = "来源"; }
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length > 64) name = name[..64];
        if (name.Length != 0 && char.IsHighSurrogate(name[^1])) name = name[..^1];
        if (string.IsNullOrWhiteSpace(name)) name = "来源";
        return name + "__" + id.ToString("N") + ".csv";
    }

    /// <summary>新发现的引用链按来源 GUID 补记；不重新打开已经关闭的名称清单。</summary>
    internal void SourceChain(string sourceKey, string chain)
    {
        if (!_scans.TryGetValue(sourceKey, out var scan)) return;
        try
        {
            _log.WriteLine("来源引用链：" + scan.Id.ToString("N") + " | " + sourceKey + " | dwgNo=" + scan.DwgNo + " | " + chain);
            Flush();
        }
        catch (Exception ex) { HasWriteFailure = true; throw new XrefBlockLogException("写入来源引用链失败：" + sourceKey + "；" + ex.Message, ex); }
    }

    /// <summary>主线程扫描检查点只检查当前活动清单的定时写入错误，不遍历全部来源。</summary>
    internal void CheckNameWrites()
    {
        try { _activeScan?.Csv?.Check(); }
        catch (Exception) { HasWriteFailure = true; throw; }
    }

    /// <summary>正常、取消和读取异常均最终刷新关闭，关闭失败仍记录已确认数量并停止批次。</summary>
    internal void EndNames(NameScan scan, string state, string detail)
    {
        if (scan.Finished) return;
        Exception? failure = null;
        try { scan.Csv?.Dispose(); }
        catch (Exception ex) { failure = ex; state = "写入失败"; detail += "；关闭或刷新失败：" + ex.Message; }
        scan.Finished = true;
        scan.State = state;
        if (_activeScan == scan) _activeScan = null;
        try { RecordNames(scan, state, detail); }
        catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
        if (failure != null)
        {
            HasWriteFailure = true;
            throw new XrefBlockLogException("结束来源名称清单失败：" + scan.Path + "；" + failure.Message, failure);
        }
    }

    /// <summary>索引追加开始和结束历史；只在 CAD 主线程操作主报告。</summary>
    private void RecordNames(NameScan scan, string state, string detail)
    {
        Report(() =>
        {
            string written = (scan.Csv?.Written ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string flushed = (scan.Csv?.Flushed ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string elapsed = _clock.GetElapsedTime(scan.Started).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            string[] fields = [scan.DwgNo.ToString(System.Globalization.CultureInfo.InvariantCulture), scan.Id.ToString("N"),
                scan.Source.Key, scan.Source.Drawing, scan.Source.DataSource, Path.GetFileName(scan.Path),
                state, written, flushed, elapsed, detail];
            _sources.WriteLine(string.Join(",", fields.Select(TagCheckJournal.EscapeCsv)));
            _log.WriteLine("来源名称清单：dwgNo=" + scan.DwgNo + " | " + scan.Id.ToString("N") + " | " + scan.Source.Key
                + " | " + scan.Source.Drawing + " | CSV：" + scan.Path + " | 状态：" + state
                + " | 已输出：" + written + " | 已刷新：" + flushed + " | 耗时(ms)：" + elapsed
                + (detail.Length == 0 ? "" : " | " + detail));
            Flush();
        }, "写入来源索引");
    }

    /// <summary>建立关系期间立即记录，取消后仍可追溯已发现的链。</summary>
    public void Relation(XrefBlockRelation relation) => Report(() =>
    {
        _log.WriteLine($"外参关系：{relation.Chain} | {(relation.Overlay ? "覆盖" : "附着")} | {(relation.Included ? "纳入" : "未纳入")} | {relation.Drawing} | {relation.Detail}");
        Flush();
    }, "写入外参关系");

    /// <summary>来源无法读取时只记录真实错误，不生成虚构的 dwgNo 或名称。</summary>
    public void Source(XrefBlockSource source) => Report(() =>
    {
        _log.WriteLine($"来源：dwgNo={source.DwgNo} | {source.Key} | {source.Drawing} | {source.DataSource}");
        foreach (string chain in source.Chains) _log.WriteLine("  引用链：" + chain);
        if (source.Error != null) _log.WriteLine("  读取错误：" + source.Error);
        Flush();
    }, "写入来源信息");

    /// <summary>主线程即时记录范围警告；输出故障必须越过普通来源错误处理。</summary>
    public void Notice(string message) => Report(() => { _log.WriteLine(message); Flush(); }, "写入扫描日志");

    /// <summary>比较前先输出名称统计；取消后的兜底可重复调用但不重复写行。</summary>
    public void WriteStatistics(XrefBlockInventory inventory, XrefBlockStatistics statistics)
    {
        if (HasWriteFailure) throw new XrefBlockLogException("此前输出已失败，停止生成后续名称统计。");
        if (_statisticsWritten) return;
        Report(() =>
        {
            _log.WriteLine("开始输出名称统计：" + (inventory.ScopeComplete ? "完整范围" : "部分范围，扫描不完整"));
            Flush();
            WriteRows(_anonymous, _anonymousStream, statistics.Anonymous);
            WriteRows(_normal, _normalStream, statistics.Normal);
            _statisticsWritten = true;
            _log.WriteLine("名称统计已刷新，候选比较将复用内存清单，不回读 CSV。");
            Flush();
        }, "输出名称统计");
    }

    /// <summary>候选完成后立即写一行三列结果；详细来源、分组、差异和错误保留在 LOG。</summary>
    public void AppendComparison(XrefBlockComparisonResult result)
    {
        if (HasWriteFailure) throw new XrefBlockLogException("此前报告输出失败，停止后续比较。");
        Report(() =>
        {
            _log.WriteLine("块 " + result.Name + "：" + result.StatusText + "；汇总：" + result.ResultText + "；"
                + (result.Complete ? "完整" : "不完整") + "；已确认内容组 " + result.Groups.Count);
            foreach (var entry in result.Entries.OrderBy(e => e.Source.DwgNo))
                _log.WriteLine("  来源 dwgNo=" + entry.Source.DwgNo + " " + entry.Source.Drawing);
            foreach (var group in result.Groups)
                _log.WriteLine("  内容组" + group.Number + "："
                    + string.Join("；", group.Definitions.Select(d => "dwgNo=" + d.Entry.Source.DwgNo + " " + d.Entry.Source.Drawing)));
            string detail = string.Join("\n", result.Differences.Concat(result.Problems));
            if (detail.Length != 0) _log.WriteLine(detail);
            string dwgNo = string.Join("/", result.Entries.Select(e => e.Source.DwgNo).Distinct().OrderBy(number => number)
                .Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            string[] fields = [result.Name, dwgNo, result.ResultText];
            _comparison.WriteLine(string.Join(",", fields.Select(TagCheckJournal.EscapeCsv)));
            _comparison.Flush(); _comparisonStream.Flush(); Flush();
        }, "输出块内容比较");
    }

    /// <summary>只使用内存完成结束汇总，源数据库已经关闭时也不再调用 SDK。</summary>
    public void Complete(XrefBlockScanResult result, XrefBlockStatistics statistics,
        IReadOnlyList<XrefBlockComparisonResult>? comparisons = null, int rootReads = 0, int readers = 0)
    {
        WriteStatistics(result.Inventory, statistics);
        Report(() =>
        {
            if (result.HasIssues)
                foreach (var scan in _scans.Values.Where(s => s.State == "完成"))
                    RecordNames(scan, "完成（批次不完整）", result.StopReason ?? "其他来源缺失、读取失败或关系范围不完整。");
            _log.WriteLine("结束时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            _log.WriteLine("批次状态：" + result.State + (result.HasIssues ? "；统计仅含已确认名称，不代表完整范围。" : ""));
            if (result.StopReason != null) _log.WriteLine("停止原因：" + result.StopReason);
            foreach (var pending in result.Inventory.Sources.Where(s => s.DwgNo == 0))
                _log.WriteLine("未扫描来源：" + pending.Key + " | " + pending.Drawing
                    + (pending.Error == null ? "；尚未进入名称扫描。" : "；" + pending.Error));
            var sourceCounts = result.Inventory.Entries.GroupBy(e => e.Source.DwgNo).ToDictionary(g => g.Key, g => g.Count());
            foreach (var source in result.Inventory.Sources.Where(s => s.DwgNo > 0).OrderBy(s => s.DwgNo))
                _log.WriteLine("来源汇总：dwgNo=" + source.DwgNo + " | " + source.Drawing
                    + " | 已确认名称：" + sourceCounts.GetValueOrDefault(source.DwgNo));
            _log.WriteLine("来源数：" + result.Inventory.Sources.Count(s => s.DwgNo > 0)
                + "；已确认名称条目：" + result.Inventory.Entries.Count
                + "；匿名名称：" + statistics.Anonymous.Count + "；非匿名名称：" + statistics.Normal.Count);
            if (comparisons != null)
            {
                _log.WriteLine("比较候选名称组：" + comparisons.Count
                    + "；实际根读取：" + rootReads + "；来源内容读取器：" + readers);
                _log.WriteLine("比较结论：" + (comparisons.Any(c => c.HasIssues)
                    ? "存在不匹配或未验证项，默认保留日志。" : "已完成候选均匹配；没有候选时无需内容比较。"));
                foreach (var group in comparisons.GroupBy(c => c.ResultText))
                    _log.WriteLine("比较" + group.Key + "：" + group.Count());
            }
            _log.WriteLine("dwg计数按不同来源去重；独立匿名根和单来源名称不做内容比较。");
            Flush();
        }, "输出结束汇总");
    }

    /// <summary>统计同样使用 RFC 4180；分批刷新缓冲，不逐行强制磁盘刷新。</summary>
    private static void WriteRows(StreamWriter writer, Stream stream, IReadOnlyList<XrefBlockNameStatus> rows)
    {
        int written = 0;
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", new[] { row.Name, row.DwgNo,
                row.DwgCount.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Select(TagCheckJournal.EscapeCsv)));
            if (++written % 50 == 0) { writer.Flush(); stream.Flush(); }
        }
        writer.Flush(); stream.Flush();
    }

    /// <summary>CSV 明确固定编码及换行，避免区域设置改变文件结构。</summary>
    private static StreamWriter CsvWriter(Stream stream) =>
        new(stream, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" };

    /// <summary>所有报告故障统一提升为致命输出故障，避免误当普通来源读取失败继续扫描。</summary>
    private void Report(Action action, string operation)
    {
        try { action(); }
        catch (Exception ex)
        {
            HasWriteFailure = true;
            throw new XrefBlockLogException(operation + "失败：" + ex.Message, ex);
        }
    }

    /// <summary>即时刷新主 LOG 和来源索引，不触碰名称清单的阈值定时器。</summary>
    private void Flush()
    {
        _log.Flush(); _sources.Flush();
        foreach (var stream in new[] { _logStream, _sourceStream })
            if (stream is FileStream file) file.Flush(true); else stream.Flush();
    }
    /// <summary>关闭所有名称定时器、文本编码器及文件流；任何失败也继续释放其余文件。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var errors = new List<Exception>();
        foreach (var scan in _scans.Values.Where(s => !s.Finished).ToArray())
            try { EndNames(scan, "已停止", "主报告关闭前终止未结束的名称扫描。"); }
            catch (Exception ex) { errors.Add(ex); }
        foreach (var resource in new IDisposable[] { _comparison, _normal, _anonymous, _sources, _log, _comparisonStream, _normalStream, _anonymousStream, _sourceStream, _logStream })
            try { resource.Dispose(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0)
        {
            HasWriteFailure = true;
            throw new XrefBlockLogException("关闭外参名称报告失败：" + string.Join("；", errors.Select(e => e.Message)),
                new AggregateException(errors));
        }
    }
}
