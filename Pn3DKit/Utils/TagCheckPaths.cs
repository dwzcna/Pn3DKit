using System.Globalization;

namespace Pn3DKit.Utils;

/// <summary>生成并验证位号检查日志目录，不沿用 DWG 导出的预留文件限制。</summary>
public static class TagCheckPaths
{
    /// <summary>使用系统文档目录和本地时间生成建议目录；此方法不创建目录。</summary>
    public static string Suggest(string documentsDirectory, DateTime localTime) =>
        RunPaths.Suggest(documentsDirectory, RunLogKind.TagCheck, localTime);

    /// <summary>检查完整目录及两件日志的冲突；实际写入仍使用 CreateNew 防止竞态覆盖。</summary>
    public static string Validate(string path)
    {
        string directory = ExportPolicy.Normalize(path);
        ExportPolicy.RejectLinks(directory);
        if (File.Exists(directory)) throw new IOException("所选路径是文件，请选择文件夹。");
        foreach (string name in new[] { "TagCheck.log", "TagCheck.csv" })
        {
            string file = Path.Combine(directory, name);
            if (File.Exists(file) || Directory.Exists(file))
                throw new IOException("所选目录已有同名日志，请选择其他文件夹：" + file);
        }
        return directory;
    }
}
