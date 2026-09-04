# xphorror.PcModCompat

PC MOD compatibility layer for the Android ADOFAI ModManager.

这个目录专门放 xphorror 侧实现的 PC MOD 兼容层，不和现有 Android ModManager 主逻辑混在一起。

## 目标

最终目标是尽量让 PC 端 UnityModManager / JAMod / JALib 风格的 MOD 以二进制形式放到 Android mods 目录后可被识别、加载，并在可支持范围内运行。

第一阶段以 `JipperResourcePack` 作为真实样本推进，但目录设计按通用兼容层保留扩展空间。

## 基本架构

兼容层采用双平面架构：

- 导入/审计层：读取 UnityModManager、JAMod、JALib、Harmony、Assembly-CSharp 等元数据，匹配 recipe 并生成可审计报告和 native hook rules。
- Native 事件层：由 Android IL2CPP 侧 Dobby hook 捕获真实游戏事件，执行受控 fixed-op / HookManager rule。
- 托管重写层：保留 UnityModManager/JAMod/JALib/Harmony API shim；PC MOD 必须先重写并绑定 generated Il2CppInterop proxies，才能显式执行 setup/self-render 并与静态 recipe / translator 输出做对照。

托管层不直接假装自己运行在 PC Mono 游戏域里。生产包不包含手写 `Assembly-CSharp`/`UnityEngine.*` stub；真实游戏对象由 dependency-closed generated proxies 访问，Hook 仍由 native metadata resolver 和永久 HookSlot 独占安装。

动态 PATCH 翻译、native HookManager、导入缓存和运行期不 unhook 的长期方案见：

- [`docs/IMPLEMENTATION_STATUS.md`](docs/IMPLEMENTATION_STATUS.md)（当前实现真值和后续顺序）
- [`docs/MVP_FEATURE_RECIPE_PLAN.md`](docs/MVP_FEATURE_RECIPE_PLAN.md)
- [`docs/DYNAMIC_PATCH_TRANSLATION_AND_NATIVE_HOOK_MANAGER.md`](docs/DYNAMIC_PATCH_TRANSLATION_AND_NATIVE_HOOK_MANAGER.md)
- [`docs/NATIVE_HOOK_MANAGER.md`](docs/NATIVE_HOOK_MANAGER.md)
- [`docs/TRANSLATOR_PIPELINE.md`](docs/TRANSLATOR_PIPELINE.md)
- [`docs/HUD_KEYVIEWER_HARMONY_COMPAT.md`](docs/HUD_KEYVIEWER_HARMONY_COMPAT.md)
- [`docs/ANDROID_CAPABILITY_BUNDLE.md`](docs/ANDROID_CAPABILITY_BUNDLE.md)
- [`docs/IL2CPPINTEROP_MIGRATION.md`](docs/IL2CPPINTEROP_MIGRATION.md)
- [`docs/ANDROID_MOD_INTEROP_AND_VIRTUAL_INPUT_CONTRACT.md`](docs/ANDROID_MOD_INTEROP_AND_VIRTUAL_INPUT_CONTRACT.md)（Android MOD 联动、公共合同和 Replay 虚拟输入）
- [`docs/UPSTREAM_ANDROID_SYNC_PLAN.md`](docs/UPSTREAM_ANDROID_SYNC_PLAN.md)（Android 上游同步、字体与固定文本字符合同）

## 当前边界

- 不移植完整 UnityModManager。
- 不承诺 Harmony 对 IL2CPP 做通用 patch。
- 不允许 PC MOD 任意直接操作 IL2CPP 对象内存。
- 真实 hook 点走白名单映射。
- 修改 Unity 对象、读取游戏状态、资源替换等行为必须逐项桥接。

## 子目录

- `shims/`：托管 API 兼容程序集设计与后续项目代码。
- `bridge/`：托管层和 native IL2CPP hook 之间的事件桥设计与后续代码。
- `docs/`：兼容层设计文档、支持矩阵和样本 MOD 适配记录。

## 当前实现状态

本节保留主链摘要；精确计数、验证记录和未实现项以 [`docs/IMPLEMENTATION_STATUS.md`](docs/IMPLEMENTATION_STATUS.md) 为准。

已经接入到主 ModManager 的部分：

- `Info.json` / `JAModInfo.json` 识别。
- `PcModManifest` 元数据模型。
- `xphorror.PcModCompat` 类型的 `ModEntry` adapter。
- PC MOD 不再被当成普通 `IModPlugin` DLL 直接 `Assembly.LoadFrom`。
- `PcCompatRuntime.RegisterMod()` 默认走 feature recipe：写 `recipe_report.json` / `hook_rules.json` / `ui_recipe.bin`，通知 native HookManager 同步，不执行真实 PC MOD DLL。
- 默认导入还会用 `System.Reflection.Metadata` 静态扫描 PC MOD DLL，不执行 MOD 代码。JipperResourcePack 当前恢复 74 条 descriptor：40 条 direct attribute、34 条 dynamic `AddPatch`，r143 激活 49 条，scanner issue 为 0。
- callback translator 会按 callback 完整参数签名定位方法体，并把通过 verifier 的 fixed-op 语义写入 `.pccompat/callback_translation.json`。JipperResourcePack r143 当前翻译 28 条 fixed-op rule；受审计的 coop 索引循环仅投影为单玩家 `player 0` 语义，不表示支持多人模式。
- 只有显式设置 `STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE=1` 或启用受控 self-render 时，才会创建 managed session、加载重写后的 PC MOD DLL并执行 `CompatSetup(modPath)`。未重写 DLL 默认拒绝执行；测试只能显式传 `AllowLegacyStubExecution=true`。
- patch 注册表数据结构。

运行时 API shim：

- `UnityModManager.dll`：最小 `UnityModManagerNet` API。
- `JALib.dll`：最小 `JAMod` / `JASetting` / `JAPatcher` / `JAPatchAttribute` / `MainThread` API。
- `0Harmony.dll`：最小 `HarmonyLib` API，默认只记录 patch 请求。
- `pc_compat_shims/` 只打包上述 API shim 和 `Newtonsoft.Json.dll`。
- `UnityEngine.*`、`Unity.TextMeshPro`、`RDTools`、`Assembly-CSharp` 与 generated corlib 来自 `pc_compat_proxies/`；当前 Release 构建为 13 个代理程序集，包含静态 TMP 重建所需的 `UnityEngine.TextCoreFontEngineModule`。
- 手写 Unity/游戏 stub 只输出到 `out/legacy_shims/`，不进入 Android runtime assets。

JipperResourcePack 当前验证状态：

- `Info.json` / `JAModInfo.json` 可由 probe 读取，目标 DLL 和主类型来自 manifest，而不是硬编码。
- probe 可构造 UMM 风格 `UnityModManager.ModEntry`。
- `JAMod.Bootstrap.Bootstrap.Setup` 可选执行并返回；当前会报告样本包缺 `JALib.Bootstrap`，但不阻断 direct JAMod setup。
- release 二进制可加载。
- `JipperResourcePack.Main` 可实例化。
- `JAMod.CompatSetup(modPath)` 可完整返回。
- `VersionSafe.Setup()` 的 ReversePatch 请求可注册并输出 `registered_only`。
- `Main.OnSetup()` 的普通 `[JAPatch]` 也会进入 patch snapshot。
- probe 可输出结构化 patch 快照，目前 Jipper setup 阶段注册 16 条 patch：9 条 ReversePatch，7 条 Postfix。
- 主项目内的 `PcCompatManagedLoader` 测试可加载 `JipperResourcePack_release` 并得到同样的 16 条 patch snapshot。
- 静态 IL scanner 已恢复 `VersionSafe.Setup()` 的 R136/R141 两组注册，能按 `VersionControl.releaseNumber` 分支生成 `0..140` / `141..max` 门禁；r143 的 9 条结果与 managed oracle 逐字段一致。
- `ResourceChanger.Patch()` 的两个有限字符串数组和 `AddPatch(MethodInfo, JAPatchAttribute)` 循环已静态解释：恢复 8 条 `scrPlanet` 旧版目标和 8 条 `PlanetRenderer` 新版目标，r143 激活后 8 条，并与 managed oracle 一致。
- ResourceChanger 已接入移动端安全子集：native 侧复刻星球白纹理、星球/尾迹颜色、Beat 轨道颜色、编辑器兔子图和官方 Logo 改色入口。PC 资源链已能通过 Resource IR/VirtualBundle 返回 Texture、Sprite、受限 Material、静态 TMP atlas/metrics 重建、TMP capability fallback 和受限通用 PrefabGraph；Jipper ProgressBar 与 Maplestory SDF 字体均由源 bundle 提取并在 Android UnityMain 重建，不再走名称特判。选关 Logo 文本克隆、动态字体及超出 Transform/RectTransform + CanvasRenderer + Image/RawImage 白名单的 prefab 仍未完成。
- 当前 28 条可证明 callback 已由 IL translator 生成与 recipe 同形的 fixed-op rule；未建立领域映射的 callback 继续 fail-closed 并进入诊断。
- Android 侧 `PcCompatDobbyBridge` 优先同步经过 CRC/边界校验的 native `ui_recipe.bin`，旧 `hook_rules.json` 只作审计和兼容回退；游戏方法 Hook 只由 native permanent slot 安装。`scrMarginTracker.CalculatePercentAcc()` 的重复托管状态发布 Hook 已删除。
- Android hook target 地址从运行时 IL2CPP metadata 解析，不使用固定 dump 偏移；托管侧 `Dobby` 兼容封装统一向 native HookBroker 注册 layer。同 target、同 detour 重复注册幂等复用 continuation，不同 detour 会追加到同一 chain。
- 已开始 MVP feature recipe 路线，`JipperResourcePack` 可生成 overlay/status 的 recipe report 和 native `hook_rules.json`；默认跳过 managed PC setup。`STARRAY_PCMOD_COMPAT_RECIPE_ONLY=1` 仍保留为强制 recipe-only 的兼容开关。
- `ProxySurfaceScanner` 可以从 PC MOD DLL 自动提取 IL2CPP proxy surface 候选，并和手写 surface 合并。除字段/方法 IL 操作数和 metadata `TypeRef` 外，它还会对基本块内的 `typeof(T)`、局部变量和常量成员名做保守传播，识别 `Type.GetField/GetProperty/GetMethod` 及返回对应 `MemberInfo` 的 helper。反射查询先写成 `RF/RP/RN`：目标成员存在时由闭包收敛为最终 `F/P/M`，不存在时保持反射返回 `null` 并写入报告；直接 surface 仍严格失败。默认构建不启用自动扫描，已验证的 `ShaderUtilities.ShaderRef_MobileSDF` getter 已固化到生产 surface。开发时可用 `build_interop_migration.ps1 -AutoSurfaceModPath <mod.dll|mod-dir>` 审计新增依赖。
- generated proxy 的泛型类型和泛型方法缓存都走受检 class/type/method lookup。生成产物全局禁止直接引用 `il2cpp_class_get_type`；构建审计和 Android 启动预检除复核当前 10 条泛型初始化链外，还会扫描全部代理方法中 `object_new`、虚分派、装箱/拆箱、`class_from_type`、反射 method inflation 和 `runtime_invoke` 的 guard 顺序。数组桥、对象池与 delegate bridge 同样失败关闭，空原生指针只允许变成带身份信息的托管异常。
- Android native 侧已经能加载 `ui_recipe.bin`，建立 bundle/target/rule 表，合并 Hook Slot，执行 capability/ABI gate，把 target 解析到 IL2CPP runtime function pointer，并通过 HookBroker 安装第一版 after-original fixed dispatcher layer。recipe 不保存 RVA/VA，源 DLL SHA-256 和游戏 revision 进入缓存身份。
- HUD 输入事件核心、五套触摸 lane projection、clock anchor、register-like Native Rule VM 和有界多时钟 deadline scheduler 已落地。scheduler 把精确 anchor 规则和允许外推的纯视觉规则分队列，当前默认零 task 路径只有一次原子检查。
- 当前 native fixed op 已覆盖 overlay 生命周期、玩家数、判定 AddHit/Reset、`scrPlanet.MoveToNextFloor(scrFloor,float,HitMargin)`、`scrPlayer.Hit(bool)`、`scrPlayer.Die(bool,bool,string,bool)`、`scrMisc.GetHitMargin(...)` 的参数观测；不会直接调用 PC MOD callback。`CalculatePercentAcc` 只发布 native accuracy snapshot，Unity HUD 通知做数值变化与 50ms 门控。
- `OnEnable` 暂不在 setup 阶段执行，因为 overlay 构造会调用依赖 ReversePatch 的 `VersionSafe.*` 方法；这些方法必须等 native bridge/IL2CPP 状态读写接上以后再启用。

还没有完成：

- PC MOD 的 enable/runtime 事件分发。
- ReversePatch method body 替换，当前只是托管 bridge API 和状态发布 hook 已就绪。
- 通用 Postfix callback bytecode、coop 对象索引桥和 IL2CPP 对象桥；当前 28 条经领域映射验证的 fixed-op callback 可执行，但不提供多人模式语义。
- `ui_recipe.bin` 已能携带并双端校验 object graph、component operations、lifecycle 和 native bytecode；native loader 会原子注册 lifecycle program，调度 VM，并把命令放入 versioned presentation snapshot。当前工作树的 Unity PresentationSink 已能创建受支持的 GameObject/Canvas/TMP/Image/ContentSizeFitter 图；Resource IR 另有受限 PrefabGraph v1。动态文本、任意组件 prefab、Mesh 和动画仍未通用化。
- 导入期资源链已有独立 `Resources/xphorror.PcModCompat.Resources.dll`：UnityFS 索引、`resource_recipe.bin`、Resource IR v1、Proven 绑定与 feature groups。桌面 bundle 只由 AssetsTools.NET 读取；运行时通过 owner/session-aware VirtualBundle 和 Android capability/object materializer 消费，不再把 PC/Linux bundle 交给 Unity 直载。同步泛型/非泛型 AssetBundle API 已桥接，异步 API 仍未完成。

## 资源 recipe 工具

资源编译器与主 runtime 保持独立程序集。Android 包会携带它，但只在 `PrepareMod` 的后台导入阶段按需加载；已有有效 recipe 直接复用，缺失或无效时串行重建，因此 AssetsTools.NET 不进入 native/HUD 热路径。离线工具仍可用于复现和审计：

```powershell
dotnet run --project .\tools\ResourceRecipeTool\ResourceRecipeTool.csproj -c Release -- compile ..\JipperResourcePack_release .\out\resource_jipper JipperResourcePack
dotnet run --project .\tools\ResourceRecipeTool\ResourceRecipeTool.csproj -c Release -- summary ..\JipperResourcePack_release\.pccompat\resource_recipe.bin
dotnet run --project .\tools\ResourceRecipeTool\ResourceRecipeTool.csproj -c Release -- validate ..\JipperResourcePack_release\.pccompat\resource_recipe.bin
# or:
.\tools\compile_resource_recipe.ps1 -ModFolder ..\JipperResourcePack_release
.\tools\verify_resource_recipe.ps1 -Path ..\JipperResourcePack_release\.pccompat\resource_recipe.bin
```

产物：

```text
<mod>/.pccompat/resource_recipe.bin
<mod>/.pccompat/resource_report.json
compiled/<mod>/<cache_key>/
  ui_recipe.bin
  hook_rules.json
  resource_recipe.bin
  resources/
    <sha256>_<fileName>
    manifest.json
```

当前边界：

- `AutoLoad`、`ControlledLoad` 和 `ForceRequired` candidate 可进入 compiled cache；只有经过验证的 Android Unity 6000.3.x candidate 能标为 `AutoLoad`。Linux/Windows/Mac candidate 保持 `ControlledLoad`，不会自动送入 Unity。
- `TryEnsure*` 默认仍需 `STARRAY_PCMOD_RESOURCE_LOAD=1`；MOD 详情页可以仅为本次进程显式开启，不持久化到下次启动。worker/ImGui 调用只会把任务放入 64 槽有界队列，native PresentationSink 在已解析的 UnityMain Canvas hook 上按需唤醒，每次机会最多推进一个 start/poll/unload 步骤；队列和 pending request 都为空时不会逐帧进入 CoreCLR。`ControlledLoad`/`ForceRequired` 仍需原有二次确认，并且授权只对当前 MOD session 有效。
- 当前实际消费者覆盖 `overlay.font` 的 `TMP_FontAsset` 与 `overlay.progress_bar` 的 `GameObject` prefab。只接受当前 session 中唯一的 `Proven/UniqueType` binding；失败、歧义、结构不匹配或未授权时继续使用游戏字体和内置 Image 进度条。中文文本在通用 TMP fallback-list 改写完成前保留游戏字体。
- recipe、candidate SHA、MOD 身份和 group/binding 引用会在导入/运行时双侧复验；同一 candidate 的加载成功或失败在当前 MOD session 内只执行一次。
- `RegisterMod` 在 UI recipe 成功或失败时都会尝试发布 resource session plan（只读 readiness）；Android 自动编译失败时继续加载其余能力，并在诊断导出中记录 `resourceCompileError`。

## 构建 shim

```powershell
.\build_shims.ps1
```

输出：

```text
out\shims\UnityModManager.dll
out\shims\0Harmony.dll
out\shims\JALib.dll
out\shims\UnityEngine.*.dll
out\shims\Unity.TextMeshPro.dll
out\shims\Assembly-CSharp.dll
```

运行时 shim 目录查找顺序包括：

- `AppContext.BaseDirectory\pc_compat_shims`
- `AppContext.BaseDirectory\xphorror.PcModCompat\out\shims`
- `AppContext.BaseDirectory\out\shims`
- `<mod>\shims`
- `<modsRoot>\pc_compat_shims`
- `<modsRoot>\xphorror.PcModCompat\out\shims`

发布到 Android 时应把 shim DLL 放进其中一个稳定目录；找不到 shim 目录时，PC MOD 会进入加载错误，而不是伪装成 loaded。

验证 Jipper setup：

```powershell
dotnet run --project .\tools\PcCompatProbe\PcCompatProbe.csproj -c Release -- ..\JipperResourcePack_release .\out\shims --recipe-only
```

`--recipe-only` 现在也会发布 resource recipe：

```text
[resource] compatibility=partial candidates=6 autoLoad=0 controlledLoad=3 rejectedOrForced=3 groups=5 bindings=8 proven=7 unsupported=0
[resource] recipe=<mod>\.pccompat\resource_recipe.bin
```

验证主项目 runtime loader：

```powershell
dotnet test ..\StArray.ModManager.Tests\StArray.ModManager.Tests.csproj -c Release --filter FullyQualifiedName~PcCompat -p:SkipNativeTestDll=true -p:BuildNativeDll=false
```

观察 PC `EntryMethod` / bootstrap：

```powershell
dotnet run --project .\tools\PcCompatProbe\PcCompatProbe.csproj -c Release -- ..\JipperResourcePack_release .\out\shims --bootstrap
```

只验证 MVP feature recipe，不执行 PC MOD setup：

```powershell
dotnet run --project .\tools\PcCompatProbe\PcCompatProbe.csproj -c Release -- ..\JipperResourcePack_release .\out\shims --recipe-only
```

只运行 metadata/IL scanner，不执行 PC MOD setup：

```powershell
dotnet run --project .\tools\PcCompatProbe\PcCompatProbe.csproj -c Release -- ..\JipperResourcePack_release .\out\shims --static-scan-only
```

验证当前 enable 边界：

```powershell
dotnet run --project .\tools\PcCompatProbe\PcCompatProbe.csproj -c Release -- ..\JipperResourcePack_release .\out\shims --enable
```

`--enable` 目前预期会停在 `VersionSafe.GetHitMarginsCount()` 的 `NotSupportedException`，这说明下一阶段缺的是 ReversePatch/native 状态桥，而不是新的加载期类型缺口。

## 第一批目标

1. 识别 `Info.json` / `JAModInfo.json`。
2. 构造兼容的 `UnityModManager.ModEntry`。
3. 提供最小 `JAMod` / `JASetting` / `JAPatcher` / `JAPatchAttribute`。
4. 将 `JAPatcher.AddPatch` 转成兼容层 patch 注册表。
5. 为 `JipperResourcePack` 的核心 patch 点建立 native event 映射。
6. 接好 ReversePatch 状态读取后，再启用 `CompatEnable()` / overlay 状态类。
7. 最后处理资源替换和 KeyViewer。
