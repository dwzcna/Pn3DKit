namespace Pn3DKit.Models;

/// <summary>指定当前 Plant 项目中的图纸类型。</summary>
public enum ProjectPartKind { PnId, Piping }

/// <summary>保存项目清单快照；空清单引用表示项目中没有对应部分。</summary>
/// <param name="RootDirectory">当前 Plant 项目根目录。</param>
/// <param name="PnIdDrawings">PnId 图纸的实际路径，或不存在的项目部分。</param>
/// <param name="PipingDrawings">Piping 图纸的实际路径，或不存在的项目部分。</param>
public sealed record ProjectCatalog(string RootDirectory,
    IReadOnlyList<string>? PnIdDrawings, IReadOnlyList<string>? PipingDrawings)
{
    /// <summary>返回所选项目部分的图纸清单，不访问 CAD。</summary>
    public IReadOnlyList<string>? Drawings(ProjectPartKind kind) =>
        kind == ProjectPartKind.PnId ? PnIdDrawings : PipingDrawings;
}

/// <summary>保存命令行确认后的批次选项；输出目录在正式执行时预留。</summary>
public sealed record BatchOptions(ProjectPartKind Part, bool SaveChanges, string Version, string OutputDirectory);

/// <summary>描述源图与正式输出之间的路径映射。</summary>
public sealed record DrawingJob(string Source, string Destination);

/// <summary>保存单图的兼容四字段结果；状态使用稳定英文值。</summary>
public sealed record DrawingResult(string Source, string Destination, string Status, string Message);

/// <summary>返回批次统计、停止原因及真实报告位置。</summary>
public sealed record BatchResult(int Total, IReadOnlyList<DrawingResult> Items,
    bool Canceled, string? StopReason, string ReportPath)
{
    /// <summary>生成供 CAD 命令行显示的中文统计。</summary>
    public string Summary => $"成功 {Items.Count(x => x.Status == "Exported")}，跳过 {Items.Count(x => x.Status == "Skipped")}，失败 {Items.Count(x => x.Status == "Failed")}，取消 {Items.Count(x => x.Status == "Canceled")}，未处理 {Total - Items.Count}。";
}

/// <summary>保存主线程在目标文档短锁内读取的修改及只读状态。</summary>
public sealed record DrawingState(bool Modified, bool ReadOnly);

/// <summary>标识宿主持有的文档；此接口不拥有 CAD 文档的生命周期。</summary>
public interface ICadDrawing { }

/// <summary>隔离 CAD 操作与批次策略；实现必须在宿主主线程安排各上下文。</summary>
/// <remarks>任务完成表示同步 CAD 操作与回调清理均已结束，不表示已经关闭源图。</remarks>
public interface ICadHost
{
    /// <summary>返回批次开始前的活动文档；允许起始页没有文档。</summary>
    ICadDrawing? OriginalDrawing { get; }
    /// <summary>核对原项目仍为当前项目；变更时抛出停止异常。</summary>
    void EnsureProject();
    /// <summary>查找已有文档；不打开、不激活图纸。</summary>
    ICadDrawing? FindOpen(string source);
    /// <summary>在应用上下文打开文档；失败时必须说明状态是否明确。</summary>
    Task<ICadDrawing> OpenAsync(string source);
    /// <summary>在应用上下文激活指定文档。</summary>
    Task ActivateAsync(ICadDrawing drawing);
    /// <summary>主线程在应用上下文和短文档锁内检查状态，不启动CAD命令。</summary>
    Task<DrawingState> ReadStateAsync(ICadDrawing drawing);
    /// <summary>通过Editor.CommandAsync执行一次QSAVE，等待命令返回、空闲及监听清理。</summary>
    Task SaveAsync(ICadDrawing drawing);
    /// <summary>通过原生命令导出临时文件，状态不明时停止；失败不得发布正式输出。</summary>
    Task ExportAsync(ICadDrawing drawing, string temporary, string version);
    /// <summary>在应用上下文关闭工具打开且已确认无修改的文档。</summary>
    Task CloseAsync(ICadDrawing drawing);
}

/// <summary>携带 CAD 失败后的状态，供调度器决定是否允许继续或清理。</summary>
/// <param name="message">向用户显示的失败原因。</param>
/// <param name="stateKnown">是否确认同步操作已结束且文档状态明确。</param>
/// <param name="stopBatch">是否必须停止后续图纸。</param>
/// <param name="canceled">是否由用户取消。</param>
/// <param name="inner">原始异常。</param>
public sealed class CadOperationException(string message, bool stateKnown = false,
    bool stopBatch = true, bool canceled = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>操作失败后是否仍可以安全检查文档。</summary>
    public bool StateKnown { get; } = stateKnown;
    /// <summary>是否停止后续图纸。</summary>
    public bool StopBatch { get; } = stopBatch;
    /// <summary>是否为取消。</summary>
    public bool Canceled { get; } = canceled;
}
