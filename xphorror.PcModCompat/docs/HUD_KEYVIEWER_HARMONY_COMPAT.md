# HUD、KeyViewer 与 Harmony 兼容设计

## 1. 文档目的

本文记录 PC MOD 兼容层在以下三方面的目标、现状与后续架构：

- PC MOD 自行创建的 Unity HUD 如何迁移到 Android IL2CPP。
- KeyViewer 如何同时兼容实体键盘和 Android 多点触摸。
- Harmony API 当前实际支持到什么程度，以及后续如何落到 Native HookSlot。

本文只讨论兼容层通用能力。`JipperResourcePack` 是首个回归样本，不允许成为生产路径中的 MOD 身份特判。

> 2026-07-13 方向修订：最终目标从“兼容层解析 MOD 后统一代绘 HUD”调整为“重写后的 MOD 托管代码通过 Il2CppInterop 自行绘制，并优先使用 MOD 自带资源”。现有 Native UI graph 保留为优化、审计和显式 fallback，不再代表最终默认后端。

> 2026-07-16 所有权修订：普通 managed component 继续优先使用无注入 surrogate bridge；确实需要真实 IL2CPP `Component` 身份的类型，后续只能通过 ModManager 的受控 `InjectionTypeRegistry` 注册。ClassInjector 内部 detour、class/vtable 和 callback roots 均归 ModManager，MOD 与 PcCompat adapter 不拥有注入权。

> 2026-07-22 KeyViewer 修订：不同 MOD 的 KeyViewer 不按类型名或固定 Jipper 对象图适配。导入器把输入、lane、状态、展示和 rain 识别为声明式 `KeyViewer Adapter IR`；自动识别失败时允许用户补充角色绑定。每个 MOD 可包含多个 `KeyViewerFeature`，每个 feature 可包含多个 `LaneGroup`。Hook 继续由 HookBroker 统一实施，异步逻辑按每 MOD `ModActor` 串行调度，Unity 副作用仍只在 UnityMain 提交。

## 2. 已确认决策

### 2.1 兼容目标以最终结果为准

HUD 的最终兼容目标是：经过验证和重写后的 PC MOD 托管代码在 Android CoreCLR 中继续拥有 HUD 的创建、更新和销毁逻辑，并通过 Android Il2CppInterop 代理操作真实 IL2CPP Unity 对象。兼容层提供加载、重写、线程调度、Unity API 代理、Hook 事件和资源策略，不默认接管 MOD 的对象图与视觉实现。

不要求 PC 与 Android 的托管对象地址、Unity wrapper、线程实现或程序集二进制完全相同，但要求 MOD 源代码仍是 HUD 行为的直接执行者，而不是只作为兼容层生成近似 HUD 的输入材料。

验收重点是：

- MOD 自己的 Entry、HUD 生命周期和更新代码实际执行。
- MOD 自己创建 Unity HUD 对象，并优先使用 MOD 自带 prefab、字体、Sprite、Texture、Material 和其它资源。
- 最终显示内容与 PC 版本大致相同。
- 设置项由同一 MOD 逻辑驱动并产生大致相同的行为。
- 游戏状态、输入状态和 HUD 生命周期正确。
- 性能和稳定性优先于内部实现一致。

静态 UI graph、对象合并、批量渲染、事件驱动更新和受限状态机继续保留，但它们的定位是可证明的优化路径、审计路径和显式 fallback。未经 MOD/manifest 授权，不允许用兼容层近似代绘静默替换已经能够执行的 MOD HUD。

### 2.2 重写后的 MOD 执行，实际 Hook 保持 Native

最终边界如下：

```text
PC MOD DLL
  -> C# 后台 IL/metadata 分析
  -> UnityMain 运行时辅助探测
  -> dependency/member closure
  -> dnlib managed rewrite
  -> Android Il2CppInterop generated proxies
  -> per-MOD AssemblyLoadContext
  -> MOD Entry / HUD lifecycle / update code
  -> UnityMain 上通过 proxy 创建和更新 IL2CPP Unity 对象

Harmony / JAPatch / game callbacks
  -> 导入期 target 与 callback 扫描
  -> Native HookBroker permanent slot
  -> typed event / immutable snapshot
  -> UnityMain managed dispatcher
  -> MOD HUD callback 或生命周期对象
```

这里的“托管重写”不是 Roslyn，也不是把整个 MOD 重新编译成 Android 程序。`ModAssemblyRewriter` 修改已经验证的 Unity/游戏 API 引用、资源路径、Harmony/JALib/UMM 入口和必要的生命周期调用，使原程序集在 Android CoreCLR 中继续执行；代理实现再按完整 metadata identity 调用实际 Android IL2CPP API。

导入期分析继续复用当前已经使用的：

- `System.Reflection.Metadata`
- `PEReader`
- 受限 CFG 和调用图分析
- `PcCompatCallbackTranslator` 的 IL 解码、验证和降级思路
- `ProxySurfaceScanner`、`ProxyInputClosure` 和 generated proxy audit
- `ModAssemblyRewriter`

导入扫描不执行 MOD static constructor。只有依赖闭包、代理 surface、方法重写和资源策略全部通过后，才把重写产物原子发布到 managed rewrite cache；原始 DLL 始终保留且不原地修改。

`PcCompatUiRecipeCompiler` 仍可为可证明的静态 HUD 生成 `ui_recipe.bin`，但 recipe 不再是所有 MOD HUD 的默认最终后端。它只在以下情况使用：

- MOD 或 manifest 显式选择 recipe 优化。
- 静态分析证明 recipe 与原 HUD 可观察行为等价。
- managed self-render 不可用，而用户明确接受 `partial` fallback。
- 作为导入审计、行为对照或故障隔离工具。

用户在游戏内执行 MOD 导入时，IL2CPP domain、metadata、程序集和 Unity runtime 已经完成加载。因此导入编译允许在 `UnityMain` 执行受限 runtime probe，用于：

- 验证 Android IL2CPP 类型、方法和字段是否存在。
- 解析 runtime method pointer 和 ABI。
- 查询已经存在的 Unity singleton、组件和场景对象。
- 求值依赖实时 Unity 对象的受限 `TargetMethod()` 查询。

若目标对象只在后续场景出现，MOD 以 `pending target resolution` 状态完成导入；进入对应场景后继续解析，不立即判定为不支持。

### 2.3 Hook 热路径 Native 化，MOD HUD 运行在 UnityMain

C# 负责：

- MOD 导入。
- IL 和 metadata 分析。
- managed rewrite、代理闭包、recipe 生成、验证和缓存。
- 在独立 `AssemblyLoadContext` 中执行通过门禁的 MOD Entry、HUD 生命周期和设置逻辑。
- MOD 自带资源 API 的路径重映射和 session ownership。
- 设置 UI、导入进度和错误展示。

Native C++ 负责：

- runtime metadata 动态解析。
- Dobby 首层入口、HookBroker permanent slot 和同步 patch 语义。
- 输入事件、timer 和 completed snapshot 管理。
- 独立于 Unity 帧率的 KeyViewer/rain、计时和可证明解析式视觉逻辑。
- recipe fallback 的 Unity 对象创建、批量几何和 dirty property 提交。

运行期允许重写后的 MOD HUD 代码经过 CoreCLR，但必须遵守以下边界：

- 不从输入线程、音频线程、render/EGL 线程或未知游戏 Hook 线程直接调用 MOD 托管代码。
- Native Hook 只写有界 event/snapshot；展示型 callback 在 UnityMain managed dispatcher 中执行。
- 需要同步修改参数、返回值或决定是否跳过 original 的 patch，必须翻译为 Native fixed-op/Rule VM，或由明确支持的 UnityMain 同步 dispatcher 执行；不能延迟后伪装成等价。
- 每个 MOD 每个 Unity presentation opportunity 默认最多一次合并 managed update；事件可批量读取，不逐事件跨 CoreCLR。
- Unity API 只允许在 UnityMain 调用。后台任务可做纯托管计算和文件读取，最终 Unity 副作用必须回到 UnityMain。
- 不依赖硬编码 RVA/VA；所有 IL2CPP 调用仍按 symbol 与完整 metadata identity 解析。

### 2.4 KeyViewer 输入域分离

实体键盘和触摸不会伪装成同一种输入：

```text
Keyboard -> 精确 Android keycode / Unity KeyCode / Windows VK 映射
Touch    -> 独立虚拟触摸键格 T1..TN
```

已确认规则：

- 实体键盘尽可能精确映射到 MOD 配置的真实键格。
- 触摸始终具有独立的 `TouchLane.T1..TN` 身份，但不要求额外绘制一套并排面板。
- 触摸不伪装成 `Z`、`X`、`Space` 或 `Mouse0`。
- AUTO、oldAuto 和异步测试宏标记为 `Synthetic`，默认不计入 KeyViewer。
- 调试模式可以另行查看 synthetic 输入，但不得污染正常 KeyViewer 统计。

触摸布局使用独立移动端设置 `TouchKeyCount`，允许 `2 / 4 / 6 / 8 / 10`，默认 `10`。它不复用原 Key10/12/16/20 的输入数量。没有可用外接键盘时，兼容层可以复用 MOD 原键盘组的视觉工厂、模板、容器和动画，重建为 N 个触摸槽位；默认显示字符为 `T1..TN`，用户可覆盖显示字符。该覆盖只影响 Android presentation profile，不修改 MOD 持久化的 PC 键位配置，底层身份也始终保持 `TouchLane`。

触摸 DOWN 时按有效屏幕横坐标映射到 `T1..TN`；映射结果保持到对应 UP/CANCEL，移动过程中不换列。同一列允许多个触点并使用 held 引用计数，每个 DOWN 仍独立增加次数、KPS 和 rain。

KeyViewer 的输入来源保持语义差异，不统一伪装成虚拟键盘：

```text
PhysicalKeyboard / PhysicalMouse / PhysicalController
Touch
LogicalAction
GameplayAccepted
Synthetic
```

`UnityEngine.Input`、Win32 `GetAsyncKeyState`、Input System、Rewired、Harmony 游戏动作和自定义 managed event source 只负责转换到统一事件/查询 ABI。轮询型 MOD 通过 per-feature 虚拟查询上下文读取 `IsHeld/WasPressed/WasReleased`；同一 Unity 帧中的多个边沿按 `sequence/raw_ns` 逐项回放，不能只暴露最终 held 状态。API alias 在 canonical physical identity 层去重，但 MOD 明确配置的多个显示 lane 可以同时绑定同一个物理键并各自执行原计数语义。

移动端展示策略为 `Auto / Touch / External / Hybrid`。`Auto` 只把非虚拟、可用于当前 feature 的完整硬件键盘视为外接键盘，并用实际键盘事件兜底厂商错误标记；鼠标不会强制切换成键盘布局，手柄只有在 feature 存在 controller lane 时参与判断。策略在关卡会话开始时冻结，播放中设备变化不重排 HUD，用户显式设置覆盖 Auto。

### 2.5 高频雨滴允许批量渲染

KeyViewer 的静态按键格、标签和统计文本可以使用真实 Unity UI 对象。

雨滴、按下脉冲和其他高频图形不要求一项对应一个 `GameObject + Image`。允许使用一个或少量 `CanvasRenderer/Mesh` 进行批量渲染，以减少：

- Unity 对象数量。
- IL2CPP setter 调用。
- Canvas hierarchy rebuild。
- GCHandle 和生命周期管理成本。
- 高频输入下的主线程波动。

### 2.6 逻辑时间与显示提交解耦

Unity API 只能在 `UnityMain` 调用，但 HUD 的逻辑时间不得由 Unity 帧推进。

该边界参考现有异步输入实现：输入事件在 producer/input thread 中保留 `CLOCK_MONOTONIC raw_ns`，后续消费即使延迟到下一 Unity 帧，也使用事件发生时刻而不是消费帧时刻。

HUD 采用同样原则：

```text
input/runtime event(raw_ns)
  -> Native ingress
  -> event-time state transition
  -> immutable completed snapshot
  -> UnityMain managed dispatcher / recipe PresentationSink 读取 snapshot
  -> MOD 代码或批量后端按绝对 now_tick 计算当前视觉位置
  -> 一次提交 Unity UI/geometry
```

主线程卡顿期间无法产生新的 Unity 渲染帧，这是平台事实；但恢复后必须立即跳到正确的绝对时间位置，不允许按丢失帧逐步补动画。已经过期的纯视觉粒子可以直接淘汰，计数、KPS、held、timer 和 MOD 状态不得丢失或延后重算。

### 2.7 共享实时事件核心，HUD 独立执行

异步输入和 HUD 共享同一套 Native `RealtimeEventCore`：

- `CLOCK_MONOTONIC` 时钟。
- 原始 touch/key ingress。
- source、device、event tick 和 session generation。
- 有界 ring、barrier 和 completed snapshot 发布基础设施。

但 HUD 不在异步输入的 latency-critical worker 上执行。事件核心将数据 fan-out 给 gameplay replay consumer、独立 `HudLogicWorker` 和 UnityMain managed dispatcher。MOD self-render 只读取不可变批量 snapshot/event cursor；MOD 托管代码、HUD bytecode、timer、布局和 rain 计算都不得阻塞输入 seal 或官方判定 replay。

HookBroker 之上采用每 MOD 一个逻辑 `ModActor`，而不是默认每 MOD 一条常驻物理线程。每个 actor 拥有独立 mailbox、event cursor、deadline、adapter state、budget、fault state 和 presentation generation；同一 MOD 严格串行，不同 MOD 可由固定 Native worker 池并行调度。同步 Prefix、返回值、skip-original、`__state` 和 original continuation 仍在 Hook 调用线程完成，禁止向 actor 往返等待。只有经验证的纯 adapter bytecode/纯计算闭包可以使用专用 worker；Unity API、generated Unity proxy 和 MOD 实例的展示副作用仍只在 UnityMain 执行。

### 2.8 PC 源语义与 Android 目标后端分离

默认编译环境使用：

```text
SourceSemanticsProfile = Windows PC
TargetRuntimeProfile   = Android IL2CPP arm64-v8a
```

MOD 中的 Windows-only 功能分支仍按 PC 行为提取，再把 Win32 输入、路径、资源和 UI API 降级到 Android 能力。否则类似 `if (platform != Windows) return` 的代码会在分析阶段错误删除整个 KeyViewer。

若 MOD 明确提供经过验证的 Android 分支，可以通过 manifest 或审计结果覆盖默认源语义。

### 2.9 HUD 后端优先级与失败可见性

同一 HUD feature 的默认后端固定为 `ManagedSelfRender`。兼容绘制不是自动 fallback，必须由用户逐 feature 手动开启：

```text
ManagedSelfRender
  -> rewritten MOD code + generated proxies + MOD resources
  -> 默认启用

CompatibilityRender.ProvenRecipe
  -> 可证明等价的 ui_recipe/native executor
  -> 用户手动启用

CompatibilityRender.CompatibleFallback
  -> 用户或 manifest 接受的 partial 近似实现
  -> 用户手动启用并显示差异

Unsupported
  -> 明确报告，不创建静默替代 HUD
```

`ManagedSelfRender` 成功的判据不是“程序集加载成功”，而是 Entry、依赖、Unity API proxy、资源候选、生命周期 dispatcher 和异常隔离全部 ready。任一关键能力缺失时默认失败关闭并列出原因，不自动显示 recipe HUD。用户随后可以在 MOD 详情页明确启用兼容绘制；选择按 MOD/feature、目标 revision 和兼容报告持久化。self-render 内部经证明等价的 batch/lowering 优化不等于切换到兼容绘制，它仍由 MOD 的状态、资源和视觉语义驱动。

标准 telemetry HUD、诊断 HUD 和兼容层自身 UI 不属于 MOD self-render，可以继续由兼容层创建。MOD HUD 与这些系统 HUD 必须使用不同 owner/session/generation，避免卸载 MOD 时误删兼容层对象。

## 3. 当前实现状态

### 3.1 当前 Unity HUD

`PcCompatUnityHudBridge` 当前已经可以通过 dependency-closed generated proxies 创建和控制：

- `GameObject`
- `Canvas`
- `CanvasScaler`
- `RectTransform`
- `Image`
- `TextMeshProUGUI`
- 简单 progress bar

当前实现是兼容层拥有的标准 telemetry HUD，不是 MOD 自己绘制的 HUD，也不是最终通用兼容后端。它只负责提供已验证的移动端状态展示、行为基准和 fallback 观测面。

当前高频更新仍会经过 CoreCLR bridge。它可以作为 Native executor 迁移期间的行为参考，但不应成为最终通用 HUD 热路径。

Managed self-render 已有基础设施，但尚未达到生产完成：

- Android CoreCLR 在 ModManager UI 打开前启动。
- PC MOD 使用独立 adapter 和 managed rewrite cache，不按普通 Android plugin 直接加载。
- Il2CppInterop fork 已支持 runtime-metadata-only proxy、依赖闭包、surface scanner 和 proxy audit。
- `ModAssemblyRewriter` 已能改写较大范围的字段访问、方法调用和 blittable value type。
- rewritten oracle 可以显式加载重写后的 MOD、执行 setup 并审计生命周期。
- `PcCompatManagedLifecycleController` 已缓存 `CompatEnable/CompatUpdate/CompatDisable` delegate，提供单 MOD 重入拒绝、异常隔离、耗时统计和幂等清理；JALib 主 MOD 与 Feature 已进入同一 lifecycle。
- JALib `MainThread.Run` 已恢复 owner-scoped UnityMain 队列。以 Jipper 为例，`KeyInputListener` 只在线程中读取兼容输入并发布按键状态、计数和 rain 原始记录；按键颜色与 `AsyncText` 更新在下一次 `CompatUpdate` 开头排空，随后 `KeyViewerUpdater/RainManager.Update` 由 managed component bridge 推进。禁止监听线程直接调用 TMP/UI generated proxy。
- 主动诊断现在同时导出 `jalibMainThread` 队列计数和逐 managed component lifecycle 计数。Jipper 自绘验收必须看到 `KeyViewerUpdater` 与 `RainManager` 存在、处于 active，并且 `UpdateCount` 随 frame 增长；仅有 JAMod `managedLifecycleUpdateCount` 不足以证明 KeyViewer 生命周期正常。
- 对 MOD 自建轮询线程，主动诊断还导出 Feature 的 `enabled/hostActive`、`Thread.IsAlive/ThreadState`、最后一个有界 JALib 异常，以及 owner-scoped Legacy/Win32 query 的调用、identity 命中、true 和当前 consumer surface。`preview transitions > 0` 只证明 Adapter 收到输入；只有原查询计数与 MOD UI/计数任务同时推进，才能证明 self-render 的输入消费闭环。
- Native PresentationSink 已增加独立门禁的 UnityMain managed-frame callback；没有活跃 managed HUD 时不会逐帧跨入 CoreCLR。
- `ModAssemblyRewriter` v13 已按当前 revision 的静态 `ReversePatch` descriptor 改写 stand-in 的直接 `call` 调用点。返回类型必须一致；参数只能完全保留，或在零参 bridge 前显式 `pop` 全部源参数。`ldftn` PATCH 注册、无调用点 descriptor、反射和委托动态调用不会被误改写。
- Jipper 实际 DLL 已覆盖零参 bridge、保留 `LoadScene(string)` 参数、丢弃 `GetPlanetSpeed(controller)` 参数、同名异类型隔离和 ABI 不兼容失败关闭，共 5 条 IL 回归。
- v13 另有独立的 external call bridge，不复用 ReversePatch stand-in 语义。它按 source assembly/type/staticness/generic arity/return/ordered parameters 完整匹配，并支持从源 by-ref 参数闭合 bridge 泛型及审计过的 opaque handle type erasure；当前受控接管同步 `AssetBundle.LoadFromFile/LoadAsset/LoadAllAssets/Unload` 子集，以及 MOD-owned assembly catalog 内的 component API、透明 owner getter、Destroy 和 coroutine API。调用会转入 owner-scoped VirtualBundle 或 managed component registry；generated proxy 组件仍执行官方 IL2CPP 调用。
- 默认 dependency-closed proxy surface 已包含 `AssetBundleModule`、`IMGUIModule`、`InputLegacyModule`、`TextCoreFontEngineModule` 和 HUD/TMP/component/coroutine surface，当前闭包为 165 个精确输入类型、13 个 proxy 程序集、176 个生成类型，metadata/proxy audit 为 0 issue。rewriter 现在会拒绝缺失 Unity/游戏 proxy 的调用，不再静默落回 PC shim。
- managed rewrite 失败作为独立 capability error 保存；它不会阻止同一 MOD 继续编译和使用 verified recipe。只有显式启用 rewritten oracle 或 managed self-render 时，该错误才升级为加载失败。
- Hook 安装仍统一交给 Native HookBroker，不允许 managed loader 自行 Dobby hook。
- 每次 Bootstrap/Setup/Enable/Update/Disable 都带线程局部的 MOD id、resource session generation 和 lifecycle phase；每帧状态对象由 session 预分配，不产生 context allocation。
- 导入期只用 AssetsTools.NET 读取 PC/Linux bundle，并发布 Resource IR。运行时按 owner/current generation 注册 VirtualBundle；重写后的 `AssetBundle.LoadFromFile` 返回虚拟 handle，`LoadAsset`/`LoadAllAssets` 在 UnityMain 懒物化 Android 可用的 generated Unity proxy。桌面 bundle 本体不交给 Android Unity。
- 有资源的 rewritten self-render session 先进入 pending activation，只等待同一 resource generation 的 VirtualBundle session 发布；随后在 UnityMain 调用 `CompatEnable`，由 MOD 的重写后资源调用按需物化对象。该入口不检查 `STARRAY_PCMOD_RESOURCE_LOAD`，也不调用旧 Unity AssetBundle load sink。等待态最多 4Hz 跨入 CoreCLR，活跃且有 `CompatUpdate` 的 HUD 才逐帧 dispatch。
- managed Enable 成功后，Native 按 MOD 转交 presentation ownership：recipe lifecycle program 保留注册但停止产生命令，现有 recipe graph 在同一次 UnityMain opportunity 退休；Native Hook/event rules 不停。managed Update fault 或卸载时可恢复 recipe presentation。
- Unity IMGUI managed OnGUI 的 arm64 主路径借用一个稳定的真实 `GUIUtility.BeginGUI` host：gate 打开后锁定首个 `instance_id`，只在该实例每次 original `BeginGUI` 完成、`Event.current/GUIClip/GUILayout` 已建立时派发一次 owner-scoped MOD 菜单；宿主 250ms 不再出现才允许重选。手机实机证明 `BeginGUI` 持续命中而导出的 `ProcessEvent` 可为 0，因此 `ProcessEvent` 只保留可选遥测，不能作为派发前提。ModManager-owned injected `MonoBehaviour` 仍是后续目标；当前上游 ClassInjector 的内部函数发现依赖 Iced/x86-x64 xref，arm64 不尝试注册。Jipper KeyViewer 由 MOD 自身重写代码创建并经 managed component Update 驱动，不依赖兼容层近似。
- 自绘接管期间兼容层通用 HUD（telemetry 文本、T1..TN KeyViewer 行、进度条）按 presentation ownership 隐藏，ownership 归还（fault/disable/卸载）时自动恢复。自绘 MOD 的屏幕内容因此只剩 MOD 自己的对象，"MOD 绘制"与"兼容层代绘"可被真实区分，不再出现两者视觉重叠造成的假成功。
- 2026-07-21 实机审计曾确认唯一断点：游戏事件回调（JAPatch postfix）不派发到 managed MOD 代码——重写后 MOD 的 JAPatch 注册全部停在 `RegisteredOnly`,postfix IL 只被翻成 native fixed-op 更新兼容层 overlay 标量,Jipper `Overlay` 根 GameObject 的唯一激活路径 `Show(floor)` 与全部内容更新（`UpdateProgress/UpdateCombo/UpdateJudgement/UpdateAccuracy`）永不执行，实机"只剩 KeyViewer"。**该断点已于同日补齐**（三层落地，见下一条目）。
- 游戏事件回调 managed 派发已落地（§2.2 "typed event -> UnityMain managed dispatcher -> MOD HUD callback" 的首个实现）:
  1. **导入期**:`PcCompatRecipeCompiler` 只为仍需执行 MOD 托管行为的活跃 Postfix patch 增发 `ManagedEventCallback=21` 规则；签名取自同一 verified 领域目录，因此与 fixed-op 规则共享同一 target 记录、同一个 hook。rule id 编码 `managed_event:<patchId>:<callbackType>:<callbackMethod>`,patchId 按回调身份确定性排序分配。Prefix（含 `HideDebugText` 这类带 skip 语义的）不派发；descriptor-only callback 已由 native fixed-op 完整消费，也不再二次派发；目录外目标（`Awake_Rewind`、`RDC.set_auto`、`scrShowIfDebug.Awake/Update`、`UnityModManager.ModEntry.Load`）审计为 `managed_event.*` info 而不猜测。Jipper r143 当前生成 18 条 managed-event 规则；加入平台规则、完整 ResourceChanger 目录和 `OttoBlink` companion 后 recipe 总计 53 条规则 / 35 个 target。callback translation cache 与 recipe cache 已升级，旧缓存强制重编译。
  2. **native hook**:dispatcher 命中后把 `{patchId, instancePtr, raw args[0..5]}` 推入每 MOD 2048 槽有界环形队列。raw 槽由全部 12 个手写 dispatcher 透传填充（float/double 存位模式）；hook 线程原子读取安装期发布的 immutable per-dispatcher target snapshot，不持有 `g_lock`/`g_managed_events_lock`，不调用 Unity API 或 CoreCLR。事件仅在 MOD 持有 presentation ownership 时入队（`modmanager_pccompat_set_managed_events_enabled` 与 ownership 同一动作翻转），兼容渲染模式不派发、MOD uGUI 树保持 inactive。64 槽生命周期保留区防止资源初始化洪峰覆盖 Show/Hide；Show/Hide/Reset/StartLoadingScene 等 lifecycle boundary 还会清除其前方尚未消费的普通事件，再作为场景屏障入队，防止旧场景裸实例在对象销毁后继续派发。
  3. **managed 派发**:session 首次 drain 时按 `ui_recipe.bin` 的 managed-event 规则 + shim `JAPatcher` 注册表（现携带活 `MethodInfo`/delegate target）构建绑定表；`DispatchManagedFrame` 在 MOD Update 前按单帧预算 drain 队列。无参回调使用 typed `Action`，带参回调、proxy `(IntPtr)` 构造和 `___field` getter 在绑定期编译为 delegate，只有表达式编译不支持的异常形态回退 `MethodInfo.Invoke`。参数绑定形态：原始参数按名字（代理方法反射，缺名回退按位）匹配 raw 槽，int/bool/enum/float/double 位级转换，代理类型按 `(IntPtr)` 构造包壳；`__instance`（含 `object` 形态按目标代理包壳）;`___field` 走代理成员读（值类型字段与引用字段均可）;`System.Enum` 参数经 `modmanager_pccompat_read_boxed_value_info` 从 IL2CPP 装箱对象恢复类型名+值再 `Enum.ToObject`(`OnChangeState(States)` 路径）。逐事件异常隔离，单 patch 连续 8 次失败后退避 1 秒并允许试探恢复。descriptor 状态翻转为 `Supported` 供详情页显示。Jipper r143 保留 18 条 managed callback；ResourceChanger 的 `OnEditorStart/OnFloorStart/OnPlanetStart` 三条 descriptor-only callback 由 native fixed-op 完整执行，不再进入 MOD 托管回调。`Combo.OnHUDTextAwake`、`JStatus.HideDebugText/OnAutoChange`、`Status.OnShowIfDebugAwake` 暂留兼容缺口（目标无目录签名或 Prefix+返回值语义）。
- **目录外目标的签名来源已打通（2026-07-27）**——上面第 1 条里"目录外目标审计为 `managed_event.*` info 而不猜测"这条限制的根因不是策略保守，是**导入期拿不到游戏方法签名**：native `validate_method_identity` 严格要求 return type 与逐个 parameter type 精确匹配（这正是 hook 安装 fail-closed 的支点），而导入器只读 MOD 程序集。`PcCompatCallbackDomainMappings` 那张手工目录存在的唯一理由就是替每个受支持目标预存一份人工审计过的签名。
  - 现在的通用解法：**导入运行在 IL2CPP 已加载的游戏进程内，所以可以直接问运行时**。新增 native 导出 `modmanager_pccompat_resolve_target_signature`（纯 metadata 读——`class_from_name` + `class_get_methods` + `method_get_*`，不分配、不 invoke、不碰 GC，因此可在导入 worker 线程调用，不必回 UnityMain），把结果按 `assembly \n namespace \n type \n method \n static|instance \n returnType \n param...` 的换行记录写回，managed 侧无需 struct ABI。
  - managed 侧 `PcCompatTargetSignatureResolver` 采用与 `PcCompatManagedAssemblyRewrite` 同构的 provider 模式，由 `PcCompatAndroidTargetSignature.Install()` 在 Android host 注册。**未注册 provider 时导入行为与打通前逐字一致**（桌面与测试即此路径），因此这是纯增量、零回归。
  - 仍然 fail-closed 的四道闸：① 类型在多个已加载 image 中出现 → 歧义，拒绝；② 同名非泛型方法有多个重载且属性没写 argument list → 歧义，拒绝（错误信息直接告诉作者补 argument types）；③ 泛型方法直接滤掉（严格解析器本就拒绝）；④ 宿主答非所问（返回的 type/method 与请求不符、返回类型为空、arity 与属性声明的 argument list 不符）→ managed 侧一致性校验拒绝。provider 抛异常只降级该目标，不中断整个导入。
  - 通过后由 `PcCompatCallbackTranslator` 标 `Translated` + 挂 `ResolvedTarget`，再由 `PcCompatRecipeCompiler` 发 `managed_event:` 规则，`Source` 记为 `managed_event:runtime_resolved`（与目录来源的 `managed_event` 区分，审计可追溯）；规则形状与目录来源完全一致，native 解析器分辨不出、也不应分辨得出。
  - **Prefix 复用同一严格目标签名解析，但不进入延迟队列**：翻译器生成 `ManagedSynchronousPrefix=23`、`Stage=BeforeOriginal` 规则，由 HookBroker 在原调用线程同步反向进入 CoreCLR。`void` 继续，`bool false` 跳过 original；callback 缺失、线程不符或异常时 fail-open。
  - 覆盖：`PcCompatRuntimeResolvedTargetTests` 与 `PcCompatHarmonySynchronousPrefixTests`，含 native 导出 ↔ Android provider 的记录布局、隐藏 `MethodInfo*` 转发和 Prefix-before-original 源码合同。callback translation 格式版本为 `callback-translation-v8-editor-rabbit-writeback`。
- 同期修复：generated delegate 代理曾缺少 `(Object, IntPtr)` 构造，`DelegateSupport.ConvertDelegate` 在 `Application.quitting +=` 处抛 `MissingMethodException` 导致 Enable fault；已随代理闭包重生成修复。mod ALC 的代理程序集统一回落 Default ALC 共享，`Il2CppSystem.*` 全进程单类型身份，不存在跨 ALC 分裂。
- 2026-07-21（续）三轮实机诊断修复，shim 与本地真实 JALib 源码语义对齐（参考 clone 不入库）:
  1. **Feature 注册断链**:shim `Feature` 从不调自己的 `Patcher.Patch()`，子类 JAPatch 不进静态注册表（14× "no shim registration")。对齐真实语义：构造函数 `AddPatch(patchType)`、`Enable/CompatEnable` 先 Patch 后 OnEnable、`patched` 改属性、`Patch()` 幂等、patched 后晚到 `AddPatch` 立即注册、`Unpatch/Dispose/OnFailPatch`、`JAPatchAttribute` 补 `MethodBase` 构造、0Harmony 补 `GeneralExtensions`。
  2. **ByteTool 缺失**：真实 JALib `Tools/ByteTool/` 11 文件近原样移入 shim（StreamTool/ByteTools + 9 attribute)，连带 `JAMod.Name/GetMods`、`SimpleReflect.Method/Constructor/Members/New`、JetBrains.Annotations 最小桩；最初使用 `JAPatcher.RegisteredPatchCount` + 60 帧节流收编晚启用 Feature。该机制后来被 registry revision 当帧检测取代，见当前行为边界。
  3. **HitMarginsCount 恒空/冻结**：重写器已把 `VersionSafe.GetHitMarginsCount()` 调用点重定向到桥（桩本体仍抛异常，只查桩会误判），但早期桥没有可靠发布者，且 generated proxy 反射镜像会盯住被 `SetPlayerCount` 替换的旧 tracker。当前桥在 MOD 加载前建立 12 槽稳定数组，公开后禁止换身份；native 经 metadata 动态解析 `scrMistakesManager.marginTrackers[0] -> scrMarginTracker.hitMarginsCount`，批量发布 ABI v1 snapshot。`AddHit/Reset/CalculatePercentAcc`、tracker 整体替换和 checkpoint 原地回写统一由 generation/checksum 捕获；帧首只在 generation 变化时复制到稳定数组，随后才派发 MOD callback。该链路不推进官方 HUD 或判定状态。
  - 实机数据：绑定 5→7→18，帧派发 3→169→226,overlay 判定行显示；3.1.2 `HitMargin` 12 值核对无槽位错位（`OverPress=11` 追加末尾）。普通单人判定计数行按 Jipper 源码本来就是固定锚点，不跟随星球；内容冻结、重试不清零和编辑器播放显示仍需使用 2026-07-22 构建复验。诊断导出已增加每个 callback 的 `ok/failed/streak/fused`，用于区分事件未产生、绑定缺失与回调内部异常。
  4. **capability 缓存 fake-null**：缓存代理的 GCHandle 只能保证 IL2CPP GC 目标，不保证 Unity 显式销毁后对象仍有效。registry 现在只在 UnityMain 返回资产，并先调用 generated `UnityEngine.Object.op_Implicit`；失效 stable-ID 从缓存移除后通过现有有界队列单项重载，字体/材质 clone 不再接收陈旧代理。

上述链路当前仍是 debug/test path，代码中的 `STARRAY_PCMOD_COMPAT_SELF_RENDER` 默认关闭，具有 recipe 的 MOD 仍先使用兼容绘制。这是尚未迁移的当前实现，不是最终策略。切换发布默认前必须先实现持久化的逐 feature 后端选择、self-render readiness/failure UI 和“失败时不自动 recipe fallback”门禁。owner identity、同步泛型/非泛型 VirtualBundle API、pending Enable 和 managed/recipe 去重已经接通并通过本机回归；实机验证已于 2026-07-21 开始：KeyViewer 自绘、managed OnGUI、presentation ownership 转移和兼容 HUD 抑制通过；游戏事件回调 managed 派发（JAPatch postfix -> native 事件队列 -> UnityMain MOD 回调）已实现并通过本机回归（当前 49 规则/31 target、18 条 managed-event、bin 往返校验），实机复验大关卡进入/退出/重入时无 callback backlog 和旧对象访问是下一步。反射调用、`LoadFromFileAsync`、跨 MOD 共享 bundle、任意 prefab/shader 和长期异常隔离仍未覆盖。因此仍不能把 managed self-render 标为生产支持。

为便于实机验收，MOD 详情页提供“启动托管自绘测试”。该动作只接受已经使用 rewritten assembly 且仍处于 `Loaded` 的 session，不改变发布默认值。点击动作只授权当前 rewritten session 进入 VirtualBundle/Resource IR 自绘链路；它不打开 `STARRAY_PCMOD_RESOURCE_LOAD`、不授权旧直载候选，也不把 PC/Linux bundle 交给 Unity。

当前受控资源链的已完成边界：

1. `LoadFromFile(string)` 不再执行 Unity `AssetBundle.LoadFromFile*`，而是按当前 owner、resource generation 和源路径取得 VirtualBundle handle。
2. Jipper 的同步泛型/非泛型 `LoadAsset`、`LoadAllAssets` 经 managed bridge 查询 Resource IR，并返回 capability clone 或运行时重建的 generated proxy；`Il2CppReferenceArray<T> -> T[]` 转换仍由 rewriter 处理。
3. `Unload(bool)` 只释放 MOD 持有的虚拟 handle；session 释放时按 Resource IR 依赖拓扑销毁该 owner 的物化对象，不存在桌面 bundle 的真实 `AssetBundle.Unload(true)`。
4. `ControlledLoad` 和 `ForceRequired` 只属于旧 debug/audit 直载支线，不能作为 rewritten self-render 的激活门禁或 fallback。
5. 实机验收目标是证明字体、prefab、sprite、texture 和 material 已从 Resource IR 正确重建并被 MOD 自绘代码消费，而不是证明 Android Unity 能读取 Linux bundle。

Native 通用路径现已补上第一版 UnityMain object graph executor：

- `ui_recipe.bin` 的 object graph/component operation 会进入有界 native registry。
- lifecycle VM 发布 `EnsureGraph` 后，PresentationSink 在 UnityMain 创建 `GameObject + RectTransform` 层级。
- 第一版组件支持 `Canvas`、`CanvasScaler`、`Image`、`RawImage`、`TextMeshProUGUI`、`CanvasRenderer`、`ContentSizeFitter`。
- 初始化支持显隐、Rect/anchor/pivot/scale、Canvas 参数、颜色、raycast、静态文本、字号、对齐、rich text、line spacing 和 fit mode。
- GameObject 使用 GCHandle rooting；clear/discard 只标记 retired，由下一次 UnityMain opportunity 执行 `Destroy` 和释放 handle。
- Unity fake-null 通过 metadata 解析的 `UnityEngine.Object.m_CachedPtr` 检查；场景销毁后命令到达时会重建整个 graph。

这条 native 路径已经脱离 CoreCLR 热路径。普通 translator 现在可以从 manifest 生命周期入口可达的受限 IL 中生成基础 object graph、resource binding 和 lifecycle；JipperResourcePack r143 已作为回归样本实际生成 16 个节点、73 条 component operation、13 条 `TextFont` binding 和 2 个 lifecycle program。该结果仍是 `partial`：动态文本、动态 prefab、KeyViewer/SideImage 构造和自定义组件不会被猜测执行。

按照最终目标，这组 Jipper graph 数据证明的是 recipe fallback/优化后端可工作，不证明 Jipper 已经由自己的代码绘制 HUD。下一主线是让其重写后的 HUD 创建与更新方法在 UnityMain managed dispatcher 中真实执行，并通过 generated proxy 直接创建上述 Unity 对象、加载自己的资源；只有该链路通过实机生命周期和性能验证后，才能标记 `ManagedSelfRender` ready。

### 3.2 当前 KeyViewer

Native 已有：

- Android MotionEvent DOWN/UP/CANCEL 观测。
- 2026-07-22 已接入双 producer raw 路由：`common/async_input_observer_abi.h` 定义 64-byte touch/key POD ABI；ModManager 通过 `RTLD_NOLOAD + dlsym` 向已加载 AsyncInput 注册回调。AsyncInput enabled 时在 capture/test-macro/gameplay gate 前发布 native snapshot，Activity 观察因 producer 不匹配被拒绝；disabled/absent 时 Activity pre-super dispatch 是唯一 producer。
- RealtimeEventCore 已记录 `InputProducer/producerEpoch`。Async/Official 切换时对旧 touch/key held 发布 CANCEL，再发布 `ProducerChanged`；sequence 和累计诊断值不回绕。Async snapshot 已补齐 pointer count/source/device/flags 与 key scan/meta/device/repeat/source/flags，virtual keyboard 在 bridge 拒绝。
- 多点触摸 held slot。
- 最近 DOWN/UP。
- 总次数。
- 一秒窗口 KPS。
- HUD bulk snapshot。
- 独立 `RealtimeEventCore` 基础实现：固定 8192 条 raw event journal、固定 512 条 KPS 时间窗、monotonic `raw_ns`、事件 sequence/cursor 和 dropped 计数。journal 只承担逐边沿回放、rain 和动画输入，不再承担累计事实的唯一保存责任；读取按 sequence 直接计算首项，不随 journal 容量线性扫描。
- `raw input event ABI v1` 已固定为 32-byte `PcCompatRawInputReadV1` 与 88-byte `PcCompatRawInputEventV1`，单次最多读取 256 条。普通 cursor 表示“最后已消费 sequence”；返回的 `droppedBeforeCursor > 0` 是不可恢复的运行期 gap。保留 cursor `UINT64_MAX/ulong.MaxValue` 只用于开户：原子返回当前 ring 尾 sequence 且不回放历史，因此 MOD 启用前的历史覆盖不会被误报为运行期丢失。
- Touch event 已保存 pointer slot、pointer count、DOWN 坐标和状态 generation，可供后续 `T1..TN` 与 rain consumer 使用。
- Activity/Async producer 同时传入有效 viewport 尺寸；RealtimeEventCore 在接受事件的同一临界区生成 `TouchKeyCount=2/4/6/8/10` 五套累计 checkpoint。同列多指使用 held 引用计数，contact 到 lane 的绑定保持到 UP/CANCEL。HudLogicWorker 直接读取 checkpoint，不再通过可能覆盖的 raw journal 重建累计值。
- 实体键盘已通过 Activity 旁路观察进入同一 raw journal，保留 Android `keyCode`、`scanCode`、`metaState`、`deviceId`、`repeatCount`、event flags 和原始事件时间。
- 键盘 held 使用固定 64 槽跟踪；repeat DOWN 保留为审计事件，但不重复增加总次数或 KPS。
- producer-side count checkpoint 使用 64 位 lifetime/session 总数、五套触摸 lane 计数和固定 256 项 open-addressed canonical key identity 表。`begin_session` 只清 session 计数和 held，不回绕 lifetime；identity 表满时递增显式 overflow fault counter，禁止静默合并身份。即使 consumer 落后超过 8192 条，raw count checkpoint 仍不丢 DOWN；过期逐事件 rain/动画可以淘汰。
- ModManager 文本输入激活时以及 Android virtual keyboard 产生的 KeyEvent 不进入 gameplay KeyViewer；原游戏 `dispatchKeyEvent` 消费链不变。
- 现有 overlay ABI v3 改为读取 `RealtimeEventCore` snapshot，字段布局和上层读取协议未变化。
- 独立 `HudLogicWorker` 已实现：使用 event cursor 消费共享 ring，按 KPS deadline 条件变量唤醒，不做固定频率轮询。
- Worker 已发布三槽 completed input snapshot history；UnityMain 使用 `try_lock` 非阻塞读取，worker 未追平或锁竞争时立即回退 producer snapshot。
- Worker 在 Native hook coordinator 启动时预启动，首次玩家输入和首次 overlay publish 仍有幂等启动兜底。
- 当前通用诊断 HUD 在关卡开始发布显式 session boundary，不回绕 event sequence，并清空自身 held、KPS 和 session total；completed snapshot 保存 session generation 与 monotonic session anchor。该计数只是兼容层诊断 projection，不能作为 MOD KeyViewer 的次数/KPS 真值，也不能驱动 Jipper 等 MOD 的显示状态。
- Native 通过独立版本化 `input HUD snapshot v1` 暴露 projection，不扩写稳定的 overlay ABI v3。每个 MOD 可按自己的移动端设置读取不同 lane 数，互不覆盖。
- `PcCompatMobileSettings.TouchKeyCount` 已接入 `2/4/6/8/10` 菜单和持久化；当前通用 HUD 文本使用 `T1..TN` lane mask、KPS 和总次数。ModManager 全局设置另提供 `ScreenRegions/TouchContacts` 两种分配模式：前者保持按横坐标等分屏幕；后者忽略坐标，将同时存在的 Android contact 分配到不同 `T1..TN` lane。为避免 Android 在高速单指连点时反复复用 contact slot 0，`TouchContacts` 对刚抬起的展示 lane 使用默认 `80 ms` 冷却，用户可在设置页调整为 `0..500 ms`；新 DOWN 优先选择未 held 且未冷却的 lane，全部冷却时选择最早释放的空闲 lane，因此不延迟也不丢弃原始事件。该门限在 managed consumer 和 Native checkpoint 中同构执行，并与模式切换一样有序热更新；它不参与 `ScreenRegions`、游戏触摸消费或判定。
- UnityMain telemetry 已按 metadata identity 可选解析 `UnityEngine.Time.time/timeScale/frameCount`，并与 song position、AudioSource time、map position、monotonic tick 一起发布三槽 completed clock-anchor history。
- clock anchor 通过独立 `clock anchor snapshot v1` 提供给 Managed/后续 Native VM；HudLogicWorker 不调用 Unity API，读取端使用 `try_lock`，失败保留上一份完整锚点。
- Native register-like rule VM 执行核已落地：`r0-r31`、`f0-f15`、`b0-b15`，支持整数/浮点运算、条件、`BR/BR_IF` 运行时循环、输入/Realtime/Unity/song/audio/map 只读指令和显式 `Return`。
- KeyViewer domain 指令已可按 recipe 的 `TouchKeyCount` 读取 lane held mask、单 lane held 引用数与累计次数；运行时 lane 索引越界会生成可观测 VM fault。
- VM 每次执行具有 instruction budget，故障写入固定 64 条 fault ring；同一 rule 累计 3 次 fault 后本 session 禁用。异常以 `StArray.RuleVM` 输出到 Logcat，并通过 `VM fault snapshot v1` 进入诊断 UI/导出报告。当前 VM 不做动态分配、不调用 Managed/Unity API，并已由 recipe lifecycle scheduler 调度。
- immutable program 按 instruction pointer/count 只 verifier 一次；输入或目标 clock domain 尚未发布、或 session generation 不匹配时返回 `Deferred` 冻结规则，不计 fault、不触发熔断。

`GameplayAccepted` 已接通。fixed dispatcher 新增精确 `InstanceBoolBoolInt`，不会复用参数顺序相反的 `InstanceBool2`；它原样转发隐藏 `MethodInfo*`、捕获 original bool result，并由 `GameplayAcceptedObserve=22` 只发布成功返回。普通输入写入独立 accepted ring 的 `GameAction Down/Up`，`isAuto=true` 写为 `Synthetic`；异步测试宏通过注册时缓存的 `ADOFAIAsyncInput_IsTestMacroEnabled` 识别，因为宏启用期间玩家输入门禁关闭，所以不会与真实输入混淆。两类 accepted 事件均不修改 raw physical held/KPS/total。当前 8192 raw journal 与 2048 accepted ring 仍允许覆盖最旧事件，不满足最终 MOD count 零丢失持久化承诺。

fixed dispatcher 已迁移为按 staging 批次增长的 AArch64 thunk arena，不再声明 `kMaxDispatcherSlots`，也不再为每种 ABI 静态生成 0..N detour 表。HookBroker 每次计划按 `required = distinct(permanentlyBoundTargetKeys union installableStagedTargetKeys)` 计算总数；同一完整 metadata 目标签名无论被多少 MOD 或 rule 引用都只计一次，已经永久绑定但当前禁用的目标仍计入。所有 installable staging target 先完成 ABI/op 验证，再一次性分配完整 runtime/thunk 批次；分配失败时整批新目标失败关闭，任何目标都不会提前进入物理安装。

每个批次拥有稳定 `DispatcherRuntimePage` 和独立匿名代码页。thunk 固定 64 字节，以 `BTI c` 开头，按 ABI 将原始 GP 参数整体右移一位、保留 FP/SIMD 参数寄存器不变，把 32 位 dispatcher index 写入 `w0`，再经 `x16` 尾跳到对应公共 dispatcher。代码页只经历 `RW -> clear_cache -> RX`，从不建立 RWX 映射。runtime page、thunk、slot id 和 original trampoline 发布后均保留到进程退出；关闭/清理 MOD 只清 rule mask 和 managed snapshot，不 unhook、不回收地址。

计划和持续诊断现同时导出 `required/capacity/bound/ready/blocked`，并附带本轮 `new/allocated/remaining`。`capacity` 是已成功发布的动态 thunk 数，不再是构建常量；`bound == capacity` 在没有 blocked 且 `capacity >= required` 时是健康状态。metadata 仍只解析目标身份，全部物理安装继续由 ModManager/HookBroker 独占执行。

`keyviewer_adapter.json` 的 v1 managed schema、校验器和首版行为扫描器已经落地：覆盖多 feature/lane-group、source profile、lane binding、角色、独立 visibility/inputActivation、MOD-owned count/KPS/reset/persistence 以及逐子能力证据。扫描器复用 metadata/IL decoder，从精确 Legacy Input、真实 `GetAsyncKeyState` P/Invoke、局部调用图、字段 writer-reader、循环、单调时钟、队列、文件 IO、Unity/TMP sink 和 lifecycle 建立候选连通图；不以 `KeyViewer` 类型名或 Jipper 身份作为 feature seed。序列化按稳定 id 排序；缓存绑定行为包 SHA-256、程序集 SHA-256/MVID、游戏 revision 和 generated proxy surface hash。

Android managed rewrite cache v15 会原子发布 `keyviewer_adapter.json`、`keyviewer_adapter_issues.json` 和对应 SHA-256 manifest；cache hit 只校验哈希与 schema，不重新扫描整个 MOD IL。ModManager 兼容诊断页按 feature 展示每项状态、首个证明断点和证据 tooltip，诊断导出也记录同一结果。扫描器把精确 Legacy/Win32 和 Rewired button polling 标为可消费候选；Input System Button/KeyControl 只作为精确但未闭合的候选。v5 analyzer 在 indexed-array 证明上支持 `KeyCode < 0x1000 ? Unity : Win32(value - 0x1000)`、Unity KeyCode、VK 直通和固定 VK offset 结构化 `IdentityTransform`，并按循环起点恢复复用 local 的最近 provider 赋值，同时发布需确认的 `LabelProvider`。Input System 整链 lowering、跨 helper provider selection、一般 dominance/alias、线程 happens-before、KPS expiry、rain ownership、visibility/inputActivation、reset 和 persistence scheduling 仍未闭合，因此只有闭合子能力及用户已确认 provider 可进入受限 lowerer。

手动角色绑定的首版控制面已经实现。ModManager 只能从 Adapter 已发布的 role candidate 中确认成员，并把结果原子保存为 MOD 私有 `.pccompat/keyviewer_overrides.json`。文件绑定 `modId`、行为包 SHA-256、目标游戏 revision、proxy surface hash 以及完整程序集 SHA-256/MVID 集；任一不一致或候选不在扫描结果中时整份 override 失败关闭。确认 `Probable/Ambiguous` 只保存选择，不修改原 evidence，也不使 `IsCoreReady` 变为 true。诊断导出记录当前 AsyncInput/OfficialActivity producer、override 状态、输入模式、Touch lane 数和已确认角色。

运行时已有 preview 与正式 consumer 两层。`PcCompatKeyViewerInputProjector` 继续提供 snapshot 级 `TouchLane` mask；`PcCompatKeyViewerPreviewRuntime` 使用有序 raw event ABI，为每个启用且指纹有效的 override 保存独立 cursor、session generation、producer epoch、held mask 和最后 transition。Touch 可按 DOWN 横坐标或活动 contact slot 生成真实 `TouchLane:T1..TN`；模式切换作为每 MOD actor mailbox 中的有序控制项执行，先释放旧 touch held，保留 external held，再切换映射，禁止让切换前已排队事件被新规则重解释。Android keyboard 只对明确表内 keyCode 生成 Unity KeyCode/Windows VK canonical identity，未知键保留 source/code unmapped 诊断，不按 scanCode 猜测。

单个后台 wake 线程等待 RealtimeEventCore native condition variable，事件到达后只提交一次可合并 pump；UnityMain frame dispatcher 保留为兜底。actor batch in-flight 时等待共享 `AutoResetEvent`，完成、拒绝、fault 和 mapping marker 都显式唤醒，不使用 1ms sleep 轮询。native read 与纯输入推进在固定 2-worker 的共享 `PcCompatModActorRuntime` 上运行；每个 MOD 是一个串行 mailbox，不是一个物理线程，不同 MOD可并行，同一 MOD不重入。空 batch 不投递；mailbox 默认硬上限 256，单 turn 同时受 64 项和 4ms cooperative slice 限制，满队列直接拒绝并让对应 feature 失败关闭，不阻塞输入生产者。actor 失败、journal gap、session/producer 协议断裂均按 MOD失败关闭。8192 槽 journal + native wake 覆盖普通 UnityMain stall，但仍是有界结构，不宣称无限期零丢失。

`PcCompatKeyViewerConsumerRuntime` 发布 per-MOD immutable key map。完整 Proven 静态 lane 可直接生成 consumer；动态 lane 只有在包/程序集/revision/proxy 指纹、确认的 `BindingProvider` 和 Proven `IdentityTransform` 同时匹配后，才会在 managed session 中读取 `KeyCode[]` 并发布逐 lane canonical plan。actor 只推进 held 和 DOWN/UP ordinal，原 MOD通过重写后的 polling call 在自己的状态机中消费，因此 MOD 原 count/KPS/rain/reset/persistence 规则不被兼容层统一计数器替代。输入 callsite 内嵌 owner ID，支持 Jipper 自建 listener thread；Touch-only 不调用 native keyboard bridge，Hybrid 合并 Android keyboard canonical Unity/VK 身份。跨 feature 的同一 raw DOWN 对 `anyKeyDown` 只发布一个 ordinal，避免 alias/多 feature 造成重复边沿。

动态 provider 返回的 `KeyCode.None/VK 0` 不可成为 consumer identity。已确认 provider 无法覆盖 lane 或只返回 None 时，lowerer 会尝试 Adapter 中其他 provider；只有恰好一个候选能覆盖全部 lane 才恢复，并在诊断中记录原 provider 和恢复目标，多候选仍失败关闭。不同显示 lane 合法地可以绑定同一非零物理 identity，不因重复键被拒绝。

preview pump 与 ModManager 窗口、HUD 可见性和 managed self-render lifecycle 解耦。当前复用常驻 metadata-resolved UnityMain frame hook 的总需求 gate：同一帧新注册项共享一次 tail-open，具有相同 cursor 的多个 MOD 共享一次 native read，再广播到各自状态，不创建每 MOD 忙轮询线程。它严格检查连续 sequence、`Reset`/session generation、`ProducerChanged`/producer epoch 和单调 state generation；运行期 ring overflow 或协议断裂后清空 held、停止该 MOD 的 preview 并在 UI/诊断导出报告 fault，禁止猜测补齐。

preview 仍保持 observe-only；正式 consumer 是并行的 owner-scoped query surface，不会写游戏输入或判定，也不会用兼容层统一计数器覆盖 MOD 状态。已经闭合 Proven 静态 lane、Jipper 形态的动态 BindingProvider/identity transform、actor 串行推进、原 MOD polling 消费、跨线程 owner、Android keyboard canonical mapper 和独立 native wake。`Auto` 使用 session 起点外接设备快照冻结为 Touch/External，热插拔只影响下一 session。确认的 `LabelProvider` 由 UnityMain 低频投影器按实际冻结模式同步：Touch 临时写入 `T1..TN`，External/Hybrid 恢复 MOD 原始 label 值，让 MOD 自己的 configured-key formatter 保持权威；只接管空白项，MOD 运行时改写的自定义文本立即脱离接管，配置刷新和卸载同样恢复原值。旧版遗留的精确 `Tn` 在 External 首次同步时清为空值。Jipper 结构匹配时调用其 `UpdateKeyText(Key,int)` 立即刷新已创建键帽。槽位/count/KPS/rain 继续由 MOD 自绘；用户显式启用 `CompatibleFallback` 时才由通用槽位和单 Mesh rain 代绘。尚未闭合的是更广 Input System/Rewired API、未证明动态 transform、一般 lane factory 和实机压力验收。

正常用户路径不再要求理解 Adapter 内部角色。没有已有 override 文件时，导入器只对 `input=Proven`、恰好一个 proven identity transform 且存在 BindingProvider 候选的 feature 创建推荐配置，默认 `Enabled + Auto + TouchKeyCount=10 + self-render`；运行期若恰好一个 BindingProvider 能覆盖全部槽位则自动 lower，零个或多个仍失败关闭。唯一 `LabelProvider` 可直接用于 `T1..TN` 默认标签，不要求手动确认。ModManager 的“MOD 设置”页提供启用、输入模式、触摸槽位和显式兼容代绘并立即保存应用；角色候选只留在折叠的高级诊断中。已有用户配置不会被自动覆盖，恢复推荐配置由用户显式执行。

自动 lower 的可信边界止于 plan 内容，不依赖 UI 勾选动作。registry 会再次验证完整 Adapter/override 指纹、provider 必须属于 scanner 候选集、lane 必须与 TouchKeyCount 完全一致且索引连续、identity 只能是受支持的 UnityKeyCode/Windows VK；通过后资格统一为 `VerifiedLoweredBinding`。显式 External 不注册 Touch consumer，但仍生成 presentation-only plan，供 MOD 自绘标签和 fallback 使用同一 configured-key 映射。兼容代绘只为 active consumer 建立 frame，不能在 consumer 未闭合时显示静态假按键；空 rain 使用一个零面积退化 quad 清空/保持 Mesh，避免部分 Android Unity 构建拒绝零长度顶点/索引提交。renderer 异常会写入诊断导出的完整 `fallbackRendererError` 块。

兼容代绘 fallback 是手动路径，但仍按低分配热路径实现：注册变化时发布排序后的 immutable dispatch snapshot；UnityMain 通过 preview 的窄读接口按 MOD 一次加锁批量复制全部 feature 状态，复用 frame、labels、counts、最多 256 项 rain list 和 bridge quad list，不构造完整诊断 snapshot，不扫描和排序 `ConcurrentDictionary`。fallback buffer 携带实际 `InputMode`，仅在模式变化时原地把同一 labels 数组切换为 `T1..TN` 或 ASCII configured-key 名称。fallback 时钟走只含 `ProviderAvailable/rawNs` 的值类型 provider，不构造完整 clock 对象。visual 用 generation 标记回收，静态布局和 lane 标签只在变化时写 Unity。单 Mesh rain 使用 1/4/16/64/256 quad 分桶的常驻 IL2CPP 顶点/索引数组；每帧只原位更新顶点，同一 bucket 不重复提交 triangles。该 transient frame 只允许 renderer 在同步 callback 内消费，不得跨帧保留引用；稳定态 128 帧托管分配测试要求 0 bytes。

presentation 后端切换必须是互斥事务。请求 `ManagedSelfRender` 时先发布 `ActivationPending`，再注销同 MOD 的 `CompatibleFallback`，并在整个 `ActivationPending` 与 `ManagedPresentationClaimed` 区间禁止 Unity HUD/fallback 重新注册；这样 fallback demand callback 观察到的 frame gate 始终至少为 pending，不会在同一 GUI callback 内短暂关停再重启。UnityMain 下一次 opportunity 先用空 fallback frame 清理旧对象，再执行 managed activation。不能只在 self-render 成功后隐藏 fallback，否则设置刷新可在 activation 窗口把旧后端重新注册，形成双绘、对象生命周期冲突或启动异常。

物理 PresentationSink Hook 只能由 native coordinator 在非 Unity callback 的后台线程安装。managed frame gate、ModManager UI、IMGUI/Canvas callback、VirtualBundle scheduler 和 `CompatEnable` 只能检查 installed 状态并提交异步安装请求；请求入口本身只允许原子发布、创建/唤醒 coordinator 线程，不得注册 HUD worker、AsyncInput observer 或初始化其他子系统，禁止同步调用 metadata resolver、HookBroker 或 Dobby。未完成请求按 pending bit 合并，不能反复递增 generation 打断 coordinator 自己的 500ms 重试。该约束避免用户点击“启动托管自绘测试”时在当前渲染调用栈修改同一入口或等待无关初始化而造成无异常卡死。按钮请求也不触发 `RegistryChanged`，因为 activation 不改变 rule/patch registry。

producer-side count checkpoint 是 raw 输入事实恢复层，不是最终 MOD count journal。它可以在 ring 覆盖后准确补回 physical DOWN 和 touch lane 累计，但不能自行还原某个 MOD 的 ghost 过滤、activation predicate、KPS 时间窗、reset 或持久化时序；这些仍必须由 Adapter IR 的原状态机 lowering/回放负责。accepted ring 也尚未建立同类累计 checkpoint。因此最终“MOD count 零丢失”仍为 partial。

Legacy polling runtime bridge 已接通。导入器把 MOD 中 `UnityEngine.Input.GetKey/GetKeyDown/GetKeyUp/anyKeyDown` 改写到 `PcCompatLegacyInputBridge`，不再让 background listener 调 generated IL2CPP Input proxy。held 查询读取 native 512-key snapshot；边沿查询携带由 assembly MVID、method、IL offset 和 target 生成的稳定 callsite token，各调用点独立消费 ordinal。rewriter 插入 token 时会重定向 branch/switch/exception-handler 边界，禁止用 `KeepOldMaxStack` 隐藏无效 CIL。桥按线程最多每 1ms 刷新一次 snapshot，generation 未变时 native 不复制 8288-byte payload。

原 MOD 设置页的按键重绑复用该 bridge，但受 Android modal epoch 隔离。菜单触摸不得伪装成 Unity KeyCode：modal 内 consumer edge 只更新基线，official/AsyncInput touch producer 也停止发布；进入 modal 时仅用 Cancel 释放旧 held，不清空 count。硬件键盘 observer 与 512-key native snapshot 保持启用。Jipper 在按钮点击同一 `OnGUI` 中立即查询 `anyKeyDown`，因此 bridge 必须同时保证进入点击不可见、关闭后不可重放，并在等待帧记录逐键 down ordinal，使后续首次枚举的新实体键仍能返回 `GetKeyDown=true`。

Android 外接键盘枚举只接受非虚拟、系统标记 external 且 `KEYBOARD_TYPE_ALPHABETIC` 的设备；媒体按键、遥控器和厂商 uinput 即使声明 `SOURCE_KEYBOARD` 也不触发 Auto External。PC-only managed feature 对 `ADOBase.platform` 的读取由 rewriter v14 在 MOD 自己的 IL 中替换为 `Platform.Windows(3)`，不临时写游戏全局字段；这使已经具备 Android 输入/资源桥的 desktop feature 能越过平台 guard。

Win32 polling 首个生产子集也已接通：导入器只接受 metadata 中真实 `pinvokeimpl`、模块为 `user32[.dll]`、入口为 `GetAsyncKeyState` 且 ABI 精确为 `Int16(Int32)` 的方法，再改写到同一 bridge；普通同名托管方法不匹配。当前返回 held 高位，覆盖 Jipper 的 listener/设置路径；Windows 低位“自上次调用后按下”语义和其他 user32 API 仍未实现。Input System 当前只识别直接 Keyboard/KeyControl/ButtonControl 查询候选，尚未折叠为 owner-scoped 查询；Rewired 当前承诺 `Player.GetButton/GetButtonDown/GetButtonUp(int)`。轴、动作名、任意设备图和事件订阅仍 fail-closed。Touch identity 只通过经验证的 consumer plan 驱动对应 MOD polling，不改写游戏输入或伪装成 raw keyboard/Mouse0。

当前 UI recipe 基础：

- 普通 translator 已能从 PC MOD IL 自动生成受限 object graph、component operations 和 lifecycle program；`UiRecipeTool fixture` 仍保留作 schema/互操作回归样本。
- object graph 初始化目前支持 `EnsureGraph`、受限 `SetActive` 意图、Rect/anchor/pivot/scale、Canvas/CanvasScaler/ContentSizeFitter、颜色、raycast、静态文本、字号、对齐、rich text、`TMP_Text.lineSpacing` 和 fit mode。资源目标支持 Image sprite、RawImage texture、Graphic material、TMP font/shared material/instance material。未知 operation 仍 fail-closed。

当前缺少：

- 动态任意字符串、动态资源选择、超出 PrefabGraph v1 白名单的 prefab、批量 Mesh、解析式动画和 scene identity generation 尚未接入。基础 Font/Sprite/Texture/Material ABI 与 setter 已完成，但 KeyViewer/SideImage 尚未被 lowerer 生成具体对象图和 binding。
- PC 风格按键格对象图。
- 独立 KeyViewer 键格布局对象；当前只在通用 HUD 文本行展示 `T1..TN`，尚未创建按键格、单键次数和 rain Mesh。
- 未证明 identity transform、Input System/Rewired 扩展入口到显示标签和 lane 的一般映射。
- 单键次数显示。
- rain、ghost rain 和对象池/批量渲染。
- Key10/12/16/20 等布局设置的移动端解释。
- 原 KeyViewer 颜色、尺寸、位置和标签设置的完整映射。

### 3.3 当前 Harmony

当前 Harmony 已完成 annotation/registry/常用工具 ABI、静态 target 聚合、运行时 metadata 目标签名、Postfix managed event/order 和同步 Prefix V2。成员级合同为 61/61 类型、871/872 成员；唯一缺口是 v42/v44 不可共存的 `HarmonyReversePatchType.AllCombine` 字面量。行为层已闭合 primitive/enum `ref/out`、primitive/enum `ref __result`、generated-proxy `ref/out __instance`、`__state`、generated-proxy `___field` 写回、Prefix 短路规则、Prefix/Postfix `__originalMethod`、同步 Prefix 的 primitive/enum/generated-proxy 可写 `__args`，以及 deferred Postfix 的只读 `__args` 快照和 primitive/enum 按值 `__result`。仍不是完整 Harmony：未知 struct/普通 proxy-byref、Postfix `ref/out __result`、Postfix 对 `ref/out` 参数的写回，以及 Transpiler/Finalizer 原生执行仍未闭合。

shim 已覆盖的 annotation ABI（`shims/0Harmony/`，15 个文件 3802 行）：

- 属性：`HarmonyAttribute`、`HarmonyPatch`、`HarmonyPatchAll`、`HarmonyPatchCategory`、`HarmonyDelegate`、`HarmonyReversePatch`、`HarmonyPriority`、`HarmonyBefore`、`HarmonyAfter`、`HarmonyDebug`、`HarmonyPrefix`、`HarmonyPostfix`、`HarmonyTranspiler`、`HarmonyFinalizer`、`HarmonyPrepare`、`HarmonyCleanup`、`HarmonyTargetMethod`、`HarmonyTargetMethods`、`HarmonyArgument`。
- 枚举：`MethodType`（0..36 全值）、`ArgumentType`、`HarmonyPatchType`、`HarmonyReversePatchType`、`MethodDispatchType`、`ExceptionBlockType`。
- 类型：`Harmony`、`HarmonyMethod`（含 `Merge`/`GetFromType`/`GetFromMethod` 语义）、`CodeInstruction`、`ExceptionBlock`、`InnerMethod`、`Patch`、`Patches`、`PatchProcessor`、`PatchClassProcessor`、`ReversePatcher`、`HarmonyException`、`Priority`、`FileLog`、`AccessTools`、`AccessToolsExtensions`、`SymbolExtensions`、`Transpilers`、扩展方法（`GeneralExtensions`/`CollectionExtensions`/`HarmonyMethodExtensions`）。
- `HarmonyRegistry` + `HarmonyRegistrationRecord` + `HarmonyDiagnostic`：逻辑注册表，`Patch`/`Unpatch` 只改 owner/generation 门控，会话内绝不物理 unhook。

属性构造器的**参数解析语义与 upstream 逐条对齐**，包括 `ParseSpecialArguments` 在 `argumentTypes`/`argumentVariations` 长度不等时的双向抛出行为（多则 `ArgumentException`，少则 `IndexOutOfRangeException`）—— 这一点由测试 fixture `VariationMismatchPatch` 在真实反射实例化下反证。

**transpiler 侧的 ABI**（`Transpilers` 三个扩展方法、`CodeInstruction.Call` 的表达式重载族、`SymbolExtensions`）是把上游 `HarmonyTests` 的 patch assets 对着本 shim 编译时暴露出来的缺口——见 §3.4 末尾的语料验证。这些方法**只变换 MOD 自己递进来的 `CodeInstruction` 列表**，不碰 IL2CPP 原生码，所以可以逐字镜像 upstream；缺一个成员会让整个 MOD 程序集 `TypeLoadException`，代价远大于补齐。唯一有意偏离的是 `CodeInstruction.CallClosure`：upstream 靠 `DynamicMethodDefinition` 现场发 IL 把捕获状态带进调用，本宿主没有运行时 IL emission，因此**静态方法引用照常返回指令，捕获闭包直接抛 `NotSupportedException`**——返回一条丢掉捕获状态的指令是错的答案。覆盖测试 `PcCompatHarmonyTranspilerAbiTests`（7 条）。

**`AccessTools` 面已按上游 `HarmonyTests/Tools/TestAccessTools*.cs` 语料闭合**（第二轮语料验证，见 §3.4）。补齐的是：16 个 `"Type:Member"` 重载（`Method`/`Field`/`Property`/`Event`/`DeclaredXxx` + 8 个 getter/setter/adder/remover 访问器）、`CodeInstruction.Call(string typeColonMethodname, …)`、整类 `AccessToolsExtensions`（56 个 fluent 转发，缺一个就是整程序集 `TypeLoadException`）、以及 20 个零散成员（`TypeSearch`/`ClearTypeSearchCache`/`FindIncludingInnerTypes`/`Inner`/`FirstInner`/`FirstMethod`/`FirstConstructor`/`FirstProperty`/`GetTypes`/`GetMethodByModuleAndToken`/`IsDeclaredMember`/`GetDeclaredMember`/`Identifiable`/`EnumeratorMoveNext`/`AsyncMoveNext`/`Is*` 七件/`IsOfNullableType`/`IsMonoRuntime`/`ThrowMissingMemberException`/`GetOutsideCaller`/`RethrowException`/`CombinedHashCode`）。四处有意偏离，都写在源码注释里：

- **`"Type:Member"` 的类型半段解析不到时统一「记诊断 + 返回 null」**。upstream 自身不一致——`Method`/`DeclaredMethod` 转发到 null-safe 重载返回 null，而 `Field`/`Property`/`Event` 族直接 `info.type.GetField(...)`，**抛 `NullReferenceException`**。PcCompat 下桌面能解析的类型可能真的不在托管侧（IL2CPP 类型不是托管程序集），而 NRE 的栈里没有 MOD 帧，所以统一成可导出的 `HarmonyUnresolvedDeclaringType` 诊断。格式错误仍然逐字抛 upstream 那条消息（` must be specified as 'Namespace.Type1.Type2:MemberName`，前导空格 + 引号不闭合原样保留）。
- **`EnumeratorMoveNext`/`AsyncMoveNext` 改读编译器属性**。upstream 读目标方法的 IL 取唯一 `Newobj` 操作数找迭代器类；本宿主没有 IL reader，改用 C# 编译器留下的 `IteratorStateMachineAttribute`/`AsyncStateMachineAttribute`。托管目标同答案，缺属性时返回 null（fail-closed，绝不猜）。**连带收窄了 `MethodType.Enumerator`/`Async` 的 fail-closed 范围**：运行时反射路径（`PatchAll`/`PatchClassProcessor`）现在能解析托管迭代器的 `MoveNext`，只有真正没有托管 metadata 的 IL2CPP 目标才留诊断；静态 metadata 扫描器仍然全程 fail-closed（它读的是 MOD 程序集的 metadata，目标方法的状态机属性根本不在里面）。
- **`Identifiable` 恒等返回**。upstream 走 MonoMod `PlatformTriple.GetIdentifiable` 拿运行时规范句柄，那是为了紧接着 detour 它；本宿主不经 MonoMod detour，且 upstream 自身在平台无独立身份时就 fallback 到入参。
- **`MethodDelegate`/`HarmonyDelegate` 只做不需要发 IL 的形状**。upstream 六条分支里四条是纯 `Delegate.CreateDelegate`（静态、接口方法、delegate 首参为接口、虚调用类实例方法）或纯 `Activator.CreateInstance(delegateType, instance, functionPointer)`（closed non-virtual；upstream 那条 `ldftn` 分支只为绕 Mono bug mono/mono#19964，CoreCLR 用不上），这四条逐字镜像；剩下两条——**open-instance 非虚调用**和**任何 struct 实例方法**（接收者按 ref 传，没有普通 delegate 签名能装）——抛 `NotSupportedException` 并记诊断。整个成员缺席是 `TypeLoadException` 整程序集陪葬，抛错只炸那一个调用点。

覆盖测试 `PcCompatHarmonyAccessToolsAbiTests`（18 条）。

**尚未镜像的上游公开面（2026-07-26 全量普查）**。前两轮语料只覆盖语料本身用到的成员，所以这轮直接对上游 `Harmony/Harmony/**/*.cs` 与 shim 做公开类型/成员声明的 diff，得到完整缺口清单。`AccessTools` 成员级 diff 为 **82 : 78**，只差 4 个；但**整类**层面的缺口比这大得多：

| 上游公开面 | 上游行数 | 能否镜像 |
| --- | --- | --- |
| ~~`CodeMatcher` + `CodeMatch` + `Code`（~220 个 opcode matcher 类）+ `Operand_` / `ErrorHandler`~~ | 1833 | **已补（2026-07-27）**——逐字镜像，见下 |
| ~~`CodeInstructionExtensions`（32 个方法，见下）~~ | 在 `Tools/Extensions.cs` 内 | **已补（2026-07-27）** |
| ~~`CodeInstructionsExtensions.Matches`、`MethodBaseExtensions.HasMethodBody`~~ | 同上 | **已补（2026-07-27）** |
| ~~`Traverse` / `Traverse<T>` + `GetterHandler` / `SetterHandler` / `InstantiationHandler`~~ | 454 | **已补（2026-07-27）**——逐字镜像，见 §3.3.2 |
| `MethodInvoker`（`FastInvokeHandler`）+ `FastAccess` + `DelegateTypeFactory` + `RefResult<T>` | 443 | **不能**——全部靠 `DynamicMethod` 发 IL，只能做显式抛错空壳 |
| `PatchInfo` | 204 | 能（数据容器），但它是 upstream 的内部 patch 存储 |
| `AccessTools.FieldRefAccess` / `StaticFieldRefAccess` / `StructFieldRefAccess` + `FieldRef<F>` / `FieldRef<T,F>` / `StructFieldRef<T,F>` 三个 delegate 类型 | — | **不能**——没有任何非发 IL 的形式 |
| ~~`AccessTools.MakeDeepCopy`~~ | — | **已补（2026-07-27）**——见 §3.3.2 |

`CodeInstructionExtensions` 的 32 个方法：`Is`×2、`Calls`、`OperandIs`×2、`IsLdarg`、`IsLdarga`、`IsStarg`、`IsLdloc`、`IsStloc`、`LoadsConstant`×5、`LoadsField`、`StoresField`、`Branches`、`WithLabels`×2、`WithBlocks`×2、`MoveLabelsTo`、`MoveLabelsFrom`、`MoveBlocksTo`、`MoveBlocksFrom`、`ExtractLabels`、`ExtractBlocks`、`ArgumentIndex`、`LocalIndex`、`IsValid`。

（表中原先把 `ModifierType` 列在 `CodeMatcher` 族里，是普查时的归属错误：它是 `Internal/InlineSignature.cs:88` 的嵌套类型，与 `CodeMatcher` 无关，也不在缺口清单内。）

缺席的代价与 §3.3 前面几处一致：**不是查找降级，是整个 MOD 程序集 `TypeLoadException`**。所以优先级按"能否零妥协镜像"排——`CodeMatcher` 族与 `CodeInstruction` 扩展最高（可逐字镜像，且是 transpiler 的主力写法，上游 `HarmonyTests/Tools/TestCodeMatcher.cs` 可直接作语料），`Traverse` 次之，发 IL 的那批最低（补了也只是抛错空壳）。**表中前三行与 `Traverse`/`MakeDeepCopy` 两行均已于 2026-07-27 落地**，剩下的三块（`MethodInvoker` 族、`PatchInfo`、`FieldRefAccess` 族）**也已在其后落地**，见 §11.3：`MethodInvoker`/`FastAccess`/`DelegateTypeFactory` 在 `LegacyRuntimeFallbacks.cs`（反射 fallback），`PatchInfo` 在 `PatchInfo.cs`，`FieldRefAccess` 三族在 `AccessTools.cs:866-898`（ABI 完整的显式抛错桩，因此不存在 `TypeLoadException` 风险）。本节表格保留的是普查当时的快照。

### 3.3.1 `CodeMatcher` 族已补齐（2026-07-27）

上表前三行已全部落地，**零语义妥协、逐字镜像上游 2.4**：

| shim 文件 | 内容 |
| --- | --- |
| `shims/0Harmony/CodeMatch.cs` | `CodeMatch : CodeInstruction`——opcodeSet / operands / labels / blocks / jumpsFrom / jumpsTo / 自由 predicate 六类条件，5 个构造 + 24 个静态工厂（`IsLdarg`/`Calls`×4/`LoadsConstant`×5/`LoadsField`×2/`StoresField`×2/`LoadsLocal`/`Branches` 等） |
| `shims/0Harmony/CodeMatcher.cs` | 完整游标：`MatchStartForward`/`MatchEndBackwards` 四向 + 四个 `PrepareMatch*` 延迟版、`Repeat`、`SearchForward/Backwards`、`RemoveSearchForward/Backward`、`RemoveUntilForward/Backward`、`Insert`/`InsertAfter`/`*AndAdvance` 全家、`Set*`、`NamedMatch`、`OnError`/`ThrowIfNotMatch*`/`ReportFailure`、`Clone`。文件尾附 `CodeInstructionsExtensions.Matches` |
| `shims/0Harmony/CodeInstructionExtensions.cs` | 32 个扩展方法 + 9 个 `internal` opcode 集合（`CodeMatch` 的静态工厂要用）+ `MethodBaseExtensions.HasMethodBody` |
| `shims/0Harmony/Code.cs` | ~220 个 opcode matcher 类（`Nop_`/`Call_`/…）+ `Operand_`。**用 `sed` 从上游源码机械生成**，只加 nullable 注解，杜绝手抄漏项 |

**唯一需要判断的地方是 `ILGenerator`**：`DeclareLocal` / `DefineLabel` / `CreateLabel*` / `InsertBranch*` 在 generator 为 null 时，上游本来就抛 `InvalidOperationException("Generator must be provided to use this method")`。本宿主里 generator 恒为 null（`CodeMatcher(IEnumerable<CodeInstruction>)` 这条构造上游也不传 generator），所以**照抄即是正确行为**——MOD 看到的异常与真实 Harmony 逐字一致，且 `OnError` 处理器仍能按上游语义吞掉它。别把"transpiler 在本宿主不执行"当成"不用补 ABI"：MOD 程序集只要**引用**这些类型就会在加载期解析，缺席 = 整个程序集 `TypeLoadException`，连 Prefix/Postfix 一起陪葬。

覆盖测试 41 条，分两个文件：

- `PcCompatHarmonyCodeMatcherAbiTests`（29 条）——语料直接取自上游 `HarmonyTests/Tools/TestCodeMatcher.cs`。上游用 `PatchProcessor.GetOriginalInstructions` 读 IL 得到 21 条指令（读 IL 恰恰是本宿主做不到的），所以这里**手工构造出同一条指令序列**，再逐字沿用上游的位置/长度期望：`MatchStartForward` 停在 Pos=6、`MatchEndForward` 停在 Pos=7、`Repeat` 依次走 6/9/18、`RemoveSearchForward` 后 Pos=8 / Length=16 / Operand="F"，等等。
- `PcCompatHarmonyCodeInstructionExtensionsAbiTests`（12 条）——覆盖 32 个扩展方法的每个形态：`Ldarg_0`/`Ldarg_S`/`Ldarg` 三种编码、`OperandIs` 的数值宽化（`Ldc_I4 5` 同时匹配 `5L`/`(short)5`/`5.0`）、`LoadsConstant` 五个重载、`LoadsField` 的 `byAddress` 分支、`MoveLabelsTo` 返回**源**而 `MoveLabelsFrom` 返回**接收方**这条容易写反的约定，以及 `HasMethodBody` 对抽象方法返回 false。

实测用量：仓库里唯一的 MOD（Jipper）对上述全部缺口 **0 引用**，所以不阻塞当前目标 MOD；但 JALib 上游 `Core/Patch/JAMethodPatcher.cs` 有 **38 处 `.WithLabels`/`.WithBlocks`** 与 6 处 `PatchInfo`——我们的 JALib shim 是重写的 façade（`Core/Patch/PatchMetadata.cs` 用自有的 `JAPatchInfo`），没有继承这条依赖，但这说明这些成员在真实 UMM 生态里是常用面而非边角。

### 3.3.2 `Traverse` 与 `MakeDeepCopy` 已补齐（2026-07-27）

| shim 文件 | 内容 |
| --- | --- |
| `shims/0Harmony/Traverse.cs` | `Traverse` + `Traverse<T>`。静态工厂 `Create`×3 / `CreateWithType`，`Field`/`Property`/`Method`/`Type` 四条链式导航（各带泛型与 `Type[] paramTypes` 重载），`GetValue`×4 / `SetValue` / `GetValueType`，`IsField`/`IsProperty`/`IsWriteable`，`Fields()`/`Properties()`/`Methods()`，四个 `*Exists()`，6 个静态 `IterateFields`/`IterateProperties` 重载，`CopyFields` |
| `shims/0Harmony/AccessCache.cs` | `Traverse` 背后的成员查找缓存（`internal`）。逐字镜像 `Internal/AccessCache.cs`，含**缓存负结果**这条行为——名字解析不到时 null 也写进字典并永不重取 |
| `shims/0Harmony/AccessTools.cs`（追加） | `MakeDeepCopy` 三个重载 + `addHandlerCache` |

**动工前那条"`Traverse` 内部很可能用 `FieldRefAccess`"的猜测是错的**——实测 `Traverse.cs` 对 `FieldRefAccess`/`DynamicMethod`/`ILGenerator` **零依赖**，全程 `FieldInfo.GetValue`/`SetValue` + `PropertyInfo.GetValue`/`SetValue` + `MethodBase.Invoke`。所以整个 454 行逐字镜像，规范化后与上游 **172 : 172 行逐行对齐，差异全部是 nullable 注解**（`_type`/`_root`/`_info`/`_method`/`_params` 五个字段与 `GetValue()`/`GetValueType()`/`ToString()` 的返回类型）。核对方式：

```bash
norm() { sed -e 's/\r$//' "$1" | grep -v '^[[:space:]]*///' | grep -v '^[[:space:]]*//' \
  | sed -e 's/[[:space:]]//g' | grep -v '^$' | grep -v '^{$' | grep -v '^}$' \
  | grep -v '^using' | grep -v '^namespaceHarmonyLib'; }
diff <(norm Harmony/Harmony/Tools/Traverse.cs) <(norm xphorror.PcModCompat/shims/0Harmony/Traverse.cs)
```

**`MakeDeepCopy` 是本轮唯一有替换的地方**。上游对泛型集合分支要调集合的 `Add`，走的是 `MethodInvoker.GetHandler(addOperation)` 产出的 `FastInvokeHandler`——那是 `DynamicMethod` 发的 IL，本宿主没有。`FastInvokeHandler` 的签名 `object (object target, object[] parameters)` 与 `MethodBase.Invoke` 一致，所以缓存值类型从 `FastInvokeHandler` 换成 `MethodInfo`，调用改走反射。**唯一的可观察差异是异常形状**：`MethodBase.Invoke` 会把被调方法抛的异常包进 `TargetInvocationException`，而发 IL 的版本不会包。所以调用点用 `ExceptionDispatchInfo.Capture(ex.InnerException).Throw()` 拆包重抛，MOD 的 `catch` 看到的仍是原异常类型与原堆栈——`PcCompatHarmonyTraverseAbiTests.MakeDeepCopySurfacesCollectionAddExceptionsUnwrapped` 就是钉这一条的。其余分支（null 短路、`Nullable` 解包、`IsPrimitive`、`IsEnum`、数组、`System.*` 原样返回、字段迭代 + `processor` 点路径）逐字照抄。

覆盖测试 `PcCompatHarmonyTraverseAbiTests`（19 条）：前 15 条是上游 `HarmonyTests/Traverse/` 那 5 个文件的用例重铸到自带 fixture 上（`Traverse.Create(null)` 后每个访问器都返回空 traverse 而不是 null、`_root/_type/_info/_method/_params` 五个内部字段名、public/private/protected/internal 四种字段读写、静态字段从实例与从类型两条解析路径、`out`/`ref` 参数、重载消歧、`IsWriteable` 拒 const 与 static readonly 但放行实例 readonly、嵌套类型导航、`CopyFields`），后 4 条打 `MakeDeepCopy`。

当前行为边界：

- `Harmony.PatchAll()` / `Harmony.Patch()` 写入逻辑注册表；host 将 active registration 与导入期 descriptor/recipe 身份匹配后绑定真实 callback。`Unpatch*` 只翻转逻辑 active 门控，不物理卸载 HookSlot。host 每帧读取预编译的 registry revision delegate；Patch、Unpatch、Repatch 和顺序变化会在当帧重建 callback/排序计划，不再有 60 帧窗口，也不依赖记录数量变化。
- Postfix 由 native 有界事件队列延迟到 UnityMain；Prefix 由 `ManagedSynchronousPrefix=23` 在原 hook 线程同步执行。二者复用 metadata 动态解析出的完整目标签名，目录外目标不再依赖手工 catalog。
- Prefix V2 使用 96 B 版本化原地 invocation frame，支持 `void/bool`、`__instance`、generated-proxy `ref/out __instance`、`__originalMethod`、最多 6 个 primitive/enum/proxy 参数、primitive/enum `ref/out`、primitive/enum `ref __result`、`ref __runOriginal`、generated-proxy `___field` 读写、可写 `__args` 与 Prefix/Postfix `__state` 配对。实例替换只接受带 `(IntPtr)` 构造和可读 `Pointer:IntPtr` 的代理，12 个实例 dispatcher 会在调用 original 前采用新指针；static dispatcher 不参与。deferred Postfix 可从 184 B 事件记录读取最多 6 个 primitive/enum/generated-proxy 的 `__args` 值快照，并按值读取 bool/int/enum `__result`；数组修改和结果修改不会回写已返回的 native 调用。目标含 `ref/out` 参数或 Postfix 声明 `ref/out __result`/`ref/out __instance` 时绑定失败关闭。`bool false` 可 skip-original；异常、错误线程和 callback 缺失均 fail-open，递归深度上限 32。
- 多 Prefix 的运行时 `owner/priority/before/after/registrationIndex` 通过完整 order plan 发布给 HookBroker。native 在 immutable snapshot 建立期做跨 MOD 拓扑排序；hook 热路径只遍历已排序 POD target，不加锁、不分配、不解析元数据。
- Prefix 短路已按上游 `MethodCreator` 实现：`runOriginal=false` 后跳过会影响 original 的后续 Prefix，但无返回值、无引用写回的观察型 Prefix 继续执行。Postfix 同构排序也已闭合。尚未实现未知 struct/普通 proxy-byref、deferred Postfix 对调用方参数/返回值的同步写回，以及 Transpiler/Finalizer 的生产执行。

### 3.4 Harmony annotation 聚合链路

`PcCompatHarmonyAttributeAggregator` 在 `PcCompatStaticPatchScanner.ScanAssembly` 的 JAPatch 循环之后、dynamic `AddPatch` 扫描之前运行。它是 metadata-only 的（`System.Reflection.Metadata`，不加载 MOD 程序集、不跑 static ctor），逐 type 而非逐 attribute，因为 Harmony 的 target 来自类级与方法级属性的合并。

聚合规则（全部按 upstream 源码逐行核对，而不是按文档推测）：

- **相关性门**：type 自身或其任一方法带 `HarmonyLib` 命名空间属性，或 MOD 自定义的 Harmony 属性子类，或**基类**带这类属性（`PatchAll` 通过 `HasHarmonyAttribute` → `GetFromType` 发现，后者用 `GetCustomAttributes(inherit: true)`）。
- **可发现性**：只有能贡献 container info 的属性（带 `info` 字段的，即从 `HarmonyAttribute` 派生的）才让 `PatchAll` 看见；`HarmonyPrepare`/`HarmonyCleanup`/`HarmonyTargetMethod(s)`/`HarmonyArgument`/`HarmonyPrefix` 等辅助属性直接派生自 `Attribute`，不贡献。只有方法级注解、没有类级注解的类会被 `PatchAll` 跳过，记 `HarmonyPatchClassNotDiscoverable`。
- **patch 方法枚举**用 declared-only（对齐 `PatchTools.GetPatchMethods` → `AccessTools.GetDeclaredMethods`）；**辅助方法查找**（`Prepare`/`Cleanup`/`TargetMethod`/`TargetMethods`）走基类链（对齐 `PatchTools.GetPatchMethod` 用不带 `DeclaredOnly` 的 `GetMethods(all)`），且「链上任意位置带属性」优先于「链上任意位置同名」。
- **kind 判定**顺序为 `[Prefix, Postfix, Transpiler, Finalizer, ReversePatch, InnerPrefix, InnerPostfix]`，首个命中即停，用未经过滤的属性集；无属性时按方法名约定 fallback。static 检查在 kind 判定之后、且只对非 ReversePatch 施加。
- **`ReversePatch`** 在正常路径之前处理，并按 upstream 在 originals 恰好 1 个时把它种入 `lastOriginal` 供后续方法继承。
- **category** 记入 `Reason`：`PatchAll` 会装带 category 的类，`PatchAllUncategorized` 会跳过它们，`PatchCategory` 只按名装一个——所以 MOD 调哪个入口决定这条 descriptor 是否真的生效。
- **priority** 用 `-1` 作 unset 哨兵、合并时取 `Math.Max`，与 `HarmonyMethod.Merge` 一致。

不可静态判定的一律 **fail-closed**：记 issue、不发 descriptor、绝不猜目标。17 个 issue code：

| code | 触发条件 |
| --- | --- |
| `HarmonyPatchClassNotDiscoverable` | 只有方法级注解，`PatchAll` 看不见 |
| `HarmonyInheritedClassAttributeUnsupported` | 类级属性从基类继承而来，与自身声明的合并顺序由运行时决定 |
| `HarmonyPatchAllUnsupported` | `[HarmonyPatchAll]` 批量展开 |
| `HarmonyDynamicTargetMethodUnsupported` | `TargetMethod`/`TargetMethods` 运行时解析 |
| `HarmonyPrepareGateNotEvaluated` | `Prepare` 运行时决定是否应用（聚合假设返回 true） |
| `HarmonyPatchMethodNotStatic` | 非 ReversePatch 的 patch 方法不是 static，Harmony 拒绝整类 |
| `HarmonyArgumentVariationsMismatch` | `argumentTypes` 与 `argumentVariations` 长度不等 |
| `HarmonyInnerPatchUnsupported` | InnerPrefix/InnerPostfix call-site patch |
| `HarmonyUndefinedTargetType` | 类级与方法级都没给 declaring type |
| `HarmonyUndefinedTargetMethod` | `MethodType.Normal` 缺方法名 |
| `HarmonyIndexerTargetUnsupported` | Getter/Setter 无属性名（indexer） |
| `HarmonyEnumeratorTargetUnsupported` | `MethodType.Enumerator` 需读目标 IL 找状态机 |
| `HarmonyAsyncTargetUnsupported` | `MethodType.Async` 同上 |
| `HarmonyUnknownMethodType` | `MethodType` 值不在镜像枚举内 |
| `HarmonyDerivedAttributeUnsupported` | MOD 自定义 Harmony 属性子类，构造器 IL 里给 `info` 赋值 |
| `HarmonyUnknownBuiltinAttribute` | `HarmonyLib` 命名空间里未镜像的属性 |
| `HarmonyAttributeDecodeFailed` | 属性 blob 解码失败 |

descriptor 以 `source=harmony_attribute` 进入 `static-patch-scan-v2` 报告，`Status` 恒为 `RegisteredOnly`；`Kind` 对 Inner patch 置 `Unknown`，从而在 `PcCompatCallbackTranslator` 的 `AllowedPatchKinds` 白名单上天然 fail-closed。

覆盖测试见 `StArray.ModManager.Tests/PcCompatHarmonyAttributeAggregatorTests.cs`（27 条），fixture 见 `HarmonyAggregationFixtures.cs`——fixture 用真实 shim 属性标注自身，扫描的是编译器真实产出的 metadata，不是手搓 blob。其中两条是防退化下限：扫描不得以被吞掉的异常收场（`BadManagedImage`/`MetadataReadFailed` 必须为空且 descriptor 非空），以及每个带注解的 fixture 类必须要么产出 descriptor、要么产出 issue，不得被无声丢弃——后者由反射枚举驱动，新增 fixture 忘了断言会直接失败。

**用真实上游语料验证过一轮**：仓库里唯一的 MOD（Jipper）静态扫描出的 `harmony_attribute` 计数为 0，自造 fixture 只能证明「我以为的规则被实现了」。所以把 `Harmony/HarmonyTests/*/Assets/*.cs`（约 2000 行真实 patch 类，43 个带类级注解）拷进一个临时工程、引用本 shim 的 `0Harmony.dll` 编译，再把产物喂给聚合器。两个结论：**编译期**抓到上面 §3.3 那两个真实 ABI 缺口（`Transpilers` 整类、`CodeInstruction.Call` 表达式族）；**聚合期零缺陷**——66 条 descriptor / 11 条 issue / 6 个 issue code 被触发，逐条对着 upstream 源码核过 target、`argumentTypes`、priority、before/after，包括两条反直觉规则：priority 合并是 `Math.Max`（`HarmonyMethod.cs:284`，类级 500 会压住方法级 200，不是 detail-wins），以及 `PatchAll` 只看类级属性（`Harmony.cs:106` → `HasHarmonyAttribute` → `GetFromType`，只有方法级注解的类整体跳过）。语料工程在仓库外、`Harmony/` 已 gitignore，不能作永久测试依赖，所以缺口都固化成了 `PcCompatHarmonyTranspilerAbiTests`。**第二轮语料**换成 `HarmonyTests/Tools/TestAccessTools*.cs` + `Assets/AccessToolsClass.cs`，专打 `AccessTools`。首编译暴露的缺口比预估大得多：不只是原先记下的 `typeColonName` 重载族，还有**整类 `AccessToolsExtensions` 缺席**（56 个成员）与 20 个零散成员。补齐后复测，剩余编译错误只有 NUnit 3→4 签名变化和外部 `Lokad.ILPack` 包这两类噪声（读语料结果必须先 `grep -v '“Assert”'` 过滤，否则上百条 `Assert.NotNull/Null/AreEqual` 已移除的报错会淹掉真缺口）。**仍然缺席**：语料只能证明语料自己用到的东西，所以完整缺口清单不来自语料，而来自 §3.3 末尾那张对上游全量 diff 得到的普查表——`AccessTools` 成员级原先差 4 个，`MakeDeepCopy` 已于 2026-07-27 补齐（§3.3.2），只剩 `FieldRefAccess`/`StaticFieldRefAccess`/`StructFieldRefAccess`；整类缺席六块，其中四块（`CodeMatcher` 族 / `CodeInstructionExtensions` / `CodeInstructionsExtensions`+`MethodBaseExtensions` / `Traverse`+`Traverse<T>`）已于 2026-07-27 补齐（§3.3.1、§3.3.2）。**该普查表已于其后被 §11.3 记录的那轮补齐追平：`MethodInvoker`/`FastAccess`/`DelegateTypeFactory`（`LegacyRuntimeFallbacks.cs`）、`PatchInfo`（`PatchInfo.cs`）以及 `FieldRefAccess` 三族（`AccessTools.cs:866-898`，显式抛错桩，ABI 完整）现均已存在。**本节上文"仍然缺席"的措辞是那轮之前的快照，当前状态以 §11.3 末尾的 61/61 类型、871/872 成员为准。2026-08-23 以 JPOV 源码复核过 `FieldRefAccess` 桩的实际风险：JPOV 唯一可能落到该桩的路径是 `PatchManager.CreateMemberGetter<T,F>`，其唯一调用点 `AudioSource.pitch` 是属性、走属性委托分支，且调用被 try/catch 降级；`CreateFieldRef` 无外部调用点。桩因此不在活跃路径上。

### 3.5 Harmony 运行时逻辑注册表

静态聚合读的是 metadata，运行时这条路读的是 MOD 真的调了什么。shim `HarmonyRegistry` 是 Harmony 侧的逻辑注册表，与 JALib 的 `JAPatcher` 暴露同一组 duck-typed snapshot/clear 成员和同一组记录属性名，host 侧 `PcCompatShimPatchRegistries` 用一份反射读两边。变更检测优先读取 Harmony 单调 `Revision`，旧 registry/JALib 回退到其 `RegisteredPatchCount` 变更计数器；getter 在建 session 时编译成 `Func<int>`，逐帧折叠 stamp 不做反射和分配。

- **来源区分**：descriptor 的 `Source` 为 `managed_oracle`（JALib）或 `shim_harmony_registry`（Harmony）。`Status` 恒为 `RegisteredOnly`，`Reason` 写明物理 hook 由 ModManager/HookBroker 独占，避免 `registered_only` 被读成「已生效」。
- **清空时机**：注册表在 MOD bootstrap **之前**清空。shim 注册表是 static，跨会话未必随 collectible ALC 回收，所以清是必要的；但排在 bootstrap 之后会把 MOD 在 `OnLoad`/bootstrap 期装的 patch 全抹掉——upstream UMM MOD 惯用 `new Harmony(id).PatchAll()` 就在这个窗口，JALib 自身的 `Bootstrap.GetConstructorPatch` 也在。
- **诊断持久化**：`ClearRegisteredPatches()` 只清注册记录，诊断另由 `ClearDiagnostics()` 显式清（host 目前不调）。诊断是「MOD 要了什么、这里给不了什么」的唯一记录，而清空发生在 MOD 代码跑过之后，一起清等于证据先于被读取就没了。诊断经 `PcCompatManagedModSession.HarmonyShimStatus` 落进诊断导出的 `harmonyShimStatusBegin/End` 段，格式为 `registrations=N active=M diagnostics=K` 加每条诊断一行。
- **缺失注册表**：JALib 侧缺失即硬失败（没有它就没有 patch 真相）；Harmony 侧缺失只记 `Manager.Logger.Warn` 后跳过，旧 shim 配新 host 不应让所有 MOD 都装不上。
- **shim 是构建产物**：改完 `shims/**` 必须重跑 `xphorror.PcModCompat/build_shims.ps1`，否则 loader 测试加载的仍是 `out/legacy_shims` 里的旧 dll；装机前还需重打包刷新 `Android/library/src/main/assets/runtime/pc_compat_shims/`。

覆盖测试：`PcCompatHarmonyRegistryTests`（诊断跨清空存活 + 两个注册表的静态成员与记录属性名契约）、`PcCompatManagedLoaderTests.BootstrapTimeHarmonyPatchSurvivesIntoTheSnapshot`（Jipper 实测，bootstrap 期的 `System.Type.GetConstructor` Prefix 必须出现在快照里）。

## 4. MOD 自绘与 UI Recipe 编译器

### 4.0 运行时后端

MOD HUD 的默认目标后端是 `ManagedSelfRender`：

```text
compiled managed bundle
  -> isolated AssemblyLoadContext
  -> UMM/JALib/Harmony compatibility entry
  -> managed lifecycle registry
  -> UnityMain dispatcher
  -> generated Unity/Assembly-CSharp proxies
  -> actual IL2CPP GameObject/Component/AssetBundle
```

每个 MOD session 维护独立状态：

```text
Discovered
Rewritten
ProxyReady
LoadedHidden
Visible
SceneInvalidated
Faulted
Unloaded
```

只有 `Rewritten + ProxyReady` 才能执行 Entry。Entry 成功不等于 HUD ready；必须等 lifecycle 注册、资源策略和 UnityMain dispatcher 都可用。MOD 创建的每个 IL2CPP object/asset handle 都记录 owner mod、session generation 和来源 proxy call，reload/unload 只回收对应 owner，禁止跨 MOD 或跨 generation 复用裸 handle。

`VerifiedRecipeOptimization` 与 `ExplicitRecipeFallback` 继续使用本节后续的 `ui_recipe.bin`、Native VM 和 PresentationSink。两条后端可按 feature 并存，但同一 feature 同一 session 只能有一个视觉 owner，避免 managed HUD 与 recipe HUD 重复显示。

### 4.1 支持的源模式

Recipe 优化/fallback 第一阶段只翻译可静态证明的 Unity UI 调用，例如：

- `new GameObject(name)`
- `AddComponent<T>()`
- `Transform.SetParent(...)`
- `Object.Destroy(...)`
- `Object.DontDestroyOnLoad(...)`
- `GameObject.SetActive(...)`
- `RectTransform` anchor、pivot、position、size、scale
- `Canvas` render mode 和 sorting order
- `CanvasScaler` scale mode 和 reference resolution
- `Image` color、sprite、raycast target
- `TextMeshProUGUI` text、font、font size、alignment、color、rich text、line spacing

不允许在无法证明时猜测调用语义。遇到动态反射、未知泛型、自定义组件或不可解析委托时，导入应失败关闭并输出精确诊断。

HUD root 从 UMM/JAMod 生命周期、Harmony callback、MonoBehaviour lifecycle 及其 helper 调用闭包中自动发现。编译器寻找 `GameObject/AddComponent/Instantiate/LoadAsset/Canvas/TMP` 等 HUD seed，再对影响对象图、设置、输入绑定和生命周期的代码做程序切片。

资源侧同时建立反向引用图：asset 名称、类型、prefab 组件、Material/Shader 和 IL 中的加载字符串共同指向调用方法，再反向追踪到生命周期入口。`hud/ui/overlay/keyviewer/panel/widget/canvas` 等关键词只作为低权重提示，不能单独证明某资源是 HUD。

候选采用置信评分。UI component/prefab 类型、直接代码引用、生命周期可达性和 Instantiate/AddComponent 为高权重；名称关键词为低权重。低置信候选进入导入报告，并允许 manifest 或调试 UI 指定通用入口，不写 MOD 身份特判。

当前 `PcCompatUiGraphLowerer` 只接受可证明的 straight-line 构造路径和递归深度受限的纯 helper。它从 manifest 的 entry/JAMod 生命周期方法建立可达索引，识别 `GameObject`、受支持的 `AddComponent<T>/GetComponent<T>`、Transform/RectTransform、Canvas/TMP setter，并把静态字段、Proven resource field 和 `ref` value-type 构造回写纳入同一 graph。每个 helper 都有 checkpoint；helper 失败会回滚该 helper 的新增节点和字段状态，不把不完整对象伪装成完整 graph。

当前已知会降级或拒绝的路径包括：动态 `Instantiate`/prefab 组件、LayoutGroup 和动态 layout loop、运行时选择的资源/字体/材质、未知 getter/setter、动态反射、无法证明的分支和一般循环。静态 `ContentSizeFitter.horizontalFit/verticalFit` 与结构化 Proven resource field 已进入受支持目录。导入报告会保留方法名与 IL offset；只要 graph 仍可独立运行，feature 标记为 `partial`，否则该候选不进入 graph。

### 4.2 Recipe 组成

建议 `ui_recipe.bin` 至少包含：

```text
Header
  schemaVersion
  compilerVersion
  sourceAssemblySha256
  targetGameRevision
  capabilityFlags

String table
Type/member identities
Object graph
Component initialization operations
Input/state bindings
Update bytecode
Lifecycle rules
Asset references
Resource limits
Diagnostics index
```

Recipe 不保存函数地址。类型、方法和字段按完整 metadata identity 保存，运行时由 ModManager 唯一 resolver 解析。

#### `ui_recipe.bin` v1 契约

当前第一版已经落地为固定小端二进制容器。它不是把 JSON 换成另一种可执行脚本，而是把导入期已经验证过的身份和 fixed-op rule 以不可变表交给 Native loader；Native 不在热路径解析 JSON。

文件布局：

```text
Header (96 bytes)
  0   char[8]  magic = "XPHUIRCP"
  8   u16      schemaVersion = 1
  10  u16      headerSize = 96
  12  u32      flags (bit0=little-endian, bit1=fixed-op tables,
                     bit2=lifecycle, bit3=object graph, bit4=resources)
  16  u32      sectionCount = 10
  20  u32      targetGameRevision
  24  u64      capabilityFlags
  32  u32      modId string offset
  36  u32      recipeId string offset
  40  u32      compatibility string offset
  44  u32      compilerVersion string offset
  48  byte[32] source assembly SHA-256 (缺失时全零)
  80  u32      totalSize
  84  u32      CRC32 (计算时本字段视为零)
  88  u32      sectionTableOffset
  92  u32      reserved

Section entry (24 bytes each)
  u32 type, u32 offset, u32 size, u32 count, u32 elementSize, u32 reserved
```

section type 1-10 依次为 string table、parameter refs、targets、rules、object graph、component ops、lifecycle、bytecode、resources、diagnostics。前四段始终存在；lifecycle/bytecode 必须成对出现。object graph/component ops 已支持非空段并进行 parent、cycle、ownership、component compatibility 和容量校验。resources 使用固定 32-byte record，保存 node、target、feature group、asset name 和 expected type 字符串引用；managed/native 双侧拒绝越界、类型不兼容和重复身份。diagnostics 当前仍为空段。

target record 为 48 bytes，保存完整 assembly/namespace/type/method/return/ABI 字符串引用、参数引用范围、static/generic 标志和 rule 范围。rule record 为 36 bytes，保存 rule/feature/source 字符串引用、stage/op code、capability 和 enabled 标志。所有字符串均为 UTF-8、NUL 结尾、offset 相对于 string table，不保存 RVA/VA 或函数指针。

lifecycle record 为 56 bytes，保存 lifecycle 字符串 ID、runtime rule ID、trigger、clock domain、flags、program range、instruction budget、presentation command/target ID、初始延迟和 `Deferred` 重试延迟。bytecode section 直接使用 16 bytes 的 Native VM instruction 布局。Managed emitter 和 Native loader 都校验寄存器与 branch 范围；Native 还使用 `verify_program()` 对每个 program range 做最终验证，任一程序失败会拒绝整包。

Native loader 在接受文件前检查 magic、版本、段边界、元素大小、数量上限、字符串终止符、target/rule 范围和 CRC32。校验失败时整包拒绝，不加载部分表。`hook_rules.json` 仍作为 UI 审计文件和旧缓存回退，新的完整 bundle 会同时写出 `ui_recipe.bin`，运行期优先使用二进制。

开发期可使用同一 emitter/validator 复现产物：

```powershell
dotnet run --project .\xphorror.PcModCompat\tools\UiRecipeTool\UiRecipeTool.csproj -c Release -- emit .\JipperResourcePack_release .\build\ui_recipe.bin 143
dotnet run --project .\xphorror.PcModCompat\tools\UiRecipeTool\UiRecipeTool.csproj -c Release -- validate .\build\ui_recipe.bin
dotnet run --project .\xphorror.PcModCompat\tools\UiRecipeTool\UiRecipeTool.csproj -c Release -- fixture .\build\ui_recipe_vm_fixture.bin
```

### 4.3 Unity API 代理与 Native executor

`ManagedSelfRender` 的 Unity API 调用链为：

```text
rewritten MOD call
  -> generated Il2CppInterop proxy
  -> cached bridge/member identity
  -> runtime metadata unique resolve
  -> typed IL2CPP call 或受控 runtime_invoke
  -> actual Unity object/result proxy
```

构造函数、静态方法、实例方法、字段、属性、delegate 参数、数组/集合和 value type 都必须由生成期 closure 与运行时 metadata 双重验证。返回的 `UnityEngine.Object` 不把裸指针直接暴露给任意 MOD 代码；proxy wrapper 保存受控 native handle、session generation 和 fake-null 检查能力。

Native recipe executor 继续区分冷路径和热路径：

- 对象创建、AddComponent、字体和资源解析等低频操作可以使用安全的 runtime invoke wrapper。
- 高频 `RectTransform`、颜色、显隐、文本等 setter，在 metadata 验证 ABI 后缓存 typed function pointer。
- 所有 Unity 对象操作只允许在 `UnityMain` 执行。
- 非主线程只能发布 command、snapshot 或 generation，不得直接调用 Unity API。

当前第一版 object executor 为了先验证行为，创建和 setter 都通过缓存 `MethodInfo + il2cpp_runtime_invoke` 执行；还没有把高频 setter 切到验证后的 typed function pointer。目标 r143 的真实 `libil2cpp.so` 已用 `llvm-readelf --dyn-syms` 确认导出 `il2cpp_object_new`、`il2cpp_array_new`、`il2cpp_runtime_invoke`、`il2cpp_string_new`、`il2cpp_gchandle_*`、class/type/field/method metadata API。运行时仍按 symbol + 完整 metadata identity 解析，不保存这些符号的 RVA/VA；嵌套类型的 `/`、`+` 只做规范化后再逐参数比较，完整签名失败时禁止退化到同名/参数数量匹配。

### 4.4 生命周期

Managed self-render 与 recipe executor 分别维护 rooted handle 表，但共享 owner/session/generation 规则，防止 IL2CPP GC 回收对象或旧 session 回调污染新对象。

每个 UI bundle 至少具有以下状态：

```text
Unloaded
Resolved
CreatedHidden
Visible
SceneInvalidated
Failed
```

场景切换时不得假定 `DontDestroyOnLoad` 足以保证所有引用有效。需要验证 Unity fake-null、对象 instance id 或受控 generation；失效后在 UnityMain 重建。

### 4.5 MOD 托管生命周期执行与 recipe fallback

最终目标优先执行重写后的 MOD 生命周期方法，而不是默认把它们全部翻译成兼容层状态机。

普通 MOD component 的默认路径不通过 `ClassInjector` 把 CoreCLR 类型注入 IL2CPP class table。arm64 当前也不启用 ModManager 自有 injected OnGUI host；原菜单借用现有真实 `BeginGUI instance_id` 获得 Unity IMGUI 上下文。兼容层仍为 MOD 创建 managed lifecycle object，并在 UnityMain dispatcher 中按已验证的注册关系调用：

- `Awake/Start` -> managed session 创建后的初始化调用。
- `OnEnable/OnDisable` -> MOD feature 显隐和资源生命周期调用。
- `Update/LateUpdate` -> 每个 Unity presentation opportunity 最多一次合并调用。
- `OnDestroy` -> 禁用、reload 或 session clear 时调用，随后统一回收 owner handles。
- `yield return null` -> 下一次 UnityMain dispatcher opportunity。
- `WaitForSecondsRealtime` -> monotonic deadline 到期后恢复到 UnityMain。
- `WaitForSeconds` -> Unity scaled-time anchor 到期后恢复到 UnityMain。
- 嵌套 `IEnumerator` -> 最多 32 层，子枚举器结束后恢复父枚举器；单次 dispatcher opportunity 最多执行 256 次立即转换。
- `StartCoroutine(IEnumerator|string[, object])` -> 立即执行到首个受支持 yield，返回在重写程序集内擦除为 `object` 的 owner-scoped handle。
- `StopCoroutine(IEnumerator|Coroutine|string)` / `StopAllCoroutines` -> 只操作同一 managed component owner 的受控协程。
- 普通 helper、delegate 和托管对象图按 rewritten assembly 正常执行；Unity/游戏对象访问必须经过 proxy closure。

若 MOD 原本依赖 `AddComponent<CustomMonoBehaviour>()`，当前 rewriter 将它改为“创建受控 managed lifecycle object + 绑定 owner GameObject proxy”，不把 CoreCLR type 伪装成 IL2CPP component。依赖 Unity 原生 `GetComponent<CustomMonoBehaviour>` 身份、序列化字段或其它无法代理的 class injection 语义时，当前路径明确标记不支持或要求后续受控注入能力。

当前生产子集会从实际主程序集与 bootstrap 两个根构建 MOD-owned PE metadata 闭包，并原子重写/缓存整个程序集包。闭包内可证明继承 `UnityEngine.MonoBehaviour` 的 `Add/Get/GetComponents/TryGetComponent` 泛型调用进入 registry；`Type` overload 由 Android host 在 generated proxy 类型和 CoreCLR 类型间动态分流。`Component.gameObject/transform` 对 managed component 返回 registry owner，对普通 proxy component 透传官方 getter。

registry 以 MOD id、resource generation 和 owner 原生指针为键；owner fake-null、`activeInHierarchy`、组件 `enabled`、session disable/reload 和单组件异常都会驱动对应生命周期与清理。`Start(): IEnumerator`、嵌套 enumerator 与常见显式 coroutine API 共用同一调度器；只接受 `yield null`、`WaitForSeconds` 和 `WaitForSecondsRealtime`，未知 yield fault 当前 session。立即 `Object.Destroy(Object)` 同步清理 managed 生命周期；`Destroy(Object,float)` 使用 Unity scaled time 调度 managed 清理，同时让真实 native 对象继续走官方销毁。CoreCLR component 不会被传给 native Destroy。entry 快照只在组件集合变化时重建，活跃 coroutine pump 不创建逐帧快照数组。

当前仍不等价于 Unity class injection：序列化 managed 字段、Inspector/native message system、`GetComponentsInChildren/Parent`、native MonoBehaviour receiver 上的托管 coroutine、自定义/未知 yield，以及把托管组件作为原生 `UnityEngine.Component` 传给未知 API 尚未支持，命中时必须失败关闭。

#### 4.5.1 Managed Component 两级后端

managed component 采用两级后端，不把所有 MOD 一律推进高风险 class injection：

```text
SurrogateManagedComponent（当前）
  -> rewriter + owner registry + UnityMain lifecycle
  -> 常见 Add/Get/GetComponents/TryGetComponent、Destroy、coroutine
  -> 没有真实 IL2CPP Component 身份

InjectedManagedComponent（计划）
  -> verified TypeInjectionPlan
  -> ModManager InjectionTypeRegistry
  -> brokered infrastructure detours
  -> 真实 Il2CppClass / Component identity / native message thunk
```

默认先使用 surrogate。只有静态扫描证明类型会流入未知 Unity API、依赖原生 `GetComponent<CustomType>`/cast、或确实需要 native message identity 时，才申请注入；用户或 MOD 不能直接调用 ClassInjector 绕过门禁。

受控注入的实现约束：

- ModManager 是 IL2CPP readiness、metadata resolver、HookBroker、ClassInjector 子集和注入类型生命周期的唯一所有者。
- ClassInjector 所需内部 detour 使用保留 infrastructure hook layer，不占普通 fixed-rule dispatcher，也不允许 direct Dobby。
- 类型键包含 owner、程序集身份、full name、base/interfaces 和 schema hash；同名不同 schema 失败关闭。
- 注入类型、ALC、thunk、delegate 和 GC roots 保留到进程退出。禁用 MOD 只关闭 callback gate，卸载类型需要重启。
- 普通游戏 Hook 热路径继续纯 native。注入 MonoBehaviour 的 native message thunk 可以进入 CoreCLR，但只能在固定 ABI、UnityMain/已审计 phase、异常屏障和 owner/session gate 下执行。
- runtime target 仍动态解析，禁止固定 RVA/VA、dump fallback 和上游 xref scanner 绕过 ModManager resolver。

真实 Component 身份不会自动解决所有剩余缺口：

- `GetComponent(s)InChildren/Parent` 和 List overload 可先在 surrogate registry 上实现，不依赖 class injection。
- `WaitUntil/WaitWhile/CustomYieldInstruction/AsyncOperation` 可扩展现有 scheduler；`WaitForFixedUpdate/WaitForEndOfFrame` 需要新的 UnityMain phase。
- ClassInjector 可以提供字段 metadata，但不等于 Unity 原生 MonoScript/type tree/Prefab serialization/Inspector。当前仍优先实现 owner-aware 自有持久化和 ModManager 设置 UI；只有获得独立 Unity serializer 证据后才宣称原生序列化兼容。

任意后台线程不得直接调用 Unity API。未知 yield instruction、未审计 P/Invoke、动态生成程序集、无法重写的反射和不受控 native library 进入 capability failure；纯托管计算与文件读取可在受控 worker 执行。

JAPatch/Harmony managed callback 在已启用会话中必须先于该 MOD 的下一次 `CompatUpdate` drain。这样 callback 内通过 `JALib.Tools.MainThread.Run` 排队的 UI/计数动作，会在同帧 `JAMod.CompatUpdate` 开头的 `MainThread.Drain` 执行；反向顺序会固定增加一帧延迟，并在 burst 输入下放大 backlog。C# dispatcher 使用 128-event 复用缓冲区做最多 8 批 drain，单帧上限 1024 条，既避免无界 UnityMain 工作，也显著降低 2048-slot native ring 被短 burst 填满的概率。诊断必须同时报告 native `queued/dropped`、dispatch `nativeDropped` 和 `budgetExhaustedFrames`；presentation ownership 即使没有 MOD `Update` 也必须维持 frame gate，保证 callback pump 不停。

UnityMain frame gate 不得依赖 gameplay telemetry 的 `UnityEngine.Time.frameCount` 快照。该快照只在 100ms timeline poll 且 gameplay controller 可用时更新；把它用于 managed frame 去重会把 HUD/KV 压到约 10Hz，并在准备态或退出 gameplay 后永久冻结。主 `Canvas.SendPreWillRenderCanvases` 与备用 `CanvasUpdateRegistry.PerformUpdate` 只安装其中一个；active 模式直接随已安装的 presentation opportunity 推进，并使用 native/managed 重入门禁阻止递归。只有 pending activation 使用 250ms 时间限流。

recipe presentation command 也必须有 UnityMain opportunity 预算。当前 native sink 每次最多提交 16 条 recipe command；未完成的 snapshot 留在 native pending buffer 中，完整处理后才 ack generation。若 pending 期间遇到 clear barrier 或 history gap，pending 被丢弃并按 fail-closed 销毁 runtime graph，优先保证不重放旧 session/旧 MOD 的 HUD 命令。

native recipe presentation 不允许把 Unity fake-null wrapper 直接交给 `runtime_invoke`。所有 GameObject/Component/TMP/Graphic/Image/RawImage/Canvas setter 必须先通过受控 alive guard；若 `m_CachedPtr` offset 无法解析，整条 native presentation 路径 fail-closed，而不是假定 wrapper 存活。该规则保护 self-render/fallback 切换、场景卸载和 graph retirement 期间的旧 command，不影响 MOD 托管自绘对象图本身。

native recipe presentation 禁止在 Canvas presentation callback 内调用 `GameObject.SetActive`。初始化阶段的 `SetActive` operation 只记录 recipe 意图，不进入 Unity API；运行期 `SetActive(false)` 语义改为销毁 runtime graph，后续 `SetActive(true)` 或 `EnsureGraph` 再重新物化对象。这样可以避免 Canvas rebuild / pre-render 阶段重入 Unity 激活生命周期，同时让兼容 fallback 保持可恢复。

当 managed lifecycle 无法通过门禁，而同一行为可静态证明时，编译器可以生成以下 recipe fallback：

- `Awake/Start` -> 创建后初始化程序。
- `OnEnable/OnDisable` -> bundle 显隐和资源生命周期程序。
- `Update/LateUpdate` -> event-driven 或 presentation update program。
- `OnDestroy` -> 资源释放程序。
- 可证明 helper -> 递归内联或 Native bytecode。

使用 fallback 必须把 feature 标记为 `partial` 或 `optimized_recipe`，不能报告成 managed self-render。

### 4.6 异步 HUD 逻辑运行时

Native HUD 运行时是 MOD self-render 的事件/时钟服务，也是 recipe/KeyViewer 批量后端的执行器。它不拥有 ManagedSelfRender 的 HUD 对象图。

Native 路径分为两个平面：

```text
AsyncHudLogic
  - ingress/event ordering
  - monotonic clock
  - deadline heap
  - held/count/KPS
  - coroutine state
  - analytic animation parameters
  - completed snapshot history

UnityPresentationSink
  - Unity object creation/destruction
  - metadata-resolved typed setter
  - latest snapshot selection
  - current geometry calculation/upload
  - Canvas/TMP dirty submission
```

连续动画优先保存 `start_tick/end_tick/curve/parameters`，不在工作线程按 60 Hz 空转。PresentationSink 每次得到运行机会时，根据绝对 `now_tick` 直接求当前值。

completed snapshot 应采用有界历史和 generation 发布。UnityMain 读取失败时可使用上一份完整快照，禁止读取写者工作区或无限自旋。

当前 Native scheduler 已实现以下约束：

- Realtime、Unity scaled、song、audio、map 五个时钟域各自使用有界最小堆，不跨时钟域比较原始 deadline。
- 每个时钟域再拆成“只接受新 anchor 确认”和“允许解析外推”两条队列。带副作用规则只能进入前者；可证明的纯视觉/计时规则才允许进入后者。
- 每条队列最多 64 个 task，一次最多发布 64 条 presentation command；溢出与跨 session 丢弃都有累计计数。
- session generation 不匹配的 task 在执行前丢弃，旧关卡任务不能污染新局。
- worker 通过 condition variable、KPS deadline 和 scheduler 的下一 raw deadline 唤醒，不固定频率轮询；新 task 和新 clock anchor 会显式唤醒 worker。
- presentation command 进入 64 槽 completed snapshot history，读取端使用 `try_lock`，非主线程仍不调用 Unity API。UnityMain 会回写已消费 generation；只有覆盖尚未消费的槽位才计入 history overflow。

这一层现在已经是普通 translator 的实际 recipe 输出链路。schema/emitter/loader 支持非空 lifecycle/bytecode，并完成 managed emitter、native parser 和 lifecycle scheduler 的互操作测试。Jipper 的 graph builder 会生成一个 `BundleLoad -> EnsureGraph` program，并为每个 root 生成 `OverlayStateChanged -> LoadOverlayVisible -> SetActive` program。它是当前可运行实现，但按新目标属于 fallback/优化基线。

overlay 可见性链路如下：

```text
verified fixed-op OverlayShow/OverlayShowPractice/OverlayHide
  -> native overlay scalar state (generation, visible)
  -> lifecycle worker wakeup
  -> LoadOverlayVisible (缺少 state 时 Deferred)
  -> SetActive presentation command
  -> UnityMain PresentationSink 创建/显隐 graph
```

worker 只传递 generation 和 bool 等标量；Unity API 仍只在 UnityMain sink 执行。`OverlayStateChanged` 是追加的 lifecycle trigger，旧 trigger/opcode 数值保持不变。

### 4.7 时钟域和副作用分类

编译器把 translated lifecycle 分为：

```text
RealtimeProgram
  input event / Stopwatch / DateTime / WaitForSecondsRealtime

ScaledTimeProgram
  WaitForSeconds / 可解析 Time.time 动画

FrameProgram
  yield null / frameCount / 依赖调用次数的 deltaTime
  实时 Unity 对象读写和其它主线程副作用
```

纯视觉 `position += speed * deltaTime` 等模式优先改写为基于起始时间的解析式。FrameProgram 不伪造主线程卡顿期间缺失的 Update 次数。

运行时维护三个时钟域：

- `RealtimeClock`：monotonic raw_ns。
- `UnityScaledClock`：带 `time/timeScale/frameCount` 锚点。
- `SongClock`：`dspTime/song position/session bridge`。

UnityMain 发布 timestamped clock anchor；纯视觉 scaled animation 可以在锚点间外推，有副作用或精确帧依赖的规则只能使用已发布锚点。暂停和场景重置按 clock domain 冻结或增加 session generation，不全局 Hook `Time.time`。

### 4.8 UnityPresentationSink 提交链

PresentationSink 只负责 recipe/system HUD，不替 MOD self-render 创建对象。Managed self-render 和 PresentationSink 共享同一组 UnityMain opportunity，但具有独立 owner registry、异常域和预算。

建议的 opportunity 顺序固定为：

```text
drain native typed events / publish immutable snapshots
  -> managed MOD lifecycle/update dispatcher
  -> recipe/system PresentationSink
  -> official Canvas callback original
```

Managed dispatcher 必须有重入门禁；同一 MOD callback 未结束时不允许递归进入下一帧 callback。某个 MOD 抛出异常只 fault 该 MOD/feature，不能跳过 system sink 或官方 original。

PresentationSink 不推进逻辑时间。它按 publication generation 顺序消费 64 槽历史中仍保留的 command snapshot；managed 诊断读取仍可只取 latest。这样同一 Unity 帧前先后发布的 `SetActive/Destroy/Invalidate` 不会因为只看最新快照而直接丢失。若生产速度仍覆盖未消费 generation，sink 会记录 gap、销毁当前 runtime graph 并停止应用后续增量命令，直到明确的 clear barrier 后恢复，禁止静默保留错误 HUD。r143 的主/备用提交点均通过 metadata 动态解析：

1. `UnityEngine.Canvas.SendPreWillRenderCanvases()` original 前。
2. `UnityEngine.UI.CanvasUpdateRegistry.PerformUpdate()` original 前。
3. 两者都不可用时当前明确失败关闭；独立场景主线程 fallback 仍是后续项，尚未实现。

不以 EGL/render thread 作为标准 HUD fallback。静态 HUD 仅在 generation 变化时提交；存在解析式动画时按当前 monotonic tick 更新批量几何。

当前实现状态：

- `ui_recipe_lifecycle_runtime` 在 native bundle 加载阶段先验证所有 descriptor，再追加稳定 program index；注册失败不会把半成品 bundle 放入 Hook Slot 表。clear 会先停用程序、清空 scheduler、等待正在执行的 VM 退出，再回收 program registry，因此反复 reload 不会耗尽 256 个槽位。
- `DeferredRetryDelayNs` 已进入实际调度：依赖 generation 改变后仍需达到 retry deadline 才会重试；产生 Deferred 时强制 worker 重新读取一次最新 input/clock，避免 anchor 发布与 deferred interest 建立之间的丢唤醒竞态。
- `HudLogicWorker` 在输入/clock generation 变化时触发 lifecycle，scheduler 到期后执行 VM；`Completed` 的 `r0/r1/f0/f1` 被映射成 presentation command。
- `pccompat_presentation_abi.h` 提供固定 64 条 command 的 ABI v1，包含 `struct_size`、`abi_version`、`publication_generation`、session、丢弃/溢出计数。清理规则会推进 generation 并清空历史，消费端不会继续看到旧命令。
- PresentationSink 已接 `unity_presentation_objects`：按 publication generation 顺序排空当前 64 槽历史，再按 bundle generation 查找 graph，在 UnityMain 创建/更新/销毁 `GameObject + RectTransform + Canvas/CanvasScaler/Image/TMP/CanvasRenderer`，最后调用官方原函数。单次 opportunity 最多消费 16 条 command；对象层返回实际 consumed 数和 deferred 状态，延期的 `EnsureGraph` 不前移 cursor、不 ack snapshot，也不允许后续增量命令越过。snapshot range 直接引用原 snapshot，不复制完整 64-command 数组。
- graph 物化按 `CreateNodes -> InitializeNodes -> ActivateCanvases` 增量推进，每次 opportunity 最多 12 个粗粒度 Unity 操作。普通组件、父子关系及非 Canvas 初始化先在无 Canvas 层级完成，Canvas/CanvasScaler 最后按节点原子配置；clear/hide/discard/history gap 可从任意阶段销毁半成品并释放 GCHandle。普通更新不再逐命令扫描全图，只检查目标 wrapper；`EnsureGraph/SetActive(true)` 保留全图存活验证。
- retired queue 满时 runtime graph 继续保留对象和 GCHandle 所有权，等待 UnityMain 扫描清理，不再丢弃 cleanup 责任。每次最多回收 4 个 graph，销毁时只直接提交 root 与尚未完成 `SetParent` 的孤立对象，已经挂入层级的子节点由 Unity root destroy 级联。worker/render/EGL 线程仍不调用 Unity API，未知 payload 也不会被当作对象地址。
- resource resolver 按 `modId + featureGroupId + assetName + expectedType` 请求精确资源。Native 先在 `g_graph_lock` 内快照最多 4 个请求，锁外调用 managed resolver，再回锁内按 bundle/binding 身份复核并执行 metadata-resolved setter；余项由 pending hint 在后续 UnityMain opportunity 续跑，回调期间发生 retire/reload 时不会写入旧 graph。
- resolver 返回 pending/unavailable 后记录 per-binding waiting bit，不在每个 presentation snapshot 重复跨入 CoreCLR。resource session 发布、成功 completion、cache hit 和 asset completion 通过合并的 UnityMain refresh 重新开放解析。
- 已接 `Image.set_sprite`、`RawImage.set_texture`、`Graphic.set_material`、`TMP_Text.set_font/set_fontSharedMaterial/set_fontMaterial`。卸载前先清除组件引用；pending AssetBundle request 完成或失败后才执行最终 `Unload(true)`。
- command catalog v1 固定为 `EnsureGraph=1`、`SetActive=2`、`SetRect=3`、`SetText=4`、`SetColor=5`、`SetFontSize=6`、`DestroyGraph=7`、`InvalidateTarget=8`。未知值只计数并丢弃。
- component operation 的 `Payload0..3` 各自保存一个标量：float 使用 IEEE-754 low 32-bit；`SetRect` 为 `x/y/width/height`，`SetAnchors` 为 `minX/minY/maxX/maxY`，颜色为 `r/g/b/a`。presentation command 只有 `r0/r1/f0/f1`：`SetRect` 使用 `f0/f1=x/y`、`r0/r1=width/height float bits`；`SetColor` 使用 `f0/f1=r/g`、`r0/r1=b/a float bits`。
- `SetText` 不接受 native pointer。`payload0` 只能引用目标 node 初始化 operation 中的静态 `SetText` slot；动态任意字符串需要后续版本化 string table/snapshot ABI。
- PresentationSink stats 继续接受 44-byte ABI v1 和 64-byte ABI v2；当前 managed 请求 80-byte ABI v3，并额外显示 presentation history overflow、stream gap 和 fail-closed 状态。
- recipe 后端下一步仍是让 lowerer 为 KeyViewer/SideImage 及更广 prefab 组件生成具体 Sprite/Texture/Material binding，并补动态文本 snapshot、批量 Mesh 和解析式动画。
- 主后端的 managed lifecycle dispatcher、组件 registry、伴随程序集、VirtualBundle 和基础 proxy surface 已建立。下一步是补层级 component 查询与常见 custom yield，完成 Jipper 自绘实机生命周期/性能验收，再按实际调用流决定哪些类型需要申请真实 IL2CPP 注入。当前仍不宣称支持任意 PC HUD。

### 4.9 设置 schema

PC MOD 自己的设置菜单是默认真源。兼容层优先执行 `UnityModManager.ModEntry.OnGUI/OnSaveGUI`、JAMod/Feature `OnGUI`、JALib `SettingGUI` 或 MOD 自建 Canvas 设置页，不从字段重新生成一份近似菜单。

ModManager 的 MOD 详情页只提供“打开 MOD 设置”入口。UMM/JALib/Unity IMGUI 菜单进入 owner-scoped Unity IMGUI host：arm64 路径锁定一个稳定的真实 `BeginGUI instance_id`，在 original 返回后、宿主自身 OnGUI 执行前调用 MOD 菜单；同一 Unity event 的其它 host 不重复派发。不能把 Unity `GUI/GUILayout` 嵌入 ImGui.NET 后声称等价。打开请求成功即进入 `Opening` modal、隐藏 ModManager 并取得 pointer/IME 所有权，不等待首个 `Open` draw。Android back 先关闭菜单，软键盘输入不进入 KeyViewer 或 gameplay。

关闭/保存时按 MOD 原语义调用 `OnSaveGUI` 或对应保存入口；原即时保存行为继续即时保存。文件路径重定向到 MOD 私有目录，但序列化格式和字段仍由 MOD 代码决定。菜单 callback、资源和 Unity API 仍受 owner/session/generation、UnityMain、异常熔断和 capability 门禁约束。

`mod_settings.schema` 同时承担导入审计、宿主兼容页 live mirror 和原菜单故障 fallback。ModManager 齿轮默认打开独立宿主页，并常驻显示可验证的 primitive/enum 绑定；这些控件不保存副本，而是排队到 UnityMain 写入原 JALib/UMM 对象，`Feature.Enabled` 调原 setter，其余字段调原保存入口。原菜单 save/close 是反向同步边界：UnityMain 重新读取全部 binding 并原子发布 snapshot，不逐帧反射轮询。原 MOD 菜单仍由宿主页顶部的明确入口打开，PcCompat 控件不得注入 MOD 的 IMGUI/Canvas 树。原 `OnGUI/SettingGUI` callback 抛异常时，只 fault 当前 settings surface：立即关闭原 Modal、释放 pointer/软键盘所有权、输出完整诊断，然后返回宿主页并让同一 binding renderer 切换为红色 `Fallback`；HUD、Hook 和 gameplay callback 继续运行。

fallback 菜单只为“字段/属性可读写、类型转换明确、范围/枚举有效”的 verified binding 创建可编辑控件。可证明的原 change callback 在 UnityMain 执行；只能读取或 callback 不安全的条目只读显示并标注原因，无法解析的条目不显示且进入诊断列表。打开菜单时保存 verified 字段快照，应用时写回原字段并执行原 callback；手动绑定只能消除成员歧义，仍须通过类型、线程和调用图验证，不能强制写未知成员。

`OnSaveGUI` 或原保存入口失败时保持 fallback 菜单打开，保留当前内存值，明确显示未保存并允许重试或放弃，不能改写成兼容层 JSON 后伪报成功。只有 native crash、owner 越界或跨 generation 污染才升级为整个 MOD fault。

移动端专属的 `Auto/Touch/External/Hybrid`、`TouchKeyCount` 和兼容绘制授权放在 ModManager 的普通 MOD 设置页，不能伪装成 MOD 原菜单项；Adapter role 与证据矩阵属于高级诊断，默认折叠。settings surface 的完整异常写入 owner data 目录的诊断文件；Logcat 只输出限频单行摘要。

当前实现（2026-07-25）已覆盖 UMM `OnShowGUI/OnGUI/OnFixedGUI/OnSaveGUI/OnHideGUI`、JAMod/Feature `OnGUI/OnShowGUI/OnHideGUI` 和 JALib `SettingGUI` 常用控件。arm64 调用主路径是按 gate 启停的 native borrowed `BeginGUI` host，owner ALC 使用 generated `Screen/GUI/GUILayout/GUILayoutUtility` proxy；布局栈按 Area/Vertical/Scroll 层级跟踪并在异常时反向清理。gate 关闭时 BeginGUI 热路径只做一次原子读，不跨入 CoreCLR。ModManager 只负责 `Closed/Opening/Open/Faulted` 路由，settings fault 写入 `.pccompat/last_settings_failure.txt` 并回到红色 `Fallback` 兼容页，不撤销 HUD presentation ownership。JALib 保存沿用原 `Settings.json` 的 `Setting/Feature` 结构、`.bak` 和未知字段保留，不写旁路配置。

自建 Canvas 设置页发现、Android modal pointer/软键盘所有权和 verified `mod_settings.schema` fallback 主链已接通：打开回调前先记录全部可见 Canvas baseline，回调后只允许“不在 baseline 且属于 owner/子层级”的新出现或重新激活 Canvas 取得设置 surface。打开前已可见的 MOD HUD Canvas 即使 owner 匹配也不得认作设置页，否则会跳过真实 `CompatOnGUI` 并在空菜单状态隐藏 ModManager。Canvas 失活/销毁结束原 modal；Activity 在 modal 期间仍把真实 MotionEvent 交给 Unity，使 IMGUI/Canvas 设置面获得原生事件，但不再送入 gameplay/AsyncInput 观察链，Back 转成 close request。surface kind 决定 Unity UI 隔离：`Opening` 和 `UnityImGui` 通过 ModManager 唯一实施的永久 HookBroker 槽跳过 metadata 动态解析的 `UnityEngine.EventSystems.EventSystem.Update()`，阻止场景 uGUI 消费同一事件；`UnityCanvas` 保持该入口原样转发，确保 MOD 自建 Canvas 菜单可交互。Activity 分流不是 gameplay 隔离的唯一依据：AsyncInput 永久 hook 通过缓存的 `modmanager_modal_input_is_active` 跨 SO 原子查询关闭 capture/replay，并让 `scrPlayer.touchEnabled`、`ValidInputWasTriggered/Released`、`CountValidKeysPressed`、`holding`、`RDInput.GetMain/GetMainPressCount` 在 modal 期间失败关闭；该热查询不得获取 ModManager overlay mutex。schema fallback 原子生成并按程序集 hash/revision 绑定 primitive/enum 字段，从同指纹旧 schema 继承 label/group 与数值 range；Feature `Enabled` 只走原 setter，其余字段在 UnityMain 写回后调用原保存入口，apply/save 错误分开发布且部分写入不会伪报保存。当前仍需实机验收 Jipper 的 IMGUI 触摸不透传、Canvas 触摸、软键盘、Feature 展开/关闭/保存和故障后 fallback，且对没有 JALib/UMM owner 绑定的任意自建 Canvas 仍保持 fail-closed。fallback 也仍不是 MOD 原菜单本身，必须在 UI/诊断中标明 owner 和降级原因。

依赖闭包 generated proxy 允许只保留 accessor 方法而不重建 CLR property metadata。设置后端解析 `Screen.width/height`、`GUI.skin` 和 `GUISkin.textField` 时必须先接受标准 property accessor，再以精确 static/instance、返回类型和零参数签名回退到 `get_*`；禁止只用 `Type.GetProperty` 判定能力，也禁止按名称绑定签名不明的同名方法。

generated proxy 也可能同时公开 `GUILayoutOption[]` convenience overload 与 Il2CppInterop array wrapper overload。设置后端必须按每个目标方法的完整前缀参数独立解析 options 容器，不能使用 `SingleOrDefault` 假设唯一，也不能把一个方法的空 options 对象传给另一个参数类型。选择顺序优先可跨帧复用的 `Il2CppReferenceArray`/`Il2CppStructArray`，其后才是其它 generic 与 CLR array；空容器按 `Type` 缓存，避免每个控件调用产生 IL2CPP 数组转换和分配。最终 Android proxy/runtime 的独立 ALC 构造测试是这条合同的构建期门禁。

移动端 Unity IMGUI 使用单一逻辑坐标空间。设置 frame 保存当前 `GUI.matrix`，将 DPI 推导的缩放矩阵乘到原矩阵右侧；panel、字体、padding、touch height 和 MOD 自定义 `GUIStyle` 参数均保持 PC/逻辑像素，不能再逐项乘同一 DPI，否则会二次缩放。正常结束、布局清理异常和 MOD callback 异常都必须恢复原矩阵并清除 active 状态；恢复失败进入 settings fault，不能把缩放泄漏到游戏或下一 MOD。generated proxy 必须显式保留手机 metadata 中真实存在的 `GUI.set_matrix(Matrix4x4)`、`Matrix4x4.TRS` 和 `Matrix4x4.op_Multiply`，禁止用硬编码 RVA/VA 或假定初始矩阵恒为 identity。

settings surface 与 HUD managed self-render 是两条独立 lifecycle。打开原菜单不得调用 self-render activation，不得取得或切换 presentation ownership，也不得因以前的 HUD activation failure 拒绝设置；只要 session 尚未释放，`Closed/Opening/Open/Faulted` 就由 settings controller 自己推进。`Loaded + Opening` 通过 native frame 总需求 gate 在下一次 UnityMain frame执行 `CompatOpenGUI`、owner-scoped Canvas probe 和 surface 发布；`Save/Close/schema/Canvas visibility` 也走该 frame lane。只有 Unity IMGUI 的 `_draw/CompatOnGUI` 进入锁定 host 的真实 `BeginGUI` 上下文。Open 后的 IMGUI draw 使用独立 `s_managedOnGUISessions` 和 OnGUI demand gate，不依赖 frame session、HUD/KeyViewer 或 managed self-render mode；settings-only frame 不执行 MOD `CompatUpdate`、managed callback 或 managed component OnGUI。frame/OnGUI demand 变化必须分别立即重算对应 gate。

`Faulted` 只隔离上一次设置打开/绘制尝试，不是永久熔断。异常出口必须先执行 best-effort close、释放 Canvas claim、modal input 与布局状态；用户下一次显式点击“设置”是允许重新进入 `Opening` 的重试边界。相同错误可再次进入 `Faulted` 并回到 fallback，但不得要求重启 APP 才能重试。

ModManager 隐藏原菜单期间仍拥有 route/modal 的收尾职责。external settings route 本身必须加入 hidden-render predicate，使 renderer 在原菜单自行关闭、Android Back 请求关闭或 surface fault 后继续轮询到 `Closed/Faulted`，再原子释放 modal input、恢复 overlay 并清除 route；否则菜单只能打开一次。该隐藏轮询只在 external route 存续期间启用，不能让普通已加载 MOD 常驻 ImGui 热路径。

### 4.10 Owner-scoped VFS

导入后的 MOD 文件分为不可变 package 与可写 data overlay：

```text
mods/<modId>/package/<assemblyHash>/
mods/<modId>/data/
```

`package` 保存原包、重写程序集、伴随程序集和导入产物，以内容 hash 定位且运行期只读。`data` 保存 MOD 设置、计数、缓存和其它可写文件。`Main.Instance.Path`、UMM `ModEntry.Path` 及受支持的等价根路径通过 owner-scoped VFS 映射；相对路径读取先查 `data`，未命中再查当前 `package`，写入、创建、删除、移动和备份只允许落入 `data`。绝对路径逃逸、跨 owner 路径和符号链接/重解析点穿透均失败关闭。

VFS 不转换序列化格式，不合并字段，也不改变 MOD 的同步/异步保存时机和文件操作顺序。Jipper 继续按原代码读写 `KeyCount.dat` 与 `KeyCount.dat.bak`。更新 MOD 时切换 package hash 并保留 data；卸载时由用户明确选择保留或同时删除 data。删除 package/reload 前，旧 generation 的文件任务必须完成、取消或因 owner token 失效而失败，不能写入新 package generation。

## 5. KeyViewer 通用模型

### 5.1 Native 输入事件

统一事件包含：

```text
sequence
raw_ns
sourceKind    PhysicalKeyboard / PhysicalMouse / PhysicalController /
              Touch / LogicalAction / GameplayAccepted / Synthetic
deviceId
identityKind  UnityKeyCode / AndroidKeyCode / WindowsVirtualKey /
              MouseButton / ControllerControl / TouchLane / ActionId / GameAction
identityValue
contactId
phase         Down / Up / Cancel
x / y         仅触摸存在
syntheticKind Human / Auto / OldAuto / TestMacro / InternalReplay
sessionGeneration
flags
```

事件流和 held snapshot 分离：

- 事件流用于次数、KPS、rain 和边缘动画。
- held snapshot 用于按键格持续高亮和恢复状态。

#### 5.1.1 输入源路由

物理事件只能由一个 active producer 发布，不能同时观察 Activity 与异步队列：

```text
AsyncInput enabled
  -> async_input.c nativeOnTouchEvent/nativeOnKeyEvent 的 native snapshot
  -> 在 test-macro/capture/gameplay gate 之前发布 Physical event

AsyncInput disabled or absent
  -> ExtraMenuUnityPlayerActivity.dispatchTouchEvent/dispatchKeyEvent
  -> 在 super.dispatch* / Unity frame polling 之前发布 Physical event
```

异步库通过版本化 C observer ABI 向 ModManager 注册/发布完整 raw event；至少包含 `raw_ns`、action/phase、source/device、pointer id/count、坐标/viewport，以及键盘 keyCode/scanCode/meta/repeat/flags。ModManager 缓存 `ADOFAIAsyncInput_IsEnabled`/observer 状态，不在每次事件上反复做符号解析。输入模式切换提升 `producerEpoch`，先向旧 producer 的全部 active contact/key 发布 CANCEL，再接受新 producer；全局 sequence 不回绕。Activity producer 在异步 enabled epoch 内必须丢弃自身观察结果，避免双计数。

异步 physical event 在 capture gate 关闭时仍可发布，因为 Jipper 等 MOD 的原规则会在菜单、选关和关卡外计数。异步 seal、replay、被 test macro 屏蔽和 gameplay gate 接受是该 raw event 的后续关联状态，不得反向抹除已经发生的物理输入。

`GameplayAccepted` 不是物理输入别名。该链现已由 RecipeCompiler 生成平台 after-rule，并由 HookBroker 观察 `scrPlayer.HitInputEvent(isAuto, state)` 的成功返回；`isAuto=true` 或异步测试宏启用时标记 Synthetic，其余成功返回按 `InputEventState` 发布 GameAction Down/Up。accepted stream 使用独立 sequence/session count/ring，不修改 physical stream。该入口已经过输入事件 FFX 的 `ignoreInput` 过滤，但不代表判定结果；Perfect/TooEarly 等属于独立 Judgement stream。若无法把 accepted event 唯一关联到某个 raw physical sequence，只保留 GameAction identity，禁止猜测成具体键或 TouchLane。

`scrPlayer.ValidInputWasTriggered()` 与 `CountValidKeysPressed()` 不作为观察 hook：它们在同一次 `Simulated_PlayerControl_Update()` 内可能重复调用，且会更新 limiter、`downKeysDuration`、`keyFrequency` 和 `keyTotal`。Hook 这些方法会产生重复计数或把观察行为耦合到官方副作用。

### 5.2 触点生命周期

Android `pointerId` 可能稀疏、复用并在数组中换位，因此内部需要稳定 contact slot：

```text
pointerId -> active contact slot -> Down 到 Up/Cancel 保持不变
```

contact slot 只用于正确跟踪多点触摸，不等于最终显示键格。最终 HUD 显示为独立 `T1..TN` 虚拟触摸键。

`N` 来自独立的 `TouchKeyCount`，默认 `10`。DOWN 坐标按有效屏幕宽度分区；同列多指使用引用计数，UP/CANCEL 只释放对应 contact。

### 5.3 键盘映射

键盘输入必须尽可能精确保留物理键身份：

```text
Android KeyEvent keyCode + scanCode + metaState
  -> compatibility key identity
  -> Unity KeyCode / Windows VK aliases
  -> MOD 配置中的对应键格
```

同一个物理键可以具有多个兼容别名，但一个事件只能计数一次。别名只用于满足不同 MOD API 的查询方式。

输入 identity 与显示 lane 分离。一个 lane 可以绑定 `DirectIdentity`、`AliasSet`、`LogicalAction`、`GameAction`、`Chord`、`AnyOf`、`TouchLane` 或受控 wildcard。首版生产承诺只要求 `DirectIdentity/AliasSet/TouchLane`；其余绑定无法证明时按子能力失败，不得静默改成键盘键。

### 5.4 Synthetic 输入

以下来源默认不进入 KeyViewer：

- AUTO
- oldAuto
- 异步测试宏
- 兼容层内部 replay

这些输入仍可进入单独诊断快照，供自动化测试使用。

### 5.5 HUD input ownership

每个 feature recipe 具有可独立覆盖的输入策略：

- `Auto`：使用编译器推断，默认选项。
- `ObserveOnly`：只观察，不消费；KeyViewer 默认使用。
- `ConsumeInsideBounds`：命中交互区域后阻止进入 gameplay ingress。
- `Modal`：显示期间消费相关触摸。
- `Disabled`：禁用该 feature 的交互。

用户可以在 ModManager 中逐 feature 修改策略。pointer 在 DOWN 时绑定 owner，直到 UP/CANCEL 不改变；策略切换、HUD 隐藏、场景切换或 layout generation 失效时必须发送 CANCEL。被消费事件不进入异步 gameplay ingress，也不计入 KeyViewer。

### 5.6 KeyViewerFeature 与 LaneGroup

识别和配置单位不是“每 MOD 一个 KeyViewer 类型”，而是：

```text
Mod
  -> KeyViewerFeature[0..N]
       -> LaneGroup[0..N]
            -> Lane[0..N]
```

每个 feature 独立持有生命周期、输入过滤器、event cursor、计数/KPS、rain、设置绑定、presentation generation 和故障状态；同一 MOD 的 feature 共享 `ModActor` 与资源 session。共享根对象、生命周期、设置和 rain manager 的 hand/foot/ghost 属于同一 feature 的多个 lane group；根对象或生命周期独立的 viewer 拆成多个 feature。同一个 observe-only 输入事件可以 fan-out 给多个 feature，但每个 feature 内按 canonical identity 和自身 binding 决定计数，不重复进入 gameplay ingress。

稳定 lane 身份为：

```text
modId / featureId / laneGroupId / laneId / layoutGeneration
```

布局 generation 变化前必须向旧 active contact 发布 CANCEL。移动端触摸模式的 N 来自 `TouchKeyCount`；导入器按 `lane factory -> template clone -> element constructor -> 可证明 UI 构造子图` 的顺序重建 MOD 风格槽位。只能固定显示原数量且无法安全隐藏/重排的实现将 Touch 子能力标为 `partial/unsupported`，不能留下不可响应的多余键位。

### 5.7 KeyViewer Adapter IR 与手动角色绑定

自动发现按行为数据流工作，不依赖 `KeyViewer` 名称：从输入查询/事件源、Thread/Update/UMM/Harmony 调度入口、held/count/KPS 状态、Unity presentation sink、资源和生命周期建立候选图，并证明 `input -> transition -> state -> presentation/rain` 链。结果写入版本化 `keyviewer_adapter.json`，进入 managed cache key。

首版实现目前只完成“候选图 + 保守证据”的阶段：方法调用边和字段 writer-reader 边用于发现同一行为组件，精确 Input/PInvoke 可达性可以证明输入源；尚未构建完整 CFG、支配关系、SSA/alias、跨线程 happens-before 和资源 owner 证明。扫描器不得因为候选唯一就把后续能力升级为 `Proven`，也不得让诊断文件直接改变运行时行为。

识别结果不提供会掩盖关键断点的单一兼容率，而对输入、lane、transition、count、KPS、rain、presentation、visibility、inputActivation、settings 和 persistence 分别记录：

```text
Proven       调用链与类型/线程/资源约束闭合，可自动启用
Probable     唯一高置信候选但仍有未闭合 helper/反射/资源路径，需用户确认
Ambiguous    多个等价候选，必须手动绑定角色
Unsupported  ABI/线程/PInvoke/Unity API/资源验证失败，禁止启用
```

UI 必须展示每项证据、首个证明断点和受影响功能，例如“count Proven / rain Probable / settings Unsupported”。用户确认 `Probable` 只选择候选，不把未满足的安全门禁提升为 Proven；运行期仍按实际 capability 执行或失败关闭。

子能力按 Adapter IR 依赖图分为核心与可选。`input -> transition -> MOD count/state -> presentation lifecycle` 是 KeyViewer 核心闭包，任一节点 `Unsupported` 时整个 feature 不启用。rain、ghost rain、设置菜单、附加统计和装饰资源只有在 MOD 原控制流本身提供关闭路径时才可作为可选能力独立降级；例如 Jipper 的 `useRain=false` 是可证明的原生路径，因此 rain 不支持时可以关闭 rain 并保留计数。始终参与计数的 lane、去重或状态转移不能为了部分兼容而删除或改写。UI 与诊断必须列出被关闭的具体子能力、原关闭路径和影响。

自动识别失败时允许用户从已扫描程序集候选中指定根类型，并按需补充以下角色：

```text
ControllerType
EnableMethod / DisableMethod
InputProfile
InputListenerMethod / InputTickMethod / EventSubscribeMethod
LaneCollection / BindingProvider / LaneFactory / LaneTemplate / LaneContainer
HeldState / CountState / TotalState / KpsWindow
FrameUpdater / PresentationRoot
RainProducer / RainConsumer / RainPool
```

一个 MOD 可以保存多项 feature override。用户绑定只解决身份和歧义，不绕过签名、调用图、线程、P/Invoke、Unity API 和资源验证；不允许手输 RVA/VA 或让未知后台线程获得 Unity API 权限。配置绑定源程序集 SHA-256/MVID、adapter schema 版本、目标游戏 revision 和代理 surface hash；任一变化后自动失效并重新验证。UI 必须逐子能力显示 `supported/partial/unsupported` 和首个证明断点。

当前首版控制面使用以下独立文件，不修改自动生成的 Adapter：

```text
<mod>/.pccompat/keyviewer_overrides.json
  formatVersion = keyviewer-overrides-v1
  packageSha256 / targetGameRevision / proxySurfaceHash
  assemblies[] = complete SHA-256 + MVID set
  features[] = enabled + inputMode + touchLaneCount + selected role candidates
```

保存前后都执行候选和指纹校验；非法 lane 数、缺失程序集指纹、重复 role、伪造 type/member 或旧 Adapter 候选均拒绝。`Touch/Hybrid` 在 Proven 静态 lane 或 verified lowered plan 存在时可启用正式 consumer；`Auto` 的会话冻结与 external canonical identity 尚未闭合，不能据此自动接管。物理 producer 来源由 Native RealtimeEventCore 直接报告为 `AsyncInput` 或 `OfficialActivity`，UI 不通过设置值猜测。

输入 profile 首批包括 `LegacyUnityPolling / Win32Polling / InputSystemEvent / RewiredPolling / HarmonyGameAction / ManagedEventSource / CustomProvider`。源 API 被重写到 `IsHeld/WasPressed/WasReleased/ReadTransition` 或 typed event ABI；`GetAsyncKeyState` 的 held 高位和 per-callsite 边沿游标分别保留。`LogicalAction` 和 `GameplayAccepted` 不具有物理键身份，除非用户另外绑定物理源，否则不能显示成 Z/X/Space。

### 5.8 ModActor 执行边界

每个已加载 MOD 拥有一个逻辑 actor，不默认拥有一条物理线程。actor 在共享固定 worker 池上按 mailbox 从空到非空的边沿调度，通过 scheduled/running gate 保证同一 MOD 不并发执行；当前 managed 实现默认 mailbox 容量 256，单个 turn 最多 64 项并采用 4ms cooperative slice，未完成任务重新排队。单个 callback 本身不能被抢占，因此导入器仍必须拒绝未经证明的阻塞工作。状态事件不得丢失，过期纯视觉 rain 可以淘汰，保存和重复设置任务可以合并。

过载时 `DOWN/UP/CANCEL`、MOD count delta、reset、settings、lifecycle、lane generation 和持久化任务不得丢失；重复 held 查询、同值 presentation update 与重复保存请求可以合并，已过期且尚未显示的纯视觉 rain 帧和中间动画采样点可以淘汰。若有序状态队列达到硬上限，feature 必须停止 presentation 并进入可诊断故障，不能清空后静默继续；游戏输入和官方判定链不得等待 MOD actor。已经接受的计数边沿必须由独立于 UnityMain presentation backlog 的可靠路径继续保存。

Native/HudLogicWorker 只维护事件顺序、raw 时间、canonical physical held 引用计数和 touch contact 真值，并可生成不参与展示的审计计数。按键次数、Total、KPS、rain、过滤条件和清零时机属于 MOD 自己的状态机语义，必须由 Adapter IR 回放原逻辑或由可证明等价的 lowering 计算，不能用兼容层统一计数器覆盖。MOD 实例方法默认只在 UnityMain 的 batch replay/presentation 阶段执行。导入器证明为纯逻辑的代码可以降低为 adapter bytecode 在 actor worker 运行，但 worker 只修改 adapter-owned state，不能与 UnityMain 并发修改 MOD 实例字段。专用物理 worker 仅作为经验证纯计算 MOD 的高级策略；线程不提供 native crash 或地址空间隔离。

Native audit 在 release 默认只维护低开销聚合项：`rawDown/eligibleDown/adapterConsumed/modCountDelta/rainEmitted/dropReason`。逐事件审计仅在 TRACE 开启后记录。audit 与 MOD 语义结果不一致时只输出差异，不自动改写 MOD 状态；只有用户显式启用的兼容绘制可以消费通用计数，且 UI/诊断必须标明它不是 MOD 原计数。

诊断导出包含与同一 event sequence 区间对应的 MOD 当前状态快照：settings、Adapter role、feature/lane state、计数/KPS/rain、lifecycle/presentation、Hook/queue/fault，以及从 MOD root 可达且可无副作用读取的 verified 字段。快照在 UnityMain 与对应 ModActor safe point 建立一致性边界；未知 property getter、任意方法和未验证 native 读取不得为导出而执行。对象引用导出稳定 owner/type/identity，数组、集合、字符串、递归深度和总字节数受预算约束；截断、循环引用和不可读字段必须以字段路径及原因显式记录，不能静默省略。

### 5.9 可见性与输入激活

KeyViewer 的显示条件和输入统计条件是两个独立角色，不能因为 HUD root 当前可见就推断输入一定应计数，也不能因为输入 callback 存在就让 HUD 常驻。两个角色的默认值来自 MOD 原控制流，不由兼容层统一设成关卡态：

```yaml
visibility:
  predicate: <proven MOD predicate>
inputActivation:
  predicate: <proven MOD predicate>
```

导入器从 `OnEnable/OnDisable`、scene callback、Harmony Show/Hide patch、`GameObject.SetActive` 条件、输入线程循环条件和游戏状态查询中分别证明两个 predicate。二者即使最终相同，Adapter IR 中仍保存独立角色，以兼容暂停时隐藏但继续统计、菜单预览以及只在特定模式计数的 MOD。

计数规则同样是 Adapter IR 的显式语义，包括触发边沿、参与 lane、去重、KPS 时钟/窗口、持久化范围和唯一合法 reset 入口。暂停、失败、重试、checkpoint、场景卸载和新 gameplay generation 只在 MOD 原控制流明确要求时修改这些状态。兼容层的 `sessionGeneration` 负责拒绝迟到 command、callback 和 resource completion，不具有隐式清空 MOD 计数状态的权限。

Jipper 当前源码给出了一个明确反例：

```text
visibility       = AlwaysWhileFeatureEnabled (DontDestroyOnLoad)
inputActivation  = AlwaysWhileFeatureEnabled (listener while Enabled)
main/foot count  = rising edge, persistent Count[] + TotalCount
ghost input      = rain only, not counted
KPS              = main + foot rising edges in Stopwatch 1000 ms window
reset            = only the confirmed Reset Count settings action
pause/retry/scene unload = no KeyViewer count reset
storage          = KeyCount.dat with .bak fallback
```

因此 Jipper 在菜单、选关和关卡外的实体输入也会累计；这是其原行为，不应被兼容层默认改成 `GameplayOnly`。自动识别无法证明任一角色或计数规则时，该 feature 必须隐藏并停止计数，不能猜测为 `Always` 或套用统一计数器。

用户可以显式启用移动端 `Compatibility Override`，仅覆盖 `inputActivation` 的作用域为 `GameplayOnly / GameplayAndEditorPlay / Always / Custom`。该项默认关闭，UI 必须标注“改变 MOD 原行为”，并记录原 predicate 与覆盖来源。覆盖不得修改计数边沿、lane 去重、KPS 时钟/窗口、rain、持久化格式、保存时机或 reset 入口；Jipper 的确认清零仍由原 MOD 菜单执行。`Custom` 仍需通过已验证的状态查询、事件或受限 predicate bytecode 表达，不能执行任意反射或未知线程回调。predicate 变化必须生成有序 lifecycle command，并受 owner/session/generation 校验；切换时只 CANCEL 当前 active input state，不清空 MOD 累计数据，也不补造 DOWN。

## 6. 混合渲染后端

混合渲染后端由 MOD self-render 拥有视觉语义。兼容层提供 generated Unity proxy 和可选批量渲染服务，但不在未授权时根据 MOD 名称或资源关键词自行创建另一套 HUD。

后端选择顺序：

```text
MOD 原代码通过 Unity proxy 创建对象/Mesh
  -> 保持原行为

MOD/manifest 显式使用 PcCompat batch service
  -> MOD 提供布局、颜色、资源和事件语义，兼容层批量提交

rewriter 证明对象池到 Mesh 的变换可观察等价
  -> 标记 optimized_managed 并保留逐项诊断

recipe fallback
  -> 仅在用户允许时启用
```

### 6.1 Unity 对象层

适合使用真实 Unity UI 对象的内容：

- HUD root 和 Canvas。
- 按键背景和边框。
- 键名、单键次数、KPS 和总次数。
- 低频图标和设置驱动元素。

这些对象优先由 MOD 自己的重写代码通过 generated proxy 创建。兼容层跟踪 owner、session 和 handle；dirty flag、对象池或更新频率保持 MOD 原逻辑，只有可证明不改变结果时才由 rewriter 优化。

### 6.2 批量几何层

适合批量渲染的内容：

- rain。
- ghost rain。
- 高频按下脉冲。
- 大量短生命周期矩形或色块。

建议使用预分配数组维护：

```text
lane
startTick
releaseTick
color
height
state
```

每帧只更新活跃项，并一次提交合并后的 vertices/indices/colors。MOD 可以直接通过 Unity `Mesh/CanvasRenderer` proxy 实现，也可以调用兼容层的 owner-scoped batch service。batch service 不决定颜色、尺寸、资源或事件含义，只负责有界缓冲、线程安全发布和 UnityMain 上传。

rain 的位置和长度由事件 `raw_ns`、release tick 和当前 monotonic tick 解析，不累计 `deltaTime`。主线程卡顿后恢复时直接显示当前应有状态，不回放已经错过的视觉帧。

### 6.3 KeyViewer rain 优化后端

当 MOD 原实现、manifest 或可证明 rewrite 选择批量路径时，rain 优化后端为单个 Unity `CanvasRenderer + dynamic Mesh`：

- 每条 rain 只占一个 Native 结构记录。
- HudLogicWorker 双缓冲发布顶点、UV、颜色和索引。
- UnityMain 每个 presentation opportunity 最多更新一个 Mesh。
- 使用 UI/Default 或已映射兼容 Material。
- 一个 KeyViewer 通常只产生一个 rain draw call。
- 使用 Canvas rect clipping，不为每条 rain 创建 Image/Mask。
- Mesh 容量预分配，超限只丢弃最旧纯视觉项。

MOD 自己创建的字体、Sprite、Texture 和 Material 继续作为 Mesh/CanvasRenderer 的资源输入，兼容层不得替换其视觉资源。只有 Mesh 更新 API 无法通过 runtime metadata 和 ABI 验证，且 MOD/用户允许 fallback 时，才回退到有限容量 pooled Image；否则该 KeyViewer rain feature 明确失败。

## 7. 资源兼容

### 7.1 资源编译器边界

资源解析采用独立、低频的导入期程序集，不把 Unity bundle 解析器放进 ModManager 启动和 HUD 热路径。导入的 PC/Linux bundle 永远不交给 Android Unity Player；最终运行时通过 VirtualBundle registry 同时支持 MOD self-render 直接消费和 recipe 精确绑定：

```text
xphorror.PcModCompat.Resources.dll
  -> AssetsTools.NET 3.0.4
  -> bundle/type tree/class database 只读索引
  -> asset reference graph
  -> feature resource groups
  -> resource recipe + Resource IR v1 + verified payload

ManagedSelfRender
  -> 重写 MOD 原 AssetBundle/File/Resources API
  -> VirtualBundle owner/session 映射
  -> O(1) resource registry 查询
  -> 返回当前已支持的真实 Texture/Sprite/TMP/GameObject proxy 给 MOD
  -> MOD 自己 Instantiate/赋值/销毁资源对象

Recipe fallback
  -> 不链接 AssetsTools.NET
  -> 只读取已验证的 resource recipe
  -> 查询同一个 VirtualBundle resource registry
  -> 按 node/target binding 交给 PresentationSink
```

`AssetsTools.NET` 当前选择为导入期主解析器，版本 `3.0.4`、MIT、目标 `netstandard2.0`。它负责 UnityFS、asset 名称/类型、serialized object、prefab 层级和依赖索引，不负责在游戏中创建 Unity 对象，也不执行 MOD assembly。

截至 2026-07-23，实际资源子集已经贯通：Resource IR 可提取 Alpha8 等七种未压缩纹理格式和 DXT1/DXT5，读取 inline/`.resS` 数据并恢复 Sprite metadata；Android UnityMain 使用 generated proxies 创建 Texture2D/Sprite。静态 TMP 字体可从 MOD bundle 提取 face、glyph/character table、atlas、Material 和 style，以 capability clone 作为 IL2CPP 外壳后重建 lookup；失败时才按 Unicode fingerprint 退回 host capability font。PrefabGraph v1 可递归提取 Transform/RectTransform 层级、CanvasRenderer、Image/RawImage 及 Sprite/Texture/受限 Material 依赖；Jipper ProgressBar 已作为首个真实样本走该通用路径，不再映射 `prefab.compat.progress_bar`。静态字体与 graph 均固定标为 `compatible`：前者仍使用 Android capability Shader，后者也不等价于任意 prefab 重建。

Material 当前支持 TMP Mobile、UI/Default、Sprites/Default 三种 capability base。导入期保存受限属性、纹理依赖和 dropped-property 诊断；UnityMain 先验证 base Material 具备所有保留属性，再复制并应用，异常时销毁半成品。该路径是显式 `compatible`，不把 desktop Shader 或被裁掉的 bevel/bump/glow 语义标成 exact。

VirtualBundle registry 按 MOD owner、session generation、bundle identity 和 selected candidate 隔离，`LoadAsset`/`LoadAllAssets` 热路径只做 O(1) 查询。required 资产任一失败时当前 bundle 获取失败关闭；optional 未支持资产可省略。CLR wrapper 非 null 不等于 Unity 对象有效：每个 materialized asset 在返回 MOD 或作为依赖消费前必须通过 generated `UnityEngine.Object.op_Implicit` 检查；required fake-null 以 `mod/generation/id/name/type` 失败，optional fake-null 从 `LoadAllAssets` 省略，禁止让 MOD 首次读取 `name` 时才得到无身份的 IL2CPP 空引用。该检查只允许出现在资源边界，不进入 HUD/frame 热路径。session 清理按 Resource IR 依赖图拓扑排序，确保 prefab 先于 Sprite/Material、Sprite/Material 先于 Texture 销毁。

MOD 原始资源身份必须保留。rewriter 记录源 bundle 路径、asset 名、预期类型和调用点，并把原 `AssetBundle` API 改写为 VirtualBundle API。运行时按 stable resource identity 返回重建对象，不把 MOD 请求静默改成游戏内置字体、Sprite 或兼容层近似 prefab。只有 MOD manifest、用户或明确 fallback policy 允许时，才可使用替代资源，并把差异写入诊断。

type tree/class database 策略固定为：

1. bundle 自带 type tree 时优先使用。
2. 缺少 type tree 时使用完整的单版本 `Unity 6000.3.10f1` class database。
3. 不内置 Unity 2022 或其它历史版本数据库。
4. 未知布局不得套用相近版本强行解析；只能保留 bundle/asset 基础索引并降低兼容状态。

### 7.2 AssetBundle 版本和解析顺序

Unity 版本门禁：

```text
6000.3.x
  -> 自动进入索引、提取和语义转换

其它 6000.x
  -> 受控解析与转换
  -> 加载前显示警告

非 6000
  -> 禁止自动转换
  -> 显示强警告
  -> 仅允许用户显式强制解析
```

所有版本不匹配和强制转换都进入审计报告，不能缓存为普通 `exact` 结果。

MOD 同时提供多平台 bundle 时按以下顺序处理：

```text
Linux bundle 优先解析
  -> Windows bundle 解析
  -> Mac bundle 解析
  -> Android bundle 只作为额外解析候选
  -> Resource IR 与资源提取/重建
  -> 明确错误或视觉降级
```

Linux bundle 是高优先级解析候选，但不能视为 Android 二进制兼容包。任何导入 candidate 都只由 AssetsTools.NET 读取，不能通过篡改平台标志或调用 `AssetBundle.LoadFromFile*` 送入游戏。解析失败后进入下一 candidate；资源语义无法重建时只隔离依赖它的 feature group。

bundle 可用或可解析索引时，asset reference graph 参与 HUD 自动发现。编译器从 prefab/UI component/Material/Shader/名称候选反查 `LoadAsset/Resources.Load/Instantiate` 及其 wrapper，再连接到 MOD 生命周期调用图。

### 7.3 缓存、生命周期和提交粒度

通过导入期验证的 candidate 被转换为 Resource IR 和 Android 可消费产物，不把桌面 bundle 作为运行时加载文件复制到 compiled bundle：

```text
compiled/<mod_id>/<cache_key>/
  ui_recipe.bin
  resource_recipe.bin
  resource_ir.bin
  resource_ir_blobs/
    <stable-payload-id>.rgba32
  resource_report.json
```

资源文件、recipe、源 candidate hash 和报告在临时目录中提交；candidate 身份使用完整 SHA-256，compiled cache key 使用 128-bit SHA-256 前缀。cache hit 时重验源身份和转换产物，损坏 complete cache 必须重建。首版不做跨 cache generation 的全局 blob 去重；旧 compiled bundle 删除时一并回收其资源。

运行时采用按需、session 级常驻：

```text
MOD self-render 首次 resource request
  -> rewritten path/API 查询 resource session
  -> O(1) VirtualBundle registry 查询
  -> 返回真实 Unity Object proxy
  -> MOD Instantiate/字段赋值
  -> issued object/asset handle 记录 owner + session generation

recipe fallback 首次 resource request
  -> O(1) VirtualBundle registry 查询
  -> stable id + type verification + session cache
  -> asset GCHandle cache
  -> ui_recipe node/target 精确绑定并刷新已 materialize graph

MOD clear/reload
  -> 停止 managed lifecycle 并阻止新 proxy request
  -> UnityMain 调用 MOD OnDisable/OnDestroy（若仍可安全执行）
  -> 清除 recipe 组件引用并销毁 owner 对象
  -> 释放 asset GCHandle
  -> 释放 session 重建对象
  -> 保留 process-wide host capability bundle，只销毁当前 session 的 clone/重建对象
```

Managed self-render 与 recipe fallback 共享 immutable Resource IR 和 session registry，但各自持有带 owner 的 asset/object handle。失败结果在当前 session 记忆，除非用户主动重新加载 MOD。worker/render/EGL 线程不得调用 Unity API；Unity 对象预建、发布和销毁只能在 UnityMain 执行。

`Task.Yield`、JALib `JATask.OnCompleted` 和 MOD async continuation 也属于 Unity 对象线程边界。它们必须捕获 owner-bound `PcCompatUnityMainExecutionContext` 并进入独立的有界 UnityMain continuation queue；owner/phase 必须由 context 实例携带，因为 `SynchronizationContext.Post()` 通常由 `.NET TP Worker` 调用，此时线程局部 owner 已不存在。直接 `ThreadPool.QueueUserWorkItem` 后调用 TMP、Material、Transform 或其它 generated proxy 属于实现错误，不是性能优化。continuation 同时携带 MOD owner/session，卸载或换代后不再执行；队列拒绝和 callback 执行异常只 fault 对应 MOD，禁止异常逃出 `Post()` 或 UnityMain work callback 触发 CoreCLR 进程级 abort。

生产实现已桥接同步 `AssetBundle.LoadFromFile(string)`、非泛型/闭合泛型 `LoadAsset`、非泛型/闭合泛型 `LoadAllAssets` 和 `Unload(bool)`。异步 API、`Resources.Load`、`Texture2D.LoadImage`、反射包装及 MOD 直接文件读取仍需进入同一能力表；未支持调用必须在导入期报告，不能返回 null 后继续把 MOD 标记为 loaded。

Android sink 的资源命令保留 64 槽队列和一次一条预算；MOD async continuation 使用独立 2048 槽队列并按每次最多 16 条消费，避免高速输入 burst 或资源轮询互相挤占容量，同时限制任意 MOD continuation 造成的单帧尖峰。两条队列复用 metadata 解析的 `Canvas.SendPreWillRenderCanvases` / `CanvasUpdateRegistry.PerformUpdate` PresentationSink permanent slot，仅在有任务时进入 managed callback，并分别维持有界预算。APP 自带 Android capability bundle 在加载前验证 manifest、文件大小和完整 SHA-256；桌面 candidate 永远不进入这条 Unity 加载链。

PresentationSink detour 必须按实际 IL2CPP ARM64 签名保留隐藏 `MethodInfo*`：static 无显式参数方法仍接收一个隐藏指针，instance 方法在 `this` 后接收该指针；`GUIUtility.ProcessEvent(Int32, IntPtr, out Boolean)` 的 continuation 为 `void(int, void*, bool*, MethodInfo*)`，`GUIUtility.BeginGUI(Int32, Int32, Int32)` 为 `void(int, int, int, MethodInfo*)`。两个 original continuation 的全部显式参数和隐藏参数都必须原样转发，不能依赖 managed 回调结束后的寄存器残值。

资源按 feature resource group 原子提交，而不是整 MOD 或单 asset 提交。例如 `overlay_core`、`keyviewer`、`credits` 分别选择 candidate；同一 group 内不得混用 Linux 和 Windows bundle。某组失败只禁用该组，MOD 其余独立功能继续运行并显示 `partial`。

asset 绑定置信度：

```text
Proven
  IL literal/switch/LoadAsset 参数和 stsfld 数据流可证明
  -> 自动绑定

UniqueType
  candidate 内该预期类型只有一个 asset
  -> 只保留审计/用户建议，不进入 required Resource IR

SemanticMatch
  字段名/资源名语义匹配
  -> 只生成候选，要求用户确认

FuzzyMatch
  关键词、编辑距离或部分名称匹配
  -> 只显示建议，禁止自动绑定
```

### 7.4 纹理转换

首版在工作线程把 DXT/BC 等桌面纹理解码为 `RGBA32`，缓存为 PNG 或压缩 RGBA。缓存键包含源 hash、转换器版本、mipmap、color-space 和 alpha 语义。ETC2/EAC 转码属于后续体积与上传性能优化，不进入首版正确性承诺。

### 7.5 Shader 和 Material

#### 7.5.1 导入期转换边界

导入期允许转换 Shader，但这里的“转换”固定定义为 **语义降级到预编译 Android Shader**，不是在 Android Player 内把任意桌面 Shader 二进制重新编译成 Unity Shader。

可以在手机导入期完成：

- 读取 serialized `Shader`、`Material`、pass、keyword、property 和纹理引用。
- 识别常见 UI/Sprite/TMP 效果及其 blend、stencil、深度和裁剪语义。
- 把效果映射到随 APP 打包、由 Unity `6000.3.10f1` 预编译的 Android shader capability bundle。
- 重建 Android `Material` recipe，复制颜色、浮点、向量、纹理、render queue 和白名单 render-state 属性。
- 将转换结果和诊断写入 compiled cache，后续启动不再重复分析。

不能在纯 Android 离线导入期可靠完成：

- 把 DXBC、DXIL、桌面 SPIR-V 或 Metal shader 直接变成 Unity Android Shader 对象。
- 从已编译 shader blob 恢复完整 ShaderLab pass、SubShader tags、keyword variants、Unity constant-buffer binding 和 Canvas/TMP stencil 语义。
- 通过 `glslang`、`shaderc` 或 `SPIRV-Cross` 生成可直接交给 Unity `Material` 的 Shader。这些工具最多生成 GPU 程序源码/字节码，不会生成 Unity Player 所需的 serialized Shader、pass metadata 和 variant 表。
- 在 Unity Player 运行时调用稳定公开 API 编译任意 ShaderLab/HLSL。该能力属于 Unity Editor 构建链，不属于 Player API。

即使 Linux bundle 内含 Vulkan SPIR-V，也不能通过修改平台标记将其视为 Android bundle。它只进入 AssetsTools.NET 解析；任何 shader、layout、variant 或 binding 无法映射到 host capability 时，都必须把依赖 feature 标为 `compatible` 或 `unsupported`。

如果 MOD 额外提供 ShaderLab/HLSL 源码，未来可以增加独立的桌面 Unity `6000.3.10f1` 离线重编译工具，但该工具不是手机端完全离线兼容链的依赖，也不能成为导入成功的必要条件。

#### 7.5.2 Android Shader Capability 库

capability 库至少提供：

- UI/Default。
- Sprite/Unlit。
- alpha、additive、multiply。
- alpha clip。
- mask/stencil。
- grayscale、outline。
- TMP SDF。

capability bundle 必须使用目标 Unity `6000.3.10f1` 为 Android/Vulkan/OpenGLES3 构建，并显式保留所需 shader variants，避免 `Shader.Find` 和构建期 stripping 造成设备差异。它作为最终 `assets/runtime/pc_compat_capabilities` 的强制产物发布；Bundle、白名单或 manifest 缺失/哈希不符时完整 APK 构建直接失败。主 APK 可使用内置 shader，但只有在运行时验证 shader 和所需 variant 确实存在时，才能记为可用 capability。构建契约见 `ANDROID_CAPABILITY_BUNDLE.md`。

导入期生成的 `ShaderBindingRecipe` 至少包含：

```text
source shader fingerprint
capability shader id/version
property rename/conversion table
texture bindings
render queue
blend/depth/cull/stencil state
required keywords/variants
compatibility class
diagnostics
```

匹配输入包括 shader 名称、property schema、pass 数量与用途、render queue、blend、z-test/z-write、cull、stencil、keywords、纹理用途和引用该 Material 的组件类型。名称只能提供弱证据，不能覆盖结构冲突。

结果分级：

```text
exact
  来源效果与 capability 的可观察语义、属性和必要 variant 均可证明一致

compatible
  核心视觉和交互语义可保留，但存在已记录的非关键视觉差异

unsupported
  依赖自定义顶点变形、多 pass/GrabPass、几何或曲面细分、计算着色、
  未知 buffer binding、无法重建的 stencil/keyword 组合或其它未证明能力
```

`compatible` 必须在导入报告中列出丢失能力；`unsupported` 不创建 Material，也不允许静默显示粉色材质。依赖该 shader 的 feature resource group 按 7.6 的规则隔离失败。

#### 7.5.3 导入与运行时流程

```text
导入工作线程
  -> 解析 Shader/Material/引用图
  -> 计算 shader semantic fingerprint
  -> 匹配 capability 库 manifest
  -> 生成 ShaderBindingRecipe
  -> 连同纹理转换结果原子写入 compiled cache

UnityMain 首次资源请求
  -> 加载预编译 Android capability bundle
  -> 校验 shader id 和 required variant
  -> 创建 Material
  -> 应用白名单 property/render-state 映射
  -> 绑定已转换纹理
  -> 发布到对应 feature resource group
```

这条链路把高成本分析放在一次性导入阶段，运行时只执行已验证资源映射，不重新分析 Shader。Managed self-render 获得重建后的 `Material` proxy 并继续由 MOD 自己赋值/使用；recipe fallback 则把同一结果绑定到 graph target。cache key 必须包含源 shader/material hash、目标 Unity 版本、capability 库版本、转换器版本、目标图形 API 和用户强制策略。

当前生产目标采用上述“预编译 capability 库 + 导入期语义映射”。不承诺任意桌面 shader 二进制转换，也不把自有 native renderer 生成的 GLSL ES/SPIR-V 视为 Unity UI Material 的通用替代品。

### 7.6 失败隔离

Linux、Windows、提取重建和 fallback 全部失败后必须报告错误。错误绑定到请求该资源的 managed HUD feature 或 recipe feature：该功能不启动，MOD 其他独立功能继续运行，MOD 总状态显示 `partial`。只有 Entry、核心配置、managed lifecycle 或全部功能均不可用时才显示 `unsupported`。

Managed self-render 请求失败时必须把 source callsite、逻辑路径、candidate、asset 名、类型和 Unity 异常透传到 MOD 诊断。禁止在 MOD 不知情时切到兼容层预定义 HUD；允许的 recipe fallback 必须显式显示当前视觉 owner 和降级原因。

## 8. 性能预算

以下是兼容层基础设施和参考 HUD 的架构预算，不是未经实机验证的保证，也不包含 MOD 自身原有算法成本：

- HUD 隐藏：近似零逐帧成本。
- 普通静态 HUD：目标低于 `0.05 ms/frame`。
- 20 键 KeyViewer、无 rain：目标低于 `0.10 ms/frame`。
- 常规 rain：目标低于 `0.30 ms/frame`。
- 极端高速输入：通过容量上限和降级策略保持低于 `0.50 ms/frame`。

必须同时记录平均值、P95、P99 和最大值。只看平均值不能发现输入爆发和 Canvas rebuild 导致的尖峰。

Managed self-render 额外记录：

- 每个 MOD 的 managed dispatcher 总时间与调用次数。
- 每类 generated proxy 调用次数、runtime invoke 次数和 typed bridge 次数。
- managed allocation、GC pause、异常和 lifecycle overrun。
- MOD 自身时间与兼容层桥接时间，不能把两者合并后无法归因。
- MOD HUD 与 recipe fallback 的同场景对照结果。

性能原则：

- 禁止热路径 `il2cpp_runtime_invoke` 风暴。
- 禁止逐帧字符串格式化未变化文本。
- 禁止逐输入创建/销毁 GameObject。
- 禁止 Native Hook 按每个输入/判定事件同步跨入 CoreCLR；事件必须批量进入 typed snapshot/event queue。
- 允许 MOD HUD 在 UnityMain 通过 generated proxy 调用 Unity API，但成员解析和 ABI 验证必须缓存，不能逐调用重新反射或扫描 metadata。
- 每个 MOD 每个 presentation opportunity 默认只进行一次合并 managed lifecycle dispatch；同一帧的多个事件由 MOD 批量读取。
- recipe presentation 的单次预算固定为 16 条 command、12 个 graph 物化工作单元、4 个 resource binding 和 4 个 retired graph；延期 command 不得被计入 consumed 或提前 ack。
- 在不改变可观察语义的前提下，rewriter/proxy 可跳过重复 setter、缓存静态字符串与资源对象；无法证明时不得擅自删除 MOD 调用。
- 禁止 Unity LayoutGroup/ContentSizeFitter 在高频层反复重排。
- 所有数组、对象池和 ring buffer 设硬上限。
- 超限时优先丢弃旧视觉粒子，不得阻塞游戏输入或官方判定。
- 禁止用 Unity 帧 `deltaTime` 累计输入可视化时间。
- 异步逻辑线程使用事件唤醒和 deadline heap，不做固定高频忙轮询。

## 9. Harmony 最终目标

Harmony 最终目标是通用 Harmony-to-Native Hook 兼容层，不追求复刻 PC Harmony 的内部对象结构。实际函数入口、original continuation、同步参数/结果修改和 skip-original 始终由 Native HookSlot/HookBroker 掌控。

这不再意味着运行期完全禁止 MOD 托管代码。重写后的 MOD Entry、HUD 生命周期和展示型 callback 可以在受控 managed domain 中执行；禁止的是从任意 Hook 线程直接执行未经调度的托管 patch IL。同步 patch 语义必须 Native lowering，展示型/观测型事件可以通过 snapshot 延迟到 UnityMain 后调用 MOD HUD 代码。

MOD 状态必须区分 `full / partial / unsupported`；无法证明等价的单个 patch 失败关闭，不允许按 MOD 身份写生产特判。

### 9.1 第一阶段：Prefix/Postfix

第一阶段已经确认为 Prefix/Postfix 核心语义：

- 类级和方法级 `[HarmonyPatch]` 聚合。
- `PatchAll()` 静态等价扫描。
- 受限程序化 `Harmony.Patch(...)` 抽象解释。
- 普通方法、getter、setter 和 constructor 目标解析。
- 参数类型和 overload 精确匹配。
- Prefix/Postfix chain。
- `__instance`。
- 原始参数读取和受限 `ref` 写回。
- `ref __result`。
- Prefix `bool` 跳过 original。
- Prefix/Postfix `__state` 配对。
- `___field` 读写。
- priority、before、after 排序。

所有补丁最终仍由 Native HookSlot/HookBroker 安装。Harmony ID 只表示 chain layer ownership，不允许运行期物理 unhook。

Prefix/Postfix callback 在导入期分成三类：

```text
SynchronousNative
  参数/ref result/skip original/游戏状态副作用
  -> fixed-op、Rule VM 或受支持 typed native callback

DeferredManagedPresentation
  只读取已捕获状态并更新 MOD HUD/资源
  -> typed event/snapshot -> UnityMain managed dispatcher

Unsupported
  无法证明线程、时序、ABI 或副作用等价
  -> 不安装并明确报告
```

`__state` 以 native 单调 `invocation_id` 关联 Prefix 与 deferred UnityMain Postfix。值保存在 MOD session 内的 managed state store，key 为 patch 声明类型与 state 类型；只为确有 Postfix 消费者的 state 建立条目，按该 key 的实际 Postfix 消费次数释放，异常/丢事件情况下由 16384 条硬上限兜底。该路径可保存 CoreCLR primitive、struct 和对象引用，但不把裸 IL2CPP 对象生命周期延长到场景边界；未知 native pointer state 必须在导入期拒绝。deferred Postfix 可读取 primitive/enum/generated-proxy `__args` 快照和 primitive/enum 按值 `__result`；若要求修改 `ref/out` 参数或 `ref __result`，必须改为 hook-thread 同步 Postfix，当前 deferred event 路径不宣称支持。

### 9.2 Transpiler

Transpiler 使用 PC `Assembly-CSharp.dll` 原始 IL 作为语义基准，恢复 MOD 希望产生的 IL 差异，再降级为入口 Hook、下游 Hook、调用替换或简单方法整体 Native bytecode。

Transpiler 模式按以下顺序推进：

1. 常量和字符串替换。
2. 方法调用替换或跳过。
3. 调用前后插入逻辑。
4. 字段读取/写入替换。
5. 参数覆盖。
6. return 前插入或替换返回值。
7. 基于单参数、字段或返回值的条件反转。
8. 简单直线方法整体翻译。
9. 有界循环和简单局部变量数据流。
10. 异常块、复杂 CFG 和多入口控制流。

调用点级修改通过 caller context 与 callee 永久 HookSlot 限定作用域，不把局部 Transpiler 错误扩大为全局 callee 修改。禁止函数中段 AArch64 覆写和机器码特征扫描；无法降级的 Transpiler 标记为不支持。

### 9.3 ReversePatch

ReversePatch 不允许 managed Harmony 自行修改 IL2CPP 入口。导入期建立 stand-in 到 IL2CPP target 的 binding，并按调用域生成两种桥：

- Native Rule 调用：`CALL_ORIGINAL(methodId)` 或 `CALL_SNAPSHOT(chainGenerationId)`。
- Managed self-render 调用：rewriter 把 stand-in callsite 改为 generated `PcCompatReversePatchProxy`，由它 P/Invoke Native bridge 后调用 original continuation 或冻结 chain，并把结果重新封装成受控 proxy/value。

Managed ReversePatch 只允许从已注册 managed lifecycle/UnityMain callback 调用；未知后台线程、音频线程或 Hook 重入上下文明确拒绝。所有调用验证实例、参数、返回值 ABI、session generation 和 owner，并具有递归保护。无法安全跨越 CoreCLR/IL2CPP 的 non-blittable 参数、引用返回或异常语义在导入期失败关闭。

### 9.4 Finalizer

Finalizer 只覆盖兼容层可控异常域：Native rule VM 异常和白名单 runtime invoke 返回的 `Il2CppException*`。允许检查、替换或吞掉异常并修改结果。

不承诺捕获 typed original 抛出的任意 IL2CPP native unwind，不全局 Hook `il2cpp_raise_exception`，也不为了 Finalizer 把所有热路径改为 runtime invoke。

### 9.5 动态目标与运行时辅助导入

`TargetMethod()`、`TargetMethods()`、`Prepare()`、`AccessTools` 和程序化 `Harmony.Patch()` 由受限解释器恢复。

由于导入发生在 IL2CPP 已加载的游戏进程内，解释器可以生成 runtime query plan，在 UnityMain 查询实时对象后确定目标。对象不存在时进入 `pending target resolution`，后续场景自动重试。

可持久缓存 metadata/build 稳定结果；设置、场景或实例依赖结果必须带 dependency fingerprint 或仅做会话缓存。有限候选目标可预先建立永久 HookSlot，运行时只切换 layer gate。

### 9.6 自动成员闭包与对象桥

扫描 callback IL 后递归收集实际使用的类型、字段、属性、方法和 helper，生成每个 MOD 的 member closure。Native VM 支持 primitive、enum、常见 blittable Unity struct、IL2CPP object handle、string、array、受限 `List<T>`、nullable 和 `ref/out` slot。

禁止硬编码字段 offset、任意指针运算、未进入闭包的调用，以及把裸 IL2CPP 对象指针交给 CoreCLR MOD 代码。

实时 Unity 对象快照按用途处理：纯显示允许短时使用最后完整值，默认容忍 250 ms；game-state 默认容忍 50 ms；任何会产生副作用的 rule 必须使用当前 session generation 的最新完整快照。过旧时冻结该规则，恢复后不补跑没有明确事件记录的中间副作用。

### 9.7 Patch registry

Native 维护逻辑 `HarmonyPatchRegistry`，兼容 owner ID、priority、before/after、注册 index、查询和逻辑禁用。

`GetPatchInfo/GetPatchedMethods/GetAllPatchedMethods/HasAnyPatches` 返回当前逻辑状态。`Unpatch/UnpatchAll` 发布新的 immutable chain generation 并禁用 layer，物理 Dobby Hook 永久保留；正在执行的调用继续使用旧快照，下一次调用使用新链。

### 9.8 Fail-closed

任何无法证明等价的 Harmony patch 必须：

- 不安装部分补丁。
- 输出目标、callback、IL offset 和不支持原因。
- 在 ModManager UI 中标明 `unsupported` 或 `partial`。
- 不把 `registered_only` 伪装成 `loaded` 或 `active`。

### 9.9 完整 Harmony 等价执行目标（2026-08-19 已确认，尚未实现）

本节记录 Transpiler/Finalizer 后续实现的已确认目标架构。它取代 9.2 和 9.4 中“受限翻译、仅覆盖兼容层可控异常域”的最终目标，但不改变当前生产状态：现有实现仍只有同步 Prefix V2、deferred Postfix 和 Native Rule 子集，不能把本节写成已经可用。

#### 9.9.1 CoreCLR 影子方法与 Semantic Pack

- 完整等价语义以 CoreCLR 影子重编译为基线。Native Rule/AArch64 lowering 只是通过等价验证后的透明性能优化，不再是 Transpiler 的唯一执行后端。
- 发行构建携带由精确 PC 游戏程序集生成的 CIL Semantic Pack。包内保存方法身份、原始指令与 operand、locals、max stack、branch、exception region、程序集/MVID/内容哈希和目标游戏构建身份；只有全部身份精确匹配时才启用。
- 完整 metadata facade 为动态 Transpiler 提供稳定一致的 `Type`、`MethodInfo`、`FieldInfo` 和 `__originalMethod` 身份。原 member closure 继续用于审计与优化，不能再决定合法游戏成员是否存在。
- 游戏对象图、静态状态和 Unity/IL2CPP 生命周期仍由 IL2CPP 唯一持有。CoreCLR 只运行目标方法的 Method Island；字段、方法、构造器、虚调用和异常全部经过 typed bridge，不复制第二份游戏世界状态。
- 任何带 Finalizer 的目标，即使没有 Transpiler，也执行完整影子 original，确保 original 异常确实进入 Finalizer 链。

#### 9.9.2 Transpiler 与 Finalizer 精确语义

- Prefix、Postfix、Transpiler、Finalizer 共用 Harmony `priority`、`before/after`、注册 index 拓扑排序；排序环、缺失依赖和不稳定 operand 失败关闭。
- 从 Semantic Pack 恢复原始 IL、locals、labels、exception blocks 和兼容 `ILGenerator`，依次执行全部 Transpiler。每一段输出做结构校验，最终做栈、分支、异常区和 operand 校验。
- `Patch()`/`PatchAll()` 是强时序 API：返回时新补丁必须已经完成 Transpiler、影子 PE、JIT/R2R、预热和 HookBroker 发布。任一步失败抛 `HarmonyException`，旧 generation 保持工作，不留下半生效状态。
- Finalizer 按 Harmony 顺序处理 Prefix、影子 original、Postfix 和桥调用产生的托管异常。IL2CPP 异常转换为 MOD generation scoped exception facade，保持具体类型、对象身份、message、stack、inner exception 和自定义字段；未吞掉的异常在原调用线程重新送回 IL2CPP。
- Finalizer 自身异常遵循上游 Harmony 规则。`SIGSEGV`、`SIGBUS`、`SIGABRT`、栈损坏和非法地址访问不转换为托管异常，也不尝试从 signal handler 恢复。
- 影子方法无条件在 original caller 线程同步执行，包括 render、input、worker 和 audio 线程；禁止转发 UnityMain。激活前完成 JIT、proxy accessor、泛型和异常桥预热。

#### 9.9.3 byref、ABI 与泛型

- `ldfld/stfld` 直接降级到 typed bridge；`ldflda/ldsflda`、`ref/out` 使用按对象与字段路径统一的受控影子单元，保证同一方法内别名一致。
- 可能观察游戏状态的 IL2CPP 调用前按确定顺序提交脏单元，调用后失效并重新读取。`ref/out` 返回后立即提交；异常路径保持原 IL 语义。
- 编译期执行 byref 逃逸分析。局部使用和已知 typed bridge 可运行；byref 返回、存入长期对象、传给未知方法或跨异步边界时拒绝补丁，不能静默复制值。
- native 到 CoreCLR 使用按完整 AAPCS64 ABI shape 生成的专用 thunk，覆盖 GP/FP 寄存器、栈参数、`ref/out`、小结构体、HFA/HVA、`x8` 间接结构体返回以及 IL2CPP `MethodInfo*`/rgctx/generic-sharing 隐藏参数。生产热路径禁止反射和 `libffi` 通用调用器。
- thunk 按签名哈希复用，生成后执行 `RW -> RX`，不长期保留可写可执行内存。无法证明 ABI 的签名拒绝激活。
- 开放泛型在 `Patch()` 时安装泛型入口 dispatcher，并预编译已知实例。未来未知实例首次调用时在原线程单航班同步优化 JIT，绝不先放行未补丁 original；该停顿记录一次 Warning。引用类型可在语义允许时复用 canonical island，值类型/HFA/布局相关实例独立编译。

#### 9.9.4 JIT、R2R 与缓存

- 影子方法使用 CoreCLR 优化 JIT、`AggressiveOptimization` 和激活前 `PrepareMethod`。同一进程按规范化输出哈希复用 native function pointer。
- 静态补丁在启动加载阶段同步生成、验证并加载 RyuJIT ReadyToRun/crossgen2 产物。动态补丁在 `Patch()` 内同步完成优化 JIT并立即生效，R2R artifact 标记为 `r2r-pending`。
- 动态 R2R 只在 loading、暂停、后台或下次启动前生成；禁止在游戏热路径后台运行 crossgen2。staging 中断产物由下次启动清理。
- 启动时按 `CoreCLR版本 + Android API + ABI + 编译器ABI` 做 R2R 能力探针。R2R 不可用或 artifact 损坏时回退每进程优化 JIT，不拒绝 MOD、不解释执行；每进程只记录一次 Warning，ModManager 状态页持续显示原因、环境和受影响 MOD 数量。
- 缓存使用内容寻址对象仓库。每个 MOD保留 `current + 1 previous`；active、previous 和 in-flight lease 为可达对象。staging 原子发布、trash 延迟删除，启动 mark/sweep 清理损坏、旧代、孤立对象和中断产物。
- 缓存键至少包含游戏/Semantic Pack、MOD程序集、规范化 Transpiler 输出、泛型实参、rgctx 布局、编译器 ABI、proxy surface、metadata facade 和桥接 ABI。MOD删除或任一身份变化后旧缓存自动回收。
- 旧 Jipper/PcCompat rewrite cache 必须有专门迁移和清理，不能继续保留重编译后不可达的旧目录。
- `DynamicMethod`、捕获委托与 `CallClosure` 使用进程内 generation lease：每次进程启动重新执行 Transpiler并优化 JIT一次，同进程复用，不直接持久化对象地址或闭包状态。只有可证明静态、不可变且可确定重建的闭包才可升级为跨进程缓存。

#### 9.9.5 generation、事务与性能

- 每次调用入口原子取得当前 generation lease；Prefix、original、Postfix、Finalizer 整条链固定使用同一 generation。发布前进入的调用完成旧链，新调用只进入新链。
- `PatchAll()`/`UnpatchAll()` 对本次调用涉及的所有目标采用整批 staging 和单一 registry epoch 原子发布。任一目标失败则整批无变化；MOD `OnLoad()` 最终失败时回滚其加载事务创建的补丁、订阅和缓存 lease。
- `Unpatch()` 不强制中断在途调用。卸载等待代码、闭包、异常 facade、跨运行时对象和 thunk lease 归零；超时保留隔离的退休 generation 并记录 Warning，禁止释放悬空函数指针。
- 性能超预算时不得运行中自动撤销补丁。软预算和硬预算均以 Warning 呈现；已激活 generation 保持到安全生命周期边界，硬超预算目标在下次激活前要求 Native Rule 优化或明确处理。
- 稳态路径禁止反射、逐调用分配、全局锁、解释执行和重复 JIT。低频采样分别记录 thunk、桥接、影子方法和字段同步耗时。
- Native Rule 必须从同一规范化 IR 自动 lowering，或声明 Semantic Pack 哈希、补丁链哈希、ABI shape 和桥版本，并通过返回值、状态写回、异常和补丁顺序的差分合同。任何身份不匹配都回到影子 JIT；运行时不得为验证而双执行有副作用的 original。

## 10. 待确认事项

后续 grill-me 需要逐项确认：

1. `RGBA32` 与 ETC2/EAC 的具体像素/显存阈值和编解码库。
2. `AssetsTools.NET 3.0.4` 导入期程序集、Unity `6000.3.10f1` class database 和资源提取器的打包方式；解析器选型本身已经确定。
3. resources/diagnostics section、`resource_recipe.bin`、source semantics manifest override 和手动 HUD/resource override 的 schema。
4. Shader capability 库首批内置效果集合、variant manifest 和匹配评分。
5. 动态文本、prefab、批量 Mesh、运行时循环和动画各自的 capability/budget 边界。
6. `ManagedSelfRender` 首批允许的 .NET/Unity API capability、P/Invoke/反射/线程策略和异常熔断阈值。
7. 受控注入的 Unity 6000.3.10f1 infrastructure target resolver、TypeInjectionPlan schema、逐类型注入与通用 host 的取舍；ModManager 唯一所有权、进程期常驻和无 direct Dobby 已确定。
8. AssetBundle/File/Resources API 的 overload 清单、异步 request 代理和 MOD 私有路径重写规则。
9. managed self-render、verified recipe optimization 和 explicit fallback 的逐 feature 用户覆盖 schema。

分阶段实施顺序和当前验证基线见 [`IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md)。

## 11. 验证要求

### 11.1 MOD self-render HUD

- 重写后的 MOD Entry 和 HUD 创建方法实际执行，诊断明确显示后端为 `ManagedSelfRender`。
- 未配置用户 override 时每个 HUD feature 默认选择 `ManagedSelfRender`；readiness/Enable/Update 失败后明确 fault，不能自动创建 recipe/兼容 HUD。
- 用户逐 feature 手动启用兼容绘制后，UI 和诊断显示 `ProvenRecipe` 或 `CompatibleFallback`、选择来源、差异和当前 presentation owner；关闭后恢复 self-render 默认策略。
- MOD 通过 generated proxy 创建真实 `Canvas/GameObject/RectTransform/TMP/Image`，对象 owner 与 session generation 可追踪。
- MOD 自带字体、prefab、Sprite、Texture 和 Material 在平台允许时由原 MOD 加载调用取得并使用，不由兼容层预定义资源静默替代。
- MOD 不打开 ModManager UI 也能在 APP 启动后按配置自动加载并创建 HUD。
- 内置关卡、编辑器播放态、暂停、继续、失败、重开和退出场景的生命周期与 PC 行为对照。
- MOD disable/reload 至少 20 次，无重复 HUD、旧 callback、旧 resource completion、泄漏 handle 或跨 generation 写入。
- 单个 MOD callback 抛异常只 fault 对应 MOD/feature，system HUD、其它 MOD 和官方 Canvas original 继续执行。
- ModManager 的“打开 MOD 设置”实际调用原 UMM/JALib/Unity IMGUI 或 Canvas 菜单；Unity IMGUI 在 ProcessEvent 发布事件代次后、原生事件泵随后的首次 BeginGUI 初始化完成时执行，菜单触摸/软键盘不进入 gameplay 或 KeyViewer。
- 关闭设置时按原语义调用 OnSaveGUI/保存入口，立即保存型 MOD 不重复保存；私有路径重定向后原序列化格式可重新读取。
- 原菜单异常后自动进入兼容菜单，顶部显示红色 `Fallback`；只有 verified binding 可编辑，只读/不支持项状态明确，原保存入口失败时不得伪报成功或写入旁路 JSON。
- recipe fallback 关闭时不允许出现兼容层代绘 HUD；开启时 UI 必须显示 fallback owner 与原因。
- 同一 MOD 分别运行 managed self-render 与 verified recipe，对显示内容、数据和设置行为做差异报告。
- 若启用受控注入：同一类型重复注册幂等，同名不同 schema 失败关闭；MOD disable 后 Component 身份保留但 callback 全停，重启后按配置决定是否重新注册。

### 11.2 KeyViewer

- 单个 MOD 的多个 `KeyViewerFeature` 和单个 feature 的多个 `LaneGroup` 独立计数、显隐和故障，不发生状态串扰。
- 自动识别与手动角色绑定生成相同 Adapter IR 时，运行结果一致；程序集 SHA-256/MVID 或 schema/revision/proxy surface 变化后旧 override 必须失效。
- 只有逐子能力 `Proven` 自动启用；`Probable` 要求用户确认，`Ambiguous` 要求手动绑定，`Unsupported` 不可强制运行。UI 显示证据与首个断点，不显示误导性的单一兼容率。
- 核心输入/transition/MOD state/presentation lifecycle 任一不支持时 feature 整体不启用；仅 MOD 原逻辑可独立关闭的 rain、菜单、附加统计或装饰能力允许局部降级。
- 单指连续点击不漏计、不重复计数。
- 10 指同时 DOWN/UP 状态独立。
- CANCEL 后 held 状态全部正确释放。
- 实体键盘同键重复、组合键和左右修饰键映射正确。
- 同一物理键的 Unity/Android/VK alias 只产生一个 canonical event；MOD 明确配置的多个 lane 绑定同一键时，各 lane 仍按原语义响应。
- AsyncInput enabled epoch 只从异步 native snapshot 发布 Physical event；disabled/absent epoch 只从 Activity dispatch 发布。切换 producer 先 CANCEL held，且同一 raw event 不得双计数。
- `GameplayAccepted` 来自 `scrPlayer.HitInputEvent` 成功返回并保持 GameAction identity；不得通过 Hook `ValidInputWasTriggered/CountValidKeysPressed` 或猜测 physical lane 生成。
- 无外接键盘时按 `TouchKeyCount` 重建 N 个 MOD 风格槽位，默认标签为 `T1..TN`；用户标签覆盖只改变显示，不改变 `TouchLane` identity 或 MOD 的 PC 键位配置。
- `Auto/Touch/External/Hybrid` 在会话内不因设备热插拔重排，下一会话按最新设备状态重新选择；用户显式模式稳定覆盖 Auto。
- AUTO、oldAuto 和测试宏不进入正常统计。
- 同一帧内 `DOWN -> UP -> DOWN` 按 sequence/raw 时间完整回放，不压缩成最终 held 状态。
- 诊断导出的 Native audit 与 MOD 状态快照具有同一 sequence 边界；Jipper 的 `Count[]/TotalCount/KPS/rain/held` 可逐项核对，截断或不可读成员有明确原因。
- rain 高负载不影响游戏输入队列和官方判定。
- actor/presentation 过载期间已接受的计数边沿零丢失；仅过期纯视觉 rain 和中间动画采样可淘汰，状态队列故障不反压 gameplay。
- 场景切换、暂停、继续和编辑器播放态显隐正确。
- `visibility`、`inputActivation`、计数、KPS 和 reset 分开验证；Jipper 的持久次数在暂停、失败、重试和场景卸载后保持，只有原设置菜单的确认清零动作可归零。用户显式启用的移动端作用域覆盖只改变 `inputActivation`，不改变其余 MOD 规则；切换时 CANCEL held、不清空累计值且不合成 DOWN。无法证明时 feature 隐藏且停止计数。
- 注入 `50/100/200/500 ms` UnityMain 卡顿后，次数、KPS、held 和 rain 绝对时间位置仍与无卡顿基准一致。
- 主线程恢复时不逐帧补播已过期 rain，不产生视觉时间膨胀。
- KeyViewer `ObserveOnly` 不改变游戏输入；逐 feature ownership override 正确消费或透传。
- 设置变化只发布 generation，不从 ModManager UI 线程直接修改 Unity 对象。

### 11.3 Harmony

- descriptor 与 PC Harmony target 解析结果逐字段对照。
- 同一 target 多 Prefix/Postfix 顺序可复现。
- 参数、返回值、skip-original 和 `__state` 有独立测试。
- 不支持补丁在导入期稳定失败关闭。
- 所有函数地址均从运行时 metadata 解析。
- 与已有 HookBroker layer 共存，不重复 Dobby hook 同一入口。
- Windows source semantics 下可发现被平台 gate 隐藏的 HUD，Android lowering 不执行 Win32 API。
- 展示型 callback 只通过 typed event/snapshot 在 UnityMain 调用 MOD；同步 callback 不得因延迟调度改变 Prefix/Postfix 语义。

当前进度（2026-07-27）：

- 「不支持补丁在导入期稳定失败关闭」已达成：17 个 issue code 覆盖全部静态不可判定路径，descriptor 一律不发，见 3.4。
- 「descriptor 与 PC Harmony target 解析结果逐字段对照」当前是**对照 upstream 源码语义**（`Harmony/` 本地 clone 逐行核对）+ 27 条 metadata fixture 测试，尚未与真机运行的 PC Harmony 实测结果对照。
- 运行时侧的逻辑注册表与诊断导出已接通（见 3.5），Jipper 实测产出 1 条 Harmony 注册（JALib bootstrap 装在 `System.Type.GetConstructor` 上的 Prefix）。
- shim ABI 面已完成两轮语料验证 + 一轮上游全量声明 diff。`PatchInfo`、`FileLog.LogIL*`、runtime probes、switch API、`MethodInvoker`/`FastAccess` 反射 fallback、相关 delegate 类型和显式失败 API 均已补齐；当前为 61/61 类型、871/872 成员，唯一缺口是 v42/v44 不可共存的 `HarmonyReversePatchType.AllCombine` 字面量。发 IL 才能实现的 API 保留 ABI，在调用点显式失败并记录诊断。
- 「所有函数地址均从运行时 metadata 解析」在 2026-07-27 从"目录内目标成立"扩展到"任意目标成立"：新增 `modmanager_pccompat_resolve_target_signature`，导入期即可从活 IL2CPP metadata 取到目录外目标的精确签名，descriptor 因此能发出严格 native 解析器接受的规则（见 §3.2 末尾"目录外目标的签名来源已打通"）。这是 descriptor 进入 HookBroker 的第一段，Postfix 已通；provider 未注册时行为与打通前逐字一致。
- 同步 Prefix V2 与 HookBroker 共存已通过本机合同：96 B invocation frame、`void/bool`、`__instance`、generated-proxy `ref/out __instance`、`__originalMethod`、最多 6 个参数、primitive/enum `ref/out`、primitive/enum `ref __result`、`ref __runOriginal`、generated-proxy `___field` 写回、可写 `__args`、`__state` 配对及完整短路规则可用；deferred Postfix 已支持只读 `__args` 和按值 `__result`。隐藏 `MethodInfo*` 原样转发，异常/线程不符 fail-open，递归上限 32。Prefix/Postfix 的 `priority/before/after` 均按运行时 owner 做跨 MOD immutable snapshot 拓扑排序，registry revision 变化在当帧重建计划。下一主线是受控 struct-byref，再进入同步 Postfix/Finalizer/Transpiler/通用 ReversePatch。

### 11.4 性能

- HUD 隐藏、静态显示、普通输入和极端输入分别测量。
- 记录 Native executor、managed dispatcher、generated proxy、Unity setter、geometry rebuild、MOD 自身代码和总 HUD 时间。
- 设备上至少采集 60 秒 P95/P99。
- 超限降级只影响视觉粒子，不影响输入、判定或游戏状态。
- 单独记录 ingress 到 completed snapshot、snapshot 到 Unity 提交的延迟分布。
- 分别测量无 MOD、ManagedSelfRender、recipe fallback、资源加载开启和 rewritten oracle 审计模式，不能混用结果。

### 11.5 原设置菜单的移动端呈现合同

- 默认仍执行 MOD 自己的 UMM/JALib/Unity IMGUI callback，不从 schema 重画近似菜单；移动端 host 只提供容器、密度、输入所有权和被明确识别的 JALib 控件适配。
- 包内本地化文件是离线真源。JALib owner 在 setup 后必须优先按游戏的 `RDString.language` 读取 `localization/<SystemLanguage>.json`，随后才允许回退 CoreCLR UI culture、English、Korean；只有所有候选均缺失的 key 才允许原样显示。不能为了显示文字在运行期依赖网络表格。
- `JAModInfo.Gid` 必须保留在 manifest/诊断中，用于解释上游 JALib 缓存来源，但 Android 菜单运行期不访问 Google Sheet。若发布包不含上游已生成的 `localization/*.json`，导入器只能显式提示缺失或在独立、可重试的导入阶段生成缓存，不能让 OnGUI 等待网络。
- Feature 首屏必须完整列出且默认折叠，包括 MOD 仅 `Loaded`、尚未启用 gameplay lifecycle 的设置会话；`hostActive` 不能作为设置可见性门禁。Feature enable 与 expand 是不同状态；只有展开项运行原 `OnGUI`，单项异常按原 JALib 临界计数隔离，不能截断后续 Feature 或直接撤销整个 settings surface。
- slider/number、enum、toggle 和 string 必须保持不同控件语义。JALib slider 保留 horizontal slider + text field 的联动和写回语义；移动端允许把 label 与 slider/value 行拆成上下两层，并限制 slider 最大宽度，避免 MOD 在外层 `BeginHorizontal` 中追加 Reset 等操作时越过 panel。普通 number 保持 label + text field，enum 保持全部候选按钮并按宽度分行；只有上游 callback 本身不可执行并进入明确 fallback 时才允许 schema 采用近似控件。字段写回、原 callback、即时保存和范围限制仍由 MOD/JALib 语义决定。
- 面板在横屏限制最大内容宽度并居中；纵屏保留安全边距。标题、顶部关闭和底部保存/关闭位于滚动区外，Feature 内容位于唯一纵向滚动区。header 必须使用显式逻辑矩形：标题固定为 `TouchHeight` 高并保留垂直内缩，关闭按钮固定为 `TouchHeight x TouchHeight` 正方形；禁止让普通 GUILayout 的单字符自然宽度决定关闭按钮几何。`GUI.Label/Button(Rect,string)` 必须由当前 Android metadata 生成代理绑定，不能硬编码地址。设置字体按当前 MOD owner/resource generation 解析：唯一的精确 `UnityEngine.Font` 优先；否则从唯一静态 `TMPro.TMP_FontAsset` 的 atlas/metrics/material 重建 `UnityEngine.TextCore.Text.FontAsset`，并以一个私有 `UnityEngine.Font` 仅作 `GUIStyle.font` 身份键；两者均不可用时才回退当前游戏语言对应的 `RDString.fontData.font`。字号继续采用 `FontData.fontScale` 与有效 `Screen.dpi`，DPI 无效时才允许按屏幕短边回退。label、textField、textArea、button、toggle 必须使用同一字体，触控高度和 padding 与物理密度匹配。
- 临时修改 `GUI.skin` 时必须在同一 OnGUI callback 的所有正常/异常出口恢复。宿主不得把移动端字号、word-wrap 或 padding 泄漏到游戏 UI、其他 MOD、下一事件或下一场景。
- MOD 直接使用 `GUILayout.Button/Toggle(..., GUIStyle, ...)` 或 `TextArea` 时也必须满足当前 settings frame 的最小触控高度。兼容层只在单次控件调用期间临时提升 `GUIStyle.fixedHeight`，调用结束立即恢复原值；这类适配不得修改 MOD 保存值、布局展开状态或 gameplay 中复用的样式。
- 所有嵌套 GUILayout group 必须按 LIFO 清理。Feature body 的异常出口固定为 `EndVertical -> EndHorizontal`，随后才能关闭所属 scroll/root；某一 close 失败时仍要继续清理外层并最终透传首个异常，禁止把损坏的 layout stack 留给下一次 Layout/Repaint。
- Layout/Repaint 之间禁止立即改变 Feature 展开、折叠或控件数量。连续 callback 异常只能发布待折叠状态，并在下一次 `Event.type == Layout` 应用；关闭菜单必须清除该 pending 状态和异常计数。
- 根设置 callback 异常必须走不绘制 footer 的 `AbortFrame`：只恢复 GUILayout、矩阵、skin 和 settings-frame 状态并保留首个 MOD 异常。只有内容 callback 完整成功后才允许执行 `EndFrame`、保存/关闭按钮和相应 action。
- 导入闭包必须同时验证 MOD 对 UMM/JALib shim 的外部 TypeRef。闭包缺失属于导入/构建失败，不能留到真机 OnGUI 才以 `TypeLoadException` 暴露；Jipper 当前已验证需要 `JALib.Tools.JARandom` 与 `JALib.Tools.Unsafe.AsUnsafe<T>(object)`。
- MOD 在原设置 callback 内新建的 `GUIStyle` 仍属于原菜单。其硬编码 `fontSize`、`fixedWidth` 和 margin 必须仅在该 settings frame 内按同一物理密度缩放；退出 frame 后 bridge 恢复 1:1，禁止改变 MOD 的 gameplay HUD 或其他 IMGUI 表面。
- 字体解析成功或失败都必须有每进程一次的限频诊断。成功诊断至少包含游戏语言、DPI、fontScale、最终字号、触控高度、`hasFont=true` 和 `fontSource=VirtualBundle/RDString`；VirtualBundle 类型不匹配、候选歧义或投影失败必须保留完整导出诊断并自动回退游戏字体，Logcat 只输出有界摘要，不得伪报已使用 MOD 字体。
- Unity 6000 IMGUI 会通过 `TextSettings.GetCachedFontAsset(Font)` 把 `GUIStyle.font` 转成 TextCore FontAsset，不再消费手工写入的 legacy `Font.characterInfo`。兼容层必须由 HookBroker 按 metadata 精确解析并永久保留该入口：命中私有 Font 身份时返回同 owner 重建的 TextCore FontAsset，未命中时原样转发 instance、参数和隐藏 `MethodInfo*`。禁止再以 `new Font() + CharacterInfo[]` 或字体名称查找作为 MOD 字体实现。
- TextCore 重建只复用 Resource IR 中的静态 atlas、全部 character/glyph metrics 和 MOD Material，不修改 MOD 原 TMP Font、atlas、HUD 或 Material。Font 身份/TextCore 对按源资产与目标类型缓存并随同一 resource session 释放，注销 native 映射后先销毁投影对象，再释放源字体、Material 和 atlas。native lookup 使用固定容量紧凑原子表，热路径无锁、无分配且只扫描当前有效映射数。
- diagnostics export 必须同时记录 `SurfaceKind` 与最后一次 settings frame 的 width/height、DPI、language、fontResolved、fontScale、fontSize、touchHeight、panelWidth；`UnityCanvas + frame=not-rendered` 可直接定位 Canvas 误认，不能再从字体外观猜测调用链。
- settings 矩形诊断必须保留最后一个有效 Repaint 快照，关闭按钮的 MouseUp 不得把导出退化为 `rects=none`。采样只允许覆盖初始有界预算及控件结构变化后的下一次 Repaint，禁止为诊断在每个稳定 Repaint 持续反射读取全部控件。
- 设置输入采用唯一 owner 状态机：`None/ModManager/UnitySettings`。切换 owner 时必须清除上一套 UI 的 Dear ImGui ActiveID 或 Unity `GUIUtility.keyboardControl/hotControl`，并以 Android `WindowInsets` 的真实 IME 可见状态收敛；禁止只用 `WantTextInput` 或自维护布尔值推测键盘状态。
- `TouchHeight` 必须落实为实际控件最小高度。只有可换行 label 允许 `wordWrap=true`；button、toggle、text field 和 enum choice 禁止隐式换行。长 label/control 行允许切换成上下布局，enum 候选必须按可用宽度分行，任何控件矩形不得超出 panel 或与 footer 重叠。
- 原菜单中的 Feature 开关必须调用 MOD/JALib 原 `Enabled` setter，不允许只修改宿主 snapshot。写入成功后，宿主页 live binding、原菜单、持久化文件和实际 lifecycle 必须在同一 UnityMain 边界收敛；至少覆盖 `开 -> 关 -> 开`、关闭菜单后重开、场景切换和卸载。回调失败不得把 UI 显示成已提交状态。
- CoreCLR/Android 不支持 `Thread.Abort()`。导入重写器必须把 MOD 调用点降级到受控 managed bridge：null receiver 保留 `NullReferenceException`，已结束线程无操作，活线程只允许使用 `Thread.Interrupt()` 发出协作停止请求。兼容层不得声称等价异步强杀，也不得从 UnityMain 等待未知线程无限退出；只有 MOD 自身还具备停止字段、lifecycle gate 或可中断等待点时才可判定该 Feature 可安全切换。Jipper 的 `KeyInputListener` 由 `Enabled`/线程字段循环门禁退出，`OnDisable` 与 `ApplicationOnquitting` 两个调用点属于已验证模式。
- `Thread.Abort` 降级必须参与 managed rewrite cache 与 bridge ABI 指纹。诊断至少导出源调用点、bridge 重写数、目标线程名称/存活状态和 Feature 提交结果；停止后旧线程继续增长 `Work()` 异常、计数或 rain 均视为 lifecycle 失败，不能只因菜单未 fault 就判定成功。
- 原菜单的按键绑定按钮必须经过兼容 IMGUI bridge。除带 `GUIStyle` 的 overload 外，`GUILayout.Button(string, GUILayoutOption[])` 也必须重写；bridge 使用当前 `GUI.skin.button` 重建等价调用，不允许因 Android 代理恰好存在该 convenience overload 而绕过输入事务。
- 每次原设置 `BeginFrame/EndFrame/AbortFrame` 构成一个 settings input transaction。任意按钮在该 transaction 内激活后，本帧后续 `Input.anyKeyDown/GetKeyDown/GetKey/GetAsyncKeyState` 只能消费并记录 consumer/native baseline，必须返回无输入；下一完整设置帧才允许新的实体键 DOWN 成为绑定。该合同直接覆盖 Jipper 的“按钮返回 true 后在同一 `OnGUI` 立即枚举 `KeyCode`”模式，禁止把菜单触摸、按钮激活键或 IME 事件绑定成 `Backspace`。
- Android modal native gate 是跨 ALC、跨线程的权威状态，managed mirror 只用于提前门禁。settings input transaction 是更内层的 UI 原子边界，两者不能互相替代：modal 负责菜单整个生命周期的触摸隔离，transaction 负责拒绝按钮激活所在的单个 GUI 帧，同时保留下一帧真实外部键盘绑定。

### Harmony Postfix 顺序合同（2026-07-27）

- Harmony 的 Postfix 与 Prefix 使用同一 `PatchSorter`：priority 降序、registration index 升序，`before/after` 形成 owner 拓扑约束；Postfix 执行时不反转该结果。
- managed event dispatcher 必须为每个成功绑定的 Postfix 发布 owner、priority、registration index、before、after。发布缺失或 native plan 不可用时，仍允许事件按 bundle/target/rule 的确定性顺序执行，但必须保持 fail-open。
- HookBroker 在 rebuild 阶段建立 Postfix immutable event snapshot；hook 线程按 snapshot 入队，并为每个事件附加全局单调 `dispatch_sequence`。native 先合并同 MOD 的多个 bundle ring，UnityMain 再用可复用 collector 合并所有 MOD，必须在任何 `CompatUpdate` 前按 sequence 调用；逐 session drain 不满足跨 MOD 顺序合同。
- 事件 ABI 的参数和 hit snapshot 字段偏移必须保持稳定；144 B 旧记录后先追加 8 B `dispatch_sequence`，Prefix V2 再追加 32 B invocation/result 元数据，当前记录总长为 184 B。collector 仅允许在历史高水位增长时扩容，稳态禁止每事件分配和 owner 字符串解析；96 B Prefix frame 与 184 B event record 都由 native size export 在 Android bridge 安装时核对，native/managed 必须同批部署。
- Prefix 与 Postfix plan 分开 staging/commit，避免同一 patch id 或不同生命周期的 plan 互相覆盖；MOD 卸载必须先提交空 plan，再释放 managed session 和 event ring。
