using System.ComponentModel;
using System.Globalization;
using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>非字符串字段按 SDK 列类型核对；不使用容差，不合并 null 和空串。</summary>
public static class PnPropertyValuePolicy
{
    /// <summary>转换不可靠时返回不相等并停止；Tag 与普通字符串保持逐字符比较。</summary>
    public static bool Equivalent<T>(PnPropertyWritePlan<T> plan, string? actual)
    {
        if (actual == plan.Value) return true;
        if (actual == null || plan.ValueType == null || plan.ValueType == typeof(string)) return false;
        try
        {
            var type = Nullable.GetUnderlyingType(plan.ValueType) ?? plan.ValueType;
            var converter = TypeDescriptor.GetConverter(type);
            var culture = CultureInfo.GetCultureInfo(plan.ValueCulture);
            object? requested = converter.ConvertFromString(null, culture, plan.Value);
            object? read = converter.ConvertFromString(null, culture, actual);
            return requested != null && read != null && requested.Equals(read);
        }
        catch (Exception) { return false; }
    }
}
