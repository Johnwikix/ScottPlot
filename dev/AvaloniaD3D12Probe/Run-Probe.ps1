param(
    [int]$Frames = 60,
    [int]$Warmup = 10,
    [int]$Rounds = 3,
    [string]$Output,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$probeRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $probeRoot '../..'))
if (-not $Output) {
    $Output = Join-Path $repoRoot ('artifacts/avalonia-d3d12-probe/' + (Get-Date -Format 'yyyy-MM-dd/HHmmss'))
}
$evidence = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $evidence) -and (Get-ChildItem -LiteralPath $evidence | Select-Object -First 1)) {
    throw 'Use a new or empty output directory so old success markers cannot be reused.'
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

Push-Location $probeRoot
try {
    if (-not $NoBuild) {
        & dotnet restore AvaloniaD3D12Probe.csproj --source https://api.nuget.org/v3/index.json -p:NuGetAudit=false -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw 'Probe restore failed.' }
        & dotnet build AvaloniaD3D12Probe.csproj -c Release --no-restore -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    }
    $probeExe = Join-Path $probeRoot 'bin/Release/net10.0-windows/AvaloniaD3D12Probe.exe'
    $probeArguments = @('--output', ('"{0}"' -f $evidence), '--run', 'suite',
        '--frames', $Frames, '--warmup', $Warmup, '--rounds', $Rounds)
    # This is the interactive validation window; leave it open for further inspection.
    $probeProcess = Start-Process -FilePath $probeExe -ArgumentList $probeArguments -WorkingDirectory $probeRoot -PassThru
    Write-Output "Probe PID $($probeProcess.Id); evidence: $evidence"
    $deadline = [DateTimeOffset]::Now.AddMinutes(20)
    while (-not (Test-Path -LiteralPath (Join-Path $evidence 'completed.txt'))) {
        if (Test-Path -LiteralPath (Join-Path $evidence 'failure.txt')) {
            throw (Get-Content -Raw -LiteralPath (Join-Path $evidence 'failure.txt'))
        }
        if ($probeProcess.HasExited) { throw 'Probe exited before recording completion.' }
        if ([DateTimeOffset]::Now -gt $deadline) { throw 'Probe did not complete within 20 minutes.' }
        Start-Sleep -Milliseconds 500
    }
    $validation = Get-Content -Raw -LiteralPath (Join-Path $evidence 'validation.json') | ConvertFrom-Json
    $benchmark = Get-Content -Raw -LiteralPath (Join-Path $evidence 'benchmark.json') | ConvertFrom-Json
    if (-not $validation.Pass -or $benchmark.PresentationReadbacks -ne 0 -or
        $benchmark.Samples.Count -ne (6 * $Frames * $Rounds)) { throw 'Incomplete or failed probe evidence.' }

    $artifacts = @(
        Get-ChildItem -LiteralPath $probeRoot -Filter '*.cs'
        Get-Item -LiteralPath (Join-Path $probeRoot 'AvaloniaD3D12Probe.csproj')
        Get-ChildItem -LiteralPath (Split-Path $probeExe) -Filter '*.dll'
        Get-Item -LiteralPath (Join-Path (Split-Path $probeExe) 'runtimes/win-x64/native/libSkiaSharp.dll') -ErrorAction SilentlyContinue
    )
    $artifacts | Get-FileHash -Algorithm SHA256 | Select-Object Path, Hash |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'hashes.json') -Encoding utf8
    $assets = Get-Content -Raw -LiteralPath (Join-Path $probeRoot 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
    $assets.libraries.Keys | Sort-Object | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $evidence 'resolved-packages.json') -Encoding utf8
    $lvt = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tools/lvt/lvt.exe'
    if (Test-Path -LiteralPath $lvt) {
        & $lvt screenshot --pid $probeProcess.Id --output (Join-Path $evidence 'completed-lvt.png')
    }
    $benchmark.Summary | Format-Table Backend, Scenario, Count, CompositionMedianMs, CompleteMedianMs, CompleteP95Ms
}
finally { Pop-Location }
