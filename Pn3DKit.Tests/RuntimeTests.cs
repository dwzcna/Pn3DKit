using System.Reflection;
using System.Runtime.Versioning;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>确认集中版本与 .NET 8 目标传播到链接插件源码的测试程序集。</summary>
public sealed class RuntimeTests
{
    /// <summary>版本采用三段产品号、四段程序集号，测试目标与 CAD 宿主一致。</summary>
    [Fact]
    public void AssemblyUsesCurrentPluginVersionAndNet8()
    {
        var assembly = typeof(ExportPolicy).Assembly;
        Assert.Equal(new Version(0, 1, 0, 0), assembly.GetName().Version);
        Assert.Equal("0.1.0", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        Assert.Equal(".NETCoreApp,Version=v8.0", assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
    }
}
