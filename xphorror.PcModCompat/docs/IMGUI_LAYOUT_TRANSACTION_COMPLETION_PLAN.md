# PcCompat IMGUI 布局事务补全计划

## 1. 文档状态

- 日期：2026-08-25
- 状态：核心实现、生命周期清理、缓存 ABI、定向回归与 Release 产物核对已完成；待受保护 runtime 交付与用户实机验收
- 适用范围：PcCompat 原菜单设置面，包括 UMM、JALib 及直接使用 `GUILayout` 的托管 MOD
- 关联设计：`RESPONSIVE_IMGUI_LAYOUT_DESIGN.md`
- 现场证据：`last_settings_failure_JPOV.txt`、`last_settings_failure_JRP.txt`

本文前半部分保留问题定性和目标设计；第 14 节开始是当前源码状态下的实际补全清单。
实现状态以当前源码、定向测试记录和 Release 产物为准，不以旧设备 runtime、旧重写缓存或本计划早期的
“待实现”措辞为准。

## 2. 问题结论

两份现场报告均为：

```text
UnityEngine.ExitGUIException: GUILayout: Mismatched LayoutGroup.Ignore
```

JRP 在按钮事件中修改 `_creditsShown`。旧分支只绘制按钮并立即返回，新分支会进入 credits 内容并增加
`BeginHorizontal`。JPOV 在按钮事件中修改 `_expandedAlign`，对齐选择区的
`BeginHorizontal + SelectionGrid` 随之出现或消失。二者都是输入结果改变后续 GUILayout 树的真实样本。

当前响应式层已经逐一透传第三方 `BeginHorizontal/EndHorizontal` 与
`BeginVertical/EndVertical`，因此本次故障不是响应式规划器替换组拓扑。现场堆栈包含
`PcCompatImGuiContainerMode` 参数，也证明设备运行的是拓扑保留版本。

直接缺陷位于现有 `PcCompatManagedImGuiInteractionFence`：

1. 输入事件发现按钮、Toggle、文本或数值发生变化时，fence 暂存结果并向 MOD 返回旧值。
2. 下一次 `Layout` 遍历到同一个控件时，fence 才把暂存结果返回给 MOD。
3. MOD 因此在 Layout 遍历中途修改自己的布局状态。
4. 当前 Layout 已有一部分条件、局部变量和组结构按旧状态计算；紧随其后的 Repaint 却读取新状态。
5. Unity 发现 Layout 缓存与 Repaint 的组序列不同，抛出 `Mismatched LayoutGroup.Ignore`。

“在 Layout 事件内交付”不等于“在 Layout 建树前完成状态切换”。这是现有 fence 的事务边界错误。

当前 `PcCompatManagedSettingsController` 还只识别
`Getting control ... position in a group ... when doing repaint`。它不识别本次
`Mismatched LayoutGroup.Ignore`，因此一次可恢复的事件间失配会直接把设置面升级为 `Faulted`。

## 3. 目标与非目标

### 3.1 目标

1. 输入导致布局状态变化时，Layout 与后续输入/Repaint 永远不跨状态混用。
2. 对 JRP、JPOV 及未知未来 MOD 使用同一通用机制，不按 MOD 名称、类型或字段建立白名单。
3. 保持第三方 MOD 声明的 Begin/End 拓扑，不插入、删除、替换或跨事件补齐原始布局组。
4. 暂态布局失配可恢复，但真正不配对的 MOD 布局仍须有界失败并隔离到当前设置面。
5. 稳态无新增托管分配、无逐帧日志；只在真实交互后支付一次有界稳定成本。
6. MOD、generation、程序集 MVID 和设置面之间的事务与待处理输入严格隔离。

### 3.2 非目标

1. 不修改 JRP、JPOV 或其它 MOD 源码。
2. 不把所有 `ExitGUIException` 一律吞掉。
3. 不用反射扫描 MOD 字段并猜测哪个字段决定布局。
4. 不通过缓存旧画面、伪造 GUILayout 组或重复调用 MOD OnGUI 掩盖错误。
5. 不改变 Android MOD、PcCompat MOD 或不同 MOD 之间既有的数据域隔离合同。

## 4. 总体方案

将 `PcCompatManagedImGuiInteractionFence` 从“延迟返回值字典”升级为设置面级的
**IMGUI 事件事务协调器**。每个设置面独立维护以下状态：

| 状态 | 含义 | 允许的 MOD OnGUI 分派 |
| --- | --- | --- |
| `Stable` | Layout 与非 Layout 使用同一业务状态 | 全部事件 |
| `InputPending` | 输入结果已捕获，尚未交给 MOD | 全部事件，但继续向 MOD返回旧值 |
| `CommitLayout` | 在真实 Layout 中交付一批输入结果 | 仅当前 Layout |
| `AwaitingRebuildLayout` | 业务状态已变化，当前 Layout 仍可能是旧树 | 禁止所有非 Layout 分派 |
| `RebuildLayout` | 下一次真实 Layout 按新业务状态重新建树 | 仅当前 Layout，不交付新输入 |
| `StableVerification` | 已得到新 Layout，等待首个匹配的非 Layout 事件 | 允许分派；成功后回到 `Stable` |
| `Recovering` | 捕获到已知布局缓存失配，等待重新建树 | 与 `AwaitingRebuildLayout` 相同 |

状态转换如下：

```text
Stable
  -> 输入变化 -> InputPending
  -> 下一 Layout 交付 -> CommitLayout
  -> 至少交付一个值 -> AwaitingRebuildLayout
  -> 跳过非 Layout，等待下一 Layout -> RebuildLayout
  -> Layout 成功 -> StableVerification
  -> 首个非 Layout 成功 -> Stable
```

如果 `CommitLayout` 中没有任何待处理控件被匹配，则不得进入重建；未匹配项按本文第 6 节的规则处理。

## 5. 关键事务规则

### 5.1 提交后的重建屏障

在 `CommitLayout` 中，只要有一个暂存结果被返回给 MOD，就认为 MOD 的任意业务状态可能发生变化。
协调器在该 Layout 结束时进入 `AwaitingRebuildLayout`。

从此刻到下一次真实 `Layout` 成功结束前，宿主不得调用该设置面的 MOD OnGUI。该屏障必须在
`PcCompatManagedSettingsController` 调用 `_draw()` 前生效，不能等到 shim 已建立外层 Area、ScrollView
和内容组后再跳过，否则宿主自身也会形成不同的组序列。

下一次真实 `Layout` 作为 `RebuildLayout`：

- 使用已经提交后的 MOD 状态完整执行一次 OnGUI；
- 不再交付同批输入，也不交付新输入；
- 只建立新的 Unity GUILayout 缓存和响应式冻结计划；
- 成功后才允许 Repaint、Mouse、Key 和 Used 等非 Layout 事件继续进入 MOD。

这样 JRP 的 credits 分支和 JPOV 的 alignment 分支都会在一次完整 Layout 开始前已经稳定。

### 5.2 不重复调用 OnGUI

禁止在同一个 `Layout` 事件内为了“重新建树”第二次调用 MOD OnGUI。Unity 的 GUILayoutUtility 已经向当前
cache 写入第一棵树，第二次调用只会追加另一棵树并改变控件 ID，无法构成合法重建。

### 5.3 输入期间的表现

- Button 激活在一个事务内最多保留一次，重复激活合并。
- Toggle、SelectionGrid、文本、数值和 Slider 使用 latest-write-wins，同一控件只保留最新值。
- `AwaitingRebuildLayout` 期间不进入 MOD，因此新的原始输入不绑定到旧树。
- 连续 Slider/Text 输入最多以隔帧方式提交并合并中间值；不得用逐输入同步重建换取表面实时性。
- 该成本只发生在值真正变化时。稳定显示、滚动和无变化的输入事件不进入重建屏障。

## 6. 控件身份与待处理队列

当前 fence 使用 `Raw/Host + 顺序游标` 作为键。布局状态变化后控件可能出现、消失或改变顺序，单纯游标会把
旧输入错误投递给另一个控件，必须替换。

### 6.1 Raw GUILayout 控件

重写器已向受支持控件注入稳定 `callsiteToken`。所有 `ResolveRawButton/Toggle/Text/Value` 调用必须携带：

```text
MOD generation + assembly MVID + callsiteToken + occurrenceIndex + control kind
```

同一 IL 调用点位于循环中时，用该调用点在本次 OnGUI 中的 occurrence index 区分。不得退回全局顺序游标。

### 6.2 Host/schema 控件

宿主控件使用稳定的 schema path 或固定 host control ID；页头、页脚与 MOD 内容使用不同 lane。宿主的保存、
关闭动作不进入 MOD 内容事务，继续在帧结束后执行。

### 6.3 消失控件

一个提交批次结束后仍未匹配的输入不得顺延给同一位置的其它控件：

- 当前批次已引起结构变化时，未匹配项立即丢弃；
- 当前批次完全未匹配时，最多保留 `2` 个 Layout epoch；
- generation、MVID、关闭、Faulted、Unload 或热更新发生时立即全部清除；
- 每个设置面最多保留 `256` 个待处理键，超限时丢弃最旧项并记录一次聚合警告。

## 7. 主动屏障与被动恢复

主动事务屏障是主路径。异常恢复只处理未覆盖调用面、Unity 事件异常顺序或旧缓存残留。

### 7.1 可恢复分类

沿异常链按类型名和精确消息分类，至少覆盖：

```text
GUILayout: Mismatched LayoutGroup.Ignore
Getting control N's position in a group with only M controls when doing repaint
```

不得仅对整个 `exception.ToString()` 做宽泛的 `Contains("ExitGUI")`。普通
`GUIUtility.ExitGUI` 控制流、业务异常、缺失代理成员和文件/资源异常均不属于布局恢复。

### 7.2 恢复动作

捕获可恢复布局失配时：

1. 调用现有 `AbortFrame()`，只关闭宿主和兼容层自己建立的 frame 资源。
2. 清除当前响应式 frame、临时 backend 引用和未完成的组捕获，不跨事件补齐 MOD 原始组。
3. 丢弃可能基于错误控件树捕获的输入批次。
4. 保持原设置面 `Open/Opening`，进入 `Recovering`，禁止立即 Close 或标记 `Faulted`。
5. 跳过非 Layout，等待下一次真实 Layout 完整重建。
6. 只有“重建 Layout 成功且首个非 Layout 事件成功”才清零连续恢复计数。

### 7.3 有界失败

上限按失败的重建事务计算，而不是按任意 `Dispatch()` 调用计算：

- 同一 MOD generation 连续 `3` 次重建事务仍出现相同布局失配，设置面进入 `Faulted`；
- 没有待处理输入、没有布局环境变化且错误稳定重复时，视为 MOD 原始 Begin/End 不配对；
- 只关闭和隔离当前 MOD 设置面，不卸载 MOD，不影响自绘 HUD、其它 MOD 或游戏输入；
- 用户再次显式打开仍是独立重试边界，但旧事务、输入和响应式缓存不得复用。

## 8. 模块改动清单

### 8.1 `PcCompatManagedImGuiInteractionFence`

1. 引入第 4 节状态机、提交批次和 Layout epoch。
2. 将 raw 键从顺序游标升级为 callsite token + occurrence。
3. 暴露 `ShouldDispatch(eventKind)`、`DeliveredDuringLayout`、`MarkFrameSucceeded`、
   `MarkRecoverableFailure` 与有界诊断快照。
4. 保持 steady-state 无分配；移除 Layout 过期时的 LINQ + `ToArray()`，改为有界原地清理。

### 8.2 `PcCompatManagedImGuiBridge`

1. 所有可产生用户结果的 bridge 方法向 fence 传递稳定 token 和控件类型。
2. Begin/End settings frame 把真实事件类型和完成结果交给事务协调器。
3. frame 异常时保证 thread-static scope、backend 和响应式 frame 在 `finally` 中归还。
4. 不改变现有第三方 Begin/End 逐一透传合同。

### 8.3 `PcCompatManagedSettingsUnityBackend`

1. 提供结构化 `EventType`，不再由多处重复反射并比较字符串。
2. 在建立 Area/ScrollView 之前提供 settings content dispatch preflight。
3. 在 `EndFrame/AbortFrame` 后向协调器报告 Layout、Repaint 或输入事务的成功/失败。
4. 将事务状态、epoch、pending/delivered 数量加入故障报告，不加入逐帧 Logcat。

### 8.4 `PcCompatManagedSettingsController`

1. `_draw()` 前执行事务 preflight；等待重建时直接跳过整个 MOD 设置面 OnGUI 分派。
2. 用结构化异常分类替换当前单一字符串判据。
3. 恢复计数只在完整稳定周期后清零，不在任意成功 Dispatch 后清零。
4. 保持 `Open/Opening` 与 `Faulted` 的原有外部语义；恢复态作为内部状态，不扩散为 MOD 生命周期故障。

### 8.5 托管重写器与缓存 ABI

1. 审计所有 Button、Toggle、SelectionGrid、TextField/TextArea、Slider 及宿主值控件均携带稳定 token。
2. 对同一调用点循环生成 occurrence，不按文本或当前序号建立身份。
3. 递增 managed rewrite cache ABI、IMGUI bridge ABI 和 settings shim ABI，禁止复用旧重写 DLL。
4. 用真实 JRP/JPOV/JPKV 发布程序集执行 production rewrite audit。

## 9. 回归测试计划

### 9.1 最小确定性事件序列

测试夹具必须显式驱动以下序列，而不是直接抛一条伪造异常：

```text
Layout(old) -> MouseUp(capture) -> Repaint(old)
-> Layout(commit) -> skip non-Layout
-> Layout(rebuild new) -> Repaint(new)
```

断言：提交 Layout 后、重建 Layout 前，MOD OnGUI 分派次数不增加；首个稳定 Repaint 不抛异常。

### 9.2 真实控制流语料

1. JRP 模式：分支条件在 Button 之前读取，Button 后修改 `_creditsShown`，当前调用立即 return。
2. JPOV 模式：`expanded` 局部变量在 Button 前计算，Button 后修改 `_expandedAlign`，后续分支仍使用旧局部值。
3. 同一调用点循环生成多个控件，结构变化后 occurrence 不串位。
4. 一个按钮折叠区域后，该区域内尚未提交的输入被丢弃，不投递给新位置控件。
5. 连续 Slider/Text 输入合并到最新值，队列和分配保持有界。

### 9.3 异常与隔离

1. 两种已知 GUILayout 缓存失配进入 Recovering，不立即 Faulted。
2. 普通 `ExitGUIException`、业务异常和缺失成员异常不得被误判为可恢复布局错误。
3. 连续三次重建失败后只 Fault 当前设置面。
4. JRP 恢复不得清理 JPOV 的事务；同一 MOD 新 generation 不继承旧 pending。
5. Close、Unload、Faulted、热更新和程序集 MVID 变化清空全部事务状态。

### 9.4 属性与性能测试

1. 对 Layout/Input/Repaint 随机事件序列做状态机属性测试：提交后未重建前永不分派非 Layout。
2. 对控件出现、消失、重排和重复调用点做随机测试：输入只到达原稳定身份。
3. `128` 个稳定控件、连续 `1000` 帧无输入时新增分配为 `0 B`。
4. pending 上限、epoch 过期和三次恢复上限均可确定复现，无无界字典或日志增长。

### 9.5 定向构建与审计

1. 只运行 PcCompat/ModManager 受影响托管项目的 Release 构建。
2. 重新生成共享代理和受影响 shim，执行代理审计与 production rewrite audit。
3. 不运行顶层全量构建，不生成 APK，不操作实机。
4. 设备行为由用户最终验收，本机不能宣称 Unity IL2CPP 事件序列已经实机通过。

## 10. 性能合同

- `Stable` 热路径：一次状态判断和 O(1) 键查询，不做反射查找、LINQ、字符串拼接或日志。
- 无输入时不建立 pending 批次，不增加每帧堆分配。
- pending 表有界为 `256`，每项只保存必要身份、值和 epoch。
- 同一值重复输入不创建新项；连续值采用原位覆盖。
- 结构重建最多增加一个 Layout epoch 和跳过一个非 Layout 周期，不忙等、不主动循环重绘。
- 诊断默认只维护计数器；详细事务快照仅在失败报告生成时格式化。

## 11. 风险与处置

| 风险 | 处置 |
| --- | --- |
| 连续 Slider/Text 交互视觉更新变慢 | latest-write-wins 合并；只在真实值变化时进入屏障；用微基准和设备验收确认 |
| Unity 在异常后迟迟不发新 Layout | 设置有界 Layout epoch/时间诊断；不自行伪造 Layout，不忙等 |
| 调用点位于循环中导致身份冲突 | token + occurrence；真实重写程序集审计覆盖 |
| 误吞 MOD 自身不配对错误 | 精确异常分类 + 三次重建事务上限 + 无 pending 重复判据 |
| 恢复清理污染其它 MOD | 全状态按 MOD/session/generation/MVID 隔离，teardown 定向清除 |
| ABI 未升级导致设备继续使用旧 fence | 同步升级 rewrite、bridge、shim ABI，并校验 Release runtime DLL 哈希 |

## 12. 原始实施顺序

1. 先建立 JRP/JPOV 真实控制流的失败事件序列测试，证明当前 fence 在 commit Layout 后直接进入 Repaint。
2. 实现结构化事件类型和设置面 preflight，使宿主能在建立任何 GUILayout 组前跳过不安全事件。
3. 实现事务状态机与提交后重建屏障，让上述测试转绿。
4. 将 raw/host 控件身份升级为稳定 token，并补循环、消失控件和连续输入测试。
5. 扩展结构化恢复分类与三次重建事务上限，删除旧的“任意成功 Dispatch 即清零”行为。
6. 补 teardown、generation/MVID 隔离、pending 上限和零分配测试。
7. 升级全部相关 ABI，执行真实 JRP/JPOV/JPKV 重写审计、定向测试和 Android 托管 Release 构建。
8. 更新实现状态文档，明确本机验证范围和仍需用户执行的设备验收。

## 13. 原始完成定义

同时满足以下条件才算补全：

1. JRP `_creditsShown` 与 JPOV `_expandedAlign` 事件序列回归均通过。
2. 提交输入后不存在“旧 Layout + 新 Repaint”组合。
3. 所有支持的交互控件都使用稳定身份，生产重写审计无漏桥调用。
4. 两类已知暂态失配可恢复，真实不配对布局在三次重建后有界 Fault。
5. 多 MOD、多 generation、热加载/卸载测试无输入串位和状态泄漏。
6. 稳态零分配、pending 有界、无逐帧日志。
7. 受影响项目 Release 构建、代理生成和定向测试全部通过。
8. 文档明确记录本机测试结果；实机结果只由用户验收后补记。

## 14. 当前实现基线

截至 2026-08-25，以下设计项已经落入源码：

| 项目 | 当前状态 | 落点 |
| --- | --- | --- |
| 输入事务状态机 | 已完成 | `PcCompatManagedImGuiInteractionFence` 已实现 `Stable`、`InputPending`、`CommitLayout`、`AwaitingRebuildLayout`、`RebuildLayout`、`StableVerification`、`Recovering` |
| 提交后重建屏障 | 已完成 | 控件值在 Commit Layout 中交付后，后续非 Layout 事件不再进入 MOD；下一真实 Layout 完整重建后才恢复分派 |
| 稳定控件身份 | 已完成 | raw 控件使用 call-site token、occurrence 和控件种类；host 控件使用稳定 FNV token，不再依赖全局顺序游标 |
| 可恢复布局异常 | 已完成 | 精确识别 `Mismatched LayoutGroup.Ignore` 与 repaint 控件数失配；普通 `ExitGUIException`、业务异常和缺失代理成员不被吞掉 |
| 有界隔离 | 已完成 | 同一设置面连续三次重建失败才进入 `Faulted`；事务、pending 输入和恢复计数按 MOD/session/generation/MVID 隔离 |
| 热卸载清理 | 已完成 | `Disable()` 先调用 controller teardown，关闭设置面、释放 backend 并清理 transaction/fence，禁止旧 generation 输入泄漏到重载实例 |
| 缓存失效 | 已完成 | managed rewrite cache 为 `v65-imgui-style-fingerprint`，IMGUI bridge 为 `v19-style-fingerprint`，并包含 `PcCompatManagedSettingsTransaction.v1`；样式和动态文本指纹变化只在下一 Layout 边界失效 |
| 回归覆盖 | 已完成 | settings、Android 输入、托管 bridge rewrite 与 JRP/JPOV/JPKV 生产 UMM/JALib 重写审计组合回归为 `153/153`；controller 生命周期回归为 `51/51` |
| Android 托管 Release 构建 | 已完成 | `StArray.ModManager.Android.dll` 已由受影响项目的 Release 定向构建生成；不包含 APK 或 native SO 构建 |

两份现场报告中 `BeginHorizontal` 是 Unity 首次检测到组栈失配的位置，不代表横排代理本身缺失。共同的
因果链是：输入改变条件分支 -> 旧 Layout 仍在建树 -> 新状态进入后续事件 -> Unity 报布局缓存失配。
因此修复范围是通用事件事务和生命周期边界，而不是对 JRP/JPOV 添加特例。

## 15. 剩余补全计划

以下步骤按顺序执行。前三项是交付一致性与回归闭环，不应改动 MOD 源码，也不应触发顶层全量构建。

### 15.1 Release 产物与重写缓存核对

本机 Release 构建、SHA 核对和生产重写审计已完成：

| 产物 | 大小 | SHA-256 |
| --- | ---: | --- |
| `StArray.ModManager.Android.dll` | `607,744` | `25B4CF6139591E240B60E1FB31C7F3FFF2DB0F7BEC98C7115594D850F3220746` |
| `StArray.ModManager.dll` | `19,685,376` | `500E87B11CDC65E2F3A5A49DE3E2E72E887FB89B6A1B5B4768741012DA01A3BE` |
| `ModAssemblyRewriter.dll` | `280,576` | `07833AFB319580DC0A6EF82BC5F41C8B656789C25BC33E437AED23A9BBC0B0B5` |

真实 JRP/JPOV/JPKV 的 production rewrite audit 已包含在本轮 `153/153` 定向组合回归中；`v65` cache 和
`v19` bridge ABI 会拒绝旧的 managed rewrite 产物。响应式布局专项为 `26/26`，包含样式指纹、动态文本/
SelectionGrid 内容变化、64/65 节点边界和密集 128 控件 Repaint 微基准；稳态托管分配保持 `0 B`。

当前 `out/android_single/assets/runtime` 的 `222` 项 manifest 自身通过审计，root 为
`986a356eb4eaf7627fad9b795a4927d44c90cac722b5606f79a1a7e1db845f6f`，但其中 Android host 和
ModManager DLL 仍是旧批次，不能作为本轮交付物。runtime manifest root 被 native SO 嵌入并在启动时验证，
因此不得为了同步候选 DLL 而直接覆盖 runtime 或 Gradle assets。

尚待的交付动作只有一个原子步骤：在允许构建 native SO 时，通过既有 `build_android_single.ps1` 运行完整的
受保护子项目交付链，使 runtime 收集、manifest/header 生成、native SO 重建和 runtime audit 来自同一批次。
直接 Release `dotnet build` 只证明托管编译，不能产出可安装 runtime。

完成判据：最终 runtime、Gradle runtime assets、嵌入 manifest root 的 native SO 和三项托管 DLL 来自同一构建批次；
双目录 runtime audit 与启动配置验证均通过。

### 15.2 生命周期与并发回归补洞

本机自动化合同已补齐并通过：

1. `InputPending`、`AwaitingRebuildLayout`、`Recovering` 会直接 reset；真实 active `CommitLayout` frame 会在 bridge 关闭后再 reset，不能中途破坏 thread-static interaction scope。
2. `OnGUI` 内同步触发 `ReleaseForSessionTeardown()` 时，controller 会关闭 surface 并在回调返回后停止分派；同一调用不会死锁、不会再读取已经关闭的 surface。
3. reset 会清除 pending、epoch、occurrence 与恢复状态；同 token 的后续 generation 从 `Stable` 开始，不继承旧输入。
4. `Faulted` settings surface teardown 只回收本 surface 的 terminal transaction，不重复调用 MOD close；其它 MOD 的 fence 和响应式 session 仍由各自的 generation key 管理。

`PcCompatManagedSettingsControllerTests` 当前为 `51/51`。仍需用户在第 15.4 的热加载场景中确认 Unity IL2CPP 的真实
线程/触控时序，但本机不存在未覆盖的 controller/fence 生命周期分支。

### 15.3 真实语料与性能回归补洞

本机真实控制流语料和事务性能合同已补齐：

1. JRP credits 与 JPOV alignment 语料均断言 `CommitLayout -> AwaitingRebuildLayout -> RebuildLayout`，中间 Repaint 不进入 MOD。
2. Text 与 Slider 连续输入为 latest-write-wins；折叠区域消失的控件 pending 会在提交后丢弃；循环中的相同 call-site 由 occurrence 区分。
3. 稳态 `128` 轮 Layout/Repaint 仍为 `0 B` 分配，密集 128 控件 Repaint 微基准也保持零分配；pending 上限、最旧项淘汰、恢复和 teardown 均有确定性测试。
4. settings、Android 输入、托管 rewrite 和 production UMM/JALib audit 组合为 `153/153`；无新增逐帧日志。

端到端 IL2CPP 绘制时延、连续滑块手感和实际触控命中仍只能由第 15.4 的用户实机结果确认。

### 15.4 用户实机验收矩阵

本机无法替代 Unity IL2CPP 事件序列和触控行为，以下由用户验收并回填结果：

| 场景 | 操作 | 通过条件 |
| --- | --- | --- |
| JRP 设置面 | 展开/收起 credits，反复打开关闭 | 不再出现 `Mismatched LayoutGroup.Ignore`、不进入 `Faulted`、文字和按钮仍完整可见 |
| JPOV 设置面 | 展开/收起 alignment，点击选择项 | 不再出现组失配；选项状态稳定且不串到其它控件 |
| 连续输入 | 滑块拖动、文本编辑、快速连续点击 | 不死锁、不闪退，最终值为最后一次输入，期间没有无界卡顿或逐帧刷屏 |
| 热加载/卸载 | 打开设置后禁用、重载、再次打开 | 新实例可正常工作，旧设置面、输入或日志不影响新 generation |
| 多 MOD | JRP 与 JPOV/JPKV 交替打开设置面 | 一个 MOD 恢复、关闭或 Fault 不影响其它 MOD 的菜单、HUD 和游戏输入 |

验收日志至少应包含 runtime DLL 版本或 SHA-256、缓存 ABI、MOD id、generation、transaction state、epoch、pending 数量；
不要启用逐帧诊断作为常规运行配置。

## 16. 交付边界

本计划的代码与本机验证可标记为“本机实现完成、待受保护交付与实机验收”：

1. Release 候选 SHA、production rewrite audit 和 runtime 当前批次差异已留档。
2. 第 15.2 的 teardown/重载合同测试已补齐并通过。
3. 第 15.3 的真实语料与性能回归通过 `153/153`，且未破坏稳态零分配合同。
4. 未运行顶层全量构建、未生成 APK、未操作实机、ADB 或 native SO 构建。
5. 最终受保护 runtime 交付仍需在获准构建 native SO 时执行第 15.1 的原子构建链。

“完全验收”还必须包含第 15.4 的用户实机结果。若实机仍出现布局失配，先收集 transaction state、epoch 和
pending 诊断，再判断是事务状态机覆盖缺口、生命周期竞态还是第三方 MOD 原始 Begin/End 不配对；不得直接为某个
MOD 加入白名单或吞掉所有 `ExitGUIException`。
