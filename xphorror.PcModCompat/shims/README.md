# shims

这里放托管 API 兼容层。

计划拆分：

- `UnityModManager`：提供 `UnityModManagerNet` 命名空间和 `ModEntry` / `ModInfo` / `ModLogger` 等常用 API。
- `JALib`：提供 `JAMod`、`Feature`、`JASetting`、`JAPatcher`、`JAPatchAttribute` 等 API。
- `Harmony`：提供最小 `HarmonyLib` API。默认只记录 patch 意图，不做通用 IL2CPP patch。
- `UnityEngine.*` / `Unity.TextMeshPro` / `Assembly-CSharp`：仅作为 legacy 离线测试 stub；生产访问由 generated Il2CppInterop proxies 提供。

这里的目标是二进制加载兼容，不是完整复制 PC 运行环境。

当前已有：

- `UnityModManager/`
- `JALib/`
- `0Harmony/`
- `UnityEngine.CoreModule/`
- `UnityEngine.UIModule/`
- `UnityEngine.UI/`
- `Unity.TextMeshPro/`
- `UnityEngine.InputLegacyModule/`
- `UnityEngine.IMGUIModule/`
- `UnityEngine.AssetBundleModule/`
- `UnityEngine.AudioModule/`
- `Assembly-CSharp/`

`build_shims.ps1` 把 `UnityModManager/JALib/0Harmony/Newtonsoft.Json` 输出到 `out/shims`，把 Unity/游戏 stub 额外输出到 `out/legacy_shims`。Android runtime assets 只打包前者；生产 managed session 必须使用重写 DLL 和 `pc_compat_proxies`，测试若要执行 legacy stub 路径必须显式设置 `AllowLegacyStubExecution=true`。
