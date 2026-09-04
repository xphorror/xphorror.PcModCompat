# Android Managed MOD 与 PcCompat MOD 运行时隔离设计

> 2026-08-24 状态修正：当前统一托管缓存 ABI 为 `xphorror.pcmod-managed-cache.v49-native-gui-surface`，IMGUI bridge ABI 为 `PcCompatManagedImGuiBridge.v7-native-gui-surface`。所有 bridge-owned 代理成员由共享精确签名契约同时约束闭包、扫描和最终审计；Android 缺失的 GUI surface 不再进入共享代理。PcCompat bootstrap 继续使用 native session generation 绑定文件/网络域，外部静态事件使用 `PcCompatManagedEventSubscriptionBridge.v2-delegate-conversion`，后台线程和任务使用 `PcCompatManagedThreadBridge.v2-background-scope`。文档下方早期缓存与桥 ABI 描述是历史记录，不代表当前实现。

> 2026-08-29 Native MOD 状态修正：Android Native MOD 使用独立的 callback-only shadow ABI。该模式只改写旧式 `HookHelper.Hook` 安装点及其托管 detour wrapper，在原生线程进入时恢复 owner/generation/domain callback scope；`Assembly.Location` 作为 shadow 执行路径恢复机制例外重写到原 MOD 目录，保证 Assets/Emoji 等非托管资源可定位；静态字段、文件、网络、异步和资源调用保持原始语义。没有旧式 Hook 的程序集仍保留原始语义。full isolation rewrite 仍保留为离线审计与显式工具能力，不作为 Native MOD 默认生产路径。

## 目的

本文定义 StArray ModManager Android Managed MOD、xphorror.PcModCompat MOD 以及两类 MOD 并存时的运行时隔离契约。目标不是把同一进程内的 MOD 建成安全沙箱，而是保证一个行为正常或发生可恢复故障的 MOD，不会覆盖另一个 MOD 的 Hook continuation、数值状态、HUD、Unity 对象和资源生命周期。

首个需要覆盖的真实组合是：Android Managed MOD XPerfect 与 PcModCompat MOD JipperResourcePack 同时加载。XPerfect 的托管 DLL 会直接 Hook `scrMisc.GetHitMargin`、`scrMarginTracker.AddHit` 等 IL2CPP 方法；JipperResourcePack 的 verified recipe 也会观察这些目标。生产实现不得按这两个 MOD 的 ID 选择规则，二者只作为交叉加载回归样本。

## 非目标

- 不防御同进程内主动恶意修改内存、直接调用任意 native 地址或主动销毁其它对象的 MOD。
- 不承诺旧的任意 nint Hook 可以安全地从任意工作线程卸载并立即回收 ALC。
- 不把所有 Unity 游戏事实复制一份。只读、不可变的官方游戏事实允许共享；MOD 派生的可变状态必须隔离。
- 不通过修改 XPerfect、JipperResourcePack 或其它第三方 DLL 来实现隔离。

## 当前实现状态

已落地：

- 每个 Android Managed MOD 使用独立 `NativeModAssemblyLoadContext`，MOD 自身程序集和私有依赖的静态字段不共享。
- Android Managed MOD 与 PcCompat MOD 使用不同 owner 命名空间：`native:<id>` 和 `pccompat:<id>`；`native` 仅为历史 ABI 名称。
- `BehaviourManager` 在注册时记录 owner，支持按 owner 挂起、恢复和退役。
- HookBroker 对每个 target 只安装一个稳定 Dobby gateway；后续 Hook 以 owner layer 追加，不再修改前一个 MOD 的 replacement。
- Hook layer 支持按 owner disable、enable、retire 和 retained-count 查询。Android legacy `HookHelper.Unhook` 在 owner scope 内执行逻辑退役，不再抛出 `NotSupportedException`。
- Dobby 托管去重键包含 owner。同一 `(target, detour)` 被两个 MOD 使用时会得到两个独立 layer。
- PcCompat managed event ring 已按 bundle 隔离，事件 ring 退役不会释放其它 bundle 的 ring。
- VirtualBundle 和部分 ResourceChanger 托管状态已经携带 `modId` 与 session generation。`ReleaseWithSession` 代理现在登记为独占 release lease，卸载批次携带 owner/generation；release sink 返回前 lease 保持 retiring，防止新 session 重新领取随后被旧 sink 销毁的对象。
- `PcCompatUnityHudRuntime` 支持显式 owner/session source snapshot；Android Unity renderer 为每个 owner 创建独立 `HudSurface`，分别维护 root、资源引用、布局缓存和故障状态。同一 owner 重载到新 generation 时先销毁旧 root 再创建新 root；旧 generation 的资源释放不会清理新 surface 的资源。
- KeyViewer fallback frame 现在携带 session generation；Android fallback visual 在 owner 相同但 generation 变化时销毁旧 root 后重建，避免 MOD 重载复用旧 visual。
- `PcCompatOverlayRuntime.Snapshot(ownerId)` 为每个 owner 返回独立托管快照对象，卸载时可单独移除 owner 投影。
- native overlay telemetry 已按 owner 建立 `OwnerOverlaySession`。dispatcher 发布不可变 owner subscriber snapshot，共享游戏事实每次 Hook 只采集一次，各 owner reducer 独立维护 visibility、combo、BPM/KPS、进度和回调节流状态。
- lifecycle VM 的 overlay state 已按 recipe bundle 发布，一个 bundle 的 show/hide 不再触发其它 bundle 的生命周期程序。
- managed overlay provider 已增加 owner-aware ABI；旧无 owner snapshot 和 scalar getter 只作为兼容聚合视图，读取当前默认 owner，不参与 owner 主路径。
- ResourceChanger native 状态已改为 `(modId, sessionGeneration)` contribution 注册表。当前 owner 按稳定 registration sequence 选择，卸载当前 owner 后会恢复 Unity 基线并重新应用前一 owner。
- ResourceChanger Rabbit Sprite 已按 owner/session 保存 GCHandle，只有当前 ResourceChanger lease 的 sprite 会投影到 Unity；旧 generation 的延迟发布不会覆盖新 session。
- generated component bridge 通过 native `AddComponent` 返回的 Unity component 已登记为 owner/session lease。`SetEnabled`、立即 `Destroy` 和延迟 `Destroy` 都会校验 lease owner；同一 GameObject 上仍有其它 MOD lease 时，在产生 Unity 副作用前拒绝销毁。
- native component lease 已接入 session teardown。Unity `Destroy` 失败时保留 lease 供后续 teardown 重试；对象已由 Unity 回收时只退休 lease，不重复调用 `Destroy`。`DontDestroyOnLoad` 的 ownership proof 同时检查 managed component 与 native component lease。
- component bridge lease 的 owner GameObject 也会进入 `SnapshotOwnerGameObjects`，因此 Android Managed MOD创建的 Canvas不会在设置页 owner probe中被误认成外部 Canvas。
- Host 已实现统一 `ModRuntimeKey { LoaderKind, ModId, Generation }`。`ModEntry` 与 `NativeModLoadState` 共享会话，真实重载递增 generation；保留 process-lifetime Hook 的同实例挂起/恢复保持原 generation，旧 `native:<id>` / `pccompat:<id>` owner ABI 不变。
- Source Generator 生成的 typed Hook wrapper 在安装时捕获 generation，并通过 HookBroker generation v2 flag 固化为 `managed callback gate` layer；每次 detour 进入取得 callback lease，退休后不再进入 MOD，而是调用 original trampoline。shadow ABI `starray-native-isolation-rewrite-v11-complete-callback-scope` 起，Android Managed MOD 中形如 `HookHelper.Hook(target, Marshal.GetFunctionPointerForDelegate(...ldftn dispatch...))` 的旧式手写 Hook 也会在加载时统一升级：安装点捕获当前 generation gate、通过 `HookRuntimeGatedRequired` 固化 HookBroker managed callback gate layer，dispatch 外包一层 callback lease，并恢复完整 owner/session/generation/domain 上下文；这样回调内的静态槽、资源归属及文件/网络桥使用同一代 MOD 身份。退出按 LIFO 恢复原上下文，不在高频 detour 上执行全量资源审计。退休竞态拒绝进入；业务异常不会越过 reverse-P/Invoke 边界，并按 callback 首次及 2 的幂次数限频记录。Host 自身的 EGL 等进程级 Hook 继续使用 `HookRuntimeGated`，无 MOD scope 时合法退回普通 Host Hook；严格入口只供 shadow MOD，空 gate 必须拒绝。无法证明静态 `ldftn` dispatch 的未知 Hook 形态失败关闭；未经过 shadow rewrite 的 legacy generation ABI 仍属于 untracked callback。同一 layer identity 改变分类会被拒绝，不会生成重复 layer。`BehaviourManager`、MOD 背景/前景 ImGui、设置页和 PcCompat managed Update/OnGUI/event/Prefix 入口使用同一 active callback 计数。
- `ModRuntimeOperations.Begin/TryBegin` 为 Android Managed MOD 和 Host 托管插件提供公开后台 operation lease。lease 绑定当前 owner/generation，携带退休取消令牌；只有任务停止访问 MOD、Host 和 Unity 资源并释放 lease 后，quiescence 才会归零。重复释放、跨 owner 或旧 generation 释放不会减少当前 generation 的计数。
- `ModNativeOperations` 和 `modmanager_native_operation_*` ABI 不属于 Android Managed MOD 的公开能力。它们只保留给 ModManager 自身或明确受控的 Host native 组件，不能作为 DLL MOD携带私有 `.so` 或启动 native worker 的旁路。
- ModLoader 卸载顺序已经扩展为阻断 Managed/native 新注册、等待 native operation、关闭 owner Hook、等待 Managed callback/operation quiescence、执行 owner 清理，再决定 `SUSPENDED` 或 `RETIRED`。无持久 Hook 且全部引用静默的失败加载会执行 best-effort `OnUnload`、释放 collectible ALC；半初始化且仍有持久 Hook或 operation 未退出时保留映射并拒绝进程内重试。
- Android Managed MOD只携带 CoreCLR托管 DLL。任何 MOD目录私有 `.so`、`NativeLibrary.Load`、`dlopen/dlsym` 或裸 native worker 都不属于 Android Managed MOD支持合同；Host可在审计中记录发现结果并拒绝相关 feature，不能把它误记为已隔离的 Android MOD资源。
- `ModDataDomainToken` 已落地为固定 24 字节的 `ProcessCookie + Generation + SlotIndex + LoaderKind`；domain slot 支持 ABA 防护、嵌套 scope 和 callback lease，旧 generation token 无法解析到新 domain。
- `IsolationManifest` 已具备确定性 JSON/hash、UTF-8 严格读取、原子写入和入口程序集身份记录。MOD目录存在 `isolation.json` 时会读取，否则生成保守 `Guarded` bootstrap 清单；清单身份与入口 DLL 的 SHA-256、MVID、名称、版本或大小不一致时，在 `OnLoad` 前失败关闭。
- CIL Semantic Pack 工具已使用 PC 3.1.2 DLL 生成确定性方法流、异常区/operand/locals、API surface、MVID/哈希和 `E:\TEMP_SHARE\adofai_decomp_312` UTF-8 源码树身份；它是 shadow rewrite 的输入审计产物，不等于 shadow rewrite 已完成。
- Android Managed MOD生产加载已使用 content-addressed shadow v3：入口和静态私有依赖闭包经metadata-only解析，逐文件校验后原子发布；扫描、重载和程序化加载都从包内路径执行。marker绑定cache key与manifest SHA-256，缓存命中复核manifest、静态槽证明和全部程序集。缓存篡改、缺失私有依赖和扫描后源变更均在 `OnLoad`前失败关闭，shadow目录不会被MOD扫描器误识别为MOD。
- metadata-only发现已覆盖声明入口和唯一具体 `IModPlugin`，并只解释可证明的常量metadata getter；插件构造和 `.cctor`延迟到 `BeginLoad`建立domain之后。无法证明的旧格式保留显式 `LegacyReadOnly` fallback，不冒充零副作用发现。
- Android shadow provider已实现第一批生产重写：`Assembly.Location`回到原MOD目录；非强签名MOD程序集的直接 `ldsfld/stsfld/ldsflda`进入稳定domain cell；原静态构造逻辑按domain执行一次且失败sticky。静态槽成员身份与ID写入shadow manifest，并绑定当前generation的 `IsolationManifest`。
- Android Managed MOD 异步生命周期重写已接入 shadow ABI `starray-native-isolation-rewrite-v3-async-domain`：所有自有 `Task`/`Task<T>` 返回方法在入口验证当前 domain，并在返回的未完成任务上登记 generation-bound operation；`Task` 身份和异常/取消语义保持不变。`Task.Run`、常用 `Thread`、`ThreadPool.QueueUserWorkItem` 和 `Timer` 重载改写到 `ModRuntimeAsyncBridge`，调度回调显式恢复 owner/session/domain scope，Timer 还登记 terminal cleanup。
- 异步重写同时写入每程序集 proof（方法身份、重写种类、计数）并绑定 shadow marker/cache ABI。`async void`、`ValueTask`/`ValueTask<T>`、`TaskFactory.StartNew`、`Task.ContinueWith`、手工 `Task`/`Start`、`ExecutionContext.SuppressFlow`、未覆盖的 Thread/ThreadPool/Timer 调度重载、`SynchronizationContext` callback 和 `CancellationToken.Register` 在 shadow 发布前失败关闭，不静默降级。
- direct static热路径为当前domain解析、`ConcurrentDictionary`槽查找和cell读写，不争用domain全局锁；首次工厂和 `.cctor`状态机才加锁。XPerfect、Replay、ShowBPM真实DLL分别通过378/83、645/222、199/70条访问/槽位的改写审计。
- Android Managed MOD 文件路径隔离已接入 shadow ABI `starray-native-isolation-rewrite-v4-file-domain`（旧 v3 shadow cache 自动失效）。每个 domain 绑定 `InstallRoot/ConfigRoot/CacheRoot/LogRoot/TempRoot` 与可选共享只读根：MOD 原目录只读（执行走 shadow 包），四个可写根位于 Host 拥有的 `.starray-data/<mod>/` 之下，官方资源目录只读放行、写入拒绝。相对路径不再解析到进程 CWD，而是锚定当前 domain 的 config 根；`Path.GetFullPath` 因此被改写，`Path.Combine/GetDirectoryName/GetFileName` 是纯字符串函数、刻意不改写。跨 MOD 根访问在产生任何文件副作用前拒绝，并在诊断中点名归属 owner/generation；越界、无 domain、未绑定根和退休 generation 均失败关闭。`.starray-data` 与 `.starray-shadow` 一样被 MOD 扫描器排除。
- `StreamWriter(string, bool, Encoding)` 已纳入 Android Managed 文件域桥：构造点改写为同栈形状的 `NativeModPathBridge.OpenStreamWriterEncoding`，先解析 owner 可写路径再创建 writer。该覆盖来自 LevelDebugger 真实程序集审计，不放行原始路径构造，也不为单个 MOD 设置白名单。
- PcCompat MOD 文件路径隔离已落地（`PcCompatManagedPathBridge.v1` 已进入 `CollectionBridgeAbi`；显式缓存版本号 `xphorror.pcmod-managed-cache.v33-net-domain` 于网络切片一并落地——该切片期间旧缓存失效由缓存键中的逐规格哈希承担）。`PcCompatManagedPathBridge` 是 `NativeModPathBridge` 的 PcCompat 对应物：两者不能合并成一份实现，因为归属键不同——Android Managed MOD 从 domain token 解析 `ModDataDomain`，PcCompat MOD 携带 `PcCompatManagedExecutionState(ModId, ResourceSessionGeneration, Phase)`；但包含判定复用同一个 `ModDataDomainPaths.IsWithin`，安全敏感的比较只存在一处。
- PcCompat 每个 MOD 会话绑定五个根：`InstallRoot` 为 MOD 目录（只读包层，执行走托管缓存），四个可写根位于该 MOD 目录下的 `.pccompat-data/`。写入检查先匹配可写根再匹配只读安装根，因此 `.pccompat-data` 虽在 InstallRoot 之内仍可写。可写根**不按 generation 分目录**，使设置在 MOD 重载后仍然存在；generation 绑定由 roots 注册表键与 `Disable` 时的 `ClearRoots` 保证，退休 generation 因"根未绑定"失败关闭。与 Android Managed 侧把可写根放在 `Mods/.starray-data/<mod>/` 不同：PcCompat 会话只拿到 `Manifest.FolderPath`，没有 Host 级 mods 根，这是有意的不对称而非疏漏。
- PcCompat 安装根已升级为 owner-scoped VFS 两层视图（`PcCompatManagedPathBridge.v2-vfs-overlay`，缓存 ABI `xphorror.pcmod-managed-cache.v35-vfs-overlay`）。新增 `DataOverlayRoot = <mod>/.pccompat-data/data`：指向安装根的**写入全部落入 overlay 并保持相对布局**，安装目录物理上不再被 MOD 触碰；**读取按 data-first/package-second**——overlay 存在该相对路径时返回 shadow，否则原样读取包层文件。因此 MOD 隔离前已存在的旧设置（Jipper 的 `Settings.json`/`Settings.json.bak`/`KeyCodes.json`）无需迁移拷贝即可继续读取，首次保存后自然被 shadow 遮蔽——「显式设置迁移合同」以此语义闭合。删除只移除 overlay 副本，包层原件重新可见；对仅存在于包层的源做 `Move` 时以"复制进 overlay 目的地 + 包层保持不变"模拟（不可变层无法被移出），这是影子语义下唯一与原始 Move 可观察行为有差异的点，已在验收矩阵标明。真实依据：Jipper `KeyCountData.SaveData` 的 `Delete(.bak) -> Move(dat -> .bak) -> FileStream(CreateNew)` 循环与 `Main.Instance.Path` 绝对路径读写，在 v1 只读安装根下三步全部失败关闭、计数无法持久化；VFS 后整条循环在 overlay 内正常往返。
- PcCompat 文件改写覆盖 `Path.GetFullPath/GetDirectoryName`、`File.Exists/ReadAllText/ReadAllBytes/WriteAllText/WriteAllBytes/Delete/Copy(2,3)/Move/OpenRead/OpenWrite`、`Directory.Exists/CreateDirectory/Delete(1,2)` 与 `FileStream` 的 2/3/4 参构造。`Path.Combine/GetFileName` 仍是纯字符串函数；`GetDirectoryName` 因 UMM `ModEntry.Path` 是虚拟包根而具有 owner 语义：根的父目录钳制为根自身，根内子路径保持标准语义，跨 MOD/根外绝对路径拒绝。`Stream`/`MemoryStream` 的实例方法作用在已解析的流上，同样不需改写。跨 MOD 根访问在产生文件副作用前拒绝并点名归属 owner（含同 MOD 的旧 generation）；越界、`..` 遍历、兄弟目录前缀、无 scope、disable 阶段与根未绑定均失败关闭。
- PcCompat 后台执行作用域已覆盖 `Thread(ThreadStart)` 与 `Task.Run(Action)` 两个经真实 MOD 证明的调度入口（`PcCompatManagedThreadBridge.v2-background-scope`，当前统一缓存 ABI 为 `xphorror.pcmod-managed-cache.v47-native-gui-focus`）。线程构造发生在生命周期 scope 内，桥捕获当前 `(modId, resource generation, phase)` 并在线程入口恢复；任务桥从当前普通或流动 scope 再捕获状态，并只在后台任务内启用 `AsyncLocal`，使 `async void` 的 continuation 穿过 `await` 后仍能访问同一文件/网络/资源域。每帧 managed Update/OnGUI 继续使用 `[ThreadStatic]`，不承担 `AsyncLocal` 写入成本。真实链路 `Jipper KeyViewer.Work(listener thread) -> KeyCountData.Save -> Task.Run(SaveData) -> await Task.Delay -> File.*` 已由运行测试和主 DLL IL 重写测试闭合；Bootstrap 的后台安装任务也命中同一精确规则。无 scope 调度、退休 generation 后访问与未知调度重载仍失败关闭。
- **每 MOD 资源预算首个切片已落地**（「每 MOD资源预算」章节）。预算挂在既有 `ModOwnedResourceRegistry.TryRegister` 上——所有 owner-scoped 资源本来就都走这一处，因此一个门覆盖全部已登记资源类别，而不是把检查复制到每个调用点。`ModResourceBudget` 按 `ModOwnedResourceKind` 给出软/硬上限：软上限**只在跨越点告警一次**（不改变 MOD 语义、也不逐次刷日志），硬上限**只拒绝该 MOD 的新登记**，不回收他人资源、不撤销已安装 Harmony 补丁、不强制释放持 lease 或 Unity 所有权的对象。存活计数在注册锁内统计，避免并发登记越过上限。
  - **退休始终允许**：预算只拦"新申请"，`Retire` 不受限——处于上限的 MOD 仍能干净卸载，且释放后额度可复用（有回归覆盖）。
  - **generation 作用域**：额度随 `ModRuntimeKey` 的 generation 计算，重载落在新 generation 上、不继承上一代用量。
  - **刻意豁免 `Hook`/`CodePatch`/`Symbol`**：这三类按设计是进程期永久的（退休只翻逻辑门），拒绝登记会让注册表与物理 Hook 链**失同步**，反而制造更坏的状态；它们保持无上限但仍在审计快照内。
  - **Host 不占 MOD 额度**：该注册表只登记 MOD 拥有的资源，ModManager/游戏/授权因此天然享有不被 MOD 挤占的保留额度。上限是 Host 侧常量、不暴露给 MOD，MOD 无法自行提高。
  - 当前上限是保守起点（如 `NativeLibrary` 软 1/硬 4——MOD 只应携带托管 DLL，本就是可诊断降级；`AsyncOperation` 软 64/硬 256）；按文档要求需以设备压力数据重新标定，且 Broker 积压/HTTP 并发/JIT 缓存/动态 thunk/临时文件等尚未进入本注册表的类别仍是本章剩余项。
- **Direct Link 调用门骨架已落地**（`ModDirectLinkGate`）。按合同实现了可在无真实跨 MOD 样本时验证的核心不变量：链接按**双方 generation** 登记（`TryRegisterLink`，`Inferred` 标记未在 manifest 声明但唯一可解析的依赖）；调用时**同时持有双方 callback lease** 并进入 Provider 的 owner scope，因此 Provider API 内部创建的 Hook/资源/任务/文件网络操作归 Provider；返回**与异常**两条路径都按 LIFO 恢复 Consumer scope；`A -> B -> A` 同线程重入按合同允许；Provider 异常**原样传播**（具体类型、对象身份、inner exception 均不被 Host 包装，测试直接断言 `Is.SameAs`）；链接缺失或任一方 generation 已退休时抛显式 `ModDependencyNotReadyException`，**不返回副本状态或空占位**；卸载在两个 owner 退休点调用 `ReleaseLinksFor`，退休 generation 既不能被作为 Provider 进入、也不能再作为 Consumer 驱动他人。链接表锁在进入 MOD 代码**之前**释放，符合"生命周期图、链接表和缓存锁不得在进入 MOD 代码时持有"。
- Direct Link 骨架**刻意未做**的部分（等真实 Provider API 形态后微调，避免把猜测固化成需要回退的假设）：API Surface Hash 身份与自动候选绑定、`AssemblyRef/MemberRef` 闭包扫描、Consumer delegate 的反向调用门、Provider 自定义对象的 `CrossDomainObjectLease`、依赖环的 SCC 整体 staging、Provider 热更新后的 staging 重绑定。今天对六个真实 MOD 程序集的审计仍为**零跨 MOD 发现调用点**，因此这些都没有可对照的真实形状。
- `UiOwnerScope` 首个切片已落地（ImGui 合成与输入隔离章节）。Host 的三条 MOD 绘制路径（`OnBackgroundGUI`/`OnForegroundGUI`/设置页 `IModSettings.OnGui`）现在统一经 `UiOwnerScope.TryDraw`：进入时注入稳定的 per-owner ID namespace（两个 MOD 的同名窗口/控件/popup 不再解析到同一 ImGui ID），退出与异常路径都恢复；MOD 抛异常被就地熔断，连续 `4` 次失败后按 `(ownerId, generation)` 隔离该 MOD 的 UI，穿插成功即清零计数；卸载/失败加载在 owner 退休处调用 `Release` 清账，重载落在新 generation 上因此不继承上一代的隔离状态。
  - **修复的真实缺陷**：设置页此前把 `settings.OnGui()` 直接放在 Host 的 `ImGui.Begin(...)` 与 `ImGui.End()` 之间且无 try/catch——MOD 一抛异常，`PopTextWrapPos`、保存按钮与 `End` 全部被跳过，整帧 ImGui 窗口栈失衡，ModManager 自身 UI 随之崩坏。这正是本章要求的"只熔断该 MOD UI"。
  - **无 context 守卫**：cimgui 不对 context 指针做 null 检查，`PushID` 在无 context 时会让进程 fatal。`TryDraw` 因此以 `GetCurrentContext()` 为门：无 context 时跳过 ID 注入但仍执行回调、熔断与记账（首版实现缺此守卫，托管测试直接崩测试宿主，已修并由 5 条回归覆盖）。
  - **明确的实现边界**：ImGui.NET 不暴露 context internals，托管侧读不到真实的 window/style/color/ID 栈深度，因此**无法回卷 MOD 泄漏的 push**。当前保证的是 Host 自身配对不被破坏 + ID 命名空间隔离；本章"scope 保存并校验 window/style/font/color/ID/group/popup/clip 栈"的完整快照-恢复需要新增 native cimgui 导出，记为本章剩余项，不得据此把本章标为完成。
- **符号链接/重解析点穿透已失败关闭**（§4.10 明列要求，此前从未实现；shadow ABI `starray-native-isolation-rewrite-v8-link-guard`、PcCompat 缓存 ABI `v36-link-guard` + `PcCompatManagedPathBridge.v3-link-guard`）。归属判定 `IsWithin` 是纯词法比较、`Path.GetFullPath` 只做文本规范化，二者都不跟随链接：MOD 只要在自己可写根内建一个目录联接（Windows 无需提权，`Directory.CreateSymbolicLink` 是普通 BCL 调用），`configRoot/escape/victim.json` 就会带着**合法前缀**通过全部归属校验并触达另一 MOD 的根、游戏目录或系统路径。已在两条管线的解析路径上、归属通过之后、任何文件副作用之前加入共享的 `ModDataDomainPaths.TraversesLinkBelow`：自目标路径向上逐级检查已存在组件的 `LinkTarget`/`ReparsePoint`，遇链接即拒绝。只检查**已存在**的组件（待创建路径不可能是链接，其父级已被覆盖），且**向上只走到根为止**——根之上的 Host 侧链接（模拟存储路径、重定位数据目录）不是 MOD 行为，不得因此拒绝其访问；组件无法探测时按失败关闭处理。回归用真实 junction 验证：读与写均被拒、受害 MOD 文件字节不变、链接旁的普通路径不受影响（防止过度拒绝）。
- Android Managed MOD 的自更新已改为 **overlay 即暂存区**（shadow ABI `starray-native-isolation-rewrite-v8-link-guard` 起沿用）。安装根内的写入**不再按扩展名分流**：数据文件与 MOD 自身程序集走同一条规则，都落进该 owner 的 data overlay。对程序集而言这构成一次 **pending self-update**——loader 仍然只读包层，因此新二进制在 Host 激活前完全惰性；包层原件从未被修改，**回滚即删除 overlay 条目**。待激活清单由 `NativeModPathBridge.SnapshotPendingSelfUpdates(roots)` 从文件系统事实直接枚举（overlay 内的 `.dll/.exe` + 其包层对应物），不使用旁路账本：因此它跨进程重启存活，且**不会因为 MOD 删除自己的下载目录而失效**（Jipper `InstallScreen` 就有 `Directory.Delete(TempPath, true)`）。激活策略（用户确认/备份/manifest 重签）属后续切片，当前未实现——写入成功但不生效，且可枚举可见。
- 关于 MOD 自更新的立场记录：先前版本对安装根内 `.dll/.exe` 写入采取失败关闭（理由是防止"更新成功但永不生效"的静默失效）。经决策改为受控放行 + 惰性暂存 + 可见待激活清单：更新器的流程得以完整执行（可继续解压/校验），而是否进入包层仍由 Host 决定。**必须明确的安全边界**：Host 无法验证 MOD 从自有服务器下载字节的真实性，只能保证换包是原子的、有备份的、对用户可见的；这不是供应链信任，是事务性与可回滚性。
- PcCompat MOD 直接创建的 Unity 对象已进入 owner 登记。`new GameObject(string)`、`Object.Instantiate<T>(T)` 与 `Object.Instantiate<T>(T, Transform)` 改写到 `PcCompatManagedComponentBridge.CreateGameObject/Instantiate`，对象在返回给 MOD 之前以当前 `(modId, sessionGeneration)` 登记；登记失败会销毁刚创建的对象再抛，不留下无主 Unity 对象。Instantiate 只登记克隆体，原型保持借用语义不被认领。这些对象因此进入 `SnapshotOwnerGameObjects` 审计快照、受既有 `Destroy` 跨 owner 拒绝保护，并由 session teardown 清理。无 owner scope 或处于 disable 阶段时失败关闭。PcCompat 托管缓存 ABI 递增为 `xphorror.pcmod-managed-cache.v31-created-object-registration` + `PcCompatManagedComponentBridge.v7-created-objects`，旧缓存自动失效。
- 跨后端统一 lease 审计接口已落地（阶段 4 的前半）：`PcCompatUnityObjectLeaseAudit.Snapshot(modId, generation)` 把四套 registry（component bridge 宿主对象、VirtualBundle 会话、ResourceChanger contribution、HUD surface）的 per-owner 库存聚合成一个只读快照，`IsClear` 单点回答"该会话是否仍被任何后端持有"；诊断导出的 per-MOD 段新增 `unityLease=` 行。归属、teardown 与恢复语义仍留在各 registry 内，未动任何所有权逻辑；完整的跨后端 `UnityObjectLease` API 归一仍是待实现项。
- PcCompat 改写规格新增构造改写能力：`ManagedCallBridgeRewriteSpec.SourceIsConstructor` 使该 spec 匹配 `newobj` 而非 `call/callvirt`。`newobj T::.ctor(args)` 替换为 `call Bridge(args) : object` 栈平衡不变（两者都弹参数、压一个引用），再由既有 `AllowObjectReturnCast` 插入 `castclass` 还原具体类型。`EraseBridgeGenericArity` 允许一个非泛型桥服务泛型源重载（泛型实参只决定产出哪个 Unity 类型，桥收发 object）。
- Android Managed MOD 网络会话隔离已接入 shadow ABI `starray-native-isolation-rewrite-v5-net-domain`（v4 及更早 shadow cache 自动失效）。每个 domain 首次用网时惰性建立独立网络身份：自有 `CookieContainer`、自有 handler 管线与连接池，两个 MOD 不共享会话 Cookie 或凭据。`new HttpClient()` / `new HttpClient(handler[,bool])` / `new HttpClientHandler()` / `new CookieContainer()` 改写到 `ModRuntimeNetworkBridge`；MOD 拿到的 client 外层套 Host `DelegatingHandler`，每个请求取得 generation-bound operation lease 并把 lease 取消令牌与请求令牌联结，因此卸载会取消在途请求并等待静默，退休后拒绝新请求，跨 owner 使用该 client 在发出请求前拒绝。domain 退休时 terminal cleanup 调用 `CancelPendingRequests` 并释放该 generation 的全部 client。
- 网络改写只作用于**产生 client 的构造点**。已绑定 domain 的实例操作（`GetAsync`、`DefaultRequestHeaders`、`Timeout`）以及其返回对象（`HttpResponseMessage`、`HttpContent`、header value）继承该 client 的 domain，刻意不改写——与 `Path.Combine` 不改写同理。`ServicePointManager`、`WebRequest/HttpWebRequest` 工厂、`WebClient`、`SocketsHttpHandler`、`System.Net.Sockets.*` 原始套接字和未覆盖的 client/handler/cookie 构造重载在 shadow 发布前失败关闭。
- PcCompat MOD 的外部静态事件订阅已进入完整登记与 ABI 转换（事件桥 ABI `PcCompatManagedEventSubscriptionBridge.v2-delegate-conversion`；首次落地缓存 ABI 为 v45，当前统一缓存已升至 `xphorror.pcmod-managed-cache.v47-native-gui-focus`，旧缓存自动失效）。改写后的 `add_` 和 `remove_` 访问器分别调用 `Subscribe`/`Unsubscribe`，由 Android 宿主注入的 converter 将 CoreCLR delegate 转成 accessor 实际要求的 IL2CPP delegate；转换按“源 delegate + 目标 delegate 类型”缓存，`remove_` 与异常 teardown 复用同一 wrapper 和精确 remover。登记按 `(modId, resource generation)` 记录；正常 `Unsubscribe` 成功后从登记移除，`Disable`/失败加载仍由 `RetireOwner` 逐条清理。这样修复了 `System.Action` 传入 `Il2CppSystem.Action` 导致 Jipper `KeyViewer.OnEnable` 抛 `ArgumentException` 的真实阻断，并避免原始 `remove_` 在 `OnDisable` 再次触发 ABI 错误。实例事件、代理面之外的事件和裸 `Delegate.Combine` 未改写，仍是可诊断的隔离降级。真实审计：Jipper 主 DLL 4 处订阅点（`SceneManager.sceneUnloaded` 于 `Main.OnEnable/OnDisable` 配对、`Application.quitting` 于 `KeyViewer.OnEnable/OnDisable` 配对）；Android Managed 语料（XPerfect/ShowBPM/Replay）为 0 处，故该管线本切片无目标、不做。
- 文件改写覆盖 `Path.GetFullPath`、`File.Exists/Delete/Copy(2,3)/Move(2,3)/ReadAllBytes/OpenRead/GetLastWriteTimeUtc/WriteAllText(Encoding)`、`Directory.Exists/CreateDirectory/Delete(1,2)/EnumerateFiles(3)` 和 `FileStream` 的 2/3/4/6 参构造；每程序集写入 file proof（方法身份、种类、计数）并绑定 shadow marker/cache ABI。`File.ReadAllText/AppendAllText/Open/Create` 等未覆盖入口、`FileInfo`/`DirectoryInfo`、`StreamReader/StreamWriter` 路径构造、`Path.GetTempPath/GetTempFileName/GetRandomFileName`、`Environment.GetFolderPath/CurrentDirectory` 和 `Directory.SetCurrentDirectory` 在 shadow 发布前失败关闭，不静默降级；进程全局 cwd 不被修改。强签名程序集需要 file 改写时同样失败关闭。
- PcCompat MOD 网络会话隔离已落地（缓存 ABI `xphorror.pcmod-managed-cache.v33-net-domain` + `PcCompatManagedNetworkBridge.v1`，旧缓存自动失效）。`PcCompatManagedNetworkBridge` 是 `ModRuntimeNetworkBridge` 的 PcCompat 对应物，归属键同为 `PcCompatManagedExecutionState(ModId, ResourceSessionGeneration, Phase)`：会话构造时绑定独立 `CookieContainer` 与 handler 管线，`Disable` 时取消在途请求并释放该 generation 的全部 client。`new HttpClient()` / `new HttpClient(handler[,bool])` / `new HttpClientHandler()` / `new CookieContainer()` 改写到桥（CookieContainer 按 `System.Net.Primitives` 与 `System` 两种声明程序集注册，避免跨目标框架静默零匹配）；已绑定实例操作与其响应对象继承该会话身份，刻意不改写。跨 owner 使用 client 在发出请求前拒绝并点名归属与调用方；无 scope、disable 阶段与退休 generation 均失败关闭。`ServicePointManager`、`WebRequest/HttpWebRequest`、`WebClient`、`SocketsHttpHandler` 与原始套接字没有改写规格，属可诊断的隔离降级而非受支持路径。真实审计：`JAMod.Bootstrap.dll` 是 PcCompat 改写闭包的第二根（manifest `Info.json` 的 `AssemblyName` 指向它，`PcCompatRuntime` 以重写副本加载进 collectible ALC），也是 JALib 的 MOD 自更新下载器——12 处 `System.Net.Http` 引用中仅 1 处构造点（`Installer/<InstallMod>` 的 `new HttpClient()`），其余 11 处为已绑定实例/响应对象操作，印证只改写构造点的设计；`JipperResourcePack.dll` 0 处网络引用。

部分完成或尚未完成：

- ResourceChanger 已形成一组独占属性 lease；generated component bridge 的共享属性 contribution registry 已通用化为按 `(对象, 属性)` 索引的描述符表，当前覆盖 `Behaviour.enabled` 与 `RectTransform.anchoredPosition`；其余共享属性（`Transform.localScale`、`CanvasGroup.*`、`Graphic.color`）有争用证据但合成语义未定，尚未加描述符。
- MOD 直接实例化的 GameObject 已自动登记（见下方 PcCompat 创建登记条目）；VirtualBundle 与 HUD 各自具备 owner/session lease 语义。跨后端统一 `UnityObjectLease` 的**审计半**已由 `PcCompatUnityObjectLeaseAudit` 提供，**销毁/恢复协议半**已由 `PcCompatUnityLeaseTeardown` 提供（按固定后端顺序驱动各 registry 的既有 teardown，逐后端隔离失败并汇总，不复制归属逻辑）。仍未做的是把四套 registry 的内部存储真正合并成一份实现——这属重构而非能力缺口，且需先有跨后端争用的真实证据。
- Source Generator typed detour 已有在途计数；任意手写 nint/ABI legacy detour仍没有统一 callback token。只要仍存在未退役或无法证明无在途调用的 layer，ModManager 必须保留插件实例和 ALC；arm64 实机验证前不得宣称可无条件立即回收。
- MOD绕过隔离 ALC自行加载 `.so` 或创建不受 Host控制的 native线程时，属于明确的隔离降级；Host只做低频审计和 Warning/拒绝，不宣称能够回收或沙箱化这类代码。Hook、HUD、Unity、Provider和公开 API登记的 managed operation进入统一 owned-resource审计快照。
- shadow v3仍不是完整CIL沙箱。当前对 `ThreadStatic`、mutable RVA、未闭合或无法证明的泛型静态字段、volatile/unaligned前缀、字段句柄逃逸和跨私有程序集直接静态字段访问失败关闭；编译器只读RVA blob归类 `SharedImmutable`，无需重写的强签名私有依赖按原字节进入每MOD ALC。动态程序集、字符串反射、事件和P/Invoke调用点尚未全部改写；无法从静态引用图发现且由MOD自行动态加载的程序集也没有按需shadow管线。异步、文件与网络已覆盖的入口仅限上方明确列出的白名单，未知调度器、未知文件 API 和未知网络入口继续失败关闭。
- 文件与网络会话隔离已落地（见上）：「文件、设置与网络隔离」章节的核心合同在 Android Managed 与 PcCompat 两条管线上均已闭合；PcCompat 侧的旧设置连续性由 VFS overlay 语义解决（无需迁移拷贝）。仍未做的是 `WebRequest`/`WebClient` 兼容路径（两条管线均无改写规格而非 domain 绑定）、每 domain 的代理/证书回调/重试策略与请求日志分区、Android Managed 侧对应的安装根 overlay 映射（XPerfect/ShowBPM/Replay 的自更新写入在 v4 下仍会失败关闭，待同款切片）；`host` domain 与 MOD domain 的网络策略隔离目前依赖 MOD 无法触达进程全局策略（失败关闭），而不是独立的 host 网络域实现。

## 2026-08-28 RVA 数据句柄证明补充

当前 shadow 重写 ABI 已升级为 `starray-native-isolation-rewrite-v14-readonly-rva-span`。真正的可变静态 RVA、未证明的字段句柄用途和跨私有程序集静态字段访问仍然失败关闭；只有固定大小、属于编译器生成 `<PrivateImplementationDetails>` 类型链，且所有引用严格符合已证明的 `ldtoken -> RuntimeHelpers.InitializeArray(Array, RuntimeFieldHandle)` 或 `ldsflda -> ldc.i4 -> ReadOnlySpan<T>(void*, int32)` 形态的数据 blob 才归类为 `SharedImmutable`。对于 `InitializeArray` 句柄形态，编译器可省略 RVA 字段的 `initonly`，但该字段不得以直接读取、取地址或写入形式出现；直接读取或构造只读 span 仍要求 `initonly`。证明同时检查字段声明类型与 RVA 值类型定义的 owner/namespace 链，覆盖 dnlib 将直接类型名展平、将生成标识保留在 namespace 中的实际程序集形态。Release 真实 `ADOFAIOnlineMod.dll` 回归结果为 `issues=0`、输出存在；非安全句柄、可写 Span 和越界长度仍拒绝。

## 下一代隔离合同（2026-08-19 已确认，尚未实现）

本节是 Android Managed MOD、PcCompat MOD及两类之间的统一目标架构。它保留现有 MOD间联动，包含事件 Broker 和更常见的“一个 MOD直接调用另一个 MOD公开 API/内置方法”。除已有实现状态明确列出的能力外，本节均不得写成当前生产已完成。

### 证据矩阵（2026-08-22 全量核查完毕）

对六个真实 MOD DLL（XPerfect、ShowBPM、Replay、System.Formats.Nrbf、Jipper 主 DLL、JAMod.Bootstrap）完成逐指令审计后，本章各节的证据状态如下。**挂起 = 当前语料零调用点拉力，维持设计；出现新样本或实机数据时按对应行重新立项，不要凭章节存在感动工。**

| 章节 | 证据 | 状态 |
| --- | --- | --- |
| ModDataDomain 与数据归属 | — | 已实现（token/slot/cell） |
| IsolationManifest 与 feature 边界 | — | 部分实现 |
| 联动模型 Broker 与 Direct Link | `AppDomain.GetAssemblies`/`Assembly.Load*`/UMM modEntries/JALib GetMods·FindMod 全部 **0** 处 | Broker 已实现；Direct Link **调用门骨架已实现**（`ModDirectLinkGate`：双 lease + Provider domain 切换 + 重入 + 异常保真 + 退休拒绝），身份哈希/自动绑定/反向门/CrossDomainObjectLease/SCC staging/热更新重绑定待真实 Provider API 形态后微调 |
| 虚拟 MOD 目录 / FindMod facade | 同上 **0** 处 | 挂起（0 拉力） |
| proxy/shim 静态状态 | — | 已实现（static cell） |
| 反射 metadata facade（隔离相关半） | Jipper 的 10 处 `Type.GetField` 全部指向游戏类型（r136 版本回退），**0** 处指向自有静态字段；其余为方法反射（不受 field cell 影响） | 挂起（0 拉力） |
| 数据源 journal 与慢消费者 | Host 侧策略，无 MOD 调用点证据形态 | 挂起 |
| 真实 Unity/IL2CPP 状态仲裁扩展 | JRP/JPOV/CheryTools 三份源码审计：`RectTransform.anchoredPosition` 在**游戏自有**对象上有三方争用（`scrShowIfDebug` 的 rect 被 JRP `Status.cs:139` 与 JPOV `ScrShowIfDebugAwakePatch` 写同一个值、被 CheryTools `RegisterAutoplayStatusText` 登记；`scrLogoText`、`ADOBase.controller.txtLevelName` 各一处）；JPOV 与 CheryTools 还各自把同一 rect 的原值存进自己的静态字段并各自恢复 | 已实现（v37 共享属性 contribution registry：`Behaviour.enabled` 与 `RectTransform.anchoredPosition`）；MOD 自建对象的 `RectTransform.set_*` 仍按私有布局状态直通 |
| Android Managed 程序集边界 | — | 已实现（ALC/shadow） |
| P/Invoke 与指针 provenance | 全语料唯一声明为 `user32!GetAsyncKeyState`（已桥接）；Android 语料 **0** 声明 | 挂起（0 新拉力） |
| ImGui 合成与输入隔离 UiOwnerScope | MOD ImGui 由 Host 托管且无 scope 破坏故障记录 | 挂起 |
| 异步执行与静态事件（事件半） | PcCompat 4 处已登记退休；Android 语料 **0** 订阅点 | PcCompat 已落地；Android 无目标 |
| 反射动态代码与函数指针（方法半） | Bootstrap/Jipper 的 Method 反射作用于自有类型，方法不受 field cell 改写影响 | 挂起（无正确性缺口证据） |
| 文件、设置与网络隔离 | — | 已实现（v4–v6 / v31–v35） |
| 跨 MOD Harmony | 依赖 Direct Link 建立，随其挂起 | 挂起 |
| 每 MOD资源预算 | Host 策略，无调用点证据形态 | 挂起 |

### ModDataDomain 与数据归属

每个 `ModRuntimeKey` 独占一个 `ModDataDomain`。以下可变状态必须属于 domain：

- proxy wrapper、单例视图和静态 slot；
- 数据源 cursor、过滤器、聚合窗口、缓存和派生状态；
- 设置、统计、日志、网络会话和文件路径视图；
- 资源句柄、事件订阅、callback/operation lease 和故障计数。

生产路径禁止用“当前、最后注册或默认 MOD”隐式选择可变数据。重写调用点、生成代理、后台 operation 和跨 MOD调用显式携带 domain token。`ThreadStatic Current` 只用于受控入口建立 scope，不能作为归属的唯一依据；scope/token 缺失时失败关闭并记录 Warning，不回退默认 owner。

官方游戏事实允许共享，但只能以不可变、带 generation/sequence 的 Host snapshot 或 journal 发布。每个 MOD维护自己的 cursor、稳定数组、wrapper cache 和派生状态；一个 MOD读取、清空、跳转、卡顿或故障不能推进或污染另一个 MOD。

domain的热路径身份使用可验证的小型 token，而不是字符串、ALC查询或反射推断：

```text
DomainToken {
    SlotIndex
    Generation
    ProcessCookie
    LoaderKind
}
```

- `ModRuntimeKey`用于诊断、持久身份和依赖图；Hook、数据源、事件、Direct Link、任务、文件、网络和资源 lease保存 `DomainToken`。
- token解析为固定槽位和 generation，拒绝过期、跨 MOD、跨 loader或错误进程 cookie。token本身不延长 domain生命周期，必须取得 callback/operation/object lease。
- 退休顺序是关闭 token、拒绝新 lease、等待在途 lease归零、回收槽位和 ALC；槽位复用依靠 generation和 process cookie阻止 ABA。
- 热路径只做原子状态读取、generation校验和槽位访问；不扫描 ALC、不查字符串、不反射、不访问文件。

### IsolationManifest 与 feature 边界

每个 MOD导入和每次 Provider/游戏 ABI变化都生成一份 `IsolationManifest`。清单绑定：

```text
ModRuntimeKey
OriginalAssemblyIdentity
ShadowAssemblyIdentity
API Surface / Member Closure
Domain Static Classification
Direct Link Closure
DataSource BindingPlan
P/Invoke Provenance
Feature Capabilities
ManifestHash
```

每项能力使用四级状态：

- `Proven`：静态扫描、重写和合同验证均证明等价；
- `Guarded`：由运行时 token、lease、contribution或调用门保护；
- `LegacyReadOnly`：只能读取共享不可变事实；
- `Unsupported`：不注册 Hook、事件、任务、资源或 Direct Link。

`IsolationManifest` 的 hash进入 shadow rewrite、Direct Link、JIT/R2R、Native Rule和缓存键。用户只能关闭已验证 feature，不能把 `Unsupported`强制提升为可执行。功能可以独立 staging和 quarantine；入口、静态构造器、必需依赖或不可拆分 Harmony事务失败时，整个 MOD事务回滚。

### 联动模型：Broker 与 Direct Mod API Link

MOD联动保留两条一等路径：

1. `ModInteropBroker`：事件、广播、高频批次和异步 RPC；跨 ALC只传 Host 合同值、只读字节载荷和稳定句柄。
2. `Direct Mod API Link`：兼容现有 MOD直接引用另一 MOD程序集、静态 API、单例、实例方法或内置方法的行为，不要求第三方 MOD改成发布订阅。

Direct Link 合同如下：

- 加载器扫描 `AssemblyRef/MemberRef`，并支持 `UnityModManager.FindMod`、JALib MOD表、按 ID查询和程序集限定反射在运行时建立链接。
- Provider逻辑身份使用 `ModId + 程序集强名称身份 + API Surface Hash`；API Surface Hash覆盖公开类型、继承关系、泛型约束、方法签名、字段/属性/事件、调用约定和可见性。MVID、内容哈希和版本用于 generation、审计与缓存失效。只有唯一身份和 ABI候选可自动绑定；不按加载顺序或简单程序集名猜测。
- 未在 Manifest 声明的唯一依赖允许推断，记录 `InferredDirectDependency`。推断链接进入统一加载拓扑、generation lease 和卸载约束。
- Consumer 调用 Provider 时同时取得双方调用 lease并进入 Provider domain。API内部创建的 Hook、资源、任务、数据源和文件/网络操作归 Provider；返回后恢复 Consumer scope。
- Consumer传入的 delegate、interface callback 和回调对象由反向调用门包装。Provider 回调时进入 Consumer domain。
- primitive、enum、字符串和不可变值按值传递。Provider自定义对象保持真实类型和引用身份，登记 `CrossDomainObjectLease`，语义为 Provider 所有、Consumer 借用。
- Provider返回的 Unity/IL2CPP/native handle 默认只借用；Consumer不得绕过 owner lease销毁、释放或转移所有权。明确的 Provider API可以在 Provider domain执行修改。
- Provider热更新后旧链接拒绝新调用。仅使用 primitive、字符串和 Host稳定合同类型的链接，在 Consumer实际成员闭包 ABI兼容时可以 staging新调用门并原子重绑定。Consumer使用 Provider自定义类型、静态字段、delegate、泛型、异常类型或持有 Provider对象时，必须按反向依赖闭包重载；旧对象不能自动投向新 generation。
- 依赖图中的循环按强连通分量整体 staging：先创建全部 ALC/domain/身份并完成链接，再按稳定顺序运行生命周期并原子发布。任一成员失败则整组回滚，卸载默认也以整个 SCC为单位。
- 静态构造器在链接未 ready时跨 MOD调用抛明确的 `ModDependencyNotReadyException`，不返回副本状态或空占位。

Direct Link 的同步调用保持普通 .NET并发和重入语义：

- `A -> B -> A` 可以在同一线程重入。每次边界切换压入 domain frame并持有双方 generation lease，返回或异常时逆序释放；
- Provider方法在调用者线程执行，不转发 UnityMain或 per-MOD actor，也不增加 domain全局锁；
- 生命周期图、链接表和缓存锁不得在进入 MOD代码时持有；一次在途调用固定使用入口取得的 immutable link generation；
- Provider抛出的普通异常保持具体类型、对象身份、inner exception和传播语义。同步 Consumer callback异常按原 delegate/API语义返回 Provider；
- 异步返回后的 continuation使用独立 operation/domain lease，不长期保留同步调用栈。

### Provider 热更新与依赖闭包生命周期

- Provider实现变化但 API Surface Hash兼容时，先验证所有 Consumer实际使用的成员闭包。未使用成员变化不阻止兼容判断。
- 只有全部跨边界类型都来自稳定 Host合同的链接可以免 Consumer重载；使用 Provider自定义 CLR类型时，不同 collectible ALC generation的类型身份不等价，必须重载反向依赖闭包。
- 重载先 staging新 ALC/domain、链接和生命周期，再原子发布；旧图等待在途 lease归零后退休。循环成员按完整 SCC处理。
- 设置和显式可迁移状态通过版本化 migration合同转移；对象引用、delegate、线程、Hook state和私有缓存不跨 generation复制。无法迁移时干净重载并记录 Warning。
- 每个 Consumer generation在 Direct Link存活期持有 Provider dependency lease。Provider不能在 Consumer继续运行时单独卸载 ALC。
- 用户禁用 Provider时按反向拓扑先停用 Consumer，再卸载 Provider；重新启用时按正向拓扑恢复。Provider仅暂停内部功能但保留程序集/API实例时，可保留 Consumer并以 `SuspendedProvider`明确拒绝新操作。
- 不使用强制 GC、WeakReference或超时猜测 Consumer是否已释放 Provider对象。Consumer主动解绑时，只有逃逸分析证明没有 Provider类型、对象、delegate、泛型或静态引用才可免重载，否则重载 Consumer。

### 虚拟 MOD目录

`UnityModManager.modEntries`、`FindMod`、JALib MOD表和 Android Host插件查询使用当前 domain专属目录视图：

- 默认包含自身、已声明依赖、已推断 Direct Link和运行时成功建立的动态链接；
- `FindMod(id)` 对唯一活跃 Provider原子建立链接，并返回绑定真实 generation 的稳定 `ModEntry` facade；
- 同一 Consumer重复查询保持 facade引用身份；Provider不存在、版本不符或候选不唯一时遵循原 API空值语义，并只记录一次 Warning；
- `GetMods()` 不暴露所有私有 MOD实例。公共发现仅返回只读描述；`AppDomain.GetAssemblies()` 和无程序集限定扫描不能绕过可见目录；
- MOD文件夹携带同名 API DLL时，只有唯一精确 Provider身份才能替换为 Direct Link。无法区分 Provider API与私有库时拒绝加载。
- 不需要第三方 MOD声明依赖。`AssemblyRef/MemberRef`、`FindMod`、程序集限定反射实际触达的 public 成员组成 API closure；closure内的 public 方法、属性、事件、字段和构造器自动建立调用门，未被引用的 public 成员不默认暴露。
- private/internal 成员只有在现有 MOD确实通过精确 MemberRef或反射访问时才兼容绑定，并记录高等级 Warning；Provider生命周期、卸载、授权、裸指针和资源释放 API默认排除，除非显式 export contract允许。
- Provider API签名暴露的第三方类型、数组元素、泛型实参、异常、基类和接口递归进入类型闭包。类型身份、版本、布局和 API Surface冲突时不做 JSON复制、反射映射或 duck typing，Direct Link失败关闭。

### proxy/shim 静态状态

共享 proxy 类型定义用于维持跨 MOD参数、返回值和反射类型身份，但其可变静态状态按 domain索引：

```text
ModDataDomain -> StaticSlotId -> value
```

- 单例 wrapper、数组、集合、查询缓存、事件 cursor和游戏 API override 属于 `DomainMutable`；即使两个 wrapper指向同一 IL2CPP对象，也不共享可变缓存和派生状态。
- 类型 metadata、方法地址、字段 offset、常量和不可变官方事实属于 `SharedImmutable`。
- 修改真实共享 Unity状态的成员属于 `HostContribution`；另一 MOD公开 API的状态属于 `DirectLinkProviderState`。
- 构建器扫描全部 static字段和属性，生成分类表。新增但未分类的可变成员默认 `DomainMutable`并记录 Warning。
- `ldsfld/stsfld/ldsflda` 在重写期绑定 domain slot；`ldsflda` 只能取得该 domain稳定单元。无法安全重写的静态地址或指针拒绝对应 MOD。
- 分类表进入 rewrite/JIT/cache ABI哈希；分类变化使旧缓存失效。
- `UnityModManager`、Harmony、JALib等随 MOD ALC私有加载的 shim registry继续自然隔离；Default ALC中的共享运行时和 proxy也必须遵守 domain slot规则。

### 数据源 journal 与慢消费者

- 输入、判定、场景、节拍等高频物理事实由 Host各写一份只读环形 journal；每个 MOD持有独立 `DataSourceLease`、cursor、过滤状态和聚合窗口。
- MOD落后超过容量时只向该 MOD报告 `DataGap`并重建它自己的 snapshot，不阻塞游戏线程、不扩大全局锁、不推进其它 cursor。
- 无损数据源为每个消费者建立独立有界队列；溢出只熔断该消费者并标记 Warning。`LatestState` 直接切换到最新不可变快照。
- 新 generation从明确开户点开始，旧 cursor和积压全部废弃。Direct Link调用 Provider API时使用 Provider cursor；只有显式传入 Consumer数据句柄时才能访问 Consumer数据。
- 诊断按 MOD显示 lag、gap、队列高水位、熔断和重建次数。

每条 PcCompat recipe/BindingPlan都必须显式绑定数据源：

```text
Consumer ModRuntimeKey
FeatureId
SourceKind
ProviderIdentity
SourceGeneration
SchemaHash
```

`SourceKind`区分官方不可变事实、当前 MOD派生状态、Direct Link Provider、Broker合同和 Host adapter。生产路径不使用默认 owner/最后注册者聚合视图；旧无 owner API只能从有效 `DomainToken`解析唯一 owner，缺失时失败关闭并记录 Warning。

数据源失效时进入 `SourceUnavailable`，禁止静默切换到其它 MOD、默认 owner或全局 snapshot。只有清单声明的 `ProvenEquivalent` fallback可自动切换；`CompatibleDegraded`必须显示 Warning和来源变化。切换生成新的 feature generation，先清理旧 cursor、held状态和缓存；统计状态只有存在 migration合同才迁移。

### 真实 Unity/IL2CPP 状态仲裁

多个 MOD修改同一个持久共享属性时，Host保存各自意图而不是让最后写入者永久覆盖：

```text
(ModRuntimeKey, ObjectId, PropertyId) -> Contribution
```

- 当前 MOD读取时优先看到自己的 contribution；Host按属性策略把结果投影到真实对象。
- 可组合属性使用显式合成器；不可组合属性使用稳定优先级；销毁、释放和所有权转移使用独占 lease；一次性方法调用保持真实调用语义。
- 卸载或 generation替换只移除对应 contribution，再恢复下一层或官方 baseline。
- Direct Link中 Consumer调用 Provider API产生的修改归 Provider domain。
- 未登记策略的持久共享属性默认独占；第二个 owner写入时拒绝并记录 Warning，不能静默覆盖。
- 绕过 rewrite、直接写裸 IL2CPP指针的行为只能诊断为隔离降级，不能宣称已安全隔离。

### Android Managed MOD 程序集边界

Android MOD只携带 CoreCLR托管 DLL；本设计不为它假设私有 `.so`。现有 `Native` LoaderKind/owner ABI作为历史兼容字符串保留，文档和 UI统一称为 Android Managed MOD。

- `StArray.ModManager`及必要 Host合同程序集在 Default ALC共享，保证 Broker、Direct Link和 lease类型身份一致。
- MOD主 DLL及普通依赖默认在各自 collectible `NativeModAssemblyLoadContext` 私有加载；同名依赖不能因其它 MOD先加载而串绑。
- MOD间引用只能通过 Direct Link建立，不通过扩大 Default ALC共享白名单实现。
- 共享 Host合同中的可变状态仍按 domain分区；程序集共享不等于状态共享。
- 加载期生成实际解析图。发现意外绑定其它 MOD ALC或未授权 Default ALC程序集时拒绝加载并记录 Warning。

所有 Android Managed MOD都从只读原件生成的 shadow package加载：

- 原始 DLL不原地修改，只用于身份、API Surface、MVID/哈希和重新生成；执行使用 content-addressed、通过 PE/metadata/IL/引用图/IsolationManifest验证的 shadow程序集。
- 主程序集及私有依赖闭包统一重写 domain静态字段、Direct Link、Task/Timer/Thread、文件、网络、反射、事件、Unity/IL2CPP bridge和 Harmony调用点。
- 原始身份和 shadow身份同时保存；Direct Link兼容判断使用原始 API身份，执行缓存使用 shadow身份。
- `Assembly.Load(byte[])`、`LoadFromAssemblyPath`、`LoadFile`、自定义 ALC、resolver、动态下载 DLL均同步进入同一重写管线；未命中缓存时调用返回前完成验证，不能先执行原始 PE。
- 同一内容 hash使用单航班编译。动态加载发生在游戏热路径时只记录一次 Warning和耗时，不以避免停顿为理由放行未隔离代码。
- 动态程序集默认继承发起者 domain，不创建新 owner；Provider API解析仍回到 Direct Link，禁止复制 Provider DLL。

共享程序集只包含 Host合同和必要运行时类型。proxy类型定义可共享以保持类型身份，但所有可变静态状态进入 domain slot；发现意外绑定其它 MOD ALC或未授权 Default ALC程序集时拒绝。

### P/Invoke 与指针 provenance

重写器扫描 `DllImport`、`LibraryImport`、`NativeLibrary.Load/GetExport`和函数指针生成点，并分类为：

- `SharedStateless`：纯函数或只读系统查询；
- `DomainAwareHost`：Host ABI，自动注入和验证 `DomainToken`；
- `ProcessGlobal`：locale、环境变量、cwd、信号、TLS、全局回调等，由 Host仲裁或拒绝；
- `UnsafeRaw`：dlopen/dlsym、任意内存读写、mprotect、裸 Hook入口和未知函数指针，默认 `Unsupported`。

`IntPtr/nint`和 unsafe IL必须保留来源：

- 当前 domain私有分配允许在登记范围内做指针运算，只有 owner可以释放；
- IL2CPP对象只能进入已验证 typed bridge/proxy；Hook地址只能用于 HookBroker声明的 target/replacement/continuation角色；
- Direct Link传递指针必须声明 borrowed/owned、长度、可读写性、线程和释放方；
- 指针存入长期静态状态、跨异步边界、跨 domain对象或传入未知方法时无法证明即拒绝；
- 未知常量地址、裸 `calli`、来源不明 native handle和任意 `Marshal.Read/Write`失败关闭并记录 Warning。

这些规则是正常 MOD的所有权和故障隔离合同，不是对主动恶意托管代码的进程沙箱。

### ImGui 合成与输入隔离

Android Managed MOD与 PcCompat MOD共用一个 Host ImGui context和一次最终渲染，但使用严格 `UiOwnerScope(ModRuntimeKey)`：

- 自动注入稳定 ID namespace，隔离同名窗口、控件、popup、drag/drop和持久窗口状态；
- scope保存并校验 window、style、font、color、ID、group、popup和 clip栈。MOD返回或抛异常时恢复未平衡栈，只熔断该 MOD UI；
- texture、font、popup、输入 capture和 UI故障状态登记 owner/generation，卸载只释放本 owner资源；
- Host按 z-order和每 owner交互区域路由输入。顶层 MOD UI消费后不得透传游戏；没有交互区域时才允许 gameplay接收；
- Direct Link调用 Provider绘制 API时资源归 Provider；显式可嵌入 UI描述通过 lease交给 Consumer；
- 稳态只增加 owner scope和栈深度快照，不为每个 MOD建立独立 ImGui context。

### 异步执行与静态事件

- `Task.Run`、线程池、`Timer`、`Thread`、异步 continuation、事件 delegate和常见调度器在创建时捕获 `ModRuntimeKey + DomainToken`，执行时恢复 domain并持有 generation operation lease。
- `ExecutionContext.SuppressFlow()` 不能绕过归属，Host wrapper显式携带 token。第三方库最终调用 MOD delegate时，由 delegate调用门恢复 domain。
- Direct Link中 Provider创建的后台任务归 Provider；Provider保存的 Consumer callback在执行时临时切回 Consumer。
- MOD退休后拒绝新调度、请求取消并等待在途任务。无法归属的私有线程不得访问 Host、proxy或共享数据，并记录 Warning；超时只保留该 MOD退休 generation，不释放其 ALC。
- Host、Unity proxy和游戏静态事件统一登记 `PublisherIdentity + EventId + Subscriber ModRuntimeKey + DelegateIdentity + Generation`。触发时取得 subscriber callback lease并进入订阅者 domain。
- `event -=` 按原 delegate相等语义只移除当前 domain订阅。卸载、失败加载和 generation更替自动退休对应订阅；高频事件使用 immutable subscriber snapshot。
- Direct Link订阅 Provider事件时，事件存储归 Provider、回调归 Consumer，并要求双方 lease有效。Host事件总线隔离单订阅者异常；普通 Direct Link delegate保持原异常传播语义。
- 无法重写且直接依赖共享静态 event backing field的事件失败关闭并标记隔离不完整。

### 反射、动态代码与函数指针

- `Type.GetType`、`Assembly.GetType`、`MethodInfo.Invoke`、`FieldInfo.Get/SetValue`、`Delegate.CreateDelegate`和表达式编译经 domain-aware metadata facade执行，只暴露当前 MOD、Host合同和已建立 Direct Link的成员。
- domain静态成员的反射读写转到对应 `StaticSlotId`；Provider成员反射调用进入 Direct Link调用门。
- 动态 delegate固化 owner、Provider generation、目标成员哈希和 ABI；热更新后旧 delegate拒绝新调用。
- `DynamicMethod`和运行时生成 IL在 JIT前执行同样的成员重写、domain验证和逃逸分析。
- `ldtoken`、`RuntimeMethodHandle`、裸函数指针和 `Marshal.GetFunctionPointerForDelegate`不能绕过边界；只有 Host生成的受控 thunk可以跨 domain。
- 反射缓存按 `ModDataDomain + metadata generation`隔离。无法证明安全的私有字段地址、跨 domain动态 IL或裸函数指针失败关闭并记录 Warning。

### 文件、设置与网络隔离

- 每个 domain独立拥有安装只读根、配置根、缓存根、日志根和临时根。相对路径、`Environment.CurrentDirectory`、常见 AppData和 PC MOD默认目录经重写解析到当前 domain，禁止修改进程全局 cwd。
- 游戏官方资源提供只读共享视图。Provider返回自己目录的绝对路径时附加隐式只读 `PathLease`；Consumer可按原字符串 API读取，但无权写入或删除。写权限必须由 Provider API执行或显式授予。
- 热更新保留兼容配置；缓存按 MOD版本和 schema管理，不兼容缓存自动清理。跨 MOD非法访问只拒绝该操作并记录 Warning。
- 每个 domain独立维护 Cookie、Header、认证、代理、证书策略、超时、重试、请求日志和连接池身份。`HttpClient`、`WebRequest`、`ServicePointManager`等兼容 API经重写绑定 domain。
- DNS和系统网络状态可以共享为不可变事实；认证、Cookie、证书回调和请求状态不能共享。Host授权、更新和运行时下载使用独立 `host` domain，MOD不能修改其网络策略。
- 后台异步延续携带明确的文件/网络 operation lease。卸载取消本 MOD请求并等待静默；超时只隔离该 MOD并标记 Warning。

### 跨 MOD Harmony

- Consumer只有已经与 Provider建立精确 Direct Link后，才能把 Provider方法解析为 Harmony target；禁止通过全局类型扫描补丁未链接 MOD。
- 补丁 owner仍是 Consumer。Consumer Prefix/Postfix/Finalizer在 Consumer domain执行，Provider original在 Provider domain执行，参数和返回对象使用双方 CrossDomain lease。
- 补丁 generation同时依赖 Consumer、Provider和 Direct Link generation；任一方退休都原子发布不含该补丁的新链。Provider更新后重新解析、重编译，禁止复用旧 token或地址。
- 公开或链接显式暴露的成员正常允许；私有成员要求 Provider程序集和成员哈希精确匹配，并记录高等级 Warning。
- 多个 Consumer补丁同一 Provider方法时执行完整 Harmony跨 owner排序。本次补丁事务失败只回滚当前 Consumer事务，不修改 Provider和其它 Consumer状态。
- 普通 Direct Link同步 API异常不自动熔断，保持原始 .NET异常传播；慢调用只产生耗时 Warning。只有 Host管理的事件、Broker消费者、Timer/callback和无损队列按各自合同熔断。

### 每 MOD资源预算

每个 `ModDataDomain` 独立统计并限制：后台 operation/Timer/callback、Broker积压、Direct Link在途调用、proxy/reflection/JIT缓存、动态 thunk、Unity/ImGui/GPU资源、HTTP并发和缓冲、日志、临时文件及普通缓存。

- 软上限只产生 Warning和诊断，不改变 MOD语义；
- 硬上限拒绝该 MOD的新资源申请，不回收其它 MOD资源，也不运行中撤销已有 Harmony补丁；
- 可丢弃缓存只清理当前 domain LRU；有 lease、在途调用或 Unity所有权的资源不得强制释放；
- 无损队列溢出熔断该消费者，`LatestState`合并，诊断/遥测允许丢弃；
- domain退休后等待 lease归零再释放预算对象；Host授权、游戏和 ModManager使用不可被 MOD挤占的保留预算；
- 初始上限由压力测试和设备内存等级缩放，MOD无权自行提高全局上限。

### 目标验收

- Android/Android、PcCompat/PcCompat和 Android/PcCompat三组组合均验证静态状态、数据 cursor、设置、网络、UI、Hook和资源互不串线。
- 直接程序集引用、`FindMod`、静态 API、实例 API、callback、反射调用、Provider热更新和循环依赖均保持原有联动语义。
- Provider自定义类型参与 Direct Link时，热更新会原子重载依赖闭包；仅稳定 Host合同类型链路可在 API Surface兼容时无感重绑定。
- 同一共享 proxy静态 API由两个 MOD并行读写时，各自看到自己的 domain状态；显式 Direct Link仍看到 Provider真实状态。
- 一个 MOD卡顿、抛异常、队列溢出、UI栈损坏、网络失败或卸载，不改变其它 MOD的数据源和生命周期。
- `Task/Timer/Thread`、静态事件、反射、动态 delegate和跨 MOD Harmony均恢复正确 domain，旧 generation调用被拒绝。
- 所有跨 domain对象、callback、路径、数据源和 Provider链接都有 generation lease；旧 generation不能进入新 generation。
- 性能热路径不扫描程序集、不回溯调用栈、不反射选择 owner、不执行文件 IO；只读取显式 token和不可变 snapshot。

## 统一身份

目标身份模型为：

```text
ModRuntimeKey {
    LoaderKind: Native | PcCompat
    ModId: string
    Generation: uint64
}
```

当前 Hook 与 Behaviour owner 继续使用 `native:<id>` 或 `pccompat:<id>`；统一 Host `ModRuntimeKey` 已落地并传入 Native load state、Source Generator typed Hook、Behaviour/ImGui 和 PcCompat managed session。PcCompat bundle、资源与 Unity lease 仍保留各自的资源 generation，两种 generation 不能混用；后续需要在跨层审计快照中同时关联 Host load generation 与资源 generation。

对于仍需保留同一插件实例的 legacy process-lifetime Hook，暂停和恢复保持同一 generation。已经执行逻辑退役并清空 detour delegate 的 Hook 节点不得因 owner 恢复而重新启用。

## 后台操作租约

后台工作必须在 `OnLoad`、MOD GUI 回调、Behaviour 回调或其它有效 owner scope 内先取得租约，再把租约和取消令牌交给后台任务：

```csharp
var operation = ModRuntimeOperations.Begin("asset-refresh");
try
{
    _ = Task.Run(async () =>
    {
        try
        {
            await RefreshAssetsAsync(operation.CancellationToken);
        }
        finally
        {
            operation.Dispose();
        }
    });
}
catch
{
    operation.Dispose();
    throw;
}
```

租约 API 的合同如下：

- `Begin` 在 scope 缺失、generation 已退休或资源登记失败时抛出；`TryBegin` 返回 `false`；
- 退休以 session 锁为线性化点，先拒绝新 operation，再异步请求已有 operation 取消；取消回调不会在 Unity/卸载线程上同步执行；
- operation 由 Host 生成单调 ID，结束必须匹配同一 session、owner、generation 和 ID；double-dispose 与 stale-end 不改变计数；
- 五秒内未退出会触发卸载超时，诊断包含 operation ID、名称和取消状态；状态回滚后原 operation 仍保持已取消并继续计数，直到实际释放；
- 此 API 提供协作式停止和引用边界，不强制终止线程，也不允许任务在释放 lease 后继续调用 MOD、Native 或 Unity 对象。

### Host native 组件租约（非 Android Managed MOD 公共能力）

Android Managed MOD只能携带托管 DLL，不能使用以下接口启动私有 `.so` worker。本节只描述 ModManager自身或明确受控 Host native 组件在内部需要 native worker时的生命周期合同：

```csharp
var operation = ModNativeOperations.Begin("native-decoder");
try
{
    StartNativeWorker(operation.Token);
    await JoinNativeWorkerAsync();
}
finally
{
    operation.Dispose();
}
```

native 线程包含 `common/modmanager_native_operation_client.h` 并低频或在每个工作批次边界轮询：

```c
while (modmanager_native_operation_is_cancellation_requested_v1(&token) == 0) {
    process_one_batch();
}
```

合同如下：

- `ModNativeOperations` 只在当前 `ModRuntimeKey` 仍为 `Loading/Active` 且 native generation 已开放时成功；token 是 blittable 24 字节值，可直接复制到 C ABI；
- 纯 native Host adapter 只有在已从 Host 获得 owner/generation 时才可直接调用 `modmanager_native_operation_begin_v1`，不得硬编码或猜测其它 MOD 身份；该机制是协作式生命周期隔离，不是恶意代码安全边界；
- poll 返回 `0` 表示继续，`1` 表示 Host 已请求取消，`-1` 表示 token stale/invalid；worker 必须把所有非零结果都视为停止请求；
- worker 停止访问 MOD、IL2CPP、Unity 和受控 native 状态后才可 end/dispose。由 Managed 负责 join 时只 dispose lease；完全 native 的 Host worker 可在退出前调用 `modmanager_native_operation_end_v1`；
- Host cancel 把 generation 原子切到 `Retiring` 后扫描 active slot 并发布取消。等待使用条件变量，worker poll 不进入 mutex；
- 5 秒超时使卸载事务回滚。generation 可恢复接受新 operation，但已经发出的旧 token 仍保持 cancelled，避免把半退出 worker误当作新工作；
- 逻辑挂起保留同一 generation 的 registry 状态，恢复时重新开放；真正 `Retired` 后不可恢复。ALC/native handle 释放必须晚于 native active-count 归零和 generation retirement。

## HookBroker

### 稳定入口

每个 target 只安装一次物理 Dobby Hook：

```text
IL2CPP target
    -> stable broker gateway
    -> owner gate N
    -> owner gate N-1
    -> ...
    -> Dobby original trampoline
```

新增 layer 只发布新的原子 head，不再修改上一 MOD 的 replacement 代码。owner gate 只执行原子读取和尾跳转，不解析参数，因此兼容任意 ABI。

当前 native 结构保存：

```text
HookChain: target, stable gateway, root original, atomic head
HookLayer: owner, replacement, continuation gateway, atomic next, enabled, retired
registration order: HookChain.layers 中的稳定顺序
```

`enabled=false` 表示暂时挂起，允许同 generation 恢复。`retired=true` 表示永久逻辑退役，后续 owner 恢复不得重新启用。

### 旧 API 兼容

旧 API 保持不变：

```csharp
nint continuation = HookHelper.Hook(target, detour);
bool retired = HookHelper.Unhook(target);
```

Android 上的 `Unhook` 改为退役当前 owner 在该 target 上的逻辑 layer，并返回逻辑退役结果，不再尝试破坏物理链。MOD 清空自己的 delegate 和 original 字段后，broker 仍保留一个永不进入旧 detour 的退休 gate。

没有 owner scope 的 legacy `Unhook` 不允许猜测所有者，也不得物理销毁共享 target。

### 生命周期

卸载或挂起顺序固定为：

1. 阻止 owner 注册新 Hook。
2. 将 owner 的未退役 gate 设为 disabled。
3. 调用 MOD 的 `OnUnload`；其中的 legacy `Unhook` 可把指定 target 标为 retired。
4. 再次关闭 owner gate，覆盖 `OnUnload` 内遗漏的 Hook。
5. 查询是否仍有未退役 process-lifetime layer。
6. 有未退役 layer时保留插件实例和 ALC，以便同 generation 恢复；全部退役时允许释放 ALC。

进程级 Hook/Instrument 安装失败或抛异常时，只回滚本次新建的 owner reservation；已有成功 layer 或成功 Instrument 不会被误清理。

Source Generator typed Hook 已使用已知 ABI wrapper 统计 in-flight 并支持 Host drain。任意手写 ABI legacy detour 不承诺精确计数，也不在通用热 stub 中增加可能破坏参数 ABI 的原子计数；此类 layer 只要未退休或无法证明 quiescence，就只执行逻辑挂起并保留插件实例和 ALC。

### 排序与故障

- 默认按注册顺序组成 continuation 链，后注册 layer 位于前面，与当前行为一致。
- PcCompat 同一 target 的多条 verified rule 仍由一个 native dispatcher 合并，不为每条 rule 安装物理 layer。
- owner disable 或 retire 不改变其它 owner 的 continuation。
- managed callback 异常必须由各自 dispatcher 捕获并记入 owner fault counter；不得清空 target 全局链。
- SIGSEGV 等 native 进程级故障不属于可恢复隔离范围。

## Telemetry

### 两层数据模型

共享层只发布不可变游戏事实：

```text
GameFactSnapshot {
    Scene
    OfficialHitMargin
    Floor
    PlayerCount
    SongPosition
    MapPosition
    PlanetSpeed
    InputSnapshot
}
```

每个 MOD 维护自己的派生状态：

```text
OwnerTelemetrySession {
    ModRuntimeKey
    Visible
    Combo
    Kps
    TileBpm
    CurrentBpm
    Accuracy
    Progress
    Attempts
    Checkpoints
    LastPublishedSequence
}
```

共享层不能暴露可由某个 MOD原地修改的数组或对象。需要保持 PC API 数组身份时，也必须在对应 `OwnerTelemetrySession` 内维护稳定数组。

### dispatcher 发布

dispatcher 已把共享操作和 owner 操作拆开。规则重建时在锁下生成 `OwnerOverlayDispatchSnapshot`，每个 target 携带 owner session、after-op mask 和 bundle ID；Hook 热路径只原子读取一次不可变 snapshot，不持有注册锁，也不分配托管对象。

执行顺序固定为：共享事实采集一次、逐 owner reducer 提交、最后投递 managed callback。owner 卸载时先从 registry 移除 session，再标记 retired；已发布 immutable snapshot 即使短暂持有旧 `shared_ptr`，也会在进入 reducer 前检查 retired。

## HUD

`PcCompatUnityHudRuntime` 当前按规范化 owner ID 注册 source，并一次返回全部 frame。renderer 使用：

```text
Dictionary<string ownerId, HudSurface>
```

每个 `HudSurface` 独立持有：

- Unity root、Canvas 和 RectTransform。
- TextMeshPro、Image、Font、Sprite、Material 绑定。
- visible、layout、style 和 content generation。
- fault/quarantine 状态。

source 返回异常、非法 frame 或 Unity 对象失效时，只隐藏并 quarantine 对应 surface。其它 MOD 的 surface 继续更新。移除 source 时只销毁其 root；同 owner 完成注销并重新注册后，可以单独重建失败 surface。

默认布局保留 MOD 自己提交的位置。明确请求相同自动布局槽位时，renderer 按稳定 owner 顺序堆叠；不得使用“最后注册者覆盖前者”的隐式策略。

## Unity 对象与资源

宿主创建或代理创建的对象登记为：

```text
UnityObjectLease {
    ModRuntimeKey
    NativeInstanceId
    Kind
    Lifetime
}
```

受支持 API 只允许 owner 销毁自己的对象。直接通过 IL2CPP 原始指针操作的 legacy MOD 无法形成安全边界，因此只提供诊断和保守生命周期保留。

共享对象属性使用 contribution/lease 模型：

```text
(Object, Component, Property)
    -> baseline
    -> ordered owner contributions
```

Sprite、Texture、Font 等不可合成属性默认采用独占 lease和显式优先级。Color 等属性除非注册专用合成器，也使用同一规则。卸载 owner 时移除它的 contribution，并重新应用下一层或 baseline。

ResourceChanger 当前实现采用上述独占 lease：

- settings contribution 以 `(modId, sessionGeneration)` 为键；同 MOD 更高 generation 到达时清除旧 generation，较旧的异步发布直接拒绝。
- contribution 第一次注册时取得稳定 sequence，同 generation 的设置更新不改变优先级；全部功能关闭等价于移除该 contribution。
- active contribution 变化时计算 Rabbit、Planet/Logo、Tile transition mask，并把恢复任务投递到 Unity-safe pending apply。
- pending apply 先恢复官方 Rabbit Sprite、PlanetRenderer、Floor Color 和 Logo，再按当前 active contribution 对仍存活对象重新应用。这样卸载顶层 owner 后不会停留在其颜色或白图，也不会丢失下一层 owner。
- Rabbit Sprite GCHandle 由 owner/session map 持有；active settings generation 为 `0` 的旧 mobile fallback 只允许回退到同 modId 最新 sprite，非零 generation 必须精确匹配。
- Logo clone 复用同一宿主对象并更新文本/颜色；无 active contribution 时隐藏，避免 owner 切换后重复创建或永久残留。

generated component bridge 当前对 native `AddComponent` 采用对象 lease：

- native `AddComponent` 成功返回的 component 以当前 `(modId, sessionGeneration)` 登记；旧 session 不能操作新 session 的 component。
- `SetEnabled` 只允许当前 lease owner 修改对象。立即和延迟 `Destroy` 在调用 Unity API 前检查 owner；目标 GameObject 上存在其它 owner 的 component lease 时拒绝销毁整个 GameObject。
- session teardown 会清理该 session 的 native component。`Object.Destroy` 抛错时 lease 不退休，保留给下一次 teardown 重试；活性探针确认对象已失效时只移除登记。
- 非 UnityMain fallback 会原子摘除 managed component、native component lease 和 persistent object 登记，避免旧 generation 被后续 session 重新发现。
- 此覆盖只适用于 generated component bridge 代理创建和操作的对象。MOD 绕过桥直接调用 Unity/IL2CPP API 创建或销毁对象时，不具备可强制执行的 ownership 边界。

共享原生对象属性使用属性 contribution，而不是让不同 MOD 直接覆盖同一个 Unity 字段。当前覆盖 `Behaviour.enabled` 与
`RectTransform.anchoredPosition` 两个属性描述符；两者共用同一套仲裁核心，按 `(目标对象引用, 属性名)` 索引：

- 通过 `GetComponent` 获得但没有 native component lease 的原生对象，被视为游戏自有的共享对象；第一次由 bridge 写入时保存该对象当前的 Unity baseline。MOD 自己创建的对象持有 lease，属私有状态，直通真实 Unity API，不进入仲裁。
- baseline **只采样一次**，之后永不重采。否则后到的 MOD 会把先到者的投影值当成"游戏原值"记下来，两者都恢复后对象永久偏移。这正是 JPOV（`Overlay.BetaWatermarkOriginalPos`）与 CheryTools（`ElementState.AnchoredPosition`）在同一个 rect 上会产生的碰撞。
- 每个 `(modId, sessionGeneration)` 在一个 `(对象, 属性)` 上最多保留一个 contribution。重复设置只更新该 owner 的值，不改变该 owner 的注册序列——所以每帧重写的 MOD 不会把只写一次的 MOD 挤下去。
- 实际 Unity 值由注册序列最高的 active contribution 投影。读取时，注册了 contribution 的 owner 看到自己的值，未注册的 owner 看到 **baseline**（不是当前投影值）：读投影值会让一个 MOD 把邻居的偏移当成游戏原值记下来并在卸载时恢复它。代价是某属性正被某 MOD 持有期间，游戏侧对它的改动对其它 MOD 不可见，直到持有者释放；完全没有 contribution 的属性不在表里，读取直通真实值。
- 卸载 owner 时先计算下一层 contribution 或 baseline，并尝试写回 Unity，再移除 owner contribution。写回失败时保留该 contribution 和 session，后续 teardown 可以重试；对象已经被 Unity 回收时只清理登记，不重复写入。写入失败时回滚本次 contribution，使失败的写入不留下需要恢复的登记。
- 同一 MOD 的新 generation 接管同一属性时会淘汰该 MOD 的旧 generation contribution；旧 generation 随后不能再次写入新 session 的属性状态。
- `anchoredPosition` 的值是 generated proxy 的 `UnityEngine.Vector2`，兼容层两侧都无法静态命名该结构体，因此改写器在 callsite 上 `box`、在返回处 `unbox.any`，registry 只负责存放与回放这个装箱值。这条 box/unbox 通路是改写机制的新增能力（`BoxLastValueTypeArgument` / `AllowValueTypeReturnUnbox`），装箱只允许作用于最后一个参数。
- 该隔离只覆盖经过 managed assembly rewrite/bridge 的属性访问调用。MOD 直接通过未改写的 IL2CPP proxy/native 指针访问这些属性，仍然属于兼容层无法强制隔离的 legacy 边界。

VirtualBundle 与 Unity HUD 的对象 lease：

- `PcCompatVirtualBundleRegistry` 对 `ReleaseWithSession` 返回的代理按对象引用登记独占 lease。同一 session 的多个资源描述可以共享一个代理并合并为一次 release；不同 MOD 或不同 generation 领取同一 release-owned 代理时 fail-closed。
- VirtualBundle 同时登记所有已解析代理的使用者。跨 MOD/generation 只有在所有使用者都声明非释放时才允许只读共享；任意一方声明 `ReleaseWithSession`，其它 owner 对同一对象的解析会被拒绝，避免释放 owner 卸载时销毁其它 MOD 仍在使用的代理。
- VirtualBundle 卸载 sink 接收 `modId`、`sessionGeneration` 和有序对象列表。对象列表按 prefab、sprite、字体/其它代理、texture 依赖顺序释放；lease 在 sink 返回前保持，避免卸载与重载并发时发生旧对象销毁新 session 对象的竞态。
- HUD source snapshot 携带 owner/session generation。Android renderer 以 owner 为 surface key，以 generation 作为 root 生命周期边界；generation 变化会重建 root，旧 generation 的资源 release 只允许匹配旧 binding，不得改动新 surface。
- 这两条链路目前仍由各自 registry 实现，不宣称已经形成通用跨后端 `UnityObjectLease` API。

## 性能约束

- 普通 Hook 调用只增加一次 gateway head 读取和每 layer 一次 enabled 读取。
- gateway 与 gate 采用只读可执行代码加独立原子数据，遵守 W^X，不在热路径修改代码页。
- telemetry subscriber 完成后必须使用不可变快照；热路径无注册锁、无文件 IO、无字符串格式化。
- native owner reducer 完成后，每帧只采集一次共享游戏事实；每个 owner reducer 只处理其 recipe 声明需要的字段。
- HUD 仅在 generation 变化时写 Unity 属性，不因 source 数量增加而每帧重建对象。
- 诊断计数使用 relaxed atomic，详细文本只在用户打开诊断或导出时生成。
- native operation begin/end、Host open/cancel/resume/retire 都是冷路径；worker poll 只读取 slot 的 `active/operation_id/cookie/cancellation_requested` 原子字段，不访问 Managed、registry mutex、文件系统或授权 Provider。

## 实施阶段

### 阶段 1：Hook owner gate

- 状态：代码完成，arm64 Release 构建通过，待实机验证。
- 已实现 stable broker gateway、owner gate、owner enable/disable/retire API。
- 已实现 `DobbyHook`、`HookHelper.Unhook` 和 ModLoader 生命周期接线。
- 已实现 W^X 动态 stub、原子 pointer acquire 读取和编译期 lock-free/layout 断言。

### 阶段 2：HUD 多 surface

- 状态：代码与主机合同测试完成，待 Unity 实机验证。
- source 注册已改为 owner key，renderer 已改为全部 source snapshot。
- Unity root、资源绑定、缓存和失败状态已按 owner 拆分。
- surface 创建、隐藏和销毁都处于 owner 故障边界内，一个 owner 失败不会关闭全局 renderer。

### 阶段 3：owner telemetry

- 状态：代码与主机合同测试完成，待 Unity/arm64 实机验证。
- native owner session、immutable dispatcher subscriber、per-bundle lifecycle overlay state 已实现。
- managed snapshot API 已支持并由 PcCompat 插件传入 owner key；旧 snapshot/scalar API 保留默认 owner 聚合兼容视图。
- 共享 realtime/input/hit-margin 事实只采集一次，owner 派生计数和 callback generation 独立。

### 阶段 4：Unity lease

- 状态：ResourceChanger 属性 lease、generated component native object lease、共享 `Behaviour.enabled` contribution、VirtualBundle release lease 和 HUD owner/session surface lease 已完成；跨后端统一 `UnityObjectLease` 的审计半（`PcCompatUnityObjectLeaseAudit`）与销毁/恢复协议半（`PcCompatUnityLeaseTeardown`）已完成；四套 registry 的内部存储合并仍未做（属重构，需先有跨后端争用的真实证据）。
- 已实现 ResourceChanger settings contribution、generation 拒绝、稳定优先级、Rabbit Sprite owner map、基线恢复和前一 owner 重应用。
- 已实现 native `AddComponent` 返回对象的 owner/session 登记、owner 校验、跨 owner GameObject 销毁拒绝、teardown 清理和失败重试。
- 已实现共享原生 `Behaviour.enabled` 的 baseline、owner/session contribution、稳定注册序列、按 owner 读取、卸载恢复和 generation 淘汰；自建 native component 仍使用原有独占 object lease。
- 已实现 VirtualBundle `ReleaseWithSession` 对象独占登记、跨 owner/generation 冲突拒绝、owner/generation release batch，以及 release sink 期间的 retiring lease。
- 已实现 HUD source/session generation 传播、同 owner generation 切换时 root 重建和旧 generation 资源释放拒绝。
- 已实现 KeyViewer fallback owner/session generation 传播，同 owner generation 切换时 visual root 重建。
- 已实现 PcCompat MOD 直接创建对象的可审计登记（`new GameObject` / `Instantiate`），创建即登记、销毁受 owner 校验、teardown 清理，创建/销毁环闭合。
- 已实现跨后端统一 lease 的两半：**审计半** `PcCompatUnityObjectLeaseAudit.Snapshot(modId, generation)` 聚合四套 registry 的 per-owner 库存并以 `IsClear` 单点回答"是否仍被持有"；**销毁/恢复协议半** `PcCompatUnityLeaseTeardown.Run(modId, generation)` 按固定依赖顺序（managed components → VirtualBundle → ResourceChanger）驱动各后端**既有**的 teardown，逐后端隔离失败并汇总全部结果，不复制归属逻辑、不绕过任何后端的所有权规则。HUD 刻意不由该协议驱动——它只能按 source 实例注销（`UnregisterSource(source)` 不接受 owner id），代驱会绕过 source 所有者的生命周期。该协议是生产卸载路径 `PcCompatRuntime.UnregisterMod` 既有顺序的**可执行规格 + 缺失的故障汇总**，不是第二遍清理（对活跃会话调用会造成重复销毁）；用途是卸载后验证、部分失败后的恢复重试（各步在其后端内幂等）与"哪个后端拒绝释放"的诊断。一条契约测试直接断言生产路径的三步顺序，防止将来改动生产顺序而协议悄悄失同步。
- 已实现共享属性 contribution registry 的通用化：仲裁核心改为按 `(目标对象引用, 属性名)` 索引的描述符表，`Behaviour.enabled` 成为其第一个描述符（行为不变），新增第二个描述符 `RectTransform.anchoredPosition`。值以 `object` 流转，因此非 bool 的结构体属性也能进入同一套仲裁；改写机制相应新增 `box`/`unbox.any` 通路。baseline 只采样一次、读取回落到 baseline 而非当前投影值，是这次通用化里两条新的正确性要求，见下条证据。
- 证据更正：此前这里写着"审计确认 `RectTransform.set_*` 作用在 MOD 自己创建的对象上，属私有布局状态，不需要仲裁"。该结论来自只有 DLL 时的 per-instruction 审计，**无法区分目标对象的归属**，现已被三份 MOD 源码推翻：
  - `JipperResourcePack/OverlayContents/Status.cs:139` 在 `scrShowIfDebug`（游戏自有）的 rect 上写 `new Vector2(300, transform.anchoredPosition.y)`；JPOV `ScrShowIfDebugAwakePatch` 在**同一个对象**上写**同一个值**；CheryTools `RegisterAutoplayStatusText` 也登记同一个对象并重定位 rect。三方争用同一属性。
  - `JipperResourcePack/ResourceChanger.cs:191` 对 `scrLogoText`（游戏自有）的 rect 做读-改-写：`anchoredPosition with { y = 0.75f }`。它保留的 x 必须是游戏原值，否则会把别的 MOD 的偏移固化进自己的写入——这条是**getter 也必须走仲裁**的直接原因。
  - `JipperResourcePack/OverlayContents/Overlay.cs:269` 写 `ADOBase.controller.txtLevelName`（游戏自有）。
  - JPOV `Overlay.BetaWatermarkOriginalPos` 与 CheryTools `ElementState.AnchoredPosition` 各自把同一 rect 的原值存进自己的静态字段并各自恢复：谁第二个采样，采到的就是对方改过的值。
  同一份 JRP 源码里也确实存在大量作用于 MOD 自建对象的 `anchoredPosition` 写入（overlay/keyviewer/rain，以及 `ResourceChanger.cs:206` 的 `Instantiate` 克隆体）。这些持有 native lease，走直通路径不进仲裁——所以 lease 的有无正好是"私有布局状态 vs 共享游戏状态"的判据，两类站点可以在同一个程序集里共存而无需静态区分。
- 待实现其余共享属性描述符。CheryTools 另外保存/恢复 `Transform.localScale`、`CanvasGroup.alpha/interactable/blocksRaycasts` 和 `Graphic.color`（`GameUIManager.ElementState`），JRP `Overlay.cs:270` 也写 `txtLevelName.localScale`；这些是同类争用，加描述符即可覆盖，但尚未验证各自的合成语义（`Graphic.color` 可能需要合成器而非 last-writer-wins），因此本轮不一并放开。

### 阶段 5：后台 operation quiescence

- 状态：Managed Task/Thread/ThreadPool/Timer 白名单重写、协作式 unmanaged worker、主机并发测试和 Android Debug/Release 托管构建完成；待实机卸载、长时间运行和真实 MOD 热更新验证。
- Managed operation 使用 session callback counter、取消令牌和精确 owned-resource 条目；native operation 使用固定槽 token registry 和独立 active-count。
- ModLoader 已覆盖正常卸载、逻辑挂起/恢复、失败加载、超时回滚和 ALC 释放前 retirement；恢复不撤销旧 token 的取消状态。
- `ModRuntimeAsyncBridge` 的调度 callback 使用显式 `EnterOwnerScope`；直接 `Task` 返回方法使用 `RequireCurrentScope + TrackTask`，不依赖完成任务时的默认 owner 猜测。Thread 只在 `Start` 线性化点创建 operation，Timer 在创建时登记终止清理、每次 callback 单独登记 operation。
- 任意 ABI 手写 detour 仍按是否具备 Source Generator managed callback gate 分类。不能证明 quiescence 的 legacy layer 继续走保守逻辑挂起，不执行 `OnUnload` 或 ALC 回收。

### 阶段 6：文件路径 domain 隔离

- 状态：domain 路径根、`NativeModPathBridge` 受控 API、rewrite ABI v4、shadow file proof、主机测试与真实 DLL 只读审计完成；待实机验证 MOD 自更新与设置读写。
- 每个 domain 绑定五个根；MOD 原目录只读，可写根位于 Host 拥有的 `.starray-data/<mod>/`，官方资源只读。
- 相对路径锚定 domain config 根，不再解析到共享的进程 CWD；`Path.GetFullPath` 改写，纯字符串 `Path` 助手刻意保留。
- 跨 MOD 根访问在产生文件副作用前拒绝并点名归属 owner；越界、无 domain、未绑定根、退休 generation 一律失败关闭。
- 真实 DLL 只读审计：XPerfect `24`、ShowBPM `22`、Replay `24`、System.Formats.Nrbf `0` 处文件调用点改写，`0` 未覆盖入口。审计过程中发现并补齐了 5 个此前遗漏的重载（`FileStream` 6 参构造、`File.OpenRead/GetLastWriteTimeUtc/WriteAllText(Encoding)`、`Directory.EnumerateFiles(3)`），补齐前这三个 MOD 会因失败关闭而无法加载。
- 未执行实机、ADB 或安装验证。

### 阶段 9：PcCompat 文件路径 domain 隔离

- 状态：`PcCompatManagedPathBridge`、会话生命周期接线、19 条改写规格、缓存 ABI 递增、主机测试与真实 DLL 审计完成；待实机验证 Jipper 设置与 KeyCount/Plays 状态文件读写。
- 归属键为 `(ModId, ResourceSessionGeneration)`；包含判定复用 `ModDataDomainPaths.IsWithin`，未复制第二份比较逻辑。
- 会话构造时 `BindRoots`，`Disable` 时 `ClearRoots`；可写根不按 generation 分目录以保持设置持久，退休 generation 由"根未绑定"失败关闭。
- 真实 DLL 审计：JipperResourcePack 改写 `14` 处（`FileExists` 6、`OpenFileStream` 2、`FileCopyOverwrite`/`FileDelete`/`FileMove`/`FileOpenRead`/`FileOpenWrite`/`FileReadAllText` 各 1），改写后无裸 `File`/`Directory`/`FileStream..ctor`/`Path.GetFullPath` 残留，`Path.Combine` 按设计保留，`ManagedBridgeIssues` 为空。
- 未执行实机、ADB 或安装验证。

### 阶段 8：MOD 直接创建对象的 owner 登记

- 状态：bridge 创建入口、IL2CPP host operation、改写器构造支持、规格注册、缓存 ABI 递增、主机测试与真实 DLL 审计完成；待实机验证 Jipper HUD/KeyViewer 对象生命周期。
- 创建即登记：对象在返回 MOD 前进入 `(modId, sessionGeneration)` lease；登记失败销毁对象再抛。
- Instantiate 只认领克隆体，原型保持借用语义。
- 登记对象进入 owner 审计快照，受既有 `Destroy` 跨 owner 拒绝保护，并由 session teardown 清理。
- 真实 DLL 审计：JipperResourcePack `19` 处 `new GameObject(string)`、`2` 处 `Instantiate` 全部改写，改写后无任何裸 `GameObject::.ctor` 或 `Object::Instantiate` 残留，`ManagedBridgeIssues` 为空；既有 `AddComponent`/`Destroy` 改写保持接通。
- 未执行实机、ADB 或安装验证。

### 阶段 7：网络会话 domain 隔离

- 状态：`ModRuntimeNetworkBridge`、每 domain 网络身份、请求 operation lease、rewrite ABI v5、shadow network proof、主机测试与真实 DLL 只读审计完成；待实机验证 MOD 自更新下载。
- 每 domain 独立 `CookieContainer` 与 handler 管线；同一 domain 内复用同一网络身份，跨 domain 不共享。
- 每请求取得 generation-bound operation lease，lease 取消与请求令牌联结；退休取消在途请求、拒绝新请求，terminal cleanup 释放全部 client。
- 只改写 client 构造点；已绑定实例的操作与返回对象刻意不改写。进程全局网络策略与原始套接字失败关闭。
- 共享 scope 捕获逻辑由 `ModRuntimeAsyncBridge` 抽出为 `ModRuntimeCapturedScope`/`ModRuntimeOwnedOperation`，网络桥复用同一套 staleness 与跨 owner 规则，未复制第二份校验。
- 真实 DLL 只读审计：XPerfect `1`、ShowBPM `1`、Replay `0`、System.Formats.Nrbf `0` 处网络改写，`0` 未覆盖入口；文件改写计数保持 `70` 不变。两个 MOD 各 6 处 `HttpClient` 调用点中只有 1 处是构造，其余 5 处作用在已绑定实例上，印证了只改写构造点的设计判断。
- 未执行实机、ADB 或安装验证。

### 阶段 10：PcCompat 网络会话 domain 隔离

- 状态：`PcCompatManagedNetworkBridge`、会话生命周期接线（构造 `BindNetworkState` / `Disable` 时 `ClearNetworkState`）、6 条改写规格、缓存 ABI 递增、主机测试与真实 DLL 审计完成；待实机验证 Jipper 自更新下载路径。
- 归属键为 `(ModId, ResourceSessionGeneration)`；每会话独立 `CookieContainer` 与 handler 管线，跨 owner 发请求在产生网络副作用前拒绝并点名归属与调用方。
- `Disable` 取消在途请求并释放该 generation 全部 client；退休 generation 因「状态未绑定」失败关闭。
- 覆盖范围前置问题的结论：`JAMod.Bootstrap.dll` **在** PcCompat 托管改写管线内——manifest `Info.json` 的 `AssemblyName` 指向它，`PcCompatManagedAssemblyCatalog` 以其为第二闭包根，运行时经 `PcCompatRuntime` 把重写副本加载进 collectible ALC。此前文档中「Bootstrap 由 JALib 自身加载、不经过改写管线」的记载是错的，已更正。
- 真实 DLL 审计：`JAMod.Bootstrap.dll` 12 处 `System.Net.Http` 引用、仅 1 处构造点改写（`Installer/<InstallMod>` 的 `new HttpClient()`），改写后无裸 `HttpClient..ctor` 残留，已绑定实例操作按设计保留，`ManagedBridgeIssues` 为空；`JipperResourcePack.dll` 0 处网络引用。
- 同切片修复两处缓存 ABI 债务：文件切片漏掉的显式版本号递增以 `v33-net-domain` 补上，并在 `CollectionBridgeAbi` 补齐 `PcCompatManagedPathBridge.v1` 标记；缓存键哈希补入 `SourceIsConstructor`/`EraseBridgeGenericArity` 两个行为字段（此前改动它们而不同步版本号会让旧重写产物滞留缓存）。
- 未执行实机、ADB 或安装验证。

### 阶段 11：外部静态事件订阅登记（下一代合同首个切片）

- 状态：`PcCompatManagedEventSubscriptionBridge`、2 条改写规格、会话 `Disable` 退订接线、缓存 ABI v34、主机测试与真实 DLL 审计完成；待实机验证 Jipper 卸载后无残留订阅。
- 覆盖：`UnityEngine.Application.add_quitting` 与 `SceneManager.add_sceneUnloaded`（审计到的全部 PcCompat 真实订阅点）。`remove_` 保持原样，MOD 自身配对语义不变。
- 退休语义：按 `(modId, resource generation)` 记录，重复订阅逐条记录逐条退订；`RetireOwner` 对单条失败 best-effort，不中断其余退订。
- Android Managed 管线经审计为 0 订阅点，本切片不涉及；后续若真实 MOD 出现订阅点再立项。
- 未执行实机、ADB 或安装验证。

## 自动验证

- `StArray.ModManager.Tests`（Android 目标配置，跳过 Windows native test DLL）：统一 generation、typed callback、managed/native operation lease、shadow static/async rewrite、失败加载 ALC 清理和 unmanaged ELF 身份登记接入后 `1013/1014` 通过，`1` 项既有 XPerfect 环境测试跳过。
- `PcCompatManagedComponentBridgeTests` 定向测试：已覆盖 shared `Behaviour.enabled` 的跨 owner contribution、按序恢复和 generation 淘汰，shared `RectTransform.anchoredPosition` 的 baseline 一次采样、跨 MOD 独立"原值"、按序回落、写失败回滚和 generation 淘汰，以及 native component lease 的创建、跨 owner 拒绝、teardown、失败重试和已销毁对象退休。
- `PcCompatManagedBridgeRewriteTests` 在真实 `JipperResourcePack.dll`（2 处 getter + 31 处 setter）上验证 box/unbox 改写的 IL 形状，并单独断言 `ResourceChanger.OnLogoTextAwake` 的读-改-写两个访问器都被路由。
- Android arm64 Release 原生构建通过，`libstarray_modmanager.so` 链接成功。
- JNI helper 导出校验：`122/122`。
- owner-control 导出已在最终 SO 中确认：install、supports、enable/disable、retire-target、retire-owner、retained-count、enabled-count。
- owner telemetry/ResourceChanger 定向合同：`48/48` 通过。
- Android arm64 Debug 与 Release 原生构建均通过，ResourceChanger contribution 与 owner telemetry 代码已进入最终链接。
- native operation registry 主机测试输出 `NATIVE_MOD_OPERATION_REGISTRY_TEST=PASS`，覆盖 256 槽容量、容量溢出、owner/generation 隔离、取消等待、timeout/resume、retired 拒绝恢复、stale/double-end、ABA 和 8 线程并发；coordinator 测试输出 `NATIVE_PATCH_COORDINATOR_TEST=PASS`。
- 最终 SO 已确认导出 operation ABI `8/8`；对应 runtime root 与 native 完整性元数据来自同一构建批次。
- 尚无 arm64 实机自动化环境。真实 Dobby 跳转、XPerfect/Jipper 双加载顺序、Unity Canvas 生命周期仍必须手工验证。
- 异步隔离定向测试 `15/15` 通过；真实 Android DLL shadow 只读审计：Replay/System.Formats.Nrbf `0` 异步重写问题，ShowBPM/XPerfect 各 `4` 个 Task 返回方法、`0` 未覆盖问题。该审计未执行实机。
- PcCompat 文件路径隔离（2026-08-21）：全量托管测试 `1060/1061` 通过（`1` 项既有 XPerfect 环境测试跳过），对象登记切片后基线为 `1047/1048`，新增 `13` 条测试且无回归；`PcCompatManagedPathBridgeTests` 定向 `12/12`；真实 JipperResourcePack 改写 `14` 处文件调用点、`0` bridge issue；Android Managed Release 构建通过；`git diff --check` 通过。未执行实机、ADB 或安装验证。
- PcCompat 网络会话隔离（2026-08-22）：全量托管测试 `1068/1069` 通过（`1` 项既有 XPerfect 环境测试跳过），文件切片后基线 `1060/1061`，新增 `8` 条测试且无回归；`PcCompatManagedNetworkBridgeTests` + `PcCompatManagedBridgeRewriteTests` + `PcCompatAndroidInputContractTests` 定向 `78/78`；真实 `JAMod.Bootstrap.dll` 改写 `1` 处网络构造、`0` bridge issue，改写后无裸 `HttpClient..ctor` 残留、已绑定实例操作按设计保留；Android Managed Release 构建通过；`git diff --check` 通过。未执行实机、ADB 或安装验证。
- 外部静态事件订阅登记（2026-08-22）：全量托管测试 `1075/1076` 通过（`1` 项既有 XPerfect 环境测试跳过），网络切片后基线 `1068/1069`，新增 `7` 条测试且无回归；`PcCompatManagedEventSubscriptionBridgeTests`（6 条）+ `PcCompatManagedBridgeRewriteTests` + `PcCompatAndroidInputContractTests` 定向 `78/78`；真实 JipperResourcePack 主 DLL 改写 `2` 处 `add_` 访问器（`sceneUnloaded`、`quitting`）、`2` 处 `remove_` 按设计保留、`0` bridge issue；Android Managed Release 构建通过；`git diff --check` 通过；触碰文件纯 LF。未执行实机、ADB 或安装验证。
- PcCompat owner-scoped VFS overlay（2026-08-22）：全量托管测试 `1077/1078` 通过（`1` 项既有 XPerfect 环境测试跳过），事件切片后基线 `1075/1076`（新增 `3` 条 VFS 测试、移除 `1` 条与影子语义冲突的旧只读断言），定向（路径桥/改写/契约/网络桥/事件桥）`99/99`；`PcCompatManagedPathBridge.v2-vfs-overlay` + 缓存 ABI `v35-vfs-overlay`，旧缓存自动失效；Android Managed Release 构建通过；`git diff --check` 通过；触碰文件纯 LF。未执行实机、ADB 或安装验证。
- Android Managed 安装根 VFS overlay（2026-08-22）：全量托管测试 `1080/1081` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过）；shadow rewrite ABI 升级为 `starray-native-isolation-rewrite-v6-vfs-overlay`，旧 cache 自动失效；`NativeModPathBridgeTests` `10→13`（新增安装根数据写影子化+执行物拒绝、状态文件轮转往返、仅包层源 Move 模拟、包层∪overlay 合并枚举四条，移除与影子语义冲突的旧"安装根写拒绝"断言）；Android Managed Release 构建通过；触碰文件纯 LF；`git diff --check` 通过。未执行实机、ADB 或安装验证。
- **验证流程事故记录（2026-08-22）**：Android VFS 切片的首轮验证因新测试里一处 `FileCopyOverwrite` 缺参导致测试工程编译失败，而后续命令只 grep 了错误计数、把尾部警告误读为构建输出，随后多次"全量回归"实际运行的是上一版编译产物（旧测试集），得出过 `1077/1078` 的假读数。已修复参数、以真实编译产物重跑并更正本节数字。教训入册：**任何"构建+测试"命令必须显式断言构建成功（匹配 error/生成失败）之后才允许采信其后的测试结果**；对总数与增量记账不符的现象应视为编译陈旧信号立即排查，而不是解释掉。
- Direct Link 调用门骨架（2026-08-23）：全量托管测试 `1110/1111` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），统一 lease 切片后基线 `1104/1105`，新增 `6` 条测试（Provider scope 归属与返回恢复、异常原样传播且 scope 恢复并断言异常对象 `Is.SameAs`、`A->B->A` 重入的 LIFO 顺序、未链接失败关闭、Provider 退休后拒绝、自链接拒绝）；Debug 与 Release 构建均显式断言 error=0。骨架未含身份哈希/自动绑定/反向门/CrossDomainObjectLease/SCC staging/热更新重绑定，待真实 Provider API 形态后微调。未执行实机、ADB 或安装验证。
- 阶段 4 统一 lease 销毁/恢复协议（2026-08-23）：全量托管测试 `1104/1105` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），资源预算切片后基线 `1101/1102`，新增 `3` 条测试（依赖顺序、未知会话幂等且审计为 clear、生产卸载路径顺序契约）；Debug 与 Release 构建均显式断言 error=0。未执行实机、ADB 或安装验证。
- 每 MOD 资源预算首个切片（2026-08-23）：全量托管测试 `1101/1102` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），`UiOwnerScope` 切片后基线 `1096/1097`，新增 `5` 条测试（硬上限只拒本 owner、退休释放额度可复用、进程期类别不受限、generation 作用域、软硬跨越描述）；同轮修复 `2` 条既有契约测试——它们借已删除的 `disable_async_for_dlc_if_needed` 定义当函数体结束锚点（DLC 熔断移除的连带影响），改为以"下一个函数定义"为语义稳定边界，断言内容未变；Debug 与 Release 构建均显式断言 error=0。未执行实机、ADB 或安装验证。
- `UiOwnerScope` 首个切片（2026-08-22）：全量托管测试 `1096/1097` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），链接防护后基线 `1091/1092`，新增 `5` 条测试（成功路径、故障不外泄、连续故障按 owner+generation 隔离且不影响他人、穿插成功清零计数、Release 只清本代）；首版缺无-context 守卫导致测试宿主 fatal，修复后测试正常执行；Debug 与 Release 构建均显式断言 error=0。未执行实机、ADB 或安装验证；ImGui 栈深度快照-恢复仍缺 native 导出。
- 符号链接/重解析点穿透失败关闭（2026-08-22）：全量托管测试 `1091/1092` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），回查修复后基线 `1089/1090`，新增 `2` 条逃逸回归（两条管线各一，使用**真实目录联接**而非模拟，本机实际创建成功并被拦截，未走 Ignore 分支）；路径桥/自更新定向 `36/36`；shadow ABI → `v8-link-guard`、PcCompat 缓存 ABI → `v36-link-guard` + `PcCompatManagedPathBridge.v3-link-guard`，安全语义变化使旧缓存失效；Debug 与 Release 构建均显式断言 error=0。未执行实机、ADB 或安装验证。
- 统一 lease 审计接口（2026-08-22）：全量托管测试 `1082/1083` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），Android VFS 切片后基线 `1080/1081`，新增 `2` 条测试；构建成功经显式断言（error 计数为 0）后才采信测试结果；Android Managed Release 构建通过。未执行实机、ADB 或安装验证。
- 跨 MOD 发现面审计（2026-08-22）：对六个真实 MOD DLL（XPerfect/ShowBPM/Replay/Nrbf/Jipper 主 DLL/JAMod.Bootstrap）做 dnlib 逐指令审计——`AppDomain.GetAssemblies`、`Assembly.Load/LoadFrom/LoadFile`、UMM `modEntries`、JALib `GetMods/FindMod` 在 Android 语料中为 **0** 处；PcCompat 仅有的 2 处 `Assembly.GetType` 均解析 MOD 自有程序集内类型（自反射）。「联动模型：Broker 与 Direct Mod API Link」「虚拟 MOD 目录」两章当前没有真实调用点拉力，维持仅设计状态；后续真实 MOD 出现跨 MOD 发现用法时再立项。
- MOD 直接创建对象登记（2026-08-21）：全量托管测试 `1047/1048` 通过（`1` 项既有 XPerfect 环境测试跳过），网络切片后基线为 `1039/1040`，新增 `8` 条测试且无回归；`PcCompatManagedComponentBridgeTests` + `PcCompatManagedBridgeRewriteTests` 定向 `86/86` 通过；真实 JipperResourcePack 改写 `19` 处 GameObject 构造 + `2` 处 Instantiate、`0` bridge issue；Android Managed Release 构建通过；`git diff --check` 通过。ABI 递增使既有 `PcCompatAndroidInputContractTests` 的硬编码 ABI 断言同步更新（该契约测试的目的正是让 ABI 变更显式化）。未执行实机、ADB 或安装验证。
- 网络会话隔离（2026-08-21）：全量托管测试 `1039/1040` 通过（`1` 项既有 XPerfect 环境测试跳过），文件切片后基线为 `1029/1030`，新增 `10` 条测试且无回归；`ModRuntimeNetworkBridgeTests` + `NativeModShadowRewriteTests` + `NativeModPathBridgeTests` 定向 `33/33` 通过；共享 scope 抽取后 `ModRuntimeAsyncBridgeTests` `17/17` 仍通过，行为未变；Android Managed Release 构建通过；`git diff --check` 通过。真实 DLL 审计 `2` 处网络改写、`0` 未覆盖入口。未执行实机、ADB 或安装验证。
- 文件路径隔离（2026-08-21）：全量托管测试 `1029/1030` 通过（`1` 项既有 XPerfect 环境测试跳过），改动前基线为 `1014/1015`，新增 `15` 条测试且无回归；`NativeModPathBridgeTests` + `NativeModShadowRewriteTests` 定向 `23/23` 通过；Android Managed Release 构建通过；`git diff --check` 通过。真实 DLL 审计 `70` 处文件调用点、`0` 未覆盖入口。未执行实机、ADB 或安装验证。

## 验收矩阵

| 场景 | 预期 |
| --- | --- |
| 只加载 JipperResourcePack | HUD 与数值持续更新 |
| 只加载 XPerfect | 判定、计数和误差量计正常 |
| 先加载 Jipper，再加载 XPerfect | 两组重叠 Hook 都执行，original 一次 |
| 先加载 XPerfect，再加载 Jipper | 与上一场景语义一致 |
| 挂起 XPerfect | Jipper HUD 继续更新，XPerfect detour 不再进入 |
| 恢复 XPerfect | 只恢复未退休 layer，不重复安装旧 layer |
| 卸载时 XPerfect 调用旧 `Unhook` | 不抛异常，指定 layer 逻辑退休 |
| 卸载 Jipper | XPerfect 继续工作，Jipper HUD root 被单独销毁 |
| 同帧两个 PcCompat HUD 更新 | 两个 surface 都可见，各自数值独立 |
| 一个 HUD source 抛异常 | 只 quarantine 该 source |
| 一个 managed event callback 抛异常 | 其它 owner ring 和 dispatcher 继续工作 |
| MOD 重载 | 旧 generation 的事件、frame 和资源发布被拒绝 |
| 两个 ResourceChanger contribution 依次加载 | 稳定 sequence 较新的 owner 生效，另一 owner 状态仍保留 |
| 卸载当前 ResourceChanger owner | 先恢复官方基线，再应用前一 owner 的颜色、Logo 与 Rabbit Sprite |
| 旧 session 延迟发布 Rabbit Sprite | 发布被拒绝，不覆盖新 session 或其它 owner |
| PcCompat `AddComponent` 创建 native component | 对象登记到当前 owner/session，卸载时只清理该 session 的对象 |
| 一个 MOD 销毁含其它 owner component lease 的 GameObject | 在调用 Unity `Destroy` 前拒绝，不影响其它 owner |
| Unity `Destroy` native component 失败 | lease 保留，后续 teardown 可重试；已失效对象不重复销毁 |
| 两个 MOD 通过 bridge 修改同一个共享 `Behaviour.enabled` | 各自读取自己的 contribution，Unity 只投影注册序列最高的值 |
| 共享 `Behaviour.enabled` 的顶层 owner 卸载 | 先恢复下一 owner 的值；最后一个 owner 卸载后恢复首次接管时的 Unity baseline |
| 同一 MOD 新 generation 接管共享 `Behaviour.enabled` | 旧 generation contribution 被淘汰，旧 generation 后续写入被拒绝 |
| 两个 MOD 先后采样同一个游戏自有 rect 的 `anchoredPosition` 作为"原值" | 两者拿到同一个游戏 baseline；后采样者不会拿到前者的偏移 |
| 两个 MOD 各自持有同一 rect 的 `anchoredPosition` contribution 后逐个卸载 | 先回落到剩余 contribution，最后一个卸载后恢复游戏原始位置 |
| 同一 MOD 新 generation 接管共享 `anchoredPosition` | 旧 generation 写入被拒绝，卸载新 generation 后恢复游戏 baseline |
| 共享 `anchoredPosition` 写入真实 Unity 失败 | 回滚本次 contribution，MOD 仍读到游戏值，teardown 无需恢复 |
| MOD 自己创建的 rect 上写 `anchoredPosition` | 持有 native lease，直通真实 Unity API，不进入仲裁表 |
| 改写 `RectTransform.anchoredPosition` 的 getter/setter callsite | setter 前 `box UnityEngine.Vector2`，getter 后 `unbox.any UnityEngine.Vector2`，栈平衡 |
| 两个 MOD 返回同一个 `ReleaseWithSession` VirtualBundle 代理 | 第二个 owner/generation fail-closed，不允许登记共享销毁 lease |
| 一个 MOD 持有可释放代理，另一个 MOD 非 owner 复用 | fail-closed，不能让前者卸载时销毁后者仍在使用的对象 |
| 两个 MOD 只读共享非释放代理 | 允许共享；双方都不拥有销毁权 |
| VirtualBundle 卸载与另一 MOD 重载并发 | release sink 返回前旧 lease 保持 retiring，新 owner 不能领取旧对象 |
| HUD 同一 owner 跨 generation 重载 | 旧 root 先销毁并创建新 root，旧资源 release 不影响新 surface |
| KeyViewer fallback 同一 owner 跨 generation 重载 | 旧 visual root 被销毁并重建，不复用旧 visual 状态 |
| Android Managed MOD 通过 component bridge 创建 Canvas | owner GameObject 出现在设置 Canvas probe，避免被认作外部 Canvas |
| MOD 写入自己的 config/cache/log/temp 根 | 允许；相对路径锚定 domain config 根，不落到进程 CWD |
| MOD 写入自己的安装目录 | 拒绝（只读），执行仍走 shadow 包 |
| MOD 读写另一个 MOD 的任一根 | 在产生文件副作用前拒绝，诊断点名归属 owner/generation |
| MOD 读官方资源目录 | 允许只读；写入拒绝 |
| MOD 用 `..` 相对遍历跳出根 | 解析后归属校验失败，拒绝 |
| MOD 在自己可写根内建符号链接/联接指向别处 | 归属校验通过后按链接检查拒绝，读写均不发生；受害方字节不变 |
| 两个 MOD 打开同名 ImGui 窗口/控件 | 各自 owner ID namespace 隔离，窗口状态与 popup 不串 |
| Consumer 经 Direct Link 调用 Provider API | Provider 代码在 Provider owner scope 内执行（其创建的资源归 Provider），返回后恢复 Consumer scope |
| Provider API 抛异常 | 原样传播（类型/对象身份/inner 均不被包装），Consumer scope 仍被恢复 |
| `A -> B -> A` 同线程重入 | 允许；每次边界切换按 LIFO 恢复，双方 generation lease 全程持有 |
| 调用未建立链接或已退休 generation 的 Provider | 抛 `ModDependencyNotReadyException`，不返回副本状态或空占位 |
| Provider 重载后旧 Consumer 继续调用 | 旧链接已随退休释放，调用失败关闭 |
| 一个 MOD 触达某类资源硬上限 | 仅拒绝该 MOD 的新登记；其它 MOD 同类申请照常通过 |
| 处于上限的 MOD 执行卸载 | 退休不受预算限制，可干净卸载；释放后额度复用 |
| MOD 重载后重新申请资源 | 新 generation 不继承上一代用量 |
| MOD 登记 Hook/CodePatch | 无数量上限（进程期永久，拒绝会使注册表与物理链失同步），仍进审计 |
| MOD 绘制回调抛异常 | 就地熔断，Host 的 Begin/End 配对不被破坏，管理器 UI 继续可用 |
| 同一 MOD 连续 4 次绘制失败 | 按 (owner, generation) 隔离其 UI；其它 MOD 不受影响 |
| 隔离后 MOD 重载 | 新 generation 不继承隔离状态，UI 恢复绘制 |
| 无 ImGui context 时触发绘制路径 | 跳过 ID 注入并安全降级，不使进程 fatal |
| 链接旁的普通路径 | 正常放行（链接检查不得过度拒绝） |
| 根之上存在 Host 侧链接（模拟存储/重定位数据目录） | 放行：向上只检查到根为止，非 MOD 行为 |
| 路径与某个根共享字符串前缀但属于兄弟目录 | 按分隔符边界判定，不视为包含，拒绝 |
| MOD 退休后再访问文件系统 | domain 已关闭，连 owner scope 都无法重入 |
| MOD 使用未覆盖的文件 API | shadow 发布前失败关闭，不静默放行 |
| 两个 MOD 各自 `new HttpClient()` | 各自获得独立 Cookie 容器与 handler 管线，会话不互见 |
| 同一 MOD 多次创建 client | 复用该 domain 的同一网络身份 |
| MOD 请求在途时该 MOD 退休 | 在途请求被取消，不落到已退休 generation；随后新请求被拒绝 |
| MOD A 使用 MOD B 的 client 发请求 | 在发出请求前拒绝，诊断点名归属 owner |
| MOD 修改 `ServicePointManager` 等进程全局网络策略 | shadow 发布前失败关闭 |
| MOD 使用原始套接字或未覆盖的 client 构造 | 失败关闭，不静默放行 |
| PcCompat MOD `new GameObject(name)` | 返回前登记到当前 owner/session，进入审计快照 |
| PcCompat MOD `Instantiate(prototype)` | 只登记克隆体；原型是借用，不被认领 |
| MOD 创建对象后 session teardown | 该 session 创建的对象被销毁清理 |
| 创建后登记失败 | 销毁刚创建的对象再抛，不留下无主 Unity 对象 |
| PcCompat MOD 写自己的 `.pccompat-data` 根 | 允许；相对路径锚定 config 根，不落到进程 CWD |
| PcCompat MOD 写自己的 MOD 目录（非 `.pccompat-data`） | 写入映射到 data overlay 同名相对路径，包层保持不可变 |
| PcCompat MOD 读隔离前已存在的旧设置文件 | overlay 无 shadow 时按包层原样可读，无需迁移拷贝 |
| PcCompat MOD 保存后读同一文件 | overlay shadow 遮蔽包层，读到最新保存值 |
| PcCompat MOD 删除安装根内文件 | 只移除 overlay 副本；包层原件重新对读取可见 |
| PcCompat MOD 在安装根内 `Move(dat -> .bak)` 轮转 | overlay 内真实移动；源仅在包层时以复制模拟，包层原件保留 |
| PcCompat MOD 访问另一 MOD 或自身旧 generation 的根 | 在产生文件副作用前拒绝并点名归属 owner |
| PcCompat MOD 在 disable 阶段访问文件 | 失败关闭 |
| PcCompat MOD 重载后读旧设置 | 可写根不按 generation 分目录，设置仍可读 |
| 无 owner scope 或 disable 阶段创建对象 | 失败关闭 |
| PcCompat MOD `new HttpClient()` | client 绑定当前会话身份，Cookie/凭据/连接池不与其它 MOD 共享 |
| PcCompat MOD 用另一 MOD 的 client 发请求 | 在发出请求前拒绝，诊断点名归属与调用方 |
| PcCompat MOD 请求在途时该 MOD Disable | 在途请求被取消，该 generation 的 client 全部释放 |
| PcCompat MOD 重载后新建 client | 绑定新 generation 的独立身份，旧代身份已随 Disable 释放 |
| PcCompat MOD 使用 `ServicePointManager` 等进程全局网络策略 | 无改写规格，属可诊断的隔离降级，不静默放行 |
| Android Managed MOD 写安装根内数据文件 | 映射到 `.starray-data/<mod>/data` 同名相对路径，包层保持不可变 |
| Android Managed MOD 读隔离前已存在的旧数据文件 | overlay 无 shadow 时按包层原样可读 |
| Android Managed MOD 替换安装根内自身 `.dll`/`.exe` | 写入落入 overlay 成为 pending self-update；loader 仍运行包层副本，激活前完全惰性 |
| MOD 替换自身程序集后删除自己的下载目录 | 暂存字节在 Host overlay 内，pending 清单不受影响（真实更新器就是这样收尾） |
| pending self-update 需要回滚 | 删除 overlay 条目即回滚，包层原件从未被修改 |
| MOD 自更新后读取自身程序集做校验 | 读到 overlay 新字节，更新器自校验流程可完整走完 |
| Android Managed MOD 在安装根内轮转状态文件（dat → .bak） | overlay 内真实移动；仅包层源以复制模拟，包层原件保留 |
| PcCompat 会话卸载后查询统一 lease 审计 | `IsClear=true`：四个后端 registry 均不再持有该 owner 的对象，单一导出行可核验 |
| 卸载时某个后端 teardown 抛异常 | 该后端记录失败但其后的后端仍继续执行，结果集报告是哪个后端拒绝释放 |
| 对未知/已清空会话运行 teardown 协议 | 各步在其后端内幂等，成功返回且审计为 clear（可作为部分失败后的恢复重试） |
| 生产卸载路径的后端顺序被改动 | 契约测试失败，防止协议与生产路径失同步 |
| PcCompat MOD `SceneManager.sceneUnloaded +=` / `Application.quitting +=` | 订阅经登记桥转发并按会话记录，事件行为与原始调用一致 |
| PcCompat MOD 自身 `OnDisable` 正常 `-=` 退订后卸载 | 原始 remove 生效；退休时的逐条退订为幂等 no-op |
| PcCompat MOD 故障或跳过 OnDisable 直接卸载 | session Disable 时该代全部残留订阅被逐条退订，共享 IL2CPP 事件上无指向退休 ALC 的委托 |
| PcCompat MOD 重复订阅同一处理函数后退订 | 登记逐条对应，退休移除全部副本，不留悬挂委托 |
| 同一 MOD 新 generation 的订阅在旧 generation 退休后继续工作 | 退休按 `(modId, generation)` 精确作用，不影响新代订阅 |
| PcCompat MOD 在 disable 阶段订阅外部事件 | 失败关闭 |
| PcCompat MOD 枚举安装根目录中的文件 | 合并 package 与 data overlay；同相对名由 overlay 覆盖，返回实际可读路径；递归逐目录拒绝链接穿透 |
| PcCompat MOD 创建安装根下的可选目录后仍用原包路径枚举 | 枚举命中新建的 overlay 目录，不修改包层，不因包路径缺失抛异常 |
| PcCompat MOD 枚举逻辑上确实不存在的目录 | 保留 `DirectoryNotFoundException`，不把所有缺失目录静默伪装为空集合 |
| Android Managed MOD 订阅外部静态事件 | 当前语料为 0 订阅点，未改写；出现时属可诊断隔离降级 |

主机测试覆盖注册表、状态机、subscriber、HUD composition 和加载顺序。真实 Dobby 指令跳转、IL2CPP ABI、Unity 对象生命周期只能通过 arm64 实机手工验证；没有实机证据时不得把阶段 1 标记为完整发布验证。

## ADOFAIOnlineMod 发现链回归（2026-08-28）

Android MOD 的分类不能由 `Info.json` 的存在或字段形状决定。发现器先对候选入口 DLL
执行不加载代码的 metadata 证明：只有确认存在具体、非抽象 `IModPlugin` 后才选择原生
loader；`Info.json` 在该情况下仅保留为显示和更新元数据。这样可避免带有 PC 风格
清单的 Android MOD 被送入 `PcCompat`。

删除 `Info.json` 时，候选仍通过 DLL 入口进入原生 shadow 发布。`ADOFAIOnlineMod` 的
实际入口是 `ADOFAIOnlineMod.Mobile.OnlinePlugin`。其完整托管闭包已用生产重写规格审计，
文件系统、路径、异步定时器等调用经 owner bridge 接管；无法证明的隔离调用仍在发布前
失败关闭，不静默回落到宿主全局 API。

回归要求同时覆盖：

1. 带 PC 形状清单的原生插件初始扫描必须保持 `NativeLoaderKind`。
2. 无清单的原生 DLL 必须仍能进入原生发现路径。
3. 真实 MOD 主程序集及其托管依赖闭包必须由生产规格重写成功。

上述检查均已在本机通过；未进行实机、ADB、APK 或设备 runtime 验证。

## 2026-08-28 编译器生成 RVA 只读 span 证明

native shadow 重写 ABI 已升级为
`starray-native-isolation-rewrite-v14-readonly-rva-span`。编译器生成的
`<PrivateImplementationDetails>` 数据除了 `InitializeArray` 句柄形式，还可能以
`ldsflda` 直接构造 `ReadOnlySpan<T>(void*, int32)`；直接取地址构造 span 只有在
`initonly`、固定可验证大小、完整生成 owner 链匹配，且 span 长度在 RVA 数据范围内时，才归类
为共享不可变数据。`InitializeArray` 形式允许字段缺少 `initonly`，但必须证明它没有其它直接
读取、取地址或写入用途。

该规则覆盖嵌套 `__StaticArrayInitTypeSize=N` 和原始整数/浮点 `static initonly` 数据，
不把普通 `ldsflda`、可写 `Span<T>`、独立字段句柄、未知指针或写入用途放行。剩余可变 RVA、
句柄逃逸和无法证明的跨程序集静态字段继续在发布前失败关闭。

本机已通过 RVA fixture `3/3` 和真实 `SixLabors.ImageSharp.dll` 重写 `1/1`；强签名输出保留
程序集 identity 所需的公钥信息，但清除旧签名标志，不伪造重签名。设备验收必须同步新 `v16`
重写器及其 runtime 资产；本轮未执行实机、ADB、APK 或 native SO 操作。

## 2026-08-28 泛型静态字段 owner 隔离

`MemberRef.Class` 可能是 `TypeSpec/GenericInstSig`，不能直接交给普通字段解析器，否则
闭合泛型嵌套类型会被错误识别为跨程序集字段。重写器现在先沿 `TypeSpec` 取得底层本地
`TypeDef`，再在该 owner 中解析字段；程序集归属仍按 metadata 证明，真正的私有依赖访问
继续失败关闭。未知 owner、开放泛型或无法建立完整字段用途证明时，不生成 shadow 输出。

对可证明的闭合泛型静态字段，重写计划保存实际 owner 类型，并生成二泛型参数的 domain
bridge 调用：

```text
GetStaticSlotForOwner<T, TOwner>(slot)
SetStaticSlotForOwner<T, TOwner>(slot, value)
GetStaticSlotReferenceForOwner<T, TOwner>(slot)
```

运行时 slot 身份是 `(slotId, closedOwnerType)`。同一个 owner 的类型一致性仍由 slot cell
校验，因此不会因为 owner 隔离而丢失原有类型冲突检测；不同闭合实例（例如 `Cache<int>`
和 `Cache<string>`）不会共享静态值或引用。

泛型类型的显式静态初始化同样按闭合 owner 隔离。当前 shadow ABI 为
`starray-native-isolation-rewrite-v16-generic-static-initializer-owner`；重写后的初始化
桥调用携带 `RuntimeMethodHandle` 和 `RuntimeTypeHandle`，运行时以
`(initializerMethod, closedOwnerType)` 作为初始化状态键，并使用带 owner 的
`MethodBase.GetMethodFromHandle` 解析和执行搬移后的 `.cctor`。因此 `Cache<int>` 与
`Cache<string>` 不会错误共享“已初始化”标志，也不会因泛型声明类型缺少第二个句柄而在
运行时无法解析初始化方法。非泛型调用保留两参数 bridge 兼容入口；未知或无法构造闭合
owner 的初始化仍失败关闭。

本机合同覆盖 `MemberRef + TypeSpec` 的读取、写入、取引用和跨闭合 owner 值隔离；真实
`ADOFAIOnlineMod` 及其完整托管 DLL 闭包也已重写回归。相关定向结果为 `39/40` 通过，`1`
项按既有环境条件跳过。当前代码已完成编译验证，但本轮仍未同步设备 runtime，未进行实机、
ADB、APK、native SO 或顶层全量构建验证。
