using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>创建两件外参检查报告，来源、关系和块结果逐条刷新；失败立即向批次传播。</summary>
public sealed class XrefBlockJournal : IDisposable
{
    private readonly FileStream _logStream, _csvStream;
    private readonly StreamWriter _log, _csv;
    /// <summary>文本报告路径。</summary>
    public string LogPath { get; }
    /// <summary>CSV 报告路径。</summary>
    public string CsvPath { get; }

    /// <summary>检查两件文件名再以 CreateNew 创建，避免覆盖或误认领其他批次文件。</summary>
    public XrefBlockJournal(string directory, string drawing, RunLogFiles files)
    {
        directory = Validate(directory);
        files.EnsureDirectory(directory);
        LogPath = Path.Combine(directory, "XrefBlockCheck.log");
        CsvPath = Path.Combine(directory, "XrefBlockCheck.csv");
        _logStream = files.CreateNew(LogPath);
        try { _csvStream = files.CreateNew(CsvPath); }
        catch { _logStream.Dispose(); throw; }
        _log = new(_logStream, new UTF8Encoding(false), leaveOpen: true);
        _csv = new(_csvStream, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" };
        try
        {
            _log.WriteLine("Pn3DKit | XREFBLOCKCHECK");
            _log.WriteLine("主图：" + drawing);
            _log.WriteLine("开始时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            _log.WriteLine("用途：Bind 的 Insert 合并前同名块冲突检查；不执行绑定或保存，不承诺模拟最终合并结果。");
            _csv.WriteLine("块名,状态,定义分组,来源图纸,引用链,使用情况,差异及错误详情");
            Flush();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>只验证目录及本命令的两个固定日志名，不创建文件。</summary>
    public static string Validate(string path)
    {
        string directory = ExportPolicy.Normalize(path);
        ExportPolicy.RejectLinks(directory);
        if (File.Exists(directory)) throw new IOException("所选路径是文件，请选择文件夹。");
        foreach (string name in new[] { "XrefBlockCheck.log", "XrefBlockCheck.csv" })
            if (File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name)))
                throw new IOException("所选目录已有同名日志，请选择其他文件夹：" + Path.Combine(directory, name));
        return directory;
    }

    /// <summary>建立关系期间即可写入，不因稍后取消丢失已发现的来源链。</summary>
    public void Relation(XrefBlockRelation relation)
    {
        _log.WriteLine($"外参关系：{relation.Chain} | {(relation.Overlay ? "覆盖" : "附着")} | {(relation.Included ? "纳入" : "未纳入")} | {relation.Drawing} | {relation.Detail}");
        Flush();
    }

    /// <summary>报告来源读取方式和错误；无法建立块清单时仍有独立 CSV 记录。</summary>
    public void Source(XrefBlockSource source)
    {
        _log.WriteLine($"来源：{source.Key} | {source.Drawing} | {source.DataSource}");
        foreach (string chain in source.Chains) _log.WriteLine("  引用链：" + chain);
        if (source.Error != null)
        {
            _log.WriteLine("  读取错误：" + source.Error);
            foreach (string chain in source.Chains.DefaultIfEmpty(""))
                Csv("", "读取错误", "", source.Drawing, chain, "", source.Error);
        }
        Flush();
    }

    /// <summary>读取阶段的具体警告立即落盘，不等待全部比较结束。</summary>
    public void Notice(string message) { _log.WriteLine(message); Flush(); }

    /// <summary>每个完整内容组及未知来源均保留引用链，不重复输出所有两两比较。</summary>
    public void Append(XrefBlockResult result)
    {
        _log.WriteLine($"块 {result.Name}：{result.StatusText}；完整性：{(result.Complete ? "完整" : "无法完整比较")}；已确认 {result.Groups.Count(g => !g.Number.StartsWith("未完整", StringComparison.Ordinal))} 种定义");
        string detail = string.Join("\n", result.Differences.Concat(result.Issues).Append(result.Risk).Where(s => s.Length != 0));
        foreach (var group in result.Groups)
        {
            _log.WriteLine("定义 " + group.Number + "：");
            foreach (var definition in group.Definitions)
            {
                _log.WriteLine("  " + definition.Source.Drawing + " | " + definition.Usage);
                foreach (string chain in definition.Source.Chains.DefaultIfEmpty(""))
                {
                    _log.WriteLine("    " + chain);
                    Csv(result.Name, result.StatusText, group.Number, definition.Source.Drawing, chain, definition.Usage,
                        "数据来源：" + definition.Source.DataSource + "；来源标识：" + definition.Source.Key + "；"
                            + (result.Complete ? "" : "无法完整比较；") + detail);
                }
            }
        }
        if (detail.Length != 0) _log.WriteLine(detail);
        Flush();
    }

    /// <summary>结束或中断时保存已完成数量；不完整范围不会被正常结束覆盖。</summary>
    public void Complete(XrefBlockBatchResult result)
    {
        _log.WriteLine("结束时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        _log.WriteLine("批次状态：" + (result.Interrupted ? "已中断" : result.StopReason != null ? "已停止"
            : result.ScopeComplete && result.Results.All(x => x.Complete) ? "已完成" : "检查不完整"));
        if (result.StopReason != null) _log.WriteLine("停止原因：" + result.StopReason);
        _log.WriteLine("总块名：" + (result.Total?.ToString() ?? "清单未完成，未知")
            + "；已完成：" + result.Results.Count + "；未完成："
            + (result.Total.HasValue ? Math.Max(0, result.Total.Value - result.Results.Count).ToString() : "未知"));
        foreach (var group in result.Results.GroupBy(x => x.StatusText)) _log.WriteLine(group.Key + "：" + group.Count());
        Flush();
    }

    /// <summary>CSV 字段统一采用已有 RFC 4180 转义。</summary>
    private void Csv(params string[] fields) => _csv.WriteLine(string.Join(",", fields.Select(TagCheckJournal.EscapeCsv)));
    /// <summary>同时刷新编码器和文件流，已完成记录可被外部只读查看器立即读取。</summary>
    private void Flush()
    {
        _log.Flush(); _csv.Flush(); _logStream.Flush(true); _csvStream.Flush(true);
    }
    /// <summary>即使一个关闭失败，也尝试关闭剩余报告流。</summary>
    public void Dispose()
    {
        try { _csv.Dispose(); }
        finally { try { _log.Dispose(); } finally { try { _csvStream.Dispose(); } finally { _logStream.Dispose(); } } }
    }
}
