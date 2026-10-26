using System.Globalization;

namespace Pn3DKit.Utils;

/// <summary>统一文档目录下的各类批次目录。</summary>
public enum RunLogKind { Export, TagCheck, TagRebuild, ProjectTagCheck, ProjectTagRebuild, XrefBlockCheck }

/// <summary>只生成路径，不迁移历史目录；系统文档重定向时沿用系统位置。</summary>
public static class RunPaths
{
    /// <summary>取得系统文档目录，不可用时回退用户主目录下 Documents。</summary>
    public static string Documents()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents") : path;
    }
    /// <summary>生成 Pn3DKit/用途/时间戳建议，重名追加序号，不创建目录。</summary>
    public static string Suggest(string documents, RunLogKind kind, DateTime time)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        string original = Path.Combine(ExportPolicy.Normalize(documents), "Pn3DKit", kind.ToString(),
            time.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture));
        string path = original;
        for (int index = 1; Directory.Exists(path) || File.Exists(path); index++)
            path = original + "_" + index.ToString("D2", CultureInfo.InvariantCulture);
        return path;
    }
}
