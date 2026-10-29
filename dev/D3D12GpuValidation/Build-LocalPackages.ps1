[CmdletBinding()]
param([string]$Version='5.1.60-gpu.2',[string]$LocalFeed='D:\nugetLocal',[switch]$SkipGpuTests,[switch]$Overwrite)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$gpu=Join-Path $repo 'src\ScottPlot5\ScottPlot5 Controls\ScottPlot.D3D12.FullGpu'
$release=Join-Path $repo ("artifacts\d3d12-fullgpu\release-"+[DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$ids=@('ScottPlot','ScottPlot.WPF','ScottPlot.D3D12.Core','ScottPlot.D3D12','ScottPlot.D3D12.WinUI','ScottPlot.D3D12.UWP')
foreach($id in $ids){if(-not $Overwrite -and (Test-Path -LiteralPath (Join-Path $LocalFeed "$id.$Version.nupkg"))){throw "Package exists: $id.$Version. Choose a new version, or pass -Overwrite; existing packages are never overwritten by default."}}
$vswhere=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
$msbuild=Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if(-not(Test-Path -LiteralPath $msbuild)){throw 'Visual Studio MSBuild is required for WinUI packaging.'}
New-Item -ItemType Directory -Path $release,(Join-Path $release 'packages'),$LocalFeed -Force | Out-Null
Push-Location $PSScriptRoot # global.json pins the SDK.
try {
    if(-not $SkipGpuTests){
        & dotnet test "$gpu\Tests\ScottPlot.D3D12.FullGpu.Tests.csproj" -c Release -p:NuGetAudit=false -m:1 --results-directory "$release\tests" --logger 'trx;LogFileName=hardware.trx' *> "$release\tests.log"
        if($LASTEXITCODE -ne 0){throw "Hardware tests failed: $release\tests.log"}
    }
    $projects=@(
        "$repo\src\ScottPlot5\ScottPlot5\ScottPlot.csproj",
        "$repo\src\ScottPlot5\ScottPlot5 Controls\ScottPlot.WPF\ScottPlot.WPF.csproj",
        "$gpu\Core\ScottPlot.D3D12.Core.csproj","$gpu\WPF\ScottPlot.D3D12.csproj",
        "$gpu\WinUI\ScottPlot.D3D12.WinUI.csproj","$gpu\UWP\ScottPlot.D3D12.UWP.csproj")
    for($i=0;$i -lt $projects.Count;$i++){
        $id=$ids[$i]
        $common=@("/p:Version=$Version","/p:PackageVersion=$Version",'/p:Configuration=Release','/p:BuildInParallel=false','/p:NuGetAudit=false','/p:UseArtifactsOutput=true',"/p:ArtifactsPath=$release\clean-$id","/p:PackageOutputPath=$release\packages",'/m:1','/nologo')
        if($i -ge 4){& $msbuild $projects[$i] /restore /t:Pack @common *> "$release\$id-pack.log"}
        else{& dotnet msbuild $projects[$i] /restore /t:Pack @common *> "$release\$id-pack.log"}
        if($LASTEXITCODE -ne 0){throw "Pack failed: $release\$id-pack.log"}
    }
    $manifest=foreach($id in $ids){
        $file=Get-Item -LiteralPath "$release\packages\$id.$Version.nupkg"
        $destination=Join-Path $LocalFeed $file.Name
        if(-not $Overwrite -and (Test-Path -LiteralPath $destination)){throw "Package appeared during build: $destination"}
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if($hash -ne (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash){throw "Copy hash mismatch: $id"}
        [pscustomobject]@{File=$file.Name;Bytes=$file.Length;SHA256=$hash}
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath "$release\package-manifest.json" -Encoding utf8
    "Local packages: $LocalFeed"; "Evidence: $release"
}
finally{Pop-Location}
