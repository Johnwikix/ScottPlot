[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Evidence,
    [Parameter(Mandatory)][string]$ProcessName,
    [string]$LaunchArgs = '',
    [string]$Lvt = 'C:\Users\90684\tools\lvt\lvt.exe',
    [int]$TimeoutSeconds = 1800,
    [switch]$ClickWheel
)
$ErrorActionPreference = 'Stop'
$evidenceFull = [IO.Path]::GetFullPath($Evidence)
if (Test-Path -LiteralPath $evidenceFull) { throw "Evidence dir already exists: $evidenceFull" }
New-Item -ItemType Directory -Path $evidenceFull -Force | Out-Null
$exeFull = [IO.Path]::GetFullPath($Exe)
$proc = Start-Process -FilePath $exeFull -ArgumentList ('"' + $evidenceFull + '" ' + $LaunchArgs) -PassThru
function Get-TargetHwnd {
    $p = Get-Process -Id $script:proc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne 0) { return $p.MainWindowHandle }
    return 0
}
function Read-SharedJson([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]'ReadWrite, Delete')
        try { return ([System.IO.StreamReader]::new($stream)).ReadToEnd() | ConvertFrom-Json } finally { $stream.Dispose() }
    } catch { return $null }
}
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $acked = -1
    $lastProgress = Get-Date
    $wheelDone = -not $ClickWheel
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) {
            if (Test-Path (Join-Path $evidenceFull 'suite-completed.txt')) { break }
            throw "App exited before completion. Check fatal.txt/unhandled.txt under $evidenceFull"
        }
        if (((Get-Date) - $lastProgress).TotalSeconds -gt 120) { throw "No scene progress for 120s (acked=$acked). Check window title / scene-ready.json under $evidenceFull" }
        if (Test-Path (Join-Path $evidenceFull 'suite-completed.txt')) { break }
        $readyPath = Join-Path $evidenceFull 'scene-ready.json'
        $info = Read-SharedJson $readyPath
        if ($null -ne $info -and $info.Index -gt $acked) {
                $hwnd = Get-TargetHwnd
                if ($hwnd -eq 0) { throw "No main window handle yet for scene $($info.Index)" }
                $shot = Join-Path $evidenceFull $info.Output
                & $Lvt screenshot --hwnd $hwnd --output $shot *> (Join-Path $evidenceFull 'lvt.log')
                if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $shot)) { throw "lvt screenshot failed for scene $($info.Index); see lvt.log" }
                Set-Content -LiteralPath (Join-Path $evidenceFull 'capture-ack.txt') -Value $info.Index
                $acked = $info.Index
                $lastProgress = Get-Date
                Write-Host ("scene {0}: {1}" -f $info.Index, $info.Name)
                if (-not $wheelDone -and $acked -ge 1) {
                    $uia = & $Lvt dump --uia --hwnd $hwnd 2>$null | Out-String
                    if ($uia -match 'WheelAction' -and $uia -match '"id"\s*:\s*"(e\d+)"[^}]*WheelAction|WheelAction[^}]*"id"\s*:\s*"(e\d+)"') {
                        $wheelElement = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                        & $Lvt click $wheelElement --hwnd $hwnd *> (Join-Path $evidenceFull 'lvt-wheel.log')
                        Start-Sleep -Seconds 2
                        $wheelDone = Test-Path (Join-Path $evidenceFull 'wheel-action.json')
                    }
                }
        }
    }
    if (-not (Test-Path (Join-Path $evidenceFull 'suite-completed.txt'))) { throw "Suite timed out after $TimeoutSeconds at scene $acked" }
    $stillAlive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($stillAlive) {
        $hwnd = Get-TargetHwnd
        if ($hwnd -ne 0) {
            $uia = & $Lvt dump --uia --hwnd $hwnd 2>$null | Out-String
            if ($uia -match 'CloseProbe' -and $uia -match '"id"\s*:\s*"(e\d+)"[^}]*CloseProbe|CloseProbe[^}]*"id"\s*:\s*"(e\d+)"') {
                $closeElement = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                & $Lvt click $closeElement --hwnd $hwnd *> (Join-Path $evidenceFull 'lvt-close.log')
                Wait-Process -Id $proc.Id -Timeout 20 -ErrorAction SilentlyContinue
            }
        }
        if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $proc.Id -Force }
    }
    Write-Host "Suite completed: $evidenceFull"
    Write-Host "Scenes: $((Get-Content (Join-Path $evidenceFull 'window-suite.json') -Raw | ConvertFrom-Json).Scenes)"
}
finally {
    if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
