# 开发与构建

插件及离线测试均以 .NET 8／Windows x64 为目标，解决方案为 Pn3DKit.slnx。使用 global.json 指定的构建 SDK、PowerShell 7.2+，以及 .NET 8 运行时／参考包。

## SDK

本机 SDK 路径保存在被忽略的 Pn3DKit/Pn3DKit.csproj.user，不设置系统环境变量：

```xml
<Project>
  <PropertyGroup Condition="'$(GITHUB_ACTIONS)' != 'true' and '$(ContinuousIntegrationBuild)' != 'true'">
    <AutodeskSdkRoot>E:\Autodesk SDK</AutodeskSdkRoot>
    <AutoCadSdkRoot>$(AutodeskSdkRoot)\CDROM1</AutoCadSdkRoot>
    <Plant3DSdkRoot>$(AutodeskSdkRoot)\AutoCAD Plant 3D 2026 SDK</Plant3DSdkRoot>
    <AutoCadManagedRoot>$(AutoCadSdkRoot)\inc</AutoCadManagedRoot>
    <PlantManagedRoot>$(Plant3DSdkRoot)\inc-x64</PlantManagedRoot>
    <PlantCommonManagedRoot>$(PlantManagedRoot)</PlantCommonManagedRoot>
  </PropertyGroup>
</Project>
```

本机只从 E:\Autodesk SDK 读取 CAD 参考，不回退 CAD 安装目录、其他 SDK 或历史输出。项目在引用解析前读取 user 属性，并在解析前后核对 CAD DLL 来源；缺失或越界时失败，不复制 Autodesk DLL 到交付包。

GitHub Actions 忽略本机 user 属性，下载并直接解压官方 SDK 到 artifacts/sdk。下载地址及 SHA-256 固定在 build/PlantSdk.json，并检查所有参考兼容 .NET 8。

## 测试、构建与打包

```powershell
# 不需要 CAD 或 Autodesk SDK 的两配置离线测试
./build/Test.ps1

# 本机参考来自 csproj.user；串行构建 x64 Debug、Release
./build/Build-Pn3DKit.ps1

# DLL 已被 CAD 加载时使用独立输出
./build/Build-Pn3DKit.ps1 -BuildOutputRoot artifacts/plugin-build/0.1.3 -OutputDirectory artifacts/plugins/Pn3DKit/0.1.3

# 复用上述已校验的构建
./build/Publish.ps1 -NoBuild -InputDirectory artifacts/plugins/Pn3DKit/0.1.3 -OutputDirectory artifacts/release/0.1.3
```

Pn3DKit.Tests 链接 Models、Services、Utils 中的同一份源码，不引用或加载 Autodesk。SDK 适配层通过插件编译、命令元数据检查及授权后的实际 CAD 验收验证。

离线验证覆盖导出策略、位号检查策略、目录和 CSV、取消及日志保留，不代表 SDK 在真实项目中的运行效果。实际 CAD 验收应使用明确授权的项目副本；本次不自动连接 CAD 或操作用户图纸。

本机模拟 CI 参考目录时：

```powershell
$sdk = ./build/Get-PlantSdk.ps1
./build/Build-Pn3DKit.ps1 -ContinuousIntegrationBuild -AutoCadManagedRoot $sdk.AutoCadManagedRoot -PlantManagedRoot $sdk.PlantManagedRoot -PlantCommonManagedRoot $sdk.PlantCommonManagedRoot
```

## 代码职责

- Commands：宿主命令入口、选择和提示；检查命令在当前文档主线程运行。
- Interop：AutoCAD／Plant SDK 适配、数据关联及格式查询；SDK 异常保留状态码。
- Models、Utils、Services：可离线验证的检查、导出策略和日志。
- UI：Ribbon 和由 C 显式打开的系统文件夹选择器。

位号检查不会赋号、保存或写回属性；自动编号沿用现有 Tag。SDK 拆分值必须能按原格式逐字符回拼，不能可靠对应时返回错误。非自动编号属性片段可单独求值以保留 SDK 格式规则，检查不对整式执行 Evaluate。

## 自动发布

唯一工作流为 .github/workflows/dotnet-desktop.yml：

1. main 和面向 main 的 PR 运行 Debug／Release 离线测试、插件构建、元数据及交付检查。
2. 复用通过固定哈希校验的 SDK 缓存，路径为 artifacts/sdk。
3. 推送 v版本号 标签时核对源码版本，全部通过后创建正式 GitHub Release。
4. 仅发布任务具有 contents: write。ZIP 只包含 Pn3DKit.dll、README.md、内部 SHA256SUMS，不包含 SDK、测试、符号或运行时。

正式资产为 DLL、Pn3DKit-版本-AutoCAD2026-x64.zip 和 SHA256SUMS。Release 说明从 Changlog.md 当前版本提取。本轮验证日志保存在 artifacts/validation/0.1.3/。

## 后续版本

0.1.3 仅修复批量检查的图纸身份误判及停止提示，CAD 复测通过后再推送发布。REBUILDPNTAGBYCLASS 按类别重建位号计划归入 0.1.4，不包含在本版中。
