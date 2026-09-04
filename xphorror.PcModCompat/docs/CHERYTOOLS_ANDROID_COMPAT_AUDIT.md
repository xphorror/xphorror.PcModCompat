# CheryTools Android 兼容缺口审计

日期：2026-07-30

状态：审计完成，尚未开始 CheryTools 专项实现

## 1. 目的

本文记录 CheryTools 对照当前 PcCompat Android 兼容层的静态审计结果，作为后续代理闭包、平台替代、Harmony、输入、绘制和生命周期实现的基线。

本文不得用于宣称 CheryTools 已可加载或已可运行。当前正式 DLL 的 managed rewrite 仍然失败，最终产物未生成。

## 2. 审计对象与基线

### 2.1 主仓库

- 仓库：`StArray.ModManager`
- HEAD：`ca088a8`（运行时包名与加载边界更新）
- 上一 PcCompat 提交：`8c10d5e fix(pccompat): harden runtime reload and HUD telemetry`
- `ca088a8` 主要修改包名、能力 gate 和运行时加载保护，没有扩展 CheryTools 所需的代理、ImGui、输入或 Harmony surface。

### 2.2 CheryTools Git 源码

- 路径：`StArray.ModManager/CheryTools`
- HEAD：`dec715d6cfbd25da7ebc3078d629e5e035636b91`
- HEAD tag：`v26.1.1`
- `Info.json` 源码版本：`26.2 Alpha`
- 工作区：干净

Git tag、源码 `Info.json` 和正式 Release 包的版本文本并不一致。后续审计必须分别标明“源码 HEAD”与“正式 Release DLL”，不得混用。

### 2.3 正式 Release 包

- 审计目录：`out/cherytools_audit/v26.1.1`
- ZIP：`CheryTools_Beta_26.1.1.zip`
- ZIP SHA-256：`178C56632E6521AF0C4B46ED1EDD7F3662CCDA7FA9A0162ADF109145AF7694A1`
- `CheryTools.dll` SHA-256：`0B0DB11EF551DD236D6F8652D43F4851FA8B328BB0BA820DBDBCE6508F633510`
- 正式包版本文本：`Beta 26.1.1`

正式 DLL 不包含 Git 源码中的 `OfficialLevelEditorPatches`、`EditorLevelLibraryPanel` 等类型。因此：

1. 当前加载兼容以正式 DLL 为运行时事实；
2. Git 源码新增功能作为下一版前向兼容基线；
3. 源码存在但正式 DLL 不存在的功能，不得计入正式包已支持功能。

## 3. 量化结果

### 3.1 当前生成代理审计

`ProxySurfaceScanner`：

- 扫描托管程序集：5
- 扫描引用：41,283
- 接受 surface：503

`ModAssemblyRewriter --audit-only`：

- 方法调用总数：2,457
- 当前可匹配方法调用：1,029
- 当前未闭合方法调用：1,428
- 字段指令：564
- 字段改写：104
- 顶层阻断 issue：207
- `outputWritten=false`

审计报告：

- `out/cherytools_audit/v26.1.1/chery_surface_report.json`
- `out/cherytools_audit/v26.1.1/chery_rewrite_report.json`

207 个顶层 issue 的主要归属：

| 归属 | 数量 |
| --- | ---: |
| `SdfTextRenderer` | 81 |
| `GameUIManager` | 46 |
| 缺失 metadata type | 35 |
| `OverlayerManager` | 18 |
| `InputInterceptor` | 14 |
| 缺失 metadata assembly | 4 |
| `Main` | 4 |
| `VisualTweaks` | 2 |
| 其他绘制/输入类 | 3 |

1,428 个未闭合方法调用的目标程序集分布：

| 程序集 | 数量 |
| --- | ---: |
| `UnityEngine.CoreModule` | 1,121 |
| `UnityEngine.UI` | 126 |
| `Assembly-CSharp` | 64 |
| `Unity.TextMeshPro` | 37 |
| `UnityEngine.VideoModule` | 30 |
| `UnityEngine.InputLegacyModule` | 24 |
| `UnityEngine.UIModule` | 21 |
| `UnityEngine.IMGUIModule` | 4 |
| `UnityEngine.TextRenderingModule` | 1 |

这些数字描述“当前 Jipper 导向生成代理”与 CheryTools 的差异，不表示 1,428 个调用都需要手写桥。多数调用可在 scanner 和闭包生成修复后由 metadata 自动生成，真正的平台缺失必须单独判定。

### 3.2 理论闭包

独立计算 Jipper + CheryTools 理论闭包时首先被以下条目阻断：

```text
RN|UnityEngine.InputLegacyModule|UnityEngine.Input|GetKeyDown
```

PC metadata 中存在多个 `GetKeyDown` 重载。CheryTools 源码实际通过 `AccessTools.Method(..., Type[])` 指定签名，但 scanner 当前只传播 name-only reflection 条目。

临时排除此歧义后：

- 理论闭包：230 exact types / 15 assemblies
- 当前闭包：181 types / 14 assemblies
- 至少新增：49 types / 1 assembly
- Android metadata 真正缺失类型：`SkyHook.Unity!SkyHook.KeyLabel`
- `scnEditor.playMode` 反射字段不存在，但源码有 fallback，不是加载阻断

## 4. 当前可复用基础

以下能力已经存在，但仍需 CheryTools 专项接线和验证：

1. UMM `Load/OnToggle/OnGUI/OnSaveGUI` 基础生命周期和设置序列化；
2. 可收集 managed ALC、owner/session generation、UnityMain managed component host 和卸载清理；
3. Harmony Prefix/Postfix、`ref __result`、`__instance`、自定义 `__state` 和逻辑 `Patch/Unpatch` 基础；
4. `ImGui.NET 1.91.6.1` 与宿主版本一致，宿主 `libstarray_modmanager.so` 已导出 cimgui ABI；
5. Android modal input gate、IME bridge、共享输入 journal、Touch T1..TN/点位模式和外接键盘投影；
6. Jipper KeyViewer Adapter、兼容代绘和批量 Mesh 的架构可复用，但不能直接把 CheryTools 判定为 Jipper；
7. JSON、XML、ZIP 和 MOD 私有目录中的普通托管文件 IO；
8. 部分 Unity、uGUI、TMP、资源和 managed callback 代理能力。

## 5. P0：加载阻断

### P0.1 Windows `LoadLibrary`

`Main.Load()` 无条件调用：

```csharp
[DllImport("kernel32.dll")]
private static extern IntPtr LoadLibrary(string lpFileName);
```

随后尝试加载正式包中的 Windows `cimgui.dll`。Android 没有 `kernel32.dll`，正式包的 PE DLL 也不能加载。

要求：

- rewrite `EnsureNativeDependenciesLoaded` 为 Android no-op，或将该 P/Invoke 精确替换为平台桥；
- 禁止尝试 dlopen 正式包的 Windows `cimgui.dll`；
- CheryTools 的 ImGui.NET 调用统一解析到宿主 `libstarray_modmanager.so`。

### P0.2 ImGui.NET ALC resolver

宿主当前只对默认 ALC 中的 `ImGui.NET`、`Managed` 和 `Il2CppFunctions` 安装 `DllImportResolver`。PcCompat 的 MOD ALC没有把 `ImGui.NET` 列为 shared runtime assembly，因此会从 MOD 文件夹加载第二份程序集，该程序集没有 resolver。

要求二选一：

1. 将版本完全一致的 `ImGui.NET` 作为 shared runtime assembly；或
2. 在 MOD ALC 加载 ImGui.NET 后，为该具体 Assembly 安装 owner-scoped resolver。

不得跨不同 ImGui.NET ABI 版本静默共享。

### P0.3 scanner 反射签名传播

scanner 必须识别并传播：

- `AccessTools.Method(Type, string, Type[])`
- 四参数重载中的 argument types
- `Type[]` 局部变量和静态数组初始化
- `null` 参数数组与精确参数数组的区别

修复后重新生成 CheryTools + Jipper 联合闭包，不允许继续人工过滤 `RN` 条目作为生产方案。

### P0.4 外部 ABI

- `SkyHook.Unity!SkyHook.KeyLabel`：为 `AsyncKeyCode.label` 字段布局提供 ABI shim，或者重写异步键值投影，消除该字段类型依赖；
- `Facepunch.Steamworks.Win64`：Android 不加载 PC Steamworks。云同步入口必须显示为平台不可用并安全返回，或提供不会触发原生 Steam 加载的最小托管空实现；
- 不得因为不可用的可选云同步功能导致整个 MOD `TypeLoadException`。

### P0.5 可写路径

CheryTools 的 `GameRoot` 使用 `Application.dataPath/..`，`AssetsRoot` 为其下的 `CheryToolsAssets`。Android 上该位置不保证可写。

要求：

- 将 CheryTools 资源和导入产物根目录重定向到 MOD 私有可写目录；
- 保留 `ModEntry.Path` 下只读随包资源的读取能力；
- 导入、解压和导出继续执行 canonical-path/root containment 检查；
- 路径映射必须随 owner/session 卸载，不得污染其他 MOD。

## 6. P1：核心功能缺口

### P1.1 CheryTools 自建 ImGui 菜单

CheryTools 菜单不是 UMM/JALib 设置页，而是独立 Canvas、独立 ImGui context 和独立输入循环。

需要专用 menu adapter：

- Android touch 到 ImGui mouse position/button；
- `CheryToolsMenu.IsMenuOpen`、`FreeMakeEditor.IsOpen`、`OvTokenNodeEditor.IsOpen` 聚合为 modal ownership；
- 阻止 EventSystem、官方 gameplay input 和其他 overlay 接收同一触摸；
- IME 所有权、文本提交、Backspace/Delete/方向键和剪贴板；
- Android Back 优先关闭 CheryTools 子窗口/菜单；
- MOD 卸载、异常和 ALC retire 时无条件释放 modal capture；
- ModManager 设置页提供明确的“打开 CheryTools”入口，不能只依赖 F10。

### P1.2 KeyViewer Adapter

CheryTools KV 具有多配置、动态节点、每配置计数、KPS、按键动画、rain、图片/视频节点和自定义字体。Jipper 的固定角色绑定不能直接复用。

要求：

- 输入来自共享 journal，禁止每个 MOD 启动忙轮询线程；
- Touch 使用 T1..TN 或点位模式；外接键盘恢复实际 `KeyCode` 标签；
- 动态计算 lane/node 总数，不设固定总槽位上限；
- 保留 MOD 自己的计数、KPS、动画和 rain 规则；
- 绑定捕获期间抑制打开按钮的触摸伪键和 stale held state；
- 多配置之间去重同一物理 key 的 rising edge，按 MOD 原规则更新总计数；
- 卸载、重载、场景退出和后台恢复后清空 held/rain/render generation。

### P1.3 输入过滤

CheryTools 动态 Patch：

- `RDInputType_Keyboard.MainIgnoreActive`
- `RDInputType_AsyncKeyboard.Main`

并修改 `RDInputType.MainStateCount.keys`。

需要证明：

- `AnyKeyCode`、`AsyncKeyCode`、`ButtonState` 和 `MainStateCount` 的真实 Android ABI；
- `List<AnyKeyCode>.RemoveAll` 在 generated proxy 上不会复制或丢失原列表写入；
- 官方和异步输入只过滤一次；
- 未知键 fail-open，不得屏蔽游戏全部输入；
- 关闭设置、禁用功能和卸载 MOD 后逻辑 patch 完整撤销；
- 动态 `Harmony.Patch/Unpatch` 必须被 static scanner 识别并进入诊断。

### P1.4 Harmony Transpiler

正式 DLL 含 `scnEditor.Update` Transpiler。当前 Harmony shim 仅提供 ABI 和 `CodeInstruction` 操作，不对 IL2CPP native 方法执行 IL rewrite。

影响：

- `DisableAutoplaySpacePause` 不会等价生效；
- `DisablePlayModeScrollZoom` 不会等价生效。

生产方案应把两个已知变换分别 lower 为明确的输入 gate/native hook 规则。不得宣称通用 Transpiler 已支持。

### P1.5 uGUI、TMP 与批量绘制

主要缺失类型/成员包括：

- `VertexHelper`、`CanvasGroup`、`Button`、`CanvasRenderer`；
- `RenderTexture`、`Graphics`、`Color32`、`MeshTopology`；
- `TMP_TextInfo`、`TMP_CharacterInfo`、`TMP_MeshInfo`、`TMP_LinkInfo`；
- TMP mesh/vertex/color 数组和 `UpdateVertexData`；
- CheryTools ImGui Mesh 上传所需重载。

性能边界：不得将 CheryTools 每个 `AddVert/AddTriangle` 调用都变成一次 generated-proxy/IL2CPP 调用。需要批量 Mesh 上传或等价的共享 renderer bridge，并缓存 Mesh、数组、Material 和 CanvasRenderer。

### P1.6 视频

需要生成并验证完整 `UnityEngine.VideoModule` surface：

- `VideoPlayer`；
- `VideoRenderMode`、`VideoAspectRatio`、`VideoAudioOutputMode`；
- `RenderTexture` 创建、active、temporary、release；
- `Prepare/Play/Pause/Stop/isPrepared/isPlaying/frame/time`。

还需处理 Activity pause/resume、场景切换、MOD disable/unload 和 stalled decoder。视频不可用时只降级对应节点，不得中断 KV/Overlayer。

### P1.7 Android 文件选择

`ModernFileDialog` 使用 Windows COM 和 `shell32!SHCreateItemFromParsingName`，Android 上必然不可用。

需要 Activity picker：

- 打开：`ACTION_OPEN_DOCUMENT`；
- 保存：`ACTION_CREATE_DOCUMENT`；
- 异步结果回到 UnityMain/ModActor；
- owner/session generation 校验；
- content URI 流复制到 MOD 私有暂存路径；
- MOD 卸载后忽略迟到结果；
- 支持 `.cyt`、`.ctkv`、`.ctov`、字体、图片和 mp4 的 MIME/扩展名过滤。

### P1.8 Game UI 与官方对象字段

当前缺口包括：

- `DetailedResults`、`scrHitErrorMeter`、`EditorDifficultySelector`、`scrEnableIfBeta`；
- `scrController`、`scnEditor`、`scrUIController`、`GCS` 多个字段/getter；
- 结果页、自动播放、不失败、难度、命中误差条和 build watermark 生命周期。

这些引用必须按 r143 Android metadata 精确生成。所有字段写入继续走 GC-aware API，不允许直接写 IL2CPP reference field。

### P1.9 VisualTweaks 与资源所有权

CheryTools 直接修改 `PlanetRenderer` 颜色、ring、tail 和 sprite。可复用 Jipper 资源替换桥的底层能力，但必须使用独立 owner/generation：

- 多 MOD 同时接管时有确定优先级；
- 禁用 CheryTools 只恢复 CheryTools 自己覆盖的状态；
- 卸载后不能销毁仍被 Unity 或其他 MOD 使用的纹理；
- AUTO、编辑器、三球模式和关卡重载均需验证；
- 不得把外部 PNG 固化到 runtime，随 MOD 文件读取或从其资源包投影。

### P1.10 生命周期与热重载

CheryTools 创建多个 `DontDestroyOnLoad` root、Canvas、Mesh、Material、Texture、TMP 对象和视频对象。

要求：

- 全部对象纳入 owner/session generation；
- unload 在 UnityMain 执行 Unity Object 销毁；
- worker 只发布 retire 请求，不直接调用 Unity API；
- 先停止输入/绘制发布，再销毁对象，最后 unload ALC；
- reload 前必须确认旧 render generation 已不可见；
- 前后台、场景切换、编辑器播放结束和异常 disable 均走同一幂等清理路径。

## 7. P2：降级功能和前向基线

### P2.1 Steam 云同步

Android 默认显示“平台不可用”，本地设置保存不受影响。除非未来有独立云后端，不实现 Steam Remote Storage。

### P2.2 XPerfect

`XPerfectBridge` 使用反射，可保留。但需要跨 collectible ALC 的程序集发现、MOD unload 后缓存失效和调用异常隔离。

### P2.3 Git 源码新增但正式 DLL 未包含的功能

前向基线包括：

- 编辑器关卡库；
- `scnEditor.Start` Postfix；
- `InspectorPanel.ShowPanel` Prefix；
- 官方关卡暂停菜单编辑器入口；
- `PauseMenu.RefreshLayout` Postfix；
- 大量关卡图标资源和编辑器 uGUI 拖拽逻辑。

这些功能等正式 Release DLL 实际包含后再进入运行时闭包，当前只做 scanner 前向兼容，不阻塞正式 `Beta 26.1.1` 第一阶段。

## 8. 审计工具回归

### 8.1 PcCompatProbe 编译失败

当前 `tools/PcCompatProbe/PcCompatProbe.csproj` 引用了 `PcCompatCallbackTranslation.cs`，但漏掉定义 `PcCompatResolvedTargetSignature` 的 `PcCompatTargetSignature.cs`，导致 probe 在当前 HEAD 无法编译。

要求：将该源文件及其直接依赖加入 probe project，并增加 probe 自身构建测试。

### 8.2 原生 DLL 被送入 managed translator

旧 probe 会扫描 MOD 文件夹中的所有 `*.dll`，把 Windows 原生 `cimgui.dll` 记录为 `AssemblyHasNoMetadata`，随后 callback translator 仍对其调用 `GetMetadataReader()` 并崩溃。

要求：

- catalog 阶段将无 CLR metadata 文件分类为 native dependency；
- static scanner 可以记录非托管文件，但 translator 不得再次打开为 managed image；
- `--static-scan-only` 必须在输出 static report 后直接返回，不运行 callback translation。

## 9. 实施顺序

### 阶段 A：可加载

1. 修 probe 和反射签名传播；
2. 生成联合代理闭包；
3. 处理 `kernel32/cimgui`；
4. 共享或 owner-install ImGui.NET resolver；
5. 提供 SkyHook ABI 和 Steam 安全降级；
6. rewrite audit 达到 `outputWritten=true`。

### 阶段 B：菜单可操作

1. 创建 CheryTools menu adapter；
2. 接入 touch、modal capture、IME、Back 和剪贴板；
3. ModManager 增加打开入口；
4. 验证菜单不向游戏透传输入；
5. 验证 unload/reload 不残留 Canvas 或 modal ownership。

### 阶段 C：KV/Overlayer

1. 共享输入 journal consumer；
2. 动态节点、计数、KPS、rain 和绑定捕获；
3. 批量 uGUI/TMP renderer；
4. 文件/字体/图片导入；
5. 视频节点作为独立可降级能力。

### 阶段 D：工具与编辑器

1. InputInterceptor 动态 patch；
2. Transpiler 两项规则的显式 lowering；
3. Game UI 和 VisualTweaks；
4. Android file picker；
5. 正式 DLL 更新后纳入编辑器关卡库和官方关卡编辑入口。

## 10. 验收条件

第一阶段完成至少满足：

1. 正式 DLL rewrite `outputWritten=true`，无未解释阻断 issue；
2. clean install 后可 Load、Enable、Disable、Unload、Reload；
3. 菜单 touch、IME、Back 正常，游戏不收到透传输入；
4. KV 在官方输入和异步输入下均正确计数、held、rain 和标签；
5. 外接键盘显示实际绑定，触摸显示选定 T1..TN/点位模式；
6. 场景进入、退出、编辑器播放、后台恢复后无残留 HUD/黑块/modal capture；
7. 禁用 Steam、视频或文件选择不会拖垮其他功能；
8. 关卡内 CPU、GC、generated-proxy 调用量和 Mesh 上传量有诊断计数；
9. managed 全量、代理生成、native host 和 arm64 单包构建全部通过；
10. 实机诊断导出包含 CheryTools 菜单、输入、KV、renderer、资源、Harmony 和 lifecycle 状态。

## 11. 当前结论

CheryTools 与当前兼容层之间不是单个 API 缺口，而是完整的第二类 MOD 运行形态：自建 ImGui 宿主、动态 KeyViewer、深层 TMP/uGUI 绘制、平台文件选择、视频、输入过滤和编辑器扩展。

已有 Jipper 工作提供了输入 journal、生命周期、资源所有权和批量绘制基础，但 CheryTools 必须拥有独立 adapter 和 owner-scoped runtime 状态。在 P0 闭合前不得实机尝试启用正式 DLL；在菜单 modal ownership 和批量 renderer 闭合前不得宣称核心功能可用。
