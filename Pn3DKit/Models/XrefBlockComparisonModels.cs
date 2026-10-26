namespace Pn3DKit.Models;

/// <summary>身份无关的有向内容关联；同标签重复边保留重复实体数量。</summary>
public sealed record BlockContentEdge(string Label, string Target);
/// <summary>只读类型化内容节点，Id 仅用于本快照内定位，不参与内容身份。</summary>
public sealed record BlockContentNode(string Id, string Kind, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<BlockContentEdge> Edges, IReadOnlyDictionary<string, byte[]>? Payloads = null);
/// <summary>根定义、必要依赖及未完整解释原因；未知内容不能证明一致。</summary>
public sealed record BlockContentSnapshot(string Root, IReadOnlyDictionary<string, BlockContentNode> Nodes,
    IReadOnlyList<string> Issues, string? ReadError = null)
{
    /// <summary>成功读取并解释的完整快照。</summary>
    public bool Complete => ReadError == null && Issues.Count == 0;
}
/// <summary>只包含跨来源同名普通块的候选及原始来源编号。</summary>
public sealed record XrefBlockCandidate(string Name, IReadOnlyList<XrefBlockNameEntry> Entries);
/// <summary>一个来源中的块定义快照，不读取插入数量或模型空间实例。</summary>
public sealed record XrefComparedDefinition(XrefBlockNameEntry Entry, BlockContentSnapshot Content);
/// <summary>内容已经严格核对相同的来源组；未知来源不进入组。</summary>
public sealed record XrefBlockContentGroup(int Number, IReadOnlyList<XrefComparedDefinition> Definitions);
/// <summary>比较结果与批次中断分开，不把未执行当作内容相同。</summary>
public enum XrefBlockComparisonStatus { Consistent, Different, Incomplete, ReadError, NotExecuted }
/// <summary>单个候选名称的来源归组、具体差异及独立完整性。</summary>
public sealed record XrefBlockComparisonResult(string Name, XrefBlockComparisonStatus Status, bool Complete,
    IReadOnlyList<XrefBlockNameEntry> Entries, IReadOnlyList<XrefBlockContentGroup> Groups,
    IReadOnlyList<string> Differences, IReadOnlyList<string> Problems)
{
    /// <summary>详细日志保留内部状态，区分读取错误、未执行及不完整原因。</summary>
    public string StatusText => Status switch
    {
        XrefBlockComparisonStatus.Consistent => "一致",
        XrefBlockComparisonStatus.Different => "不一致",
        XrefBlockComparisonStatus.ReadError => "读取错误",
        XrefBlockComparisonStatus.NotExecuted => "未执行",
        _ => "无法完整比较"
    };
    /// <summary>最终报告仅使用三种结论；已确认差异优先，未完整验证不能报告匹配。</summary>
    public string ResultText => Status == XrefBlockComparisonStatus.Different ? "不匹配"
        : Complete && Status == XrefBlockComparisonStatus.Consistent ? "匹配" : "未验证";
    /// <summary>不一致、未知、错误及未执行均默认保留日志。</summary>
    public bool HasIssues => !Complete || Status != XrefBlockComparisonStatus.Consistent;
}
