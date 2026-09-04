# PcCompat 响应式 IMGUI 布局设计

## 1. 文档状态

- 日期：2026-08-25
- 状态：本机实现、事务生命周期闭环和 Release 候选核对已完成；待受保护 runtime 交付与设备验收
- 范围：PcCompat MOD 的 Unity IMGUI 设置面与自绘菜单
- 目标样本：JipperResourcePack、JipperOverlayer、JipperKeyViewer，以及后续 ABI 正常的未知 PC MOD
- 实机验证：不在本机执行，由用户完成

本文定义通用响应式布局合同，不是某个 MOD 的名称白名单。后续实现与本文冲突时，必须先更新本文并说明契约变化。

## 2. 问题与当前实现

当前移动端设置宿主已经根据 `Screen.width`、`Screen.height`、DPI 和字体缩放计算逻辑尺寸、面板宽度及 `48` 逻辑像素触控高度。`PcCompatManagedSettingsUnityBackend` 会把整个内容区宽度传给 `PcCompatManagedImGuiBridge`。

旧桥只有全局内容宽度，不知道控件所在横排的真实剩余宽度，并曾按字符种类和文本长度估算是否换行。首轮实现已删除该估宽判据，改为按当前 `GUIStyle.CalcMinMaxWidth/CalcHeight` 采集的行级测量结果编译计划。

- 外层横排已经被固定标签、输入框、间隔或嵌套横排占用一部分宽度；
- JPOV 常见的 `100/120/140` 固定标签、`42/55` 输入框、`24x20` 箭头按钮和滑块组合；
- MOD 显式 `GUILayout.Width/Height` 覆盖自动高度；
- 短文本控件被错误保持在窄矩形中，或长文本按整个面板宽度误判为可容纳；
- 嵌套 `BeginHorizontal/EndHorizontal` 无法表达为独立语义组；
- 首次渲染、分辨率变化和字体变化没有稳定的行级测量缓存。

因此，字符宽度启发式只能视为临时实现，最终实现必须由行级布局协议替代。

## 3. 目标与非目标

### 3.1 目标

1. 文字完整可读，控件完整可操作，不重叠、不裁切、不超出内容区。
2. 根据每一行的实际可用宽度决定布局，不使用设备型号、MOD 名称或固定分辨率断点。
3. 尽量保持原 MOD 的视觉顺序和语义关系；空间不足时按语义组分段换行。
4. 对未知 MOD、动态文本、字体变化、横竖屏切换和热加载 generation 保持确定性。
5. 不让布局测量成为持续的游戏帧开销或日志刷屏来源。

### 3.2 非目标

- 不缩小字体来掩盖布局溢出。
- 不按比例压缩整个 PC 菜单。
- 不为 JRP、JPOV、JPKV 编写名称白名单或源代码补丁。
- 不改变 MOD 的设置值、按钮行为、控件顺序或业务逻辑。
- 不把布局适配扩展为新的 UI 框架；仍由 Unity IMGUI 完成绘制和事件处理。

## 4. 术语

- **布局事务**：同一个 Unity IMGUI 帧中，从 `EventType.Layout` 得到计划，到输入事件和 `Repaint` 复用该计划的完整过程。
- **布局节点**：由布局边界或控件调用点形成的稳定节点。
- **横排节点**：一次匹配的 `BeginHorizontal/EndHorizontal`。
- **语义组**：不能在内部拆行的最小控件组合，例如“标签 + 对应输入框”。
- **视觉矩形**：控件实际绘制的矩形。
- **命中矩形**：用于触摸命中的矩形，可大于视觉矩形。
- **原生透传**：不改变 MOD 的 `BeginHorizontal/EndHorizontal` 拓扑，仅在当前 `Layout` 捕获尺寸并为后续事务准备候选计划。
- **拓扑保留**：无论规划是否检测到溢出，第三方 MOD 声明的 `GUILayout` Begin/End 序列与类型均保持不变；响应式仅可调整文字换行、SelectionGrid 列数和高度。

## 5. 调用点身份与捕获面

### 5.1 稳定身份

重写器已有 `AppendCallsiteToken` 与 `ComputeManagedCallsiteToken`。布局实现复用同一机制，以程序集 MVID、宿主方法、IL 偏移和目标方法生成稳定调用点 ID。不得以运行时对象地址、显示文字或 MOD 名称作为身份。

以下调用必须带稳定调用点 ID 进入布局桥：

- `BeginHorizontal/EndHorizontal` 和必要的嵌套 `BeginVertical/EndVertical`；
- Label、Button、Toggle、TextField、TextArea、Slider、SelectionGrid；
- `Space/FlexibleSpace`；
- 生成 `GUILayoutOption` 的 Width、MinWidth、MaxWidth、Height、MinHeight、MaxHeight、ExpandWidth、ExpandHeight。

同一调用点在程序集更新后因 MVID 改变自然获得新身份，旧布局缓存不得跨程序集版本复用。

### 5.2 捕获原则

布局桥必须看到完整行边界和所有直接影响尺寸的 option。只截获部分按钮或只传整个面板宽度不构成响应式布局。

捕获层记录结构和尺寸意图，不吞掉 MOD 控件调用。最终仍须对 Unity 发出结构配对、顺序等价的 IMGUI 调用。

## 6. 布局树与可用宽度

每个匹配的横排形成独立节点。嵌套横排不直接扁平化；在尚未得到它的最终 rect 前，父节点也不得把“整个父宽度”的猜测值当作它的可分配宽度并据此改排。子节点独立计算内部布局，外层只有在获得足够的等价证明后才能重排。

节点可用宽度必须从父节点实际分配结果计算，并扣除以下占用：

- 容器 padding、margin 和 spacing；
- 已确定的固定视觉控件；
- 固定或最小尺寸的输入控件；
- `Space`；
- 同行其它语义组的最小尺寸。

不得用整个 panel/content width 代替当前横排剩余宽度。

## 7. 测量与语义分组

### 7.1 测量来源

文本尺寸使用当前字体、字体大小、GUIStyle padding 和 `GUIStyle.CalcSize/CalcHeight` 的等价能力测量。禁止继续使用按字符数量估算的 `EstimateMobileTextWidth()` 作为布局判据。

动态文字、语言、字体、样式或 option 发生变化时重新测量。测量值使用逻辑像素。

### 7.2 分组规则

布局保持原控件顺序，不重排业务顺序。可证明的关系形成不可拆语义组：

- 相邻标签与其后的输入框、滑块、枚举选择器；
- 一组互相关联的紧凑箭头或图标按钮；
- 明确由同一容器包围的复合控件。

标签与输入控件之间出现组终止边界、无关交互控件或无法解释的动态控制流时，不猜测关系。

### 7.3 未知结构兜底

无法可靠识别语义关系的横排一律原生透传，只在 `Layout` 捕获候选尺寸；不得为已知 MOD 建白名单来绕过此规则。未知结构不因兼容层猜测而改变 Begin/End 拓扑。

## 8. 重排算法

1. 对横排中的语义组计算最小宽度、首选宽度、可扩展性和触控要求。
2. 若所有语义组在当前实际可用宽度内可容纳，保持原横排。
3. 若溢出，按原顺序贪心分段；当前段加入下一组后超宽时，在该组之前换行。
4. 单个语义组自身仍超宽时，执行该组内部允许的响应式布局；仍无法证明安全时改为组内竖排。
5. 不允许通过裁切文字、缩小字体或重叠命中区域强行保持横排。

该算法是“语义组 + 分段换行”，不是达到某个固定屏幕宽度后把整页全部竖排。

## 9. 首帧与 IMGUI 事件一致性

首次遇到未知行时，首个布局事务保留原始拓扑并采集真实尺寸；下一次 `Layout` 起，只有已识别且确认溢出的组才可以切换到测量后的最紧凑分段方案。这样不会让 JRP 一类未知复杂行在首帧被兼容层改成竖排。

布局计划只允许在 `EventType.Layout` 边界切换。输入事件和 `Repaint` 必须复用该次 Layout 已选定的同一计划，禁止在 Repaint 中因新测量值临时改变组结构，否则 Unity 控件 ID、键盘焦点和点击命中会错位。

这里的“一致性”同时包含 MOD 业务状态。2026-08-25 的 JRP/JPOV 现场报告证明，仅把控件结果延迟到下一次 `Layout` 内交付仍不充分：结果是在 Layout 遍历中途返回，MOD 可能让当前 Layout 保持旧分支、下一 Repaint 改走新分支。完整的提交后重建屏障、稳定控件身份、暂态恢复和回归要求见 `IMGUI_LAYOUT_TRANSACTION_COMPLETION_PLAN.md`。该计划是本节的必需补全，不是可选容错。

这允许内部存在一帧稳定过程，但首帧不得凭猜测破坏 MOD 的布局结构；已识别的后续计划仍以完整、可操作为前提，不以裁切换取紧凑。

## 10. GUILayoutOption 语义

兼容层可以覆盖 MOD 显式宽高，但必须按控件语义处理：

| 类型 | Width/Height 处理 | Expand/Flexible 处理 |
| --- | --- | --- |
| 文本按钮、Toggle、Label | 显式值是首选尺寸；文字和 padding 形成可读最小值；换行后允许自动增高 | 可消费剩余宽度 |
| TextField、数值输入 | 显式宽度是最小/首选宽度，不无条件拉满；高度至少满足触控合同 | 仅显式 Expand 时扩展 |
| Slider | 保留轨道最小宽度，与其标签/数值框作为复合组计算 | 轨道优先消费组内剩余宽度 |
| 纯图标、箭头、纹理 | 视觉固定尺寸保持精确 | 默认不扩展 |
| Space | 保持明确间隔 | 不扩展 |
| FlexibleSpace | 最小为零 | 只消费当前段剩余空间 |

`MaxWidth/MaxHeight` 不能迫使文本不可读或触控区域不可用；发生冲突时，可读性和触控合同优先，并记录一次约束覆盖计数。

## 11. 视觉尺寸与触控尺寸

普通交互控件的目标最小命中矩形为 `48x48` 逻辑像素。JPOV 的 `24x20` 箭头等紧凑控件保持原视觉尺寸；真正的命中区域扩大必须由原生输入投影完成，不能通过 bridge 临时修改 `GUIStyle.fixedHeight`、扩大视觉布局或挤压相邻控件来伪造。该原生投影尚未完成。

相邻命中矩形不得重叠。若同行空间不足以同时满足最小命中区域，应换行或增大组间距，不能以重叠命中区维持视觉横排。

鼠标/键盘导航和 Unity 控件 ID 继续对应原控件；扩大命中区不得生成第二个业务按钮。

## 12. 缓存、隔离与失效

### 12.1 缓存键

缓存至少按以下维度隔离：

- MOD ID；
- resource/runtime generation；
- 稳定调用点 ID；
- 当前节点的实际有效内容宽度；
- 字体、字号、GUIStyle padding/spacing 和相关 option 的样式指纹。

缓存不得跨 MOD、跨 generation 或跨程序集 MVID 共享。MOD Disable、Unload、Faulted、热更新和 generation 退休时清除对应状态。

### 12.2 失效条件

以下任一变化立即使相关计划失效，并从原生透传重新测量：

- 屏幕尺寸、方向、DPI、渲染缩放或内容区宽度变化；
- 游戏语言、字体对象、字号或样式指纹变化；
- 行结构、调用点序列、动态文本测量宽度或 GUILayoutOption 变化；
- MOD generation 或程序集 MVID 变化。

宽度采用实际逻辑像素，不使用分辨率档位。浮点比较允许 `0.5` 逻辑像素的数值噪声容差，但不能把不同布局宽度归入粗粒度 bucket。

### 12.3 防抖

从横排切换到换行的条件是“所需宽度大于可用宽度”。已经换行后，只有可用宽度至少达到“所需宽度 + 8 逻辑像素”才恢复横排。决策在一个布局事务内冻结，避免临界宽度、字体测量抖动导致逐帧来回切换。

### 12.4 有界状态

每个 MOD generation 的默认上限：

- `512` 个缓存行；
- 每行 `64` 个直接子节点；
- 最大嵌套深度 `16`。

缓存使用有界 LRU。超过任一上限时，该行进入原生透传并按 generation 记录一次警告，不允许无界增长或逐帧重新分配。

## 13. 异常与不配对布局调用

布局捕获必须检测栈下溢、结束类型不匹配、超深嵌套和帧末未闭合节点。

发生异常时：

1. 只清理由兼容层额外创建的布局组，且必须在 `finally` 中逆序关闭；
2. 不伪造、吞掉或跨帧补齐 MOD 原始的 Begin/End 调用；
3. 将受影响调用点标记为该 generation 的原生透传保守路径；
4. 同一调用点只记录一次警告，并增加聚合计数；
5. 自适应层自身不得让异常越过 unmanaged/managed OnGUI 回调边界。

如果 MOD 原始布局本身不配对并最终由 Unity 抛错，仍由现有 settings surface fault 合同处理；兼容层不隐藏 MOD 自身的结构错误。

## 14. 性能合同

响应式层的目标预算：

- 热路径：每帧 `128` 个控件时，新增布局开销 p95 不高于 `0.25 ms`；
- 冷测量：单个设置面首次计划不高于 `1 ms`；
- 稳态：布局层零托管堆分配；
- 仅活跃响应式横排的 Layout 事件允许 `GUIStyle.CalcMinMaxWidth/CalcHeight`；根控件、普通纵向内容、输入和 Repaint 不做该测量；稳定文本按 native GUIStyle 身份做有界 intrinsic-measurement 缓存；
- 每帧不做程序集扫描、类型枚举、字符串身份拼接或反射成员查找；
- 缓存命中后按调用点进行 O(1) 查询，整帧工作量与实际控件数线性相关。

这些是工程预算，不是已完成验证。无实机自动化条件下，本机测试至少要提供分配计数、确定性微基准和最坏上限测试；最终设备帧耗时由用户验收。

## 15. 诊断合同

禁止逐帧、逐控件输出 Logcat。默认只保留聚合状态：

- measuredRows、cachedRows、horizontalRows、segmentedRows、safeVerticalRows；
- cacheHit/cacheMiss/eviction；
- constraintOverrides、malformedRows、limitFallbacks；
- 最近一次失效原因和布局环境摘要。

正常计划编译最多输出一次 INFO。原生透传回退、结构错误和上限回退按 `MOD + generation + call-site + reason` 最多输出一次 WARN。详细行树仅进入显式诊断报告，不进入持续 Logcat。

## 16. 验证要求

### 16.1 本机合同测试

1. 调用点 ID 对同一程序集确定，对 MVID/IL 调用点变化敏感。
2. Begin/End、嵌套横排和 option 捕获完整，重写后不存在绕过布局桥的受支持签名。
3. 标签 + 输入框、滑块 + 数值框、紧凑箭头组和嵌套横排按预期分组。
4. 无法证明分组时整行原生透传，不按 MOD 名称特殊处理。
5. 首帧原生透传，下一 Layout 才切换已确认计划；Layout、输入与 Repaint 结构一致。
6. 宽度、DPI、字体、样式、动态文字、MVID 和 generation 变化会失效缓存。
7. `8` 像素迟滞阻止临界宽度振荡。
8. 显式 Width/Height、Expand、Space 和 FlexibleSpace 保留本文定义的语义。
9. 紧凑控件视觉尺寸保持；原生触控投影完成后再验证命中区至少 `48x48` 且不重叠。
10. 不配对调用、超限和桥异常不会泄漏兼容层布局组或跨 MOD 污染状态。
11. 缓存达到上限后确定性回退，Unload/generation 退休后状态可回收。
12. 稳态分配和微基准满足第 14 节预算。

真实 JPOV `Settings.cs` 中固定标签、窄输入框、箭头、滑块和嵌套横排必须作为生产语料测试，不允许只用人工构造的单层按钮样本。

### 16.2 用户实机验收

- JRP、JPOV、JPKV 菜单所有按钮和标签文字完整；
- 2400x1080、不同 DPI/字体及方向变化后无裁切、重叠、越界和输入错位；
- 固定箭头易于触摸且不会误触相邻控件；
- 首次打开无溢出闪帧，稳定后不逐帧抖动；
- MOD 热加载、禁用、重载后不继承其它 MOD 或旧 generation 的布局；
- Logcat 无布局逐帧刷屏。

## 17. 实施顺序与完成定义

1. 扩展 managed rewrite 捕获面并为布局边界、控件和 option 注入稳定调用点 ID。
2. 在 bridge 中建立按 MOD/generation 隔离的布局事务与轻量布局树。
3. 实现真实测量、语义分组、文本换行、稳定列数和触控矩形；不得改变第三方 `GUILayout` 组拓扑。
4. 加入有界缓存、失效、迟滞、异常清理及聚合诊断。
5. 删除 `EstimateMobileTextWidth()` 及只依赖全局 content width 的启发式判据。
6. 递增 managed rewrite cache ABI 与 IMGUI bridge ABI，确保旧重写缓存失效。
7. 只运行受影响的 ModManager 单项目构建、生产 MOD 重写审计和定向测试；不运行顶层全量构建，不操作实机。

当前已完成以下本机实现：

1. 已确认 GUILayout 面的 token 化重写：横/竖容器、间隔、全部八项 option（`Width/MinWidth/MaxWidth/Height/MinHeight/MaxHeight/ExpandWidth/ExpandHeight`）、按钮、标签、开关、输入框、文本区和滑块。
2. `PcCompatManagedResponsiveImGuiLayout` 已接入 settings frame。所有第三方 `BeginHorizontal/EndHorizontal` 与 `BeginVertical/EndVertical` 均逐一透传；规划器只在 Layout 边界冻结文本换行、列数和高度策略，绝不插入、删除或替换 Unity layout group。
3. 代理输入面已加入 `GUIStyle.CalcMinMaxWidth`、`CalcHeight` 与 `GUILayout.ExpandHeight`；重新生成的共享 `UnityEngine.IMGUIModule.dll` 已包含 `ExpandHeight(bool)`，生成代理审计为 `0` issue。
4. `MaxWidth/MinHeight/MaxHeight` 通过 Android native `GUILayoutOption` materializer 以已确认的 Unity enum 值创建；`ExpandHeight` 使用生成 proxy 的真实 Android API。
5. `SelectionGrid` 已获得稳定调用点 token，并作为不可拆的选择组参与父横排规划；内部列数使用独立的 Layout 事务，首个 Layout 保留 MOD 请求的列数、下一 Layout 才应用由单元最小宽度和可用宽度确定的列数，输入与 Repaint 复用冻结列数。其渲染只建立一个 `GUILayoutUtility.GetRect` 条目，再以 `GUI.Toggle` 切分该矩形，不建立内部 layout group。
6. 托管重写缓存 ABI 已升级为 `xphorror.pcmod-managed-cache.v65-imgui-style-fingerprint`，IMGUI bridge ABI 为 `PcCompatManagedImGuiBridge.v19-style-fingerprint`，并包含 `PcCompatManagedSettingsTransaction.v1`；旧重写 DLL 不会复用。测量缓存纳入样式/字体对象身份、字号、fixedHeight、wordWrap、richText、margin/padding 和动态文本内容，变化只在下一次 Layout 边界重新规划。
7. 文本换行使用三态决策：原生透传保持 MOD 声明的 `GUIStyle.wordWrap`，已验证可容纳的横排才明确强制单行，已证明溢出的文本才明确打开自动换行；这不会改变原 MOD 的 Begin/End 结构。`CalcMinMaxWidth/CalcHeight` 测量时临时关闭 `wordWrap`，获得真实 intrinsic 单行宽度；否则 Unity 会把可断词的最短 token 错当成最小宽度，导致一行可容纳的文字被错误拆成多行。
8. `SelectionGrid` 的单元在一个外层矩形内按冻结列数和 `8` 逻辑像素间隔切分。降列或单元换行时，会移除由兼容层标记的 `Height/MaxHeight` 并注入计算出的总高度；普通文本 Button/Toggle/Input 及 Label 的低高度 `Height/MaxHeight` 也会按字形基线移除。单字符箭头和图标保持紧凑高度，`48x48` 命中扩展不由此伪造。
9. 已增加布局状态机、八项 option、native materializer、组拓扑和生产重写规则的合同测试。`Repaint` 状态机（含 SelectionGrid）和普通托管 option 数组 snapshot 已有 `0 B / 128` 分配回归；非 Layout 测量门禁、稳定文本有界缓存、frame 内 host backend/空 option 复用及取消逐控件 `fixedHeight` 往返已实现；线程本地 scope 池归还时清空 backend/options 引用，避免固定可卸载 MOD ALC。代理生成、Android 托管构建和相关定向测试均已通过。
10. 设置面输入事务已在 `PcCompatManagedImGuiInteractionFence` 中闭合：Commit Layout 交付值后，下一 Rebuild Layout 前不会再把 Input 或 Repaint 分派给 MOD；热卸载若发生在活动 OnGUI frame 中，fence reset 会延后到 interaction bridge 与 thread-static frame 清理完成。JRP credits、JPOV alignment、连续 Text/Slider、折叠控件消失和循环 call-site occurrence 均有通用语料测试，不依赖 MOD 名称白名单。
11. `SelectionGrid` 的选择合并只接受未选中格的 `false -> true` 转换。Unity `GUI.Toggle` 会让旧选中格在同帧继续返回 `true`；若把任意 `true` 都当作激活，高索引旧项会覆盖低索引新点击。该顺序回归由高索引切换到低索引的合同测试固定。

仍待完成：设备上的 native option materializer 验证、实际触控命中区域扩大到 `48x48` 的原生投影、尚未纳入代理的其它 Unity `GUIStyle`/spacing 属性、端到端 IL2CPP 绘制路径的时延微基准，以及第 16.2 节的人工实机验收。

## 18. 已确认决策

1. 不允许兼容层响应式改变 MOD 声明的任何 `GUILayout` 组拓扑。
2. 使用每行实际测量结果，不使用固定分辨率断点。
3. 溢出时按语义组选择文本换行、列数和高度，不插入分段 layout group。
4. 标签与对应输入控件是不可拆语义组。
5. 无法可靠分组时整行原生透传。
6. 首帧允许内部稳定过程，但不得凭猜测改变 MOD 的原始 Begin/End 拓扑。
7. 缓存按 MOD、generation、call-site、有效宽度和样式环境隔离并及时失效。
8. 嵌套横排保留为布局树节点；未得到最终 rect 前不得据猜测让父行重排。
9. 可按控件语义覆盖显式 Width/Height。
10. 紧凑控件保持视觉尺寸；触控区扩展到至少 `48x48` 由后续原生投影实现且不得重叠。
11. 使用 `8` 逻辑像素迟滞防抖。
12. 布局结构异常时安全降级、限频告警，不污染其它 MOD 或 generation。
13. 热路径遵守有界缓存、稳态零分配和无逐帧日志合同。
