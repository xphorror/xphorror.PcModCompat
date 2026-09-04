# 动态 PATCH 翻译与 Native HookManager 设计

运行期和导入期的细化设计分别见：

- [`NATIVE_HOOK_MANAGER.md`](NATIVE_HOOK_MANAGER.md)
- [`TRANSLATOR_PIPELINE.md`](TRANSLATOR_PIPELINE.md)

## 背景

PC MOD 兼容层不能长期依赖“加载并执行 PC MOD C# 代码”来获得 patch 列表。这个方案在 Android 上有几个问题：

- 执行 `OnSetup()` / `Bootstrap` 会运行 MOD 代码，无法保证安全和可控。
- 热路径如果从 native hook 进入 CoreCLR，会带来明显性能成本。
- PC MOD 的托管对象和 Android IL2CPP 对象不是同一个运行时对象，直接调用 callback 容易形成假兼容。
- 多个功能同时 hook 同一 native/IL2CPP 函数时，直接多次 Dobby inline hook 会互相覆盖或污染入口。

因此目标调整为：

```text
导入阶段：
  静态读取 PC MOD 元数据、DLL metadata、资源文件
  翻译/编译成 Android native hook bundle
  缓存到 app 私有目录

运行阶段：
  native HookManager 加载已编译 bundle
  运行时解析 IL2CPP metadata 得到真实函数地址
  Dobby 只接管每个 target 一次
  hook 后逻辑全部在 native 层执行

C# 层：
  负责 UI、配置、导入/重写、状态展示和受控 managed self-render
  不进入普通游戏 hook 热路径
```

## 总原则

### Translator 不执行 MOD 代码

Translator 禁止执行 PC MOD 代码，只允许静态读取：

- `Info.json`
- `JAModInfo.json`
- DLL metadata
- custom attribute blob
- method signature
- IL method body
- MOD 自带资源文件

禁止：

- `Assembly.Load` / `AssemblyLoadContext.LoadFromAssemblyPath` 加载 MOD 主 DLL
- `Activator.CreateInstance` MOD 类型
- 调用 `Bootstrap` / `Setup` / `OnEnable`
- 触发静态构造函数
- 执行 MOD 依赖库里的任意托管代码

这意味着“动态 PATCH 支持”不能靠真正运行 `Patcher.AddPatch(...)` 得到结果，而要靠静态 IL 分析和受限解释。

这条禁令限定的是导入期 translator 和 PATCH 发现链，不代表整个兼容层永久禁止执行 MOD。另一路经过完整重写、proxy closure、resource/session 和 UnityMain 门禁的 `ManagedSelfRender` 可以执行 MOD Entry/HUD 生命周期；它不能反向成为 PATCH 扫描 oracle，也不能从任意 Hook 线程直接执行托管 callback。

### Hook 只在 native 层执行

实际 hook、dispatcher、callback chain、original 调用、返回值修改全部在 native 层完成。

C# 可以做：

- 导入 MOD
- 展示翻译日志
- 展示支持/不支持项
- 写配置
- 开关某个 compiled bundle
- 在 UnityMain 执行通过门禁的 rewritten MOD HUD 生命周期

C# 不做：

- native hook callback
- 高频事件处理
- 从输入/render/audio/未知 Hook 线程同步进入 CoreCLR

### 不允许运行期 unhook

目标函数入口一旦被 HookManager 接管，运行期不执行 `DobbyDestroy`。

关闭 MOD 时只做：

```text
dispatcher.after_op_mask = 0
dispatcher.enabled = false
dispatcher.original = preserved
```

目标对应的 dispatcher index、target key、ABI 和 continuation 在进程内永久绑定。再次加载同一 target 复用该绑定，不重复向 HookBroker 注册同一 layer；不同 target 不能占用这个 index。彻底停止、卸载或释放容量通过重启进程完成。重启后如果没有 bundle 需要该 target，则不再安装对应 hook slot。

## Hook Slot 模型

每个真实目标函数地址对应一个 Hook Slot：

```text
target function entry
        |
        | HookBroker owns the entry
        v
stable native detour / universal arm64 bridge
        |
        v
Hook Slot dispatcher
        |
        +-- prefix rules
        +-- original trampoline
        +-- postfix rules
        +-- replace / return patch rules
```

规则：

- 同一个 target 只允许 Dobby hook 一次。
- hook 入口所有权属于 HookManager，不属于某个 MOD。
- MOD 只拥有 slot 内的 rule/callback。
- 关闭 MOD 只跳过该 MOD 的 rule，不恢复入口。
- 如果多个 bundle 请求同一 target，合并到同一个 Hook Slot。

## 零适配自动 hook 的目标边界

目标是“任意函数零适配自动 hook”，但第一版限定运行平台：

```text
支持：
  Android arm64-v8a
  AArch64 ABI
  IL2CPP/native C ABI

不支持：
  x86/x64
  armeabi-v7a
  Java method hook
  managed Mono hook
  varargs 函数
```

实现重点是 native AArch64 universal bridge，而不是 C# delegate adapter。

bridge 需要捕获：

- `x0-x30`
- `sp`
- `pc/lr`
- 必要的 `v0-v7` / SIMD 返回寄存器
- 原函数返回后的 `x0/x1` 或 `v0/v1`

HookContext 示例：

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
} XHookContext;
```

dispatcher 通过 `HookContext` 读写参数和返回值。对 IL2CPP 方法，`x0` 通常是 `this`，后续参数按 AArch64 ABI 分布。

## 导入与缓存目录

MOD 文件放在 app 私有目录：

```text
/sdcard/Android/data/<package>/files/mods/
```

建议结构：

```text
mods/
  imported/
    <mod_id>/
      original/
      manifest.json
      source_hash.txt

  compiled/
    <mod_id>/
      <cache_key>/
        ui_recipe.bin
        hook_rules.json
        compile_report.json
        unsupported.json
        assets/
```

`cache_key` 应至少包含：

```text
hash(
  mod_source_hash,
  translator_version,
  game_version,
  il2cpp_build_id,
  hook_rule_format_version
)
```

缓存命中时不重新翻译。

## 编译产物

采用双格式：

- `hook_rules.json`：人类可读，用于 UI、日志、调试和 diff。
- `ui_recipe.bin`：native 运行时加载，避免解析 JSON 进入热路径。

JSON 示例：

```json
{
  "formatVersion": 1,
  "modId": "JipperResourcePack",
  "sourceHash": "...",
  "targets": [
    {
      "assembly": "Assembly-CSharp",
      "class": "scrMarginTracker",
      "method": "CalculatePercentAcc",
      "paramCount": 0,
      "stage": "AfterOriginal",
      "rules": [
        {
          "op": "PublishSnapshot",
          "fields": ["percentAcc", "percentXAcc"]
        }
      ]
    }
  ],
  "unsupported": [
    {
      "callback": "JipperResourcePack.Main.OnChangeState",
      "reason": "Enum argument bridge is not implemented"
    }
  ]
}
```

二进制头示例：

```c
#define XHOOK_BUNDLE_MAGIC 0x4B4F4858u /* XHOK */

typedef struct XHookBundleHeader {
    uint32_t magic;
    uint16_t version;
    uint16_t target_count;
    uint64_t source_hash_hi;
    uint64_t source_hash_lo;
} XHookBundleHeader;
```

## 动态 PATCH 的定义

动态 PATCH 指 patch descriptor 不是直接通过方法上的 `[JAPatch]` attribute 静态声明，而是在 MOD 代码里构造出来，例如：

```csharp
patcher.AddPatch(callback, new JAPatchAttribute(targetType, targetMethod, PatchType.Postfix, false));
```

或：

```csharp
if (ADOBase.version >= 141) {
    patcher.AddPatch(GetPercentAccR141, new JAPatchAttribute(GetPercentAcc, PatchType.Replace, false));
}
```

这类 patch 不能靠简单扫描 custom attribute 得到。

## 动态 PATCH 支持策略

### 第一层：直接 attribute 扫描

支持方法上直接声明：

```csharp
[JAPatch(typeof(scnGame), "Play", PatchType.Postfix, false)]
private static void OnGameStart1(int seqID) { ... }
```

Translator 读取 custom attribute blob，生成 patch descriptor。

这是最稳定的路径。

### 第二层：受限 IL pattern matcher

支持可静态识别的 `AddPatch` 模式，不执行代码。

目标模式：

```text
ldftn callback
newobj delegate
ldtoken target type / ldstr target type
ldstr target method
ldc.i4 patch type
newobj JAPatchAttribute
callvirt JAPatcher.AddPatch
```

Translator 做 IL 扫描，识别：

- callback method token
- target type
- target method
- patch type
- min/max version 条件
- tryingCatch 等 attribute 参数

对 JipperResourcePack 的 `VersionSafe.Setup()`，这层应能静态恢复 `R141` / `R136` 两组 patch，并根据当前 Android 目标游戏版本选择可用分支。

### 第三层：受限抽象解释器

为了支持更复杂的动态注册，Translator 可以实现一个只读、无副作用的 IL 抽象解释器。

它不是 CLR，不执行真实 MOD 代码，只在抽象域里推导 patch descriptor。

允许的操作：

- 常量加载
- `typeof`
- `nameof` 编译后的字符串
- delegate method token
- `new JAPatchAttribute(...)`
- `JAPatcher.AddPatch(...)`
- 局部变量赋值/读取
- 简单数组初始化
- 简单 `if` 分支
- 基于已知常量的版本判断

禁止或降级：

- 调用未知方法
- 反射查找目标
- 从文件/网络/系统 API 读取 patch 信息
- 根据用户设置动态决定 target
- 循环次数无法静态确定
- 捕获运行时对象状态

抽象解释器输出的是所有可证明 patch descriptor。无法证明的路径写入 `unsupported.json`。

### 第四层：兼容配方

对于高价值 MOD，可以增加显式 recipe：

```text
recipes/
  JipperResourcePack.json
```

recipe 用于补充静态分析无法可靠恢复但我们已人工确认的行为。

recipe 不能绕过安全边界：

- 不能执行 MOD 代码
- 不能直接写任意 native 地址
- 必须生成同样的 hook rules
- 必须进入 compile report

## 版本条件处理

PC MOD 经常按游戏版本注册不同 patch，例如 R136/R141。

Translator 需要输入目标游戏信息：

```text
game_version = 3.1.2
game_revision = r143
il2cpp_build_id = ...
platform = android-arm64
```

遇到版本条件时：

- 条件能确定：只保留目标版本分支。
- 条件不能确定：保留多个候选，但标记 `requiresRuntimeVersionGate`。
- 候选之间 target/语义冲突：编译失败并要求 recipe 或人工适配。

## Rule 语义

基础 rule 类型：

```text
BeforeOriginal
AfterOriginal
ReplaceOriginal
SkipOriginal
PatchReturn
PublishState
ResourceRedirect
```

第一版建议优先支持：

- `AfterOriginal`
- `PublishState`
- `ResourceRedirect`
- `PatchReturn` 的简单整数/浮点返回值

谨慎支持：

- `BeforeOriginal`
- `SkipOriginal`
- `ReplaceOriginal`

因为这些会改变官方流程，必须按 target 白名单审计。

## Callback 逻辑翻译

动态 PATCH 支持分成两个层次：

```text
A. 恢复 patch 注册关系
   target type / target method / patch type / callback / version gate

B. 复刻 callback 方法体逻辑
   把 callback IL 翻译成 native rule bytecode
```

长期目标包含 B，但不做通用 IL AOT。第一版 callback 逻辑只支持受限 IL 子集，无法证明安全或无法翻译的 callback 进入 `unsupported.json`。

当前首批实现已经落地：

- callback 用完整参数签名定位，避免同名重载歧义。
- 通用 `fixed-op-v2` catalog 不按 MOD 身份匹配；JipperResourcePack r143 当前有 31 条 callback 通过翻译，并生成 32 条 runtime rule。
- 其中 telemetry/judgement 的 11 条是无 back-edge 的直接领域映射；accuracy、margin hit、margin reset 3 条使用严格的单玩家投影。
- 单玩家投影会验证完整 opcode 序列、字段/调用集合和唯一 back-edge，再把 coop index 固定收敛为 `player 0`；这不构成多人模式支持。
- ResourceChanger 作为 descriptor-only 领域映射执行已审计 fixed-op：before-original skip、`UnityEngine.Color` 参数覆盖、星球颜色、Beat 轨道颜色、Logo clone/着色和恢复链均在 native/UnityMain 侧执行。编辑器兔子的 `Auto` Sprite 只从 MOD 自带 bundle 生成的 Resource IR 经 VirtualBundle 重建并按 owner/session generation 发布，不读取 MOD/runtime PNG，也不调用 `TextureManager.LoadNewSprite`。`OttoUpdate` 映射通过审计后会派生 `OttoBlink` after-original companion，专门覆盖击打时官方方法对 `autoImage.sprite` 的再次写入；该 companion 复用 `ResourceApplyEditorRabbit`，不扩大为全局 `Image.set_sprite` Hook。PC callback IL 不在 Android 执行；这仍不等于任意资源替换 MOD 的通用解释器。

### 受限 IL 子集

允许：

- 常量加载。
- 参数读取。
- primitive static state 读取/写入。
- 白名单 IL2CPP 字段读取。
- recipe 授权的 IL2CPP 字段写入。
- 简单算术和比较。
- `if` / `else` / `switch`。
- 运行时循环，但必须受 instruction budget 限制。
- 调用内置 domain/native callable。
- 调用白名单 IL2CPP method。
- 递归内联可证明的 static pure helper。

禁止：

- 通用 IL AOT。
- 反射。
- 任意 virtual/interface call。
- 任意未知 method call。
- 任意实例方法内联。
- 托管对象分配。
- LINQ。
- coroutine。
- delegate invocation。
- 文件、网络、线程、sleep、join 等阻塞或外部副作用。
- 动态堆分配、boxing、闭包。

### Pure Helper 内联

允许递归内联 static pure helper，但必须满足：

```text
1. static
2. 非 virtual / 非 interface call
3. 不访问文件、网络、线程、时间、随机数
4. 不 new 复杂对象
5. 不写非白名单静态字段
6. 不调用未知方法
7. 不依赖 C# exception flow
8. 方法体 IL 大小低于阈值
9. 递归深度低于阈值
```

建议阈值：

```text
max_inline_depth = 4
max_inline_il_bytes_per_method = 512
max_total_inlined_il_bytes = 4096
```

实例方法不做通用内联。类似 `Overlay.Instance.Hide()` 的调用必须命中 domain mapping，例如翻译成 `CALL_NATIVE OVERLAY_HIDE`。

### Register-like 领域 Bytecode

受限 IL 不生成 native so，而是编译成 HookManager VM 的 native rule bytecode。

bytecode 采用领域专用指令集，并尽量贴近 AArch64 寄存器模型，不使用通用 stack VM。

虚拟寄存器建议：

```text
r0-r31    64-bit integer / pointer
f0-f15    float / double
b0-b15    predicate / bool

ctx.x0-x7 -> r0-r7
ctx.v0-v7 -> f0-f7
integer/pointer return -> r0
float/double return -> f0
```

指令风格：

```text
MOV_R dst, src
LOAD_ARG_R dst, argIndex
LOAD_CONST_I64 dst, constIndex
LOAD_FIELD_F32 dst, objectReg, fieldId
STORE_FIELD_F32 objectReg, fieldId, src
CMP_EQ_I64 pred, lhs, rhs
BR relOffset
BR_IF pred, relOffset
CALL_NATIVE funcId, argBase, argCount
CALL_IL2CPP methodId, argBase, argCount, retReg
SET_RETURN_R src
SET_RETURN_F src
SKIP_ORIGINAL
RET
```

领域 op 示例：

```text
OVERLAY_SHOW
OVERLAY_HIDE
OVERLAY_DEATH
OVERLAY_CLEAR
PUBLISH_SNAPSHOT
RESOURCE_REDIRECT
KEYVIEWER_EVENT
```

运行时加载 bundle 后，所有字符串和 metadata key 必须解析成整数 ID：

```text
fieldId  -> resolved field offset / accessor
methodId -> resolved IL2CPP FunctionPtr + call wrapper
funcId   -> HookManager builtin native callable
```

热路径禁止 JSON 解析、字符串比较、反射和 managed call。

### Native Callable

bytecode 允许调用 native function pointer，但来源必须是 HookManager 内置 callable table。

允许：

```text
CALL_NATIVE funcId, argBase, argCount
```

不允许：

```text
CALL_ABSOLUTE 0x...
CALL_REG r5
dlopen MOD 自带 so
从任意系统库解析符号
```

PC MOD 本身是 PC 托管 MOD，正常不会自带 Android `.so`。`CALL_NATIVE` 只是为了让 bytecode 高效调用我们提供的底层能力。

示例 callable：

```text
0x0001 overlay_show
0x0002 overlay_hide
0x0003 overlay_death
0x0004 overlay_clear
0x0100 read_il2cpp_field_f32
0x0101 write_il2cpp_field_i32
0x0200 resource_redirect
0x0300 publish_snapshot
0x0400 log_rate_limited
```

### 未知调用策略

遇到 method call 时按顺序处理：

```text
1. 命中 builtin/domain mapping -> 翻译为 CALL_NATIVE
2. 命中白名单 IL2CPP method -> 翻译为 CALL_IL2CPP
3. 命中同程序集 static pure helper 且可证明 -> 递归内联
4. 其它 -> UnsupportedUnknownCall
```

不猜测未知调用无副作用，不自动递归翻译任意对象模型。

### 字段与状态

PC MOD 自己的静态字段只支持 primitive state：

```text
bool / int / float / enum / string 常量
fixed-size primitive array
```

这些字段编译进 bundle state，由 HookManager 保存。

禁止：

```text
object
List / Dictionary
UnityEngine.Object
复杂引用类型
需要 static ctor 初始化的字段
```

IL2CPP 字段访问必须走 metadata id：

```text
READ_IL2CPP_FIELD objectReg, fieldId
WRITE_IL2CPP_FIELD objectReg, fieldId, valueReg
READ_IL2CPP_STATIC_FIELD fieldId
WRITE_IL2CPP_STATIC_FIELD fieldId, valueReg
```

读字段允许白名单。写字段默认禁止，必须 recipe 或规则显式授权。

### 运行时循环与 Budget

支持运行时循环。bytecode 只需要合法 `BR` / `BR_IF` 即可形成循环。

所有 HookInvocation 必须带 instruction budget：

```text
default_budget = 1024 instructions
high_frequency_hook_budget = 256
low_frequency_hook_budget = 4096
```

每执行一条 bytecode 指令扣 1。budget 耗尽时：

```text
abort current rule
生成 VM exception
记录 fault counter
跳过本次 callback 后继续官方流程
连续超限 N 次后自动 disable rule
UI 标红显示
```

建议：

```text
budget_exhausted >= 3 within session -> disable rule
```

Verifier 必须：

- 构建 CFG。
- 校验跳转目标合法。
- 禁止跳入指令中间。
- 校验寄存器类型在 loop back-edge 上一致。
- 限制最大 bytecode 大小。

### VM Exception 与 UI/LOGCAT 透传

第一版实现 VM 级 exception，不复刻完整 CLR exception semantics。

运行时错误生成 `XHookException`：

```c
typedef enum {
    XHOOK_EX_NONE = 0,
    XHOOK_EX_BUDGET_EXHAUSTED,
    XHOOK_EX_NULL_DEREF,
    XHOOK_EX_TYPE_MISMATCH,
    XHOOK_EX_INVALID_FIELD,
    XHOOK_EX_NATIVE_CALL_FAILED,
    XHOOK_EX_DIVIDE_BY_ZERO,
    XHOOK_EX_OOB,
    XHOOK_EX_UNSUPPORTED_OPCODE
} XHookExceptionCode;

typedef struct XHookException {
    XHookExceptionCode code;
    uint32_t rule_id;
    uint32_t pc;
    uint32_t opcode;
    char message[256];
} XHookException;
```

处理策略：

```text
当前 rule abort
LOGCAT 输出
写入 native fault ring buffer
UI 拉取并展示
达到阈值自动 disable rule
不影响 original/game flow
```

第一版不支持 C# `try/catch/finally` 语义。后续可以增加受限 EH block，但不能阻塞 VM exception 的观测链路。

### 内存与字符串

第一版禁止 hook bytecode 动态堆分配。

禁止：

```text
newobj
newarr runtime length
List / Dictionary / StringBuilder
string.Format
LINQ
boxing
closure
delegate allocation
```

允许：

```text
编译期常量字符串表
编译期常量数组
fixed-size primitive state array
```

运行期日志使用 string id 和寄存器参数，不拼接动态字符串：

```text
LOG_STRING stringId
LOG_I64 stringId, reg
```

### 对象模型

PC MOD 复杂对象不通用复刻。

```text
不支持:
  new Overlay()
  通用 Overlay.Instance
  任意 SomeClass.Method()
```

必须通过 domain mapping 转成我们自己的 native domain object 或 callable：

```text
JipperResourcePack.Overlay.Instance.Hide()
  -> CALL_NATIVE OVERLAY_HIDE
```

IL2CPP 游戏对象只作为受控 `nint` 指针处理：

```text
允许:
  null check
  读白名单字段
  recipe 授权写白名单字段
  调用白名单 IL2CPP method

禁止:
  当作 PC shim object
  长期保存裸指针且不校验生命周期
```

### IL2CPP Method 调用

允许 bytecode 调用白名单 IL2CPP 方法，但必须通过 metadata methodId，不允许绝对地址。

```text
CALL_IL2CPP methodId, argRegs, retReg
```

`methodId` 编译时记录：

```text
assembly
namespace
class
method
paramCount / paramTypes
returnType
static / instance
```

运行时解析：

```text
metadata resolver -> MethodInfo -> FunctionPtr -> call wrapper
```

默认允许 getter、setter、简单状态查询。改变游戏状态的方法必须 recipe 授权。

### Capability Manifest

每个 compiled bundle 必须带 capability manifest，导入 UI 展示并要求用户确认高风险能力。

能力分级：

```text
Low:
  READ_STATE
  AFTER_ORIGINAL_OBSERVE
  LOG
  UI_OVERLAY

Medium:
  RESOURCE_REDIRECT
  READ_IL2CPP_FIELD
  CALL_IL2CPP_GETTER

High:
  WRITE_IL2CPP_FIELD
  CALL_IL2CPP_MUTATOR
  PATCH_RETURN
  SKIP_ORIGINAL
  REPLACE_ORIGINAL
  INPUT_INJECTION
```

策略：

```text
Low 自动允许
Medium 默认勾选但可关闭
High 必须用户手动确认
```

recipe 也必须声明能力，不能静默提升权限。

### 部分启用

翻译失败或权限未批准时允许部分启用，但必须明确标记“部分兼容”。

```text
supported + permission allowed -> enabled
unsupported -> disabled
high-risk not approved -> disabled
```

UI 必须按 feature/rule 级展示：

- 支持项。
- 不支持项。
- 被权限关闭项。
- 是否影响主功能。

## 动态 PATCH 的不支持判定

以下场景第一版标记为不支持：

```text
UnsupportedDynamicRegistration
UnsupportedReflectionPatch
UnsupportedUnknownMethodCall
UnsupportedRuntimeConfigDependentPatch
UnsupportedTranspiler
UnsupportedSignature
UnsupportedTarget
UnsupportedCallbackBody
UnsupportedUnknownCall
UnsupportedInlineDepth
UnsupportedHelperTooLarge
UnsupportedLoopType
UnsupportedExceptionFlow
UnsupportedAllocation
UnsupportedCapability
```

UI 应展示：

- 哪个 callback 不支持
- 原因
- 是否影响 MOD 主功能
- 是否有 recipe 可补

## UI 导入过程

导入时 UI 展示编译/翻译过程：

```text
1. 复制 MOD 到 app 私有目录
2. 计算 source hash
3. 检查缓存
4. 读取 manifest
5. 扫描 DLL metadata
6. 扫描 direct attributes
7. 分析 dynamic AddPatch
8. 翻译 callback 受限 IL
9. 匹配 native rule / callable / IL2CPP 白名单
10. 生成 capability manifest
11. 生成 hook_rules.json
12. 生成 ui_recipe.bin
13. 写 compile_report.json / unsupported.json
14. 展示能力和部分兼容状态
15. 启用或等待用户确认启用
```

UI 不能阻塞主线程。translator 可以跑后台线程，进度通过状态文件或事件上报。

## 性能策略

翻译性能靠缓存和少读，而不是一开始就把 translator 写成 native。

策略：

- source hash 缓存，命中直接加载 compiled bundle。
- 只读 metadata，不执行 DLL。
- 快速扫描和深度编译分层。
- 文件 hash、资源清单、DLL metadata 可并行。
- 最后写 compiled 目录时使用临时目录 + 原子 rename。
- native 运行时优先加载 `ui_recipe.bin`；迁移期允许显式回退审计 JSON。

目标：

```text
缓存命中：< 100ms
普通 MOD 首次快速扫描：< 1s
JipperResourcePack 首次完整编译：1-3s 可接受
超大资源包：允许更久，但必须可取消并显示进度
```

## 与当前实现的关系

当前已有的 `PcCompatManagedLoader` / shim / probe 仍然有价值，但定位应调整：

- 作为开发期对照工具。
- 用于理解 PC MOD 的 UMM/JAMod/JAPatch 行为。
- 用于测试 translator 恢复出的 patch descriptor 是否与“执行 setup 得到的 snapshot”一致。
- 用作开发期 oracle：执行 `CompatSetup()` 得到 `setup_snapshot.json`，再和静态 translator 输出的 `translated_patch_descriptors.json` 对照。

静态 PATCH/recipe 发布路径不应依赖执行 PC MOD setup。Managed self-render 是独立受控后端，可以执行重写后的 setup/lifecycle，但不能作为 translator 获得 patch descriptor 的必要条件。

开发期 oracle 对照项：

```text
target type
target method
patch type
callback type
callback method
version gate
flags
```

静态 PATCH translator 的发布导入阶段仍然禁止执行 MOD 代码，只能静态翻译。受控 managed self-render 在改写产物发布后进入独立运行阶段。

当前 `PcCompatDobbyBridge` 已是托管控制面桥，不再是游戏方法 Hook 的过渡实现：

- 它同步经过验证的 runtime bundle、诊断请求和低频 snapshot provider。
- metadata resolve、安装计划、永久 HookSlot、HookBroker 和 Dobby 首层入口均由 native ModManager 持有。
- 它不得缓存 target 地址、安装 managed detour 或拥有 original continuation。

## 分阶段落地

### Phase 0：文档和模型冻结

- 固化 Hook Slot 生命周期。
- 固化导入目录和缓存 key。
- 固化 translator 禁止执行 MOD 代码。
- 固化 dynamic PATCH 支持层级。

### Phase 1：静态 Patch Scanner

- 读取 `Info.json` / `JAModInfo.json`。
- 用 dnlib/Cecil 扫描 `[JAPatch]` attribute。
- 输出 `hook_rules.json` 和 `compile_report.json`。
- 与当前 probe snapshot 做对照。

当前状态：`JAPatchAttribute` scanner、版本门禁和审计 JSON 已完成；Harmony target 聚合与 scanner -> runtime rule 的自动映射仍待完成。JipperResourcePack 1.4.8.2 已恢复 40 条 direct attribute，r143 生效 32 条。

### Phase 2：AddPatch IL Pattern

- 支持 `new JAPatchAttribute + AddPatch` 模式。
- 覆盖 Jipper `VersionSafe.Setup()`。
- 支持简单版本分支。

当前状态：已完成 `AddPatch(Delegate, JAPatchAttribute)` pattern、C# delegate cache 识别和 `VersionControl.releaseNumber` 简单 CFG 可达性分析。Jipper `VersionSafe.Setup()` 的 R136/R141 共 18 条均已恢复；r143 激活 9 条 R141 ReversePatch，结果与 managed oracle 一致。

### Phase 3：Callback 受限 IL Translator

- 支持 callback 方法体的受限 IL 子集。
- 支持 static pure helper 递归内联。
- 支持领域 bytecode IR。
- 输出 capability manifest。
- 输出 unsupported callback body reason。

### Phase 4：Native HookManager

- 实现 native slot registry。
- 接入 HookBroker 永久 layer 注册与 continuation。
- 实现 arm64 universal bridge。
- 加载 `ui_recipe.bin`。
- 支持 enable/disable rule。

### Phase 5：Rule VM 执行

- 实现 register-like VM。
- 支持 `AfterOriginal` / `PublishState`。
- 支持资源重定向类 rule。
- 支持简单返回值 patch。
- 支持 instruction budget。
- 支持 VM exception LOGCAT/UI 透传。

### Phase 6：动态注册抽象解释器

- 支持局部变量、简单数组、简单分支。
- 支持运行时循环在 bytecode 层执行，由 budget 兜底。
- 输出不支持路径。
- 引入 recipe 补丁机制。

当前状态：已提前落地一个只读有限子集，支持 callback `MethodInfo` 局部变量、显式字符串数组、静态长度 foreach、`TryingCatch` 覆盖和 `_isAfterR<n>` 版本化目标选择。Jipper `ResourceChanger.Patch()` 的 16 条版本化 descriptor 已全部恢复，r143 激活 8 条 `PlanetRenderer` Prefix，并与 managed oracle 一致。运行时循环、未知反射和一般抽象解释仍未实现。

## 开放问题

1. `ui_recipe.bin` v1 已固定 header/section/string/target/rule/object graph/component operations/lifecycle/bytecode/resources 布局并接通执行链；resources 已使用 32-byte binding record，diagnostics 段仍待冻结。
2. universal bridge 是否保存全部 SIMD 寄存器，还是先保存 `v0-v7`，需要按实际目标函数风险决定。
3. `ReplaceOriginal` / `SkipOriginal` 是否默认禁用，只允许 recipe 白名单开启。
4. recipe 是否允许用户侧安装，还是只允许我们随版本发布。
5. `CALL_IL2CPP` 的 method whitelist 和 mutator 授权边界需要逐 target 审计。
6. VM instruction budget 的默认值需要真机压测后确定。
7. VM exception ring buffer 的容量和 UI 展示频率需要按性能测试调整。
8. 部分启用时 feature 按已翻译 rule 的领域与 capability 自动归组，禁止按 MOD ID 建专属分支。
