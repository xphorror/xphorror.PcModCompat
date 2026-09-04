param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimeProjects = @(
    "shims\UnityModManager\UnityModManager.csproj",
    "shims\0Harmony\0Harmony.csproj",
    "shims\JALib\JALib.csproj"
)
$legacyOracleProjects = @(
    "shims\UnityEngine.CoreModule\UnityEngine.CoreModule.csproj",
    "shims\UnityEngine.UIModule\UnityEngine.UIModule.csproj",
    "shims\UnityEngine.UI\UnityEngine.UI.csproj",
    "shims\Unity.TextMeshPro\Unity.TextMeshPro.csproj",
    "shims\UnityEngine.InputLegacyModule\UnityEngine.InputLegacyModule.csproj",
    "shims\UnityEngine.IMGUIModule\UnityEngine.IMGUIModule.csproj",
    "shims\UnityEngine.AssetBundleModule\UnityEngine.AssetBundleModule.csproj",
    "shims\UnityEngine.AudioModule\UnityEngine.AudioModule.csproj",
    "shims\Assembly-CSharp\Assembly-CSharp.csproj"
)
$buildTarget = @()
if ($Rebuild) {
    $buildTarget += '-t:Rebuild'
}

foreach ($project in $runtimeProjects + $legacyOracleProjects) {
    dotnet build (Join-Path $root $project) -c $Configuration @buildTarget
    if ($LASTEXITCODE -ne 0) {
        throw "PcCompat shim build failed: $project ($LASTEXITCODE)"
    }
}

$apiManifestProject = Join-Path $root "tools\JALibApiManifest\JALibApiManifest.csproj"
$apiManifestDll = Join-Path $root "tools\JALibApiManifest\bin\$Configuration\net10.0\JALibApiManifest.dll"
$jalibCandidate = Join-Path $root "shims\JALib\bin\$Configuration\net10.0\JALib.dll"
$apiReportDir = Join-Path $root "out\api"
$apiReport = Join-Path $apiReportDir "JALib-shim-coverage.json"
$apiReference42 = Join-Path $root "docs\api\JALib-v42.api.json"
$apiReference44 = Join-Path $root "docs\api\JALib-v44.api.json"

dotnet build $apiManifestProject -c $Configuration @buildTarget
if ($LASTEXITCODE -ne 0) {
    throw "PcCompat JALib API manifest tool build failed: $LASTEXITCODE"
}
New-Item -ItemType Directory -Force -Path $apiReportDir | Out-Null
dotnet $apiManifestDll verify $jalibCandidate $apiReport $apiReference42 $apiReference44
if ($LASTEXITCODE -ne 0) {
    throw "PcCompat JALib API compatibility gate failed: $LASTEXITCODE"
}

$outDir = Join-Path $root "out\shims"
$legacyOutDir = Join-Path $root "out\legacy_shims"
foreach ($directory in @($outDir, $legacyOutDir)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$runtimeOutputs = @(
    "shims\UnityModManager\bin\$Configuration\net10.0\UnityModManager.dll",
    "shims\0Harmony\bin\$Configuration\net10.0\0Harmony.dll",
    "shims\JALib\bin\$Configuration\net10.0\JALib.dll",
    "shims\JALib\bin\$Configuration\net10.0\Newtonsoft.Json.dll"
)
$legacyOracleOutputs = @(
    "shims\UnityEngine.CoreModule\bin\$Configuration\net10.0\UnityEngine.CoreModule.dll",
    "shims\UnityEngine.UIModule\bin\$Configuration\net10.0\UnityEngine.UIModule.dll",
    "shims\UnityEngine.UI\bin\$Configuration\net10.0\UnityEngine.UI.dll",
    "shims\Unity.TextMeshPro\bin\$Configuration\net10.0\Unity.TextMeshPro.dll",
    "shims\UnityEngine.InputLegacyModule\bin\$Configuration\net10.0\UnityEngine.InputLegacyModule.dll",
    "shims\UnityEngine.IMGUIModule\bin\$Configuration\net10.0\UnityEngine.IMGUIModule.dll",
    "shims\UnityEngine.AssetBundleModule\bin\$Configuration\net10.0\UnityEngine.AssetBundleModule.dll",
    "shims\UnityEngine.AudioModule\bin\$Configuration\net10.0\UnityEngine.AudioModule.dll",
    "shims\Assembly-CSharp\bin\$Configuration\net10.0\Assembly-CSharp.dll"
)

foreach ($output in $runtimeOutputs) {
    $path = Join-Path $root $output
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "PcCompat runtime shim output is missing: $path"
    }
    Copy-Item -Force -LiteralPath $path -Destination $outDir
    Copy-Item -Force -LiteralPath $path -Destination $legacyOutDir
}

foreach ($output in $legacyOracleOutputs) {
    $path = Join-Path $root $output
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "PcCompat legacy oracle shim output is missing: $path"
    }
    Copy-Item -Force -LiteralPath $path -Destination $legacyOutDir
}

Write-Host "[built] runtime shims $outDir"
Write-Host "[built] legacy test shims $legacyOutDir"
