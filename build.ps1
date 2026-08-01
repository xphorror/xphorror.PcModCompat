param(
    [Parameter(Mandatory = $true)]
    [string]$NdkRoot,

    [Parameter(Mandatory = $true)]
    [string]$DobbyLibrary,

    [Parameter(Mandatory = $true)]
    [string]$Il2CppMscorlibPath,

    [Parameter(Mandatory = $true)]
    [string]$ProxyAssembliesDir,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$PackageName = "com.fizzd.connectedworlds.leveleditor.debug",
    [int]$MinApi = 26,
    [switch]$RunTests,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ndk = (Resolve-Path -LiteralPath $NdkRoot).Path
$dobby = (Resolve-Path -LiteralPath $DobbyLibrary).Path
$corlib = (Resolve-Path -LiteralPath $Il2CppMscorlibPath).Path
$proxies = (Resolve-Path -LiteralPath $ProxyAssembliesDir).Path

if ((Split-Path -Leaf $ndk) -ne "25.2.9519653") {
    throw "Android NDK 25.2.9519653 is required: $ndk"
}
if ((Split-Path -Leaf $dobby) -ne "libdobby.a") {
    throw "DobbyLibrary must point to libdobby.a"
}

$sdkRoot = Split-Path -Parent (Split-Path -Parent $ndk)
$cmake = Join-Path $sdkRoot "cmake\3.22.1\bin\cmake.exe"
$ninja = Join-Path $sdkRoot "cmake\3.22.1\bin\ninja.exe"
if (!(Test-Path -LiteralPath $cmake)) {
    $cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmakeCommand) { $cmake = $cmakeCommand.Source }
}
if (!(Test-Path -LiteralPath $ninja)) {
    $ninjaCommand = Get-Command ninja -ErrorAction SilentlyContinue
    if ($ninjaCommand) { $ninja = $ninjaCommand.Source }
}
if (!(Test-Path -LiteralPath $cmake) -or !(Test-Path -LiteralPath $ninja)) {
    throw "CMake 3.22.1 and Ninja are required"
}

$buildRoot = Join-Path $root "build\public"
$nativeBuild = Join-Path $buildRoot "native\$Configuration"
$outRoot = Join-Path $root "out"
$abiOut = Join-Path $outRoot "arm64-v8a"
$runtimeOut = Join-Path $outRoot "runtime"
if ($Clean) {
    foreach ($path in @($buildRoot, $outRoot)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}
New-Item -ItemType Directory -Force -Path $abiOut, $runtimeOut | Out-Null

function Invoke-Checked([string]$Label, [scriptblock]$Action) {
    Write-Host "[build] $Label"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
}

function Resolve-NuGetRuntimePack([string]$Rid, [string]$TargetFramework) {
    $output = dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw "dotnet nuget locals failed" }
    $packagesRoot = $null
    foreach ($line in $output) {
        if ($line -match '^global-packages:\s*(.+)$') {
            $packagesRoot = $Matches[1].Trim()
            break
        }
    }
    if (!$packagesRoot) { $packagesRoot = Join-Path $env:USERPROFILE ".nuget\packages" }
    $packRoot = Join-Path $packagesRoot "microsoft.netcore.app.runtime.$Rid"
    $candidate = Get-ChildItem -LiteralPath $packRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName "runtimes\$Rid\lib\$TargetFramework\System.Private.CoreLib.dll")
        } |
        Select-Object -First 1
    if (!$candidate) { throw "Android CoreCLR runtime pack is unavailable: $packRoot" }
    return $candidate.FullName
}

function Copy-DirectoryFiles([string]$Source, [string]$Destination, [string[]]$Patterns) {
    foreach ($pattern in $Patterns) {
        Copy-Item -Path (Join-Path $Source $pattern) -Destination $Destination -Force -ErrorAction SilentlyContinue
    }
}

$requiredProxies = @(
    "RDTools.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.UIModule.dll",
    "UnityEngine.AudioModule.dll",
    "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.TextCoreFontEngineModule.dll",
    "UnityEngine.TextCoreTextEngineModule.dll",
    "UnityEngine.AssetBundleModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "UnityEngine.InputLegacyModule.dll",
    "UnityEngine.UI.dll",
    "Unity.TextMeshPro.dll",
    "Assembly-CSharp.dll",
    "Il2Cppmscorlib.dll"
)
$proxyStage = Join-Path $root "xphorror.PcModCompat\out\interop\proxy_assemblies"
New-Item -ItemType Directory -Force -Path $proxyStage | Out-Null
foreach ($name in $requiredProxies) {
    $source = Join-Path $proxies $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required generated proxy is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $proxyStage -Force
}

$androidProject = Join-Path $root "StArray.ModManager.Android\StArray.ModManager.Android.csproj"
$managedProperties = @(
    "-p:Il2CppInteropAndroidSlim=true",
    "-p:Il2CppMscorlibPath=$corlib",
    "-p:PcCompatRewrittenOracleDefault=true"
)
Invoke-Checked "restore managed runtime" {
    dotnet restore $androidProject -r android-arm64 @managedProperties --nologo
}
Invoke-Checked "build managed runtime" {
    dotnet build $androidProject -c $Configuration @managedProperties --no-restore --nologo
}

if ($RunTests) {
    $testProject = Join-Path $root "StArray.ModManager.Tests\StArray.ModManager.Tests.csproj"
    Invoke-Checked "managed regression tests" {
        dotnet test $testProject -c $Configuration "-p:Il2CppMscorlibPath=$corlib" --nologo
    }
}

$managedOut = Join-Path $root "StArray.ModManager.Android\bin\$Configuration\net10.0"
if (!(Test-Path -LiteralPath $managedOut)) { throw "Managed output missing: $managedOut" }
Copy-DirectoryFiles $managedOut $runtimeOut @("*.dll", "*.json")
Get-ChildItem -LiteralPath $managedOut -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "runtimes" } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $runtimeOut -Recurse -Force }

$runtimePack = Resolve-NuGetRuntimePack "android-arm64" "net10.0"
$runtimeManaged = Join-Path $runtimePack "runtimes\android-arm64\lib\net10.0"
$runtimeNative = Join-Path $runtimePack "runtimes\android-arm64\native"
Copy-DirectoryFiles $runtimeManaged $runtimeOut @("*.dll", "*.json")
Copy-DirectoryFiles $runtimeNative $runtimeOut @("*.so")

Invoke-Checked "build PcCompat runtime shims" {
    & (Join-Path $root "xphorror.PcModCompat\build_shims.ps1") -Configuration $Configuration
}
$shimSource = Join-Path $root "xphorror.PcModCompat\out\shims"
$shimOut = Join-Path $runtimeOut "pc_compat_shims"
New-Item -ItemType Directory -Force -Path $shimOut | Out-Null
Copy-Item -Path (Join-Path $shimSource "*") -Destination $shimOut -Force

$proxyOut = Join-Path $runtimeOut "pc_compat_proxies"
New-Item -ItemType Directory -Force -Path $proxyOut | Out-Null
foreach ($name in $requiredProxies) {
    $source = Join-Path $proxyStage $name
    Copy-Item -LiteralPath $source -Destination $proxyOut -Force
}
Copy-Item -LiteralPath (Join-Path $proxyStage "Il2Cppmscorlib.dll") -Destination $runtimeOut -Force

$capabilitySource = Join-Path $root "xphorror.PcModCompat\assets\pc_compat_capabilities"
$capabilityOut = Join-Path $runtimeOut "pc_compat_capabilities"
New-Item -ItemType Directory -Force -Path $capabilityOut | Out-Null
foreach ($name in @(
    "pccompat_capabilities_android",
    "pccompat_capability_whitelist.json",
    "pccompat_capabilities_android.manifest.json"
)) {
    $source = Join-Path $capabilitySource $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Capability asset missing: $source" }
    Copy-Item -LiteralPath $source -Destination $capabilityOut -Force
}

$toolchain = Join-Path $ndk "build\cmake\android.toolchain.cmake"
Invoke-Checked "configure native ModManager" {
    & $cmake -S (Join-Path $root "Android\library\src\main\cpp") -B $nativeBuild -G Ninja `
        "-DCMAKE_MAKE_PROGRAM=$ninja" `
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
        "-DANDROID_ABI=arm64-v8a" `
        "-DANDROID_PLATFORM=android-$MinApi" `
        "-DANDROID_STL=c++_static" `
        "-DCMAKE_BUILD_TYPE=$Configuration" `
        "-DSTARRAY_DOBBY_LIBRARY=$dobby"
}
Invoke-Checked "build native ModManager" {
    & $cmake --build $nativeBuild --target starray_modmanager
}
$modManagerSo = Join-Path $nativeBuild "libstarray_modmanager.so"
if (!(Test-Path -LiteralPath $modManagerSo)) { throw "Native output missing: $modManagerSo" }
Copy-Item -LiteralPath $modManagerSo -Destination $abiOut -Force

$dobbyRoot = Split-Path -Parent $dobby
Invoke-Checked "build AsyncInput submodule" {
    & (Join-Path $root "external\AsyncInput\build.ps1") `
        -NdkRoot $ndk -DobbyRoot $dobbyRoot -PackageName $PackageName -Configuration $Configuration
}
$asyncSo = Join-Path $root "external\AsyncInput\out\arm64-v8a\libAsyncInput.so"
if (!(Test-Path -LiteralPath $asyncSo)) { throw "AsyncInput output missing: $asyncSo" }
Copy-Item -LiteralPath $asyncSo -Destination $abiOut -Force

if ($Configuration -eq "Release") {
    $strip = Join-Path $ndk "toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-strip.exe"
    & $strip --strip-unneeded (Join-Path $abiOut "libstarray_modmanager.so")
    & $strip --strip-unneeded (Join-Path $abiOut "libAsyncInput.so")
}

$readelf = Join-Path $ndk "toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-readelf.exe"
$symbols = (& $readelf --dyn-syms --wide (Join-Path $abiOut "libstarray_modmanager.so")) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw "llvm-readelf JNI export audit failed with exit code $LASTEXITCODE"
}
foreach ($name in @(
    "modmanager_libil2cpp_handle",
    "modmanager_runtime_configure_app_files_dir",
    "modmanager_pccompat_load_hook_rules_json"
)) {
    if ($symbols -notmatch "(?m)\b$([regex]::Escape($name))$") {
        throw "Required ModManager export missing: $name"
    }
}

$jniBindings = Get-Content -Raw -LiteralPath (Join-Path $root "StArray.ModManager.Android\Native\JniHelperNative.cs")
$jniExports = [regex]::Matches($jniBindings, 'EntryPoint\s*=\s*"(jnihelper_[a-z0-9_]+)"') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique
$missingJniExports = @($jniExports | Where-Object {
    $symbols -notmatch "(?m)\b$([regex]::Escape($_))$"
})
if ($missingJniExports.Count -ne 0) {
    throw "JNI helper exports missing from final SO: $($missingJniExports -join ', ')"
}

foreach ($name in @(
    "libcoreclr.so",
    "libclrjit.so",
    "System.Private.CoreLib.dll",
    "StArray.ModManager.Android.dll",
    "StArray.ModManager.dll",
    "Il2CppInterop.Runtime.dll",
    "Il2Cppmscorlib.dll"
)) {
    if (!(Test-Path -LiteralPath (Join-Path $runtimeOut $name))) {
        throw "Runtime output missing: $name"
    }
}

foreach ($name in @("xphorror.PcModCompat.Resources.dll", "AssetsTools.NET.dll")) {
    if (!(Test-Path -LiteralPath (Join-Path $runtimeOut $name))) {
        throw "Resource compiler runtime dependency missing: $name"
    }
}
if (!(Test-Path -LiteralPath (Join-Path $shimOut "Newtonsoft.Json.dll"))) {
    throw "Runtime shim dependency missing: Newtonsoft.Json.dll"
}
$runtimeCorlibHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (
    Join-Path $runtimeOut "Il2Cppmscorlib.dll")).Hash
$proxyCorlibHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (
    Join-Path $proxyStage "Il2Cppmscorlib.dll")).Hash
if ($runtimeCorlibHash -ne $proxyCorlibHash) {
    throw "Runtime Il2Cppmscorlib.dll was not replaced by the generated proxy"
}

Write-Host "[built] $(Join-Path $abiOut 'libstarray_modmanager.so')"
Write-Host "[built] $(Join-Path $abiOut 'libAsyncInput.so')"
Write-Host "[built] $runtimeOut"
