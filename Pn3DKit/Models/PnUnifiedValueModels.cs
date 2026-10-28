namespace Pn3DKit.Models;

/// <summary>统一键值的纯文本请求；草稿修订独立于路径修订，不携带 SDK 对象。</summary>
/// <param name="Draft">null 表示尚无草稿；空字符串表示用户明确输入的空草稿。</param>
/// <remarks>确认仅回填草稿，真正写入仍需值格中另一次 Enter；票据和两种修订均须保持。</remarks>
public sealed record PnUnifiedValueRequest(Guid Token, Guid RowId, long RowRevision, string Path,
    long DraftRevision, string? Draft);
