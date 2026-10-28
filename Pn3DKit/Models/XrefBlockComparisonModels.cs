namespace Pn3DKit.Models;

/// <summary>跨来源同名普通块候选及来源编号，不含旧字段快照。</summary>
public sealed record XrefBlockCandidate(string Name, IReadOnlyList<XrefBlockNameEntry> Entries);

/// <summary>详细报告状态，最终CSV只映射匹配、不匹配及未验证。</summary>
public enum XrefBlockComparisonStatus { Consistent, Different, Incomplete, ReadError, NotExecuted }
