# Pn3DKit 0.1.0

AutoCAD／Plant 3D 2026 的 Windows x64 / .NET 8 NETLOAD 插件。Autodesk 程序集及运行时由 CAD 提供。

## 加载和导出

1. 将 Pn3DKit.dll 放在可信目录，在 CAD 中执行 NETLOAD。更新 DLL 后须重启 CAD，再加载新文件。
2. 在 Plant 3D 中打开项目和一张图纸，点击 Pn3DKit Ribbon“批量导出CAD”，或执行 PNDWGEXPORT。
3. 点击“P - PnId”／“D - Piping”或输入 P／D。默认匹配当前图纸类型，未匹配时默认 PnId。
4. 点击 Yes／No 或输入 Y／N，默认 Yes。有修改时仅保存一次，保存失败或仍有修改时不导出；No 跳过有修改图纸并继续，已保存只读图纸可以导出。
5. 在版本选择前确认一次目录。FILEDIA=0：仅命令行输入，支持中文／空格／成对引号、回车默认和 Esc 取消。FILEDIA=1：显示 `输出导出目录<建议路径> 或 [选择..(C)]:`；回车默认，输入 C 或点击完整标签才打开系统文件夹选择器。取消结束批次，选中路径冲突返回命令行。插件不更改 FILEDIA。
6. 点击格式或输入年份／LT 关键字；支持 2007、2010、2013、2018，回车默认 2013／AC1027。
7. 查看统计和所选目录根部 Export.log；.pn3dkit-logs/ 保存逐图结果、JSON 报告、LISP 和状态文件。

QSAVE 会改写源图，后续失败或取消不会撤销保存。请先备份，使用明确授权的项目副本检查图形完整性、外参和专业对象转换。缺少 Plant 环境或当前项目时命令会说明原因并结束。

## 输出与恢复

建议路径 %USERPROFILE%\Documents\Pn3DKit.Export\yyMMddHHmmss，同秒存在时建议编号。确认后直接使用所选目录，保留项目相对层次，外部图纸按路径摘要隔离，逐图不再询问。已有正式 DWG 跳过，已有日志／批次标记需选择其他目录。

LISP command-s 按“完整格式关键字 → 输出路径”运行，先写短名临时 DWG；确认命令结束、文件非空且版本头符合后才发布。拒绝覆盖源图与已有输出、目录联接／符号链接及超过 CAD 长度限制的路径。

原有文档保持打开。工具打开的图纸只在确认无修改且状态明确后关闭；取消、状态不明或新修改时保留图纸及临时输出，累计保留三张后停止。安全结束时恢复原活动图纸，运行时请勿切换项目、图纸或启动其他命令。

非空 DWG 和离线测试不能代替真实 CAD 验收。

## 源码与校验

仓库：https://github.com/dwzcna/Pn3DKit
发布：https://github.com/dwzcna/Pn3DKit/releases

SHA256SUMS 校验本目录的 DLL 和说明。插件与测试目标均为 .NET 8；解决方案为 Pn3DKit.slnx，测试项目为 Pn3DKit.Tests。构建与发布说明见仓库 README。
