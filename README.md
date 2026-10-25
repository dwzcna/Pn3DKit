# Pn3DKit

当前版本 **0.1.1**，程序集及文件版本 **0.1.1.0**。

AutoCAD／Plant 3D 2026 的 Windows x64 / .NET 8 NETLOAD 插件，按当前 Plant 项目的 PnId／Piping 清单批量导出普通 AutoCAD DWG。

## 使用

1. 从 [GitHub Release](https://github.com/dwzcna/Pn3DKit/releases) 下载 DLL 或 ZIP，将插件放在 AutoCAD 可信目录，通过 NETLOAD 加载。
2. 在 Plant 3D 中打开项目和一张图纸，点击 Pn3DKit Ribbon“批量导出CAD”，或执行 `PNDWGEXPORT`。
3. 点击“P - PnId”／“D - Piping”或输入 P／D；默认匹配当前图纸类型，未匹配时默认 PnId。
4. 点击 Yes／No 或输入 Y／N，默认 Yes。有修改时仅执行一次 QSAVE；失败或仍有修改时不导出。No 跳过有修改图纸并继续，已保存的只读图纸可以导出。
5. 在版本选择前统一确认一次输出目录。FILEDIA=0 仅命令行输入，支持中文、空格、成对引号、回车默认及 Esc 取消。FILEDIA=1 显示 `输出导出目录<建议路径> 或 [选择..(C)]:`；回车默认，输入 C 或点击完整标签才打开系统文件夹选择器。插件不更改 FILEDIA。
6. 点击格式或输入年份／LT 关键字，支持 2007、2010、2013、2018，回车默认 2013（AC1027）。取消目录或版本选择均不开始批次。
7. 查看逐图统计和所选目录根部的 Export.log。

QSAVE 会改写源图，后续失败或取消不会撤销保存。请先备份，在明确授权的项目副本检查外参、专业对象转换及图形完整性。普通 AutoCAD 缺少 Plant 环境或当前项目时，命令提示原因并结束。

更新 DLL 后须重启 CAD 再 NETLOAD，同一会话重复加载不能替换已加载程序集。

命令 `GETPNTAGFORMAT` 读取单个部件的位号格式：可先选中部件再执行，也可执行命令后选择一个部件、回车确认。支持 PnId／Piping，显示实际类别、格式名称、定义类别和格式表达式；非 Plant 部件或没有格式时直接提示。

命令 `EVALUATEPNTAG` 支持相同的单选方式，随后输入位号格式（可带成对外层引号），调用 SDK Evaluate 输出位号。求值前检查目标属性；缺失时逐项显示原始格式片段，例如“所选目标不含属性 #(+TargetObject.Area)”。不执行位号赋值或保存。

TargetObject 字段检查所选部件自身；PipeLineGroup、PipeLines 等显式类别优先通过 SDK RetrieveProperty 按完整 Where.What 读取，覆盖管线组及阀门安装管线的上下文。单字段定位失败时再核对官方管线组关系；无法确认的字段继续完整 SDK 求值，不直接误报缺失。只有明确缺失才显示缺失提示；完整求值失败时显示 SDK 状态及未定位片段。加载和求值提示包含版本号。

## 输出与恢复

- 建议路径 `%USERPROFILE%\Documents\Pn3DKit.Export\yyMMddHHmmss`，同秒存在时建议编号。确认后直接使用所选目录，不再逐图询问，也不自动换目录。
- 保留项目相对层次，外部图纸按完整路径摘要隔离。不覆盖源图或已有 DWG；已有 Export.log／批次标记时需选择其他目录。
- 根部 Export.log 保存运行日志；.pn3dkit-logs/ 保存逐图 JSONL、最终 JSON、LISP 表达式和完成标记。
- LISP command-s 按“完整格式关键字 → 输出路径”导出，不输入 _F 或中间空回车。先写短名临时文件，确认命令结束、文件非空且 DWG 版本头匹配后发布。
- 原有文档保持打开；工具打开的图纸只在确认无修改且状态明确后关闭。取消、未知状态或新修改时保留现场，累计保留三张后停止。安全结束时恢复原活动图纸。
- CAD 路径须少于 260 字符，拒绝目录联接／符号链接。等待上限 600 秒，超时不强行中断原生命令。执行时不要手动切换项目、图纸或启动其他命令。

## 构建与测试

需要 PowerShell 7.2+、global.json 指定的构建 SDK，以及 .NET 8 运行时／参考包。插件和测试均以 .NET 8 为目标。

```powershell
# 离线策略测试无需 CAD 或 Autodesk SDK
pwsh -File build/Test.ps1

# 默认使用本机已安装 CAD 的参考程序集
pwsh -File build/Build-Pn3DKit.ps1

# 已加载 DLL 被占用时使用独立目录
pwsh -File build/Build-Pn3DKit.ps1 -BuildOutputRoot artifacts/plugin-build/0.1.1 -OutputDirectory artifacts/plugins/Pn3DKit/0.1.1

# 下载清单指定的原版 SDK，校验后解压，不运行安装程序；需要 7-Zip
$sdk = ./build/Get-PlantSdk.ps1
./build/Build-Pn3DKit.ps1 -AutoCadManagedRoot $sdk.AutoCadManagedRoot -PlantManagedRoot $sdk.PlantManagedRoot

# 打包默认交付目录，复用已通过的构建
./build/Publish.ps1 -NoBuild
```

解决方案为 Pn3DKit.slnx，仅支持 x64 Debug／Release。Pn3DKit.Tests 链接 Models／Services／Utils 同一份策略源码，用离线宿主验证路径、格式、保存、导出、取消及文档恢复，不加载 Autodesk 程序集。CAD 适配层通过插件构建、元数据检查和授权后的实际验收验证。

可通过 Plant3DSdkRoot、AutoCadManagedRoot、PlantManagedRoot 覆盖参考目录。构建会拒绝要求高于 .NET 8 的参考。Autodesk DLL 不复制到输出，也不增加独立桥接／策略 DLL。

使用原版 Plant 3D 2026 SDK，固定官方下载地址和 SHA-256；直接解压作为构建参考，无需运行安装程序。

## 自动发布

唯一工作流 .github/workflows/dotnet-desktop.yml 使用 Windows 2025 托管 runner：

- main 提交与 PR 执行两配置测试、构建、元数据检查及打包。
- SDK 下载地址和 SHA-256 固定在 build/PlantSdk.json，仅用于构建缓存，不进入源码或发布资产。
- 推送 v版本号 标签才创建正式 Release，标签必须匹配 Directory.Build.props；普通提交不发布。
- Release 提供 Pn3DKit.dll、Pn3DKit-版本-AutoCAD2026-x64.zip、SHA256SUMS。ZIP 内含 DLL、使用说明和内部校验，不含 SDK、测试、符号或运行时。

NETLOAD、Ribbon、完整标签点击、真实项目导出、QSAVE 和图形完整性仍须在明确授权的 CAD 项目副本中验收。离线测试和非空 DWG 不能代替实际验收。
