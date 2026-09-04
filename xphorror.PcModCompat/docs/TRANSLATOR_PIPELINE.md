# Translator Pipeline 设计

## 目标

Translator Pipeline 负责在导入 PC MOD 时，把 PC MOD 的 manifest、DLL metadata、JAPatch/Harmony 注册信息和可翻译 callback 逻辑编译成 Android native HookManager 可加载的 bundle。

它是低频导入路径，可以使用 C# / dnlib / Cecil 等托管工具，但必须满足：

- 不执行 MOD 代码。
- 不加载 MOD 主 DLL 到运行时执行环境。
- 输出可审计的 JSON report。
- 输出 native 运行时使用的 `ui_recipe.bin`。
- 支持缓存命中快速跳过。
- 支持部分兼容。

这里的“不执行 MOD”只约束静态 translator。managed self-render 使用另一条经过闭包、重写、缓存和 UnityMain 门禁的加载链；它不能作为 translator 恢复 PATCH descriptor 的依赖，也不能在导入扫描阶段触发 MOD 代码。

## 输入与输出

输入：

```text
imported/<mod_id>/
  original/
  Info.json
  JAModInfo.json?
  *.dll
  assets/
```

输出：

```text
compiled/<mod_id>/<cache_key>/
  ui_recipe.bin
  hook_rules.json
  compile_report.json
  unsupported.json
  capabilities.json
  source_hash.txt
  translator_version.txt
  assets/
```

`ui_recipe.bin` 是 runtime 首选且最终应成为唯一必需的规则文件。迁移期 `hook_rules.json` 仍用于 UI、调试、审计、回归测试和旧 loader 回退。

## 模块拆分

```text
ImportController
  |
  +-- SourceHasher
  +-- ManifestReader
  +-- MetadataReader
  +-- ResourceCompiler
  +-- PatchDescriptorScanner
  +-- DynamicPatchAnalyzer
  +-- CallbackTranslator
  +-- DomainMappingResolver
  +-- CapabilityAnalyzer
  +-- BytecodeVerifier
  +-- BundleEmitter
  +-- CompileReportWriter
```

### ImportController

负责导入事务：

- 拷贝或引用用户选择的 MOD。
- 创建临时工作目录。
- 调度扫描/翻译。
- 写入 compiled 临时目录。
- 校验成功后原子提交。
- 失败时保留报告但不启用 bundle。

### SourceHasher

生成稳定 source hash：

- zip 导入：按 zip 文件 bytes hash。
- folder 导入：按相对路径、文件大小、mtime 可选、文件内容 hash 合成。
- 忽略临时文件和系统文件。

hash 进入 cache key。

### ManifestReader

读取：

- `Info.json`
- `JAModInfo.json`
- UMM/JAMod metadata

输出 `PcModManifestModel`，不解析或执行 entry method。

### MetadataReader

只读 DLL metadata：

- assembly refs
- type defs
- method defs
- custom attributes
- method signatures
- IL method body
- embedded resources

禁止：

- `Assembly.Load`
- `AssemblyLoadContext.LoadFromAssemblyPath`
- `Activator.CreateInstance`
- 触发 static ctor

当前 Phase 1/2 选择 `System.Reflection.Metadata` + `PEReader`，直接读取 PE/CLI metadata 和 IL method body，不加载程序集，也不触发类型初始化。简单 CFG 和 dynamic AddPatch 已经不依赖 dnlib/Cecil；进入异常流、复杂抽象解释或 IL 重写时再重新评估依赖。

### ResourceCompiler

资源编译器是独立的导入期程序集 `xphorror.PcModCompat.Resources.dll`。主 ModManager、Native HookManager 和 HUD runtime 不直接引用 Unity bundle 解析库。

首版使用 `AssetsTools.NET 3.0.4` 进行只读索引：

- 解析 UnityFS/bundle header。
- 枚举 asset 名称、类型和 container。
- 读取 serialized object、prefab 层级和依赖。
- 建立 MOD IL 静态字段、`LoadAsset`/`LoadAllAssets`、asset name switch 与 `stsfld` 的数据流关联。
- 输出 feature resource group、resource recipe 和 Resource IR v1。

type tree 解析顺序：bundle embedded type tree 优先；缺失时只使用完整的 `Unity 6000.3.10f1` 单版本 class database。不内置 Unity 2022 数据库，也不把相近版本 class layout 当作当前版本使用。

版本门禁用于选择解析 class database 和 capability 兼容等级：`6000.3.x` 自动进入提取/重建；其它 `6000.x` 警告后受控转换；非 6000 禁止自动转换，但允许用户显式强制。所有 override 都进入报告和 cache key。导入的 PC/Linux bundle 不因版本相同而交给 Android Unity Player。

自动 asset 绑定只允许：

- `Proven`：IL literal/switch/LoadAsset 参数和字段写入数据流可证明。
- `UniqueType`：candidate 中预期类型唯一，同时记录 warning。

语义名称和模糊匹配只能生成 UI 建议，必须由用户确认。

Resource IR 的 required 集合比 recipe binding 更严格：只有 `Proven` 可成为 required；`UniqueType` 只进入审计/建议，不能在缺少调用点证明时决定 MOD 是否可启用。IR 二进制使用 64-byte envelope、SHA-256、CRC32、stable resource ID、candidate identity、payload 长度/哈希与路径边界校验，导入工具和 runtime 分别解析验证。

通过验证的 candidate bundle 只作为 AssetsTools.NET 输入。compiled cache 保存源 hash、Resource IR、转换后的纹理/素材、recipe 和报告，不把桌面 candidate 复制成待 Unity 加载的运行时 bundle。candidate 身份使用完整 SHA-256，compiled cache key 使用 128-bit SHA-256 前缀；cache hit 时重验源身份和转换产物。首版不做全局 blob 去重。

平台门禁与版本门禁同时生效。PC/Linux/Mac candidate 只能进入解析、提取和 VirtualBundle 转换，不进入 Unity `AssetBundle.LoadFromFile*`。UnityMain 有界队列只负责加载 APP 自带 Android capability bundle，以及分批创建 Texture/Sprite/Material/TMP/白名单 Prefab 对象。受控或强制转换授权绑定当前 session，reload 后失效。

当前实际提取器支持 Alpha8、RGBA32、RGB24、ARGB32、BGRA32、RGB565、RGBA4444、DXT1、DXT5，支持 UnityFS inline image data 和 `m_StreamData/.resS`；Alpha8 保持单通道原样 payload，其余格式进入 RGBA32。Sprite 保留 texture dependency、rect、pivot、PPU、border 和 extrude。运行时已能创建 Texture2D/Sprite/受限 Material、从 atlas/metrics 重建静态 TMP_FontAsset，并通过显式 alias 提供 TMP fallback；ProgressBar 已进入通用 PrefabGraph v1，不再依赖专属 capability prefab。

Material 首批只接受 source Shader 名称可证明映射到 `TMP Mobile / UI Default / Sprites Default` capability 的对象。导入器保存白名单 int/float/color/texture、scale/offset、keyword、render queue 和 GI 标志；TMP Desktop -> Mobile 会显式记录被裁掉的 bevel/bump/glow 等 inactive property。外部引用、未知 active keyword、tag/pass 和超预算属性失败关闭。UnityMain 克隆 base material 后逐属性调用 `HasProperty`，任何缺失都会销毁 clone 并拒绝资源。通用 TMP 重建、超出 PrefabGraph v1 白名单的组件、其它 Shader 和其它纹理压缩格式仍未支持。

`ModAssemblyRewriter` v13 当前桥接同步 `AssetBundle.LoadFromFile`、非泛型/闭合泛型 `LoadAsset`、非泛型/闭合泛型 `LoadAllAssets` 和 `Unload(bool)`。泛型实参会原样闭合 host bridge，并在 object 返回后恢复精确 proxy/array cast。字段/local/signature 只有经过完整用途审计才擦除为 `System.Object`；异步、反射和无法证明的嵌套容器不做猜测改写。v13 还覆盖静态 ReversePatch 直接调用点、MOD-owned managed component 的泛型/`Type` API、透明 owner、Destroy 和受限 coroutine；伴随程序集作为 managed cache v9 的原子闭包发布。

资源按 feature resource group 原子提交。同一 group 必须来自同一 candidate bundle；某个 group 失败只降低该 feature，不阻止其它独立 group 和 hook rule 运行。

Shader/Material 在导入期采用语义 lowering，不在 Android Player 内尝试通用 shader 重编译：

```text
serialized Shader/Material
  -> pass/property/keyword/render-state fingerprint
  -> Android shader capability manifest 匹配
  -> ShaderBindingRecipe
  -> 预编译 Android capability bundle + 转换后的纹理
```

capability bundle 使用 Unity `6000.3.10f1` 为 Android/Vulkan/OpenGLES3 预编译并显式保留 variants，作为最终 runtime 的 `pc_compat_capabilities` 发布。结果分为 `exact / compatible / unsupported`；`compatible` 必须报告可观察差异，`unsupported` 隔离到依赖它的 feature resource group，禁止静默粉色材质。DXBC、DXIL、桌面 SPIR-V 和任意 ShaderLab/HLSL 不在手机端转换承诺内；`glslang`、`shaderc`、`SPIRV-Cross` 不能直接产出 Unity Player 可加载的 serialized Shader。完整构建契约见 `ANDROID_CAPABILITY_BUNDLE.md`。

完整边界、缓存键和运行时 Material 重建流程见 `HUD_KEYVIEWER_HARMONY_COMPAT.md` 的“Shader 和 Material”章节。

### PatchDescriptorScanner

扫描直接 attribute：

```csharp
[JAPatch(...)]
[HarmonyPatch(...)]
```

输出：

```text
PatchDescriptor
  target key
  patch kind
  callback method
  version gate
  flags
  source location
```

当前已落地 `PcCompatStaticPatchScanner`：

- 解码方法上的 `JAPatchAttribute`。
- 支持 `Type` / `string` 两种 target type 构造参数。
- 支持 `PatchType`、`NeedInstance`、`MinVersion`、`MaxVersion`、`TryingCatch`、`ArgumentTypesType`。
- 保留 callback 方法的完整参数签名，用于无执行定位重载方法体。
- 保留所有版本分支，并按目标 revision 计算 active patch。
- 检测 `HarmonyPatch` attribute，但 target 聚合尚未实现，输出 `UnsupportedHarmonyAttribute` issue。
- 输出 `static-patch-scan-v2` JSON 审计报告；v2 的 `patches` 同时包含 direct attribute 和静态恢复的 dynamic AddPatch，并不再重复序列化派生的 `activePatches`。

JipperResourcePack 1.4.8.2 当前静态结果：

```text
direct JAPatch attributes: 40
dynamic AddPatch registrations: 34
active for r143: 32 direct + 17 dynamic
known unsupported descriptor paths: 0
```

默认运行路径会写：

```text
<mod>/.pccompat/static_patch_scan.json
```

### DynamicPatchAnalyzer

分析动态注册：

```csharp
Patcher.AddPatch(callback, new JAPatchAttribute(...))
Harmony.Patch(original, prefix, postfix, transpiler)
```

实现层级：

1. IL pattern matcher。
2. 受限抽象解释器。
3. recipe 补充。

禁止执行真实注册函数。

当前 `PcCompatDynamicAddPatchScanner` 与 `PcCompatRestrictedAddPatchInterpreter` 已实现前两层的首批子集：

- 用 `System.Reflection.Emit.OpCodes` 解码一字节/双字节 IL 和 branch target。
- 识别 `new JAPatchAttribute(Delegate, PatchType, bool)` + `JAPatcher.AddPatch(Delegate, JAPatchAttribute)`。
- 兼容 C# 编译器生成的 delegate cache 分支，从 `ldftn` 恢复 callback 和 target stub。
- 识别 `JALib.Tools.VersionControl.releaseNumber` 与常量的简单比较，计算连续 revision gate。
- 当 callback 与 target stub 同类型、名称不同且 stub 确实存在时，按兼容层语义标记为 `ReversePatch`，同时在 reason 中保留原始 JALib kind。
- 已确认真实 JALib `PatchType` 数值为 `Prefix=0`、`Postfix=1`、`Transpiler=2`、`Finalizer=3`、`Replace=4`，不再沿用旧 shim 的错误顺序。
- `VersionSafe.Setup()` 的 18 条候选全部恢复；r143 选择 9 条 R141，r140 选择 9 条 R136。
- r143 结果通过 managed oracle 集合级对照。
- 支持 `AddPatch(MethodInfo, JAPatchAttribute)` 中由 delegate `get_Method()` 生成的 callback `MethodInfo` 局部变量。
- 支持显式 `new string[]`、`stelem.ref` 初始化、`ldelem.ref` 读取和静态长度 foreach 展开。
- 支持 `JAPatchAttribute` 构造后的简单字段覆盖，当前已保留 `TryingCatch=false`。
- 支持形如 `_isAfterR129` 的有限 revision guard，把同一目标 Type 局部分解为 `0..129` 与 `130..max` 两组 descriptor；无法证明语义的运行时 bool 不采用该推断。
- `ResourceChanger.Patch()` 已恢复 16 条版本化 descriptor，r143 激活 8 条 `PlanetRenderer` Prefix，并通过 managed oracle 对照。
- ResourceChanger 已接入 descriptor-only 领域映射：translator 生成 17 条安全子集 rule，包括 Postfix 颜色应用、before-original skip 和 `UnityEngine.Color` 参数覆盖；Android 不执行原 PC callback IL。

当前未支持：

- Harmony target attribute 聚合。
- 动态长度数组、无法静态界定次数的循环、未知 MethodInfo 来源。
- 复杂运行时配置、任意反射和非连续 revision gate。
- ResourceChanger 的 PNG/Sprite 与 Logo 颜色安全桥已接入；通用 UI graph 已支持 Proven 字体/Sprite/Texture/Material binding，首批 TMP Mobile/UI Default/Sprites Default Material 语义 lowering 也已进入 Resource IR。尚缺选关 Logo 文本克隆、KeyViewer/SideImage 具体 lowering、通用 Shader fingerprint 和更广 prefab 组件。

### CallbackTranslator

把 callback 方法体从受限 IL 子集翻译为 register-like 领域 bytecode。

职责：

- 构建 callback CFG。
- 内联 static pure helper。
- 解析 domain mapping。
- 解析 IL2CPP field/method metadata id。
- 生成 rule bytecode。
- 输出 unsupported reason。

当前已落地第一批 `fixed-op-v2` translator：

- 只读取 PE metadata 和 IL body，不加载 MOD DLL。
- 按 callback 类型、名称和完整参数签名定位方法体。
- 拒绝异常区、分配、字段写入、未知调用、超过 128 条指令和一般 loop/back-edge。
- 领域目录只按游戏目标、参数 ABI 和 callback 效果匹配，不读取 MOD ID、程序集名或 callback 所属类型作为选择条件。
- 领域调用允许 MOD 自己定义承载类型，但要求语义方法名、调用次数和未知调用集合完全通过 verifier；通过后生成 `PcCompatCompiledRule`，不执行 callback。
- JipperResourcePack r143 作为回归样本当前由 callback translator 生成 28 条 fixed-op rule；recipe 合并平台生命周期兜底后为 30 条 runtime rule。同形的其它 MOD 进入同一链路，不需要新增专属 mapping。
- accuracy、margin hit、margin reset 的 coop 索引循环是唯一特例：必须匹配审计过的 29 条 opcode 序列、`coopMode`/`marginTrackers`/`playerCount` 数据依赖、调用集合和唯一 back-edge。MOD 自身的单个 `*.Instance` 字段允许类型名变化，随后按项目不支持多人模式的边界投影为 `player 0`。任何其它形状变化都会回到 `Unsupported`。
- 输出 `<mod>/.pccompat/callback_translation.json`；runtime recipe 直接由验证成功的 rule 生成，并补 capability 允许的平台生命周期规则。

这仍不是通用 IL AOT，也不代表任意 Postfix 或多人模式已可运行。当前产物是领域调用经 verifier 证明后收敛出的 fixed-op rule。

不职责：

- 通用 IL AOT。
- 托管对象模型复刻。
- 任意实例方法内联。

### UI Graph Lowering

普通 translator 现在还会对 manifest 生命周期入口做一条独立的 UI lowering pass。该 pass 仍然只读 PE/CLI metadata 和 IL，不加载、实例化或执行 MOD assembly：

```text
entry/JAMod lifecycle methods
  -> bounded reachable-method index
  -> UI seed / helper candidates
  -> straight-line abstract evaluation
  -> PcCompatUiObjectNode + component operations
  -> lifecycle programs
```

当前可生成的基础图包括：

- `new GameObject(string)`、`AddComponent<T>`、`GetComponent<T>`。
- `Transform.SetParent`、`GameObject.SetActive`、`Object.DontDestroyOnLoad`。
- `RectTransform` 的 anchors、pivot、anchored position、size delta、local scale。
- `Canvas`/`CanvasScaler` 的静态设置。
- `ContentSizeFitter` 的静态 horizontal/vertical fit mode。
- `Image`/`RawImage`/`Graphic` 的颜色、raycast target 和受验证资源目标。
- `TextMeshProUGUI` 的静态 text、font size、alignment、rich text、line spacing、颜色、font 与 font material 目标。
- Proven 静态资源字段读取可与 `resource_recipe.bin` 的结构化 `sourceFieldIdentity` 精确关联；旧 v1 recipe 只按编译器固定 `Reason` 格式严格恢复字段身份。

lowerer 对每个 helper 建立 graph checkpoint；遇到未知组件、动态资源、动态 prefab、未知 getter/setter、动态反射、无法证明的分支或一般循环时，只回滚当前 helper，并把方法与 IL offset 写入 `ui_graph.*` 诊断。可证明的静态资源引用会写入资源 section；Jipper 当前生成 13 条 `TextFont` binding。无法证明来源的资源仍保持 `partial`，不按名称猜测执行。

成功的 graph 会自动附带：

```text
BundleLoad -> EnsureGraph
OverlayStateChanged -> LoadOverlayVisible -> SetActive(root)
```

overlay trigger 的 generation 来自 native verified fixed-op overlay state；worker 只处理标量状态，Unity object graph 的创建和 setter 仍由 UnityMain PresentationSink 执行。资源 section 与 RawImage component/runtime setter 已完成；Resource IR 另有 Transform/RectTransform + CanvasRenderer + Image/RawImage 的 PrefabGraph v1。动态文本 snapshot、任意组件 prefab、KeyViewer/SideImage 对象图、批量 Mesh、Update/动画 lowering 仍是后续阶段。

### DomainMappingResolver

把 PC MOD API 调用映射到 native callable 或 domain op。

示例：

```text
AnyOverlayType.Instance.Hide
  -> CALL_NATIVE OVERLAY_HIDE

AnyOverlayType.Instance.Show(int)
  -> CALL_NATIVE OVERLAY_SHOW

VersionSafe.GetPercentAcc
  -> READ_STATE percentAcc
```

Domain mapping 是兼容质量核心，必须独立测试。

### CapabilityAnalyzer

根据 rule 行为生成 capability manifest。

示例：

```text
READ_STATE -> Low
RESOURCE_REDIRECT -> Medium
WRITE_IL2CPP_FIELD -> High
SKIP_ORIGINAL -> High
```

capability 既供 UI 展示，也供 native runtime 强制校验。

### BytecodeVerifier

导入期验证 bytecode：

- 指令合法。
- 跳转合法。
- 不能跳入指令中间。
- 寄存器类型一致。
- loop back-edge 类型一致。
- bytecode 大小在限制内。
- callable、field、method id 合法。
- high-risk op 有 capability。

Verifier 通过后才能写入 `ui_recipe.bin`。

### BundleEmitter

生成：

- `ui_recipe.bin`
- `hook_rules.json`
- section offset table
- string table
- target table
- field/method/callable table
- rule table
- bytecode section
- resource binding section（固定 32-byte record）
- checksum

## Cache Key

cache key 必须包含：

```text
mod_source_hash
translator_version
game_version
game_revision
il2cpp_build_id
hook_rule_format_version
domain_mapping_version
native_callable_table_version
```

任一项变化都必须重新编译。

## 事务与原子提交

编译过程不能直接写最终目录。

流程：

```text
compiled/<mod_id>/.tmp-<cache_key>/
  write files
  fsync important files if needed
  validate bin
  write complete.marker
rename .tmp-<cache_key> -> <cache_key>
```

启动时如果发现 `.tmp-*`：

- 认为上次导入中断。
- 可删除临时目录。
- 不加载。

最终目录缺 `complete.marker` 时不加载。

## 快速扫描与深度编译

UI 可以分两步：

### 快速扫描

目标：

- 显示 MOD 名称、版本、作者。
- 显示 patch 数量估计。
- 显示可能支持的 feature。
- 不生成完整 bytecode。

预算：

```text
普通 MOD < 1s
```

### 深度编译

目标：

- 完整扫描 dynamic patch。
- 翻译 callback body。
- 生成 bin/json/report。

预算：

```text
JipperResourcePack 1-3s 可接受
超大资源包允许更久，但必须可取消
```

## 动态 PATCH 分析

动态注册恢复层级：

```text
DirectAttribute
AddPatchPattern
RestrictedInterpreter
Recipe
```

分析失败时输出：

```text
UnsupportedDynamicRegistration
UnsupportedReflectionPatch
UnsupportedUnknownMethodCall
UnsupportedRuntimeConfigDependentPatch
UnsupportedTranspiler
```

不要因为一个 callback 失败导致整个 MOD 失败，除非该 callback 被 recipe 标记为 required。

## Callback 翻译

CallbackTranslator 输出 rule：

```text
Rule
  rule_id
  feature_id
  target_id
  stage
  priority
  bytecode_offset
  bytecode_length
  required_capabilities
  default_enabled
```

遇到未知调用：

```text
domain mapping -> CALL_NATIVE
IL2CPP whitelist -> CALL_IL2CPP
static pure helper -> inline
otherwise -> UnsupportedUnknownCall
```

遇到复杂对象、分配、异常控制流：

```text
UnsupportedAllocation
UnsupportedExceptionFlow
UnsupportedCallbackBody
```

## Recipe

Recipe 是人工适配文件，用于高价值 MOD。

用途：

- 补充静态分析无法恢复的 patch descriptor。
- 把 callback 映射到已知 domain op。
- 声明 feature grouping。
- 声明 capability。
- 声明 required/optional rule。

Recipe 禁止：

- 任意 native 地址。
- 任意 so 加载。
- 绕过 capability。
- 跳过 verifier。

Recipe 也必须进入 report，UI 要能显示“此项来自 recipe”。

## Capability 与部分启用

编译结果按 feature 分组：

```text
Feature
  Overlay
  ResourceRedirect
  KeyViewer
  StatusSnapshot
```

每个 feature 状态：

```text
supported
partial
unsupported
disabled_by_capability
faulted
```

部分启用规则：

- 支持且授权的 rule 启用。
- 不支持的 rule 禁用。
- 高风险未授权的 rule 禁用。
- UI 必须显示 partial，不伪装完整兼容。

## Dev Oracle

开发期允许执行现有 managed loader/probe，作为 translator 对照 oracle。

流程：

```text
PcCompatProbe executes CompatSetup()
  -> setup_snapshot.json

StaticTranslator scans same MOD
  -> translated_patch_descriptors.json

Compare:
  target type
  target method
  patch kind
  callback type/method
  version gate
  flags
```

静态 translator 的发布导入阶段禁止执行 MOD 代码。oracle 只能用于开发/测试；受控 managed self-render 在改写产物发布后进入独立运行阶段。

## UI 进度事件

Translator 应输出进度事件：

```text
CopyingSource
HashingSource
ReadingManifest
ReadingMetadata
ScanningAttributes
AnalyzingDynamicPatch
TranslatingCallbacks
ResolvingDomainMappings
AnalyzingCapabilities
VerifyingBytecode
WritingBundle
Completed
Failed
```

每个事件带：

```text
current
total
message
warning count
unsupported count
```

UI 可取消。取消后删除临时目录，不写 final compiled bundle。

## 测试

测试分层：

1. Manifest tests
   - UMM Info.json。
   - JAModInfo.json。
   - 缺字段/异常字段。

2. Metadata scanner tests
   - `[JAPatch]` attribute。
   - Harmony attribute。
   - method signature。

3. Dynamic patch tests
   - Jipper `VersionSafe.Setup()`。
   - AddPatch local variable。
   - version branch。
   - unsupported reflection。

4. Callback translator tests
   - simple postfix。
   - switch enum。
   - helper inline。
   - loop with budget。
   - unsupported allocation。

5. Verifier tests
   - invalid jump。
   - type mismatch。
   - missing capability。
   - unknown callable.

6. Oracle compare tests
   - translator output vs probe snapshot。

## 与现有实现的关系

当前 `PcCompatManagedLoader`、shim 和 probe：

- 保留为开发期 oracle。
- 不作为最终发布导入路径。
- 不进入 hook 热路径。

当前 `PcCompatDobbyBridge`：

- 作为托管控制面桥，把经过验证的 runtime bundle、诊断请求和低频 snapshot 接到 native。
- 不直接安装游戏方法 Hook，也不拥有 target 地址或 original trampoline。
- metadata resolve、安装计划、永久 slot 和 Dobby 首层入口已经由 Native HookManager/HookBroker 负责。

## 剩余工程决策

已确定 metadata/IL scanner 使用 `System.Reflection.Metadata` + `PEReader`，独立 MOD 重写器使用 dnlib，不再维护 dnlib/Cecil 双栈选型问题。资源解析器也已确定为独立程序集中的 `AssetsTools.NET 3.0.4`。

仍需冻结：

1. folder source hash 是否包含 mtime；内容 hash 仍必须是缓存身份主依据。
2. 生产 recipe 的信任边界；当前只接受 translator/cache 和随 APP 发布的 recipe，不开放任意用户 recipe 执行。
3. source semantics manifest override、手动 HUD/resource override 和 feature grouping schema。
4. 快速扫描缓存的持久化粒度与失效规则。
5. Android 端 translator 依赖裁剪与资源编译器的独立部署方式。
6. diagnostics section、手动资源 override 和 `ShaderBindingRecipe` 的最终 schema；resources section 与 `resource_recipe.bin` 的当前生产契约已落地。

当前实现矩阵和实施顺序见 [`IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md)。
