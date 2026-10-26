namespace Pn3DKit.Models;

/// <summary>本批来源及全部引用链；dwgNo 按首次名称扫描顺序分配，0 表示尚未进入扫描。</summary>
public sealed record XrefBlockSource(string Key, string Drawing, string DataSource, bool IsHost,
    IReadOnlyList<string> Chains, string? Error = null, int DwgNo = 0);

/// <summary>已确认的原始块名及 SDK 匿名标志，不包含实体内容或引用数量。</summary>
public sealed record XrefBlockNameEntry(string Name, XrefBlockSource Source, bool IsAnonymous);

/// <summary>内存名称清单；取消后仍可捕获已确认条目，范围完整性单独标明。</summary>
public sealed record XrefBlockInventory(IReadOnlyList<XrefBlockNameEntry> Entries,
    IReadOnlyList<XrefBlockSource> Sources, bool ScopeComplete);

/// <summary>外参关系及附着／覆盖边界，保留完整到达链和读取问题。</summary>
public sealed record XrefBlockRelation(string Parent, string Alias, string Drawing, string Chain,
    bool Overlay, bool Included, string Detail);

/// <summary>一种块名涉及的去重来源编号，不表示插入数或内容是否相同。</summary>
public sealed record XrefBlockNameStatus(string Name, IReadOnlyList<int> DwgNumbers)
{
    /// <summary>来源编号以数值升序用斜线连接。</summary>
    public string DwgNo => string.Join("/", DwgNumbers);
    /// <summary>包含该名称的不同来源数据库数量。</summary>
    public int DwgCount => DwgNumbers.Count;
}

/// <summary>匿名和普通块名分别汇总，单来源名称也保留。</summary>
public sealed record XrefBlockStatistics(IReadOnlyList<XrefBlockNameStatus> Anonymous,
    IReadOnlyList<XrefBlockNameStatus> Normal);

/// <summary>本次扫描的结束状态；名称重复不构成错误或内容冲突。</summary>
public sealed record XrefBlockScanResult(XrefBlockInventory Inventory, bool Interrupted = false, string? StopReason = null)
{
    /// <summary>只有全范围完成且没有停止／读取错误时，日志保留才默认 N。</summary>
    public bool HasIssues => !Inventory.ScopeComplete || Interrupted || StopReason != null
        || Inventory.Sources.Any(s => s.Error != null);
    /// <summary>供命令行和日志使用的真实扫描状态。</summary>
    public string State => Interrupted ? "已中断" : StopReason != null ? "已停止" : HasIssues ? "扫描不完整" : "已完成";
}
