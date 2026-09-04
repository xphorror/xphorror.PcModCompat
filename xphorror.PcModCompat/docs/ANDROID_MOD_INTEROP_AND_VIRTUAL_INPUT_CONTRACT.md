# Android MOD 联动与虚拟输入合同

## 1. 文档状态

本文是 Android MOD 联动层的设计基线，记录已经确定的跨 MOD 语义和实现约束。

截至 2026-08-17，本文定义的生产源码、Replay/Jipper V2 接入、V1 兼容、PcCompat Adapter Hub、统计事务、RPC、配额、熔断和 generation 生命周期已经实现。本机托管全量回归为 `919/919`，Replay 与 Jipper Debug 构建均为 `0` 警告、`0` 错误；Android arm64 Debug 单包构建通过。本文不替代 `IMPLEMENTATION_STATUS.md` 中的详细实现记录，也不把尚未执行的实机验证写成已验收。

目标场景是：

```text
Replay Android MOD
        |
        v
ModManager Interop Broker
        |
        +--> JipperKeyViewer Mobile
        +--> 其他 Android KeyViewer MOD
        +--> PcCompat KeyViewer Adapter Hub
                    |
                    +--> JipperResourcePack Adapter
                    +--> 未来其他 PC MOD 的 KeyViewer Adapter
```

Replay 不知道消费者是谁，JipperResourcePack 不需要知道 Replay，PcCompat 不为任何单一 MOD 保留专用分支。

## 2. 设计目标

- 允许 Android MOD 之间动态发布、订阅和异步请求/响应。
- 允许订阅者在发布者加载前注册，并在发布者出现后自动接入。
- 允许一个发布者同时服务多个 Android MOD 和多个 PcCompat KeyViewer。
- 让 PcCompat 按完整 KeyViewer Adapter 转发，而不是只转发某个 JRP 特判或单个 feature。
- 保护 MOD、程序集加载上下文和 runtime generation 的隔离。
- 不让跨 MOD 消息进入游戏 Unity Input、Android 原始输入或游戏判定。
- 在高频输入路径中避免反射、JSON、跨线程同步等待和逐事件堆分配。
- 对旧版 Replay/Jipper 私有协议保留兼容。
- 对消费者故障执行单消费者熔断，不影响 Replay、游戏和其他消费者。

## 3. 非目标

- 不把 Broker 变成 Unity 全局事件中心。
- 不允许 MOD 通过联动 API 直接注入游戏键盘、触摸或判定。
- 不允许任意 MOD 定义的 CLR 对象跨 ALC 传递。
- 不提供同步阻塞式跨 MOD RPC。
- 不使用运行时扫描程序集来猜测未知 MOD 的事件语义。
- 不把 PcCompat 的标准 Adapter 资格降级为只要找到某个 `Input.GetKey` 调用就自动启用。

## 4. 核心组件

### 4.1 ModInteropBroker

Broker 位于共享的 `StArray.ModManager.dll` 合同层。Android MOD 的 AssemblyLoadContext 共享该合同程序集，跨 ALC 只传递合同定义的值类型、只读载荷和 lease，不传递 MOD 私有类型。

Broker 负责：

- 合同注册和版本协商。
- 发布者 lease、订阅者 lease 和 generation 验证。
- 发布者与消费者的动态快照。
- 有序批量派发。
- 订阅者队列、内存预算和熔断。
- 异步请求/响应、超时和取消。
- 服务能力发现。
- MOD 卸载、热更新和异常清理。
- 诊断高水位、拒绝数、熔断数和回调耗时。

Broker 不在发布者线程同步调用消费者。

### 4.2 Android MOD 订阅者

Android MOD 在 `OnLoad` 或受控后台操作中申请订阅，在 `OnUnload` 前释放订阅。订阅会记录：

- `ModRuntimeKey`。
- loader kind、MOD ID 和 generation。
- 合同 ID、主版本和次版本范围。
- 派发上下文。
- 队列模式。
- 订阅者自己的 cancellation lease。

订阅回调执行时，Broker 必须进入订阅者的 callback lease 和 owner scope。这样回调内创建的 Hook、后台操作、输入订阅和其他资源仍归属于正确的 MOD generation。

### 4.3 PcCompat KeyViewer Adapter Hub

PcCompat 不将 JRP 写死为发布目标。`PcCompatKeyViewerPreviewRuntime` 成功完成以下步骤后，向 Hub 注册完整 Adapter：

```text
静态扫描
  -> Adapter 文档
  -> MOD/代理/游戏版本指纹验证
  -> override 验证
  -> IdentityTransform 验证
  -> BindingProvider lower
  -> 全部 consumer feature 资格确认
  -> Adapter generation 注册
```

一个 Adapter 可以包含多个 feature，例如：

- 主 KeyViewer。
- Foot KeyViewer。
- Ghost KeyViewer。
- Rain 或其他按键显示 feature。

Hub 将同一虚拟输入批次发送给整个 Adapter。每个 feature 使用自己的 lane、身份变换、Hybrid 状态和统计语义。单个 feature 失败时关闭该 feature；整个 Adapter 的 generation 失败时只熔断该 PcCompat MOD。

未来增加新的 PcCompat MOD 时，只增加新的 Adapter 注册，不增加 Replay 分支。

## 5. 合同命名和声明

### 5.1 标准合同

由 ModManager 预注册的标准合同可以使用全局 ID，例如：

```text
starray.virtual-input.playback.v2
starray.mod.interop.request-response.v1
```

标准合同包含固定的载荷布局、版本规则、Provider 数量规则和兼容测试。

### 5.2 未声明合同

MOD 不需要在 Manifest 或额外 JSON 中预先声明主题。第一次发布时，Broker 根据 owner scope 自动补全命名空间：

```text
mod/{publisherId}/{localTopic}
```

例如：

```text
mod/Replay/replay/input
```

未声明可见范围的合同默认是 `Public`，并进入公共合同发现列表，以优先保证 MOD 间联动便利性和未知兼容性。

发布者不能冒充其他 MOD 的命名空间，也不能把本地主题注册成未经预注册的全局合同。

### 5.3 可见范围

显式声明时支持：

- `Public`：所有兼容订阅者可发现并订阅。
- `DependenciesOnly`：仅允许声明依赖的 MOD。
- `AllowList`：只允许指定 MOD ID。
- `Private`：仅发布者自己可见。

没有声明时使用 `Public`。

## 6. 版本和发现

合同身份由以下字段组成：

```text
ContractId
MajorVersion
MinorVersion
SchemaId
ContentType
```

规则：

- 主版本不兼容时拒绝订阅。
- 次版本向后兼容时允许订阅。
- 订阅者必须声明自己接受的主版本和次版本范围。
- Provider generation 变化时重新完成协商，旧消息不能穿透到新 generation。
- 合同发现只返回描述信息，不执行发布者或订阅者代码。

如果发布者尚未加载，订阅保持 `WaitingForPublisher`，不创建轮询线程。

如果发布者完全不调用 Broker，无法通用推断其私有事件。已知旧协议由 ModManager 维护兼容适配器处理。

## 7. 虚拟输入合同 V2

### 7.1 会话语义

Replay 通过唯一的独占发布 lease 创建一个 `VirtualInputPlaybackV2` 会话：

- 同时最多一个活跃发布会话。
- 先到者持有 lease，后续发布者明确失败。
- 其他 MOD 不能抢占会话。
- `Complete`、Dispose、MOD 卸载或异常都会终止会话。
- 终止时向消费者发送取消和 session ended，清理所有 held 键和活动触点。

### 7.2 输入模式

回放期间使用 `Exclusive + Hybrid`：

- KeyViewer 只显示录制的输入。
- 用户实时触摸和键盘仍可操作游戏或回放界面。
- 用户实时输入不进入 Jipper、PcCompat 或其他 KeyViewer 的回放状态。
- 录制轨道中的触摸和键盘两路同时生效。
- 同一 lane 被两路按住时只产生一次逻辑按下，直到两路都释放才产生逻辑释放。

### 7.3 事件字段

逻辑事件包含：

```text
SessionGeneration
Sequence
OffsetMicroseconds
Device: Keyboard | Touch
Phase: Down | Move | Up | Cancel
CanonicalKey 或 PointerId
RepeatCount
X/Y
ViewportWidth/ViewportHeight
```

Broker 自己分配 `Sequence`，发布者不能传入伪造的全局序列。

键盘使用 ModManager 自己的稳定 canonical key，不直接依赖 ImGui、Unity KeyCode、Android KeyCode 或 Windows VK 的整数值。消费者再按自己的 Adapter 绑定转换。

Replay 发布端使用稳定名称：字母 `A`-`Z`、数字 `_0`-`_9`、`F1`-`F24`、`Keypad0`-`Keypad9`，以及 `Enter`、`LeftShift`、`PrintScreen` 等具名键。消费适配器兼容 `KeyA`、`Digit0`、`ArrowUp`、`ControlLeft`、`MetaLeft` 等历史/平台别名，但别名不是发布端的新格式。

触摸保留录制时的坐标和视口尺寸，由每个 Adapter 根据自身 lane 和坐标模式映射。

### 7.4 时序

V2 必须携带录制时序。Replay 现有数据结构已经保存触摸和键盘的相对时间，正式适配器应发布该时间，而不是使用 Broker 收到消息的时间。

消费者使用：

- `OffsetMicroseconds` 计算 KPS。
- 该时间驱动 Rain 的开始和结束。
- `Sequence` 处理相同时间点内的严格顺序。

长帧只能延迟显示，不能丢弃或把事件压成最终 held 状态。

## 8. V1 兼容

现有私有协议继续保留：

```text
Replay.Mobile.ReplayKeyViewerApi
```

兼容规则：

```text
新 Replay -> V2 + V1
新 Jipper -> 优先 V2，找不到时回退 V1
旧 Jipper -> 继续 V1
旧 Replay -> 新 Jipper 可通过 V1 降级接入
```

同一消费者不能同时收到 V1 和 V2。V1 消费者标记为 `LegacyReplayConsumer` 和 `DegradedTiming`。

V1 旧消费者的统计行为保持旧语义。只有 V2 消费者保证临时统计事务，避免把兼容性承诺扩展到无法表达该语义的旧 API。

## 9. PcCompat 转发规则

虚拟输入不写入 native raw input journal。native journal 有自己的 cursor、producer epoch 和 session generation，合流会污染实体输入和其他 MOD 的序列。

PcCompat 使用独立的虚拟输入分支：

```text
V2 batch
  -> Adapter Hub
     -> 每个 Adapter 的 ModActor
        -> 全部 FeatureState
           -> PcCompatKeyViewerConsumerRuntime
              -> 重写后的 Input.GetKey / GetKeyDown / GetKeyUp / GetAsyncKeyState
```

会话开始时：

1. 该 Adapter 停止消费实体 raw journal。
2. 提升 consumer publication generation。
3. 清除实体 held、touch slot 和 edge cursor。
4. 开始消费 V2 事件。

会话结束时：

1. 注入 Cancel，释放所有虚拟 held 状态。
2. 完成临时统计恢复。
3. 提升 publication generation。
4. 从 native journal 尾部重新开户，避免旧实体边沿重放。

## 10. 统计事务

### 10.1 V2 消费者

`StatisticsMode = Ephemeral` 时：

- 回放前保存 MOD 的累计统计。
- 回放期间允许更新显示所需的内存统计。
- 禁止回放统计持久化。
- 回放结束、取消、熔断或卸载时恢复快照。

### 10.2 PcCompat

PcCompat Adapter 可声明并验证以下统计角色：

- per-key count。
- total count。
- KPS 窗口和 press time 队列。
- Save sink。

只有能证明快照、临时写入和恢复路径的 Adapter 才能启用 Ephemeral。不能证明时，该 Adapter 的虚拟输入 consumer fail closed，不退化为任意反射写字段。

静态 Save sink 与静态相对路径可证明时，事务继续使用精确文件快照并在恢复后调用 Save sink。自绘 KV 的输入、lane、transition、count 已全部 Proven，但 profile 文件名只能在运行期计算时，不猜测方法名或文件名；事务改为快照该 owner/resource-generation 已绑定的 data overlay。该回退最多接受 256 个文件、单文件 1 MiB、总计 8 MiB，拒绝链接和越界路径；结束时恢复原文件并删除回放期间新增的 overlay 文件。它不读取或修改安装层、共享目录和其它 MOD 的根。

### 10.3 旧 V1

旧 Jipper 继续使用原有统计行为。新 Jipper 迁移 V2 后才获得临时统计保证。

## 11. 线程和派发

订阅者只能选择受控上下文：

- `SerializedWorker`：默认，每个订阅者串行处理。
- `UnityMainBatched`：按帧合并后提交到 UnityMain。

禁止发布者线程直接执行消费者回调。

虚拟输入事件使用值类型和批量派发；普通自定义合同使用受大小限制的只读字节载荷。每个 MOD 的回调必须通过自己的 callback lease 执行。

## 12. 载荷类型

不允许跨 ALC 传递 MOD 私有 CLR 对象。

支持两类载荷：

1. ModManager 预注册的强类型标准合同，适用于高频和严格 ABI 场景。
2. 带 `SchemaId`、版本和 `ContentType` 的只读字节载荷，适用于低频自定义主题；提供 UTF-8 JSON 辅助 API。

跨 ALC 传递 JSON 或字节载荷时，Broker 仍检查最大大小、主题配额和 generation。

## 13. 异步请求/响应

完整联动层支持异步请求/响应，但不提供 Broker 内部同步阻塞 RPC。

请求包含：

- `CorrelationId`。
- 合同和版本范围。
- Provider 选择模式。
- 截止时间。
- Cancellation token。

Provider 选择模式：

- `Single`：按兼容版本优先、稳定 MOD ID 次序选择一个 Provider。
- `FanOut`：发送给所有兼容 Provider，收集截止时间前的响应。
- `Targeted`：指定 MOD ID 和 generation。

Provider 卸载时，未完成请求返回 `ProviderRetired`，不能永久挂起。

## 14. 可靠性和熔断

合同创建时固定投递模式：

- `OrderedLossless`：严格有序，慢消费者溢出时熔断该消费者。
- `LatestState`：只保留最新状态。
- `BestEffort`：允许丢弃，适合诊断和遥测。

虚拟输入和 RPC 使用 `OrderedLossless`。

某个消费者发生以下任一情况时：

- 队列溢出。
- 回调连续异常。
- generation 不匹配。
- sequence 或 session 校验失败。
- 处理超出允许的 cooperative slice。

Broker 对该消费者执行一次 Cancel 和统计回滚，然后熔断该消费者。本次 Replay、游戏和其他消费者继续运行。

## 15. 资源和并发上限

初始生产上限如下：

| 项目 | 上限 |
| --- | ---: |
| 活跃虚拟输入发布会话 | 1 |
| 单合同订阅者 | 32 |
| 单虚拟输入消费者待处理事件 | 8192 |
| 单虚拟输入批次 | 512 |
| 虚拟输入全局队列预算 | 16 MiB |
| 单发布者活跃自定义主题 | 32 |
| 单自定义消息载荷 | 32 KiB |
| 单普通主题消费者队列 | 128 |
| 普通主题全局队列预算 | 16 MiB |
| 单调用方到 Provider 的 RPC 并发 | 16 |
| 全局未完成 RPC | 128 |
| RPC 请求或响应载荷 | 32 KiB |
| RPC 默认超时 | 5 秒 |
| RPC 最大超时 | 30 秒 |
| Broker 后台 worker | 2 |

虚拟输入不设置固定墙上时间速率限制，避免长帧后丢失录制事件；它受队列、批次和全局内存预算限制。

上限由以下因素推导：

- 单事件约 64 字节，8192 事件约 512 KiB/消费者。
- 32 个消费者的最坏虚拟输入队列约 16 MiB。
- Actor 采用单消费者串行执行，避免 MOD 代码并发重入。
- 现有兼容层沿用 64 项、4ms cooperative slice。
- 所有容量按实际积压惰性占用，不在启动时预分配全部上限。

## 16. 卸载和热更新

卸载顺序固定为：

```text
停止新消息
  -> 标记 subscription retired
  -> 中断等待和队列派发
  -> 等待 callback lease 静默
  -> Cancel held/统计事务
  -> 退役 owned InputSubscription
  -> 释放跨 ALC 引用
  -> 允许 AssemblyLoadContext 回收
```

旧 generation 的异步响应、队列消息和回调不能进入新 generation。

发布者热更新时，订阅者可以保留订阅但必须重新执行合同和版本协商；标准输入会话的当前状态只同步给新加入的消费者，不补放整个历史事件。

## 17. 错误和降级

错误必须按消费者隔离：

- Replay V2 发布失败：保留 Replay 游戏回放，记录联动错误。
- Jipper 消费失败：只关闭 Jipper 的虚拟输入显示。
- 某个 PcCompat Adapter 失败：只关闭该 PcCompat MOD 的 KeyViewer consumer。
- 一个 Adapter 的 feature 失败：只关闭该 feature。
- Broker 全局合同损坏：拒绝新订阅，不影响已经完成隔离的其他合同。
- 旧 V1 兼容适配失败：V2 消费者不受影响。

## 18. 实现检查清单

当前实现检查结果：

- [x] Android MOD 可以动态注册、注销和重新订阅公共合同。
- [x] 发布者先加载、订阅者先加载、发布者热更新三种顺序都有合同测试。
- [x] 同一 MOD 的旧 generation 消息不能进入新 generation。
- [x] 一个消费者队列熔断不影响其他消费者。
- [x] PcCompat 一次向完整 Adapter 广播，而不是只向 JRP 特判。
- [x] 同一个 Adapter 的多个 feature 都能收到同一输入流。
- [x] 触摸、键盘和 Hybrid 两路合并无重复 down/up。
- [x] V2 的时间和 sequence 在长帧后仍保持正确。
- [x] V1/V2 不会对同一消费者重复派发；新 Replay 在 V1 回调前建立 V2 lease，新 Jipper 仅在 V2 订阅可用时用只读 V2 活跃状态覆盖 Broker 回调延迟窗口。
- [x] PcCompat 实体 raw journal 在虚拟会话期间不被污染。
- [x] 回放结束后实体输入从 journal 尾部重新开户。
- [x] V2 统计事务在正常结束、取消、熔断和卸载路径都恢复；Save sink 暂时失败时事务保持可重试。
- [x] 自定义合同禁止传递 MOD 私有 CLR 类型。
- [x] 普通主题和 RPC 都执行载荷、速率、并发和内存配额。
- [x] ALC 卸载后没有 Broker 静态委托、队列消息或请求引用旧程序集。

## 19. 已确定决策

截至 2026-08-17，设计讨论已确定：

1. 回放期间只显示录制输入，实时输入不进入 KeyViewer。
2. 触摸和键盘轨道合并为 Hybrid 语义。
3. PcCompat 按完整 KeyViewer Adapter 广播，支持未来多 MOD、多 KV。
4. Android MOD 可以互相发布和订阅，Broker 负责生命周期和隔离。
5. 未声明合同默认 `Public`，并进入公共发现列表。
6. 未注册的全局合同 ID 被拒绝，本地主题自动添加发布者命名空间。
7. V2 保留精确录制时间，V1 作为降级兼容。
8. 消费者晚加入只获得当前状态，不补放完整历史。
9. 消费者队列溢出或异常时单独熔断。
10. 同时只能存在一个虚拟输入发布会话。
11. 跨 MOD RPC 完整支持异步请求/响应，不提供 Broker 内部同步阻塞调用。
12. 标准高频合同使用强类型值类型，自定义合同使用版本化字节载荷。
13. V2 使用临时统计事务，旧 V1 保留旧统计语义。
14. 所有后台派发复用固定 worker 和 ModActor，不创建逐 MOD 线程。
