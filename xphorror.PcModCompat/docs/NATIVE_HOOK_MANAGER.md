# Native HookManager 设计

## 目标

Native HookManager 是 PC MOD 兼容层的运行期核心。它负责在 Android arm64-v8a 上接管 IL2CPP/native 函数入口，并执行已经编译好的 native rule bytecode。

它同时是 ModManager 进程级 `Il2CppRuntimeHost` 的 Hook 子系统。metadata 解析、入口修改、original continuation 和未来受控类型注入所需的内部 detour 都只能由 ModManager 实施；PcCompat 和 MOD 只能提交经过验证的声明式计划。

运行期目标：

- Dobby 对每个真实 target 只安装一次。
- 所有 hook 热路径只在 native 层执行。
- C# 可做 UI、配置、状态展示和 UnityMain managed self-render，但不参与普通游戏 hook callback 热路径。
- 运行期不 unhook；关闭 MOD 只禁用 rule。
- 已安装 detour 的 original trampoline 和 dispatcher index 在进程生命周期内永久保留；清理规则不能把 original 置空。
- 支持多个 compiled bundle 合并到同一个 Hook Slot。
- 所有 target 地址从运行时 metadata 解析，不使用固定 RVA 或 dump 地址。
- fault 可观测、可限频、可自动禁用问题 rule。

## 非目标

第一版不做：

- x86/x64/armeabi-v7a。
- Java method hook。
- Mono managed hook。
- 任意 PC C# callback 直接执行。
- 任意 native so 插件加载。
- 运行期恢复 target 原入口。
- 无限制 `SkipOriginal` / `ReplaceOriginal`。
- 直接开放上游 ClassInjector 或允许 MOD 自行注册 IL2CPP 类型。

## 总体结构

```text
compiled bundle
        |
        v
Native HookManager
        |
        +-- Bundle loader
        +-- IL2CPP metadata resolver
        +-- Hook slot registry
        +-- Infrastructure hook registry (planned injection support)
        +-- Injection type registry (planned)
        +-- AArch64 universal bridge
        +-- Rule VM
        +-- Capability gate
        +-- Fault ring buffer
        |
        v
HookBroker(target + slot detour layer)
```

## Hook Slot

每个目标函数对应一个 Hook Slot：

```text
HookSlot
  target_id
  target metadata key
  target runtime address
  original trampoline
  detour kind
  slot state
  rule chains
```

Slot 状态：

```text
PendingResolve
Resolved
HookInstalled
InstallFailed
DisabledByCapability
Faulted
```

Rule chain：

```text
BeforeOriginal[]
ReplaceOriginal[]
AfterOriginal[]
FaultHandlers[]
```

同一 target 被多个 bundle 请求时：

- target metadata key 相同则合并到同一个 Hook Slot。
- rule 按 priority、bundle load order、rule id 稳定排序。
- 高风险 stage 必须通过 capability gate。

## Infrastructure Hook 与 Injection Registry

普通 HookSlot 管理“某个游戏/native 函数执行哪些 rule”。真实 IL2CPP 类型注入是另一类进程级状态，不能通过增加普通 rule 模拟：

```text
HookSlotRegistry
  target metadata identity
  fixed/native dispatcher
  before/replace/after rule chains

InfrastructureHookRegistry
  IL2CPP runtime internal target
  exact native ABI
  process-lifetime detour/continuation
  reserved owner = modmanager.il2cpp-injection

InjectionTypeRegistry
  source type/schema identity
  Il2CppClass/vtable/MethodInfo/native thunk
  owner/session behavior gate
  process-lifetime roots
```

当前只实现 `HookSlotRegistry` 和通用 HookBroker；`InfrastructureHookRegistry`/`InjectionTypeRegistry` 是后续受控 class-injection 扩展，不能在状态文档中标为已完成。

注入扩展必须满足：

- ClassInjector 内部 detour 只能经 HookBroker 安装，不直接调用 Dobby。
- infrastructure hook 不占用普通 MOD 的动态 fixed-rule dispatcher arena，也不接受普通 MOD rule。
- target 从当前进程导出、metadata 或经过 ABI/指令边界验证的动态 native resolver 获得；禁止固定 RVA/VA 和 dump fallback。
- 同一 target/replacement 注册幂等；不同 schema 争用同一 IL2CPP 类型名时失败关闭。
- 注入类型、thunk、delegate、GC handle 和承载它的 ALC 保持到进程退出。
- 关闭 MOD 只关闭 owner/session callback gate；不 unhook、不删除 class、不释放 continuation。

ModManager 只接受导入器生成并通过 capability/schema 审计的 `TypeInjectionPlan`。MOD 代码直接调用 `ClassInjector.RegisterTypeInIl2Cpp` 必须拒绝。普通 generated proxy 调用和当前 surrogate managed-component bridge 不需要进入注入 registry。

## 生命周期

```text
process start
  Activity starts ModManager background bootstrap
  initialize CoreCLR and scan configured MODs
  load compiled bundle index
  validate bundle headers
  merge target requests
  wake native hook coordinator
  wait for libil2cpp / global-metadata / Assembly-CSharp readiness
  resolve target metadata and install hook slots

optional verified type injection
  validate TypeInjectionPlan
  initialize brokered infrastructure hooks once
  register/freeze type schema before first instance
  keep type and callback roots for process lifetime

menu action
  show the already initialized ImGui overlay
  do not restart CoreCLR or reinstall existing hook slots

runtime enable mod
  enable bundle rules

runtime disable mod
  disable bundle rules
  do not DobbyDestroy

uninstall mod
  mark disabled / remove config
  keep current process slot alive
  next restart no longer load bundle

process restart
  if no bundle needs target, target is never hooked
```

运行期禁止普通模块直接调用 `DobbyDestroy`。Hook 入口属于 HookManager，不属于某个 MOD。

第三方 managed native MOD 仍可能通过公开 `HookHelper.Hook()` 直接安装 delegate detour，且旧 MOD 的
`OnUnload()` 常在忽略 `Unhook()` 返回值后清空 delegate 字段。HookBroker 拒绝物理 unhook 时，这会让永久
detour 指向可回收的 managed trampoline。Android 宿主因此采用以下生命周期合同：

- `IHook.SupportsRuntimeUnhook=false` 表示 provider 的 detour 与 continuation 具有进程生命周期。
- `ModLoader` 在 `BeginLoad/OnLoad/CompleteLoad` 外建立 owner scope；`HookHelper` 记录安装过此类 hook 的 MOD。
- 关闭该 MOD 时不调用 `OnUnload()`，而是把条目挂起为 `NotLoaded`、停止 HUD/UI，并保留插件实例、delegate 根和
  continuation；配置写为关闭，APP 重启后才通过“不再加载”完成真正停用。
- 同一会话重新启用挂起 MOD 只恢复条目状态，不重复执行 `OnLoad()` 或安装 hook。
- 绕过 loader 直接调用 `HookHelper.Unhook()` 必须抛出 `NotSupportedException`，不能让调用方误判失败后继续释放根。

这条合同只保护 managed native MOD 的 delegate 生命周期；PcCompat bundle 仍按 owner/rule gate 停用，并继续复用
native permanent dispatcher。

## Target Key

IL2CPP target 使用 metadata key：

```text
assembly
namespace
class
method
genericArity
paramTypes
returnType
isStatic
```

运行期解析：

```text
metadata key
  -> Il2CppClass*
  -> MethodInfo*
  -> methodPointer / invoker
  -> target runtime address
```

native target 使用 symbol key：

```text
library name
symbol name
expected abi
```

所有 key 都进入 cache key，游戏版本、IL2CPP build id 或 rule format 变化后必须重新解析。

## AArch64 Universal Bridge

第一版只支持 Android arm64-v8a。

Bridge 需要保存：

- `x0-x30`
- `sp`
- `pc/lr`
- 必要的 `nzcv`
- `v0-v7`
- original 返回后的 `x0/x1`
- original 返回后的 `v0/v1`

HookContext：

```c
typedef struct XHookContext {
    uint64_t x[31];
    uint64_t sp;
    uint64_t pc;
    uint64_t nzcv;
    __uint128_t v[8];
    uint64_t ret_x0;
    uint64_t ret_x1;
    __uint128_t ret_v0;
    __uint128_t ret_v1;
    uint32_t flags;
    uint32_t target_id;
    uint32_t thread_kind;
    uint32_t reserved;
} XHookContext;
```

返回值规则：

- integer/pointer 返回：`x0`。
- 128-bit integer 返回：`x0/x1`。
- float/double 返回：`v0`。
- 大结构体返回第一版不承诺通用支持，必须通过 target 白名单确认。

## Original 调用

默认流程：

```text
capture context
run BeforeOriginal
if not skipped:
  call original trampoline
  capture return
run AfterOriginal
apply return patch
return to caller
```

`SkipOriginal` / `ReplaceOriginal` 只有 capability 允许且 target 白名单通过时才可执行。

如果 rule fault：

- 中止当前 rule。
- 不中止其它 owner 的 rule，除非该 fault 污染 HookContext。
- 不阻断 original。

## Rule VM

HookManager VM 执行 register-like 领域 bytecode。

热路径禁止：

- JSON 解析。
- 字符串比较。
- managed call。
- 动态堆分配。
- map 级别的频繁查找。

注入类型的 Unity native message thunk 可能进入 CoreCLR，这是独立于普通 game-hook rule VM 的受控边界。它必须使用固定签名、进程期 rooted thunk、异常屏障和 owner/session gate；不得借此从任意 Hook 线程分发 Harmony/JAPatch callback。

运行期加载 bundle 后预解析：

```text
fieldId  -> offset/accessor
methodId -> function pointer/call wrapper
funcId   -> native callable pointer
stringId -> string table offset
```

VM 每次 invocation 带 instruction budget。budget 耗尽生成 VM exception。

## Native Callable

`CALL_NATIVE` 只能调用 HookManager 内置 callable table。

允许：

```text
CALL_NATIVE funcId, argBase, argCount
```

禁止：

```text
CALL_ABSOLUTE
CALL_REG
dlopen MOD 自带 so
从任意系统库解析符号
```

Callable 必须声明：

```text
funcId
name
signature
allowed stages
thread requirements
required capability
fault behavior
```

## Capability Enforcement

capability 不只用于 UI 展示，runtime 必须强制校验。

执行 rule 前检查：

```text
bundle enabled
rule enabled
capability approved
stage allowed
thread allowed
fault state allowed
```

高风险能力：

```text
WRITE_IL2CPP_FIELD
CALL_IL2CPP_MUTATOR
PATCH_RETURN
SKIP_ORIGINAL
REPLACE_ORIGINAL
INPUT_INJECTION
```

未授权时：

- rule 不执行。
- slot 仍可安装。
- UI 显示 disabled by capability。

## 线程模型

HookManager 必须识别 thread kind：

```text
UnityMain
RenderThread
InputThread
AudioThread
UnknownNativeThread
```

默认规则：

- UnityMain：允许状态读取、overlay、资源状态更新、低风险 IL2CPP getter。
- RenderThread：只允许渲染相关 callable 和低开销日志。
- InputThread：只允许输入相关 native callable，不允许 UI/资源加载。
- AudioThread：第一版禁止 managed/UI/资源类 callable，只允许明确审计的常量时间 native callable。
- UnknownNativeThread：只允许 observe/log，默认不允许 mutator。

每个 callable 和 rule 都必须声明允许 thread kind。

## Fault Model

VM exception 写入 fault ring buffer：

```c
typedef struct XHookFault {
    uint64_t timestamp_ns;
    uint32_t bundle_id;
    uint32_t rule_id;
    uint32_t target_id;
    uint32_t code;
    uint32_t pc;
    uint32_t opcode;
    uint32_t count;
    char message[256];
} XHookFault;
```

策略：

- LOGCAT 限频输出。
- UI 通过 native API 拉取最近 fault。
- 连续 fault 达阈值后自动禁用 rule。
- fault 状态可持久化到本次 session 状态文件，避免 UI 丢失。

建议阈值：

```text
same rule fault >= 3 -> disable rule for session
budget exhausted >= 3 -> disable rule for session
```

## Bundle Loading

加载流程：

```text
read bundle index
validate magic/version
validate size and section offsets
validate checksum
validate capability approvals
load string table
load target table
load field/method/callable table
verify bytecode
merge rules into slots
```

Bundle 校验失败：

- 不安装该 bundle 的 rules。
- 记录 compile/runtime error。
- 不影响其它 bundle。

## 与现有 Hook 的兼容

现有项目里已经有 EGL/Input/Vulkan/ImGui hook。长期方案要求：

- 新增兼容层 hook 必须走 HookManager。
- 已有直接 Dobby hook 可逐步迁移到 HookManager。
- 迁移前，托管侧 Dobby registry 作为过渡保护，避免同地址重复 inline。
- 同一个 native target 如果必须共享，最终由 Hook Slot dispatcher 承载多 owner rule。

第一版不能假设所有旧 hook 已迁移，因此 native HookManager 安装前仍要能检测 Dobby 安装失败并降级。

## Native API

C# UI 只通过低频 API 控制 HookManager：

```c
int xhook_load_bundle(const char *bundle_dir);
int xhook_enable_bundle(uint32_t bundle_id, int enabled);
int xhook_get_bundle_status(uint32_t bundle_id, XHookBundleStatus *out);
int xhook_get_recent_faults(XHookFault *out, uint32_t max_count);
int xhook_set_capability_approval(uint32_t bundle_id, uint64_t capability_mask);
```

这些 API 不在 hook 热路径里调用。

## 当前落地切片

当前完成度和验证证据统一见 [`IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md)。本节描述当前 native 机制和仍保留的 fixed-op/ABI 边界。

当前代码实现了 HookManager 的最小 loader、metadata resolver、slot registry、安装计划，以及第一版受限 fixed dispatcher。

这不是保存任意寄存器上下文的通用 AArch64 universal bridge。当前按去重 target 动态生成 ABI 专用 thunk，进入公共 fixed dispatcher；只接受已验证的 14 类 ABI/fixed-op，用来闭合 “runtime rule bundle -> native slot -> Dobby -> original -> fixed op”。

当前也没有启用 class injection：Android Runtime 仍使用 `SlotOnlyDetourProvider` 且 `EnableClassInjection=false`。本节后续所有已落地计数只描述普通 HookSlot/recipe 链，不包含未来 infrastructure hook 或注入类型。

已落地：

```c
int modmanager_pccompat_load_hook_rules_json(const char *path);
int modmanager_pccompat_get_loaded_bundle_count(void);
int modmanager_pccompat_get_loaded_target_count(void);
int modmanager_pccompat_get_loaded_rule_count(void);
int modmanager_pccompat_get_merged_slot_count(void);
int modmanager_pccompat_resolve_pending_slots(void);
int modmanager_pccompat_get_resolved_slot_count(void);
int modmanager_pccompat_get_failed_slot_count(void);
int modmanager_pccompat_get_pending_slot_count(void);
int modmanager_pccompat_get_slot_rule_count(void);
int modmanager_pccompat_get_enabled_slot_rule_count(void);
int modmanager_pccompat_get_disabled_slot_rule_count(void);
int modmanager_pccompat_get_installable_slot_count(void);
int modmanager_pccompat_get_install_blocked_slot_count(void);
int modmanager_pccompat_get_installed_slot_count(void);
int modmanager_pccompat_get_dispatcher_ready_slot_count(void);
int modmanager_pccompat_get_dispatcher_capacity(void);
int modmanager_pccompat_get_bound_dispatcher_count(void);
int modmanager_pccompat_prepare_install_plan(void);
int modmanager_pccompat_install_planned_slots(void);
const char *modmanager_pccompat_get_slot_summary(void);
uint64_t modmanager_pccompat_get_approved_capabilities(void);
void modmanager_pccompat_set_approved_capabilities(uint64_t capabilities);
int modmanager_pccompat_get_rule_count_for_target(const char *type_name, const char *method_name, int param_count);
const char *modmanager_pccompat_get_last_error(void);
void modmanager_pccompat_clear_hook_rules(void);
int modmanager_pccompat_unload_hook_rules_for_mod(const char *mod_id);
void modmanager_pccompat_set_overlay_changed_callback(void *callback);
int modmanager_pccompat_read_overlay_snapshot(void *output, uint32_t output_size);
int modmanager_pccompat_get_overlay_visible(void);
int modmanager_pccompat_get_overlay_practice(void);
uint32_t modmanager_pccompat_get_overlay_show_count(void);
uint32_t modmanager_pccompat_get_overlay_hide_count(void);
uint32_t modmanager_pccompat_get_overlay_player_update_count(void);
uint32_t modmanager_pccompat_get_overlay_state_change_count(void);
int modmanager_pccompat_get_overlay_last_op(void);
int modmanager_pccompat_get_overlay_last_target_kind(void);
int modmanager_pccompat_get_overlay_player_count(void);
int modmanager_pccompat_get_overlay_last_seq_id(void);
int modmanager_pccompat_get_overlay_last_is_restart(void);
int modmanager_pccompat_get_overlay_last_wipe_direction(void);
int modmanager_pccompat_get_overlay_last_reset_to_editor(void);
uint32_t modmanager_pccompat_get_overlay_judgement_hit_count(void);
uint32_t modmanager_pccompat_get_overlay_judgement_reset_count(void);
int modmanager_pccompat_get_overlay_last_hit_margin(void);
uint32_t modmanager_pccompat_get_overlay_floor_move_count(void);
float modmanager_pccompat_get_overlay_last_floor_exit_angle(void);
int modmanager_pccompat_get_overlay_last_floor_move_hit_margin(void);
uint32_t modmanager_pccompat_get_overlay_player_hit_count(void);
int modmanager_pccompat_get_overlay_last_player_hit_is_auto(void);
uint32_t modmanager_pccompat_get_overlay_death_count(void);
int modmanager_pccompat_get_overlay_last_death_overload(void);
int modmanager_pccompat_get_overlay_last_death_multipress(void);
int modmanager_pccompat_get_overlay_last_death_hitbox(void);
uint32_t modmanager_pccompat_get_overlay_hit_timing_count(void);
float modmanager_pccompat_get_overlay_last_hit_timing_ms(void);
int modmanager_pccompat_get_overlay_last_hit_timing_margin(void);
```

HUD 热路径只允许调用 `modmanager_pccompat_read_overlay_snapshot()`。当前使用固定布局 ABI v3，并继续接受旧 v2/160 字节前缀；结构携带 `struct_size`、`abi_version` 和 `generation`。Android 托管层不再按 EGL 帧轮询；native 在 UnityMain 上完成稳定的 lifecycle/final-judgement fixed op 后，通过 `modmanager_pccompat_set_overlay_changed_callback()` 注册的回调触发一次 bulk read。调用方把已缓存的 `generation` 写回请求，未变化时 native 直接返回 `0`。逐字段 getter 仅保留给旧调用方、可见性快速探测和调试。

加载对象是 recipe cache 生成的二进制 bundle，JSON 只作审计和旧缓存回退：

```text
<modsRoot>/compiled/<mod_id>/<cache_key>/ui_recipe.bin
<modsRoot>/compiled/<mod_id>/<cache_key>/hook_rules.json
```

当前 loader 行为：

- 校验 `formatVersion == "mvp-fixed-op-v2"`；旧 v1 bundle 由新 cache key 淘汰并拒绝加载，避免旧 key 与永久 slot 并存。
- 解析 `modId`、`recipeId`、`compatibility`、`requiredCapabilities`。
- 解析 `targets[]` 为 native `RuntimeTarget` 表。
- 解析每个 target 的 `rules[]` 为 native `RuntimeRule` 表。
- 同一路径重复加载是幂等操作，不重复写入表。
- 支持多个 bundle 常驻在同一 native table。
- 运行时卸载按 `modId` 退役 bundle、managed event ring、Prefix/Postfix order plan、UI lifecycle program 与 presentation graph；不能用全局 `clear` 影响其他 MOD。
- 退役先发布 callback gate，再移除 active rule，最后才允许托管 session/ALC Dispose。旧 immutable snapshot 通过共享所有权安全退出，但看到 `retired` 后不得再次进入托管回调。
- synchronous Prefix 使用 per-bundle in-flight lease；退役置 `retired` 后等待该 bundle 已进入的 Prefix 全部返回。从 Prefix callback 内递归卸载会失败关闭，不能等待自身。
- platform sink 已注册但 native 退役失败时必须中止卸载/重载并保留 managed session；禁止在 active rule 仍可回调时继续 Dispose。未注册 sink 表示当前平台没有 native bundle，可直接完成托管清理。
- event ring 不再为满足旧 snapshot 使用裸指针永久泄漏；registry、snapshot 和 drain cache 使用共享所有权。反复重载同一 MOD 时，退役 ring 可释放，UI lifecycle tombstone 可复用，不能累计撞上 256 program 上限。
- 统计 bundle / target / rule 数量，给托管 UI 和后续接线用。
- 按规范化 `assembly + namespace + type + method + static + genericArity + returnType + parameterTypes` 统计合并后的唯一 Hook Slot 数。
- 可按 `typeName + methodName + paramCount` 查询某 target 当前累计 rule 数。
- 可调用 `modmanager_pccompat_resolve_pending_slots()` 将 target 解析到 IL2CPP `Class`、`Method` 和 runtime function pointer。
- method resolve 会枚举同名候选，先严格比较 static/instance、非泛型身份、返回类型和每个有序参数类型，再要求唯一候选通过 `abiKind` dispatcher 校验；零个或多个匹配都会阻断。当前 fixed target 均为非泛型，非零 `genericArity` 明确失败关闭。
- resolver 直接复用现有 Android Hook 的目标式 IL2CPP API 链路：枚举 domain assembly image，按 `assembly -> namespace/class -> method` 精确查找。它不调用 UnityResolve 全量枚举全部 assembly/class/field/method，因此启动成本和 readiness 风险都更低。
- metadata resolve 失败会保留错误信息并允许后续同步重试；这处理 Unity/IL2CPP 尚未 ready 的启动期窗口。
- runtime bundle 成功加载会唤醒 native coordinator。没有规则时线程休眠；有规则时每 500 ms 检查 `libil2cpp.so`、`global-metadata.dat` 和 `Assembly-CSharp`，ready 后执行 `resolve -> prepare -> install`。UI 是否显示不参与这条链路。
- 解析完成后重建 Hook Slot registry：同一个 target key 的多个 bundle target 合并到同一个 slot。
- 同一 target key 的 bundle 如果声明不同 `abiKind`，slot 立即进入 `Faulted`；后续 bundle 或旧安装状态不能覆盖该冲突。
- 每个 slot 维护 `before_rules`、`replace_rules`、`after_rules` 三段 rule chain。
- rule chain 按 bundle id、target id、rule id 稳定排序。
- native capability gate 已启用，未批准能力的 rule 会被标记为 disabled，不进入 installable 统计。
- 默认批准低风险能力：`ReadState`、`AfterOriginalObserve`、`Log`、`UiOverlay`、`ReadIl2CppField`。
- `installable slot` 当前含义是：target 已 resolve，至少有一条 enabled rule，并且通过 fixed dispatcher 的 ABI / stage / op gate。
- `modmanager_pccompat_prepare_install_plan()` 只生成安装计划，不调用 Dobby。
- `modmanager_pccompat_install_planned_slots()` 会对计划内 slot 调用 Dobby，保存 original trampoline，并把 slot 标记为 `HookInstalled`。
- install plan 不直接调用 Dobby，而是调用 `modmanager_hook_broker_install()`。HookBroker 是集成 APK 中唯一允许创建 inline hook 的组件。
- 同一 target 第一次注册时，HookBroker 才对真实 IL2CPP/native 入口安装 Dobby。后续注册不再改真实入口，而是 hook 当前 detour head，形成 `target -> newest detour -> previous detour -> ... -> original trampoline`。
- 每个 layer 都拿到自己的 continuation，因此现有 detour 的“调用 original”语义保持不变。`scrPlayer.Hit(bool)` 和 `scrMisc.GetHitMargin(...)` 可以同时经过 PcCompat 与 AsyncInput，不再通过跳过规则牺牲任一侧功能。
- HookBroker 以 target 和 replacement 做永久 registry。同 replacement 重复注册幂等返回原 continuation；同 replacement 绑定不同 target 会被拒绝。
- 对每条 chain 的第一个 patch point，以及追加 layer 时尚未被 patch 的当前 head，HookBroker 检查 AArch64 入口。它按 target 到 replacement 的距离计算 Dobby 实际需要的 12/16 字节覆写范围；未知直接 `B`、literal-load + `BR`、BTI + `B` 跳板，以及在覆写末尾前出现 `B/BR/RET/BRK` 的短 stub 都会被拒绝，避免接管旧 hook 或污染相邻函数。
- ModManager 导出的 `modmanager_dobby_destroy()` 在进程生命周期内无条件拒绝调用，既不能拆根 target，也不能拆被 broker 作为 patch point 的 detour head。关闭 MOD 只停用 rule，不拆 chain。
- rule/capability/bundle 变化会重新计算已安装 dispatcher 的 after-op mask；slot fault、规则关闭或 bundle 清理只把 mask/enable 清零，detour 仍始终调用 original。
- 卸载 A 时只从 active plan 移除 A；B 的 bundle、规则、event ring 和 UI graph 必须继续工作。清理后重新加载同一 target 会按 target key 和 ABI 复用永久绑定，不再次调用 Dobby；加载新 target 才追加 dispatcher page，不得复用已经绑定过的 dispatcher index。
- `required/capacity/bound` 是进程期物理绑定诊断：卸载不会缩小 `capacity/bound`，且 `required` 仍包含永久绑定 target。`bundles/slots/rules/ready` 描述当前 active plan，会随按 MOD 卸载下降；两组数不能混为一谈。
- `scrMarginTracker.CalculatePercentAcc` 只由 native permanent fixed dispatcher 接管。旧托管 state publisher Hook 已删除，不再存在第二条游戏函数 detour。
- install plan 支持 after-original 观测，以及 ResourceChanger 已审计的 before-original 参数覆盖/skip-original fixed-op；一般 `ReplaceOriginal` 和其它 before-original 操作仍失败关闭。
- install plan 会按 `abiKind` 过滤 dispatcher 可支持的签名形态；当前只允许已实现公共 dispatcher 和 AArch64 thunk 参数布局的 ABI。
- 完整方法身份唯一匹配后，metadata resolve 阶段再把 `abiKind` 与运行时 IL2CPP `Method` 对照，验证 AArch64 GP32/GP/FP32/FP64 dispatcher 布局。两道校验都发生在读取 function pointer 和 Dobby 安装之前。
- 当前支持的 ABI：
  - `InstanceVoid0`
  - `InstanceVoid1`
  - `InstanceVoidInt1`
  - `InstanceVoidPtrFloatInt`
  - `InstanceVoid3`
  - `InstanceVoidBoolBoolPtrBool`
  - `InstanceVoidColor1`
  - `InstanceVoidIntBool`
  - `InstanceVoidPtrBool`
  - `InstanceBool1`
  - `InstanceBool2`
  - `InstanceBoolBoolInt`
  - `StaticVoid1`
  - `StaticIntFloatFloatBoolFloatFloatDouble`
- `abiKind` 必须描述真实 IL2CPP 签名，而不是 C# 默认参数调用形态。未列入支持表的返回值、static 方法、多参数方法即使 feature recipe 可识别，也不能进入当前 fixed dispatcher。
- dispatcher 不再使用固定 slot stub。install plan 按永久绑定 target key 与当前 installable staging target key 的并集计算 `required`，先为整个新批次分配稳定 runtime page 和 RW→RX AArch64 thunk page，成功后才允许 HookBroker 物理安装。已 hook target 在本进程永久占用 index 和 thunk；后续 MOD 只为去重后新增 target 扩页。同批分配失败会整体阻断，诊断导出 `required/capacity/bound/ready/blocked/new/allocated/remaining`。
- 当前支持的 fixed op：
  - `OverlayShow`
  - `OverlayShowPractice`
  - `OverlayHandleStateChange`
  - `OverlayHide`
  - `OverlayUpdatePlayers`
  - `PublishMarginSnapshot`
  - `OverlayRecordHit`
  - `OverlayResetJudgement`
  - `OverlayRecordFloorMove`
  - `OverlayRecordPlayerHit`
  - `OverlayRecordDeath`
  - `OverlayRecordHitTiming`
  - `OverlayPollTelemetry`
  - `ResourceApplyEditorRabbit`
  - `ResourceApplyFloorColor`
  - `ResourceApplyPlanetColor`
  - `ResourceApplyLogoText`
- 当前支持的 before-original fixed op：
  - `ResourceSkipPlanetColorOriginal`
  - `ResourceOverridePlanetColorArg`
  - `ResourceSkipTileColorOriginal`
- `PublishMarginSnapshot` 已进入 native dispatcher，并在官方 `CalculatePercentAcc()` 返回后读取结果。
- 通用 `ResourceRedirect` 仍未支持；上述 ResourceChanger fixed-op 是已审计的安全子集，不代表通用资源 recipe 已完成。
- Jipper ResourceChanger 的 17 个 R143 目标均由 fixed-op 覆盖，其中 16 个安装物理 Hook。`PlanetRenderer.SetRainbow(bool)` 的 arm64 函数体只有 8 字节且紧邻 `SetTailColor(Color)`，不能满足 HookBroker 的 12/16 字节安全覆写要求；install plan 将其标记为 `SkippedKnownConflict`，并要求该 slot 的所有 enabled rule 都是 `ResourceSkipPlanetColorOriginal`。其效果由 RainbowMode、SetColor、LoadPlanetColor 和五个颜色 setter 组合覆盖，不计安装失败、不分配 dispatcher。`modmanager_pccompat_set_resource_changer_settings` 接收 owner/generation、三个原 MOD 开关、三组动态颜色和 `ResourcePackName`；热路径仅原子读取颜色/开关，字符串只在 Logo Awake 低频路径加锁读取。
- `modmanager_pccompat_publish_resource_changer_sprite` 只接受 VirtualBundle 已物化的 `Auto` Sprite identity，并以 owner/session generation + GCHandle 管理生命周期；`modmanager_pccompat_retire_resource_changer_sprite` 在 Resource IR session 退休时解除持有。状态 sink 延迟注册和 VirtualBundle session ready 都会重放最新 ResourceChanger 状态；同 owner/generation 发布去重。诊断导出包含 Sprite 的 `requested/resolved/published/retired/failure/lastError`。native 不接收文件路径，不调用 `TextureManager.LoadNewSprite`，Android runtime 不打包 `Auto.png`。
- true -> false 的开关变化发布恢复位，Android resource work queue 在 UnityMain 调用 `modmanager_pccompat_apply_pending_resource_changer_state`。恢复覆盖编辑器原 Sprite + `OttoUpdate`、逐星球 `LoadPlanetColor(isRed)`、Beat 默认色和 `scrLogoText.UpdateColors`；Hook 路径也会消费未执行的恢复位，场景 Hide 清理星球/地板/Logo GCHandle。
- fixed op 会维护 native overlay runtime state，可通过 `modmanager_pccompat_get_overlay_*` 读取当前可见性、练习态、op 计数、最近判定事件和最后 op。
- `PublishMarginSnapshot` 已进入 fixed dispatcher 支持表。resolver 同时解析 `scrMarginTracker` 的官方 accuracy/X-accuracy backing-field offset；after-op 在官方 `CalculatePercentAcc()` 返回后读取结果并发布原始 0..1 比例，不执行兼容层准确率算法。该 op 只在 snapshot 数值变化时触发 Unity HUD 通知，并对纯 accuracy 通知加 50ms 门控；native snapshot 本身仍每次更新。
- overlay show 会清空上一局的准确率、判定和偏移快照。所有 gameplay observation op 都要求 overlay session 处于 active；session 外的 `AddHit`/`GetHitMargin` 等调用不会进入 HUD。
- overlay recipe 只要声明 `UiOverlay`，平台层就补入 `scrController.QuitToMainMenu` Hide 兜底；其它 Hide 点来自经 callback IL 验证的通用领域规则。
- fixed op 会按已知 target kind 解码安全参数：`scnGame.Play(seqID,isRestart)`、`scrMistakesManager.SetPlayerCount(playerCount)`、`scrUIController.WipeToBlack(direction,...)`、`scrController.StartLoadingScene(direction)`、`scnEditor.ResetScene(clsToEditor)`、`scrMarginTracker.AddHit(hitMargin)`、`scrPlanet.MoveToNextFloor(floor,exitAngle,hitMargin)`、`scrPlayer.Hit(isAuto)`、`scrPlayer.Die(overload,multipress,failMessage,hitbox)`、`scrMisc.GetHitMargin(hitangle,refangle,isCW,bpmTimesSpeed,conductorPitch,marginScale)`。
- `scrPlanet.MoveToNextFloor` 当前只记录 `exitAngle` 和 `hitMargin`，不解析 `scrFloor` 对象，也不把 IL2CPP 对象指针交给 PC MOD callback。
- `scrPlayer.Die` 当前只记录三个 bool 参数，不读取 `failMessage` 字符串，也不解析 `playerID` 或 `planetarySystem` 字段。
- `scrMisc.GetHitMargin` 当前先调用官方 original 并保留返回值，再按通用 timing telemetry 公式记录最近 timing ms 和官方 `HitMargin` 返回值；不改写判定结果。
- HUD bulk snapshot ABI v2 额外暴露 progress、combo、attempt、BPM/KPS。progress 通过 IL2CPP metadata 动态解析 `scrController.get_instance` 与 `scrController.get_percentComplete`，只在会话开始和 `MoveToNextFloor` 后低频调用；标准 progress bar 只消费托管层已缓存的 progress frame，不引入额外 native 读取；combo/attempt/BPM 只写 native 原子字段，不进入 CoreCLR 热路径。
- combo 遵循 Jipper 基础版语义：Perfect 和 Auto 递增，其它非 Auto 判定清零。Jongyeol 的 yellow-combo 语义还没有作为独立设置接入。
- attempt/best 已通过 `<mod>/.pccompat/mobile_play_stats.json` 持久化，并按关卡身份、起始进度和倍速隔离；AUTO/noFail 会话不更新统计。它是移动端等价实现，不执行 PC MOD 的原始 `PlayCount` 托管代码。
- 参数解码不是通用 IL2CPP 参数反射。没有 target kind 白名单的参数不读、不展示。
- `modmanager_pccompat_get_slot_summary()` 返回低频诊断摘要，用于 logcat / UI 展示 slot 状态；该 API 不在 hook 热路径调用。
- 不解析任意 callback IL。
- fixed dispatcher 不执行未经翻译的任意 callback；受验证 lifecycle/native bytecode 由独立 Rule VM 和 scheduler 执行。
- 规则 table 和永久 dispatcher binding 都只存在当前进程内存；进程重启后 binding 消失，再由 recipe cache 决定需要安装哪些 target。

托管接线：

```text
PcCompatRuntime.RegisterMod
  -> PcCompatRecipeBundleCache.Write
  -> PcCompatRuntime.RegistryChanged
  -> PcCompatDobbyBridge.SynchronizeRuntimeRuleBundles
  -> modmanager_pccompat_load_hook_rules_json
  -> modmanager_pccompat_resolve_pending_slots
  -> modmanager_pccompat_prepare_install_plan
  -> modmanager_pccompat_install_planned_slots
```

这个切片已经把 `recipe_report.json` 和 runtime `hook_rules.json` 拆开，并确认 C# 到 native 的加载、metadata resolve、slot 合并、capability gate、安装计划、Dobby 安装和 after-original fixed op 状态更新链路存在。

Android 托管层已经注册 overlay snapshot provider：

```text
PcCompatDobbyBridge.Install()
  -> PcCompatOverlayRuntime.RegisterProvider(PcCompatNativeHookRules.GetOverlaySnapshot)
  -> PcCompatDiagnosticsRuntime.RegisterProvider(...)
  -> load hook_rules.json
  -> wake native hook coordinator
  -> wait IL2CPP metadata readiness
  -> resolve / prepare / install through HookBroker
```

`PcCompatUnityHudBridge` 在启动时注册 native overlay 变更回调。回调沿原 IL2CPP Hook 的 UnityMain 调用链执行，读取一次 bulk snapshot，并通过 generated proxies 把满足标准 HUD capability 的 MOD 显示模型写入持久化 Unity `Canvas/TextMeshProUGUI`。HUD source 可注册多个，当前选择最近注册且可见的 source；选择过程不读取 MOD ID。HUD 文本仅在内容变化时调用 `TMP_Text.set_text`，RectTransform/字号/背景色仅在设置代变化时更新；标准 progress bar 使用两个持久化 `Image` 节点，仅在 progress bar 可见性或填充值变化时调整 RectTransform。ModManager 主窗口关闭后不再为 HUD 执行 ImGui/EGL 帧。`PcCompatModPlugin.OnForegroundGUI()` 只保留为 Il2CppInterop/proxy、Canvas 或 TMP 初始化失败时的自动回退。

PcCompat MOD 的设置窗口同时提供低频 native 诊断页。诊断快照按 500 ms 缓存，避免打开窗口后每帧执行多次 P/Invoke。页面展示扫描/翻译覆盖率、bundle/target/rule 数、slot 生命周期、dispatcher 永久绑定占用、capability、native error 和完整 slot summary，并提供 `Resolve`、`Prepare`、`Install`、`ReloadRules` 与带二次确认的 `ClearRules`。

PC MOD 启用时采用两阶段加载：metadata 扫描、callback 翻译和 recipe 编译在后台任务执行；bundle 注册只把 `hook_rules.json` 交给 native。`CompleteLoad/CompatSetup` 不属于后台阶段，它可以触发 MOD 静态构造和 generated proxy，因此必须经 UnityMain scheduler 执行。metadata readiness、target resolve、安装计划和 HookBroker 安装由 native coordinator 完成。关闭 MOD 会取消后台任务，generation token 会阻止旧任务覆盖后续加载的进度。重新扫描 MOD 目录时必须保留仍在 `Loading` 的插件实例和任务状态。

后台自动加载不再借 hidden ImGui render 推进 pending task。启动时 `Managed.EntryCore()` 先扫描 MOD 目录并按 `modmanager_config.json` 的启用状态调用 `ModLoader.LoadConfiguredEnabledMods()`；随后托管层用 100 ms timer 调用一次 guarded pending-load poll。这样 APP 启动即加载已启用 MOD，但未打开菜单时不会因为加载状态安装或执行 EGL/ImGui 每帧热路径。

slot 明细默认调用 native `modmanager_pccompat_get_slot_summary_for_mod(modId)`，按 bundle 的 `modId` 精确过滤；共享 target 会显示在每个关联 MOD 的页面中，但仍只占一个永久 dispatcher。用户可切换到全局 slot 视图。两种摘要均按 500 ms 缓存。

诊断页可以通过 Android `ACTION_CREATE_DOCUMENT` 导出 UTF-8 文本。报告包含当前 MOD 的 manifest、扫描/翻译覆盖率、全局 native 计数、当前 MOD slot、全局 slot 和 callback issue。系统文档选择器打开或写入期间禁止发起第二次导出，导出状态经 Java bridge 回传 UI。

`ClearRules` 的语义严格限定为停用规则：dispatcher 的 `enabled` 与 `after_op_mask` 会清零，overlay runtime state 会重置，但 Dobby detour、original trampoline 和 target/ABI/index 永久绑定都保留到进程退出。`ReloadRules` 清理 bundle path 去重状态后重新同步当前启用 MOD；同 target 必须复用原 dispatcher，不能再次安装 Dobby。手动诊断命令与自动 `Synchronize()` 共用 bridge 同步门禁，禁止两个安装阶段并发。

## 测试

必须补三类测试：

1. Native HookManager 单测
   - slot merge。
   - rule enable/disable。
   - capability gate。
   - fault disable。
   - bytecode verifier。

2. 真机 smoke test
   - metadata resolve。
   - HookBroker 首层接管真实 target，后续 layer 形成 continuation chain。
   - AsyncInput 先注册、PcCompat 后注册时，`scrPlayer.Hit` 和 `scrMisc.GetHitMargin` 均形成两层 chain，两个 detour 都执行且 original 只执行一次。
   - PcCompat 先注册、AsyncInput 后注册时得到相同语义。
   - original 调用后游戏流程不变。
   - fault ring buffer 可读。
   - 打开 PcCompat 设置页时，状态每 500 ms 更新且游戏输入不穿透设置窗口。
   - `ClearRules` 后 `installed=0`、`bound` 不下降，官方游戏流程仍正常。
   - `ReloadRules` 后规则恢复，同 target 的 `bound` 不增长，logcat 中没有重复注册同一 broker layer。
   - `Faulted/InstallFailed` 为红色、`PendingResolve` 为黄色、`HookInstalled` 为绿色，长 slot 行可完整换行和滚动；`PlanetRenderer.SetRainbow(bool)` 应显示 `SkippedKnownConflict` 和组合覆盖说明，不计入 failed。
   - 首次启用 MOD 时进度依次经过扫描、翻译、规则编译和 native coordinator 安装；期间 UI/游戏持续响应。
   - APP 启动但未打开 ModManager 菜单时，已启用 MOD 仍完成扫描、规则加载和 HookBroker 安装，overlay 保持隐藏。
   - 首次打开菜单只切换 overlay 可见性，不出现第二次 CoreCLR 初始化或同 target 重复 Dobby 安装。
   - 加载中关闭 MOD 后不安装规则；立即重新启用时旧任务不能覆盖新任务的阶段。
   - 当前 MOD slot 过滤与全局视图可切换，共享 target 不增加 `bound`。
   - 导出诊断报告会打开系统文档选择器，保存内容为 UTF-8，取消和写入失败都能回显。

3. 兼容回归
   - EGL/Input/Vulkan/ImGui 现有 hook 不被 PcCompat slot 破坏。
   - 同地址冲突时降级可观测。

4. 受控注入扩展（实现后）
   - 未请求注入时不安装任何 injection infrastructure hook。
   - 同一类型计划重复注册幂等，不重复分配 class/vtable 或 HookBroker layer。
   - 同名不同 schema、未知 base/interface、ABI 不符和 resolver 多候选全部失败关闭。
   - MOD disable 后 native `Component` 身份仍存在，但 owner callback 全部跳过；重启后未启用 MOD 不重新注册。
   - 注入内部 hook 与普通 HookSlot、AsyncInput、EGL/Input/Vulkan/ImGui layer 共存，original 只执行一次。
   - 注入 ALC、delegate、thunk 和 GC roots 在进程期不被错误回收。

## 开放问题

1. universal bridge 是否需要保存全部 `v0-v31`，还是第一版保存 `v0-v7`。
2. 大结构体返回是否直接标记 unsupported。
3. 是否把现有 EGL/Input/Vulkan hook 第一批迁移进 HookManager。
4. fault ring buffer 是否需要跨进程/跨重启持久化。
5. 受控注入首批采用逐 MOD 类型注册，还是先注册单个通用 `PcCompatManagedBehaviourHost`；后者只能提供通用 Component 身份，不能提供每个 MOD 自定义类型的精确 cast/GetComponent 身份。
6. Unity 6000.3.10f1 的 injection infrastructure 内部目标如何在无 xref、无固定地址条件下形成可重复的 native resolver 与 ABI 证明。
