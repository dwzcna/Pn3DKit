# Pn3DKit 0.1.4

用于 AutoCAD Plant 3D 2026、Windows x64 的工具插件。

## 安装

将 Pn3DKit.dll 放在 CAD 可信目录，在 Plant 3D 中执行 NETLOAD 加载。打开 Plant 项目和图纸后使用命令。升级时先退出 CAD，再替换并重新加载 DLL。

## 功能

| 命令 | 功能 |
| --- | --- |
| CHECKPNTAG | 检查一个部件的现有位号，显示期望位号和属性差异 |
| CHECKPNTAGBYCLASS | 按类别检查当前图纸模型空间，生成 TagCheck.log 和 TagCheck.csv |
| REBUILDPNTAGBYCLASS | 预检并确认后按类别更新 PnId 部件位号，生成重建日志与 CSV |
| GETPNTAGFORMAT | 查看一个部件采用的位号格式 |
| EVALUATEPNTAG | 输入格式，计算所选部件对应的位号 |
| PNDWGEXPORT | 将项目 PnId／Piping 图纸批量导出为普通 AutoCAD DWG |

CHECKPNTAG、GETPNTAGFORMAT、EVALUATEPNTAG 均支持先选择部件再执行命令，也支持执行后选择一个部件、回车确认。批量检查按当前图纸扫描，不使用预选范围。

## 按类别重建位号

执行 REBUILDPNTAGBYCLASS，选择当前模型空间的类别和日志目录，查看预检汇总及位号变化后，再明确选择 Y 执行。重建确认默认 N；写完后的 QSAVE 保存确认也默认 N。保存会包含图纸中已有的其他修改。

仅处理可写、以读写方式加入当前项目的图纸，只写 PnId.Tag。保留自动编号；Piping、PipeLines 及其子类（SLINE）、无格式、无 Tag、临时 Tag、期望 Tag 含问号或 SDK 错误均跳过。项目占用、同批目标重复、互换、占用链的所有涉及对象一律跳过。没有可用注册表时会提示冲突检查仅覆盖本图。

日志默认位于“系统文档目录/Pn3DKit.TagRebuild/yyMMddHHmmss”，支持与检查相同的 FILEDIA 和 C 目录选择。TagRebuild.log 记录预检、确认、写入及保存；TagRebuild.csv 带 UTF-8 BOM，七列为类别、句柄、原Tag、新Tag、动作、原因、差异字段。两文件均不覆盖。

CSV 是追加历史，先预检后追加最终动作，同一句柄最后一条为最终结果。同一数据行的多个图形合并句柄，只写一次。Esc 保留已完成修改，剩余项记录未执行；写入后回读验证并刷新图纸。

写入用一个 UNDO 组组织，但 Plant 数据缓存能否随 UNDO 完整回退尚待 CAD 实机确认，必要时请用日志恢复。请先使用项目副本验证位号注释、自动编号、撤销和保存。候选仅本地交付，CAD 反馈通过后再推送正式发布。

## 检查位号

CHECKPNTAG 显示例如：

```text
位号不符：现有 PT-101；按格式应为 PT-102
TargetObject.LoopNumber：Tag 中为 101，当前属性为 102
```

结果分为一致、不符、无格式、无 Tag、临时 Tag、错误。临时 Tag 以 ? 开头。检查保留现有自动编号，不修改位号、属性或保存图纸；无法可靠拆分时显示错误原因及可获得的 SDK 状态。

CHECKPNTAGBYCLASS 列出类别及数量后，可输入 1,3,5-7 或 *（全部）；重复去重，回车或 0 不执行。无格式类别单独提示跳过。

选择类别后确认日志目录，默认位于系统“文档”下的 Pn3DKit.TagCheck/yyMMddHHmmss。FILEDIA=1 时回车采用建议目录，输入 C 或点击“选择..(C)”浏览文件夹；FILEDIA=0 时在命令行输入路径。已有同名日志时需更换目录。

TagCheck.log 记录参数、逐条结果及类别汇总。TagCheck.csv 可用 Excel 打开，包含类别、句柄、现有Tag、期望Tag、状态、差异字段。逐条保存结果，Esc 中断保留已写内容；切换图纸或关闭原图时停止并说明原因。命令行显示六种状态统计和前 20 条不符，完整明细见日志。

## 查看与计算

GETPNTAGFORMAT 显示类别、格式名称和表达式。EVALUATEPNTAG 在选择后请求输入表达式，例如：

```text
#(+TargetObject.Type)-#(+TargetObject.Area)#(TargetObject.Number^NN)
```

计算结果只显示在命令行，不向部件赋号；属性明确缺失时会显示对应片段。

## 批量导出 CAD

点击 Pn3DKit Ribbon 的“批量导出CAD”或执行 PNDWGEXPORT，依次选择 PnId／Piping、是否保存修改、输出目录及 DWG 版本。支持 2007、2010、2013、2018，默认 2013。

目录在批次前只确认一次，直接写入确认的文件夹，并保留项目图纸层次。FILEDIA 的回车／C／命令行方式与检查日志一致。默认建议 %USERPROFILE%/Documents/Pn3DKit.Export/yyMMddHHmmss；根目录 Export.log 保存导出日志。

Yes／Y 会通过 QSAVE 保存有修改的源图，取消不会撤销保存；No／N 跳过有修改图纸。保存失败或仍有修改时不导出，已保存只读图纸允许导出。请先备份项目。

不覆盖源图或已有正式输出。取消或状态不明确时，可能保留工具打开的图纸和临时文件，请根据提示检查现场。执行时不要切换项目、图纸或启动其他命令。

## 说明与校验

升级后仍显示旧版本时，请退出 CAD，核对 DLL 位置后重新加载。缺少当前项目、非 Plant 部件、无格式等情况会在命令行说明原因。

本版构建及离线策略验证不能代替真实 CAD 验收；位号重建、取消响应及图形导出完整性仍待实机确认。

SHA256SUMS 用于校验本目录的 DLL 和说明。完整使用说明：https://github.com/dwzcna/Pn3DKit
发布下载：https://github.com/dwzcna/Pn3DKit/releases
