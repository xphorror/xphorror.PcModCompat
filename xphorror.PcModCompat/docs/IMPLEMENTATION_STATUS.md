# PC MOD 兼容层实施状态

### 2026-08-29 Native MOD callback-only 重写

Android Native MOD 当前启用独立的 callback-only shadow ABI。它只改写旧式
`HookHelper.Hook` 的安装点和托管 detour wrapper：回调进入时恢复对应的
`ModRuntimeSession`、generation、owner 与 `ModDataDomain`，退出时释放 callback lease。
静态字段、文件、网络、异步和资源调用不参与此模式，避免全量 shadow 改写改变原生
MOD 的既有运行语义；`Assembly.Location` 作为 shadow 执行路径恢复机制例外重写到原
MOD 目录，保证 MOD 自带的 Assets/Emoji 等非托管资源仍可定位。没有旧式 Hook 的程序集
仍保留原始语义。缓存 ABI 包含模式版本，旧缓存
不会被复用。普通 Native MOD 的卸载仍经过 HookBroker 层退休与 session quiescence。

## 文档定位

本文是 `xphorror.PcModCompat` 的当前实施状态真值，回答两件事：

1. 哪些能力已经存在于当前源码并通过了何种验证。
2. 哪些能力只有设计、部分实现或仍未开始。

其它文档负责解释长期架构和具体机制；当“目标设计”与“当前状态”表述冲突时，以运行时行为、测试结果和本文为准。

### 2026-08-27 JPKV 多键布局 lane-origin 与有界前缀解析闭合

- 重启后出现的 `core capability closure is not Proven; a verified lowered plan is required` 不是 Adapter JSON 丢失能力。
  真实 JPKV 扫描中 input/lane/transition/count/inputActivation 已为 `Proven`，presentation/visibility 为
  `Probable`、persistence 为 `Unsupported`；该文案只是没有 lowered plan 时的旧兜底提示，掩盖了 lowerer 的真实错误。
- 真实失败是 BindingProvider 歧义：完整设置加载后，主键 provider 可返回 108 键，脚键 provider 可返回 16 键；旧
  lowerer 只验证候选长度能否覆盖当前 10 个触摸 lane，因此两个候选同时可用并以 `found 2 usable candidates` 失败关闭。
  不能靠把 32 键上限提高到 108 解决，因为那既无法识别主组，也会继续随 MOD 的合法布局上限变化。
- `keyviewer_adapter.json` 升级为 `keyviewer-adapter-v2-lane-origin`。扫描器从输入事务调用图恢复 provider 的
  `ConsumerLaneBase`：主组为 `0`，追加组为正基址（真实 JPKV 脚键组为 `24`）。调用实参若来自零参数、无字段读写、
  无调用、无分支/回边且方法体仅为 `ldc.i4; ret` 的纯常量 getter，也可保守折叠；其它 getter 继续保持未知，不按方法名、
  MOD、键数或字段名猜测。
- 多个运行期可用候选存在时，lowerer 只自动选择唯一且已证明 `ConsumerLaneBase == 0` 的主组；没有唯一零基址证明时仍
  失败关闭。主组 108 键和脚键 16 键可以同时存在，附加组不会再与触摸主投影竞争。
- provider resolver 不再完整枚举布局，也没有固定 32/108 键上限。lowerer 把当前计划的 `requiredCount` 传到 managed
  session，只物化所需前缀（当前 10 个触摸 lane）；因此 108 键布局仍由 MOD 完整持有，无限或异常 `IEnumerable`
  也不能拖住兼容层。配置 watcher 同样只保存和比较实际投影前缀，后缀变化不会触发无意义重发布；provider 抛错、
  返回不足前缀或出现非整数值仍被视为变化并撤下计划。
- 设置页优先显示真实 `_keyViewerLoweringStatus`，不再用 core closure 兜底文案覆盖 lowerer 原因。原
  `[DEBUG-jpkv-deep-v1]` 桥和诊断代码保留，但默认构建通过 `PCCOMPAT_DEEP_DEBUG` 条件裁掉日志调用及昂贵参数构造，
  避免重启后的高频刷屏。
- 托管重写缓存升级为 `xphorror.pcmod-managed-cache.v86-keyviewer-lane-origin-prefix`，旧 `902102...` 命中缓存会失效并
  重新扫描。核心定向回归 `68/68`，全部 KeyViewer、override/rebase/preview、真实 JPKV/JRP/JPOV UMM 重写审计及
  Android 输入契约扩展回归 `155/155`。
- Android Slim Release 托管定向重建为 `0` 错误、`209` 个既有警告。最终候选：
  `StArray.ModManager.dll` 为 `19,882,496` bytes / `80E144FEF91FA8057FCF2EB83E1ED1E7D3F8B50166E2AF1198E157219982A6A4`；
  `StArray.ModManager.Android.dll` 为 `636,416` bytes / `4F87654D9AC39480F877723F246670FC425F1D932D5AC42F3812BDAFABF0535A`；
  `ModAssemblyRewriter.dll` 为 `282,112` bytes / `9123F6B4BB8BBA25CDD1D098D40AF53E761E310233ABB058DB72CB6AFE6D7DE3`。
- 未修改 JPKV/JRP/JPOV 源码或原始 DLL，未运行顶层全量构建、未构建 native SO、未生成 APK、未同步 runtime，也未操作
  实机。设备侧仍由用户验证重启冷改写后出现 `loweringPlans>0`、`registeredPlans>0`，且不再显示 core closure 兜底错误。

### 2026-08-27 Unity Type-based 组件返回类型擦除闭合

- 新设备报告确认上一轮 JPKV 修复已进入运行时：JPKV 持续执行 owner-scoped 输入查询，触摸发布和消费均有记录，
  未再出现 `Rain.Awake` 或 `frame_fault`。新的失败独立发生在 JRP `CompatEnable`：
  `Overlay.InitializeProgressBar()` 将 `UnityEngine.Component` 转为 `UnityEngine.RectTransform` 时抛出
  `InvalidCastException`，导致 `managed_self_render=activation_failed`。
- 根因是 Unity 的 `GameObject.GetComponent(Il2CppSystem.Type)`、`GetComponents(Type)` 和
  `AddComponent(Type)` 在托管代理中声明返回 `Component`/`Component[]`。即使原生调用按
  `RectTransform` 等精确类型完成，Il2CppInterop 仍按声明返回类型创建基类包装器。上一轮 bridge 已正确选择宿主
  原生查询，却直接向泛型调用点返回该基类包装器，因此 JPKV 的“能查询到对象”回归通过，而 JRP 的真实派生类型转换失败。
- 组件 bridge 现在统一归一化所有 native `GetComponent`、`GetComponents`、`TryGetComponent` 和
  type-based `AddComponent` 结果：结果已是请求类型时原样返回；否则读取其有效 IL2CPP 指针，并由 Android host
  重新构造请求的生成代理类型。空结果保持 `null`/空数组语义；无有效指针或 host 返回错误代理类型时明确失败关闭。
  该逻辑不按 MOD、方法调用点、`RectTransform` 或具体组件名特判。
- 新增类型擦除夹具，宿主刻意以基类包装器返回同一原生指针。修复前两项回归为 `0/2`：泛型查询复现设备相同的
  `InvalidCastException`，native AddComponent 返回错误基类；修复后为 `2/2`。组件桥、真实 JPKV/JRP/JPOV
  重写审计、深度诊断和 ABI 合同组合回归为 `119/119`。
- 同类反查确认，当前已支持的单项、批量和 Try 查询全部经过同一归一化边界。长期文档已明确标记
  `GetComponent(s)InChildren/Parent` 尚不支持并失败关闭，本轮没有把它们伪装成已兼容能力。
- 托管重写缓存为 `xphorror.pcmod-managed-cache.v85-native-component-result-rewrap`，组件桥 ABI 为
  `PcCompatManagedComponentBridge.v13-native-component-result-rewrap`。Android Slim Release 托管定向构建
  `0` 错误、`205` 个既有警告。当前候选：
  `StArray.ModManager.dll` 为 `19,901,440` bytes /
  `45075EF9C6C65E5D0D3551F6831E7187D8601061AAC824099B0040E0F14BB488`；
  `StArray.ModManager.Android.dll` 为 `636,416` bytes /
  `F96DBA0CD506ACEEF9FE630D34DB2BF12E78F82654E2E4B8D604DFF8E16287B0`；
  `ModAssemblyRewriter.dll` 为 `282,112` bytes /
  `9123F6B4BB8BBA25CDD1D098D40AF53E761E310233ABB058DB72CB6AFE6D7DE3`。
- 未修改 JPKV/JRP/JPOV 源码或原始 DLL，未执行顶层构建、未生成 APK、未构建 native SO、未同步 runtime，
  也未操作实机。设备侧仍由用户验证 JRP `InitializeProgressBar` 可完成且三项自绘同时保持活动。

### 2026-08-27 JPKV 动态 Rain 组件宿主原生组件查询闭合

- 设备日志中的 `managed_self_render=enabled` 表示托管自绘已成功激活，不是失败状态。真实故障发生在约 9 秒后的
  触摸路径：JPKV 动态 `AddComponent<Rain>`，兼容层立即调度 `Rain.Awake()`；其第一句
  `GetComponent<RectTransform>()` 在 IL2CPP 内抛出 `NullReferenceException`。异常随后越过父
  `KeyViewer.Update()`，触发 `managed_self_render=frame_fault` 和会话卸载。
- 真实 JPKV 发布 DLL 的生产重写审计确认，旧规则只允许泛型参数为 MOD 托管组件时重写
  `GetComponent<T>`；`RectTransform` 属于生成代理组件，因此调用仍落到没有有效 IL2CPP `this` 的托管组件壳对象。
  同时，原泛型 bridge 固定查询 owner-scoped 托管组件表，不能返回宿主原生组件。这是通用的“托管组件查询宿主原生
  组件”能力缺口，不是字体、VirtualBundle、输入发布或自绘激活问题。
- `ModAssemblyRewriter` 新增 `ModOwnedOrProxyComponent` 泛型参数筛选，并统一用于
  `GameObject/Component` 的 `GetComponent<T>`、`GetComponents<T>` 和 `TryGetComponent<T>` 六条规则。
  `AddComponent<T>` 仍严格使用 `ModOwnedMonoBehaviour`，不会扩大托管组件创建权限。组件 bridge 按目标类型分流：
  生成代理组件经 Android host 查询真实 Unity 对象，MOD 托管组件继续查询 owner-scoped 组件表；实现不按 MOD、
  `Rain` 或 `RectTransform` 名称特判。
- 新增真实 JPKV 发布 DLL 回归，锁定 `Rain.Awake` 的 `GetComponent<RectTransform>`、`get_gameObject` 和
  `AddComponent<RainGraphic>` 重写结果；新增生命周期回归，证明托管组件注册后可在同一次 `AddComponent` 触发的
  `Awake` 中查询宿主原生组件。组件桥和真实 JPKV/JRP/JPOV 生产重写审计合计 `174/174` 通过。
- 深度诊断继续保留原有 `[DEBUG-jpkv-deep-v1]` 标签和降频策略；单次浅层字段快照增加 8192 字符硬上限，截断时附加
  `; <snapshot-truncated>`，避免故障后的大型 `OnDisable/OnDestroy` 快照刷屏。
- 托管重写缓存升级为 `xphorror.pcmod-managed-cache.v84-owner-aware-proxy-query`，组件桥 ABI 升级为
  `PcCompatManagedComponentBridge.v12-owner-aware-proxy-query`，代理重写身份升级为
  `xphorror.pcmod-proxy-rewrite.v22-proxy-component-query-filter`；旧重写缓存不会被继续复用。
- Android Slim Release 托管定向构建为 `0` 错误、`205` 个既有警告。当前候选：
  `StArray.ModManager.dll` 为 `19,900,416` bytes /
  `D0A112E09CF64E479F27751F80A93B32A90FE930A945B504F97A36B2709457A4`；
  `StArray.ModManager.Android.dll` 为 `636,416` bytes /
  `8046732AB40B25E18F1107F1C214F6CCFE137133EA8790B12822258A3FCAEEFD`；
  `ModAssemblyRewriter.dll` 为 `282,112` bytes /
  `9123F6B4BB8BBA25CDD1D098D40AF53E761E310233ABB058DB72CB6AFE6D7DE3`。
- 本轮未修改 JPKV/JRP/JPOV 源码或原始 DLL，未运行顶层构建、未生成 APK、未构建 native SO、未同步 runtime，
  也未操作实机。设备侧仍由用户验收：触摸生成雨滴后不应再出现 `Rain.Awake` 异常、`frame_fault` 或自绘会话卸载。

### 2026-08-27 TMP 可选实例材质降级与深度诊断降频

- 用户提供的唯一故障证据显示：JipperOverlayer 调用 `TMP_Text.set_font` 前，诊断读取
  `fontMaterial` 时由 IL2CPP 抛出 `ArgumentNullException(source=null)`。结合真实执行顺序可确定：字体 setter
  本身可以成功，字体自身 atlas/material 也可以修复；失败的是 TMP 尚未建立实例材质时的可选 getter。旧桥在真实
  setter 成功后再次强制读取该 getter，后置修补异常会越过字体桥并中断 MOD 自绘激活。日志中的
  `snapshot-failed` 由诊断捕获，不是直接崩溃点，但证明业务后置路径中的同一次读取存在确定性风险。
- `PcCompatManagedFontBridge.SetFont` 现在仍严格执行真实 setter，并严格校验字体自身 atlas/material；实例
  `fontMaterial` 改为可选后置能力。其 getter 抛异常时不回滚已成功的字体设置、不调用实例材质 setter，保留一条
  按状态去重的 `[DEBUG-jpkv-deep-v1]` 诊断，然后继续调用 `SetAllDirty()`。getter 可用时仍修复实例材质
  `_MainTex` 并通过真实 setter 重新应用。该行为不按 MOD、字体名、索引或具体异常消息特判。
- 新增回归直接模拟 `fontMaterial` getter 抛异常，断言 `SetFont` 不抛出、真实字体 setter 已执行、字体材质仍绑定
  atlas、实例材质 setter 未被强制调用且 `SetAllDirty()` 已执行。
- 深度诊断全部保留，但高频采样身份已收敛：字体从每个 Text 实例改为 MOD/generation/phase、Text 类型和 setter；
  输入从每个 KeyCode 改为 MOD、query 和 callsite；组件注册和生命周期从每实例改为 MOD/generation、组件类型和
  stage。字体为前 2 次、2 的幂次及每 4096 次；输入和组件 Update 为前 2 次、2 的幂次及每 8192 次；其它组件
  生命周期每 256 次，注册每 128 次。
- 组件存活清单从每实例每 5 秒改为每 MOD/generation 每 30 秒一条有界摘要：最多列出 12 个类型计数和 3 个代表
  实例，只有代表实例读取非静态浅层字段。其余 `[DEBUG-jpkv-deep-v1]` 直接写点经全局反查均位于注册、卸载、
  资源物化或异常等有限生命周期，不是逐帧热路径。
- Debug 定向回归 `78/78` 通过，覆盖字体桥、深度诊断、托管组件和托管渲染组件；`git diff --check` 通过。
  Android Slim Release 托管定向重建 `0` 错误、`210` 个既有警告。当前候选：
  `StArray.ModManager.dll` 为 `19,899,904` bytes /
  `54C8F6E3478B7946A96E0F85A46FD1EA9C60EEDD7665624F7A283D97BD7C8A35`；
  `StArray.ModManager.Android.dll` 为 `636,416` bytes /
  `CF6D545AD77C946CA387E3232477FCBAB04CA19449DDFB0DC7C34498EE2EA797`；
  `ModAssemblyRewriter.dll` 为 `282,112` bytes /
  `639DD228723441E340CCEC56BA1713D8D95674C22801D373A78178A93CE89AAC`。
- 本轮未修改 JPKV/JRP/JPOV 源码或原始 DLL，未运行顶层全量构建、未构建 native SO、未生成 APK、未同步
  runtime，也未操作实机。由于没有新的完整设备日志，当前只能声明离线根因与回归闭合；自绘和字形最终恢复、日志
  实际频率及触摸输入仍由用户在设备侧验收。

### 2026-08-27 JPKV 托管组件 Unity Object 语义与 Adapter 恢复闭合

- 目标以 `JipperKeyViewer-1.7.0/` 的发布版源码为结构参照，以设备实际加载的
  `JipperKeyViewer-AssetBundle/JipperKeyViewer.dll` 为二进制真值；后者 SHA-256 为
  `ABA77914A04F745E4B06EA46739493C4E7EE1E222DE27CDC3480DD45DFFF6AED`。未修改该源码树或原始 DLL。
- 根因已由源码、真实发布 DLL 重写审计和组件生命周期回归共同闭合：JPKV 的 `Key : MonoBehaviour` 由
  `PcCompatManagedComponentBridge` 托管，没有 IL2CPP 原生对象身份。原先仍直达 Unity
  `Object.op_Equality/op_Inequality/op_Implicit` 的判空把所有活动 `Key` 当作 fake-null，导致
  `ProcessKeyGroup` 跳过输入查询，`UpdateKeyText` 不写按键文字，`UpdateAllFonts` 也跳过字体更新。
- 三个 Unity Object 运算现由全局 managed call rewrite 转到 `PcCompatManagedComponentBridge`，不按 MOD、类型名、
  字段名或调用点特判。托管组件活动/退役状态存入 `ConditionalWeakTable`，不会阻止 collectible ALC 卸载；普通
  Unity 原生对象仍由 Android host 调用真实代理语义。比较逻辑同时覆盖两个已退役托管组件，以及已退役托管组件与
  已销毁原生对象的 Unity null-like 等价关系。
- 托管重写缓存升级为 `xphorror.pcmod-managed-cache.v83-managed-component-object-semantics`，组件桥 ABI 升级为
  `PcCompatManagedComponentBridge.v11-object-semantics`。Release 反编译核对确认三个重写规则、ABI 常量和桥实现均已进入
  最终程序集。
- KeyViewer Adapter 不再因包/游戏/代理/程序集指纹变化永久停在旧 override。兼容层仅在格式与 MOD 身份一致时安全
  rebase：刷新所有当前指纹，保留启用状态、输入模式、合法 lane 数和回退配置，只迁移当前扫描仍存在的精确角色，
  丢弃伪造、消失和重复角色；无法证明安全时继续拒绝。Adapter 扫描、override 载入/rebase 和注册阻塞现有去重状态日志。
- Debug 定向回归 `128/128`，覆盖托管组件生命周期与 Object 语义、真实 JPKV/JRP/JPOV 重写审计、JRP 回归锚点、
  KeyViewer 生产 catalog 扫描、override rebase、输入 ABI 与托管渲染组件。Android Slim Release 托管重建为
  `0` 错误、`210` 个既有警告。
- 当前 Release 候选：`StArray.ModManager.dll` 为 `19,897,344` bytes /
  `E5098D9028473FB58DBC64630507D99BC48F64EE3579361D3F8CB979620E30B4`；
  `StArray.ModManager.Android.dll` 为 `636,416` bytes /
  `589C10C8FAA0CBFCB31CA728C764B959EE476F17E7827E9DCA78891894FA9547`；
  `ModAssemblyRewriter.dll` 为 `282,112` bytes /
  `639DD228723441E340CCEC56BA1713D8D95674C22801D373A78178A93CE89AAC`。
- 未运行顶层全量构建、未构建 native SO、未生成 APK、未同步 `out/android_single` 或 Gradle runtime，也未操作实机。
  runtime 目录仍含旧候选，不能只替换 DLL 绕过 manifest 生成；应由后续正式构建重新收集托管产物并生成一致的 manifest。
  设备侧字体与触摸最终表现仍由用户验收。预期日志会出现 Adapter `ready/rebased`、JPKV 注册成功、输入查询数大于零。

### 2026-08-27 JPKV 字体与触摸深度诊断候选

- 当前设备现象仍是 JPKV 字体不显示、触摸输入不生效；JRP 在相同兼容层中正常。旧日志已经排除 VirtualBundle required
  资产未就绪、managed component 完全未运行、`Application.isFocused=false`、Adapter 未注册以及 consumer identity
  为空，但不足以区分组件内部早退、输入桥未命中、TMP 最终对象关系失效和 VirtualBundle 返回失效代理。
- 新增通用诊断基础设施 `PcCompatDeepDebug`，统一使用 `[DEBUG-jpkv-deep-v1]` 前缀。高频路径按前 8 次、2 的幂次、
  状态变化和周期窗口采样；日志控制集中在诊断基础设施中，不在各业务桥重复实现，也不按 MOD id、字段名、字体名或
  具体键数特判。
- managed component 链现在记录组件注册、owner/generation、生命周期绑定、每次采样 Update 前后的输入查询增量、耗时、
  Unity fake-null/销毁状态和浅层字段，并每 5 秒输出一次存活组件清单。该证据可区分 `Update=0`、组件已销毁、内部字段
  未就绪以及 Update 已运行但未查询输入。
- legacy input 与 KeyViewer consumer/preview 链现在记录 owner、callsite、key、匹配分支、输入 mode、held/down/up、
  source/session/registration generation、identity 到 lane 的映射、发布序列、触摸坐标与最终消费结果。该证据可区分
  重写调用未进入 bridge、identity 不匹配、状态已发布但查询返回 false，以及组件根本没有查询。
- TMP 字体链现在记录 `CreateFontAsset` 的 face metrics、warmup、character table、atlas 尺寸和 material，并在
  `set_font`、`set_fontMaterial`、`set_fontSharedMaterial` 前后记录 text/font/atlas/material 的对象身份和校验结果。
  VirtualBundle 链同步记录 required/optional 资产、materializer、payload、resolver、lease claim、liveness probe、
  `LoadAsset` 选择、投影链和最终返回对象，可判定 Ready 资产是否实际返回了错误类型、fake-null 或失效代理。
- 新增诊断基础设施合同 `4/4`；组件、字体、VirtualBundle 与 KeyViewer 输入相关定向回归 `170/170`；Android Slim
  Release 定向构建 `0` 错误、`205` 个既有警告。最终候选 `StArray.ModManager.dll` 为 `19,888,128` bytes /
  `B222ECE189C79CF15AB054849C27DC1E014BB3E2B335D4651CF63D6C87680123`，`StArray.ModManager.Android.dll` 为
  `635,392` bytes / `889D2ACEFB19F3D9533B424CD3CD85FC645BB38D8A21EC8EC2F8B432C4410A9C`。
- 本轮是诊断增强，不能仅凭离线合同宣称设备问题已经修复。下一轮必须由用户自行部署候选并提供包含
  `[DEBUG-jpkv-deep-v1]` 的日志，再依据组件 Update、输入查询、TMP setter 和 VirtualBundle 对象存活四条证据确定根因。
  未修改 JPKV/JRP/JPOV 源码或原始 DLL，未运行顶层全量构建、未生成 APK、未构建 native SO、未同步设备 runtime，
  未操作实机。

### 2026-08-27 VirtualBundle 就绪度、TMP 最终绑定与输入/组件诊断闭合

- 已确认旧日志中的 `ready=0/1` 描述的是原始桌面 AssetBundle 候选能否由 Android Unity 直接加载，不是
  VirtualBundle IR 物化结果。JPKV 原始候选为 `Windows + Unity 0.0.0`，直接加载被拒绝是预期行为；此前
  `CompatEnable` 已成功，反而证明两个字体和三个 Sprite 等 required 资产已通过独立 VirtualBundle 路径物化。
- 新增 `PcCompatVirtualBundleSessionReadiness` 与
  `PcCompatVirtualBundleRegistry.GetSessionReadiness(modId, generation)`，按 owner/resource generation 分别报告
  required/optional 的 Ready、Pending、Unsupported、Failed、`IsReady` 与 `LastError`。运行时、诊断面板和激活日志
  现在明确区分 `rawCandidateReady`/`rawReady` 与 `virtualBundle`/`requiredReady`，不再用同一个 `ready` 混淆两条链；
  managed 激活前会输出 `VirtualBundle required assets ready`。
- `PcCompatManagedFontBridge.SetFont` 已实现并纳入生产重写。桥先调用真实 `TMP_Text.set_font`，随后读取最终字体 atlas，
  修正字体自身 material 和文本当前 material 的 `_MainTex`，重新应用真实 material setter，并在代理提供能力时调用
  `SetAllDirty()`；`null` 保持原 setter 语义。`set_font`、`set_fontMaterial`、`set_fontSharedMaterial` 三条最终绑定路径
  现均由同一通用桥接管，不替换字体，也不按 MOD、字体名或索引特判。
- 托管缓存升级为 `xphorror.pcmod-managed-cache.v82-tmp-font-final-binding`，字体桥 ABI 升级为
  `PcCompatManagedFontBridge.v5-font-final-binding`。真实 JPKV 发布 DLL 的三类 TMP setter 均无残留直达调用，JRP/JPOV
  同时进入共享回归。
- `PcCompatLegacyInputBridge` 新增 owner-scoped 类型化诊断快照和卸载清理；managed session 以低频、仅状态类别变化的
  `PcCompatManagedPipeline` 摘要关联组件注册/Update、focus、输入 queried/matched/true、consumer identity 和
  VirtualBundle required 就绪度。该摘要替代逐查询、逐触摸热日志，不随帧计数刷屏。
- 定向合同结果：资源/字体/组件/输入 `118/118`，缓存 ABI/通用重写/资源编译 `98/98`，真实 JPKV/JRP/JPOV
  production rewrite 审计 `27/27`。Android Slim Release 定向构建 `0` 错误。
- 最终候选：`StArray.ModManager.dll` 为 `19,844,608` bytes /
  `6607B703463627FB94842BAC8BA9DFAB8B299F9E69001B42288C0213FE52AA94`；
  `StArray.ModManager.Android.dll` 为 `635,392` bytes /
  `00CE45B1853B387183D6C7AE06F990E51DF84BC73EADEDAA384468ACCB77E03C`。
- 未修改 JPKV/JRP/JPOV 源码或原始 DLL，未运行顶层全量构建、未生成 APK、未构建 native SO、未同步设备 runtime、
  未操作实机。当前不能宣称设备问题已经解决；字体最终显示和触摸消费必须由用户使用该候选验收，并依据
  `VirtualBundle required assets ready` 与 `PcCompatManagedPipeline` 判定断点。

### 2026-08-27 lowered consumer plan 与 MOD 自有配置的运行时漂移闭合

- 离线审计（`ilspycmd` 读设备正在使用的 2026-08-12 发布 DLL，SHA-256 `ABA779…6AED`）确认：`GetKeyCode()` 是
  `static`，每次调用现读 `Settings.Data.KeyViewerStyle` 做 switch，8 种主样式各返回一个不同的数组字段，
  `GetFootKeyCode()` 同理 8 种；MOD 自己在 `ProcessMainAndFootKeysInUpdate` 按
  `cachedKeyStyle != data.KeyViewerStyle` 刷新缓存，**所以 MOD 侧会跟着配置变**。
- 缺口是我们侧不跟着变。`RefreshKeyViewerPreviewRegistration` 只有 4 个触发点——构造、`CompleteLoad`、
  `OnManagedActivationCompleted`、以及用户保存**我们自己**的 override store——全是一次性生命周期事件，
  `PcCompatManagedModSession` 内没有任何指纹或重新 lowering 机制。因此初值是对的（最晚触发点晚于 MOD 的
  `LoadSettings`，读到持久化样式），但用户在 MOD 自己菜单里改样式或改键位之后，快照与 MOD 实时配置必然分叉：
  触摸 lane 发布 MOD 不再查询的 identity，MOD 查询没人发布的 identity，**输入静默停止到达且不会自愈**，
  直到重载 MOD。这是可离线证明的确定性缺陷，不依赖设备日志。
- `PcCompatKeyViewerBindingPlanLowerer` 新增 `ResolvedProviders`：lowerer 本来就调用了每个候选 provider，是唯一
  能在不二次进入 MOD 代码的前提下报告原始序列的位置。只报告每个 feature **最终选中**（含恢复路径选中）的那一个，
  被拒候选不进入观察集；External 模式的 presentation-only feature 也报告，其标签由同一序列渲染、会同样陈旧。
- 新增 `PcCompatManagedProviderSequenceWatcher`（通用，只认 provider role 与整数序列，不认样式、字段名或键数），
  按 candidateKey 记录指纹并自带 500ms 轮询闸门。三处判断是刻意的：**解析失败也算变化**并撤下 plan（provider
  抛错、返回不足当前投影前缀或包含非整数值时，报告变化才能失败关闭，否则 consumer 会继续发布 MOD 不再读取的
  identity）；**基线按 candidateKey 合并而非整体替换**（lowering 失败什么都不报告，丢掉没被提到的 provider 就
  等于丢掉「能让它恢复」的那次值变化）；**指纹只覆盖当前计划实际消费的前缀**，布局后缀不影响当前 lane 投影，
  不应触发无意义重发布或迫使兼容层完整枚举 108 键布局。
- `PcCompatManagedModSession` 仿照既有 activation observer 增加 configuration poll observer，从
  `TryDispatchUpdate` 在 `TryDispatchUpdateCore` **之外**通知——观察器解析 MOD 成员时会自己进入 update context，
  在已进入的作用域内再嵌套没有理由。闸门放在观察器而非会话：会话无法知道任意观察器的正确节奏，两层计时器会让实际
  节奏变成两者之积；闸门关闭时每帧代价是一次锁加一次比较。`PcCompatModPlugin` 订阅后在漂移时记一次低频日志、把
  原因显示在设置面板并重新 lowering/register；generation 变化时清空指纹与原因文本。
- **未升级托管缓存或 bridge ABI，这是刻意的**：本轮没有改任何重写规则、闭包清单、bridge 约定、泛型参数擦除、
  setter 后置语义或生成代理面，改动全部位于宿主侧的 lowering/发布链内。无必要地升级身份会让设备白跑一次全量冷改写。
  缓存仍为 `xphorror.pcmod-managed-cache.v81-input-hotpath-diagnostics-removed`。
- 顺带修掉一处既有缺陷：`PcCompatOverlayRuntime.CloneSnapshot` 漏掉 82 个可写属性中的 10 个——`SessionEpoch`、
  `HasExplicitGameSnapshotValidity`、`ValidGameSnapshotFields` 与全部 7 个对象根指针。专门拦这个的
  `OwnerCloneCopiesEveryStoredSnapshotProperty` 在能报告漏项之前就抛异常挂掉，因为它的 `CreateNonDefaultValue`
  没教过枚举，异常把缺陷盖住了。**当前是潜在而非活跃**：owner 投影克隆只有 4 个消费者、全在 `PcCompatModPlugin`
  自己的 HUD/设置里且都不读这 10 个属性，JPOV 数据链两处 `FromOverlay` 取的都是 `GetSharedGameSnapshot()`
  共享实例、不经过克隆。**因此这不是历史上 JPOV「进度正常、其余字段为空」的根因，不得如此宣称。**
- 改动文件：`PcCompatKeyViewerBindingPlanLowerer.cs`、`PcCompatManagedProviderSequenceWatcher.cs`（新增）、
  `PcCompatManagedModSession.cs`、`PcCompatModPlugin.cs`、`PcCompatOverlayRuntime.cs`；测试
  `PcCompatManagedProviderSequenceWatcherTests.cs`（新增 9 项）、`PcCompatKeyViewerBindingPlanLowererTests.cs`
  （新增 4 项）、`PcCompatOverlayOwnerIsolationTests.cs`。
- 全量回归 `1360` 通过 / `0` 失败 / `2` 按原条件跳过。本轮首次把 `w64devkit` 的 gcc 放进 PATH，因此
  `test_native.dll` 是真编出来的，只跳过需要 cmake 的 Windows native 项目。`PcCompatUmmModRewriteAudit` 27/27，
  真实 JPOV 与 JPKV 发布 DLL 均为 `Issues` 空、`ManagedBridgeIssues` 空、`OutputWritten=True`。
- Android Slim Release 定向构建 `0` 错误。候选 `StArray.ModManager.dll` 为 `19,825,152` bytes /
  `45F58F2BE219F420D1A0AB9FED52F360E2DF86E232D104E5C6C860809F930933`，`StArray.ModManager.Android.dll` 为
  `635,392` bytes / `DC66BACCD1DA4BCD4EA41B5A8E1CC6DDE17E95243FB2C5BC56C6392E682A816E`。
- 用户提供的 2026-08-27 17:42:41–17:43:30 设备日志**无法反映本条改动**，有两个独立原因：一是整场
  `SCENE state` 采样全为 `scene=scnMobileMenu isScnGame=0 gameworld=0`，从未进关，16 条 dispatcher 采样全部
  `visibleOwners=0`，键盘显示器本来就不该显示；二是采样共 49 秒、末尾 Ctrl-C，plan 首次注册在 17:43:18 之后只剩
  12 秒且期间没有任何配置变更，触发条件从未满足。日志里也没有缓存身份或版本横幅，因此**连设备是否已加载本轮候选
  产物都无法从该日志确认**。
- 该日志正面确认的是启动链健康：JPKV `managed rewrite rewritten=477 bridge=476 passthrough=81`、
  `static scan patches=0 issues=0`、`callback translation unsupported=0`、
  `recipe compatibility=supported features=1 rules=1`、`managed activation completed elapsedMs=590.690`、
  `managed_self_render=enabled`、`Converted 15 traditional font(s) to TMP_FontAsset`，以及最关键的
  `keyviewer adapter registration ... activationReady=True loweringPlans=1 registeredPlans=1 loweringIssues=0
  previewRegistered=True` 与 `boundary=open result=ready cursor=13 consumer=1 registration=1`。全日志零托管异常
  （唯一栈是 Firebase 连接失败）。这印证了上文的判断：**初值正确，本条修的是之后才发生的漂移。**
- 该日志暴露的三项独立未闭合项（均与本条改动无关）：JPKV 是三个 MOD 中唯一资源链被判
  `compatibility=unsupported groups=3 ready=0/1 loadEnabled=False`（JPOV/JRP 为 `partial`），但 15 个字体确实经
  VirtualBundle 转换成功，说明路径不死而是唯一候选组从未 ready；JPOV `callback translation rules=1 translated=4
  unsupported=15`、`recipe features=5 rules=23 unsupported=15` 是全日志最大缺口数；一条
  `ThreadGuard: patch window 0x780a8f8288 has interior branch`（整体 `28 instrumented, 0 skipped`）。
  另确认 43 条 `managed_event skip ... reason=no shim registration` 中 JPOV 激活后仍跳过的 5 条是**预期交接**——
  `RDC.set_auto`、`scrMisc.GetHitMargin`、`scrShowIfDebug.Awake/Update` 在 17:43:08–09 由原生 recipe 分别装成
  slot 12/30/38/39，不是缺陷。
- 未修改 JPKV/JPOV/JRP 源码或原始发布 DLL，未按 ModId 或字段名特判，未运行顶层全量构建、未生成 APK、
  未构建 native SO、未同步 runtime、未操作实机/ADB/安装。设备侧仍需用户验收：进关后改样式或改键位时是否出现
  `keyviewer provider configuration changed` 并随即重新注册、Full108 保持 plan 且只投影所需前缀的实际表现、500ms
  轮询与 provider 反射在 UnityMain 的真实耗时。

### 2026-08-27 JPKV 动态字体最终材质绑定与输入热日志清理

- JPKV 空字形的最终缺口不在字体对象保活，也不在 `CreateFontAsset` 的初始可用性检查。JPKV 会从场景扫描结果中选择
  动态 `TMP_FontAsset`，克隆其材质后写入 `TMP_Text.fontMaterial`；旧兼容层只验证源字体的字符表、atlas 和源材质，
  没有保证最终克隆材质的 `_MainTex` 仍绑定该字体的 `atlasTexture`。JRP 主要使用预烘焙字体和
  `fontSharedMaterial`，所以没有经过这条不完整的动态克隆链。
- `PcCompatManagedFontBridge` 现在同时闭合创建端和最终 setter：动态字体创建后把 `atlasTexture` 写入源材质
  `_MainTex`；`TMP_Text.set_fontMaterial` 与 `set_fontSharedMaterial` 在调用真实代理 setter 前，重新从当前
  `TMP_Text.font.atlasTexture` 校正传入材质的 `_MainTex`。传入 `null` 时仍保持原 setter 语义。实现不替换字体，
  不按 MOD id、字体名或索引特判。
- 生产重写接管上述两个 TMP setter，字体桥 ABI 为
  `PcCompatManagedFontBridge.v4-material-atlas-binding`。托管缓存现为
  `xphorror.pcmod-managed-cache.v81-input-hotpath-diagnostics-removed`，旧重写缓存不会复用。
- 输入诊断完成定位后，逐查询、逐触摸和逐发布的 `[DEBUG-kv-input-v3]` 热路径日志及其预算状态均已删除；每次
  registration 只保留一次 provider `boundary=open` 摘要，真实 fault 仍按原错误路径报告。输入桥 ABI 为
  `PcCompatLegacyInputBridge.v3-hotpath-diagnostics-removed`。
- 字体 bridge、真实 JPKV/JRP/JPOV 生产重写、组件帧门禁、KV 输入状态和 Android 静态合同组合回归
  `171/171` 通过。未修改 JPKV/JRP/JPOV 源码或原始 DLL，未运行顶层全量构建、未生成 APK、未操作实机；
  JPKV 最终字形和触摸响应仍由用户在设备侧验收。
- Android Slim Release 定向重建 `0` 错误。候选 `StArray.ModManager.Android.dll` 为 `635,392` bytes /
  `A967D831F975DDEBC200195C15B5A8C58F12CB7140474449639CE8AAE5349D64`，`StArray.ModManager.dll` 为
  `19,816,448` bytes / `C4EB7CE46039F6FD94F2A4B59231BEDF7993BB68135972183C2F214DE38D0F3E`，
  `ModAssemblyRewriter.dll` 为 `282,112` bytes /
  `639DD228723441E340CCEC56BA1713D8D95674C22801D373A78178A93CE89AAC`。候选输出不含
  `Iced.dll` 或 `TerraFX.Interop.Windows.dll`。

### 2026-08-27 JPKV 焦点、输入查询、字体可用性与 VirtualBundle 卸载闭合

- 源码回查确认 JPKV 的 `Update()` 与 `ProcessKeySelection()` 都先读取 `Application.isFocused`。旧代理可能返回
  `false`，使 JPKV 在 `Input.GetKey/GetKeyDown/GetKeyUp` 之前退出；这解释了统一触摸状态已有发布记录、JRP
  正常，但 JPKV 没有任何消费查询。现在所有 PC MOD 的 `Application.get_isFocused` 都重写到通用
  `PcCompatManagedApplicationBridge`，由 Activity 的 resumed/window-focus 状态经 Java/JNI/native ABI 提供；状态
  尚未观察或 ABI 不可用时按 Unity 前台默认值 `true` 失败开放，避免启动竞态永久屏蔽输入。
- 定位阶段曾把 `[DEBUG-kv-input-v3]` 额度改为进程级 `(modId, registrationGeneration)` 共享预算，以确认线程
  切换不是刷屏来源；当前版本已按上节删除逐查询、逐触摸和逐发布日志。此前日志永远只有 JRP，是因为 JPKV 被焦点门
  提前短路，尚未进入 legacy input bridge，不代表触摸发布只面向 JRP。
- `TMP_FontAsset.CreateFontAsset(Font)` 的验收从 face metrics + `TryAddCharacters` 扩展为渲染就绪合同：字符表非空、
  atlas 存在且宽高大于 0、material 非空。代理 surface 新增 `get_characterTable` 与 `get_atlasTexture`，生成产物合同
  验证这两个 MethodDef 及返回类型；默认 `FontIndex=1` 不再选中“非空但不能渲染”的 TMP 包装器。
- `AssetBundle.Unload(bool)` 不再丢弃参数：`false` 只关闭虚拟句柄并把 release-owned 资产留到会话 teardown；`true`
  在 owner 生命周期锁内冻结 bundle，按依赖逆序立即释放并清理 AssetUse、ReleaseLease 与物化缓存，之后允许干净重新
  物化。同一可释放代理跨 bundle 领取所有权会失败关闭，避免一个 bundle 卸载后给另一个 bundle 留下失效包装器。
- managed rewrite 缓存升级到 `xphorror.pcmod-managed-cache.v79-virtual-bundle-unload-semantics`。本轮定向回归
  `40/40` 通过；代理闭包为 15 个程序集、195 个精确类型，生成 207 个类型和 16 个泛型初始化器，
  `missingAndroid=0`、`unresolvedMetadata=0`、严格审计 issue `0`。
- Android Slim Release 定向构建 0 错误。`StArray.ModManager.Android.dll` 为 `634,880` bytes /
  `8BC635DBE6785FD4673BCB3AC07BCED5D26EF6A995CA0080B872AA7A5A010F34`，`StArray.ModManager.dll` 为
  `19,815,424` bytes / `C5333757E8AA1BBE67566DCF97BA7076000DF2243E839F3E9B51929E6D47027C`，
  `ModAssemblyRewriter.dll` 为 `282,112` bytes / `639DD228723441E340CCEC56BA1713D8D95674C22801D373A78178A93CE89AAC`，
  generated `Unity.TextMeshPro.dll` 为 `32,768` bytes /
  `52D17E4530ED0DC4EC2E2D2B0FC6D5113DA225BEA7610CF5DB58BAA7385D5C38`。
- 未修改 JPKV/JRP/JPOV 源码或原始 DLL，未运行顶层全量构建、未生成 APK、未同步 runtime、未操作实机。
  设备侧字体与触摸行为仍由用户验收。

### 2026-08-27 JPKV 菜单、字体与 Adapter 激活时序闭合

- JPKV 分类按钮有按压动画但部分切换无效的根因位于通用 `SelectionGrid` 实现：旧选中格在同帧仍返回
  `true`，会覆盖较低索引的新点击。网格现在只把未选中格的 `false -> true` 视为新选择，不改变 MOD
  声明的布局组拓扑，也不按 MOD 或菜单名称特判。
- Android 上部分 legacy `Font` 在 native FontEngine 拒绝字体面后仍会留下非空 TMP 代理。新增
  `PcCompatManagedFontBridge` 接管 `TMP_FontAsset.CreateFontAsset(Font)`，通过 `TMP_Asset.get_faceInfo()`
  的 `pointSize/unitsPerEM/lineHeight` 判断字体面是否可用；失败返回 `null`，不把无效字体按成功结果加入列表。
  `fallbackFontAssetTable` 原生为 `null` 时，可写集合 getter 现保留 owner，通过真实 proxy setter 创建并绑定
  IL2CPP List，再返回写穿托管副本，后续 `Add/Remove/Clear/Insert` 不再丢失。
- KV Adapter 的首次降低可能早于 `CompatEnable/Awake`，此时实例字段尚未就绪。managed session 现提供激活完成
  观察者；只有 runtime 成功取得 managed presentation ownership 后才通知。plugin 按 resource generation
  幂等重新降低并替换注册，避免提前注册 fallback 导致双重绘制。每代输出一次
  `keyviewer adapter registration` 摘要，包含 `activationReady/loweringPlans/registeredPlans`。
- 首轮设备日志中 JPKV、JRP、JPOV 均在原生 fallback 表为 `null` 时失败于
  `Il2CppSystem.Collections.Generic.List<T>..ctor()`。根因是 `CopyOrCreateBoundList` 初版使用无参构造，
  但 dependency-closed Android generated corlib 只暴露 `List<T>(Int32)`。初始化现统一使用容量 `0`
  构造；新增生产 IL 合同禁止桥再次引用无参构造。同类回查确认其它 IL2CPP List 创建路径已经使用容量构造。
- 缓存升级为 `xphorror.pcmod-managed-cache.v77-font-face-and-adapter-activation`。菜单、字体、VirtualBundle
  激活、bridge rewrite、集合 ABI 与真实 JPKV/JPOV/JRP 生产重写审计组合 `117/117` 通过。
- 代理闭包为 `195` 个精确类型、`15` 个程序集，生成 `207` 个类型、`16` 个泛型初始化器，
  `missingAndroid=0`、`unresolvedMetadata=0`、audit issue `0`。终检发现原 surface 只把
  `get_faceInfo/set_faceInfo` 登记为方法，导致 MethodDef 存在但 PropertyDef 不完整；现改为读写属性 surface，
  `ProxyAssemblyAudit` 和生成代理合同均强制要求 getter，避免再次假绿。
- Android Slim Release 定向构建 `0` 错误。候选 `StArray.ModManager.Android.dll` 为 `634,368` bytes /
  `83131E5713938BD6298B7264728B2D057E27F281AFF1F0D259010F847C11937F`，`StArray.ModManager.dll` 为
  `19,799,552` bytes / `366DAA240C2C81B55DDB663EAC21CF3D0FE6502B03DAB079FBECB738CFE28569`，
  `ModAssemblyRewriter.dll` 为 `282,112` bytes / `639DD228723441E340CCEC56BA1713D8D95674C22801D373A78178A93CE89AAC`，
  generated `Unity.TextMeshPro.dll` 为 `31,744` bytes /
  `B0E8F4A7BF930FB1833FB15B71EF9607BEAF2E58EFFC4B895ABB4CBB4B3B480C`。
- 未修改 JPKV/JPOV/JRP 源码或原始 DLL，未运行顶层全量构建、未生成 APK、未同步 runtime、未操作实机。
  字体渲染、菜单切换和 Replay 触摸显示仍由用户在设备上验收。

### 2026-08-26 JPKV 泛型资源需求与 Unity Font 物化修复

- 新版 `last_managed_failure.txt` 已越过 owner 路径桥，直接失败于
  `KeyViewer.TryLoadResources -> VirtualBundle.LoadAsset`：设备资源编译日志为 `assets=13 required=0`，运行时访问
  的资产仍是 `MetadataOnly`。根因是旧 `AssetLoadFlowAnalyzer` 只解析普通 method token，并要求结果写入
  `stsfld`；JPKV 的五个调用均为 `AssetBundle.LoadAsset<T>(常量名)` 的 `MethodSpecification`，结果写入实例字段
  或局部变量，因此真实需求没有进入 recipe/IR。
- 资源流分析现完整解码 ECMA-335 opcode，解析闭合泛型 method specification，并把“按名加载”与
  `LoadAllAssets<T>` 作为独立的已证明请求；常量可直接入栈或经可证明 local 传递。请求证明不再依赖静态字段，
  但动态字符串、跨分支歧义和无法追踪的 `Type` 参数仍失败关闭，不扫描附近任意字符串猜测资产名。该机制按
  AssetBundle API 形态工作，没有 JPKV mod-id 或资产名特判。
- 新增 `FontFromFile` Resource IR：导入器从 Unity `Font.m_FontData` 提取 OTF/TTF。AssetsTools 同时支持直接
  `ByteArray` 与 Unity 6000 实际出现的 `vector<Array<char>>` 字段布局；Android 在 UnityMain 校验 payload
  长度/SHA/路径后通过 generated `UnityEngine.Font(string)` 构造并纳入 session teardown。JPKV 的三个 Sprite
  继续走 `SpriteFromTexture`，两个字体走 `FontFromFile`。
- `ResourceIrCompiler` 现在在发布前终检所有 `RequiredByMod`：任何必需资产仍为 `MetadataOnly/Unsupported`
  会在导入期连同资产名、期望类型、源类型和 extraction failure 明确失败，不再延迟到 MOD `Awake` 才抛出无身份
  的 `materializer is not implemented`。
- 缓存身份升级为 `resource-compiler-v3-proven-load-requests` 与
  `resource-ir-compiler-v5-font-file`，旧 `required=0` 产物不会复用，升级后会冷编译一次。真实 JPKV 发布 DLL
  快速请求合同 `1/1`、23.5 MB 真实 bundle 冷编译/稳定缓存回归 `1/1`、资源编译/IR/缓存/VirtualBundle 组合
  `42/42` 通过；真实 IR 恰有五个 required，且没有 `MetadataOnly/Unsupported`。最终 Android Slim 候选
  `StArray.ModManager.Android.dll` 为 `630,784` bytes / SHA-256
  `DAAFDB003BE8758EC54605B9180563EF42A1E4204C0B5907353134030129826E`，
  `xphorror.PcModCompat.Resources.dll` 为 `202,752` bytes / SHA-256
  `F7F1C2088EF7F015333ABF91A9DF972944AAC9C9E40A7A77748E72FA1992AC43`。未构建 APK/native SO，未操作实机，
  Android `Font(string)` 的设备行为仍由用户验收。

### 2026-08-26 JPKV 虚拟包根与资源翻译缓存修复

- `last_managed_failure.txt` 的直接根因是 UMM 适配器对 `ModEntry.Path` 调用
  `Path.GetDirectoryName`。兼容层为兼容 JPOV，向所有 UMM MOD 暴露的是 MOD 包目录而不是程序集文件；
  原始 BCL 调用因此把 JPKV 的 `ModPath` 算成共享 `mods` 根，后续 `mods/config` 被 owner 文件隔离正确拒绝。
- PcCompat 托管重写现将 `Path.GetDirectoryName(string)` 路由到 owner-scoped VFS。普通相对路径和虚拟根内
  子路径保持 BCL 语义；参数恰好是 package/config/cache/log/temp/data-overlay/shared-readonly 根时，父目录钳制为
  该虚拟根；其它 MOD 或所有 owner 根之外的绝对路径失败关闭。没有修改 JPKV/JPOV 源码或 DLL，也没有按 MOD id
  特判。托管缓存升级为 `xphorror.pcmod-managed-cache.v75-owner-path-parent`，路径桥 ABI 升级为
  `PcCompatManagedPathBridge.v4-owner-root-parent`。
- JPKV 的 23,489,050 字节 UnityFS 冷路径会在 AssetsTools 中完成全 Bundle 索引和资源 IR 物化；本机真实 fixture
  冷编译为 `15,546 ms`，设备日志对应约 `47 s`。此前产物只存在导入目录内的 `.pccompat`，重新导入会删除它，
  于是相同内容重复执行冷编译。
- 新增稳定的内容寻址缓存 `mods/compiled/<modId>/resource-compile/<fingerprint>`。fingerprint 覆盖顶层 DLL、
  全部 UnityFS、`pccompat_resource_aliases.json`、recipe/IR/compiler 格式、目标 Unity 版本和 AssetsTools 版本；
  命中时仍完整校验 recipe、IR、payload 和当前候选 Bundle SHA，再原子恢复到本次导入目录。DLL/Bundle/alias 或
  编译器变化都会失效；损坏项重建，最多保留三个内容版本，十分钟以上的遗留临时目录会清理。
- 真实 JPKV fixture 删除 `.pccompat` 后的稳定恢复为 `391 ms`，相对本机冷编译约快 `40` 倍。首次见到全新
  Bundle 内容仍必须执行冷编译；本修复消除的是热重载、重新导入和同内容更新中的重复 47 秒解析，不以跳过资源
  验证或退回不可靠的原生 AssetBundle provider 换取速度。
- 旧版一行 compiler marker 不做自动升级。旧 compiled bundle 没有记录资源流分析读取的全部顶层 DLL 与 alias
  输入身份，`ui_recipe.bin` 也只携带入口程序集 SHA，无法证明旧 recipe 与当前完整输入一致；因此升级到本版本后会
  有一次冷编译，随后才进入内容寻址缓存。不能用文件时间戳或入口 DLL SHA 猜测迁移，否则可能永久复用错误资源绑定。
- 路径、资源缓存、Android 输入合同、生产桥重写及真实 JRP/JPOV/JPKV 审计组合 `131/131` 通过；真实 JPKV
  性能集成测试 `1/1` 通过。Android managed Slim Release 定向重建 `0` 错误；候选
  `StArray.ModManager.Android.dll` 为 `629,760` bytes / `942B45B5888746DE261A94B9F5165289ECBF42E23F33857A63E8664C040590D9`，
  `StArray.ModManager.dll` 为 `19,744,768` bytes / `F344B4C155083606745C0969CCC0D2B93977CD41D95783B30F625A05DFE8F803`，
  `xphorror.PcModCompat.Resources.dll` 为 `190,976` bytes /
  `9373CE32E23189EADEA5865A88BB23B850016DD0589D926D78D5695BB5151C07`。未构建 APK/native SO，未操作实机；
  设备实际缓存命中耗时仍由用户验收。

### 2026-08-26 场景事件 UnityMain 自等待修复

- 实机日志中的四次场景切换分别出现 `5775/6185/5798/5924 ms` 的 Unity Looper 长消息，但 CPU 实际运行只有
  `431-573 ms`，I/O 只有数毫秒。阻塞发生在 `StartLoadingScene`/`QuitToMainMenu` 后的场景事件阶段，不是资源加载、
  native `OverlayHide` reducer 或 MOD 卸载。
- 根因是 IL2CPP `sceneLoaded/sceneUnloaded` 从物理 UnityMain 进入新事件桥时，只恢复了 MOD owner、generation 和
  callback lease，没有恢复 `PcCompatUnityMainExecutionContext`。JPOV/JRP 的场景回调执行 `Overlay.Hide()` 后，动态 getter
  把当前线程误判为 worker，将读取重新排回同一个 UnityMain 并同步等待，命中固定 `5000 ms` 上限。
- 外部事件作用域现在先通过宿主已注册的 UnityMain 线程探针确认当前线程；仅探针为真时建立可跨回调持有的 UnityMain
  作用域，再进入 owner 域。作用域按 owner、UnityMain、lease 的逆序安全释放；worker 事件仍不获得 UnityMain 权限，继续走
  有界调度路径。该实现不包含 JPOV/JRP 名称特判，也不修改 MOD 源码或 DLL。
- 新增正向与反向合同：UnityMain 场景事件内的动态 getter 必须直接执行且 scheduler 调用为 0；线程探针为 false 时不得冒充
  UnityMain。事件桥、动态 getter 与 UnityMain 上下文定向回归 `40/40`；Android managed Slim Release 定向构建 `0` 错误。
- 当前候选 `StArray.ModManager.Android.dll` 为 `620,544` bytes /
  `03E73BFE23925B8D1E3BCCB577B99DDB671D7EB3169B1D7CE7CCE0E2EDD19208`，`StArray.ModManager.dll` 为
  `19,744,256` bytes / `DEEBAE54236A2CDE9C79F58F699AE4BC6F6794F2D3C55510C9C16C17BDE08E29`，
  `Il2CppInterop.Runtime.dll` 为 `302,080` bytes /
  `5E6970F6B7BFD01BDA0FA53071673E86921DCB84D4FDF139CF9BB8689B3DE109`。未构建 APK/native SO，未操作实机。

### 2026-08-26 JPOV/JRP generated delegate 事件回归修复

- `last_managed_failure_JPOV.txt` 与 `last_managed_failure_JRP.txt` 均在 `Enable` 阶段失败：重写后的
  `SceneManager.sceneUnloaded` handler 已是 Il2CppInterop generated `UnityAction<Scene>` 代理，不继承 CoreCLR
  `System.Delegate`；事件桥却直接执行 `(Delegate)handler`，因此两个 MOD 在自绘初始化前同时进入 `Faulted`。
- `DelegateSupport.TryResolveManagedDelegate` 现在从 generated IL2CPP delegate 的 rooted target 恢复原始 CoreCLR
  delegate。事件桥以该原始 delegate 建立 owner/generation callback scope，再转换为访问器要求的精确 IL2CPP delegate；
  `+=`、`-=` 和 session retirement 共享同一记录，取消订阅继续按原始 delegate 相等语义匹配，不依赖代理 wrapper 引用相等。
  无 rooted managed delegate 的原生 IL2CPP handler 失败关闭，不伪造可调用对象。
- 同类回查确认真实 JPOV、发布版 JPKV 和当前开发版 JPKV 都使用 `SceneManager.sceneLoaded`，而旧桥只接管
  `Application.quitting` 与 `sceneUnloaded`。`sceneLoaded` 的 add/remove 已纳入相同通用桥；真实 JRP/JPOV/JPKV
  重写产物中 `Application/SceneManager` 直接静态事件访问器现为 0。
- 托管缓存升级为 `xphorror.pcmod-managed-cache.v74-proxy-event-delegates`，事件桥 ABI 为
  `PcCompatManagedEventSubscriptionBridge.v4-proxy-source-delegate`，旧 v73 重写缓存不会复用。失败测试先复现了与设备
  相同的 `InvalidCastException`，修复后事件桥、真实 MOD 重写、生命周期和静态合同回归 `91/91`；Android managed
  Release 定向构建 `0` 错误。
- 候选 `StArray.ModManager.Android.dll` 为 `620,544` bytes /
  `FB9931F792975C31DA7A3BB2E82C365A607C41B63D68EBEFE7DA40FB16F51060`，`StArray.ModManager.dll` 为
  `19,743,744` bytes / `79A2F86330C85B864D3122FE5202E465F70FC07B462D0B3F1D298D0B21ADBAB9`，
  `Il2CppInterop.Runtime.dll` 为 `302,080` bytes /
  `5E6970F6B7BFD01BDA0FA53071673E86921DCB84D4FDF139CF9BB8689B3DE109`。未构建 APK/native SO，未操作实机。

### 2026-08-26 JPKV 开发版渲染组件闭包与高频分派优化

- 渲染组件兼容已从 `RainGraphic` 类型名单改为通用能力发现：类型必须属于当前 MOD、闭合且非抽象、直接派生
  `UnityEngine.UI.MaskableGraphic`，并在自身精确声明一个 `void OnPopulateMesh(VertexHelper)`。静态扫描、recipe、
  managed rewrite 和运行时桥复用同一形状合同；`Selectable`、`ScrollRect`、`LayoutGroup` 等没有对应宿主回调的代理
  派生类型仍失败关闭。扫描格式为 `static-patch-scan-v4-render-components`，托管缓存为
  `xphorror.pcmod-managed-cache.v74-proxy-event-delegates`，组件桥 ABI 为
  `PcCompatManagedComponentBridge.v10-shape-render-component`。
- 当前开发版 JPKV 源码产物中的 `KeyShapeLayer`、`RainLayer`、`GhostRainLayer` 均被扫描发现，三处代理基类构造调用
  均按既有宿主绑定合同置换；没有按 MOD id 或类型名特判。其新增的 16 个 Unity 代理成员已进入人工审核 surface。
  闭包复算为 `195` 个精确类型、`15` 个程序集，`missingAndroid=0`、`unresolvedMetadata=0`；只更新
  `UnityEngine.CoreModule.dll`、`UnityEngine.UIModule.dll`、`UnityEngine.UI.dll` 后，完整代理审计为
  `15` 个程序集、`207` 个类型、`16` 个泛型初始化器、`0` issue。
- `sceneUnloaded` 等托管事件回调现在捕获 owner generation，分派前取得 callback lease 并恢复 MOD execution/domain
  scope；旧代回调在 session 退休后丢弃。该修复覆盖 JPOV 离开关卡后 HUD 生命周期，不把事件委托作为进程级回调执行。
- 高频组件查询改为读取冷路径发布的不可变快照，组件 entry 使用原子 registered 位，不再在每个组件的每帧路径重复进入
  全局字典/锁。Harmony managed event 的 callback lease 从每条回调一次收敛为每个 session 每帧一次；collector 在排序和
  分派完成后统一释放，空帧、异常、缺 reader 和 reset 路径也释放，跨 MOD 排序及每条回调的 owner scope 保持不变。
- 本机验证：真实 JRP/JPOV/JPKV/JAMod/Loader 生产重写审计 `21/21`，组件、渲染、事件和 lease 合同 `90/90`，
  扫描、recipe、Harmony 聚合和输入合同 `82/82`，Loader/会话合同 `71/71`。另修复测试夹具把目录名误当
  `manifest.Id` 拼接重写输出路径的陈旧问题。Android managed Release 定向构建 `0` 错误；候选
  `StArray.ModManager.Android.dll` 为 `619,520` bytes / `DBA0575706B79ACC334B984CE339D83103D796B996403B7BFADB6A2A9DFEC4C0`，
  `StArray.ModManager.dll` 为 `19,742,208` bytes / `88A8188847BF4480D7B02FAB173D3355810CCDDDD6A380DA1B70F2BC2F54D7DC`。
  未构建 APK/native SO，未修改 MOD 源码或原始 DLL，未操作实机；真实 IL2CPP 三层渲染和设备性能仍需用户验收。

### 2026-08-26 JPOV Enable 数据源与共享泛型回调修复

- 设备日志中 `CompatEnable` 已进入，但 GameRefs/VersionSafe 的全部动态 getter 工厂都报告 `scope is retired`。直接原因是 lifecycle 在执行 `CompatEnable` 期间为 `Enabling`，旧 `CanDispatchManagedContinuation` 却只允许 `Enabled`，导致 MOD 初始化数据源和 `ShadowManager.ShaderRef` 静态初始化先于状态发布被拒绝。
- 现在仅 session 自身预分配的 `_enableContext`、当前 ambient scope 与 lifecycle=`Enabling` 三者同时成立时开放该窗口；同 ID/generation 的新建或伪造 execution state 仍失败关闭。`CompatEnable` 返回后继续使用正常 `Enabled` 合同。
- `StateBehaviour.ChangeState(System.Enum)` 的物理 method pointer 由多个泛型闭包共享，日志中的 boxed `System.Boolean`/`System.Single` 不是枚举补丁参数，而是其它闭包调用。callback dispatcher 现在用无异常哨兵对“可解析但不是枚举”的 boxed 类型统一判为本次规则不适用：Postfix 不分派，Prefix 保持 original，不增加失败计数、不触发熔断，也不在预期过滤热路径分配异常；真正枚举仍按具体 proxy enum 类型分派。无法解析的潜在枚举类型仍作为代理面缺口报错，不会被静默吞掉。
- 缓存 ABI 为 `xphorror.pcmod-managed-cache.v70-proxy-logical-getters`，dynamic getter 合同为 v4，callback dispatch 合同为 v2。动态 getter 现可把 PC 字段/属性统一绑定到 Il2CppInterop 代理的 C# 属性或裸 `get_xxx()` 方法，并允许根对象为空时优先读取有效 snapshot；真实 JPOV 发布 DLL 的五类工厂生产重写审计已闭合。
- 候选 `StArray.ModManager.Android.dll`：`610,304` bytes / `0CDD1245FC8E6FDF824992A8CD798DE66FF75D1E20B803FB3FDFFA67D4A6777B`。候选 `StArray.ModManager.dll`：`19,724,288` bytes / `E065EF8E940D3A6CA57D78AE012FD63E3D3D52A561FA8E9B4F9C2307CA6B82DB`。本轮没有 native 源码改动，未生成 APK、未刷新最终 runtime manifest、未操作实机。

### 2026-08-26 通用动态 getter 数据源桥与 owner 隔离

- 新增 `PcCompatManagedDynamicGetterBridge`，在不修改 JPOV/JPKV/JRP 源码或原始 DLL 的前提下，接管 `PatchManager` 的五类动态 getter 工厂：静态字段、静态属性、实例 object getter、实例 typed getter、静态 member getter。工厂 ABI 保持不变，加载副本仍保留原 MOD 方法体。
- getter 绑定键包含 `modId`、`resourceSessionGeneration`、声明类型、成员名、getter 类型和返回类型；同一代复用委托，不同 MOD/代不共享。generated proxy 返回值通过已注册的 native pointer provider 做同代 canonicalization，session 退休时清理委托和对象缓存。
- 工厂创建要求合法 managed scope 与 UnityMain；每次 getter 调用校验当前 scope 与创建 owner 的 `modId + generation` 一致。已采样的 immutable snapshot 标量允许 worker 直接读取，真实对象图读取必须进入有界 UnityMain 调度，防止委托泄漏后跨 MOD 或跨线程直接解引用 IL2CPP 对象。`PcCompatManagedModSession.Disable()` 已接入 generation 退休清理。
- `PcCompatAndroidManagedAssemblyRewrite` 已删除 JPOV 专用 `GameRefs`/`VersionSafe` getter call bridge，改为按源程序集、类型、方法、静态性、泛型阶数和 ABI 精确接管五类通用工厂；当前缓存 ABI 已提升为 `v70-proxy-logical-getters`，并登记 dynamic getter v4、snapshot scalar v1、callback gate v2 与 callback dispatch v2。
- Android host 已注册 generated proxy 类型探针和 `Il2CppObjectBase` 指针探针。新增行为合同覆盖工厂语义、null/default、静态性/缺失成员拒绝、同代缓存、跨 MOD 隔离、proxy canonicalization、UnityMain、scope mismatch 和 session retirement。本轮 dynamic getter、JPOV snapshot 与真实 UMM 生产重写审计组合为 `36/36`；`StArray.ModManager.Android` Release 定向构建通过，`0` error。
- 当前已完成 Android managed Release 定向构建和既有 arm64 native target 增量构建；未生成 APK、未刷新 `out/android_single` runtime，也未操作实机。设备侧仍需验证真实 IL2CPP generated proxy 指针、JPOV 实际数据读取和热卸载重载；不能据此宣称 JPOV 设备验收完成。

### 2026-08-26 Snapshot、callback lease 与 UnityMain 调度闭合

- native overlay snapshot 升级为 V6，尾部加入 `session_epoch`。V2-V5 读取 ABI 保留；`generation` 继续表示 telemetry publication，`session_epoch` 专门表示 gameplay/session 边界，避免普通刷新导致 proxy 对象误退休或旧对象跨场景复用。
- `PcCompatGameSnapshot` 现在携带 `Generation`、`SessionEpoch`、`ResourceSessionGeneration` 和 `ValidFields`。`FromOverlay()` 按 accuracy/BPM/timeline 等 publication count 生成有效性；无有效字段不伪造零值。动态 getter 的 snapshot route 只接受同一 resource generation 且字段有效的标量，未命中时回到 UnityMain 对象图。
- 新增 `PcCompatManagedCallbackLeaseGate`。每个托管 session 独立维护 in-flight callback 数、retirement token 和同线程递归退休保护；Update、OnGUI、settings、managed event、同步 Prefix 与动态 getter 都在 callback lease 内执行。`Disable()` 先停止新进入并等待排空，再退休 getter、proxy 和 snapshot 状态。
- worker 线程的动态 getter 分流为两条：不可变同代 snapshot 标量直接返回；对象图读取通过现有有界 UnityMain work scheduler 投递。排队工作开始时 callback lease 所有权显式转移到 UnityMain；未开始的拒绝、异常、超时或 retirement 由调用方取消并释放，已经开始但超时的工作继续持有 lease，完成后在 UnityMain `finally` 释放。调用方不再无限等待，也不会在工作仍执行时提前退休 session。
- callback gate 合同为 v2。新增 snapshot 有效性、session epoch、worker snapshot/object graph 调度、scheduler 缺失/抛错/拒绝、pending retirement、started timeout、callback lease retirement 与自退休防死锁合同；v69 阶段曾完成 `117/117` 扩展回归，本轮 v70 对动态 getter、snapshot 与生产重写执行 `36/36` 定向回归。此前已配置的 arm64 Release native target 有历史增量构建结果，但本轮未构建 native SO；未生成 APK、未刷新最终 runtime manifest、未操作实机。
- 本轮 native 改动尚未与最终 SO 的页 commitment/runtime manifest 重新生成绑定；最终交付仍必须通过 `build_android_single.ps1` 原子重建 native SO、runtime 和审计产物，不能单独替换 DLL/SO。

### 2026-08-25 IMGUI 事务生命周期闭环与 Release 交付门禁

- `ReleaseForSessionTeardown()` 已不只在空闲状态清理设置面。若 MOD 在自己的 `OnGUI` 回调中同步触发 disable，controller 会在该回调返回后立即停止当前分派；不会再读取已关闭 surface 的可见性、继续调用 OnGUI 或把旧帧动作交给新 generation。
- `PcCompatManagedSettingsUnityBackend.ReleaseCanvasSurface()` 在活动 IMGUI frame 内改为登记延后 reset。`CloseFrame()` 完成 interaction bridge、responsive frame 和 legacy input frame 的清理后，才 reset fence；这避免了 active `CommitLayout` 中直接 reset 的非法状态，也避免 `InputPending` 从被卸载 MOD 泄漏到后续实例。
- 新增确定性回归覆盖：回调内 teardown、`Faulted` 后终态 teardown、`InputPending`/`CommitLayout`/`AwaitingRebuildLayout`/`Recovering` 的 reset、连续 Text/Slider latest-write-wins、折叠后消失控件的 pending 丢弃、同一 call-site 的 occurrence 区分。`PcCompatManagedSettingsControllerTests` 为 `51/51`；settings、Android 输入、托管 bridge rewrite 与 JRP/JPOV/JPKV 生产 UMM/JALib 重写审计组合为 `153/153`。
- 本轮受影响托管 Release 构建通过，未构建 native SO、未生成 APK。当前候选产物为：`StArray.ModManager.Android.dll` `607,744` bytes / `25B4CF6139591E240B60E1FB31C7F3FFF2DB0F7BEC98C7115594D850F3220746`；`StArray.ModManager.dll` `19,685,376` bytes / `500E87B11CDC65E2F3A5A49DE3E2E72E887FB89B6A1B5B4768741012DA01A3BE`；`ModAssemblyRewriter.dll` `280,576` bytes / `07833AFB319580DC0A6EF82BC5F41C8B656789C25BC33E437AED23A9BBC0B0B5`。
- `out/android_single/assets/runtime` 仍通过自身 runtime manifest 审计（`222` entries，root `986a356eb4eaf7627fad9b795a4927d44c90cac722b5606f79a1a7e1db845f6f`），但其中 Android host 和 ModManager DLL 与上述候选 SHA 不同。这说明它是对旧源码批次**自洽**的 runtime，不是本轮可交付 runtime；本轮没有覆盖它，也没有改动 Gradle assets。
- 这是有意的安全交付门禁：runtime manifest root 被 native SO 编译进启动验证。不得单独复制 `StArray.ModManager.Android.dll`、`StArray.ModManager.dll` 或重写器到 runtime；否则 native 层会拒绝启动配置，表现为 `protected startup config invalid` / `0x7008`。最终交付必须通过 `build_android_single.ps1` 原子执行 runtime 收集、manifest 生成、匹配 native SO 重建和双目录 runtime audit。该步骤会构建 native SO，未在本轮执行。

### 2026-08-25 PcCompat 响应式 IMGUI 布局实现与回归修复

- 新增 `RESPONSIVE_IMGUI_LAYOUT_DESIGN.md`，将 PcCompat 设置面从“按整个 panel 宽度和字符数量猜测文本换行”收敛为通用的行级布局协议。协议使用稳定 call-site ID、完整 Begin/End 与 GUILayoutOption 捕获、嵌套布局树、真实 GUIStyle 测量及语义组文本策略，不包含 JRP/JPOV/JPKV 名称白名单。
- 所有第三方 `GUILayout` Begin/End 调用均严格一一透传；响应式规划只冻结文字换行、SelectionGrid 列数和高度，绝不增加、删除或替换 layout group。此前把已识别横排替换为分段行/竖排的实现会在 Unity 的 Layout、Repaint、输入事件之间破坏组栈，JRP/JPOV 的 `GUILayout: Mismatched LayoutGroup.Ignore` 已据此定性并修复。缓存按 MOD、resource/runtime generation、call-site occurrence、实际逻辑宽度、当前字体缩放、触控高度、样式/字体身份和动态内容隔离。当前已纳入的 GUIStyle 指纹覆盖字号、fixedHeight、wordWrap、richText、margin/padding；其它尚未纳入代理的 Unity 样式/spacing 属性仍单独列为缺口。
- `48x48` 仍是待实现的原生触控命中合同，不由 bridge 伪造成所有第三方控件的视觉高度。当前交互控件保留紧凑视觉基线：按移动端字体/skin padding 推导 `28-36` 逻辑像素的可读最小高；文本型 Button/Toggle/Input 及普通 Label 会在其 `Height/MaxHeight` 低于字形基线时移除该上限，自动换行时移除全部已标记高度上限。宿主底部操作和文本输入仍使用 `48` 高，实际第三方命中投影仍是待实现项。
- 性能合同为 `128` 控件热路径新增开销 p95 不高于 `0.25 ms`、冷测量不高于 `1 ms`、稳态零布局层托管分配、无逐帧日志；每 MOD generation 最多缓存 `512` 行、每行 `64` 个直接子节点、嵌套深度 `16`。本机已锁定纯响应式 `Repaint` 状态机（含 SelectionGrid）和托管 option 数组 snapshot 的 `0 B / 128`；新增密集 128 控件 Repaint 微基准，并仅对桌面 host 使用宽松 p95 sanity 门槛，不能替代设备时延验收。SelectionGrid 现在只登记一个布局条目，不再按每行建立反射 Begin/End 调用；真实 IL2CPP 绘制、反射测量与设备时延仍未计入该证据。
- 已接入通用实现：`PcCompatManagedResponsiveImGuiLayout` 按 MOD + resource generation 保存有界布局状态；它采集测量并在 Layout 边界冻结文本/格子策略，但不改变 MOD 已声明的横/竖布局组。输入和 Repaint 只复用已冻结计划。
- 重写器现为横/竖容器、`Space/FlexibleSpace`、八项 option（`Width/MinWidth/MaxWidth/Height/MinHeight/MaxHeight/ExpandWidth/ExpandHeight`）、按钮、标签、开关、输入框、文本区、滑块及 `SelectionGrid` 注入稳定 call-site token。`SelectionGrid` 在父横排中作为不可拆选择组参与计划；首个 Layout 保留 MOD 请求的列数，下一 Layout 才根据单元最小宽度和可用宽度应用已确认的列数，输入与 Repaint 绝不临时变列。它以一个 `GUILayoutUtility.GetRect` 和多个 `GUI.Toggle` 矩形绘制，不嵌套 `GUILayout` 组。响应式文本决策为三态：原生透传保持 MOD 声明的 `GUIStyle.wordWrap`，已证明可容纳的横排强制单行，已证明溢出的文本允许自动换行。测量时临时关闭 `wordWrap` 取得 intrinsic 单行宽度，避免 breakable token 误导行拆分。当前托管缓存为 `xphorror.pcmod-managed-cache.v65-imgui-style-fingerprint`，IMGUI ABI 为 `PcCompatManagedImGuiBridge.v19-style-fingerprint`，并包含 `PcCompatManagedSettingsTransaction.v1`；此前 `v63/v17` 与 `v64/v18` 仅是历史 ABI，旧重写产物不会复用。测量缓存现在同时纳入样式/字体对象身份、字号、fixedHeight、wordWrap、richText、margin/padding 和动态文本内容，内容或样式变化只会在下一次 Layout 边界重新规划。
- `PcCompatManagedSettingsUnityBackend` 的 Toggle、Button 和 Label 已改为显式的 style 路径，不再依赖可能被第三方 callback 改写的默认 overload。一次性 `SettingsFont` 日志会输出 `layoutRevision=v19-style-fingerprint`；若仍看到逐帧 `PcCompatSettingsDiag sequence=...` 或没有该 revision，说明设备正在运行旧 runtime，而不是当前源码。
- 当前 `v19` 定向组合回归为 `153/153`，其中 controller 生命周期回归为 `51/51`；响应式布局专项为 `26/26`，包含样式指纹、动态文本/SelectionGrid 失效、64/65 节点边界和密集 128 控件 Repaint 微基准。随后 `StArray.ModManager.Android` 的 Release 定向构建通过（`0` error）。未运行顶层构建，未操作实机。
- `GUIStyle.CalcMinMaxWidth`、`CalcHeight` 和 `GUILayout.ExpandHeight` 已进入 dependency-closed proxy surface；原 `EstimateMobileTextWidth()` 和 `ShouldUseMobileFlexibleTextHeight()` 已删除。`MaxWidth/MinHeight/MaxHeight` 通过 Android native materializer 创建真实 IL2CPP `GUILayoutOption`，其它 option 使用当前 MOD ALC 内的 proxy，避免跨 ALC 类型不匹配。
- 本机验证：本轮代理生成闭包为 `194` 精确类型，生成代理审计为 `0` issue；生成后的 `UnityEngine.IMGUIModule.dll` 已静态确认含有 `GUILayout.ExpandHeight(bool)`。Android 托管 Debug 构建通过，生成代理/UMM 重写审计（含真实 JRP）/响应式布局/设置/Android 输入定向测试共 `119/119` 通过。未操作实机或顶层全量构建。
- 尚未闭合：native option materializer 的实机验证、命中矩形扩大到 `48x48` 的原生触摸投影、尚未纳入代理的其它 Unity `GUIStyle`/spacing 属性、端到端 IL2CPP 绘制时延微基准及设备验收。因此 JPOV/JPKV 菜单自适应仍为“可进入实机验证”，不是完整功能闭合。

### 2026-08-25 JPOV Nullable 字段 ABI 回查与旧报告定性

- `xphorror.PcModCompat/last_managed_failure.txt` 的 `generatedUtc` 为
  `2026-08-24T16:17:45.6136297Z`。该报告生成时仍命中了旧的字段重写产物：
  `Overlay.ApplyLevelNamePatch()` 在 `stfld scrController::txtLevelNameOriginalPosition`
  路径上把 CoreCLR 的 `System.Nullable<Vector2>` 直接送入 generated proxy 的
  `Il2CppSystem.Nullable<Vector2>` setter，CoreCLR 因栈类型不一致抛出
  `InvalidProgramException`。同一报告中的 `UnityEngine.Object` 类型解析异常也来自旧的
  `BuildAliveResolver` 按具体组件程序集解析基类的路径。
- 当时重写器已统一字段 getter/setter 的 ABI 转换：写入使用
  `PcCompatAbiBridge.ToIl2CppNullable<T>`，读取使用 `ToManagedNullable<T>`；无值 Nullable
  使用 generated proxy 的无参构造，不传空引用给 setter 的 unbox 路径。重写器格式为
  `xphorror.pcmod-proxy-rewrite.v21-field-abi-converters`，当时托管缓存为
  `xphorror.pcmod-managed-cache.v54-field-abi-converters`，桥 ABI 为
  `PcCompatAbiBridge.v3-field-nullable`；当前版本已由下方 v55 切片升级。
- `BuildAliveResolver` 现在沿生成 proxy 继承链查找 `UnityEngine.Object`，再回退到
  `UnityEngine.CoreModule` 和唯一匹配 proxy；不会再从 `Unity.TextMeshPro` 等具体组件程序集
  伪造 `UnityEngine.Object` 类型。
- 验证：生产 JPOV/JPKV/JRP/JAMod/Loader 重写审计 `19/19`；完整离线托管回归
  `1212` 通过、`2` 项按环境跳过、`0` 失败；JPOV 的 `ResetLevelName` 与
  `ApplyLevelNamePatch` 两处 setter 均确认紧邻 `ToIl2CppNullable<Vector2>`。未执行实机、ADB、
  安装或顶层全量构建，因此该旧报告在用户提供新 runtime 实机日志前仍作为历史证据保留。

### 2026-08-25 JPOV StringBuilder 运行时代理契约修复

- 最新 `last_settings_failure.txt` 的直接异常是
  `MissingMethodException: Il2CppSystem.Text.StringBuilder..ctor(System.String)`，调用链为
  `PcCompatAbiBridge.ToIl2CppStringBuilder -> JipperOverlayer.Overlay.UpdateAttempts`。根因不是
  JPOV 文本逻辑，而是**编译期引用与设备 runtime 代理不是同一份程序集**：
  `Il2CppInterop.Runtime/Libs/Il2Cppmscorlib.dll` 是完整编译引用，暴露了 `.ctor(String)`；实际随
  Android runtime 发布的 `Il2Cppmscorlib.dll` 与 `pc_compat_proxies/Il2Cppmscorlib.dll` 是
  metadata-only skeleton，只保留 `.ctor(IntPtr)`。此前桥误把编译引用的构造函数当成设备契约。
- `PcCompatAbiBridge.ToIl2CppStringBuilder` 现在不再调用任何生成代理的字符串构造函数。它只依赖
  native IL2CPP metadata：缓存 `System.Text.StringBuilder` 的 native class 与 `.ctor(String)`
  方法地址，使用 `il2cpp_object_new` 分配对象，再以 `il2cpp_runtime_invoke` 调用构造；引用类型
  参数槽直接存放 `ManagedStringToIl2Cpp` 返回的 `Il2CppObject*`，与现有 generated wrapper 的
  `EmitObjectToPointer` ABI 一致。若目标构建裁掉
  `.ctor(String)`，则回退为 native 无参构造加 `Append(String)`，两条路径都只使用 `.ctor(IntPtr)`
  包装已分配对象。
- 托管重写缓存 ABI 从 `xphorror.pcmod-managed-cache.v54-field-abi-converters` 升至
  `xphorror.pcmod-managed-cache.v55-stringbuilder-native-materialization`，桥标识升级为
  `PcCompatAbiBridge.v4-stringbuilder-native-materialization`，旧重写产物不会复用。
- 同类回查覆盖 Android 宿主全部 `Il2Cppmscorlib` 构造调用：剩余 `Nullable<T>`、`List<T>`
  构造均有生成代理对应成员和既有 native 参数布局，没有发现第二个遗漏的完整引用依赖。
- 离线验证：`StArray.ModManager.Android` Release 定向构建 `0` 错误；生成 corlib、Android 合同、
  生产 MOD 重写审计和桥回归筛选共 `129/129` 通过。未执行实机、ADB、安装或顶层全量构建，故
  `TMP_Text.SetText(StringBuilder)` 的 UnityMain 实际渲染仍需用户用包含完整 v55 runtime 的产物验收。

### 2026-08-24 VirtualBundle 不透明句柄空值运算修复

- `JipperOverlayer`、`JipperKeyViewer` 的 `AssetBundle` 字段在 Android 重写后会擦除为
  `System.Object`，但旧重写只处理字段类型，未处理残留的 `UnityEngine.Object.op_Equality`/
  `op_Inequality`。设备侧因此会把 VirtualBundle 句柄送入 Unity 对象比较器，触发
  `ObjectCollectedException`。
- 重写器现在识别擦除字段、参数和局部变量附近的 Unity 空值/隐式布尔运算，改写到
  `PcCompatOpaqueHandleBridge` 的引用相等/非空语义；该桥放在
  `StArray.ModManager.dll`（`PcCompatReversePatchBridge` 所在的 managed bridge 程序集），
  不放在 Android host 程序集。生产准备阶段会校验两者程序集归属，避免桥类型再次落入错误
  的 DLL。
- VirtualBundle 资源会话存在时，异常路径不再退回旧的原生 AssetBundle provider，避免
  重新暴露已失效的 IL2CPP wrapper。托管缓存 ABI 已升至
  `xphorror.pcmod-managed-cache.v53-opaque-handle-null-operators`，重写器格式为
  `xphorror.pcmod-proxy-rewrite.v20-opaque-handle-null-operators`。
- 验证：JipperResourcePack、JipperOverlayer、JipperKeyViewer 生产重写审计及程序集归属
  回归 `47/47`；全量托管测试 `1211` 通过、`2` 项按环境跳过。Release runtime 已由
  `build_android_single.ps1 -NoManagedBuild` 同步，`assets/runtime` 与 Gradle 资产均为
  `222` 项，根哈希 `b07cde79269a86dc15be4816cbe875301913d00c890e851826169a7afc7e931f`，
  并通过双目录哈希审计。未操作实机，仍需设备侧验证 JPOV/JPKV/JRP 的实际资源加载。

### 2026-08-24 共享代理精确契约与 Android 原生 GUI surface

### 2026-08-24 AssetBundle bridge 的缺失 Unload ABI 修复

- 修复前的 JRP 失败样本已闭合到 `MAPLESTORY_OTF_BOLD SDF` 的 TMP 字体物化链：首个必需资源本身及其纹理、材质依赖均已进入 VirtualBundle，但旧版 `PcCompatGeneratedUnityBundleApi` 构造时仍把 `AssetBundle.Unload(bool)` 当成必需 generated-proxy 成员，触发 `MissingMethodException`，使字体准备在进入 `CreateTmpFont` 前失败。
- Android 3.1.2 dump 的 `AssetBundle` 实际包含托管 `Unload(bool)`，但该成员属于 bridge-owned 生命周期操作，按契约被共享代理有意排除，不能因为 dump 中存在就重新放回共享代理。bundle API 现在按 ABI 可选解析：存在旧代理成员时保留调用，不存在时跳过 native bundle unload，由 capability bundle 进程级保留和 VirtualBundle owner lease/物化对象销毁承担生命周期。
- 新增托管契约测试，约束 `_unload` 为 nullable、构造路径使用 `TryGetMethod`，防止“代理审计已排除但宿主 facade 仍硬解析”的同类缺口再次出现。本机定向 Android 托管构建和筛选测试均已通过；设备侧尚未产生修复后新报告，因此仍需实机验证。XPerfect 的 `ModDataDomainRuntime.RequireCurrent()` UnityMain 回调崩溃是独立问题，不属于本条资源 ABI 修复。

- 已系统修复共享代理、托管重写和最终审计清单不同步的问题。`ManagedBridgeOwnedSurface` 现在是单一精确签名真值，`ProxyInputClosure`、`ProxySurfaceScanner` 与 `ProxyAssemblyAudit` 共同引用；审计按程序集、类型、静态性、泛型元数、返回值和参数检查最终 MethodDef，不再按方法名整体禁止，因此 `BeginVertical`、`GetRect` 的 Android 可用重载仍可正常进入代理。
- Android 3.1.2 缺失的 `GUI.DrawTexture(Rect,Texture)`、`GUILayout.SelectionGrid`、`HorizontalSlider`、`TextField`、`BeginVertical(GUIStyle,options)` 与 `GUILayoutUtility.GetRect(float,float)` 均由 `PcCompatManagedImGuiBridge` 重建。`DrawTexture` 以泛型参数保留 `Rect` 值类型，`GetRect` 返回后由重写器显式 `unbox.any Rect`；`SelectionGrid` 只在外层应用一次布局 options，再通过 `Rect` 访问器切分并绘制各单元，不建立或关闭内部布局组。
- `JsonUtility.ToJson/FromJsonOverwrite/FromJson<T>` 与 AssetBundle、GUI focus/style 等既有桥接成员也进入同一契约。最终代理不再保留任何 bridge-owned 包装器，避免某个 MOD 引入 Android 缺失方法后污染共享类型静态构造器并使其它 MOD 一并 faulted。
- 托管缓存 ABI 升至 `xphorror.pcmod-managed-cache.v49-native-gui-surface`，IMGUI bridge ABI 升至 `PcCompatManagedImGuiBridge.v7-native-gui-surface`；缓存键补入 `BoxLastValueTypeArgument` 与 `AllowValueTypeReturnUnbox`，并修复 bridge method 与 instance-forwarding 字段之间缺少分隔符的问题。
- 验证：旧代理可确定性复现 `6` 条精确签名泄漏；重生成后闭包为 `194` 个精确类型/`15` 个程序集，`missingAndroid=0`、`unresolvedMetadata=0`，最终代理 `206` 个类型/`16` 个 generic initializer、审计 `0 issue`。相关测试 `111/111`，完整托管回归 `1206` 通过、`2` 项按环境跳过。`UnityEngine.IMGUIModule.dll` SHA-256 为 `DABE4E1A09E0F4CF9CAB9692AE73CA2EA5A754F6BA9295394DF716C1A54AD3EF`，`UnityEngine.JSONSerializeModule.dll` 为 `ABF5BCD0CDBF410746135F44E018FF4052A8568A996ED352AD39C6B211913F80`。未运行顶层全量构建，未操作实机。

### 2026-08-24 PcCompat bootstrap generation 与 GUI 拖窗桥

- `JipperOverlayer` 没有 resource recipe 时，managed loader 不再把 session generation 置为 `0`；现在以已打开的 native `PcCompatModSessionLease.Request.ResourceGeneration` 为权威回退，并在 recipe 存在时要求两者一致，防止错误 generation 串入其它 MOD。
- 文件根和网络身份在 bootstrap 前绑定，`config/cache/log/temp/data` 目录也在 MOD 代码运行前准备好，因此 bootstrap 读取或保存 `Settings.json` 不会再落入“roots 未绑定”或父目录不存在的失败路径。绑定按 `(modId, generation)` 幂等复用，避免 session 构造时替换 bootstrap 已创建的网络 state；失败加载和 `Dispose` 会撤销两类绑定。
- Android 3.1.2 缺少 `UnityEngine.GUI.DragWindow(Rect)` 托管 wrapper，但 `DragWindow_Injected` icall 仍存在。该调用现由 managed rewrite 以 `box Rect -> PcCompatManagedImGuiBridge.DragWindow(object)` 改写，Android bridge 读取生成代理的 Rect 四个坐标并调用真实 icall；surface scanner 将其标记为 bridge-owned，避免共享 GUI 代理静态构造器解析缺失方法。
- 托管缓存 ABI 升至 `xphorror.pcmod-managed-cache.v48-native-gui-drag-window`，IMGUI bridge ABI 升至 `PcCompatManagedImGuiBridge.v6-native-focus-drag-window`。完整托管回归 `1202/1202` 通过、`2` 项按环境跳过；单项目 Release 构建通过，代理闭包 `194` 个精确类型/`15` 个程序集，`missingAndroid=0`、`unresolvedMetadata=0`、proxy audit `issues=0`。最新 runtime manifest 为 `222` 项，根哈希 `8707d9988019c274b5ce3fbfb99ca6d3466c6cd40bc815bebf4bbc951aa1795f`；`UnityEngine.IMGUIModule.dll` 代理 SHA-256 为 `881910a25e61aa316c1a4dad7dcb494ded8d1dc54bde541d006d47ae69230b85`，与 runtime 复制品一致。未操作实机，仍需用户使用新 runtime 验证 JRP 设置拖动和 JPOV bootstrap。

### 2026-08-24 Unity 6 GUI 焦点桥与共享代理隔离

- Android 3.1.2 的 IL2CPP 元数据裁掉了 `GUI.SetNextControlName(string)` 与 `GUI.GetNameOfFocusedControl()` 托管包装器，但 `libunity.so` 仍提供对应 `*_Injected` icall。JPKV 引用这两个 API 后，旧 surface 将它们合入共享 `UnityEngine.IMGUIModule` 代理，导致 `UnityEngine.GUI` 静态构造器在任意 MOD 首次触发时解析缺失方法并整体失败；因此日志可能显示 JRP faulted，实际污染来源是共享代理中的 JPKV API。
- 两个调用点现由 managed rewrite 精确改写到 `PcCompatManagedImGuiBridge`，Android 宿主按 Unity 6 `ManagedSpanWrapper` ABI 调用真实 icall。返回字符串使用 `UnityEngine.Bindings.BindingsAllocator::Free` 按官方 `OutStringMarshaller` 语义释放，不维护伪造焦点状态，也不改变 IMGUI 控件 ID 行为。
- ProxySurfaceScanner 将这两个签名标记为 bridge-owned，手工 surface 与自动扫描都不能再把它们放回共享代理。最终代理审计会检查 `GUI` 静态构造器不再包含这两个解析名称。
- 托管缓存 ABI 升至 `xphorror.pcmod-managed-cache.v47-native-gui-focus`，IMGUI bridge ABI 升至 `PcCompatManagedImGuiBridge.v5-native-focus`，旧 MOD 重写缓存自动失效。当前仅完成源码与定向测试验证；单项目 Release 构建结果见本节后续更新，未操作实机。

### 2026-08-24 JRP 后台持久化作用域修复

- JipperResourcePack 的键数保存不是从常规 managed Update 直接完成：`KeyViewer.Work()` 运行在 MOD 自建的 `Thread(ThreadStart)` 上，再由 `KeyCountData.Save()` 调用 `Task.Run(Action)` 启动 `async void SaveData()`；原有 `[ThreadStatic]` 执行状态在这两次调度和 `await Task.Delay` 后均不会自然保留，导致文件桥以“无 active managed scope”失败关闭。
- PcCompat 生产重写现在精确桥接 `Thread(ThreadStart)` 与 `Task.Run(Action)`。线程在创建时捕获 `(modId, resource generation, phase)`，在线程入口恢复；后台任务再次捕获并使用仅限后台路径的 `AsyncLocal` 流动作用域，因此 `async void` 在 `await` 前后仍解析到同一 MOD 文件域。普通 Update/OnGUI 继续使用无分配的 `[ThreadStatic]` 热路径。
- 主程序集中的 1 个监听线程与 1 个键数保存任务、`JAMod.Bootstrap.dll` 中的 1 个安装任务均有真实 IL 重写回归；无作用域调度仍失败关闭，任务结束后作用域不会泄漏到无关任务。该修复落地时托管缓存 ABI 为 `xphorror.pcmod-managed-cache.v46-background-scope`，当前已由 GUI 焦点桥升级为 `xphorror.pcmod-managed-cache.v47-native-gui-focus`；线程桥 ABI 仍为 `PcCompatManagedThreadBridge.v2-background-scope`。
- 验证：路径/生命周期/重写/Android 合同集合 `110/110`，完整 `PcCompatManaged*` 与 UnityMain 执行上下文集合 `262/262`；`build_android_single.ps1 -Configuration Release -IncrementalManagedBuild` 通过，代理闭包 `194` 个精确类型/`15` 个程序集，`missingAndroid=0`、`unresolvedMetadata=0`、代理审计 `issues=0`。稳定后的 runtime manifest 为 `222` 项，根哈希 `f48cc716e3c1a5f33fb04183a34e951a9a79f50ef1f6913e2ae4d27c2498d81f`。未运行顶层全量构建，未进行实机操作。

### 2026-08-24 事件 delegate ABI 回查

- 修复 PcCompat 外部静态事件桥的真实阻断：`System.Action`/`UnityAction<T>` 不能直接反射传给生成代理的 `Il2CppSystem` delegate accessor。Android 宿主现在通过 `PcCompatAbiBridge` 注入 `DelegateSupport.ConvertDelegate<T>` converter；事件桥按源 delegate 与目标 delegate 类型缓存转换结果，并在 add/remove/异常退休路径复用同一 IL2CPP wrapper。
- `add_quitting`、`remove_quitting`、`add_sceneUnloaded`、`remove_sceneUnloaded` 全部改写到事件桥；正常 `Unsubscribe` 成功后从当前 generation 登记移除，`Disable` 阶段允许退订，避免 `OnDisable` 的 ABI 失败和退休阶段重复 remove。非 Android 或普通托管事件在 handler 已可赋值时不走 converter。
- 事件桥 ABI 保持 `PcCompatManagedEventSubscriptionBridge.v2-delegate-conversion`；其首次落地使用的缓存 ABI 为 v45，当前统一托管缓存已由后续修复递增为 `xphorror.pcmod-managed-cache.v47-native-gui-focus`。定向事件桥、重写器、Android 合同测试通过；完整顶层构建和实机验证未执行。
- 同类回查覆盖 `DelegateSupport.ConvertDelegate`、managed-call argument converter、optional settings delegate、callback dispatch、所有 PcCompat 事件反射调用和 `add_`/`remove_` 访问器。当前未发现第二个“把 CoreCLR delegate 直接送入 IL2CPP accessor”的生产入口；未覆盖的实例事件、动态反射事件、裸 `Delegate.Combine` 仍保持 fail-closed/诊断降级边界。

审计快照：

```text
日期: 2026-08-19
实施批次起始基线: 17c534c (master)
资源消费提交基线: eaf602e (master)
当前已提交基线: f03d059 (master)
当前工作批次: 2026-08-19 MOD runtime isolation foundation + PC 3.1.2 semantic pack
配套架构文档提交: bcd4b37 (master)
Il2CppInterop 上游基线: 81a6f78 + Android slim/runtime-metadata-only 本地迁移
dnlib 上游基线: 9ab9b58a
范围: UI graph、ui/resource recipe、Resource IR v1、VirtualBundle、managed self-render lifecycle、Android capability/object materialization、静态 TMP 字体重建、managed-event dispatch
目标游戏: ADOFAI 3.1.2 r143
目标 Unity: 6000.3.10f1
目标 ABI: arm64-v8a
```

### 2026-08-19 隔离基础设施批次

本批次使用 `C:\Program Files (x86)\Steam\steamapps\content\app_977950\depot_977953` 中的
PC 3.1.2 程序集作为 CIL/MVID/文件哈希权威输入，使用
`E:\TEMP_SHARE\adofai_decomp_312` 作为已存在的 UTF-8 语义回查源码树；不会修改或重复反编译这两个来源目录。

已实现并通过本机定向测试：

- `ModDataDomainToken` 固定布局、进程 cookie、slot generation、嵌套 scope 和 callback lease 绑定。
- `ModRuntimeSession` 在 BeginLoad/legacy adoption、retire、abort 时创建和关闭 domain；前置加载失败不再遗留 Loading generation/domain slot。
- `IsolationManifest` 规范化、UTF-8 严格读写、原子发布、确定性 JSON/hash、程序集/MVID/文件大小/SHA-256身份。
- MOD 目录 `isolation.json` 读取接线；无清单时生成保守 `Guarded` bootstrap 清单。清单身份与入口程序集不一致时 fail-closed。
- 真正加载或复用 Native Managed MOD 实例前再次复核入口身份，防止 manifest 绑定后热更新/替换文件进入 `OnLoad`。
- Android Managed MOD生产加载已切换到 `mods/.starray-shadow/starray-native-shadow-v3/<cacheKey>`。入口与静态私有依赖闭包使用 `PEReader`在不执行代码的情况下确定，缓存键覆盖格式版本、rewrite ABI、文件名、程序集名/版本、MVID、大小和 SHA-256；临时目录在逐文件复核后原子发布。完整 marker最后写入并同时绑定 cache key 与 `shadow-package.json` SHA-256，命中缓存仍复核manifest、静态槽证明和全部shadow程序集，任一篡改都会拒绝并重建。
- 目录扫描使用 metadata-only `ModEntryPointAttribute`/唯一 `IModPlugin`发现和受限常量getter解释，不执行插件构造器或静态构造器；无法证明的旧MOD才进入明确的 `LegacyReadOnly` fallback。插件实例化延迟到 `BeginLoad`建立domain、shadow与manifest完成绑定之后，并在owner/domain scope内执行。
- Android shadow provider已把 `Assembly.Location`改写到domain路径桥。XPerfect、Replay和ShowBPM继续看到原MOD安装目录而不是内容地址缓存目录；shadow程序集离开有效domain后调用该桥会失败关闭。rewrite ABI包含规则版本与Host桥程序集MVID，规则或桥ABI变化自动使旧缓存失效。
- 非强签名Android MOD程序集的直接 `ldsfld/stsfld/ldsflda` 已改写为 `ModDataDomainRuntime`稳定cell。槽ID由原始MVID和完整字段身份确定；原静态构造逻辑移入每domain一次性初始化器，失败在该domain内永久重抛。普通读写热路径使用 `ConcurrentDictionary`稳定cell，不争用domain全局锁；仅首次cell工厂和 `.cctor`状态转换加锁。静态槽证明进入shadow v3 manifest并合并进当前generation的 `IsolationManifest.StaticMembers`。
- 重写器对 `ThreadStatic`、mutable RVA、泛型静态字段、volatile/unaligned前缀、静态字段句柄逃逸和跨私有程序集直接静态字段访问失败关闭。编译器生成的只读 `<PrivateImplementationDetails>` RVA blob保持 `SharedImmutable`；无需改写的强签名私有依赖保持原字节并依靠每MOD collectible ALC隔离，若其使用 `Assembly.Location`则仍拒绝。
- 真实Android MOD语料审计通过：XPerfect改写378条静态访问/83槽/2个Location调用，Replay为645/222/1，ShowBPM为199/70/1；Replay的强签名 `System.Formats.Nrbf.dll` 无Location改写需求，按原字节shadow通过。目录扫描、重载和程序化 `AddMod`/旧 `NativeModLoadState` 均绑定同一shadow路径；原始DLL只用于身份与重新生成，扫描后源文件变化要求重新扫描。
- CIL Semantic Pack 工具：确定性方法流、异常区/operand/locals/程序集身份/API surface hash 和 UTF-8源码树 hash。

验证结果：shadow/static/domain定向矩阵 `47/47`通过，另有陈旧AsyncLocal scope退休回归通过；managed全量 `1004/1005`通过，1项既有XPerfect环境测试跳过；Android managed构建0错误。Semantic Pack 已对 PC 3.1.2 的2个程序集、24,420个方法体和628,647条指令生成可复现产物。当前只声明metadata-only发现、content-addressed shadow、`Assembly.Location`和上述直接静态字段子集已完成，不宣称完整runtime isolation rewrite已完成。

仍未完成：反射字符串查找/`FieldInfo.GetValue/SetValue`、动态IL、Task/Timer/Thread、文件/网络、事件、P/Invoke provenance和动态加载尚未进入同等重写；generic/ThreadStatic/跨程序集静态字段当前是拒绝而非等价支持。Direct Link closure、Provider热更新依赖重载和跨MOD Harmony Method Island也未完成。实机验收仍由设备侧完成。

`983cf59` 已提交 managed/native UI graph、generated proxy、managed component/lifecycle、Resource IR/VirtualBundle、capability bundle 和 presentation runtime 主链；`ea54df7` 补齐 JAMod bootstrap 的 managed exception logging bridge。两者均通过本机回归。P0 第 2/3 步（Android 实机 graph 验收与 presentation 压力回归）仍未完成，因此通用 UI graph 继续记为“部分实现”。

2026-07-16 追加架构结论：ModManager 是 IL2CPP readiness、metadata resolver、generated proxy bootstrap、HookBroker 和未来受控类型注入的唯一进程级所有者。runtime 已配置 HookBroker detour provider，但 Android arm64 不调用上游 ClassInjector：其内部函数发现仍依赖 Iced/x86-x64 xref。真实 IL2CPP `Component` 注入尚未实现，不能因 provider 已接通而标为可用。

构建依赖已按本仓库的扁平化规则纳入顶层 Git：`Il2CppInterop` 包含本地迁移修改，`dnlib` 保持上述上游基线；二者均作为普通文件跟踪，不是 embedded repository/gitlink。其它上游参考 clone、Jipper 样本产物、本地工具链和运行诊断文件不属于可复现源码提交。

## 状态定义

| 状态 | 定义 |
| --- | --- |
| 已实现 | 生产路径已有代码，至少通过本机自动测试或既有实机验证 |
| 部分实现 | 主链已接通，但只覆盖受限子集、存在 fallback，或缺少完整实机验收 |
| 仅设计 | 文档已确定边界和方案，当前源码没有对应生产实现 |
| 未实现 | 尚未形成可执行链路 |
| 非目标 | 当前项目明确不承诺支持 |

## 当前端到端链路

当前已经存在的生产主路径：

```text
MOD 文件夹/导入 ZIP
  -> UMM/JAMod manifest 识别
  -> System.Reflection.Metadata 只读扫描
  -> direct attribute + 受限 dynamic AddPatch 恢复
  -> callback fixed-op / UI graph lowering
  -> recipe_report.json + hook_rules.json + ui_recipe.bin
  -> compiled cache 原子发布
  -> native bundle verifier
  -> IL2CPP runtime metadata 完整身份解析
  -> HookBroker 永久 slot / Dobby 首层入口
  -> fixed-op / Rule VM / realtime event core
  -> bounded presentation snapshot
  -> UnityMain PresentationSink
```

受支持的 managed self-render 资源支线：

```text
PC/Linux bundle + rewritten MOD DLL
  -> AssetsTools.NET Resource IR v1 + verified texture/compact TMP payload
  -> owner/session-aware VirtualBundle
  -> Android UnityMain Texture/Sprite/Material/静态 TMP/PrefabGraph 重建或 capability fallback
  -> generated Unity proxy 返回 MOD
  -> MOD 自己 Instantiate、赋值和绘制 HUD
```

默认生产路径不执行原 PC MOD 托管代码。managed loader、shim 和 rewritten oracle 是开发审计路径，必须显式启用。

## 已实现

### 导入、发现和生命周期

- 识别 UnityModManager `Info.json`、JAMod `JAModInfo.json` 和对应入口程序集/类型。
- PC MOD 使用独立 adapter，不按普通 Android `IModPlugin` 直接加载。
- Document API 导入后可重新扫描、展示状态，并由用户执行加载、取消、卸载和重试。
- APP 启动时后台扫描并加载配置中已启用 MOD；ModManager 管理窗口仍保持隐藏。
- 后台翻译具有取消和 generation 防旧任务覆盖机制，不依赖 ImGui/EGL 每帧推进。
- recipe cache 和 managed rewrite cache 使用临时目录、完成标记和原子发布。

### Metadata、PATCH 扫描和翻译

- `System.Reflection.Metadata` + `PEReader` 只读程序集 metadata 和 IL，不触发 static constructor。
- 支持 direct `JAPatchAttribute` 扫描、完整 callback 签名、版本门禁和重载区分。
- Harmony annotation 聚合按 upstream 语义在 metadata 上重建 target：类级与方法级属性合并、七种 patch kind（含 ReversePatch/Inner）判定、方法名约定 fallback（`Prefix`/`Postfix`/`Transpiler`/`Finalizer`）、`MethodType` 全枚举、`ArgumentType` 变体装饰、`__instance`、`priority`/`before`/`after`、category 和 `ReversePatch` 的 `lastOriginal` 传递。静态不可判定的一律 fail-closed 记 issue 而不猜目标，共 17 个 issue code；descriptor 以 `source=harmony_attribute` 进入 `static-patch-scan-v2` 报告。这套规则已用上游 `HarmonyTests` 的真实 patch assets 当语料跑过（66 descriptor / 11 issue / 6 code 被触发），逐条对齐 upstream 后未发现聚合缺陷。
- Harmony 运行时注册与 JALib 同走逻辑注册表：shim `HarmonyRegistry` 记录 `Patch/ReversePatch/PatchAll` 的逻辑注册与 ABI 诊断，host 侧 `PcCompatShimPatchRegistries` 统一读两个注册表，descriptor 以 `source=shim_harmony_registry` 与 `managed_oracle` 区分来源。注册表在 MOD bootstrap **之前**清空，因此 MOD 在 `OnLoad` 期装的 patch 会保留；诊断不随注册表清空丢失，经 `harmonyShimStatus` 段落进诊断导出。仍不物理装 hook，状态保持 `registered_only`。
- 受限 dynamic `AddPatch` interpreter 支持 `MethodInfo` 局部变量、有限字符串数组、静态长度 foreach、`TryingCatch` 和版本分支。
- callback translator 可把已证明的 overlay、判定观测、状态观测和 ResourceChanger 安全子集翻译为通用 fixed-op。
- 支持少量经逐 opcode 审计的固定循环，并按项目范围投影为单玩家语义；不把它扩展为一般循环支持。
- translator、recipe 和 native slot 都禁止按 MOD ID 选择生产逻辑。
- managed oracle 可以执行 shim 下的 PC setup 并和静态结果交叉审计，但默认关闭。
- managed rewriter 会对可由 CFG 证明的有限文件读取循环做零进度保护：严格的 `bool ReadExactly(Stream, byte[])` 形态整体改写为 `PcCompatManagedIoBridge.TryReadFileExactly`，短文件返回 `false`；不能安全整体替换的累加读取循环在 `Read()==0` 时抛出有界 `EndOfStreamException`。普通依赖 `Read()==0` 判断 EOF 的合法循环不改写。该规则源于 2026-07-24 实机现场：Jipper 的空 `KeyCount.dat` 令 UnityMain 对同一 fd/offset 无限 `pread64(...)=0`。
- managed component bridge 已接管 surrogate `UnityEngine.Behaviour.enabled` 的 getter/setter 与即时 `OnEnable/OnDisable`；真实 IL2CPP `Behaviour` 仍通过 generated proxy 透传。managed cache ABI 为 `PcCompatManagedComponentBridge.v5 + PcCompatManagedIoBridge.v2`，旧 cache 会强制失效重写。

### Recipe、VM 和调度

- `ui_recipe.bin` v1 已固定 10 段容器、CRC、边界、数量、元素大小和字符串校验。
- managed validator 已与 native parser 对齐 target/rule/parameter range、string reference、object graph、lifecycle budget 和 bytecode 边界；managed 判定有效但 native 拒绝的 cache 假阳性已补回归测试。
- target/rule、object graph、component operations、lifecycle、native bytecode 和 resources 段可非空并完成 managed/native 双端解析。
- resources 段使用固定 32-byte record，header flag `0x10` 表示存在资源绑定；managed/native 双侧校验字符串、节点、组件、目标类型、容量和重复身份。diagnostics 段仍为空。
- register-like Native Rule VM 已支持整数/浮点寄存器、比较、分支、受限循环、输入/时钟读取、budget 和异常 ring buffer。
- realtime event core、五套 touch lane projection、多时钟 anchor、deadline scheduler 和 HUD logic worker 已接通。
- lifecycle program 支持 `BundleLoad`、`OverlayStateChanged` 等 trigger；clear 会等待执行退出并回收 program registry。
- presentation 使用 64 槽有界历史、generation 顺序消费和覆盖 fail-closed。

### Native HookManager 和 IL2CPP 解析

- 游戏函数地址只通过运行时 IL2CPP metadata 按程序集、命名空间、类型、方法、static、generic arity、返回类型和有序参数类型解析。
- recipe、dump 和缓存中不保存生产 RVA/VA；`dump.cs` 只作为生成期或审计输入。
- 同一目标只建立一个永久 Hook Slot；多个 MOD/内置模块形成 continuation chain，不重复 Dobby hook 入口。
- 禁止运行时 unhook。关闭 MOD 只禁用 rule；重新启用复用原 slot 和 trampoline。
- 入口覆写前检查 AArch64 短 stub、直接分支和可能冲突的已有跳板。
- capability、stage、fixed-op 和 ABI gate 在安装前统一失败关闭。
- dispatcher 容量已改为运行时动态计算。HookBroker rebuild 按去重后的 `permanentlyBoundTargetKeys ∪ installableStagedTargetKeys` 计算 `required`，同一完整 metadata 目标签名只计一次，禁用但已永久绑定的目标仍计入；完整 staging 批次在任何物理安装前原子完成容量决策。
- Android arm64 使用按批次增长的 AArch64 thunk arena 和稳定分页 runtime slot。thunk 以 BTI 入口、GP 参数右移、FP 参数原位和隐藏 `MethodInfo*` 原样转发实现现有 14 种 ABI；代码页执行 RW→RX，已发布地址、slot id 和 original trampoline 保持到进程退出。诊断导出 `required/capacity/bound/ready/blocked/new/allocated/remaining`，不再存在 64/128 等固定兼容上限。
- 已支持审计过的 after-original 观测，以及 ResourceChanger 所需的 before-original 参数覆盖/跳过原函数子集。

### Telemetry、HUD 和输入观测

- native snapshot 已覆盖 overlay 生命周期、玩家数、判定、偏移、准确率、X-准确率、combo、attempt、progress、BPM/KPS、music/map time、checkpoint 和场景状态。
- 兼容层判定统计与官方即时判定文字是两条链：前者来自 `scrMarginTracker` snapshot；后者由 `scrPlanet -> scrHitTextManager.ShowHitText -> scrHitTextMesh.Show/Update` 维护对象池、世界坐标和淡出。PcCompat 当前不 hook 后三者，排查“完美！”位置/动画时不能用 `hitMarginsCount` 正常来代替即时文字链验证。
- HUD 有明确关卡会话门禁；非关卡点击不会进入判定/偏移显示。
- snapshot 使用固定布局 ABI v3，并兼容旧 v2 前缀。
- 标准移动端 HUD 使用 Unity `ScreenSpaceOverlay Canvas + TextMeshProUGUI`，不依赖 ImGui 前景 draw list。
- Unity HUD 只在 UnityMain 创建和修改对象，高频 Hook 路径不进入 CoreCLR。
- JALib `Task.Yield().OnCompleted(...)` shim 已恢复标准 awaiter 语义。每个 managed owner/phase 使用预建的 owner-bound `SynchronizationContext`，因此 CoreCLR 从 ThreadPool 调用 `Post()` 时仍携带 MOD/session 身份；continuation 进入独立的 2048 槽 UnityMain 队列，每次 presentation opportunity 最多消费 16 条，不再与 64 槽资源队列争用。scheduler 缺失、抛异常、容量拒绝或 continuation 执行异常只会让对应 MOD fail-closed 并持久化首次根因，禁止异常逃出 `.NET TP Worker` 导致 CoreCLR `abort()`，也禁止把 TMP、Material 或 Transform continuation 投递到 ThreadPool。
- PresentationSink 的 `Canvas.SendPreWillRenderCanvases` 与 `CanvasUpdateRegistry.PerformUpdate` detour 均按 IL2CPP ARM64 ABI 接收并原样转发隐藏 `MethodInfo*`，managed 回调前后不再依赖未定义寄存器残值。Unity IMGUI 使用配对 permanent slot：`ProcessEvent(Int32, IntPtr, out Boolean)` 发布 thread-local 事件代次，Unity 原生事件泵随后调用的首次 `BeginGUI(Int32, Int32, Int32)` original 返回、`Event.current/GUIClip/GUILayout` 就绪后消费该代次并派发 managed OnGUI。两者是相邻阶段而非嵌套调用；两个入口均由 metadata 精确解析并经 HookBroker 安装，禁止硬编码 RVA 或 direct Dobby。OnGUI session snapshot 和 native enable 独立于 managed frame mode。
- PresentationSink 的主 `Canvas.SendPreWillRenderCanvases` 与备用 `CanvasUpdateRegistry.PerformUpdate` 安装互斥；active managed self-render 直接随当前已安装的 UnityMain presentation opportunity 推进，不读取 gameplay telemetry clock anchor。Pending activation 仍单独使用 250ms 限流。禁止用只在关卡内低频更新的 `Time.frameCount` 快照给全场景 KeyViewer/managed lifecycle 去重。
- PresentationSink 对 recipe presentation command 做每次 UnityMain opportunity 最多 16 条的预算化提交。未处理完的 snapshot 保存在 native pending buffer 中，直到完整处理后才 ack publication generation；`EnsureGraph` 内部物化若耗尽预算，当前 command 不计入 consumed、cursor 不前移，后续 command 不得越过。若期间出现 clear barrier 或 history gap，则丢弃 pending 并按 fail-closed 规则销毁旧 graph，禁止重放过期 HUD 命令。
- object graph 物化已拆成 `CreateNodes -> InitializeNodes -> ActivateCanvases`，每次 opportunity 最多推进 12 个粗粒度 Unity 操作。普通组件、父子关系和非 Canvas 初始化先完成，Canvas/CanvasScaler 最后按节点启用；半成品、hide、discard、retry failure 都由同一销毁路径释放 root/未挂载对象和全部 GCHandle。已物化 graph 的普通更新只验证目标对象，只有 `EnsureGraph/SetActive(true)` 执行全图存活检查。
- recipe resource resolver 每次 opportunity 最多处理 4 个 binding；余项通过 pending hint 续跑，resolver 返回 pending 的 binding 继续由现有 resource refresh 唤醒，不做逐帧重复 managed callback。retired graph 每次最多回收 4 个，并只直接销毁 root 与构建失败后尚未挂入层级的对象，避免对子节点重复调用 `Object.Destroy`。两类四项批次使用栈上 `FixedBatch`，不再为每次 UnityMain opportunity 创建并 `reserve` 临时 `std::vector`；managed resolver 和 Unity destroy 仍在 `g_graph_lock` 外执行。
- 触摸 KeyViewer 输入是 observe-only，不消费游戏事件；支持最多 32 个 held slot、DOWN/UP、总次数和一秒 KPS。
- KeyViewer Adapter 主线已进入正式 consumer 阶段：声明式 IR 支持每 MOD 多个 `KeyViewerFeature`、每 feature 多个 `LaneGroup`；扫描器从精确输入 API、真实 P/Invoke、调用/字段图、循环、时钟、队列、IO、Unity/TMP sink 和 lifecycle 生成保守候选，闭合子能力进入 owner-scoped consumer。自动识别失败时由用户补充根类型和角色绑定。输入 ABI 保留 Physical/Touch/LogicalAction/GameplayAccepted/Synthetic 的语义差异；手动 override 不绕过线程、调用图、P/Invoke、Unity API 和资源验证。
- 无外接键盘时，移动端 Touch 模式复用 MOD 的 lane factory/template/视觉资源重建 `TouchKeyCount=2/4/6/8/10` 个槽位，默认标签 `T1..TN` 且允许用户覆盖；该 presentation profile 不修改 MOD 原 PC 键位配置，底层仍是独立 `TouchLane` 身份。`Auto/Touch/External/Hybrid` 在关卡会话边界冻结布局。
- Hook 所有权继续集中在 HookBroker。计划中的每 MOD 执行隔离采用逻辑 `ModActor` + 固定共享 Native worker 池，不默认一 MOD 一物理线程；同步 Hook 语义不离开调用线程，MOD Unity 副作用仍只在 UnityMain，只有降低后的纯 adapter bytecode 可在 actor worker 执行。
- 最终后端默认使用 `ManagedSelfRender`；self-render readiness 失败时默认不自动启用 recipe HUD。`ProvenRecipe/CompatibleFallback` 兼容绘制必须由用户逐 feature 手动开启并显示差异；fallback 已提供通用槽位与单 Mesh rain，但不替代 MOD 原计数、持久化和视觉语义。后端选择持久化与更完整 failure UI 仍需实机验收。
- MOD 设置采用宿主兼容设置与原 MOD 设置双入口。ModManager 齿轮始终打开 `IModSettings` 宿主页，使 KeyViewer、移动端覆盖、绘制后端和兼容诊断保持可访问；宿主页顶部的“打开 MOD 原设置”才发出 owner-scoped 请求，成功后进入 `Opening` modal 并暂时隐藏管理器。宿主页正常状态也渲染 verified `mod_settings.schema` 的 live mirror，但它直接绑定原 JALib/UMM 对象、原 setter 与原保存入口，不创建第二份设置；原菜单保存或关闭后在 UnityMain 刷新 snapshot，使两边双向一致。该镜像仍不注入 MOD 的 IMGUI/Canvas 树。原菜单关闭、unavailable 或 fault 时恢复同一个宿主页；fault 会写入 `last_settings_failure.txt`，在宿主页顶部显示红色 `Fallback`，并复用同一 verified binding renderer。`Opening/Open/Faulted` 使用独立 settings 状态机，回调异常只熔断 settings surface，HUD/Hook/MOD lifecycle 与 presentation ownership 不受影响。打开回调前后新增/激活的 owner-scoped Canvas 会被识别为 `UnityCanvas` surface；Canvas 失活/销毁自动结束原 modal；IMGUI surface 在真实 `BeginGUI` 上下文中执行。Android modal ownership 保证原 Unity Canvas/IMGUI 收到真实 Unity MotionEvent，同时 Activity、AsyncInput 与 metadata 动态解析并由 HookBroker 永久安装的 `EventSystem.Update` gate 阻断 gameplay 输入透传。JALib shim 继续按原 `Settings.json -> Setting/Feature/<name>/{Enabled,Setting}` 结构读写并保留 `.bak` 和未知字段；Feature `Enabled` 使用原 setter，普通字段在 UnityMain 写回后调用原保存入口。字段 apply 与 save 失败分别进入 snapshot、诊断和 fallback UI，部分写入保持未保存状态并允许重试原保存。未知对象、跨版本 schema 和不安全 callback 仍失败关闭。
- 持久化路径目标已冻结为 `package/<assemblyHash>` 只读层与 `data` 可写 overlay。VFS 只做 owner/path 隔离，读取 data-first/package-second，所有写操作只落入 data；MOD 原文件格式、备份逻辑和保存时序保持不变。MOD 更新保留 data，卸载时由用户选择是否删除。
- KeyViewer 的 `visibility`、`inputActivation`、计数/KPS/rain 和 reset 在 Adapter IR 中是独立的 MOD-owned 语义。Native 只拥有有序输入、canonical held 和 touch contact 真值，不能用统一计数器覆盖 MOD 规则。Jipper 已核实为 feature 启用期间全场景显示和计数、`KeyCount.dat` 跨局持久化、仅设置菜单确认动作清零；暂停、失败、重试和场景卸载均不触发 KeyViewer 重置。用户可显式启用默认关闭的移动端 `Compatibility Override`，但只能覆盖 `inputActivation` 作用域；其余规则保留 MOD 原逻辑。自动识别无法证明时 feature 默认隐藏并停止计数，不能猜测生命周期。
- Native 通用次数/KPS 只保留为 release 聚合 audit，TRACE 才记录逐事件；差异不自动纠正 MOD。诊断导出目标包含同一 event-sequence 边界下的 MOD verified 状态快照（settings、Adapter、feature/lane、count/KPS/rain、lifecycle/presentation、Hook/queue/fault 和安全可读字段），不调用未知 getter/方法；预算截断和不可读项必须显式报告。
- KeyViewer 自动识别按子能力记录 `Proven/Probable/Ambiguous/Unsupported`，不生成单一兼容率。只有 Proven 自动启用；Probable 需用户确认候选，Ambiguous 需手动角色绑定，Unsupported 禁止启用。UI 必须给出证据、首个断点和受影响功能。
- KeyViewer 核心 `input -> transition -> MOD count/state -> presentation lifecycle` 按依赖闭包失败关闭；rain、ghost rain、设置、附加统计和装饰仅在 MOD 原逻辑存在独立关闭路径时允许局部降级，不能删改参与计数的 lane 或状态转移。
- KeyViewer 过载策略以计数零丢失为硬约束：状态边沿、count/reset/settings/lifecycle/persistence 不可丢；同值展示/重复查询可合并，过期纯视觉 rain/动画采样可淘汰。MOD backlog 不得反压游戏输入或官方判定，计数推进必须与 UnityMain presentation backlog 解耦。
- KeyViewer 物理输入最终路由已冻结：AsyncInput enabled 时从 `async_input.c` 的 native snapshot observer ABI 取得，且在 capture/test-macro/gameplay gate 前发布；AsyncInput disabled/absent 时从 Activity `dispatchTouchEvent/dispatchKeyEvent` 的 pre-super 观察取得。同一时刻只允许一个 producer，切换 epoch 先 CANCEL held。`GameplayAccepted` 另由 HookBroker 观察 `scrPlayer.HitInputEvent` 成功返回；禁止 Hook 有重复调用和副作用的 `ValidInputWasTriggered/CountValidKeysPressed`。
- [done 2026-07-22] 物理输入双 producer 路由已落地：AsyncInput 导出 raw observer ABI v1，ModManager 用 `RTLD_NOLOAD/dlsym` 注册；enabled 时 Async native snapshot 是唯一 producer，disabled/absent 时 Activity pre-super dispatch 是唯一 producer。RealtimeEventCore 已实现 producer epoch、旧 held CANCEL、`ProducerChanged`、旧 producer 拒绝和完整 touch/key metadata；AsyncInput broker arm64、ModManager arm64、host producer 测试与 Android input contract 均通过。
- [done 2026-07-28] Android modal 下的实体键重绑链已修复：Activity 对 Back 保持菜单关闭所有权，其他 `KeyEvent` 无论 modal 与否均先进入同一 observer/AsyncInput 路由，再交回 Unity；AsyncInput raw observer 仍在 capture gate 前发布。Bootstrap 只按 `KeyCharacterMap.VIRTUAL_KEYBOARD` 排除软键盘事件，不再用 `sKeyboardShown` 同时误杀外接物理键盘。该约束由 Android input contract 覆盖，防止 Jipper 已进入等待态但 journal 永远收不到键值。Activity 修复位于 APK 的 `classes6.dex`，部署时必须同步该 dex；只替换 managed runtime 或 native SO 不会改变 Java 事件路由。
- [done 2026-07-22] `GameplayAccepted` 已落地：RecipeCompiler 为 HUD/KeyViewer 能力自动生成 `scrPlayer.HitInputEvent(bool, InputEventState)` after-rule；HookBroker 使用精确 `InstanceBoolBoolInt` ABI、原样转发隐藏 `MethodInfo*` 并捕获原始 bool 返回值，只有 `true` 才发布。事件进入独立 2048 槽 accepted ring，不修改 raw physical held/KPS/total；普通调用保持 `GameAction Down/Up`，`isAuto=true` 保持 `Synthetic`。异步测试宏虽调用 `HitInputEvent(false, ...)`，但 AsyncInput bridge 在注册时一次性解析并缓存 `ADOFAIAsyncInput_IsTestMacroEnabled`，宏启用期间同样标记 Synthetic。raw journal 已在 2026-07-23 扩为 8192 槽并接入 native wake；accepted ring 仍为 2048 槽。两者均为有界输入流，不能替代最终 MOD count 零丢失持久化语义。
- [done 2026-07-22] raw count checkpoint 已落地：RealtimeEventCore 在 producer 接受事件的同一锁内维护 64 位 lifetime/session 总数、`2/4/6/8/10` 五套精确 touch lane projection 和 256 项 canonical key identity 表。event ring 覆盖不再破坏通用 HUD 的 held/累计值，HudLogicWorker 直接消费 checkpoint；identity 表耗尽显式记录 overflow，不静默合并。该层只恢复 raw 输入事实，不能替代 MOD-owned ghost/filter/KPS/reset/persistence 状态机，因此最终 MOD count journal 仍为 partial。
- [done 2026-07-22] `keyviewer-adapter-v1` schema/validator 已落地：多 feature/lane-group、source profile、lane binding、角色、visibility/inputActivation、count semantics、逐子能力证据和 SHA-256/MVID/revision/proxy-surface 失效上下文均有确定性 JSON 契约与测试。`Probable` 即使用户确认也不会升级为 core ready。
- [done 2026-07-23] `PcCompatKeyViewerBehaviorScanner` 已按行为生成 Adapter 诊断：不使用 MOD/类型身份作 seed，精确识别 Legacy Input、真实 `user32.GetAsyncKeyState:Int16(Int32)`、Input System Button/KeyControl 查询候选和 Rewired polling，并用调用边和字段 writer-reader 边关联 transition/count/KPS/rain/presentation/lifecycle/persistence 候选。v5 analyzer 在局部 CFG back-edge、数组 provider/same-index consumer 和结构化 identity transform 上生成保守证明，已覆盖 `0x1000` Unity/VK split、Unity KeyCode、VK 直通和 VK offset；复用同一 local 的 provider 按循环起点选择最近支配赋值。managed cache v15 原子发布 Adapter/issue/SHA-256 manifest，cache hit 不重扫 IL；Input System 整链 lowering、一般 dominance/alias、其他未证明 transform、跨 helper provider selection 和任意 API 仍保持 fail-closed。
- [done 2026-07-23] KeyViewer 手动绑定控制面已落地：`.pccompat/keyviewer_overrides.json` 只允许选择 Adapter 已扫描角色，绑定行为包 SHA-256、完整程序集 SHA-256/MVID 集、目标 revision 和 proxy surface；伪造候选、缺失指纹、旧版本和重复角色均失败关闭。ModManager 支持逐 feature enable、`Auto/Touch/External/Hybrid`、`2/4/6/8/10` Touch lane 和角色确认，诊断导出包含当前 native producer 与配置状态。确认不会提升 evidence/readiness。
- [done 2026-07-23] `raw input event ABI v1` 已落地：32-byte read header、88-byte event、每批最多 256 条，保留完整 sequence/raw_ns/session/producer/source/phase/设备/触点/坐标字段。`UINT64_MAX` cursor 原子开户到当前 ring 尾；运行期 `droppedBeforeCursor > 0` 明确表示不可恢复 gap。
- [done 2026-07-23] Adapter consumer / wake 主链已接通：有效 override 按 MOD 持有独立 cursor，同 cursor 注册项共享 native read；单个后台 wake 线程等待 native condition variable，UnityMain frame 只保留兜底提交。actor batch in-flight 时通过共享 `AutoResetEvent` 等待推进，不再 1ms `Thread.Sleep` 忙轮询。8192 槽 raw journal 按 sequence O(batch) 读取，固定 2-worker pool 执行逐 MOD串行 mailbox；空 batch 不投递。mailbox 默认硬上限 256，单 turn 最多 64 项并按 4ms cooperative slice 让出；过载拒绝并只 fault 对应 feature，不等待游戏输入。Touch 按全局 `ScreenRegions/TouchContacts` 规则映射 `T1..TN`，严格验证 sequence、session/Reset、producer epoch/ProducerChanged；gap 或 actor 异常只熔断对应 MOD。行为扫描已证明 Jipper 的 `0x1000` Unity/VK identity transform，生产 lowerer 解析动态 `BindingProvider KeyCode[]` 并按完整指纹发布 immutable consumer plan。Android keyboard canonical mapper 同时发布 Unity KeyCode/Win32 VK，Hybrid alias 不重复推进逻辑边沿。原 MOD polling/state/count/KPS/rain 继续执行；确认的 `LabelProvider` 在 Touch 模式只填空白 `T1..TN` 标签。`Auto` session freeze、Input System 精确候选扫描、Rewired 首批 bridge、显式 fallback 槽位和单 Mesh rain 均已接通；剩余是 Input System 整链 lowering、实机 stall/多指/reload 压测和更广 API/transform 覆盖。
- [done 2026-07-28] KeyViewer 标签已改为实际模式驱动的可逆投影。lowerer 为显式 External 额外生成 presentation-only plan，但不注册 Touch consumer；UnityMain 在 preview mode pump 后为 Touch 临时写入 `T1..TN`，External/Hybrid 则恢复 `LabelProvider` 原值，由 MOD 自己依据 configured key 生成键名。投影状态只接管空白值，MOD 运行时自定义会解除该槽位所有权，配置替换/卸载恢复原值；旧版遗留的精确 `Tn` 在 External 首次同步时清理。Jipper 的 `Keys + UpdateKeyText(Key,int)` 结构会立即刷新现有键帽。fallback buffer 同步携带实际 `InputMode`，使用 presentation plan 的 ASCII configured-key 名称并复用同一 labels 数组，仅在模式变化时原地切换标签。诊断导出新增 `labelProjectionError`。
- [fixed 2026-07-28] 原 MOD 设置菜单的按键重绑已接入 Android modal 输入所有权。Jipper 在 `GUILayout.Button` 返回 true 的同一次 `OnGUI` 末尾立即执行 `Input.anyKeyDown -> Enum<KeyCode> -> GetKeyDown`；此前菜单触摸仍进入 Touch consumer，因此横向区域可被投影成默认键表中的 `Backspace` 并当场写回。现在 ModManager modal 状态同步到 `PcCompatLegacyInputBridge`：modal 内 Touch consumer 的 held/down/up/any edge 只做代次基线，不返回给 MOD，真实 Android 外接键盘 snapshot 继续可读；退出 modal 时旧边沿不重放，held 触点必须先释放。`anyKeyDown=false` 帧保存 512 键 down ordinal，随后首次枚举一个从未查询过的新硬件键也能按“高于等待基线”返回 true。native official Activity 与 AsyncInput touch observer 同时受 modal gate 控制，进入 modal 只发布一次 touch Cancel 释放 held，不清空 lifetime/session count；键盘 observer 不受 gate 影响。
- [done 2026-07-24] 触摸 KV 双映射模式已接通 ModManager 本体设置和 `modmanager_config.json`。`ScreenRegions` 保持横向等分；`TouchContacts` 把 Android 活动 contact slot 与展示 lane 解耦：同时存在的触点仍分别占用 `T1/T2...`，单指快速抬起再按下时优先避开仍在冷却的 lane。复用延迟默认 `80 ms`、用户可在设置页调整为 `0..500 ms`，`0 ms` 关闭延迟检测；所有 lane 都在冷却时复用最早释放的空闲 lane，不丢输入，超过 lane 数量的同时 contact 仍显式忽略而非合并。managed Adapter/self-render/fallback 与 Native 五套 checkpoint 使用同构算法；模式和门限热更新均通过 `PumpOrderingLock` 发布有序 control item，不能越过 in-flight batch。模式切换有序释放 touch held、保留 external held，并清空旧 lane 统计。诊断导出记录逐 feature `touchMapping` 与 `touchReuseDelayMs`。该设置仅改变 KV 展示身份，不修改原始触摸、游戏输入、AUTO、宏或判定。
- [done 2026-07-24] 兼容代绘 fallback 的帧内分配已收紧：MOD 注册排序移到 Register/Unregister 时发布 immutable dispatch snapshot；UnityMain 复用 dispatch frame、counts 和最多 256 项 rain buffer，并通过 preview 内部窄读接口按 MOD 一次加锁批量复制全部 feature 状态，不再构造完整 preview/feature snapshot、每帧数组或逐 feature 重复获取锁。Android bridge 使用 generation 标记回收 visual，复用 rain quad list，只在 visual 顺序、lane 标签、count、held 或 visible 变化时调用对应 Unity setter。`SetBatchMesh` 使用 1/4/16/64/256 quad 五档常驻 IL2CPP `Vector3[]`/index buffer，热段只原位写顶点；mesh bucket 不变时不重交 triangles，清空 rain 使用零面积 quad，不重新分配数组。fallback 时钟只读取值类型 `ProviderAvailable/rawNs`，不再为每个 native clock generation 构造完整诊断 snapshot；128 帧稳定态托管分配回归为 0 bytes。
- [done 2026-07-23] KeyViewer 正常配置路径已闭合：无 override 文件时，对可证明自动输入的 feature 原子保存推荐 `Enabled/Auto/10 lanes/self-render` 配置并在 MOD 加载完成时注册，不再等待用户进入诊断页。未手选 BindingProvider 时会解析全部扫描候选，只在恰好一个候选能覆盖全部触摸 lane 时自动 lower；唯一 LabelProvider 自动写入 `T1..TN`，歧义保持失败关闭。普通设置移到 MOD 设置页并立即保存应用，手动角色绑定折叠到高级诊断。诊断新增 registration `startCursor` 与当前 provider tail，可直接区分“注册太晚”和“注册后 producer 停止发布”。
- [done 2026-07-23] 修复自动 lower 与 registry 的契约断裂：`pccompat_JipperResourcePack_20260723_110959.txt` 中 lowerer 已产生 plan，但 registry 仍要求 confirmed BindingProvider，导致 `consumerRegistered=False`，自绘仅创建静态槽位且无 held/rain。registry 现接受通过完整指纹、候选归属、lane 和 identity 二次验证的自动 plan，资格统一为 `VerifiedLoweredBinding`。兼容代绘同时拒绝 inactive consumer，并以退化 quad 处理空 rain Mesh；renderer 完整异常进入 UI 和诊断文件，不再只留一条 Logcat 摘要。
- [done 2026-07-23] Legacy/Win32 输入查询桥已落地，并补齐 Rewired 首批入口：rewriter 把 `Input.GetKey/GetKeyDown/GetKeyUp/anyKeyDown` 和 `Rewired.Player.GetButton/GetButtonDown/GetButtonUp(int)` 改到 owner-scoped consumer bridge；edge API 使用稳定 callsite token，真实 `user32.GetAsyncKeyState:Int16(Int32)` P/Invoke 也按 metadata import 精确改写。所有输入 callsite 额外内嵌 `manifest.Id`，因此 Jipper 的 `KeyInputListener` 等 MOD 自建线程不依赖 thread-static UnityMain owner。Touch-only 正式 consumer 直接读 O(1) per-MOD immutable key map，Hybrid 才刷新并合并 1ms native snapshot。完整 Win32 低位边沿、Input System 整链 lowering、Rewired 轴/动作名和其他未证明入口仍 fail-closed。
- [done 2026-07-23, awaiting device verification] `pccompat_JipperResourcePack_20260723_114836.txt` 已证明 KeyViewer consumer 正常（363 events、362 transitions、10 identities、0 unmapped），故“输入正常但 MOD 自绘无变化”的断点收敛到 JALib/managed component lifecycle。根因是旧 `JALib.Tools.MainThread.Run` shim 在 Jipper 监听线程直接执行 Unity UI；`Key.UpdateRequestKey` 会在颜色更新处进入错误线程，后续计数和 rain 生产也可能被该轮异常中断。当前 shim 恢复 owner-scoped UnityMain 队列，由 `CompatUpdate` 在 MOD update 前有界排空，并以启用代次丢弃 disable 前旧任务。诊断导出新增 JALib queued/dequeued/executed/failed/pending 与逐组件 `Awake/OnEnable/Start/Update/LateUpdate/OnDisable/OnDestroy/OnGUI` 计数；下一次实机报告应直接显示 `KeyViewerUpdater/RainManager` 是否持续 Update，不再用 JAMod 总帧数替代组件证据。
- [diagnosis 2026-07-23] `pccompat_JipperResourcePack_20260723_122006.txt` 排除了 managed component lifecycle：`KeyViewerUpdater`、`RainManager` 均 active/started 且 `Update=3411`，JAMod 无 fault；consumer 同时处理 62 个 transition、0 unmapped。但 JALib 主线程队列 `queued=0`，说明 Jipper 原 `Work()` 从未产生任何按键状态变化。已从设备实际 cache 拉取 DLL 并核对 IL：`CheckKey` 确实调用 owner=`JipperResourcePack` 的 `GetKeyOwned/GetAsyncKeyStateOwned`，不是旧缓存或重写漏失。下一版主动诊断新增 Feature host/enable、MOD Thread alive/state、JALib 有界异常快照、Legacy/Win32 查询调用/命中/true 计数和 consumer identity surface；异常 Logcat 按 1/2/4/8... 次幂采样，完整最新异常只进入诊断文件。该阶段只缩小运行态断点，不把尚未实机验证的 listener/query 原因标为已修复。
- [fixed 2026-07-23, awaiting device verification] `pccompat_JipperResourcePack_20260723_124943.txt` 定位到最终断点：`KeyInputListener` alive/running，owner query 已调用 474438 次、命中 237388 次并返回 true 1664 次，但每个状态变化在 `Key.UpdateRequestKey` 抛 `MissingMethodException: JALib.Tools.MainThread.Run(JALib.Core.JAMod, System.Action)`。shim 此前只有 `Run(object, Action)`；CLR 按精确 MemberRef 绑定，不执行参数协变替代。当前补入 exact `Run(JAMod, Action)` overload，并保留其内部统一进入 owner-generation UnityMain queue。新增真实 Jipper DLL MemberRef 对 shim reflection surface 的回归，防止源码调用可编译却在设备首次执行时报 MissingMethod。
- [fixed 2026-07-24, awaiting device verification] 修复兼容代绘切换托管自绘的 presentation 竞态与点击卡死：`TryRequestManagedSelfRender` 必须先发布 `ActivationPending`，再注销同 MOD 的 KeyViewer fallback，因此 frame gate 不会在同一 GUI callback 内短暂经过 `Active -> Disabled -> PendingActivation`。按钮路径不再触发 `RegistryChanged` 全量同步。PresentationSink 的 metadata 解析与 Dobby/HookBroker 安装统一由 native coordinator 异步预装和 500ms 重试；managed frame gate 与 VirtualBundle/resource scheduler 只读取 installed 标志、未就绪时发请求并返回 pending，禁止在 ModManager UI、IMGUI、Canvas 或 `CompatEnable` 调用栈同步安装物理 Hook。异步请求入口只原子发布并创建/唤醒 coordinator，不再同步注册 HUD worker 或 AsyncInput observer；重复 pending 请求合并，不再持续更新 generation 干扰重试。`ActivationPending/ManagedPresentationClaimed` 继续阻止 Unity HUD 和 fallback 重新注册。`CompatEnable` 前后新增一次性阶段与耗时日志，完整失败仍只写 `last_managed_failure.txt`。
- [fixed 2026-07-24, awaiting device verification] 针对 Jipper 快速击打时 `PERFECT COMBO` 暂停计数，managed callback 时序改为已启用会话先 drain native postfix，再执行 `CompatUpdate`，使 callback 内 `MainThread.Run` 能被同帧 `JAMod.CompatUpdate` 开头的 `MainThread.Drain` 消费。单帧 managed event drain 从单批 128 条扩为最多 8 批/1024 条；presentation ownership 本身保持 frame gate 常驻。诊断导出新增 dispatch `nativeDropped` 与 `budgetExhaustedFrames`，并保留 native `queued/dropped`，用于区分 ring 溢出和单帧预算耗尽。该修复不改触摸、判定或 MOD 自有计数规则。
- [fixed 2026-07-24, awaiting device verification] 修复运行中 managed self-render 回退后 `.NET TP Worker` 在 `libcoreclr.so` 主动 abort：旧 UnityMain `SynchronizationContext` 为无 owner 单例，且 `Post()` 在 scheduler 不可用或共享 64 槽队列满时直接抛异常。现在 owner/phase 身份绑定到被 await 捕获的 context，而不是在 ThreadPool 上读取 `[ThreadStatic]`；资源与 continuation 队列分离，continuation 使用 2048 槽、每次 UnityMain 最多 16 条的独立预算。所有 rejection 和 continuation 执行异常转为对应 session 的 pending fault，由 UnityMain 安全执行 disable/组件清理/ownership 归还；`last_managed_failure.txt` 只保留首次根因，后续 rejection 不覆盖。诊断 `platformRuntime` 新增两条队列的 `pending/capacity/high/accepted/rejected/executed/failed`。`OnGUI` fault 也会在当前 UnityMain 回调立即归还 presentation ownership。
- AUTO、oldAuto 和测试宏不计入普通玩家输入统计。

### 通用 UI graph

当前实施提交已经实现普通 MOD 的受限 UI graph lowering：

- 从 manifest/JAMod 生命周期入口建立 bounded reachable-method index。
- 支持递归深度受限的静态 helper，并在单 helper 失败时 checkpoint 回滚。
- 支持 `GameObject`、`RectTransform`、`Canvas`、`CanvasScaler`、`Image`、`RawImage`、`TextMeshProUGUI`、`CanvasRenderer` 和 `ContentSizeFitter`。
- 支持 parent、受限 active 意图、anchors、pivot、rect、local scale、Canvas 设置、颜色、raycast、静态文本、字号、对齐、rich text、line spacing 和 fit mode。
- native PresentationSink 可按 recipe 创建、更新、隐藏/重建、销毁对象，并用 GCHandle 管理 Unity 对象生命周期；隐藏不再调用 `GameObject.SetActive`，而是销毁 runtime graph，重新显示时再物化。
- overlay fixed-op generation 已连接到 `OverlayStateChanged -> LoadOverlayVisible -> SetActive`。
- Proven 静态资源字段可 lowering 为 `ImageSprite`、`RawImageTexture`、`GraphicMaterial`、`TextFont`、`TextFontSharedMaterial`、`TextFontMaterial`；运行时 setter 全部按完整 metadata identity 解析。

该部分当前仍是“部分实现”：Jipper 的 TMP 字体已进入实际 graph binding，但动态文本、动态 prefab、KeyViewer/SideImage 具体对象图、一般循环和动画尚未进入通用 recipe。

### Il2CppInterop 迁移与 MOD 重写

- forked generator 已提供 `runtime-metadata-only` 模式，不生成生产 RVA/xref cache。
- 已建立 Android dump index、严格依赖闭包、proxy surface scanner、proxy audit 和 Android slim Runtime。surface scanner 已覆盖常量字符串反射成员查询；可空查询与直接成员依赖分开处理，缺失查询会报告但不伪造成员。
- 默认 surface 已吸收经自动扫描验证的 `AssetBundleModule`、`IMGUIModule`、`InputLegacyModule`、`TextCoreFontEngineModule` 和 HUD/TMP/Material/PrefabGraph/component/coroutine 调用，当前闭包选择 165 个精确输入类型，生成 13 个代理程序集（含 generated corlib）；审计覆盖 176 个生成类型，闭包缺失和 metadata audit 均为 0 issue。由 managed bridge 接管的同步 `AssetBundle.LoadFromFile`、`LoadAsset`、`LoadAllAssets` 不进入 native proxy surface，避免 Android metadata 已裁剪的成员使整个 generated `AssetBundle` 类型初始化失败；auto surface scanner、构建审计和 Android 启动审计共同执行该边界。
- generated generic proxy 和 `MethodInfoStoreGeneric_*` 均禁止直接调用 `il2cpp_class_get_type`。普通泛型代理会校验泛型定义、每个参数和最终 inflated class；泛型方法缓存会校验参数 class、反射对象和最终 method pointer。缺失或歧义的 exact method 直接抛托管异常，不再返回可进入 `runtime_invoke` 的伪指针。构建期审计与 Android 启动预检同时覆盖当前 14 条泛型初始化链，并逐方法校验对象分配、虚分派、装箱/拆箱、class/type inflation、反射 method inflation 和 invoke target 的 guard 顺序。
- Android slim Runtime 的 `GetIl2CppField`、对象 wrapper、对象池、泛型值转换、三类数组桥和 delegate bridge 已统一使用 `RequireIl2CppClass/Object/Method/Pointer`。generated static non-blittable field getter 也会在调用 `il2cpp_class_value_size` 前校验 value class；构建期与 Android 启动期同时审计 `il2cpp_class_is_valuetype` 的 class guard。缺失 class、field、method、GC handle、array/object allocation 或 unboxed data 时抛托管异常，禁止把零指针继续传给 IL2CPP API。当前 PcCompat 契约测试为 238/238。
- HUD 代理审计和 Android 启动预检已强制要求 `CanvasScaler.uiScaleMode`、`ContentSizeFitter.horizontalFit/verticalFit` 与 `Image.type` setter。surface scanner 统一使用 `/` 表示嵌套类型，并只在同程序集简单名唯一时接受 `dump.cs` 展平的嵌套枚举。
- forked generator 会区分 Cpp2IL 自定义 field offset 与标准 CLR layout：缺少显式 offset 的 AssetRipper blittable struct 保留顺序布局。代理审计和 Android bootstrap 分别静态、运行时校验 `Vector2/Vector3/Color` 的 8/12/16 字节布局，防止 `runtime_invoke` 因重叠字段覆盖 CoreCLR 栈。
- Unity/`Assembly-CSharp` 假 shim 已从 runtime assets 隔离到 `out/legacy_shims`，未重写 PC DLL 默认拒绝执行。HUD 与 AssetBundle 消费链已使用 generated proxies；`UnityResolve` 源码和生产依赖已删除。
- Android slim Runtime 继续硬禁 xref；arm64 上游 ClassInjector 当前不尝试注册，未来受控注入的内部 detour 仍必须经 HookBroker 永久安装，MOD 不能直接调用。普通 delegate 使用 IL2CPP `System.Object` target + CoreCLR process-lifetime rooted cache，不请求 class injection。primitive/enum/blittable struct 参数可用，by-ref 和含引用 struct 失败关闭。泛型代理方法按 static、generic arity、返回类型和有序参数类型精确解析，歧义时失败关闭。
- `ModAssemblyRewriter` 使用 dnlib 将可证明的字段访问和方法调用改写到 generated proxy accessor。
- rewriter 会把缺失的 `Assembly-CSharp`、`RDTools`、`Unity.TextMeshPro` 和 `UnityEngine.*` proxy 记为失败，不能再由 PC shim 静默承接后报告成功。
- `ModAssemblyRewriter` v13 已支持把当前 revision 的 ReversePatch stand-in 直接调用重写到 managed bridge；支持完全相同参数和零参 bridge 前显式丢弃源参数，拒绝实例 stand-in、重载歧义、返回类型不一致、`callvirt` 和 tail call。
- Jipper ReversePatch 实际 DLL 回归覆盖零参、参数保留、参数丢弃、同名异类型隔离和 ABI 不兼容失败关闭，5/5 通过。`ldftn` 仅用于 PATCH 注册并保持原样；反射/委托动态调用尚不支持。
- v13 external call bridge 按程序集、类型、静态性、泛型元数、返回值和有序参数完整匹配；`AssetBundle.LoadFromFile(string)`、非泛型 `LoadAsset(string)` / `LoadAsset(string, Type)`、`LoadAllAssets()` / `LoadAllAssets(Type)` 与 `Unload(bool)` 已转到 owner-scoped VirtualBundle bridge，错误重载和 ABI 歧义失败关闭。bridge 还支持从源 by-ref 参数闭合 bridge 泛型，并对 `Coroutine` 这类受控句柄执行模块级 opaque type erasure；句柄流入未知 API 时导入失败。
- `UnityEngine.Debug.LogException(System.Exception)` 会按完整方法身份改写到 `PcCompatManagedLogBridge`，由 CoreCLR 日志链处理原始异常；禁止把 `System.Exception` 传给要求 `Il2CppSystem.Exception` 的 generated proxy。bridge ABI 已进入 managed cache key，升级后自动生成新缓存。
- v13 会按显式 MOD-owned assembly catalog 选择性改写 `GameObject/Component` 的 managed `AddComponent`、`GetComponent`、`GetComponents` 和 `TryGetComponent` 泛型/`Type` 重载。跨伴随程序集可证明继承 `UnityEngine.MonoBehaviour` 的类型进入 managed component bridge；generated proxy 类型保持官方 IL2CPP 泛型路径；未知或 catalog 外类型在导入期失败关闭。`Component.gameObject/transform` 统一回到 registry owner。Jipper 的 `KeyViewerUpdater` 与 `RainManager` 已进入真实 DLL 回归，`Canvas` 等原生组件保持官方 IL2CPP 路径。
- 当前改写产物使用 managed cache v15：从主程序集和 bootstrap 两个根读取 PE `AssemblyRef` 闭包，将主 DLL、bootstrap 和伴随 DLL 作为一个原子 bundle 写入缓存。cache key 覆盖闭包内每个 DLL、当前 166-type proxy surface、rewriter v14、bridge/platform-oracle SHA/spec、KeyViewer Adapter schema 与扫描器版本；Adapter、issue、rewrite report 和 SHA-256 manifest 一并原子发布。ALC 按 assembly simple name 优先加载全部重写副本，任一 DLL 失败时整包不发布；旧 cache 不复用，也不要求用户手工删除。
- managed lifecycle 已接 `CompatEnable/CompatUpdate/CompatDisable` delegate、Feature 调度、owner execution context、重入拒绝、单 MOD fault 隔离和 update 统计。managed component registry 按 MOD id、resource generation 和 owner GameObject 原生身份绑定 CoreCLR 组件，调度 `Awake/OnEnable/Start/Update/LateUpdate/OnDisable/OnDestroy`。`Start(): IEnumerator` 与三种 `StartCoroutine`、三种 `StopCoroutine`、`StopAllCoroutines` 已接入同一调度器；支持最多 32 层嵌套 `IEnumerator`、`yield null`、`WaitForSeconds` 和 `WaitForSecondsRealtime`，每次 opportunity 有 256 次立即转换预算，未知 yield 直接 fault。`Object.Destroy(Object,float)` 使用 Unity scaled clock 镜像 managed 清理并让 native GameObject/Component 继续走官方延迟销毁；CoreCLR component 从不传给 native Destroy。callback 内自毁有重入回归。失败会立即写入 `<mod>/.pccompat/last_managed_failure.txt`，诊断导出保留完整异常，logcat 只留单行摘要。
- managed rewrite 失败现在单独记录为 capability error；默认 verified recipe 仍可继续编译和加载，显式 oracle/self-render 才要求 rewrite bundle 必须存在。
- managed rewrite 汇总异常会附带首个具体 field/method/bridge issue 的方法、IL offset、目标和原因，避免设备诊断只显示 `methodIssues=N` 而丢失决定性证据。
- 生产默认仍使用 native recipe；`STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE=1` 仅用于验证改写 DLL 和 managed 生命周期。

### JipperResourcePack 当前回归结果

2026-07-13 对 r143 的当前 probe：

```text
static patch descriptors: 74
  direct: 40
  dynamic AddPatch: 34
  active on r143: 49
  scanner issues: 0

callback translation:
  translated rules: 28/28
  unsupported callback groups: 4

compiled recipe:
  features: 6
  runtime rules: 30
  unsupported/diagnostic items: 14
  compatibility: partial

translated UI:
  nodes: 16
  roots: 1
  lifecycle programs: 2
  component operations: 73
  resource bindings: 13 (TextFont)
```

Jipper 是回归样本，不是生产特判。当前 `partial` 的主要原因是：仍有未映射 callback group、SideImage 的更广动态构造、`Transform.childCount`、动态循环和 `UpdateSize` 尚未 lowering。`ProgressBar` 已作为通用 PrefabGraph v1 的首个真实样本，从源 bundle 递归提取 4 个节点及 Sprite/Texture 依赖并在 Android UnityMain 重建；`MAPLESTORY_OTF_BOLD SDF` 已从源 bundle 提取 face、glyph/character table、4096x4096 atlas、Material 和样式参数，在 UnityMain 重建为静态 `TMP_FontAsset`。字体外壳仍来自 capability clone，Material 的 Shader 仍使用 Android capability Shader，因此该路径标为 `compatible`，不冒充 desktop Shader 完全一致的 `exact` 对象。

### 2026-07-13 一致性审计修复

- 修复 managed `ui_recipe.bin` validator 漏检 target/rule/parameter string/range、空 graph 非空 operation section 和 lifecycle budget 上限，避免 compiled cache 接受 native 必然拒绝的 recipe。
- resource recipe 现在同时验证 container 完整性与语义完整性，包括 `modId`、recipe identity、candidate SHA、group/binding 引用、数量上限及平台/版本/load policy 组合。
- 修复 candidate 只按文件存在与 SHA 前缀判定 `Ready` 的问题；复制和实际加载前均验证文件大小与完整 SHA-256，并去掉跨 MOD compiled 目录递归兜底搜索。
- 修复 complete marker 掩盖损坏 `hook_rules.json`、recipe report 和 resource candidate 的问题；损坏 cache 会在同一进程串行重建。
- 修复危险 MOD ID（如 `..`）可影响 compiled cache 路径的问题；cache 目录段现在采用严格白名单与保留名处理。
- 修复 load failure 每次 `TryEnsure*` 都重新进入 sink，以及单个异常永久毒死全局 Android loader 的问题；结果改为 session 级记忆，reload 后才允许重试。
- 修复 recipe bundle 发布失败仍把 MOD 报告为 loaded 的问题；native 真值 bundle 无法发布时注册明确失败关闭。
- 修复桌面 Unity 6000.3.x bundle 被误标为 `AutoLoad`；Linux/Windows/Mac 现在统一为 `ControlledLoad`。
- `UnityBundleIndexer` 改为单 FileStream 流式 SHA/索引，并在所有异常路径 `UnloadAll(true)`，避免损坏大 bundle 导致整文件副本和 AssetsManager 泄漏。
- Native resource resolver 改为锁内快照、锁外 managed callback、锁内身份复核与应用，避免持有 `g_graph_lock` 跨入 CoreCLR；重入由原子门禁失败关闭。
- 未就绪或门禁关闭的 binding 进入 per-binding waiting 状态，不随 presentation snapshot 逐帧重试；recipe 发布、成功 completion、cache hit 和 asset 状态变化通过有界 UnityMain 队列合并唤醒消费者。
- bundle unload 的 Unity 引用清理、HUD 释放、GCHandle 和 Unity API 清理均移出 managed loader `Gate`，pending request 仍按 generation 延迟最终 unload。
- 旧 `resource-recipe-v1` 继续可用：缺少结构化 `sourceFieldIdentity` 时只解析编译器固定 `Reason` 格式，并使用严格字段分隔符恢复身份。

## 部分实现

| 能力 | 当前边界 |
| --- | --- |
| JAPatch | 可恢复 descriptor 并翻译受支持 fixed-op；不执行任意 callback |
| Harmony | annotation/registry/常用工具 ABI 已闭合到 61/61 类型、871/872 成员；唯一缺口是 v42/v44 无法同时提供的 `HarmonyReversePatchType.AllCombine` 字面量。不能无损实现的发 IL API 保留完整 ABI，并在调用点显式抛错和记录诊断。descriptor、运行时 metadata 目标签名、Postfix managed event 与同步 Prefix 已进入 HookBroker；物理 Hook 仍只由 ModManager/HookBroker 安装。完整边界见 §Harmony 完整度。 |
| Prefix/Postfix | Postfix 通过 UnityMain managed-event 队列派发；同步 Prefix V2 在原 hook 线程执行，支持 `void/bool`、`__instance`、generated-proxy `ref/out __instance`、Prefix/Postfix `__originalMethod`、最多 6 个 primitive/enum/proxy 参数、primitive/enum `ref/out`、primitive/enum `ref __result`、`ref __runOriginal`、generated-proxy `___field` 读写、同步 Prefix 可写 `__args` 和 Prefix/Postfix `__state` 配对。实例替换要求 `(IntPtr)` 构造和可读 `Pointer:IntPtr`，并由全部 12 个实例 dispatcher 在 original 前采用。deferred Postfix 支持最多 6 槽的 primitive/enum/generated-proxy 只读 `__args` 快照与 bool/int/enum 按值 `__result`；任何 `ref/out` 参数或 `ref/out __result`/`ref/out __instance` 写回要求均失败关闭。运行时 `owner/priority/before/after/registrationIndex` 以 staging+commit 计划发布，HookBroker 在 immutable snapshot 建立期做跨 MOD 拓扑排序；Harmony registry revision 每帧由预编译 delegate 检查，Patch/Unpatch/Repatch 当帧重建计划。`false` 后只跳过会影响 original 的 Prefix，纯观察型 Prefix 继续执行。仍缺未知 struct/普通 proxy-byref、同步 Postfix 写回，以及 Transpiler/Finalizer 的生产执行。 |
| ReversePatch | rewritten managed 路径已支持静态 descriptor 驱动的直接调用点 bridge；反射/委托调用、任意 method body 替换和生产默认切换未完成 |
| generated proxy | 默认生成 13 个程序集（含运行时 generated corlib 和 `UnityEngine.TextCoreFontEngineModule`）并严格审计缺失目标；HUD、AssetBundle 和静态 TMP 重建已切换，native telemetry snapshot 仍按既定边界保留 |
| managed self-render | lifecycle、owner context、UnityMain 三态 frame gate、pending activation 和按 MOD recipe presentation ownership 已接通，当前代码默认关闭；最终策略已改为 self-render 默认、失败关闭，兼容绘制只允许用户逐 feature 手动开启。VirtualBundle、批量/`TryGetComponent` component API、伴随程序集、透明 owner、嵌套/显式 coroutine 与立即/延迟 Destroy 已接通；managed OnGUI 派发与自绘期间兼容 HUD 抑制已实机生效（2026-07-21）。**游戏事件回调（JAPatch postfix）managed 派发已落地**：导入期只为仍需托管行为的 callback 生成 `ManagedEventCallback=21` 规则，descriptor-only fixed-op 不二次派发；native 每 MOD 2048 槽事件队列、UnityMain 帧 drain + 参数绑定（无参/标量/枚举/装箱枚举/`__instance`/`___field`）已接通。Jipper r143 当前为 18 条 managed callback；加入完整 ResourceChanger 17-target fixed-op 与平台 `GameplayAcceptedObserve=22` 后样本为 52 规则/34 target。2026-07-24 lifecycle boundary 已改为场景队列屏障，防止旧场景裸实例跨 Hide/Reset 延迟派发。切换默认前仍缺后端选择持久化、readiness/failure UI 和禁止自动 recipe fallback 的门禁；并仍不支持序列化 managed 字段、Inspector/native message system、`GetComponentsInChildren/Parent`、自定义 yield、动态生成程序集，以及把 CoreCLR 组件作为真实 IL2CPP `Component` 传给任意未知 Unity API |
| MOD 设置菜单 | UMM/JALib 原 Unity IMGUI、owner-scoped Canvas 识别、UnityMain 写回、独立故障报告、Android modal pointer/keyboard/Back ownership和原保存入口已接通；宿主兼容页常驻显示 verified schema live mirror，原菜单 save/close 后刷新同一对象 snapshot，compat 写入走原 setter/save，fault 时同一 renderer 切为红色 fallback。设置 surface 已与 managed self-render lifecycle/presentation 解耦，`Loaded` session 可独立打开且同一 session 支持重复关闭/打开；仍缺本轮双向设置与 64 槽 HUD 链的真机验收，以及对任意非 JALib 自建设置 Canvas 的更广 owner 证明 |
| Unity HUD | 标准 telemetry HUD 可用；普通 MOD graph 已覆盖静态基础对象和 Proven UI 资源绑定 |
| KeyViewer | Native 触摸输入统计、Async/Official 双 producer、8192 槽 raw journal/checkpoint、native condition-variable wake、独立 `GameplayAccepted`、Legacy/Win32/Rewired 首批 bridge、Input System 精确候选扫描、Adapter schema/手动绑定、observe-only preview、固定共享 ModActor worker 和正式 consumer 可用。Proven 静态 lane 或结构化动态 identity lowered plan 可由原 MOD polling/state/count/KPS/rain 消费；Android keyboard canonical mapper 支持 Unity/VK Hybrid，owner 固化支持 MOD 自建线程。`Auto` 在 session 边界冻结；Touch `LabelProvider` 可为原 MOD 自绘空标签补 `T1..TN`，用户显式启用时可使用通用槽位与单 Mesh rain fallback。仍缺 Input System 整链 lowering、通用 lane factory 证明、更广输入 API/identity transform 和 stall/多指/reload 实机压测 |
| ResourceChanger | Jipper R143 的 17/17 patch、VirtualBundle `Auto` Sprite、动态原设置/Jongyeol 配色、完整 Logo clone 与关闭/卸载恢复已接通；这是 Jipper 专用完整适配，不等于任意 MOD 通用资源层 |
| AssetBundle / resource recipe | AssetsTools.NET -> Resource IR v1 -> VirtualBundle -> Android UnityMain 真实 proxy 已形成生产子集；支持 Texture2D、Sprite、白名单 Shader 的 Material、静态 TMP atlas/metrics 重建、TMP capability fallback、owner-scoped 设置 Font 选择与 Unity 6 TextCore FontAsset 投影，以及 Transform/RectTransform + CanvasRenderer + Image/RawImage 的受限通用 PrefabGraph v1。同步泛型/非泛型 AssetBundle API 已桥接；非空 OpenType feature table、动态字体、异步 API 和任意组件 prefab 未完成 |
| 多 Hook | HookBroker chain 已实现；任意 ABI universal bridge 未实现 |

### 2026-07-16 managed component 完成度与注入边界

按“不使用 ClassInjector、覆盖常见自定义 MonoBehaviour”标准，当前 managed-component 主干约完成 92%；按“完全等价 Unity 原生/ClassInjector”标准约 60-65%。这些百分比是能力面估算，不替代实机验收。

当前已实现：

- 泛型与 `Type` 形式的 `AddComponent/GetComponent/GetComponents/TryGetComponent`，并在 native proxy 类型与 MOD-owned CoreCLR 类型间分流。
- 主程序集、bootstrap 和伴随程序集原子重写/加载。
- 透明 `gameObject/transform`、七类生命周期、owner fake-null/active/enabled/session 门禁。
- `Start(): IEnumerator`、三种 `StartCoroutine`、三种 `StopCoroutine` 和 `StopAllCoroutines`。
- `yield null`、`WaitForSeconds`、`WaitForSecondsRealtime` 与最多 32 层嵌套 `IEnumerator`。
- `Object.Destroy(Object)`、`Destroy(Object,float)`、callback 内自毁和 process/session 清理。

仍可在无注入架构内继续完成：

1. `GetComponent(s)InChildren/Parent`、List overload、`includeInactive` 与 native/managed 合并顺序。
2. `WaitUntil/WaitWhile`、`CustomYieldInstruction.keepWaiting`、`AsyncOperation/ResourceRequest` 和 coroutine-handle waiting。
3. `WaitForFixedUpdate/WaitForEndOfFrame` 对应的 UnityMain phase。
4. public/`SerializeField` 字段扫描、自有持久化和 ModManager 设置 UI；该能力不得标为 Unity 原生 Prefab 序列化。

需要架构扩展的真实 IL2CPP Component 身份采用独立 `InjectionTypeRegistry`，不复用普通 HookSlot。ClassInjector 内部 detour 必须由 ModManager 的 brokered infrastructure provider 经 HookBroker 安装，不占 64 个 fixed-rule dispatcher，不允许 MOD direct Dobby/ClassInjector。注入 class、vtable、MethodInfo、thunk、delegate、GC root 和承载 ALC 一旦注册即保持到进程退出；MOD disable 只停用 callback。该扩展当前为“仅设计”。

资源导入链当前可用子集：

- 独立 `xphorror.PcModCompat.Resources.dll` + `AssetsTools.NET 3.0.4` 已进入 Android managed runtime assets，但只由后台导入 provider 调用，不进入 native Hook/HUD 热路径；首次编译使用全局可取消串行门禁。
- UnityFS 索引、版本门禁、Proven/UniqueType 绑定、feature groups。
- Jipper r143：candidates=6、groups=5、bindings=8、unsupported=0、compatibility=partial；`AutoLoad=0`、`ControlledLoad=3`、`Rejected=3`。
- compiled cache 现在同时发布 `resource_recipe.bin`、`resource_ir.bin`、`resource_ir_blobs/*.rgba32`、`resource_ir_blobs/*.alpha8`、`resource_ir_blobs/*.tmpfont` 和报告；Resource IR 使用 64-byte envelope、SHA-256、CRC32、stable resource ID、payload 长度/哈希和路径边界双侧校验。`.pccompat/resource_ir_compiler.txt` 最初只记录 `resource-ir-compiler-v4-alpha8-atlas`；当前已升级为 cache 格式、compiler revision、输入 SHA-256 三行 marker，缺失、旧 marker 或输入变化都会强制重编并在全部产物成功后最后原子发布。cache key 使用完整输入 SHA-256，candidate 身份也使用完整 SHA-256。
- resource recipe 在导入工具和 runtime 双侧校验 MOD 身份、candidate/group/binding 引用、平台/版本/策略组合、路径边界和数量上限。
- candidate 复制与旧 `LoadFromFileAsync` 审计路径启动前均核对文件大小和完整 SHA-256；损坏 complete cache 会重建，不能继续命中。该直载路径不再是桌面 bundle 的生产目标。
- 运行时解析并交叉校验 Resource IR 后注册 owner/session/generation 隔离的 VirtualBundle；bundle、asset、payload 和 preferred candidate 均为 O(1) 索引。Jipper 在 Android 请求 Windows 路径时按 recipe 选择 Linux 6000.3.x candidate，而不是把任一桌面 bundle 交给 Unity。
- Android UnityMain 使用既有 64 槽有界队列加载 host capability bundle，并创建转换后的 Texture/Sprite/Material、静态 TMP、capability fallback clone 或受限 PrefabGraph 模板；桌面 `AssetBundleCreateRequest` / `AssetBundleRequest` 直载状态机只保留 debug/audit，不是生产消费链。
- Android MOD 后台任务只执行文件、metadata、资源索引、IL 重写和 rule 编译；`CompleteLoad/CompatSetup`、静态构造及 generated proxy 调用复用同一 UnityMain 队列。Timer/UI 只能请求合并后的 finalization pass，不再直接执行 MOD setup。
- managed self-render 资源等待使用 `Disabled/PendingActivation/Active` 三态 frame gate：rewritten session 的 pending 门禁只确认同一 generation 的 VirtualBundle 已发布，不调用旧 Unity bundle loader；pending 最多 4Hz 进入 CoreCLR，active 才逐帧执行 MOD Update。
- Android 资源队列回调与 managed frame 回调共享零分配、线程局部的 `PcCompatUnityMainExecutionContext`；VirtualBundle 物化和 managed install probe 只接受该 native UnityMain hook scope，不能把普通 CoreCLR worker 误认为主线程。
- `ControlledLoad`/`ForceRequired` 通过 MOD 详情页受控/强制二次确认，会话准入绑定当前 session，reload 后失效；queued completion 通过 generation 防止旧 session 覆盖或卸载新 session。
- 旧 candidate 直载审计路径仍核验文件大小和完整 SHA-256，但不参与 Android 生产对象消费。
- 只有 `Proven` binding 会成为 required Resource IR 资产；`UniqueType` 仅保留审计信息，不能把猜测升级为 required 真值。reload 会提升 generation，旧 generation completion 不能覆盖新 session。
- rewritten MOD 的同步 `AssetBundle.LoadFromFile(string)` 返回 VirtualBundle handle；非泛型及闭合泛型 `LoadAsset`/`LoadAllAssets` 返回真实 generated Unity proxy 或 proxy array，`Unload(bool)` 只释放 owner handle。泛型参数被原样转发给 host bridge，字段/local/signature 在完整用途审计通过后才可从 `AssetBundle` 擦除为 `System.Object`。
- MOD 详情页可显式启动 rewritten session 的 managed self-render 测试；该入口不改变默认门禁，只开放 VirtualBundle/Resource IR 自绘路径，不读取 `STARRAY_PCMOD_RESOURCE_LOAD`、不调用旧 load sink，并会显示 pending/failure 原因和当前 presentation owner。
- `ui_recipe.bin` 按 node/target/group/asset/type 精确请求资源；同 group 多个同类型 asset 不再按类型猜测。bundle 或 asset 完成后会主动刷新已经 materialize 的 graph。
- Native graph 资源解析不会在持有 graph mutex 时回调 managed；ready 结果返回后重新按 bundle 和完整 binding 身份复核，graph 在回调期间 retire/reload 时不会把资源写入旧对象。
- pending/unavailable 结果只设置 waiting bit；后续成功 completion、已加载 cache hit 或新 resource session 会合并排入一次 UnityMain refresh，因此门禁关闭和异步等待阶段没有逐帧 CoreCLR resolver 开销。
- Resource IR 已实际提取 Alpha8、RGBA32、RGB24、ARGB32、BGRA32、RGB565、RGBA4444、DXT1、DXT5，支持 UnityFS inline image data 与 `m_StreamData/.resS`，并保存 Sprite rect/pivot/PPU/border/extrude 和纹理依赖。
- Material 子集按 source Shader 名称映射到 `material.compat.tmp_mobile/ui_default/sprite_default`，复制白名单 float/int/color/texture/scale/offset、keyword、render queue 和 GI 标志；runtime 先验证所有属性存在，再克隆 host base material。未知 Shader、外部引用、tag/pass、未知 active keyword 或超预算属性失败关闭。
- `overlay.font` 的 `MAPLESTORY_OTF_BOLD SDF` 优先进入 `TmpFontFromAtlas`：导入器保存 face、glyph/character 紧凑 payload、atlas、Material 和样式参数；UnityMain 克隆 `font.adofai.korean` 只作为已注册 `ScriptableObject` 外壳，随后覆盖源 MOD 数据并调用 `ReadFontAssetDefinition()`。只有该静态重建不满足门禁时才退回纯 capability clone。`overlay.progress_bar` 不再映射 capability：导入器递归验证父子指针、组件脚本、事件和依赖，输出 4 节点 PrefabGraph；UnityMain 把模板放在 inactive `DontDestroyOnLoad` holder 下，MOD 的原生 `Object.Instantiate` 得到正常脱离 holder 的克隆。
- 普通 UI graph 已可调用 `Image.set_sprite`、`RawImage.set_texture`、`Graphic.set_material`、`TMP_Text.set_font/set_fontSharedMaterial/set_fontMaterial`；Jipper 当前实际生成 13 条 `TextFont` binding。
- session 清理按 Resource IR 依赖图拓扑排序，保证 Prefab -> Sprite/Material -> Texture；无依赖对象才使用稳定类型/序号排序。process-wide host capability bundle 和 shared root asset 保持强引用，不由 MOD `Unload` 接管。
- 同一 candidate 的成功或失败结果按 MOD session 记忆，失败不会在当前 session 反复调用 sink；reload/unload 清理结果并只卸载实际成功加载的 bundle。
- Android 导入时会先复核 `.pccompat/resource_recipe.bin` 的二进制、MOD 身份和引用关系；文件缺失或无效时在 worker 自动重建并原子发布。编译异常不会阻断 Hook/managed rewrite，但会进入 `resourceCompileError` 诊断。
- 诊断页/导出与 `ResourceRecipeTool summary` 只读展示 readiness。
- `PcCompatProbe --recipe-only` 会编译并发布 resource recipe，不进入 AssetBundle 加载。
- 离线脚本 `tools/compile_resource_recipe.ps1` 可一键 compile+summary（默认读取 Info.json Id）。
- 离线脚本 `tools/verify_resource_recipe.ps1` 可 validate+summary。
- 官方 `build_android_single.ps1` Release arm64-v8a 产物位于 `out/android_single`。

### 2026-07-23 静态 TMP 字体深度重建

- Resource IR 新增 `TmpFontFromAtlas` 与 `tmp-font-static-v1` 紧凑 payload。payload 以固定记录保存 glyph metrics/rect/scale/atlas/class definition 和 character unicode/glyph/scale/element type，避免把 Jipper 一万余条记录展开成巨型 JSON；导入端和 runtime 均校验数量、长度、有限数值、重复 identity 与 character-to-glyph 引用。
- AssetsTools.NET 从 Jipper Linux bundle 成功提取 `Maplestory OTF / Bold`、4096x4096 atlas、11617 级别 glyph/character 表、Material、face metrics、atlas 参数和字体样式。atlas/Material 仍沿既有 Resource IR 依赖图物化，不把 Linux bundle 交给 Android Unity。
- UnityMain 使用 generated proxies 重建 `FaceInfo`、`GlyphMetrics`、`GlyphRect`、`Glyph`、`TMP_Character`、IL2CPP `List<T>` 和 `Texture2D[]`；随后覆盖 clone 外壳的 face/material/table/atlas/style 字段并调用 `ReadFontAssetDefinition()`。手机 runtime metadata 不保证暴露离线 `dump.cs` 中的完整参数构造器，因此 TextCore/TMP 重建禁止依赖 `FaceInfo/GlyphMetrics/GlyphRect/Glyph/TMP_Character` 的非必要参数构造器：非 blittable `FaceInfo` 使用默认 wrapper，blittable struct 使用 boxed default + `unbox/stfld` delegate，`Glyph/TMP_Character` 使用 `il2cpp_object_new` 默认分配并通过 metadata 字段 accessor 填满状态。`m_AtlasTexture`、`m_AtlasTextureIndex`、character scale/element type、glyph class definition、float point size 和 clone 遗留 hash 均显式处理。
- Jipper 的源 OpenType feature table 已验证为空；运行时不构造新的 `TMP_FontFeatureTable`，而是保留 capability clone 中已初始化的 table，避免未运行构造器造成内部集合为空或触发 runtime metadata 构造器解析。非空 feature table 仍失败关闭。
- 新增 `UnityEngine.TextCoreFontEngineModule.dll` 代理，当前闭包为 165 个精确输入类型、13 个代理程序集、176 个生成类型、14 个 generic initializer，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。代理审计会拒绝上述参数构造器重新进入 runtime surface；三份 runtime proxy 的 SHA-256 已核对一致。
- bootstrap 的运行时类型验证必须与 dependency-closed proxy surface 同步；`TMP_FontFeatureTable` 从闭包移除后不再作为启动必需类型。三个 generated Unity API 入口统一调用 `RequireReady()`，初始化失败会把 bootstrap 原始异常链写入 managed failure 导出，不再退化为无上下文的 `generated proxies are not ready`。
- 兼容级别固定为 `Compatible`：字形、metrics、atlas 与白名单 Material 属性来自 MOD，但 Shader 使用 Android capability Shader。非空 OpenType feature table 当前失败关闭并退回 capability font；不能将该路径报告为 `Exact`。
- 本机验证：真实 Jipper 字体提取/Alpha8 体积/二进制往返/双解析器/代理契约定向测试 11/11；排除只可在游戏进程执行的 P/Invoke 烟测后全量 managed 330/330；Release arm64 Android 单包构建通过。
- 首次实机重编译暴露了 atlas 内存峰值：4096x4096 Alpha8 被旧通用纹理路径展开成 67,108,864-byte RGBA32，系统在同一时间段报告 `mem-pressure-event`，随后 MIUI Android 13 ART JIT 于 framework DexCache `ClassLinker::DoResolveType` 中 SIGSEGV。`TextureAlpha8` 路径现将该 atlas 保持为 16,777,216-byte 单通道 payload，并在 Android 创建 `TextureFormat.Alpha8`；compiler marker 升为 `resource-ir-compiler-v4-alpha8-atlas`，旧 64 MiB cache 不会复用。

### 2026-07-15 Android capability bundle

- 已在 Unity `6000.3.10f1` 工程新增独立 `BuildPcCompatCapabilityBundle.cs`；原 `BuildScnEditorBundle.cs` 保持只构建 `scnEditor`。
- Bundle 固定为 `pccompat_capabilities_android`，同包编译 Vulkan 和 OpenGLES3，不包含场景。
- 白名单包含真实 TMP Mobile/Overlay/Masking/SSD/Sprite/Bitmap Shader、通用 UI 素材、裁剪后的 Latin/CJK/日文/韩文静态 SDF 字体，以及显式标为 `compatible` 的 AssetRipper ADOFAI 占位 Shader。
- 字体克隆会清除 source font 和 fallback 链并改绑真实 TMP Mobile Shader；能力包从首轮约 42 MB 降为约 468 KB，不携带 40 MB CJK OTF 和整套无关字体。
- Unity 构建输出 Bundle、白名单和双层 manifest；外部 manifest 固定记录文件大小、Bundle/白名单 SHA-256 和内部 manifest SHA-256。
- `build_android_single.ps1` 会结构化解析 JSON、重算 SHA-256 并把三件产物复制到 `assets/runtime/pc_compat_capabilities`；`install_android_overlay.ps1` 逐层强制断言。缺失或哈希不符时完整构建失败，未新增或修改顶层构建参数。
- host capability runtime registry 已接通：Java 发布精确 runtime root，托管层校验外部 manifest/白名单/SHA，UnityMain 加载 host Android bundle、复核内部 TextAsset manifest，并顺序预载 37 个 required root asset；`prefab.compat.progress_bar` 仍作为能力包兼容/回归资产保留，但 Jipper 生产资源编译已不再路由到它。
- registry 使用 immutable stable-ID dictionary，成功后没有逐帧扫描；`AssetBundleRequest.asset` 返回的基类 wrapper 会按已验证 expected type 对同一 IL2CPP pointer 建立具体 generated proxy，避免 TextAsset/TMP/Sprite 的托管 cast 失败。Bundle、manifest、SVC 和 Unity object proxy 进程级强引用；`ShaderVariantCollection` 因 Android metadata 被裁剪而保持 `UnityEngine.Object`，不生成不存在的类型代理。
- owner-aware `LoadCapabilityAsset(stableId, expectedType)` 已进入 managed bridge，只允许 Setup/Enable/Update；它不向 MOD 暴露或转移 host AssetBundle 所有权。
- PC MOD AssetBundle 调用/字段/local 的 VirtualBundle v8 重写已接通同步泛型/非泛型 API；Resource IR 已能重建 Texture/Sprite/白名单 Material、静态 TMP atlas/metrics 和受限 PrefabGraph v1，并保留显式 alias 的 TMP fallback。非空字体 feature table、动态 TMP、超出组件白名单的 prefab、通用 Shader fingerprint 和异步 API 仍未接通。

## 最新实机状态、部分实现与未完成项

### 2026-07-21 managed self-render 实机链路与游戏事件回调派发（已落地）

实机（ADOFAI 3.1.2 r143 + JipperResourcePack）首次完成 managed self-render 端到端观察：

- `GUIUtility.ProcessEvent` native hook -> CoreCLR OnGUI 派发链实机生效；Jipper KeyViewer 由 MOD 自身重写代码创建并经 managed component Update 驱动，证明 UnityMain 帧派发、IMGUI 事件泵、generated proxy 对象创建和 VirtualBundle 物化（字体/ProgressBar prefab）整条链可用。
- `CompatEnable` 全量完成：session Enabled 后 `ManagedPresentationClaimed=true`，recipe graph 按 bundle 退休，不再双画。
- 自绘接管期间兼容层通用 HUD（标题/数据行/T1..TN KeyViewer 文本/进度条）按 ownership 隐藏（`PcCompatModPlugin` 的 Unity HUD 与 ImGui 兜底两个显隐闸门），ownership 归还后自动恢复；自绘 MOD 的屏幕内容只剩 MOD 自己绘制的对象。
- 早间故障（generated delegate 代理缺少 `(Object, IntPtr)` 构造，`DelegateSupport.ConvertDelegate` 在 `Application.quitting +=` 抛 `MissingMethodException`、Enable fault）已随代理闭包重生成修复；mod ALC 的代理程序集统一回落 Default ALC 共享，无跨 ALC 类型分裂。

**游戏事件回调 managed 派发已于同日实现**（审计原断点：JAPatch 注册全部停在 `RegisteredOnly`、postfix 只翻成 native fixed-op、Jipper `Overlay` 唯一激活路径 `Show(floor)` 与全部内容更新永不执行，实机"只剩 KeyViewer"）。三段落地：

1. **导入期**:`PcCompatRecipeCompiler` 只为仍需执行 MOD 托管行为的活跃 Postfix patch 增发 `ManagedEventCallback=21` 规则，签名取自同一 verified 领域目录并与 fixed-op 规则共享 target 记录/同一 hook;rule id 编码 `managed_event:<patchId>:<callbackType>:<callbackMethod>`。Prefix 不派发；descriptor-only callback 已由 native fixed-op 完整消费，也不二次派发；目录外目标（`Awake_Rewind`、`RDC.set_auto`、`scrShowIfDebug.Awake/Update`、`UnityModManager.ModEntry.Load`）进入 `managed_event.*` 审计而不猜测。Jipper r143 当前包含 18 条 managed-event；加入平台 `GameplayAcceptedObserve=22` 后 recipe 总计 49 条规则 / 31 个 target。callback translation 与 recipe cache 格式已升级，旧缓存强制重编译。
2. **native**:12 个手写 dispatcher 透传填充 `raw_args[0..5]`(float/double 存位模式）,`ManagedEventCallback` op 命中后把 `{patchId, instancePtr, raw args}` 推入每 MOD 2048 槽环形队列，并为 Show/Hide 等生命周期事件预留 64 槽；Show/Hide/Reset/StartLoadingScene 到达时先清除尚未消费的普通事件，再作为场景屏障入队。hook 线程只读安装期 per-dispatcher 规则快照 + leaf 锁，零 `g_lock`、零 Unity API、零跨 CoreCLR。事件仅在 MOD 持有 presentation ownership 时入队（`modmanager_pccompat_set_managed_events_enabled` 随 ownership 同步翻转）。
3. **managed**:session 按 `ui_recipe.bin` managed-event 规则 + shim `JAPatcher` 注册表（新增活 `MethodInfo`/delegate target）构建绑定表，`DispatchManagedFrame` 在 MOD Update 前按预算 drain；无参 callback 走 typed `Action`，常见带参 callback、proxy 构造与字段读取使用绑定期编译委托，只有异常方法形态回退 `MethodInfo.Invoke`。参数绑定覆盖按名/按位原始参数 int/bool/enum/float/double 位级转换、代理类型 `(IntPtr)` 构造包壳、`__instance`（含 `object` 形态按目标代理包壳）、`___field` 代理成员读（值/引用字段）、`System.Enum` 经 `modmanager_pccompat_read_boxed_value_info` 恢复装箱枚举（`OnChangeState(States)`)。逐事件异常隔离；单 patch 连续 8 次失败后退避 1 秒，再允许试探调用，成功即清零，避免场景切换中的瞬时错误永久冻结该 callback。descriptor 翻转 `Supported` 供详情页显示。Jipper r143 保留 18 条 managed callback；ResourceChanger 对应 17 个目标的 callback 只执行 descriptor-only native fixed-op。

本机回归：`PcCompatRecipeCompilerTests.EmitsManagedEventRulesForActivePostfixCallbacks` 覆盖 18 条规则身份/签名/共享 target/bin 往返与 reader 恢复，既有编译器断言更新至 49 规则/31 target。实机需复验 Jipper overlay/progress bar/BPM/combo/judgement 自绘，并确认大关卡进入/退出/重入不再形成旧 callback backlog；`Combo.OnHUDTextAwake`、`JStatus.HideDebugText/OnAutoChange`、`Status.OnShowIfDebugAwake` 暂留兼容缺口（目标无目录签名或 Prefix+返回值语义）;coop 专用 `AsUnsafe` 路径维持不支持。

### 2026-07-21（续）shim JALib 语义对齐、ByteTool 移植与 HitMarginsCount 活镜像

事件派发落地后的三轮实机诊断驱动修复（依据诊断导出 `[managed-events]` 段 + 本地真实 JALib/JipperResourcePack 源码对照；参考源 clone 不入库，shim 按真实语义对齐）:

**Round 1 — Feature 注册断链（绑定 5→7)**。根因：shim `Feature.CompatEnable` 只调 `OnEnable()`，从不调 feature 自己的 `Patcher.Patch()`;Feature 子类（Combo/Judgement/Status/ResourceChanger 等）的 JAPatch 永远留在各实例 `_patches`，不进静态注册表 → dispatcher 14× "no shim registration"(MOD 自己的 `Patcher` 走 `JAMod.CompatSetup→Patch()`，所以 Main.* 回调一直能绑定）。按真实 JALib 对齐 shim:

- `Feature` 主构造函数补 `if(patchType != null) Patcher.AddPatch(patchType)`;`Enable()`/`CompatEnable()` 恢复真实顺序：先 `Patcher.Patch()` 后 `OnEnable()`。
- `JAPatcher.patched` 字段改属性（MOD IL 引用 `get_patched`，字段会 MissingField);`Patch()` 幂等防重；**patched 后晚到的 `AddPatch` 立即注册**（覆盖 Feature 在 OnEnable 内补注册的形态）;`AddPatch(Type)` 扫 `Public|NonPublic|Static|Instance` 全方法；新增 `AddPatch(MethodInfo)`、`Unpatch()`、`Dispose()`、`OnFailPatch`。
- `JAPatchAttribute` 补 `(MethodBase, PatchType, bool)` 构造重载；0Harmony shim 补 `GeneralExtensions.Join<T>/FullDescription`（修 TypeLoadException)。

**Round 2 — ByteTool.StreamTool 缺失（绑定 7→18)**。MOD 引用 `JALib.Tools.ByteTool.StreamTool` 全套序列化 API,shim 缺整个命名空间 → TypeLoadException。真实 JALib `Tools/ByteTool/` 11 文件近原样移入 shim（ByteTools 589 行 + StreamTool 187 行 + 9 个 attribute 文件），连带补 `JAMod.Name`、静态 `JAMod.GetMods()` 注册表、`SimpleReflect.Method/Constructor/Members/New` 与 JetBrains.Annotations 最小桩。同期最初新增 `JAPatcher.RegisteredPatchCount` + 60 帧节流的晚注册收编；现已升级为 Harmony `Revision` 优先、旧 registry/JALib 变更计数器回退的当帧检测，Patch/Unpatch/Repatch 和排序变化不再受记录数不变或 60 帧窗口影响。

**Round 3 — HitMarginsCount 恒空（"部分显示" + IndexOutOfRangeException)**。根因链：重写器已把 `VersionSafe.GetHitMarginsCount()` 的**调用点**重定向到 `PcCompatReversePatchBridge.GetHitMarginsCount()`（设备重写 DLL 实证；桩方法本体仍抛 NotSupportedException，只查桩本体会误判），但桥快照的 `HitMarginsCount` 没有任何发布者填充 → `Overlay.Hit` 恒空 → `UpdateJudgement` 读 `hits[9]` 抛 IORE → `Show()` 在 judgement 行中止，后续 Combo/BPM/TimingScale/Attempt 全部跳过 = 实机"部分显示"。修复：

- 桥交出**单一稳定 int[]**（不再 clone——MOD 构造器存一次引用后每次判定重读，必须内容持续更新）;`PublishSnapshot` 保留稳定数组身份。
- Android 桥已改为 native bulk 活镜像：通过 metadata 动态解析 `scrMistakesManager.marginTrackers` 和 `scrMarginTracker.hitMarginsCount`，从当前 player-0 tracker 一次复制至版本化 `PcCompatHitMarginSnapshotV1`。managed 只在 snapshot generation 变化时更新稳定数组；generated proxy 构造、`PropertyInfo.GetValue`、`Il2CppStructArray` indexer 和逐元素 `object[]` 已从帧热路径删除。

**实机数据**：绑定 5→7→18 条回调，帧派发 3→169→226 条事件，overlay 自"不激活"推进到"判定行显示"。**版本核对**:3.1.2 `HitMargin` 枚举 12 值，`OverPress=11` 追加在末尾，0..10 顺序与 Jipper 预期一致——不存在计数槽位错位。**验证教训**：桩方法本体状态 ≠ 调用点状态；ReversePatch 类修复必须核对设备上重写后 DLL 的调用点重定向。

**已知剩余症状（2026-07-21 晚实机报告，调查中）**:①判定计数行固定一处不移动；②重试后计数不清零（初判方向：`scrMarginTracker` 实例随重开重建而 native 实例指针未跟随）；③除 KeyViewer 外功能只在场景加载的内置关卡显示（初判方向：自定义关卡不走 `scnGame.Play`,`Show(floor)` 触发链待查）。

### 2026-07-22 hitMargins 生命周期与 capability 缓存加固（本机完成，待实机复验）

对上述三个症状完成源码级复查后，确认 `scnEditor.Play()` 会直接调用 `customLevel.Play(num)`，最终仍进入已安装的 `scnGame.Play(int,bool)`；因此当前没有增加重复的 editor 专属 Show Hook。真正可复现的断点位于 ReversePatch 数组生命周期：

- `Overlay` 构造器只保存一次 `VersionSafe.GetHitMarginsCount()` 返回引用。旧桥初始为 `Array.Empty<int>()`，后续发现游戏真实长度时会换成新数组，MOD 仍持有旧空引用。
- 新局开始时 native 会清空 tracker 指针，但托管稳定数组保留上一局内容；`Overlay.Show()` 在新 tracker 第一次 `CalculatePercentAcc()` 前立即读取，因此重试会显示旧计数或在空数组上抛 `IndexOutOfRangeException`。
- `AddHit/Reset` 的 after-op 已拿到刚被官方修改过的 tracker 实例，但旧实现只在较晚的 `CalculatePercentAcc` 发布该指针，managed `OnHit` 可能先于活镜像到达。

本轮修复：

- Android 启动时从 generated `HitMargin` enum 得到 12 槽布局，在任何 MOD 取得引用前初始化稳定数组；数组身份一旦公开永不更换，运行时长度变化失败关闭。
- 新会话没有 tracker 时原地 `Array.Clear`，保持 MOD 引用不变；`AddHit/Reset/CalculatePercentAcc` 可使用方法实例作为受限兜底，`SetPlayerCount`/Show 只认静态 `marginTrackers[0]`，不会把 `scrMistakesManager` 错当 tracker。tracker 发布不依赖 recipe Overlay 已可见。
- `SetPlayerCount` 整体替换 tracker 时读取新静态数组；checkpoint 等绕过 `AddHit/Reset` 的直接数组修改由 managed 帧首 native refresh 捕获。snapshot 在 managed event 入队前完成提交，回调不会看到上一拍数组。
- managed-event 诊断导出增加逐 callback `ok/failed/streak/backoff/retryMs` 计数，用于下一轮区分 `scnGame.Play` 未产生事件、回调未绑定和回调内部异常，不增加 Hook 日志。
- capability registry 在返回缓存资产前于 UnityMain 调用 `UnityEngine.Object.op_Implicit` 检查 fake-null；失效资产从 stable-ID 表移除并经现有有界队列单项重载，禁止把陈旧字体/材质代理传给 clone 路径。

判定计数行在普通单人模式下本来就是 Jipper 固定锚点，不会跟随星球移动；若用户所指是“内容冻结”，上述稳定数组断点可以解释。自定义关卡显示与重试计数仍需使用本轮构建实机复验后才能标记解决。

### 2026-07-22 长时间冻结与部分关卡不显示加固（本机完成，待实机复验）

后续实机报告出现“判定文本先正常后永久不更新”以及“部分关卡完全不显示 HUD”。源码回查确认两个独立的确定性状态机缺陷：

- callback 连续失败 8 次后旧实现永久 `Disabled`，而被跳过的 callback 不可能再成功并清零失败计数。当前改为 1 秒退避后试探恢复；持续失败仍限频，瞬时场景/对象错误不会永久冻结本次 MOD 会话。
- 每 MOD 128 条 native managed-event 环满时旧实现覆盖最旧事件，关卡开始的 `scnGame.Play` / `scrPressToStart.ShowText` 往往早于初始化和击打事件，因此可能丢失唯一的 `Show` callback。首轮修复曾预留 8 个生命周期槽；2026-07-22 后续实机仍观测到 748 条累计丢弃，因此固定环扩到 2048 条、生命周期保留区扩到 64 条，继续保持有界内存。
- ownership 尚未启用时，native 环现在保留最后一个 Show/Hide 生命周期事件并在启用时原样回放；ownership 禁用时清空普通排队事件。这样覆盖“MOD 在关卡已经开始后才完成 managed activation”及 ownership 重建，不依赖 native 猜测当前场景或伪造 Jipper 专属 Show 调用。

诊断导出新增 `platformRuntime=frame[...] overlay[...] hitMirror[...]`，包含 frame mode/callback/failure/last-frame age、overlay show/hide/last-op，以及 hit-margin mirror attempt/success/failure/skip/throttled/last-success age/tracker/length/checksum/counts/issue；managed-event 段同时输出逐事件快照成功/失败、最后 generation 与最后非零 counts。它只在用户主动导出时格式化，不增加逐帧 Logcat。2026-07-22 本轮定向生命周期/事件/镜像/loader 测试 `28/28`、全量桌面测试 `255/260`、Android arm64 完整构建、154 类型代理闭包与 165 类型审计均通过。全量 5 个失败均为既有环境/fixture 基线：两个缺本地 shim、一个 Jipper oracle 漂移、一个 native 源码格式断言、一个桌面进程无游戏 IL2CPP 导出；判定文字与部分关卡 Show 仍需 v2 事件构建实机复验。

### 2026-07-22 native 判定快照与 managed 帧热路径优化（本机完成，待实机复验）

- 官方 r143 Android dump 与目标 `libil2cpp.so` 再核对：`scrMarginTracker.hitMarginsCount` 是实例数组字段，`scrMistakesManager.marginTrackers` 是静态 tracker 数组；PC 近源码确认 `SetPlayerCount()` 会替换数组和 tracker 对象，而 `Reset()` 原地清零，checkpoint 回滚可直接改计数数组。
- 新增 96 字节、ABI v1、最多 16 槽的 native 判定快照。字段和静态对象均由运行时 metadata 解析，数组头和长度使用 `il2cpp_array_object_header_size/il2cpp_array_length`；无固定 RVA、VA 或字段偏移。seqlock + 原子字段保证跨 native/managed 读取一致，generation/checksum 不变时 managed 为 O(1) 不拷贝。
- managed frame session 列表改为门禁变化时发布的稳定数组；正常每帧不再锁 `SessionLock`、LINQ 过滤并分配新数组，也不再无条件重算 frame gate。只有激活完成、故障、卸载或连续帧需求变化时重建。
- managed-event binding 预分配并复用反射参数和代理构造参数数组；无参数 `void` callback 使用 `Action` 直调，异常形态回落 `MethodInfo.Invoke`；boxed enum 的 256 字节名称缓冲复用。没有增加 Hook 数量或逐帧日志。
- managed lifecycle 的 `CompatUpdate(float)` 改为专用无分配调用，移除每帧捕获 `deltaTime` 的 lambda/委托；这减少长期运行中的托管 GC 压力，并针对旧导出少量超长 managed frame 尖峰。
- 主动诊断的 `platformRuntime.frame` 新增 `workUs/avgWorkUs/maxWorkUs/over4ms`，在现有帧入口用单次额外 monotonic 采样累计，不逐帧格式化或输出，可用于同设备同关卡 A/B 对照。
- 本机验证：新增/相关定向契约 `6/6`；更新旧 tracker 指针源码契约后全量 `250/255`，5 个失败均为既有环境/fixture 基线；Android Release 全构建通过，arm64 C++ 重新编译链接、154 类型代理闭包、165 类型代理审计零问题。实机需重点验证长局判定计数持续变化、重试清零、编辑器/自定义关卡 Show，以及 managed HUD 开启前后的帧时间和 GC 分配。

后续对 `pccompat_JipperResourcePack_20260722_092117.txt` 的回查又完成以下收紧：

- 导出证明 `Judgement.OnHit` 已成功 887 次、native hit snapshot 成功 25804 次且 generation 到 899，故排除“Hook 已停止”和“官方数组完全不再更新”；同时确认 ring 累计丢弃 748 条、单帧 managed 最大耗时 181.875 ms。
- `AddHit/Reset/CalculatePercentAcc` 现在以刚被官方方法修改的 typed receiver 为权威 tracker；`SetPlayerCount/Show` 才从静态 `marginTrackers[0]` 重新绑定。避免编辑器/重开短窗口里静态数组仍指向旧 tracker 时，把新实例计数覆盖成旧快照。
- managed 判定数组不再每帧强制进入 IL2CPP。判定事件直接携带官方调用出口快照并在对应 callback 前发布；只有事件快照无效时才强制回读。普通 reverse-snapshot 路径按 100 ms 节流，配合 native 100 ms 低频兜底发现 checkpoint 直接数组写入，并且刚收到权威 tracker 事件时不会被静态兜底立即覆盖。旧实机导出的 `hitMirror.attempts` 基本等于帧数，v2 事件路径下应主要剩低频兜底和异常 fallback。
- 2026-07-22 10:36 实机导出进一步证明 `Judgement.OnHit=165`、native generation `170`、`dropped=0`，排除 Hook/队列停摆。根因收敛为延迟 Postfix 的状态时序：旧实现整批 callback 前只发布一次当前数组，同帧较晚 Reset/Hide 可让较早 OnHit 读取未来零值。managed-event v2 现把官方调用出口的 16 槽计数快照嵌入每条 AddHit/Reset/CalculatePercentAcc 事件并逐 callback 发布；导出新增 `hitSnapshots/invalidHitSnapshots`。
- 同轮性能数据为平均 `167 us/frame`（上一版 `168 us`），持续成本没有恶化；尖峰来自首次 `OnGameStart2=41.542 ms`、frame max `183.620 ms` 及若干首次 callback JIT。callback 绑定表现改为 session 构造期在加载线程建立，lifecycle/callback method 与 delegate 尽力 `PrepareMethod/PrepareDelegate`，避免首次击打或开局再承担可前移的 JIT/绑定成本。
- Hook 线程的 managed-event fan-out 改为安装期 immutable `shared_ptr` 快照，不再逐事件持有 `g_managed_events_lock`。空帧 drain 使用按 registry generation 缓存的 thread-local ring 指针，不再锁 `g_lock`、遍历 bundle 或分配临时 vector。
- 带参 callback、IL2CPP proxy `(IntPtr)` 构造和 `___field` 读取均预编译为 delegate；不支持的私有/异常形态才回退反射。诊断增加每 callback `avgUs/maxUs/over2ms`、managed lifecycle 总/最大 Update 时间、native overlay `visible/show/hide/lastOp` 和完整 hit counts。
- 更新后定向测试 `9/9`，新增私有 callback 编译调用实测；全量 `252/257`，5 个失败仍为既有基线。Android arm64 native 与 Android managed Release 均通过。

### 资源运行时消费与 class database

- Unity `6000.3.10f1` class database 尚未打包；无 type tree 的 bundle 目前只能做基础索引并降级。
- 静态 TMP 字体重建与 capability fallback、Texture/Sprite、白名单 Material 和受限 PrefabGraph v1 已进入 VirtualBundle 消费路径；SideImage 的更广动态对象图和超出当前组件白名单的 prefab 尚未完成。
- Linux/Windows/Mac Unity 6000.3.x bundle 只由 AssetsTools.NET 读取；VirtualBundle 按 selected candidate 路由请求，禁止调用 Android Unity 直载桌面 bundle。
- 首版 Android Shader/素材 capability bundle、host registry、纹理解码、Texture/Sprite/受限 Material 重建和 MOD VirtualBundle 同步泛型/非泛型消费已完成；通用 shader semantic matching 尚未完成。
- `ui_recipe.bin` resources section 已承载 graph 消费身份；独立 `resource_recipe.bin` 继续负责 bundle candidate、feature group、asset index 和来源字段证明。diagnostics section 仍为空。

### 纹理、Shader 和 Material

- DXT1/DXT5 与七种未压缩格式到 RGBA32 的首版解码及 payload 缓存已实现；BC4/5/6H/7、ASTC、ETC2/EAC 尚未实现。
- Android shader capability bundle、白名单、variant manifest、runtime 打包、内部 manifest 校验和 stable-ID registry 已接入；MOD source shader 到 capability 的映射尚未接入。
- 字体 Unicode 范围和首批 Material Shader-name/property fingerprint 已实现；ProgressBar 已退出 fingerprint 特判并进入通用 PrefabGraph v1。通用 Shader/Material fingerprint、匹配评分和 `ShaderBindingRecipe` 尚未冻结为 schema。
- Material capability clone、属性存在性门禁、白名单属性/纹理应用和失败销毁已接入 UnityMain；当前只承诺 TMP Mobile、UI/Default、Sprites/Default 三类 base capability。
- 不计划在 Android Player 内通用重编译 DXBC、DXIL、桌面 SPIR-V 或任意 ShaderLab/HLSL。

### 通用 UI 和 KeyViewer

- 动态字符串 snapshot/string-table ABI。
- [done] 版本化 `keyviewer_adapter.json` schema/validator、多 feature/lane group、source profile/lane binding、逐能力证据和 SHA-256/MVID/revision/proxy-surface 失效契约已完成；behavior scanner v5、managed cache v15、诊断 UI/导出、逐 MOD cursor preview 和正式 consumer 已接通。动态 `BindingProvider KeyCode[]` 在 proven `0x1000` transform 下可生成生产 plan；无效 provider 只在唯一可用候选存在时恢复。`keyviewer_overrides.json`、严格指纹/候选校验和手动角色 UI 已完成。一般 CFG/dominance/alias、线程时序和未证明 identity transform 仍保持 fail-closed。
- [partial] Physical/Touch/Synthetic raw 路由、独立 GameplayAccepted、Legacy Unity polling、Win32 `GetAsyncKeyState` held 查询、Rewired `GetButton/GetButtonDown/GetButtonUp` 以及 Android keyboard -> Unity/VK canonical mapper 已接通；Input System 目前只完成 Button/KeyControl 精确候选扫描，整链 lowering、完整 Win32 低位边沿、LogicalAction、Harmony/managed event source profile 仍未完成。
- KeyViewer/SideImage 等动态构造路径的具体 Sprite、Texture、Material 和 prefab binding；基础 ABI 与 setter 已完成。
- Touch 模式已能让原 MOD polling/state machine 驱动其自有槽位、次数、KPS 和 rain；确认的 `LabelProvider` 会只为空白显示项补 `T1..TN`，已有自定义标签不覆盖。通用 lane factory/template 重建仍待一般化。
- 任意 prefab 的 `Instantiate/GetComponent` 图恢复；当前仅有 `overlay.progress_bar` 受限适配器。
- `Transform.childCount` 等可证明运行时循环和动态布局更新。
- 更广泛的批量 Mesh 资源/动画 lowering、解析式动画和 scene identity generation；KeyViewer fallback 的有界单 Mesh rain 已完成。
- per-MOD `ModActor` 串行 mailbox、fault isolation、共享固定 worker 池、单共享 native wake 线程和 8192 槽有界 journal 已完成；deadline/budget、持久化 MOD count journal 和 presentation generation 的统一调度仍待补齐。
- source semantics manifest override 与用户手动 HUD/resource/KeyViewer role override schema。

### Harmony 完整度

- **最新状态（2026-07-27，覆盖下方按施工时间保留的缺口快照）**：shim ABI 为 61/61 类型、871/872 成员；同步 Prefix V2、Prefix/Postfix `__state`、运行时 owner 排序计划和 HookBroker immutable snapshot 拓扑排序已落地。下方“仍缺 `PatchInfo`/`MethodInvoker`/同步 Prefix”及 Prefix v1 的段落仅记录当时的审计过程，不再代表当前状态。

- attribute target 聚合本身已完成，并已用上游 `Harmony/HarmonyTests/*/Assets/*.cs`（约 2000 行真实 patch 类）作语料验证：编译期抓到并补齐两处真实 shim ABI 缺口（`Transpilers` 整类、`CodeInstruction.Call` 表达式重载族 + `SymbolExtensions`），聚合期零缺陷（66 descriptor / 11 issue / 6 issue code，逐条对齐 upstream，含 `Math.Max` 优先级合并与 `PatchAll` 只看类级属性两条反直觉规则）。语料工程在仓库外、`Harmony/` 已 gitignore，缺口固化为 `PcCompatHarmonyTranspilerAbiTests`（7 条）。
- `CodeInstruction.CallClosure` 有意偏离 upstream：静态方法引用照常返回指令，捕获闭包抛 `NotSupportedException`——upstream 靠 `DynamicMethodDefinition` 现场发 IL 携带捕获状态，本宿主没有运行时 IL emission，返回一条丢掉捕获状态的指令是错的。
- `AccessTools` 面已用第二轮语料（`HarmonyTests/Tools/TestAccessTools*.cs` + `Assets/AccessToolsClass.cs`）闭合：补齐 16 个 `"Type:Member"` 重载 + 8 个访问器、`CodeInstruction.Call(string typeColonMethodname, …)`、整类 `AccessToolsExtensions`（56 成员，原先整类缺席）与 20 个零散成员（`TypeSearch`/`Inner`/`FirstXxx`/`GetTypes`/`IsDeclaredMember`/`GetDeclaredMember`/`EnumeratorMoveNext`/`AsyncMoveNext`/`Is*`/`CombinedHashCode` 等）。四处有意偏离见 `HUD_KEYVIEWER_HARMONY_COMPAT.md` §3.3；覆盖测试 `PcCompatHarmonyAccessToolsAbiTests`（18 条）。
- `MethodDelegate`/`HarmonyDelegate` 按形状实现：upstream 六条分支里四条本就是纯 `Delegate.CreateDelegate` / `Activator.CreateInstance(delegateType, instance, functionPointer)`，逐字镜像；**open-instance 非虚调用**与**任何 struct 实例方法**（接收者按 ref 传）是 upstream 唯二发 IL 的形状，抛 `NotSupportedException` 并记诊断。整个成员缺席会让 MOD 程序集 `TypeLoadException` 整体陪葬，抛错只炸单个调用点。
- 仍然缺席的 shim ABI（2026-07-26 对上游 `Harmony/Harmony/**` 做公开面全量 diff 得出，不再依赖语料覆盖）：`AccessTools` 成员级 82 : 78，只差 `FieldRefAccess`/`StaticFieldRefAccess`/`StructFieldRefAccess`（无任何非发 IL 形式）与 `MakeDeepCopy`（依赖 `Traverse`，已于 2026-07-27 补齐，见下条）；但**整类**缺席的有六块——`CodeMatcher`+`CodeMatch`+`Code`（1833 行，~220 个 opcode matcher 类）、`CodeInstructionExtensions`（32 个方法：`Is`/`Calls`/`OperandIs`/`IsLdarg`/`IsLdloc`/`LoadsConstant`/`LoadsField`/`Branches`/`WithLabels`/`WithBlocks`/`MoveLabelsTo`/`ExtractLabels`/`ArgumentIndex` 等）、`CodeInstructionsExtensions.Matches`+`MethodBaseExtensions.HasMethodBody`、`Traverse`/`Traverse<T>`（454 行）、`MethodInvoker`+`FastAccess`+`DelegateTypeFactory`+`RefResult<T>`（443 行）、`PatchInfo`（204 行）。前四块**可以逐字镜像**（纯 `List<CodeInstruction>` 数据操作与纯反射，不发 IL），第五块只能做抛错空壳。缺席代价是整个 MOD 程序集 `TypeLoadException` 而非查找降级。实测 Jipper 对全部缺口 0 引用；JALib 上游 `JAMethodPatcher.cs` 有 38 处 `.WithLabels`/`.WithBlocks`（我们的 JALib shim 是重写 façade，未继承该依赖）。清单见 `HUD_KEYVIEWER_HARMONY_COMPAT.md` §3.3 末尾。
- **2026-07-27 已补齐前三块**（`shims/0Harmony/{CodeMatch,CodeMatcher,CodeInstructionExtensions,Code}.cs`，逐字镜像上游 2.4，零语义妥协，41 条测试）。需要 `ILGenerator` 的成员（`DeclareLocal`/`DefineLabel`/`CreateLabel*`/`InsertBranch*`）**不做任何改写**：上游在 generator 为 null 时本就抛 `InvalidOperationException("Generator must be provided to use this method")`，而本宿主 generator 恒为 null，照抄即得到与真实 Harmony 逐字一致的可观察行为。`Code.cs` 的 ~220 个 opcode matcher 类用 `sed` 从上游源码机械生成（只加 nullable 注解）。测试语料取自上游 `HarmonyTests/Tools/TestCodeMatcher.cs`——上游用 `PatchProcessor.GetOriginalInstructions` 读 IL 拿到那 21 条指令，本宿主读不了 IL，所以手工构造同一序列后逐字沿用上游的位置/长度期望。**剩余缺席**：`Traverse`/`Traverse<T>`（可镜像）、`PatchInfo`（可镜像）、`MethodInvoker` 族与 `FieldRefAccess` 族（只能抛错空壳）。另：原普查表把 `ModifierType` 算作 `CodeMatcher` 族成员是归属错误，它属于 `Internal/InlineSignature.cs:88`，不在缺口内。
- **2026-07-27 同日补齐第四块** `Traverse`/`Traverse<T>`，顺带闭掉 `AccessTools.MakeDeepCopy`（`shims/0Harmony/{Traverse,AccessCache}.cs` + `AccessTools.cs` 追加，19 条测试）。动工前记的那条"`Traverse` 内部很可能用 `FieldRefAccess`"**是错的**：实测 `Traverse.cs` 对 `FieldRefAccess`/`DynamicMethod`/`ILGenerator` 零依赖，全是普通反射，所以 454 行逐字镜像，规范化后与上游 172:172 行对齐、差异只有 nullable 注解。真正需要替换的 IL 依赖只在 `MakeDeepCopy` 一处：泛型集合分支上游用 `MethodInvoker.GetHandler(addOperation)` 拿 `FastInvokeHandler` 调 `Add`，这里改缓存 `MethodInfo` 走 `MethodBase.Invoke`；因为 `Invoke` 会把被调方法的异常包进 `TargetInvocationException` 而发 IL 的版本不会，调用点用 `ExceptionDispatchInfo.Capture(ex.InnerException).Throw()` 拆包重抛以保持可观察行为一致。`AccessCache` 的**负结果缓存**行为（解析不到时把 null 也写进字典并永不重取）一并保留。测试语料重铸自上游 `HarmonyTests/Traverse/` 五个文件。**剩余缺席**：`PatchInfo`（可镜像）、`MethodInvoker` 族与 `FieldRefAccess` 族（只能抛错空壳）。
- 仍无法静态判定、因此一律记 issue 并否决的是：`TargetMethod`/`TargetMethods` 等运行时目标 helper、`[HarmonyPatchAll]` 批量展开、`MethodType.Enumerator`/`Async`（静态 metadata 扫描器读的是 MOD 程序集，目标方法的状态机属性不在其中；**运行时反射路径已能解析托管迭代器的 `MoveNext`**，只有无托管 metadata 的 IL2CPP 目标才留诊断）、无属性名的 indexer getter/setter、`Prepare` 运行时门（当前假设返回 true 并记 issue）、继承而来的类级属性（合并顺序由运行时决定）、MOD 自定义 Harmony 属性子类（构造器 IL 里给 `info` 赋值），以及 InnerPrefix/InnerPostfix 这类 call-site patch。
- **descriptor → HookBroker 的签名来源已于 2026-07-27 打通**。此前目录外目标不可 hook 的根因不是策略保守，是导入期拿不到游戏方法签名：native `validate_method_identity` 严格要求 return type 与逐个 parameter type 精确匹配（fail-closed 的支点），而导入器只读 MOD 程序集——`PcCompatCallbackDomainMappings` 那张手工目录存在的唯一理由就是替每个受支持目标预存人工审计过的签名。既然导入运行在 IL2CPP 已加载的游戏进程内，就可以直接问运行时：新增 native 导出 `modmanager_pccompat_resolve_target_signature`（纯 metadata 读，不分配/不 invoke/不碰 GC，故可在导入 worker 线程调用）+ managed `PcCompatTargetSignatureResolver`（provider 模式，`PcCompatAndroidTargetSignature.Install()` 在 Android host 注册；未注册时导入行为与打通前逐字一致，桌面与测试即此路径）。四道 fail-closed 闸：类型跨多 image 歧义拒绝、同名重载歧义拒绝（提示作者补 argument types）、泛型直接滤掉、宿主答非所问由 managed 一致性校验拒绝；provider 抛异常只降级该目标。通过后 Postfix 规则 `Source` 记 `managed_event:runtime_resolved`，同步 Prefix 规则记 `managed_prefix:runtime_resolved`。覆盖 `PcCompatRuntimeResolvedTargetTests` 与 `PcCompatHarmonySynchronousPrefixTests`；当前格式为 `callback-translation-v8-editor-rabbit-writeback`。
- **同步 Prefix v1 已于 2026-07-27 落地**：`ManagedSynchronousPrefix=23` 在原 hook 线程反向进入 CoreCLR；`void` 继续，`bool false` 跳过 original。支持无参、`__instance`、最多 6 个按值 primitive/enum/proxy 参数；callback 缺失、线程不符或异常时 fail-open，递归深度上限 32。detour 原样转发隐藏 `MethodInfo*`，HookBroker 仍是唯一物理 Hook 所有者。
- **同步 Prefix V2 已于同日覆盖 v1**：96 B 版本化 invocation frame 在 native/managed 间原地传递，dispatcher 在回调后把 primitive/enum `ref/out` 写回真实 C++ 参数；bool/int 返回 dispatcher 接收 primitive/enum `ref __result`，generated proxy accessor 承担 `___field` 读写。`run_original` 在整个跨 MOD snapshot 中共享，按上游 `PrefixAffectsOriginal` 规则只跳过会影响 original 的后续 Prefix。184 B Postfix event 追加 invocation/result 元数据，Prefix/Postfix `__state` 以 invocation id 配对并有界释放。
- **多 Prefix 排序 v1 已落地**：shim registry 的真实 `owner/priority/before/after/registrationIndex` 不再在 loader snapshot 丢失；Android 通过 staging/add/commit 原子发布完整 order plan，native 只在重建 immutable dispatcher snapshot 时做拓扑排序。无依赖节点按 priority 降序、registrationIndex 升序稳定排序；环路按同一稳定键打破，不禁用物理 Hook。steady-state hook 路径不解析字符串、不分配、不加锁。
- 尚缺 `ref/out`、`ref __result`、`__state`、Prefix `___field` 写回、异常回写和完整 Harmony Prefix 短路规则；Postfix 的 priority/before/after 调度也尚未提升到同一排序合同。
- Transpiler、Finalizer、ReversePatch 的受限生产实现。
- 任意反射、任意托管对象图和通用 IL AOT 明确不在当前承诺内。

### 运行时与质量门槛

- universal bridge 的 SIMD 保存范围和大结构体返回策略尚未冻结。
- VM instruction budget、fault ring 容量和 UI 更新频率仍需真机数据确定。
- rewritten oracle 尚未完成 Android 实机生命周期、delegate、Nullable 和集合桥验收。
- 通用 UI graph 当前变更尚未完成 Android 实机回归。
- 仍缺多 MOD 样本矩阵、资源 fuzz、损坏 bundle、压力和长期性能测试。

## 明确非目标

- 不移植完整 UnityModManager。
- 不在生产路径任意执行未经翻译的 PC MOD 托管逻辑。
- 不实现通用 IL AOT 或任意 Harmony transpiler。
- 当前不支持多人模式；已审计循环只保留单玩家语义。
- freeroam 输入和方块不进入兼容层判定模型，继续透传官方流程。
- 当前只维护 `arm64-v8a`。
- 不承诺旧 Unity 2022 MOD 自动加载；当前版本策略以 Unity 6000 为边界。

## 后续实施顺序

### P0：稳定当前 UI graph

1. [done] 提交 `PcCompatUiGraphLowerer` 和配套 managed/native schema 变更。
2. 实机验证 16 节点 Jipper graph、`ContentSizeFitter`、13 条字体 binding、场景切换和反复 reload。
3. 对 presentation history overflow、Unity fake-null 和 clear barrier 做压力回归。

### P1：建立通用资源闭环

1. [done] 新建独立 `xphorror.PcModCompat.Resources.dll`。
2. [done] 接入 `AssetsTools.NET 3.0.4`；Unity `6000.3.10f1` class database 仍待打包。
3. [done] 冻结 `resource_recipe.bin` v1、feature group 和 `ui_recipe.bin` resources section；后者使用 32-byte binding record。
4. [done] 已实现 bundle 索引、candidate 选择、Proven 绑定、feature groups、compiled cache 原子复制、runtime recipe/session plan、UnityMain 有界命令队列、session 级 attempt cache、controlled/forced confirmation，以及 `STARRAY_PCMOD_RESOURCE_LOAD` 显式门控。
5. [superseded] Unity 6 `LoadFromFileAsync` / `LoadAssetAsync`、request rooting 和 session-aware asset cache 已实现，但只证明了调度链；桌面 target bundle 不能作为 Android 生产消费路径。
6. [partial] Resource IR v1、VirtualBundle、同步泛型/非泛型 AssetBundle 重写、Texture/Sprite/受限 Material、静态 TMP atlas/metrics 重建、TMP fallback 和受限 PrefabGraph v1 已完成；下一步扩展 prefab 组件白名单、非空 OpenType feature table/动态 TMP 与异步 API。

### P2：Shader capability 与 Material 重建

1. [done] 制作 Unity `6000.3.10f1` Android/Vulkan/OpenGLES3 Shader 与素材 capability bundle，并接入最终 runtime 强校验。
2. [done] 在 UnityMain 加载 host bundle、校验内部 manifest、预载 required assets，并发布 owner-aware stable-ID bridge。
3. [partial] 已冻结 TMP Mobile、UI/Default、Sprites/Default 的 source shader name/property whitelist；通用 fingerprint 和 `ShaderBindingRecipe` 待完成。
4. [partial] 受限 Material 路径已有 `compatible / unsupported` 和 dropped-property 诊断；`exact` 证明与通用评分待完成。
5. [done] UnityMain 克隆 capability Material、验证并应用白名单属性、绑定转换纹理，失败时销毁半成品。

### P3：动态 UI 与 KeyViewer

1. 实现持久化的逐 feature 后端选择：默认 `ManagedSelfRender`，失败不自动 recipe fallback；`ProvenRecipe/CompatibleFallback` 只能由用户手动开启。
2. [partial] UMM/JALib 原 Unity IMGUI host、owner-scoped Canvas 识别、settings-only fault、Android modal ownership、诊断、UMM/JALib 保存入口及 JALib `Settings.json`/`.bak` 已接通；`mod_settings.schema` 已支持 verified primitive/enum live mirror 与 fallback，两边共享原对象/setter/save 并在原菜单 save/close 后刷新。继续补非 JALib Canvas 的 owner 证明、SettingGUI 精确 label/range/callback trace 和 `package + data overlay` owner-scoped VFS。
3. [done] `keyviewer_adapter.json` v1 schema/validator、行为扫描、managed cache 原子发布、逐能力诊断、observe-only raw-event preview 和正式 consumer 已实现：覆盖多 `KeyViewerFeature/LaneGroup`、Legacy/Rewired/Win32 消费与 Input System 精确候选、lane binding、独立 `visibility/inputActivation` predicate、MOD-owned count/KPS/rain/reset/persistence 语义；preview 使用独立 cursor、共享 drain 和 gap fault，正式 consumer 通过 actor mailbox 执行。
4. [done] ModManager 手动角色绑定 UI 与 `.pccompat/keyviewer_overrides.json` 已实现；override 绑定完整程序集 SHA-256/MVID、schema、revision 和 proxy surface，更新后强制重验，且只能选择扫描候选。
5. [partial] 双 producer Physical/Touch/Synthetic 与 HookBroker GameplayAccepted 已接通；Legacy/Rewired 的受限统一查询 ABI 和轮询事件化已完成，完整边沿按 `sequence/raw_ns` 回放且各 source kind 不互相冒充。Input System 整链 lowering、LogicalAction、完整 Win32 低位边沿和其他事件源仍另行 fail-closed。
6. [partial] per-MOD `ModActor`、共享 worker、native wake 与 8192 槽有界 journal 已完成；继续补 deadline/budget 和可恢复的 MOD-owned count 持久化。同步 Hook 继续留在调用线程，MOD Unity 副作用继续回 UnityMain。
7. [done] Touch consumer 已驱动原 MOD 自绘槽位/count/KPS/rain，确认的 `LabelProvider` 只补空白 `T1..TN`；无自绘能力时的明确 fallback 已提供通用槽位和批量 Mesh rain，并由用户逐 feature 手动开启。一般化 lane factory/template 证明仍待补齐。
8. 加入动态文本 snapshot ABI；[partial] RawImage/Sprite/字体/Material recipe ABI 和受限 PrefabGraph v1 已接通，仍需 KeyViewer/SideImage 的具体 lowering及更广 TMP/动画/组件支持。
9. [done] 为用户显式启用的兼容绘制 fallback 实现通用槽位与有界批量 Mesh rain；默认 ManagedSelfRender 不替换 MOD 原 rain。更复杂动态布局/动画 lowering 仍未承诺。
10. 用输入基准验证 KeyViewer observe-only 不改变游戏输入和判定，并覆盖多 MOD、多 feature、10 指、同帧多边沿、500 ms UnityMain 卡顿和设备模式切换。

### P4：PATCH 与 interop 扩展

1. [partial] 游戏事件回调 managed 派发（2026-07-21 落地）：导入期 `ManagedEventCallback=21` 规则与 `managed_event:<patchId>:<callback>` 身份编码、native 每 MOD 有界事件队列与 raw 参数捕获、UnityMain 帧 drain + 回调绑定表（标量/枚举/装箱枚举/`__instance`/`___field`）；Jipper r143 当前保留 18 条 callback，descriptor-only fixed-op 不二次派发，连同平台 accepted observer 为 49 规则/31 target。2026-07-24 lifecycle boundary 已成为清除旧普通事件的队列屏障；待大关卡进入/退出/重入实机复验后再标 `done`。
2. 扩展受限 CFG/callback bytecode translator。
3. [partial] Harmony target aggregation 已完成（metadata-only、fail-closed、27 条定向测试 + 上游 `HarmonyTests` 真实语料两轮验证），运行时逻辑注册表与诊断导出已接通（`source=shim_harmony_registry`，bootstrap 期注册可保留）；transpiler/表达式 `Call` shim ABI（7 条测试）与 `AccessTools`/`AccessToolsExtensions`/`MethodDelegate` 面（18 条测试）均已补齐。descriptor 接进 HookBroker 的**第一段已于 2026-07-27 落地**：新增 native 导出 `modmanager_pccompat_resolve_target_signature` + managed `PcCompatTargetSignatureResolver`（provider 模式，Android host 注册；未注册时行为与打通前逐字一致），导入期直接从活 IL2CPP metadata 取目录外目标的精确签名，因此 `PcCompatCallbackDomainMappings` 手工目录不再是 descriptor 能否成为 hook 的硬门槛——Postfix 已通，规则 `Source` 记 `managed_event:runtime_resolved`（10 条测试，含 native↔managed 记录布局源码契约）。仍缺**同步 Prefix 桥**（Prefix 决定 original 是否执行，不能延迟到后续 UnityMain 帧，当前目录外 Prefix 一律审计为不支持）与 `ref`/`__result`/`__state` 参数桥；ABI 侧经 2026-07-26 对上游全量 diff 普查出六块**整类**缺席，2026-07-27 已补齐四块——`CodeMatcher`+`CodeMatch`+`Code`（1833 行）、`CodeInstructionExtensions`（32 方法）、`CodeInstructionsExtensions.Matches`+`MethodBaseExtensions.HasMethodBody`（以上 41 条测试）、`Traverse`/`Traverse<T>`+`AccessCache`+`AccessTools.MakeDeepCopy`（454 行，19 条测试），全部逐字镜像。**剩两块**：`MethodInvoker`+`FastAccess`+`DelegateTypeFactory`（443 行）、`PatchInfo`（204 行）。`PatchInfo` 是纯数据容器，可逐字镜像；`MethodInvoker` 族与 `FieldRefAccess` 族无非发 IL 形式，只能做抛错空壳。
4. [partial] rewritten lifecycle、owner-scoped AssetBundle bridge 和按 MOD presentation ownership 已完成本机验证；managed OnGUI、ownership 转移与兼容 HUD 抑制已实机（2026-07-21）；游戏事件回调派发已落地、实机复验后把 generated proxy 逐项提升为生产真源。
5. 先补 managed component 层级查询和常见 custom yield，再加入 FixedUpdate/EndOfFrame phase 与自有字段持久化。
6. 只有实际 MOD 调用流证明 surrogate 不足后，才实现 ModManager-owned `InjectionTypeRegistry` 和 brokered injection infrastructure；不能直接打开上游 ClassInjector。
7. 最后评估 ReversePatch、Finalizer 和受限 Transpiler，不以牺牲 fail-closed 为代价。

### P5：兼容性与发布门槛

1. 建立多 MOD、多个 Unity 6000 bundle 和损坏输入样本矩阵。
2. 完成 60 秒以上 P95/P99、主线程卡顿、reload 和多 Hook 压测。
3. 将所有 `partial`、fallback 和用户强制加载行为纳入 UI 报告。
4. 只有实机链路可重复、无静默 fallback 后，才把对应能力标记为 `supported`。

## 当前验证记录

2026-07-16 审计实际执行：

```text
implementation snapshot: ea54df7 (master)
PcCompat/managed tests: 239/239 passed
  (SkipWindowsNativeTests=true; SkipNativeTestDll=true; PInvokeTests2 filtered)
native_rule_vm_test: passed (host g++ + realtime_event_core)
pccompat_recipe_binary_test: passed for lifecycle fixture and Jipper ui_recipe.bin
ui_recipe_lifecycle_runtime_test: passed (含 OverlayStateChanged visibility 与 bundle presentation gate)
UiRecipeTool fixture/emit/validate: passed
  fixture size=1160
  jipper size=11268, targets=30, rules=30, resources=13, revision=143
build_android_single.ps1 Release arm64-v8a: passed
  libstarray_modmanager.so + assets/runtime
  flattened Il2CppInterop/dnlib source build: passed
  interop proxy closure: 154 exact input types
  interop proxy audit: 12 assemblies, 165 generated types, 0 issues
  target libil2cpp dynsym audit: 241 il2cpp_* exports; all production-used imports present
  ModAssemblyRewriter: v13; managed cache v9；Jipper 全外部 TypeRef、ReversePatch + VirtualBundle + managed component/companion/coroutine IL regression passed
  JAMod.Bootstrap real-DLL LogException(System.Exception) bridge regression: passed
Android 构建链: passed
  stripped SO, HookBroker=true, rewrittenOracleDefault=true
ResourceRecipeTool:
  index jipperresourcepackbundle: 34 assets, Unity 6000.3.10f1, typeTree=true
  compile/summary JipperResourcePack_release:
    candidates=6 groups=5 bindings=8 unsupported=0 compatibility=partial
    autoLoad=0 controlledLoad=3 rejectedOrForced=3 proven=7 uniqueType=1
    runtimeLoadDefault=disabled gate=STARRAY_PCMOD_RESOURCE_LOAD
    resourceIrCompiler=resource-ir-compiler-v4-alpha8-atlas
  validate resource_recipe.bin: passed
  published to <mod>/.pccompat/resource_recipe.bin
PcCompatProbe --recipe-only: ui partial features=6 rules=30 resources=13; resource groups=5 bindings=8 proven=7
Resource IR / VirtualBundle:
  Jipper bundles=6 assets=204 required=7
  reconstructed RGBA32 textures=6 sprites=5 prefab graphs=1 capability references=1
  required: GhostRain, Auto, SideImage, KeyOutline, KeyBackground, ProgressBar, Korean TMP font
  resource_ir.bin + *.rgba32 payload hash validation: passed
  owner/session/generation routing, required fail-closed, release order: passed
  Jipper ProgressBar source PrefabGraph (4 nodes + shared Background Sprite/Texture): import/runtime schema passed
  Android fallback capability ProgressBar serialized graph (3 children + RectTransform/CanvasRenderer/Image): passed
  Jipper TMP Material forced-Proven fixture: shader/property/Alpha8 atlas import + runtime schema passed
```

未过滤的桌面测试还包含一个需要真实 `IL2CPP_LIBRARY_NAME` 导出的 `PInvokeTests2.Test1`；普通 Windows host 不具备该游戏进程导出，因此该测试不属于上述 `239/239` PcCompat 基线。

2026-07-22 工作批次验证：

```text
ReversePatch stable-array/session-clear + capability contract: 8/8 passed
Final tracker/overlay decoupling regression: 4/4 passed
PcCompat filtered desktop suite: 243/247 passed
  4 failures are existing environment/reference-fixture failures outside this batch
  (missing local shim folders, ignored Jipper oracle drift, native source-format assertion)
StArray.ModManager.Android Release build: passed
build_android_single.ps1 Release + RuntimeAssets + RewrittenOracleDefault: passed
  arm64-v8a libstarray_modmanager.so: compiled and linked
  proxy closure: 154 exact input types in 12 assemblies
  proxy audit: 165 generated types, 14 generic initializers, 0 issues
  runtime assets: generated
```

2026-07-23 Adapter consumer / ModActor 工作批次验证：

```text
managed 定向测试: 41/41 passed
  ModAssemblyRewriter + KeyViewer preview/consumer + ModActor
managed 过滤全量: 291/291 passed
  SkipWindowsNativeTests=true; SkipNativeTestDll=true; PInvokeTests2 filtered
realtime_event_core_test: passed
  使用仓库 xphorror.PcModCompat/w64devkit/bin/g++ 从当前源码重建
build_android_single.ps1 Release + RuntimeAssets + RewrittenOracleDefault: passed
  arm64-v8a libstarray_modmanager.so + assets/runtime
  proxy closure: 154 exact input types in 12 assemblies
  proxy audit: 165 generated types, 14 generic initializers, 0 issues
```

2026-07-23 Dynamic Binding / native wake 工作批次验证：

```text
managed KeyViewer 定向测试: 29/29 passed
  behavior scanner v3 + dynamic lowerer + external mapper + native-wake pump + labels
managed 过滤全量: 306/306 passed
  SkipWindowsNativeTests=true; SkipNativeTestDll=true; PInvokeTests2 filtered
realtime_event_core_test: passed
  8192-slot journal + direct sequence lookup + condition-variable wake
build_android_single.ps1 Release + RuntimeAssets + RewrittenOracleDefault: passed
  arm64-v8a libstarray_modmanager.so + assets/runtime
  proxy closure: 154 exact input types in 12 assemblies
  proxy audit: 165 generated types, 14 generic initializers, 0 issues
llvm-readelf dynamic symbol audit: passed
  read_raw_input_events / wait_raw_input_change / interrupt_raw_input_wait exported
```

这些结果只证明本机编译、schema 互操作和离线 translator 行为；通用 UI graph、资源链和 rewritten oracle 仍需 Android 实机验收。

2026-07-23 KeyViewer consumer / proxy final verification:

```text
managed PcCompat filtered suite: 312/312 passed
  SkipWindowsNativeTests=true; SkipNativeTestDll=true; PInvokeTests2 filtered
  cache v14 contract, Auto session freeze, Rewired bridge, identity transforms,
  ModActor/wake, fallback slots and fallback Mesh rain included
native realtime_event_core_test: passed
  rebuilt with w64devkit g++; raw journal, device snapshot, session reset,
  producer switching, condition-variable wake and count checkpoint covered
build_android_single.ps1 Release + RuntimeAssets + RewrittenOracleDefault: passed
  arm64-v8a libstarray_modmanager.so + assets/runtime
  proxy closure: 155 exact input types in 12 assemblies
  proxy audit: 166 generated types, 14 generic initializers, 0 issues
llvm-readelf dynamic symbol audit: passed
  read_external_input_devices / read_raw_input_events / wait_raw_input_change /
  interrupt_raw_input_wait exported from the arm64 SO
```

本批次仍未进行 Android 实机压力验收；Input System 只完成精确 Button/KeyControl 候选扫描，未进入运行期 owner-scoped lowering。

2026-07-23 Jipper KeyViewer 实机失败链修复验证：

```text
captured trace:
  raw events=51, dropped=0, actor completed=51
  Auto resolved External, transitions=0
  selected BindingProvider=GetGhostKeyCode, LaneCollection=GhostKey12
root causes:
  non-alphabetic SOURCE_KEYBOARD devices could set external keyboard flag
  one reused keyCodes local was globally associated with its last GetGhostKeyCode assignment
  Jipper KeyViewer constructor returned when ADOBase.platform was not Windows
managed focused regression: 42/42 passed
managed PcCompat filtered suite: 318/318 passed
real Jipper regression:
  scanner publishes GetKeyCode and GetGhostKeyCode independently
  six-lane ghost/None selection uniquely recovers to GetKeyCode
  KeyViewer constructor contains no ADOBase.platform load after rewrite
  rewriter report contains Assembly-CSharp!ADOBase::platform -> constant:i4:3
build_android_single.ps1 Release + Dex + RuntimeAssets + RewrittenOracleDefault: passed
  javac + d8 classes.dex generated
  arm64-v8a libstarray_modmanager.so + assets/runtime generated
  proxy closure: 155 exact input types in 12 assemblies
  proxy audit: 166 generated types, 14 generic initializers, 0 issues
```

Android managed cache 已升级到 v15，rewriter 格式升级到 v14；更新 runtime 资产后会自动生成新编译目录，不要求手工删除旧 compiled cache。下一轮实机导出应重点检查 `previewFeature` 的 `requested/mode/sessionFrozen/frozenSession/sessionDeviceFlags/sessionModeReason`、`loweringStatus` 和 `transitions`。

2026-07-23 KeyViewer 自动配置与设置入口验证：

```text
captured trace pccompat_JipperResourcePack_20260723_104124.txt:
  previous fixes active: mode=Touch, deviceFlags=None, provider=GetKeyCode,
  consumer identities=6, lowering succeeded
  registration opened at existing journal tail cursor=186 after gameplay;
  events=0/session=0 confirms no post-registration gameplay input was observed
fix:
  first provable import persists recommended Enabled/Auto/10-lane/self-render config
  no manual BindingProvider is required when exactly one runtime candidate is usable
  unique LabelProvider applies T1..TN without manual role confirmation
  normal controls moved to MOD settings and save/apply immediately
  diagnostics export startCursor and current providerTail
managed KeyViewer focused suite: 42/42 passed
managed PcCompat filtered suite: 322/322 passed
build_android_single.ps1 Release + Dex + RuntimeAssets + RewrittenOracleDefault: passed
  javac + d8 classes.dex generated
  arm64-v8a libstarray_modmanager.so + assets/runtime generated
  proxy closure: 155 exact input types in 12 assemblies
  proxy audit: 166 generated types, 14 generic initializers, 0 issues
```

2026-07-23 自绘 consumer registry / fallback Mesh 修复验证：

```text
captured trace pccompat_JipperResourcePack_20260723_110959.txt:
  lowerer plan succeeded, but registry rejected it for missing manual confirmation
  consumerRegistered=False, consumerIdentities=0, transitions=0, rain unavailable
managed KeyViewer focused suite: 45/45 passed
managed PcCompat filtered suite: 324/324 passed
  automatic lower -> registry -> preview -> owned legacy input covered
  inactive-consumer fallback suppression and renderer failure snapshot covered
build_android_single.ps1 Release + Dex + RuntimeAssets + RewrittenOracleDefault: passed
  arm64-v8a SO, DEX and runtime generated
  proxy closure: 155 exact input types in 12 assemblies
  proxy audit: 166 generated types, 14 generic initializers, 0 issues
```

2026-07-24 self-render 切换与 managed callback burst 修复验证：

```text
managed 定向测试: 33/33 passed
  managed-event ordering/budget + JALib MainThread + KeyViewer preview/fallback
managed 过滤全量: 339/339 passed
  SkipWindowsNativeTests=true; SkipNativeTestDll=true; PInvokeTests filtered
build_android_single.ps1 Release + RuntimeAssets + RewrittenOracleDefault: passed
  arm64-v8a libstarray_modmanager.so + assets/runtime generated
  proxy closure: 165 exact input types in 13 assemblies
  proxy audit: 176 generated types, 14 generic initializers, 0 issues
```

本轮尚待 Android 实机验证两项：先开启兼容代绘再启动托管自绘不再出现 presentation 竞态；快速击打时 Jipper `PERFECT COMBO` 连续计数。若仍异常，诊断导出的 `native queued/dropped`、dispatch `nativeDropped/budgetExhaustedFrames`、JALib `pending/failed` 可直接区分 native ring、UnityMain drain 预算和 MOD action queue。

2026-07-25 MOD 原设置菜单主链本机验证：

```text
managed 过滤全量: 387/387 passed
  original settings controller + Canvas surface + JALib Settings.json round-trip included
  unknown setting fields、.bak、schema template、apply/save failure 与 retry covered
build_android_single.ps1 Release + RuntimeAssets + RewrittenOracleDefault: passed
  arm64-v8a libstarray_modmanager.so + runtime assets generated
  proxy closure: 167 exact types in 13 assemblies
  proxy audit: 178 generated types, 14 generic initializers, 0 issues
extra_menu_activity classes6.dex: passed
async_input normal/independent/HookBroker 四种 arm64 -Werror builds: passed
modal native/JNI exports: verified with llvm-readelf
```

首轮实机诊断 `pccompat_JipperResourcePack_20260725_050330.txt` 暴露了 generated proxy 形态差异：closure 明确包含 `Screen.get_width/get_height`、`GUI.get_skin` 与 `GUISkin.get_textField`，但依赖闭包代理不保证为这些 accessor 重建 CLR `PropertyDefinition`。旧设置后端用 `Type.GetProperty`，因此在构造阶段即以 `MissingMemberException: UnityEngine.Screen.width` 退出，原菜单回调、Canvas probe、modal 和 schema 均未启动。当前统一改为优先 property accessor、缺失时按 static/instance、零参数和精确返回类型绑定 `get_*`；method-only 代理回归、四项 closure 合同、managed 全量及 Android 代理审计均已通过。

第二轮实机诊断 `pccompat_JipperResourcePack_20260725_051701.txt` 继续推进到 `GUILayout.BeginVertical`，暴露 generated proxy 同时提供 CLR `GUILayoutOption[]` convenience overload 与 Il2CppInterop array wrapper overload。旧 `SingleOrDefault` 因多个合法匹配直接抛错；同时单个 `_emptyOptions` 来自 `BeginVertical`，却被传给 Button/Toggle/Scroll/GetRect 等全部方法，即使随意选择首项也可能因容器类型不同而失败。当前每个方法按精确前缀签名独立选择 options overload，优先可常驻复用的 `Il2CppReferenceArray`/`Il2CppStructArray`，再回退其它 generic/CLR array；空 options 按实际参数类型缓存，不在控件热调用中重复做 CLR 到 IL2CPP 数组转换。新增动态双重重载回归和最终 Android proxy + runtime 独立 ALC 完整后端构造测试，后者一次覆盖全部构造期绑定。

仍待实机验证：Jipper 原菜单的触摸、文本输入、Feature 展开/关闭/保存和故障后自动 fallback；Android modal 输入所有权、Activity observer 隔离及官方 gameplay getter fail-closed 已实现并通过本机契约/双构建验证，但尚未完成真机触摸穿透、软键盘和 Back 的端到端验收，测试时仍不得只凭“菜单能显示”判定输入隔离正确。

2026-07-24 native presentation fake-null 防崩修复：

```text
captured err.log:
  UnityMain native crash inside libstarray_modmanager.so
  pc=0x1080c4 runtime_invoke
  pc=0x138684 unity_presentation_objects::consume_snapshot + SetActive path
root cause:
  native presentation graph only checked node GameObject before dispatch
  Unity fake-null check unavailable/stale component wrappers could still enter runtime_invoke
fix:
  m_CachedPtr offset unavailable now fails closed instead of treating wrappers as alive
  UnityApi setters/AddComponent/GetTransform/SetParent/TMP/Graphic/Image/RawImage/Canvas setters require live target before runtime_invoke
follow-up fix:
  later err.log still showed BuildId 33eb1a92 at consume_snapshot + SetActive path
  native recipe presentation now treats SetActive operation as intent only
  runtime SetActive(false) destroys graph; SetActive(true)/EnsureGraph rematerializes
  UnityApi no longer resolves or exposes GameObject.SetActive in native presentation path
verification:
  PcCompatNativeHudContractTests + PcCompatManagedEventResilienceContractTests: 33/33 passed
build_android_single.ps1 Release: passed
  arm64-v8a libstarray_modmanager.so relinked, proxy audit 176 generated types, 0 issues
```

### 2026-07-24 大关卡 managed-event backlog 与旧 IL2CPP 实例崩溃修复

实机现象为主界面正常、进入大关卡后 MOD 绘制严重卡顿、退出后绘制冻结、再次进入关卡时 UnityMain native crash。`tombstone_02` 显示 `libil2cpp.so + 0x1f0ec9c` 对失效 receiver 执行 `ldr x0, [x0]`，上层来自 CoreCLR Expression 编译的 generated-proxy delegate。

根因不是 Unity 主画面或判定线程：`ResourceChanger` 三条 callback 已由 descriptor-only native fixed-op 完整执行，但 recipe 又无条件生成 managed-event。`scrFloor.Start` 按地板数瞬间灌满 2048 槽 ring，延迟事件保存裸 `__instance`；退出后 backlog 仍逐帧 drain，场景对象销毁后旧指针被包装成 generated proxy，最终在 IL2CPP getter/runtime helper 崩溃。

修复：

- callback translation 新增 `ManagedDispatchRequired`；descriptor-only 映射设为 false，RecipeCompiler 不再为其生成 managed-event。Jipper 从 21 条降为 18 条，recipe 从 52 条降为 49 条，target 仍为 31 个。
- callback translation cache 升至 `callback-translation-v5-managed-dispatch-policy`，recipe cache 升至 `mvp-recipe-cache-v10-managed-dispatch-policy`，禁止设备继续复用旧 21 条规则。
- lifecycle boundary 入队前清除同 MOD 队列中旧普通事件，再入队 boundary 本身。旧场景 pointer-bearing event 不再越过 Hide/Reset/StartLoadingScene。
- 回归覆盖 descriptor-only 去重、18/49 recipe 计数与 lifecycle queue barrier；managed PcCompat 全量测试为 352/352。
- `build_android_single.ps1 -RewrittenOracleDefault` 完整通过；arm64 SO 已重新链接，13 个代理程序集共 176 个生成类型、14 个 generic initializer，代理审计 0 issue。当前 `libstarray_modmanager.so` Build ID 为 `ba3ba46d6229814a61f9c5703e031442c95210b8`。

仍待实机验证：大关卡进入时 `queued/dropped/budgetExhaustedFrames` 显著下降；退出后 HUD 立即停止追旧事件；重进关卡不再崩溃。一般 managed event 仍保存裸 `__instance`，若后续出现没有 lifecycle boundary 可隔离的长延迟对象事件，需要引入 owner/session-scoped IL2CPP GCHandle，并在 callback 完成或丢弃时显式释放。

### 2026-07-24 managed HUD/KV 10Hz 节流与准备阶段 owner scope 修复

上一轮构建实机确认重进关卡不再崩溃，但关卡内仍卡顿、准备阶段 HUD 停止刷新、退出后 KV 永久冻结。诊断 `pccompat_JipperResourcePack_20260724_114632.txt` 排除了队列过载：`queued=0`、`budgetExhaustedFrames=0`、actor pending 为 0；同时显示 `lastFrameAgeMs=80`，以及 `scnGame.Play -> Main.OnGameStart1` 因 `Managed component access occurred outside an owner-scoped MOD callback` 失败。

根因有两条：

- PresentationSink 用 telemetry 发布的 `Time.frameCount` anchor 合并 managed frame，但该 anchor 位于 `poll_overlay_telemetry()` 的 100ms timeline 分支，并依赖 gameplay `PlayerControl_Update`。因此 active managed lifecycle 实际被压到约 10Hz；准备阶段没有新 anchor，退出 gameplay 后最后一个 frameCount 永久不变，所有 Canvas callback 都被当作重复帧跳过。
- native managed-event drain 调用 MOD Postfix 时没有进入对应 session 的 `PcCompatManagedExecutionContext`。`OnGameStart1 -> Overlay.Show` 首次访问 managed component bridge 即失败，准备阶段 HUD 无法完成 Show。

修复：

- 删除 PresentationSink 对 `ClockAnchorFrameCount`、`read_latest_clock_anchor()` 和 `g_managed_frame_last_frame_count` 的依赖。active 模式随唯一已安装的主/备用 Canvas hook 推进，继续保留 native/managed 重入门禁；只有 pending activation 保留 250ms 限流。
- `TryDispatchManagedCallbacks()` 在构建/执行 callback drain 前进入当前 session `_updateContext`，Postfix 内的 component、resource 和 owner 校验与普通 `CompatUpdate` 使用同一 MOD/generation 身份。
- 两条回归先在旧实现上 `0/2` 失败，修复后 `2/2`；PcCompat 全量 `353/353`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过，代理审计仍为 176 类型、0 issue；新 SO Build ID 为 `cecee5c65c387bcdc0c21ed0bfe8ed39b08e4f30`。

实机验收点：准备阶段 KV/HUD 持续刷新；关卡内刷新频率跟随真实 Canvas opportunity，不再约 100ms 跳一次；退出到菜单后 KV 继续响应输入；`Main.OnGameStart1` 不再出现 owner-scope failure。下一份诊断中 `lastFrameAgeMs` 在持续渲染时应接近实际帧间隔，callback `failed` 应回到 0。

### 2026-07-24 编辑器 Overlay 外部值类型重写修复

诊断 `pccompat_JipperResourcePack_20260724_144142.txt` 证明这次“编辑器播放态只有 KeyViewer、其他 HUD 不显示”不是场景门禁或 Hook 断链：`overlay visible=True`、managed lifecycle 为 `Enabled`、native ring 已启用，但 `scnGame.Play -> Main.OnGameStart1` 持续失败。完整调用链为 `Overlay.Show -> PlayCount.GetMapHash/GetHash -> LevelEvent["planets"] -> BoxUnboxedValue<PlanetCount>`，CLR 最终以 `TypeLoadException: value type mismatch` 拒绝加载 `PlanetCount`。异常发生在 Overlay `GameObject.SetActive(true)` 之前，因此 KV 独立链正常，而其余 Overlay 没有显示。

根因是 managed rewriter 在没有 dnlib resolver 的情况下保留了外部类型的 `Assembly-CSharp` scope，却把 `unbox.any` 后续泛型实参编码成 `ClassSig`。程序集 scope 与 class/value-kind 是两个独立约束；只保住前者仍会生成不可加载 IL。

修复与约束：

- `GetFollowingUnboxType` 继续保留原 `TypeRef` 和 assembly scope，并按 `DefinitionAssembly + FullName` 查询 generated proxy；只有代理中的真实 `TypeDef.IsValueType` 为 true 时才构造 `ValueTypeSig`。未知类型维持原分类，不根据命名空间、类型名或 MOD 身份猜测。
- `CreateBoxUnboxedValueConverter` 直接使用已经确定的 `TypeSig`，不再经同模块 `Importer.Import` 二次改写 class/value-kind。
- 产物级回归枚举所有 `PcCompatAbiBridge.BoxUnboxedValue<T>`，对能在 generated proxy 中唯一定位的每个 `T` 同时核对 assembly scope 与 `IsValueType`。当前 Jipper 样本的外部闭包为 `PlanetCount`、`SpeedType`；`Boolean/Int32/Single` 属于 BCL primitive，不要求 generated proxy。
- `ModAssemblyRewriteApi.FormatVersion` 升为 `xphorror.pcmod-proxy-rewrite.v18-external-valuetype-kind`。Android managed cache key 已有契约保证纳入该版本，因此设备不会复用旧 `fad31f98e35180cf2339d33d` 重写目录，无需加入 Jipper 专属缓存清理。

验证：managed 全量 `365/365` 通过；`build_android_single.ps1 -RewrittenOracleDefault` 完整通过，代理闭包为 13 个程序集、165 个输入类型、176 个生成类型、14 个 generic initializer、0 issue。arm64 SO Build ID 为 `8168abad72e9b7a3a3b4185192d6a7ce306f9603`。

实机复验要求：新诊断中 `Main.OnGameStart1 failed` 不再增长，`dispatchLastError` 不再包含 `PlanetCount` 或 `value type mismatch`，编辑器播放态 Overlay 与 KV 同时显示。

### 2026-07-24 近距离多指 KV held 闪断修复与原始触摸诊断

实机报告为两个以上触点近距离同时按下时，KV 中对应按键只显示极短时间；异步输入开关不影响复现。代码级链路核对确认 AsyncInput 和 OfficialActivity 最终都进入同一个 raw touch journal，但只允许当前 producer 写入；因此异步开关无关不能证明判定链异常，也不能单独定位到 AsyncInput。

本机回归分别覆盖两层：

- `TouchContacts` 下两个相邻坐标使用独立 pointerId/slot，两个 DOWN 后在没有 UP/CANCEL 前持续保持 `held=0b11`；逐个 UP 只释放自己的 lane。native journal 和 managed preview 的 pointer/lane 引用计数在理想事件流下均正常。
- 同一 Unity/VK identity 可由多个 lane/alias 聚合。旧 `ReadConsumerHeld` 按合并后的 down/up ordinal 逐项回放，部分 lane UP 会把 thread-local cursor 暂时置为 false，即使发布态 `current.Held` 仍为 true。现在 aggregate held 为 true 时优先保持 true 并同步当前 ordinals；只有 aggregate 已释放时才逐项回放完整快速点击。该用例修复前失败、修复后通过。

目前仍不能从旧诊断判断实机是否收到了 Android `ACTION_CANCEL`、`FLAG_CANCELED` pointer-up 或 pointerId 重建。为此每个 MOD 增加固定 16 槽原始触摸诊断环，只在导出时格式化，不写逐事件 logcat。新诊断包含：

```text
previewRawTouch=down=...|up=...|cancel=...|tail=...
previewRawTouchEvent=sequence=...|origin=...|phase=...|pointerId=...|slot=...|pointerCount=...|androidFlags=0x...|edgeMask=0x...|x=...|y=...
```

其中 `androidFlags=0x20` 可识别 Android `MotionEvent.FLAG_CANCELED`。若复现窗口内出现真实 `Cancel` 或带取消标志的 UP，则 KV 短闪来自上游触控流，官方输入和 HOLD/持续按压语义也可能受影响；若原始 tail 只有 DOWN 且无释放，问题仍在兼容消费/绘制层。此次没有修改 native 输入、AsyncInput 队列或官方判定状态。

验证：新增近距离双 pointer、共享 identity 部分释放和原始 flags 保留回归；managed 全量 `367/367` 通过，`build_android_single.ps1 -RewrittenOracleDefault` 完整通过，13 个代理程序集、176 个生成类型、0 issue。

实机诊断 `pccompat_JipperResourcePack_20260724_162508.txt` 已给出决定性上游证据：

```text
previewRawTouch=down=310|up=157|cancel=48|tail=16
previewActorRegistered=True|faulted=False|pending=0|highWatermark=1|accepted=487|completed=487|rejected=0
previewRegistered=True|events=517|dropped=0|origin=OfficialActivity
previewFeature=...|held=0x0|unmapped=0|consumerActive=True
```

actor、journal 和 consumer 均无丢包或背压。`310 - 157 = 153` 个没有对应 UP 的触点由 48 次全局 `ACTION_CANCEL` 释放，平均每次约 `3.19` 个触点。实机关闭小米三指截屏后的 A/B 已确认根因就是该系统手势：手势识别会取消整组 pointer。`origin=OfficialActivity` 证明在 AsyncInput 关闭时，Activity 已从 Android 收到这些取消事件；同一个事件随后仍会进入 Unity，所以问题不局限于 KV，HOLD/持续按压也可能被释放。不能简单忽略 Cancel，否则真正失焦或手势取消后会留下永久 held。

导出尾部 16 条事件已经被复现后的普通点击覆盖，不能据此断言 Cancel 本身的 flags。尾部事件均带 `androidFlags=0x80800`；其中 AOSP `MotionEvent.FLAG_IS_ACCESSIBILITY_EVENT` 为 `0x800`，说明当前触摸流经过无障碍事件路径，`0x80000` 的设备/vendor 含义尚未证明，不作推断。

为保留下次复现的决定性上下文，诊断增加独立的最后一次 Cancel 快照：固定保存 Cancel 前 8 条、Cancel 本身和后 8 条触摸事件，不受滚动 tail 覆盖。导出新增：

```text
previewRawTouch=...|cancelContext=...
previewRawTouchCancelEvent=sequence=...|phase=...|pointerCount=...|androidFlags=...|...
```

该增强只观察和导出原始事件，不修改 Activity 转发、AsyncInput、官方输入、判定或 Cancel 释放语义。已确认的使用约束是：需要稳定三指及以上同时触摸时，应关闭系统三指截屏；应用侧继续尊重 Android Cancel，不加入会制造 stuck-held 的规避逻辑。

验证：最后一次 Cancel 上下文在后续 20 条触摸事件覆盖滚动 tail 后仍保留前后各 8 条事件；managed `368/368` 通过。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过，代理仍为 13 个程序集、176 个生成类型、0 issue。

### 2026-07-25 原 MOD 设置重复打开状态机修复

实机表现为：MOD 已加载时点击设置无反应；卸载后兼容设置可打开；重新加载后原菜单只可打开一次，关闭后无法再次打开。问题不是 JALib/UMM 的 `CompatCloseGUI()` 非幂等，而是设置路由错误依赖 HUD self-render 状态：

- `TryOpenOriginalSettings()` 在 `Loaded` session 上会隐式请求 managed self-render，设置入口因此受资源激活和 presentation ownership 影响；之前的 self-render activation failure 还会永久拒绝设置请求。
- 旧 `DispatchManagedOnGUI()` 只选择 `Enabled` session，导致 `Loaded + Opening` 永远没有机会执行原 `CompatOpenGUI/CompatOnGUI`；仅放宽 OnGUI 筛选后，实机仍可能因 `GUIUtility.ProcessEvent` opportunity 未到或被重入门禁跳过而停在 `Opening`。
- 原菜单打开后 ModManager overlay 被隐藏，但 hidden-render predicate 没有包含 external settings route。原菜单自行关闭后，`Closed` 无人轮询，route/modal 状态不能清理，overlay 也不会恢复。

修复后设置 surface 是独立状态机：打开设置不请求 self-render、不取得 HUD presentation ownership。controller 将 `Opening/Save/Close/schema/Canvas visibility` 放到常驻 UnityMain frame lane，下一次 frame 即可执行 `CompatOpenGUI` 并发布 `Open`；只有真正的 Unity IMGUI draw 留在 `GUIUtility.ProcessEvent` OnGUI lane。两条 lane 共享同一请求位和锁，谁先到都只执行一次；settings-only frame 不执行 MOD `CompatUpdate`、managed callback 或 component OnGUI。每次 request 和状态/Surface 转换只输出一条限量 Logcat 诊断。OnGUI/frame 派发前后比较总需求并即时更新 native gate；external route 存续期间隐藏的 ImGui renderer 继续轮询，消费 `Closed/Faulted` 后恢复 ModManager、释放 modal input 并清除 route。MOD 被卸载或 surface 消失时也会恢复此前被隐藏的 overlay。

回归覆盖 controller 与真实 managed session 的 `Open -> Close -> Open`、UnityMain frame 打开/关闭不执行 IMGUI draw、`Loaded` lifecycle 下不触发 activation/presentation、以及 hidden external route 的关闭恢复和再次建立。定向测试为 `19/19`，PcCompat managed 全量为 `387/387`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过：依赖闭包为 13 个程序集/167 个精确类型，最终代理为 178 个类型、14 个 generic initializer、0 issue。此次行为修复只改变 managed 资产；native `ninja` 无工作，SO Build ID 仍为 `429cc0cd32457f988bde6a6383f6413f38db1a65`。最终 `assets/runtime/StArray.ModManager.dll` SHA-256 为 `3391ED4D1EAC574FF3EEE4BE4C87E8C8548F8C33B12D329EA694D57BBB38EBBA`，并已同步到 Gradle runtime assets；实机请求日志必须包含 `revision=settings-frame-lane-v2`，否则运行的仍是旧 managed 资产。

### 2026-07-25 原 MOD 设置 IMGUI 上下文修复

最新实机现象已经证明设置状态机和 ModManager route 正常：请求能从 `Opening` 进入 `Open`，overlay 随后按设计隐藏，但 Jipper 原菜单没有任何绘制。断点因此位于 native Unity IMGUI presentation hook，而不是 settings lifecycle、fallback 或输入 modal。

旧实现存在两个同时成立的错误：

- 手机 Unity 6000.3.10f1 dump 中真实入口是 `Void GUIUtility.ProcessEvent(Int32, IntPtr, out Boolean)`，旧 hook 却按 `int(int, void*, MethodInfo*)` 解析和调用，返回类型错误且遗漏 `out Boolean*`，会破坏 ARM64 continuation ABI。
- 旧 replacement 在调用 original `ProcessEvent` 之前派发 managed OnGUI；此时 Unity 尚未执行 `BeginGUI`，`Event.current`、GUIClip 和 GUILayout 上下文均不保证有效。把派发移到 `ProcessEvent` 返回后同样错误，因为 GUI 上下文可能已由 `EndGUI` 清理。

修复使用两个 metadata 动态解析、HookBroker 永久安装的 slot。`ProcessEvent` replacement 只维护 thread-local event depth 和 pending 标记，并以 `void(int, void*, bool*, MethodInfo*)` 原样转发；同一事件首次进入 `BeginGUI(Int32, Int32, Int32)` 时先调用 `void(int, int, int, MethodInfo*)` original，再清除 pending 并派发一次 managed OnGUI。安装顺序固定为 `BeginGUI -> ProcessEvent -> publish g_ongui_hook`；若第二步失败，已安装的 `BeginGUI` 因没有 ProcessEvent depth 而保持无副作用，不需要也不允许 unhook。未使用 RVA/VA、direct Dobby 或逐帧日志。

验证结果：双 hook ABI/上下文/安装顺序定向合同 `2/2` 通过；按项目标准过滤 Windows native Hook 与 PInvoke 环境测试后 managed 全量 `392/392` 通过。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过，代理闭包为 13 个程序集/167 个精确类型，最终代理为 178 个类型、14 个 generic initializer、0 issue。新 arm64 SO Build ID 为 `2fe4d47fa0d3e0bfd4c3707acab9702e3ed15d62`；managed 行为未改，runtime `StArray.ModManager.dll` SHA-256 仍为 `3391ED4D1EAC574FF3EEE4BE4C87E8C8548F8C33B12D329EA694D57BBB38EBBA`。

待实机验证：点击 Jipper 设置后 ModManager overlay 隐藏，原菜单应在当前 Unity IMGUI event 中显示并可接收触摸；关闭后 route 回到 `Closed`、overlay 恢复，随后可再次打开。若仍无显示，应导出 settings failure/route snapshot；不要回退到兼容设置代绘，也不要重新改动已经证明正常的 `Opening -> Open` 状态机。

### 2026-07-25 原 MOD 设置 IMGUI 相邻阶段与独立门禁修复

上一版实机行为完全不变，证明正确 ABI 本身不是当前无绘制的充分修复。对同版 `libil2cpp.so` 的 RVA `0x44f7358..0x44f75d4` 做窄范围 ARM64 反汇编后，已否证“`ProcessEvent` 函数体内部调用 `BeginGUI`”这一假设：真实 `ProcessEvent` 只遍历 UIElements utility 并返回，函数体内没有到 `BeginGUI(0x44f75d4)` 的调用。`BeginGUI` 是 Unity 原生事件泵在 `ProcessEvent` 返回后进入的相邻阶段。因此旧 thread-local depth/pending 会在 `ProcessEvent` 返回时立即清空，后续 `BeginGUI` 永远无法派发 managed OnGUI。

同一次回查还确认第二个独立断点：settings frame lane 把状态推进到 `Open + UnityImGui` 后，`Settings.RequiresFrameDispatch` 变为 false。旧 `UpdateManagedFrameGate()` 只把 frame-demand session 放进 `s_managedFrameSessions`，native `dispatch_managed_ongui()` 又复用 `g_managed_frame_mode`；结果是原菜单刚进入 Open 就被同时移出 managed OnGUI 遍历数组并关闭 native gate。只有 HUD/KeyViewer 偶然保持 frame active 时才可能掩盖该错误。

当前修复：

- `ProcessEvent` 每次命中只递增 thread-local event generation，不在返回时清理；后续首次 `BeginGUI` original 返回后与 last-dispatched generation 比较并消费一次。重复 `BeginGUI` 不会重复 draw，没有 ProcessEvent 的孤立 BeginGUI 不派发。
- `s_managedOnGUISessions` 与 `s_managedFrameSessions` 分开原子发布；`DispatchManagedOnGUI()` 只读前者。`RegisterManagedOnGUIGateSink` 单独控制 `modmanager_pccompat_set_managed_ongui_enabled`，native 不再使用 frame mode 判断 OnGUI。菜单在 draw 内关闭时比较 draw 前后的 OnGUI demand，立即刷新 snapshot 并关闭 gate。
- presentation sink 诊断 ABI 增至 V4，同时保留 V1/V2/V3。诊断导出新增 `onGuiHook/onGuiProcessHook/onGuiBeginHook/onGuiEnabled/onGuiProcess/onGuiBegin/onGuiDispatch`，不增加逐事件 Logcat。下一轮可直接区分 hook 未安装、入口未命中、gate 被关闭和 managed callback 已派发四类断点。

验证结果：设置/门禁定向测试 `8/8`，按项目标准过滤 Windows native Hook 与 PInvoke 环境测试后 managed 全量 `393/393`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过：13 个代理程序集、167 个精确输入类型、178 个生成类型、14 个 generic initializer、0 issue。arm64 SO Build ID 为 `e9874b66dab784bc895d488df3b256dfd78a9b66`，已确认导出 `modmanager_pccompat_set_managed_ongui_callback`、`modmanager_pccompat_set_managed_ongui_enabled` 和 `modmanager_pccompat_read_presentation_sink_stats`。runtime SHA-256：`StArray.ModManager.dll=CEDBA05D0BC6E2895E61A9D79E22E4A6794CFD71FF316E88EE16DB37B6B6F5DA`，`StArray.ModManager.Android.dll=E37511441A51B1B3040726E53ECB5BC29FC784808A6FDB7A1A00670E2972C4BE`。

### 2026-07-25 原 MOD 设置 BeginGUI host 修正

`pccompat_JipperResourcePack_20260725_100238.txt` 已把失败链闭合：`onGuiBegin=8350`、`onGuiProcess=0`、`onGuiDispatch=0`。场景中一直存在可借用的真实 OnGUI host；错误是把并不经过导出 `GUIUtility.ProcessEvent` 的 Android player 路径当成必要事件边界，导致所有 BeginGUI 都被 generation 门禁拒绝。settings 随后在 3 秒无有效 draw 后按设计 fault，并非 MOD 菜单 callback 自身异常。

同一诊断也证伪了 arm64 injected host：注册在进入 Unity 对象创建前失败，摘要为 `TargetInvocationException`。源码回查确认上游 `InjectorHelpers` 只识别 `GameAssembly.*`，并使用 Iced/x86-x64 xref 解析 `GenericMethod::GetMethod`、`MetadataCache`、`Class::FromIl2CppType` 等内部函数；不能只补 `libil2cpp.so` 模块名后继续在 arm64 猜地址。因此当前修复为：

- Android arm64 明确不尝试上游 ClassInjector；诊断写 `arm64 upstream unsupported`，不再输出被截断的反射异常。动态 injected host 代码只保留给未来具备正确内部解析器的平台。
- native gate 打开时锁定首个完成 original `BeginGUI` 的 `instance_id`；同一 host 后续每次真实 OnGUI event 派发一次 managed 菜单，其余 host 跳过。锁定 host 连续 250ms 不出现时才重选，覆盖 scene/host 生命周期变化。
- 只借用 `useGUILayout!=0` 的 host；Jipper 原菜单使用 `GUILayout`，不能在禁用布局的 OnGUI 上下文中执行。
- `ProcessEvent` hook 降为可选遥测。它解析或安装失败不再使 BeginGUI host 不可用；`ProcessEvent=0` 在 Android 是允许状态。
- gate 关闭时 BeginGUI 热路径只做原子读，不跨入 CoreCLR。打开时 callback 仍经过 managed/native 双重重入门禁和 settings session 快照。
- `Opening -> Open` 仍要求首次 `_draw/CompatOnGUI` 成功，ModManager 不能在没有真实 draw 证据时隐藏 overlay 或取得 modal 输入。

本机定向合同覆盖 original `BeginGUI` 先于派发、`useGUILayout` host 过滤、稳定 instance 选择、250ms 重选、ProcessEvent 解析/安装非必要和 arm64 ClassInjector 门禁；定向合同 `25/25` 通过。排除已知只适用于 Windows 原生 IL2CPP DLL 的 `PInvokeTests2.Test1` 后，managed 全量 `394/394` 通过；不排除时结果为 `394 passed / 1 environment failure`，唯一失败是 Windows 测试环境没有 `il2cpp_domain_get`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过：13 个代理程序集、167 个精确输入类型、178 个生成类型、14 个 generic initializer、0 issue。迁移报告字段改为 `classInjection=arm64_upstream_unsupported_not_attempted`。最终 arm64 SO Build ID 为 `c186af30d5d70984e2bbb85a8364347305298633`，SHA-256 为 `A073AB899E3A643428435232390446B6EDD4F438588271D04E458E9861297F7A`；runtime `StArray.ModManager.Android.dll` SHA-256 为 `DC9DB523CDFF64FAB9FBD7CFC84F367EE993DEFCD12F72F4A6B61DBB821F9188`，`StArray.ModManager.dll` SHA-256 为 `10425B6F1B85B79839477E2C1047BC254120673D6251D01675F04FB77ED5F20E`。

仍待实机验证菜单显示、触摸、关闭和再次打开。成功诊断允许 `onGuiProcess=0`，但必须看到 `onGuiBegin` 持续增长、`onGuiDispatch>0` 且 `managedSettingsState=Open`；若 `onGuiDispatch` 仍为 0，应继续排查 BeginGUI host 选择或 native gate，不回退已证明正常的 settings lifecycle。

### 2026-07-25 Android 裁剪 IMGUI convenience overload 桥接

`pccompat_JipperResourcePack_20260725_103817.txt` 证明 BeginGUI host 修复已经生效：`onGuiBegin=4042`、`onGuiDispatch=1`。首次真实 MOD draw 随后在 generated `UnityEngine.GUILayout` 静态构造器失败：手机 IL2CPP 不存在 PC MOD 引用的 `Button(Texture, GUIStyle, GUILayoutOption[])`。手机 3.1.2 dump 的 `GUILayout` 只保留 `Button(String, GUILayoutOption[])`，同时还裁剪了 `Button(String, GUIStyle, GUILayoutOption[])` 和 `TextArea(String, GUILayoutOption[])`。旧 ProxySurfaceScanner 只验证 Android 类型是否存在，没有验证具体方法，因此把三个 PC convenience overload 错误放入代理静态构造器。

当前实现不伪造 Android metadata，也不为 Jipper 写专属分支：

- 三个缺失 overload 被登记为通用 managed IMGUI bridge 所有；自动扫描和手工 surface 都禁止它们进入 generated proxy。
- MOD 重写把对应 callsite 改写到 `PcCompatManagedImGuiBridge`。文字/图片按钮通过手机真实存在的 `GUIContent` 与私有 `GUILayout.DoButton` 重建，保留原 `GUIStyle` 和图片。
- `TextArea` 通过 `GUILayoutUtility.GetRect`、`GUIUtility.GetControlID`、`GUI.DoTextField(multiline=true)` 和 `GUIContent.get_text` 重建，不退化成单行输入。
- bridge backend 按 generated proxy assembly 使用 `ConditionalWeakTable` 缓存，不阻止 collectible MOD ALC 卸载；同时兼容 CLR `GUILayoutOption[]` 与 `Il2CppReferenceArray` 双 overload，并优先无转换的 CLR 数组。
- managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v20-imgui-convenience-bridge`，旧 Jipper 重写缓存不会复用。

验证结果：bridge ownership/重写、最终代理成员和独立 collectible ALC backend 绑定合同均通过；排除已知 Windows-only `PInvokeTests2.Test1` 后 managed 全量 `398/398`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过，闭包为 13 个程序集/170 个精确类型，最终代理为 181 个类型、14 个 generic initializer、0 issue。SO 未变化，SHA-256 仍为 `A073AB899E3A643428435232390446B6EDD4F438588271D04E458E9861297F7A`；runtime `StArray.ModManager.dll` SHA-256 为 `E7EBC915A2331C14939C34C29499815DE87379DC47E2884ECAFAC31DE1E24065`，`StArray.ModManager.Android.dll` 为 `9C937DF19E501D317E7FEDF35466504CEB786D543B00977620D7483E9162285A`，`pc_compat_proxies/UnityEngine.IMGUIModule.dll` 为 `7EC19DA918F834ACF5AFDC81984F16089CF5A08CDE0AF8204539FA3CE79BA8F9`。

下一轮实机必须更新整个 runtime，而不是只替换 SO。预期首次打开设置时 `managedSettingsState=Open`、`onGuiDispatch` 持续增长，且不再出现 `GUILayout..cctor` 的 `MissingMethodException`；随后验证 style button、图片 button、KeyViewer 文本多行输入、关闭和再次打开。

### 2026-07-25 presentation graph 新建对象跨帧失去 GC root 修正

`err.log` 中旧 arm64 SO（Build ID `c186af30d5d70984e2bbb85a8364347305298633`）在 UnityMain 触发 `SIGSEGV`。tombstone 的直接链为 `pccompat_metadata::runtime_invoke -> unity_presentation_objects::consume_snapshot_range -> libil2cpp -> libunity`；使用同版本未剥离 SO 符号化后，兼容层内部链收敛为 `ensure_materialized -> materialize_graph_step -> build_node_step -> UnityApi::get_transform`。崩溃发生在 presentation graph 创建 HUD `GameObject` 后读取其 `transform`，不是 IMGUI convenience bridge、MOD lifecycle 或 Hook ABI 问题。

根因是 materialization 原本把 `CreateObject`、`CreateHandle` 和 `GetTransform` 分成三个可让出帧预算的 step。`CreateObject` 返回的 IL2CPP wrapper 在第一次 yield 前只保存在 C++ 裸指针中，尚未成为 IL2CPP GC root；CoreCLR/runtime 加载扩大该窗口后，对象可在下一帧 `GetTransform` 前被回收，随后失效 receiver 经 `runtime_invoke` 进入 `libunity` 崩溃。当前删除独立 `CreateHandle` step，并强制 `create_game_object -> root_graph_object` 在同一 `CreateObject` step、第一次 yield 之前完成。进一步审计确认 `RectTransform`、`Canvas`、`CanvasScaler`、`ContentSizeFitter`、`Image`、`RawImage`、TMP Text 和 `CanvasRenderer` wrapper 同样会存入 `NodeRuntime` 跨帧使用；所有返回点现均在下一次 Unity 调用或 yield 前立即建立并登记 GC handle。后续所有分帧 Unity 对象构造都必须遵守“构造成功后同帧建立 GC root”的硬约束。

新增回归合同 `PresentationMaterializationRootsPersistentObjectsBeforeYielding`，验证 GameObject handle 早于 `CreateObject` 分支首个 `return true`，覆盖所有持久组件 wrapper 的 root 调用，并禁止重新引入 `NodeBuildStep::CreateHandle`。定向合同 `1/1`、排除已知 Windows-only `PInvokeTests2.Test1` 后 managed 全量 `399/399` 通过。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过并实际重编 `unity_presentation_objects.cpp.o`：13 个代理程序集、170 个精确输入类型、181 个生成类型、14 个 generic initializer、0 issue。新 arm64 SO Build ID 为 `9e8a63b22a973435f020acb87667eb5150142108`，SHA-256 为 `D5505EA49EFC448F83CF46783D871C7AC5D8CF652C5DA1192CC1DFB28E0645B4`；runtime `StArray.ModManager.dll` SHA-256 为 `E7EBC915A2331C14939C34C29499815DE87379DC47E2884ECAFAC31DE1E24065`，`StArray.ModManager.Android.dll` 为 `9C937DF19E501D317E7FEDF35466504CEB786D543B00977620D7483E9162285A`，`pc_compat_proxies/UnityEngine.IMGUIModule.dll` 为 `438932EE87F18AA7D8043AFA3ED8F1174271810A207FC59B482A3FF1ED401571`。

### 2026-07-25 Loaded-only 原设置菜单可空实例 callback 修正

`pccompat_JipperResourcePack_20260725_115445.txt` 的原菜单失败已精确定位：settings session 在 `managedLifecycleState=Loaded` 下独立打开，Jipper `Main.OnGUI()` 随后计算 `Overlay.Instance.UpdateSize` 方法组；HUD `OnEnable` 未运行，因此 `Overlay.Instance == null`，CLR 在进入允许 `Action?` 的 `SettingGUI.AddSettingSliderFloat` 之前就由 `System.Action::.ctor` 抛出 `ArgumentException: Delegate to an instance method cannot have null 'this'`。这不是 BeginGUI host、modal 输入、IMGUI convenience bridge 或 native GC root 回归。

设置与 HUD activation 继续保持独立：打开菜单不得暗中执行 MOD `OnEnable`、切换 presentation ownership 或启动 self-render。导入重写器新增可配置的 `ManagedOptionalDelegateRewriteSpec`，生产规则只匹配 `JALib.Tools.SettingGUI.AddSetting*` 的最后一个 `System.Action` 参数，并且只改写直接流入该参数的实例 `ldftn/ldvirtftn + Action::.ctor`。改写后的 `PcCompatManagedSettingsDelegateBridge.CreateOptionalAction` 在 receiver 为空时返回 `null Action`，receiver 存在时按 receiver、method handle 和 declaring type handle 创建并缓存原 Action。`ldvirtftn` 的 receiver `dup` 会同步消除；静态 callback、Task/JATask continuation、事件和其他非设置 delegate 不进入此规则。

managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v21-nullable-settings-delegate`，旧 v20 Jipper 产物不会复用。回归覆盖真实 Jipper `Main.OnGUI`、`Status.OnGUI` virtual callback、非设置 continuation 保持原样，以及 bridge 的 null/live/cache 行为；新增定向合同 `3/3`，排除已知 Windows-only `PInvokeTests2.Test1` 后 managed 全量 `402/402`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过，代理闭包仍为 13 个程序集/170 个精确输入类型，最终代理 181 类型、14 个 generic initializer、0 issue。native 未变化，SO Build ID 仍为 `9e8a63b22a973435f020acb87667eb5150142108`；runtime `StArray.ModManager.dll` SHA-256 为 `36ECC18DE863A5F26ECB7C9658385B9BAD3E5B5D58CD08511266890DA83361AD`，`StArray.ModManager.Android.dll` 为 `215C4EBE0475D84C76B4CAAD37883521F835A704C81DC62B3894393533808513`，`ModAssemblyRewriter.dll` 为 `6ADFC621DD2A21D59C9F6EDEAEE74F46DD8B21B18CAA079E758379CB327F97E5`。实机必须同步完整 `assets/runtime`，只替换 SO 或两个主 managed DLL 都不足以带入新 rewriter。

同一诊断另有 `presentationSink graphs=0/1 graphFailures=1`，但导出 ABI 只包含计数，现有 logcat 缓冲也没有保留 `StArray.PresentationObjects` 的精确 `presentation graph materialization failed ... error=...` 行。该观测与本次 settings 异常链独立，尚未归因；若更新后仍为非零，必须以该 TAG 的首次错误继续定位，不能从计数猜测阶段。

### 2026-07-25 Unity 6 GUIStyle 旧 setter 兼容桥

`pccompat_JipperResourcePack_20260725_122431.txt` 证明 v21 与上一轮修复已生效：`rewriteCacheHit=False`、rewrite 指令由 311 增至 338，null receiver delegate 异常不再出现。新失败发生在 generated `UnityEngine.GUIStyle..cctor`，它急切解析 PC surface 的 `set_fixedWidth(Single)`，而手机 Unity 6000.3.10f1 metadata 中 `fixedWidth` 只有 getter。举一反三核对同一代理静态构造器和 Jipper 对象初始化器后确认，PC MOD 同时使用了手机裁剪的 `set_normal(GUIStyleState)` 与 `set_margin(RectOffset)`；若只修 fixedWidth，后两者会依次成为下一断点。

三者按 Unity 6 真实能力分别重建，不使用 Jipper 类型判断，也不静默丢弃样式：

- `libunity.so` 字符串表确认存在 `UnityEngine.GUIStyle::set_fixedWidth_Injected`。Android bridge 通过 `il2cpp_object_get_class`、`GetIl2CppField("m_Ptr")` 与 `il2cpp_field_get_value` 获取 GUIStyle native pointer，再由 `il2cpp_resolve_icall` 动态解析并缓存 setter；不硬编码字段偏移、RVA、VA 或函数地址。
- Unity 6 `GUIStyle.normal` 为 getter-backed `GUIStyleState`。shared bridge 读取 PC 新状态的 `textColor`，写入 `style.normal.textColor`，保持 Jipper 当前实际使用的语义。
- Unity 6 `GUIStyle.margin` 为 getter-backed `RectOffset`。shared bridge复制源对象的 left/right/top/bottom 到 `style.margin`，保持四边值。
- 三个旧 setter 标记为 managed-bridge-owned，不再进入生成代理静态构造器；proxy surface 改为手机确实存在的 `get_normal/get_margin`、`GUIStyleState.get/set_textColor` 与 RectOffset 四边 getter/setter。其他 GUIStyle API不受影响。

managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v22-unity6-guistyle-bridge`，确保 v21 Jipper 产物失效。定向合同覆盖真实 Jipper 三个 setter callsite、scanner ownership、最终代理成员、shared backend 绑定和 Android metadata/icall 无硬编码约束；排除已知 Windows-only `PInvokeTests2.Test1` 后 managed 全量 `405/405`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过，闭包仍为 13 个程序集/170 个精确输入类型，最终代理 181 类型、14 个 generic initializer、0 issue。native 未变化，SO Build ID 仍为 `9e8a63b22a973435f020acb87667eb5150142108`。runtime SHA-256：`StArray.ModManager.dll=AADCFC543C1156D2D5D5EBE1A44F4B12940E92FBCDB57725E067D8000D3EE514`，`StArray.ModManager.Android.dll=F17C99EFDDBD1F0BCC5AC92B73DCEDE42B856F1BD7410A2A20BBD6EADD039995`，`ModAssemblyRewriter.dll=6ADFC621DD2A21D59C9F6EDEAEE74F46DD8B21B18CAA079E758379CB327F97E5`，`UnityEngine.IMGUIModule.dll=13767D37CCA921310FD52CC5D1676B8A79CD6CD672E0E4044014CEE26426EBF8`，`UnityEngine.CoreModule.dll=DA222098FBBCC80FF51D6AAC190A45B7BAD720142A2A7C2F16AB2A8ABC236A41`。实机必须同步完整 `assets/runtime`。

该诊断仍有独立的 `presentationSink graphs=0/1 graphFailures=1`，与 settings `GUIStyle..cctor` 链无调用关系；本轮不把它误报为已修复。

### 2026-07-25 原 MOD 设置菜单移动端适配

真机已能进入 Jipper 原 Unity IMGUI 菜单，但首版 host 只把 JALib `SettingGUI` 映射为最小控件：slider 退化为单文本框、enum 退化为循环按钮，Feature 还错误地默认全部展开；同时 shim 的 `JALocalization` 从未读取 MOD 包内 `localization/*.json`，因此 `credit.button` 等 key 会原样显示。该问题属于原菜单 host 的呈现和 JALib API 语义缺口，不是 fallback schema、modal 输入、HUD self-render 或 HookBroker 问题。

当前通用修复不包含 Jipper 专属菜单：

- `JALocalization` 在 `CompatSetup` 时从 owner MOD 路径加载当前文化对应的 JSON，缺失时依次回退 English、Korean；字典仍对未知 key 原样回退，诊断附带实际加载路径。运行期保持离线，不访问 JALib 的 Google Sheet。
- Feature 设置呈现不再错误依赖 gameplay `CompatEnable`。MOD 仅处于 `Loaded` 时也会完整列出全部 Feature；`hostActive` 只控制 patch/lifecycle/update，不控制设置可见性。Feature 同时恢复原 JALib 的首屏折叠语义和 `CanEnable/_canExpand` 区分，展开后才运行原 `OnGUI`。单个 Feature 的 `OnGUI` 异常按原 JALib 语义隔离并限次折叠，不再让后续 Feature 全部消失。
- `PcCompatSettingsUiBridge` 新增专用 `Number`、`SliderNumber`、`Enum` 与 `Section` 协议。float/int/long/double、slider、toggle-int 和 enum 均保留原字段写回、callback 与 `Settings.json` 保存入口；普通 number 保持同行 label + text field，slider 保持 horizontal slider + text field，enum 保持同行全部选项按钮，不再把不同 JALib API 压成同一种近似控件。
- Unity IMGUI host 使用屏幕短边计算 24..38 字号、52..76 控件高度和内边距；竖屏接近全宽，横屏限制内容宽度并居中。标题与顶部关闭按钮位于滚动区外，底部保存/关闭栏固定在滚动区外，长 Feature 内容只滚动中段。
- host 只在当前 MOD 设置 `BeginFrame/EndFrame` 期间临时调整 `label/textField/textArea/button/toggle` skin，异常清理也会逆序恢复字号、word-wrap 与 padding，不污染游戏和其他 MOD 的 IMGUI。

managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v23-mobile-settings-ui`。新增回归覆盖包内 English fallback、Loaded-only 下 8 个 Feature 首屏完整分组、slider/enum 专用协议及保存 callback。定向设置测试 `7/7`、代理/设置合同 `38/38`、排除 Windows-only P/Invoke 环境测试后 managed 全量 `408/408`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过：13 个代理程序集、170 个精确类型、181 个生成类型、14 个 generic initializer、0 issue。native 未变化，SO Build ID 仍为 `9e8a63b22a973435f020acb87667eb5150142108`；runtime SHA-256：`StArray.ModManager.dll=6132EFC6EC10DE512B97E7D1E16C8CA2DC2DD97D73864091D66FEE1930C35124`、`StArray.ModManager.Android.dll=12A5521F63DD5EF37425B94B88693C0850EE00682E984E97DD781200E9A98A6A`、`pc_compat_shims/JALib.dll=4DD894D42AFFAA46599B5C0FEAB0C64CC89232D52AB47E0092E646CEAD5F116B`、`pc_compat_shims/UnityModManager.dll=88E6384C454F1F25C1EE3B620975C637BFD41496F42A56739DF648B6E4428E22`、`pc_compat_proxies/UnityEngine.IMGUIModule.dll=7A4C4B8FC248421519C362F84ECDFA6E0B6F2EA89CE7027CCDD5898F62FAA5CA`。实机必须同步完整 `out/android_single/assets/runtime`；只替换 SO、主 DLL 或单个 shim 都不会得到完整结果。

### 2026-07-25 原设置菜单本地化字体与物理密度修正

实机发现 v23 只修改 `GUIStyle.fontSize`，没有给 `GUI.skin.font` 和各控件 `GUIStyle.font` 绑定游戏字体；中文、日文、韩文因此仍可能使用缺字或尺寸不合适的 Unity 默认 IMGUI 字体。旧字号又只按屏幕宽度计算，在 1080x2400、约 395 dpi 的设备上约为 33 px，触控高度仅 72 px。

当前后端在每次设置 frame 开始时调用 `RDString.Setup()`，读取 `RDString.fontData.font/fontScale` 与 `RDString.language`，并在 callback 期间临时写入 `GUI.skin.font` 以及 label、textField、textArea、button、toggle 的字体。字号与触控高度优先使用 `Screen.dpi`；同一设备预期约为 44 px 和 118.5 px。所有字体、字号、word-wrap 和 padding 在正常及异常出口逆序恢复。空字体现在视为明确降级，不再静默假装成功；成功与失败分别输出一次 `[PcModCompat][SettingsFont][INFO/WARN]`。JALib 本地化候选也改为游戏语言优先，再回退 CoreCLR UI culture、English、Korean。

managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v24-localized-settings-font`，确保旧设置产物失效。字体/DPI/代理定向测试 `10/10`、排除 Windows-only P/Invoke 环境测试后 managed 全量 `409/409`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过：13 个代理程序集、172 个精确类型、183 个生成类型、14 个 generic initializer、0 issue；最终生成代理后端构造测试通过。native 未变化，SO Build ID 仍为 `9e8a63b22a973435f020acb87667eb5150142108`，SHA-256 为 `D5505EA49EFC448F83CF46783D871C7AC5D8CF652C5DA1192CC1DFB28E0645B4`。runtime SHA-256：`StArray.ModManager.dll=FCAF8091A877019AE5BF1A8127BB2C0D2131706EEB020E9ABC08797204060EFD`、`StArray.ModManager.Android.dll=61BA6A90E3FBB39077493F5039277CF4A6092482CBDAB7D725F2432C6B0A1B46`、`pc_compat_shims/JALib.dll=459B41AC7F06F0A075F1327D7A12FA3D1B43CB8CDFB124151AA8874862691ED2`、`pc_compat_proxies/Assembly-CSharp.dll=2B287463F8F5FBE42F4426328311FF76DBFBFF3F698657EFB59E4A5B84F246BB`、`pc_compat_proxies/UnityEngine.IMGUIModule.dll=E0D5F6CDC59CA53C338FC7F47D432C18EE41CB54025B343005B7225B23A57FB5`、`pc_compat_proxies/UnityEngine.TextRenderingModule.dll=ECCAEDC1B2336F26A3260883E2BB4034D3A9742386951AE3BA90481AF84CD004`。实机必须同步完整 `out/android_single/assets/runtime`。

### 2026-07-25 MOD 自定义 IMGUI 样式移动端缩放

进一步源码审计确认，Jipper 的 KeyViewer 和颜色设置展开项会临时创建自定义 `GUIStyle`，硬编码 `fontSize=15`、`fixedWidth=10` 和桌面像素 margin。这些对象不是 `GUI.skin` 的默认样式，v24 即使正确加载官方字体，局部展开图标仍会保持过小字体和极窄布局。

当前通用 managed IMGUI bridge 新增 settings-frame 局部缩放上下文。MOD 的 `GUIStyle.set_fontSize` callsite 被改写到 `SetFontSize`；现有 `set_fixedWidth` 和 `set_margin` bridge 同时消费物理密度。缩放上下文只在 `BeginFrame` 后进入并在所有正常/异常清理出口恢复，非设置 IMGUI 和 gameplay HUD 保持 MOD 原值。生成代理仍保留真实 Android `GUIStyle.set_fontSize` 给宿主反射调用，bridge ownership 不会误删该 API。

managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v25-mobile-custom-imgui-style`。局部样式和重写定向测试 `8/8`、排除 Windows-only P/Invoke 环境测试后 managed 全量 `410/410`，最终生成代理构造测试通过。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过：13 个代理程序集、172 个精确类型、183 个生成类型、14 个 generic initializer、0 issue。native 未变化，SO Build ID 仍为 `9e8a63b22a973435f020acb87667eb5150142108`，SHA-256 为 `D5505EA49EFC448F83CF46783D871C7AC5D8CF652C5DA1192CC1DFB28E0645B4`。runtime SHA-256：`StArray.ModManager.dll=75CB4CE98C0F3459834E11B763CE5379CBBBF887DE53B62EB0CC1DCA4B4A9F97`、`StArray.ModManager.Android.dll=EEC308CAC53308CA3731A613A4E466522DE215206A57754D562F39CFF5148786`、`pc_compat_shims/JALib.dll=459B41AC7F06F0A075F1327D7A12FA3D1B43CB8CDFB124151AA8874862691ED2`、`pc_compat_proxies/Assembly-CSharp.dll=25FE41975AC65CC49AF5A94ECD36131BB121EC63CA58AAAECD07BE879E9453B8`、`pc_compat_proxies/UnityEngine.IMGUIModule.dll=11AA1071485CFF73F7711A2B5E5DF2BF24697F017F6346F0163898113C65BE88`、`pc_compat_proxies/UnityEngine.TextRenderingModule.dll=5897045B61F71EBE6B361705E4891230B33414F39C2B4744C4F8820C3D73D7B7`。实机必须同步完整 `out/android_single/assets/runtime`；v25 会自动拒绝旧 managed rewrite cache。

### 2026-07-25 设置 Canvas 误认、重开与离线本地化修正

源码级全链路回查确认，加载后的 Jipper 已存在 owner-scoped HUD Canvas。旧 `TryClaimCanvasSurface()` 使用“不是 baseline **或** owner 匹配”，因此打开设置时会把打开前已可见的 HUD Canvas 直接认作 MOD 自建设置页。controller 随即发布 `Open + UnityCanvas`、跳过 `CompatOnGUI()`，ModManager 又按 Open 状态隐藏自身，形成“叠加层消失但原菜单完全不渲染”。字体、DPI 和 JALib Feature 修复在这条错误路径上都不会执行。

当前 Canvas claim 改为“必须不在打开前 visible baseline；已知 owner 时还必须属于 owner/子层级”。打开前已可见的 HUD Canvas 一律排除，原 JALib 菜单继续进入真实 `UnityImGui` draw。设置 callback 一次异常进入 `Faulted` 后已完成 close/release 清理，下一次用户显式打开现在允许重置为 `Opening`；持续错误仍会再次 fault 并回到 fallback，不再永久要求重启 APP。ModManager 侧同时修复了“未加载时先展开 fallback，加载后第一次点击只折叠旧状态”的残留路由。

本地 `JALib` 对照确认上游先读 `localization/<SystemLanguage>.json`，再按 `JAModInfo.Gid` 联网刷新 Google Sheet 缓存。兼容层保留 `Gid` 到 manifest 和诊断，但遵守离线菜单合同，只消费包内缓存并按游戏语言、CoreCLR culture、English、Korean 回退；字典改为不可变快照，避免 OnGUI 读取与未来导入缓存发布发生并发修改。真实 `JipperResourcePack_release` 闭环已证明实际 `Main.OnGUI` 提交 `Size`，随后提交全部 8 个 Feature 分组，并能从包内 English 缓存得到 `Status` 与 `Key Viewer`，因此不是 Jipper lifecycle 漏调。

诊断导出新增 `managedSettingsPresentation`，包含最后一次 frame 的 width/height、DPI、language、fontResolved、fontScale、fontSize、touchHeight 和 panelWidth；配合 `managedSettingsSurfaceKind` 可直接区分 Canvas 误认、IMGUI 未进入和字体解析失败。managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v26-settings-surface-recovery`，旧 v25 产物不会复用。

回归覆盖 existing-owned HUD Canvas 排除、新 owner Canvas 允许 claim、无关 Canvas 拒绝、settings fault 后显式重试、fallback-before-load 路由替换、`JAModInfo.Gid` 与真实 Jipper 全菜单闭环。排除 Windows-only P/Invoke 环境测试后 managed 全量 `415/415` 通过。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过：13 个代理程序集、172 个精确类型、183 个生成类型、14 个 generic initializer、0 issue。native 未变化，SO Build ID 仍为 `9e8a63b22a973435f020acb87667eb5150142108`，SHA-256 为 `D5505EA49EFC448F83CF46783D871C7AC5D8CF652C5DA1192CC1DFB28E0645B4`。runtime SHA-256：`StArray.ModManager.dll=F1C21F13A71034630C9D7B387F51DBEEB1817C4BE0F6E7E6BB989AFE5A0DB0A0`、`StArray.ModManager.Android.dll=ED29CAF6C34337CC5C07D1970EABA9F5E8399970A5552846AD1CC201BEFBDE34`、`pc_compat_shims/JALib.dll=68CECFE0CDD9BA86334F6AEEDC89E760F3ECBD87F02257B0FF4B3E09924A2962`、`pc_compat_proxies/Assembly-CSharp.dll=FCE9705F715456A7F2B1E482439E8B0C5E9D249B7CA74288BABD1D03F91DAD65`。实机必须同步完整 `out/android_single/assets/runtime`；v26 会自动拒绝旧 managed rewrite cache。

### 2026-07-26 原设置菜单逻辑坐标缩放

实机诊断证明原菜单执行链完整：`Layout/Repaint/MouseDown/MouseUp` 均进入，Jipper 提交 `8 section / 11 button / 8 toggle / 1 text`，屏幕为 `2400x1080`、panel 为 `1350x1025`，官方本地化字体存在且 style 回读 `fontSize=50`。决定性异常是 GUILayout 仍给 `Main`、`X`、`Status` 分配 `29x20.5`、`21x20.5`、`52x17.5` 的 PC 默认矩形；只放大 skin 字号和 padding 不会建立移动端布局坐标，大字体被约 20 px 高的矩形裁掉，形成“只看到一个输入框和少量按钮”。

当前后端改为单一 `GUI.matrix` DPI 缩放。以 440 DPI 横屏为例，物理 `2400x1080` 转为约 `872.7x392.7` 的逻辑 viewport，设置 skin 使用逻辑 `18 px` 字体和 `48 px` touch height，再由 `2.75x` 矩阵统一映射到物理屏幕。MOD 自定义 `GUIStyle` bridge 在该 frame 内保持 `1x`，不再与矩阵二次放大。矩阵通过 generated `GUI.set_matrix`、`Matrix4x4.TRS/op_Multiply` 与原矩阵组合；正常、BeginFrame 部分失败和 EndFrame/布局清理失败均恢复原值并清除 active 状态。

回归覆盖逻辑 metrics、method-only setter、proxy surface、矩阵成功/异常恢复和最终 generated proxy ALC 构造。settings controller 定向测试 `18/18`，排除已知 Windows-only `PInvokeTests2.Test1` 后 managed 全量 `419/419`。`build_android_single.ps1 -RewrittenOracleDefault` 完整通过：13 个代理程序集、175 个精确输入类型、186 个生成类型、14 个 generic initializer、0 issue；最终代理绑定与矩阵异常恢复复测 `2/2`。native 未变化，SO Build ID 为 `ce7cf5fe2fe49a63ed27fa56c5fb48eb0dd17d28`，SHA-256 为 `1E7BBECDF9D5E8B65CF287C12AE5D724221A1687C6ABA9EA0C1BEDEF988F8B36`。runtime SHA-256：`StArray.ModManager.dll=65B3AE566EC8DC06B0F7D28343318D95F04C5BB3D6D09F38E33B518490CE39D7`、`StArray.ModManager.Android.dll=20AC9B45ED3942E04EF5B982370C135434F6F92D73FF31ED606D5BCEF62AF4CE`、`pc_compat_proxies/UnityEngine.IMGUIModule.dll=1EA910BC5270C01DE9AC6FD20E5220266DF0F7FC015EF0B54BC081BF5A0FEACB`、`pc_compat_proxies/UnityEngine.CoreModule.dll=FBD741272D542CA5E59B67F124B6F2BB1D66D1A7E4DED5E175FDDC6B167F8958`。实机必须同步完整 `out/android_single/assets/runtime`；只替换主 DLL 或单个代理不会带入完整结果。

### 2026-07-26 JALib 原菜单控件语义重建

继续对照本地 `JALib/JALib/Core/Feature.cs`、`JALib/JALib/Tools/SettingGUI.cs` 与 Jipper 各 Feature 的真实 `OnGUI()` 后确认，上一版虽然修复了逻辑坐标，但 JALib shim 仍改变了上游绘制结构：`AddSettingSliderFloat/Int` 被降成普通 number，Feature 展开体缺少 `24 px` 缩进与 `12 px` 尾间距，enum 也没有保持同行全部选项按钮。真实调用链应为 `JAMod.OnGUI0 -> Main.OnGUI -> Feature.OnGUI0 -> Main.OnGUIBehind`，其中 slider 是 `Label + HorizontalSlider + TextField`，Feature 是独立 expand/enable 行和嵌套 body；Jipper 本身的 direct `GUILayout` 调用仍由 MOD 原 callback 执行，不由 schema 重画。

当前通用修复恢复该结构：slider 走真实 Unity 6 `GUILayoutUtility.GetRect + GUIUtility.GetControlID + GUI.Slider`，普通 number 只保留同行输入框；enum 同行绘制所有候选，skin 支持 rich text 时保持上游 `<b>` 选中态，否则使用可读的方括号标记；Feature body 使用 `BeginHorizontal -> Space(24) -> BeginVertical` 和逆序关闭，末尾恢复 `Space(12)`。展开箭头改为固定移动端逻辑矩形上的 `GUI.Toggle(Rect, Boolean, GUIContent, GUIStyle)`，避免默认 label style 抢占整行。`GUIContent` 按文本缓存，不在每帧重复创建 IL2CPP wrapper。

同时修复异常收尾的 LIFO 错误：旧 `EndFrame()` 可能在 section body 仍打开时先关闭 scroll，导致本次异常污染后续 Layout/Repaint。现在统一按 `section vertical -> section horizontal -> scroll -> root vertical -> area` 清理；任一 close 抛错仍继续关闭后续层级，最后再透传首个异常。回归覆盖正常和 `EndVertical` 失败两种路径，均证明三层状态被清空且调用顺序不变。

定向 JALib/设置/代理合同 `49/49`、排除已知 Windows-only `PInvokeTests2` 后 managed 全量 `421/421` 通过。`build_android_single.ps1 -Configuration Release -RewrittenOracleDefault` 完整通过：13 个代理程序集、175 个精确输入类型、186 个生成类型、14 个 generic initializer、0 issue。native 无变化，SO Build ID 仍为 `ce7cf5fe2fe49a63ed27fa56c5fb48eb0dd17d28`，SHA-256 为 `1E7BBECDF9D5E8B65CF287C12AE5D724221A1687C6ABA9EA0C1BEDEF988F8B36`。runtime SHA-256：`StArray.ModManager.dll=44B3AA27FCC89CB4959A49A583767686C0F35E9C883C1B6CBC93C6E0167404E4`、`StArray.ModManager.Android.dll=F2E188415888F011CD819F9D75411494C6F48D31F213E60626E4EF2C00FE6749`、`pc_compat_shims/JALib.dll=96AA28AA6E15B69667314A2AF61732D0EE7E63D021E53C78E27D4E0BE533787F`、`pc_compat_shims/UnityModManager.dll=332D1D1939AB702592BB3EFEC3265769346C59E4A667D225CDB35A21BA690935`、`pc_compat_proxies/UnityEngine.IMGUIModule.dll=0E8F76414C900B455BAC78BA667FD2849EA666DE124987E6A8FF82C4F91A4A54`。rewrite 规则未变化，managed cache 版本保持 v26；但 shim、host 和代理均已变化，实机仍必须同步完整 `out/android_single/assets/runtime`。

### 2026-07-26 JALib 全面审计与移动设置宿主下一阶段

本地 JALib `1.0.0.44`、Jipper 引用的 `1.0.0.42` 与当前 shim 已完成源码/IL surface 对照。真实 JALib 约有 83 个 C#/IL 文件、61 个公开类型和 758 个 public/protected 成员；shim 只有 30 个源码文件、42 个公开类型和约 278 个成员，共有公开类型约 33/61。Jipper 自身的 16 个 JALib 类型和 58 个归一化成员名已全部存在，但这只能证明 Jipper ABI 闭包，不能代表通用 JALib。当前通用核心语义约 50-60%，完整文档能力约 35-45%，尚未达到 85%。详细合同与缺口见 `JALIB_SHIM_COMPAT.md`。

同轮代码审计确认键盘反复拉起不是 settings modal 每帧切换。Dear ImGui 在 overlay 隐藏后仍保留文本 ActiveID，诊断已出现 `overlayVisible=False ioWant=True`；Unity settings 后端只读取 `GUIUtility.keyboardControl`，关闭/切换 owner 时从未清除；Java `sKeyboardShown` 又是请求后的推测值，没有以 WindowInsets 校准真实 IME。下一实现采用 `None/ModManager/UnitySettings` 单一 owner，并在 owner 切换时清除旧 UI 焦点。

排版审计发现 `TouchHeight=48` 仅写入 metrics，普通 button/toggle/label 未使用该最小高度；同时 label、textField、textArea、button、toggle 全部被强制 `wordWrap=true`，slider/enum 又固定使用单行，在约 490.91 逻辑像素 panel 中会产生挤压、换行和不一致行高。下一实现必须使用真实最小高度、按控件类型设置 wrap、长行改为上下布局、enum 按宽度分行，并在 Repaint 诊断中检测 clipping/overlap/out-of-panel。

### 2026-07-26 JALib 设置异常帧与类型闭包修正

`pccompat_JipperResourcePack_20260726_025803.txt` 证明字体、DPI、逻辑矩阵和 OnGUI 派发均已正常；新的决定性失败是 Jipper `Combo.OnGUI()` 首次访问 `JALib.Tools.JARandom.Instance` 时抛出 `TypeLoadException`。旧 JALib shim 没有发布该类型，Feature 连续异常四次后又在当前 Repaint 事件内立即折叠，造成下一次 Layout/Repaint 控件结构不一致，最终由 GUILayout 抛出 `Getting control 2's position in a group with only 2 controls`。这不是字体、panel 尺寸、BeginGUI host 或 modal 输入问题。

当前 JALib shim 补齐 Jipper 实际引用的 `JARandom`，并由真实 Jipper -> JALib TypeRef 闭包合同额外发现并补齐 `JALib.Tools.Unsafe.AsUnsafe<T>(object)`。设置异常语义同时改为：Feature 第四次异常只发布待折叠状态，必须等下一次 `Event.type == Layout` 才改变展开结构；关闭菜单清除 pending 和失败计数。根 `JAMod.CompatOnGUI()` 内容异常时调用 `AbortFrame()`，只按 LIFO 清理已打开的 GUILayout/矩阵/skin 状态，不再绘制 footer，也不允许 cleanup 异常覆盖原始 MOD 异常；仅内容完整成功后才调用 `EndFrame()`。

失败基线已分别证明旧实现缺少 `JARandom`、根异常仍调用 `EndFrame`、Feature 第四次异常立即折叠。修复后核心回归 `4/4`、JALib/真实 Jipper rewrite/settings 定向组 `83/83`、排除已知 Windows-only P/Invoke 环境项后的 managed 全量 `425/425`，Jipper -> JALib TypeRef 差集为零。`build_android_single.ps1 -Configuration Release -RewrittenOracleDefault` 完整通过：13 个代理程序集、175 个精确输入类型、186 个生成类型、14 个 generic initializer、0 issue。native 未变化，SO Build ID 为 `ce7cf5fe2fe49a63ed27fa56c5fb48eb0dd17d28`，SHA-256 为 `1E7BBECDF9D5E8B65CF287C12AE5D724221A1687C6ABA9EA0C1BEDEF988F8B36`。runtime SHA-256：`StArray.ModManager.dll=98F8052A2430C793E3189C88D6E52B221B881AEDEEA3F533731E0CFAE2AB8848`、`StArray.ModManager.Android.dll=89D0361971F9245BE742DCEB236FDC61EBC15D036D95A5E47A80E8564D801C9F`、`pc_compat_shims/JALib.dll=9665ADD77F580BD6BE92CB30CDA4EA44F5A817567E2A8EE04D788FE8B8174FFA`、`pc_compat_shims/UnityModManager.dll=24D80C825AD46B2513DC85F1E93C68C507C8BD3A0AC3A6C0CC681EDF021DBF4B`。Gradle runtime 与 `out/android_single/assets/runtime` 的关键文件哈希一致；实机必须同步整个 runtime，不能只替换主 DLL。

### 2026-07-26 JALib 全表 ABI 闭包基线

兼容目标已从“Jipper 当前引用可加载”提升为官方 JALib `1.0.0.42` 与 `1.0.0.44` 公共 API 并集。固定 release assembly manifest 当前要求 61 个类型、872 个成员；最终 shim 实测为 `61/61` 类型（`100%`）和 `871/872` 成员（`99.89%`），candidate SHA-256 为 `DA87772495AAD29BB39E292FA01611146066EAA79CC4D93E891F7283EA70A157`。唯一差异是 `ReversePatchType.AllCombine` 在 v42 为 `127`、v44 为 `255`，单个 CLR enum 字段无法同时发布两个 literal；当前选择 v44 值 `255`，因此 `871/872` 是并集的理论可满足上限。Jipper TypeRef 闭包已完整，但不能再把单一 MOD 闭包视作通用 JALib 完成度。

已完成的高频运行语义包括 UnityMain owner/generation 调度、`Task.Yield`/`JATask` continuation、异步生命周期、受限协程、`Feature/MultiFeature`、永久 HookSlot 上的逻辑 Patch/Unpatch、真实 patch data/query projection、设置序列化/Dispose、反射/Unsafe、ZIP/stream/network 便携工具、JAMod 完整构造/日志/报告 ABI、`SystemLanguage` 与自定义语言重载、异常类型、`ModReloadCache` 和 UMM load event。`JAMod.DownloadComplete` 与 `ModTools.ApplyMod` 明确标为 `ExplicitlyUnavailable`，会持久记录诊断，禁止绕过 PcModCompat 翻译缓存直接加载桌面 DLL。未翻译的 Harmony 行为继续保持 `registered_only/unsupported`，不伪报 active，也不允许 managed 侧物理 unhook。

定向 `PcCompatJALib` 测试 `42/42`；排除已知 Windows-only `PInvokeTests2.Test1` 后 managed 全量 `455/455`。`build_android_single.ps1 -Configuration Release -RewrittenOracleDefault` 完整通过：13 个代理程序集、175 个精确输入类型、186 个生成类型、14 个 generic initializer、0 issue。`Application.version` 与 `SystemLanguage` 均进入最终 `UnityEngine.CoreModule` 闭包，`missingAndroid=0`、`unresolvedMetadata=0`。arm64 SO Build ID 为 `0a2d60d7b926314ef63725d8f38295ce059129f0`，SHA-256 为 `AC22D4BAF328F278E29998C19FE222826A6F4936BE584A2845F8CCE027DEB635`。runtime SHA-256：`StArray.ModManager.dll=B93FE03705233AEFB5240A983E4A2C14B5C52FC18E44CC961F9C57B4AA064683`、`StArray.ModManager.Android.dll=DACEB9BBF5A0B9D526F0EEFAE30806EE0B9DDF41FFC5960ED61E3160804E4CAA`、`pc_compat_shims/JALib.dll=DA87772495AAD29BB39E292FA01611146066EAA79CC4D93E891F7283EA70A157`、`pc_compat_shims/UnityModManager.dll=624F68F6B82650C8319F03E504FAAE7135FDD78A52C465EE9B644C7CB9CE29A2`、generated `UnityEngine.CoreModule.dll=0275160ED44E7AB02818E0BC3B8B6350704306C1C4B72F2239CDCC9FEF937194`。`build_shims.ps1` 现强制运行 `JALibApiManifest verify`：参考版本、并集规模或唯一允许缺口之外的任何变化都会中止构建，完整报告写入 `out/api/JALib-shim-coverage.json`。

### 2026-07-26 MOD VirtualBundle 设置字体投影

JALib/UMM 原设置菜单的字体选择已从固定 `RDString.fontData.font` 改为 owner-scoped VirtualBundle 优先。设置 backend 绑定 MOD id 与 resource session generation；每个 backend 首次绘制只解析一次：唯一精确 `UnityEngine.Font` 优先，否则从唯一静态 `TMPro.TMP_FontAsset` 的 Resource IR 重建 Unity 6 TextCore FontAsset，均不可用时回退当前游戏语言的 `RDString` 字体。解析结果按源资产和目标类型缓存，不进入逐帧反射或重建路径。

首版实现曾生成 `new Font() + CharacterInfo[] + UI/Default`。实机 `log.txt` 证明 Unity 6000.3 的 IMGUI 会立即调用 `TextSettings.GetCachedFontAsset(Font) -> FontAssetFactory.ConvertFontToFontAsset`，完全绕过 legacy `CharacterInfo[]`，随后因为合成 Font 没有字体文件而报 `Unable to load font face`。当前实现已删除该错误路径和对应代理面：从同一 TMP atlas、全部 glyph/character metrics 与 MOD Material 重建 `UnityEngine.TextCore.Text.FontAsset`，私有 `UnityEngine.Font` 只作为 `GUIStyle.font` 身份键。HookBroker 按 metadata 动态解析 `TextSettings.GetCachedFontAsset(Font)`，命中映射时返回重建 FontAsset，未命中时原样转发 instance、参数与隐藏 `MethodInfo*`；hook 永不物理卸载，owner 结束时仅注销映射。

映射使用 64 槽紧凑原子表，热路径无锁、无分配并只扫描当前有效映射数；注册/注销低频加锁。VirtualBundle 先注销映射并销毁 Font 身份/TextCore FontAsset，再按依赖拓扑释放源 TMP、Material 和 atlas。候选歧义、类型不匹配、hook 未安装和重建异常均失败关闭并回退游戏字体；完整原因进入诊断导出，Logcat 只保留有界摘要。当前仍不支持动态字体或非空 OpenType feature table。

代理闭包新增手机 metadata 中真实存在的 `UnityEngine.TextCore.Text.FontAsset/TextAsset/Character/AtlasPopulationMode` 及其构造、table/atlas/style setter 和 `ReadFontAssetDefinition`，删除不再生效的 `CharacterInfo`/`Font.set_characterInfo` surface，没有硬编码 RVA/VA。排除已知 Windows-only `PInvokeTests2.Test1` 后 managed 全量 `458/458` 通过。`build_android_single.ps1 -Configuration Release -RewrittenOracleDefault` 完整通过：14 个代理程序集、180 个精确输入类型、191 个生成类型、14 个 generic initializer、`missingAndroid=0`、`unresolvedMetadata=0`、代理审计 0 issue。`llvm-readelf --dyn-syms` 已确认 `modmanager_pccompat_register_imgui_font_mapping` 与 `modmanager_pccompat_unregister_imgui_font_mapping` 导出存在。arm64 SO Build ID 为 `e0ef78c21ea9cbf75dec37f3d821e66865788768`，SHA-256 为 `0974F64BABFE8FF5F2DDDE28F999A7A1EEB08EE585F35C8D6DC67BB742D63DAE`。runtime SHA-256：`StArray.ModManager.dll=67A1C2F0B9B693CF164E5BBDCDA20242D1D5D234DB3814027ADCF7F9FE245D5D`、`StArray.ModManager.Android.dll=35EF08E087A577B7B4FC833327718FEA80AA41F400B4BF06EEEE72A969CDC516`、`pc_compat_proxies/UnityEngine.TextCoreTextEngineModule.dll=2368FDF169A50ADB96B74EAFF754E23AE02EC01CC25459DADD22E7427C626817`、`pc_compat_proxies/UnityEngine.TextRenderingModule.dll=DCB5ABEBC3FC660856BDE6C0B1291607E6595A0CC476EB15B97314B8AF1D3C6D`、`pc_compat_shims/JALib.dll=DA87772495AAD29BB39E292FA01611146066EAA79CC4D93E891F7283EA70A157`。实机验收必须确认启动日志出现 `installed TextSettings.GetCachedFontAsset bridge`、设置诊断出现 `fontSource=VirtualBundle`，并确认不再出现 `Unable to load font face`；同时检查首次打开耗时、字体清晰度、中文缺字、关闭重开和卸载重载。

### 2026-07-26 原设置菜单触摸隔离

原 Unity IMGUI 菜单必须继续接收 Activity 送入 Unity 的真实 MotionEvent，因此不能在 Java `dispatchTouchEvent` 直接吞掉 modal 触摸。旧链只阻断 AsyncInput/gameplay observer 和 ADOFAI 的部分输入 getter，Unity `EventSystem.Update()` 仍处理同一事件，导致菜单按钮同时触发场景 uGUI。另有一个较短的 `Opening -> Open` 窗口：ModManager 等首个成功 draw 后才取得 modal 所有权。

当前设置 snapshot 透传 `None/UnityImGui/UnityCanvas` surface kind。打开请求成功后立即隐藏 ModManager 并以 `Opening` 模式取得 modal；确认 `UnityCanvas` 后保留 modal 但恢复 EventSystem，确认 `UnityImGui` 后继续阻断。native presentation sink 通过 metadata 精确解析 `UnityEngine.UI / UnityEngine.EventSystems.EventSystem.Update() : void`，由 HookBroker 永久安装单一入口；IMGUI modal 时跳过 continuation，其余状态原样转发 instance 与隐藏 `MethodInfo*`。逻辑关闭只切换原子 gate，禁止 physical unhook。Activity 的 Unity 事件投递、GUIUtility BeginGUI 和 MOD callback 均未改写。

新增合同覆盖 Opening 即时所有权、IMGUI EventSystem 隔离、Canvas EventSystem 保留、metadata/HookBroker/无硬编码地址。排除既有 Windows-only `PInvokeTests2.Test1` 后 managed 回归 `459/459` 通过；总结果为 `459 passed / 1 environment failure`。`build_android_single.ps1` 完整通过：14 个代理程序集、180 个精确输入类型、191 个生成类型、14 个 generic initializer、`missingAndroid=0`、`unresolvedMetadata=0`、代理审计 0 issue。`llvm-readelf --dyn-syms` 已确认 `modmanager_modal_input_blocks_unity_event_system` 与 `modmanager_modal_input_set_unity_event_system_blocked` 导出；arm64 SO Build ID 为 `942909637803be053e7d01d3accca4e9e34acef4`，SHA-256 为 `37EA4C1C105B5CF1FFF9F245C55DB1355C0D26526794D3E90AC4E83FEABEEC71`。实机验收需确认启动出现 `installed EventSystem.Update modal input gate`，Jipper IMGUI 菜单可点击且不触发场景 UI/判定，关闭后场景 UI 恢复；同时单独验证一个 `UnityCanvas` 设置面仍可点击。

### 2026-07-27 Harmony Postfix 跨 MOD 顺序闭合

上游 Harmony `PatchSorter` 与 `MethodCreator` 已核对：Prefix、Postfix、Transpiler、Finalizer 共用 priority/index/before/after 排序器；Postfix 不反转排序结果，仍按 priority 降序、registration index 升序调用。此前兼容层只把这套 owner metadata 发布给同步 Prefix，Postfix 事件仍按 recipe 的 bundle/target/rule 顺序入队，跨 MOD 的 `HarmonyPriority`、`HarmonyBefore`、`HarmonyAfter` 会失效。

当前实现为 managed event Postfix 增加独立 order plan：dispatcher 在成功绑定 Postfix 后发布 owner、priority、registration index、before、after；Android bridge 通过独立 begin/add/commit native 导出原子替换该 MOD 的 plan。native 在重建 HookBroker dispatcher snapshot 时对 `ManagedEventCallback` 建立不可变拓扑序列，使用与 Prefix 相同的确定性 cycle break；没有可用 metadata 时仍使用 bundle/rule 确定性顺序，保持 fail-open。

代码回查还发现仅按 snapshot 向 per-MOD ring 入队不足以保留跨 MOD 顺序，因为旧 UnityMain 路径逐 session 排空 ring。事件 ABI 先从 144 B 升至 152 B，在尾部追加 hook-time 单调 `dispatch_sequence`；Prefix V2 随后在其后追加 `invocation_id/result_kind/result_valid/result_value/run_original/reserved`，当前总长为 184 B，原参数、hit snapshot 与 `dispatch_sequence` 偏移保持不变。同一 MOD 的多个 bundle ring 先在 native drain 时按各 ring FIFO head 合并；UnityMain 再把所有 MOD 的批次复制到可复用 collector，按 sequence 排序并在任何 MOD `CompatUpdate` 前调用。collector 只在历史高水位增长时扩容，稳态复用 byte/entry 数组；hook 热路径仍只做 atomic sequence、ring lock 和定长复制，不解析 owner 字符串。

本轮新增 Postfix order plan、native event snapshot、跨 ring sequence merge 和 ABI 导出契约测试；随后 Prefix V2 闭合 primitive/enum `ref/out`、primitive/enum `ref __result`、generated-proxy `ref/out __instance`、`__state`、Prefix `___field` 写回和 Harmony 短路规则。Prefix invocation 为版本化 96 B 原地可变 frame；事件记录为 184 B。Prefix/Postfix `__originalMethod`、同步 Prefix 可写 `__args`、deferred Postfix 只读 `__args` 和按值 `__result` 已补齐：最多 6 个 primitive/enum/generated-proxy 参数可作为快照读取，Prefix 回写真实 native frame，Postfix 数组修改不回写；实例替换通过 frame 的 `instance_ptr` 回写 `FixedOpArgs.instance`，全部 12 个实例 dispatcher 在 original 前刷新 `self`。含 `ref/out` 目标的 Postfix `__args`、Postfix `ref/out __result`/`ref/out __instance`、未知 blittable struct 和普通 `ref proxy` 均在绑定期失败关闭。`__state` 的 Prefix/Postfix 类型冲突也改为构建期明确拒绝，不再静默向 Postfix 提供默认值。registry liveness 已从 60 帧记录数轮询升级为预编译 revision delegate 的逐帧无反射检查。测试工程默认 Windows native fixture 会拉起缺失的 MinHook/kiero，验证 managed 时使用 `-p:SkipWindowsNativeTests=true`；该环境限制不代表本轮回归。Transpiler/Finalizer、受控 struct-byref 与同步 Postfix 写回仍未实现，不能据此宣称完整 Harmony 行为等价。

最终验证：同步 Prefix 定向 `15/15`、Harmony 全族 `137/137`，排除已知桌面无 IL2CPP 的 `PInvokeTests2.Test1` 后 managed 全量 `607/607`；`build_shims.ps1` 的既有 JALib 闸门保持 61/61 类型、871/872 成员。`build_android_single.ps1 -Configuration Release -RewrittenOracleDefault` 通过，closure 为 180 exact types / 14 assemblies，生成 191 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。`llvm-readelf --dyn-syms` 已确认 managed Prefix callback、96 B invocation size 与 184 B event record size 导出。arm64 SO Build ID 为 `3edb527cd48cc057cb79ae147c55ee743a5152cd`，SHA-256 为 `EFDB5D020C2F58008ACDA70AEE8581D8DB41EB83530354895DFADAFE3C3C579B`；runtime `StArray.ModManager.dll=95A67AC19F308B37835A906E400DEA4F506E6E19554AFE44AD76FD59CA74CF47`、`StArray.ModManager.Android.dll=D1AD7BE357E8C4E1B4DD96A5EDDC554E534926EBD980FB352A581AB888BE891D`。native/managed 事件 ABI 必须同批部署，禁止只替换一侧。

### 2026-07-27 原设置双向绑定与 Jipper HUD 槽容量修正

原宿主兼容页此前只在原菜单 fault 时渲染 `mod_settings.schema`，正常状态下 KeyViewer/mobile override 与 JALib `Feature.Enabled/Setting` 是两套不可互见的控制面。现在宿主页常驻渲染 verified live bindings：兼容页写入继续排队到 UnityMain，`Feature.Enabled` 调真实 setter，普通 setting 字段写回后调用原 `CompatSaveGUI/SaveSetting`；原菜单 save/close 后在同一 UnityMain 调用边界重新读取 binding 并发布 snapshot，不逐帧轮询、不额外重复保存。原菜单与兼容页因此共享同一对象和 `Settings.json`，fallback 只改变呈现状态，不创建第三份值。

同时确认“除 KeyViewer 外大部分功能不显示、某个内置关卡正常”不是 Feature 开关问题：诊断中 8 个 Jipper Feature 均为 `enabled=True hostActive=True`。断点是 32 个 fixed dispatcher 槽全部占满，`scrPlayer.Hit`、`scrPressToStart.ShowText`、`scrShowIfDebug.*` 和 `scrUIController.WipeToBlack` 被标记为 `no fixed dispatcher slot available`。Jipper 普通关卡通过已安装的 `scnGame.Play` 调 `Overlay.Show`，内置/练习关卡通过被阻断的 `scrPressToStart.ShowText`，因此出现关卡相关差异。该历史问题曾以 64 项静态表止血；2026-07-30 已由增长式 thunk allocator 取代，不再通过猜测固定常量扩容。

验证结果：设置/schema/native 合同定向 `55/55`，排除桌面无 IL2CPP 的 `PInvokeTests2` 后 managed 全量 `618/618`。`build_android_single.ps1` Release/arm64-v8a 通过，closure 为 180 exact types / 14 assemblies，生成 191 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。SO 导出 `modmanager_pccompat_get_dispatcher_capacity` 的反汇编为 `mov w0, #64; ret`；Build ID `ffbe396197348b489d2974db9294e889c036803a`，SHA-256 `2222542EF264E66B19E201F368A3A22F929EA9DCD36B4A42375D961974072C83`。runtime `StArray.ModManager.dll=59A52B52A06BCFE8A00B57336E1A11C91684F6E141F89CB16DA7BB367F7B7FAA`、`StArray.ModManager.Android.dll=9F6F909A192973D525B06541EB44CC41BD8D291C7B6E3CCDB0781A940626B893`。必须完整重启 APP 才能替换永久 HookBroker 槽和 SO。

### 2026-07-27 Jipper 原菜单移动端嵌套排版修正

`pccompat_JipperResourcePack_20260727_101857.txt` 证明本轮不是 lifecycle、字体或回调异常：`managedLifecycleState=Enabled`、VirtualBundle 字体已解析、8 个 Feature 和整帧控件均提交完成，`lastLoggedException=none`。源码对照定位到真实排版冲突：Jipper KeyViewer 在外层 `GUILayout.BeginHorizontal()` 中调用 `SettingGUI.AddSettingSliderFloat()`，随后还在同一外层追加 Reset 按钮；旧 backend 又按完整 `_contentWidth * 0.5` 给内层 slider 分配约 331 逻辑像素，并追加 label 和 72 像素输入框。在 663 逻辑像素内容区内，内外两层都按独占整行计算，必然挤压或越界。KeyViewer/颜色页直接使用 `GUILayout.Button/Toggle(..., GUI.skin.label/customStyle)` 的路径又绕过默认 button/toggle 的 48dp 高度，形成同页大小不一致的点击行。

当前 slider 重建为移动端复合纵向组：label 独立一层，slider 与 88 像素 value field 位于下一层；slider 宽度使用 `clamp(contentWidth * 0.32, 160, 220)`，2400x1080/440dpi 的 Jipper 面板约为 212 逻辑像素，为外层 Reset 保留确定空间。窄屏 text/number 的 stacked 分支也显式建立 `BeginVertical/EndVertical`，不再只依赖父布局碰巧为 vertical。managed IMGUI bridge v3 新增 styled Toggle 重写，并把 styled button/toggle/text area 在单次调用期间临时提升到当前 48dp touch height，finally 中恢复原 fixedHeight；managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v27-mobile-settings-layout`，旧改写产物不会复用。

顶部 header 不再把 `Main` 和 `X` 交给普通 `GUILayout.Label/Button` 的自然尺寸推导。标题占用 `contentWidth - TouchHeight - spacing`、固定 `TouchHeight` 的逻辑矩形，并在绘制时加入垂直内缩；关闭按钮占用严格 `TouchHeight x TouchHeight` 的正方形矩形，避免单字符自然宽度配合 48dp 高度形成纵向拉伸。绘制入口使用手机 metadata surface 中真实存在的 `GUI.Label(Rect,string)` 与 `GUI.Button(Rect,string)`，由生成代理动态绑定，不引入硬编码地址；诊断导出分别记录 `header-title` 和 `header-close` 矩形。

slider 在数值文本暂时为空或非法时仍保留同一控件结构，只以最小值作为临时视觉位置；用户未操作 slider 时不覆盖原文本，避免 Layout/Repaint 因 parse 结果变化而增删控件。诊断同时保留最后一个有效 Repaint 的前 8 个控件矩形；关闭 MouseUp 和诊断 Logcat 预算耗尽后导出会显示 `lastRepaint:`，不再退化为 `rects=none`。为避免稳定菜单持续反射，初始 24 个事件后仅在 section/control 结构计数变化时安排下一次 Repaint 采样。

失败基线覆盖缺失 slider 宽度策略、缺失 styled Toggle bridge、旧 v26 cache，以及旧生成代理缺少 `GUI.Label(Rect,string)` 时 backend 构造明确失败。最终排除桌面无 IL2CPP 的 `PInvokeTests2` 后 managed 全量 `617/617`，最终 generated proxy 设置组 `46/46`；`build_android_single.ps1` 默认 Release/arm64-v8a 构建通过，closure 为 180 exact types / 14 assemblies，生成 191 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。native 未变化，SO Build ID 仍为 `3edb527cd48cc057cb79ae147c55ee743a5152cd`，SHA-256 为 `EFDB5D020C2F58008ACDA70AEE8581D8DB41EB83530354895DFADAFE3C3C579B`；runtime `StArray.ModManager.dll=2FA8D0CAA48ABB001C41F91AF0B2130E1094E199C87CE5B4F10DB67EFEFD408B`、`StArray.ModManager.Android.dll=0A4D76540A903692B2A66F36A96662923735D509F89316B1A4643BE2629486F5`、`pc_compat_shims/JALib.dll=B91B192403C85A72B34F89FE3FA0B962C3BE40498166B44A1AF8959DFFC7F6DF`、`pc_compat_proxies/UnityEngine.IMGUIModule.dll=2D15D218820C853FCC49692D6F716E9B919B15965DFE4F39BCA2D0D90343DA69`。实机必须同步完整 runtime；v27 会自动拒绝旧 managed rewrite cache。

### 2026-07-27 原菜单 Feature 启停与 `Thread.Abort` 降级

`pccompat_JipperResourcePack_20260727_121216.txt` 先证明上一轮 dispatcher 扩容已经生效：`bound=36/64`，原先因 32 槽耗尽而缺失的目标均为 `HookInstalled`，诊断中不再出现 `no fixed dispatcher slot available`。本次原菜单切换 KeyViewer 失败是另一条独立链：`Feature.CompatOnGUI -> Feature.Enabled=false -> Feature.Disable -> KeyViewer.OnDisable -> Thread.Abort()`。CoreCLR/Android 的 `Thread.Abort()` 固定抛出 `PlatformNotSupportedException`；Jipper 在调用它之前已经销毁 KeyViewer 对象并把 `Keys` 清空，但异常阻止后续 `Interrupt()`、线程字段清理和 Feature 状态提交。结果是 settings surface 进入 `Faulted`，仍存活的 `KeyInputListener` 随后在 `KeyViewer.Work()` 累计记录 6383 次空引用。

导入期 managed call rewrite 现在把 MOD 程序集中的 `mscorlib!System.Threading.Thread::Abort()` 精确改写为 `PcCompatManagedThreadBridge.Abort(object)`。bridge 保留 null receiver 的 `NullReferenceException`；目标线程已经结束时直接返回，仍存活时调用 CoreCLR 支持的 `Thread.Interrupt()` 发出协作停止请求，使 Jipper 能继续执行其原有的第二次 `Interrupt()`、线程字段清理和 Feature 提交。该策略不伪装成 CoreCLR 已支持异步强杀：任意 CPU-bound 线程若没有自己的停止条件或可中断等待点，不能保证仅靠此 bridge 立即退出，导入审计与诊断必须继续显式报告这种边界。

真实 Jipper 重写回归已证明 `KeyViewer.OnDisable` 与 `ApplicationOnquitting` 两个 `Thread.Abort()` 调用点都被替换，输出中不再残留原调用；bridge 运行测试覆盖阻塞线程中断、已结束线程和 null receiver。managed rewrite cache 升至 `xphorror.pcmod-managed-cache.v28-thread-abort`，`CollectionBridgeAbi` 加入 `PcCompatManagedThreadBridge.v1`，旧 v27 产物不会复用。排除桌面无 IL2CPP 的 `PInvokeTests2` 后 managed 全量 `621/621`；`build_android_single.ps1` 默认 Release/arm64-v8a 构建通过，closure 为 180 exact types / 14 assemblies，生成 191 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。native 未变化，SO Build ID 为 `ffbe396197348b489d2974db9294e889c036803a`，SHA-256 为 `2222542EF264E66B19E201F368A3A22F929EA9DCD36B4A42375D961974072C83`；runtime `StArray.ModManager.dll=57681828A4B76531DE827C03E3CF6B4B27C8CCF76A07A4943CFD2EE8524CEAFB`、`StArray.ModManager.Android.dll=B1DCAC6FA04BFF4CDE291CA1E10D8D507A2833F686CA40B21221B7EFD4EEF95B`、`ModAssemblyRewriter.dll=6ADFC621DD2A21D59C9F6EDEAEE74F46DD8B21B18CAA079E758379CB327F97E5`。实机需完整同步本轮 runtime，并验证 KeyViewer 在原菜单中执行 `开 -> 关 -> 开` 后 surface 不进入 `Faulted`、旧 listener 不再增长异常、HUD 对象与计数按 Jipper 原逻辑恢复。

### 2026-07-27 VirtualBundle Unity-null 返回边界

旧版本的 `last_managed_failure.txt` 记录了另一条独立启动失败：`phase=Enable`、`resourceSessionGeneration=2`，Jipper 在 `Main.OnEnable -> BundleLoader.LoadBundle` 遍历 `LoadAllAssets` 结果并读取 `UnityEngine.Object.name` 时，由 IL2CPP `Object.GetName` 抛出空引用。该堆栈说明 CLR wrapper 与数组项本身存在，但底层 Unity 对象已经是 destroyed/fake-null；若数组项为普通 CLR null，异常不会进入 IL2CPP `ThrowHelper`。旧文件没有记录数组索引或 Resource IR asset id，因此仅凭该文件不能证明是 materializer 直接返回死对象，还是对象在返回前被重载/清理链销毁。

VirtualBundle 现在为 `LoadAsset`、`LoadAllAssets`、asset dependency 和直接 preferred-asset 返回增加 Unity liveness 边界。Android 后端通过当前 generated `UnityEngine.Object.op_Implicit(Object): bool` 读取 Unity 真实对象状态，不读取 `m_CachedPtr`、不硬编码字段或地址。必需资产失效时立即以 `mod/generation/id/name/type` 失败关闭，使 `last_managed_failure` 指向资源身份而不是 MOD 后续的 `get_name`；可选资产失效时按既有 optional 语义从 `LoadAllAssets` 结果省略。检查只发生在低频资源返回边界，不进入 HUD/frame 热路径。

回归覆盖 destroyed required 明确失败、destroyed optional 省略、探针异常保留 session teardown 所有权，以及最终 CoreModule 代理中 `Object.op_Implicit` 的真实签名。排除桌面无 IL2CPP 的 `PInvokeTests2` 后 managed 全量 `625/625`；`build_android_single.ps1` 默认 Release/arm64-v8a 构建通过，closure 为 180 exact types / 14 assemblies，生成 191 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。native 未变化，SO SHA-256 仍为 `2222542EF264E66B19E201F368A3A22F929EA9DCD36B4A42375D961974072C83`；runtime `StArray.ModManager.dll=42BCA4224E2C3FB0C145DE4C04271E0B65E5D5A097BC6CC4E5A24382AB93894B`、`StArray.ModManager.Android.dll=A14E7F215246B86BFEA90EE0EEF5B5141B77E4A6FAE3B7E8D537DF4612AF6ED4`、`pc_compat_proxies/UnityEngine.CoreModule.dll=7C131E90E8971322F0F9A93B1A4266953B83300F08E19FE9E6A679DEBCAC4C71`。该防线阻止旧模糊异常再次穿透到 MOD，但不把旧日志中缺失的销毁者伪报为已归因；若当前版本仍复现，新的精确 asset identity 才能闭合 materializer 与清理时序。

#### 2026-07-27 GhostRain Sprite fake-null 首轮假设（实机证伪）

新版 `last_managed_failure.txt` 已把失败收敛到 `GhostRain` 的 `UnityEngine.Sprite`：`Main.OnEnable -> BundleLoader.LoadBundle -> LoadAllAssets` 在返回边界被 `Object.op_Implicit` 判定为 fake-null。后续代码回查确认 required 资产实际由 `TryPrepareRequiredAssets` 提前 materialize，`LoadAllAssets` 只读取缓存，因此该栈不能证明 Sprite 在同一调用中创建后立即死亡。生成代理核对仍确认 `Sprite.Create(Texture2D,Rect,Vector2,float,uint,SpriteMeshType,Vector4)` 与 `Object.op_Implicit(Object): bool` 均为 metadata 精确绑定；Linux 与根目录候选中的 GhostRain 也具有相同且合法的 `100x100` 纹理、rect、pivot、border 和 texture dependency。

首轮假设认为 `CreateTexture` 的 `Texture2D.Apply(false, true)` 在 Sprite dependency 消费前过早丢弃 CPU backing，因此改为 `Apply(false, false)`。第二次实机运行仍以完全相同的 GhostRain id 和栈失败，明确证伪“纹理可读性是唯一根因”。该改动仍保留，因为运行期 `Sprite.Create` 的依赖纹理必须保持可读，且 session teardown 会按依赖序显式 `Destroy`；但不能再把它作为本故障的闭合结论。

该轮失败合同与构建结果仍作为可读性修复的有效回归记录，但其实机验收失败，不能作为 GhostRain 故障通过证据。

#### 2026-07-27 VirtualBundle 原生资源 rooting 修复与再诊断

required 资产在 `TryPrepareRequiredAssets` 与 `CompatEnable` 之间存在跨帧/资源清理窗口。兼容层此前只在外部 CoreCLR registry 中持有 generated wrapper；Il2CppInterop GCHandle 能保住 IL2CPP wrapper，却没有为 Unity 原生资产声明 `DontUnloadUnusedAsset`。Unity 资源清理因此可能销毁 Texture/Sprite/Material/Font 的原生对象，留下仍可从 CoreCLR 取回但 `Object.op_Implicit=false` 的 fake-null wrapper，这与当前时序完全吻合。

手机 dump 已确认 `UnityEngine.Object.get_hideFlags/set_hideFlags` 和 `HideFlags.DontUnloadUnusedAsset=32` 真实存在。代理 surface 新增这两个 metadata 精确入口；兼容层对所有新建 Texture、Sprite、Material、TMP Font、IMGUI Font/TextCore Font 和 capability clone 保留原 flags 并追加 `DontUnloadUnusedAsset`，卸载时仍由 owner session 显式 `Destroy`，不引入永久泄漏。`TryPrepareRequiredAssets` 现在在发布 ready 前立即执行 liveness：若 `Sprite.Create` 当场产生 fake-null，会在 preparation 阶段失败；若仅在其后死亡，则说明仍有未覆盖的跨窗口所有权问题。失败文本同时新增 `materialization kind`、bundle id、source path 和 selected 状态，不再仅凭 asset id 反推候选。

失败基线覆盖旧代理缺少 `HideFlags`、旧 preparation 不检查 liveness、旧错误缺少 bundle/kind；修复后定向 VirtualBundle/generated-proxy 测试 `33/33`，排除桌面无 IL2CPP 的 `PInvokeTests2` 后 managed 全量 `626/626`。`build_android_single.ps1` 默认 Release/arm64-v8a 完整通过，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。该修复仍需实机验证，不能在新日志到来前宣称 GhostRain 根因已经闭合。

### 2026-07-27 Android 上游 P0 同步与 ModManager 内嵌字体

上游 `2719a69` 的 Android 能力已按 [`UPSTREAM_ANDROID_SYNC_PLAN.md`](UPSTREAM_ANDROID_SYNC_PLAN.md) 完成首批 P0 手工同步，不做整体 cherry-pick。ModManager 现在内嵌 SHA-256 为 `2B1304A1A2D6B811A38C2C90A2BE503CBAC0BFBF4D8B0E6A6A598146564A61AD` 的 `NotoSansCJK-Regular.otf` 和新版 FontAwesome。`AndroidImGuiFontLoader` 统一 EGL、Vulkan 与调试 renderer：Noto 为基础字体，FontAwesome 只合并实际引用字形，atlas 按 native pointer 只 Build 一次；16.5 MB 字体用 64 KiB pooled buffer 分段复制，Build 后释放 unmanaged 临时内存。2026-07-28 修复了字体文件有字但 `GetGlyphRangesChineseSimplifiedCommon()` 未把“辑”等本地化字符编入 atlas 的缺口：`L10n` 现在枚举当前 culture 实际资源字符串的全部 BMP codepoint，FontLoader 合并基础 Latin 后压缩为动态 ImGui range，并把 range 生命周期保持到 `Build()` 完成；字形诊断验证当前本地化全集且只输出有界缺失摘要。内嵌资源失败时依次降级系统 Noto 和 ImGui default，不再空 `catch`。当前生产仍只安装 EGL，Vulkan/调试 renderer 仅复用同一合同。

固定 UI 文本已移除状态、toast、ZIP 说明和详情标题中的 FontAwesome 前缀，设置入口补充文字；中文 smart quote 和空列 em dash 已改为普通文本。资源测试遍历中英文 `.resx` 并拒绝 Emoji、私用区、smart quote、em dash、箭头和未允许 symbol。`ModEntryPointAttribute` 与集中 resolver 已替换发现/加载两处 `GetTypes()`；无效声明回退扫描，`ReflectionTypeLoadException` 保留成功类型并输出单条摘要。设置 JSON 改为逐字段恢复，坏字段保留默认值且不阻断后续字段。

定向回归 `6/6`，排除桌面无 IL2CPP 的既有 `PInvokeTests2.Test1` 后 managed 全量 `632/632`；未过滤结果为 `632 passed / 1 known environment failure`。Android Release 快速编译和 `build_android_single.ps1 -Configuration Release -RewrittenOracleDefault` 均通过，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 runtime `StArray.ModManager.dll=0DCC3AEF78AC3182317A08FEC0084D23AC10362E6A8A745B07C7283CCA6C7B09`、`StArray.ModManager.Android.dll=618271D6B2089DC6B1C56054C3D06FA601ED6FB6532AB50E4C82A75480EEB514`；native 未变化，SO Build ID 为 `ffbe396197348b489d2974db9294e889c036803a`，SHA-256 为 `2222542EF264E66B19E201F368A3A22F929EA9DCD36B4A42375D961974072C83`。字体状态仍为“本机完成、待实机”：必须确认日志 `ready:cjk=embedded,icons=embedded`，并验证首次打开、关闭重开、加载 MOD 后重开、关卡内打开和中文缺字。该段保留为 P0 构建基线，P1 结果见下一节。

### 2026-07-28 Jipper ResourceChanger 完整适配

Jipper R143 ResourceChanger 从 14/17 补齐到 17/17 个 patch 目标，新增 `scrLogoText.Awake` after-op 与 `UpdateColors/LateUpdate` before-original skip。Logo Awake 不再只重着色：现在镜像原 MOD 的 RectTransform y 修正、`Education Edition` clone、重复实例检查、动态 `ResourcePackName/TitleColor`、字号 100 和 `(-50,330)` 锚点位置。星球、尾迹、Beat 地板、五个 PlanetRenderer 颜色 setter、Rainbow/Enby 与 coop 门禁继续使用 metadata-resolved shared HookSlot。

兔子资源不再读取 MOD 目录 PNG，也不再将 `Auto.png` 打进 runtime。`Auto` 必须来自 MOD 自带 `jipperresourcepackbundle` 生成的 Resource IR：VirtualBundle 在 UnityMain 重建真实 Sprite 后，通过 owner/session generation 发布 IL2CPP identity；native 用 GCHandle 持有并在 session retire 时释放。除 `scnEditor.OttoUpdate` after-op 外，translator v8 在该映射通过审计后派生 `scnEditor.OttoBlink` after-original companion，覆盖击打时官方 `autoSprites[2..5]` 的直接写入并重投影当前 AUTO 颜色；不安装全局 `Image.set_sprite` Hook。`build_android_single.ps1` 已删除旧 PNG 复制与发布断言，生产源码、Gradle runtime 和最终 runtime 均不含该文件。

新增 managed state adapter，绑定原 `ResourceChanger._settings`、`PlanetColor/TitleColor/TileColor` 和 `ResourcePackName` 的编译委托，只在状态变化时发布。原 MOD 菜单和 Jongyeol `FeatureReset` 会驱动 native；兼容菜单写回同一 `_settings`，并从 managed snapshot 反向刷新，避免两套开关互相覆盖。true -> false 或 MOD 卸载通过 UnityMain work queue 恢复兔子原 Sprite、官方星球颜色、Beat 默认色和 Logo 官方 `UpdateColors`；场景退出清理对象 GCHandle。

本轮定向资源测试 43/43 通过，Android-targeted managed 全量 673/673 通过；Android arm64 Release 单包构建通过，JNI helper 100/100，proxy closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO Build ID 为 `f1c6f1d637c879d28eebfdc4c6575102d1d372ad`，SHA-256 为 `22BCCBE8EE321ABDCBBC18846530EA34B2F2E220B45BB9A71034BD991F9AB54F`；runtime `StArray.ModManager.dll=789EC50FC26FFEB39F6B4E6902EFA6478473CE2E92D011F444DFF10B5C9B92AB`、`StArray.ModManager.Android.dll=0F4BD1A69F59B154E474A85FE4E68946993A8A7BBED59E3C0F5512A1D73E7591`。该结论表示 Jipper 已审计 ResourceChanger 适配闭合，不表示任意 MOD 的动态字体、任意 prefab/shader 或异步 AssetBundle 已成为通用能力。

同日后续回归补齐 ModManager 本地化动态字形范围、AUTO 击打写回与原 MOD 菜单 KV 重绑 modal 隔离：字体/资源定向合同 34/34、输入链定向合同 49/49、Android-targeted managed 全量 693/693 通过；NDK 25.2 arm64 单包构建通过，JNI helper 100/100，proxy closure 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO Build ID 为 `b8434b79b471e84fe3a50e569186beaa801da122`，SHA-256 为 `1CC56A4F43B38F377F81B53936C86A755F202DC9512572F813EA557827338529`；runtime `StArray.ModManager.dll=4FA9688FE8F0BFB24874A2AD857ECDCE6074C1504C7166D6E9B9C481A9D2C240`、`StArray.ModManager.Android.dll=5E1F47F42EC4FA3982CFD8B1735C7DD07B577F69DA4D914A02514E2940B52A1C`。

### 2026-07-28 Android 上游 P1 Inspector 与 JNI A 调用

Inspector 合并保留旧版 public 自动纳入规则，同时开放显式标注的 non-public field/property。`ModSettingIgnore` 始终排除；`NoSave` 和 `ReadOnly` 不进入持久化集合。Save/Load 与绘制现在共用 `GetSettingMembers`，支持 public/static/property 和显式 private 成员逐项恢复，单成员失败继续保留默认值并处理后续成员。旧公开 `GetInspectorFields(Type)` 按 public instance field 合同恢复。`ShowIf` 已冻结为隐藏语义：普通项隐藏，标在 Header 上时隐藏整个组直到下一 Header；`ReadOnly` 才负责禁用。Hotkey 未绑定显示和新增中英文文本均改为 CJK/ASCII 字体可覆盖字符。

JNI 已从首批 Android 调用闭包扩展到上游完整 managed manifest：`85/85`。当前 `JniHelperNative` 有 97 个唯一绑定，其中 12 个为本地 Android 扩展；`jni_helper.c` 有 100 个唯一 helper 定义。覆盖引用、异常、UTF 字符串、实例与静态全部 primitive `Call*MethodA`、实例与静态全部字段以及对象数组 API。8-byte explicit-layout `JValue` 保持不变；`JValue.C` 使用 CLR `char`，`jchar` 显式 `U2`，`jboolean` 显式 `I1`，UTF-8 入参显式 `LPUTF8Str`。`Call*MethodA` 在 native 内检查、输出并清除 Java 异常；字段和数组保留 JNI 原始异常状态，交给 `CheckException/ClearException`。旧 helper ABI、Activity/Surface/input/data 扩展和 HookBroker 所有权保持不变。

新增 `JniUpstreamApiContractTests` 固定 85 项上游 manifest，并同时审计 managed 声明、native 定义、`JValue` 布局、boolean/char marshalling 和 UTF-8 字符串。JNI 定向合同 `2/2`，managed 未过滤全量 `639/639`。上游没有专门自动测试文件，因此本地合同不冒充设备端 Java 功能测试。完整 `build_android_single.ps1` 通过，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0；构建脚本从 `jni_helper.c` 自动生成导出预期，并确认最终 stripped arm64 SO 的 helper `.dynsym` 为 `100/100`。最终 SO 大小 2,875,800 bytes，Build ID `6c0b7269861b1034df6218913578f56dd14f0e62`，SHA-256 `E538A7C35FB70C35125F4794CACEE228DB7AF624DD78C174E0A268B9E2F45C34`；runtime `StArray.ModManager.dll=A9C817FF371001BF9888B4C8BA9F084F0C508A04EB0F8013A0119DE2D6F4B01D`、`StArray.ModManager.Android.dll=DF2223F6D8F01621C1C485A36166AA1650AC4283EF7151921CD584F95B996780`。仍需设备端 Java fixture 覆盖 Activity/IME、文件、Toast、modal input、字段、数组和 primitive 返回路径；当前只能声明 API/ABI 与最终导出完整。

### 2026-07-28 managed self-render 切换后输入全阻断修复

“关卡内先运行兼容 HUD，退出到主菜单后启用托管自绘”不再触发旧激活竞态，但随后官方触摸和异步测试宏同时失效。源码链路证明两类输入的共同门禁不是 MOD lifecycle 或 KeyViewer：`async_input.c` 查询 native `modmanager_modal_input_is_active`，active 时停止捕获并清空队列；Android platform 的 `IsModalInputCaptureActive` 却只查询 Java `sModalInputCapture`，设置也只通过 Java method 间接修改 C++ `g_modal_input_active`。两份状态分叉后，managed 清理分支可看到 false，而 native 仍为 true，因此触摸与宏同时永久被拒绝。

Android platform 现在把 native gate 作为 gameplay input 真源，直接调用 `modmanager_modal_input_is_active/set_active`；Java mirror 继续维护 Activity/Back 状态。查询返回 native 与 Java 状态的 OR，确保任意残留都会被后续 no-target 清理。设置顺序改为先更新 native，再同步 Java；JNI void 调用异常即使被 helper 清除，也无法留下 native active。该修复不改变 modal 打开时应有的输入隔离，也不修改 async_input、宏或官方输入逻辑。

新增 `AndroidModalCaptureUsesNativeStateAsAuthoritativeInputGate` 先红后绿；managed 未过滤全量 `637/637`，Android Release 与完整单包均通过。closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。SO 未变化，Build ID 仍为 `d899d20711ea4d21944bc7e6bb38387ed1f5842a`，SHA-256 为 `AF6DE916FCBCC4A76D9348A6A0CA02EFAC087692383130F4594D142EC4FEBEFB`；runtime `StArray.ModManager.dll=A9C817FF371001BF9888B4C8BA9F084F0C508A04EB0F8013A0119DE2D6F4B01D`、`StArray.ModManager.Android.dll=B2C3B6F146929C79216260195FAB6E9D27E6469A37D353D84D968D7E98CF1764`。仍需实机按原序列确认退出 ModManager 后触摸和测试宏恢复。

### 2026-07-28 上游 Runtime 缺陷本地同步

旧公开 `StArray.ModManager.Il2Cpp` helper 已同步上游六项 Runtime 修复，但没有替换 PcCompat/Il2CppInterop 生产主线。`Il2CppDomain.Current` 现在缓存单一 wrapper，attach 使用 thread-local 深度和所有权；旧 static `ThreadDetach()` ABI 保持不变。Windows/Linux 只 detach helper 自己创建的附着；Android 遵守 `il2cpp_foreign_thread_guard.cpp` 已验证的 Boehm 边界，附着后不主动 detach。`OpenAssembly` 新增 UTF-8 `const char*` P/Invoke，旧 pointer overload 保留以维持二进制 ABI，不再把 `Il2CppString*` 错传为 assembly name。

legacy `Il2CppArray<T>` 现跨过完整数组头并保留 null；实例值字段使用 `il2cpp_field_set_value`，对象引用字段使用 `il2cpp_field_set_value_object`，静态字段继续走官方 static API。所有 helper `runtime_invoke` 集中检查 exception pointer，并抛出包含 native message/stack 的 `Il2CppInvocationException`；`Il2CppObject.GetHashCodeIl` 同时修正为读取 boxed return 的 unboxed `Int32`。真实 3.1.2 arm64 `libil2cpp.so` 已用 `llvm-readelf` 确认本批使用的 18 个导出全部存在，未新增地址或虚构符号。

新增 legacy IL2CPP 定向回归 `17/17`，managed 未过滤全量 `656/656`。`build_android_single.ps1` 默认 Release/arm64-v8a 完整通过：JNI helper exports `100/100`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 大小 2,875,800 bytes，Build ID `6c0b7269861b1034df6218913578f56dd14f0e62`，SHA-256 `E538A7C35FB70C35125F4794CACEE228DB7AF624DD78C174E0A268B9E2F45C34`；runtime `StArray.ModManager.dll=C99275905AB48AF8068AF1CD8BFFFFE8B335D065563CE520C95EB142E80BCA54`、`StArray.ModManager.Android.dll=713A70CDFE7188D9BD8AAC65DE1DF63B23DD719F51615AFA9ECEB75F5BC9AD62`。本批不改变 managed self-render、HookBroker、generated proxy 或 foreign-thread guard 的生产所有权。

### 2026-07-28 ResourceChanger 启动回放、Auto Sprite 与短 setter 修复

`pccompat_JipperResourcePack_20260728_101327.txt` 中 `providerAvailable=True`，但 UI 仍显示 native bridge unavailable；根因是 MOD `OnLoad` 早于 Android settings sink 注册，旧实现把一次性的 `TryApply=false` 永久缓存到页面。ResourceChanger runtime 现在缓存最新 owner state，sink 注册时按 MOD 身份确定性回放，VirtualBundle session 注册完成后再次重放；UI 直接查询当前 sink 注册状态，不再依赖启动瞬间结果。

`Auto` Sprite 的早期调度此前会因 VirtualBundle session 尚未注册直接返回，且状态不变时永不重试。session ready 重放现会重新进入 UnityMain 资源队列；同 owner/generation 的 pending 与 published 请求分别去重，避免重复 native GCHandle。诊断导出新增 `requested/resolved/published/retired/failure/lastError`，可直接区分未调度、IR 解析失败、native 拒绝和正常退役。资源仍只来自 MOD 自带 bundle 的 Resource IR，runtime 不包含 `Auto.png`。

一般 static `void(GP32)` 目标现在推导为现有 `StaticVoid1`，覆盖 bool、32 位整数和 native 同步白名单中的 `HitMargin/InputEventState` 枚举；未知值类型仍保持 `Unknown`。`PlanetColor` 是 24 字节非 GP32 结构体，`PlanetRenderer.SetColor(PlanetColor,bool)` 在 AArch64 上必须通过 `InstanceVoidPtrBool` 间接指针 ABI 转发。因此 `RDC.set_auto(bool)` 不再停在 `abi=Unknown/PendingResolve`。`PlanetRenderer.SetRainbow(bool)` 在 r143 arm64 中只有 8 字节且与 `SetTailColor(Color)` 相距 8 字节，HookBroker 拒绝强装是正确行为；install plan 将其标记为 `SkippedKnownConflict`，不分配 dispatcher、不计失败，并由 RainbowMode、SetColor、LoadPlanetColor 和五个颜色 setter 的组合规则覆盖。定向回归覆盖 sink 延迟回放、显式重放、静态 GP32 ABI、VirtualBundle ready 顺序、Sprite 发布去重/诊断和短 setter 组合覆盖。

最终资源/recipe/native 定向回归 `64/64`，Android-targeted managed 全量 `678/678`；Android managed Release 与 `build_android_single.ps1` Release/arm64-v8a 均通过。构建使用 NDK `25.2.9519653`，JNI helper exports `100/100`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 大小 2,886,608 bytes，Build ID `2b9dcd3b295dbd169cf3ca2a6a170683cffbf1e4`，SHA-256 `F09BFE92269F4BF1002FACA7913392EFB87D3FDB407E4CE7C76C03726D9E312B`；runtime `StArray.ModManager.dll=047EBE1EE73A234E5CB662CBD7DC7DFF35F1027A5B76B6148C785B5AF9EB9E80`、`StArray.ModManager.Android.dll=EC186BFA9E242888B83BBDB24F09FFCAFBE83139FBF34BDA4BF6CCD849700935`。`publish/retire/set settings/prepare plan/resolved count` 五个关键 native 导出已由 NDK 25.2 `llvm-readelf` 确认存在。

### 2026-07-28 Jipper 原菜单按键绑定同帧隔离

实机持续出现点击 KV 绑定按钮后立即变成 `Backspace`，说明只在输入 producer/modal 层过滤不构成完整修复。真实 `JipperResourcePack.dll` 反编译确认，`CreateButton` 的 `GUILayout.Button(...)` 返回 true 后会在同一次 `OnGUI` 立即执行 `Input.anyKeyDown -> Enum<KeyCode> -> Input.GetKeyDown`，否则继续扫描 `GetAsyncKeyState(0..255)`；`Backspace` 是枚举中最早的有效键之一。旧 rewrite 只接管带 `GUIStyle` 的 Button，Jipper 绑定按钮使用的 `GUILayout.Button(string, GUILayoutOption[])` 仍直达代理，因此兼容层无法标记按钮激活所在的 GUI 帧。旧 modal 修复即使正确隔离 touch producer，也没有建立“按钮激活不能成为绑定输入”的 UI 事务合同。

当前 managed rewrite 新增无样式 Button -> `PcCompatManagedImGuiBridge.ButtonText`，按当前 `GUI.skin.button` 重建原调用。settings backend 在成功 `BeginFrame` 后开启 thread-local input transaction，并在所有正常、Abort 和清理异常出口成对结束；Button 激活后，本 transaction 内的 `anyKeyDown/GetKeyDown/GetKey/GetAsyncKeyState` 只消费 consumer/native cursor 并建立逐键 baseline，统一返回无输入。下一帧新的实体键 DOWN 仍按原 Jipper 逻辑绑定。Android modal 查询继续直接读取 native `modmanager_modal_input_is_active`，managed mirror 只作提前门禁；rewrite cache 升至 `xphorror.pcmod-managed-cache.v29-settings-input-transaction`，bridge ABI 升至 `PcCompatManagedImGuiBridge.v4`，不会复用缺少 Button rewrite 的旧产物。现有 `legacyInputQueries` 追加 `settingsButtons/settingsSuppressed/settingsLastKind/settingsLastKey/settingsLastThread`。因实机仍复现，临时有界 Logcat 诊断统一使用 `[DEBUG-kv-binding-v2]`：每进程一次 `bridge-ready`、每次按钮一次 `activation`、最多 8 条普通 `suppress`（`key=8` 始终保留）、一次 `end` 摘要，以及点击后 5 秒内最多 12 条真实 `accepted`；不输出逐帧或 256 键完整扫描。

失败基线同时证明旧实现的真实 Jipper rewrite 报告为 `expected one compatible external bridge method, found 0`，且 settings transaction 三个入口不存在；修复后真实 rewrite 与同帧输入行为回归 `2/2`、输入/rewrite/settings 定向 `114/114`、Android-targeted managed 全量 `694/694`。使用 NDK `25.2.9519653` 的 `build_android_single.ps1` 完整通过：JNI helper exports `100/100`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO Build ID `b8434b79b471e84fe3a50e569186beaa801da122`，SHA-256 `1CC56A4F43B38F377F81B53936C86A755F202DC9512572F813EA557827338529`；带诊断 runtime `StArray.ModManager.dll=21046F6B30C1C43710766E05B8B98567F6CDBD08920A5B870FD2AAEBD281D417`、`StArray.ModManager.Android.dll=443587F1A1259DB31E7376D4F008C4EFDB2675B32AE89EDDBF618DFB8611060D`、`ModAssemblyRewriter.dll=6532E0957DCAB18003648CD23E1E8AA6801FF6DCBB6453731C3318ED82BDF0C7`。实机必须同步完整 runtime；预期行为是点击绑定按钮后保持等待状态，松开触摸后按下实体键才提交，不能再次瞬时显示 `Backspace`。

### 2026-07-30 PlanetColor ABI 与动态 dispatcher thunk arena

运行中关闭 Jipper 资源替换后再进入关卡会让 `ResourceSkipPlanetColorOriginal` 从 skip 切换到 passthrough；旧 `PlanetRenderer.SetColor(PlanetColor,bool)` 被误分类为 `InstanceVoidIntBool`，把 AArch64 间接传递的 24 字节 `PlanetColor` 指针截断为 32 位。当前 recipe 改用 `InstanceVoidPtrBool`，native verifier 将 `PlanetColor` 单独归类为 `IndirectStruct`，cache 升至 `mvp-recipe-cache-v11-indirect-struct-abi`。资源替换开启和关闭现在都沿同一完整指针 ABI 调用链运行。

fixed dispatcher 已删除 64 槽 runtime 数组、managed snapshot 数组及 14 组 0..63 静态 wrapper 表。install plan 先对完整去重 staging 集合执行 ABI/op gate，再按 `required = distinct(permanentlyBoundTargetKeys union installableStagedTargetKeys)` 一次性创建稳定 `DispatcherRuntimePage` 和匿名 AArch64 thunk 页。thunk 由统一 `DispatcherAbiSpec` 计算 GP 寄存器数量，保留 FP/HFA 参数寄存器，以 `BTI c`、GP 右移、32 位 index 和 `x16` 尾跳进入公共 dispatcher；代码页严格执行 RW→clear-cache→RX。批次分配失败时所有新目标一起 blocked，任何物理 Hook 安装均不会提前发生。已发布 page、thunk、slot id 和 original trampoline 保持到进程退出，clear/reload 只停用 rule 与 snapshot。

诊断新增 `required/capacity/bound/ready/blocked/new/allocated/remaining`，动态 `capacity` 导出反汇编为 acquire load，不再是 `mov w0,#64; ret`；最终对象中旧 `pccompat_detour_*_<index>` 符号数为 0。定向回归 `79/79`，Android-targeted managed 全量 `706/706`。NDK `25.2.9519653` 单包构建通过，JNI helper exports `112/112`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 大小 2,826,760 bytes，Build ID `1d99ced2e34ad4e7c849937644f442081ea2e66d`，SHA-256 `5993F557D528113B1CAB1B06869326F653947E805ACFDA6FE440E337B953F5E6`；runtime `StArray.ModManager.dll=4EB891F189ECD69A54D288D4AAF64581CE6297672D66B6ED79DC6C053F86DCFD`、`StArray.ModManager.Android.dll=304BE91FBA97D47C8A6912E07AF85B2E997772636055AF356D6C4B13125D98D0`。构建与产物审计已闭合；匿名 thunk 的首次真实执行仍需完整重启 APP 后实机验证。

### 2026-07-30 运行时卸载 HUD 与 Android 触摸事务收尾

运行中卸载 MOD 后，`PcCompatUnityHudRuntime.UnregisterSource()` 现在会通知 Android Canvas bridge；bridge 通过已验证的 UnityMain work queue 刷新资源。没有可见 source 或没有 frame 时先执行 `SetVisible(false)`，因此标准兼容 HUD 的 `DontDestroyOnLoad` root 会立即隐藏。source 变化若与 renderer callback 重入，则保留 pending refresh，不静默丢弃；source 注册/注销仍在发布 immutable snapshot 后通知，避免在 source lock 内调用 Unity API。后续实机复现证明这只覆盖标准兼容 HUD 链，不能据此断言 Jipper 自己创建的持久 KeyViewer root 已被销毁。

Android `dispatchTouchEvent` 为每个触摸事务冻结 owner：`DOWN` 时在 Unity modal、ModManager、AsyncInput 和 Unity gameplay 之间选择，后续 MOVE/POINTER/UP/CANCEL 不再重新判路由；ModManager owner 即使窗口在事务中关闭也消费完整事务，避免 UP 透传给官方 `PauseMenu`。Activity pause/destroy 会清理未完成 owner。native ModManager 可见时整屏消费，隐藏持久 overlay 仍只消费已发布窗口区域。该边界针对卸载/加载期间官方 `PracticeTimeline.SetPositions -> PauseMenu.Unpause` 的触摸透传 NRE。

回归结果：HUD、Android input、native HUD 定向 `60/60`；managed 未过滤全量 `713/713`。`build_android_single.ps1 -Configuration Release -Dex -RuntimeAssets -RewrittenOracleDefault` 使用 NDK `25.2.9519653` 通过，JNI helper exports `112/112`，proxy closure `181 exact types / 14 assemblies`，生成 `192 types / 14 generic initializers`，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues `0`。修改后的 `classes6.dex` 单独编译和 keep audit 通过。SO Build ID 为 `210c0e067778ff464b1d7caaec593ca41ea5c1da`，SHA-256 为 `EE6581B06FDB5B58060FAFECE69DBF45676E758AD38132E237D710200C4E9672`；runtime `StArray.ModManager.dll=DA7E8ED089ACBA3FA438EB44F67F02C3A8D9C015C0E8E9221FF3FBEFA59BBDA8`、`StArray.ModManager.Android.dll=297A664982C318DD1FE463EA7C5B34E06B977690B41FD000362D0F6A8FB84054`。暂停菜单触摸事务仍需实机复验；KV 黑块的后续 owner teardown 修复见下节。

### 2026-07-30 运行时按 MOD 退役与 dispatcher 永久绑定复用

动态 dispatcher 初版只闭合了运行时新增 bundle 的扩容，`PcCompatRuntime.UnregisterMod()` 删除托管 session 后，native `g_state.bundles` 与 Android `LoadedRuntimeRulePaths` 仍保留旧 MOD；卸载后的 fixed-op/managed Prefix/Postfix 仍可能执行，重载又会被路径去重误判为已经加载。当前增加 `modmanager_pccompat_unload_hook_rules_for_mod(modId)`：在统一 lifecycle 锁内先退役 managed callback ring，再移除该 MOD 的 order plan 和 active bundle，重建剩余 slot snapshot；UI lifecycle program、deadline task 和 presentation graph 按 bundle id 单独退役。物理 Dobby detour、original trampoline、dispatcher page 和 AArch64 thunk 不回收、不 unhook。

托管注销顺序固定为“先从 `RecipeBundles` 隐藏 -> native retire -> managed session Dispose -> 其余 registry 清理”，防止 retire 等待同步时旧 bundle 被并发 reconcile 重新发布；native retire 失败会恢复托管 bundle 并中止卸载，`PcCompatModPlugin` 也不会预先拆 HUD/KeyViewer 外围状态。managed frame/OnGUI dispatch 与 session Dispose 由同一 lifecycle lock 串行化，从 MOD 自己的 frame/OnGUI callback 内递归卸载会失败关闭。Android bridge 以 `modId -> paths` 记账，A/B 同时加载时卸载 A 不清 B；同 MOD 路径变化先退役旧 bundle，再加载新 bundle；同步期间发生的新 registry change 会被标记并重跑，不再静默丢失。managed event ring 改为共享所有权并增加 `retired` gate；synchronous Prefix 通过 per-bundle in-flight lease 排空，旧 immutable snapshot 可安全退出但不能进入已销毁 session，从 Prefix 内递归卸载同样失败关闭。UI lifecycle 退役槽在所有 in-flight VM 执行结束后复用，并取消该 bundle 的 scheduler task，反复 reload 不再永久消耗 256 program 容量。

容量合同保持不变：`capacity/bound` 是进程期物理高水位，不因卸载下降；当前 active `bundles/slots/rules/ready` 会下降；同 target 重载复用原绑定，只有从未绑定的新 target 才追加 page。新增合同测试覆盖退役顺序、per-MOD native erase、ring 共享所有权、Prefix in-flight 排空、managed dispatch/Dispose 串行、lifecycle task 取消/槽位复用、fail-closed，以及禁止 `DobbyDestroy/munmap`。定向回归 `64/64`、managed 未过滤全量 `708/708`；NDK `25.2.9519653` 完整 arm64 Release 单包构建通过，JNI helper exports `112/112`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 大小 2,840,576 bytes，`modmanager_pccompat_unload_hook_rules_for_mod` 位于 `.dynsym`，旧静态 detour 符号为 0；Build ID `1f357428c091d9116de0a046291b20d7387dfb44`，SHA-256 `8A1C75430A7131B4FF9ADFD58B5DCE246AAB41EFC66E506CB2EC8ED78095454D`。runtime `StArray.ModManager.dll=7FDE6DC47A4894FC6808E2CE49C87CF27783032F259118479E75C75F13A06ABE`、`StArray.ModManager.Android.dll=89BBD37BE8B314323FB10400DC5A5653E1F870E8221F8EC68138DDA62DB6A658`。仍需实机验证匿名 thunk 首次执行及 A/B MOD 连续 load -> unload -> reload 行为。

### 2026-07-30 Jipper 持久 KeyViewer root 的 session owner teardown

标准兼容 HUD 隐藏后黑块仍会跨 `unload -> load` 保留，说明残留不是单一 Canvas bridge source。Jipper 的真实 `KeyViewer.OnEnable()` 会创建 `JipperResourcePack KeyViewer`、`KeyViewerUpdater`、`RainManager`、Canvas 和槽位对象，随后调用 `UnityEngine.Object.DontDestroyOnLoad(KeyViewerObject)`；旧 managed component bridge 只改写 `Destroy`，没有观察该持久化调用，因此 session registry 无法在异常 lifecycle、延迟 Destroy 或卸载顺序变化时兜底回收 Jipper root。

managed component bridge v6 新增 owner-scoped `DontDestroyOnLoad(Object)`：先原样转发 Unity 调用，只有目标可证明为 `GameObject` 且当前 session 已在同一 owner 上挂载 managed component 时，才把对象登记到 session `PersistentObjects`。显式即时 `Destroy` 成功后注销登记；`Destroy(Object,float)` 保留 owner 到 session teardown，使 MOD 在延迟到期前卸载时仍能立即回收 root。session teardown 销毁仍登记的对象，且只在 native Destroy 成功后清除登记，避免失败后失去重试目标。无法证明 MOD 所有权的官方对象只转发、不登记，也不会在 session teardown 被兼容层销毁。真实 Jipper rewrite 合同已确认 `KeyViewer.OnEnable()` 的原始 `UnityEngine.Object.DontDestroyOnLoad` 调用被替换为 bridge；managed cache 升级为 `xphorror.pcmod-managed-cache.v30-ddol-owner-teardown`，旧缓存不得复用。

新增合同覆盖 session teardown 回收、显式 Destroy 去重、延迟 Destroy 在提前卸载时强制回收、native Destroy 失败后保留登记并可重试、无 owner 证明时只转发、真实 Jipper IL 改写和 generated proxy metadata。teardown 只有在 Unity Destroy 成功后才移除持久对象登记；故障清理即使组件条目已经清空、只剩 persistent root，也不会提前返回。定向回归 `96/96`，含 Windows native test DLL 的全量 `719/719`。NDK `25.2.9519653` 的完整 `build_android_single.ps1 -Configuration Release -Dex -RuntimeAssets -RewrittenOracleDefault` 通过：JNI helper exports `112/112`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0；`classes6.dex` 单独编译及 keep audit 通过。最终 SO 大小 2,840,688 bytes，Build ID `7cad0ae07bd7009e02738ef71e9a4afeef9e22c3`，SHA-256 `498652CAA0FD33233360109C81FC21ED1B796CA27084DF0693AF72D38449A05D`；runtime `StArray.ModManager.dll=467ADBCD01E9B29D3BEA89D8BFEC32A1F6A5FFD0F3E426E05C192E220BE714F8`、`StArray.ModManager.Android.dll=626627530A2C7AF626CA9FAD904351BB3A3356BF51FBEE3522E7445F2843B1E1`。部署必须同步 managed runtime、SO、主 dex、Activity `classes6.dex` 和 rewrite cache 版本。仍需从完整重启 APP 的干净基线实机执行 `load -> 显示 KV -> unload -> load`，确认黑块和旧 root 均不再保留；设备确认前该项保持待验收。

### 2026-07-30 PcCompat 进程期 Hook 的逻辑卸载门禁修复

上述 DDOL owner teardown 首轮实机不生效，后续现象显示旧黑块 Canvas 的图层高于重载后新 KV。源码回查确认直接断点不在 bridge：`ModLoader.UnloadMod()` 发现 `HookHelper.HasProcessLifetimeHooks(mod.Id)` 后沿用旧策略，写入 `Suspended without OnUnload` 并立即返回；`PcCompatModPlugin.OnUnload()` 从未执行。因此 `PcCompatRuntime.UnregisterMod`、managed session `Dispose/OnDisable`、DDOL root Destroy 和 logical native bundle retire 全部被绕过，资源退役后旧 Canvas 仍存活并显示为高层黑块。DDOL bridge 是必要兜底，但此前没有执行机会。

新增 `ILogicalProcessLifetimeHookRetirement` 合同：只有能够在 `OnUnload` 中退役全部 MOD-owned logical callback/rule，同时保证进程期物理 detour、trampoline 和静态 delegate root 继续有效的 host plugin 才能实现。`PcCompatModPlugin` 声明该合同，ModLoader 对它执行完整 `OnUnload`；普通拥有不可卸载 Hook 的 MOD 仍保持原 suspend 行为，不调用不安全的 `OnUnload`，也不释放 delegate root。这样卸载顺序恢复为 `logical native retire -> managed session Dispose/OnDisable -> DDOL root Destroy -> VirtualBundle/resource retire`，重载继续复用永久 dispatcher，不物理 unhook。

失败基线证明旧 ModLoader 对声明逻辑退役的插件仍保留原实例且 `UnloadCount=0`；修复后永久 Hook lifecycle 定向 `6/6`，并直接断言 `PcCompatModPlugin` 实现该合同。含 Windows native test DLL 的全量回归 `721/721`。NDK `25.2.9519653` arm64 单包构建通过，JNI helper exports `112/112`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 大小 2,840,688 bytes，Build ID `c0000bb6828f2ec03531fd0f025984b13fc34b5b`，SHA-256 `6171CA91CDB3CF5B26770588766CD60078E0243D8857782AB5C02C66F5D53EAF`；runtime `StArray.ModManager.dll=732498866D0F45900438E1199AF458DF86A586939866E722783558B5C06BCA07`、`StArray.ModManager.Android.dll=7113C2A74E2BD0994CEEC8AFBB4333FC6E70053CD9179B90F297B657F5874CBC`。实机仍须从完整重启 APP 的干净基线执行 `load -> 显示 KV -> unload -> load`；旧进程中由失效版本遗留的 Canvas 不受新生命周期追溯管理。

因第二轮实机仍无改善，增加有界诊断前缀 `[DEBUG-kv-unload-v1]`。每次卸载记录 ModLoader 的 `request/route`、plugin unload 入口和出口、runtime native retire、session dispose、`unityMain` 与 generation；managed component bridge 在 DDOL 时记录 `persistent-registered/forward-only`，在 teardown 时记录 `session-clear-empty/enter/detached`、每个持久 root 的 native Destroy 与 registry retire 结果。该诊断不在 Update/OnGUI 或输入热路径输出；一次加载最多一条持久 root 登记，一次卸载约 8 至 12 条。下一轮只需按此前缀截取，即可区分未部署新 runtime、重写未命中、错误线程清理、session 缺失和 Unity Destroy 失败。

### 2026-07-30 PcCompat 卸载事务 UnityMain 调度

实机诊断已给出最终决定性证据：ModLoader 正确选择 `route=onunload`，session generation 2 中存在 `entries=2 persistent=1`，但 `runtime-unregister-enter` 与 `session-dispose-enter` 均为 `unityMain=False tid=10`；bridge 随后进入 `session-clear-detached`。因此此前门禁和 DDOL rewrite 均已生效，真正残留来自旧的非 UnityMain cleanup 策略：它从 registry 删除 component/persistent 引用，却禁止调用 Unity lifecycle 与 `Object.Destroy`，使旧 Canvas 成为不可追溯孤儿。

`PcCompatRuntime.UnregisterMod` 现在在 Android context probe 为 false 时，把**整个卸载事务**提交到既有 `PcCompatResourceBundleLoader` UnityMain work queue，并同步等待完成；不只单独调度 Destroy，从而保持 native logical retire、managed session Dispose/OnDisable、DDOL root Destroy、VirtualBundle/resource retire 的原子顺序。队列拒绝或 5 秒内尚未开始执行时，pending work 被取消且卸载失败关闭，ModLoader 不切换为未加载；若事务已开始则等待其完成并原样传播异常。禁止通过手工 `Enter()` 在未知线程伪造 UnityMain。诊断新增 `unitymain-unregister-queue/run`，成功实机链必须在 `run` 后看到 `runtime-unregister-enter unityMain=True`，且不得再出现 `session-clear-detached`。

新增回归证明后台卸载只在调度后的 UnityMain context 调用 native retire，以及队列拒绝时 retire 调用数保持 0；定向 `3/3`，含 Windows native test DLL 的全量 `723/723`。NDK `25.2.9519653` arm64 单包构建通过，JNI helper exports `112/112`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 大小 2,840,688 bytes，Build ID `54abf443001076504a5dc61d83845dab69667664`，SHA-256 `6C779CC3124CD8818FB90249209BC44864EDF974DFCB93E47A5B1465B34B93DD`；runtime `StArray.ModManager.dll=2443BCF2EBC7CEB35E3D6D288A32D7DF1038E520F32872BF4CE311A3D8411F36`、`StArray.ModManager.Android.dll=7B2353884C3F49C2FEF37CA5A4DC6FE8E753F904A7505EB97FC9652849841AD1`。仍需完整重启 APP 后复验同一 `load -> unload -> load` 序列。

### 2026-07-30 UnityMain 线程身份与卸载自等待修复

首版同步调度在卸载时触发 `TimeoutException`，随后 ImGui render callback 报错并出现 `igRender -> OnSwapBuffers -> HookEglSwapBuffers` stack overflow，CoreCLR 主动 abort。tombstone 明确标记崩溃线程名为 `UnityMain`，说明调用线程本来就是 UnityMain；`PcCompatUnityMainExecutionContext.IsActive=False` 只代表当前不在 managed frame/OnGUI scope，不能证明它是后台线程。首版把工作排回同一线程并同步等待，形成确定性 5 秒自锁；timeout 异常再越过 ImGui unmanaged callback 边界，导致二次致命故障。

Android managed self-render bridge 已由真实 frame/OnGUI 回调记录稳定的 UnityMain managed thread id，并新增当前线程身份 probe。卸载时若 context 已 active，直接执行；若 context inactive 但 thread id 与已验证 UnityMain 一致，则记录 `unitymain-unregister-inline`，临时建立受控 scope 并原地执行完整事务，不排队、不等待；只有身份不匹配的真实后台线程才进入 UnityMain work queue。未知线程禁止伪造 scope。ModManager UI action 同时捕获 load/unload 异常、写入 `LoadError` 并记录错误，禁止异常再次逃出 ImGui render callback。

新增回归锁定“已验证 UnityMain 原地执行、scheduler 调用数为 0、native retire 仍观察到 active context”，并保留后台调度与队列拒绝合同；定向 `9/9`，含 Windows native test DLL 的全量 `724/724`。NDK `25.2.9519653` arm64 单包构建通过，JNI helper exports `112/112`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 大小 2,840,688 bytes，Build ID `fe7c2a6ba8c04d5db1ce2ea4aeb2fb8b70ced902`，SHA-256 `0C66ED225EEC9972FE8161F406612B40747E1BDA4E309423F486C15B92895781`；runtime `StArray.ModManager.dll=AFEB515254F3A96755360BF05F43AEEFF252A8D5625318463EABF2526311E13E`、`StArray.ModManager.Android.dll=AB2113344825C1631D154AC35980DC1920BD099B7ADFEF89BFF9D720B099ECC7`。成功实机链应出现 `unitymain-unregister-inline`，随后 `runtime-unregister-enter unityMain=True`，且无 `unitymain-unregister-queue`、timeout 或 `session-clear-detached`。

### 2026-07-30 Jipper TBPM planet speed 反向补丁链修复

Jipper 的 `Overlay.UpdateBpm()` 与 `JOverlay.UpdateBpm()` 都直接计算 `scrConductor.bpm * song.pitch * VersionSafe.GetPlanetSpeed(scrController.instance)`。Android rewrite 已把 `VersionSafe.GetPlanetSpeed(scrController)` 改写到 `PcCompatReversePatchBridge.GetPlanetSpeed()`，但 Android snapshot refresh 此前把 `PcCompatGameSnapshot.PlanetSpeed` 明确写死为 `0`，所以无论官方 BPM 和 pitch 是否正确，最终 TBPM 都必然为 `0`。这和 native `record_bpm_snapshot()` 发布的 `TileBpm/Kps` 不是同一条读取链；后者存在不能证明 MOD 自身的 reverse-patch 返回值有效。

overlay snapshot ABI 已从 v3 升到 v4，在尾部追加独立的 `float planet_speed`，native 从 metadata 解析的 `PlanetarySystem.speed` 字段读取并发布该值；`speed_multiplier = song.pitch * planet_speed` 继续保留给 play-stats session key，两者不混用。native reader 继续接受 236-byte v3 和 160-byte v2 请求；Android P/Invoke 使用 240-byte v4，并把 `PlanetSpeed` 依次传入 `PcCompatOverlaySnapshot`、`PcCompatGameSnapshot` 和 reverse patch。reverse patch 对 `0`、负数、NaN 和 Infinity 统一回退 `1.0`，避免 session reset 到首次 telemetry publication 之间的短窗口再次把 TBPM 乘成零。

失败基线分别证明旧 native/managed 链缺少 `planet_speed` 且 bridge 包含 `PlanetSpeed = 0`，以及四种非法速度会原样泄漏。修复后定向 `7/7`、managed 全量 `729/729`。`build_android_single.ps1 -Configuration Release -Abi arm64-v8a -Dex -RuntimeAssets -RewrittenOracleDefault` 使用 NDK `25.2.9519653` 通过，JNI helper exports `112/112`，closure 为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 大小 2,840,880 bytes，Build ID `a5cf47e012c3786cbe2fc40879ae8e67286167ba`，SHA-256 `33E5A3E385A1C3D1242D39D48F47F5BA18BAF1DE79973475378F9E72BB2440DE`；runtime `StArray.ModManager.dll=3792F4BC220F4BE53BEADA460205758FB3A245FC4521F29DAB470EB62EF62BBC`、`StArray.ModManager.Android.dll=ECA07E7616473F89D875491F13A0413548F9D789CCEE5017D254DD76E32059FB`。实机应在有效关卡首拍后显示非零 TBPM；变速关卡还需单独确认游戏是否在运行期改变 `PlanetarySystem.speed`。

### 2026-08-17 Android MOD Interop 与 VirtualInput V2 完成

Android MOD 联动已从 Replay/Jipper 私有反射协议扩展为 ModManager 所有的 generation-aware `ModInteropBroker`。生产合同包括公共/私有/AllowList/真实 `DependenciesOnly` 可见性、发布与订阅热更新、固定两个共享 worker、`SerializedWorker`/`UnityMainBatched` 派发、单消费者熔断、值类型 VirtualInput V2、版本化字节载荷和异步 RPC。RPC 使用 correlation ID、调用方/Provider 生命周期取消、Single/FanOut/Targeted 选择，配额为单调用方 16、全局 128；普通载荷和响应上限为 32 KiB。队列同时受事件单位和 16 MiB 分类内存预算约束，VirtualInput 上限按事件数计算为 8192，禁止 512 事件批次通过“按批次数计费”放大积压。

`ModRuntimeSession` 在 load generation 建立时固化可信 `ModEntry.Dependencies`，所有 Publisher、Subscription、RPC 和 playback lease 都绑定 `ModRuntimeKey`。卸载、失败和热更新会退役旧 generation，取消活跃虚拟会话并清除静态 handler 引用；发布者退役保留 `Cancelled/Ended` 控制批次，只删除普通旧消息。晚加入消费者的 `Started/Snapshot`、held 状态和后续事件现在在同一 Broker 注册表临界区排序，消除了并发枚举和“事件先于会话开始”窗口。

Replay Mobile 同时发布 V2 与既有 V1：V2 事件保留录制的键盘/触摸相对时间，按 512 条拆批；V1 API、事件和旧 Jipper 行为继续保留。新 Replay 会在同步派发 V1 `PlaybackStarted` 前建立 V2 lease，并公开只读 `IsVirtualInputV2Active`；新 Jipper 仅在自身 V2 订阅可用时据此抑制 V1，覆盖 Broker `Started` 回调延迟并避免首批输入重复。V2 建立或订阅失败时不会置位该抑制状态，Jipper 自动继续 V1 fallback；该状态按播放会话缓存，V1 高频事件不执行逐事件反射。

PcCompat 使用单进程 VirtualInput Adapter Hub，不写入 native raw journal。Hub 对每个已验证 Adapter 广播完整批次，每个 Adapter 再向全部 feature 投递；实体输入在 V2 会话期间停止消费，结束后提升 publication generation 并从 journal 尾部重新开户。新 Adapter 的会话同步已改为通过 Hub Actor 排队，结束竞态不会再插入过期 `Started/Snapshot`。Hybrid 键盘/触摸按 contact 计数合并，同 lane 只产生一次 down/up；snapshot 只重建 held，不补记 count、KPS 或 rain。

PcCompat KeyViewer scanner 版本为 `keyviewer-behavior-scan-v6-statistics-transaction`，验证 Held、per-key count、total、KPS queue/state、持久化 dirty/pending 和唯一 Save sink。Ephemeral 事务在开始时快照字段与受限持久化文件，在正常结束、取消、熔断、Adapter fault 和卸载时恢复，再调用原 Save sink；角色闭包不完整时 consumer fail closed。Save sink 暂时失败不会把事务错误标记为已恢复，后续 fault/dispose 路径仍可重试。

本机验证：Interop/PcCompat 定向回归 `49/49`，未过滤托管全量 `919/919`；Replay Debug 和 Jipper Debug 均以当前主程序集重新构建，结果为 `0` 警告、`0` 错误。`build_android_single.ps1 -Configuration Debug -RewrittenOracleDefault` 的 arm64 单包构建通过，JNI helper exports `122/122`，runtime manifest 为 221 entries，root `bb3daf4224cbf576782f8d6559375bca65c5c30493cc95653ccf4f9cd77824fc`；源码 Debug DLL、Android runtime asset 和 `out/android_single` 的 `StArray.ModManager.dll` SHA-256 均为 `08AD53E4B3755439821C7CCCEC5DEA2575D65FF0D9118EE48F3EE2857B9DE166`，最终 `libstarray_modmanager.so` SHA-256 为 `028401DC2EFAE8E922063B99DB7EA4CDDB9FF9D9ED4448A99C058402A4D99B4D`。没有执行实机、ADB 或安装验证，因此设备上的长时序、多 MOD 热更新和真实 Replay -> Jipper/JRP 显示仍属于实机验收项，不影响源码实现状态。

### 2026-08-19 Harmony 动态生命周期与调用参数语义补齐

Harmony/JALib callback registry 的活性检测已从“记录数量变化 + 60 帧节流”改为当帧 revision 检测。Harmony 优先读取每次 Patch、Unpatch、Clear 都递增的 `Revision`；旧 registry 和 JALib 回退到各自的变更计数器。host 在 session 建立时把静态 getter 编译成 `Func<int>`，逐帧只折叠无分配 FNV stamp，不执行反射。这样 Unpatch 时即使历史记录数不变，callback dispatcher、Prefix/Postfix order plan 和 active 门控仍在同一帧更新；随后 Repatch 也不会错过一次性 Awake/Start 事件。

deferred UnityMain Postfix 现可读取最多 6 个 primitive/enum/generated-proxy 参数组成的 `object[] __args` 快照，以及 bool/int/enum 按值 `__result`。回调修改数组不会回写已完成的 native 调用；目标签名含 `ref/out`、Postfix 声明 `ref/out __result` 或槽位属于未知 struct 时在绑定期失败关闭。同步 Prefix 保持原有可写 `__args` 与 primitive/enum `ref/out` 行为，不因 Postfix 子集放宽而降低 ABI 检查。

同步 Prefix 同时补齐 generated-proxy `ref/out __instance`。绑定要求代理具备 `(IntPtr)` 构造和可读 `Pointer:IntPtr`，managed callback 返回后把新指针写回 96 B invocation frame。native 再复制到 `FixedOpArgs.instance`；全部 12 个实例 dispatcher 在调用 original 前刷新 `self`，两个 static dispatcher 不参与。deferred Postfix 的 `ref/out __instance` 继续失败关闭，因为调用已经返回，无法提供 Harmony 的调用方写回语义。

本地生产语料复查确认 JPOV/JPKV 没有 Finalizer、Transpiler、byref struct 或 deferred Postfix 写回用法；JPOV 的动态 `CreateClassProcessor().Patch/Unpatch` 是本轮 revision 修复直接覆盖的路径。手动程序化 Prefix/Postfix 注册 API 存在但当前无调用点，不能据此猜测静态 target。剩余行为缺口为受控 struct-byref、同步 Postfix 写回、Finalizer/Transpiler 原生执行和通用程序化 target 恢复。

验证结果：Harmony 定向 `148/148`；managed 全量 `971` 通过、`1` 个既有 XPerfect 条件测试跳过；Android `:library:assembleDebug` 成功，CMake arm64-v8a 与 managed runtime 均为 `0` 错误。未执行实机、ADB、安装或 root 操作。

### 2026-08-19 Android Managed MOD 异步生命周期隔离

新增 `StArray.ModManager.Runtime.ModRuntimeAsyncBridge`，并把 Android Managed MOD 的 shadow rewrite ABI 升级为 `starray-native-isolation-rewrite-v3-async-domain`。入口为 `Task`/`Task<T>` 的 MOD 自有方法现在在方法入口验证当前 `ModRuntimeKey` 与 `ModDataDomainToken`，返回未完成任务时登记 operation lease，任务完成、取消或失败后单次释放；返回对象本身不替换。这样直接 `async Task` 方法也参与卸载 quiescence，不要求 MOD逐个手写 `ModRuntimeOperations.Begin`。

`Task.Run` 的 Action、Func<Task>、泛型结果及 CancellationToken 重载，常用 `Thread` 构造/Start，`ThreadPool.QueueUserWorkItem`（含泛型 state）和常用 `Timer` 创建/Dispose/DisposeAsync 重载会被改写到桥。桥在 callback 内显式恢复 owner、session 和 domain scope；Thread 在 `Start` 而非构造时登记 operation，Timer 在构造时登记 terminal cleanup，避免“只构造未启动”或卸载期间产生新的 Timer callback。

生成期失败关闭覆盖 `async void`、ValueTask、TaskFactory.StartNew、Task.ContinueWith、手工 Task.Start、ExecutionContext.SuppressFlow、未覆盖调度重载、SynchronizationContext callback、CancellationToken.Register 和 Parallel callback。每个 shadow assembly manifest 保存异步 proof，缓存 marker 绑定新 ABI，旧 shadow 不能复用。

验证：异步 bridge、线程/线程池/Timer 退休竞态、跨 owner 拒绝及陈旧 ExecutionContext 定向 `7/7`；Native shadow rewrite/cache/ValueTask 拒绝定向 `8/8`（合计 `15/15`）；真实 Android DLL 只读审计中 Replay/System.Formats.Nrbf `0` 问题，ShowBPM/XPerfect 各 `4` 个 Task 返回方法、`0` 未覆盖入口。Android Managed Debug/Release 构建和 ModAssemblyRewriter 构建均通过。未执行实机、ADB 或安装验证。

### 2026-08-21 Android Managed MOD 文件路径 domain 隔离

`ModDataDomain` 新增 `ModDataDomainPathRoots` 绑定，Android Managed MOD 的 shadow rewrite ABI 升级为 `starray-native-isolation-rewrite-v4-file-domain`（旧 v3 shadow cache 自动失效）。每个 domain 拥有 `InstallRoot/ConfigRoot/CacheRoot/LogRoot/TempRoot` 与可选共享只读根：MOD 原目录只读（执行仍走 shadow 包），四个可写根位于 Host 拥有的 `Mods/.starray-data/<mod>/` 之下，`.starray-data` 与 `.starray-shadow` 一样被 MOD 扫描器排除。

`NativeModPathBridge` 从只有 `GetAssemblyLocation` 扩展为完整文件边界：`GetFullPath/ResolvePath/ResolveWritablePath`、`File*`、`Directory*` 和 `OpenFileStream*`。相对路径不再解析到共享的进程 CWD，而是锚定当前 domain 的 config 根，这也是 `Path.GetFullPath` 必须改写的原因；`Path.Combine/GetDirectoryName/GetFileName` 是纯字符串函数，刻意不改写并在代码注释中说明，避免后人误判为遗漏。跨 MOD 根访问在产生任何文件副作用前拒绝，并通过 `ModDataDomainRegistry.TryFindForeignPathOwner` 在诊断中点名归属 owner/generation；无 domain、未绑定根、越界、`..` 遍历、兄弟目录前缀和退休 generation 一律失败关闭。包含判定按路径分隔符边界比较，`…/config-evil` 不会被当作 `…/config` 的子路径。

改写覆盖 `Path.GetFullPath`、`File.Exists/Delete/Copy(2,3)/Move(2,3)/ReadAllBytes/OpenRead/GetLastWriteTimeUtc/WriteAllText(Encoding)`、`Directory.Exists/CreateDirectory/Delete(1,2)/EnumerateFiles(3)` 和 `FileStream` 的 2/3/4/6 参构造。生成期失败关闭覆盖 `File.ReadAllText/AppendAllText/Open/Create` 等未覆盖入口、`FileInfo`/`DirectoryInfo`、`StreamReader/StreamWriter` 路径构造、`Path.GetTempPath/GetTempFileName/GetRandomFileName`、`Environment.GetFolderPath/CurrentDirectory` 与 `Directory.SetCurrentDirectory`；强签名程序集需要 file 改写时同样失败关闭。每程序集 shadow manifest 新增 file proof（方法身份、种类、计数），发布与缓存命中都复核。共享的 `CallRewritePlan`/`RewriteCallSites` 由异步侧改名复用，未复制第二份改写循环。

切片选择依据真实 DLL 只读 IL 审计，而非文档候选排序：四个真实 Android Managed MOD 的声明事件、`add_`/`remove_` 访问器与 `Delegate.Combine/Remove` 全为 `0`，直接创建 GameObject 与动态程序集加载亦为 `0`，而文件调用点为 `95`、`HttpClient` 为 `12`。字面量证据显示 XPerfect/ShowBPM/Replay 都在做自更新（下载 zip、暂存、`File.Copy/Move/Delete` 替换自身 DLL），跨 MOD 破坏风险真实存在。

真实 DLL 审计过程中发现并补齐 5 个此前遗漏的重载：`FileStream(String,FileMode,FileAccess,FileShare,Int32,FileOptions)`、`File.OpenRead`、`File.GetLastWriteTimeUtc`、`File.WriteAllText(String,String,Encoding)`、`Directory.EnumerateFiles(String,String,SearchOption)`。补齐前这三个在用 MOD 会因失败关闭而无法加载——这正是「每个新入口必须有真实 DLL 审计」的价值。

验证：全量托管 `1029/1030` 通过（`1` 项既有 XPerfect 环境测试跳过），改动前基线 `1014/1015`，新增 `15` 条测试且无回归；`NativeModPathBridgeTests` + `NativeModShadowRewriteTests` 定向 `23/23`；真实 DLL 审计 XPerfect `24`、ShowBPM `22`、Replay `24`、System.Formats.Nrbf `0` 处改写、`0` 未覆盖入口，既有 static/async/location 计数与本文既有记录一致（Replay `645`、ShowBPM `199`），说明未破坏既有改写。Android Managed Release 构建通过，`git diff --check` 通过。未执行实机、ADB、安装或 Gradle/CMake/NDK 产物构建。

下一垂直切片：网络会话按 domain 隔离（`HttpClient` Cookie/Header/认证/代理/证书/超时/连接池身份 + 在途请求 lease），真实审计已定位 XPerfect 与 ShowBPM 各 `6` 处调用点。

### 2026-08-21 Android Managed MOD 网络会话 domain 隔离

新增 `StArray.ModManager.Runtime.ModRuntimeNetworkBridge`，shadow rewrite ABI 升级为 `starray-native-isolation-rewrite-v5-net-domain`（v4 及更早 cache 自动失效）。每个 `ModDataDomain` 首次用网时惰性建立独立网络身份：自有 `CookieContainer` 与 handler 管线，因此两个 MOD 不共享会话 Cookie、凭据或连接池。

改写只作用于**产生 client 的构造点**：`new HttpClient()`、`new HttpClient(HttpMessageHandler[, bool])`、`new HttpClientHandler()`、`new CookieContainer()`。已绑定 domain 的实例操作（`GetAsync`、`DefaultRequestHeaders`、`Timeout`）与其返回对象（`HttpResponseMessage`、`HttpContent`、header value）继承该 client 的 domain，刻意不改写并在代码注释中说明理由——与 `Path.Combine` 不改写同理。真实审计验证了这一判断：两个 MOD 各 6 处 `HttpClient` 调用点中只有 1 处是构造。

MOD 拿到的 client 外层套 Host `DelegatingHandler`：每个请求先校验调用方 owner，再取得 generation-bound operation lease，并用 `CreateLinkedTokenSource` 把 lease 取消令牌与调用方请求令牌联结。因此 MOD 卸载会取消在途请求并参与 quiescence 等待，退休 generation 拒绝新请求，另一个 MOD 用该 client 发请求会在发出前被拒绝并点名归属 owner。domain 退休时 terminal cleanup 调用 `CancelPendingRequests` 并释放该 generation 的全部 client。

生成期失败关闭覆盖 `ServicePointManager`（进程全局网络策略）、`WebRequest/HttpWebRequest` 工厂、`WebClient`、`SocketsHttpHandler`、`System.Net.Sockets.*` 原始套接字和未覆盖的 client/handler/cookie 构造重载。每程序集 shadow manifest 新增 network proof（方法身份、种类、计数），发布与缓存命中都复核。

为避免复制安全敏感的 domain 校验逻辑，把 `ModRuntimeAsyncBridge` 的私有 `CapturedRuntime`/`OwnedOperation` 抽成 internal 共享类型 `ModRuntimeCapturedScope`/`ModRuntimeOwnedOperation`，网络桥复用同一套 staleness、跨 owner 与 lease 规则；异步桥改为调用共享类型后 `ModRuntimeAsyncBridgeTests` `17/17` 仍通过，行为未变。

验证：全量托管 `1039/1040` 通过（`1` 项既有 XPerfect 环境测试跳过），文件切片后基线 `1029/1030`，新增 `10` 条测试且无回归；`ModRuntimeNetworkBridgeTests` + `NativeModShadowRewriteTests` + `NativeModPathBridgeTests` 定向 `33/33`；真实 DLL 审计 XPerfect `1`、ShowBPM `1`、Replay `0`、Nrbf `0` 处网络改写、`0` 未覆盖入口，文件改写计数保持 `70` 不变，既有 static/async/location 计数未变。Android Managed Release 构建通过，`git diff --check` 通过。未执行实机、ADB、安装或 Gradle/CMake/NDK 产物构建。

「文件、设置与网络隔离」章节的核心合同至此闭合。该章节剩余项：旧路径既有配置不自动迁移到新 domain config 根（需显式 migration 合同）、`WebRequest`/`WebClient` 兼容路径当前失败关闭而非改写、每 domain 代理/证书回调/重试策略与请求日志分区、独立 `host` 网络域实现。

### 2026-08-21 PcCompat MOD 直接创建 Unity 对象的 owner 登记

补做了此前缺失的 **PcCompat 侧**真实 DLL 审计——前两个切片（文件、网络）只审计了 Android Managed MOD，而文档首要交叉加载目标 XPerfect + JipperResourcePack 的另一半走的是不同改写管线。Jipper 的结果与 Android Managed MOD 完全不同：`GameObject.AddComponent<T>` 38 处、`Object.Destroy` 6 处**已**改写到 `PcCompatManagedComponentBridge`，但 `newobj GameObject::.ctor(String)` 19 处与 `Object.Instantiate<T>` 2 处**未登记**。

即环是断的：对象创建时无主，销毁时却要查 owner lease。`Destroy` 已在调用 Unity API 前校验归属，但 MOD 自己 `new GameObject(...)` 造出的宿主对象从未进入登记表，因此不在 owner 审计快照里、teardown 无法据此清理、跨 MOD 销毁保护对它们不成立。

`PcCompatManagedComponentBridge` 新增 `CreateGameObject(string)` 与 `Instantiate` 两个重载，复用既有 `NativeObjectLease` 与 session teardown。对象在返回 MOD 之前登记；登记失败销毁刚创建的对象再抛，不留下无主 Unity 对象。Instantiate 只认领克隆体，原型保持借用语义。`PcCompatManagedComponentHostOperations` 新增 `CreateNativeGameObject` / `InstantiateNativeObject`，Android 侧用既有 `PcCompatIl2CppInteropBootstrap.TryGetProxyType` + `Expression` 编译模式绑定；`Instantiate` 的 parent 为 null 时路由到单参重载，不传 null Transform。

改写器新增两项通用能力（不是本切片专用 hack）：`ManagedCallBridgeRewriteSpec.SourceIsConstructor` 使 spec 匹配 `newobj`；`newobj T::.ctor(args)` → `call Bridge(args) : object` 栈平衡不变（两者都弹参数压一引用），既有 `AllowObjectReturnCast` 插 `castclass` 还原类型。`EraseBridgeGenericArity` 允许一个非泛型桥服务泛型源重载。过程中发现桥 arity 计算在两处重复（`ManagedCallBridgeSignatureMatches` 与候选过滤），两处都需对齐——只改一处会得到"找到 0 个兼容桥"的误导性诊断。

**刻意不做**：Jipper 的 `RectTransform.set_*`（114 处）与 `Transform.set_localScale`（12 处）不做 property contribution。审计确认它们作用在 MOD 自己创建的对象上，是私有布局状态；文档要求 contribution 的是多个 MOD 争用同一持久共享属性，与此不同。盲目建通用 property registry 会做错东西。

验证：全量托管 `1047/1048` 通过（`1` 项既有 XPerfect 环境测试跳过），网络切片后基线 `1039/1040`，新增 `8` 条测试且无回归；`PcCompatManagedComponentBridgeTests` + `PcCompatManagedBridgeRewriteTests` 定向 `86/86`；真实 JipperResourcePack 改写审计 `19` 处 GameObject 构造 + `2` 处 Instantiate 全部命中，改写后无裸 `GameObject::.ctor` / `Object::Instantiate` 残留，`ManagedBridgeIssues` 为空，既有 `AddComponent`/`Destroy` 改写保持接通。Android Managed Release 构建通过，`git diff --check` 通过。

缓存 ABI 递增 `v30-ddol-owner-teardown` → `v31-created-object-registration`，bridge ABI `PcCompatManagedComponentBridge.v6` → `v7-created-objects`，旧 PcCompat 托管缓存自动失效。`PcCompatAndroidInputContractTests` 硬编码的 ABI 断言同步更新——该契约测试的目的正是让 ABI 变更必须被显式确认，它的失败是设计意图而非噪声。

未执行实机、ADB、安装或 Gradle/CMake/NDK 产物构建。阶段 4 剩余项：跨后端统一 `UnityObjectLease` API（当前 ResourceChanger / VirtualBundle / HUD / component bridge 仍是四套 registry）与任意 component property contribution registry。

### 2026-08-21 PcCompat MOD 文件路径 domain 隔离

把文件隔离扩到第二条改写管线。`PcCompatManagedPathBridge` 是 `NativeModPathBridge` 的对应物，两者不能合并成一份实现：归属键不同——Android Managed MOD 从 domain token 解析 `ModDataDomain`，PcCompat MOD 携带 `PcCompatManagedExecutionState(ModId, ResourceSessionGeneration, Phase)`。但包含判定复用同一个 `ModDataDomainPaths.IsWithin`，安全敏感的路径比较只存在一处。

每个会话绑定五个根：`InstallRoot` 为 MOD 目录（只读），四个可写根在该目录下的 `.pccompat-data/`。解析顺序先匹配可写根再匹配只读安装根，因此嵌套关系不冲突。可写根**不按 generation 分目录**，否则 MOD 重载后设置会消失；generation 绑定由 roots 注册表键与 `Disable` 时的 `ClearRoots` 保证，退休 generation 因"根未绑定"失败关闭。这与 Android Managed 侧把可写根放在 `Mods/.starray-data/<mod>/` 不同——PcCompat 会话只拿到 `Manifest.FolderPath`，没有 Host 级 mods 根，是有意的不对称。

改写规格最初覆盖 `Path.GetFullPath`、`File.Exists/ReadAllText/ReadAllBytes/WriteAllText/WriteAllBytes/Delete/Copy(2,3)/Move/OpenRead/OpenWrite`、`Directory.Exists/CreateDirectory/Delete(1,2)` 与 `FileStream` 2/3/4 参构造。`Path.Combine/GetFileName` 与 `Stream`/`MemoryStream` 实例方法仍保持原样；`Path.GetDirectoryName` 后续因 UMM `ModEntry.Path` 是虚拟包根而纳入 owner-scoped VFS，见本文 2026-08-26 的 JPKV 虚拟包根修复。

过程中踩到两个陷阱，都值得记录：

1. **spec 的 `SourceAssembly` 必须写运行时实际的程序集名**。我按 .NET 现代命名写了 `System.Runtime`，但 Jipper 引用的是 `mscorlib`，导致 19 条规格全部零匹配。零匹配不会报错——`ManagedCallSourceMatches` 只是不返回候选，改写静默跳过。因此测试里钉住了具体命中数（14）而不是只断言"非空"，否则一次程序集名写错就会静默退化成不隔离。
2. **python 文本写入在 Windows 上会把 LF 转成 CRLF**。仓库全仓是 LF，我用 `io.open(..., 'w')` 编辑的 14 个文件被整体转成 CRLF。`git diff --check` 不检测这个，编译和绝大多数测试也不受影响；抓住它的是 `PcCompatManagedEventResilienceContractTests` 那条用 `
` 字面量断言源码片段的契约测试。已全部还原为 LF，后续 python 写入统一带 `newline=''`。另有 4 个文件（`HookHelper.cs`、`PcCompatCallbackTranslator.cs`、`PcCompatRecipeCompiler.cs`、`PcCompatRecipeCompilerTests.cs`）本来就是混合行尾，属前序未提交工作，未触碰。

验证：全量托管 `1060/1061` 通过（`1` 项既有 XPerfect 环境测试跳过），对象登记切片后基线 `1047/1048`，新增 `13` 条测试且无回归；`PcCompatManagedPathBridgeTests` 定向 `12/12`；真实 JipperResourcePack 改写 `14` 处（`FileExists` 6、`OpenFileStream` 2、其余各 1），改写后无裸 `File`/`Directory`/`FileStream..ctor`/`Path.GetFullPath` 残留，`Path.Combine` 按设计保留，`ManagedBridgeIssues` 为空。Android Managed Release 构建通过，`git diff --check` 通过。未执行实机、ADB 或安装验证。

下一候选：PcCompat 网络 domain 化。真实审计显示 JipperResourcePack 主 DLL 无网络调用点，`JAMod.Bootstrap.dll` 有 19 处，但 Bootstrap 由 JALib 自身加载、不经过 PcCompat 托管改写管线，需先确认它是否属于本隔离合同的覆盖范围——不要假设它自动适用。

### 2026-08-22 PcCompat MOD 网络会话 domain 隔离

上一条目挂起的覆盖问题已解决，且**结论与其记载相反**：`JAMod.Bootstrap.dll` 就在 PcCompat 托管改写管线内。证据链：manifest `Info.json` 的 `"AssemblyName": "JAMod.Bootstrap.dll"` → `EntryAssemblyPath` → `PcCompatManagedAssemblyCatalog.Discover` 以其为第二闭包根（`IsBootstrap=true`）→ 重写 bundle 含重写后的 Bootstrap → 运行时 `PcCompatRuntime` 把 `RewrittenAssemblyPaths[bootstrapName]` 作为 `BootstrapAssemblyPath` 传入 → `TryInvokeBootstrap` 经 collectible ALC 加载该重写副本。「由 JALib 自身加载、不经过改写管线」的说法是错的，两份文档均已更正。教训：对"是否在覆盖范围内"这类可验证命题，应在记录为限制前先读加载链，而不是把未验证的猜测写进文档。

真实 DLL 逐指令审计（dnlib）：`JAMod.Bootstrap.dll` 共 12 处 `System.Net.Http` 引用（此前记录的"19 处"偏大），其中仅 **1 处构造点**（`Installer/<InstallMod>d__3::MoveNext` 的 `newobj HttpClient::.ctor()`），其余 11 处为已绑定实例及其响应对象上的操作；`JipperResourcePack.dll` 0 处。该构造点是 JALib 的 MOD 自更新下载器——与 Android Managed 侧 XPerfect/ShowBPM 自更新同类风险。

实现镜像 Android Managed 的 v5 切片：

- 新增 `PcCompatManagedNetworkBridge`（归属键 `PcCompatManagedExecutionState(ModId, ResourceSessionGeneration, Phase)`）。会话构造时 `BindNetworkState`，`Disable` 时 `ClearNetworkState`（取消在途请求并释放该 generation 全部 client）。
- 改写规格 6 条，只覆盖 client 构造点：`HttpClient..ctor()` / `(handler)` / `(handler, bool)` / `HttpClientHandler..ctor()` / `CookieContainer..ctor()` ×2 种声明程序集拼写。`CookieContainer` 的 declaring assembly 在 netstandard 与桌面框架下不同（`System.Net.Primitives` vs `System`），单拼写会在另一侧静默零匹配，故两种都注册。
- 跨 owner 使用 client 在 `SendAsync` 内、任何网络副作用发生前拒绝，诊断点名归属与调用方；无 scope、disable 阶段、退休 generation 均失败关闭。会话取消令牌联结进每个请求，卸载即停流量。
- `ServicePointManager`/`WebRequest`/`WebClient`/原始套接字无规格——PcCompat 管线没有 Android Managed 那样的未知入口白名单审计，这些引用会原样保留并在运行期按隔离降级诊断，不宣称被桥接覆盖。

同切片发现并修复两处缓存 ABI 债务（文件切片遗留）：

1. 文件切片的文档记载了 `v32-path-domain` 版本递增，但代码与契约测试实际停在 `v31-created-object-registration`——版本号从未真正落地。旧缓存失效当时由缓存键中的逐规格哈希兜底（每条 call-bridge 规格都进入哈希），所以没有产生陈旧缓存事故，但文档描述与代码不一致。本切片以 `v33-net-domain` 一次补上，并在 `CollectionBridgeAbi` 补齐 `PcCompatManagedPathBridge.v1` 标记。
2. 缓存键哈希遗漏了 `SourceIsConstructor` 与 `EraseBridgeGenericArity` 两个行为字段——改动它们而不同步版本号会让旧重写产物滞留缓存。两者已并入哈希行。

测试基建新增：`PcCompatManagedBridgeRewriteTests` 此前只改写主 DLL；网络覆盖全在 Bootstrap，故夹具现在同时改写真实的 `JAMod.Bootstrap.dll` 并钉住命中数。过程中确认 ReversePatch 类规格（`ManagedBridgeRewriteSpec`）零匹配会记 issue（不同于 call-bridge 规格的静默跳过），因此 Bootstrap 改写按生产语义只传 call-bridge 规格。

验证：全量托管 `1068/1069` 通过（`1` 项既有 XPerfect 环境测试跳过），文件切片后基线 `1060/1061`，新增 `8` 条测试且无回归；`PcCompatManagedNetworkBridgeTests`（7 条）+ `PcCompatManagedBridgeRewriteTests` + `PcCompatAndroidInputContractTests` 定向 `78/78`；真实 `JAMod.Bootstrap.dll` 改写 `1` 处、`0` bridge issue、无裸 `HttpClient..ctor` 残留、`GetAsync`/`get_DefaultRequestHeaders` 按设计保留；既有文件/对象改写计数不变（主 DLL 14 处路径、19+2 对象）。Android Managed Release 构建通过；`git diff --check` 通过；本切片触碰文件均为纯 LF。未执行实机、ADB 或安装验证。

下一候选：「文件、设置与网络隔离」章节剩余项（显式设置迁移合同、`WebRequest`/`WebClient` 兼容路径、每 domain 网络策略分区、host 网络域），或阶段 4 剩余的跨后端统一 `UnityObjectLease` API（四套 registry 归一，属重构，应单独立项评估）。

### 2026-08-22 外部静态事件订阅登记（历史 v1/v34 实现记录）

> 历史实现记录：本节描述 v1/v34 的首版行为。2026-08-24 已由本文顶部的 v2/v45 双向 delegate ABI 转换实现取代，尤其是下述“`remove_` 不改写”不再是当前行为。

「异步执行与静态事件」段落（卸载、失败加载与 generation 更替自动退休订阅）的首个垂直切片。切片选择依旧证据先行：对两个语料做事件订阅点审计（`add_`/`remove_` 访问器调用与 `Delegate.Combine/Remove`）——Android Managed 语料（XPerfect/ShowBPM/Replay/System.Formats.Nrbf）**0 处**，PcCompat 语料 Jipper 主 DLL **4 处、2 个 Unity 静态事件**：`SceneManager.sceneUnloaded +=/−=`（`Main.OnEnable/OnDisable`）、`Application.quitting +=/−=`（`KeyViewer.OnEnable/OnDisable`）。因此本切片只落 PcCompat 管线；Android Managed 侧无真实目标、不做。

隔离动机：这些订阅的委托经 IL2CPP 事件持有，指向 collectible ALC 里的重写 MOD 程序集。Jipper 自己配对退订是好行为，但隔离不能依赖 MOD 自觉——故障、半途卸载或 OnDisable 未执行的会话会把委托留在共享 IL2CPP 事件上：既是 ALC 无法回收的 GC root，也是退休 generation 的回调污染向量。

实现完全骑在既有改写机制上，改写器零改动：

- 新增 `PcCompatManagedEventSubscriptionBridge.Subscribe(object handler, string eventKey)`。2 条规格（`add_quitting(System.Action)`、`add_sceneUnloaded(UnityAction<Scene>)`，均 `AllowUnproxiedSource` + `AllowObjectParameterForwarding`）把 `add_` 调用改写到桥；**`AppendOwnerId` 携带 `assembly!type::event` 身份**——该机制本为 owner 内嵌而设，但"调用点内嵌常量字符串"正是事件身份需要的形状。
- `Subscribe` 先按原语义转发（MOD 观察到与原始调用相同的失败面），成功后按 `(modId, resource generation)` 登记。重复订阅逐条记录——.NET 多播语义下同一委托实例的两次 add 是两次独立调用，退休必须逐条移除。
- `remove_` 访问器**刻意不改写**：行为正常的 MOD 保持自己的配对语义；登记只兜异常路径。session `Disable` 时 `RetireOwner` 反射调用对应 `remove_` 逐条退订，单条失败 best-effort。
- 访问器经反射解析（默认 ALC 的 generated proxy 程序集），按 `(eventKey, handler 运行时类型)` 缓存；多重重载时先精确类型匹配、再唯一可赋值候选，否则失败关闭。实例事件、代理面之外的事件、裸 `Delegate.Combine` 未改写，保持可诊断隔离降级。

缓存 ABI `v33-net-domain` → `v34-event-subscription`，`CollectionBridgeAbi` 追加 `PcCompatManagedEventSubscriptionBridge.v1`。

**调试记录（值得留档）**：桥测试一度全部失败，症状是"订阅后事件无订阅者、且无异常"。逐层标记定位到 `MetadataToken` 对比才暴露根因——**元组解构写反**：`RequireAccessors` 返回 `(Add, Remove)`，而 `Subscribe` 写成 `var (_, add) = ...`（`add` 绑到第二元素 = Remove）。于是订阅实际执行了退订（静默 no-op），首次报错 `Func<int> cannot be converted to System.Action` 也是拿 remove 去调 Func<int>。教训：`(a, _)`/`(_, a)` 的视觉相似性足以骗过逐行 review；对"转发后无效果且无异常"的静默症状，应当第一时间比对 MethodHandle/MetadataToken，而不是先怀疑反射机制本身。定位过程中还连续证伪了三个假设（测试程序集双副本、`[ThreadStatic]` 上下文丢失、访问器解析错误），独立最小复现（普通控制台 + 反射 add）证明机制无误后才把嫌疑收窄到调用侧。

测试基建：新增 `PcCompatManagedEventSubscriptionBridgeTests`（6 条：无 scope 拒绝、disable 拒绝、转发并登记、退休逐条移除且幂等、按 generation 精确退休、未知身份失败关闭；测试宿主用测试程序集自身的 public 静态事件验证真实反射退订路径）；`PcCompatManagedBridgeRewriteTests` 新增真实 Jipper 主 DLL 断言：2 处 `Subscribe` 桥调用、eventKey 字符串内嵌、裸 `add_` 消失、`remove_` 2 处按设计保留。测试里未标注类型的 lambda 会被 C# 自然类型推断成 `Func<int>`（桥参数为 object），必须显式标注 `Action`——与真实改写代码中编译好的委托形态一致。

验证：全量托管 `1075/1076` 通过（`1` 项既有 XPerfect 环境测试跳过），网络切片后基线 `1068/1069`，新增 `7` 条测试且无回归；定向 `78/78`；真实 Jipper 改写 `2` 处命中、`0` bridge issue、既有文件/对象/网络改写计数不变；Android Managed Release 构建通过；`git diff --check` 通过；触碰文件纯 LF。未执行实机、ADB 或安装验证。

下一候选：同合同段落剩余项（静态事件仅覆盖 PcCompat 外部静态事件；Host/Unity proxy 事件登记、`event -=` 按 domain 移除、跨 MOD Harmony 事件语义未动），或「文件、设置与网络隔离」章节剩余项，或阶段 4 的 `UnityObjectLease` 归一。

### 2026-08-22 PcCompat owner-scoped VFS overlay（设置连续性闭合）

文件隔离切片留下的功能性缺口在源码层面实锤了：Jipper `KeyCountData.SaveData` 的保存循环是 `File.Delete(path + ".bak") -> File.Move(path, path + ".bak") -> new FileStream(path, FileMode.CreateNew)`，路径全部来自 `Path.Combine(Main.Instance.Path, "KeyCount.dat")`——安装目录**绝对路径**。v1 路径桥下这三步全部失败关闭，KeyViewer 计数在设备上首次保存即断；`Settings.json`/`Settings.json.bak`（JALib）与 `KeyCodes.json` 同样住在安装目录。这正是文档挂起的「显式设置迁移合同」，且比一次性拷贝迁移更好的解法早已写在 HUD_KEYVIEWER §4.10 的 VFS 设计里。

实现（`PcCompatManagedPathBridge.v2-vfs-overlay`，缓存 ABI `v35-vfs-overlay`）：

- `PcCompatModPathRoots` 新增必填 `DataOverlayRoot = <mod>/.pccompat-data/data`。
- **写入重定向**：凡落在安装根内的写操作（Write/Delete/Copy 目的端/Open 写访问/FileStream 非只读模式/Directory 创建删除）映射到 `<overlay>/<相对布局>` 并自动创建父目录；包层物理上不再被 MOD 触碰。
- **读取回退**：安装根内的读取先看 overlay 是否存在同名 shadow，存在则返回 shadow，否则原样读包层——旧设置无需任何迁移即可继续读取，首次保存后自然被遮蔽。`GetFullPath/ResolvePath/FileExists` 同走该有效解析，MOD 手里的字符串路径保持一致视图。
- **Move 特殊处理**：overlay 内真实移动；源仅存在于包层时以"复制到 overlay 目的地、包层原件保留"模拟（不可变层无法被移出）。这是影子语义下唯一与原始 Move 有可观察差异的点：包层源在移动后仍可读。已在验收矩阵标明；对 Jipper 的实际影响为零——其轮转发生在刚写过的 dat 上，dat 必有 overlay 副本。
- 跨 owner 拒绝、`..` 遍历、兄弟前缀、disable 拒绝等既有合同不变——映射发生在归属校验通过之后，shadow 始终在本 MOD 自己的数据根内。

刻意不做：相对路径不改走 data/package 双层（维持锚定 config 根的现状）——没有证据表明 Jipper 用相对路径访问这些文件，改语义属于无证据扩面。Android Managed 侧的同款问题（XPerfect/ShowBPM/Replay 自更新写入安装根会失败关闭）记录为下一候选切片，本切片不动那条管线。

验证：全量托管 `1077/1078` 通过（`1` 项既有 XPerfect 环境测试跳过），事件切片后基线 `1075/1076`，新增 `3` 条测试（安装根写影子化+旧文件可读、Jipper 计数轮转整循环往返、仅包层源的 Move 模拟）、移除 `1` 条与影子语义冲突的旧"安装根写拒绝"断言；路径桥/改写/契约/网络桥/事件桥定向 `99/99`；Android Managed Release 构建通过；`git diff --check` 通过；触碰文件纯 LF。未执行实机、ADB 或安装验证。

下一候选：Android Managed 侧同款安装根 overlay 映射（自更新与旧设置连续性），或章节剩余的网络策略分区/host 网络域，或阶段 4 的 `UnityObjectLease` 归一。

### 2026-08-22 Android Managed 安装根 VFS overlay

PcCompat 影子层的同款对等切片（shadow ABI `starray-native-isolation-rewrite-v6-vfs-overlay`，v5 及更早 cache 自动失效；bridge 程序集 MVID 本就参与 Android cache key，行为变更会双重失效旧缓存）。证据沿用文件切片审计：XPerfect/ShowBPM/Replay 全部自更新（下载 zip、暂存、`File.Copy/Move/Delete` 替换自身 DLL），且同样有设置/计数数据落在安装目录。

实现与 PcCompat 侧同构：`ModDataDomainPathRoots.DataOverlayRoot = .starray-data/<mod>/data`，安装根内数据文件写入映射 overlay 同名相对路径，读取 data-first/package-second。

**一处与 PcCompat 有意的分歧**：安装根内的 `.dll`/`.exe` 写入**保持失败关闭**，不进入 overlay。理由：Android Managed 的 loader 从包层（原始目录）扫描与加载，若 MOD 自替换的二进制静默落进 overlay，会形成"更新写入成功但 loader 永远加载旧程序集"的静默失效——比今天的响亮失败更差。执行物的更换按目标架构归 Host 的 package 管理（§4.10「更新 MOD 时切换 package hash 并保留 data」）；loader 按 overlay 解析 MOD 文件从而使自更新真正生效，是后续独立决策，本切片不假装解决。因此 XPerfect 的自更新链在替换自身 DLL 一步仍会失败（与 v4 行为一致、可诊断），而设置/计数持久化恢复正常。

`FileMove/FileMoveOverwrite` 改为安装感知：overlay 内真实移动；仅包层源以复制模拟（不可变层无法移出，包层原件保留，验收矩阵已标明该可观察差异）。

验证：全量托管 `1080/1081` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过）；`NativeModPathBridgeTests` `10→13`（新增：安装根数据写影子化+执行物拒绝、状态文件轮转往返且包层零触碰、仅包层源 Move 模拟、包层∪overlay 合并枚举去重；移除：与影子语义冲突的旧"安装根写拒绝"断言）；Android Managed Release 构建通过；`git diff --check` 通过；触碰文件纯 LF。未执行实机、ADB 或安装验证。

**验证流程事故（同日，留档）**：本切片首轮验证时新测试里一处 `FileCopyOverwrite` 缺参使测试工程编译失败，而后续命令只 grep 错误计数、把尾部警告误读为构建输出，随后多次"全量回归"实际运行的是上一版编译产物，得出过 `1077/1078` 的假读数（也是总数与增量记账对不上的根因）。修复后以真实产物重跑并更正数字。流程修正：**"构建+测试"命令必须显式断言构建成功后才采信测试结果**；总数与增量不符视为编译陈旧信号立即排查。

下一候选：loader 侧 overlay 解析决策（自更新闭环）、章节剩余网络项（策略分区/host 域），或阶段 4 `UnityObjectLease` 归一。

### 2026-08-22 统一 lease 审计接口（阶段 4 前半）+ 跨 MOD 发现面审计

**统一审计接口**：阶段 4 挂起的「跨后端统一 `UnityObjectLease` API」先落地其文档点名的读半边——`PcCompatUnityObjectLeaseAudit.Snapshot(modId, generation)` 把四套 registry 的 per-owner 库存聚合成一个只读快照（component bridge 宿主对象数、VirtualBundle 会话存在性、ResourceChanger contribution、HUD surface 数，按 owner+generation 精确过滤），`IsClear` 单点回答"该会话是否仍被任何后端持有"；诊断导出 per-MOD 段新增一行 `unityLease=hostObjects=N virtualBundle=… resourceChanger=… hudSurfaces=N`。归属、teardown、恢复语义全部留在各 registry 内——这是纯聚合，零所有权逻辑改动；完整的统一 API（统一销毁/恢复协议）仍是待实现项，待真实需求出现再立项。

**跨 MOD 发现面审计**（Direct Link/虚拟 MOD 目录章节的证据核查）：对六个真实 MOD DLL 逐指令审计 `AppDomain.GetAssemblies`、`Assembly.Load/LoadFrom/LoadFile`、UMM `modEntries`、JALib `GetMods/FindMod`——Android 语料（XPerfect/ShowBPM/Replay/Nrbf）**0** 处；PcCompat 仅 2 处 `Assembly.GetType` 且都解析 MOD 自有程序集内类型。结论：这两章当前没有真实调用点拉力，维持「仅设计」；记录在案，避免未来被当成"漏做"。

验证：全量托管 `1082/1083` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），Android VFS 切片后基线 `1080/1081`，新增 `2` 条测试；**构建成功经显式断言（error 计数为 0）后才采信测试结果**（上一事故的流程修正首次执行）；Android Managed Release 构建显式验证通过。未执行实机、ADB 或安装验证。

下一候选：loader 侧 overlay 解析决策（自更新闭环，涉及"MOD 自替换二进制"与"Host 拥有包内容"两种立场的取舍，需要产品决策）、章节剩余网络项、阶段 4 统一 API 后半，或下一代合同其余章节（均无当前真实调用点拉力，按纪律维持仅设计）。

### 2026-08-22 下一代合同证据矩阵闭合（反射 + P/Invoke 审计）

继发现面审计后，把「下一代隔离合同」剩余可证据化章节全部核查完毕：

- **反射 metadata facade**：Jipper 的 10 处 `Type.GetField` 全部带 `typeof(scrController)/scrMistakesManager/ADOBase` 提示——是 r136 版本兼容回退在反射**游戏**类型，不是自有静态字段；Bootstrap/Replay 以方法反射为主（`GetMethod`/`Invoke`），不受 field-cell 改写影响。"静态 cell 使自有静态字段的反射读到死存储"这一正确性缺口在当前语料中为 **0** 触发，挂起。
- **P/Invoke provenance**：全语料唯一声明为 `user32!GetAsyncKeyState`（PcCompat legacy input bridge 已接管）；Android 语料 **0** 声明、无 NativeLibrary/dlopen 形态。挂起。

至此本章每节的证据状态都已入册（见 MOD_RUNTIME_ISOLATION.md「证据矩阵」表）：**已实现 6 节、按证据挂起 10 节**。基于当前真实样本的隔离主线推进到此闭合；后续再动工的前提是新 MOD 样本带来新调用点、实机数据暴露新故障，或 loader overlay 的产品决策落地。

本机验证：本轮仅审计与文档变更，未触碰生产代码。全量托管基线维持 `1082/1083`（零失败）。未执行实机、ADB 或安装验证。

### 2026-08-22 自更新决策落地（overlay 即暂存区）+ 回查发现的两个缺陷

用户决策：走 **Host 中介的自更新**（C 路线），但明确了关键约束——**MOD 不会主动 request**，它们是既有第三方代码，只会照旧 `Copy/Move` 替换自身 DLL；因此必须由 Host 自行识别，或干脆放开热更新口子。

#### 设计：不要账本，overlay 本身就是暂存区

第一版实现（已废弃）是「桥捕获 → 事务账本 → 卸载时 finalize」：桥拦截安装根内 `.dll/.exe` 的 Copy/Move/Delete，把 `(相对路径 → 暂存源路径)` 记进 per-owner 事务。**回查时发现它有致命 bug**：账本只记录暂存源的**路径**，而真实更新器完成后会删掉自己的暂存目录（Jipper `InstallScreen` 就有 `Directory.Delete(TempPath, true)`）——到 finalize 时被引用的字节已经不存在。

改为更简单也更正确的形态：安装根写入**不再按扩展名分流**，程序集与数据文件走同一条 overlay 规则。于是

- 字节由 MOD 自己的 copy 直接写入 overlay，**无需 Host 快照**，vanishing-source bug 从根上消失；
- **回滚免费**：包层原件从未被修改，删除 overlay 条目即回滚；
- 读取 data-first 已存在 → 更新器"替换后自校验"天然可用；
- 待激活清单 `NativeModPathBridge.SnapshotPendingSelfUpdates(roots)` 由**文件系统事实**枚举（overlay 内 `.dll/.exe` + 包层对应物），跨重启存活、不会与所描述的字节漂移；
- 账本、finalization 协议、内容快照三套机制全部不需要。

loader 仍只读包层 → 新二进制在激活前**完全惰性**，安全性与"失败关闭"等同，但更新器流程能完整跑完而不是中途报错。激活策略（用户确认 / 备份 / manifest 重签）留待后续切片。**安全边界必须写明**：Host 无法验证 MOD 从自有服务器下载字节的真实性，只能保证换包原子、有备份、对用户可见——这是事务性与可回滚性，不是供应链信任。

#### 回查发现的缺陷（本轮修复）

1. **`PcCompatManagedNetworkBridge` 会拒绝它本该隔离的那个下载（严重）**。`SendAsync` 原本要求存在环境 scope，但 `PcCompatManagedExecutionState` 是 `[ThreadStatic]`——异步 continuation 上下文必然丢失。JALib 下载器 `<GetResponse>d__4` 的 `GetAsync` 若在 await 之后的 continuation 线程执行，请求会被我自己的归属检查拒绝。对照 Android 侧 `ModRuntimeCapturedScope.ValidateCurrentCaller` 才发现契约本就应是 `currentOwner == null → 放行`：**client 的持有本身即凭证，归属在构造时已绑定**；只有 scope 属于*另一个* session 才拒绝。已按此修正，两侧契约现在一致。
2. **测试编码了错误契约且依赖网络**。原 `OwnerlessCallerCannotUseAModClient` 断言"无 scope 必须抛异常"——正是上面那个 bug 的固化。替换为 `AsyncContinuationWithoutAmbientScopeIsNotTreatedAsAForeignCaller`，并改用**进程内 stub handler**：原测试用 `.invalid` 主机，而本机 DNS 会劫持它导致请求真的成功，断言形态本身不可靠。

验证：全量托管 `1086/1087` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），上一切片基线 `1082/1083`，新增 `4` 条自更新测试（覆盖：替换自身程序集后包层仍在运行且 pending 可枚举、**更新器删除下载目录后 pending 仍有效**、删 overlay 即回滚、数据写入不被误报为 pending）；网络桥 `7/7`；路径桥/自更新/影子改写 `33/33`；Debug 与 Release 构建均**显式断言 error=0** 后才采信结果。未执行实机、ADB 或安装验证。

下一候选（激活切片）：pending self-update 的用户确认 UI + 包层备份 + `isolation.json` 由 Host 重签 + 扫描前原子换包与失败回滚。这一步会真正改变 loader 行为，建议单独立项并先确认确认流的产品形态。

### 2026-08-22 全量回查（对当日七个切片逐条追问必要性/最优性/文档一致性/缺陷）

按「这是必要的吗 / 是最优解吗 / 偏离文档了吗 / 有 Bug 吗」对当日产出逐项复审，除已记录的两个缺陷外**又发现并修复三个**，全部由我自己当日引入：

1. **`DataOverlayRoot` 未纳入 `WritableRoots`（两侧，严重）**。overlay 路径被交回给桥时：PcCompat 侧 overlay 位于 install root 之内 → 判定 `inInstall` 成立 → 写入被**二次映射**成 `overlay/.pccompat-data/data/...` 嵌套；Android 侧 overlay 位于 install root 之外 → 不属任何根 → **直接拒绝**。后者被我同日新增的合并枚举**直接放大**：枚举对影子条目返回的正是 overlay 路径，MOD 拿它调桥的任何读 API 都会被自己的桥拒绝。已把 overlay 纳入两侧 `WritableRoots`（writable 判定先于 install 判定，因此 overlay 路径直接放行、解析幂等），并新增两条回归钉住"解析 overlay 路径幂等且不产生嵌套"。
2. **PcCompat `FileMove` 用裸 `Path.GetFullPath` 解析源**。这正是文档反复强调禁止的"相对路径解析到共享进程 CWD"；Android 侧的 `MoveInstallAware` 写对了，PcCompat 侧漏了。当前 CWD 不在 MOD 目录内时后果被 `ResolveForRead` 掩盖（实际移动路径仍正确），属潜在缺陷+一致性破损。已按 config 根锚定并加回归。
3. **网络状态退休时 dispose CTS 引入竞态**。`Cancellation => _cancellation.Token` 在 dispose 之后访问会抛 `ObjectDisposedException`；退休与在途请求并发时，`CreateLinkedTokenSource` 拿到的是错误异常类型而非取消语义。当初加 dispose 只为"卫生"。已改为不 dispose 并注明理由：每个退休 generation 残留一个未 dispose 的 CTS 是有界且低廉的，teardown 竞态下的错误异常不是。

另做一处健壮性调整：`Disable()` 的清理顺序改为「网络 → 事件 → **最后**撤销路径根」。原先路径根先撤销，任何将来需要写诊断/flush 文件的 cleanup 都会因根已撤销而失败关闭。

复审同时确认无需改动的判断（记录以免重复推翻）：静态事件 `RetireOwner` 排在 MOD 自身 `OnDisable`（含其 `-=`）之后是正确的——重复 remove 在 .NET 中是 no-op，而 MOD 抛异常时兜底仍然执行；`lease audit` 的 ResourceChanger 判定按 modId 而非 generation 是既有 registry API 的限制，聚合器如实反映而不伪造精度；v6/v7 两次 shadow ABI bump 严格说属桥行为变化（本已由 bridge MVID 进 cache key 覆盖），保留显式 bump 作为审计痕迹。

验证：全量托管 `1089/1090` 通过、零失败（`1` 项既有 XPerfect 环境测试跳过），修复前基线 `1086/1087`，新增 `3` 条缺陷回归；路径桥/自更新定向 `34/34`；网络桥 `7/7`；Debug 与 Release 构建均显式断言 error=0。未执行实机、ADB 或安装验证。

### 2026-08-23 Harmony 样本源码审计（JPKV/JPOV/JRP/CheryTools）+ 共享属性 contribution registry 通用化

本轮起点是"推进 HARMONY 兼容"，主样本按用户指定为 `JipperKeyViewer` 与 `JipperOverlayer`。中途得知 `JipperResourcePack` 也有源码，因此把此前只有 DLL 的 JRP 审计重做了一遍。**源码审计推翻了三条此前基于 DLL 的文档判断**，并直接决定了本轮实现的内容。

#### 审计结论（修正既有文档）

1. **JPKV 源码零 Harmony 用法。**全树（含 `-FileBased`/`-Unity`/`-Unity2022` 与两个 Loader 变体）无任何 `HarmonyPatch`、`Harmony.Patch` 或特殊参数。JPKV 的兼容风险在输入与绘制，不在 Harmony；`JPOV_JPKV_COMPAT_GAPS.md` 的 K-xx 缺口不含 Harmony 项。
2. **JPOV 是唯一 Harmony 样本，且形态远比文档假设的窄。**全部为 Postfix + 一个 Prefix；全树特殊参数只有 12 个 `__instance` 与 2 个 `___txt`，**零 `__result`、零 `ref/out`、零 Transpiler、零 Finalizer**。因此此前长期挂在待办上的 "Postfix `ref/out __result` 写回"、"Transpiler/Finalizer native 执行" 在当前样本上**没有对应证据**，不应作为 Harmony 推进的下一步。
3. **`RectTransform.set_*` 是"MOD 私有布局状态、无需仲裁"的结论是错的。**该结论来自只有 DLL 时的 per-instruction 审计，**无法区分目标对象归属**。源码显示同一个 MOD 里两类站点共存，且游戏自有对象上存在三方争用：
   - `scrShowIfDebug` 的 rect：JRP `Status.cs:139` 与 JPOV `ScrShowIfDebugAwakePatch` 写**同一对象的同一属性、同一个值**（`new Vector2(300, y)`），CheryTools `RegisterAutoplayStatusText` 也登记同一对象并重定位。
   - `JRP ResourceChanger.cs:191` 对 `scrLogoText` 的 rect 做**读-改-写**（`anchoredPosition with { y = 0.75f }`）——它保留的 x 必须是游戏原值。
   - `JRP Overlay.cs:269` 写 `ADOBase.controller.txtLevelName`。
   - JPOV `Overlay.BetaWatermarkOriginalPos` 与 CheryTools `GameUIManager.ElementState.AnchoredPosition` **各自保存同一 rect 的"原值"并各自恢复**：谁第二个采样，采到的就是对方改过的值，两者都恢复后对象永久偏移。

#### 实现（阶段 4 剩余项：任意 component property contribution registry）

- 仲裁核心从 `Behaviour.enabled` 专用改为按 `(目标对象引用, 属性名)` 索引的**描述符表**。值以 `object` 流转，`Behaviour.enabled` 成为第一个描述符（行为不变，原有回归全绿），新增 `RectTransform.anchoredPosition`。
- **两条新的正确性要求**（`enabled` 的原实现不含这两条，属同类隐患）：
  - **baseline 只采样一次**，之后永不重采。
  - **未持有 contribution 的 MOD 读到 baseline，而不是当前投影值**。读投影值会让 MOD 把邻居的偏移当成游戏原值记下来并在卸载时恢复它。代价是某属性被持有期间游戏侧改动对其它 MOD 不可见（无 contribution 的属性不在表里，读取直通），已在文档中显式记录为取舍。
- MOD 自建对象持有 native lease，走直通路径不进仲裁——**lease 的有无正好是"私有布局 vs 共享游戏状态"的判据**，因此两类站点无需静态区分即可共存于同一程序集。
- 写入真实 Unity 失败时回滚本次 contribution，使失败的写入不留下需要恢复的登记。
- **改写机制新增 box/unbox 通路**（`BoxLastValueTypeArgument` / `AllowValueTypeReturnUnbox`）：`Vector2` 是 generated proxy 的结构体，两侧都无法静态命名，故 callsite 前发 `box`、返回处发 `unbox.any`，registry 只存放与回放装箱值。装箱只允许作用于**最后一个参数**（`box` 需在值位于栈顶时执行，为到达更早参数而把尾随实参溢出到局部变量，对现有任何已审计站点都不值得）。两个新 flag 已进 spec 身份串，旧缓存失效。
- ABI：`CacheFormatVersion` → `v37-shared-property`，`PcCompatManagedComponentBridge` → `v8-shared-property`。

#### 过程中自我更正

- 我先判断"JRP `Status.cs` 把写入放在 `Task.Yield().OnCompleted` 续体里，`[ThreadStatic]` 的执行上下文会丢失，本切片会把这个真实 MOD 改坏"。**该判断错误**：`PcCompatManagedExecutionContext.Enter` 会装一个携带 owner 的 `UnityMainSynchronizationContext`，`DispatchContinuation` 在续体中重建 UnityMain 与 MOD 两层 scope，两个前置条件都满足。这套异步续体机制本就是为这种情形建的。
- 首个读路径实现让无 contribution 的 MOD 读**活的 native 值**，被新写的跨 MOD 基线测试当场抓到（`secondModOriginal` 拿到了第一个 MOD 的偏移）。该缺陷与 `Behaviour.enabled` 原实现同源，一并修正。

#### 文档更正

- `MOD_RUNTIME_ISOLATION.md`：证据矩阵"真实 Unity/IL2CPP 状态仲裁扩展"由"挂起（无争用证据）"改为已实现并列出三方争用证据；阶段 4 待实现项替换为已实现条目 + **显式记录被推翻的旧结论及其原因**；共享属性章节重写；新增 7 条验收矩阵行。
- `JPOV_JPKV_COMPAT_GAPS.md`：新增 §0.1 Harmony 样本源码审计与 §0.2 共享属性仲裁；J-05 矩阵中 `scrEnableIfBeta.Awake` 的 unsupported 理由收窄为"只剩保留代理生命周期"（原值保存/恢复一侧已被本轮覆盖）。
- `HUD_KEYVIEWER_HARMONY_COMPAT.md`：§3.3 与 §11.3 之间的自相矛盾已消除。§3.3 表格与 §11.3 前的普查表都写着 `MethodInvoker`/`FastAccess`/`DelegateTypeFactory`/`PatchInfo`/`FieldRefAccess` "仍然缺席"，但它们均已存在（`LegacyRuntimeFallbacks.cs`、`PatchInfo.cs`、`AccessTools.cs:866-898` 的 ABI 完整抛错桩，无 `TypeLoadException` 风险）；已标注那些段落是补齐之前的快照，当前状态以 61/61 类型、871/872 成员为准。同时记录：JPOV 唯一可能落到 `FieldRefAccess` 桩的路径（`CreateMemberGetter<T,F>` → `AudioSource.pitch`）走的是属性委托分支且被 try/catch 降级，`CreateFieldRef` 无外部调用点，桩不在活跃路径上。

#### 尚未做（有证据但本轮不放开）

`Transform.localScale`、`CanvasGroup.alpha/interactable/blocksRaycasts`、`Graphic.color` 有同类争用证据（CheryTools `ElementState` 保存/恢复全部四项，JRP `Overlay.cs:270` 写 `txtLevelName.localScale`），加描述符即可覆盖，但合成语义未定——`Graphic.color` 很可能需要合成器而非 last-writer-wins，不宜与本轮一并放开。

`scrEnableIfBeta.Awake` 解封的剩余一半是**保留代理的生命周期**：`__instance` 目前是每次调用用裸 `IntPtr` 新建的包装，MOD 存进静态字段后跨帧解引用即悬垂指针；需要 owner/generation 约束下的代理保留、native 销毁后的 fake-null 失效与场景退休闭包。这是下一个候选切片。

验证：全量托管 `1115/1116` 通过、零失败（`1` 项既有 XPerfect 环境测试按原条件跳过），上一切片基线 `1080/1081`，本轮新增 `4` 条测试（跨 MOD 基线独立 + 按序回落 + 写失败回滚 + generation 淘汰，以及真实 DLL 上的 box/unbox IL 形状与读-改-写双访问器路由）——**增量与总数一致，排除过期产物**。component bridge 定向 `48/48`；改写契约 `47/47`（在真实 `JipperResourcePack.dll` 的 2 处 getter + 31 处 setter 上验证 box/unbox）。Android Release 构建 error=0。构建与测试均先显式断言 `BUILD_EXIT=0` 且 error 计数为 0 才采信结果。未执行实机、ADB 或安装验证；跨 MOD 位置争用的最终表现仍需实机确认。

### 2026-08-23 JPOV/JPKV 真实 UMM 产物接入：代理面扩容 + 三处同源归一化缺陷

本轮目标由用户明确为：**让 JipperOverlayer 与 JipperKeyViewer 兼容，同时不破坏 JRP**。只支持 UMM loader，Melon loader 明确不在范围。

#### 前提变化：拿到了真实产物

此前文档反复记录"仓库内只有 JPOV 源码而无可供重写的发布 DLL，最终发布程序集 rewrite 仍待有真实产物时验证"。本轮拿到了 `JipperOverlayer-UMM/` 与 `JipperKeyViewer-AssetBundle/`，均为 UMM 入口 + 独立主程序集（与 JRP 的 `JAMod.Bootstrap` 同构）。两者 `Info.json` 的 `AssemblyName` 都是 `*.Loader.UMM.dll`。JPKV 目录同时带 Melon loader，已按范围排除。

（源码工程的 `Libs/` 是空的，PC 引用程序集不在其中；但代理管线的输入 `AssetRipper_export/AuxiliaryFiles/GameAssemblies` 齐全，所以不需要构建 MOD 源码。）

#### 方法学更正：裸 CLI 审计是无效的

我最初用 `ModAssemblyRewriter --audit-only` 直接审计，得到 JPOV 282 / JPKV 298 个 methodIssue。**这个数字是假的**：CLI 不传任何桥 spec，于是所有由托管桥接管的 callsite（`AssetBundle.LoadAllAssets`、`GUILayout.Button`、`GUIStyle.set_*` 等）都被报成未解析代理方法。判据是 **JRP 也一样失败**——而 JRP 是当前能加载的 MOD。

改为新增 `PcCompatUmmModRewriteAuditTests`，通过 `InternalsVisibleTo` 调用生产自己的 spec 工厂（`BuildManagedBridgeRewrites` / `BuildManagedCallBridgeRewrites` / `BuildManagedFieldConstantRewrites`），走与 Android 宿主同一个 `ModAssemblyRewriteApi.Rewrite` 重载。JRP 与 `JAMod.Bootstrap` 在该测试下**干净通过**，方法学由此自证。两个 MOD 的主程序集为"差距清单"测试（打印去重后的未解析成员），转为 `AssertClean` 即为完成定义；MOD 产物缺失时跳过而非失败，JRP 是必需的回归锚。

（该测试早期两个失败均为我的测试代码问题，非产品缺陷：给 `JAMod.Bootstrap` 传了 `isPrimary: true`，把只存在于主程序集的 ReversePatch spec 套上去，产生 9 个幻影 bridge issue——生产是按程序集分发的；以及断言 JPKV 必须有改写产出，而 JPKV 本来就无可改写。)

#### 代理面扩容（零丢失的超集）

代理面此前按 JRP 依赖闭合裁剪，因此 `scrController` 只有 19 个方法、`GUIContent` 只有 5 个，`Resources`/`Random`/`scrEnableIfBeta`/`UIVertex`/`VertexHelper`/`Color32`/`JsonUtility` 整型缺失。把三个 MOD（含两个 UMM loader，不含 Melon）一起喂给 `ProxySurfaceScanner`：

- surface：6 程序集 / 23,636 引用 / 接受 465；对 JRP-only 基线**新增 113 条、丢失 0 条**，是严格超集。
- 唯一"丢失"项 `AssetBundle.Unload(bool)` 经核实是 `ManagedBridgeOwnedSurface` **故意排除**的——它由 VirtualBundle 托管桥接管，不该有代理直通入口。自动面才是正确的。
- 闭包：193 个精确类型 / 15 个程序集，`missingAndroid=0`、`unresolvedMetadata=0`（JRP-only 为 181/14）。
- 代理程序集 14 → 15（新增 `UnityEngine.JSONSerializeModule`）。

效果（生产 spec 审计，issues/methodIssues）：JPOV `16/260 → 2/30`，JPKV `17/285 → 0/16`。JRP 与 `JAMod.Bootstrap` 全程保持干净。

#### 三处同源缺陷：dnlib 与 AsmResolver 对泛型实例名的分歧

管线里 `ProxySurfaceScanner` 用 dnlib 写 manifest，`ProxyInputClosure` 与 Il2CppInterop 生成器用 AsmResolver 读回。两者对泛型实例的渲染不同——**AsmResolver 在泛型实参逗号后加空格，dnlib 不加**：`UnityAction`2<Scene, LoadSceneMode>` vs `UnityAction`2<Scene,LoadSceneMode>`。三处 `NormalizeTypeName` 都只处理了 `/`→`+`，都漏了这一条：

1. `ProxyInputClosure`：闭包**直接抛异常**中止（`Proxy surface method must resolve uniquely`），JPOV 的 `SceneManager.add_sceneLoaded` 一进面就炸。这是显式失败，容易定位。
2. `Il2CppInterop.Generator/GeneratorOptions.ShouldGenerateMethod`：**静默丢成员，无任何诊断**。同一个 `SceneManager` 上，`add_sceneUnloaded(UnityAction`1<Scene>)` 生成成功而 `add_sceneLoaded(UnityAction`2<Scene, LoadSceneMode>)` 被跳过——唯一差别是泛型实参个数。这是本轮最隐蔽的一处：闭包报告显示该类型收了 4 个方法，产物里只有 2 个。
3. 同一函数被 closure 与 generator 各自持有一份副本，故两处都要改。

修复即在归一化里追加 `.Replace(", ", ",")`。**行为保守性已验证**：用手工面（原输入）重跑闭包，产出的 allowlist 与现役 `out/interop/proxy_type_allowlist.txt` **逐字节一致**。

构建生成器 fork 时发现其 `global.json` 钉 `8.0.0` + `latestFeature`，本机无 8.x SDK。用临时改 `rollForward: latestMajor` 构建后**立即恢复原文件**（`git diff` 为空），未把这项环境适配留在仓库里。

#### 当前剩余差距（9 类，两 MOD 高度重合）

均为**参数形态失配**，不是功能缺失：

| 类别 | 成员 | JPOV | JPKV |
| --- | --- | :-: | :-: |
| BCL `System.Object` → `Il2CppSystem.Object` | `Debug.Log/LogWarning/LogError` | ✓ | ✓ |
| 同上 | `JsonUtility.ToJson(Object,Boolean)` / `FromJsonOverwrite(String,Object)` | | ✓ |
| BCL `StringBuilder` → `Il2CppSystem.Text.StringBuilder` | `TMP_Text.SetText(StringBuilder)` | ✓ | ✓ |
| 托管数组 → `Il2CppStringArray` | `GUILayout.SelectionGrid(Int32,String[],Int32,GUILayoutOption[])` | ✓ | ✓ |
| 托管数组 → `Il2CppStructArray<Char>` | `TMP_Text.SetText(Char[],Int32,Int32)` | | ✓ |
| 泛型集合 → `Il2CppSystem.Collections.Generic.List` | `TMP_FontAsset.set_fallbackFontAssetTable(List<TMP_FontAsset>)` | ✓ | ✓ |
| 字段 setter 未生成 | `scrController.txtLevelNameOriginalPosition` 的 `set_` | ✓ | |
| 非 MonoBehaviour 的 MOD 组件 | `JipperKeyViewer.KeyViewer.RainGraphic`（派生自 `MaskableGraphic`，`AddComponent` 桥当前要求直接派生 `MonoBehaviour`） | | ✓ |

前六类改写器已有数组/返回值转换器机制，属同类扩展；`RainGraphic` 是真实的新形态（MOD 自有 UI Graphic 组件），需要单独设计。

#### 结论与边界

JPKV 顶层 issue 已归零，两个 UMM loader 程序集均干净通过，JRP 与 `JAMod.Bootstrap` 全程无回归。**但两个 MOD 的主程序集仍 `outputWritten=False`，因此现在还不能宣称 JPOV/JPKV 可加载。** 差距已从"不可枚举"变成"9 类可逐项闭合"，且有可执行清单持续度量。

验证：全量托管 `1122/1123` 通过、零失败（`1` 项既有 XPerfect 环境测试按原条件跳过），上一切片基线 `1115/1116`，新增 `7` 条审计测试——增量与总数一致。构建与测试均先显式断言退出码与 error 计数。代理产物位于未跟踪的 `out/interop/`，未提交。未执行实机、ADB 或安装验证。

### 2026-08-23 托管桥与自动转换器设计定稿（未实现）

对 JPOV/JPKV 剩余 9 类形态失配做了逐项设计，成文为 `MANAGED_BRIDGE_AND_CONVERTER_DESIGN.md`。**本条只记录设计与实测结论，尚无代码变更。**

补上了此前没有成文的机制选择判据：**语义归宿主的走手工桥，纯数据形态搬运的走自动转换器**。"语义归宿主"指目标行为在 Android 上应落到宿主（`Debug.LogException` 先例）、实参是 IL2CPP 类表里不存在的 MOD 自有类型、或需要仲裁/归属/生命周期管理。

#### 实测纠正的三处判断

1. **"恰好一个参数失配"这条限制不需要放开。** 我先前说要放开是错的：实测 5 类里 4 类只失配 1 处，`SelectionGrid` 有一个只失配 1 处的重载可用。`InsertArgumentConverter` 也**已经**支持失配参数在任意位置（尾随实参溢出到局部）。真正缺的只有转换器种类。
2. **managed→Il2Cpp 的数组 `op_Implicit` 全部现成存在**（`Il2CppStringArray`、`Il2CppStructArray<T>`、`Il2CppReferenceArray<T>`），所以 `String[]`/`Char[]` 两类成本极低；而 `Object`/`StringBuilder`/`List<T>` 没有，需要宿主辅助方法。另更正措辞：现有转换器**不往 MOD 模块注入方法体**，而是引用 `PcCompatAbiBridge`/`PcCompatCollectionBridge` 上的宿主方法。
3. **`RainGraphic` 被拒的根因与报错文字不符。** `IsManagedOwnedMonoBehaviour` 本就沿继承链上溯；真实原因是 `Program.cs:2012` 显式要求链上每一环都在 MOD 自有模块内，而 `MaskableGraphic` 在代理模块里。这是刻意约束——MOD 类型继承代理类等于要求 Unity 实例化并回调一个不在 IL2CPP 类表里的类型，正是 managed component bridge 前提所否定的。

#### 发现一个现存缺陷（不是形态缺口）

`TMP_FontAsset.fallbackFontAssetTable` 的现有 getter 转换器 `PcCompatCollectionBridge.CopyList` 是**拷贝语义**。三个 MOD 的 IL 形态完全一致（`get_` → 立即 `Contains`/`Add`，从不存变量复用），于是 `.Add` 写进的是托管副本，**永远回不到 Unity**——CJK fallback 字体静默失效且不报错。补 setter 转换器也修不好。

同时更正缺口文档此前的记载：**这个属性 JPOV 也在用**（`BundleLoader.LoadBundle`、`FontManager.ScanFonts`），不只 JPKV。

设计为"通用可写集合属性名单 + 改写 getter 返回值上的变更调用"。不能返回 `IList<T>` 包裹器——已编译 IL 调的是 `List<T>::Add/Contains/get_Count/get_Item/GetEnumerator` 等具体方法，返回类型必须仍是 `List<T>`；`List<T>::Add` 不是 virtual，子类拦截也无效。兼容性约束已实测：代理里返回 `Il2CppSystem…List<T>` 的成员共 4 个，其余三个（`listFloors`、`allPlanets`、`events`）JRP/JPOV **全是只读**，继续走 `CopyList` 不受影响。

> **本段两处已被后续实测推翻**（见 2026-08-23 实现条目）：`allPlanets` 根本没有 callsite（JRP 走反射），`events` 不是 JPOV 独有（JRP 也有一处）。真正经过 `CopyList` 的是 3 个成员共 12 个 callsite。

#### 其余各类要点

- `Debug.Log` 三个重载（仅 JPOV 16 处，实参 16/16 静态均为 `System.String`）：自动转换器 + 新增**极保守**向前实参类型推断，只认单指令可定类型的情形，推不出即失败关闭（与 `GetFollowingUnboxType` 同构）。不做通用对象装箱——那要面对跨界对象的所有权与生命周期，而语料里没有这种站点。**［本项已被推翻：改为手工桥，推断机制未实现也不需要，见 2026-08-23 实现条目。］**
- `JsonUtility` 两个入口（仅 JPKV）：手工桥，托管侧完成序列化。实参 `ProfileData` 是纯 CoreCLR 类型，IL2CPP 侧 `JsonUtility` 靠反射读字段，拿到不认识的类型只会失败。序列化器手写最小 Unity-JSON 子集（字段而非属性、枚举按整数、数组/List、代理 valuetype 递归其真实字段）；实测 `UnityEngine.Color` 代理的 `r/g/b/a` 是真正的 `Single` 字段可直读，`ProfileData` 里有 16+ 个 `Color`。不用 `System.Text.Json`——默认行为在字段/命名/枚举三处都不同，扯平后代码量相当而差异难穷尽。
- `RainGraphic`：放宽判据允许继承链穿过代理类型，**并同时做渲染回调桥**。只放宽会得到"改写干净但雨不渲染"——桥只转发 `Awake`/`Update`，而 `MaskableGraphic` 的价值是 Unity 回调 `OnPopulateMesh` 要顶点。顶点流地基已验证：`VertexHelper` 有 `AddVert`/`AddTriangle`，`UIVertex` 字段可直读，**无需跨界封送**。驱动用宿主模式：加一个真 IL2CPP `RawImage`，hook `RawImage::OnPopulateMesh`（dump `428594`）转发给托管实例。**不能 hook `Graphic::OnPopulateMesh`**（`426150`）——它是 virtual 且 `Image`/`RawImage` 都已 override，hook 基类对已 override 的子类不触发。另记录：1.7.0 之后的源码已删除 `RainGraphic`，改用 `RainLayer` 自绘；当前以正式 Release DLL 为运行时事实故仍需处理。

#### 实施顺序（含一条必须先做的防线）

`CopyList` 那条转换器路径**目前无任何测试钉住**（JRP 三个只读 List 站点全靠它），而设计要改的正是它；现有 `PcCompatManagedBridgeRewriteTests` 只断言了一处 `BoxUnboxedValue`。因此第一步是**先补基线测试**，在真实 DLL 上钉住三个只读站点的现有改写结果，之后才动可写集合名单——否则误伤只读路径不会被任何测试发现（改写仍然干净、测试仍然绿），只能到实机暴露。

其后顺序：数组两类 → `StringBuilder`/`Object←string`（含推断）→ 可写集合名单 → `JsonUtility` → `RainGraphic`（最大且独立，建议单独立项）。每步之后 JRP 与 `JAMod.Bootstrap` 必须保持零 issue、`outputWritten=True`。

本条无代码变更，故无测试数字。

### 2026-08-23 托管桥与自动转换器实现（设计 §4.1–§4.3，5 类闭合）

按 `MANAGED_BRIDGE_AND_CONVERTER_DESIGN.md` 的顺序实施了前三步。JPOV methodIssue `30 → 2`，JPKV `16 → 7`；JRP 与 `JAMod.Bootstrap` 保持零 issue、`outputWritten=True`。

#### 先做的回归防线（设计里的第 1 步）

在 `PcCompatManagedBridgeRewriteTests` 上新增两条基线测试，钉住 `CopyList` 路径的现有行为：`ReadOnlyListSitesKeepTheCopyListConverter`（转换器身份，逐字符串比对）与 `CopyListConverterIsEmittedDirectlyAfterTheProxyAccessor`（IL 形态：accessor 之后必须紧邻 `CopyList`，中间不得插入任何指令）。

写这两条测试时实测出**四处与设计初稿不符的事实**：

1. **`PlanetarySystem::get_allPlanets` 没有任何 callsite。** 初稿表格把它列为"JRP 只读"，实际 JRP 走反射 `obj.GetValue<List<scrPlanet>>("allPlanets")`，不经过转换器。
2. **`scnGame::get_events` 不是 JPOV 独有**，JRP `PlayCount.GetHash` 也有一处。
3. **`scrLevelMaker::listFloors` 是 10 个 callsite**，不是一处；而且它走**字段路径**（`ldfld` → `call get_listFloors`，opcode 被改写），与方法路径（`callvirt` 保持不变）不同。测试同时钉住了这个 opcode 差异。
4. **`fallbackFontAssetTable` 的拷贝语义缺陷波及 JRP。** 初稿说"JPOV 与 JPKV"，实际 JRP `BundleLoader.cs:42` 同样 `.Add`。所以设计 §4.5 不是新增能力，而是修一个当前发布路径上的静默失效。

第 4 点使基线测试的写法也变了：`fallbackFontAssetTable` 站点**故意也被钉住**，注明它的拷贝语义是缺陷。这样 §4.5 改到引用语义时必须手工更新该断言，被迫明确说出动了哪些站点，而不是让三个站点一起被静默转换。

#### 已闭合的 5 类

- **`Debug.Log`/`LogWarning`/`LogError(System.Object)`（JPOV 16 处）改为手工桥。** 这推翻了初稿的"自动转换器 + 向前实参类型推断"：那套推断机制**没有实现，也不需要**——源参数静态类型本就是 `System.Object`，桥签名精确匹配，现有 spec 机制直接可用；且这一类恰好符合初稿自己定的"语义归宿主"判据，初稿把它归错了边。`PcCompatManagedLogBridge` 在托管侧 `ToString()` 后写宿主 Logger，`null` 渲染为 `"Null"`（对齐 Unity），并捕获 `ToString()` 抛出——PC 上该异常落在 Unity logger 内部，放行会窜回只是想打日志的 MOD 代码。
- **`GUILayout.SelectionGrid` 的 `String[]` → `Il2CppStringArray`**（JPOV 2 + JPKV 3），现成 `op_Implicit`。失配在 4 参中的第 1 位，两个尾随实参由既有溢出机制处理。
- **`TMP_Text.SetText(Char[],Int32,Int32)` 的 `Char[]` → `Il2CppStructArray<Char>`**（JPKV 6），失配在第 0 位。这两条一起验证了 `InsertArgumentConverter` 的任意位置支持是真的可用，无需改动。
- **`TMP_Text.SetText(StringBuilder)` → `PcCompatAbiBridge.ToIl2CppStringBuilder`**（JPOV 10）。生成代理里的 `Il2CppSystem.Text.StringBuilder` 是 skeleton，只有 `.ctor(IntPtr)`；当前桥通过 native metadata + `il2cpp_runtime_invoke` materialize，绝不依赖编译期完整引用程序集的 `.ctor(String)`。转换是**拷贝**语义，对已审计站点正确（全部形如 `text.SetText(sb)`，Unity 在该次调用内读走字符并自留副本，从不持有 builder）。
- **`Il2CppReferenceArray<T>` 方向刻意不接入**：托管 `T[]` 里装的是代理引用需逐元素解包，而唯一的引用数组参数 `GUILayoutOption[]` 有保持托管数组的重载。

#### 缺口清单改为精确钉住

`RemainingGapsAreExactlyTheKnownSet` 按 `(target, callsite 数)` 精确断言两个 MOD 的剩余 methodIssue，不是上界。既拦回归，也强制下一步显式更新清单而非静默缩小。`ArgumentFormMismatchesResolveToTheIntendedConverters` 另外钉住每类失配解析到的**转换器身份**——issue 消失只证明"不再报错"，不证明"解析到了预期的那个转换"。`DebugLoggingIsReplacedByTheHostLogBridge` 同时断言改写后无任何 callsite 仍指向 `UnityEngine.Debug` 代理。

#### 数字

全量托管回归 `1127` 通过、`1` 个既有 XPerfect JIT 测试按原条件跳过；`StArray.ModManager.Android` Release 构建通过。缓存 ABI 递增 `v37-shared-property → v38-array-and-log-args`，`PcCompatManagedLogBridge.v1 → v2-object-messages`，`PcCompatAndroidInputContractTests` 的固定 ABI 串同步并新增日志桥路由断言。

**两个主程序集仍 `outputWritten=False`，因此不宣称 JPOV/JPKV 可加载。** 未做实机验证；日志落点、字体行为只能由用户在设备上确认。

### 2026-08-23 可写集合属性（设计 §4.4–§4.5，JPOV methodIssue 归零）

实施了设计的第 4 步。JPOV methodIssue `2 → 0`（只剩 2 处代理面顶层 issue），JPKV `7 → 6`（只剩 `JsonUtility`）；JRP 与 `JAMod.Bootstrap` 保持零 issue、`outputWritten=True`。

#### 修掉了 JRP 今天就在踩的静默失效

`TMP_FontAsset.fallbackFontAssetTable` 登记为可写集合：getter 从 `CopyList` 换成 `CopyBoundList`（返回绑定到 Il2Cpp 原集合的拷贝），`List<TMP_FontAsset>` 的变更调用重定向到写穿桥。三个 MOD 的 `.Add` 现在都能到达 Unity——**包括 JRP**（`BundleLoader.cs:42`），它的 CJK fallback 字体此前一样是静默失效的。

setter 不走名单，而是普通参数转换器 `ToIl2CppList<T>`，与 `CopyList` 严格对称。null 透传为 null 而非空 List：Unity 区分"无 fallback 表"与"空 fallback 表"。

顺带记录一个改写后的行为差异：JPOV/JPKV 的判空分支（`??=` 与 `== null`）恒不成立，因为 `CopyBoundList` 与 `CopyList` 一样永不返回 `null`，所以 `new List<>()` + setter 是死代码。方向上安全——它若执行反而会用空表覆盖 Unity 现有 fallback。但 setter 仍按真实语义实现，因为 MOD 可以直接用一个已填充的 List 调它。

#### 实现比设计简单，代价是多了一条必须测的不变式

设计初稿要求"识别 getter 返回值上的变更调用"，即需要栈流分析证明 `List<T>::Add` 的接收者来自登记的 getter。实际做法换了个方向：**让四个写穿桥对未绑定的 List 完全等价于它们替换的 `List<T>` 成员**（原地改，别的什么都不做）。于是误匹配没有后果——MOD 自建的 `List<TMP_FontAsset>` 被重定向后行为不变——所以改写只按元素类型匹配，省掉整套分析，也不会误报 issue。

这条等价性成了设计的承重墙，因此单独建 `PcCompatCollectionBridgeTests` 钉住：`Add`/`Remove`/`Clear`/`Insert` 在未绑定 List 上的返回值、副作用与**异常类型**（`ArgumentOutOfRangeException`、`NullReferenceException`）都与 `List<T>` 一致。异常那条尤其要紧——MOD 自己的错误处理是照 `List<T>` 写的。

绑定用 `ConditionalWeakTable<object, object>`：键是 getter 每次新建的拷贝，不同 MOD、不同调用之间不会串；弱引用让拷贝的生命周期归 MOD，强表会把 MOD 读过的每张表都钉死。

全量实测支撑了这个取舍：三个 MOD 里 `List<TMP_FontAsset>` 的变更调用**共 4 处，全是 `Add`**。

#### 一个必须记的实现陷阱

`CollectWritableCollectionMutations` 必须在方法扫描循环的"非代理程序集则 `continue`"**之前**调用。`List<T>` 在 corlib 里，不是代理程序集，否则 mutation callsite 会被当作"与代理无关"直接跳过，名单永远不生效——而且不报错。

#### 基线测试起作用了

第 1 步补的基线测试在这一步按预期失败：`fallbackFontAssetTable` 站点的转换器从 `CopyList` 变成 `CopyBoundList`，断言当场报错，逼我手工确认"只有这一个站点变了、另两个只读站点没被误伤"。测试随即改名为 `ListSitesGetTheCopyConverterMatchingTheirWritability`，按名字逐项断言谁拿哪个转换器——这是"一个属性被刻意升级"与"所有 `List` 成员静默改语义"之间唯一的防线。

另加 `FallbackFontTableMutationWritesThroughToUnity`：JRP `BundleLoader::LoadBundle` 里必须出现 `AddToBoundList`，且改写后**不得残留任何** `List<TMP_FontAsset>` 的原生变更调用（残留的就是静默 no-op）。

#### 数字

全量托管回归 `1134` 通过、`1` 个既有 XPerfect JIT 测试按原条件跳过；`StArray.ModManager.Android` Release 构建通过。缓存 ABI 递增 `v38-array-and-log-args → v39-writable-collection`，`PcCompatCollectionBridge.v1 → v2-bound-writes`，`PcCompatAbiBridge.v1 → v2-stringbuilder`；**可写集合名单本身也进了托管缓存键**，否则登记项变化不会让旧产物失效。

**写穿路径（绑定那一半）本机不可测**——构造 `Il2CppSystem...List<T>` 需要活的 IL2CPP 运行时。已测的是未绑定时的等价性与改写形态；`.Add` 真正到达 Unity、CJK 字体真正显示，只能由用户在设备上确认。两个主程序集仍 `outputWritten=False`，因此不宣称 JPOV/JPKV 可加载。

### 2026-08-23 JsonUtility 手工桥与 Unity-JSON 序列化器（设计 §4.4，JPKV methodIssue 归零）

实施了设计的第 5 步。JPKV methodIssue `6 → 0`、顶层 issue `0`；JPOV 保持 `0`；JRP 与 `JAMod.Bootstrap` 保持零 issue、`outputWritten=True`。

#### 审计漏了一个调用点，而且是最危险的那个

审计报的是 `ToJson(Object,Boolean)` ×3 与 `FromJsonOverwrite(String,Object)` ×3。实际有 **7 处**：`KeyViewer::LoadSettings` 还有一个 `FromJson<KeyViewerSettings>(String)`，**审计报它干净**——代理的泛型签名精确匹配。

这是**假阴性**。签名匹配只说明形态对得上，完全不说明 `T` 在 IL2CPP 类表里存不存在，而 `KeyViewerSettings` 不存在。放任它转发就是运行时静默失败，任何审计数字都看不出来。

由此得到一条一般结论，已写进设计文档 §4.4：**"审计干净"不等于"运行时可用"**。凡实参或泛型实参是 MOD 自有类型的成员，都要按"语义归宿主"判据主动归到手工桥，不能等审计报错。现有 spec 机制本就支持泛型桥（`SourceGenericArity: 1`），无需改动改写器。

#### 序列化器：与 Unity 的格式兼容，不与 .NET 惯例兼容

目标是 PC 写的 profile 能在这里读回、反之亦然。已实现并逐条测试的规则里，有六条与通用序列化器的默认行为相反：序列化字段而非属性、字段名原样不 camelCase、枚举按整数、`null` 字符串写 `""`、`null` 数组写 `[]`、`null` 对象写 `{}`。另加两条 Unity 特有的：`NaN`/`Infinity` 写成 `0`（Unity 自己的 reader 读不回 `NaN`），浮点用 invariant 格式（跟随当前区域会在欧洲语言设备上写出逗号小数点——这条单独有测试，因为它只在特定设备上暴露）。

`[SerializeField]` 按**全名字符串**匹配而非类型匹配：该 attribute 来自生成代理，本程序集不引用它；按名字匹配还能容忍 MOD 用别的 Unity 版本编译。

**两个方向刻意不对称**：序列化严格（不支持的形态抛异常），反序列化宽松（形态不合的字段跳过）。写方向的结果会覆盖用户现有配置，一个"看起来合法但残缺"的 JSON 会毁掉它；读方向遇到手改或版本错位的文件，应当只丢一个字段而不是整个加载失败。Unity 本身就是这个不对称。

JPKV 依赖的两条语义各有专门测试：`FromJsonOverwrite` 的**部分覆盖**（它 `LoadProfile` 先换全新默认实例再覆盖，正是靠"JSON 里没有的字段保持当前值"防跨 profile 污染），以及**短数组反序列化后仍是短的**（`MigrateAllProfileFiles` 靠 `Count.Length != MaxKeySlots` 识别旧 profile，其源码注释记录了越界风险）。

不用 `System.Text.Json`：上述前四条它默认全部相反，逐一扳平后代码量与手写相当，而剩下的行为差异难穷尽。

#### 测试抓出一个真实缺陷

初版实现里 `Dictionary<K,V>` 落进了"是 class 就递归字段"的兜底分支，会把它的私有 bucket 和 comparer 写成 JSON——**看起来合法、恢复不出任何东西，而且会被写进用户配置文件**。现已在兜底之前显式拒绝非数组/非 `List<T>` 的 `IEnumerable`。Unity 也不支持字典，大声失败既对齐 Unity，也是调用方唯一能察觉的方式。

#### JPKV 的最后阻塞项换了类别，清单测试因此改了

步骤 5 之后 JPKV 的 methodIssue 与顶层 issue 全为 `0`，我据此断言 `OutputWritten` 应为 `True`——**测试立刻失败**。剩的是第三类 issue：`ManagedBridgeIssues` 里的 `RainGraphic`（`AddComponent` 桥拒绝派生自代理模块 `MaskableGraphic` 的类型），而清单测试此前只钉了前两类计数。

已改为三类分别断言，并显式断言两个 MOD 的 `OutputWritten=False`——防止"审计安静"被误读成"MOD 可加载"。这也说明只钉一部分 issue 类别是不够的。

#### 数字

全量托管回归 `1149` 通过、`1` 个既有 XPerfect JIT 测试按原条件跳过；`StArray.ModManager.Android` Release 构建通过。缓存 ABI 递增 `v39-writable-collection → v40-json-bridge`，新增 `PcCompatJsonBridge.v1`。

**未与真实 Unity 输出做逐字节对比**（那需要设备），因此不宣称与 `JsonUtility` 完全等价；真实 profile 往返只能由用户确认。两个主程序集仍 `outputWritten=False`：JPOV 差代理面扩容，JPKV 差 `RainGraphic`（第 6 步）。

### 2026-08-23 RainGraphic 宿主渲染回调桥立项（设计 §4.7，未实现）

对 JPKV 唯一剩余阻塞项做了立项讨论，成文为 `RAINGRAPHIC_RENDER_CALLBACK_DESIGN.md`。**本条只记录立项结论与实测纠正，无代码变更。**

#### 两处前提被推翻

1. **`RainGraphic` 在发布版源码里存在。** 此前记载"1.7.0 之后源码已删除该类、只能以 Release DLL 为运行时事实"。实际仓库有**两份** JPKV 源码：`JipperKeyViewer-1.7.0/` 是发布版源码（`RainGraphic.cs` 在其中，与 `JipperKeyViewer-AssetBundle/JipperKeyViewer.dll` 对应），`JipperKeyViewer/` 才是后续开发版（已改为 `KeyShapeLayer`/`RainLayer`/`GhostRainLayer` 三层自绘）。立项对象是 1.7.0 形态——唯一有发布产物、可审计、可验收的形态。
2. **hook 目标一度选错。** 访谈中先选定 `MaskableGraphic::OnPopulateMesh`，被 dump 推翻：`public abstract class MaskableGraphic` **不 override** 该方法，没有地址可 hook，且 `abstract` 也不能 `AddComponent`。dump 里只有 `Graphic`（virtual）、`Image`、`RawImage`（各自 override）有实现。仍选 `RawImage::OnPopulateMesh`（RVA `0x47b9c78` / VA `0x7c0e7e1c78`）——原结论成立，但此前引用的 "dump `428594`/`426150`" 是行号而非地址，已更正为 RVA/VA。

#### 目标形态是 per-drop，不是整层自绘

1.7.0 的 `RainGraphic` 是**每个雨滴一个组件**（`Rain::Awake` 里 `AddComponent<RainGraphic>`），`RainSystem` 用 `Stack<Rain>` 池复用、`MAX_POOL_SIZE = 64`、超出即 `Destroy`。所以 hook 发射频率是"活跃雨滴数 × 网格重建次数"，不是每帧一次。这个量级是 owner 筛选选型的依据（见下）。

#### 立项决策

- **分派语义：完全替代。** 命中 MOD 宿主时不执行原 `RawImage::OnPopulateMesh`，直接转发给托管 `RainGraphic.OnPopulateMesh`——它第一行就是 `vh.Clear()`，语义与 PC 上 Unity 调它的 override 一致。这条决策顺带消解了"额外加的真 `RawImage` 会画自己的 quad 并与 MOD 顶点叠加"的问题。
- **驱动：复用现有 managed-only Prefix 链路，不新建机制。** `PcCompatManagedPrefixInvocationV2` 字段刚好够用：`Instance`=RawImage 指针、`Argument0`=VertexHelper 指针、`RunOriginal=0`。与 `scrShowIfDebug.Update` 同一条已验证链路。
- **owner 筛选放在 native 侧。** 游戏本体有 9 个 `RawImage` 字段（`pausePlanetsImage`、`waveRaw`、`GetBlurredScreenshot` 等），其中有高频重建的。native 侧维护 MOD 宿主指针集合预筛，不命中直接 tail-call 原方法，不进托管、不分配、不填 ABI 结构。
- **放宽判据用登记制名单，不做通用放宽。** 不采用"只要最终到达 `MonoBehaviour` 就允许穿过代理类型"：那样继承 `Selectable`/`ScrollRect`/`LayoutGroup` 之类的 MOD 类型也会被接受，而我们只有一个 hook，结果是"改写干净但行为静默丢失"——正是这一项要避开的失败模式。登记制把"能过判据"与"有对应 hook"绑定。

#### 构造路径：现成机制可用，但需一处额外改写

代理 `MaskableGraphic..ctor()` 会 `il2cpp_object_new` 一个 **abstract** 类，直接构造必然失败。方案是绑定到真实 `RawImage` 实例：`AddComponent<RawImage>()` 拿到 IL2CPP 指针 → `RuntimeHelpers.GetUninitializedObject` 造托管外壳 → `CreateGCHandle` + `isWrapped = true`。**这套机制现成存在**——`Il2CppObjectBase.InitializerStore<T>` 在"只有无参 ctor"分支里做的正是这三步，`isWrapped` 置位后基类 ctor 里的 `CreateGCHandle` 会 early-return，绑定的指针不被覆盖。

但 `InitializerStore` 走完还会调无参 ctor，而代理 `MaskableGraphic..ctor()` 的后半段会**在已构造好的真实实例上再跑一遍 native `MaskableGraphic..ctor`**（`Il2CppObjectBaseToPtrNotNull(this)` + `il2cpp_runtime_invoke`）。`AddComponent<RawImage>` 已经跑过真正的构造，二次初始化的后果未验证。因此实现要把 MOD 侧 `RainGraphic..ctor()` 里那一处 `call MaskableGraphic::.ctor()` 改写为空操作，字段初始化（`renderMain = true`）保留。

这样 `RainGraphic` 仍**继承**代理 `MaskableGraphic`，于是它内部对 `Graphic::get_rectTransform`/`get_color`/`SetVerticesDirty` 的三处调用**零改动**——`this` 就是绑定实例。实测全类只有 4 处基类调用，一处改写换来另外三处不动。

#### 生命周期

**双登记**：托管实例进 `ComponentEntry`（owner 校验、audit 快照、session teardown），绑定的 `RawImage` 指针进 `NativeObjectLease`，两表交叉引用供 hook 查询。`RainGraphic` 没有 `Awake`/`Update`，单看 hook 只要一张指针映射就够；仍双登记是为泄漏面——池上限 64、超出即 `Destroy`，独立清理逻辑要自己保证不漏，而现有 teardown 已被其它组件验证过。

**池复用不需要新机制**：`ReturnRain` 只 `SetActive(false)` + `SetParent`，对象未销毁、组件未移除，对应现有 `entry.Active` 的 OnDisable/OnEnable 语义；超池时的 `Object.Destroy(r.gameObject)` 已经过 `Destroy` 桥的 owner 校验与 entry 清理。

#### 明确不宣称

本条无代码变更，故无测试数字。写穿到 Unity 的那一半（托管实例是否真收到 `OnPopulateMesh`、顶点是否正确提交、`.ctor` 置空后 `RawImage` 内部状态是否完好、per-drop hook 在 108 键连击下的实际开销）**本机全部不可验证**，需活的 IL2CPP 运行时与设备。设计文档 §9 另留了三项实现时才定案的未决事项（托管侧如何调 `protected override`、`VertexHelper` 包装是否需按指针缓存、native 指针集合的 ABI 形态）。

### 2026-08-23 RainGraphic 宿主渲染回调桥实现（设计 §4.7 / 第 6 步，改写侧已验证）

上一条的立项已实现。**JipperKeyViewer 主程序集三类 issue 全零、`outputWritten=True`，`RainGraphic` 阻塞项闭合。** 但改写干净不等于雨能渲染，见文末。

#### 落地位置

新增 `src/PcCompatManagedRenderComponentCatalog.cs` 作为**唯一登记来源**，由改写器、recipe 编译器、组件桥三方共读——三者若各持一份就会不一致。改写器侧新增 `ManagedRenderComponentSpec` / `MatchRenderComponent` / `PlanManagedRenderComponentRewrites`；桥侧新增 `AddManagedRenderComponent` / `TryDispatchRenderCallback` / `ClearRenderComponentsForSession` / `RemoveRenderComponentBinding`；native 侧新增 op 24、`managed_render:` id 前缀、指针集合与三个导出；`PcCompat/PcCompatNativeRenderHostRegistry.cs` 是其托管封装。缓存 ABI `v40-json-bridge → v41-render-component`，`PcCompatManagedComponentBridge.v9-render-component`，渲染组件名单进托管缓存键。

#### 三处立项结论被实测推翻或修正

1. **"复用现有 managed-only Prefix 链路"只在 ABI 与 native 传输层成立，绑定层不成立。** ABI 结构、传输、排序、in-flight/退休全部复用了；但 `CallbackBinding` 在构造时就把 callback 目标定死，而渲染回调的接收者是**每次按实例指针查出来的托管对象**。更根本的是 JPKV 零 Harmony 用法——没有 patch descriptor、没有 shim registration、没有 callback translation item 可以派生出 binding，所以立项时写的"目录项加进 `PcCompatManagedOnlyCallbackCatalog`"也不可行（那份目录按 Harmony descriptor 索引）。改为新增 `PcCompatRuleOp.ManagedRenderCallback`（op 24）与独立 id 前缀，规则由 `PcCompatRecipeCompiler.EmitManagedRenderCallbackRules` 从登记表直接发出，dispatcher 按 patch id 路由到组件桥。

2. **发出 recipe 规则会把 JPKV 推上"已验证 recipe"路径，从而完全跳过托管 setup。** 这是实现中发现的一个会静默失效的交互，且方向危险：JPKV 此前因零 Harmony 而 `hasRecipe=false`、走 self-render 兜底；一旦为渲染回调发出规则，`hasRecipe` 变 true，`PcCompatRuntime.RegisterPreparedMod` 就会打印 "loaded from verified rule recipe" 并 `return`——hook 装上了而 MOD 托管代码从未运行。修复是让渲染规则强制 `requiresManagedSynchronousPrefix`：它的存在本身就意味着必须走托管分派。由 `RenderRuleForcesManagedDispatchInsteadOfTheRecipeOnlyPath` 钉住。

3. **`managed_render:` 必须有自己的 id 前缀。** 若复用 `managed_prefix:`，native 的 `parse_managed_prefix_rule_id` 会成功解析并把它当普通同步 prefix，**owner 预筛随之丢失**——游戏本体那 9 个 `RawImage`（含高频重建的模糊截图与波形）每次网格重建都会穿一趟 native→managed 边界。§4.1 早已把预筛写成设计意图，但只有实现时才发现"用哪个前缀"是它的实际执行点。

#### §9 三项未决事项定案

1. **不需要改可访问性。** `Expression.Call` 直接绑定 `protected override`，编译成 `Action<object?>` 缓存在 binding 上。倾向的 open delegate 方向对，但 `CreateDelegate` 需要静态已知的 `Action<T>` 而 `T` 只在运行时可知；表达式编译同时解决了这一点和"不改 MOD 元数据"。测试桩用的就是真 `protected override`。
2. **`VertexHelper` 不按指针缓存。** 该指针是 Unity 在回调期间自己持有的对象，回调返回后地址可被复用；按指针缓存会把复用后的另一个 `VertexHelper` 当成同一个。
3. **native 指针集合用新增专用导出。** rule/slot 是"按签名解析目标"的机制，而这里维护的是运行时实例集合；且 clear 必须能按 MOD 精确清空而不动别人的。实现为扁平排序 `vector` + copy-on-write（读侧无锁 `lower_bound`）+ 一份 per-MOD 列表供 teardown。

#### 两处实现细节值得记

**`.ctor` 用 `pop` 而非 `nop`。** `call MaskableGraphic::.ctor()` 弹掉一个 `this` 不压回，`pop` 栈效果相同，所以前面那条 `ldarg.0` 保持原样、无需删指令、无分支目标移动。代理 ctor 的两处危险已由 dump 逐条确认（此前是推断）：IL 里既有 `il2cpp_object_new(NativeClassPtr)`（在 abstract 类上分配）也有 `il2cpp_runtime_invoke(..., Il2CppObjectBaseToPtrNotNull(this))`（在绑定指针上二次构造），`pop` 同时消除两者。

**登记与撤销的顺序都是刻意的，且反向。** 登记时 native 指针**最后**发布（发布那刻起 hook 就能分派，所以之前每张托管表必须已一致）；撤销时 native 指针**最先**withdraw（teardown 第一件事就是 `ClearNativeRenderHosts`，早于 OnDisable/OnDestroy）。反序会留下"native 分派进来、映射表已空"的窗口，结果是宿主画自己的 quad 一帧。

#### 数字

全量托管回归 `1171` 通过、`1` 个既有 XPerfect JIT 测试按原条件跳过（新增 18 项：`PcCompatManagedRenderComponentTests` 9、`PcCompatManagedRenderComponentBridgeTests` 9，另在审计套件加 3 项）；`StArray.ModManager.Android` Release 构建与 Android `:library:assembleDebug` 均通过。

顺带修了一个非功能问题：本轮编辑把 `pccompat_hook_rules.cpp` 从 LF 转成了 CRLF（仓库 `core.autocrlf=true`），而同目录另外 16 个 cpp 是 LF，且三个测试按 `"\n}\n"` 解析该文件。已还原为 LF。

#### 明确不宣称

**改写干净不等于雨能渲染。** 三个 IL2CPP 相关的宿主操作（绑定托管外壳、读实例指针、包装 `VertexHelper`）在本机测试里是 fake，被证明的是桥按正确顺序调用它们，不是它们本身可用。以下全部需要设备：绑定后的实例是否真收到 Unity 的 `OnPopulateMesh`、顶点是否正确提交、`SetVerticesDirty` 是否触发重建、`.ctor` 置 `pop` 后 `RawImage` 内部状态是否完好、per-drop hook 在 108 键连击下的开销、native 预筛的真实命中率。

JPOV 的 `set_txtLevelNameOriginalPosition` 代理面扩容已完成；本机托管重写审计现为
`JPOV/JPKV/JRP/JAMod/Loader` 全部 clean。设备侧功能和渲染仍不在本机验证范围。

### 2026-08-23 代理面闭包扩展与字段 facade 修复

本轮以 `E:\TEMP_SHARE\adofai_decomp_312` 的 3.1.2 反编译源码、Steam PC 3.1.2
`Assembly-CSharp.dll` 和当前 Android dump 为输入，继续推进通用 generated proxy surface，未加入
JPOV 专用运行时分支。

- `ProxyInputClosure` 的 `P` surface 现在先解析 PC `PropertyDefinition`；若 PC 端是同名字段，
  则选择该字段作为 generator 输入，由现有 Il2CppInterop field-accessor pass 生成完整
  `get_*/set_*` facade。`G` 不会降级成可写字段，仍失败关闭。
- 因此 `P|Assembly-CSharp|scrController|txtLevelNameOriginalPosition` 适配了 PC 的
  `Vector2?` 字段和 Android 生成的 nullable proxy 属性，保留 null、`Vector2.Zero`、x/y 的完整语义。
- `ModAssemblyRewriter` 增加受限的 `System.*` 与 `Il2CppSystem.*` generated corlib 类型等价匹配，
  只用于字段 accessor 签名比较；其它代理类型仍要求精确匹配。
- 根据 JPOV/JPKV/JRP 真实发布程序集经 `ProxySurfaceScanner` 扫描并与 Android catalog 复核，
  扩展人工 surface，覆盖 JsonUtility、VertexHelper/UIVertex、Resources、sceneLoaded、
  Color32、FontStyles、GUI overload、TMP overload 等实际调用，不开放任意反射或未知成员。
- 重新生成结果：closure `194` exact types / `15` assemblies，显式字段 `144`、方法 `490`、
  属性 `13`；`missingAndroid=0`、`unresolvedMetadata=0`、generated proxy audit `0 issue`。
  7 个 `RF/RN` 条目因 PC 参考程序集没有对应成员而按既有 null-reflection 策略保留诊断，不是 Android
  metadata 缺失。
- `PcCompatUmmModRewriteAuditTests` 定向回归 `16/16` 通过；JPOV/JPKV/JRP/JAMod/Loader 的
  production managed rewrite 均输出 clean。仍需设备确认 UnityMain 的真实对象、渲染和场景生命周期。

### 2026-08-24 Android Managed 旧式 native Hook 回调域恢复

运行途中热加载 XPerfect 后，UnityMain 进入 `GameHooks.SwitchChosen` 时触发
`ModDataDomainRuntime.RequireCurrent()`，异常越过 reverse-P/Invoke 边界后 CoreCLR 主动 `abort()`。
这不是 Hook 安装失败：静态域隔离已把 XPerfect 的字段访问改为 `SetStaticSlot<T>`，但旧二进制仍用
`HookHelper.Hook` 手工安装 dispatch，未经过 Source Generator 的 callback wrapper；从 Unity 线程进入时
没有 ambient MOD domain。此前版本正常，是因为静态字段仍是进程全局状态，不要求 domain，并不代表旧
回调生命周期已经安全。

shadow ABI 升级为 `starray-native-isolation-rewrite-v11-complete-callback-scope`。重写器现在识别旧式
`HookHelper.Hook` 安装点，在安装期间捕获当前 generation gate，改用 `HookRuntimeGatedRequired`，并把每个静态
`ldftn` dispatch 拆为原业务体与同签名 wrapper。wrapper 取得 callback lease 后恢复完整 owner/session/generation/domain
上下文，确保回调内的静态槽、资源归属及文件/网络桥使用同一代 MOD 身份；退出按 LIFO 恢复调用前上下文，
不在高频 detour 上执行全量资源审计。业务体异常在 native 边界内截断，并按 callback 首次及 2 的幂次数限频记录
`NativeModCallback` 诊断；generation 已退休或正在竞态退休时不再进入 MOD。生成 IL 使用单一
`catch` 与显式 lease 释放，dnlib 正常计算 MaxStack；禁止用 `KeepOldMaxStack` 掩盖无效异常区。最小回归
同时验证 active generation 可读写隔离静态槽、retired generation 返回默认值且不抛异常。每个安装方法的
二参 `Hook` 数与静态 `ldftn` callback 数必须严格一致；动态来源或混合歧义在 shadow 发布前失败关闭。
`HookRuntimeGatedRequired` 收到空 gate 直接拒绝，不退回普通 Hook。Host 自身的 EGL/输入等进程级
Source Generator Hook 继续调用 `HookRuntimeGated`；它们没有 MOD owner scope，空 gate 是合法 Host
语义并应安装普通 Hook。插入 gate 捕获时会同步重定向 branch、switch 与异常区边界，避免分支绕过新增
参数准备。

真实 DLL 离线审计覆盖 XPerfect `12`、Replay `12`、ShowBPM `11`、LevelDebugger `3`，共 `38` 个旧式
Hook：原始二参 `Hook` 全部消失，逐一变为 `HookRuntimeGatedRequired`，业务 callback body 均不再被 `ldftn`
直接引用。审计同时发现 LevelDebugger 的 `StreamWriter(string,bool,Encoding)` 被既有文件隔离失败关闭；
已补通用 `NativeModPathBridge.OpenStreamWriterEncoding` 构造桥，不加入 MOD 白名单。验证结果：
`NativeModShadowRewriteTests` `18` 通过、`1` 项可选审计跳过；真实 DLL 审计 `1` 通过；`StArray.ModManager.Android` Release
构建 `0` 错误。未执行实机、ADB、安装或顶层全量构建；热加载与实际 UnityMain 回调由用户实机验收。

同日回归确认：曾把 `HookRuntimeGated(null)` 全局改成拒绝，误伤了 ModManager 自身从 lazy-install timer
安装的 `EglHooks`。Source Generator 会先捕获 gate，但 Host timer 没有 MOD scope，因此得到 null；旧
`ImGuiEGLRenderer.InstallInstance` 又忽略 `EglHooks.InstallHooks()` 的 bool 返回值，最终日志仍宣称 renderer
安装成功，实际 `eglSwapBuffers` 从未 Hook，表现就是请求 UI 后无界面、无准确错误而其它模块日志正常。
现已按上述双 API 拆分，并要求 EGL 安装返回 false 时记录 `EGL hook install returned false` 且整个 renderer
安装失败。宿主宽松语义、MOD strict 语义、EGL 返回值合同与 callback wrapper 共 `4` 项定向回归通过；
38 个真实 MOD Hook 的 v11 离线审计仍全通过。

### 2026-08-26 通用动态数据源兼容决策

已冻结通用方案：只重写加载副本，接管动态 getter 工厂，保留原 MOD 方法体，使用
稳定 IL2CPP 代理对象图承载关系语义，并以同代 native snapshot 优化高频标量；不修改
JPOV/JPKV/JRP 源码，不按 MOD 名称特判。代理访问受 owner/session/lease 和 generation
约束，缺失或失效时失败关闭。详细决策见
[`GENERIC_DYNAMIC_DATA_SOURCE_COMPAT_DECISION.md`](GENERIC_DYNAMIC_DATA_SOURCE_COMPAT_DECISION.md)。
通用 getter bridge、session 绑定、稳定对象缓存及生产托管审计已在 2026-08-26 落地；真实
IL2CPP 对象图返回和设备数据刷新仍未验收。

### 2026-08-26 JPOV 共享数据源刷新闭合

真实 JPOV recipe 已确认包含 `OverlayPollTelemetry`。为消除该 Hook 的单点依赖，managed
UnityMain 帧现以 100ms 间隔驱动 host-owned 共享 sampler；native telemetry cache 同时拆分为核心
状态和音频/checkpoint/身份/速度等可选能力组，外围 ABI 缺失不再拖垮整份进度快照。已知标量在非
gameplay 状态返回同代安全值，不再进入半构造对象图；新增按 MOD/成员/generation 限频诊断。
定向回归 `126/126` 通过，arm64 Release `starray_modmanager` 单目标编译、链接和导出符号审计通过。
未操作实机，设备数据正确性仍待用户验收。

### 2026-08-26 JPOV V7 对象根闭合

设备上“进度可用而其余 HUD 无数据”的后续根因是：高频标量已进入共享 snapshot，但 JPOV 的
BPM、时间、floor、checkpoint 和速度链仍从动态 getter 取得 controller/conductor/level maker 等
对象关系；V6 没有提供稳定对象根。native snapshot 已升级到 V7，新增显式字段有效位以及 controller、
conductor、level maker、current/first floor、song、planetary system 七个根指针。managed resolver
按成员语义物化 generated proxy，并以 MOD、resource generation、session epoch、类型和指针隔离缓存。
采样器使用同一批 singleton/ADOBase/Hook 实例完成根选择、发布和 ready 判定，避免初始化竞态把有效
controller 覆盖为 0。缓存 ABI 升级为 `xphorror.pcmod-managed-cache.v71-snapshot-object-roots`。

本机定向回归 `131/131`，Android managed Release、arm64 native 单目标构建与 `122/122` JNI 导出
审计均通过。候选 `StArray.ModManager.Android.dll` SHA-256 为
`55ECA13E6A294FF3944FAEBAA7865E7B66FAD062711A27BDE96C70084E8216BD`，
`StArray.ModManager.dll` 为
`7AE7B4B87EBE7BF85D700C53FA864FC5F8C35DBACD1974EFAE8353DDB2D8CD13`；最终 stripped SO 大小
`3,140,400` 字节，SHA-256 为
`97C24FE6BE8941FF851E75B23A70AC4BFADFE1EE9F4FFF18A6AC9FE20BB4D46A`。未修改 MOD 源码或原始 DLL，
未全量构建、未生成 APK、未同步 runtime、未操作实机。设备数据刷新仍需用户验收。

### 2026-08-26 JPKV 1.7.0 通用 KV Adapter 识别闭合

真实发布 DLL 的自绘和资源物化已经成功，但旧行为扫描器只识别“provider 返回数组并存入当前方法 local”的形态。JPKV 1.7.0 在调用者中把 `GetKeyCode/GetFootKeyCode` 结果缓存到字段，再把字段作为 `KeyCode[]` 参数传给 `ProcessKeyGroup`；共享 Settings 字段又把输入、GUI、字体和配置连接为一个大分量，最终错误选择 `OnDestroy` 为输入监听器，且没有 `BindingProvider` 或 identity transform，Replay 无法取得 consumer。

行为扫描升级为 `keyviewer-behavior-scan-v7-parameter-provider-transaction`。扫描器现在证明 `KeyCode[]` 参数的 same-index `Input.GetKey`、held 比较/写回和 rising-edge count，再从 helper 调用点反向还原数组实参，经缓存字段 writer 追到零参数 provider。核心角色只取事务已证明字段，不再把 GUI 开关标成 held/count。真实 JPKV 结果为 input/lane/transition/count/inputActivation 全部 Proven，监听器为 `ProcessKeyGroup`，provider 为 `GetKeyCode/GetFootKeyCode`，transform 为唯一 `UnityKeyCodeIdentity`；默认 Auto 配置可生成唯一 10-lane `VerifiedLoweredBinding`。

Replay 启动还要求 Ephemeral 统计事务。JPKV 的 `Count` local 来源和 `Queue<long>[]` 已加入精确快照；动态 profile JSON 无法使用旧静态路径契约时，已验证自绘 KV 使用 owner/resource-generation 绑定的 data overlay 有界快照，禁止跨 owner 和链接遍历，不猜测 Save 方法。KeyViewer、输入桥和路径隔离组合回归 `124/124` 通过。未修改 JPKV/JRP/JPOV 源码或 DLL，未操作实机、ADB、安装、APK 或顶层全量构建；设备侧 Replay 输入显示仍由用户验收。

### 2026-08-26 PcCompat VFS 目录文件枚举闭合

JPKV 在 `ScanCustomFonts` 中先对安装根下的 `CustomFont` 执行 `Directory.Exists/CreateDirectory`，再从原始安装路径调用 `Directory.GetFiles`。前两项已被 VFS 接管：创建实际落入 data overlay；`GetFiles` 却没有重写，因此仍枚举不存在的包层路径并抛 `DirectoryNotFoundException`。同类只读审计确认真实 JPOV 有 1 个三参 `GetFiles`，真实 JPKV 有 2 个三参加 1 个两参调用，四处此前都绕过 owner VFS。

`PcCompatManagedPathBridge` 现接管 `GetFiles/EnumerateFiles` 的 1/2/3 参数重载。安装根枚举合并 package 与 overlay，同相对名由 overlay 覆盖并返回实际可读路径；逻辑目录两层均不存在时仍保留标准 `DirectoryNotFoundException`。递归枚举逐目录执行链接穿透检查；`EnumerateFiles` 在 owner scope 内立即物化为有界快照，避免延迟枚举越过 session 生命周期。生产重写缓存升级为 `xphorror.pcmod-managed-cache.v76-directory-file-enumeration`，bridge ABI 为 `PcCompatManagedPathBridge.v5-directory-file-enumeration`，旧缓存自动失效。

路径语义、缓存合同、真实 JPOV/JPKV/JRP 生产重写与 KV Adapter 组合回归 `179/179` 通过；真实 DLL 中 4 个 `GetFiles` 调用全部桥接，重写副本不再残留原始调用。最终 Android Slim Release 定向构建 0 错误；`StArray.ModManager.dll` SHA-256 为 `DAF779AE452D068929603BEA621D9004ED428C2BFD46CF839CA0816C4FE1EDD5`，`StArray.ModManager.Android.dll` 为 `461D65BDC5EB3B76AB27529BC0754FA7D848AC38BFCFCCACD41862E8410C32D3`，`ModAssemblyRewriter.dll` 为 `07833AFB319580DC0A6EF82BC5F41C8B656789C25BC33E437AED23A9BBC0B0B5`。未修改 MOD 源码或原始 DLL，未运行顶层全量构建、未生成 APK、未同步 runtime、未操作实机。

### 2026-08-27 lowered plan 与 MOD 自有配置的漂移闭合（provider 序列变更观察器）

本轮先按接手文档 §19 做离线事实核对，核对本身推翻了该文档 §16 建议的工作顺序。

**离线审计（`ilspycmd` 读 2026-08-12 发布 DLL，SHA-256 `ABA779…6AED`）确认的形态**：`GetKeyCode()` 是 `static`，每次调用现读 `Settings.Data.KeyViewerStyle` 做 switch，8 种主样式各返回一个不同的数组字段；`GetFootKeyCode()` 同理 8 种。MOD 自己在 `ProcessMainAndFootKeysInUpdate` 按 `cachedKeyStyle != data.KeyViewerStyle` 刷新缓存，**所以 MOD 侧会跟着配置变**。全量输入查询点也一并量化：`Input.GetKey` 2 处（`ProcessKeyGroup`、ghost）、`Input.GetKeyDown` 1 处（改键）、`GetKeyUp` 0 处；`Application.isFocused` 2 处（`Update` 第一句、`ProcessKeySelection`）；`get_anyKeyDown` 已在生产规则内。

**缺口**：我们侧不跟着变。`RefreshKeyViewerPreviewRegistration` 只有 4 个触发点——构造、`CompleteLoad`、`OnManagedActivationCompleted`、以及用户保存**我们自己**的 override store——全是一次性生命周期事件，`PcCompatManagedModSession` 里没有任何指纹或重新 lowering 机制。因此初值大概率正确（最晚触发点晚于 MOD 的 `LoadSettings`，读到的是持久化样式），但用户在 MOD 自己的菜单里改样式或改键位之后，快照与 MOD 的实时配置必然分叉：触摸 lane 发布 MOD 不再查询的 identity，MOD 查询没人发布的 identity，**输入静默停止到达且不会自愈**，直到重载 MOD。这是可离线证明的功能失效，不依赖设备日志，比接手文档 §8.4 #1「需要确认调用时点和值」的表述强。

**实现**。`PcCompatKeyViewerBindingPlanLowerer` 新增 `ResolvedProviders` 输出：lowerer 本来就调用了每个候选 provider，因此它是唯一能在不二次进入 MOD 代码的前提下报告原始序列的位置；只报告每个 feature **最终选中**（含恢复路径选中）的那一个，被拒候选不进入观察集，否则每个轮询周期都会执行不支撑任何活跃 plan 的 MOD 代码。External 模式的 presentation-only feature 也报告，它的标签由同一序列渲染、会同样变陈旧。

新增 `PcCompatManagedProviderSequenceWatcher`（通用，只认 provider role 与整数序列，不认样式、字段名或键数）：按 candidateKey 记录指纹、检测漂移、自带 500ms 轮询闸门。三处判断是刻意的：

- **解析失败也算变化**。provider 抛错、返回不足当前计划所需前缀或包含非整数值时，报告变化才能让调用方撤下 plan，而不是让 consumer 继续发布 MOD 不再读的 identity。撤下走既有 `RefreshKeyViewerPreviewRegistration` 的 `Remove` 先行路径，天然失败关闭。
- **基线按 candidateKey 合并而非整体替换**。lowering 失败什么都不报告、部分失败只报告成功的 feature；若把没被提到的 provider 丢掉，正好丢掉的就是「能让它恢复」的那次值变化，MOD 会被困在失败态直到重载。
- **指纹覆盖当前计划实际消费的前缀**。布局可合法包含 108 键或更多项；后缀变化不改变当前触摸 lane 投影，不应触发空转重新发布，也不应迫使兼容层完整枚举 provider。

驱动侧：`PcCompatManagedModSession` 仿照既有 activation observer 增加 configuration poll observer，从 `TryDispatchUpdate` 在 `TryDispatchUpdateCore` **之外**通知——观察器解析 MOD 成员时会自己进入 update context，在已进入的作用域里再嵌套没有理由。闸门放在观察器而非会话：会话无法知道任意观察器的正确节奏，两层计时器会让实际节奏变成两者之积；闸门关闭时每帧代价是一次锁加一次比较。`PcCompatModPlugin` 订阅后在检测到漂移时记录一次低频日志并重新 lowering/register，同时把漂移原因显示在设置面板（重新发布本来是不可见的：plan 静默变成另一个，这是唯一说明哪个 provider 动了、动到什么值的地方）。generation 变化时清空指纹——新 generation 重载了 MOD 自己的配置，旧指纹对它不携带信息。

**未升级缓存或 bridge ABI，这是刻意的**：本轮没有改任何重写规则、闭包清单、bridge 约定、泛型参数擦除、setter 后置语义或生成代理面，改动全部在宿主侧的 lowering/发布链内。按 §5 的合同这几类才强制升级身份；无必要地升级会让设备白跑一次全量冷改写。

**顺带修掉的既有缺陷**：`PcCompatOverlayRuntime.CloneSnapshot` 漏掉 82 个可写属性中的 10 个——`SessionEpoch`、`HasExplicitGameSnapshotValidity`、`ValidGameSnapshotFields` 与全部 7 个对象根指针，都是 JPOV V5/V7 期间新增的字段。`OwnerCloneCopiesEveryStoredSnapshotProperty` 正是为拦这个而写，但它的 `CreateNonDefaultValue` 没教过枚举，于是在能报告漏项之前就抛异常挂掉，缺陷被这个异常盖住。**它当前是潜在的而不是活跃的**：owner 投影克隆只有 4 个消费者、全在 `PcCompatModPlugin` 自己的 HUD/设置里，且都不读这 10 个属性；JPOV 的数据链两处 `FromOverlay` 取的都是 `GetSharedGameSnapshot()` 共享实例，不经过克隆。**因此这不是接手文档 §7 记录的 JPOV 设备现象的根因，不得如此宣称。**

**回归**：全量 `1360` 通过、`0` 失败、`2` 按原条件跳过（新增 12 项测试）；`PcCompatUmmModRewriteAudit` 27/27，真实 JPOV 与 JPKV 发布 DLL 均为 `Issues` 空、`ManagedBridgeIssues` 空、`OutputWritten=True`。本轮测试首次带 `w64devkit` 的 gcc 在 PATH 上运行，因此 `test_native.dll` 是真编出来的，只跳过需要 cmake 的 Windows native 项目。Android Slim Release 定向构建 0 错误；`StArray.ModManager.dll` SHA-256 为 `45F58F2BE219F420D1A0AB9FED52F360E2DF86E232D104E5C6C860809F930933`，`StArray.ModManager.Android.dll` 为 `DC66BACCD1DA4BCD4EA41B5A8E1CC6DDE17E95243FB2C5BC56C6392E682A816E`。

**发现的文档欠账**：无。此前本条曾断言「v77–v81 五次缓存升级没有状态条目」，那是错的——本文件顶部是倒序 changelog 区段，`v81-input-hotpath-diagnostics-removed` 连同字体材质绑定与输入热日志清理的完整记录就在开头第一条，只是本节（`## 当前验证记录`）是按时间正序追加的另一个区段，只看本节末尾会误判文档落后于代码。同一错误结论也曾写进 `doc/HANDOFF_PCCOMPAT_CURRENT_STATE_20260827.md` §5，已一并更正。

未修改 JPKV/JPOV/JRP 源码或原始发布 DLL，未按 ModId 或字段名特判，未运行顶层全量构建、未生成 APK、未构建 native SO、未同步 runtime、未操作实机/ADB/安装。设备侧仍需用户验收：改样式后触摸是否真的继续到达、Full108 保持 plan 且只投影所需前缀的实际表现、500ms 轮询在实机的开销，以及 provider 反射调用在 UnityMain 上的真实耗时。

## 8. 2026-08-28 ADOFAIOnlineMod 发现与原生 shadow 闭合

本节的 v12 复核已完成：实际 `ADOFAIOnlineMod.dll` 的 RVA 字段 `<PrivateImplementationDetails>{GUID}.D98...::3` 的直接声明类型名为 `D98...`，生成标识位于 namespace/owner 链，而不是直接类型名。重写器现检查字段声明类型和 RVA 值类型定义的完整 owner/namespace 链，并要求全部字段句柄引用严格匹配 `ldtoken -> RuntimeHelpers.InitializeArray(Array, RuntimeFieldHandle)`；因此不会把真正可变静态状态放行。Release 探针结果为 `issues=0`、`outputExists=True`，RVA 定向回归为 `2/2`，非 `InitializeArray` 句柄仍失败关闭。

本轮回查的目标是 `ADOFAIOnlineMod` 同时存在 `Info.json` 与 Android `IModPlugin`
入口时被误分到 PC 兼容层，以及删除 `Info.json` 后无法发现入口的问题。

- 发现器现在先读取入口 DLL 的 PE/metadata，只证明具体的非抽象 `IModPlugin` 类型，不构造插件、
  不执行身份 getter。探针确认当前发布入口为 `ADOFAIOnlineMod.Mobile.OnlinePlugin`，所以 PC
  形状的 `Info.json` 只作为展示/更新元数据，不再压过原生加载器。
- 删除清单后仍使用同一个入口 DLL 进入原生 shadow 路径。真实 DLL SHA-256 为
  `9416AF0AB055EE5E9B62903FA23ECE3028B2EFA976A93FEBE0AF3CA95FE97843`；其主程序集和依赖闭包
  均使用生产 bridge 规格重写，`Issues`、`MethodIssues`、`ManagedBridgeIssues` 均为空，输出成功。
- 本次真实闭包审计覆盖文件/目录/文件流、路径、异步定时器和网络相关调用；未证明的隔离调用仍保持
  失败关闭，不通过 MOD 名称或字段名特判。
- 回归覆盖“初始存在 PC 形状 `Info.json` 仍归类原生”、真实入口 metadata 探针以及真实
  `ADOFAIOnlineMod` 完整托管闭包，定向结果为 `4/4` 通过；既有原生 shadow 重写/路径组仍为
  `37/37` 通过。

本轮未修改 `ADOFAIOnlineMod` 源码或原始 DLL，未连接实机/ADB，未生成 APK，未运行顶层全量构建，
未同步设备 runtime。设备端仍需验证有无清单两种安装形态都能被发现并正常启用。

### 2026-08-28 ImageSharp 编译器生成 RVA 数据证明闭合

`ADOFAIOnlineMod/SixLabors.ImageSharp.dll` 的首次强签名放行回归暴露了一个通用元数据证明缺口：
该程序集的只读常量不只使用 `ldtoken -> RuntimeHelpers.InitializeArray`，还使用
`ldsflda -> ldc.i4 -> newobj ReadOnlySpan<T>(void*, int32)`。后者同时出现在嵌套
`__StaticArrayInitTypeSize=N` 值类型和 `int16/int32/int64` 原始 `static initonly` 字段上，
旧证明器把它们误报为可变 RVA 静态字段。

`ModAssemblyRewriter` 现使用 `v14` 格式版本
`starray-native-isolation-rewrite-v14-readonly-rva-span`，并在满足以下全部条件时将该字段归类为
`SharedImmutable`：字段属于编译器生成的 `<PrivateImplementationDetails>` owner/namespace 链；
值类型是可确定大小的固定 blob 或原始标量；直接读取或取地址构造
`System.ReadOnlySpan<T>(void*, int32)` 时字段必须为 `initonly`，而仅用于
`ldtoken -> RuntimeHelpers.InitializeArray(Array, RuntimeFieldHandle)` 时允许编译器省略
`initonly`；span 长度非负且不超过 RVA 数据大小。非证明用途的直接读取、取地址、写入、
`Span<T>`、未知指针、独立 `RuntimeFieldHandle` 和越界长度仍失败关闭，不按 ImageSharp 或 MOD
名称特判。

回归结果：RVA 合同 `3/3`，真实 ImageSharp 强签名/Parallel/File/静态数据重写 `1/1`；真实输出
保留程序集名称、版本和公钥 token，且不保留原 PE 强签名标志。未修改 `ADOFAIOnlineMod` 或第三方
ImageSharp DLL，未操作实机、ADB、APK、native SO 或顶层全量构建。仍需用包含新 `v14` 重写器和
runtime 的完整设备资产做实机验收。

最终托管 Release 定向构建（`Il2CppInteropAndroidSlim=true`、`PcCompatRewrittenOracleDefault=true`）
通过，未运行顶层构建脚本：

| 产物 | 大小 | SHA-256 |
| --- | ---: | --- |
| `StArray.ModManager.Android.dll` | 639,488 | `CDF39A1562DB3E61AA0E69A7E2425FF09394144A05207F953EF338307E24AC4C` |
| `StArray.ModManager.dll` | 19,905,024 | `367F04DA1799D95E7CF9A0FAD03E4F9F04CEF2DCFBCF6395725D616646A08826` |
| `ModAssemblyRewriter.dll` | 291,328 | `69CFB3501053A49C4D9ADE76CC2582ACB9ADCEFABF8EADA3763313AC81532720` |

### 2026-08-28 泛型静态字段 owner 隔离与完整闭包回归

上一轮已解决真实 ImageSharp 的泛型嵌套 `MemberRef/TypeSpec` 误判，但还缺少两项能防止
回归的合同：重写后的调用是否真的携带闭合 owner，以及不同闭合 owner 是否会串用同一个
静态缓存。现已补齐这两项测试，并修正运行时 slot key 的兼容性语义。

- `ResolveLocalField` 先从 `MemberRef.Class` 的 `TypeSpec/GenericInstSig` 取得底层本程序集
  `TypeDef`，再按 owner 内字段名解析；真正属于私有依赖的字段仍按跨程序集访问失败关闭。
- 静态字段计划保存字段类型和实际 owner 类型。闭合泛型 owner 使用
  `GetStaticSlotForOwner<T, TOwner>`、`SetStaticSlotForOwner<T, TOwner>` 或
  `GetStaticSlotReferenceForOwner<T, TOwner>`；非泛型字段继续使用旧 bridge。
- slot 字典 key 为 `(slotId, closedOwnerType)`。`valueType` 不参与字典 key，以保留既有合同：
  同一 domain、同一 owner、同一 slot 若被不同类型访问，仍由 cell 类型校验抛出异常；不同
  闭合 owner 即使 slot ID 相同也完全隔离。
- 编译器生成 RVA 的只读证明保持失败关闭边界：只有完整生成 owner 链、固定大小和已证明的
  `InitializeArray`/`ReadOnlySpan` 用途才允许共享不可变数据。

回归覆盖：闭合泛型 `MemberRef + TypeSpec` 的 `get/set/ref` 三种静态字段访问、两个闭合
owner 的运行时值隔离、真实 `ADOFAIOnlineMod` 完整 DLL 闭包以及真实 ImageSharp Parallel
闭包。相关测试实际结果为 `39/40` 通过，`1` 项按既有环境条件跳过；重写器和托管测试项目
编译通过。未修改第三方 MOD 源码或原始 DLL，未操作实机、ADB、APK、native SO 或顶层全量
构建；最终 Release 重写器产物仍待本轮定向构建后核对，设备侧尚未验证。

### 2026-08-28 泛型静态初始化 owner 句柄闭合

泛型静态字段的第一版 owner 隔离已经能分开 slot，但其搬移后的 `.cctor` 仍只向运行时
传递 `RuntimeMethodHandle`。对 `GenericStaticProbe<int>` 这类闭合类型，CLR 要求同时提供
闭合声明类型句柄，否则 `MethodBase.GetMethodFromHandle` 会拒绝解析方法，表现为
`Cannot resolve method ... because the declaring type ... is generic`。

现已将初始化 bridge 扩展为
`EnsureStaticTypeInitialized(Int32, RuntimeMethodHandle, RuntimeTypeHandle)`。重写器为每个
字段访问点传入实际 owner 类型 token；显式初始化入口也传入其类型 token。运行时初始化
状态键扩展为 `(initializerMethod, closedOwnerType)`，并使用带 owner 的反射解析重写后的
初始化方法；旧的两参数公开入口保留给已有托管调用。该规则是按 CLR 泛型方法句柄语义
实现，不依赖 MOD 名称、字段名或特判。

本轮新增的泛型初始化运行时回归与既有粘性失败回归通过 `2/2`；完整
`NativeModShadowRewriteTests` 与 `ModDataDomainTests` 通过 `39/40`，`1` 项既有环境条件
跳过。测试使用流加载隔离临时程序集，避免 Windows ALC 文件锁影响清理；未修改第三方
MOD，设备 runtime 尚未同步或实机验证。ABI 已升级为
`starray-native-isolation-rewrite-v16-generic-static-initializer-owner`，最终 Release
重写器及 Android 托管产物已完成定向构建并核对如下：

| 产物 | 大小 | SHA-256 |
| --- | ---: | --- |
| `StArray.ModManager.Android.dll` | 639,488 | `5C04DAD0EB4482DFF7CE881AD2A963455C242B837121BA883A90A27D4B43552A` |
| `StArray.ModManager.dll` | 19,909,120 | `E37B835D93E2F9F77179B9F6EC42A07B26C88F2F97EB62330DB39302AFE818B4` |
| `ModAssemblyRewriter.dll` | 296,448 | `C6E4323C86799B6561D25375D4FFE5DC707860506C6D17F57C0E0C40719F3B22` |

上述构建使用 `Il2CppInteropAndroidSlim=true` 与 `PcCompatRewrittenOracleDefault=true`，未运行
顶层构建脚本，设备 runtime 尚未同步或实机验证。

### 2026-08-29 Android Native MOD 暂停 Shadow 重写

当前 Android 生产加载策略已切换为直接加载 Native MOD 原始程序集：启动阶段调用
`NativeModShadowRewriteRuntime.Disable()`，不再注册 Native MOD shadow rewrite provider；扫描、首次
加载和重载均绕过 `NativeModShadowPackage.Prepare`，并使用 MOD 目录中的原始入口 DLL。设备上已有的
旧 shadow 缓存不会被使用，也不需要通过删除缓存来切换策略。

本次变更只关闭 Android Native MOD 的 shadow 重写，不修改第三方 MOD，不删除重写器、shadow package
或其离线测试能力；PcCompat MOD 的代理/重写链不受影响。代价是 Native MOD 不再获得 shadow 重写提供的
静态字段域隔离、文件路径重定向、异步回调代际门禁和网络调用改写，这些能力需由 Native MOD 自身与宿主
运行环境保证。该策略已加入 Android 启动顺序与 loader 直接路径合同，待定向构建及设备侧验证完成后再更新
最终产物哈希。
