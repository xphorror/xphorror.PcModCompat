# StArray ModManager Android 1.0.6 上游同步设计

## 目标与基线

- 上游仓库：`E:\TEMP_SHARE\git\StArray.ModManager`
- 上游版本：`1.0.6`
- 上游提交：`7f84f6a1ea0f578a5381f3dd501802f8a45fc1c0`
- 本地已同步基线：`77302d32966499cd35dec0d65aef321101740603`
- 本轮功能提交：`24ab1c6`、`0ec422d`、`d95b75a`
- 目标平台：Android API 25-36，arm64-v8a

上游工作区显示的额外修改均为 CRLF/LF 换行差异，不进入同步范围。本轮只同步公开 Android API 和对应运行语义，不覆盖本地授权、HookBroker、PcCompat、AsyncInput、Activity 输入路由和 MOD 生命周期实现。

## 同步矩阵

| 项目 | 上游能力 | 本地实现策略 | 状态 |
| --- | --- | --- | --- |
| `AndroidInput` | 修正动作枚举，增加时间戳和 raw action helper | 原签名同步，不安装新输入 Hook | 已完成 |
| `InputEvents` | 完整触摸与时间戳广播 | 接入现有 Hook，并绑定 MOD generation | 已完成 |
| `RuntimeManager.GetObjectClass` | 根据对象取得真实运行时类型 | 分别转发 IL2CPP 和 Mono 后端 | 已完成 |
| `Dobby.GetLayerCount` | 查询共享 Hook 链层数 | 转发 native HookBroker，不引入托管 Hook 链 | 已完成 |
| 输入 Hook 语义 | 保留已有 Hook 后广播 | 原函数返回后广播，兼容两条旧入口并统一去重 | 已完成 |
| MOD 隔离 | 上游没有 generation 所有权 | 增加回调租约、暂停门禁和终态自动清理 | 已完成 |

## 公开 API 合同

### AndroidInput

`MotionAction` 必须与 Android NDK 一致：

```text
Down=0, Up=1, Move=2, Cancel=3, Outside=4,
PointerDown=5, PointerUp=6, HoverMove=7, Scroll=8,
HoverEnter=9, HoverExit=10, ButtonPress=11, ButtonRelease=12
```

新增以下常量和方法：

```text
ActionMask
ActionPointerIndexMask
ActionPointerIndexShift
AMotionEvent_getEventTime
AMotionEvent_getDownTime
GetMainAction(int rawAction)
GetPointerIndex(int rawAction)
```

原有 `IntPtr` 扩展方法继续保留，并转发给 raw action helper，避免重复读取原生事件。

### InputEvents

新增公开值类型与事件：

```text
TouchEventInfo
TouchTimestampInfo
InputEvents.OnTouch
InputEvents.OnTouchTimestamp
InputEvents.HasSubscribers
```

`OnTouch` 提供动作、指针索引、稳定指针 ID、单调时钟时间戳和坐标。`OnTouchTimestamp` 只广播 Down、PointerDown、Up、PointerUp 和 Cancel，不读取 Move 坐标。回调位于 Android 输入线程，订阅方只能复制值和入队，不得直接访问 Unity 对象。

### RuntimeManager

`GetObjectClass(nint objectPtr)` 的合同如下：

- 空指针返回 `null`。
- IL2CPP 后端调用 `il2cpp_object_get_class` 并包装为 `Il2CppClass`。
- Mono 后端调用 `MonoObjectGetClass` 并包装为 `MonoClass`。
- 未检测到运行时后端时返回 `null`。

ApiCompat 复查还发现上游已公开 `RuntimeManager.SetBackend` 和 `HookHelper.Instance` setter。本地已补齐公开 ABI，同时保留隔离约束：Host 可正常配置；MOD generation scope 内只允许幂等设置，不得切换全局运行时后端或替换进程级 Hook provider。

### Dobby

新增 `Dobby.GetLayerCount(nint address)`，直接查询 `modmanager_hook_broker_get_layer_count`。`Hook(..., string? owner)` 接受空 owner，但空值只能归一化为当前 MOD owner 或 `host:unknown`，不能绕过本地 owner/generation 隔离。

## 输入链路语义

生产链路保持为：

```text
libinput initializeMotionEvent
  -> native HookBroker 稳定网关
  -> 调用上一层 continuation
  -> InputEvents 解析并广播
  -> 按现有 ImGui 状态转发
```

约束如下：

1. 必须先调用上一层 continuation，确保 `MotionEvent` 已完成初始化。
2. `InputEvents` 广播不得依赖 `ImGuiInputHandler.IsInitialized`。
3. ImGui 是否消费输入仍由 Activity、modal、IME 和现有叠加层状态决定。
4. 不恢复上游 `consume/consumeSamples` Hook，不增加第三套输入所有权。
5. `ImGuiInputHandler` 和旧调试 `ImGuiRender` 同时出现时，以 raw action、pointer、event time 和短时间窗统一去重。
6. 没有订阅者时只执行一次原子读取后返回；只有时间戳订阅者时不读取 Move 坐标。

## MOD generation 隔离

上游全局静态事件会长期持有订阅 delegate。本地必须增加以下约束：

1. 在 MOD owner scope 内订阅时，记录 `ModRuntimeSession` 和 `ModRuntimeKey`。
2. 每次回调先取得 generation callback lease；暂停、退休、故障或已替换 generation 不进入 MOD 代码。
3. 回调期间恢复对应 owner scope，使嵌套 Hook、后台操作和资源注册仍归属原 MOD。
4. 单个订阅者异常只记录一次有界诊断，不中断其他订阅者，也不穿透 native 输入栈。
5. 显式退订遵循 C# 事件语义，只删除最后一个匹配项。
6. 暂停保留订阅但禁止回调；恢复同一 generation 后继续使用。
7. 终态退休或加载失败时自动移除订阅，释放 delegate 和 AssemblyLoadContext 引用。
8. 订阅登记为 `InputSubscription` 资源，暂停策略为 `RetainWhileSuspended`，终态必须退休。

为避免平台程序集反向依赖，终态清理由 `ModRuntimeSession` 提供内部注册接口；Android `InputEvents` 只登记自身清理动作。终态动作在 session 锁外执行，任何清理异常都不得改变生命周期提交结果。

## 明确不同步

- 不复制上游托管 `HookChains`、`DetourTargets` 和物理拆链实现。
- 不整体覆盖 `Dobby.cs`、`ImGuiInputHandler.cs`、`Managed.cs`。
- 不恢复直接接管 `consume/consumeSamples` 的输入 Hook。
- 不改变 AsyncInput 的捕获、重放和 modal gate。
- 不同步 Windows、桌面 renderer、CI、release 和 `version.json` 下载信息。
- 不降低 capability、Provider、lease 和 native startup gate。

## 验证要求

1. 公开 API 合同测试覆盖所有新增类型、成员、枚举值和 overload。
2. 输入测试覆盖完整事件、时间戳快速通道、异常隔离和重复过滤。
3. 生命周期测试覆盖显式退订、暂停、恢复、退休、加载失败和不同 generation 隔离。
4. HookBroker 合同测试确认 `GetLayerCount` 使用已有 native 导出。
5. RuntimeManager 测试确认双后端分派源码合同和空指针行为。
6. managed 定向测试、managed 全量测试、Android Release 构建通过。
7. 不同构建配置不得因同步产生输入链路或门禁差异。

实机输入时序、触摸坐标和 MOD 热更新最终表现由设备测试确认；本机验收只声明 API、生命周期、构建和静态导出合同成立。

## 实施结果

2026-08-17 已完成同步：

- Core 单向 ApiCompat：`0` 断点。
- Android 单向 ApiCompat：`0` 断点。
- API、输入和生命周期定向测试：`51/51` 通过。
- managed 全量测试：`886/886` 通过。
- Android Release managed 构建：通过。
- Android 托管与 native 构建链：通过，PostBuild 资源、ABI 和 ELF 安全属性审计通过。
- IL2CPP 代理闭包：181 个精确类型、14 个程序集；`missingAndroid=0`、`unresolvedMetadata=0`。
- 生成代理审计：192 个类型、14 个泛型初始化器、`issues=0`。
- 最终 arm64 SO 已导出 `modmanager_hook_broker_get_layer_count`。

最终构建产物：

```text
StArray.ModManager.dll
  sha256: CCF0CC9E91DBD725002701F480B073FF36B2551C728F85CCF7CFCC6818A1B94C

StArray.ModManager.Android.dll
  sha256: FE8DEB30D307437135F50112457D80D1605F06DFF7EBECC45A6ACFBBED67CA3A

libstarray_modmanager.so
  sha256: 55FE1A80F994C48379ECDB23DFE08DB535B265503F0BE8E2961A4ABAB51DE203
```

未执行实机验证。设备端仍需验证坐标、时间戳、双入口去重、MOD 暂停恢复和热更新后的真实输入表现。
