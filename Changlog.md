# Pn3DKit 需求、实现与验证记录

## 0.1.1（2026-10-25）

新增 EVALUATEPNTAG、GETPNTAGFORMAT。

### 变更

- 新增 GETPNTAGFORMAT：支持预选单个部件，或运行命令后选择并回车；读取 PnId／Piping 部件实际类别及继承的位号格式，显示格式名称、定义类别和表达式，非 Plant 部件或无格式时提示。
- 新增 EVALUATEPNTAG：支持相同单选方式及带成对外层引号的格式输入，通过 FormatStringUtils.Evaluate 输出位号；明确缺失的属性逐项显示原始片段，不执行位号赋值或 QSAVE。
- 显式类别字段通过 FormatToolsUtil.RetrieveProperty 按 Where.What 读取，分别处理 SLINE 的 PipeLineGroup 字段、阀门安装管线 PipeLines.Size 与阀门自身 TargetObject.Size；单字段未定位时继续完整求值，失败时保留 SDK 状态和格式片段。SDK 单字段失败后的管线组检查使用官方具名关系。
- 产品与信息版本升为 0.1.1，程序集与文件版本为 0.1.1.0；同步说明、版本断言、三条命令登记校验和 SDK 参考校验。加载及求值提示显示程序集版本。
- 本轮汇总此前本地追加代码并提交，保留 PNDWGEXPORT 等现有功能；仅本地提交，不推送、不创建发布标签或远端 Release。

### 验证

- 原版 2026 SDK 的 x64 Debug／Release 构建均通过，零警告、零错误；两配置各 83 项离线测试全部通过，版本断言为产品 0.1.1、程序集 0.1.1.0。
- 两配置均通过 .NET 8／AMD64／三条命令登记校验；本地 ZIP 载荷、内部清单、DLL 一致性及外部 SHA-256 复算通过，Autodesk DLL 未进入交付包。PowerShell 与工作流静态检查通过。
- 日志 artifacts/validation/0.1.1/。本轮未连接真实 CAD；SLINE／阀门的实际 SDK 求值待授权环境复测，离线用例不能替代真实 CAD 验收。

### 实现阶段记录

- 0.1.0～0.1.0.1 的本地追加阶段曾分别通过构建、82／83 项策略测试；用户复测确认通用关联枚举仍会将管线组及安装管线字段误报缺失，因此最终改为 SDK 上下文读取。历史日志保留于 artifacts/validation/0.1.0/ 与 artifacts/validation/0.1.0.1/，不作为本轮实机验收结论。

## 0.1.0（2026-10-25）

### 变更

- 独立为 AutoCAD／Plant 3D 2026 x64 / .NET 8 插件仓库；解决方案 Pn3DKit.slnx 只包含插件与 Pn3DKit.Tests。测试与 CAD 宿主同为 .NET 8，继续链接插件策略源码，不需要 Autodesk SDK。
- 保留 PNDWGEXPORT、Ribbon“批量导出CAD”、PnId／Piping 识别、条件 QSAVE、四种格式、FILEDIA 与 [选择..(C)]、精确目录、Export.log、临时 DWG 保护及文档恢复行为。
- 清理无关源码、测试、脚本和说明；保留本地 AGENTS.md 并忽略，不进入仓库。产品版本 0.1.0，程序集及文件版本 0.1.0.0。
- 使用远端已有 .github/workflows/dotnet-desktop.yml。main／PR 验证两配置测试和插件构建；版本标签校验后自动创建正式 GitHub Release。
- SDK 固定来源与 SHA-256，直接解压并校验托管参考，仅用于构建缓存。Release 提供单 DLL、含说明及内部校验的 ZIP、外部 SHA256SUMS，不包含 SDK／测试／符号／运行时。
- 源图保存、真实图形转换和 CAD 内交互仍须在明确授权的项目副本验收。

### 验证

- 本机 .NET 8 离线测试 Debug／Release 各 71 项全部通过。初次还原遇到网络／TLS 失败，补齐官方 NuGet 包缓存后通过，未改变仓库包源。
- 使用 SHA-256 已固定的原版 2026 SDK 直接解压，六个参考均兼容 .NET 8；插件 Debug／Release 构建零警告、零错误。解决方案两配置校验通过。
- 两配置程序集均为 0.1.0.0／AMD64／.NET 8，入口属性及唯一公开命令 PNDWGEXPORT 校验通过。交付仅含 DLL、说明及内部校验；ZIP 逐项与外部 SHA-256 复算通过。
- 用户确认沿用原版 Plant 3D 2026 SDK，删除其他运行时的相关描述；脚本已验证会拒绝不兼容参考和不匹配哈希。
- 工作流 actionlint 检查通过；main 运行 40156752798、v0.1.0 运行 40156979041 均成功，云端两配置测试、原版 SDK 下载／缓存／校验、插件构建、元数据及打包均通过。
- 正式 Release：https://github.com/dwzcna/Pn3DKit/releases/tag/v0.1.0。已回下载 DLL、ZIP、SHA256SUMS，核对 GitHub 摘要、外部清单、ZIP 根目录、内部清单及内外 DLL 一致性；三件附件均通过，未包含 Autodesk DLL、测试、符号或运行时。
- 发布 DLL 为 0.1.0.0／AMD64／.NET 8，仅注册 PNDWGEXPORT，SHA-256 为 31f18a198c77fa0209e842d45292ccdbaa0d51f2473f0abfd07e657d673eda15。真实 CAD 验收待授权；本轮日志 artifacts/validation/0.1.0/。
- 原有远端 main 历史作为提交基线；本轮已授权普通推送 main 和 v0.1.0，禁止强制推送。

## 0.0.x 插件阶段归档

- 实现 NETLOAD、延迟访问 Plant API、Ribbon 延迟创建与工作空间恢复；最终公开命令 PNDWGEXPORT，目标 .NET 8 / x64，Autodesk 程序集由宿主提供。
- 按 ProjectParts／ResolvedFilePath 获取、去重及识别图纸，支持 P／D 默认和完整点击标签；Yes／No 与 Y／N 输入共存，有修改时仅保存一次，No 跳过该图继续。
- 根据真实 Plant 2026 提示修订为完整年份关键字直接接输出文件；LISP command-s 配合完成标记、LISP 结束与空闲事件判定完成，验证 DWG 文件头后发布。
- 增加精确目录、项目层次、外部图纸摘要隔离、根部 Export.log、增量结果、短名临时输出、源图保护、文档所有权、取消保留、系统变量／原活动图恢复及三张保留上限。
- FILEDIA=0 保持纯命令行；FILEDIA=1 先提示建议目录与 [选择..(C)]，只有 C 才打开系统文件夹选择器，回车默认、取消结束。移除自定义窗口，Ribbon 使用上图标下文字。
- 插件曾通过双配置构建、策略测试及交付校验，历史结果不作为 0.1.0 验证结论。真实 NETLOAD、标签点击、两类项目、源图格式和图形完整性仍待授权验收。
