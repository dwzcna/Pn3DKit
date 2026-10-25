# Pn3DKit 需求、实现与验证记录

## 0.1.9（2026-10-25，候选待 CAD 复测）

### 变更

- 类别清单主排序键从 ClassName 改为缓存的 DisplayName，使用 StringComparer.CurrentCultureIgnoreCase 按当前系统区域规则升序排列。显示名称比较相同时，依次按 ClassName、PartName 使用 Ordinal 排序，稳定区分同名类别。
- 当前图纸及项目级检查、重建的四个批量命令，以及无格式类别提示，统一沿用排序后的扫描清单。显示名称缺失时仍按回退的实际类名参与排序，内部身份、类别格式和日志结构保持。
- 产品／信息版本 0.1.9，程序集／文件版本 0.1.9.0；同步版本断言、用户 README、随包说明和开发文档。补丁检查应用、验证后本地提交，不推送、不创建正式 Release。

### 验证

- x64 Debug／Release 插件构建均零警告、零错误；两配置现有 232 项离线回归全部通过。两配置 0.1.9.0／AMD64／.NET 8／八条命令登记及单 DLL 交付检查通过，实际九个 CAD／Plant 参考均来自 E:\Autodesk SDK。ZIP 仅 DLL、使用说明及内部校验文件，内部和外部 SHA-256 复算、git diff --check 与忽略规则检查通过。
- CAD 的中英文混排、大小写、名称回退、同名类别稳定顺序及四个命令的编号与目标对应关系待复测。本轮不自动连接 CAD 或操作用户图纸；日志 artifacts/validation/0.1.9/。

## 0.1.8（2026-10-25，候选待 CAD 复测）

### 变更

- 类别列表及无格式跳过提示改用项目配置的显示名称，通过 SDK 官方示例的 GetTableAttributeValue(PnPDatabase.DisplayNameAttribute) 读取。每次扫描按项目部分＋实际类别读取并缓存一次，不在逐对象处理或访问显示属性时重复读取。
- 显示纯配置名称，不附加内部类名或硬编码管线组后缀；空名称或普通读取异常回退实际 ClassName，取消和 SDK UserBreak 继续传播。内部 ClassName、类别排序、数据行身份、格式与日志结构保持，同名显示类别不合并。
- 保留并纳入既有暂存的 Ribbon 文案及格式调整：检查元件位号(本页)、检查项目元件位号、重建元件位号、重建项目元件位号；用户说明同步。
- 产品／信息版本 0.1.8，程序集／文件版本 0.1.8.0；仍为 .NET 8／x64、八条命令和单 DLL 交付。验证后本地提交，不推送、不创建正式 Release。

### 验证

- x64 Debug／Release 插件构建均零警告、零错误；两配置现有 232 项离线回归全部通过。两配置 0.1.8.0／AMD64／.NET 8／八条命令登记检查通过，实际九个 CAD／Plant 参考均来自 E:\Autodesk SDK；交付目录仅 DLL、使用说明及内部校验文件，ZIP 载荷、内部和外部 SHA-256 复算通过。git diff --check 与忽略规则检查通过。
- 真实 CAD 的当前图纸／项目类别名称、缺失名称回退及同名类别独立性待复测；未自动连接 CAD 或操作用户图纸。本轮日志 artifacts/validation/0.1.8/。

## 0.1.7（2026-10-25，候选待 CAD 复测）

### 变更

- Pn3DKit Ribbon 面板改为“位号与导出”。三列七个位号按钮按约定三行布局，16 DIP 小图标配右侧中文文字，右侧原生纵向分隔符隔开现有大型“批量导出CAD”。
- 第一列为检查元件、按类型检查元件、按类型检查项目元件；第二列为评估元件、按类型重建元件、按类型重建项目元件；第三列为获取位号格式。Tooltip 显示用途和命令，CAD 队列执行且不发送取消前缀，保留 PICKFIRST。
- WPF 矢量区分检查、求值、格式、重建及批量／项目范围，无新增图片或依赖。空闲通知同步忙碌状态，延迟初始化、工作空间恢复及防重复逻辑保留。
- 产品 0.1.7，程序集 0.1.7.0，仍为八条命令；同步用户 README、随包说明和开发说明。验证并本地提交，待 CAD 反馈再推送和发布。

### 验证

- x64 Debug／Release 插件均零警告、零错误；两配置各 232 项离线回归通过。两配置 0.1.7.0／AMD64／.NET 8／八条命令及单 DLL 校验通过；SDK 参考仅来自 E:\Autodesk SDK，ZIP 载荷、内外 SHA-256 和 git diff --check 通过。
- 待 CAD：三列三行、七个小图标及右侧大图标、PICKFIRST、忙碌禁用、工作空间重载和不同 DPI。0.1.5～0.1.6 的数据行发现、SLINE／组、项目写入与撤销同样待实机反馈。日志 artifacts/validation/0.1.7/。

## 0.1.6（2026-10-25，候选待 CAD 复测）

### 变更

- 新增 CHECKPROJECTPNTAGBYCLASS、REBUILDPROJECTPNTAGBYCLASS，复用批量数据行目录，按项目部分＋数据行ID去重，包含未打开图纸和无独立图形的有效管线组。检查不打开图纸、不保存或提交。
- 重建先只读预检、输出完整图纸清单，提供可点击 Y／N／C。回车默认 N 并明确将提交项目数据；C／Esc 保留预检。只写 PnId.Tag，保留 SLINE／组规则，不扩展未选目标。
- N 拒绝相关项目库已有待提交修改，通过 SetProperties 行重载和 DataLinks AcceptChanges 提交并回读；失败或未知待提交状态停止，不全局 Reject。Y 统一选择保存策略、默认不保存，逐图重验、每图撤销组、共享行只写一次及逐图刷新；未保存图纸全部保持打开，不设置三张上限。安全结束才恢复原活动文档。
- 冲突覆盖注册表或完整项目表索引，包含未选类别及无格式对象；索引不完整禁止相关回退写入。新增项目即时日志和 BOM CSV，包含真实行ID、关联图纸及句柄，追加保留预检与最终动作。
- 产品 0.1.6，程序集 0.1.6.0，八条命令；同步用户说明、开发说明及版本断言。待 CAD 反馈再推送、发布，不自动操作图纸。

### 验证

- x64 Debug／Release 构建均零警告、零错误；两配置各 232 项离线回归通过，其中新增 27 项覆盖 Y／N／C、已有修改阻止提交、提交与回读失败停止、共享行、跨图纸、统一保存、全部保持打开、取消和日志故障。两配置均通过 0.1.6.0／.NET 8／AMD64／八条命令元数据检查；SDK 参考仍仅来自 E:\Autodesk SDK。单 DLL 交付、ZIP 载荷与内外 SHA-256、PowerShell 语法和 git diff --check 通过。
- CAD 项目数据提交／持久化、共享组、图形刷新、保存和撤销效果仍待实机；日志 artifacts/validation/0.1.6/。

## 0.1.5（2026-10-25，候选待 CAD 复测）

### 变更

- 批量扫描从当前图纸数据管理器 ID 出发，使用 SelectAcPpRowIds、GetClassesWithContent 和按类别批量读行，通过 PnPRow.ClassName 取得实际类别；模型空间只限定直接图形。缓存类别继承、有效格式和关联，不逐元件调用 GetObjectClassname 发现类别。
- 用户反馈两个批量命令无类别，核对指定 bin/x64/Debug DLL 实为 0.1.4.0。排查候选扫描时另发现 SDK 整数 dbHandle 被错误按十进制匹配 CAD 十六进制句柄，已修正并覆盖 30836、30855 等真实句柄；空清单显示程序集版本、图纸 ID、关联行及筛选计数，关联存在但批量读不到行时明确报错。
- 通过 PipeLineGroupRelationship 纳入当前工艺管线关联的 PipeLineGroup（管线组）；组没有独立图形也可检查／重建。无格式显示组数，缺失或失效关联记录异常，不自动创建、合并或修复组。
- 统一数据行与图形检查入口，保留组自身格式、SLINE 的管线组属性上下文及自动编号。取消 PipeLines 子类跳过；SLINE／管线组允许可靠非空期望 Tag 保留问号，其他元件规则保持。
- SLINE 写自身 Tag，组写自身 Tag，不改写源属性、编号或成员关系。预览列出组成员和共享图纸范围；组先于成员写入，后续快照变化则跳过并要求重新预检。仍保留 SDK 注册键、冲突、回读、单撤销组和默认不保存策略。
- 日志增加项目部分及真实数据行ID；组句柄保持为空，文本日志记录成员和关联图纸；追加历史以项目部分加行ID识别最终动作。产品 0.1.5，程序集／文件 0.1.5.0，保留六条命令。

### 验证

- 最终 x64 Debug／Release 插件构建通过，零警告、零错误；两配置各 205 项离线回归通过，其中新增 20 项覆盖实际类别、组关联、共享组、无图形行身份、问号许可、SWR／SWS 字段兜底、组优先写入及整数／十六进制句柄匹配。
- 两配置继续校验 .NET 8／AMD64／0.1.5.0／六条命令，参考仅来自 E:\Autodesk SDK。包结构、单 DLL、内外 SHA-256 通过；类别发现代码不调用 GetObjectClassname。
- 本轮日志 artifacts/validation/0.1.5/。尚未连接 CAD；组发现、共享范围、SLINE／组 Tag 写入与持久化、注释刷新和缓存撤销待实机反馈。验证后本地提交，不推送；随后立即实施 0.1.6 和 0.1.7。

## 0.1.4（2026-10-25，候选待 CAD 复测）

### 变更

- 按用户要求在 0.1.3 验证及本地提交后立即实现 REBUILDPNTAGBYCLASS。当前图纸按类别选择、只读预检、目录确认和 CSV 预览完成后，默认 N 请求重建确认；写入结束后默认 N 请求保存。
- 共用导出、按类别检查和重建的批次租约；异步 QSAVE 与日志清理完成后才释放。会话命令在目标文档命令上下文锁定图纸，复用完整 BlockTableId 身份核对、项目切换检查和容错输出。
- 第一版只写 PnId.Tag，要求图纸非只读并以读写方式加入项目。Piping、PipeLines 及子类、无格式／无 Tag／临时 Tag、期望值含问号、SDK 错误均跳过。保留共享检查逻辑及现有自动编号，不调用生成号码接口。
- 冲突通过 GetTagRegistryName、GetCandidateTag 和 FindRowByTag 查询内部注册键；缺失注册表时明确提示只覆盖本图模型空间，包含未选类别和无格式对象。未知查询错误不当作空闲。重号、占用、互换及占用链的所选参与者全部跳过，同一行多个图形只写一次并保留句柄。
- 确认后再次核对占用、Tag、属性、格式与可写状态。写入用单个 UNDO BEGIN/END 组，逐对象 SetProperties 后回读；普通失败继续，图纸／项目变化、未知状态或日志故障停止。Esc 保留已完成修改，剩余项记为未执行。收尾不明时不自动保存。
- 日志使用系统文档目录/Pn3DKit.TagRebuild/时间戳，FILEDIA 及 C 目录选择沿用检查流程。TagRebuild.log、带 BOM 的七列 TagRebuild.csv 均 CreateNew、逐条即时刷新；CSV 按用户选择保留预检及最终动作追加历史，同句柄最后一条为最终结果。
- 产品／信息版本 0.1.4，程序集／文件版本 0.1.4.0，元数据检查更新为六条命令；用户 README、随包说明与开发职责同步。本机继续只使用 E:\Autodesk SDK，SDK 不进入交付。
- 候选本机验证后本地提交；0.1.3、0.1.4 均等待 CAD 反馈再推送和正式发布，不自动操作真实图纸。

### 验证

- 最终 x64 Debug／Release 插件构建均通过，零警告、零错误；两配置各 185 项离线回归通过（新增 59 项重建用例）。覆盖范围和六种检查状态、自动编号保留复用、注册键冲突、占用链、数据行别名、确认后新增冲突、默认否、只读前置、属性变化、写入／回读异常、Esc、日志故障、单撤销组和异步保存租约。
- 两配置均通过 0.1.4.0／AMD64／.NET 8／六条命令元数据校验；实际解析的九个 CAD 参考均来自 E:\Autodesk SDK。交付目录和 ZIP 仅含 DLL、使用说明、内部 SHA256SUMS；内外 SHA-256 和 ZIP 逐项一致性校验通过，未携带 SDK、测试、符号或运行时。
- PowerShell 语法、git diff --check、忽略规则检查通过；沿用现有工作流，本轮无流水线改动。验证日志 artifacts/validation/0.1.4/；候选 DLL artifacts/plugins/Pn3DKit/0.1.4/Pn3DKit.dll，SHA-256 ad1af54c1fc86791178e2cc220814fd027cae66049b3ff22ddb71e39de480ff8。
- 待 CAD 实机：0.1.3 同图身份与切换／关闭；0.1.4 属性重建、自动编号保持、冲突、SLINE 跳过、写入及回读、注释刷新、Esc、QSAVE、一次 UNDO 对 Plant 数据缓存的效果。离线测试不能代替上述验收；候选提示必要时请用日志恢复。

## 0.1.3（2026-10-25，候选待 CAD 复测）

### 变更

- 用户要求先局部修复 CHECKPNTAGBYCLASS 图纸身份误判：开始时记录数据库完整 BlockTableId；扫描、目录确认后和逐对象检查前比较当前数据库的完整 ObjectId，不依赖文件名、句柄数值或托管 Document／Database 包装引用。
- 读取当前数据库身份前先检查原数据库是否释放及记录的 ObjectId 是否仍有效；分别提示无活动图纸、原数据库失效／关闭、活动图纸已变化和读取数据库状态异常，保留原始异常信息。
- 批量检查的进度、停止原因、汇总及最终日志路径改用容错输出；图纸关闭后使用当前可用编辑器，无法输出时降级跟踪，不再让失效 Editor 产生第二个异常。项目切换检查、Esc、目录选择及日志保留流程继续沿用。
- 产品／信息版本 0.1.3，程序集／文件版本 0.1.3.0；版本断言、README 和随包说明同步。REBUILDPNTAGBYCLASS 另定为 0.1.4，本轮不实现。
- 沿用既定候选安排：本机验证后本地提交并交付 DLL，CAD 复测反馈通过后再推送 main 和版本标签、发布正式 Release。

### 验证

- 最终 x64 Debug／Release 插件构建通过，零警告、零错误；两配置各 126 项现有离线回归通过。两配置程序集均通过 0.1.3.0／AMD64／.NET 8／五条命令检查，参考仍来自 E:\Autodesk SDK；候选 ZIP 的三件载荷、内部和外部 SHA-256 校验通过。
- 补丁检查、应用及 git diff --check 通过；本轮日志 artifacts/validation/0.1.3/。候选 DLL 位于 artifacts/plugins/Pn3DKit/0.1.3/Pn3DKit.dll。
- 待用户在 CAD 复测：同一图纸进入类别列表、选择目录和完成检查；实际切换图纸、关闭原图、关闭后重新打开同名文件均停止；两张未保存图纸保持不同身份；FILEDIA 目录选择与 Esc 行为正常。
- 托管包装是否实际变化及修复后的运行效果尚未实机确认，离线编译和回归不代替上述验收。

## 0.1.2（2026-10-25）

### 变更

- 新增 CHECKPNTAG：预选或单选 Plant 部件，按类别配置的格式检查现有 Tag，显示期望位号和属性差异；区分一致、不符、无格式、无 Tag、临时 Tag、错误六种状态。
- 新增 CHECKPNTAGBYCLASS：扫描当前图纸模型空间，按名称列出有格式的类别及数量；支持编号、范围和全选，未选择不执行，Esc 保留已完成结果。
- 检查共享 PnId／Piping 数据关联及继承格式查询；Piping 的空 Tag 回退 LineNumberTag。优先 SDK RebuildTagValue，逐字段回拼验证后保留自动编号；无法可靠拆分时报告错误与 SDK 状态，不猜测、不赋号、不保存。
- 日志目录采用文档/Pn3DKit.TagCheck/时间戳，并保留 FILEDIA 和 [选择..(C)] 操作。TagCheck.log 和带 BOM 的 TagCheck.csv 通过 CreateNew 创建，逐条刷新；记录六种状态汇总、停止原因及未处理数量。
- 产品版本为 0.1.2，程序集和文件版本为 0.1.2.0。保留此前 SDK 修复：本机仅使用 E:\Autodesk SDK，CI 使用 artifacts/sdk，本机路径保存在被忽略的 csproj.user。
- README 和随包说明改为用户功能、操作、示例和日志说明，构建配置移至开发文档。保留现有导出、Ribbon 及三个命令行为。
- 本轮按用户授权提交、普通推送 main 和 v0.1.2，由既有 GitHub Actions 发布正式 Release。

### 验证

- 本机 x64 Debug／Release 插件构建全部通过，零警告、零错误；两配置各 126 项离线测试通过，其中新增 43 项覆盖位号重建、类别选择、作用域、即时日志、CSV、中断和失效对象的类别归属。
- 两配置程序集均通过 0.1.2.0／AMD64／.NET 8／五条命令登记检查。实际解析的九个 CAD 参考均在 E:\Autodesk SDK 内；user 文件、SDK、产物和 AGENTS.md 均被忽略。
- 本地发布 ZIP 仅含 DLL、使用说明和内部 SHA256SUMS；包结构、内部清单、内外 DLL 一致性及外部 SHA-256 复算通过。PowerShell 语法、工作流 actionlint 和 git diff --check 通过。
- 先前中断的发布已收尾：main 运行 40174879729、v0.1.2 运行 40177161299 均成功；正式 Release https://github.com/dwzcna/Pn3DKit/releases/tag/v0.1.2。三件附件已回下载，GitHub 摘要、内外 SHA256SUMS、ZIP 载荷和 DLL 元数据均通过；普通下载的校验文件曾超时，改用官方资产 API 后完成。
- 本轮日志保存在 artifacts/validation/0.1.2/；真实 CAD 验收仍待执行，不将本机或云端构建结果代替实机结论。
- 真实 CAD 的位号重建、自动编号保持、Esc 响应、源数据未修改及导出图形完整性仍待实机验收；本轮不自动操作用户图纸。

## 0.1.1.2（2026-10-25，本地未提交）

### 变更

- 用户指定本机 SDK 统一使用 E:\Autodesk SDK；CAD SDK 为 CDROM1，Plant SDK 为 AutoCAD Plant 3D 2026 SDK，两者的必需托管参考已核对为 .NET 8。路径属性写入被忽略的 Pn3DKit.csproj.user，不设置系统环境变量，也不回退其他 CAD 安装／SDK 目录。
- 通过显式 SDK 导入顺序确保 user 属性先于引用声明生效；限定 CAD 引用搜索位置，并检查必需文件及最终解析出的 CAD 依赖都在所选 SDK 根目录内，缺失或越界立即报错。
- GitHub Actions 改用仓库 artifacts/sdk 解压及缓存，CI 跳过 user 本机属性，构建脚本移除旧 E 盘默认值并支持独立 CI 模式。
- 产品、信息、程序集和文件版本统一为 0.1.1.2；保留既有未提交修复。本轮不暂存、不提交、不推送。

### 验证

- 本机 E:\Autodesk SDK 与本地模拟 CI 的 artifacts/sdk 两种模式共四次 x64 Debug／Release 构建全部通过，零警告、零错误；两配置各 83 项离线测试通过，版本与单 DLL 交付检查通过。
- 实际 ResolveReferences 输出确认两种模式均解析 9 个 CAD 程序集：本机全部在 E:\Autodesk SDK 内，CI 模式全部在仓库 artifacts/sdk 内，未使用 CAD 安装目录或其他 SDK 位置。
- 引用缺失、目录越界、未配置 SDK 的反例均被拒绝；构建脚本在读取 CAD DLL 元数据前拒绝越界路径。本机 user 文件已确认被 Git 忽略；PowerShell 及工作流静态检查通过。
- 日志 artifacts/validation/0.1.1.2/。本轮未暂存、提交、推送或运行真实云端工作流、CAD 实机验收。

## 0.1.1.1（2026-10-25，本地未提交）

### 变更

- 用户报告默认本机构建出现 DataLinks／PnPCommonDbx 的 CS0234。已复现：项目管理 DLL 位于 AutoCAD 的 PLNT3D 子目录，而 PnPDataLinks、PnPDataObjects、PnPCommonDbxMgd 位于 AutoCAD 根目录；原版 SDK 将这些 DLL 放在同一个 inc-x64 目录，之前仅验证显式 SDK 路径未覆盖默认安装布局。
- 增加 PlantCommonManagedRoot，自动兼容安装版与 SDK 布局，保留显式覆盖；通用 DLL 引用与项目管理引用分开解析，仍不复制 Autodesk DLL。构建／打包脚本同步目录传递和参考校验，缺少通用 DLL 时提前报告具体文件与目录。
- 产品、信息、程序集和文件版本统一为用户指定的 0.1.1.1，同步文档及版本断言。本轮不暂存、不提交、不推送。

### 验证

- 修改前默认目录构建已复现三个 MSB3245 和两个 CS0234。修复后默认本机与显式原版 SDK 两种布局的 x64 Debug／Release 四次构建均通过，零警告、零错误；默认通用引用目录已确认为 AutoCAD 根目录。
- 两配置各 83 项离线测试通过；四份程序集的版本 0.1.1.1、AMD64／.NET 8、三条命令登记及单 DLL 交付校验通过。缺失通用目录的反例构建在程序集解析前明确提示 PnPDataLinks.dll 和目录，不再出现 CS0234／MSB3245；PowerShell 语法检查通过。
- 日志 artifacts/validation/0.1.1.1/；本轮未暂存、提交、推送或操作真实 CAD，真实 CAD 验收仍待复测。

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
