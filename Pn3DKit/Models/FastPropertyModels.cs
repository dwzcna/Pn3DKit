namespace Pn3DKit.Models;

/// <summary>面板固定八项属性，顺序同时用于只读视图和离线汇总。</summary>
public enum FastProperty { Type, Layer, Color, Linetype, ProjectPart, Class, Tag, TagFormat }
/// <summary>空值、未就绪、确认缺少属性、失败与多种值分别表达，不用占位文本参与比较。</summary>
public enum FastValueKind { Value, Missing, Error, Mixed, AbsentProperty }

/// <summary>Raw 保留真实 null、空字符串和空白字符作为比较键；Text 为独立显示名称。</summary>
public sealed record FastPropertyValue(FastValueKind Kind, string? Raw = "", string? Text = null, string? Detail = null)
{
    /// <summary>仅结构化的混合、空白和 null 值具有占位提示；真实同文字符串不是占位。</summary>
    public string? Placeholder => Kind switch
    {
        FastValueKind.Mixed => "*(多种)",
        FastValueKind.Value when Raw == null => "(null)",
        FastValueKind.Value when Raw == "" && string.IsNullOrEmpty(Text) => "(空白)",
        _ => null
    };

    /// <summary>将结构化状态转换为用户可读文本，不将异常伪装为空属性。</summary>
    public string Display => Placeholder ?? (Kind switch
    {
        FastValueKind.Value => Text ?? Raw ?? "",
        FastValueKind.AbsentProperty => "--",
        FastValueKind.Error => "读取失败",
        _ => ""
    });
    /// <summary>创建普通值，也可用独立显示文本表示类别本地化名称。</summary>
    public static FastPropertyValue Value(string? raw, string? text = null) => new(FastValueKind.Value, raw, text);
    /// <summary>表示属性未配置、不适用或当前环境不可用。</summary>
    public static FastPropertyValue Missing(string? detail = null) => new(FastValueKind.Missing, Detail: detail);
    /// <summary>仅在已定位的 Plant 对象确认不含此属性时使用；不表示空值、无选择或环境不可用。</summary>
    public static FastPropertyValue AbsentProperty(string? detail = null) => new(FastValueKind.AbsentProperty, Detail: detail);
    /// <summary>表示实际读取失败，保留错误详情。</summary>
    public static FastPropertyValue Error(string detail) => new(FastValueKind.Error, Detail: detail);
}

/// <summary>单对象快照只持有字符串和状态，不向 WPF 传递 SDK 对象。</summary>
public sealed record FastObjectProperties(string TypeKey, string TypeLabel, IReadOnlyList<FastPropertyValue> Values,
    string? Note = null);

/// <summary>完成一轮读取后的面板快照，不显示未完成批次的共同值。</summary>
public sealed record FastPanelSnapshot(string Header, int Count, IReadOnlyList<FastPropertyValue> Values, string Status);
