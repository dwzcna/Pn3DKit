namespace Pn3DKit.Models;

/// <summary>对象自身属性的目录项；仅持有文本，不把 SDK 对象交给弹窗。</summary>
/// <param name="PropertyPath">内部属性名，可作为 TargetObject 简写输入。</param>
/// <param name="FriendlyDisplayName">实际类别配置的有效显示名称，缺失时使用内部名。</param>
/// <param name="Detail">元数据读取失败的具体原因。</param>
public sealed record PnPropertyCatalogEntry(string PropertyPath, string FriendlyDisplayName, string Detail = "");

/// <summary>检索与追加时核对的不可变目标快照，身份必须使用完整数据库 ObjectId。</summary>
public sealed record PnPropertyCatalogContext<TIdentity, TTarget>(
    TIdentity Identity, TTarget Target, long Epoch, string TargetKey, int SelectionCount);
