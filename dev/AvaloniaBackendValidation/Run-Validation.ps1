param([string]$Output, [switch]$NoBuild, [switch]$Close)

$ErrorActionPreference = 'Stop'
$validationRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $validationRoot '../..'))
if (-not $Output) { $Output = Join-Path $repoRoot ('artifacts/avalonia-backend/' + (Get-Date -Format 'yyyy-MM-dd/HHmmss')) }
$evidence = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $evidence) -and (Get-ChildItem -LiteralPath $evidence | Select-Object -First 1)) {
    throw 'Use a new or empty output directory.'
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
Push-Location $validationRoot
try {
    if (-not $NoBuild) {
        & dotnet restore AvaloniaBackendValidation.csproj --source https://api.nuget.org/v3/index.json -p:NuGetAudit=false -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
        & dotnet build AvaloniaBackendValidation.csproj -c Release --no-restore -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $exe = Join-Path $validationRoot 'bin/Release/net10.0-windows/AvaloniaBackendValidation.exe'
    $lvt = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tools/lvt/lvt.exe'
    foreach ($mode in @('hardware', 'no-provider', 'software')) {
        $modeOutput = Join-Path $evidence $mode
        $arguments = @('--output', ('"{0}"' -f $modeOutput))
        if ($mode -eq 'hardware') { $arguments += '--run' }
        elseif ($mode -eq 'no-provider') { $arguments += '--no-provider' }
        else { $arguments += '--software' }
        $instance = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $validationRoot -PassThru
        Write-Output "$mode PID $($instance.Id) -> $modeOutput"
        $deadline = [DateTimeOffset]::Now.AddMinutes(5)
        while (-not (Test-Path -LiteralPath (Join-Path $modeOutput 'completed.txt'))) {
            foreach ($errorFile in @('failure.txt', 'fatal.txt')) {
                if (Test-Path -LiteralPath (Join-Path $modeOutput $errorFile)) { throw (Get-Content -Raw -LiteralPath (Join-Path $modeOutput $errorFile)) }
            }
            if ($instance.HasExited) { throw 'Window exited before validation completed.' }
            if ([DateTimeOffset]::Now -gt $deadline) { throw 'Validation timed out.' }
            Start-Sleep -Milliseconds 200
        }
        $resultName = if ($mode -eq 'hardware') { 'validation.json' } else { 'expected-failure.json' }
        $result = Get-Content -Raw -LiteralPath (Join-Path $modeOutput $resultName) | ConvertFrom-Json
        if (-not $result.Pass) { throw "$mode failed." }
        if (Test-Path -LiteralPath $lvt) {
            & $lvt screenshot --pid $instance.Id --output (Join-Path $modeOutput 'window-lvt.png')
            if ($Close) { & $lvt close --pid $instance.Id }
        }
        Write-Output "$mode PASS"
    }
}
finally { Pop-Location }
