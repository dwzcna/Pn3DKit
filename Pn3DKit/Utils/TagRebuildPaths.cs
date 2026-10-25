using System.Globalization;

namespace Pn3DKit.Utils;

/// <summary>重建日志目录的建议和冲突校验；选择取消之前不创建文件。</summary>
public static class TagRebuildPaths
{
    /// <summary>生成文档目录下的独立时间戳建议，已有目录时递增后缀。</summary>
    public static string Suggest(string documentsDirectory, DateTime localTime)
    {
        string original = Path.Combine(ExportPolicy.Normalize(documentsDirectory), "Pn3DKit.TagRebuild",
            localTime.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture));
        string path = original;
        for (int index = 1; Directory.Exists(path) || File.Exists(path); index++)
            path = original + "_" + index.ToString("D2", CultureInfo.InvariantCulture);
        return path;
    }

    /// <summary>拒绝路径链接、文件路径及任何同名日志，实际打开仍使用 CreateNew。</summary>
    public static string Validate(string path)
    {
        string directory = ExportPolicy.Normalize(path);
        ExportPolicy.RejectLinks(directory);
        if (File.Exists(directory)) throw new IOException("所选路径是文件，请选择文件夹。");
        foreach (string name in new[] { "TagRebuild.log", "TagRebuild.csv" })
            if (File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name)))
                throw new IOException("所选目录已有同名日志，请选择其他文件夹：" + Path.Combine(directory, name));
        return directory;
    }
}
