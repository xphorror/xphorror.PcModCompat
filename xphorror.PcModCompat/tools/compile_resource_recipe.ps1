param(
    [Parameter(Mandatory = $true)]
    [string]$ModFolder,

    [string]$OutputDir = "",

    [string]$ModId = "",

    [switch]$ForceNonUnity6000
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$ToolProject = Join-Path $Root "tools\ResourceRecipeTool\ResourceRecipeTool.csproj"

if (!(Test-Path -LiteralPath $ToolProject)) {
    throw "ResourceRecipeTool project missing: $ToolProject"
}

$ModFolder = (Resolve-Path -LiteralPath $ModFolder).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $env:TEMP ("pccompat-resource-" + [IO.Path]::GetFileName($ModFolder))
}
if ([string]::IsNullOrWhiteSpace($ModId)) {
    $infoPath = Join-Path $ModFolder "Info.json"
    if (Test-Path -LiteralPath $infoPath) {
        try {
            $info = Get-Content -LiteralPath $infoPath -Encoding UTF8 -Raw |
                ConvertFrom-Json
            if ($info.Id) {
                $ModId = [string]$info.Id
            }
        }
        catch {
            # Fall through to folder-name default.
        }
    }
}
if ([string]::IsNullOrWhiteSpace($ModId)) {
    $ModId = [IO.Path]::GetFileName($ModFolder.TrimEnd('\', '/'))
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$args = @(
    "run",
    "--project", $ToolProject,
    "-c", "Release",
    "--",
    "compile",
    $ModFolder,
    $OutputDir,
    $ModId
)
if ($ForceNonUnity6000) {
    $args += "--force-non-6000"
}

Write-Host "[resource] compile mod=$ModFolder id=$ModId out=$OutputDir"
& dotnet @args
if ($LASTEXITCODE -ne 0) {
    throw "ResourceRecipeTool compile failed with exit code $LASTEXITCODE"
}

$published = Join-Path $ModFolder ".pccompat\resource_recipe.bin"
$publishedIr = Join-Path $ModFolder ".pccompat\resource_ir.bin"
$publishedCompiler = Join-Path $ModFolder ".pccompat\resource_ir_compiler.txt"
if (!(Test-Path -LiteralPath $published)) {
    throw "Expected published recipe missing: $published"
}
if (!(Test-Path -LiteralPath $publishedIr)) {
    throw "Expected published Resource IR missing: $publishedIr"
}
if (!(Test-Path -LiteralPath $publishedCompiler)) {
    throw "Expected Resource IR compiler marker missing or stale: $publishedCompiler"
}
$compilerMarker = @(Get-Content -LiteralPath $publishedCompiler -Encoding UTF8)
if ($compilerMarker.Count -ne 3 -or
    $compilerMarker[0] -ne "pccompat-resource-compile-cache-v1" -or
    $compilerMarker[1] -ne "resource-ir-compiler-v5-font-file" -or
    $compilerMarker[2] -notmatch '^[0-9a-f]{64}$') {
    throw "Expected content-addressed Resource IR compiler marker is invalid: $publishedCompiler"
}

Write-Host "[resource] published $published"
& dotnet run --project $ToolProject -c Release -- summary $published
if ($LASTEXITCODE -ne 0) {
    throw "ResourceRecipeTool summary failed with exit code $LASTEXITCODE"
}
& dotnet run --project $ToolProject -c Release -- validate-ir $publishedIr
if ($LASTEXITCODE -ne 0) {
    throw "ResourceRecipeTool Resource IR validation failed with exit code $LASTEXITCODE"
}
