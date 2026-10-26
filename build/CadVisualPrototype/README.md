# CAD 离屏绘制原生验证

这些项目不加入产品解决方案或正式单 DLL 载荷，只用于用户授权的测试目录及 Plant 3D 实例，不调用 Computer Use，不保存或改写来源图纸，不更改 SECURELOAD／TRUSTEDPATHS。

当前入口为 build/CadVisualHost 的 Pn3DKit.CadVisualHost.dll／PNVISUALTEST34，用户已选择加载一次。Host 只接受 artifacts/validation/0.1.34/261027003013/ 下的活动图纸；读取 artifacts/validation/0.1.34/native-host.json，按 ProbeAssembly／ProductAssembly 的本轮本地绝对路径加载独立上下文，因此迭代 Probe 或产品 DLL 无须再次 NETLOAD。配置 Diagnostics 用于 SDK 诊断，RealCatalog 用于授权真实来源流程；均为测试入口，不属于产品十命令。

build/CadVisualPrototype 保留首次失败原型及依据。初稿把 Device.IsValid 当作完成条件，2026 实测标志不清除；后续改用有上限的完整读回稳定性核对。不要把历史失败原型当作验收入口。

build/CadVisualProbe 引用真实 SDK 和产品 DLL，在 CAD 主线程验证产品准备及绘制层。构建时指定 AutoCadManagedRoot、ProductAssemblyPath 和独立 BaseOutputPath，所有输出放本轮 validation 下，避免已加载文件锁定；不从其他位置装载替代 DLL。

最终 0.1.34 有 32 项原生检查通过：29 项绘制／未知边界，加 A／a1／a2 实际像素归组及受控 PNG、正负图像行向两项。证据 native-20261027075005579；身份、选择、WorkingDatabase、系统变量、测试前已有 DBMOD=17 前后均保持。此通过包含对不能绘制内容正确返回未验证，不是完整 DWG 比较覆盖。

实际合集.dwg 来源流程读到 11 来源、684 名称、103 候选，全部未验证；动态、依赖块 SDK 克隆错误、匿名原名不完整等原因保留在报告，不能据此称准确性验收通过。非连续线型、遮罩和重叠排序是已确认的后端边界，产品保守拒绝匹配。

每次输出 native-yyyyMMddHHmmssfff/ 下的 host.log／host.json／probe.log／probe.json、必要 PNG 及报告。只有实际生成的原生记录可作为证据，离线策略和辅助项目编译不能代替。0.1.34 仅本地提交，按最新安排由后续 0.1.35 推送发布。
