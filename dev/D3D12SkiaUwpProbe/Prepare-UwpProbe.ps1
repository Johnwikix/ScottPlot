param([string]$StageRoot = "")
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($StageRoot)) {
    $StageRoot = Join-Path $repoRoot ("artifacts\d3d12-skia-probe\uwp-prepare-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$StageRoot = [IO.Path]::GetFullPath($StageRoot)
if (Test-Path -LiteralPath $StageRoot) { throw 'Stage output already exists. Choose a new directory.' }
New-Item -ItemType Directory -Path $StageRoot | Out-Null
Push-Location $PSScriptRoot
try {
    & dotnet publish D3D12SkiaUwpProbe.csproj -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $StageRoot 'build') -o (Join-Path $StageRoot 'app') -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'UWP probe build failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Destination (Join-Path $StageRoot 'app\AppxManifest.xml')
    $appAssets = Join-Path $StageRoot 'app\Assets'
    New-Item -ItemType Directory -Path $appAssets -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Assets') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $appAssets }
    Write-Output "Prepared, not registered or launched: $(Join-Path $StageRoot 'app\AppxManifest.xml')"
}
finally { Pop-Location }

