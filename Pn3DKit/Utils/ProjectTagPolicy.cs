using System.Globalization;
using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>项目模式、图纸任务与日志目录的离线策略。</summary>
public static class ProjectTagPolicy
{
    /// <summary>回车为明确约定的 N；Esc 由调用方映射取消。</summary>
    public static ProjectTagMode ParseMode(string? input) => (input ?? "").Trim().ToUpperInvariant() switch
    {
        "" or "N" => ProjectTagMode.Data,
        "Y" => ProjectTagMode.Drawings,
        "C" => ProjectTagMode.Cancel,
        _ => throw new ArgumentException("请选择 Y、N 或 C。")
    };

    /// <summary>共享数据行只作为一个目标，但每张关联图纸都列入清单。</summary>
    public static IReadOnlyList<ProjectTagDrawingJob> DrawingJobs(IEnumerable<TagRebuildPlan> plans) =>
        plans.Where(x => x.State == TagRebuildState.Ready)
            .SelectMany(x => x.Item.Check.Subject.Drawings.Select(path => (Path: ExportPolicy.Normalize(path), x.Item.Key)))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectTagDrawingJob(g.Key, g.Select(x => x.Key).Distinct().ToArray())).ToArray();

    /// <summary>项目日志建议路径不创建文件；时间戳冲突递增。</summary>
    public static string Suggest(string documents, DateTime time, bool rebuild) =>
        RunPaths.Suggest(documents, rebuild ? RunLogKind.ProjectTagRebuild : RunLogKind.ProjectTagCheck, time);

    /// <summary>只验证所选项目日志对，不覆盖任何现有同名文件。</summary>
    public static string Validate(string path, bool rebuild)
    {
        path = ExportPolicy.Normalize(path);
        ExportPolicy.RejectLinks(path);
        if (File.Exists(path)) throw new IOException("所选路径是文件。");
        string name = rebuild ? "ProjectTagRebuild" : "ProjectTagCheck";
        foreach (string ext in new[] { ".log", ".csv" })
            if (File.Exists(Path.Combine(path, name + ext)) || Directory.Exists(Path.Combine(path, name + ext)))
                throw new IOException("所选目录已有同名日志，请重新选择：" + Path.Combine(path, name + ext));
        return path;
    }
}
