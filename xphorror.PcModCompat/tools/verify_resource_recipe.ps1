param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$ToolProject = Join-Path $Root "tools\ResourceRecipeTool\ResourceRecipeTool.csproj"

if (!(Test-Path -LiteralPath $ToolProject)) {
    throw "ResourceRecipeTool project missing: $ToolProject"
}

$Path = (Resolve-Path -LiteralPath $Path).Path
Write-Host "[resource] validate $Path"
& dotnet run --project $ToolProject -c Release -- validate $Path
if ($LASTEXITCODE -ne 0) {
    throw "ResourceRecipeTool validate failed with exit code $LASTEXITCODE"
}

Write-Host "[resource] summary $Path"
& dotnet run --project $ToolProject -c Release -- summary $Path
if ($LASTEXITCODE -ne 0) {
    throw "ResourceRecipeTool summary failed with exit code $LASTEXITCODE"
}
