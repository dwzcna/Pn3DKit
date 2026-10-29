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
./build/Build-Pn3DKit.ps1 -BuildOutputRoot artifacts/plugin-build/0.1.47 -OutputDirectory artifacts/plugins/Pn3DKit/0.1.47

# 复用上述已校验的构建
./build/Publish.ps1 -NoBuild -InputDirectory artifacts/plugins/Pn3DKit/0.1.47 -OutputDirectory artifacts/release/0.1.47
```

Pn3DKit.Tests 链接 Models、Services、Utils、WPF 视图和外参来源代码，不加载 Autodesk；SDK 替身仅验证来源与生命周期。build/CadVisualPrototype、CadVisualHost、CadVisualProbe 是独立原生验证项目，不加入产品解决方案或单 DLL 载荷；使用授权目录及实例，不调用 Computer Use、不更改 CAD 安全设置。

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

## 目录提示与外参进度

六个目录入口共用 CadOutputDirectoryPrompt，主线程读取一次 FILEDIA，统一生成关键字／路径提示、窗口标题和容错输出，各用途传入自己的验证器及上下文检查。OutputDirectorySelection 在输入、浏览及验证前后调用 ensureContext，且检查位于路径异常重试之外；数据库／项目失效不作为目录错误重试，不修改 FILEDIA。

XrefBlockProgress 不依赖 Autodesk，按主线程检查点和 TimeProvider 每三秒输出当前阶段，不建立后台 CAD 回调。比较结果先调用既有写入并刷新，再输出符合／不符合／未验证；报告异常不打印完成。Paths 固定先提醒再展示路径，提醒不进入心跳。CSV ResultText 保持匹配／不匹配／未验证，未增加公开命令。

## 外参块名称统计与候选比较实现

XrefBlockCadCatalog 先用 IndexReferences 发现附着／覆盖来源并冻结 SourceState 清单，再逐图 IndexNames。关系和名称阶段各枚举一次块表，来源数据库和引用链缓存复用，不重复装载源文件；ReferenceStamp 保存完整对象身份、路径、加载身份和覆盖状态，两阶段之间变化则停止。名称阶段才创建 GUID 清单并分配 dwgNo，表头先于该阶段块表读取，50 行／1 秒写出保持。sourceFound 回调报告已确认来源数，scanStarted 回调在表头刷新后报告序号／固定总数；原 progress 回调仍是累计名称行数。CaptureNames 包含已发现但 DwgNo=0 的未扫描来源，LOG 不将其视为空图纸。

候选通过 LocateVisual／VerifyVisual 核对来源、目标名称、完整数据库身份和磁盘状态，XrefBlockVisualPreparation 为每个候选来源建立隔离临时库。只读来源保留到比较结束后释放，宿主数据库不释放；产品不再包含旧字段读取器，无候选零绘制。

专用 XrefBlockVisualSession 在 CAD 主线程串行准备及绘制，公共 WCS 俯视正交取景、2048×2048 双背景；解析有效颜色／透明度／线宽，多线宽使用同组编码色表。原生后端不能确认的非连续线型、遮罩、重叠异色排序及复杂类型明确未验证。AcDbMgdBrep 不恢复，仍为九个不复制 SDK 参考。

VisualFrame／VisualSample／BlockVisualComparison／BlockVisualDiff 不依赖 Autodesk。CompressedVisualSample 无损压缩代表，摘要筛选后仍解压完整比较；XrefBlockVisualBatch 串行归组，已确认差异优先于未知来源。旧字段快照、DwgFiler及比较批次已移除，候选筛选、报告、取消和名称统计回归继续保留。

XrefBlockVisualBatch 每个候选报告刷新成功才登记完成；取消、身份变化、设备或释放故障直接停止。Remaining／CaptureNames／结束报告只处理内存。中立 XrefBlockReportResult 驱动三列 Result.csv 和详情 LOG，不用空快照冒充视觉结果。VisualDifferenceReport 仅为不同组与首组代表创建带图注的无损 PNG。

五件固定输出和名称清单由 RunLogFiles.CreateNew 登记，PNG 使用受限 CreateImageReport；所有流关闭后统一保留／清理。逐名称 50 行／1 秒、逐候选完成刷新保持，任一输出故障立即停止。新建图形对象、临时数据库和 Bitmap 及时释放，WorkingDatabase 同步恢复。

## 自动发布

唯一工作流为 .github/workflows/dotnet-desktop.yml：

1. main 和面向 main 的 PR 运行 Debug／Release 离线测试、插件构建、元数据及交付检查。
2. 复用通过固定哈希校验的 SDK 缓存，路径为 artifacts/sdk。
3. 推送 v版本号 标签时核对源码版本，全部通过后创建正式 GitHub Release。
4. 仅发布任务具有 contents: write。ZIP 只包含 Pn3DKit.dll、README.md、内部 SHA256SUMS，不包含 SDK、测试、符号或运行时。

正式资产为 DLL、Pn3DKit-版本-AutoCAD2026-x64.zip 和 SHA256SUMS。Release 说明从 Changlog.md 当前版本提取。本轮验证日志保存在 artifacts/validation/0.1.47/。

## 当前实现与验收

已完成：项目写后增量更新索引、写前SDK查询、范围管线逆查组、批次纯格式缓存；多选局部复核和类型化回读；旧字段比较删除、WPF目录选择器、视觉内存和空间筛选优化。0.1.47候选已替换原生命令、修正自动检查配置和Unicode筛选。

已测试：两配置各963项离线回归；真实视觉33项／49次绘制；最终生产过滤函数真实SDK12项；原生命令原型11项含实际Esc；最终生产命令9项及独立COM空闲核验；WPF目录CAD所有者及取消；真实Plant多选写入、数值01→1、严格文本、4个对象事件、选择保持及管线组逆查。OPEN边界修正后应用及文档均空闲，不再需要用户Esc；产品不自动发送取消。路径132成功／133失败且旧LISP同样受限，已增加打开／保存前预检，撤回前导点归因。证据见Changlog.md和artifacts/validation/0.1.47/。

已完成：0.1.3 图纸身份修复已独立提交；0.1.4 新增 REBUILDPNTAGBYCLASS。BatchRunGate 覆盖导出、批量检查、重建及异步保存；TagRebuildPolicy 负责预检分类、注册键重号及占用链冲突；TagRebuildBatch 负责单撤销组和逐对象回读；TagRebuildJournal 保存追加历史。SDK 适配位于 PlantTagRebuildAccess，只写 PnId.Tag，支持 SLINE 和管线组；PlantTagCatalogAccess 批量读取 PnPRow.ClassName 并缓存类别、格式和成员关联。

已测试：离线策略、Fake Host 生命周期、默认否、冲突、数据行别名、日志、保存等待及异常收尾；插件通过双配置构建和十条命令元数据校验，实际结果见 Changlog.md 和 validation 日志。

历史验证及已替代实现见Changlog.md对应版本，不作为本轮通过证据。


未完成：PNFASTPANEL原生焦点、首次浮动、菜单与输入法、Plant缓存撤销及用户项目注释等具体场景复测。上述专用副本验收不扩大产品支持边界。所有实际操作只针对核对PID的Plant 3D工具项目副本，另开的普通AutoCAD未操作；不保存用户原图、不使用Computer Use。源码及必要真实验收通过后提交0.1.47，并按授权统一推送、发布v0.1.47。

存在的问题：无法以离线测试确认 Plant 缓存撤销、组 Tag 持久化与注释刷新；缺少注册表时按本图关联行保守检查，索引不完整禁止写入。Piping 管线号继续跳过。

项目级入口位于 ProjectTagCommands；PlantProjectTagAccess 使用数据行重载与全项目占用索引，拒绝提交既有待提交修改；ProjectTagDataBatch 顺序提交和回读，失败即停止。PlantProjectDrawingHost／ProjectTagDrawingBatch 复用 AutoCadHost，在应用上下文开关文档，在命令上下文校验及写入，保留完整 BlockTableId 身份。ProjectTagJournal 逐条持久化项目范围日志。数据模式不使用 DWG 撤销，不做全局 Reject；图纸模式每图一个撤销组，未保存文档全部保留。 PlantProjectDrawingHost 的 InDrawingAsync 在每次重建及 HasPendingDataAsync 回调内，通过 ScopedContextGuard 独立绑定当前数据库及完整 BlockTableId；作用域结束恢复原 Guard，覆盖只读跳过和前一图已关闭的路径，不保留跨图纸永久赋值。

RibbonController 在“Pn3D工具箱”中按顺序创建快速操作、位号检查、图纸操作三个原生 RibbonPanel，各有固定 Source.Id。Rows 用 RibbonRowPanel／RibbonRowBreak 组织三行：单个检查、本图检查／重建、项目检查／重建；首行不创建占位按钮，不增加手工 RibbonSeparator。所有小按钮 Width／MinWidth 为 128 DIP，统一由 Register 登记，保留既有按钮／选项卡 ID。七个位号命令使用 16 DIP 矢量，特性与导出为大图标，队列及忙碌逻辑保持。合成预览只证明命令分组和示意布局，原生 Ribbon 收缩、实际 DPI 及工作空间恢复待 CAD 复测。

类别名称适配分在 PlantTagCatalogNames.cs（PlantTagCatalogAccess 的 partial），必须接收所属 Project。CAD 主线程先调用 part.GetClassDisplayName(className)，项目名称空白或普通读取错误时再读 db.Tables[className].GetTableAttributeValue(PnPDatabase.DisplayNameAttribute)，最终回退内部类名。OperationCanceledException 和 SDK UserBreak 继续中止；每级独立处理，项目读取成功不访问表。批量 Read 传已有 part，面板传 context.Part；名称缓存仍按项目部分与实际类别。四个本图／项目检查和重建清单共用扫描结果，DisplayName 仅用于标题和排序，不改变 ClassName、格式、身份或写入。

RunPaths 统一以系统文档目录生成 Pn3DKit/用途/时间戳。RunLogFiles 在 CreateNew 成功后登记文件归属，记录本批新建末级目录；所有流关闭并封存后才可按清单删除，不递归清理。LogRetentionPolicy 根据结构化最终结果判断默认值；LogRetentionPrompt 在命令末尾处理 Y／N／Esc，Session 入口通过当前文档命令上下文交互，批次租约覆盖清理。AutoCadHost 在原生回调状态未知或命令监听清理失败时保护本批现场；普通错误仍可在安全交互时明确选择 N。该流程不接触离线验证产物。

PNDWGEXPORT的ExportAutoCheckScope在互斥和活动图纸确认后、项目读取前开始。PlantAutoCheckEnvironment使用accore.dll公开acedGetEnv／acedSetEnv访问DWGCHECKERAUTOCHECKSWITCH；缺省按有效1，原始有效整数由作用域记录，设置尝试前登记恢复责任。原生操作及监听结束后、日志保留询问前恢复回读；ProtectionReason非空则保持关闭，恢复失败独立报告，不覆盖原始错误。其他命令不使用该保护，FILEDIA／CMDDIA／PROXYNOTICE按安全的单次原生命令恢复。

PNFASTPANEL 保留 PICKFIRST，由 FastPanelController 管理原生 PaletteSet。首次创建在 Visible=true 后设置 DockSides.None，覆盖宿主恢复的旧停靠；复用分支只显示、激活，不重设位置。GUID、左右 DockEnabled、320×480 DIP 初始尺寸和 280×300 DIP 最小尺寸沿用原值。FastPropertiesView 使用连续的 Pn属性／值两列，默认一空行；属性行可增删改，路径列始终可编辑。路径输入立即撤销旧行许可，回车或失焦才读取；值仅回车提交，失焦、Tab、切换行及菜单结束均保留草稿，Esc 撤销当前格输入。UI 不持有 SDK 对象，模型不引用 Autodesk。

值草稿保存在行内独立缓冲，与当前编辑票据、行修订、属性路径及原值绑定；同票据的快照重绘保留输入及光标选区，新票据／原值／权限变化立即丢弃。FastSelectionController.Invalidate 在事件发生时撤销视图许可及草稿，隐藏／卸载和移除亦清理；某行提交沿用全局票据失效，其余草稿不重挂到新对象。FastPropertyValue.Placeholder 以结构化状态区分 `*(多种)`、空字符串 `(空白)`、真实 null `(null)`，Dirty 隔离实际输入；不按显示文本猜测。焦点内使用独立叠加提示，焦点外使用占位前景画刷，均只对文字使用 0.5 不透明度，跟随深浅主题与只读颜色；背景、边框、光标、选区不变。真实同文、文字 null、空格、制表符及实际输入保持正常前景和原始内容。SDK 读取不再将 null 合并到空字符串，Raw／票据 Original／预检及写前复核保留 nullable 身份；回读成功的 null 与无法回读分别记录，仍只向 SDK 提交用户的非 null 输入字符串。

统一键值由值格专用 RoutedUICommand 固定 RowId／路径修订／票据／独立 DraftRevision，关闭菜单并延迟排队；改输入、Esc 或失效都会拒绝旧回填，不能覆盖新状态。FastSelectionController 在 Idle 前后通过完整 BlockTableId、项目、选择集合及逐对象原值／权限复核，短锁和事务在 ShowModalWindow 前释放。候选来自完整 EditLease.Contexts.Original，经 PnUnifiedValuePolicy 全量 Ordinal 去重；不读聚合提示，不求值编号。有效确认／取消不 RequestRefresh 或消耗票据，TryApplyUnifiedDraft 只更新草稿并延迟恢复焦点；真正提交继续共用原批次。UnifiedValueView 的可编辑 ComboBox 不自动补全或默认选项，文本框、Popup、选项及按钮使用局部主题资源，Enter／Esc 先消费后结束窗口。

行重排由两列各自的本地 RoutedUICommand 固定所属行和源控件，精确 Alt 修饰键并解析 SystemKey；不注册面板／CAD 全局绑定。移动意图先取消所有已排队的菜单收尾，再在 Dispatcher Input 阶段复用原 PropertyRow、Container 和 TextBox 同步调整列表及视觉树，合并过渡焦点通知，按修订恢复原列选区并 BringIntoView；真隐藏或票据失效取消延迟恢复。移动不触发 RowsChanging／RowsCommitted、SuspendEditing 或 SDK 刷新；ReadSpecs、异步回填、编辑及统一键值按行 ID 识别，属性检索请求按行 ID 核对集合，顺序变化本身不使其过期。不新增配置持久化。


文本输入／复制期间由 WPF 焦点事件驱动 PaletteSet.KeepFocus，离开表格或隐藏时释放，属性提交期间保持原焦点。FastSelectionController 在读锁外结合批次、CommandInProgress、IsQuiescent 和表格输入焦点固定忙碌状态，再传入属性适配；事务内不重复查询 IsQuiescent。选择、文档、对象及命令事件立即使轮次失效并撤销值许可及草稿，SDK 查询仍在主线程 Idle 分批读取完整选择。PnPropertyPolicy 归一化自身路径、汇总共同值并保守判断权限，PnPropertyBatch 丢弃旧轮次。完整 BlockTableId、项目身份、行修订和票据共同隔离跨图纸、跨选择及跨属性编辑。多选权限取所有对象的许可交集，任一缺少／只读／未知则整行只读；隐藏或忙碌暂停，关闭及退出解绑。

FastPlantPropertiesAccess 直接读取 TargetObject 对应的数据行列，不展开关系或回退改写其他列。权限与提交前的类型、长度校验统一通过 FindEffectiveColumn 解析 table.AllColumns，包含继承字段；列约束仍从实际类别的 GetColumnAttributeNames／GetColumnAttribute 读取，保留类别覆盖。不得改回仅含本类定义的 Columns，否则继承的 Comment、Area 会被误判缺少元数据。权限包括 PnPColumn 的只读／系统／计数／自动生成／表达式／扩展／唯一性、明确列约束、IsAcquisitionProperty／GetAcquisitionStatus、Project.ReadOnly、图纸只读、读写登记、图层锁定和批次状态。PnPropertyMetadataPolicy 是实际 SDK 适配调用的纯策略：描述性元数据不判只读、不额外查询其值；必要约束无法解析或读取失败时只读。非获取字段不查询获取状态，普通字段不读取或拆分 Tag 格式；Tag 单独校验有效格式和注册表。Tag 限有有效格式和注册表的非临时 PnId 普通元件；管线、组、Piping 和缺少注册表时只读。

每次值提交通过共用 BatchRunGate 持有互斥，通过 AutoCadHost.InLockedDocumentAsync 在主线程应用上下文锁定当前文档，保留 PICKFIRST 与焦点，不启动 CAD 命令。多目标票据记录完整 ObjectId 集合、逐对象数据关联及原值。PnPropertyEditPolicy 忽略选择顺序但拒绝增减、过期、原值或权限变化；PnPropertyEditBatch 先对全部目标 Prepare，再检查项目／批内注册键冲突，预检通过后逐数据行写入并回读，别名只写一次。实际错误立即停止，结果保留逐项句柄、原值、新值、实际值和 SDK 状态；不假定回滚。预检、确认前及最终全量复核，逐数据行重读当前组；写入事件按关联目标复核，未知变化回退全量检查，结束后统一 Regen，刷新失败单独报告。不保存、不生成编号、不改变获取模式、不全局提交项目数据；DWG／Plant 缓存撤销待实机验证。

FastPropertiesView 沿用蓝灰／灰白动态画刷、原生停靠和高 DPI。值列使用独立样式，正常背景／文字置于 Setter，IsReadOnly 触发器切换专用灰色动态资源，避免局部值遮蔽；保持 IsEnabled=true，保留焦点、选择复制和 Tooltip。无有效票据等所有实际只读状态均灰显，恢复许可及深浅主题切换自动更新。表格最小行高 24 DIP，上下内边距 3 DIP，字号仍为 12，自动高度避免裁切。纯 WPF 离屏宿主使用合成数据，不加载 Autodesk；十条命令、单 DLL。面板保留末尾自动空行和右键移除；菜单借用焦点不提交，移除前撤销草稿与票据。菜单 MenuOpen 仅表示实际打开；ContextMenuOpening 的临时状态在 Dispatcher 确认，取消开启及关闭／隐藏／卸载均清理。混合值输入缓冲和提示分离，只有实际输入或明确删除才产生脏态；保留既有身份校验。

属性检索由 FastSelectionController 在主线程 Idle 读取，不新增 CAD 命令：固定完整 BlockTableId、对象、项目关联及轮次；SDK 读锁／事务在 CAD ShowModalWindow 前释放，确认后再次核对选择和关联。FastPlantPropertiesAccess.Catalog 使用 GetAllProperties(objectId, true) 的自身属性键，调用 context.Part.GetColumnDisplayName(table, name)，通过所属项目字符串表及实际类别配对名称，再由 SDK 回退列 DisplayName；不再以 GetColumnAttributeNames 是否声明 DisplayName 为门槛；不展开关系、不调用格式求值／写入／提交。PropertyCatalogView 只接收文本 DTO，勾选通过确认后才调用面板规范化追加流程；元数据失败保留属性和详情。属性路径始终保留内部名，显示名只用于第二列及提示；只读属性仍可加入供浏览。无效路径保留在目录但禁用添加并说明原因。

FastPanelControlStyles 在每个视图 UI 线程创建局部 WPF 模板，统一按钮、行右键菜单及分隔线。Popup 显式合并视图资源，深浅主题即时同步；菜单不再采用系统白色图标槽，禁用文字、悬停及分隔线分别使用面板动态画刷。按钮模板覆盖 WPF 默认白色禁用背景，保留原生 IsEnabled、Command、CommandTarget 和焦点／移除规则，不修改 CAD 全局资源。检索窗口第二列标题为“显示名称”，内部 FriendlyDisplayName 数据字段保持。

行移除使用每视图专用 RoutedUICommand，命令绑定和 Ctrl+D 输入绑定只安装在本表的路径／值格。菜单 CommandTarget 明确指向所属格，显示 Ctrl+D；PreviewKeyDown 在移除导致焦点变化前消费按键。占位空行及已移除行拒绝执行，不注册到 CAD 全局，不修改其他按键含义，不触发值提交。

值显示通过 FastValueKind 区分已有值、未就绪和确认不存在属性：SDK HasProperty 返回 false 才产生 AbsentProperty 并显示 ASCII --；已有空值和未就绪显示空白，错误仍显示读取失败。Raw 保持真实内容用于共同值比较和提交，显示标记不参与写入。多选部分缺少属性保留原因；混选未知状态不推断为确认缺少。

Pn3DKit.Tests 启用纯 WPF 并链接 FastPropertiesView，以独立 STA 路由事件验证 Comment 许可、空值、焦点保留请求、Enter／失焦／Esc、默认空行及旧快照失效。测试不打开窗口、不设置系统焦点、不加载 Autodesk；WPF 离屏预览不能代替原生 PaletteSet 和真实 SDK 验收。
