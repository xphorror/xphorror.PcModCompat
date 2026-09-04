# StArray ModManager Android 上游同步计划

> 当前审计与实现状态见 [`UPSTREAM_ANDROID_API_SEMANTICS_AUDIT_20260731.md`](UPSTREAM_ANDROID_API_SEMANTICS_AUDIT_20260731.md)。`1.0.6` 增量同步记录见 [`UPSTREAM_ANDROID_SYNC_1_0_6.md`](UPSTREAM_ANDROID_SYNC_1_0_6.md)。上游目标 `7f84f6a1ea0f578a5381f3dd501802f8a45fc1c0` 已完成本机同步，Core/Android ApiCompat 均为 `0` 断点，managed 全量测试 `886/886` 通过。

## 文档定位

本文记录本仓库从上游 StArray ModManager 同步 Android 能力时的决策、边界和验收条件。本文只描述待实施方案，不表示对应能力已经进入生产路径；当前实现状态仍以 [`IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md) 为准。

审计快照：

```text
审计日期: 2026-07-27
上游仓库: E:\TEMP_SHARE\git\StArray.ModManager
上游提交: 2719a69
共同导入基线: 50b1698
本地仓库: E:\ADOFAI\ADOFAI_312_HOOKS
目标平台: Android arm64-v8a
目标游戏: ADOFAI 3.1.2 r143
目标 Unity: 6000.3.10f1
当前生产叠加层: ImGui EGL
```

上游工作区审计时存在 39 个未提交的换行变更。同步判断只以上游已提交的 `origin/main` 为依据，不采纳该脏工作区内容。

## 总体决策

1. 只评估 Android、ModManager 叠加层和 Android 上的 PC MOD 兼容链。
2. 不同步 Windows、Mono、桌面图形后端、CI、release 和版本下载配置。
3. 本地已经大幅扩展 ModLoader、HookBroker、异步输入、PcCompat 和 Android 生命周期，禁止整体 cherry-pick 上游大文件。
4. 字体是 P0 必做能力。ModManager 叠加层必须携带自己的中文字体，不能依赖设备 ROM 的字体路径。
5. 普通描述、错误、toast 和诊断正文不得依赖 Emoji、私用区图标或不稳定特殊符号。
6. 尚未完成代码、构建和实机验证的同步项必须保持为“待实施”。

## 同步矩阵

| 优先级 | 更新项 | 同步策略 | 当前状态 |
| --- | --- | --- | --- |
| P0 | 内嵌 NotoSansCJK 字体 | 同步字体资产，重写 Android 共享字体加载器 | 本机完成，待实机 |
| P0 | 更新 FontAwesome 字体 | 同步上游新版字体文件，保留受控图标用途 | 本机完成，待实机 |
| P0 | 叠加层特殊字符清理 | 清理本地化文本、toast、描述和占位符 | 已实现并通过资源测试 |
| P0 | MOD 入口类型解析 | 手工引入 `ModEntryPointAttribute` 和容错 resolver | 已实现并通过定向测试 |
| P0 | Android RuntimeAbstractions ABI | 补齐上游公开抽象并适配本地 IL2CPP runtime | 已实现，managed 全量与 arm64 构建通过 |
| P0 | 设置反序列化容错 | 每个设置成员独立读取、报错和继续 | 已实现并通过定向测试 |
| P1 | Inspector 扩展 | 兼容式合并 non-public/property/static 和扩展 attribute | 待实施 |
| P1 | JNI API 完整同步 | 保留本地扩展并补齐上游 managed/native ABI | 本机完成，待实机语义验证 |
| P2 | Stub/Assembly 生成器 | 仅作为隔离的开发工具评估 | 待实施 |

## P0 字体合同

### 资产

需要从上游同步以下两个资源：

| 资源 | 大小 | SHA-256 | 用途 |
| --- | ---: | --- | --- |
| `NotoSansCJK-Regular.otf` | 16,558,780 bytes | `2B1304A1A2D6B811A38C2C90A2BE503CBAC0BFBF4D8B0E6A6A598146564A61AD` | ModManager 中文、ASCII 和普通标点 |
| `fa-solid-900.ttf` | 322,024 bytes | `4BA69DAE6214FD61D71F44CD6F2BD802A955FFBD3317A71946EC133F41E8B0F0` | 受控操作图标 |

`StArray.ModManager.csproj` 必须把两个文件都声明为 `EmbeddedResource`。字体属于 ModManager 自身叠加层，不属于 MOD VirtualBundle、capability bundle 或 Unity HUD 字体链。

### 加载顺序

上游 `IImGuiRenderer.InitImGui()` 只能作为参考，不能原样复制。生产实现应使用独立的共享字体加载器，并遵守以下顺序：

1. 创建并设置当前 ImGui context。
2. 从嵌入资源加载 NotoSansCJK，作为基础字体。
3. 以 `MergeMode` 合并 FontAwesome，只加入已使用的私用区图标范围。
4. 只调用一次 `io.Fonts.Build()`。
5. Build 完成后释放托管侧分配的临时字体内存和 `ImFontConfig`。
6. 字体初始化结果在 renderer 生命周期内保持不变，禁止每帧重建 atlas。

字体回退顺序：

```text
embedded NotoSansCJK
  -> Android system NotoSansCJK
  -> ImGui default font
```

内嵌字体失败必须输出一条包含资源名和失败阶段的限流日志，不能使用空 `catch` 静默降级。

### 字形范围

默认使用 `ChineseSimplifiedCommon + ASCII`，并补入当前本地化资源实际使用但不在默认集合中的字符。禁止默认使用 `ChineseFull`，避免 Android 上产生过大的 GPU atlas。

任意 MOD 名称、作者或外部输入中的 Emoji 不属于显示保证。无法显示的外部字符允许回退，但 ModManager 自己提供的固定文本不得出现缺字。

字体初始化后至少验证以下字形：

```text
A 0 中 设 兼 诊
```

同时验证当前保留的 FontAwesome 操作图标。任一固定 UI 字形缺失时，字体初始化应记为降级状态并写入诊断。

### Android renderer 边界

当前 Android 生产入口只安装 `ImGuiEGLRender`，因此首轮验收以 EGL 为准。Vulkan renderer 后续必须复用同一字体加载器，不得维护第二套资源名、内存所有权或 glyph range 逻辑。

## P0 字符政策

### 固定文本

ModManager 自己提供的描述、状态、错误、toast 和诊断正文只允许：

- ASCII 字母、数字和普通 ASCII 标点。
- 简体中文及正常中文标点。
- 明确经过字体验收的 FontAwesome 操作图标，但图标不得作为正文语义的唯一来源。

以下内容不得出现在固定正文中：

- Emoji。
- `✓`、`✗`、`⚠`、箭头、星形、旋转符号等装饰性 Unicode symbol。
- 私用区 FontAwesome 字符直接拼入本地化资源。
- smart quote、em dash 等可用普通文本替代的特殊标点。

状态和错误必须同时有文字。例如加载成功显示“已加载”，不能只显示一个勾号。

### 已发现替换项

| 位置 | 当前内容 | 替换要求 |
| --- | --- | --- |
| `ModManagerUI.cs` 空设置列 | `—` | `-` |
| `AddMod_ImportedReady` | `“加载”` | 改成不依赖 smart quote 的“点击加载” |
| `PcCompat_ClearWarning` | `“重载活动规则”` | 改成不带引号的普通描述 |
| 保存成功/失败 toast | `CircleCheck` / `CircleXmark` 前缀 | 删除图标前缀，只保留文字 |
| ZIP 导入说明 | `FileZipper` 前缀 | 删除图标前缀，只保留说明 |

FontAwesome 可以继续用于关闭、设置、展开、加载、停止等明确操作按钮。按钮必须保留 tooltip 或文本标签；字体加载失败时，用户仍应能从文字识别操作。

应增加资源测试，扫描两个 `.resx` 的 `<value>`，拒绝 Emoji、smart quote、em dash 和未列入白名单的 Unicode symbol。代码中的 FontAwesome 常量引用单独维护白名单，不与正文字符扫描混合。

## P0 MOD 入口解析

同步上游 `ModEntryPointAttribute` 和 `ResolvePluginType(Assembly)` 的能力：

1. 优先读取程序集声明的入口类型。
2. 验证入口类型实现 `IModPlugin`，且不是 interface 或 abstract type。
3. 属性无法解析时记录 warning，并回退到类型扫描。
4. `Assembly.GetTypes()` 抛出 `ReflectionTypeLoadException` 时继续使用 `ex.Types` 中成功加载的类型。

本地 `ModLoader` 有异步插件、PcCompat adapter、ALC 隔离和完整失败导出，禁止覆盖完整加载方法。只替换两处重复的入口类型选择逻辑。

## P0 Android RuntimeAbstractions ABI

Android 原生 MOD可能按上游 `StArray.ModManager.dll` 编译，并直接引用
`StArray.ModManager.RuntimeAbstractions.IAppDomain` 及其配套接口。此前本地仅同步了
JNI、Inspector 和部分 IL2CPP 修复，没有发布这一组类型，导致 MOD在入口执行前抛出
`TypeLoadException`。

当前 Android 合同为：

1. 发布上游 `RuntimeAbstractions` 的全部公开类型和成员，包括 domain、assembly、class、
   method、field、object、array、string、enumerable、stub attribute 和 screen helper。
2. `Il2CppDomain`、`Il2CppAssembly`、`Il2CppClass`、`Il2CppMethod` 和 `Il2CppField` 实现统一接口。
3. Android managed 入口在 MOD扫描前调用 `RuntimeManager.Detect()`，确保生成代码调用
   `RuntimeManager.GetDomain()` 时获得官方 IL2CPP domain。
4. 保留本地已有的 UTF-8 assembly open、外部线程 attachment ownership、IL2CPP exception
   propagation、array layout 和 GC-aware reference field setter，不用上游旧实现覆盖。
5. 本轮不引入上游 Mono P/Invoke 绑定；`RuntimeBackend.Mono` 仅保留公开 ABI，Android IL2CPP
   是当前生产语义。
6. 成员级上游差分必须为 `0 missing`，并由程序集反射合同测试锁住类型和接口。
7. Android 入口必须在 `RuntimeManager.Detect()` 前给 `Il2CppFunctions` 所在的
   `StArray.ModManager.dll` 安装 native resolver；`IL2CPP_LIBRARY_NAME`、`libil2cpp.so` 和
   `GameAssembly` 均惰性映射到 `RTLD_NOW | RTLD_NOLOAD` 取得的已加载 `libil2cpp.so` 句柄。
   resolver 是按声明 P/Invoke 的程序集注册，给 `StArray.ModManager.Android.dll` 注册不能覆盖核心程序集。
8. 上游旧 MOD 可能直接覆写 `IModPlugin.OnBackgroundGUI/OnForegroundGUI`，但不知道本地新增的
   `IPersistentModOverlay`。没有实现新接口时，真实覆写任一绘制回调即作为 legacy persistent overlay；
   显式实现新接口时始终以 `ShouldRenderWhenManagerHidden` 为准。能力检测按插件类型弱缓存，不能每帧反射，
   也不能阻止 collectible ALC 回收。

## P0 设置反序列化容错

当前本地设置加载会因单个字段类型变化或损坏而终止整份设置恢复。同步后必须：

1. 先解析顶层 JSON object。
2. 对每个已知设置成员单独执行反序列化和 setter。
3. 单字段失败时记录字段名、目标类型和异常摘要，然后继续其它字段。
4. 不认识的旧字段保持忽略。
5. 读取失败不能覆盖仍然有效的内存默认值。

## P1 Inspector 兼容合并

本地已经支持 public instance/static field 和 property 绘制，但绘制元数据与保存元数据尚未统一。上游提供以下可复用能力：

- 显式标注的 non-public field/property。
- `Tooltip`、`Header`、`ShowIf`、`Color`、`ReadOnly`、`Order` 和 `NoSave`。
- `Hotkey` 类型和编辑器。
- 绘制与持久化共用同一份 member metadata。

上游把全部设置项改为 opt-in。该规则不能直接同步，因为会隐藏现有未标注的 public 设置。兼容规则应为：

```text
public member without Ignore
  -> 保持旧版自动显示

non-public member
  -> 只有显式 ModSetting attribute 才显示
```

`ShowIf` 采用“隐藏”语义：普通项条件不满足时不绘制；条件标在 `Header` 成员上时隐藏整个分组，直到下一个 `Header`。`ReadOnly` 才表示绘制但禁用。

## P1 JNI `A` 调用迁移

上游 `JniNative` 的 managed API manifest 固定为 85 项。本地已在保留 Android 输入、Activity、Surface 和 data channel 扩展的前提下完成 `85/85` 同步；当前 `JniHelperNative` 有 97 个唯一绑定，其中 12 个为本地扩展，`jni_helper.c` 有 100 个唯一 helper 定义。迁移遵循以下规则：

1. 在不删除本地扩展的前提下补齐全部上游 `jnihelper_*` 导出。
2. 为 `jboolean` 参数和返回值显式使用 1-byte marshaling。
3. 优先把高频 JNI vtable delegate/varargs 调用改为 `Call*MethodA + JValue[]`。
4. 以固定 manifest 同时检查 managed 声明和 native 定义，并从最终 stripped arm64 SO 的 `.dynsym` 审计全部 helper 导出。
5. 最后才允许删除已无调用者的旧 helper。

不得整体覆盖本地 `JNI.cs`、`JniHelperNative.cs` 或 `jni_helper.c`。

## 不同步项

以下上游内容不进入 Android 同步主线：

- libinput `consume/consumeSamples` hook。它与 HookBroker、async_input 和 modal 输入所有权冲突。
- `UnmanagedHook` 和 HookGenerator 整体替换。所有物理 hook 仍由 HookBroker 安装。
- 上游 `Managed.cs`、Android bootstrap 和 renderer 大文件整体覆盖。
- `BehaviourManager/GameBehaviour`。它没有 MOD owner，卸载时不保证清理，并把生命周期绑定到 ImGui render frame。
- Mono runtime 和 Windows renderer。
- cimgui 目录迁移、CI、release、版本号和下载地址变更。
- 上游 `RuntimeAbstractions` 原始实现直接进入生产。

## 上游阻塞缺陷关闭记录

2026-07-28 已为上游和本地 legacy IL2CPP helper 分别关闭原六项缺陷：

1. `Il2CppDomain.Current` 缓存稳定 wrapper；线程附着改为 thread-local 深度和所有权管理。
2. Unix 探测使用 `RTLD_NOW | RTLD_NOLOAD`，其中 Bionic `RTLD_NOLOAD=0x4`；短期探测 handle 执行 `dlclose`。本地 PcCompat resolver 的 handle 需要覆盖整个 P/Invoke 生命周期，因此继续有意缓存而不关闭。
3. 数组数据指针统一跨过 `Il2CppObject + bounds + max_length`，空 unbox 不再继续解引用。
4. 实例值字段走 `il2cpp_field_set_value`，对象引用字段走 `il2cpp_field_set_value_object`；静态字段走官方 static API，不再直接写 GC 管理内存。
5. `runtime_invoke` 的 exception pointer 转为 `Il2CppInvocationException`，诊断包含格式化异常和 native stack；`Object.GetHashCode` 同时修正为 unbox 返回值。
6. 新增可替换的最小 IL2CPP runtime seam，以及 Domain、数组、字段、异常和 Stub 回归合同。

本地同步没有覆盖 PcCompat/Il2CppInterop 生产主线。Android foreign-thread guard 已证明线程退出阶段主动 detach 会与 Boehm pthread-key 清理竞态，因此本地 helper 在 Android 只记录并复用附着，不调用 `il2cpp_thread_detach`；Windows/Linux 仅释放 helper 自己创建的附着。Runtime/Stub 仍是 legacy/开发工具能力，不能据此替代 Il2CppInterop、generated proxy 或 PcCompat runtime bridge。

## 实施顺序

```text
1. 内嵌 Noto + 更新 FontAwesome + 共享字体加载器
2. 固定文本字符清理和资源扫描测试
3. ModEntryPoint resolver 和 ReflectionTypeLoadException 容错
4. 设置逐成员反序列化容错
5. Inspector 兼容式扩展和统一 persistence metadata
6. JNI API 完整同步（已完成本机 API/ABI 与导出验收）
7. 隔离评估 Stub/Assembly 开发工具
```

## 验收条件

- Android 单包和顶层完整构建成功。
- APK 中能找到两个嵌入字体资源，manifest resource name 与加载代码完全一致。
- 无系统 CJK 字体路径的测试环境仍能完整显示 ModManager 中文。
- 固定 UI 文本中不出现 `?` 缺字、Emoji 或未允许的特殊符号。
- FontAwesome 加载失败时按钮仍有可识别文本，不丢失操作语义。
- 字体 atlas 只构建一次，打开和关闭叠加层不会重复分配字体内存。
- MOD 入口解析和设置容错新增定向测试通过。
- 现有 managed 全量测试、Android native 构建和 arm64 导出审计保持通过。
- 实机验证覆盖启动、首次打开、关闭后重开、加载 MOD 后重开和关卡内打开五条路径。

## 2026-07-27 P0 实施记录

首批 P0 已进入源码：

- `AndroidImGuiFontLoader` 统一 EGL、Vulkan 和调试 renderer 的字体初始化。Noto 使用内嵌资源作为基础字体，系统 Noto 和 ImGui default 仅为降级路径；FontAwesome 只合并当前源码实际引用的图标字形。atlas 按 native pointer 只初始化一次，失败后也不逐帧重试。字体文件通过 64 KiB pooled buffer 复制到 unmanaged memory，Build 后释放。
- ModManager 固定正文移除了状态图标、toast 图标、ZIP 说明图标、详情标题图标、smart quote 和 em dash；设置按钮增加文字，不再只依赖私用区图标表达语义。
- `ModEntryPointAttribute` 和集中 `ResolvePluginType` 已替换发现与加载阶段的两处重复扫描。无效声明回退到 concrete `IModPlugin` 扫描；`ReflectionTypeLoadException` 保留成功类型并只输出一条有界摘要。
- `LoadSettings` 改为逐成员反序列化。坏字段保留内存默认值、记录单行 warning，并继续恢复其它字段。

本机验证：

```text
定向 Android upstream sync tests: 6/6 passed
managed full excluding known no-IL2CPP PInvokeTests2: 632/632 passed
unfiltered managed result: 632 passed / 1 known environment failure
Android Release quick build: passed
build_android_single.ps1 -Configuration Release -RewrittenOracleDefault: passed
proxy closure: 181 exact types / 14 assemblies
generated proxies: 192 types / 14 generic initializers
missingAndroid: 0
unresolvedMetadata: 0
proxy audit issues: 0
```

最终产物：

```text
StArray.ModManager.dll
  size: 18,516,992 bytes
  sha256: 0DCC3AEF78AC3182317A08FEC0084D23AC10362E6A8A745B07C7283CCA6C7B09

StArray.ModManager.Android.dll
  size: 450,048 bytes
  sha256: 618271D6B2089DC6B1C56054C3D06FA601ED6FB6532AB50E4C82A75480EEB514

libstarray_modmanager.so
  Build ID: ffbe396197348b489d2974db9294e889c036803a
  sha256: 2222542EF264E66B19E201F368A3A22F929EA9DCD36B4A42375D961974072C83
```

本段为 P0 基线。P0 字体仍保留“待实机”状态；实机必须确认日志出现 `ready:cjk=embedded,icons=embedded`，并验证首次打开、关闭重开、加载 MOD 后重开、关卡内打开、中文缺字和按钮图标。P1 结果记录如下。

## 2026-07-28 P1 Inspector 与 JNI A 调用实施记录

Inspector 已按兼容规则合并：未标注的 public field/property/static member 继续自动显示；non-public member 只有带 `ModSettingAttributeBase` 派生特性时才显示；`ModSettingIgnore` 始终排除。绘制与 Save/Load 统一使用 `GetSettingMembers`，因此显式 private、property 和 static member 能逐项持久化，`NoSave` 与 `ReadOnly` 不进入持久化集合。公开 `GetInspectorFields(Type)` 已按旧版 public instance field 合同恢复，避免破坏既有 MOD ABI。

`ShowIf` 已实现为真正隐藏。普通成员条件为 false 时不绘制；带 `Header` 的成员条件为 false 时隐藏整个分组，直到下一个 `Header`。`ReadOnly` 单独保留禁用语义。`Hotkey` 未绑定文本改为普通 `-`，按键捕获和列表添加文本进入中英文资源，固定文本不使用 Unicode ellipsis 或 em dash。

JNI 同步已从当前调用闭包扩展为完整上游 manifest，但没有整体覆盖本地 helper。当前覆盖引用、异常、UTF 字符串、实例与静态 primitive `Call*MethodA`、全部实例与静态字段以及对象数组 API。新增 8-byte explicit-layout `JValue`；`JValue.C` 使用 CLR `char`，`jchar` 参数和返回显式固定为 `UnmanagedType.U2`，`jboolean` 固定为 `UnmanagedType.I1`，`NewStringUtf` 使用 `LPUTF8Str`。C# wrapper 对调用参数继续使用 `stackalloc`，不读取 `JNIEnv` vtable 索引，也不调用 JNI varargs。

`Call*MethodA` 延续本地安全策略：native helper 在同一次跨边界调用内检查、输出并清除 Java 异常，返回对应 JNI 默认值。字段和数组 helper 保留 JNI 原始异常状态，由显式 `CheckException`/`ClearException` 管理。旧 helper ABI、Activity、Surface、输入和 data channel 扩展继续保留，Hook 安装所有权没有变化。

本机验证：

```text
JNI manifest and ABI contract tests: 2/2 passed
managed full unfiltered: 639/639 passed
Android Release quick build: passed
build_android_single.ps1 -Configuration Release -RewrittenOracleDefault: passed
proxy closure: 181 exact types / 14 assemblies
generated proxies: 192 types / 14 generic initializers
missingAndroid: 0
unresolvedMetadata: 0
proxy audit issues: 0
upstream managed JNI API: 85/85
managed JNI bindings: 97
JNI helper exports in final arm64 .dynsym: 100/100
```

最终产物：

```text
StArray.ModManager.dll
  size: 18,543,104 bytes
  sha256: A9C817FF371001BF9888B4C8BA9F084F0C508A04EB0F8013A0119DE2D6F4B01D

StArray.ModManager.Android.dll
  size: 453,632 bytes
  sha256: DF2223F6D8F01621C1C485A36166AA1650AC4283EF7151921CD584F95B996780

libstarray_modmanager.so
  size: 2,875,800 bytes
  Build ID: 6c0b7269861b1034df6218913578f56dd14f0e62
  sha256: E538A7C35FB70C35125F4794CACEE228DB7AF624DD78C174E0A268B9E2F45C34
```

依赖真实桌面 IL2CPP 导出的 `PInvokeTests2.Test1` 已从测试工程删除；它把宿主环境缺失当成产品失败，且不验证本项目可在普通 managed 测试环境重现的合同。

上游没有可直接移植的自动化 JNI 测试文件，仅有开发者手工可用结论。本项目因此建立自己的 85 项 manifest、ABI 布局/marshalling 和最终 SO 导出合同。P1 仍需实机覆盖 public 旧设置、显式 private/property/static 设置的保存恢复、`ShowIf` 分组隐藏、Hotkey 输入，以及 Activity/IME、文件导出、Toast、modal input、字段、数组和 primitive 返回路径；在完成设备端 Java fixture 功能测试前，只能声明 API/ABI 和产物导出完整，不能声明全部 Java 运行语义已经验证。

## 2026-07-28 modal input 双状态收敛

源码回归复现了“兼容 HUD 运行后回主菜单启用 managed self-render，随后触摸和测试宏同时失效”的共同阻断条件。`async_input` 直接读取 C++ `g_modal_input_active` 并在 active 时清空捕获队列；Android platform 却只查询 Java `sModalInputCapture`，并且只通过 Java bridge 间接修改 native gate。两份状态一旦因切换竞态或 JNI void 调用失败而分叉，managed 会误判 modal 已关闭，不再执行清理，而 native gate 会持续阻断官方输入和宏。

`AndroidModManagerPlatformServices` 现在直接读写 `modmanager_modal_input_is_active/set_active`，native 状态为 gameplay input 真源；Java 状态继续同步，用于 Back/Activity 行为。查询使用 native/Java OR，任意一侧残留都会继续触发清理。关闭时先清 native gate，再调用 Java mirror，因此 Java bridge 失败也不能继续阻断 async_input。未修改 async_input 的输入判定、宏或队列规则。

新增源码合同先在旧实现上失败，再在修复后通过；managed 未过滤全量 `637/637`，Android Release 快速构建和完整 `build_android_single.ps1 -Configuration Release -RewrittenOracleDefault` 均通过。代理闭包仍为 181 exact types / 14 assemblies，生成 192 types / 14 generic initializers，`missingAndroid=0`、`unresolvedMetadata=0`、audit issues=0。最终 SO 未变化，SHA-256 仍为 `AF6DE916FCBCC4A76D9348A6A0CA02EFAC087692383130F4594D142EC4FEBEFB`；`StArray.ModManager.Android.dll` SHA-256 更新为 `B2C3B6F146929C79216260195FAB6E9D27E6469A37D353D84D968D7E98CF1764`。实机需按原序列复验，并确认退出 ModManager 后触摸和测试宏同时恢复。
