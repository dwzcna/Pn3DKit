using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>实现不依赖 Autodesk 的类型、格式与输出路径策略。</summary>
public static class ExportPolicy
{
    /// <summary>六种 DWG 年份与文件头代码；实际导出支持范围由 ExportKeywords 定义。</summary>
    public static IReadOnlyDictionary<string, string> Versions { get; } =
        new Dictionary<string, string>
        {
            ["2000"] = "AC1015",
            ["2004"] = "AC1018",
            ["2007"] = "AC1021",
            ["2010"] = "AC1024",
            ["2013"] = "AC1027",
            ["2018"] = "AC1032"
        }.AsReadOnly();

    /// <summary>Plant 2026 命令资源中的完整全局格式关键字；裸 LT2013 不是其前缀。</summary>
    public static IReadOnlyDictionary<string, string> ExportKeywords { get; } =
        new Dictionary<string, string>
        {
            ["2007"] = "_2007(LT2007)",
            ["2010"] = "_2010(LT2010)",
            ["2013"] = "_2013(LT2013)",
            ["2018"] = "_2018(LT2018)"
        }.AsReadOnly();

    /// <summary>构造不依赖当前命令语言和缩写推断的完整导出参数。</summary>
    public static object[] ExportArguments(string temporary, string version)
    {
        if (!ExportKeywords.TryGetValue(version, out string? keyword))
            throw new ArgumentException("Plant 2026 导出命令仅支持 2007、2010、2013、2018。");
        return ["_.-EXPORTTOAUTOCAD", keyword, temporary];
    }

    /// <summary>验证导出文件头与所选版本一致，阻止错误提示后回退默认格式的文件发布。</summary>
    public static void ValidateOutput(string path, string version)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[6];
        stream.ReadExactly(header);
        string actual = Encoding.ASCII.GetString(header);
        if (actual != Versions[version])
            throw new IOException($"导出 DWG 格式不符：要求 {Versions[version]}，实际 {actual}；临时文件已保留。");
    }

    /// <summary>按实际绝对路径识别活动图纸；无匹配时默认 PnId。</summary>
    public static ProjectPartKind DetectPart(ProjectCatalog catalog, string? activePath)
    {
        if (string.IsNullOrWhiteSpace(activePath) || !Path.IsPathFullyQualified(activePath))
            return ProjectPartKind.PnId;
        string active = Normalize(activePath);
        bool Contains(IReadOnlyList<string>? paths) => paths?.Any(p =>
            Path.IsPathFullyQualified(p) && Normalize(p).Equals(active, StringComparison.OrdinalIgnoreCase)) == true;
        return Contains(catalog.PnIdDrawings) ? ProjectPartKind.PnId
            : Contains(catalog.PipingDrawings) ? ProjectPartKind.Piping : ProjectPartKind.PnId;
    }

    /// <summary>将显式 P／D 关键字转换为图纸类型；拒绝其他输入。</summary>
    public static ProjectPartKind ParsePart(string keyword) => keyword.ToUpperInvariant() switch
    {
        "P" => ProjectPartKind.PnId,
        "D" => ProjectPartKind.Piping,
        _ => throw new ArgumentException("图纸类型仅支持 P 或 D。", nameof(keyword))
    };

    /// <summary>生成系统文档目录下的批次路径；此步骤不创建目录。</summary>
    public static string SuggestDirectory(string documentsDirectory, DateTime localTime) =>
        RunPaths.Suggest(documentsDirectory, RunLogKind.Export, localTime);

    /// <summary>规范完整绝对路径，拒绝相对路径与设备路径。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("必须提供普通完整绝对路径。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>按目录分隔符与 Windows 大小写规则判断路径边界。</summary>
    public static bool Inside(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>拒绝现存路径中的重解析点，防止输出通过链接越过边界。</summary>
    public static void RejectLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("路径包含目录联接或符号链接：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>检查 CAD 传统路径长度；在提交保存或导出前调用。</summary>
    public static void ValidateCadPath(string path)
    {
        if (path.Length >= 260) throw new PathTooLongException("CAD 路径须少于 260 字符：" + path);
    }

    /// <summary>Plant2026命令实测只接受至132个UTF16字符的输出路径；在打开／保存源图前拒绝，避免再次停留输入。</summary>
    /// <param name="path">实际传给Plant导出命令的临时DWG完整路径。</param>
    public static void ValidatePlantExportPath(string path)
    {
        ValidateCadPath(path);
        if (path.Length > 132)
            throw new PathTooLongException("Plant 2026 导出命令的输出路径不能超过132字符，请缩短输出目录：" + path);
    }

    /// <summary>预检用户选定的目录与报告冲突，不创建目录，也不擅自更换路径。</summary>
    public static string ValidateOutputDirectory(string path)
    {
        string directory = Normalize(path);
        RejectLinks(directory);
        ValidateCadPath(Path.Combine(directory, ".pn3dkit-logs", "report.json.tmp"));
        if (File.Exists(directory)) throw new IOException("所选路径是文件，请选择文件夹。");
        foreach (string name in new[] { ".pn3dkit-run", ".pn3dkit-logs", "Export.log" })
        {
            string reserved = Path.Combine(directory, name);
            if (File.Exists(reserved) || Directory.Exists(reserved))
                throw new IOException("所选目录已有批次日志或预留标记，请选择其他文件夹：" + reserved);
        }
        return directory;
    }

    /// <summary>在所有选项确认后原样预留所选目录；独占标记防止多实例同时使用。</summary>
    public static string ReserveDirectory(string selected)
    {
        string directory = ValidateOutputDirectory(selected);
        Directory.CreateDirectory(directory);
        using var reservation = new FileStream(Path.Combine(directory, ".pn3dkit-run"), FileMode.CreateNew);
        return directory;
    }

    /// <summary>固定去重清单，保留项目相对路径，隔离外部图纸并消解重名；不操作 CAD。</summary>
    public static IReadOnlyList<DrawingJob> CreateJobs(ProjectCatalog catalog, BatchOptions options)
    {
        if (!ExportKeywords.ContainsKey(options.Version)) throw new ArgumentException("Plant 2026 导出命令不支持此 DWG 版本。");
        var drawings = catalog.Drawings(options.Part) ?? throw new InvalidOperationException("当前项目没有所选图纸部分。");
        string projectRoot = Normalize(catalog.RootDirectory), outputRoot = Normalize(options.OutputDirectory);
        var sources = drawings.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<DrawingJob>(sources.Length);
        foreach (string source in sources)
        {
            if (!Path.GetExtension(source).Equals(".dwg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("项目图纸不是 DWG：" + source);
            string relative = Inside(source, projectRoot) ? Path.GetRelativePath(projectRoot, source)
                : Path.Combine("_External", Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..16], Path.GetFileName(source));
            string target = Path.GetFullPath(Path.Combine(outputRoot, relative));
            if (!Inside(target, outputRoot) || sources.Contains(target, StringComparer.OrdinalIgnoreCase))
                throw new IOException("输出不得越界或覆盖项目源图：" + target);
            string original = target;
            for (int suffix = 1; !targets.Add(target); suffix++)
                target = Path.Combine(Path.GetDirectoryName(original)!,
                    Path.GetFileNameWithoutExtension(original) + "_" + suffix.ToString("D2", CultureInfo.InvariantCulture) + ".dwg");
            jobs.Add(new(source, target));
        }
        return jobs;
    }
}
