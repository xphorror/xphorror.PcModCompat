# 通用动态数据源兼容决策

更新时间：2026-08-26

## 1. 文档目的

本文冻结 PC MOD 在 Android IL2CPP 兼容层中的动态数据源方案，重点解决旧版
MOD 通过动态 getter 工厂读取游戏状态时，读取结果为空、默认值或跨运行时对象
失效的问题。

本文是兼容层决策，不是 JPOV 源码改造方案。实现必须在 ModManager、PcCompat、
托管重写器、generated proxy 和 native bridge 内完成。

## 2. 已确认的访问差异

### 2.1 JPOV 的访问方式

旧版 JPOV 的 `GameRefs` 不直接依赖一组固定 generated proxy 属性，而是通过
`PatchManager` 创建动态委托：

```text
GameRefs.BindDelegates()
  -> PatchManager.CreateMemberGetter / CreateStaticPropertyGetter
  -> DynamicMethod 或 Delegate.CreateDelegate
  -> Func<...>
```

它读取的内容包括进度、当前序号、当前/首个 floor、floor 列表、conductor、
audio source、audio clip、pitch、offset、地图时间以及 floor 的前后关系和
运动参数。该路径的关键问题不是字段名字，而是委托创建后仍可能指向 PC Mono
成员，或者返回无法跨 CoreCLR/IL2CPP 生命周期保存的对象。

### 2.2 JRP 的访问方式

JRP 主要通过 generated proxy 的强类型对象图读取：

```text
scrController.instance
  -> currFloor
scrLevelMaker.instance
  -> listFloors
scrConductor.instance
  -> song / addoffset / songposition_minusi
```

同时有 native fixed-op、resource recipe 和 snapshot 兜底。因此 JRP 能工作只能
证明强类型代理路径的部分链路已经可用，不能证明 JPOV 动态 getter 工厂已经被
正确接管。

## 3. 总体决策

采用以下通用主路径：

```text
原始 PC MOD DLL
  -> 复制到兼容层工作目录
  -> 仅重写加载副本
  -> 接管动态 getter 工厂调用点
  -> 返回同签名的 owner/session 绑定委托
  -> 原始 MOD 方法体继续执行
  -> 委托通过稳定 IL2CPP proxy object graph 取值
  -> 高频标量可读取同一代 native snapshot
```

这条路径的核心目标是保留原 MOD 的控制流和业务状态，让兼容层替换运行时
边界，而不是把 MOD 逻辑改写成 JPOV/JRP 专用 recipe。

### 3.1 不修改 MOD

- 不修改 JPOV、JPKV、JRP 或其它上游 MOD 源码。
- 不修改原始发布 DLL。
- 只对导入后的加载副本执行托管重写。
- 不新增要求上游 MOD 调用的 facade，也不要求 MOD 作者重新编译。
- 不把某个 MOD 的业务逻辑复制进 ModManager。

### 3.2 不做 MOD 特判

实现选择不能依赖：

- MOD ID；
- 显示名称；
- 程序集文件名；
- 入口类型名；
- JPOV/JRP/JPKV 专用白名单。

上述信息只能用于日志、缓存目录、错误报告和资源隔离。是否接管一个调用点，
必须由目标程序集、目标类型、成员、静态性、参数/返回 ABI、读写方向和已验证
语义共同决定。

## 4. 动态 getter 工厂接管

### 4.1 接管范围

第一版通用识别器覆盖以下语义等价的工厂调用：

- `CreateMemberGetter<T>()`；
- `CreateMemberGetter<T,F>()`；
- `CreateStaticPropertyGetter<T>()`；
- `CreateStaticFieldGetter<T>()`；
- 经测试确认与上述形式等价的属性 getter 工厂。

必要时可以扩展 setter、属性 getter/setter 组合和只读字段访问，但每一种扩展都
必须新增 ABI 合同和回归测试，不能通过把所有调用降级为 `object` 来扩大兼容
范围。

### 4.2 严格匹配条件

每个工厂调用点至少需要确认：

1. PC 侧宿主程序集和目标类型；
2. 成员名称和成员种类；
3. 实例成员或静态成员；
4. 委托泛型参数及实际返回类型；
5. 参数数量、顺序和类型；
6. 字段/属性的读写方向；
7. Android generated proxy 的对应成员和 native ABI；
8. 调用点所在的异常和生命周期边界。

只要其中一项无法证明，就将该调用点标为 unsupported 或 registered-only，停止
该能力的自动启用。不得回退到 PC Mono getter，不得以 `0`、`false`、空集合、
空对象或固定字符串掩盖数据源缺失。

### 4.3 委托合同

重写后产生的委托必须：

- 保持原始托管签名；
- 保持原始成员的实例/静态语义；
- 保持原始异常传播或转换合同；
- 在同一 MOD session 内复用稳定绑定；
- 在调用前取得 callback/object lease；
- 在调用结束后按 LIFO 释放 lease 和 ambient scope；
- 不把 CoreCLR 对象直接作为 IL2CPP 对象参数传入；
- 不把已退休 generation 的对象或委托继续交给 MOD。

委托工厂接管的是“如何取得数据”，不是“替换 MOD 的业务方法”。`UpdateTime`、
`UpdateBPM`、Jongyeol 状态机和其它原始方法体仍由重写后的 MOD 副本执行。

## 5. 数据源分层

### 5.1 稳定 IL2CPP 对象图：语义主路径

对象图负责提供需要引用关系、对象身份或多个成员协同的语义。第一批通用根和
关系包括：

- `scrController`；
- `scrConductor`；
- `scrLevelMaker`；
- `scrFloor`；
- `AudioSource`；
- `AudioClip`；
- `PlanetarySystem`。

对象按需懒加载。关键根对象可以在 session 建立时预热；floor 链、音频对象和
其它下游对象只在调用需要时展开，不允许每帧构建完整对象图。

同一 resource session 内，相同 native 指针对应稳定的代理绑定。代理不跨
resource session、场景、关卡重载或 MOD generation 复用。

### 5.2 Native snapshot：高频标量优化路径

以下类型的高频、无引用标量可以从 native snapshot 读取：

- 进度；
- 准确率和误差相关数值；
- BPM/KPS；
- 音乐时间和地图时间；
- checkpoint；
- floor 数量和速度；
- 已纳入 V5 ABI 的其它固定标量。

snapshot 必须带有 `struct_size`、`abi_version`、generation 和字段有效性信息。
读取时只能使用同一 resource session 的 immutable snapshot。snapshot 是性能
优化和跨线程读取手段，不负责伪造对象关系、对象生命周期或 Unity 引用语义。

### 5.3 两层数据源的一致性

对象图和 snapshot 不能互相无条件覆盖：

- 高频标量优先复用当前 generation 的 snapshot；
- 对象关系和引用成员必须走代理对象图；
- snapshot 字段缺失时不能静默补零；
- 对象图刷新后不得把旧 generation 的标量写回当前 snapshot；
- 同一次 MOD 调用中需要一致视图时，必须固定读取 session generation；
- `PublishAccuracySnapshot` 等发布操作只能更新明确拥有的字段，不能重建并覆盖
  其它字段。

## 6. 代理、session 和 lease

### 6.1 代理身份

代理绑定键固定为：

```text
modId + resourceSessionGeneration + sessionEpoch + proxyType + nativeObjectPointer
```

`modId` 用于隔离不同 MOD 的缓存和所有权；resource generation 用于隔离热更新
和 MOD 重载；`sessionEpoch` 用于隔离场景切换、关卡重载和 gameplay 对象域；
proxy 类型与 native 指针共同区分同一 session 内的 Unity 对象实例。

### 6.2 线程约束

- 真实 IL2CPP 对象读取和写回必须在 UnityMain owner/session 上下文执行。
- worker 线程只能读取 immutable snapshot，或提交有界的 UnityMain 请求。
- worker 不得直接解引用 IL2CPP 对象代理。
- 动态 getter 委托若从非 UnityMain 线程进入，必须进入已有调度合同；不能同步
  无限等待 UnityMain，也不能在没有 owner 的情况下创建代理。

### 6.3 退休顺序

以下事件统一触发 lease 退休：

- 场景切换；
- 关卡重载；
- Unity 对象销毁；
- resource session generation 变化；
- MOD 禁用、卸载或加载副本替换；
- session 故障。

退休顺序为：

```text
阻止新调用
  -> 取消尚未开始的 worker 调度
  -> 等待已开始及已进入的 lease 返回
  -> 撤销 native/object 映射
  -> 清理 managed session 和 ALC
  -> 丢弃代理与 snapshot 缓存
```

旧委托在 retirement 后不得进入原 MOD 方法体。回调在竞态窗口中必须失败关闭，
不能继续使用上一代代理或上一代 snapshot。

## 7. 缓存和 ABI

动态 getter 重写结果不能只按 DLL 文件名缓存。缓存键至少包含：

- 原始 DLL SHA-256；
- 原始 DLL MVID；
- 游戏 revision；
- IL2CPP metadata 指纹；
- generated proxy surface hash；
- native snapshot ABI；
- rewrite/bridge ABI；
- proxy object graph contract；
- 动态 getter 规则版本。

任一项变化都必须使旧副本和旧委托绑定失效。缓存内容必须区分原始输入 DLL、
重写输出 DLL、规则/审计报告和运行时 lease 数据，禁止不同 MOD 或不同 generation
共享可变对象图缓存。

## 8. 失败策略

以下情况均不得自动降级为部分数据继续运行：

- 动态工厂签名不完整或存在歧义；
- Android proxy 缺少必需成员；
- metadata 目标不唯一；
- snapshot ABI 不匹配或 generation 不一致；
- 当前线程没有合法 owner/session；
- native 对象已销毁或 lease 已退休；
- 代理对象无法 materialize；
- 重写后仍残留 PC Mono getter 调用；
- 缓存身份与当前输入不一致。

失败结果必须带有可诊断的阶段、成员、generation 和 ABI 信息，并将相关能力
标记为 `unsupported`、`registered-only` 或 `faulted`。不能用“加载成功”掩盖
“数据源不可用”。

## 9. 性能约束

性能优先，但不得以每帧重建对象图换取兼容性：

- 高频标量读取使用 immutable snapshot；
- 代理查找使用有界 session 缓存；
- 相同 session 内的 getter 绑定只创建一次；
- 不在每次 getter 调用中重新解析 metadata；
- 不在高频路径执行全量资源审计、反射扫描或逐帧日志；
- worker 不因对象读取建立无限制同步等待；
- lease、代理和快照在 generation 退休后及时清理，避免热更新缓存累积。

最终性能结论仍需要设备实测；本机测试只能证明缓存、ABI、线程和生命周期合同，
不能替代 UnityMain/IL2CPP 运行时延迟测量。

## 10. 实现边界和验收顺序

实现必须按以下通用顺序推进：

1. 读取并固定现有动态 getter 工厂和 bridge ABI；
2. 在重写器中识别调用点并生成严格的委托接管描述；
3. 建立代理对象图的 root、关系、身份和 lease 合同；
4. 将当前 snapshot 字段完整暴露给 managed bridge，并保持字段所有权；
5. 加入 generation、退休、跨线程和异常边界测试；
6. 对真实旧 DLL 生成重写副本并执行生产审计；
7. 只构建受影响的托管项目和代理生成链；
8. 最后由设备验收 UnityMain 对象读取、场景切换、热加载、数据刷新和性能。

## 11. 明确状态

截至 2026-08-26：

- 决策已冻结；
- JPOV 源码和原始 DLL 不应被修改；
- 部分 snapshot、generated proxy 和 session 基础设施已经存在；
- 通用动态 getter 工厂接管、session 绑定、稳定 proxy 对象缓存和对应生产托管审计已完成；
- 当前实现已覆盖五类 `PatchManager` 工厂 ABI，并删除 JPOV 专用 getter call bridge；
- V7 native snapshot 已加入独立 `session_epoch`、显式字段有效位和稳定对象根，区分 gameplay/session 边界与普通 telemetry generation；
- 动态 getter 已按签名将已确认的高频标量路由到同代 snapshot，未采样字段回到 UnityMain 对象图；
- 每个 PcCompat managed session 已拥有独立 callback lease gate。Update、OnGUI、managed event、同步 Prefix 和动态 getter 均受 gate 保护；worker 对象图读取进入有界 UnityMain scheduler，snapshot 标量读取不触发 UnityMain 调度；
- worker 排队工作在开始执行时显式把 callback lease 线程所有权转移到 UnityMain；队列拒绝、scheduler 异常和未开始超时由调用方释放，已开始超时由 UnityMain 工作在 `finally` 释放，避免提前退休和 worker 线程自退休误判；
- retirement 会先停止新 callback、取消等待中的调度、排空已进入 lease，再清理对象、getter 和 snapshot generation 缓存；
- `CompatEnable` 的合法 `Enabling` 窗口只接受 session 自身预分配的 `_enableContext` 对象；同 ID/generation 的伪造 scope 仍被拒绝。共享泛型 IL2CPP method pointer 收到与规则类型不符的 boxed 值时，Prefix/Postfix 按“不适用”跳过，不计 callback 故障，也不阻断 original；
- 重写缓存 ABI 已提升为 `xphorror.pcmod-managed-cache.v71-snapshot-object-roots`，dynamic getter 合同为 v4、callback dispatch 合同为 v2；字段属性化、裸 `get_xxx()`、空根 snapshot 与真实 JPOV 发布 DLL 工厂重写均有合同测试覆盖；
- 真实 IL2CPP proxy 指针、对象图和设备运行时数据源仍未完成设备验收；
- 未经设备验证，不宣称 JPOV 数据源已经闭合。

## 12. 2026-08-26 共享采样链闭合修复

真实 JPOV 发布程序集的生产扫描、callback 翻译和 recipe 编译已确认会生成
`scrController.PlayerControl_Update -> OverlayPollTelemetry`，因此本次“getter 已绑定但无数据”
不是重写遗漏。实际系统缺口有两项：共享 gameplay snapshot 的刷新依赖该单个 Hook 成功安装，且
native telemetry cache 将音频、checkpoint、关卡身份、速度和核心进度成员视为一个全有或全无的
依赖组。任一外围 ABI 解析失败都会使整个快照不可用，JRP 的独立 fixed-op 仍可工作，因而表现为
JRP 正常而 JPOV 没有数据。

现有实现按以下通用语义修复：

- managed UnityMain 连续帧以 100ms 间隔调用 host-owned native sampler；native 仍保留自身 100ms
  节流。共享游戏事实不再依赖任一 MOD 的 `PlayerControl_Update` Hook 是否可安装；
- telemetry cache 只把 controller、conductor、level maker、floor 和 ADOBase 的核心状态列为
  必需能力；音频、checkpoint、关卡身份、planet speed 和 Unity 时间锚点独立解析，单组失败只使
  对应数据退化，不再清空进度与状态；
- 已识别的 snapshot 标量在非 gameplay 或场景切换状态使用当前 generation 的安全值，不回退到
  半构造的 IL2CPP 对象图。这样设置面调用 HUD 刷新时不会再触发
  `scrController.get_percentComplete()` 的空引用；
- `PcCompatDynamicGetterSnapshot` 每个 MOD/成员/generation 只记录一次不可用诊断，包含 provider、
  snapshot generation、有效字段、timeline/accuracy/BPM 计数和 gameplay 状态；热加载仅覆盖该
  成员的最后 generation，不按 generation 无限累积日志键；
- 仍保留严格边界：未知成员继续走受 lease 和 UnityMain 调度约束的对象图；核心 metadata、owner、
  session、generation 或 ABI 不合法时仍失败关闭，没有放宽 runtime manifest 门禁。

本机验证包括真实 JPOV recipe 合同、动态 getter、recipe/compiler、callback translator、managed
loader、真实 JPOV/JRP 生产重写和 native HUD 合同，共 `126/126` 通过；`starray_modmanager`
arm64 Release native 目标单独编译、链接和新导出符号审计通过。设备数据值与场景恢复仍由用户实机
验收，本机结果不替代该验收。

## 13. 2026-08-26 V7 对象根闭合

设备结果表明，标量 snapshot 正常并不等于对象关系链可用。JPOV 的 BPM、音乐/地图时间、floor、
checkpoint 和速度读取仍会先访问 `ADOBase.controller/conductor/lm`、`currFloor`、`firstFloor`、
`song` 或 `planetarySystem`。V6 只携带标量，无法为这些动态 getter 提供稳定 IL2CPP 根对象。

V7 在保持共享游戏事实与 MOD 私有展示状态分离的前提下增加：

- `valid_game_snapshot_fields`，由 native 明确声明本代可用字段组；
- controller、conductor、level maker、current/first floor、song 和 planetary system 七个对象根；
- controller/conductor 根从 singleton、`ADOBase` facade 和当前 Hook 实例中一次选择并一致发布，
  避免初始化窗口中有效实例被另一路径的空 singleton 覆盖；
- managed host 按类型与成员语义物化 generated proxy；对象身份缓存键包含 MOD、资源 generation、
  gameplay session epoch、proxy 类型和 native 指针；
- worker 线程只读取 immutable 标量，对象包装继续经有界 UnityMain 调度；session 退休或 epoch 切换
  会淘汰旧对象和 getter 绑定。

缓存 ABI 为 `xphorror.pcmod-managed-cache.v71-snapshot-object-roots`。本机定向回归扩大为
`131/131`，并通过 Android managed Release、arm64 native 单目标构建和 `122/122` JNI 导出审计。
候选产物哈希：

- `StArray.ModManager.Android.dll`: `55ECA13E6A294FF3944FAEBAA7865E7B66FAD062711A27BDE96C70084E8216BD`
- `StArray.ModManager.dll`: `7AE7B4B87EBE7BF85D700C53FA864FC5F8C35DBACD1974EFAE8353DDB2D8CD13`
- `libstarray_modmanager.so`: `97C24FE6BE8941FF851E75B23A70AC4BFADFE1EE9F4FFF18A6AC9FE20BB4D46A`

最终 stripped SO 大小为 `3,140,400` 字节。未生成 APK、未同步设备 runtime、未操作实机。
