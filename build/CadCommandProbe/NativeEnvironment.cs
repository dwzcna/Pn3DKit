using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Pn3DKit.CadCommandProbe;

/// <summary>按2026 SDK的三参数acedGetEnv访问Plant命令实际使用的配置，仅供受控原型。</summary>
internal static class NativeEnvironment
{
    internal const string Name = "DWGCHECKERAUTOCHECKSWITCH";

    [DllImport("accore.dll", EntryPoint = "acedGetEnv", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Get(string name, StringBuilder value, nuint capacity);

    [DllImport("accore.dll", EntryPoint = "acedSetEnv", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Set(string name, string value);

    /// <summary>返回原生状态及完整原值，供日志区分未配置和显式开关。</summary>
    internal static (int Status, string Raw) ReadRaw()
    {
        var value = new StringBuilder(4096);
        int status = Get(Name, value, (nuint)value.Capacity);
        return (status, value.ToString());
    }

    /// <summary>Plant原生命令对缺省或空配置采用启用；其他无效值拒绝继续。</summary>
    internal static int Read()
    {
        var value = ReadRaw();
        if (value.Status == -5001 || value.Status == 5100 && value.Raw.Length == 0) return 1;
        if (value.Status != 5100 || !int.TryParse(value.Raw, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number is not (0 or 1)) throw new InvalidOperationException("读取Plant自动检查配置失败：" + value);
        return number;
    }

    /// <summary>只接受0或1；调用方在原生操作安全结束后恢复原始有效值。</summary>
    internal static void Write(int value)
    {
        if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(value));
        int status = Set(Name, value.ToString(CultureInfo.InvariantCulture));
        if (status != 5100) throw new InvalidOperationException("写入Plant自动检查配置失败：SDK状态=" + status);
    }
}
