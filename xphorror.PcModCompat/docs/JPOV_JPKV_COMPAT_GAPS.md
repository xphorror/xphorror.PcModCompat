# JPOV / JPKV Android 兼容层缺口

更新时间：2026-08-26

本文只记录 JPOV（JipperOverlayer）和 JPKV（JipperKeyViewer）在 Android
IL2CPP 兼容层中的剩余缺口。CheryTools、通用 PC MOD、视频编辑器和其它
未纳入这两个 MOD 目标的功能不在本文范围内。

## 0. 本轮推进结果

### 0.0.4 2026-08-26 外部场景事件 generated delegate 修复

JPOV/JRP 最新失败报告中的“自绘不可用”发生在自绘启动之前：两者订阅
`SceneManager.sceneUnloaded` 时，事件桥把 generated `UnityAction<Scene>` 代理强制转换为 CoreCLR
`Delegate`，触发 `InvalidCastException` 并使整个 managed session 进入 `Faulted`。当前桥从 Il2CppInterop
rooted target 恢复原始 managed delegate，再包装 owner/generation scope并转换回访问器要求的精确代理类型。
同类回查补入 JPOV/JPKV 使用的 `sceneLoaded`；真实 JRP/JPOV/JPKV 重写产物不再保留
`Application/SceneManager` 的直接静态事件 add/remove。缓存 ABI 为 v74，定向回归 `91/91`，设备行为仍待用户验收。

### 0.0 真实 UMM 产物已接入（范围：仅 UMM loader）

`JipperOverlayer-UMM/` 与 `JipperKeyViewer-AssetBundle/` 提供了真实发布产物，此前"仓库内只有源码、发布 DLL rewrite 待验证"的前提已解除。**只支持 UMM loader**；JPKV 目录内的 Melon loader 明确不在支持范围。

审计方法：新增 `PcCompatUmmModRewriteAuditTests`，用生产自己的 spec 工厂走 Android 宿主同一个 `ModAssemblyRewriteApi.Rewrite`。裸 `ModAssemblyRewriter --audit-only` **不是有效审计**——它不传桥 spec，会把所有托管桥接管的 callsite 报成未解析；判据是 JRP 也一样失败，而 JRP 当前能加载。

代理面已由三 MOD 和开发版 JPKV 合并闭合扩容，并补齐 PC 字段到 Android generated property facade 的通用映射
（195 exact types / 15 程序集，Android 缺失与未解析元数据均为 0，generated proxy audit 为 0 issue）。
同时保留既有的 dnlib/AsmResolver 泛型实例名归一化修复；surface 仍是人工审核权威来源，未知成员继续失败关闭。

当前状态（生产 spec 审计）：

| | issues | methodIssues | bridgeIssues | outputWritten |
| --- | ---: | ---: | ---: | :-: |
| JipperResourcePack.dll | 0 | 0 | 0 | ✓ |
| JAMod.Bootstrap.dll | 0 | 0 | 0 | ✓ |
| JipperOverlayer.Loader.UMM.dll | 0 | 0 | 0 | ✓ |
| JipperKeyViewer.Loader.UMM.dll | 0 | 0 | 0 | ✓ |
| JipperOverlayer.dll | 0 | 0 | 0 | ✓ |
| JipperKeyViewer.dll | **0** | **0** | **0** | **✓** |

（此前旧代理面阶段分别记录过 JPOV `16/260`、JPKV `17/285`；当前桥、转换器和字段 facade 均已进入同一生产审计链。）

**JPOV/JPKV 主程序集现在均产出重写结果。** 两者仍有不同的设备验证边界：

- JPOV：`scrController.txtLevelNameOriginalPosition` 在 PC 是 `Vector2?` 字段，闭包工具的 `P` surface
  现在选择该字段，Il2CppInterop 生成 `get_/set_` facade；两个写入点已通过生产重写审计。
- JPKV：三类 issue 全零，`outputWritten=True`。**但"改写干净"不等于"雨能渲染"**——`RainGraphic` 依赖 native hook 把 `RawImage::OnPopulateMesh` 转发给绑定的托管实例，那条路径本机不可验证（三个 IL2CPP 宿主操作在测试里是 fake）。见 `RAINGRAPHIC_RENDER_CALLBACK_DESIGN.md` §10。

### 0.0.3 2026-08-26 开发版 JPKV 渲染代理闭包

开发版 `JipperKeyViewer/` 已从单一 `RainGraphic` 演进为三个直接派生
`MaskableGraphic` 的自绘层：`KeyShapeLayer`、`RainLayer`、`GhostRainLayer`。兼容层没有加入类型白名单，
而是按“MOD 自有闭合非抽象类型 + 直接派生 `MaskableGraphic` + 自身精确声明
`void OnPopulateMesh(VertexHelper)`”发现能力，并让扫描、recipe、重写和运行时复核共享该合同。

真实开发版 DLL 最初收敛出的 16 个精确代理成员如下，现已全部进入人工审核 surface：

1. `Canvas.get_additionalShaderChannels()`
2. `Canvas.set_additionalShaderChannels(AdditionalCanvasShaderChannels)`
3. `Graphic.get_mainTexture()`
4. `Graphic.SetMaterialDirty()`
5. `Rect.get_center()`
6. `Sprite.get_texture()`
7. `Sprite.get_textureRect()`
8. `Sprite.get_pixelsPerUnit()`
9. `Sprite.get_border()`
10. `Texture.get_width()`
11. `Texture.get_height()`
12. `Texture.set_wrapMode(TextureWrapMode)`
13. `Vector4.op_Multiply(Vector4, Single)`
14. `Vector4.get_zero()`
15. `Vector4.op_Equality(Vector4, Vector4)`
16. `Mathf.Ceil(Single)`

闭合方案不是运行时反射兜底：方法返回值和参数会把 `AdditionalCanvasShaderChannels`、`TextureWrapMode`、
`Vector4` 等签名类型带入精确依赖闭包；生成器只输出 allowlist 内类型和成员。当前闭包为 `195` 个精确类型、
`15` 个程序集，完整代理审计覆盖 `207` 个类型、`16` 个泛型初始化器且 `0` issue。生产重写报告为
`issues=0`、`methodIssues=0`、`managedBridgeIssues=0`、`outputWritten=True`，三处渲染组件基类构造均被置换。
这表示开发版 DLL 已通过本机生产重写门禁，不表示设备上的顶点提交、材质 dirty、ghost rain 生命周期或性能已验收。

9 类差距**全部闭合**（见 `MANAGED_BRIDGE_AND_CONVERTER_DESIGN.md` §6.1）：

- [已实现] `Debug.Log`/`LogWarning`/`LogError(System.Object)` → `PcCompatManagedLogBridge` 手工桥（JPOV 16 处）。托管侧 `ToString()` 后写宿主 Logger，不把任意 CoreCLR 对象送进 IL2CPP 域。
- [已实现] `GUILayout.SelectionGrid` 的 `String[]` → `Il2CppStringArray`（JPOV 2 + JPKV 3），用现成 `op_Implicit`。
- [已实现] `TMP_Text.SetText(Char[],Int32,Int32)` 的 `Char[]` → `Il2CppStructArray<Char>`（JPKV 6），现成 `op_Implicit`，失配在第 0 位。
- [已实现] `TMP_Text.SetText(StringBuilder)` → 宿主 `PcCompatAbiBridge.ToIl2CppStringBuilder`（JPOV 10）。**拷贝语义**，对已审计站点正确（Unity 在该次调用内读走字符）。
- [已实现] `TMP_FontAsset.set_fallbackFontAssetTable` 的 `List<T>` → `PcCompatCollectionBridge.ToIl2CppList`（JPOV 2 + JPKV 1），与 `CopyList` 严格对称。
- [已实现] **`fallbackFontAssetTable` 的静默失效已修**：该属性登记为可写集合，getter 改用 `CopyBoundList` 返回绑定拷贝，`List<TMP_FontAsset>` 的变更调用（三 MOD 共 4 处，全是 `Add`）重定向到写穿桥。**JRP 的 CJK fallback 字体也随之修复。** 写穿路径本机不可测（需活的 IL2CPP 运行时），只能实机确认。
- [已实现] `JsonUtility` → `PcCompatJsonBridge` + `PcCompatUnityJson` 手工桥（JPKV **7 处**）。实参是 JPKV 自有的 `ProfileData`/`KeyViewerSettings`/`SettingsMeta`，IL2CPP 类表里不存在。**注意实际是 7 处而非审计报的 6 处**：`FromJson<KeyViewerSettings>` 的代理泛型签名精确匹配、审计报干净，但 `T` 不在类表里，转发必然运行时静默失败——**"审计干净"不等于"运行时可用"**。序列化器手写 Unity-JSON 子集（字段而非属性、名字原样、枚举按整数、`null` 写 `""`/`[]`/`{}`、非有限浮点写 `0`、invariant 格式），序列化严格、反序列化宽松，与 Unity 的不对称一致。
- [不适用] `Debug.Log` 原计划的"向前实参类型推断"经实测不必要，未实现该机制。
- [已实现] JPKV 自绘类型的继承链穿过代理模块：扫描器按受检 `MaskableGraphic.OnPopulateMesh`
  能力形状生成登记项，未知形状仍失败关闭；发布版 `RainGraphic` 与开发版三个渲染层共用宿主渲染回调桥。
  该桥 hook `RawImage::OnPopulateMesh`，native 侧按 owner 指针集合预筛，命中时完全替代原方法并转发给
  托管实例。托管外壳绑定真实 `RawImage` 指针，MOD 自绘类型的代理基类构造调用统一改写为 `pop`。
  **改写侧与登记/分派链路已验证，实机渲染未验证。** 详见 `RAINGRAPHIC_RENDER_CALLBACK_DESIGN.md`。

JPOV/JPKV 当前没有托管代理面阻塞项；剩余的是设备侧真实 IL2CPP 对象、渲染、场景生命周期和输入/资源行为验收，不能由本机 metadata-only 审计替代。

### 0.0.2 2026-08-25 Nullable 字段 ABI 与历史设备报告回查

- JPOV 的 `scrController.txtLevelNameOriginalPosition` 是 PC 侧
  `System.Nullable<UnityEngine.Vector2>` 字段，Android generated proxy setter 接收
  `Il2CppSystem.Nullable<UnityEngine.Vector2>`。当前重写链在两个写入点都插入
  `PcCompatAbiBridge.ToIl2CppNullable<Vector2>`，因此不再生成旧报告所示的非法 IL 栈。
- Nullable 无值状态由桥构造 generated proxy 的无参 Nullable 对象；不能把 `null` 引用直接
  传给 setter，因为 setter 会先执行 `RequireIl2CppObject` 再 unbox。
- `last_managed_failure.txt` 的时间早于上述版本构建，报告中的
  `InvalidProgramException` 与 `UnityEngine.Object` 类型解析异常均属于旧产物；当前离线生产
  重写审计为 `19/19`，完整托管回归为 `1212` 通过、`2` 项环境跳过。实机行为仍待用户使用
  最新完整 runtime 验收。

### 0.0.1 2026-08-23 字段 facade 与三 MOD surface 闭包复核

- `ProxyInputClosure` 的 `P` 条目先解析 PC 属性；属性不存在但同名字段唯一时，按完整读写语义选字段，交给
  Il2CppInterop 的 field-accessor pass 生成属性 facade。`G` 不允许走该可写 fallback。
- `ModAssemblyRewriter` 只在字段 accessor 签名比较时把 `Il2CppSystem.*` 归一化为 `System.*`，不放宽其它代理类型。
- 经 JPOV/JPKV/JRP release assembly 扫描并与 Android catalog 复核，surface 补入实际使用的 JsonUtility、
  VertexHelper/UIVertex、Resources、sceneLoaded、Color32、FontStyles、GUI/TMP overload 等条目。
- 当前 closure：194 exact types / 15 assemblies；显式字段 144、方法 490、属性 13；
  `missingAndroid=0`、`unresolvedMetadata=0`、proxy audit issues=0。
- `PcCompatUmmModRewriteAuditTests` 当前 `16/16` 通过，JPOV/JPKV/JRP/JAMod/Loader production rewrite
  均 clean。该结果只证明 metadata/IL 重写闭合，不宣称设备端 Unity 行为已验证。

**逐项设计与实测结论：`MANAGED_BRIDGE_AND_CONVERTER_DESIGN.md`**（含机制归属判据、现有能力实测、实施顺序、回归防线，以及 §8 记录的 11 处被实测推翻的设计结论）。其中与本节此前表述冲突的两点：

- `fallbackFontAssetTable` 的 `CopyList` 拷贝语义缺陷**波及 JRP**（`BundleLoader.cs:42` 同样 `.Add`），不只 JPOV/JPKV。**该缺陷已修**（见上）；修复顺带覆盖 JRP，因此"保持 JRP 兼容性"这一目标在这一项上是收益而非风险。
- `RainGraphic` 被拒的真实根因不是"未派生 MonoBehaviour"（该判据本就沿继承链上溯），而是继承链被要求**每一环都在 MOD 自有模块内**，而 `MaskableGraphic` 在代理模块里。**报错文字已一并修正**（旧文字谎称 MonoBehaviour 派生失败，测试现同时断言新文字出现、旧文字不出现）。**此前"1.7.0 之后源码已删除该类"的记载有误**：仓库有两份 JPKV 源码，`JipperKeyViewer-1.7.0/` 是**发布版源码**且 `RainGraphic.cs` 在其中，`JipperKeyViewer/` 才是已改三层自绘的开发版。立项与实现对象都是 1.7.0 形态（唯一有发布产物、可审计、可验收）。

### 0.1 Harmony 样本审计（源码，非 DLL）

以 JPOV/JPKV/JRP/CheryTools 四份**源码**重做 Harmony 用法审计，结论修正了此前基于 DLL 的若干判断：

- **JPKV 源码零 Harmony 用法。**`JipperKeyViewer` 全树（含 `-FileBased`、`-Unity`、`-Unity2022` 与两个 Loader 变体）没有任何 `HarmonyPatch`、`Harmony.Patch` 或特殊参数。JPKV 的兼容风险在输入与绘制路径，不在 Harmony 层；本文第 5 节的 K-xx 缺口不含 Harmony 项。
- **JPOV 是唯一 Harmony 样本**，全部为 Postfix + 一个 Prefix：13 个版本 callback、6 个生命周期 Postfix、Jongyeol 四项、Beta watermark 一项。全树特殊参数只有 12 个 `__instance` 与 2 个 `___txt`，**零 `__result`、零 `ref/out`、零 Transpiler、零 Finalizer**。因此文档第 7 节"不宣称任意 Transpiler 自动兼容"在当前样本上没有对应缺口，`__result` 写回也不是 JPOV/JPKV 的阻塞项。
- JPOV 的注册路径不是 `PatchAll`，而是 `PatchManager` 的 `CreateClassProcessor(type).Patch()/.Unpatch()`，并由设置项在运行时反复开关（`Settings.cs` 的 JongyeolMode / AllowELCombo 触发 `RefreshPatches`），另有 `RegisterLazyPatches` 的 100ms 轮询与 `Main.Disable()` 的 `UnpatchAll`。shim 侧 `HarmonyRegistry` 的 `Deactivate` 只翻 `Active` 标志、重新 `Patch()` 追加新记录，`Revision` 随之递增，宿主 `PcCompatManagedModSession.ShimRegistryChanged()` 据此重建 dispatcher，所以运行时开关链路是通的。
- `PatchManager.CreateFieldRef` 无外部调用点；`CreateMemberGetter<T,F>` 只在 `AudioSource.pitch`（属性）上被调用，走属性委托路径，不落到 `AccessTools.FieldRefAccess` 的抛出桩。该桩因此不在 JPOV 活跃路径上，且调用点包在 try/catch 中降级为 `SongPitch = 1f`。

### 0.2 共享属性仲裁（本轮代码变更）

- [已实现] `RectTransform.anchoredPosition` 进入共享属性 contribution 仲裁。JPOV `ScrShowIfDebugAwakePatch`、JRP `Status.cs:139` 与 CheryTools `RegisterAutoplayStatusText` 三方作用于 `scrShowIfDebug` 的同一个 rect，且 JPOV（`BetaWatermarkOriginalPos`）与 CheryTools（`ElementState.AnchoredPosition`）各自保存并恢复同一 rect 的"原值"——谁第二个采样就会把对方的偏移当成游戏原值。现在 baseline 只采样一次，未持有 contribution 的 MOD 读到 baseline 而非当前投影值。详见 `MOD_RUNTIME_ISOLATION.md` 阶段 4。
- [已实现] 该仲裁按 `(对象, 属性)` 索引，`Behaviour.enabled` 与 `anchoredPosition` 共用同一核心；MOD 自建对象持有 native lease，走直通路径不进仲裁。
- [待实现] `Transform.localScale`、`CanvasGroup.alpha/interactable/blocksRaycasts`、`Graphic.color` 有同类争用证据（CheryTools `GameUIManager.ElementState` 保存/恢复全部四项，JRP `Overlay.cs:270` 写 `txtLevelName.localScale`），但合成语义未定（`Graphic.color` 可能需要合成器而非 last-writer-wins），本轮未放开。

### 0.3 此前各轮结论

- [已实现] JPOV 的版本分支激活策略已进入静态扫描报告。`VersionSafe` 在源码中
  通过普通 `if` 选择 `V141Patches` 或 `V136Patches`，元数据扫描无法执行该
  分支；兼容层现在为 13 个 callback 设置精确版本范围。r143 只激活 7 个
  V141 callback，r140 只激活 6 个 V136 callback，不再把两套命中/连击/判定/
  accuracy 状态同时更新。
- [已实现] 已知 MOD 激活范围会与作者声明的 `MinVersion/MaxVersion` 求交集；
  冲突或同一语义角色跨版本重叠时写入 scan issue 并失败关闭，不以“最后扫描
  到的 callback”为准。
- [已实现] native fixed-op 与 managed callback 的职责已固定：非资源类 fixed-op
  只维护公共 snapshot/固定后端，JPOV 原 Postfix 仍通过 managed callback 更新
  自己的 Overlay、combo、attempt 和 Jongyeol 状态；资源类 `DescriptorOnly`
  callback 只执行 native fixed-op，不重复调用原 callback。
- [已实现] 建立 managed-only 精确 callback 目录。`RDC.set_auto(bool)` 已按
  `static void` ABI 支持，只生成一条 `ManagedEventCallback`，不伪造 native
  fixed-op；recipe 编译器现可接受纯 managed-only recipe，但仍拒绝完全没有
  已验证规则的 MOD。
- [已实现] `scrShowIfDebug.Update` 已完成 managed-only Prefix 闭合：生成代理
  surface 暴露 `scrShowIfDebug.txt` 为 `UnityEngine.UI.Text`，目录固定目标为
  `instance void Update()`，Prefix 可通过 `Behaviour.enabled` 写回并返回 `false`
  跳过原方法；不生成 native fixed-op。
- [已实现] `scrShowIfDebug.Awake` 已按 `instance void Awake()` 精确 ABI 启用为
  managed-only Postfix。生成代理已闭合泛型 `GetComponent<RectTransform>()`、
  Unity 对象真值、`Vector2` 和 anchored-position 读写；回调只做一次写回，
  不保留 Unity 对象代理，也不生成 native fixed-op。
- [明确不支持] `scrEnableIfBeta.Awake` 仍显式失败关闭。它把官方对象保留到 `Overlay.BetaWatermark` 静态字段，之后跨帧解引用（`AdjustBetaWatermark`/`ResetBetaWatermark` 读 `gameObject.activeInHierarchy`、`GetComponent<RectTransform>()`）。缺口现在只剩**保留代理的生命周期**：owner/generation 约束下的代理保留、native 对象销毁后的 fake-null 失效、以及场景退休闭包——`__instance` 目前是每次调用用裸 `IntPtr` 新建的包装，存进静态字段后再解引用就是悬垂指针。原值保存/恢复那一侧已由共享 `anchoredPosition` 仲裁覆盖。运行时 metadata 不能绕过该限制将其误标为可用。
- [已验证] JPOV 生命周期合同测试覆盖 13 个版本 callback、版本范围冲突、
  managed-only setter ABI、`scrShowIfDebug.Update/Awake` 的精确 ABI、代理成员和
  无 fixed-op 合同，以及 Beta watermark 显式 unsupported。完整迁移构建选出 `181` 个精确类型，
  Android 缺失和未解析元数据均为 `0`，生成代理审计 `14` 个程序集且问题数为 `0`；
  全量 managed 回归为 `959` 通过、`1` 个既有 XPerfect JIT 测试按原条件跳过，
  Android `:library:assembleDebug` 构建通过。
- [已实现] JPOV `VersionSafe` 的 `GetHideWithNoAuto`、`GetPlayerIndex`、
  `GetHitMarginsCountForPlayer`、`GetPlayerColorHex` 已加入 Android bridge ABI，
  并在 managed rewrite 阶段按程序集、类型、静态性、参数和返回值精确重写到
  `PcCompatReversePatchBridge`。
- [已实现] JPOV 的 player-0 降级不会把非 player-0 统计别名到 player 0：当前
  Android 版本明确不支持多人，非 player-0 请求返回独立、同长度的稳定零数组。
- [已实现] native 和 managed hit-margin 链路保持 V1 单 tracker 合同，不新增
  多人快照、tracker 数组扫描或多人 ABI。这样不会把当前已经移除的多人功能
  伪装成可用能力，也不会因为不存在的多人状态引入额外启动和运行时风险。
- [已实现] `GetPlayerIndex` 在当前单玩家版本固定返回 0；不从托管对象提取
  native 指针，也不猜测不存在的多人 tracker 映射。多人请求由上层以失败关闭
  处理，而不是投影到另一个玩家。
- [已实现] JPOV 的 12 个高频 `GameRefs` 标量 getter 已精确改写到同一代
  native telemetry snapshot：`CurrentSeqID`、`PercentComplete`、
  `CheckpointsUsed`、`IsPaused`、`IsNoFail`、`SongPitch`、`IsGameWorld`、
  `ConductorAddoffset`、`ConductorSongpositionMinusi`、`IsAuto`、`IsScnGame`
  和 `IsGameReady`。这条路径不再为这些值重建
  controller/conductor/floor proxy 图。
- [已实现] overlay snapshot ABI 升级到 V5，保留 V2/V3/V4 前缀读取；RDC auto
  与 controller no-fail 以独立实时字段发布，旧 `SessionAuto` 继续只承担整次
  游玩累计污染标记，不能再被误用为 `GameRefs.IsAuto`。当前 V5 为 284 字节，
  `IsScnGame` 与 `IsGameReady` 只追加在 V5 尾部。
- [已实现] `IsGameReady` 保持 JPOV 原源码语义：`ADOBase.controller`、
  `ADOBase.conductor`、`scrConductor.instance.isGameWorld` 和
  `scrController.instance` 必须同时有效；不会用 `levelMaker` 替代任一条件。
- [已验证] JPOV 使用的十个对象 getter/字段以及 `scrShowIfDebug.txt` 已存在于 generated proxy surface：
  ADOBase controller/conductor/lm、controller instance/currFloor/firstFloor、
  conductor instance/song，以及 scnGame/scnEditor instance。当前剩余项是发布
  DLL rewrite 与设备闭环，不是 proxy surface 缺失。
- [明确不支持] 玩家颜色仅保留白色回退；多人 accuracy、进度、存活/auto
  状态和多人结果页不进入当前版本兼容范围。
- [已实现] bridge ABI 版本已递增，旧 managed cache 不会继续复用缺少上述
  rewrite 的产物。
- [已修复] JPKV/JRP 的 Rewired `ActionId` lowered plan 已被 consumer registry
  接受；此前 scanner、lowerer 和 Rewired query bridge 虽然存在，registry 仍
  误拒绝 `ActionId`，导致 verified consumer 永远无法注册。
- [已验证] 全量 managed 回归测试 `959` 通过、`1` 个既有 XPerfect JIT 测试
  按原条件跳过；Android `:library:assembleDebug` 完整构建通过。仓库内只有
  JPOV 源码而无可供重写的发布 DLL，因此最终发布程序集 rewrite 仍待有真实
  产物时验证；未进行实机验收。

## 1. 目标和当前边界

JPOV/JPKV 的兼容目标不是只让程序集被加载，而是尽可能保留原 MOD 的：

- 生命周期和设置语义；
- 输入身份、计数、KPS、rain 和状态机；
- Unity UI、TMP、Sprite、Texture、Material 和资源关系；
- 场景切换、关卡重载、暂停、禁用、卸载和热更新行为。

当前 Android 后端采用两条可并存的路径：

```text
原始 MOD 逻辑
  -> 经过审计的 managed rewrite + generated proxy
  -> Android CoreCLR / UnityMain

已证明的固定行为
  -> native fixed-op / recipe / HookBroker
  -> immutable snapshot / UnityMain presentation
```

第二条路径不是对任意 MOD 的通用执行器，也不能静默宣称等价于第一条路径。
如果原始托管逻辑、ReversePatch 或 callback 尚未完成，必须在诊断中标记为
partial、registered-only 或 unsupported。

## 2. 已完成且不应重复列为缺口

### 2.1 共同基础

- Android 公共 API 同步、输入事件和时间戳合同已建立。
- HookBroker 负责物理 Hook、owner/generation 隔离和 layer 生命周期。
- MOD session、callback lease、暂停、退休和终态清理已接入。
- 物理键盘和触摸使用不同身份域，不把触摸伪装成 `Z`、`X`、`Space` 或
  `Mouse0`。
- JPKV 输入事件已经支持 per-MOD cursor、DOWN/UP ordinal、held 状态和
  统计事务隔离。
- JPOV 的部分 native 状态观测已经覆盖命中、重置、移动地板、死亡和误差量
  相关 fixed-op。
- 静态 Texture、Sprite、受限 Material、静态 TMP atlas/metrics 和进度条
  recipe 已有受限 Android 路径。

### 2.2 不等于完整兼容的能力

以下能力已经存在，但只代表兼容子集，不应扩大解释为完整 JPOV/JPKV 支持：

- `Proven` 键位身份闭包；
- Touch、External、Hybrid presentation profile；
- native KeyViewer snapshot 和 batch statistics；
- JPOV overlay/status fixed-op；
- 静态字体和静态进度条资源重建；
- 经过审计的 callback fixed-op。

## 3. 共同缺口

| 编号 | 缺口 | 当前状态 | 影响 |
| --- | --- | --- | --- |
| C-01 | 原始 MOD 托管生命周期未完整执行 | 已验证 Postfix/Prefix 可走 managed callback，JPOV 版本分支已互斥；完整 Entry、OnEnable、Update、OnDisable 和销毁仍未闭合 | 不能保证未列入精确目录的原始生命周期逻辑等价执行 |
| C-02 | ReversePatch/状态入口的 method body 替换范围有限 | 已登记的 reverse patch、JPOV `VersionSafe` 四个入口及 `GameRefs` 十二个 primitive getter 可重写；未知 callback、动态反射调用仍不自动执行 | 依赖未列入 ABI 的官方状态读取仍需保持失败关闭 |
| C-03 | 通用 Postfix/callback/object bridge 未完成 | 已证明 fixed-op 与精确 managed-only callback 可执行；未知 callback 和对象图保持 fail-closed | MOD 更新或新 callback 不能自动获得完整运行语义 |
| C-04 | 多版本 ABI 的自动前向兼容未完成 | 当前目标以 r143 Android metadata 和已审计 identity 为准 | 游戏或 MOD 更新后需要重新扫描、重写和验证 |
| C-05 | 真实设备验收未闭合 | 本机合同、构建和静态导出已验证 | 触摸坐标、输入时序、场景切换、热更新和厂商差异仍需设备确认 |

### C-01：原始托管逻辑

当前 `PcCompatRuntime` 默认生成 recipe 并同步 native 规则，不执行完整的
JPOV/JPKV PC DLL setup。即使 recipe 能显示部分内容，也不能据此证明：

- 原 MOD 的设置修改会驱动同一状态；
- 原 MOD 的静态字段和缓存会按原逻辑更新；
- 原 MOD 自己创建的对象会按原生命周期销毁；
- MOD 禁用、重载和 ALC 退休后不会残留 callback 或 Unity 对象。

### C-02：ReversePatch

已登记的 ReversePatch 和 JPOV 当前使用的 `VersionSafe` 状态查询已经有受控
managed rewrite。该 rewrite 只接受完整 ABI 匹配；未知状态、未知 callback 和
动态反射调用仍保持失败关闭，不能为了让 MOD 继续执行而伪造可用状态。

## 4. JPOV 缺口

| 编号 | 缺口 | 当前状态 | 影响 |
| --- | --- | --- | --- |
| J-00 | 主程序集 rewrite 未产出 | **已闭合。** 生产 spec 审计：顶层 issue **0**、methodIssue **0**、bridgeIssue **0**、`outputWritten=True`。`scrController.txtLevelNameOriginalPosition` 在 PC 参考程序集中的字段形态由通用 `P` surface 字段 facade 生成 nullable `get_/set_` proxy，`Overlay.ResetLevelName` / `ApplyLevelNamePatch` 两处均已重写 | 代理/转换器层不再阻塞；仍需设备确认真实 IL2CPP 对象写回和场景生命周期 |
| J-01 | VersionSafe/官方状态读取链 | 四个 VersionSafe 入口和十二个高频 GameRefs primitive getter 已重写；十个对象 getter/字段已有 generated proxy surface。发布 DLL 已到位并已进入审计（见 J-00），设备闭环仍未做 | JPOV 高频标量读取链已闭合，未知/动态入口和对象图仍不能宣称完整 |
| J-02 | 完整命中、结果和误差状态 | native 只覆盖部分观测和 snapshot | 详细判定、结果页和部分 timing 文本可能缺数据 |
| J-03 | 高级 Jongyeol 功能 | FPS、状态、死亡次数、起始位置、timing analysis 等未形成完整后端 | Jongyeol 模式只能部分兼容 |
| J-04 | 多人/coop 语义 | 当前 ADOFAI 版本已移除多人功能，兼容层明确标记为 `unsupported` | 不伪造 `IsCoopMode`/`GetPlayerCount`；非 player-0 访问失败关闭 |
| J-05 | 高级 UI patch | `RDC.set_auto` 与 `scrShowIfDebug.Update/Awake` managed-only callback 已支持；Beta watermark 因保留对象生命周期闭包不完整而显式失败关闭 | debug text 隐藏和 auto text 初始重定位可执行；Beta watermark 暂不生效，但不会表面注册后在运行时绑定失败 |
| J-06 | 动态资源和对象图 | 静态字体、进度条和受限 prefab 已支持；`TMP_FontAsset.fallbackFontAssetTable` 的写穿已实现（改写侧已验证，实机未验证） | 动态字体、任意 prefab、任意 Shader 和异步 AssetBundle 仍受限 |
| J-07 | XPerfect 生命周期 | 有反射联动入口 | ALC 更新、缓存失效、卸载和异常隔离仍需闭合 |
| J-08 | JPOV coop 状态桥 | 当前版本明确为 `unsupported`；只保留 player-0 单玩家状态和独立零值失败关闭数组 | 不提供多人 accuracy、进度、对象 state、tracker 索引、颜色或结果页语义 |

### J-02：JPOV 数据范围

当前 native 观测已经覆盖部分：

- `AddHit` / reset；
- `scrPlanet.MoveToNextFloor`；
- `scrPlayer.Hit` / `Die`；
- `scrMisc.GetHitMargin`；
- accuracy、margin 和 overlay 生命周期的部分 snapshot。

仍不能宣称以下内容全部等价：

- `DetailedResults` 和完整结果页；
- 误差量计全部显示状态；
- 自动播放、不失败和难度相关 UI；
- JPOV 自己的所有 combo、timing、checkpoint/best 和 attempt 状态；
- 编辑器播放态与普通游戏态之间的全部切换。

### J-04：多人边界

当前 ADOFAI 版本已经移除多人功能，因此 JPOV/JPKV 兼容层不实现也不模拟
coop。所有对多人语义的请求都必须显式失败关闭：

- `IsCoopMode` 固定返回 `false`；
- `GetPlayerCount` 固定返回 `1`；
- `GetPlayerIndex` 只保留单玩家返回值 `0`，不执行 native tracker 推断；
- `GetHitMarginsCountForPlayer(0)` 返回单玩家稳定数组；非 `0` 参数返回独立、
  同长度的零数组，不与 player 0 共享引用；
- 玩家颜色保留白色回退，不宣称存在多人颜色语义。

这样做是版本边界声明，不是把多人数据投影为单玩家后继续运行。未来若游戏
重新提供多人 ABI，应以新的版本审计和新的兼容合同重新设计，不在当前 V1
单 tracker ABI 上追加隐式兼容。

### J-05：生命周期 callback 覆盖矩阵

| Callback 组 | r143 | r140 | 执行模型 |
| --- | --- | --- | --- |
| V141 `scrPlayer` / `scrMarginTracker` 6 项 | active | inactive | native fixed-op + 原 managed Postfix |
| V141 `SetPlayerCount` | active | inactive | native fixed-op + 原 managed Postfix |
| V136 `scrController` / `scrMistakesManager` 6 项 | inactive | active | native fixed-op + 原 managed Postfix |
| 通用 Play/Hide/State/Floor/HitMargin | active | active | native fixed-op + 原 managed Postfix |
| `RDC.set_auto(bool)` | active | active | managed-only Postfix；无 fixed-op |
| `scrShowIfDebug.Update` | supported | supported | managed-only synchronous Prefix；`txt` 为 generated `UnityEngine.UI.Text` proxy；无 fixed-op |
| `scrShowIfDebug.Awake` | supported | supported | managed-only Postfix；精确 RectTransform 获取和 anchored-position 写回；无 fixed-op |
| `scrEnableIfBeta.Awake` | unsupported | unsupported | 剩余缺口只有保留对象生命周期闭包；属性写回一侧已由共享 `anchoredPosition` 仲裁覆盖 |

固定规则和 managed callback 不互相替代：公共 snapshot 的 native 更新与 JPOV
私有 UI 状态更新都需要保留。唯一例外是明确标记 `DescriptorOnly` 的资源规则，
其原 callback 不再执行，以避免同一资源状态应用两次。

## 5. JPKV 缺口

JPKV 的发布产物只引用 Unity 模块与 `UnityModManager`——**不引用 `0Harmony`、`Assembly-CSharp` 或 `JALib`**（已由 `PcCompatUmmModRewriteAuditTests.JipperKeyViewerReferencesNoHarmonyAndNoGameAssembly` 钉住）。因此 JPKV 不存在 Harmony 缺口，也不 patch 游戏；它的全部兼容面是 Unity 代理、输入与绘制。K-xx 各项据此理解。

| 编号 | 缺口 | 当前状态 | 影响 |
| --- | --- | --- | --- |
| K-00 | 主程序集 rewrite 未产出 | **发布版与当前开发版均已闭合。** 生产 spec 审计三类 issue 均为 0、`outputWritten=True`。发布版 `RainGraphic` 与开发版 `KeyShapeLayer/RainLayer/GhostRainLayer` 均走通用形状发现和宿主渲染桥；开发版新增 16 个代理成员已闭合 | 程序集可被加载与执行。**但不等于功能正确**：宿主渲染回调和顶点提交仍需设备验证 |
| K-01 | 非 Legacy 输入源完整 lowering | 当前仓库 JPKV 1.7 源码只使用 Legacy `Input.GetKey/GetKeyDown`，其生产路径已有精确 rewrite；Rewired 三个 polling 入口已接入，Input System 任意控制对象和自定义输入源仍失败关闭 | 当前 JPKV 不受影响；未来改用未证明输入源的版本不能自动启用 |
| K-02 | 动态配置和节点完整执行 | IR 支持多 feature/lane-group，但复杂动态结构需 Proven binding | 任意布局、动态 lane 和动态显示节点不能保证自动兼容 |
| K-03 | 原始 JPKV 托管绘制 | adapter、snapshot、统计和部分 presentation plan 已有；宿主渲染回调桥已从单一 `RainGraphic` 名单扩展为受检 `MaskableGraphic.OnPopulateMesh` 能力形状，发布版和开发版改写/登记/分派链均已验证 | 原始按键格、文本、动画和 rain 的设备行为仍未验证；未知渲染回调形状继续失败关闭 |
| K-04 | 全布局闭环证明 | 数据模型可表达 8K-108 键和脚键 | 从原配置到输入、布局、对象和卸载的全链路尚未全部证明 |
| K-05 | 自定义字体/资源变体 | 静态字体和图片有受限 Resource IR；`fallbackFontAssetTable` 的写穿已实现（改写侧已验证，实机未验证） | 动态字体、字体样式和复杂 AssetBundle 依赖仍不完整 |
| K-06 | Replay/JRP 多消费者验收 | VirtualInput V2、Public consumer、KV adapter 已接入 | 回放、JRP 和多个 KV 同时存在时仍需设备端确认无重复和串扰 |

### K-01：输入源

当前自动启用的前提是输入身份闭包为 `Proven`，并且能够明确得到：

- Unity `KeyCode`；
- Windows virtual key；
- Android physical key identity；
- 或已声明且可证明的 Touch lane。

无法证明的 Input System/Rewired/custom transform 必须保持 observe-only 或
unsupported，不能按名称猜测为 JPKV 键位。

### K-02：动态配置

当前模型支持多个 `KeyViewerFeature` 和多个 `LaneGroup`，但以下情况仍不
保证自动执行：

- 运行中动态创建或销毁 lane；
- 配置之间共享复杂状态并互相改变计数；
- 同一物理键经过未证明的多级 identity transform；
- 自定义文本、图片和 rain 节点依赖任意 prefab 或任意脚本组件。

用户确认只能消除候选歧义，不能把未证明的 ABI 或对象图变成已支持能力。

### K-03：绘制和统计

输入核心已能维护按事件序列推进的 held、pressed、released、count 和 KPS
状态，但这不等于原始 JPKV 绘制代码已经运行。尤其是：

- 复杂按键动画；
- ghost rain 和自定义 rain 生命周期；
- 108 键专用布局偏移；
- 自定义字体和标签测量；
- 多配置共享计数规则；
- 场景切换和重载后的对象池恢复。

这些行为需要通过 managed self-render、已证明 recipe 或明确的 native batch
后端逐项闭合。

**rain 一项已有实现但未验证。** `RainGraphic` 的宿主渲染回调桥（`RAINGRAPHIC_RENDER_CALLBACK_DESIGN.md`）让 `AddComponent<RainGraphic>` 通过并把 `RawImage::OnPopulateMesh` 转发给托管实例，本机验证覆盖改写形态、登记顺序、分派路由与 teardown；**顶点是否真的提交到 Unity 需要设备**。per-drop 形态下 hook 发射频率是"活跃雨滴数 × 网格重建次数"（池上限 64），该开销也只能实机测量。

## 6. 优先级和完成标准

### P0：让两者从“兼容子集”进入“可执行核心”

1. [部分完成] 完成 JPOV 使用到的 `VersionSafe` ReversePatch/native 状态桥；
   四个新增入口、十二个 GameRefs primitive getter、十个对象 proxy surface
   和单玩家 hit-margin 已完成；多人/coop 按当前版本边界明确标记为
   unsupported，发布 DLL/设备闭环、真实颜色和未知动态入口仍未完成。
2. 建立 JPOV/JPKV 生命周期 callback 的完整 owner/generation dispatch。
3. 为 JPOV/JPKV 原始托管代码建立可验证的 managed rewrite + generated proxy
   执行路径。
4. 对未知 callback、对象桥和 ABI 保持失败关闭，不使用默认值伪造通过。

### P1：闭合各自主要功能

1. JPOV：完整命中/结果/误差、Jongyeol、编辑器和 coop 边界。
2. JPKV：Legacy/Input System/Rewired 输入、动态 layout、rain、108 键和
   多配置统计。
3. 两者：静态资源、字体 fallback、进度条、对象销毁和热重载。

### P2：设备验收

至少覆盖：

- 启动自启；
- 首次启用、禁用、重启；
- 进入和退出关卡；
- 编辑器播放态；
- 触摸、外接键盘和 Hybrid；
- Replay/JRP 多消费者；
- 场景切换、前后台和热更新；
- MOD 卸载后 callback、对象、输入和统计均不残留。

## 7. 明确不宣称

在上述缺口关闭前，项目不宣称：

- 任意 JPOV/JPKV 版本可直接运行；
- 任意 Harmony callback 或 Transpiler 自动兼容；
- 完整多人 JPOV；
- 任意 JPKV 输入源和布局自动识别；
- recipe/native fixed-op 与原始 MOD 托管逻辑完全等价；
- 未经设备验证的输入、资源和热更新行为已经完成。
