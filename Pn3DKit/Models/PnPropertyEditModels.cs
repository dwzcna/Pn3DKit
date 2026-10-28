namespace Pn3DKit.Models;

/// <summary>位号占用的实际登记身份；不同项目部分或注册表的相同显示文字不混同。</summary>
public sealed record PnPropertyRegistration(string Part, string Registry, string Candidate);

/// <summary>只读预检的写入计划，不持有 SDK 对象；DataKey 标识项目中的同一实际数据行。</summary>
public sealed record PnPropertyWritePlan<TTarget>(TTarget Target, string DataKey, string? Original, string Value,
    PnPropertyRegistration? Registration = null)
{
    /// <summary>SDK 列类型；缺失或字符串时继续严格比较原始文本。</summary>
    public Type? ValueType { get; init; }
    /// <summary>预检使用的解析区域，提交期间不随线程区域变化。</summary>
    public string ValueCulture { get; init; } = "";
}

/// <summary>逐对象实际处理状态；失败不意味着 Plant 数据已经回滚。</summary>
public enum PnPropertyWriteState { Updated, Unchanged, Failed, NotExecuted }

/// <summary>保留原值、请求值、实际回读及错误，别名对象仍分别列示。</summary>
public sealed record PnPropertyWriteResult<TTarget>(TTarget Target, string Label, string DataKey, string? Original,
    string Requested, string? Actual, PnPropertyWriteState State, string Detail = "")
{
    /// <summary>区分已成功回读的真实 null 与无法回读；占位文字不充当实际值。</summary>
    public bool ActualIsNull { get; init; }
}

/// <summary>整个面板编辑请求的结果，预检拒绝时不会调用写入。</summary>
public sealed record PnPropertyEditResult<TTarget>(bool PreflightPassed, IReadOnlyList<PnPropertyWriteResult<TTarget>> Items,
    string Message = "")
{
    /// <summary>底部计数与 Tooltip 使用同一实际结果，不把部分成功显示为全量成功。</summary>
    public string Summary => $"已更新 {Items.Count(x => x.State == PnPropertyWriteState.Updated)}；无需修改 {Items.Count(x => x.State == PnPropertyWriteState.Unchanged)}；失败 {Items.Count(x => x.State == PnPropertyWriteState.Failed)}；未执行 {Items.Count(x => x.State == PnPropertyWriteState.NotExecuted)}";
    /// <summary>逐项详情不截断目标集合，保留空值及无法回读的区别。</summary>
    public string Details => Summary + (Message.Length == 0 ? "" : "\n" + Message) + "\n" + string.Join("\n",
        Items.Select(x => $"{x.Label} [{x.DataKey}] {StateText(x.State)}：{x.Original ?? "(null)"} → {x.Requested}；实际：{(x.ActualIsNull ? "(null)" : x.Actual ?? "无法回读")}"
            + (x.Detail.Length == 0 ? "" : "；" + x.Detail)));

    private static string StateText(PnPropertyWriteState state) => state switch
    {
        PnPropertyWriteState.Updated => "已更新", PnPropertyWriteState.Unchanged => "无需修改",
        PnPropertyWriteState.Failed => "失败", _ => "未执行"
    };
}
