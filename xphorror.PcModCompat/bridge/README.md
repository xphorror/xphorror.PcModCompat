# bridge

这里放托管兼容层和 Android IL2CPP native hook 之间的桥。

职责：

- 接收 native Dobby hook 产生的游戏事件。
- 将事件分发给托管 patch registry。
- 提供只读状态 snapshot，例如当前场景、当前 floor、combo、命中统计、准确率。
- 提供少量白名单写操作，例如 overlay 显示、资源替换、颜色替换。

原则：

- native 层负责真实 hook。
- 托管层负责 MOD 生命周期和回调执行。
- 不暴露任意 IL2CPP 对象指针给 PC MOD。

## 当前托管入口

发布运行路径默认不执行 PC MOD 托管代码。当前默认链路是：

```text
PcCompatRuntime.RegisterMod(manifest)
        |
        v
PcCompatRecipeCompiler + PcCompatRecipeBundleCache
        |
        v
hook_rules.json
        |
        v
PcCompatDobbyBridge -> Native HookManager
```

开发期 oracle 可显式打开：

```text
STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE=1
        |
        v
PcCompatRuntime.RegisterMod(manifest)
        |
        v
PcCompatManagedLoader.Load(...)
        |
        v
PcCompatRuntime.PatchRegistry
```

native bridge 当前优先读取 recipe cache 中的 `ui_recipe.bin`，`hook_rules.json` 只作审计/兼容回退；目标按完整 metadata identity 建立永久 HookSlot。`PcCompatRuntime.PatchRegistry.Snapshot()` 仍保留给 rewritten oracle / ReversePatch 状态桥调试使用。

JipperResourcePack 当前会注册 9 条 `ReversePatch`，它们不是事件回调，而是 PC MOD 侧用来读取游戏状态的替代实现。native bridge 需要先为这些状态读取提供等价托管 API：

- `GetHitMarginsCount`
- `CalculatePercentAcc`
- `GetPlanetSpeed`
- `LoadScene`
- `GetPercentAcc`
- `GetPercentXAcc`
- `IsCoopMode`
- `GetPlayerCount`
- `ColorLogoSafe`

在这些状态读取可用前，不应默认调用 `CompatEnable()`。

## Patch registry 查询入口

托管侧现在提供这些查询能力，给 native bridge 建立映射用：

```csharp
PcCompatRuntime.PatchRegistry.Snapshot();
PcCompatRuntime.PatchRegistry.SnapshotByKind(PcCompatPatchKind.ReversePatch);
PcCompatRuntime.PatchRegistry.FindByTarget(targetType, targetMethod);
PcCompatRuntime.PatchRegistry.FindCallback(callbackType, callbackMethod);
PcCompatRuntime.PatchRegistry.UpdateStatus(modId, callbackType, callbackMethod, status, reason);
```

这里的 `target` 语义要按 patch 类型区分：

- 普通 `Prefix/Postfix/Replace`：`TargetType.TargetMethod` 表示游戏函数，native bridge 应按这个目标安装 Dobby hook。
- `ReversePatch`：`TargetType.TargetMethod` 表示 PC MOD 内部的待替换 stub，例如 `JipperResourcePack.VersionSafe.GetHitMarginsCount`。`CallbackType.CallbackMethod` 表示 PC 版真正替换逻辑，例如 `JipperResourcePack.VersionSafe.GetHitMarginsCountR141`。

所以 Jipper 的第一阶段不是 hook `scrMarginTracker.GetHitMarginsCount`，而是给 `VersionSafe.GetHitMarginsCount()` 提供 Android 等价实现。等价实现应从 native/IL2CPP 状态 snapshot 读数据，再返回给托管 MOD。

native bridge 完成一项映射后，应调用 `UpdateStatus(...)` 把 patch 标成 `Supported`；找不到安全映射时保持 `RegisteredOnly` 或标成 `Unsupported`，UI 后续可以直接显示这些状态。

## Jipper ReversePatch 状态层

`PcCompatReversePatchBridge` 是当前的托管 bridge 骨架：

- `SnapshotHandlers()`：列出已知 ReversePatch handler。
- `TryFindHandler(targetType, targetMethod, out handler)`：确认某个 PC MOD stub 是否有 Android 等价入口。
- `PublishSnapshot(PcCompatGameSnapshot snapshot)`：由 native bridge 发布游戏状态。
- `GetHitMarginsCount()` / `GetPlanetSpeed()` / `GetPercentAcc()` 等：从最近一次 snapshot 读取状态。
- `LoadScene(name)` / `ConsumeRequestedSceneName()`：记录写侧请求，后续由 native/Unity 主线程实际执行。

这个层目前不直接操作 IL2CPP 对象，也不伪造准确率、combo、场景等数据。没有 native publish 时，它只能返回默认安全值。这样可以先把 PC MOD 兼容 API 的边界固定下来，再逐项接 native 状态读取。

## C ABI 草案

`xphorror_pcmod_compat_bridge.h` 定义了 native hook 层和托管 bridge 层之间的最小数据结构：

- `xphorror_pcmod_compat_game_snapshot_v1`：native 每帧或关键事件后发布的只读游戏状态。
- `xphorror_pcmod_compat_publish_snapshot_fn`：发布 snapshot。
- `xphorror_pcmod_compat_update_patch_status_fn`：把某个 patch 标为 `Supported/Unsupported`。
- `xphorror_pcmod_compat_consume_scene_request_fn`：消费托管侧产生的场景切换请求。

当前项目是托管 DLL 由 Android bootstrap 拉起，不是 NativeAOT 直接导出 C 符号。因此这个头文件现在只是 ABI 契约；真正绑定可以走两条路：

1. native 启动链通过 Mono/.NET runtime 查找托管静态方法并缓存函数入口。
2. 后续如果切 NativeAOT 或显式导出，再让这些函数名成为真实导出符号。

在绑定完成前，native 层不要假设这些函数已经可 `dlsym`。

## 当前可反射调用的托管入口

当前可直接通过托管类型名定位：

```text
Xphorror.PcModCompat.PcCompatNativeBridge
```

方法：

```csharp
public static void PublishGameSnapshot(
    int[]? hitMarginsCount,
    double planetSpeed,
    float percentAcc,
    float percentXAcc,
    int playerCount,
    string? sceneName)

public static bool UpdatePatchStatus(
    string modId,
    string callbackType,
    string callbackMethod,
    int status,
    string? reason)

public static string? ConsumeRequestedSceneName()
```

`status` 数值对应：

- `0`：`RegisteredOnly`
- `1`：`Supported`
- `2`：`Unsupported`

`PublishGameSnapshot` 会 clone `hitMarginsCount`，并把 `playerCount < 1` 归一化成 `1`。native 层可以安全复用自己的临时数组，不需要保持数组生命周期。

## Android Dobby 接入状态

Android 侧入口在：

```text
StArray.ModManager.Android.PcCompat.PcCompatDobbyBridge
```

`Managed.EntryCore()` 启动时会：

1. 初始化 `GameAssembly ->` 已加载 `libil2cpp.so` 的 DllImport resolver 和 Android slim Il2CppInterop Runtime。
2. 加载、校验 dependency-closed generated proxy assemblies。
3. 设置 `HookHelper.Instance = DobbyHook`，注册 `PcCompatRuntime.RegistryChanged` 监听。
4. 在 PC MOD 加载/卸载后把 recipe/patch registry 同步给 native HookManager。

游戏函数只有一条实际 Dobby 安装路径：

```text
runtime metadata resolver -> permanent HookSlot -> HookBroker -> Dobby
```

托管 `Dobby` facade 只向 native HookBroker 登记 layer，不直接 inline hook 游戏函数。`scrMarginTracker.CalculatePercentAcc()` 的旧托管 snapshot hook 和 `UnityResolve` 路径已经删除；accuracy publisher 现在是 native permanent slot 的 after-original fixed op。

目标地址不使用 dump 偏移或硬编码 RVA。native resolver 按 assembly、namespace、type、method、static/generic identity、返回类型和有序参数类型唯一解析 metadata；同 target 的多个 layer 由 HookBroker chain 组合，运行期只禁用规则，不执行 unhook。

### Native HookManager fixed dispatcher

runtime `hook_rules.json` 现在会走 native HookManager：

```text
load hook_rules.json
  -> resolve IL2CPP metadata
  -> merge HookSlot
  -> prepare install plan
  -> install planned slots
  -> HookBroker(target + fixed dispatcher layer)
```

第一版 fixed dispatcher 只支持 after-original observe 规则：

```text
InstanceVoid0
InstanceVoid1
InstanceVoidInt1
InstanceVoidPtrFloatInt
InstanceVoid3
InstanceVoidBoolBoolPtrBool
InstanceBool1
InstanceBool2
StaticVoid1
StaticIntFloatFloatBoolFloatFloatDouble
OverlayShow / OverlayShowPractice / OverlayHandleStateChange / OverlayHide / OverlayUpdatePlayers / OverlayRecordHit / OverlayResetJudgement / OverlayRecordFloorMove / OverlayRecordPlayerHit / OverlayRecordDeath / OverlayRecordHitTiming
```

它会先调用 original trampoline，再执行 native fixed op。当前 fixed op 会维护 native overlay runtime state 并输出低频 logcat 诊断。Android 托管层注册 `PcCompatOverlayRuntime` provider 后，`PcCompatModPlugin.OnForegroundGUI()` 可以在 snapshot `Visible` 为 true 时绘制最小 overlay HUD。

HUD 读取的是 target kind 白名单参数，不是通用 IL2CPP 参数反射：

- `scnGame.Play(seqID,isRestart)`
- `scrMistakesManager.SetPlayerCount(playerCount)`
- `scrUIController.WipeToBlack(direction,...)`
- `scrController.StartLoadingScene(direction)`
- `scnEditor.ResetScene(clsToEditor)`
- `scrMarginTracker.AddHit(hitMargin)`
- `scrPlanet.MoveToNextFloor(floor,exitAngle,hitMargin)`
- `scrPlayer.Hit(isAuto)`
- `scrPlayer.Die(overload,multipress,failMessage,hitbox)`
- `scrMisc.GetHitMargin(hitangle,refangle,isCW,bpmTimesSpeed,conductorPitch,marginScale)`

`scrMarginTracker.CalculatePercentAcc` 已进入 native HookManager 安装计划，由 native fixed-op permanent slot 独占状态发布职责；托管 snapshot hook 已删除。

当前不会直接调用 PC MOD 的普通 patch callback。原因是普通 callback 需要更完整的签名适配和 IL2CPP 对象桥；例如 `scrPlanet.MoveToNextFloor(scrPlanet __instance)` 不能把 Android IL2CPP 对象指针直接交给 PC MOD 的 CoreCLR shim 类型。

`scrPlanet.MoveToNextFloor(scrFloor,float,HitMargin)` 现在已作为 native fixed op 观测点接入，用来记录 floor move 次数、最近 exitAngle 和最近 hitMargin。它不解析 `scrFloor`，也不调用 PC MOD callback；它只是给 Android overlay/runtime snapshot 提供可验证状态。

`scrPlayer.Hit(bool)` 和 `scrPlayer.Die(bool,bool,string,bool)` 也已作为 native fixed op 观测点接入。`Die` 当前只读取三个 bool 参数，不读取 `failMessage` 字符串，也不解析 `playerID` / `planetarySystem` 字段。

`scrMisc.GetHitMargin(...)` 已作为 native fixed op 观测点接入。它先调用官方 original 并原样返回，再按 JipperResourcePack 的公式记录最近 timing ms 和官方 `HitMargin` 返回值，不修改判定。

环境变量：

- `STARRAY_PCMOD_COMPAT_ENABLE_DOBBY=0`：关闭 PcCompat Dobby bridge。默认开启。
- `STARRAY_PCMOD_INTEROP_AUDIT=1`：按 1/128 采样比较 generated proxy getter 与 native accuracy snapshot，默认关闭，首次异常后自动熔断。

普通 patch 当前只做目标解析和状态说明：

- 解析到 IL2CPP 目标：保持 `RegisteredOnly`，reason 会包含目标地址和 `callback dispatch pending`。
- 解析失败：标成 `Unsupported`。
