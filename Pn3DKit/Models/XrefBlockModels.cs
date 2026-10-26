namespace Pn3DKit.Models;

/// <summary>块定义比较的五种状态；完整性独立于已确认冲突。</summary>
public enum XrefBlockStatus { Consistent, Conflict, Unique, Incomplete, ReadError }

/// <summary>来源数据库及其全部引用链；Key 仅标识本批来源，不参与内容比较。</summary>
public sealed record XrefBlockSource(string Key, string Drawing, string DataSource, bool IsHost,
    IReadOnlyList<string> Chains, string? Error = null);

/// <summary>外参关系，覆盖边界不属于读取失败；循环及错误须说明原因。</summary>
public sealed record XrefBlockRelation(string Parent, string Alias, string Drawing, string Chain,
    bool Overlay, bool Included, string Detail);

/// <summary>内容图中的有向关联；相同 Label 的边构成多重集，有序关系由适配层编码序号。</summary>
public sealed record BlockContentEdge(string Label, string Target);

/// <summary>忽略数据库身份后的类型、精确字段及关联；Id 只用于图内连接。</summary>
public sealed record BlockContentNode(string Id, string Kind, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<BlockContentEdge> Edges, IReadOnlyDictionary<string, byte[]>? Payloads = null);

/// <summary>根定义及可达依赖图；不完整原因和读取错误不能被哈希相同掩盖。</summary>
public sealed record BlockContentSnapshot(string Root, IReadOnlyDictionary<string, BlockContentNode> Nodes,
    IReadOnlyList<string> Issues, string? ReadError = null)
{
    /// <summary>只有完整成功读取的快照才可以归入相同内容组。</summary>
    public bool Complete => ReadError == null && Issues.Count == 0;
}

/// <summary>来源中的一个原始命名块；直接引用数与模型／布局可达使用状态分别记录。</summary>
public sealed record XrefBlockDefinition(string Name, XrefBlockSource Source, BlockContentSnapshot Content,
    int DirectReferences, bool Used)
{
    /// <summary>不把未使用父块中的引用冒充实际使用。</summary>
    public string Usage => (Used ? "已使用" : "未使用定义") + "；直接引用数 " + DirectReferences;
}

/// <summary>完整等价定义组成一组；不完整定义不与其他来源合并。</summary>
public sealed record XrefBlockGroup(string Number, IReadOnlyList<XrefBlockDefinition> Definitions);

/// <summary>同名定义的内容分组、差异和合并风险；已知冲突与未完整比较可以同时存在。</summary>
public sealed record XrefBlockResult(string Name, XrefBlockStatus Status, bool Complete,
    IReadOnlyList<XrefBlockGroup> Groups, IReadOnlyList<string> Differences,
    IReadOnlyList<string> Issues, string Risk)
{
    /// <summary>报告使用的明确中文状态。</summary>
    public string StatusText => Status switch
    {
        XrefBlockStatus.Consistent => "一致",
        XrefBlockStatus.Conflict => "冲突",
        XrefBlockStatus.Unique => "无同名定义，无需比较",
        XrefBlockStatus.ReadError => "读取错误",
        _ => "无法完整比较"
    };
}

/// <summary>记录本批结束状态，避免把取消或未知来源当作检查正常。</summary>
public sealed record XrefBlockBatchResult(IReadOnlyList<XrefBlockResult> Results, bool ScopeComplete,
    bool Interrupted, string? StopReason, int? Total = null)
{
    /// <summary>冲突、不完整、错误或中断时默认保留报告。</summary>
    public bool HasIssues => !ScopeComplete || Interrupted || StopReason != null || Total.HasValue && Results.Count != Total.Value
        || Results.Any(x => !x.Complete || x.Status is XrefBlockStatus.Conflict or XrefBlockStatus.Incomplete or XrefBlockStatus.ReadError);
}
