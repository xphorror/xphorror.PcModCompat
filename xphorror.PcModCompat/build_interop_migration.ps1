param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$DumpPath = 'E:\ADOFAI\ADOFAI_312_DUMP\dump.cs',
    [string]$PcAssemblyDirectory = 'E:\ADOFAI\scnEditor_312_unity6\AssetRipper_export_20260620_031057\AuxiliaryFiles\GameAssemblies',
    [string]$TypeSeedPath,
    [string]$SurfacePath,
    [string]$AutoSurfaceModPath,
    [string]$OutputDirectory,

    [switch]$SkipForkBuild,
    [switch]$SkipAndroidBuild,
    [switch]$SkipProxyGeneration,
    [switch]$IncludeAutoSurfaceIgnored,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$CompatRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $CompatRoot
$ForkRoot = Join-Path $RepoRoot 'Il2CppInterop'
$ToolProject = Join-Path $CompatRoot 'tools\AndroidDumpIndex\AndroidDumpIndex.csproj'
$ClosureToolProject = Join-Path $CompatRoot 'tools\ProxyInputClosure\ProxyInputClosure.csproj'
$AuditToolProject = Join-Path $CompatRoot 'tools\ProxyAssemblyAudit\ProxyAssemblyAudit.csproj'
$RewriteToolProject = Join-Path $CompatRoot 'tools\ModAssemblyRewriter\ModAssemblyRewriter.csproj'
$SurfaceScannerProject = Join-Path $CompatRoot 'tools\ProxySurfaceScanner\ProxySurfaceScanner.csproj'
$buildTarget = @()
if ($Rebuild) {
    $buildTarget += '-t:Rebuild'
}

if ([string]::IsNullOrWhiteSpace($TypeSeedPath)) {
    $TypeSeedPath = Join-Path $CompatRoot 'tools\AndroidDumpIndex\proxy_seed_types.txt'
}
if ([string]::IsNullOrWhiteSpace($SurfacePath)) {
    $SurfacePath = Join-Path $CompatRoot 'tools\ProxyInputClosure\proxy_surface_members.txt'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $CompatRoot 'out\interop'
}

$DumpPath = [System.IO.Path]::GetFullPath($DumpPath)
$PcAssemblyDirectory = [System.IO.Path]::GetFullPath($PcAssemblyDirectory)
$TypeSeedPath = [System.IO.Path]::GetFullPath($TypeSeedPath)
$SurfacePath = [System.IO.Path]::GetFullPath($SurfacePath)
if (![string]::IsNullOrWhiteSpace($AutoSurfaceModPath)) {
    $AutoSurfaceModPath = [System.IO.Path]::GetFullPath($AutoSurfaceModPath)
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$IndexPath = Join-Path $OutputDirectory 'android_dump_seed_index.json'
$CatalogPath = Join-Path $OutputDirectory 'android_type_catalog.json'
$EffectiveSurfacePath = $SurfacePath
$AutoSurfacePath = Join-Path $OutputDirectory 'proxy_surface_auto_merged.txt'
$AutoSurfaceReportPath = Join-Path $OutputDirectory 'proxy_surface_auto_report.json'
$AllowListPath = Join-Path $OutputDirectory 'proxy_type_allowlist.txt'
$ClosureReportPath = Join-Path $OutputDirectory 'proxy_closure_report.json'
$ProxyAuditReportPath = Join-Path $OutputDirectory 'proxy_audit_report.json'
$ProxyOutputPath = Join-Path $OutputDirectory 'proxy_assemblies'

foreach ($required in @($DumpPath, $TypeSeedPath, $SurfacePath, $ToolProject, $ClosureToolProject, $AuditToolProject, $RewriteToolProject, $SurfaceScannerProject)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required migration input is missing: $required"
    }
}
if (![string]::IsNullOrWhiteSpace($AutoSurfaceModPath) -and
    !(Test-Path -LiteralPath $AutoSurfaceModPath)) {
    throw "Auto surface MOD input is missing: $AutoSurfaceModPath"
}
if (!(Test-Path -LiteralPath $PcAssemblyDirectory -PathType Container)) {
    throw "PC managed assembly input is missing: $PcAssemblyDirectory"
}
if (!(Test-Path -LiteralPath $ForkRoot -PathType Container)) {
    throw "Il2CppInterop fork is missing: $ForkRoot"
}

function Invoke-Checked([string]$Label, [scriptblock]$Action) {
    Write-Host "[interop] $Label"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed: $LASTEXITCODE"
    }
}

function Assert-File([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected file is missing: $Path"
    }
}

function Assert-NoForbiddenArtifacts([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    $forbidden = Get-ChildItem -LiteralPath $Path -Recurse -File |
        Where-Object {
            $_.Name -in @(
                'MethodAddressToToken.db',
                'MethodAddressToToken.db.txt',
                'MethodXrefScanCache.db',
                'MethodXrefScanCache.db.txt',
                'Il2CppInterop.HarmonySupport.dll',
                'Iced.dll',
                'TerraFX.Interop.Windows.dll'
            )
        }
    if ($forbidden) {
        throw "Forbidden migration artifacts found: $($forbidden.FullName -join ', ')"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Push-Location $RepoRoot
try {
    if (!$SkipForkBuild) {
        Invoke-Checked 'build forked Il2CppInterop' {
            dotnet build '.\Il2CppInterop\Il2CppInterop.sln' -c $Configuration --nologo @buildTarget
        }
    }

    Invoke-Checked 'build Android dump indexer' {
        dotnet build $ToolProject -c $Configuration --nologo @buildTarget
    }
    Invoke-Checked 'build proxy dependency closure tool' {
        dotnet build $ClosureToolProject -c $Configuration --nologo @buildTarget
    }
    Invoke-Checked 'build generated proxy audit tool' {
        dotnet build $AuditToolProject -c $Configuration --nologo @buildTarget
    }
    Invoke-Checked 'build PC MOD proxy member rewriter' {
        dotnet build $RewriteToolProject -c $Configuration --nologo @buildTarget
    }
    Invoke-Checked 'build PC MOD proxy surface scanner' {
        dotnet build $SurfaceScannerProject -c $Configuration --nologo @buildTarget
    }

    $ToolDll = Join-Path $CompatRoot "tools\AndroidDumpIndex\bin\$Configuration\net10.0\AndroidDumpIndex.dll"
    Assert-File $ToolDll
    Invoke-Checked 'index Android dump with strict UTF-8' {
        dotnet $ToolDll `
            --input $DumpPath `
            --output $IndexPath `
            --catalog-output $CatalogPath `
            --type-file $TypeSeedPath `
            --pretty
    }

    if (![string]::IsNullOrWhiteSpace($AutoSurfaceModPath)) {
        $SurfaceScannerDll = Join-Path $CompatRoot "tools\ProxySurfaceScanner\bin\$Configuration\net10.0\ProxySurfaceScanner.dll"
        Assert-File $SurfaceScannerDll
        Invoke-Checked 'scan PC MOD proxy surface' {
            $surfaceArgs = @(
                $SurfaceScannerDll,
                '--mod', $AutoSurfaceModPath,
                '--android-catalog', $CatalogPath,
                '--manual-surface', $SurfacePath,
                '--output', $AutoSurfacePath,
                '--report', $AutoSurfaceReportPath,
                '--pretty'
            )
            if ($IncludeAutoSurfaceIgnored) {
                $surfaceArgs += '--include-ignored'
            }
            dotnet @surfaceArgs
        }
        Assert-File $AutoSurfacePath
        Assert-File $AutoSurfaceReportPath
        $EffectiveSurfacePath = $AutoSurfacePath
    }

    $ClosureToolDll = Join-Path $CompatRoot "tools\ProxyInputClosure\bin\$Configuration\net10.0\ProxyInputClosure.dll"
    Assert-File $ClosureToolDll
    Invoke-Checked 'build Android proxy dependency closure' {
        dotnet $ClosureToolDll `
            --assemblies $PcAssemblyDirectory `
            --android-catalog $CatalogPath `
            --seed-file $TypeSeedPath `
            --surface-file $EffectiveSurfacePath `
            --allowlist-output $AllowListPath `
            --report-output $ClosureReportPath `
            --pretty
    }

    if (!$SkipProxyGeneration) {
        $CliDll = Join-Path $ForkRoot 'bin\Il2CppInterop.CLI\net6.0\Il2CppInterop.CLI.dll'
        Assert-File $CliDll

        $expectedRoot = $OutputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedProxyOutput = [IO.Path]::GetFullPath($ProxyOutputPath)
        if (!$resolvedProxyOutput.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace proxy output outside migration output: $resolvedProxyOutput"
        }
        if (Test-Path -LiteralPath $resolvedProxyOutput) {
            Remove-Item -LiteralPath $resolvedProxyOutput -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $resolvedProxyOutput | Out-Null

        Invoke-Checked 'generate dependency-closed Android proxies' {
            dotnet $CliDll generate `
                --input $PcAssemblyDirectory `
                --output $resolvedProxyOutput `
                --runtime-metadata-only `
                --type-allowlist $AllowListPath `
                --passthrough-names `
                --no-parallel
        }

        foreach ($required in @(
            'Assembly-CSharp.dll',
            'RDTools.dll',
            'Unity.TextMeshPro.dll',
            'UnityEngine.TextCoreFontEngineModule.dll',
            'UnityEngine.CoreModule.dll',
            'UnityEngine.UI.dll',
            'Il2Cppmscorlib.dll'
        )) {
            Assert-File (Join-Path $resolvedProxyOutput $required)
        }
        Assert-NoForbiddenArtifacts $resolvedProxyOutput

        $AuditToolDll = Join-Path $CompatRoot "tools\ProxyAssemblyAudit\bin\$Configuration\net10.0\ProxyAssemblyAudit.dll"
        Assert-File $AuditToolDll
        Invoke-Checked 'audit generated proxy metadata' {
            dotnet $AuditToolDll `
                --input $resolvedProxyOutput `
                --report $ProxyAuditReportPath `
                --pretty
        }
    }

    if (!$SkipAndroidBuild) {
        Invoke-Checked 'build Android managed bootstrap' {
            dotnet build '.\StArray.ModManager.Android\StArray.ModManager.Android.csproj' `
                -c $Configuration `
                -p:Il2CppInteropAndroidSlim=true `
                --nologo `
                @buildTarget
        }

        $ManagedOut = Join-Path $RepoRoot "StArray.ModManager.Android\bin\$Configuration\net10.0"
        foreach ($required in @(
            'StArray.ModManager.Android.dll',
            'Il2CppInterop.Runtime.dll',
            'Il2CppInterop.Common.dll',
            'Il2Cppmscorlib.dll'
        )) {
            Assert-File (Join-Path $ManagedOut $required)
        }
        Assert-NoForbiddenArtifacts $ManagedOut
    }

    Assert-File $IndexPath
    Assert-File $CatalogPath
    Assert-File $AllowListPath
    Assert-File $ClosureReportPath
    if (!$SkipProxyGeneration) {
        Assert-File $ProxyAuditReportPath
    }
    Assert-NoForbiddenArtifacts $OutputDirectory

    $index = Get-Content -LiteralPath $IndexPath -Encoding UTF8 -Raw | ConvertFrom-Json
    if ($index.source.runtimeAddressPolicy -ne 'metadata_only') {
        throw 'Dump index runtimeAddressPolicy is not metadata_only'
    }
    if ($index.source.dumpAddressPolicy -ne 'audit_only') {
        throw 'Dump index dumpAddressPolicy is not audit_only'
    }
    if ($index.summary.parseWarningCount -ne 0) {
        throw "Dump index contains parse warnings: $($index.summary.parseWarningCount)"
    }
    $closure = Get-Content -LiteralPath $ClosureReportPath -Encoding UTF8 -Raw | ConvertFrom-Json
    if ($closure.summary.missingAndroidTypeCount -ne 0 -or $closure.summary.unresolvedMetadataTypeCount -ne 0) {
        throw 'Proxy dependency closure contains unresolved or Android-missing types.'
    }
    $proxyAuditIssueCount = $null
    if (Test-Path -LiteralPath $ProxyAuditReportPath -PathType Leaf) {
        $proxyAudit = Get-Content -LiteralPath $ProxyAuditReportPath -Encoding UTF8 -Raw | ConvertFrom-Json
        $proxyAuditIssueCount = @($proxyAudit.issues).Count
        if ($proxyAuditIssueCount -ne 0) {
            throw "Generated proxy audit contains issues: $proxyAuditIssueCount"
        }
    }

    $proxyFiles = @()
    if (Test-Path -LiteralPath $ProxyOutputPath -PathType Container) {
        $proxyFiles = @(Get-ChildItem -LiteralPath $ProxyOutputPath -File -Filter '*.dll' |
            Sort-Object Name | ForEach-Object {
                [ordered]@{ name = $_.Name; size = $_.Length }
            })
    }

    $migrationReport = [ordered]@{
        formatVersion = 'xphorror.il2cppinterop-migration-build.v1'
        generatedUtc = [DateTime]::UtcNow.ToString('O')
        configuration = $Configuration
        dumpSha256 = $index.source.sha256
        indexedTypes = $index.summary.typesWritten
        indexedMethods = $index.summary.methodsWritten
        closureTypes = $closure.summary.selectedTypeCount
        closureAssemblies = $closure.summary.selectedAssemblyCount
        explicitProxyFields = $closure.summary.explicitFieldCount
        explicitProxyMethods = $closure.summary.explicitMethodCount
        explicitProxyProperties = $closure.summary.explicitPropertyCount
        autoSurfaceEnabled = ![string]::IsNullOrWhiteSpace($AutoSurfaceModPath)
        effectiveSurfacePath = $EffectiveSurfacePath
        proxyAuditIssueCount = $proxyAuditIssueCount
        proxyFiles = $proxyFiles
        runtimeAddressPolicy = 'metadata_only'
        dumpAddressPolicy = 'audit_only'
        xrefScanner = 'disabled'
        detourProvider = 'hook_broker_infrastructure'
        classInjection = 'arm64_upstream_unsupported_not_attempted'
        harmonySupportPackaged = $false
        androidSlimRuntime = $true
        icedPackaged = $false
        terraFxWindowsPackaged = $false
        generatedCorlibPackaged = $true
        corlibBinding = 'dependency-closed generated Il2Cppmscorlib.dll'
        knownSizeDebt = @()
    }
    $ReportPath = Join-Path $OutputDirectory 'migration_build_report.json'
    $migrationReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8

    Write-Host "[interop] index  $IndexPath"
    Write-Host "[interop] report $ReportPath"
}
finally {
    Pop-Location
}
