using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Pn3DKit.Interop;

/// <summary>访问 Plant 2026 自动检查命令实际使用的配置；只允许 CAD 主线程调用。</summary>
/// <remarks>PLANTDWGCHECKERAUTOCHECK 是命令，DWGCHECKERAUTOCHECKSWITCH 是其原生环境配置。</remarks>
internal static class PlantAutoCheckEnvironment
{
    internal const string Name = "DWGCHECKERAUTOCHECKSWITCH";

    [DllImport("accore.dll", EntryPoint = "acedGetEnv", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Get(string name, StringBuilder value, nuint capacity);

    [DllImport("accore.dll", EntryPoint = "acedSetEnv", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Set(string name, string value);

    /// <summary>按2026 SDK三参数缓冲接口读取；缺省为空按Plant命令默认启用处理。</summary>
    internal static object Read()
    {
        var value = new StringBuilder(4096);
        int status = Get(Name, value, (nuint)value.Capacity);
        return Pn3DKit.Utils.PlantAutoCheckValue.Parse(status, value.ToString());
    }

    /// <summary>保护作用域只写0或1；恢复后仍由作用域回读核对。</summary>
    internal static void Write(object value)
    {
        int number = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (number is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(value));
        int status = Set(Name, number.ToString(CultureInfo.InvariantCulture));
        if (status != 5100) throw new InvalidOperationException("写入Plant自动检查配置失败：SDK状态=" + status);
    }
}
