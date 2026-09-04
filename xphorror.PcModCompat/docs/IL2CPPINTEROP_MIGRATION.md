# Il2CppInterop 迁移目标与方法

## 文档状态

本文定义 `xphorror.PcModCompat` 向 forked `Il2CppInterop` 迁移时必须遵守的目标、边界、实施方法和验收标准。

这次迁移不是替换 Native HookManager，也不是把 Harmony 搬到 Android。迁移对象是当前手写的 IL2CPP 对象互操作、代理程序集和 PC MOD 托管绑定层。

截至 2026-07-16，generated proxy、Android slim Runtime、MOD IL 重写和无 ClassInjector managed-component bridge 的基础迁移已经落地。本文后半保留阶段历史用于解释来源；新增的真实 IL2CPP 类型身份属于迁移后的受控扩展，不得通过简单打开上游 `ClassInjector` 开关实现。

## 最终目标

迁移完成后，PC MOD 兼容层应形成以下结构：

```text
PC MOD DLL
  -> 离线扫描与受限 IL 翻译
  -> 重写后的 Android MOD DLL
  -> Il2CppInterop generated proxy assemblies
  -> Il2CppInterop Runtime 对象/字段/方法互操作
  -> xphorror native rule bundle
  -> Native HookManager permanent slot
  -> HookBroker
  -> Dobby
  -> Android IL2CPP runtime
```

核心结果：

- 用 `Il2CppInterop` generated proxies 替换长期维护成本高的手写游戏 stub 和大部分手写对象封送。
- PC MOD 的普通游戏对象访问通过代理程序集映射到 Android IL2CPP 对象。
- PATCH 目标在运行时通过 IL2CPP metadata 精确解析。
- 所有实际游戏函数 Hook 仍只在 native 层安装。
- 同一函数只保留一个永久 Hook slot，多个 MOD 共享 rule chain。
- MOD 禁用只关闭 rule，不执行运行期 unhook。
- 翻译后的 MOD 和代理程序集可缓存，正常启动不重复执行完整翻译。
- 迁移完成前只做本机生成、重写和回归；完整链路通过后才进入实机验证。

## 不可破坏的架构契约

### Hook 所有权

`Il2CppInterop` 和单个 MOD 都不拥有 Hook 安装权。Hook 安装权属于 ModManager。

唯一允许的安装链路是：

```text
symbolic target descriptor
  -> native runtime metadata resolver
  -> permanent HookSlot
  -> HookBroker
  -> DobbyHook
```

上面是普通游戏方法 Hook 链。未来类型注入需要的 IL2CPP 内部 detour 也必须进入同一个 HookBroker，但使用 ModManager 保留的 infrastructure layer，不进入普通 MOD rule chain，也不占用当前 32 个 fixed dispatcher slot：

```text
verified injection infrastructure target
  -> ModManager native resolver
  -> infrastructure hook registry
  -> HookBroker
  -> permanent detour + continuation
```

硬约束：

- HarmonySupport 不进入 Android 运行时。
- `Il2CppInterop` 的 detour API 不得直接调用 Dobby。
- 当前 Runtime 必须配置拒绝所有 detour 的 `IDetourProvider`；只有受控注入扩展落地后，才能替换为只接受 ModManager 注入基础设施目标的 brokered provider。
- `ClassInjector` 当前禁用。任何后续启用都必须经 ModManager `InjectionTypeRegistry`、capability gate 和 schema/ABI 校验，MOD 不得直接调用 mutating API。
- xref scanner 第一阶段禁用。
- C# 可负责 UI、扫描、翻译、配置、managed self-render 和低频状态桥，但普通游戏 Hook 热路径不进入 CoreCLR。注入类型的 native message thunk 属于单独审计的互操作边界，不能借此开放任意托管 patch callback。
- native slot、dispatcher index、original trampoline 在进程生命周期内永久保留。
- 禁止运行时 `DobbyDestroy` 或其它形式的入口恢复。

### 地址解析

运行时禁止使用任何固定地址：

- 禁止硬编码 VA。
- 禁止硬编码 RVA。
- 禁止 `moduleBase + RVA` 定位游戏方法。
- 禁止使用 ELF file offset 定位运行时方法。
- 禁止把离线 dump 地址写入 `hook_rules.json`、代理程序集、MOD 缓存或 native bytecode。
- 禁止把版本固定地址作为 metadata 解析失败后的 fallback。

生产运行时的唯一权威是当前进程的 IL2CPP metadata：

```text
il2cpp_domain_get
  -> il2cpp_domain_get_assemblies
  -> il2cpp_assembly_get_image
  -> il2cpp_class_from_name
  -> il2cpp_class_get_methods
  -> exact signature match
  -> MethodInfo
  -> current runtime method pointer
```

`MethodInfo` 只能在完整 metadata 身份匹配成功后使用。方法入口最终来自当前进程中的 `MethodInfo`，不能来自离线二进制地址换算。

### `dump.cs` 和 ELF 的用途

Android `dump.cs` 仍是离线 API 清单的重要输入，所有文本按 UTF-8 读取。它可以用于：

- 建立程序集、命名空间、类型、方法和字段清单。
- 与 PC/AssetRipper 程序集对照签名。
- 检查 Android 与 PC 版本的类型差异。
- 生成裁剪代理程序集需要的类型依赖闭包。
- 离线审计某个方法的 RVA、VA 和 ELF file offset 是否对应同一函数。
- 在测试报告中证明符号映射没有错位。

它不能用于：

- 生成运行时 Hook 地址。
- 覆盖 metadata resolver 的结果。
- 在解析失败时强制选择某个方法。
- 给生产构建写入固定 `AddressAttribute` 并据此调用或 Hook。

离线工具可以输出 RVA/VA 作为审计列，但该列必须标记为 `audit_only`，不得成为运行时输入。

## 方法身份模型

每个游戏方法使用符号身份描述，而不是地址描述：

```text
assembly name
namespace
declaring type
method name
static / instance
generic arity
return type
ordered parameter types
by-ref / pointer / array modifiers
```

建议的运行时 target schema：

```json
{
  "assemblyName": "Assembly-CSharp",
  "namespace": "",
  "typeName": "scrMarginTracker",
  "methodName": "CalculatePercentAcc",
  "isStatic": false,
  "genericArity": 0,
  "returnType": "System.Void",
  "parameterTypes": []
}
```

解析规则：

1. 程序集名统一处理 `.dll` 后缀和大小写，不跨程序集猜测。
2. 类型必须按 namespace、外层类型和嵌套类型精确匹配。
3. 方法必须同时匹配名称、static 身份、泛型参数数、返回类型和有序参数类型。
4. 参数类型必须保留 `ref`、指针、数组秩和泛型实例信息。
5. 找不到候选时失败关闭。
6. 出现多个候选时失败关闭。
7. 禁止退化为“同名且参数数量相同就取第一个”。
8. 禁止 `Il2CppInterop.GetIl2CppMethod` 当前的随机同名候选回退进入严格模式。
9. metadata token 只能作为同版本、同 fingerprint 下的可验证加速索引，不能单独作为跨构建身份。
10. 即使 token 命中，也必须复核完整签名。

现有 `abiKind` 继续负责 dispatcher 的 AArch64 调用约定验证，但不能替代完整方法身份。完整签名决定“是哪一个方法”，`abiKind` 决定“当前 dispatcher 是否能安全调用它”。

## Il2CppInterop 的职责

### 迁移后由它负责

- 生成 Android IL2CPP 对应的托管代理程序集。
- 把 IL2CPP object pointer 包装为 CoreCLR 可使用的代理对象。
- 通过运行时 metadata 查找 class、field 和 method。
- 执行普通 IL2CPP 方法调用。
- 处理字符串、数组、枚举、引用类型和值类型的封送。
- 提供生成代理中的字段访问器和属性访问器。
- 为 PC MOD 重写后的类型引用提供真实 Android 绑定目标。
- 统一 Unity Core、Unity UI、TextMeshPro 和 `Assembly-CSharp` 的托管视图。

### 不由它负责

- 安装 Harmony patch。
- 安装 Dobby hook。
- 管理多 MOD Hook 顺序。
- 管理 original trampoline。
- 动态 unhook。
- 决定 MOD capability。
- 执行未验证的任意 IL callback。
- 绕过 native rule VM 直接修改游戏判定或输入。

## ModManager 的 IL2CPP 与注入所有权

迁移后的进程级边界固定为：

```text
PcCompat importer/rewriter
  -> HookPlan / RuntimeQueryPlan / TypeInjectionPlan
  -> ModManager Il2CppRuntimeHost
       +-- domain/thread attach
       +-- export + metadata resolver/cache
       +-- generated proxy bootstrap
       +-- HookSlotRegistry / HookBroker
       +-- InjectionTypeRegistry (planned)
       +-- UnityMain phase dispatcher
```

PcCompat 决定 MOD 语义和是否需要某项能力；ModManager 验证并实施所有会改变 IL2CPP 全局状态的动作。普通 generated proxy 调用不需要逐次绕经管理 RPC，但只能在已验证线程和 session 中执行。

`InjectionTypeRegistry` 与 `HookSlotRegistry` 并列，不复用普通 rule slot。建议类型键至少包含：

```text
owner mod id
source assembly MVID/hash
full type name
base type identity
ordered interfaces
field/method schema hash
target Unity/IL2CPP fingerprint
```

状态固定为：

```text
PendingValidation -> Validated -> Registered
                  -> Rejected / Faulted
```

同一完整键重复请求必须幂等；同一 IL2CPP image/namespace/name 对应不同 schema 时失败关闭。注入产生的 `Il2CppClass`、vtable、MethodInfo、thunk、delegate、GC root 和对应 MOD `AssemblyLoadContext` 在进程内永久保活。关闭 MOD 只通过 owner/session gate 停止 callback；不删除 class、不释放 trampoline、不尝试卸载承载注入类型的 ALC。彻底卸载依赖进程重启。

上游 ClassInjector 在 Unity 6 上需要若干内部 hook。Android slim 当前禁用 xref/signature scanner，因此不能直接设置 `EnableClassInjection=true`。受控扩展必须由 ModManager 为目标 Unity `6000.3.10f1` 动态定位并验证这些基础设施目标：优先从导出 wrapper 和当前进程指令关系解析，不能解析的内部目标失败关闭；禁止固定 RVA/VA、离线 dump fallback 或让上游 scanner 绕过 native resolver。

Class injection 只解决真实 `Il2CppClass/Component` 身份和 native message 入口，不自动获得 Unity 原生序列化、MonoScript、type tree 或 Inspector。自有字段持久化/设置 UI 仍是独立能力；需要 Unity 原生 prefab 序列化时必须另行证明。

## 替换范围

| 当前实现 | 迁移目标 | 处理方式 |
| --- | --- | --- |
| `UnityResolve` 手写对象/方法封装 | generated proxy + targeted native resolver | 已删除源码和生产依赖，不保留回退 |
| `xphorror.PcModCompat/shims/Assembly-CSharp` 手写游戏 stub | Android 代理程序集 | 已隔离到 `out/legacy_shims`，不进入 runtime assets |
| 手写 Unity/TMP 方法查找 | Unity/TMP proxies | HUD 和 AssetBundle 生产消费链已迁移 |
| PC 字段直接访问 | proxy getter/setter | MOD IL 重写 `ldfld/stfld` 调用访问器 |
| 托管层普通目标探测 | native resolver 诊断 | 移除重复地址缓存和宽松查找 |
| `CalculatePercentAcc` 托管 publisher Hook | native fixed-op slot | 删除托管旁路，保留 native snapshot |
| Native HookManager / HookBroker | 保留 | 不迁移、不重写语义 |
| native rule bytecode | 保留并扩展 | 继续作为 PATCH 执行真源 |

`UnityResolve` 已从 CMake、托管项目和生产源码删除。离线 dump 只用于生成期审计；运行时对象访问走 generated proxies，Hook target 走轻量 native metadata resolver，两条路径都不保存 RVA/VA。

## 代理程序集生成方法

### 输入来源

生成器使用两个互相校验的输入层：

1. Android `dump.cs`：提供 Android 实际存在的 API 清单和离线审计信息。
2. PC/AssetRipper managed assemblies：提供高质量托管签名和依赖关系候选。

任何 PC 签名只有在能够与 Android 类型和方法清单严格对应时才能进入代理输入。无法证明对应关系的成员标记为不可用，不允许靠 RVA 对齐强行接入。

### Android 依赖闭包

不能把整个 AssetRipper `GameAssemblies` 直接交给 Generator。它包含 WinForms、Ookii、桌面 Newtonsoft 等 Android 不需要的依赖。

生成流程应先建立闭包：

```text
目标 MOD 的 TypeRef/MemberRef
  + PATCH target/callback 所需类型
  + Unity 基础对象类型
  + HUD/UI/TMP 所需类型
  -> 递归类型签名依赖
  -> Android 允许程序集集合
  -> 裁剪后的生成输入
```

闭包规则：

- 保留方法签名、字段签名、基类、接口、泛型约束和自定义属性所需类型。
- 排除未被闭包引用的桌面程序集。
- Windows-only 类型进入兼容报告，不进入 Android 代理。
- 缺失外部类型时输出完整依赖路径，禁止静默生成错误类型。
- 不可解析值类型按 non-blittable 处理，直到布局得到验证。

当前实现使用两层 UTF-8 清单：

- `tools/AndroidDumpIndex/proxy_seed_types.txt`：声明需要存在的首批代理类型。
- `tools/ProxyInputClosure/proxy_surface_members.txt`：声明当前真正允许暴露的成员 surface。

surface 格式：

```text
F|assembly|type|field
G|assembly|type|getter-only-property
P|assembly|type|read-write-property
M|assembly|type|static-or-instance|generic-arity|return-type|method|param1;param2
MM|assembly|type|static-or-instance|generic-arity|return-type|method|param1;param2
```

`MM`（managed-only method）表示 PC surface 中存在、但 Android IL2CPP metadata
没有对应方法且已有等价托管实现的成员。它仍会生成公共方法和属性语义，但不会在
代理类型静态构造器中创建 native method lookup；生成器必须为该成员提供明确的
托管实现，否则构建失败。当前使用该规则的成员是 `UnityEngine.Color.grayscale`，
其实现按 `0.299*r + 0.587*g + 0.114*b` 计算。

`ProxyInputClosure` 用 PC managed metadata 递归解析基类、接口继承、泛型约束和显式成员签名，再用完整 Android 类型目录验证每个依赖。普通类在 PC 程序集里实现的 Unity 内部接口视为平台实现细节，不进入 Android proxy 闭包；只有接口类型自身的继承接口会继续闭包。引用类型依赖默认只生成 skeleton；值类型依赖额外展开实例布局字段。缺失、歧义或无法解析均失败关闭。

### Blittable 值类型布局

代理生成不能假定输入程序集一定来自 Cpp2IL。Cpp2IL 通常通过自定义 `FieldOffsetAttribute` 提供 IL2CPP 字段偏移，而当前 AssetRipper/PC managed assemblies 的 `Vector2`、`Vector3`、`Color` 等类型只携带标准 CLR 顺序布局；把缺失的自定义属性解释成 offset `0` 会令所有字段重叠，并在 `il2cpp_runtime_invoke` 写回值类型时覆盖 CoreCLR 栈。

生成规则固定为：

- 所有实例字段均有有效 Cpp2IL offset 时生成 `ExplicitLayout`。
- 源类型本身是 explicit layout 时使用标准 metadata `FieldLayout`；字段 offset 不完整则失败关闭。
- 其余 blittable struct 保留 `SequentialLayout`、字段声明顺序以及源 `ClassLayout` 的 packing/size。
- value-type closure 必须包含全部实例布局字段，不能只保留被 MOD 直接读取的字段。
- `ProxyAssemblyAudit` 在打包前验证 `Vector2/Vector3/Color` 的字段顺序、类型和 explicit offset；Android bootstrap 再用 `Marshal.SizeOf/OffsetOf` 验证 8/12/16 字节运行时布局。任一层不一致时禁止启动 generated proxy 链路。

首批闭包结果：

- 9 个种子类型。
- 27 个生成期类型，分布于 6 个程序集；其中两个是 Generator 所需的 corlib scaffold。
- 5 个可打包代理程序集、25 个可打包类型。
- Android 缺失类型 0，未解析 metadata 类型 0。
- 首批只读 surface 为 `scrMarginTracker.hitMarginsCount`、`percentAcc` getter 和 `percentXAcc` getter。

Jipper 字段访问及当前资源重建 surface 合并后的闭包：

- 165 个精确输入类型，生成 13 个可打包代理程序集、176 个生成类型。
- 固定 surface 当前含 78 个显式字段、273 个方法身份、13 个显式属性和 14 个种子类型；闭包仍保持 Android 缺失与 metadata 未解析均为 0。
- 新增 `ADOBase.platform`、`scrController.currentSeqID/checkpointsUsed/noFail`、`scrPlayer.alive/playerID/tapsOnThisFloor`。
- 新增 `UnityEngine.Color.r/g/b/a` 与 `UnityEngine.Vector2.x/y`；这些 blittable 布局字段在重写器中按兼容字段直通，不改成 accessor 调用。
- 普通对象引用字段重写为 generated proxy accessor。
- IL2CPP 数组通过 `Il2CppArrayBase<T>.op_Implicit` 复制为 PC MOD 期望的托管数组。
- `Il2CppSystem.Collections.Generic.List<T>` 通过 `PcCompatCollectionBridge.CopyList<T>` 按需复制为托管 `List<T>`。

### 严格 runtime-metadata-only 模式

forked Generator 需要增加显式模式，暂定名：

```text
--runtime-metadata-only
```

启用后：

- 不把 `AddressAttribute.RVA` 或 `AddressAttribute.Offset` 作为代理调用依据。
- 不要求提供 `GameAssemblyPath`。
- 不执行 xref 扫描。
- 不生成 `MethodAddressToToken.db`。
- 不生成依赖 RVA 的 method xref cache。
- 方法静态构造器始终生成 runtime metadata 查询。
- 默认按完整签名查找方法。
- token 缺失是正常情况，不视为生成失败。
- 离线 token 即使存在也默认不直接信任。
- 生成报告记录每个代理成员的符号身份和来源证据。

上游默认模式保持不变；该模式只由本项目的 Android 生成脚本显式启用。

## Android Runtime 接入方法

### Native library 解析

`Il2CppInterop.Runtime` 中的：

```text
DllImport("GameAssembly")
```

在 Android CoreCLR 中必须映射到当前进程已加载的：

```text
libil2cpp.so
```

使用统一 `DllImportResolver` 完成映射，禁止复制、重命名或再次加载另一份 `libil2cpp.so`。

### 初始化顺序

```text
App process start
  -> ModManager native library loaded
  -> libil2cpp.so observed
  -> IL2CPP domain ready
  -> CoreCLR ready
  -> install GameAssembly -> libil2cpp resolver
  -> create Il2CppInterop Runtime
  -> configure rejecting detour provider
  -> set Unity version 6000.3.10f1
  -> start Runtime without Harmony/ClassInjector/xref
  -> load generated proxies
  -> load translated MOD cache
  -> load native rule bundles
  -> native coordinator resolves and installs slots
```

Runtime 初始化不能等待 ModManager UI 打开。UI 只控制展示；代理运行时、MOD 扫描和 HookManager 必须在进程启动链中完成。

未来受控注入不会改变上述默认启动链。只有某个经过验证的 `TypeInjectionPlan` 实际需要真实 IL2CPP 类型身份时，ModManager 才在 IL2CPP ready、代理加载完成且任何该类型实例创建之前初始化 injection infrastructure、注册类型并冻结 schema。普通 MOD 和现有 surrogate component bridge 不为此支付注入成本。

### 代理与 shim 隔离

两类同名程序集不能混用：

```text
pc_compat_shims/
  -> MOD 专属 AssemblyLoadContext
  -> 仅含 UnityModManager/JALib/0Harmony/Newtonsoft.Json API 形状

pc_compat_proxies/
  -> CoreCLR default AssemblyLoadContext
  -> generated Android IL2CPP 对象代理

out/legacy_shims/
  -> 仅供显式离线测试
  -> 禁止打包进 Android runtime assets
```

bootstrap 按固定依赖顺序加载 13 个代理程序集，并检查程序集身份、必需 Unity/游戏/TextCore 类型、`scrMarginTracker` 只读 surface、值类型布局以及 delegate/corlib 运行成员。默认 ALC 若已有同名但路径不同的程序集，启动失败关闭，禁止把 shim 当 proxy。

### Android slim Runtime

Android 托管构建必须传入：

```text
-p:Il2CppInteropAndroidSlim=true
```

该 profile：

- 不引用或打包 `Iced.dll`。
- 不引用或打包 `TerraFX.Interop.Windows.dll`。
- 用明确抛出 `NotSupportedException` 的 xref stub 保持 API 形状。
- 当前启动配置和 `ClassInjector` mutating API 双重硬禁 class injection。
- 当前 detour provider 拒绝所有 Il2CppInterop detour；实际游戏 Hook 只能进入 native HookSlot/HookBroker。未来 brokered injection provider 只对 ModManager 内部 allowlist 开放，默认行为仍为拒绝。
- 禁止启用 xref scanner。
- 禁止执行 Windows signature scan，要求改走 runtime metadata。
- `DelegateSupport.ConvertDelegate` 使用普通 IL2CPP `System.Object` 作为 target，并在 CoreCLR 侧按“目标代理类型 + 托管 delegate 等价性”缓存和保活；Android 不注入 `Il2CppToMonoDelegateReference`。
- 同一回调的 `+=`/`-=` 复用同一个 IL2CPP delegate 对象；缓存生命周期与进程一致，符合当前“不运行时 unhook”的 MOD 生命周期约束。
- delegate 参数允许 primitive、enum 和不含托管引用的 blittable struct；by-ref、开放泛型和含引用 struct 继续失败关闭。
- generated corlib 必须包含 `Delegate` 字段访问器，以及 `Type/MethodBase/MethodInfo/ParameterInfo/RuntimeTypeHandle` 的完整 delegate 反射依赖面；代理审计和 Android bootstrap 双重检查。
- 保留上游默认 profile 的完整桌面实现。

当前 Android 输出已移除约 19.6 MB 的无用 Iced/TerraFX 依赖。

### 线程要求

- 调用 IL2CPP API 前，当前 native 线程必须 attach 到 IL2CPP domain。
- Unity 对象创建、场景对象修改和 UI 更新只在 UnityMain 执行。
- MOD 扫描、依赖闭包、IL 重写和缓存校验在工作线程执行。
- Hook 回调不进入 CoreCLR，不在热路径执行托管反射。

## PC MOD IL 重写

generated proxy 的字段通常表现为访问器，而 PC Mono MOD 可能直接生成 `ldfld/stfld/ldsfld/stsfld`。因此导入时必须重写 MOD IL。

主要变换：

```text
game field ldfld   -> generated getter / field wrapper read
game field stfld   -> generated setter / field wrapper write
game static field  -> generated static accessor
game method call   -> generated proxy method
game type ref      -> generated proxy type
Harmony/JAPatch    -> xphorror PATCH descriptor
```

要求：

- 只重写游戏/Unity 代理类型，不修改 MOD 自身普通字段。
- 每次变换记录原始 token、目标成员、变换类型和验证结果。
- 找不到唯一目标时拒绝加载该功能，不猜测。
- 原始 DLL 保持只读，翻译产物写入独立缓存目录。
- 缓存键至少包含 MOD DLL hash、代理程序集 fingerprint、游戏版本、兼容层版本和翻译器版本。
- UI 展示扫描、依赖解析、代理绑定、PATCH 翻译和缓存命中状态。

## PATCH 接入方法

代理程序集解决的是“托管代码如何看见 IL2CPP 对象”，不改变 PATCH 的执行架构。

PATCH 链路保持：

```text
PC patch declaration
  -> static scanner / restricted interpreter
  -> exact symbolic target
  -> callback verifier
  -> fixed op or native rule bytecode
  -> capability gate
  -> native HookManager
  -> permanent slot
```

完整签名必须贯通 scanner、translation report、recipe cache、`hook_rules.json` 和 native resolver。`mvp-fixed-op-v2` 已携带 assembly、namespace、declaring type、method、static/instance、generic arity、return type 和有序 parameter types；`paramCount` 只保留为诊断冗余，并强制与参数数组一致。

native resolver 先按完整身份唯一匹配，再单独用 `abiKind` 验证 fixed dispatcher 的 AArch64 调用约定。两者不能互相替代。当前已安装的 fixed-op target 均为非泛型方法；`genericArity != 0` 会明确失败关闭，直到泛型 metadata arity 读取和 dispatcher 支持完成。

迁移后的 slot key 应基于规范化完整身份，而不是地址：

```text
assembly|namespace|type|method|static|genericArity|return|parameterTypes
```

两个 MOD 只有在完整身份相同且 dispatcher ABI 相容时才能合并到同一个 slot。

## 失败策略

迁移链路统一失败关闭：

- metadata 未 ready：延迟并限频重试。
- assembly/type 不存在：目标不可用。
- 方法签名无匹配：目标不可用。
- 方法签名多匹配：目标歧义，不安装 Hook。
- runtime method pointer 为空：不安装 Hook。
- dispatcher ABI 不支持：slot `Faulted`。
- 代理依赖缺失：对应 MOD 功能不可用。
- IL 重写不完整：不加载翻译产物。
- 非 ModManager 注入基础设施的 detour 请求到达 `Il2CppInterop` provider：明确拒绝并记录调用方。
- rule 运行异常：禁用问题 rule，保留永久 slot 和 original。

错误必须同时进入：

- logcat。
- ModManager UI 的兼容诊断页。
- MOD 缓存目录中的结构化迁移报告。

禁止任何地址猜测、同名随机回退或无日志降级。

## 分阶段实施

### 阶段 0：冻结契约

- 固化 native slot 和 HookBroker ABI。
- 记录当前手写桥的调用面。
- 建立禁止固定地址、禁止托管 detour 的静态检查。
- 保留当前可工作的 JipperResourcePack 作为回归样本。

完成条件：迁移工作不会修改 slot 生命周期和现有 Hook 顺序。

### 阶段 1：生成器严格模式

- 修复 fork 在缺失字段签名和外部类型时的诊断。
- 增加 `runtime-metadata-only` 模式。
- 关闭 xref 和 method address map。
- 生成方法改为完整签名 runtime lookup。
- 禁止宽松同名回退。

完成条件：代理产物不包含运行时所需的固定方法地址数据库。

### 阶段 2：Android 代理输入闭包

- 解析 UTF-8 `dump.cs`。
- 建立 Android 类型/方法索引。
- 用 PC managed assemblies 补充签名候选。
- 构建 MOD 所需类型依赖闭包。
- 生成 Assembly-CSharp、Unity Core、UI 和 TMP 的最小代理集合。

首批验证类型：

- `scrMarginTracker`
- `scrController`
- `scrPlayer`
- `scrPlanet`
- `UnityEngine.Object`
- `UnityEngine.GameObject`
- `UnityEngine.Component`
- `UnityEngine.UI.CanvasScaler`
- `TMPro.TextMeshProUGUI`

完成条件：上述类型的 class、field、method 都能由模拟 runtime metadata 测试精确定位。

### 阶段 3：Runtime bootstrap

- 接入 `GameAssembly -> libil2cpp.so` resolver。
- 接入拒绝 detour provider。
- 禁用 HarmonySupport、ClassInjector 和 xref。
- 实现 IL2CPP domain readiness 和 thread attach。
- 加载代理程序集但暂不替换生产调用。

完成条件：本机启动测试能验证配置和程序集依赖，不出现隐式 detour 请求。

### 阶段 4：对象互操作迁移

按风险从低到高替换：

1. 只读 static getter。
2. 普通实例字段读取。
3. 普通实例方法调用。
4. 数组和泛型集合读取。
5. Unity Object 生命周期。
6. HUD GameObject/Canvas/TMP 创建。

迁移期曾使用旧实现做双路对照；当前旧生产路径已删除。新增调用面必须通过 generated proxy metadata audit、契约测试和实机行为验证。

完成条件：生产代码不再为已迁移类型新增手写 `UnityResolve` 调用。

### 阶段 5：MOD IL 重写与缓存

- 重写程序集引用和游戏成员访问。
- 生成可加载的 Android MOD 副本。
- 建立 hash/fingerprint 缓存。
- 在 UI 展示逐阶段状态。

完成条件：不执行 PATCH 时，JipperResourcePack 的托管初始化和普通代理访问可完成且不触发游戏 Hook。

### 阶段 6：PATCH 完整签名贯通

- 扩展 descriptor 和 runtime bundle schema。
- native resolver 按完整签名唯一匹配。
- 所有 rule 继续进入永久 slot。
- 删除 `PcCompatDobbyBridge` 中重复的游戏方法 Hook 旁路。
- 将 `CalculatePercentAcc` publisher 完全归入 native fixed-op slot。

完成条件：托管层没有任何游戏方法 Dobby Hook 安装点。

### 阶段 7：删除手写运行依赖

- 删除已由 proxies 覆盖的游戏 stub。
- 删除 `UnityResolve` 生产依赖。（已完成）
- 清理重复 metadata cache。
- 清理旧托管 snapshot Hook。

完成条件：关闭兼容 oracle 后，完整功能仍通过本机回归。

### 阶段 8：实机验证

只有阶段 1 到阶段 7 全部满足完成条件后才打包实机版本。

实机验证顺序：

1. 无 MOD 启动和场景切换。
2. ModManager UI 打开、关闭和触摸隔离。
3. 导入、翻译、缓存和重启加载。
4. JipperResourcePack 加载和设置项。
5. 内置关卡 HUD 生命周期。
6. 编辑器播放态 HUD 生命周期。
7. 多 MOD 同目标 slot 合并。
8. MOD 禁用后 rule 跳过且 slot 保留。
9. 高负载下 Hook 热路径性能。

## 本机验证要求

### 静态检查

- 生产代码中不存在游戏方法固定 RVA/VA。
- `hook_rules.json` schema 不接受地址字段。
- Android 代理输出不包含 `MethodAddressToToken.db`。
- Android 代理输出不包含依赖 RVA 的 xref cache。
- Android 项目不引用 `Il2CppInterop.HarmonySupport`。
- 当前默认 Android 启动代码不调用 `ClassInjector`；受控注入构建只能经 ModManager `InjectionTypeRegistry` 调用，MOD/compat adapter 直接调用仍由静态审计拒绝。
- 所有游戏方法 Hook 最终调用点都在 native HookManager/HookBroker。

### resolver 测试

至少覆盖：

- 同名不同参数数。
- 同名同参数数但参数类型不同。
- static/instance 同名。
- 返回类型不同。
- `ref` 参数。
- 数组参数。
- 嵌套类型。
- 泛型方法和泛型类型。
- 缺失方法。
- 多候选歧义。
- token 与完整签名冲突。

预期结果是唯一匹配或明确失败，不允许随机回退。

### 代理验证

- 生成程序集可由 CoreCLR 加载。
- 所有依赖都来自允许列表。
- 代理静态构造器只执行 runtime metadata lookup。
- 字段访问器绑定正确 field metadata。
- 普通方法调用使用运行时 `MethodInfo`。
- 代理调用不会触发 detour provider。
- `ProxyAssemblyAudit` 不允许宽松 `GetIl2CppMethod`、`GetIl2CppMethodByToken`、`AddressAttribute` 或 Harmony/Iced/TerraFX 引用。
- `scrMarginTracker.percentAcc/percentXAcc` 在首批 surface 中必须只有 getter。

### slot 回归

- 同一 target 只安装一次真实 Dobby entry hook。
- 多 owner rule chain 顺序稳定。
- reload rules 不分配第二个 slot。
- disable MOD 只禁用 rule。
- clear rules 不清空 original trampoline。
- resolver 失败不会留下半安装状态。

## 完成定义

完整迁移必须同时满足：

- PC MOD 通过 generated proxies 访问 Android IL2CPP 对象。
- 生产运行时不依赖固定方法地址。
- 生产 Hook 只通过 runtime metadata resolver 和 native slot 安装。
- HarmonySupport、ClassInjector、xref 和 Runtime detour 全部不在当前默认运行链；若后续启用受控注入，只有 ModManager 拥有的 infrastructure detour 作为明确例外。
- MOD IL 重写可复现、可缓存、可审计。
- JipperResourcePack 的已支持功能不依赖 MOD 专属分支。
- HUD、设置、状态读取和 PATCH rule 在本机回归中通过。
- native slot 的永久性和多 Hook 兼容行为保持不变。
- 手写 stub 和重复托管 Hook 不再是生产运行依赖。
- 完成本机迁移验收后，才进入 Android 实机回归。

## 当前最近任务

已完成：

1. forked Generator 已增加 `runtime-metadata-only` 模式。
2. 严格模式始终使用 runtime metadata 完整签名查询，不使用离线 Token、RVA 或同名随机 fallback。
3. 严格模式不生成 method xref cache 和 `MethodAddressToToken.db`。
4. 已建立严格 UTF-8、流式读取的 `AndroidDumpIndex` 工具；dump 地址只写入 `audit_only` 对象。
5. 首批 9 个种子类型已从 r143 Android dump 建立索引。
6. Android CoreCLR bootstrap 已引用 Runtime，并将 `GameAssembly` 映射到已加载的 `libil2cpp.so`。
7. Android Runtime 已关闭 xref scanner，并配置拒绝所有 detour 的 slot-only provider。
8. Android 打包脚本已要求 Runtime/Common 和 dependency-closed generated `Il2Cppmscorlib.dll`，生成产物在资产阶段强制覆盖 `Runtime/Libs` 编译引用桩，并继续拒绝 HarmonySupport。
9. 已实现严格成员级依赖闭包；Jipper 字段访问闭环后为 94 个生成期类型、66 个显式字段，缺失和未解析均为 0。
10. 已生成 13 个 dependency-closed 代理程序集（含 generated corlib）；当前闭包为 165 个精确输入类型、176 个生成类型。`ProxyAssemblyAudit` 会验证 `Object(IntPtr)` 非 `throw null` 桩、`Nullable<T>(T)`、`List<T>.Count/Item/capacity/Add`、delegate 字段/反射依赖、`HitMargin` 和必需泛型 Unity/Material/PrefabGraph/TMP TextCore/component/coroutine API，当前结果为 0 个问题。
11. 已建立 Android slim Runtime，Android 输出不再包含 Iced/TerraFX。
12. 代理与 shim 已分目录、分 `AssemblyLoadContext`；bootstrap 会在 UI 打开前加载和验证代理。
13. 已加入可选只读双路审计：`STARRAY_PCMOD_INTEROP_AUDIT=1` 时按 1/128 采样比较 generated proxy 与 native fixed-op 直接读取的准确度字段；默认关闭且异常后熔断。
14. `build_android_single.ps1 -RuntimeAssets` 已验证 13 个代理 DLL 同时进入单包输出和 Gradle runtime assets；runtime 根目录、proxy 目录和生成输出的代理 SHA-256 一致。
15. runtime rule schema 已升级为 `mvp-fixed-op-v2`，slot key 使用规范化完整方法身份，不再按 `type + method + paramCount` 合并。
16. native resolver 已先严格校验 static、非泛型身份、返回类型和有序参数类型，再执行 dispatcher ABI gate；同名同参数数但参数类型不同不会合并或误选。
17. `CalculatePercentAcc` 状态发布已完全归入 native permanent fixed-op slot；`PcCompatDobbyBridge` 中重复的托管 Dobby Hook、original delegate 和手写字段读取已删除。
18. ReversePatch accuracy getter 改为按需读取 native snapshot；正常 Hook 热路径不进入 CoreCLR，代理审计仍可使用 native tracker oracle。
19. 已加入独立导入期工具 `tools/ModAssemblyRewriter`。它使用 dnlib 扫描 `ldfld/stfld/ldsfld/stsfld`，只在 generated proxy 中存在唯一、静态性一致、类型一致的 property accessor 时生成 `call`，任何未覆盖访问都会使整个输出失败关闭；原始 MOD DLL 始终只读。
20. 规则去重与 runtime target grouping 已将程序集纳入完整身份，并统一处理 assembly 名的空白、`.dll` 后缀和大小写；序列化仍保留首个规则的规范显示名，避免无意义 schema 文本变化。
21. 对 `JipperResourcePack.dll` 的首轮只读扫描发现 256 处代理程序集字段指令。当前最小 surface 尚不足以生成完整改写产物；缺口主要集中在 `scrFloor`、`scrConductor`、`scrPlayerManager`、`PlanetarySystem`、Unity 值类型和 TMP 静态字段。`scrMarginTracker.hitMarginsCount` 还需要 `Il2CppStructArray<int>` 到 PC `int[]` 的显式转换桥，禁止用不兼容签名直接替换。
22. primitive/blittable surface 扩展后，Jipper 的 256 处字段指令中已有 36 处可严格改写为 generated accessor，22 处 `Color/Vector2` 字段可按相同布局原样直通，剩余 198 处继续失败关闭。完整产物仍不会在对象引用和数组转换桥完成前生成。
23. 普通对象引用、数组和 `List<T>` 转换桥已完成。真实 `JipperResourcePack.dll` 的 256 处字段指令现有 234 处改写、22 处 blittable 直通、0 issues；已能写出独立改写 DLL。二次扫描只看到 22 处合法直通字段，0 次重复改写、0 issues。
24. 代理打包会验证全部 13 个必需程序集后同步审计目录内 DLL；启动日志预期为 `proxies=13`。
25. Android 导入链已注册 managed rewrite provider。缓存键包含原 MOD DLL SHA-256、全部 proxy DLL 名称与 SHA-256、rewriter schema 和集合桥 ABI；产物写入 `compiled/<modId>/managed/<cacheKey>/`，使用独立临时目录、完成标记和并发安全发布。失败或取消不会覆盖旧缓存。
26. 方法 surface 已按 Jipper 调用点、Material、PrefabGraph、静态 TMP 和 managed component coroutine 重建面扩展并继续按唯一完整身份审计。当前默认依赖闭包选择 165 个精确输入类型，生成 13 个可打包 proxy、176 个生成类型，proxy metadata 审计为 0 issues。
27. 方法审计与 ABI adapter 已覆盖 Jipper 当前产物的 1067/1067 个调用点。已实现数组/List 返回、泛型数组返回、按后续 `unbox.any T` 精确转换的 `Il2CppSystem.Object`、managed `Nullable<T>` 参数、managed `Action/Func` 和 `UnityAction<T>` 构造转换。禁止仅用类型名归一化掩盖 managed/IL2CPP ABI 差异。
28. proxy 闭包不再递归普通 PC 类实现的 Unity 内部接口。此类接口属于平台实现细节，曾由 `AudioClip.get_length` 错误拖入 Android 不存在的 `Unity.Audio` 私有类型；接口类型自身的继承关系仍严格闭包。
29. `ModAssemblyRewriter` schema 已升级到 v3。真实 Jipper 产物可写出，首次扫描为字段 234 改写、22 blittable 直通、方法 1067/1067、0 issues；对改写产物二次扫描为字段 0 重复改写、方法 1299/1299、0 issues。Android 缓存同时校验 `issues`、`methodIssues` 和 `outputWritten`，任一不满足即失败关闭。
30. `PcCompatPreparedMod` 已携带 managed rewrite bundle，Runtime snapshot 和诊断导出会记录 cache key、命中状态、改写数量、直通数量及报告路径。默认生产仍只执行 native recipe；`STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE=1` 可显式验证改写 DLL、default ALC proxy 解析和 managed 生命周期，不作为默认执行路径。
31. 已加入离线 `tools/ProxySurfaceScanner`。它只读 PC MOD DLL metadata，不执行 MOD 代码；除 `ldfld/stfld/ldsfld/stsfld/call/callvirt/newobj` 和全部 metadata `TypeRef` 外，还对基本块内 `typeof(T)`、局部变量和常量字符串做保守符号传播，识别 `Type.GetField/GetProperty/GetMethod` 以及返回对应 `MemberInfo` 的 helper。反射查询使用 `RF/RP/RN` 中间项：闭包只把目标 PC 元数据中存在且唯一的成员收敛为 `F/P/M`，不存在时保持运行时反射返回 `null` 并记入报告；直接 surface 仍 fail-closed。只接受 `Assembly-CSharp`、`RDTools`、`Unity.TextMeshPro` 和 `UnityEngine.*` 中 Android catalog 存在的类型，BCL/CoreCLR 调用继续留给托管运行时。
32. `build_interop_migration.ps1 -AutoSurfaceModPath <mod.dll|mod-dir>` 会生成 `proxy_surface_auto_merged.txt` 和 `proxy_surface_auto_report.json`，再把合并后的 surface 交给闭包工具。默认不传参时仍使用手写 surface，避免无意改变生产代理体积。
33. 用 `JipperResourcePack_release` 验证自动 surface：字段/方法、metadata TypeRef 和常量反射合计扫描 6784 个引用；当前手工运行时 surface 另固定 component batch/try、coroutine、`Time.deltaTime`、`WaitForSeconds.m_Seconds`、`WaitForSecondsRealtime.waitTime` 和静态 TMP 重建所需 TextCore 字段及默认分配入口。参数构造器不进入该 surface，实际对象状态由 metadata 字段 accessor 写入。最终闭包为 165 个类型、13 个程序集、0 missing、0 unresolved，audit 0 issues。旧游戏版本分支里不存在于 r143 的反射成员按可空反射语义报告后跳过。
34. generated generic method store 会在静态初始化器中调用 `Il2CppSystem.Reflection.MethodInfo.MakeGenericMethod(Il2CppReferenceArray<Il2CppSystem.Type>)`。其 PC 输入签名 `MethodInfo.MakeGenericMethod(Type[])` 已纳入 generated-corlib 基础 surface，代理审计会验证生成方法存在；否则 `AddComponent<T>()`、`GetComponent<T>()` 等首次调用会在 CoreCLR 绑定阶段以 `MissingMethodException` 失败。
35. Native Unity presentation resolver 已删除同名/参数数量 fallback；嵌套类型分隔符规范化后仍逐项校验完整返回值和参数类型。Presentation history 扩展为 64 槽并对未消费覆盖 fail-closed；lifecycle clear 可回收 program registry，Deferred retry deadline 已接入 worker 唤醒。
36. 已删除 `UnityResolve` C++/C# 实现并把假 Unity shim 隔离为显式测试资产。HUD 和 AssetBundle 使用 generated proxy API，未重写 MOD 不能进入生产托管执行路径。
37. 已用目标 r143 ARM64 `libil2cpp.so` 的真实动态符号表核对 Runtime：共 241 个 `il2cpp_*` 导出。泛型解析所需 `il2cpp_method_get_object/object_get_class/class_get_method_from_name/runtime_invoke/array_length/array_object_header_size` 均存在；5 个未导出的旧版 API 只有未调用声明，不在生产链。
38. generated proxy 泛型方法现在按 static、generic arity、返回类型和有序参数类型唯一解析。Android Runtime 同时硬禁 xref、ClassInjector 和 Il2CppInterop detour；由 managed bridge 接管的同步 `AssetBundle.LoadFromFile`、`LoadAsset`、`LoadAllAssets` 被排除出 native proxy surface，并由 auto scanner、构建审计和 Android 启动审计阻止回流。当前可在桌面执行的 PcCompat 契约测试为 238/238。
39. 当前 `ModAssemblyRewriter` schema 为 v14，managed cache 为 v15。写出缓存前会核对 MOD 指向生成代理程序集的全部 metadata TypeRef；缺类型时失败关闭并报告精确 `assembly!type`。external bridge 支持 source by-ref 参数驱动的泛型闭合与显式 opaque handle erasure，无法证明的句柄方法流向会阻止产物写出。v14 还支持对 MOD 重写产物中的精确静态字段读取应用常量 oracle；Android 只用它把已审计 PC feature 的 `ADOBase.platform` 读取改写为 `Platform.Windows(3)`，不修改游戏全局 IL2CPP 字段。v15 cache 纳入平台 oracle、KeyViewer Adapter 与对应 SHA-256 manifest，升级后自动使用新缓存目录。
40. Android slim delegate 转换不再触发被硬禁的 `ClassInjector`。rooted delegate 路径支持 primitive/blittable 参数，并覆盖 Jipper `SceneManager.sceneUnloaded` 的 `UnityAction<Scene>`；标准桌面 profile 仍保留上游 class-injection 实现。
41. generated generic 初始化链已完成空指针封口。普通 `Nullable<T>`、`List<T>`、`UnityAction<T>` 在泛型定义、参数和最终 inflated class 三处校验；7 个 `MethodInfoStoreGeneric_*` 在参数 class、反射对象和最终 method pointer 三处校验。生成代理全局禁止引用裸 `il2cpp_class_get_type`，`GetIl2CppMethodExact` 缺失或歧义时直接失败关闭。
42. guard 审计已扩展到全部 generated proxy 方法：对象分配、虚分派、装箱/拆箱、`class_from_type`、class value size、反射 method inflation 和 `runtime_invoke` 都要求相邻或先行 guard。Android 启动时使用同一规则只读 IL，旧代理无法静默进入 MOD setup。Android slim Runtime 的对象、数组、对象池和 delegate 汇合点也会把零指针转换为托管异常。
43. managed lifecycle enable/update/disable 失败会立即以 UTF-8 写入 `<mod>/.pccompat/last_managed_failure.txt`。诊断导出包含完整 activation/lifecycle exception，logcat 只输出单行根因和报告路径，避免长堆栈刷屏或被截断。
44. 不启用 `ClassInjector` 的 managed component bridge 已进入生产重写链。rewriter 在显式 MOD-owned assembly catalog 内跨 DLL 证明 `GameObject/Component` 的 managed `Add/Get/GetComponents/TryGetComponent` 泛型与 `Type` 调用；generated proxy 泛型实参保持原路径，未知实参失败关闭。透明 `gameObject/transform` 返回 registry owner；CoreCLR component 销毁不进入 native API，真实 Unity 对象保持官方立即/延迟 Destroy。Jipper 的 `KeyViewerUpdater/RainManager` 与原生 `Canvas` 分流已有真实 DLL 回归。
45. managed rewrite provider 从主程序集和 bootstrap 两个根读取 PE `AssemblyRef` 闭包，将全部 MOD-owned DLL 作为一个原子缓存包发布。ALC 优先按 simple name 装载重写映射，bootstrap 不再回到原始 DLL。managed component 的 `Start(): IEnumerator` 与常见 Start/Stop coroutine API 支持嵌套 enumerator、`yield null`、Unity scaled `WaitForSeconds` 和 monotonic `WaitForSecondsRealtime`；未知/custom yield、序列化字段和原生 `Component` 身份继续失败关闭。
46. `Il2CppSystem.Object -> unbox.any T` adapter 会同时保留外部类型的 assembly scope 与 class/value-kind。rewriter 按 `DefinitionAssembly + FullName` 查询 generated proxy，只有真实代理 `TypeDef.IsValueType` 才生成 `ValueTypeSig`；未知类型不猜测。产物级闭包测试覆盖全部 `BoxUnboxedValue<T>`，Jipper 当前外部实例 `PlanetCount`、`SpeedType` 均通过。rewriter schema 已升至 `v18-external-valuetype-kind`，Android cache key 明确包含该 schema，旧错误产物自动失效。

下一步：

1. 继续补齐 native Hook target resolver 的 `ref`、数组和嵌套类型失败关闭测试；generated proxy 已支持泛型方法身份，但 native fixed-op Hook target 仍保持非泛型白名单。
2. 用 `STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE=1` 完成首轮 Android 实机加载、生命周期、delegate 回调和 Nullable 参数验证；通过前不切换生产默认路径。
3. 实机验证 generated proxy HUD 创建、MOD 静态字体、生命周期和 AssetBundle acquire/release；确认 default ALC 启动日志显示 `proxies=13`。
4. 开启 `STARRAY_PCMOD_INTEROP_AUDIT=1` 长期对照首批只读状态；native telemetry snapshot 继续作为 Hook 热路径真源，不为形式统一引入逐帧 CoreCLR 调用。
5. 在现有 surrogate component bridge 上补 `GetComponent(s)InChildren/Parent`、List overload 和常见 custom yield；这些能力不等待 class injection。
6. 为 FixedUpdate/EndOfFrame 增加明确 UnityMain phase，并实现 owner-aware 自有字段持久化/设置 UI；不把它描述成 Unity 原生序列化。
7. 最后实现 ModManager-owned `InjectionTypeRegistry` 与 brokered injection provider，先验证 Unity 6000.3.10f1 infrastructure target resolver，再决定逐类型注入或通用 host。当前 `EnableClassInjection=false` 保持不变，直到整条计划、冲突、生命周期和实机回归闭环通过。
