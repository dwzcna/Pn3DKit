namespace Pn3DKit.Utils;

/// <summary>表示属性已确认存在、已确认缺失，或预检查无法确定其上下文。</summary>
public enum TagPropertyAvailability
{
    /// <summary>SDK 或明确的对象属性定义已确认字段存在，空值仍属于存在。</summary>
    Present,
    /// <summary>明确的目标记录缺少该字段。</summary>
    Missing,
    /// <summary>无法单独定位字段，须保留给完整 SDK 求值处理。</summary>
    Unresolved
}

/// <summary>区分目标自身与显式类别引用，不通过通用关联枚举推断字段缺失。</summary>
public static class TagPropertyScope
{
    /// <summary>自身属性直接检查，显式类别通过 SDK 上下文读取器解析。</summary>
    /// <param name="className">SDK 解析的类别名，空值或 TargetObject 表示所选部件自身。</param>
    /// <param name="propertyName">SDK 解析的内部属性名。</param>
    /// <param name="targetHasProperty">检查目标本身是否包含属性，不以属性值是否为空判断。</param>
    /// <param name="readScopedProperty">按类别解析安装管线、管线组或其他 SDK 上下文属性。</param>
    /// <returns>明确存在、明确缺失，或无法确定，后者不得直接输出缺失提示。</returns>
    public static TagPropertyAvailability Check(string? className, string propertyName,
        Func<string, bool> targetHasProperty,
        Func<string, string, TagPropertyAvailability> readScopedProperty)
    {
        string scope = (className ?? "").Trim().TrimStart('+', '?', '=');
        if (scope.Length == 0 || scope.Equals("TargetObject", StringComparison.OrdinalIgnoreCase))
            return targetHasProperty(propertyName)
                ? TagPropertyAvailability.Present : TagPropertyAvailability.Missing;
        return readScopedProperty(scope, propertyName);
    }
}
