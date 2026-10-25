namespace Pn3DKit.Utils;

/// <summary>描述 SAVEAS 式年份标签与可点击命令关键字，独立于 CAD 宿主。</summary>
/// <param name="Year">传给导出策略的格式年份。</param>
/// <param name="Keyword">命令行关键字。</param>
/// <param name="DisplayName">类似 SAVEAS 的可点击显示标签。</param>
public sealed record DwgVersionOption(string Year, string Keyword, string DisplayName);

/// <summary>统一版本提示的选项、默认值及输入解析，不调用 SAVEAS 或修改图纸。</summary>
public static class DwgVersionSelection
{
    /// <summary>保持批量导出默认采用 2013 格式。</summary>
    public const string DefaultYear = "2013";

    /// <summary>仅显示 Plant 导出实际支持的四种格式，不列出 DXF、样板等其他文件类型。</summary>
    public static IReadOnlyList<DwgVersionOption> Options { get; } = ExportPolicy.ExportKeywords.Keys
        .Select(year => new DwgVersionOption(year, "LT" + year, year + "(LT" + year + ")"))
        .ToList().AsReadOnly();

    /// <summary>返回包含可点击标签与年份默认值的 SAVEAS 式命令提示。</summary>
    public static string Message => "\n选择导出文件格式 [" +
        string.Join("/", Options.Select(option => option.DisplayName)) + "] <" + DefaultYear + ">: ";

    /// <summary>将点击结果、年份或完整显示标签归一为已支持的格式年份；空输入选择默认值。</summary>
    /// <param name="input">命令行返回的关键字或直接输入，允许前导全局关键字符号。</param>
    /// <param name="year">成功时返回支持的年份；失败时为空字符串。</param>
    /// <returns>输入是否对应支持的导出格式。</returns>
    public static bool TryParse(string? input, out string year)
    {
        string text = input?.Trim() ?? "";
        if (text.Length == 0) { year = DefaultYear; return true; }
        text = text.TrimStart('_');
        foreach (var option in Options)
        {
            if (text.Equals(option.Year, StringComparison.OrdinalIgnoreCase)
                || text.Equals(option.Keyword, StringComparison.OrdinalIgnoreCase)
                || text.Equals(option.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                year = option.Year;
                return true;
            }
        }
        year = "";
        return false;
    }
}
