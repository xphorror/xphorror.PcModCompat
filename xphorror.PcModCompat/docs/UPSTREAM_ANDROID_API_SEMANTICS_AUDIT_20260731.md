# Android 上游 API 语义审计与同步记录

## 基线

- 上游仓库：`E:\TEMP_SHARE\git\StArray.ModManager`
- 既有同步基线：`77302d32966499cd35dec0d65aef321101740603`
- 当前上游目标：`7f84f6a1ea0f578a5381f3dd501802f8a45fc1c0`（`1.0.6`）
- 本地目标：Android arm64-v8a，ADOFAI 3.1.2 r143
- 审计范围：Android 公共 API、JNI ABI、IL2CPP 运行时、Native hook 生成入口
- 不同步范围：Mono、Windows、桌面 renderer、上游直接接管输入的实现

`1.0.6` 增量 API、输入链路和 MOD generation 隔离已经完成，实施与验证结果单独记录在 [`UPSTREAM_ANDROID_SYNC_1_0_6.md`](UPSTREAM_ANDROID_SYNC_1_0_6.md)。本文后续历史结果仍对应 `77302d3` 基线，当前状态以增量文档为准。

## 审计结论

初始源码审计中，核心 `StArray.ModManager.dll` 有 141 项上游到本地断点，其中 126 项属于 Android 不使用的 Mono API，剩余 15 项为 Android 相关或工具 API；`StArray.ModManager.Android.dll` 有 13 项断点。反向比较显示，本地已有大量 PcCompat、HookBroker、授权、生命周期和输入所有权扩展，不能用上游整文件覆盖。

同步后使用 .NET SDK `Microsoft.DotNet.ApiCompat` 对两边 Release DLL 做正式单向二进制检查。结果为：Core 共 126 条断点且全部位于 `StArray.ModManager.Mono.*`，非 Mono 断点 `0`；Android DLL 断点 `0`。该结果证明上游预编译 Android MOD 所需的公开非 Mono 二进制表面闭合，不表示所有方法体语义与上游相同。

### 必须同步的 ABI 缺口

1. 上游公开 `StArray.ModManager.Android.Native.JniNative`，本地只有功能等价但类型名不同的 `JniHelperNative`。
2. 上游公开 `AndroidInput`，本地缺少该类型。
3. 上游 `NativeImportAttribute` 和 `NativeImportGenerator` 缺失。
4. Hook 生成器只识别 `NativeHookAttribute` 和旧 `Il2CppHookAttribute`，不识别 `UnmanagedHookAttribute`。
5. IL2CPP concrete API 有签名差异：`GetAssemblies`、`OpenAssembly`、`Il2CppClass.New`、`il2cpp_domain_assembly_open`。
6. `JavaClass` 缺少 `CallStaticObjectMethod3(..., nint)` 和 `CallStaticVoidMethod1(..., nint)`。
7. 缺少核心 `StArray.ModManager.Native.DL`、`Runtime.NativeFuncResolver`/`MatchValidator`。
8. `Dobby._SymbolResolver` 和若干生成 hook 的公开辅助入口缺失。
9. Android DLL 缺少 `HotUpdater` 和 `LogcatCapture`。
10. Core 缺少 `BehaviourManager`、`GameBehaviour` 及其逐帧生命周期驱动。
11. Core 缺少 `DL.Addr`、`IImGuiRenderer.InitImGui()`、两参数 `ModManagerUI` 构造函数，且 `NativeFuncResolver` 被错误声明为 `sealed`。
12. Core 缺少 `AssemblyEmitter`、`StubAssemblyGenerator`、`TestStubs.Transform`，source generator 缺少 `UnmanagedStubGenerator`。

### 本地语义必须保留

- 物理 hook 统一由 HookBroker 安装；不恢复上游直连 Dobby 的生命周期。
- Android hook 是进程永久链，`HookHelper.Unhook` 不进行物理卸载。
- 不主动调用 Android `il2cpp_thread_detach`，避免 Boehm pthread-key 清理竞态。
- 触摸由 Activity/modal/IME ownership 链路管理；不恢复上游 Unity MotionEvent hook。
- 保留 PcCompat、授权门禁、异步加载、原 MOD Unity 设置页和 schema fallback。
- Inspector 继续使用本地兼容规则：未标注 public 自动显示，`ShowIf=false` 真正隐藏。

## 同步决策

本轮按“公开 ABI facade + 现有实现转发”同步：

- `JniNative` 转发到本地 `JniHelperNative`，不复制一套 DllImport，也不改变现有异常清理语义。
- `AndroidInput` 只补齐上游声明和 NDK 常量，不安装新的输入 hook。
- `NativeImportGenerator` 只生成现有 `HookHelper` 解析路径的调用，不绕过 HookBroker。
- IL2CPP 对外签名以 `IRuntime*` 为准；本地 concrete typed helper 另提供，不破坏 PcCompat 既有调用。
- `BehaviourManager` 和 `GameBehaviour` 属于 Android 运行时 API，按上游 Awake/Enable/Start/Update/LateUpdate/GUI/Disable/Stop 顺序接入 `ModManagerUI.Render()`，并在存在活跃行为时维持隐藏帧需求。
- `HotUpdater` 和 `LogcatCapture` 补齐公开类型和成员，但不接入本地自动启动，避免改变既有更新、授权和日志策略。
- `AssemblyEmitter` 和 `StubAssemblyGenerator` 补齐可加载、可调用的公开 ABI；当前 Android-only 后端不导入上游 Mono 和持久化动态程序集实现，调用时输出 warning，并分别返回 `null` 或不生成输出。

## 分阶段状态

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| A | 审计上游提交、Android 分层和本地差异 | 已完成 |
| B | 文档化缺口与不可同步的本地语义 | 已完成 |
| C | JNI facade、AndroidInput、核心 DL/resolver | 已完成 |
| D | IL2CPP concrete 签名、JavaClass overload、Dobby alias | 已完成 |
| E | NativeImport/UnmanagedHook 生成入口 | 已完成 |
| F | managed、ApiCompat、Android Release、arm64 导出复验 | 部分完成：managed、ApiCompat、Android Release 通过；arm64 `.dynsym` 与实机待复验 |

## 本轮实施结果

- 新增 `JniNative` facade 和 `AndroidInput` ABI 声明。JNI 调用继续转发到 `JniHelperNative`；触摸、modal 和 IME 所有权没有切换到上游 MotionEvent hook。
- 新增 `StArray.ModManager.Native.DL`、`NativeFuncResolver`、`MatchValidator`、`NativeImportAttribute` 和对应 source generator。
- `UnmanagedHookAttribute`、生成 hook 的公开辅助入口和 Dobby symbol alias 已补齐；生成代码仍通过本地 `HookHelper`/HookBroker 安装。
- IL2CPP concrete API 已对齐上游可绑定签名，同时保留 `GetIl2CppAssemblies`、`OpenIl2CppAssembly`、`NewObject` 等本地强类型 helper，避免破坏 PcCompat 既有调用。
- `JavaClass` 已补齐上游静态对象/void 参数 overload；Android 原有 JNI 异常、Activity、Surface、输入和 data channel 扩展均保留。
- `BehaviourManager`/`GameBehaviour` 已按上游生命周期执行；`ModManagerUI` 恢复两参数构造 ABI，`IImGuiRenderer` 恢复默认 `InitImGui()`，`DL.Addr` 和可继承 `NativeFuncResolver` 已补齐。
- Android 已补 `HotUpdater`、`LogcatCapture`；Core 已补 `AssemblyEmitter`、`StubAssemblyGenerator`、`TestStubs.Transform`，并移植 `UnmanagedStubGenerator`。

## 本机验证

```text
StArray.ModManager.SourceGenerator build: passed
StArray.ModManager.Android Release build: passed
StArray.ModManager.Tests build: passed
Android/API 定向测试: 46/46 passed
managed 全量测试: 738/738 passed
Core ApiCompat: 126 Mono-only, 0 non-Mono
Android ApiCompat: 0
```

定向测试覆盖 JNI facade、AndroidInput、DL/resolver、NativeImport/UnmanagedHook/UnmanagedStub 生成路径、IL2CPP concrete 签名、JavaClass overload、Dobby alias、非 Mono 公开 API 闭包及 Behaviour 生命周期。此结果证明 managed API/ABI 和本地所有权合同成立，不替代最终 arm64 SO 导出审计和设备端 JNI/输入语义验证。

## 验收口径

- 上游 Android 公共类型/成员的可加载 ABI 不再出现 `TypeLoadException`。
- JNI 公开 manifest 仍为 85/85，已有本地扩展不减少。
- NativeImport/UnmanagedHook 生成代码必须走本地 resolver/HookBroker。
- `GetAssemblies`、`OpenAssembly` 等 API 的上游返回类型可被预编译 Android MOD 直接绑定。
- managed 定向测试、Android Release 构建、最终 arm64 `.dynsym` 审计全部通过。
