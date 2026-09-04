# 托管桥与自动转换器设计

日期：2026-08-23

状态：§4.1–§4.7 已实现并有测试；§4.6 的字段 facade 与跨运行时 ABI 转换已闭合，JPOV/JPKV 托管重写均已产出。真实 IL2CPP 对象行为仍需设备验收。实现过程中有十一处结论被实测推翻，见 §8。

## 1. 目的与范围

本文只回答一个问题：**MOD 调用一个 Unity/游戏成员，而生成代理的签名与 PC 签名形态不一致时，谁来适配、怎么适配。**

范围来自 `JPOV_JPKV_COMPAT_GAPS.md` §0.0 记录的剩余 9 类差距。目标 MOD 为 JipperOverlayer、JipperKeyViewer（均只支持 UMM loader），同时不得破坏 JipperResourcePack。

不在本文范围：native hook、recipe、隔离与 owner/generation 语义（见 `MOD_RUNTIME_ISOLATION.md`）、Harmony ABI（见 `HUD_KEYVIEWER_HARMONY_COMPAT.md`）。

## 2. 两套并存机制及其分工

改写器里有两套机制，此前没有成文的选择判据，本文补上。

### 2.1 声明式手工桥

`ManagedCallBridgeRewriteSpec` → `xphorror.PcModCompat/src/*Bridge.cs`。把 callsite 换成调用我们自己写的方法，代理侧完全不参与。

### 2.2 自动代理转换器

`CreateMethodArgumentConverter` / `CreateMethodReturnConverter` 及其下属。callsite 仍指向生成代理成员，只在参数/返回值处插入转换。

### 2.3 判据

> **语义归宿主的走手工桥；纯数据形态搬运的走自动转换器。**

"语义归宿主"指以下任一成立：

- 目标行为在 Android 上应当落到宿主而非 Unity（例：`Debug.LogException` → 宿主 Logger，`PcCompatManagedLogBridge` 是既有先例）；
- 实参是 **MOD 自有的纯 CoreCLR 类型**，IL2CPP 类表里不存在，交给代理必然失败（例：`JsonUtility` 的 `ProfileData`）；
- 需要仲裁、归属校验或生命周期管理（例：path、network、component 各桥）。

其余属"纯数据形态搬运"：签名两侧描述同一份数据，只是容器/封送形式不同（数组、`List`、`StringBuilder`、装箱对象）。这类必须走自动转换器——一次覆盖所有 MOD，而不是每个 MOD 每个成员加一条 spec。

## 3. 现有机制的真实能力（实测，非推断）

设计前逐条核实过，以下是事实：

| 能力 | 现状 |
| --- | --- |
| 参数失配数量限制 | `Program.cs` 的 `CreateMethodArgumentConverter` 只允许**恰好一个**参数失配 |
| 失配参数位置 | `InsertArgumentConverter` **已支持任意位置**：把尾随实参溢出到局部、转换、再压回。已由第 0 位（`SetText(Char[],Int32,Int32)`）与第 1 位（`SelectionGrid` 4 参）两种真实站点验证 |
| 已有参数转换器 | Nullable、Delegate、**数组（新增）**、**StringBuilder（新增）** |
| 已有返回值转换器 | 数组 proxy→managed、`List` proxy→managed（`CopyList`）、`Il2CppSystem.Object`→`System.Object`（需紧随 `unbox.any`） |
| 转换器实现形态 | 两种：①直接引用 `Il2CppInterop.Runtime` 已有的 `op_Implicit`（数组类，双向都有）；②引用宿主侧辅助方法 `PcCompatAbiBridge` / `PcCompatCollectionBridge`（Nullable/Delegate/List/装箱/StringBuilder）。**都不往 MOD 模块注入方法体** |
| 上下文推断 | 只有 `GetFollowingUnboxType`（向后看一条 `unbox.any`）。没有向前推断实参来源类型的能力——**且已确认当前语料不需要**（见 §4.1） |
| 推断失败时行为 | 返回 `null` → 转换器为 `null` → 候选被过滤 → 成为 issue 失败关闭 |

**"恰好一个失配"这条限制不需要放开。** 实测 5 类形态失配里 4 类只失配 1 处；`SelectionGrid` 有个只失配 1 处的重载（`GUILayoutOption[]` 保持托管数组）可用，已按此实现。

managed→Il2Cpp 方向的 `op_Implicit` 实测**全部现成存在**：`Il2CppStringArray`、`Il2CppStructArray<T>`、`Il2CppReferenceArray<T>`。而 `Il2CppSystem.Object`、`Il2CppSystem.Text.StringBuilder`、`Il2CppSystem.Collections.Generic.List<T>` **没有**，需要宿主辅助方法。

`Il2CppReferenceArray<T>` 方向刻意**不**接入自动转换器：托管 `T[]` 里装的是代理引用，需逐元素解包，而已审计站点无人需要（唯一的引用数组参数 `GUILayoutOption[]` 有保持托管数组的重载）。

## 4. 逐类设计

### 4.1 `Debug.Log` / `LogWarning` / `LogError`（仅 JPOV，16 处）

**手工桥**（`PcCompatManagedLogBridge`），已实现。

> 本节推翻了设计初稿的"自动转换器 + 新增向前实参类型推断"。推断机制**没有实现，也不需要**：源参数静态类型本就是 `System.Object`，桥签名精确匹配，现有 spec 机制直接可用；且这一类恰好符合 §2.3 自己定的"语义归宿主"判据——初稿把它归错了边。

三个重载各自映射到 `PcCompatManagedLogBridge.Log/LogWarning/LogError`，参数保持 `object?`，在托管侧 `ToString()` 后写宿主 Logger。理由：

- 代理要 `Il2CppSystem.Object`，构造它意味着把任意 CoreCLR 对象交给 IL2CPP 域并在那边管生命周期——而 Unity 对它唯一的操作就是 `ToString()`；
- 宿主 Logger 是 Android 上用户唯一能读到的日志；
- `ToString()` 留在托管侧，自定义 `ToString()` 的 MOD 类型行为与 PC 完全一致。

`Describe` 对 `null` 返回 `"Null"`（对齐 Unity），并捕获 `ToString()` 抛出——PC 上该异常落在 Unity logger 内部，这里若放行会窜回只是想打日志的 MOD 代码。

实测：JPOV `Log` 6 + `LogWarning` 8 + `LogError` 2 = 16 处全部改写，改写后无任何 callsite 仍指向 `UnityEngine.Debug` 代理（由 `DebugLoggingIsReplacedByTheHostLogBridge` 钉住）。

### 4.2 `TMP_Text.SetText(StringBuilder)`、`SetText(Char[],Int32,Int32)`

自动转换器，已实现。

- `Char[]` → `Il2CppStructArray<Char>`：现成 `op_Implicit`（JPKV 6 处）。失配在第 0 位、后随两个 `Int32`，由已有的 `InsertArgumentConverter` 溢出机制处理——实测该机制可用，无需改动。
- `StringBuilder` → `Il2CppSystem.Text.StringBuilder`：无 `op_Implicit`，新增宿主方法 `PcCompatAbiBridge.ToIl2CppStringBuilder`（JPOV 10 处）。该桥不调用生成代理的 `.ctor(String)`；实际 runtime corlib 只有 `.ctor(IntPtr)`，所以通过 native metadata 查找 `.ctor(String)`，用 `il2cpp_object_new` + `il2cpp_runtime_invoke` materialize，裁剪构建再用无参构造 + `Append(String)` 回退。引用参数槽直接存放 `ManagedStringToIl2Cpp` 返回的对象指针，与 generated wrapper 的 `EmitObjectToPointer` ABI 一致。

**这里有一个必须记录的前提差异。** 生成代理里的 `Il2CppSystem.Text.StringBuilder` 是 skeleton，只有 `.ctor(IntPtr)`，不能把 `Il2CppInterop.Runtime/Libs/Il2Cppmscorlib.dll` 中完整引用程序集的 `.ctor(String)` 当成设备契约。运行时必须通过 native metadata 查找并调用真实 IL2CPP 方法，不能直接 `new Il2CppSystem.Text.StringBuilder(string)`。

转换是**拷贝**语义，不是别名——两个 builder 在不同堆上，无法共享存储。对已审计调用点是正确的：全部形如 `text.SetText(sb)`，Unity 在该次调用内读走字符并自留副本，从不持有 builder，所以后续 MOD 侧 append 本来也观测不到。若有调用者指望 Unity 持活引用，这个实现会破坏它——故明写而非默认。

### 4.3 `GUILayout.SelectionGrid(Int32,String[],Int32,GUILayoutOption[])`

自动转换器，已实现。选用只失配 1 处的重载（第 4 参保持托管 `GUILayoutOption[]`），`String[]` → `Il2CppStringArray` 用现成 `op_Implicit`。失配在第 1 位（4 参中），两个尾随实参由溢出机制转存局部再压回。JPOV 2 处 + JPKV 3 处。

### 4.4 `JsonUtility`（仅 JPKV）

**手工桥**（`PcCompatJsonBridge` + `PcCompatUnityJson`），已实现。

实参是 JPKV 自有的 `ProfileData` / `KeyViewerSettings` / `SettingsMeta`（纯 CoreCLR 类型，IL2CPP 类表里不存在）。IL2CPP 侧的 `JsonUtility` 靠反射读它拿到的对象，遇到不认识的类型只会失败或返回 `{}`——装箱转换器无论做得多好都解决不了。

#### 实际有 7 个调用点，不是 6 个

审计只报了 `ToJson(Object,Boolean)` ×3 与 `FromJsonOverwrite(String,Object)` ×3。但 `KeyViewer::LoadSettings` 还有一处 `FromJson<KeyViewerSettings>(String)`，**审计报它干净**——代理的泛型签名精确匹配。

这是一个**假阴性**：签名匹配只说明形态对得上，完全不说明 `T` 在 IL2CPP 类表里存不存在，而 `KeyViewerSettings` 不存在。放任它转发就是运行时静默失败，且任何审计数字都看不出来。因此 `FromJson<T>` 也登记为手工桥（`SourceGenericArity: 1`，现有 spec 机制直接支持泛型桥）。

由此得到一条一般结论：**"审计干净"不等于"运行时可用"。** 凡实参/泛型实参是 MOD 自有类型的成员，都要按 §2.3 判据主动归到手工桥，不能等审计报错。

#### 序列化器：手写最小 Unity-JSON 子集

目标是与 Unity 的**格式**兼容，不是与 .NET 惯例兼容——PC 上写的 profile 要能在这里读回，反之亦然。已实现并逐条测试的规则：

| 规则 | 与通用序列化器的差异 |
| --- | --- |
| 序列化字段，**不序列化属性** | `System.Text.Json` 默认相反 |
| 字段名原样，不 camelCase | 默认相反 |
| `[SerializeField]` 私有字段纳入，`[NonSerialized]` 排除，编译器 backing field 排除 | — |
| 枚举按**整数** | 默认按字符串 |
| 数组与 `List<T>` 按 JSON 数组 | — |
| struct 递归其字段（`Color` → `{"r":..,"g":..,"b":..,"a":..}`） | — |
| `null` 字符串写成 `""`，`null` 数组写成 `[]`，`null` 对象写成 `{}` | 默认写 `null` |
| `NaN`/`Infinity` 写成 `0` | 默认写 `NaN`，而 Unity 的 reader 读不回来 |
| 浮点用 invariant 格式 | 跟随当前区域会在欧洲语言设备上写出逗号小数点 |

`[SerializeField]` 按**全名**匹配而非类型匹配：该 attribute 来自生成代理，本程序集不引用它；按名字匹配还能容忍 MOD 用别的 Unity 版本编译。

**方向不对称，这是刻意的**：序列化严格（不支持的形态**抛异常**），反序列化宽松（形态不合的字段**跳过**）。因为写方向的结果会覆盖用户现有配置文件，一个"看起来合法但残缺"的 JSON 会毁掉它；而读方向遇到手改过或版本错位的文件，应当只丢一个字段而不是整个加载失败。Unity 本身就是这个不对称，对齐它才是正确行为。

`FromJsonOverwrite` 的**部分覆盖**语义被 JPKV 依赖：它 `LoadProfile` 时先换成全新默认实例再覆盖，正是靠"JSON 里没有的字段保持当前值"来保证跨 profile 不污染；`MigrateAllProfileFiles` 也靠"短数组反序列化后仍是短的"来识别旧版本 profile（其源码注释明确记录了越界风险）。这两条都单独有测试。

不用 `System.Text.Json`：上表前四行它默认全部相反，逐一扳平后代码量与手写相当，而剩下的行为差异难穷尽。

#### 一个被测试抓出的真实缺陷

初版实现里 `Dictionary<K,V>` 落进了"是 class 就递归字段"的兜底分支，会把它的私有 bucket 和 comparer 写成 JSON——**看起来合法、恢复不出任何东西，而且会被写进用户配置文件**。现已在兜底之前显式拒绝非数组/非 `List<T>` 的 `IEnumerable`。Unity 也不支持字典，所以大声失败既对齐了 Unity，也是调用方唯一能察觉的方式。

### 4.5 `TMP_FontAsset.fallbackFontAssetTable`（JRP、JPOV **与** JPKV 三家）

这一项是本轮设计里最重要的发现，与其余各类性质不同。

#### 现存缺陷

现有 getter 转换器是 `PcCompatCollectionBridge.CopyList` ——**拷贝语义**。三个 MOD 的 IL 形态完全一致（抄的同一段代码）：

```
callvirt get_fallbackFontAssetTable        // → CopyList，得到托管副本
newobj   List<TMP_FontAsset>::.ctor()
callvirt set_fallbackFontAssetTable        // 写入
callvirt get_fallbackFontAssetTable        // → 又一个副本
callvirt List<TMP_FontAsset>::Contains
callvirt get_fallbackFontAssetTable        // → 第三个副本
callvirt List<TMP_FontAsset>::Add          // Add 到副本 → 丢弃
```

**`.Add` 的结果永远回不到 Unity。** CJK fallback 字体静默失效——字符显示成方块，且不报错。补上 setter 转换器也不能修好它。

**这个缺陷现在就在已发布路径上。** 更正设计初稿的记载：不只 JPOV/JPKV，**JRP 自己也 `.Add`**（`BundleLoader.cs:42`，改写后落在 `BundleLoader::LoadBundle` @474 的 `CopyList` 副本上）。所以 §4.5 不是"新 MOD 的新需求"，而是修一个 JRP 今天就在踩的 bug——三家用法：

| MOD | 源码位置 | 形态 |
| --- | --- | --- |
| JRP | `BundleLoader.cs:42` | `.Add` |
| JPOV | `BundleLoader.cs:70`、`FontManager.cs:77-79` | `??=` + `Contains` + `.Add` |
| JPKV | `KeyViewerResources.cs:275-278` | `== null` 判空 + 显式 setter + `Contains` + `.Add` |

JPOV/JPKV 的判空分支还会调 **setter**（`set_fallbackFontAssetTable(List<TMP_FontAsset>)`），这就是审计里剩下的 3 处 methodIssue。注意 `CopyList` 永不返回 `null`（`source is null` 时返回空 `List`），所以改写后这些判空分支恒不成立、setter 恒不执行——方向上安全（不会误写空 List），但与 PC 行为不同，需一并处理。

#### 决策：可写集合属性名单 + 绑定拷贝写穿

已实现。建立声明式的 `ManagedWritableCollectionSpec` 名单，登记项的 getter 从 `CopyList` 换成 `CopyOrCreateBoundList`，同时把 `List<T>` 的变更调用重定向到写穿桥。

**实现比初稿设计简单，因为多了一条不变式。** 初稿要求"识别 getter 返回值上的变更调用"，即需要栈流分析证明接收者来自登记的 getter。实际做法是让四个写穿桥对**未绑定**的 List 完全等价于它们替换的 `List<T>` 成员（原地改，什么都不做别的）。于是"匹配错了"不再有后果——MOD 自建的 `List<TMP_FontAsset>` 被重定向后行为不变。因此改写只按**元素类型**匹配，不做栈流分析。

这条不变式是设计的承重墙，由 `PcCompatCollectionBridgeTests` 单独钉住：`Add`/`Remove`/`Clear`/`Insert` 在未绑定 List 上的返回值、副作用与异常类型（`ArgumentOutOfRangeException`、`NullReferenceException`）都与 `List<T>` 一致。

绑定用 `ConditionalWeakTable<object, object>`：键是 getter 每次新建的拷贝，故不同 MOD、不同调用之间不会串；弱引用使拷贝的生命周期归 MOD，强表会把 MOD 读过的每张表都钉住。

`Contains`/`Count`/索引器/枚举**不改**——当次 `get_` 刚同步过，读拷贝即可。重写器为可写 getter 保留 receiver，并把 receiver、原 getter 结果和属性名传给 `CopyOrCreateBoundList`。若 Unity 侧集合为 `null`，bridge 使用 generated corlib 实际提供的 `List<T>(Int32)` 容量构造创建空 IL2CPP List，再通过该 receiver 的唯一匹配 proxy setter 绑定；无参构造不在 dependency-closed 代理面。setter 缺失或歧义时失败关闭，不返回无法写回的空副本。

setter（`set_fallbackFontAssetTable(List<TMP_FontAsset>)`）不走名单，而是普通参数转换器 `PcCompatCollectionBridge.ToIl2CppList<T>`——与 `CopyList` 严格对称。null 透传为 null 而非空 List：Unity 区分"无 fallback 表"与"空 fallback 表"。

#### 兼容性约束

代理里返回 `Il2CppSystem.Collections.Generic.List<T>` 的成员有 4 个，但**真正有 callsite 经过 `CopyList` 的只有 3 个**（更正初稿的表格——它列的是代理成员，不是 MOD 用法）：

| 成员 | MOD 用法 | JRP 站点数 |
| --- | --- | ---: |
| `scrLevelMaker::listFloors`（字段） | JRP/JPOV **只读**（`Count`、`Last()`、`get_Item`、LINQ） | 10 |
| `scnGame::get_events` | JRP **与** JPOV 只读（`foreach`） | 1 |
| `TMP_FontAsset::get_fallbackFontAssetTable` | 三家**都要写** | 1 |
| `PlanetarySystem::get_allPlanets` | **无 callsite**：JRP 走反射 `obj.GetValue<List<scrPlanet>>("allPlanets")`，不经过转换器 | 0 |

只有 `fallbackFontAssetTable` 进名单；前两个继续走 `CopyList`，行为不变。**登记名单是"MOD 确实会改它"的明示，不是对所有 `List` 成员的批量升级。** 这个二分由 `ListSitesGetTheCopyConverterMatchingTheirWritability` 按名字逐项断言（谁拿 `CopyList`、谁拿 `CopyBoundList`），并由 `CopyListConverterIsEmittedDirectlyAfterTheProxyAccessor` 钉住 IL 形态（含字段路径 `call` 与方法路径 `callvirt` 的 opcode 差异）。JRP 的写穿由 `FallbackFontTableMutationWritesThroughToUnity` 验证：`BundleLoader::LoadBundle` 里必须出现 `AddToBoundList`，且改写后**不得残留任何** `List<TMP_FontAsset>` 的原生变更调用。

JPOV/JPKV 的判空分支（`??=` 与 `== null`）在改写后恒不成立：`CopyOrCreateBoundList` 永不返回 `null`；原生 null 初始化已由 bridge 使用真实 setter 完成，因此 MOD 自身的 `new List<>()` 分支无需再次执行。setter 仍按真实语义实现，因为 MOD 可以直接用一个已填充的 List 调它。

### 4.6 `scrController.txtLevelNameOriginalPosition` 的 `set_`（仅 JPOV，2 处）

代理里 setter 未生成。这不是形态失配，属代理面缺口，按 `JPOV_JPKV_COMPAT_GAPS.md` §0.0 的代理面扩容流程处理（补进 surface manifest 后重跑闭包与生成）。本文不覆盖。

### 4.7 `JipperKeyViewer.KeyViewer.RainGraphic`（仅 JPKV）

**已实现。逐项设计、决策依据与实现修正见 `RAINGRAPHIC_RENDER_CALLBACK_DESIGN.md`。** 本节只保留结论摘要。

`AddComponent` 桥此前拒绝它，报错文字是"MOD-owned component type does not derive UnityEngine.MonoBehaviour"。

#### 根因（实测，与报错文字不符）

`IsManagedOwnedMonoBehaviour`（`Program.cs:1992`）**已经是沿继承链上溯的**。真实失败原因在 `Program.cs:2036-2039`：它显式要求继承链上**每一环都在 MOD 自有模块内**。发布 DLL 里 `RainGraphic : UnityEngine.UI.MaskableGraphic`，碰到代理模块的类型链就断。

这是刻意约束而非疏漏：MOD 类型继承代理类，意味着 Unity 要能实例化并回调一个 IL2CPP 类表里不存在的类型——正是 managed component bridge 存在的前提所否定的。报错文字一并修正。

#### 两处此前记载被推翻

- **`JipperKeyViewer-1.7.0/` 就是发布版源码**，`RainGraphic.cs` 在其中。此前"1.7.0 之后源码已删除该类、无法交叉验证"的判断只看了 `JipperKeyViewer/`（后续开发版，确实已改为三层自绘）。立项对象是 1.7.0 形态——唯一有发布产物、可审计、可验收的形态。
- **hook 目标不能是 `MaskableGraphic::OnPopulateMesh`**：dump 确认它**不 override** 该方法（没有地址可 hook），且是 `abstract`（不能 `AddComponent`）。仍选 `RawImage::OnPopulateMesh`（RVA `0x47b9c78`），原结论成立但此前引用的 "dump `428594`/`426150`" 是行号而非地址。

#### 决策摘要

放宽判据改为**登记制名单**（不做"到达 `MonoBehaviour` 即可"的通用放宽——那会让继承 `Selectable`/`ScrollRect` 之类的类型得到"改写干净但静默失效"），并**同时**做宿主渲染回调桥：hook `RawImage::OnPopulateMesh`，命中 MOD 宿主时不执行原方法、直接转发给托管 `RainGraphic.OnPopulateMesh`（其第一行就是 `vh.Clear()`，语义与 PC 一致）。owner 筛选放在 native 侧指针集合预筛（扁平排序 vector + copy-on-write，读侧无锁）。

托管实例通过 `GetUninitializedObject` + `CreateGCHandle` + `isWrapped` 绑定到真实 `RawImage` 指针，并额外把 `RainGraphic..ctor()` 里的 `call MaskableGraphic::.ctor()` 改写为 **`pop`**——否则它会在已构造的实例上二次跑 native 构造（dump 已确认代理 ctor 里同时有 `il2cpp_object_new` 于 abstract 类与 `il2cpp_runtime_invoke` 于 `this`）。保留继承换来另外三处基类调用（`get_rectTransform`/`get_color`/`SetVerticesDirty`）零改动。

**驱动机制只部分复用了既有链路。** ABI 结构（`PcCompatManagedPrefixInvocationV2`）与 native 传输、排序、in-flight/退休处理全部复用；但 `CallbackBinding` 在构造时定死目标，而渲染回调的接收者按实例指针每次查出，且 JPKV 零 Harmony 用法所以没有 shim registration 可绑——因此新增 `PcCompatRuleOp.ManagedRenderCallback`（op 24）与独立的 `managed_render:` id 前缀。**id 前缀必须独立**：复用 `managed_prefix:` 会让 native 把它当普通 prefix 解析，owner 预筛随之丢失。

**顶点流地基已验证**：代理 `VertexHelper` 有 `Clear`/`AddVert`/`AddTriangle`/`get_currentVertCount`；`UIVertex` 是 valuetype，`position`/`color` 等字段可直读。`RainGraphic.AddQuad` 拿到的 `VertexHelper` 就是代理实例，**顶点数据不需要跨界封送**。

**实机渲染未验证**：三个 IL2CPP 相关的宿主操作（绑定、读指针、包装 `VertexHelper`）在本机测试里是 fake，被证明的是桥按正确顺序调用它们。

## 5. 落点

- 新增的宿主辅助方法加在 `StArray.ModManager.Android.PcCompat.PcCompatAbiBridge`（已有 `BoxUnboxedValue<T>`、`ToIl2CppNullable<T>`、`ToManagedNullable<T>`、`ToIl2CppDelegate<T>` 同类先例）。Nullable 写入的无值状态使用生成代理无参构造，读取通过 native `get_HasValue/get_Value`，不假设两侧 struct padding 相同。**已落地**：`ToIl2CppStringBuilder` 与 Nullable 双向字段转换。
- `Debug.Log/LogWarning/LogError` 落在 `xphorror.PcModCompat/src/PcCompatManagedLogBridge.cs`（原本只有 `LogException`）。**已落地。**
- 数组类转换器 `CreateArrayToIl2CppArgumentConverter` 照 `CreateArrayToManagedConverter` 的模式反向构造 `MemberRefUser`，不新增宿主方法。**已落地。**
- 可写集合属性的桥方法加在 `PcCompatCollectionBridge`（已有 `CopyList<T>`）。**已落地**：`CopyBoundList`、`CopyOrCreateBoundList`、`AddToBoundList`、`RemoveFromBoundList`、`ClearBoundList`、`InsertIntoBoundList`、`ToIl2CppList`。
- 可写集合名单为新 spec `ManagedWritableCollectionSpec`，由 `PcCompatAndroidManagedAssemblyRewrite.BuildManagedWritableCollections` 注册，并参与托管缓存键。**已落地。**
- `JsonUtility` 桥与 Unity-JSON 序列化器为新文件 `xphorror.PcModCompat/src/PcCompatJsonBridge.cs`、`PcCompatUnityJson.cs`。**已落地。**
- 改写机制变更（可写集合名单）在 `tools/ModAssemblyRewriter/Program.cs`：`MatchWritableCollectionGetter` 切换 getter 转换器，`CollectWritableCollectionMutations` 重定向变更调用。后者必须在"非代理程序集则跳过"之前调用——`List<T>` 在 corlib 里，否则永远到不了名单。**已落地。**
- 桥 ABI 与 `CacheFormatVersion` 需递增，使旧托管缓存失效。同时 `PcCompatAndroidInputContractTests` 里的固定 ABI 串必须同步，否则该测试会挡住版本递增。**当前已落地**：`v55-stringbuilder-native-materialization`、`PcCompatAbiBridge.v4-stringbuilder-native-materialization`，并保留既有 render-component ABI 内容。
- 渲染组件登记表为新文件 `src/PcCompatManagedRenderComponentCatalog.cs`，由改写器、recipe 编译器与组件桥三方共读（一份来源，三者不会不一致）；native 指针集合的托管封装为新文件 `PcCompat/PcCompatNativeRenderHostRegistry.cs`。**已落地。**

## 6. 实施顺序与回归防线

`CopyList` 所在的转换器路径**目前无任何测试钉住**——JRP 的 `listFloors`/`allPlanets`/`events` 三个只读站点全靠它，而 §4.5 要改的正是这条路。现有 `PcCompatManagedBridgeRewriteTests` 只断言了一处 `BoxUnboxedValue`。

因此顺序是：

1. [已完成] **先补基线测试**：在真实 JRP DLL 上钉住三个只读 List 站点的现有改写结果（转换器身份 + IL 形态）。这样可写集合名单若误伤只读路径，测试当场报错，而不是到实机才发现。
2. [已完成] 数组类两条转换器（现成 `op_Implicit`，风险最低）。
3. [已完成] `StringBuilder` 宿主辅助方法；`Debug.Log` 走手工桥（原计划的"`Object←string` 向前推断"经实测不必要，见 §4.1）。
4. [已完成] 可写集合属性名单 + `fallbackFontAssetTable`（含 setter 的 `ToIl2CppList` 参数转换器）。
5. [已完成] `JsonUtility` 手工桥 + Unity-JSON 序列化器（实际 7 个调用点，含审计报干净的 `FromJson<T>`）。
6. [已完成] `RainGraphic`：登记制放宽 + `RawImage` 宿主渲染回调桥。详见 `RAINGRAPHIC_RENDER_CALLBACK_DESIGN.md`。**JPKV 现在三类 issue 全零且 `outputWritten=True`；但改写干净不等于雨能渲染，实机未验证。**

每步之后 `PcCompatUmmModRewriteAuditTests` 里 JRP 与 `JAMod.Bootstrap` 必须保持零 issue、`outputWritten=True`；两个目标 MOD 的差距清单单调下降。JPOV/JPKV 的清单测试转为 `AssertClean` 即为完成定义——**JPKV 已转为 `JipperKeyViewerMainAssemblyRewritesClean`**。

差距清单由 `RemainingGapsAreExactlyTheKnownSet` **精确**钉住（不是上界）：既拦回归，也强制下一步显式更新清单而非静默缩小。已闭合项的转换器身份由 `ArgumentFormMismatchesResolveToTheIntendedConverters` 单独钉住——issue 消失只证明"不再报错"，不证明"解析到了预期的那个转换"。该测试同时断言 JPOV 的 `OutputWritten` 仍为 `False`，防止"审计安静"被误读成"MOD 可加载"。

### 6.1 已完成部分的实测结果

| | 起始 methodIssues | 步骤 1–6 后 | 阻塞项 |
| --- | ---: | ---: | --- |
| JipperResourcePack.dll | 0 | 0 | — |
| JAMod.Bootstrap.dll | 0 | 0 | — |
| JipperOverlayer.dll | 30 | **0** | — |
| JipperKeyViewer.dll | 16 | **0** | **无**（三类 issue 全零，`outputWritten=True`） |

此前渲染组件批次的全量托管回归为 `1171` 通过、`1` 个既有 XPerfect JIT 测试按原条件跳过；本轮代理面扩展的 JPOV/JPKV/JRP/JAMod/Loader 定向审计为 `16/16`。缓存 ABI 当前为 `v55-stringbuilder-native-materialization`，并保留 `PcCompatManagedComponentBridge.v9-render-component`；可写集合、渲染组件、proxy surface、字段 ABI 转换和 native StringBuilder materialization 均进入托管缓存身份。

**JPOV 与 JPKV 均已产出重写结果**（`outputWritten=True`），JPOV 的 nullable 字段 facade 已闭合。两者均只证明程序集重写链路可用，不意味着雨效果或其它 UnityMain 行为已经过设备验证——见 §7。

## 7. 明确不宣称

- §4.6 的字段 facade 与 Nullable ABI 转换已实现；JPOV `outputWritten=True`。真实 IL2CPP 写回、空 Nullable 的 native 语义仍需设备验收。
- **JPKV `outputWritten=True` 只意味着程序集可被加载与执行，不宣称雨效果渲染正确。** 绑定后的实例是否真收到 Unity 的 `OnPopulateMesh`、顶点是否正确提交、`.ctor` 置 `pop` 后 `RawImage` 内部状态是否完好、per-drop hook 在 108 键连击下的开销——全部需要设备。
- `Debug.Log(object)` 的桥只做 `ToString()` + 宿主日志，不宣称与 Unity 的 logger 行为（堆栈捕获、context 对象、Console 双写）等价。
- `ToIl2CppStringBuilder` 是拷贝语义，不宣称 Unity 侧能观测到转换之后的 MOD 侧 append。
- 可写集合的**写穿路径（绑定那一半）本机不可测**——构造 `Il2CppSystem...List<T>` 需要活的 IL2CPP 运行时。已测的是未绑定时的等价性（那条不变式支撑整个设计）与改写形态；`.Add` 真正到达 Unity 只能在设备上确认。
- Unity-JSON 序列化器覆盖的是上表列出的规则与 JPKV 实测用到的形态，**不宣称与 `JsonUtility` 完全等价**；未与真实 Unity 输出做过逐字节对比（那需要设备）。真实 profile 往返只能由用户确认。
- 所有实机行为（字体 fallback、profile 往返、雨渲染、日志落点）只能由用户在设备上确认。

## 8. 实现过程中被实测推翻的设计结论

如实记录，因为它们都改变了实现方向：

1. **`Debug.Log` 不需要"向前实参类型推断"，而且归错了机制。** 初稿设计了一套只认单指令的保守推断，用来把 `System.Object` 参数证明成 `System.String`。实测发现源签名本就是 `System.Object`、桥签名精确匹配，现有 spec 机制直接可用；而这一类恰好满足初稿自己在 §2.3 定的"语义归宿主"判据。改为手工桥后无需任何新推断机制。
2. **代理里的 `Il2CppSystem.Text.StringBuilder` 是不可用托管构造函数的 skeleton。** 初稿写"加宿主辅助方法"时未区分生成代理与 `Il2CppInterop.Runtime/Libs/Il2Cppmscorlib.dll`。此前实现误用了后者的完整 `.ctor(String)`，设备报告证明该假设错误；现改为 native metadata + `il2cpp_runtime_invoke` materialization，并用 generated wrapper 的参数槽 ABI 契约测试锁定。
3. **`PlanetarySystem::get_allPlanets` 没有任何 callsite 经过 `CopyList`。** 初稿表格把它列为"JRP 只读"，实际 JRP 走反射 `obj.GetValue<List<scrPlanet>>("allPlanets")`。同时 `scnGame::get_events` 被标为"JPOV 只读"，实际 JRP 也有一处。基线测试因此覆盖 3 个站点（12 个 callsite），不是初稿说的 3 个只读站点。
4. **`fallbackFontAssetTable` 的拷贝语义缺陷波及 JRP。** 初稿说"JPOV 与 JPKV"，实测 JRP `BundleLoader.cs:42` 同样 `.Add`。§4.5 因此不是新增能力，而是修一个当前发布路径上的静默失效。
5. **可写集合不需要栈流分析。** 初稿要求"识别 getter 返回值上的变更调用"，即证明接收者来自登记的 getter。实际做法是让写穿桥对未绑定 List 完全等价于原 `List<T>` 成员，于是误匹配无后果，按元素类型匹配即可。代价是把这条等价性变成必须测的不变式（`PcCompatCollectionBridgeTests`），收益是省掉一整套分析且不会误报 issue。全量实测：三个 MOD 里 `List<TMP_FontAsset>` 的变更调用共 4 处，全是 `Add`。
6. **`JsonUtility` 有 7 个调用点，不是 6 个；多出的那个审计报干净。** `FromJson<KeyViewerSettings>` 的代理泛型签名精确匹配，所以不进 issue 列表——但 `T` 不在 IL2CPP 类表里，转发必然运行时静默失败。由此得到一条一般结论并写进 §4.4：**"审计干净"不等于"运行时可用"**，实参或泛型实参为 MOD 自有类型的成员必须主动归到手工桥。
7. **JPKV 的最后阻塞项不是 methodIssue。** 步骤 5 之后 JPKV 的 methodIssue 与顶层 issue 全为 0，但 `outputWritten` 仍是 `False`——剩的是 `ManagedBridgeIssues` 里的 `RainGraphic`。此前的清单测试只钉了前两类计数，所以"清单为空"曾一度被我当成"可产出输出"。清单测试已改为三类分别断言并显式断言 `OutputWritten`。
8. **`RainGraphic` 在发布版源码里存在，而且 hook 目标选错过一次。** §4.7 此前记载"1.7.0 之后源码已删除该类"，实际是仓库有两份源码：`JipperKeyViewer-1.7.0/`（发布版，含 `RainGraphic.cs`）与 `JipperKeyViewer/`（开发版，已改三层自绘）。另外访谈中一度选定 hook `MaskableGraphic::OnPopulateMesh`，被 dump 推翻——它不 override 该方法、也没有地址，且是 `abstract`。详见 `RAINGRAPHIC_RENDER_CALLBACK_DESIGN.md` §0。
9. **§4.7"复用现有 managed-only Prefix 链路"只在 ABI 与 native 传输层成立。** 绑定层不能复用：`CallbackBinding` 构造时定死 callback 目标，而渲染回调的接收者按实例指针每次查出；且 JPKV 零 Harmony 用法，没有 patch descriptor / shim registration / callback translation item 可派生。因此新增 op 24 与独立 `managed_render:` id 前缀，规则由 recipe 编译器从共享登记表直接发出，不进 `PcCompatManagedOnlyCallbackCatalog`（那份按 Harmony descriptor 索引）。
10. **为渲染回调发出 recipe 规则会把 JPKV 推上"已验证 recipe"路径，从而完全跳过托管 setup。** JPKV 此前因零 Harmony 而 `hasRecipe=false`、走 self-render 兜底；一旦有了规则，`hasRecipe` 变 true，`PcCompatRuntime` 就打印"loaded from verified rule recipe"并 `return`——hook 装上了而 MOD 托管代码从未运行。这是实现中发现的一个会静默失效的交互，修复是让渲染规则强制 `requiresManagedSynchronousPrefix`。
11. **登记名单里声明的基类必须校验，而且要校验两次。** 初稿把 `BaseType` 当描述性字段。实际它是判据：改写器沿链走到第一个非 MOD 模块的基类并要求逐字相等，组件桥在 `AddComponent` 时再查一次已加载类型。两次不冗余——前者读磁盘上的程序集，后者读运行时真正加载的类型，代理重生成或 MOD 换版本只有后者能发现。JPKV 开发版已把 `RainGraphic` 换成三层自绘，说明这不是假想风险。
