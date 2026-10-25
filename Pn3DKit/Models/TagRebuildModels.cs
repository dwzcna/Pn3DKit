namespace Pn3DKit.Models;

/// <summary>只读预检的四种分类。</summary>
public enum TagRebuildState { Consistent, Ready, Unavailable, Conflict }
/// <summary>CSV 追加事件；待重建是预览，其余为对象最终动作。</summary>
public enum TagRebuildAction { Pending, Updated, Skipped, Conflict, Failed, Unexecuted }

/// <summary>以项目部分和数据行标识对象，同一行的多个图形句柄合并为一条。</summary>
/// <param name="Key">项目部分及数据行的稳定组合，不使用托管包装引用。</param>
/// <param name="Check">共享只读检查结果，句柄可列出同一数据行的全部图形。</param>
/// <param name="Registry">位号注册表名；空值表示只能在本图保守检查。</param>
/// <param name="CandidateTag">SDK 转换后的注册键；不可直接将显示 Tag 送入注册表。</param>
/// <param name="Exclusion">范围或 SDK 错误导致的不可写原因。</param>
public sealed record TagRebuildCandidate(string Key, TagCheckResult Check, string Registry = "",
    string CandidateTag = "", string? Exclusion = null);

/// <summary>保存预检分类及涉及对象的冲突证据。</summary>
public sealed record TagRebuildPlan(TagRebuildCandidate Item, TagRebuildState State, string Reason);

/// <summary>保存可即时写盘的对象动作，不持有 SDK 对象。</summary>
public sealed record TagRebuildRecord(TagRebuildCandidate Item, TagRebuildAction Action, string Reason)
{
    /// <summary>命令行和文件使用相同的动作名称。</summary>
    public string ActionText => Action switch
    {
        TagRebuildAction.Pending => "待重建",
        TagRebuildAction.Updated => "已更新",
        TagRebuildAction.Skipped => "跳过",
        TagRebuildAction.Conflict => "冲突",
        TagRebuildAction.Failed => "失败",
        _ => "未执行"
    };
}

/// <summary>批次的最终动作与停止信息；允许保存必须同时满足上下文和撤销收尾明确。</summary>
public sealed record TagRebuildBatchResult(IReadOnlyList<TagRebuildRecord> Records,
    bool Attempted, bool SafeToSave, string? StopReason);

/// <summary>写入异常携带数据状态是否仍可回读；未知状态必须停止后续写入。</summary>
public sealed class TagRebuildWriteException(string message, bool stateKnown, Exception inner)
    : Exception(message, inner)
{
    /// <summary>是否已成功回读异常后的真实 Tag。</summary>
    public bool StateKnown { get; } = stateKnown;
}

/// <summary>由 CAD 主线程实现的重建操作；预检和日志策略可离线验证。</summary>
public interface ITagRebuildHost
{
    /// <summary>核对原图纸、项目及可写状态，检测 Esc；失败停止批次。</summary>
    void EnsureContext();
    /// <summary>开始独立的撤销组；失败不得执行任何写入。</summary>
    void BeginUndo();
    /// <summary>仅结束本次成功开始的撤销组，失败后不允许自动保存。</summary>
    void EndUndo();
    /// <summary>写入前核对原 Tag、格式及期望值；变化时返回跳过原因。</summary>
    string? Revalidate(TagRebuildCandidate item);
    /// <summary>重新查询占用者；不含自身数据行。</summary>
    IReadOnlyList<string> FindOwners(TagRebuildCandidate item);
    /// <summary>按 Tag 列写入并立即回读，异常须含 SDK 状态和可能的实际值。</summary>
    string WriteAndRead(TagRebuildCandidate item);
    /// <summary>有成功修改后刷新当前图纸；失败记录并保留数据。</summary>
    void Regen();
}

/// <summary>记录预检和写入事件；写入失败向调用方传播以停止后续修改。</summary>
public interface ITagRebuildJournal
{
    /// <summary>追加一条对象历史并立即刷新。</summary>
    void Append(TagRebuildRecord record);
    /// <summary>追加运行及生命周期信息并立即刷新。</summary>
    void Message(string message);
}
