using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>本批日志的明确归属清单；只登记成功创建的文件。</summary>
/// <remarks>主线程串行使用；关闭全部文件且确认操作结束后才允许删除，不递归清理目录。</remarks>
public sealed class RunLogFiles
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> _streams = [];
    private bool _sealed;
    /// <summary>未知操作或未关闭文件导致必须保留的原因。</summary>
    public string? ProtectionReason { get; private set; }
    /// <summary>本批已登记的日志路径，不包含 DWG。</summary>
    public IReadOnlyList<string> Paths => _files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    /// <summary>是否存在需要用户决定保留的实际文件。</summary>
    public bool HasFiles => _files.Any(File.Exists);
    /// <summary>创建尚不存在的目录，只登记本次创建的末级目录。</summary>
    public void EnsureDirectory(string directory)
    {
        directory = ExportPolicy.Normalize(directory);
        ExportPolicy.RejectLinks(directory);
        bool existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!existed) _directories.Add(directory);
    }
    /// <summary>记录由批次目录预留函数刚刚创建的末级目录。</summary>
    public void OwnCreatedDirectory(string directory) => _directories.Add(ExportPolicy.Normalize(directory));
    /// <summary>成功 CreateNew 后才登记归属；现有同名文件绝不进入清理清单。</summary>
    public FileStream CreateNew(string path)
    {
        path = Validate(path);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _files.Add(path); _streams.Add(stream);
        return stream;
    }
    /// <summary>只允许本工具命名的差异 PNG，CreateNew 成功后才纳入统一保留／删除清单。</summary>
    public FileStream CreateImageReport(string path)
    {
        if (_sealed) throw new InvalidOperationException("日志清单已经封存，不能创建差异图。");
        path = ExportPolicy.Normalize(path);
        ExportPolicy.RejectLinks(path);
        if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),
            @"^VisualDiff__[0-9a-fA-F]{32}__G1-G(?:[2-9]|[1-9][0-9]+)\.png$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("只能创建本批视觉比较差异 PNG：" + path);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _files.Add(path); _streams.Add(stream);
        return stream;
    }
    /// <summary>同目录发布报告成功后才登记目标；移动失败不认领已有目标。</summary>
    public void Publish(string temporary, string destination)
    {
        temporary = Validate(temporary); destination = Validate(destination);
        if (!_files.Contains(temporary)) throw new InvalidOperationException("临时报告不属于本批。");
        File.Move(temporary, destination, false);
        _files.Add(destination);
    }
    /// <summary>未知命令状态必须保留现场；此保护不可被后续成功覆盖。</summary>
    public void Protect(string reason) => ProtectionReason ??= reason;
    /// <summary>日志 Dispose 完成后封存；未关闭的文件不允许删除。</summary>
    public void Seal()
    {
        _sealed = true;
        try { if (_streams.Any(s => s.CanWrite)) Protect("仍有日志文件流未关闭。"); }
        catch (Exception ex) { Protect("无法确认日志流关闭：" + ex.Message); }
    }
    /// <summary>用户明确选 N 后按归属逐文件删除；失败仍继续，目录只在为空时删除。</summary>
    public LogCleanupResult Delete()
    {
        if (!_sealed || ProtectionReason != null)
            return new(0, Paths.Where(File.Exists).ToArray(), [ProtectionReason ?? "日志尚未封存。"]);
        int deleted = 0;
        var errors = new List<string>();
        foreach (string path in Paths)
        {
            try
            {
                ExportPolicy.RejectLinks(path);
                if (File.Exists(path)) { File.Delete(path); deleted++; }
                else if (Directory.Exists(path)) errors.Add(path + "：原文件已被替换为目录，保留。");
            }
            catch (Exception ex) { errors.Add(path + "：" + ex.Message); }
        }
        foreach (string directory in _directories.OrderByDescending(x => x.Length))
        {
            try
            {
                ExportPolicy.RejectLinks(directory);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory, false);
            }
            catch (Exception ex) { errors.Add(directory + "：" + ex.Message); }
        }
        return new(deleted, Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray(), errors);
    }
    /// <summary>日志清单拒绝 DWG 与非日志文件，绝不推断整个输出目录的归属。</summary>
    private static string Validate(string path)
    {
        path = ExportPolicy.Normalize(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".log" or ".csv" or ".json" or ".jsonl" or ".tmp" or ".txt"))
            throw new ArgumentException("只能登记日志、报告或命令诊断文件：" + path);
        ExportPolicy.RejectLinks(path);
        return path;
    }
}

/// <summary>清理后的实际删除数量、剩余文件和失败原因。</summary>
public sealed record LogCleanupResult(int Deleted, IReadOnlyList<string> Remaining, IReadOnlyList<string> Errors);
