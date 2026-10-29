param([string]$EvidenceRoot = "")
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repoRoot ("artifacts\d3d12-skia-probe\rerun-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Evidence output already exists. Choose a new directory.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null

function Invoke-ProbeDotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Probe command failed: dotnet $($Arguments -join ' ')" }
}

Push-Location $PSScriptRoot
try {
    $build = Join-Path $EvidenceRoot 'build'
    Invoke-ProbeDotnet -Arguments @('build', 'D3D12SkiaProbe.csproj', '-c', 'Release', '--artifacts-path', $build, '-p:NuGetAudit=false')
    $dll = Join-Path $build 'bin\D3D12SkiaProbe\release\D3D12SkiaProbe.dll'
    Invoke-ProbeDotnet -Arguments @($dll, $EvidenceRoot)
    Invoke-ProbeDotnet -Arguments @($dll, $EvidenceRoot, 'dense')

    $compatBuild = Join-Path $EvidenceRoot 'build-compatibility'
    Invoke-ProbeDotnet -Arguments @('build', '../D3D12SkiaCompatibilityProbe/D3D12SkiaCompatibilityProbe.csproj', '-c', 'Release', '--artifacts-path', $compatBuild, '-p:NuGetAudit=false')
    Invoke-ProbeDotnet -Arguments @((Join-Path $compatBuild 'bin\D3D12SkiaCompatibilityProbe\release\D3D12SkiaCompatibilityProbe.dll'), $EvidenceRoot)

    $memoryBuild = Join-Path $EvidenceRoot 'build-memory'
    Invoke-ProbeDotnet -Arguments @('build', '../D3D12SkiaMemoryProbe/D3D12SkiaMemoryProbe.csproj', '-c', 'Release', '--artifacts-path', $memoryBuild, '-p:NuGetAudit=false')
    $memoryDll = Join-Path $memoryBuild 'bin\D3D12SkiaMemoryProbe\release\D3D12SkiaMemoryProbe.dll'
    Invoke-ProbeDotnet -Arguments @($memoryDll, $EvidenceRoot)
    Invoke-ProbeDotnet -Arguments @($memoryDll, $EvidenceRoot, 'bounded')
    Write-Output "Evidence: $EvidenceRoot"
}
finally { Pop-Location }
