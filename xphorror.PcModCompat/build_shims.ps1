param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$projects = @(
    "shims\UnityModManager\UnityModManager.csproj",
    "shims\0Harmony\0Harmony.csproj",
    "shims\JALib\JALib.csproj"
)

foreach ($project in $projects) {
    dotnet build (Join-Path $root $project) -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "PcCompat shim build failed: $project ($LASTEXITCODE)"
    }
}

$outDir = Join-Path $root "out\shims"
if (Test-Path -LiteralPath $outDir) {
    Remove-Item -LiteralPath $outDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$outputs = @(
    "shims\UnityModManager\bin\$Configuration\net10.0\UnityModManager.dll",
    "shims\0Harmony\bin\$Configuration\net10.0\0Harmony.dll",
    "shims\JALib\bin\$Configuration\net10.0\JALib.dll",
    "shims\JALib\bin\$Configuration\net10.0\Newtonsoft.Json.dll"
)
foreach ($output in $outputs) {
    $path = Join-Path $root $output
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "PcCompat runtime shim output is missing: $path"
    }
    Copy-Item -Force -LiteralPath $path -Destination $outDir
}

Write-Host "[built] runtime shims $outDir"
