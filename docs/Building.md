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
./build/Build-Pn3DKit.ps1 -BuildOutputRoot artifacts/plugin-build/0.1.8 -OutputDirectory artifacts/plugins/Pn3DKit/0.1.8

# 复用上述已校验的构建
./build/Publish.ps1 -NoBuild -InputDirectory artifacts/plugins/Pn3DKit/0.1.8 -OutputDirectory artifacts/release/0.1.8
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

正式资产为 DLL、Pn3DKit-版本-AutoCAD2026-x64.zip 和 SHA256SUMS。Release 说明从 Changlog.md 当前版本提取。本轮验证日志保存在 artifacts/validation/0.1.8/。

## 当前实现与验收

已完成：0.1.3 图纸身份修复已独立提交；0.1.4 新增 REBUILDPNTAGBYCLASS。BatchRunGate 覆盖导出、批量检查、重建及异步保存；TagRebuildPolicy 负责只读分类、注册键重号及占用链冲突；TagRebuildBatch 负责单撤销组和逐对象回读；TagRebuildJournal 保存追加历史。SDK 适配位于 PlantTagRebuildAccess，只写 PnId.Tag，支持 SLINE 和管线组；PlantTagCatalogAccess 批量读取 PnPRow.ClassName 并缓存类别、格式和成员关联。

已测试：离线策略、Fake Host 生命周期、默认否、冲突、数据行别名、日志、保存等待及异常收尾；插件通过双配置构建和八条命令元数据校验，实际结果见 Changlog.md 和 validation 日志。

未完成：候选版本均待用户 CAD 反馈后才推送和发布。实机需确认同图身份、自动编号、DataLinks 写入和重复错误、注释刷新、Esc、QSAVE，以及一次 UNDO 对图形及 Plant 缓存的完整回退。不得自动连接 CAD 验收用户图纸。

存在的问题：无法以离线测试确认 Plant 缓存撤销、组 Tag 持久化与注释刷新；缺少注册表时按本图关联行保守检查，索引不完整禁止写入。Piping 管线号继续跳过。

项目级入口位于 ProjectTagCommands；PlantProjectTagAccess 使用数据行重载与全项目占用索引，拒绝提交既有待提交修改；ProjectTagDataBatch 顺序提交和回读，失败即停止。PlantProjectDrawingHost／ProjectTagDrawingBatch 复用 AutoCadHost，在应用上下文开关文档，在命令上下文校验及写入，保留完整 BlockTableId 身份。ProjectTagJournal 逐条持久化项目范围日志。数据模式不使用 DWG 撤销，不做全局 Reject；图纸模式每图一个撤销组，未保存文档全部保留。

RibbonController 使用三个 RibbonRowPanel、RibbonRowBreak、原生纵向 RibbonSeparator 和原有大型导出按钮。七个位号命令通过队列执行，不发送取消前缀以保留 PICKFIRST；空闲通知同步批次与忙碌状态。RibbonImages 在 WPF 内存中绘制并冻结 16 DIP 矢量图标，区分单对象／批量／项目以及检查／计算／格式／重建，无外部图片依赖。实际行列排布、DPI 及工作空间恢复待 CAD 复测。

类别显示名称在 PlantTagCatalogAccess 中按项目部分及实际类别读取一次，使用类别表 GetTableAttributeValue(PnPDatabase.DisplayNameAttribute)，缓存到 PlantTagClassGroup.DisplayName。空值和普通异常回退 ClassName，OperationCanceledException 与 CAD UserBreak 继续中止批次。内部类名、排序、格式、数据身份和日志不变；同名显示类别不合并。
