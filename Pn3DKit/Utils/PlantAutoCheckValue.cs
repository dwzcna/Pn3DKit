namespace Pn3DKit.Utils;

/// <summary>解释Plant命令的环境配置；读取故障和无效配置拒绝导出。</summary>
public static class PlantAutoCheckValue
{
    /// <summary>RTERROR缺省或RTNORM空配置沿用Plant默认启用；其余仅接受显式0或1。</summary>
    public static int Parse(int status, string raw) => (status, raw) switch
    {
        (-5001, "") or (5100, "") => 1,
        (5100, "0") => 0,
        (5100, "1") => 1,
        _ => throw new InvalidOperationException($"读取Plant自动检查配置失败：SDK状态={status}；原值={raw}")
    };
}
