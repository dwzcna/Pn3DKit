namespace Pn3DKit.Utils;

/// <summary>检查、求值及重建预检共用的 SDK 副作用说明；由命令入口及日志头各输出一次。</summary>
/// <remarks>依据 Plant SDK 开发指南 GUID-08089DFF：^ 在求值后更新序列字段，不能以非自动编号片段推断无副作用。</remarks>
internal static class TagEvaluationNotice
{
    internal const string Text = "注意：SDK 位号求值可能按格式规则（如 ^）更新项目序列字段，检查及重建预检不保证无副作用。";
}
