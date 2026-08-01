# 技术实现

## 1. 文档范围

本文面向兼容层开发者和 Android 宿主集成维护者，说明仓库中各组件的职责、启动顺序、数据流、生命周期和构建验证标准。

目前对JipperResourcePack支持较好，理论上只用JALib的Mod都可以工作，但只测试过JipperResourcePack。

当前实现以 A Dance of Fire and Ice 3.1.2 Android IL2CPP、`arm64-v8a` 和 JipperResourcePack 1.4.8.2 为主要目标。文中的类型、方法和 ABI 名称均对应当前源码。

## 2. 组件结构

运行时由以下组件组成：

| 组件 | 职责 |
| --- | --- |
| `libAsyncInput.so` | 初始化既有异步输入能力，发布原始输入观察 ABI，并提供经过校验的进程内 IL2CPP 句柄。 |
| `libstarray_modmanager.so` | 承载 CoreCLR、JNI helper、Dobby Hook、原生规则 VM、输入转发、Dear ImGui 与 Unity 绘制提交。 |
| `StArray.ModManager.Android.dll` | 连接 JNI、原生导出、IL2CPP 代理和托管 ModManager。 |
| `StArray.ModManager.dll` | 扫描与加载 MOD，管理 PcCompat 会话、程序集重写、设置、资源、HUD 和卸载。 |
| `xphorror.PcModCompat` | 提供 JALib/Harmony shim、静态扫描器、规则编译器、托管桥、资源编译器与诊断模型。 |
| 生成代理程序集 | 提供 Android IL2CPP 中真实存在的 Unity、TMPro、RDTools 与游戏类型表面。 |

Android Java 入口为：

```text
Android/library/src/main/java/com/fizzd/connectedworlds/editorport/
  StArrayModManagerBootstrap.java
```

宿主 Activity 参考实现位于：

```text
examples/android/PcCompatUnityPlayerActivity.java
```

## 3. 启动顺序

宿主必须遵循以下顺序：

1. 加载 `libAsyncInput.so`。
2. 首次调用 `StArrayModManagerBootstrap`，由其静态初始化加载 `libstarray_modmanager.so`。
3. 调用 `StArrayModManagerBootstrap.startInBackground(activity)`。
4. Bootstrap 将应用文件目录传给原生层，建立输入设备和输入法观察器。
5. 后台线程准备 CoreCLR 环境并执行 `StArray.ModManager.Android.dll` 入口。
6. 托管入口初始化 ModManager、PcCompat 服务、原生桥和绘制需求。
7. MOD 扫描与重写准备可以在后台执行；所有 Unity 对象操作和最终会话安装必须回到 UnityMain。

`startInBackground` 默认不显示 ModManager 叠加层。需要立即显示叠加层时可调用 `StArrayModManagerBootstrap.launch(activity)`。CoreCLR 初始化只能尝试一次；失败后应重启进程，不应在同一进程重复启动另一套运行时。

## 4. 宿主 Activity 接入

### 4.1 必需职责

宿主 Activity 需要完成以下工作：

- 在 Bootstrap 初始化前加载 AsyncInput。
- 在 `onCreate` 中启动 ModManager 后台引导。
- 在 `dispatchTouchEvent` 中固定一次手势的输入 owner。
- ModManager 窗口拥有手势时，将整段手势持续交给 `forwardMotionEvent` 并消费。
- MOD 原始模态界面拥有手势时，将事件交给 Unity，不转发给游戏观察通道。
- 普通游戏输入先调用 `observeGameplayMotionEvent`，再交给 Unity。
- 在 `dispatchKeyEvent` 中调用 `observeGameplayKeyEvent`。
- MOD 原始模态界面显示时消费返回键，并在抬起时调用 `requestModalClose`。
- 将 `onActivityResult` 交给 Bootstrap，以完成 MOD ZIP 导入和诊断导出回调。
- Activity 暂停、销毁或手势结束时清除本地手势 owner。

### 4.2 手势 owner 不变量

owner 只能在以下时机选择：

- 收到 `ACTION_DOWN`；
- Activity 未持有有效 owner，但收到了后续事件。

owner 一旦选定，必须保持到 `ACTION_UP` 或 `ACTION_CANCEL`。不能在 MOVE 或 POINTER 事件中根据界面可见性重新选择，否则同一手势可能同时点击 ModManager、MOD 设置和游戏本体。

owner 分为三类：

| owner | 处理方式 |
| --- | --- |
| Unity 模态界面 | 调用 `super.dispatchTouchEvent`，由 Unity IMGUI/uGUI 处理。 |
| ModManager | 调用 `StArrayModManagerBootstrap.forwardMotionEvent` 并返回 `true`。 |
| Unity 游戏输入 | 调用 `observeGameplayMotionEvent` 后调用 `super.dispatchTouchEvent`。 |

完整实现见 `examples/android/PcCompatUnityPlayerActivity.java`。

## 5. IL2CPP 句柄桥

AsyncInput 在 `JNI_OnLoad` 阶段发布 `ADOFAIAsyncInputGetIl2CppHandleV1` 提供器。提供器信息通过应用文件目录中的指针文件传递，ModManager 不自行再次打开 `libil2cpp.so`。

`runtime_il2cpp_bridge.c` 接受句柄前执行以下检查：

1. provider magic 与 ABI 版本匹配。
2. provider 中的 PID 等于当前进程 PID。
3. 地址 cookie 与 PID、函数地址一致。
4. `dladdr` 证明函数位于 `libAsyncInput.so`。
5. 函数符号名为 `ADOFAIAsyncInputGetIl2CppHandleV1`。
6. 返回句柄中的 `il2cpp_domain_get` 与进程已映射 `libil2cpp.so` 的同名符号一致。

AsyncInput 尚未完成初始化时，提供器可以返回 `NULL`。ModManager 会等待提供器可用，不会把“尚未就绪”当作永久失败。

## 6. CoreCLR 宿主

`mono_droid_coreclr.c` 负责：

- 解析并初始化 `libcoreclr.so`；
- 建立 `host_runtime_contract`；
- 从 runtime 目录映射托管程序集；
- 创建托管入口 delegate；
- 保存 mmap 生命周期信息，并在释放时统一 `munmap`；
- 初始化需要 JNI 的运行时原生库。

Bootstrap 设置 `DOTNET_ROOT`、`TRUSTED_PLATFORM_ASSEMBLIES`、`APP_PATHS`、`NATIVE_DLL_SEARCH_DIRECTORIES` 与 ModManager 运行参数。`out/runtime/` 的目录层级属于运行合同，部署时不能把 shim、代理和能力包混放到同一目录。

## 7. MOD 发现与加载

ModManager 扫描 MOD 目录，读取原生 StArray、UnityModManager 与 JALib 常见清单。PcCompat 为符合条件的 PC MOD 创建准备会话：

1. 读取 MOD 清单和程序集集合。
2. 建立 owner 与 generation。
3. 静态扫描 Harmony/JALib Patch 描述。
4. 生成 Android 重写计划与规则 bundle。
5. 校验所需代理类型和成员是否存在。
6. 重写 MOD 程序集引用与支持的调用点。
7. 在隔离的 AssemblyLoadContext 中加载重写结果及 shim。
8. 在 UnityMain 完成原生规则、设置、资源与绘制会话安装。

后台阶段不得直接访问 Unity 对象。需要 UnityMain 的工作通过有界队列提交，最终加载状态只在配置的完成调度器中切换。

## 8. 静态扫描与程序集重写

### 8.1 静态扫描

扫描器在不执行 MOD PC 初始化代码的前提下读取：

- Harmony Patch 特性；
- JALib `JAPatch` 元数据；
- 受约束的动态 `AddPatch` 描述；
- 版本条件、目标类型、目标方法、参数类型和 Prefix/Postfix 语义。

扫描结果经过目标签名解析后，被降低为托管事件或原生固定操作。目标身份包括程序集、命名空间、类型、静态/实例、泛型元数、返回类型和参数类型，不能只按方法名匹配。

### 8.2 重写

重写器处理当前兼容链需要的调用，包括：

- Unity 类型引用改写为生成代理；
- JALib、Harmony、UnityModManager API 改写为 shim；
- AssetBundle 加载改写为 VirtualBundle；
- Unity 组件创建、查找、启停与销毁改写为 owner-scoped 托管桥；
- Unity IMGUI 便利重载改写为 Android 可用的基础调用；
- legacy input polling 改写为 PcCompat 输入快照；
- 不支持的线程终止改写为协作式停止；
- 支持的 ReversePatch 调用改写为明确的托管桥。

每次重写生成报告。缺失代理、签名不一致或无法证明安全的改写必须明确失败，不能静默绑定到同名成员。

## 9. JALib、Harmony 与 UnityModManager shim

shim 的目标是让 MOD 在加载期获得与原程序集一致的公开类型和成员，并把可执行语义转发到 PcCompat：

- JALib feature、setting、localization、patch 与常用工具 API；
- Harmony Patch 描述、排序、Prefix/Postfix、`__state`、`__result`、`__args` 与字段引用语义；
- UnityModManager setting、key binding、logger 与 ModEntry 表面；
- Newtonsoft.Json 等 shim 的运行依赖。

shim 与生成代理分目录部署：

```text
runtime/pc_compat_shims/
runtime/pc_compat_proxies/
```

shim 是宿主 API；生成代理代表 Android IL2CPP 类型。二者不能互相替代，也不能把游戏代理程序集放入 shim 目录。

## 10. Hook 与动态分派

原生规则由 `pccompat_hook_rules.cpp`、`native_rule_vm.cpp` 和 hook broker 管理。每个规则属于明确的 owner 与 generation。

分派槽位数量根据当前编译出的规则集合动态计算，不使用固定总量。加载时为规则集合分配所需槽位；卸载时先逻辑退役 owner/generation，再释放托管会话和 Unity 表现对象。旧 trampoline 即使仍被进程代码引用，也只能看到已退役状态，不能再次调用卸载后的托管对象。

Dobby 静态库必须同时导出：

```text
DobbyHook
DobbyCodePatch
DobbyGetVersion
```

短函数或已知相邻函数冲突不能强行安装 inline Hook。此类目标由组合规则覆盖，并记录为已知跳过状态。

## 11. 绘制链路

### 11.1 ModManager

ModManager 使用 Dear ImGui 绘制。原生 EGL/输入桥只在存在实际显示需求时工作。窗口可见时，Activity 将该手势完整路由到 `forwardMotionEvent`。

### 11.2 MOD 原始设置

JipperResourcePack 原始设置通过 Unity IMGUI 绘制。原生层在有效的 `GUIUtility.BeginGUI` 上下文建立后派发托管 OnGUI，确保 `Event.current`、GUIClip 和 GUILayout 已初始化。

每个 Unity 事件只派发一次兼容 OnGUI。Layout 与 Repaint 必须成对保持一致，不能在同一事件中重复绘制，也不能在 `ProcessEvent` 建立 GUI 上下文前调用托管 OnGUI。

### 11.3 HUD

HUD 优先使用 MOD 资源和已编译表现图。原生表现 sink 在 UnityMain 上分批执行对象创建与更新，工作量受每次机会的上限约束。持续更新数据由快照和 deadline scheduler 驱动，不在 UnityMain 阻塞等待。

## 12. 设置、本地化与字体

设置控制器从 MOD 的 JALib setting/feature 结构生成移动端布局，并保持设置值、回调和 MOD 原逻辑关联。布局尺寸根据实际屏幕、DPI 和触摸高度计算，不依赖固定控件总数。

本地化按 MOD 当前语言资源解析。显示文本优先使用已解析的本地化值，不能把内部 key 当作最终按钮文本。

字体加载顺序为：

1. owner 对应 VirtualBundle 中的 `UnityEngine.Font`；
2. MOD 自带 TMP 静态 atlas 投影出的 legacy Font；
3. 宿主能力包字体兜底。

TMP atlas 投影只生成设置界面使用的兼容表示，不修改 MOD 原 TMP Font、atlas、材质或 HUD 资源。投影结果按 owner 和资源身份缓存。

输入法请求经过状态合并。只有文本控件稳定请求输入且键盘状态发生变化时，才向 Android 请求显示或隐藏，避免 ModManager 与 MOD 设置反复拉起键盘。

## 13. 资源替换

资源链以 MOD 自带 Bundle 为数据源：

1. VirtualBundle 注册 owner-scoped Bundle。
2. 资源编译器索引纹理、Sprite、材质、字体和预制体关系。
3. 运行时按 recipe 解析资源身份。
4. UnityMain 创建或投影 Android 可用对象。
5. 原生资源桥发布 Sprite、颜色和状态更新。

Jipper 的字体、纹理、Sprite 和本地化数据必须从 MOD 自带 Bundle 提取，不得把 Jipper PNG 或其他资源复制进 runtime。

`xphorror.PcModCompat/assets/pc_compat_capabilities/` 只包含宿主提供的 Android 能力资源，例如兜底 Shader、字体和预制体外壳，不包含 JipperResourcePack 资源。

资源发布按 owner/generation 去重。卸载时先撤销发布，再退役 Unity 对象和 GCHandle，避免旧图层覆盖重新加载后的 HUD。

## 14. 输入数据流

PcCompat 支持两个带版本的输入生产者：

- `OfficialActivity`：宿主直接调用 Bootstrap 的 `observeGameplayMotionEvent` 与 `observeGameplayKeyEvent`。
- `AsyncInput`：AsyncInput 在其输入处理前通过 observer ABI 发布原始事件。

原生 realtime event core 记录当前生产者和 producer epoch。生产者变化会建立新的输入阶段，防止同一物理事件从两个来源重复计数。

模态输入优先级为：

1. MOD 原始设置模态界面；
2. ModManager Dear ImGui；
3. 游戏输入。

模态界面关闭或 MOD 卸载时，必须同步清除 managed 与 native capture 状态、活动触摸矩形、键盘焦点和手势 owner。

## 15. 遥测与 HUD 更新

兼容层从已验证的游戏回调和轮询桥生成不可变快照，包含 BPM、目标 BPM、CBPM、KPS、连击、准确率、AUTO 与 KeyViewer 状态。UnityMain 只发布最新快照，不在绘制回调中执行阻塞采样。

会话开始、重置、退出播放态和返回菜单时都会更新 generation。HUD 可见性与遥测 generation 绑定；旧会话的快照不能更新新会话对象。

## 16. 卸载与重新加载

卸载顺序必须保持：

1. 标记 MOD 停止接收新工作。
2. 退役 owner/generation 对应的原生规则和分派槽。
3. 停止托管 actor、轮询器与事件消费者。
4. 关闭设置界面、输入捕获和输入法请求。
5. 在 UnityMain 隐藏并销毁 owner 创建的 HUD、Canvas 和组件。
6. 撤销 VirtualBundle 与资源发布。
7. 释放 AssemblyLoadContext 和托管会话。

异步清理通过 UnityMain 工作队列完成。非 UnityMain 线程不能直接销毁 Unity 对象。重新加载会获得新 generation，不复用旧会话状态。

## 17. 构建输入与输出

`build.ps1` 需要显式输入：

| 参数 | 内容 |
| --- | --- |
| `NdkRoot` | Android NDK `25.2.9519653` 根目录。 |
| `DobbyLibrary` | 具备完整所需导出的 arm64 `libdobby.a`。 |
| `Il2CppMscorlibPath` | 完整编译期 `Il2Cppmscorlib.dll`。 |
| `ProxyAssembliesDir` | 预先生成并审计通过的 Android 代理程序集目录。 |

构建脚本执行：

1. staging 必需代理程序集；
2. 构建 Android managed runtime；
3. 可选执行 managed 全量测试；
4. 复制 .NET Android CoreCLR runtime pack；
5. 构建并分目录打包 shim；
6. 打包代理、能力包和资源编译依赖；
7. 使用 NDK 25.2 构建 `libstarray_modmanager.so`；
8. 构建 AsyncInput 子模块；
9. strip Release SO；
10. 使用 `llvm-readelf` 审计关键导出与全部 JNI helper 导出。

输出结构：

```text
out/
  arm64-v8a/
    libAsyncInput.so
    libstarray_modmanager.so
  runtime/
    StArray.ModManager.Android.dll
    StArray.ModManager.dll
    Il2CppInterop.Runtime.dll
    Il2Cppmscorlib.dll
    libcoreclr.so
    libclrjit.so
    pc_compat_shims/
    pc_compat_proxies/
    pc_compat_capabilities/
```

宿主需要将 `out/runtime/` 中的内容按原层级打包到 APK 的 `assets/runtime/`。Bootstrap 启动时会把该资产目录释放到应用内部的 `files/ModManager/runtime/`，CoreCLR 从释放后的目录加载程序集和原生运行库。两个 SO 应放入目标 APK 的 `lib/arm64-v8a/`。本仓库不负责 APK 重打包。

## 18. 验证标准

提交前应满足以下标准：

- managed 全量测试零失败；
- 缺少未分发的 Jipper 二进制样本时，相关样本测试明确跳过而不是误报失败；
- 原生规则 VM、recipe、realtime event 与生命周期测试通过；
- NDK 25.2 arm64 编译和链接成功；
- `libstarray_modmanager.so` 包含 Bootstrap、JNI helper、PcCompat 和 IL2CPP 句柄桥所需导出；
- `libAsyncInput.so` 包含 raw observer 和 IL2CPP handle provider 导出；
- runtime 中包含 CoreCLR、托管入口、shim、代理和资源编译依赖；
- Git 不跟踪 SO、DLL、静态库、APK、游戏 dump、metadata、MOD 样本或运行日志；
- `git diff --check` 无空白错误。
