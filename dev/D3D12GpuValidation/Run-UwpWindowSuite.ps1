[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageFamilyName,
    [string]$LaunchArgs = '--suite',
    [string]$Lvt = 'C:\Users\90684\tools\lvt\lvt.exe',
    [int]$TimeoutSeconds = 1800,
    [int]$ExpectedScenes = 89,
    [string]$WindowTitle = 'ScottPlot Full GPU Validation',
    [switch]$ClickWheel
)
$ErrorActionPreference = 'Stop'
Get-Process -Name D3D12GpuUwpValidation -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$evidenceFull = Join-Path $env:LOCALAPPDATA ("Packages\" + $PackageFamilyName + "\LocalState\validation")
if (Test-Path -LiteralPath $evidenceFull) { Remove-Item -LiteralPath $evidenceFull -Recurse -Force }
New-Item -ItemType Directory -Path $evidenceFull -Force | Out-Null
function Read-SharedJson([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]'ReadWrite, Delete')
        try { return ([System.IO.StreamReader]::new($stream)).ReadToEnd() | ConvertFrom-Json } finally { $stream.Dispose() }
    } catch { return $null }
}
function Get-TargetTitle {
    param($Info)
    $ErrorActionPreference = 'Continue'
    $candidates = @()
    if ($Info) { $candidates += ("UWP [{0}/{1}] {2}" -f ($Info.Index + 1), $ExpectedScenes, $Info.Name) }
    $candidates += $WindowTitle
    foreach ($candidate in $candidates) {
        & $Lvt dump --title $candidate --fast 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return $candidate }
    }
    return $null
}
Start-Process ("shell:AppsFolder\" + $PackageFamilyName + "!App") -ArgumentList $LaunchArgs
Start-Sleep -Seconds 3
$activatedProc = Get-Process -Name D3D12GpuUwpValidation -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $activatedProc) { throw "UWP app did not start" }
[uint32]$activatedPid = [uint32]$activatedProc.Id
Write-Host "Activated PID $activatedPid with args: $LaunchArgs"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$acked = -1
$lastProgress = Get-Date
$wheelDone = -not $ClickWheel
$processName = 'D3D12GpuUwpValidation'
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $alive = Get-Process -Id $activatedPid -ErrorAction SilentlyContinue
        if (-not $alive) {
            if (Test-Path (Join-Path $evidenceFull 'suite-completed.txt')) { break }
            throw "App exited before completion. Check fatal.txt/unhandled.txt under $evidenceFull"
        }
        if (((Get-Date) - $lastProgress).TotalSeconds -gt 120) { throw "No scene progress for 120s (acked=$acked). Check window title / scene-ready.json under $evidenceFull" }
        if (Test-Path (Join-Path $evidenceFull 'suite-completed.txt')) { break }
        $info = Read-SharedJson (Join-Path $evidenceFull 'scene-ready.json')
        if ($null -ne $info -and $info.Index -gt $acked) {
            $ErrorActionPreference = 'Continue'
            $title = Get-TargetTitle $info
            if (-not $title) { throw "No window found for scene $($info.Index)" }
            $shot = Join-Path $evidenceFull $info.Output
            & $Lvt screenshot --title $title --output $shot *> (Join-Path $evidenceFull 'lvt.log')
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $shot)) { throw "lvt screenshot failed for scene $($info.Index); see lvt.log" }
            Set-Content -LiteralPath (Join-Path $evidenceFull 'capture-ack.txt') -Value $info.Index
            $acked = $info.Index
            $lastProgress = Get-Date
            Write-Host ("scene {0}: {1}" -f $info.Index, $info.Name)
            if (-not $wheelDone -and $acked -ge 1) {
                $uia = & $Lvt dump --uia --title $title 2>$null | Out-String
                if ($uia -match 'WheelAction' -and $uia -match '"id"\s*:\s*"(e\d+)"[^}]*WheelAction|WheelAction[^}]*"id"\s*:\s*"(e\d+)"') {
                    $wheelElement = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                    & $Lvt click $wheelElement --title $title *> (Join-Path $evidenceFull 'lvt-wheel.log')
                    Start-Sleep -Seconds 2
                    $wheelDone = Test-Path (Join-Path $evidenceFull 'wheel-action.json')
                }
            }
        }
    }
    if (-not (Test-Path (Join-Path $evidenceFull 'suite-completed.txt'))) { throw "Suite timed out after $TimeoutSeconds at scene $acked" }
    Write-Host "Suite completed: $evidenceFull"
    $suiteJson = Join-Path $evidenceFull 'window-suite.json'
    if (Test-Path $suiteJson) { Write-Host "Scenes: $((Read-SharedJson $suiteJson).Scenes)" }
}
finally {
    $ErrorActionPreference = 'Continue'
    $alive = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if ($alive) {
        $title = Get-TargetTitle $null
        if ($title) {
            $uia = & $Lvt dump --uia --title $title 2>$null | Out-String
            if ($uia -match 'CloseProbe' -and $uia -match '"id"\s*:\s*"(e\d+)"[^}]*CloseProbe|CloseProbe[^}]*"id"\s*:\s*"(e\d+)"') {
                $closeElement = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                & $Lvt click $closeElement --title $title *> (Join-Path $evidenceFull 'lvt-close.log')
                Start-Sleep -Seconds 5
            }
        }
        Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}
