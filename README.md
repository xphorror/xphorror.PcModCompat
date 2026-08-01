# xphorror.PcModCompat

面向 A Dance of Fire and Ice Android IL2CPP 版本的 PC MOD 兼容层，仓库包含修改后的 StArray ModManager Android 运行时、PcCompat 实现及其构建工具。

目前对JipperResourcePack支持较好，理论上只用JALib的Mod都可以工作，但只测试过JipperResourcePack。

## 当前目标

- Android `arm64-v8a`
- A Dance of Fire and Ice 3.1.2
- JipperResourcePack 1.4.8.2
- Android NDK `25.2.9519653`
- .NET 10 / CoreCLR

仓库不包含游戏 dump、metadata、游戏程序集、MOD 二进制或生成的代理程序集。

## 获取源码

```powershell
git clone --recurse-submodules https://github.com/xphorror/xphorror.PcModCompat.git
```

AsyncInput 子模块是当前运行链的必需依赖，用于提供原始输入观察 ABI 和进程内 IL2CPP 句柄。

## 构建

构建前需要准备：

- .NET 10 SDK
- Android SDK CMake 3.22.1 与 Ninja
- Android NDK `25.2.9519653`
- 导出 `DobbyHook`、`DobbyCodePatch`、`DobbyGetVersion` 的 arm64 `libdobby.a`
- 完整的编译期 `Il2Cppmscorlib.dll`
- 已生成的 Android 代理程序集目录

```powershell
.\build.ps1 `
  -NdkRoot "C:\Android\Sdk\ndk\25.2.9519653" `
  -DobbyLibrary "C:\deps\Dobby\libdobby.a" `
  -Il2CppMscorlibPath "C:\inputs\Il2Cppmscorlib.dll" `
  -ProxyAssembliesDir "C:\inputs\proxy_assemblies" `
  -Configuration Release `
  -RunTests `
  -Clean
```

输出位于：

```text
out/arm64-v8a/libstarray_modmanager.so
out/arm64-v8a/libAsyncInput.so
out/runtime/
```

详细设计、宿主接入、运行时目录和验证标准参见 [技术实现文档](docs/TECHNICAL_IMPLEMENTATION.md)。

## 来源与许可证

- StArray ModManager 上游仓库：[StArraySharp/StArray.ModManager](https://github.com/StArraySharp/StArray.ModManager)
- JipperResourcePack 仓库：[Jongye0l/JipperResourcePack](https://github.com/Jongye0l/JipperResourcePack)
- 本仓库修改部分使用 `LGPL-3.0-only`，详见 [LICENSE](LICENSE)
