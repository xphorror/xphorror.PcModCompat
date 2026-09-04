# MVP Feature Recipe 路线

> 当前实现真值和验证记录见 [`IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md)。本文保留 MVP 演进设计，历史阶段描述不应覆盖最新运行时状态。

## 目的

通用 PC MOD 兼容层会快速膨胀成：

- IL metadata scanner
- dynamic patch analyzer
- callback IL translator
- native bytecode VM
- AArch64 universal bridge
- IL2CPP object bridge
- capability runtime

这些都是长期目标，但不适合作为第一批可见产出的前置条件。

MVP 路线改为兼容“表现效果”，不是兼容“任意 PC MOD 代码”。

```text
PC MOD
  -> 扫描 active PATCH
  -> 验证 callback IL 与领域效果
  -> 归组高价值 feature
  -> 生成少量 native rules
  -> HookManager 执行固定 op
```

## 核心取舍

### 保留最终方向

长期方案仍然保留：

- 静态 translator。
- dynamic PATCH 分析。
- callback 受限 IL 翻译。
- native HookManager。
- rule bytecode。

### MVP 收缩

第一阶段不做：

- 任意 callback IL 翻译。
- 通用 IL AOT。
- 任意函数零适配 bridge。
- 通用 bytecode VM。
- 完整 Harmony 语义。

第一阶段只做：

- manifest 识别。
- verified fixed-op rule 归组。
- 固定 native rule op。
- 编译报告。
- capability 标记。
- unsupported feature 展示。

## 通用 verified fixed-op recipe

recipe ID：

```text
xphorror.recipe.verified_fixed_op.v1
```

该 recipe 不按 MOD ID、程序集名或入口类型选择。只有 active PATCH 的目标、ABI 和 callback IL 效果都通过验证，rule 才进入 recipe。JipperResourcePack 是首个覆盖完整目录的测试样本，不拥有专属 recipe。

支持 feature：

```text
Overlay lifecycle:
  scnGame.Play -> overlay_show(seqID)
  scrPressToStart.ShowText -> overlay_show_practice()
  StateBehaviour.ChangeState -> overlay state transition
  scrUIController.WipeToBlack -> overlay_hide()
  scnEditor.ResetScene -> overlay_hide()
  scrController.StartLoadingScene -> overlay_hide()
  scrMistakesManager.SetPlayerCount -> overlay_update_players()

Status snapshot:
  scrMarginTracker.CalculatePercentAcc -> publish_margin_snapshot()
```

MVP 最初暂不支持（以下边界已被后续实现部分超越）：

```text
ResourceChanger:
  等 native file/resource hook 点确定后再接。

KeyViewer:
  等 input-domain rules 定义后再接。

Arbitrary callback IL:
  MVP 不翻译任意 callback 方法体。
```

当前 ResourceChanger 的星球/尾迹/地板/编辑器兔子/Logo 安全子集和 touch KeyViewer 观测已经落地；它们仍是 verified fixed-op/typed snapshot 子集，不应把本段历史列表理解成当前状态，也不代表通用 callback IL 已支持。

## 产物

MVP 先输出托管 report model 和 JSON cache，后续再接 native bin emitter：

```text
PcCompatRecipeCompileReport
  ModId
  RecipeId
  Compatibility
  Features[]
  Rules[]
  Unsupported[]
  RequiredCapabilities
```

Rule 是固定 op，不是自由 bytecode：

```text
PcCompatCompiledRule
  target type
  target method
  param count
  stage
  op
  capabilities
```

当前已落地的缓存形态：

```text
<modsRoot>/compiled/<mod_id>/<cache_key>/
  recipe_report.json
  hook_rules.json
  ui_recipe.bin
  cache_key.txt
  format_version.txt
  complete.marker
```

`complete.marker` 存在才认为该 compiled bundle 可用。

`recipe_report.json` 是 UI / 审计用的完整报告，保留 feature、unsupported 和说明文本。

`ui_recipe.bin` 是新的 Native 运行时首选输入，使用 `HUD_KEYVIEWER_HARMONY_COMPAT.md` 中定义的 v1 分段格式；JSON 保留用于审计和旧缓存兼容。

`hook_rules.json` 是可审计的运行时规则镜像和旧 loader 回退，不再复制完整 report。当前 schema：

```json
{
  "formatVersion": "mvp-fixed-op-v2",
  "modId": "ExampleOverlayMod",
  "recipeId": "xphorror.recipe.verified_fixed_op.v1",
  "compatibility": "partial",
  "requiredCapabilities": 131083,
  "targets": [
    {
      "id": 1,
      "assemblyName": "Assembly-CSharp",
      "namespace": "",
      "typeName": "scrMarginTracker",
      "methodName": "CalculatePercentAcc",
      "isStatic": false,
      "genericArity": 0,
      "returnType": "System.Void",
      "parameterTypes": [],
      "paramCount": 0,
      "abiKind": "InstanceVoid0",
      "rules": [
        {
          "id": "domain.status.margin_snapshot",
          "featureId": "status_snapshot",
          "stage": "AfterOriginal",
          "stageCode": 1,
          "op": "PublishMarginSnapshot",
          "opCode": 5,
          "requiredCapabilities": 131073,
          "defaultEnabled": true,
          "source": "translator:fixed-op-v2:ExampleOverlay.Status.OnAccuracyChange"
        }
      ]
    }
  ]
}
```

## Native UI Recipe 当前进度

MVP 运行时已经不再止于 fixed-op hook：

```text
ui_recipe.bin object graph/lifecycle
  -> Native verifier
  -> HudLogicWorker + deadline scheduler + Rule VM
  -> 64-slot bounded presentation history
  -> UnityMain PresentationSink
  -> native GameObject/Canvas/TMP/Image object graph
```

当前可执行 command 为 `EnsureGraph/SetActive/SetRect/SetText/SetColor/SetFontSize/DestroyGraph/InvalidateTarget`；object graph 初始化另外支持 Canvas、CanvasScaler、颜色、raycast、rich text 和 `TMP_Text.lineSpacing`。实际 Hook 仍只由 HookBroker 安装，Unity API 由完整 metadata identity 动态解析，不使用固定 RVA/VA，也不允许完整签名失败后退化到同名/参数数量匹配。64 槽历史按 publication generation 顺序提交；未消费覆盖会使 presentation stream fail-closed，等待 clear barrier 恢复。

这一阶段已经包含受限 translator 的实际 graph/lifecycle 输出，不再只有 fixture 互操作：JipperResourcePack r143 可以从生命周期可达 IL 生成基础 Unity object graph 和 visibility lifecycle。它仍不是任意 PC MOD 的通用执行器；动态 prefab、动态文本、自定义组件以及超出当前 Resource IR/PrefabGraph/TMP/Material 白名单的资源会被记录为 partial/unsupported，并且 recipe 后端不会执行原 MOD 托管代码。

当前 translator 输出的顺序是：

```text
manifest + static patch scan
  -> reachable lifecycle/helper index
  -> bounded UI graph lowering
  -> object graph/component operations
  -> BundleLoad EnsureGraph + OverlayStateChanged visibility programs
  -> ui_recipe.bin
```

`OverlayStateChanged` 由 verified fixed-op 的 overlay state generation 驱动；visibility program 使用 `LoadOverlayVisible` 读取标量状态，再由 UnityMain sink 执行 `SetActive`。这条链路不改变既有 hook slot，也不在 worker 线程调用 Unity API。

这样拆分后，native 侧只需要按 `targets[]` 解析 hook 点和固定 op；UI 继续读取 `recipe_report.json`。

当前 native loader 优先把 `ui_recipe.bin` 解析成内存中的 bundle / target / rule 表；二进制拒绝时才允许回退到同目录 `hook_rules.json`。两条路径都会统计合并后的唯一 Hook Slot 数：

```text
bundle count: loaded compiled bundle 数量
target count: bundle 内声明的 target 总数
rule count: 所有 target 下的 rule 总数
slot count: 按规范化完整方法身份合并后的唯一 hook 入口数
```

loader 还会尝试把 target 解析到 IL2CPP metadata：

```text
assemblyName/namespace/typeName/methodName/static/genericArity/returnType/parameterTypes
  -> IL2CPP domain/image/class/method metadata
  -> Method.function runtime pointer
```

这里不使用 dump RVA，也不调用 UnityResolve 全量索引。所有目标都先按完整身份从运行时 IL2CPP metadata 唯一匹配，再通过 `abiKind` dispatcher gate，最后取 `Method.function` 交给 native HookManager。`paramCount` 只是与 `parameterTypes` 强制一致的诊断字段。

解析完成后，native 会重建 Hook Slot registry：

```text
target key
  -> HookSlot
       abiKind
       before_rules[]
       replace_rules[]
       after_rules[]
       state
       function pointer
       original trampoline placeholder
```

同一个 target key 的多个 bundle rule 会合并到同一个 slot。当前已启用 capability gate、ABI gate、stage gate 和 fixed-op gate；`installable slot` 表示该 slot 已 resolve、至少有一条 enabled rule，并且可以进入第一版 fixed dispatcher。

安装计划和安装阶段拆开：

```text
resolve slots
  -> rebuild HookSlot registry
  -> apply capability gate
  -> prepare install plan
  -> install planned slots
  -> log planned / blocked / pending / resolved
```

`prepare install plan` 不修改函数入口，只计算计划。`install planned slots` 才对计划内 slot 调用 HookBroker，保存当前 layer 的 continuation，并把 slot 状态切到 `HookInstalled`。

安装计划不再维护“已知 hook 所有者”跳过表。共享目标由 HookBroker 合并成永久 chain，每个 detour 保留自己的 continuation，真实目标入口只由 broker 首次接管。`scrMarginTracker.CalculatePercentAcc` 的旧托管 publisher 已删除，目前只有 native fixed dispatcher 这一层。

第一版 dispatcher 只支持：

```text
InstanceVoid0: instance method, no declared C# parameters
InstanceVoid1: instance method, one declared C# parameter
InstanceVoidInt1: instance method, one enum/int declared C# parameter
InstanceVoidPtrFloatInt: instance method, object pointer + float + enum/int parameters
InstanceVoid3: instance method, three declared C# parameters
InstanceVoidBoolBoolPtrBool: instance method, bool + bool + object pointer + bool parameters
InstanceVoidColor1: instance method, one UnityEngine.Color value parameter
InstanceVoidIntBool: instance method, one int/enum + one bool parameter
InstanceVoidPtrBool: instance method, one indirect value/object pointer + one bool parameter
InstanceBool1: instance method returning bool, one bool parameter
InstanceBool2: instance method, two declared C# parameters, bool return
InstanceBoolBoolInt: instance method returning bool, one bool + one int/enum parameter
StaticVoid1: static method, one declared C# parameter
StaticIntFloatFloatBoolFloatFloatDouble: static method returning int, float + float + bool + float + float + double parameters
```

其它 `abiKind` 先进入 blocked 状态，等后续有明确桥接 stub 再开放。

当前 dispatcher 的限制：

- after-original 观测已覆盖 overlay/status；before-original 只开放 ResourceChanger 已审计的参数覆盖和 skip-original fixed-op。一般 `ReplaceOriginal` 仍失败关闭。
- `PublishMarginSnapshot` 已归入 native permanent slot，正常 Hook 热路径不进入 CoreCLR。
- ResourceChanger 的编辑器兔子、地板、星球、尾迹和 Logo 安全子集已接入；Resource IR v1、VirtualBundle 和同步 AssetBundle API 已形成 Texture/Sprite/受限 Material/TMP/PrefabGraph 生产子集。异步 API、通用 TMP/Shader 和更广 prefab 组件仍未实现。
- detour slot 由 AArch64 thunk arena 按完整去重 target 集合动态扩页；每批在物理安装前整体分配，失败时整批明确阻断。

通用 fixed-op catalog 使用真实 IL2CPP metadata 参数数，不使用 C# 默认参数的调用表象：

- `scnGame.Play(int, bool)` 返回 `bool`，标记为 `InstanceBool2`。
- `scrUIController.WipeToBlack(WipeDirection, Action, Action)` 是 3 参数，标记为 `InstanceVoid3`。
- `scrMistakesManager.SetPlayerCount(int)` 是 static，标记为 `StaticVoid1`。
- `scrMarginTracker.AddHit(HitMargin)` 是 1 个 enum/int 参数，标记为 `InstanceVoidInt1`。
- `scrPlanet.MoveToNextFloor(scrFloor, float, HitMargin)` 是 object + float + enum/int 混合参数，标记为 `InstanceVoidPtrFloatInt`。
- `scrPlayer.Hit(bool)` 返回 `bool`，标记为 `InstanceBool1`。
- `scrPlayer.Die(bool,bool,string,bool)` 是 bool + bool + object pointer + bool 参数，标记为 `InstanceVoidBoolBoolPtrBool`。
- `scrMisc.GetHitMargin(float,float,bool,float,float,double)` 是 static int 返回值 + 混合浮点参数，标记为 `StaticIntFloatFloatBoolFloatFloatDouble`。
- 当前可进入第一版 dispatcher 的目标必须命中上面的固定 ABI 表。

当前 fixed op 会维护 native overlay runtime state，并低频输出 logcat 诊断。标准 telemetry HUD 已迁到 Unity `ScreenSpaceOverlay Canvas + TextMeshProUGUI`；ImGui 只负责 ModManager 管理窗口和兼容回退，不再是 HUD 常规热路径。

当前 HUD 可读字段：

- 最后触发的 fixed op。
- 最后触发的 target kind。
- `SetPlayerCount(int)` 的玩家数。
- `scnGame.Play(int seqID, bool isRestart)` 的播放参数。
- `WipeToBlack` / `StartLoadingScene` 的 wipe direction。
- `scnEditor.ResetScene(bool clsToEditor)` 的编辑器 reset 标记。
- `scrMarginTracker.AddHit(HitMargin)` 的最近判定 margin。
- `scrPlanet.MoveToNextFloor(scrFloor,float,HitMargin)` 的 floor move 次数、最近 exitAngle 和最近 move hit margin。
- `scrPlayer.Hit(bool isAuto)` 的 hit 次数和最近 auto 标记。
- `scrPlayer.Die(bool overload,bool multipress,string failMessage,bool hitbox)` 的死亡事件次数和最近三个 bool 标记。
- `scrMisc.GetHitMargin(...)` 的最近 timing ms 和官方返回的 `HitMargin`。
- `scrController.instance.percentComplete` 的低频 progress 快照，读取点限制在会话开始和 `MoveToNextFloor` 后。
- 基于同一 progress 快照渲染的标准 HUD progress bar。它不复用 PC MOD 的 prefab 或颜色字典，只承诺表现等价的移动端安全子集。
- 从 `GetHitMargin` 参数派生的最近 BPM/KPS。
- 从 `AddHit` 派生的 Jipper 基础 combo：Perfect/Auto 递增，其它非 Auto 判定清零。
- attempt/best 已通过 `<mod>/.pccompat/mobile_play_stats.json` 持久化，并按关卡身份、起始进度和倍速隔离；AUTO/noFail 会话不更新统计。

这些字段来自 target kind 白名单，不做通用参数反射。
`scrPlanet.MoveToNextFloor` 当前不会解析 `scrFloor`，只记录参数中可安全读取的 float/int 值。
`scrPlayer.Die` 当前不会读取 `failMessage` 字符串，也不会解析 `playerID` / `planetarySystem` 字段。
`scrMisc.GetHitMargin` 当前只记录通用 timing telemetry 与 BPM/KPS 所需的计算结果，不修改官方返回值。

## 验证标准

第一阶段完成标准：

- 任意 MOD 只要提供同形、经验证的 callback，就能生成同一 recipe；仅修改 MOD ID 不影响结果。
- recipe 能生成 overlay/status rules。
- 默认可跳过 managed PC setup，只写 recipe report / hook rules，并通知 native HookManager。
- `STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE=1` 时才执行重写后的 managed PC setup，作为开发期 oracle；原始 DLL 不允许落回假 Unity shim 执行。
- `STARRAY_PCMOD_COMPAT_RECIPE_ONLY=1` 仍保留为强制 recipe-only 的兼容开关。
- unsupported feature 清晰展示。music/map time、checkpoint/best 和 touch KeyViewer 已有稳定 typed snapshot；未进入 snapshot/Resource IR/managed bridge 的字段与行为继续 fail closed。
- report 能序列化给 UI。
- `ui_recipe.bin`、`hook_rules.json` 与 `recipe_report.json` 分离，且二进制/JSON 可被 native loader 分别校验解析。
- `callback_translation.json` 单独记录 translator 成功项和拒绝原因；recipe 直接使用验证通过的 rule，其中 loop callback 必须记录严格单玩家投影。
- 现有 PcCompat probe / managed loader 测试不被破坏。

开发期可用 probe 验证 recipe，不执行 PC MOD setup：

```powershell
dotnet run --project .\tools\PcCompatProbe\PcCompatProbe.csproj -c Release -- ..\JipperResourcePack_release .\out\shims --recipe-only
```

## 后续升级路径

```text
MVP recipe rules
  -> JSON report emitter
  -> native fixed-op executor
  -> resource redirect recipe
  -> key viewer recipe
  -> static patch scanner [direct JAPatch complete]
  -> dynamic AddPatch analyzer [VersionSafe delegate pattern complete]
  -> restricted AddPatch interpreter [ResourceChanger finite MethodInfo loops complete]
  -> callback IL translator [28 fixed-op callbacks complete; 3 audited loops use player-0 projection]
  -> full HookManager VM
```

这个顺序保证第一批可见效果不被通用框架拖住。
