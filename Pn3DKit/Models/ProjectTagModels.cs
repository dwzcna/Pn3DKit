namespace Pn3DKit.Models;

/// <summary>预检后明确选择的数据或图纸重建模式；取消不产生写入。</summary>
public enum ProjectTagMode { Data, Drawings, Cancel }
/// <summary>固定一张关联图纸及其中涉及的已确认数据行。</summary>
public sealed record ProjectTagDrawingJob(string Path, IReadOnlyList<string> Keys);
/// <summary>分别记录图纸同步和数据行更新，避免把共享数据已更新误报为每张图均保存。</summary>
public sealed record ProjectTagDrawingResult(string Path, string Status, string Reason, bool Retained);
/// <summary>项目批次的最终行结果、逐图结果及停止原因。</summary>
public sealed record ProjectTagBatchResult(IReadOnlyList<TagRebuildRecord> Records,
    IReadOnlyList<ProjectTagDrawingResult> Drawings, string? StopReason);

/// <summary>数据模式只提交本次修改；进入时已有待提交修改必须拒绝。</summary>
public interface IProjectTagDataHost
{
    /// <summary>核对原项目、主线程及命令文档，检测取消。</summary>
    void EnsureContext();
    /// <summary>相关项目数据库是否还有未提交修改。</summary>
    bool HasPendingChanges();
    /// <summary>重新核对原值、格式、属性、关联与注册键。</summary>
    string? Revalidate(TagRebuildCandidate item);
    /// <summary>返回当前真实占用者，排除自身。</summary>
    IReadOnlyList<string> FindOwners(TagRebuildCandidate item);
    /// <summary>按数据行 SetProperties、AcceptChanges，并回读；失败停止，不全局 Reject。</summary>
    string WriteAndCommit(TagRebuildCandidate item);
}

/// <summary>跨图纸批次适配器；文档生命周期与既有 CAD 宿主一致。</summary>
public interface IProjectTagDrawingHost : ICadHost
{
    /// <summary>检测项目状态和 Esc，不允许外部切换破坏当前操作。</summary>
    void CheckContext();
    /// <summary>在目标文档重建未处理行，验证及刷新已更新的共享行。</summary>
    Task<TagRebuildBatchResult> RebuildAsync(ICadDrawing drawing, IReadOnlyList<TagRebuildPlan> work,
        IReadOnlyList<TagRebuildRecord> synchronize, ITagRebuildJournal journal);
    /// <summary>检查关闭时是否仍有项目缓存待提交；不以 DBMOD 单独断言组数据安全。</summary>
    Task<bool> HasPendingDataAsync(ICadDrawing drawing);
}
