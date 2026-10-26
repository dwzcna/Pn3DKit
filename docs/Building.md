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
./build/Build-Pn3DKit.ps1 -BuildOutputRoot artifacts/plugin-build/0.1.31 -OutputDirectory artifacts/plugins/Pn3DKit/0.1.31

# 复用上述已校验的构建
./build/Publish.ps1 -NoBuild -InputDirectory artifacts/plugins/Pn3DKit/0.1.31 -OutputDirectory artifacts/release/0.1.31
```

Pn3DKit.Tests 链接 Models、Services、Utils、WPF 视图和外参来源检索代码，不引用或加载 Autodesk。来源检索通过最小 SDK 替身验证生命周期；实体及 BRep 适配仍需插件编译和授权后的真实 CAD 验收，不能用替身结果替代。

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
- UI：Ribbon、由 C 显式打开的系统文件夹选择器，以及 PN快速特性 PaletteSet／WPF 视图。

位号检查不主动 SetProperties 写回目标 Tag、显式 AcceptChanges 或 QSAVE；期望值的自动编号片段沿用现有 Tag。SDK 拆分值须可靠回拼；非自动编号片段仍可能带 ^，ReadFields 的 Evaluate 和 RebuildTagValue 不保证无副作用。官方开发指南 GUID-08089DFF-12E7-43F4-8BD4-24C4AF3D7A89 明确说明 caret 在求值后更新序列字段，不能以 IsValid 或片段类型推导只读。三个检查、EVALUATEPNTAG 和两个重建预检入口各输出一次 TagEvaluationNotice.Text；四类检查／重建日志在日志头记录，CSV 结构及日志保留默认规则保持。保留求值算法，不回滚序列字段或增加确认步骤。

PlantTagCheckAccess.ReadFields 对非自动编号的 N／NN／NNN 字段，将 RetrieveProperty 已读原值传给 FormatToolsUtil.FormatNumerals(formatCharacters, raw)，不再通过片段 Evaluate 获取数字段。TagCheckPolicy.FormatProperty 统一路由并验证结果；非空数字返回空值或格式化抛错时返回字段格式化失败，保留路径、原值、格式和可获得的 SDK 状态。该错误不由可选字段的 KeyNotFound 规则吞掉，不生成缺号期望值；共享检查与两个重建预检入口均沿用此结果。其他格式、配对字段与自动编号原有处理保持；不以本修复重新承诺所有 SDK 求值无副作用。

## 外参块检查实现

XREFBLOCKCHECK 共用 BatchRunGate、完整 BlockTableId 身份检查、目录选择及 RunLogFiles／日志保留。XrefBlockCadCatalog 使用宿主外参图、只读事务及受控旁路数据库；成功／失败读取均缓存，保留多链和同路径不同内存状态，工具不释放宿主数据库。已确认的源命名空间映射用于原名恢复，禁止截取最后一个竖线后内容。

0.1.31 将清单与内容分开：Discover／ReadNames 仅读块表，XrefBlockNameEntry 不含内容；XrefBlockBatch.Select 按不同来源 Key 及 OrdinalIgnoreCase 名称构造 XrefBlockSelection，唯一名称按来源汇总，匿名候选以 AnonymousSkipped 和 Uncompared 条目落报告。Compare 仅为命名候选调用 ReadDefinition，来源读取器、根快照及可达使用索引惰性缓存。命名父块的匿名和唯一名依赖沿用原快照算法；不伪造完整空快照。VerifySources 在名称读取及结束时核对所有有效来源，逐根读取也检查身份与磁盘状态；文件变化停止并标记先前记录的时间范围。读取器先于旁路数据库释放。LOG 记录名称扫描、筛选、内容读取及比较耗时和实际根读取数；CSV 不逐块输出唯一名，无候选时只有表头。

SDK 的托管 DxfFiler 没有直接可用的托管写出器构造；实现改用官方 samples/dotNet/FilerSample 所示 DwgFiler.CopyFiler 和 DBObject.DwgOut，在内存捕获类型化数据，不导出临时图纸或引入原生桥接 DLL。图层、线型、字体和标注样式读取公开语义属性，排除外参依赖／使用缓存标志。未知二进制和未解释类型均标记不完整。

新增宿主参考 AcDbMgdBrep.dll，Private=false，并进入 SDK 来源／.NET 8 校验；共十个 SDK 参考，Actions 原包已有该文件。BRep 读取精确拓扑及解析／NURBS 参数；SubDMesh 顶点索引转为图连接。动态原定义与可读参数、约束关联均纳入读取，原生 EvalGraph、约束子实体／专有参数、部分曲面与网格语义仍明确不完整，详见 XrefBlockCheck.md。

XrefBlockComparison 按类型、精确字段、重复边、资源完整字节及带标签图双射比较，哈希仅加速，显式栈回溯可取消；XrefBlockJournal 独占创建两件报告并逐条刷新。读取缺失、上下文变化或写盘失败不会被正常结束覆盖。十条命令、单 DLL；不得自动连接 CAD 验收。

## 自动发布

唯一工作流为 .github/workflows/dotnet-desktop.yml：

1. main 和面向 main 的 PR 运行 Debug／Release 离线测试、插件构建、元数据及交付检查。
2. 复用通过固定哈希校验的 SDK 缓存，路径为 artifacts/sdk。
3. 推送 v版本号 标签时核对源码版本，全部通过后创建正式 GitHub Release。
4. 仅发布任务具有 contents: write。ZIP 只包含 Pn3DKit.dll、README.md、内部 SHA256SUMS，不包含 SDK、测试、符号或运行时。

正式资产为 DLL、Pn3DKit-版本-AutoCAD2026-x64.zip 和 SHA256SUMS。Release 说明从 Changlog.md 当前版本提取。本轮验证日志保存在 artifacts/validation/0.1.31/。

## 当前实现与验收

已完成：0.1.3 图纸身份修复已独立提交；0.1.4 新增 REBUILDPNTAGBYCLASS。BatchRunGate 覆盖导出、批量检查、重建及异步保存；TagRebuildPolicy 负责预检分类、注册键重号及占用链冲突；TagRebuildBatch 负责单撤销组和逐对象回读；TagRebuildJournal 保存追加历史。SDK 适配位于 PlantTagRebuildAccess，只写 PnId.Tag，支持 SLINE 和管线组；PlantTagCatalogAccess 批量读取 PnPRow.ClassName 并缓存类别、格式和成员关联。

已测试：离线策略、Fake Host 生命周期、默认否、冲突、数据行别名、日志、保存等待及异常收尾；插件通过双配置构建和十条命令元数据校验，实际结果见 Changlog.md 和 validation 日志。

0.1.31 本轮 x64 Debug／Release 构建零警告、零错误，各 593 项离线回归全部通过，其中外参定向 85 项。真实目录源码的 SDK 替身在 1,500 个唯一名、仅匿名同名时根快照／读取器／使用索引均为 0；加入两组跨三来源命名候选后根读取为 6。纯策略 20 来源的 5,000 个唯一名不触发读取，三组命名候选只请求 60 个定义。阶段耗时与计数保存在 artifacts/validation/0.1.31/performance-evidence.json；版本、十命令、SDK 来源、单 DLL、ZIP 和 SHA-256 校验见 summary.json。真实 CAD 多外参提速及 Bind → Insert 对照待授权测试图纸复测，不以替身耗时替代。

0.1.29 已完成的 25 项数字格式化回归，覆盖 SDK 回调路由、已读原值、302／303／2→002、相邻 H、原自动编号、空返回、SDK 错误／取消及重建预检拒绝；两配置各 507 项通过，插件 Debug／Release 零警告、零错误。离线回调模拟不等同于 SDK 在 CAD 中的实际输出，仍需对照真实 LoopNumber 及相邻 Feature 的格式拆分。

未完成：CAD 实机验收仍待执行；本轮 0.1.31 完成本机验证与候选交付后本地提交，不推送、不创建 Release。实机需确认同图身份、自动编号、DataLinks 写入和重复错误、注释刷新、Esc、QSAVE，以及一次 UNDO 对图形及 Plant 缓存的完整回退。不得自动连接 CAD 验收用户图纸。

存在的问题：无法以离线测试确认 Plant 缓存撤销、组 Tag 持久化与注释刷新；缺少注册表时按本图关联行保守检查，索引不完整禁止写入。Piping 管线号继续跳过。

项目级入口位于 ProjectTagCommands；PlantProjectTagAccess 使用数据行重载与全项目占用索引，拒绝提交既有待提交修改；ProjectTagDataBatch 顺序提交和回读，失败即停止。PlantProjectDrawingHost／ProjectTagDrawingBatch 复用 AutoCadHost，在应用上下文开关文档，在命令上下文校验及写入，保留完整 BlockTableId 身份。ProjectTagJournal 逐条持久化项目范围日志。数据模式不使用 DWG 撤销，不做全局 Reject；图纸模式每图一个撤销组，未保存文档全部保留。 PlantProjectDrawingHost 的 InDrawingAsync 在每次重建及 HasPendingDataAsync 回调内，通过 ScopedContextGuard 独立绑定当前数据库及完整 BlockTableId；作用域结束恢复原 Guard，覆盖只读跳过和前一图已关闭的路径，不保留跨图纸永久赋值。

RibbonController 在“Pn3D工具箱”中按顺序创建快速操作、位号检查、图纸操作三个原生 RibbonPanel，各有固定 Source.Id。Rows 用 RibbonRowPanel／RibbonRowBreak 组织三行：单个检查、本图检查／重建、项目检查／重建；首行不创建占位按钮，不增加手工 RibbonSeparator。所有小按钮 Width／MinWidth 为 128 DIP，统一由 Register 登记，保留既有按钮／选项卡 ID。七个位号命令使用 16 DIP 矢量，特性与导出为大图标，队列及忙碌逻辑保持。合成预览只证明命令分组和示意布局，原生 Ribbon 收缩、实际 DPI 及工作空间恢复待 CAD 复测。

类别显示名称在 PlantTagCatalogAccess 中按项目部分及实际类别读取一次，使用类别表 GetTableAttributeValue(PnPDatabase.DisplayNameAttribute)，缓存到 PlantTagClassGroup.DisplayName。空值和普通异常回退 ClassName，OperationCanceledException 与 CAD UserBreak 继续中止批次。类别返回顺序使用 DisplayName／StringComparer.CurrentCultureIgnoreCase，再依次以 ClassName、PartName／StringComparer.Ordinal 稳定排序。四个批量检查、重建命令与无格式提示沿用同一清单。内部类名、格式、数据身份和日志不变；同名显示类别不合并。

RunPaths 统一以系统文档目录生成 Pn3DKit/用途/时间戳。RunLogFiles 在 CreateNew 成功后登记文件归属，记录本批新建末级目录和原生独占标记；所有流关闭并封存后才可按清单删除，不递归清理。LogRetentionPolicy 根据结构化最终结果判断默认值；LogRetentionPrompt 在命令末尾处理 Y／N／Esc，Session 入口通过当前文档命令上下文交互，批次租约覆盖清理。AutoCadHost 在原生回调状态未知或等待器清理失败时保护本批现场；普通错误仍可在安全交互时明确选择 N。该流程不接触离线验证产物。

PNFASTPANEL 保留 PICKFIRST，由 FastPanelController 管理原生 PaletteSet。FastPropertiesView 使用连续的 Pn属性／值两列，默认一空行；属性行可增删改，路径列始终可编辑。输入立即撤销旧行许可，回车或失焦才读取；值按一次性票据在回车或失焦时提交，Esc 还原当前输入。UI 不持有 SDK 对象，模型不引用 Autodesk。

文本输入／复制期间由 WPF 焦点事件驱动 PaletteSet.KeepFocus，离开表格或隐藏时释放，属性提交期间保持原焦点。FastSelectionController 在读锁外结合批次、CommandInProgress、IsQuiescent 和表格输入焦点固定忙碌状态，再传入属性适配；事务内不重复查询 IsQuiescent。FastSelectionController 的选择、文档、对象及命令事件只使轮次失效，主线程 Idle 分批读取完整选择。PnPropertyPolicy 归一化自身路径、汇总共同值并保守判断权限，PnPropertyBatch 丢弃旧轮次。完整 BlockTableId、项目身份、行修订和票据共同隔离跨图纸、跨选择及跨属性编辑。多选权限取所有对象的许可交集，任一缺少／只读／未知则整行只读；隐藏或忙碌暂停，关闭及退出解绑。

FastPlantPropertiesAccess 直接读取 TargetObject 对应的数据行列，不展开关系或回退改写其他列。权限与提交前的类型、长度校验统一通过 FindEffectiveColumn 解析 table.AllColumns，包含继承字段；列约束仍从实际类别的 GetColumnAttributeNames／GetColumnAttribute 读取，保留类别覆盖。不得改回仅含本类定义的 Columns，否则继承的 Comment、Area 会被误判缺少元数据。权限包括 PnPColumn 的只读／系统／计数／自动生成／表达式／扩展／唯一性、明确列约束、IsAcquisitionProperty／GetAcquisitionStatus、Project.ReadOnly、图纸只读、读写登记、图层锁定和批次状态。PnPropertyMetadataPolicy 是实际 SDK 适配调用的纯策略：描述性元数据不判只读、不额外查询其值；必要约束无法解析或读取失败时只读。非获取字段不查询获取状态，普通字段不读取或拆分 Tag 格式；Tag 单独校验有效格式和注册表。Tag 限有有效格式和注册表的非临时 PnId 普通元件；管线、组、Piping 和缺少注册表时只读。

每次值提交通过共用 BatchRunGate 持有互斥，通过 AutoCadHost.InLockedDocumentAsync 在主线程应用上下文锁定当前文档，保留 PICKFIRST 与焦点，不启动 CAD 命令。多目标票据记录完整 ObjectId 集合、逐对象数据关联及原值。PnPropertyEditPolicy 忽略选择顺序但拒绝增减、过期、原值或权限变化；PnPropertyEditBatch 先对全部目标 Prepare，再检查项目／批内注册键冲突，预检通过后逐数据行写入并回读，别名只写一次。实际错误立即停止，结果保留逐项句柄、原值、新值、实际值和 SDK 状态；不假定回滚。每步完整 Capture 区分写入带来的刷新与非预期目标变化，结束后统一 Regen，刷新失败单独报告。不保存、不生成编号、不改变获取模式、不全局提交项目数据；DWG／Plant 缓存撤销待实机验证。

FastPropertiesView 沿用蓝灰／灰白动态画刷、原生停靠和高 DPI。值列使用独立样式，正常背景／文字置于 Setter，IsReadOnly 触发器切换专用灰色动态资源，避免局部值遮蔽；保持 IsEnabled=true，保留焦点、选择复制和 Tooltip。无有效票据等所有实际只读状态均灰显，恢复许可及深浅主题切换自动更新。表格最小行高 24 DIP，上下内边距 3 DIP，字号仍为 12，自动高度避免裁切。纯 WPF 离屏宿主使用合成数据，不加载 Autodesk；十条命令、单 DLL。面板保留末尾自动空行和右键移除；菜单借用焦点不提交，移除前撤销草稿与票据。菜单 MenuOpen 仅表示实际打开；ContextMenuOpening 的临时状态在 Dispatcher 确认，取消开启及关闭／隐藏／卸载均清理。混合值输入缓冲和提示分离，只有实际输入或明确删除才产生脏态；保留既有身份校验。

属性检索由 FastSelectionController 在主线程 Idle 读取，不新增 CAD 命令：固定完整 BlockTableId、对象、项目关联及轮次；SDK 读锁／事务在 CAD ShowModalWindow 前释放，确认后再次核对选择和关联。FastPlantPropertiesAccess.Catalog 使用 GetAllProperties(objectId, true) 的自身属性键，调用 context.Part.GetColumnDisplayName(table, name)，通过所属项目字符串表及实际类别配对名称，再由 SDK 回退列 DisplayName；不再以 GetColumnAttributeNames 是否声明 DisplayName 为门槛；不展开关系、不调用格式求值／写入／提交。PropertyCatalogView 只接收文本 DTO，勾选通过确认后才调用面板规范化追加流程；元数据失败保留属性和详情。属性路径始终保留内部名，显示名只用于第二列及提示；只读属性仍可加入供浏览。无效路径保留在目录但禁用添加并说明原因。

FastPanelControlStyles 在每个视图 UI 线程创建局部 WPF 模板，统一按钮、行右键菜单及分隔线。Popup 显式合并视图资源，深浅主题即时同步；菜单不再采用系统白色图标槽，禁用文字、悬停及分隔线分别使用面板动态画刷。按钮模板覆盖 WPF 默认白色禁用背景，保留原生 IsEnabled、Command、CommandTarget 和焦点／移除规则，不修改 CAD 全局资源。检索窗口第二列标题为“显示名称”，内部 FriendlyDisplayName 数据字段保持。

行移除使用每视图专用 RoutedUICommand，命令绑定和 Ctrl+D 输入绑定只安装在本表的路径／值格。菜单 CommandTarget 明确指向所属格，显示 Ctrl+D；PreviewKeyDown 在移除导致焦点变化前消费按键。占位空行及已移除行拒绝执行，不注册到 CAD 全局，不修改其他按键含义，不触发值提交。

值显示通过 FastValueKind 区分已有值、未就绪和确认不存在属性：SDK HasProperty 返回 false 才产生 AbsentProperty 并显示 ASCII --；已有空值和未就绪显示空白，错误仍显示读取失败。Raw 保持真实内容用于共同值比较和提交，显示标记不参与写入。多选部分缺少属性保留原因；混选未知状态不推断为确认缺少。

Pn3DKit.Tests 启用纯 WPF 并链接 FastPropertiesView，以独立 STA 路由事件验证 Comment 许可、空值、焦点保留请求、Enter／失焦／Esc、默认空行及旧快照失效。测试不打开窗口、不设置系统焦点、不加载 Autodesk；WPF 离屏预览不能代替原生 PaletteSet 和真实 SDK 验收。
