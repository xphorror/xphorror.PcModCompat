# PC MOD 兼容层架构

## 文档入口

当前代码完成度、验证证据和剩余实施顺序统一记录在 [`IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md)。本文描述长期架构，不再重复维护容易漂移的逐项状态；两者冲突时，以运行时行为、测试结果和实施状态文档为准。

## 命名

目录名：`xphorror.PcModCompat`

含义：

- `xphorror` 表示兼容层由我们维护，不归属上游 StArray ModManager。
- `PcModCompat` 表示目标是兼容 PC MOD 生态。
- 不命名为 `UnityModManager`，避免误导为完整 UMM 移植。
- 不命名为 `JAModCompat`，因为最终范围不只 JAMod。

## 进程级所有权边界

ModManager 是当前进程中唯一的 IL2CPP 基础设施所有者。凡是会改变 IL2CPP 或本机代码全局状态的操作，都必须由 ModManager 统一执行：

- `libil2cpp.so` readiness、domain/thread attach、导出符号和 runtime metadata 解析。
- HookBroker、HookSlot、Dobby 安装、original continuation 和永久 dispatcher binding。
- generated proxy Runtime 的启动、程序集身份校验和进程级缓存。
- 后续受控类型注入所需的 `Il2CppClass`、vtable、MethodInfo、native thunk、delegate 和 GC root 生命周期。

`xphorror.PcModCompat` 保留 MOD 语义层职责：扫描、重写、PATCH/Harmony 翻译、资源策略、managed lifecycle 和 capability 判定。它向 ModManager 提交经过验证的 `HookPlan`、`RuntimeQueryPlan` 或后续 `TypeInjectionPlan`，不得自行安装 Dobby、修改 IL2CPP class table 或维护第二套 metadata resolver。

这条边界不要求每次 generated proxy getter/setter 都绕经一个 ModManager RPC。已经完成 metadata/ABI 校验的普通 Unity API 调用仍可在 UnityMain 通过 generated proxy 直接执行；必须集中的是解析、Hook、注入、对象身份和其它进程级可变状态。

Hook 与类型注入是两类不同的永久资源：

```text
HookSlotRegistry       -> 函数入口和 rule chain
InjectionTypeRegistry  -> 注入类型身份、schema 和进程期保活
```

当前生产实现已经集中 Hook 所有权，但 `ClassInjector` 仍硬禁，`InjectionTypeRegistry` 尚未实现。未来开启真实 IL2CPP `Component` 身份时，注入内部 detour 也只能使用 ModManager 保留的 infrastructure hook layer，并通过 HookBroker 安装；不能占用普通 fixed-rule dispatcher，也不能由 MOD 直接调用 ClassInjector。注入类型注册后保持到进程退出，关闭 MOD 只停用行为，彻底移除需要重启。

## 兼容对象

优先兼容以下 PC MOD 形态：

- UnityModManager `Info.json` + `EntryMethod`
- JAMod `Info.json` + `JAModInfo.json`
- JALib `JAMod` 派生类
- 使用 Harmony/JAPatch 注册 patch 的 MOD

不优先兼容：

- 强依赖 Windows API 的 MOD
- 强依赖 PC Mono 游戏域对象直接访问的 MOD
- 需要任意 Harmony transpiler 修改 IL2CPP native 逻辑的 MOD

## 加载流程

动态 PATCH 翻译、native HookManager 和编译缓存的长期方案单独记录在：

- [`IL2CPPINTEROP_MIGRATION.md`](IL2CPPINTEROP_MIGRATION.md)
- [`MVP_FEATURE_RECIPE_PLAN.md`](MVP_FEATURE_RECIPE_PLAN.md)
- [`DYNAMIC_PATCH_TRANSLATION_AND_NATIVE_HOOK_MANAGER.md`](DYNAMIC_PATCH_TRANSLATION_AND_NATIVE_HOOK_MANAGER.md)
- [`NATIVE_HOOK_MANAGER.md`](NATIVE_HOOK_MANAGER.md)
- [`TRANSLATOR_PIPELINE.md`](TRANSLATOR_PIPELINE.md)
- [`ANDROID_MOD_INTEROP_AND_VIRTUAL_INPUT_CONTRACT.md`](ANDROID_MOD_INTEROP_AND_VIRTUAL_INPUT_CONTRACT.md)

```text
mods/<mod>/
  Info.json
  JAModInfo.json?
  *.dll
        |
        v
xphorror PcModCompat scanner
        |
        v
UMM/JAMod metadata model
        |
        v
Assembly resolver + shim assemblies
        |
        v
EntryMethod / JAMod setup
        |
        v
Patch registry
        |
        v
Native IL2CPP event bridge
```

主 ModManager 当前默认仍不执行 PC MOD 托管代码，而是使用 recipe/cache/native HookManager 链路：

```text
ModLoader.LoadMod
        |
        v
PcCompatRuntime.RegisterMod
        |
        v
PcCompatResourceAssemblyCompile
  (worker-only, valid recipe fast path)
        |
        v
PcCompatRecipeCompiler
        |
        v
PcCompatRecipeBundleCache
        |
        v
hook_rules.json
        |
        v
PcCompatDobbyBridge -> Native HookManager
```

这描述的是当前发布默认值，不是最终 HUD 后端。最终策略已冻结为 `ManagedSelfRender` 默认：经过重写和能力门禁的 MOD 自己在 UnityMain 通过 generated proxy 绘制；readiness 失败时默认失败关闭。verified recipe/兼容代绘只能由用户逐 feature 手动开启，不再自动 fallback。当前 self-render 仍通过显式测试开关启用，待后端选择持久化、失败 UI 和禁止自动 recipe fallback 的门禁完成后再切换发布默认。

## 通用规则选择契约

运行时禁止按 MOD ID、显示名、程序集名或入口类型选择兼容实现。上述字段只能用于日志、缓存隔离和 UI 展示，不能决定是否安装 rule、是否启用 Unity HUD 或是否提供 ReversePatch bridge。

当前通用选择链路是：

```text
active PATCH descriptor
  -> 游戏目标 type/method + 参数 ABI
  -> callback IL 安全检查
  -> callback 的受支持领域调用效果
  -> verified fixed-op rule
  -> capability gate
  -> native HookManager / Unity HUD adapter
```

判定依据：

- hook 目标必须命中受支持的游戏领域事件目录。
- callback 必须是允许的 patch stage，并通过指令数、异常区、未知调用、写字段、循环形态等检查。
- callback 的效果按方法语义匹配，例如 `Show`、`Hide`、`UpdateAccuracy`，不要求特定命名空间或 MOD 类型。
- coop 单玩家投影仍要求已审计的完整 opcode、字段集和 back-edge 形态；只把 MOD 自身的 `*.Instance` 字段视为可变部分。
- recipe 只由已经翻译成功的 rule 生成。没有验证证据的 MOD 不会因为名称命中而获得内置 recipe。
- `scrController.QuitToMainMenu -> OverlayHide` 是平台生命周期兜底，仅在 recipe 已声明 `UiOverlay` 时补入。
- 标准 Unity HUD 只检查 recipe 是否同时具备 overlay show 与已支持 telemetry rule，不检查 MOD 身份。
- ReversePatch bridge 按操作语义（如 `GetPlayerCount`）匹配，声明这些 stub 的 PC MOD 类型名不参与选择。

JipperResourcePack 继续作为首个完整回归样本，但不再拥有生产代码专属分支。

开发期 oracle 仍可显式打开，用来执行 PC MOD setup 并对照 patch snapshot：

```text
ModLoader.LoadMod
        |
        v
PcCompatRuntime.RegisterMod
        |
        v
PcCompatManagedLoader.Load
        |
        v
managed session + patch snapshot
        |
        v
PcCompatPatchRegistry
```

`PcCompatManagedModSession` 持有重写后 PC MOD 的 `AssemblyLoadContext`、主实例、setup/enable 状态和注册到的 patch 列表。它只在 `STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE=1` 或受控 self-render 启用时创建；未重写 DLL 默认拒绝执行。卸载 MOD 时 `PcCompatRuntime.UnregisterMod()` 会释放 session 并清理 registry。

当前 `JAMod` shim 把生命周期拆成两段：

- `CompatSetup(modPath)`：只设置路径、执行 `OnSetup()`、注册 patch 意图。
- `CompatEnable()`：后续 native bridge 准备好后再执行 `OnEnable()`。

这个拆分是有意的。JipperResourcePack 的 `OnEnable()` 会构造 overlay，并立即调用 `VersionSafe.GetHitMarginsCount()` 等 ReversePatch 方法。PC 端这些方法会被 Harmony/JAPatcher 替换成游戏真实实现；Android 当前阶段还只是 `registered_only`，提前执行会触发 MOD 自身的 `NotSupportedException` stub。

## 二进制加载探针

`tools/PcCompatProbe` 是第二阶段的本地验证入口。它必须覆盖完整加载前半段：

```text
Info.json/JAModInfo.json
        |
        v
manifest model
        |
        v
UMM ModEntry shim
        |
        v
AssemblyLoadContext + shim resolver
        |
        v
JAMod main type construction
        |
        v
CompatSetup(modPath)
        |
        v
registered patch snapshot
```

probe 参数：

- 默认：只执行 manifest -> direct JAMod setup -> patch snapshot。
- `--bootstrap`：额外尝试执行 PC `EntryMethod`，用于观察 bootstrap ABI 缺口；失败不阻断 direct JAMod setup。
- `--enable`：强制执行 `CompatEnable()`，用于定位 runtime/bridge 缺口；当前不是通过项。

`--enable` 只验证离线 probe/shim 环境，不代表 Android generated-proxy、native snapshot 或 managed self-render 的当前能力。Android 的 ReversePatch 直接调用点、VirtualBundle 和 managed lifecycle 状态统一以实施状态文档和实机报告为准，不能再用旧 probe 的 `NotSupportedException` 作为当前主链结论。

## Shim 部署约定

managed loader 会按顺序查找 shim 目录：

1. `AppContext.BaseDirectory\pc_compat_shims`
2. `AppContext.BaseDirectory\xphorror.PcModCompat\out\shims`
3. `AppContext.BaseDirectory\out\shims`
4. `<mod>\shims`
5. `<modsRoot>\pc_compat_shims`
6. `<modsRoot>\xphorror.PcModCompat\out\shims`

Android 发布包应使用第 1 种或第 5 种稳定目录。找不到 shim 时视为加载错误；不能把这种情况标为 `registered_only`，否则 UI 会误报 MOD 已加载。

## Generated proxy 部署约定

generated proxies 与 shim 即使文件名相同，职责和加载上下文也完全不同：

- `pc_compat_shims` 只进入每个 PC MOD 的自定义 `AssemblyLoadContext`。
- `pc_compat_proxies` 只进入 CoreCLR default `AssemblyLoadContext`。
- shim 用于保留 PC API 形状；proxy 用于访问 Android IL2CPP 对象。
- default ALC 已存在同名非 proxy 程序集时必须失败，不能复用或覆盖。
- `Il2CppInterop.Runtime/Libs/Il2Cppmscorlib.dll` 只是编译期引用程序集，方法体含 `throw null`，禁止进入运行时包。运行时必须使用 dependency-closed generated `Il2Cppmscorlib.dll`，并与其他 generated proxy 使用同一 surface/metadata 闭包。
- `pc_compat_shims` 只允许 `UnityModManager/JALib/0Harmony/Newtonsoft.Json`；手写 Unity/游戏 stub 只进入 `out/legacy_shims` 的显式离线测试路径。
- 当前默认闭包为 165 个精确输入类型，输出 13 个 generated assemblies、176 个生成类型；新增 `UnityEngine.TextCoreFontEngineModule.dll` 以覆盖静态 TMP 重建所需的 `FaceInfo/Glyph*`。打包脚本会校验 runtime/proxy/output 三份 generated corlib 及全部代理资产的一致性。

Android 启动链在 UI 打开前加载并验证当前闭包生成的 13 个代理程序集（含 generated corlib）。代理加载本身不执行游戏 Hook；所有 Hook 仍由 native permanent slot 管理。

静态 TMP 字体不通过 Android Unity 直载 PC/Linux bundle。导入期由 AssetsTools.NET 将 face、glyph/character table、atlas、Material 和样式参数编译为 Resource IR；运行时在 UnityMain 以 capability font clone 提供合法 `TMP_FontAsset` 对象身份，再用 generated proxies 覆盖 MOD 数据并重建 lookup table。TextCore/TMP 对象采用默认 IL2CPP 分配和 metadata 字段访问，不依赖离线 dump 中存在但手机 runtime metadata 未必暴露的参数构造器。capability clone 不是视觉字体来源，只有 Android Shader 仍由 capability Material 提供，因此该链路标为 `Compatible`。源 feature table 为空时保留 clone 已初始化的 feature table；非空 OpenType feature table 和动态字体保持失败关闭/显式 fallback。

## Patch 处理策略

PC 端：

```text
Harmony/JAPatch -> Mono method body patch
```

Android 端：

```text
Harmony/JAPatch metadata
  -> static translator / verified recipe
  -> Native HookManager + HookBroker
  -> fixed-op / Rule VM / UnityMain presentation
```

也就是说，PC MOD 看到的是类似 UMM/JALib 的 API；真正 hook Android 游戏逻辑的是 native 层。

## Assembly-CSharp proxy 与 legacy stub 策略

PC MOD 通常会引用 `Assembly-CSharp.dll` 里的类型，例如：

- `scrController`
- `scrPlayer`
- `scrPlanet`
- `scrMarginTracker`
- `scnEditor`
- `RDC`
- `ADOBase`

Android CoreCLR 里没有真实 PC Mono `Assembly-CSharp.dll`。当前保留两种严格隔离的同名程序集：

- `out/legacy_shims/Assembly-CSharp.dll`：只供显式离线测试；不进入 Android runtime assets。
- `pc_compat_proxies/Assembly-CSharp.dll`：供重写后的 Android MOD 访问真实 IL2CPP 对象，是唯一生产同名程序集。

legacy shim 不是完整游戏源码复刻，也不能读取生产游戏状态；proxy 只暴露经过 Android metadata 验证且进入成员闭包的 surface。

## 通用 fixed-op 事件目录

当前已验证的游戏领域事件：

- `scnGame.Play`
- `scrPressToStart.ShowText`
- `StateBehaviour.ChangeState`
- `scrUIController.WipeToBlack`
- `scnEditor.ResetScene`
- `scrController.StartLoadingScene`
- `scrMarginTracker.AddHit`
- `scrMarginTracker.Reset`
- `scrMarginTracker.CalculatePercentAcc`
- `scrPlayer.Hit`
- `scrPlayer.Die`
- `scrPlanet.MoveToNextFloor`

当前还已接入 ResourceChanger 安全子集的目标：

- `scrFloor.Start`
- `scrFloor.SetTileColor`
- `scrLogoText.Awake`
- `scrLogoText.UpdateColors`
- `scrLogoText.LateUpdate`
- `scnEditor.OttoUpdate`
- `scnEditor.OttoBlink`（由已验证的 `OttoUpdate` 资源映射派生的 after-original companion）

这些资源目标只执行已审计 fixed-op，不等同于 Resource IR/VirtualBundle。后者已支持受限 Texture/Sprite/Material、静态 TMP 和 PrefabGraph v1，但仍不表示任意 AssetBundle、动态字体或桌面 Shader 已完成。

## 验证标准

每个兼容能力都必须有三类结果：

- `supported`：已实现并验证。
- `registered_only`：MOD 可以注册，但 native 事件尚未映射。
- `unsupported`：明确不可支持或暂不支持。

日志必须能输出：

```text
mod=<id>
patch=<target type>.<method>
kind=<prefix|postfix|replace|transpiler>
status=<supported|registered_only|unsupported>
reason=<text>
```

## JipperResourcePack 回归样本状态

已验证：

- `Info.json` / `JAModInfo.json` 可驱动 probe，不再依赖硬编码 DLL 名称。
- `UnityModManager.ModEntry` shim 可构造并传入 bootstrap。
- `JAMod.Bootstrap.Bootstrap.Setup` 可被 `--bootstrap` 调用并返回；样本包会报告缺少 `JALib.Bootstrap`，但 direct JAMod setup 不受影响。
- `JipperResourcePack_release\JipperResourcePack.dll` 可通过 shim resolver 加载。
- `JipperResourcePack.Main` 可实例化。
- `CompatSetup(modPath)` 可返回。
- 主项目 `PcCompatManagedLoader` 可在测试中执行同样链路。
- `PcCompatRuntime.RegisterMod()` 默认只写 recipe/cache，并把 runtime bundle 交给 native HookManager。
- 设置 `STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE=1` 后，`PcCompatRuntime.RegisterMod()` 会用改写产物和 generated proxies 创建 managed session，并把 patch snapshot 写入 `PcCompatPatchRegistry`。
- `VersionSafe.Setup()` 内 9 个 ReversePatch 请求可注册：
  - `ColorLogoSafe`
  - `CalculatePercentAcc`
  - `GetHitMarginsCount`
  - `GetPlanetSpeed`
  - `LoadScene`
  - `GetPercentAcc`
  - `GetPercentXAcc`
  - `IsCoopMode`
  - `GetPlayerCount`
- probe 可输出 9 条 patch snapshot，后续 native bridge 可以用这份清单做白名单映射依据。
- `PcCompatPatchRegistry` 已提供按 kind、按目标和按 callback 的查询入口，并支持 bridge 回写 patch 状态。
- `PcCompatStaticPatchScanner` 可在不加载 MOD DLL 的情况下恢复 direct `JAPatchAttribute`；JipperResourcePack 当前恢复 40 条，r143 生效 32 条。
- `PcCompatDynamicAddPatchScanner` 使用只读 IL decoder 恢复 `AddPatch(Delegate, JAPatchAttribute)`，并对 `VersionControl.releaseNumber` 的简单比较分支做可达性分析。`VersionSafe.Setup()` 的 R136/R141 两组共 18 条已全部恢复，r143 生效 9 条。
- `PcCompatRestrictedAddPatchInterpreter` 支持 `MethodInfo` 局部变量、有限字符串数组、`ldelem.ref` foreach 和 `TryingCatch` 字段覆盖。`ResourceChanger.Patch()` 共恢复 16 条版本化 descriptor：r0..129 使用 `scrPlanet`，r130+ 使用 `PlanetRenderer`，每个版本分支 8 条。
- r143 的 9 条 ReversePatch 与 managed oracle 的 target、kind、callback、`NeedInstance` 集合完全一致。
- r143 的 8 条 ResourceChanger Prefix 与 managed oracle 的 target、kind、callback 集合完全一致，且 `TryingCatch=false` 已保留。
- 静态报告写入 `<mod>/.pccompat/static_patch_scan.json`，包含版本门禁、`NeedInstance`、`TryingCatch`、显式参数类型和发现来源；派生的 `ActivePatches` 不重复写入 JSON。
- descriptor 还会保留 callback 的完整参数类型；callback translator 使用类型、方法名和参数签名定位重载，不再按方法名猜测。
- `PcCompatCallbackTranslator` 当前从 r143 active descriptor 自动翻译 31 条 fixed-op callback。报告写入 `<mod>/.pccompat/callback_translation.json`；recipe 直接由这些验证通过的 rule 生成，不再先按 MOD 身份选择静态 recipe。
- accuracy、margin hit、margin reset 3 条 callback 含 coop 玩家索引循环。项目当前不支持多人模式；translator 只接受已经逐 opcode、字段、调用集和唯一 back-edge 验证的固定循环，并把它投影为单玩家 `player 0` 语义。其它任意循环仍拒绝。
- 当前 Jipper 样本的 metadata/dynamic descriptor 扫描 issue 为 0。ResourceChanger 已作为 descriptor-only 领域映射进入可执行 runtime rule：PC callback IL 不在 Android 执行，native fixed-op 只实现审计过的安全子集。

当前仍为 `registered_only` 的部分：

- Harmony/JAPatch 不做通用 IL patch。
- ReversePatch 已能把静态 descriptor 可证明的直接调用点改写到 managed bridge；反射、委托调用和任意方法体替换仍不支持。
- `CompatEnable()` 只在受控 managed self-render/session 门禁通过后执行，当前不是发布默认加载步骤。
- 普通 PC MOD callback 仍不从任意 Hook 线程直接分发；同步语义由 translator 证明后的 fixed-op/rule 执行，展示型托管回调必须经 typed snapshot 和 UnityMain dispatcher。

当前已由 native fixed-op 观测规则接入的部分：

- overlay 生命周期：`scnGame.Play`、`scrPressToStart.ShowText`、`StateBehaviour.ChangeState`、`scrUIController.WipeToBlack`、`scnEditor.ResetScene`、`scrController.StartLoadingScene`。
- 玩家数：`scrMistakesManager.SetPlayerCount`。
- 判定事件：`scrMarginTracker.AddHit`、`scrMarginTracker.Reset`。
- 过地板事件：`scrPlanet.MoveToNextFloor(scrFloor,float,HitMargin)`，只记录 floor move 次数、`exitAngle` 和 `hitMargin`，不解析 `scrFloor`。
- 玩家事件：`scrPlayer.Hit(bool)`、`scrPlayer.Die(bool,bool,string,bool)`，只记录 hit/death 次数和安全 bool 参数，不读取 `failMessage`，不解析 `playerID` / `planetarySystem`。
- Timing 事件：`scrMisc.GetHitMargin(...)`，先保留官方返回值，再记录 JipperResourcePack 用于 timing 文本的 ms 偏移和 `HitMargin` 返回值。
- Jipper ResourceChanger 的 R143 目标已覆盖 17/17：其中 16 个目标安装物理 Hook；8 字节的 `PlanetRenderer.SetRainbow(bool)` 紧邻 `SetTailColor(Color)`，小于 HookBroker 安全覆写长度，因此标记为 `SkippedKnownConflict`，由 `PlanetarySystem.RainbowMode`、`PlanetRenderer.SetColor/LoadPlanetColor` 和五个颜色 setter 的组合规则覆盖。`scrPlanet.Start` 后按原顺序执行 `DisableAllSpecialPlanets -> PlanetSprite.sprite = ADOBase.gc.tex_planetWhite -> SetPlanetColor -> SetTailColor`；`scrFloor.Start`、`scrFloor.SetTileColor(Color)`、Rainbow/Enby 与 `scrLogoText.Awake/UpdateColors/LateUpdate` 保持原 Prefix/after-op 语义。所有目标仍由 ModManager metadata resolver 合并到共享 HookSlot，不使用 RVA/VA，也不允许功能模块自行安装 Dobby hook。
- 编辑器兔子的 `Auto` Sprite 只能从 MOD 自带 bundle 编译出的 Resource IR，经 owner/session-aware VirtualBundle 在 UnityMain 重建并发布真实 IL2CPP identity。状态先于 Android sink 或 VirtualBundle 注册时会缓存；sink 注册与 VirtualBundle session ready 都会重放最新状态。同 owner/generation 的 Sprite 发布去重，native 使用 generation 校验和 GCHandle 持有，禁止读取 MOD 目录 PNG、runtime 内置 `Auto.png` 或调用 `TextureManager.LoadNewSprite`。官方 `OttoUpdate()` 每帧写 Sprite，`OttoBlink()` 又会在击打时直接写 `autoSprites[2..5]`；translator 因此只在 `OttoUpdate` callback 已通过 fixed-op 审计时派生 `OttoBlink` after-original companion，两条路径都重新应用 MOD Sprite 与当前 AUTO 颜色。诊断导出包含 `requested/resolved/published/retired/failure/lastError`。Logo Awake 同步火/冰颜色，并按原 MOD 几何克隆 `Education Edition`、写入动态 `ResourcePackName/TitleColor`、字号和位置。
- managed state adapter 读取原 `_settings`、`PlanetColor/TitleColor/TileColor` 与 `ResourcePackName`，只在变化时发布；因此 Jongyeol `FeatureReset` 和原菜单变化会更新 native，兼容菜单写回同一 `_settings`。`RDC.set_auto(bool)` 使用现有 `StaticVoid1` dispatcher，使 JStatus 自动模式回调进入正常事件链。开关关闭或 MOD 卸载时在 UnityMain 恢复兔子原 Sprite、官方星球颜色、Beat 默认色和 Logo 官方颜色，场景退出释放追踪句柄。
- 以上是 Jipper 已审计的完整资源替换适配，不代表任意 MOD 的通用资源层已经完成；动态字体、任意 bundle 对象图、通用 shader semantic matching 和异步 AssetBundle API 仍受 Resource IR/VirtualBundle 白名单限制。

ReversePatch 的查询语义：

- 普通 patch 的 `TargetType.TargetMethod` 是游戏函数。
- ReversePatch 的 `TargetType.TargetMethod` 是 PC MOD 内等待被替换的 stub，例如 `JipperResourcePack.VersionSafe.GetHitMarginsCount`。
- ReversePatch 的 `CallbackType.CallbackMethod` 是 PC 版替换逻辑，例如 `GetHitMarginsCountR141`。Android 侧不应直接执行它，而应提供等价的 bridge API。

已新增 `PcCompatReversePatchBridge` 作为托管状态桥骨架：

- 它维护按操作语义索引的 ReversePatch handler catalog；Jipper 的 9 个 stub 是当前回归覆盖，不是类型白名单。
- 它通过 `PcCompatGameSnapshot` 暴露命中统计、速度、准确率、进度、combo、尝试次数、玩家数和场景名。
- 它只读取最近一次 native bridge 发布的 snapshot；数据不可用或 generation 不匹配时失败关闭或返回该操作定义的安全值。
- `ModAssemblyRewriter` v14 已覆盖静态 descriptor 驱动的直接 stand-in callsite，并支持精确静态字段常量 oracle。当前 PC MOD 的 `Assembly-CSharp!ADOBase.platform` 读取在 MOD 重写产物内替换为 `Platform.Windows(3)`，不修改游戏全局 IL2CPP 字段；该能力不等于任意 ReversePatch 方法体替换，也不自动授权 `CompatEnable()`。

已新增 `PcCompatNativeBridge` 作为当前 Android bootstrap 模式下的反射入口：

- `PublishGameSnapshot(...)`
- `UpdatePatchStatus(...)`
- `ConsumeRequestedSceneName()`

这三个入口是托管静态方法，不是当前 SO 的 C 导出符号。native 侧后续应通过已运行的托管 runtime 定位并调用它们，或者在切换 NativeAOT/显式导出后再对齐 `bridge/xphorror_pcmod_compat_bridge.h`。

Android 侧已新增 `PcCompatDobbyBridge`：

- 随 `Managed.EntryCore()` 启动注册。
- 监听 `PcCompatRuntime.RegistryChanged`。
- 只同步 runtime bundle、诊断 provider 和低频 snapshot bridge，不再直接安装任何游戏方法 Dobby Hook。
- 同步 recipe cache 的 `hook_rules.json` 到 native HookManager，并让 HookManager 对通过 capability/ABI/stage/op gate 的 after-original observe target 安装 fixed dispatcher hook。
- `mvp-fixed-op-v3` 把 assembly、namespace、type、method、static、generic arity、return type 和 parameter types 全部写入 target，并只为仍需执行 MOD 托管行为的 verified Postfix 增发 managed-event 规则。已由 descriptor-only native fixed-op 完整消费的 callback 不再二次进入 managed dispatcher。native metadata resolver 先按完整身份唯一匹配，再用 `abiKind` 验证 AArch64 GP/FP dispatcher 布局；合并 bundle 对同一 target 声明冲突 ABI 时直接 `Faulted`。
- Dobby target 地址只从运行时 IL2CPP metadata 解析：`Assembly-CSharp -> type -> method -> FunctionPtr`，不使用 dump 偏移、RVA 或固定地址。
- ModManager 在 `ExtraMenuUnityPlayerActivity.onCreate()` 后后台启动；CoreCLR、MOD 扫描和 native HookManager 不等待菜单。菜单 ACTION 只显示 overlay。
- 隐藏启动期间不安装 ImGui/EGL renderer；`Managed.EntryCore()` 会在创建 Android UI platform 前直接 `ScanMods()`、读取 `modmanager_config.json` 并启动已启用 MOD 的后台翻译。托管 `Timer` 只低频调用 `RequestPendingLoadUpdate()`；`CompleteLoad/CompatSetup`、MOD 静态构造和 generated proxy 调用必须由 metadata-resolved PresentationSink 的 UnityMain 有界队列执行。只有管理面板可见或 ImGui fallback overlay 真正需要渲染时才懒安装 `eglSwapBuffers` hook。
- native coordinator 在规则加载后独立等待 `libil2cpp.so`、`global-metadata.dat` 和 `Assembly-CSharp` ready，再执行 `resolve -> prepare -> install`。解析使用轻量 IL2CPP API 目标查找，不依赖 UnityResolve 全量 metadata cache。
- 托管侧 `Dobby` 封装会登记已安装 hook，同一地址重复安装不同 detour 会被拒绝，避免和现有 EGL/Input/Vulkan hook 或后续 MOD hook 冲突。
- `CalculatePercentAcc` 由 native permanent slot 在调用原函数后直接读取 `percentAcc` / `percentXAcc`；ReversePatch getter 只在被读取时把 native bulk snapshot 同步到托管状态，不在 Hook 热路径进入 CoreCLR。
- progress/BPM/combo/attempt 也走同一 bulk snapshot。`scrController.PlayerControl_Update` 只新增一个共享 after-op `OverlayPollTelemetry`：每帧先做原子 generation 检查，音乐/谱面时间与检查点数据按 100ms 门控读取。music time 来自 `AudioSource.time/clip.length`，map time 使用 `addoffset + songposition_minusi` 并按末地板 `entryTime` 截断；checkpoint 列表在会话开始或地板数变化时通过 `List<scrFloor>` 与 `GetComponent(ffxCheckpoint)` 扫描。BPM/KPS 仍从官方 `GetHitMargin` 参数取得，combo 仍由官方 margin 事件推进。移动端 PlayCount 已持久化到 `<mod>/.pccompat/mobile_play_stats.json`，按关卡身份、起始进度和倍速区分，AUTO/noFail 会话不更新 attempt/best。
- `STARRAY_PCMOD_INTEROP_AUDIT=1` 时，按 1/128 采样把 generated proxy getter 与 native 字段读取结果比较；默认关闭并在首次异常后熔断。
- `hitMarginsCount` 使用 native `PcCompatHitMarginSnapshotV1` 批量快照镜像到单一稳定 `int[]`。native 通过 IL2CPP metadata 动态解析 `scrMistakesManager.marginTrackers` 静态字段和 `scrMarginTracker.hitMarginsCount` 实例字段，读取当前 `marginTrackers[0]`，不硬编码 dump 偏移。`SetPlayerCount` 替换 tracker、`AddHit/Reset` 原地修改数组、checkpoint 直接回写数组都由同一 generation/checksum 数据源覆盖；managed 仅在 generation 变化时拷贝，已删除每帧 generated proxy 构造、属性反射、逐元素 indexer 和 `object[]` 分配。稳定数组身份在 MOD 加载前确定，会话边界只原地清零。
- verified Postfix callback 已通过 native 每 MOD 2048 条有界事件队列延迟到 UnityMain managed dispatcher。环为通用 Show/Hide 生命周期预留 64 槽；managed ownership 尚未启用时保留最后一个生命周期事件，启用时原样入队，避免 MOD 在关卡中途完成加载后永久错过唯一 Show。Show/Hide/Reset/StartLoadingScene 等 lifecycle boundary 到达已启用队列时，会先丢弃此前尚未消费的普通事件，再把自身作为场景屏障入队；禁用 ownership 同样清空普通旧事件。这样旧场景的裸 `__instance` 不会越过 Hide/Reset 进入下一场景。判定目标的 144 字节 v2 事件记录同时携带该官方调用返回时的 `hitMarginsCount[16]` 快照，managed 在逐 callback 调用前发布对应快照，避免帧末延迟派发读取到同帧后续 Reset/Hide 的未来状态；native/managed 启动时握手 record size，混装资产直接失败。Hook fan-out 原子读取 immutable target snapshot，空帧 drain 使用 registry-generation/thread-local ring cache，均不进入主规则锁。无参和常见带参 callback 使用绑定期编译 delegate，反射只作异常形态兜底；绑定表和可预热入口在加载线程准备，单 callback 连续 8 次异常后退避 1 秒并允许恢复，不再永久熔断。Jipper r143 当前生成 18 条 managed-event 规则；ResourceChanger 的 17 个目标均由 descriptor-only fixed-op 执行，不生成重复 managed-event。同步 Prefix/返回值/skip-original 语义仍必须 native lowering，不能走延迟派发。
- fixed after-op 先提交 overlay、tracker 和资源状态，最后才把 managed event 入队，保证回调不读取上一拍快照。managed frame 的活跃 session 集合只在门禁变化时重建；稳态帧不再执行 `Where().ToArray()`、两轮 `Any()` 或无条件 gate 更新。active frame 直接由互斥安装的主/备用 UnityMain Canvas presentation hook 驱动，不使用 100ms gameplay telemetry anchor 去重；pending activation 才使用 250ms 限流。callback binding 复用参数/代理构造缓冲，无参 `void` 回调预编译为 `Action` 直调；boxed enum 类型名缓冲固定复用。整个 callback drain 在对应 session 的 owner-scoped `_updateContext` 中执行。
- MOD callback、lifecycle 和 `JALib.Tools.JATask.OnCompleted` 均捕获 owner-bound `PcCompatUnityMainExecutionContext`。owner 必须存放在被 await 捕获的 `SynchronizationContext` 实例中，不能在 CoreCLR 调用 `Post()` 时从 ThreadPool 的 `[ThreadStatic]` 反查。yield continuation 使用独立的 2048 槽有界队列，每次 UnityMain opportunity 最多执行 16 条；64 槽资源队列仍按一次一条推进，两类工作互不占用容量。owner/session 失效后丢弃；scheduler 缺失、异常、拒绝或 callback 执行异常只发布 session fault，`Post()` 不得向 `.NET TP Worker` 抛异常，也不得回退到 ThreadPool 调用 generated Unity proxy。标准 `YieldAwaitable` awaiter是该语义的唯一入口，shim 不自行创建线程。
- `JALib.Tools.MainThread.Run(owner, action)` 保持 JALib 原有的主线程队列语义。MOD 监听线程只入队，`JAMod.CompatUpdate` 在调用 MOD `OnUpdate` 前于 UnityMain 排空；单帧最多执行 4096 项。任务携带 owner 启用代次，MOD disable 后的旧任务即使同一实例重新 enable 也会丢弃，禁止在错误线程或错误生命周期修改 TMP、Graphic、Transform。队列计数只在主动诊断导出时格式化，不产生逐帧 Logcat。
- managed component bridge 在 JAMod update 后调度 MOD-owned `Awake/OnEnable/Start/Update/LateUpdate`。主动诊断导出逐组件类型、active/started 状态和各 lifecycle 调用次数，用于区分“JAMod 帧在跑”“组件未创建”和“组件已创建但 Update 未推进”；该观测不改变组件调度。
- managed frame 性能计数只保存在原子累计值中，主动导出时才格式化 `workUs/avgWorkUs/maxWorkUs/over4ms`；默认无逐帧日志。

Android 管理 UI 与通用已翻译状态 HUD：

- Document API 导入完成后由 `ModLoader.RefreshImportedMod(path)` 重新扫描并选中新条目，但保持 `NotLoaded`。用户通过列表中的“加载”按钮显式启动两阶段加载；加载中、已加载和错误状态分别提供“取消”“卸载”和“重试”。
- MOD 列表状态列同时显示图标和 `未加载/加载中/已加载/错误` 文字；异步翻译阶段继续显示进度和阶段名。
- `PcCompatModPlugin` 设置窗口分为 `MOD 设置` 与 `兼容诊断`。诊断页仍用于查看 translator、recipe 和 native HookManager，不再冒充 MOD 自身设置。
- 任何 recipe 满足标准 telemetry HUD 能力条件时都使用同一移动端设置适配器，不直接执行 PC 版 Unity `OnGUI()`。设置保存到 `<mod>/.pccompat/mobile_settings.json`。
- 已接通真实数据的 HUD 选项包括 progress、progress bar、BPM/KPS、combo、attempt、accuracy、X-accuracy、最近判定、击打偏移、玩家数，以及 HUD 尺寸、位置和背景透明度。
- accuracy/X-accuracy 由 native `scrMarginTracker.CalculatePercentAcc` after-op 直接读取 IL2CPP 的 `<percentAcc>k__BackingField` 与 `<percentXAcc>k__BackingField`，显示时再从官方 0..1 比例换算成百分数；兼容层不重算判定或准确率。
- HUD 使用显式关卡会话门禁。`scnGame.Play`/练习开始会重置本局快照并显示，三个原版 Hide 点及 `scrController.QuitToMainMenu` 兜底会隐藏；判定、偏移、死亡和进度事件在会话外全部丢弃，菜单点击不会污染最近判定。
- music/map time、checkpoint、best 和触摸 KeyViewer 已接入同一 ABI v3 bulk snapshot；native reader 继续兼容旧 v2/160 字节前缀，避免设备只替换 SO、未同步 runtime DLL 时整个 HUD 静默不可用。KeyViewer 的 Android Activity 路径只观测未被 ModManager 面板消费的 DOWN/POINTER_DOWN/UP/CANCEL，不消费游戏事件；native 保存 32 位 held slot、最后 DOWN/UP、总次数和一秒窗口 KPS，Unity HUD 仍只在 UnityMain 更新。当前 KeyViewer 是移动端文本/slot 视图，尚未复刻 PC 版方块样式和 rain 动画。
- KeyViewer raw physical 输入采用唯一 active producer：AsyncInput enabled 时由其 native observer ABI 在 capture/test-macro/gameplay gate 前发布，disabled/absent 时由 Activity pre-super dispatch 发布；producer epoch 切换先 CANCEL 旧 held，禁止 Activity 与 Async 双计数。
- RealtimeEventCore 同锁维护独立 raw count checkpoint：64 位 lifetime/session 总数、五套 `2/4/6/8/10` touch projection 和 canonical key identity。raw journal 已扩为 8192 槽；producer 追加后通过 native condition variable 唤醒共享 drain，journal 读取按连续 sequence 直接定位，不随容量线性扫描。journal 仍为有界且溢出必须显式 fault，通用 held/累计事实不依赖 journal 重放；checkpoint 仅是 Adapter 的输入恢复材料，不能覆盖 MOD 自己的 count/KPS/reset/persistence 规则。
- `GameplayAccepted` 由 HookBroker 对 metadata 精确解析的 `scrPlayer.HitInputEvent(bool, InputEventState)` 安装 `InstanceBoolBoolInt` after-rule。只有 original 返回 `true` 才进入独立 accepted ring；普通事件保持 GameAction identity，AUTO/oldAuto 由 `isAuto`、测试宏由 AsyncInput bridge 缓存的运行时开关标记 Synthetic，不污染 physical held/KPS/total，也不冒充判定结果。
- `keyviewer_adapter.json` 使用 `keyviewer-adapter-v2-lane-origin`，保存多 feature/lane-group、source profile、lane/role binding、BindingProvider 的 `ConsumerLaneBase`、visibility/inputActivation、MOD-owned count semantics 和 `Proven/Probable/Ambiguous/Unsupported` 证据。`PcCompatKeyViewerBehaviorScanner` 当前从精确 Legacy/Input System/Rewired/Win32 输入、PInvoke、调用与字段 writer-reader 图、局部 CFG back-edge、数组 provider/same-index consumer、结构化 identity transform、循环/时钟/队列/IO/Unity sink 生成保守候选；除保留方法内 local provider 证明外，还能从 `KeyCode[]` helper 参数反向还原调用实参，经过 owner 字段缓存及其 writer 追到零参数数组 provider，并从输入事务调用参数证明主组/附加组 lane 基址。仅由 `ldc.i4; ret` 构成且无字段、调用、分支或回边的零参数纯常量 getter 可折叠；其它来源保持未知。Android managed cache 把 analyzer version 纳入 key 并原子发布 Adapter 与 issue 文件。核心 readiness 只接受 Proven 闭包；用户确认只消除候选歧义。包/程序集/proxy/game revision 任一指纹变化都要求重新验证。
- 手动选择不回写自动生成的 Adapter，而保存到 MOD 私有 `.pccompat/keyviewer_overrides.json`。该文件必须携带完整程序集 SHA-256/MVID 集、行为包 SHA-256、游戏 revision 和 proxy surface，并且 role 只能引用 Adapter 已发布候选；不一致时整份配置失败关闭。ModManager 当前已提供逐 feature 输入模式、Touch lane 数和角色确认，并显示 RealtimeEventCore 报告的实际 Async/Official producer。
- Android 外接键盘快照只接受 `isExternal && !isVirtual && KEYBOARD_TYPE_ALPHABETIC`；仅声明 `SOURCE_KEYBOARD` 的媒体按键、遥控器和厂商 uinput 不再令 `Auto` 错误冻结为 External。诊断同时输出 requested/resolved mode、冻结 session、device flags 和冻结原因。
- KeyViewer 输入同时提供 snapshot 投影和有序 raw event ABI。`PcCompatKeyViewerPreviewRuntime` 为有效 override 建立逐 MOD cursor，并按 cursor 合并 native read；单个后台 native-wake 线程等待 RealtimeEventCore condition variable，UnityMain frame hook 只保留兜底提交。native read 和逐 MOD 纯状态推进由固定 2-worker 的共享 `PcCompatModActorRuntime` 执行。每个 MOD 拥有串行 mailbox，不创建逐 MOD 物理线程；空 batch 不进入 mailbox。mailbox 默认硬上限为 256，单 turn 最多执行 64 项并按 4ms cooperative slice 让出 worker；满队列拒绝新任务，由 feature 失败关闭，绝不等待或反压游戏输入。诊断导出包含容量、高水位、拒绝数和让出次数。`UINT64_MAX` cursor 从当前 journal 尾开户；运行期 gap、非法 session/producer 代际变化均失败关闭并只熔断对应 actor。首次加载且没有用户配置时，可证明的 input + 唯一 proven identity transform 会生成并保存推荐 Auto 配置，使 consumer 在 MOD 完成加载时立即注册；BindingProvider 未手选时，单候选可直接采用，多候选只接受唯一且已证明 `ConsumerLaneBase == 0` 的主组，其余歧义继续失败关闭。lowered plan registry 不再要求同一 provider 还出现在手动确认列表，但仍重新验证 MOD/revision/proxy 指纹、provider 候选归属、lane 数量与连续性、identity domain；自动候选和手动候选最终共用 `VerifiedLoweredBinding` 资格。
- 行为扫描可证明 `KeyCode < 0x1000 ? Unity Input : Win32(value - 0x1000)` 以及 `KeyCode[]` 元素直接进入 `UnityEngine.Input.GetKey` 的 identity transform。确认的动态 `BindingProvider` 在 managed session 建立后按当前计划的 `requiredCount` 只解析 `KeyCode[]` 前缀，并按完整包/程序集/revision/proxy 指纹发布 immutable consumer plan；`KeyCode.None/VK 0`、不足前缀或非整数 provider 不可发布。MOD 可继续完整持有 108 键或更大的布局，兼容层没有固定布局上限，也不会完整枚举未知 `IEnumerable`。已选 provider 无效时，单个可用候选可恢复；多个可用候选仅在其中恰有一个已证明 `ConsumerLaneBase == 0` 时恢复到该主组，其余歧义继续失败关闭。Android keyboard 通过 canonical mapper 同时投影到 Unity KeyCode 和 Win32 VK，未知 Android key 不按 scanCode 猜测。
- Touch consumer 继续驱动 MOD 原 polling/state/count/KPS/rain 代码。确认的 `LabelProvider` 由独立 UnityMain 投影器读取 preview 的实际冻结模式：Touch 临时写 `T1..TN`，External/Hybrid 恢复原始值并由 MOD 自己格式化 configured key；投影可逆，只接管空白值，MOD 自定义文本保持所有权。显式 External 只生成 presentation plan，不注册 Touch consumer。用户启用兼容绘制 fallback 时，通用槽位用该 plan 生成 ASCII configured-key 标签，单 `CanvasRenderer + dynamic Mesh` rain 由独立 renderer 提交，不写 MOD state。
- 正式 Adapter consumer 已建立 per-MOD immutable query surface。完整 `Proven` 且具有精确静态 `UnityKeyCode/WindowsVirtualKey/ActionId` lane 的 feature 可直接启用；非 Proven feature 只能消费由导入器发布、绑定包指纹和用户已确认 `BindingProvider` 的 `PcCompatKeyViewerLoweredConsumerPlan`。actor 按 sequence/raw event 更新 held 与 DOWN/UP ordinal，重写后的 MOD 原 Legacy/Win32/Rewired 查询在自己的状态机中读取，因此 count/KPS/rain/reset/persistence 仍由 MOD 原逻辑拥有。Touch-only 查询不读取 native keyboard snapshot，Hybrid 才合并两路。输入 callsite 内嵌 `manifest.Id`，MOD 自建 worker 不依赖 UnityMain thread-static owner；注册代际隔离 reload 和同 callsite cursor。`anyKeyDown` 按 raw sequence 每个物理 DOWN 最多推进一次，不会因同一输入被多个 feature/lane alias 同时映射而重复回放。Jipper 动态 `KeyCode[]` 的 `0x1000` Unity/VK 身份变换已由 lowerer 证明并发布 plan；Input System 目前只做精确候选扫描，整链 lowering 和其他未证明 transform 保持 observe-only。
- Legacy Unity polling 不直接执行 generated IL2CPP Input proxy。managed rewriter 把 `GetKey/GetKeyDown/GetKeyUp/anyKeyDown` 定向到 PcCompat native snapshot bridge；edge callsite 使用稳定 token 独立推进。真实 `user32.GetAsyncKeyState:Int16(Int32)` P/Invoke 同样在导入期按 module/entry/ABI 精确改写。Bridge 每线程 1ms 合并查询，native generation 未变化时不复制 bulk snapshot。
- 原 MOD 设置页打开后拥有全局 Android modal input ownership。该区间的 official/AsyncInput 触摸不进入 RealtimeEventCore 计数或 Adapter consumer；进入 modal 前仍 held 的触点以 Cancel 释放但不重置累计计数。managed Legacy/Win32 bridge 同步屏蔽 Touch consumer 并以 modal epoch 基线消费边沿，关闭后不得重放菜单点击；Android 硬件键盘 snapshot 始终保留，因此 Jipper 的 `anyKeyDown + GetKeyDown(KeyCode)` 原重绑流程可直接工作。等待帧保存逐键 ordinal 基线，首次查询的新键不会因 cursor 初始化而丢失。
- 插件 HUD 与 ModManager 主窗口的可见性已经解耦。标准已翻译 HUD 不再借用 ImGui/EGL 前景 draw list，而是在 UnityMain 上创建 `ScreenSpaceOverlay Canvas + CanvasScaler + Image + TextMeshProUGUI`，与内置关卡和编辑器播放态共用 Unity UI/TMP 渲染管线。ImGui 只负责 ModManager 管理窗口。
- Unity HUD 优先读取 `RDString.fontData.fontTMP`，其次使用 `RDConstants` 的游戏 TMP 字体，最后才回退 TMP 组件默认字体。Canvas 使用 `1920x1080` 参考分辨率并 `DontDestroyOnLoad`，生命周期继续由现有 overlay show/hide gate 控制。CoreCLR 保存的 Unity 对象同时建立 IL2CPP GCHandle，不能把裸 `nint` 当作 GC root。
- Unity HUD 的 GameObject 通过 generated proxy `GameObject(string, Type[])` 创建并在构造阶段安装 `RectTransform`，禁止先创建普通 `Transform` 再把它传给 RectTransform setter。HUD 高频 setter 和 AssetBundle 请求都调用编译后的 proxy delegate；对象指针、值类型和数组转换由 Il2CppInterop 生成代码处理，不再维护手写 `runtime_invoke` 参数表。
- PresentationSink 三个永久入口必须使用完整 IL2CPP ABI：static/instance 显式参数之后保留隐藏 `MethodInfo*`，调用 original continuation 时原样转发。不得因为当前 r143 方法体看似未读取该参数而省略。
- HUD 状态仍通过单次固定布局 native bulk snapshot 读取，不再逐字段执行三十余次 P/Invoke；但读取由 native `generation` 稳定点回调触发，不再按 EGL 帧轮询。`CalculatePercentAcc` 仍更新 native accuracy snapshot，但只有数值变化且通过 50ms 门控时才单独通知 Unity HUD；`GetHitMargin`、`MoveToNextFloor` 等同一次判定的中间观测不会各自重绘，最终 `AddHit`、生命周期、玩家数、reset/death 仍即时通知 Unity HUD。
- native snapshot 带 `generation`。同一代数据复用托管快照、格式化文本和尺寸；Unity 对象只创建一次，HUD 仅保留单个 TMP 富文本层，常规击打只在文本实际变化时更新。用户设置只在 `StyleGeneration` 变化时重设 RectTransform、字号和背景色。
- Il2CppInterop/proxy/TMP 初始化失败时，renderer 会被标记为不可用并注销 native 回调，原 ImGui HUD 自动恢复为兼容回退。菜单空闲态不会为了 HUD 持续建立 ImGui 帧。
- recipe bundle 的 16 字符 cache key 使用 SHA-256。自定义 CoreCLR host 在 `dlopen()` Android crypto PAL 后必须显式调用该子库的 `JNI_OnLoad(JavaVM, null)`；否则首次 `CryptoNative_EvpDigestOneShot` 会在未初始化 JNI 状态下崩溃。临时目录后缀不使用 `Guid.NewGuid()`。

下一步工程重点：

1. 实机复验稳定 hit-margin 数组在重试、编辑器播放、场景切换和 tracker 替换时不会保留旧计数或触发 callback 熔断。
2. 扩展 callback translator 的受限 CFG；若后续支持多人模式，再实现 coop 对象索引桥，当前不能把单玩家投影当成 coop 支持。
3. 放宽经过完整 metadata 身份验证的目录外 callback target，并实现 Prefix/返回值等同步语义。
4. 在实机完成 music/map time、checkpoint、PlayCount、触摸 slot 和兔子/Logo 颜色的逐项审计，再补选关 Logo 文本克隆、PC KeyViewer rain 视觉和通用 AssetBundle 资源映射。
