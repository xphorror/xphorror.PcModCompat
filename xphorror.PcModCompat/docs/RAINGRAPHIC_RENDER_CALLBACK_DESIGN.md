# RainGraphic 宿主渲染回调桥（立项）

日期：2026-08-23

状态：**已实现**（改写侧与登记/分派链路全部落地并有测试；实机渲染未验证）。本文是 `MANAGED_BRIDGE_AND_CONVERTER_DESIGN.md` §4.7（实施顺序第 6 步）的独立立项文档。JipperKeyViewer 的 `RainGraphic` 阻塞项已闭合，主程序集现在 `outputWritten=True`。

实现过程中有 **三处设计结论被实测推翻或修正**，见 §11。§9 的三项未决事项已全部定案，见 §9。

## 0. 前提修正

立项前先纠正两处此前记载错误的事实。

### 0.1 `JipperKeyViewer-1.7.0/` 就是发布版源码

此前记载"1.7.0 之后的源码里 `RainGraphic` 已被删除，逻辑移进 `RainLayer` 自绘"，并据此推断"发布 DLL 与仓库源码形态不一致，无法交叉验证"。实际情况是仓库里有**两份**源码：

| 目录 | 与发布产物的关系 |
| --- | --- |
| `JipperKeyViewer-1.7.0/` | **发布版源码**，与 `JipperKeyViewer-AssetBundle/JipperKeyViewer.dll` 对应。`RainGraphic.cs` 在其中 |
| `JipperKeyViewer/` | 后续开发版，`RainGraphic` 已删，改为 `KeyShapeLayer`/`RainLayer`/`GhostRainLayer` 三个合并 Layer |

**本立项只针对 1.7.0 形态**，因为它是唯一有发布产物、可审计、可验收的形态。开发版的三层自绘形态没有发布 DLL，无法进入改写审计，故不在范围内——但设计要避免把自己锁死在 per-drop 假设上，见 §5.2。

### 0.2 目标形态是 per-drop，不是整层自绘

1.7.0 的 `RainGraphic` 是**每个雨滴一个组件**：

```
RainSystem::GetRainFromPool     → new GameObject("Rain") → AddComponent<RectTransform>() → AddComponent<Rain>()
Rain::Awake                     → AddComponent<RainGraphic>()   ← 阻塞点
                                → new GameObject("GhostImage") → AddComponent<Image>()
RainSystem::ReturnRain          → SetActive(false) + SetParent(pool)，池上限 MAX_POOL_SIZE = 64
                                  超出上限则 Object.Destroy(r.gameObject)
```

所以 hook 的发射频率是 **活跃雨滴数 × 网格重建次数**，而非每帧一次。这是选型时必须带上的量级。

## 1. 目标与不做

**目标**：让 `AddComponent<RainGraphic>` 通过，且雨效果真的渲染。

两者必须一起做。只放宽 `AddComponent` 判据会得到"改写干净、`outputWritten=True`、实机雨不渲染"——用可加载换一个静默失效的功能，比现在的失败关闭更糟。理由：桥只转发 `Awake`/`Update`/`OnEnable` 等生命周期消息，而 `MaskableGraphic` 的价值是 Unity 渲染管线回调 `OnPopulateMesh` 向它要顶点，那条路径桥当前完全没有。

**不做**：
- 开发版的 `KeyShapeLayer`/`RainLayer`/`GhostRainLayer` 三层形态（无发布产物）；
- 通用的"任意 `Graphic` 子类都能自绘"能力（见 §4 的登记制）；
- `Image`、`Text`、`TextMeshProUGUI` 等已在 IL2CPP 类表里的组件（走现有 native component 路径，不受影响）。

## 2. hook 目标：`RawImage::OnPopulateMesh`

### 2.1 dump 事实

```
Graphic          protected virtual  OnPopulateMesh(VertexHelper)   ← 有槽位
Graphic          protected virtual  OnPopulateMesh(Mesh)           ← 旧重载
Image            protected override OnPopulateMesh(VertexHelper)
MaskableGraphic  （无 OnPopulateMesh）
RawImage         protected override OnPopulateMesh(VertexHelper)   RVA 0x47b9c78 / VA 0x7c0e7e1c78
```

`public abstract class MaskableGraphic : Graphic, IClippable, IMaskable, IMaterialModifier`。

### 2.2 三个候选的排除依据

- **`MaskableGraphic::OnPopulateMesh` 不存在。** 它不 override 该方法，没有地址可 hook；而且它是 `abstract`，也不能 `AddComponent`。（访谈中先选了这个，由 dump 推翻。）
- **`Graphic::OnPopulateMesh` 是 virtual 且被 `Image`/`RawImage` 都 override 了**，hook 基类对已 override 的子类根本不触发。只有当宿主组件是"没有 override 的 `Graphic` 子类"时才有效——而能 `AddComponent` 的具体子类基本都 override 了。
- **`Image::OnPopulateMesh` 可用但代价高。** `Image` 是游戏 UI 到处在用的组件，hook 它会让每帧大量无关调用穿过 owner 判断，把它变成热路径。

选 `RawImage`：它是最简单的 `MaskableGraphic` 具体子类（类体 1039 字符，只靠 texture 画一个 quad），且游戏本体用得少——全 dump 只有 9 个 `RawImage` 字段（`pausePlanetsImage`、`waveRaw`、`clsIcon`、`GetBlurredScreenshot(RawImage)` 等）。

> 更正：此前设计文档引用的 "dump `428594`" / "`426150`" 是行号而非地址。本文记录 RVA/VA。原文的**结论**（必须选 `RawImage`、不能选 `Graphic`）经 dump 确认成立，理由也成立。

## 3. 分派语义：完全替代

hook 命中 MOD 宿主实例时 **不执行原 `RawImage::OnPopulateMesh`**，直接把该 `VertexHelper` 转给托管 `RainGraphic.OnPopulateMesh`。非 MOD 实例原样执行。

依据：`RainGraphic.OnPopulateMesh` 第一行就是 `vh.Clear()`，语义与 PC 上 Unity 调它的 override 完全一致。这条决策同时消解了"额外加的真 `RawImage` 会画自己的 quad 与 MOD 顶点叠加"的问题——命中路径下 `RawImage` 的 quad 根本不生成，无需把 texture 置 null 之类的间接手段。

排除的两个替代：先跑原方法再转发（会被 `vh.Clear()` 抹掉，纯浪费）；反序（MOD 顶点被覆盖）。

## 4. 驱动机制：复用现有 managed-only Prefix 链路（部分成立，见 §11.1）

> **实现修正**：ABI 结构与 native 传输层确实全部复用了；**绑定层不能复用**，因为 `CallbackBinding` 在构造时定死 callback 目标，而渲染回调的接收者是每次按实例指针查出来的托管对象，且 JPKV 零 Harmony 用法所以没有 shim registration 可绑。因此新增 op 24 与独立 id 前缀，dispatcher 按 patch id 路由到组件桥。详见 §11.1。

不新建 ABI。仓库已有 native→managed 同步 prefix 分派 ABI `PcCompatManagedPrefixInvocationV2`，字段刚好够用：

| ABI 字段 | 用途 |
| --- | --- |
| `Instance` | `RawImage` 实例指针 |
| `Argument0` | `VertexHelper` 实例指针 |
| `ArgumentCount` | 1 |
| `ResultKind` | `Void` |
| `RunOriginal` | 置 `0` 实现 §3 的完全替代 |

这与 `scrShowIfDebug.Update` 的 managed-only synchronous Prefix 同一条 native 链路（in-flight 计数、退休、fault 计数全部复用）。**但目录项不加进 `PcCompatManagedOnlyCallbackCatalog`**——那份目录按 Harmony patch descriptor 索引，而渲染回调没有 descriptor。规则改由 `PcCompatRecipeCompiler.EmitManagedRenderCallbackRules` 从共享登记表直接发出，见 §11.1。

排除的两个替代：新建专用 native fixed-op（顶点生成逻辑要在 native 重写一遍，就不再是"执行原 MOD 托管代码"，辜负整个改写管线的目的）；伪驱动即宿主每帧主动 `new VertexHelper` 提交（会与 Unity 自己的网格重建互相覆盖，产生闪烁或顶点丢失）。

### 4.1 owner 筛选放在 native 侧

native 侧维护一个 MOD 宿主 `RawImage` 指针集合，桥在 `AddComponent` 时登记、在组件销毁/session teardown 时注销。hook 入口先查集合：**不命中就直接 tail-call 原方法**，不进托管、不分配、不填 ABI 结构。

理由：游戏那 9 个 `RawImage` 里有高频重建的（模糊截图、波形），让它们每次重建都穿一趟 native→managed 边界是不可接受的。放在托管侧判断实现更简单，但把边界开销加在了游戏自身路径上。

**实现形态**：扁平的按指针排序 `vector`，copy-on-write 发布（`shared_ptr` + `atomic_load/store`），读侧无锁——`is_managed_render_host` 只做一次 `lower_bound`。写侧（登记/注销/清空）重建并发布，代价可忽略：JPKV 自己的雨滴池上限就是 64。另维护一份 per-MOD 列表，使 teardown 能精确清空一个 MOD 而不动其它 MOD 的条目。

**预筛必须早于 invocation 构造**，否则省下的只有那次托管调用，而不包括结构体填充与参数拷贝。`NativeParsesTheRenderRuleIdAndFiltersByOwner` 断言了 `any_dispatchable` 的位置在 `invocation.struct_size` 赋值之前。混合 slot（同一目标上既有普通 prefix 又有渲染回调）逐 target 再判一次，因为只有后者是实例限定的。

## 5. 托管实例的构造与绑定

这是本立项技术上最细的一环，也是唯一需要额外定向改写的地方。

### 5.1 绑定到真实 `RawImage` 实例

代理 `MaskableGraphic..ctor()` 会 `il2cpp_object_new` 一个 **abstract** 类——直接构造必然失败。因此：

1. 在宿主 GameObject 上 `AddComponent<RawImage>()`，得到真实 IL2CPP 实例指针；
2. 用 `RuntimeHelpers.GetUninitializedObject` 造托管 `RainGraphic` 外壳；
3. `CreateGCHandle(该指针)` + `isWrapped = true` 绑定；
4. 跑 `RainGraphic` 自己的字段初始化（`renderMain = true` 等）。

**这套机制现成存在**：`Il2CppObjectBase.InitializerStore<T>`（`Il2CppObjectBase.cs:100-150`）在"类型只有无参 ctor"分支里做的正是 1–3 步。`isWrapped` 置位后基类 ctor 里的 `CreateGCHandle` 会 early-return，所以基类 ctor 新建的对象被丢弃、绑定的指针不被覆盖。

### 5.2 必须额外做一处定向改写

`InitializerStore` 那条路走完 1–3 步后**还会调无参 ctor**。而 `RainGraphic..ctor()` 编译成"字段初始化 → `call MaskableGraphic::.ctor()`"，代理那个 ctor 的后半段是：

```
ldsfld  NativeMethodInfoPtr__ctor_Protected_Void_0
ldarg.0 → Il2CppObjectBaseToPtrNotNull(this)      ← 这是我们绑定的 RawImage 指针
call    il2cpp_runtime_invoke(...)
```

即它会**在已构造好的真实 `RawImage` 实例上再跑一遍 native `MaskableGraphic..ctor`**。`AddComponent<RawImage>` 已经跑过真正的构造，二次初始化会重置 Unity 内部状态（stencil 重算标记之类），后果未验证。

因此实现要把 MOD 侧 `RainGraphic..ctor()` 里那一处 `call UnityEngine.UI.MaskableGraphic::.ctor()` **改写为空操作**（登记制名单内的类型才允许，见 §6），字段初始化保留。这样：

- `RainGraphic` 仍然**继承**代理 `MaskableGraphic`，于是它内部对 `Graphic::get_rectTransform` / `get_color` / `SetVerticesDirty` 的三处 `callvirt` **不需要任何改写**——`this` 就是绑定实例，调用直达真实 `RawImage`；
- 基类 ctor 的副作用被消除。

实测 `RainGraphic` 全类只有 4 处基类调用（`.ctor` + 上述三个），所以这一处改写换来另外三处零改动。

**实现细节：用 `pop` 而非 `nop`。** `call MaskableGraphic::.ctor()` 弹掉一个 `this` 不压回，`pop` 的栈效果完全相同，所以前面那条 `ldarg.0` 保持原样、无需删指令、无分支目标移动。改写后形态（已由测试断言）：

```
IL_0000 ldarg.0
IL_0001 ldc.i4.1
IL_0002 stfld  RainGraphic::renderMain      ← 字段初始化保留
IL_0007 ldarg.0
IL_0008 pop                                 ← 原 call MaskableGraphic::.ctor()
IL_0009 ret
```

**代理 ctor 的两处危险已由 dump 逐条确认**（此前是推断）：`MaskableGraphic..ctor()` 的 IL 里既有 `il2cpp_object_new(NativeClassPtr)`（在 **abstract** 类上分配，必然失败），也有 `il2cpp_runtime_invoke(NativeMethodInfoPtr__ctor_..., Il2CppObjectBaseToPtrNotNull(this))`（在我们绑定的指针上二次跑 native 构造）。`pop` 同时消除两者。

### 5.3 为什么不选另两条路

- **改写 MOD 类型的基类为 `RawImage`**：代理 `RawImage` 没有无参 `.ctor()`，仍然构造不出来；且改基类会改变 MOD 类型语义，风险面大得多。
- **基类 ctor 置空 + 三个成员各自桥到绑定实例**：可行且干净，但要为 `rectTransform`/`color`/`SetVerticesDirty` 各写一条 spec。§5.2 的方案用一处改写达到同样效果，成员调用走原生继承路径而非桥，形态与 PC 更接近。

### 5.4 与开发版三层形态的兼容余量

上述机制没有任何一处依赖"每个雨滴一个组件"。若将来发布版换成 `RainLayer` 整层自绘，只需在登记名单里换类型名——绑定、hook、分派语义都不变。per-drop 只影响**发射频率**（§0.2）与验收关注点（§8），不影响设计。

## 6. `IsManagedOwnedMonoBehaviour` 的放宽：登记制名单

### 6.1 当前拒绝的真实根因

`IsManagedOwnedMonoBehaviour`（`Program.cs:1992`）**本就沿继承链上溯**。真实失败点在 `Program.cs:2036-2039`：它要求链上每一环的 `TypeDef` 都在 MOD 自有模块内。发布 DLL 里 `RainGraphic : UnityEngine.UI.MaskableGraphic`，碰到代理模块的类型链就断。

这是刻意约束而非疏漏：MOD 类型继承代理类，意味着 Unity 要能实例化并回调一个 IL2CPP 类表里不存在的类型——正是 managed component bridge 存在的前提所否定的。**报错文字（"does not derive UnityEngine.MonoBehaviour"）与真实原因不符，一并修正。**

### 6.2 决策：不做通用放宽

新增声明式名单，登记 `(MOD, 类型全名, 宿主代理类型, hook 目标)` 四元组，只有登记过的类型才允许继承链穿过代理模块。未登记的仍失败关闭。

不做"只要最终到达 `MonoBehaviour` 就允许"的通用放宽：那样任何继承代理类的 MOD 类型都会被接受，而我们只有 `RawImage::OnPopulateMesh` 一个 hook。继承 `Selectable`/`ScrollRect`/`LayoutGroup` 之类的类型会得到"改写干净但行为静默丢失"——正是 §1 要避开的那种结果。登记制让"能过判据"与"有对应 hook"绑在一起。

### 6.3 实现追加：声明的基类要校验，而且校验两次

名单里记的 `BaseType` 不是注释，是判据。改写器沿继承链走到**第一个不在 MOD 自有模块内**的基类，要求它与登记声明逐字相等（含程序集名），否则失败关闭。组件桥在 `AddComponent` 时再查一次已加载类型的 `BaseType`。

两次不是冗余：改写器读的是磁盘上的 MOD 程序集，桥读的是运行时真正加载的类型；两者之间若发生代理重生成或换了 MOD 版本，只有第二次能发现。这条不是假想——JPKV 自己的开发版已经把 `RainGraphic` 换成三层自绘，未来的发布版完全可能改父类或删类型。由 `RenderComponentRegistrationVerifiesTheDeclaredBaseType` 钉住。

### 6.4 报错文字修正

原文字"MOD-owned component type does not derive UnityEngine.MonoBehaviour"从来不是真实失败条件（`IsManagedOwnedMonoBehaviour` 本就沿链上溯）。现改为"has a base chain that leaves the MOD's own modules and is not a registered render component"。`UnregisteredProxyDerivedComponentStillFailsClosed` 同时断言新文字出现、旧文字不出现，防止回退。


## 7. 落点

| 变更 | 位置 | 状态 |
| --- | --- | --- |
| 共享登记表（改写器 / recipe 编译器 / 组件桥三方唯一来源） | `src/PcCompatManagedRenderComponentCatalog.cs`（新） | 已落地 |
| 放宽判据 + 基类校验 + `.ctor` 空操作改写 | `tools/ModAssemblyRewriter/Program.cs`（`ManagedRenderComponentSpec`、`MatchRenderComponent`、`PlanManagedRenderComponentRewrites`） | 已落地 |
| 名单注册 + 托管缓存键 | `PcCompatAndroidManagedAssemblyRewrite.BuildManagedRenderComponents`、`"managed-render-component|"` | 已落地 |
| 绑定构造、双登记、分派入口、teardown | `src/PcCompatManagedComponentBridge.cs`（`AddManagedRenderComponent`、`TryDispatchRenderCallback`、`ClearRenderComponentsForSession`、`RemoveRenderComponentBinding`） | 已落地 |
| 绑定与包装的宿主实现 | `PcCompatManagedComponentOwnerHost.BindManagedRenderComponent` / `WrapNativeProxyPointer` | 已落地 |
| recipe 规则发出 | `PcCompatRecipeCompiler.EmitManagedRenderCallbackRules` | 已落地 |
| dispatcher 路由 | `PcCompatManagedCallbackDispatcher`（`_renderPatchIds`）、`PcCompatManagedEventRecipeReader`（op 24 / `managed_render:`） | 已落地 |
| 强制托管分派（否则 recipe 路径会跳过托管 setup，见 §11.2） | `PcCompatRuntime.RegisterPreparedMod` | 已落地 |
| native 指针集合、预筛、三个导出 | `Android/library/src/main/cpp/core/pccompat_hook_rules.cpp` | 已落地 |
| native 导出的托管封装 | `PcCompat/PcCompatNativeRenderHostRegistry.cs`（新） | 已落地 |
| ABI 与 `CacheFormatVersion` 递增 | `v42-proxy-surface-nullable-vector2`、`PcCompatManagedComponentBridge.v9-render-component` | 已落地 |

**未采用立项时的 `PcCompatManagedOnlyCallbackCatalog` 落点**：那份目录是按 Harmony patch descriptor 索引的（`CallbackType`/`CallbackMethod`/`PatchKind`/`TargetType`），而渲染回调没有 patch descriptor，见 §11.1。

`Rain::Awake` 里其余调用都不受影响：`graphic.raycastTarget = false` 走代理 `Graphic::set_raycastTarget`（代理里有）；`ghostImage` 是真 `Image`，走现有 native component 路径。`RainSystem::CreateRainDropForKey` 读写的 `shadowEnabled`/`shadowColor`/… 是托管字段，直接访问。`RainSystem::UpdateFadeOut` 的 `Graphic::get_color`/`set_color` 走绑定实例。（改写产物已确认：`Rain::Awake` 的 `AddComponent<RainGraphic>` 落到 `PcCompatManagedComponentBridge::AddComponent<RainGraphic>`，`AddComponent<Image>` 与 `AddComponent<RectTransform>` 保持 `callvirt` 走代理。）

## 8. 实例归属与生命周期

**双登记**：托管 `RainGraphic` 实例进 `ComponentEntry`（拿 owner 校验、audit 快照、session teardown），它绑定的 `RawImage` 指针进 `NativeObjectLease`。两表交叉引用：hook 拿到 `RawImage` 指针 → 查到 entry → 确认 owner → 转发。

`RainGraphic` 没有 `Awake`/`Update`/`OnEnable`，不需要帧分派，单看 hook 只要一张指针映射就够。仍双登记的理由是**泄漏面**：池上限 64、超出即 `Destroy`，一套独立的清理逻辑要自己保证不漏，而现有 teardown 已经被其它组件验证过。

**池复用不需要新机制**：`ReturnRain` 只 `SetActive(false)` + `SetParent`，对象未销毁、组件未移除，对应现有 `entry.Active` 的 OnDisable/OnEnable 语义；真正超池时的 `Object.Destroy(r.gameObject)` 已经过 `Destroy` 桥的 owner 校验与 entry 清理。

**实现追加：两个方向的顺序都是刻意的。**

- 登记时 native 指针**最后**发布（`AddComponent` → 租约 → 绑定 → entry → 映射表 → native）。发布那一刻起 hook 就能分派，所以之前每张托管表都必须已经一致。
- 撤销时 native 指针**最先**withdraw（teardown 第一件事就是 `ClearNativeRenderHosts`，早于 OnDisable/OnDestroy；单组件销毁在 `DestroyEntryCore` 置 `Destroying` 之后立即 `RemoveRenderComponentBinding`）。

反序会留下一个窗口：native 分派进来、映射表已空、查不到 binding → 返回"未消费" → 宿主画自己的 quad。不致命但会出现一帧宿主方块。由 `NativeHostPointerIsPublishedLast` 与 `TeardownWithdrawsTheNativeRegistrationBeforeDroppingBindings` 分别钉住。

`DestroyingOneComponentWithdrawsOnlyItsOwnPointer` 单独存在，是因为池上限 64、超出即 `Destroy` 会持续发生：这里漏一个指针不是一次性泄漏，而是随 MOD 运行时长增长。

**回调抛异常必须收敛。** 调用方是经 reverse-P/Invoke 进来的 Unity 渲染回调，托管异常穿过那道边界会终止进程。因此 `TryDispatchRenderCallback` 捕获并返回 false（宿主画自己的 quad）——用"一帧可见瑕疵"换"不崩"。由 `ThrowingOverrideIsContainedAndReportsNotConsumed` 钉住。


## 9. 未决事项（已全部定案）

立项时留了三项，实现时的结论：

1. **托管侧如何调用 `protected override OnPopulateMesh`——不需要改可访问性。** `Expression.Call` 直接绑定 protected 方法，编译成 `Action<object?>` 缓存在 binding 上。倾向的方案（缓存 open delegate）方向对，但 `CreateDelegate` 需要静态已知的 `Action<T>`，而 `T` 只在运行时可知；表达式编译同时解决了这一点和"不改 MOD 元数据"。**由 `ThrowingOverrideIsContainedAndReportsNotConsumed` 等用真 `protected override` 的桩类型验证。**
2. **`VertexHelper` 包装不按指针缓存。** 该指针是 Unity 在回调期间自己持有的栈上对象，回调返回后地址可被复用；按指针缓存会把复用后的另一个 `VertexHelper` 当成同一个。每次新建一个只有 GC handle 的薄包装，代价远小于错误绑定的风险。
3. **native 指针集合用新增专用导出，不复用 rule/slot 机制。** 三个导出（register/unregister/clear）加一个诊断计数。理由：rule/slot 是"按签名解析目标"的机制，而这里要维护的是运行时实例集合，语义不同；且 clear 必须能按 MOD 精确清空而不动别人的。

## 10. 验收与明确不宣称

**本机已验证**（`1171` 通过、`1` 个既有 XPerfect JIT 测试按原条件跳过；`StArray.ModManager.Android` Release 构建与 `:library:assembleDebug` 均通过）：

| 项 | 测试 |
| --- | --- |
| `AddComponent<RainGraphic>` 不再是 bridge issue，`outputWritten=True` | `JipperKeyViewerMainAssemblyRewritesClean`、`RemainingGapsAreExactlyTheKnownSet` |
| `.ctor` 那一处改写的 IL 形态，且**只有**这一处 | `RegisteredRenderComponentBaseConstructorIsBlanked` |
| 未登记类型仍失败关闭，且报错文字不再谎称 MonoBehaviour | `UnregisteredProxyDerivedComponentStillFailsClosed` |
| 登记项声明的基类被校验，不匹配即失败关闭 | `RenderComponentRegistrationVerifiesTheDeclaredBaseType` |
| 规则形态、id 前缀、经 recipe 二进制往返后仍被读回为 render callback | `PcCompatManagedRenderComponentTests`（9 项） |
| 双登记、native 指针最后发布/最先撤销、分派命中与未命中、异常收敛、逐组件销毁只撤自己 | `PcCompatManagedRenderComponentBridgeTests`（9 项） |
| JRP 与 `JAMod.Bootstrap` 保持零 issue | `JipperResourcePackStillRewritesCleanWithProductionSpecs` 等 |

**本机不可验证**（需活的 IL2CPP 运行时与设备）：绑定后的托管实例是否真收到 Unity 的 `OnPopulateMesh` 回调；顶点是否正确提交；`SetVerticesDirty` 是否触发重建；`.ctor` 置空后 `RawImage` 内部状态是否完好；per-drop hook 在 108 键全键盘连击下的实际开销；native 指针集合在真实 hook 上的预筛命中率。

**明确不宣称**：
- **改写干净不等于雨能渲染。** 三个 IL2CPP 相关的宿主操作（绑定、读指针、包装 `VertexHelper`）在测试里是 fake，被证明的是桥按正确顺序调用它们，不是它们本身可用；
- 不宣称雨效果渲染正确——顶点流地基（代理 `VertexHelper` 有 `Clear`/`AddVert`/`AddTriangle`/`get_currentVertCount`，`UIVertex` 是 valuetype 且 `position`/`color` 可直读，顶点数据**不需要跨界封送**）只说明路径存在；
- 不宣称本方案对任意 `Graphic` 子类通用——登记制刻意限制到有对应 hook 的类型；
- 不宣称开发版三层自绘形态可用（无发布产物，未审计）；
- 所有实机行为只能由用户在设备上确认。

## 11. 实现过程中被推翻或修正的设计结论

如实记录，因为它们都改变了实现：

1. **§4"复用现有 managed-only Prefix 链路"只在 ABI 与 native 传输层成立，绑定层不成立。** `PcCompatManagedPrefixInvocationV2` 结构、`SetManagedPrefixCallback` 传输、以及 `run_managed_prefix_rules` 的 in-flight/退休处理确实全部复用了。但 `CallbackBinding` 在构造时就把 callback 目标定死，而渲染回调的接收者是**每次调用按实例指针查出来的托管对象**；而且 JPKV 零 Harmony 用法，没有 patch descriptor、没有 shim registration、没有 callback translation item 可以派生出 binding。因此新增 `PcCompatRuleOp.ManagedRenderCallback`（op 24）与独立的 `managed_render:` id 前缀，dispatcher 侧按 patch id 直接路由到组件桥。
2. **发出 recipe 规则会把 JPKV 推上"已验证 recipe"路径，从而完全跳过托管 setup。** 这是实现中发现的一个会静默失效的交互：JPKV 此前因零 Harmony 而 `hasRecipe=false`、走 self-render 兜底；一旦为渲染回调发出规则，`hasRecipe` 变 true，`PcCompatRuntime` 就会打印"loaded from verified rule recipe"并 `return`，hook 装上了而托管代码从未运行。修复是让渲染规则强制 `requiresManagedSynchronousPrefix`——它的存在本身就意味着必须走托管分派。由 `RenderRuleForcesManagedDispatchInsteadOfTheRecipeOnlyPath` 钉住。
3. **`managed_render:` 必须有自己的 id 前缀，不能复用 `managed_prefix:`。** 若复用，native 的 `parse_managed_prefix_rule_id` 会成功解析它并把它当普通同步 prefix，**owner 预筛随之丢失**——游戏本体那 9 个 `RawImage`（含高频重建的模糊截图与波形）每次网格重建都会穿一趟 native→managed 边界。这一点在 §4.1 已作为设计意图写明，但只有实现时才发现"用哪个前缀"是它的实际执行点。由 `RuleIdUsesTheRenderPrefixSoNativeKeepsTheOwnerFilter` 与 `NativeParsesTheRenderRuleIdAndFiltersByOwner`（后者同时断言预筛位置在 invocation 构造之前）共同钉住。
