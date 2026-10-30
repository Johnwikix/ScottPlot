[CmdletBinding()]
param(
    [string]$Version='5.1.60-gpu.1',
    [string]$Prefix='Johnwikix.',
    [string]$ApiKey=$env:NUGET_API_KEY,
    [string]$FeedUrl='https://api.nuget.org/v3/index.json',
    [switch]$SkipGpuTests,
    [switch]$PackOnly
)
$ErrorActionPreference='Stop'
if(-not $PackOnly -and [string]::IsNullOrWhiteSpace($ApiKey)){throw 'NuGet API key required for push: pass -ApiKey or set NUGET_API_KEY (use -PackOnly to pack without pushing).'}
if($Prefix -ne '' -and -not $Prefix.EndsWith('.')){throw 'Prefix must end with a dot (e.g. Johnwikix.).'}
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$gpu=Join-Path $repo 'src\ScottPlot5\ScottPlot5 Controls\ScottPlot.D3D12.FullGpu'
$release=Join-Path $repo ("artifacts\d3d12-nuget\release-"+[DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$ids=@('ScottPlot','ScottPlot.WPF','ScottPlot.D3D12.Core','ScottPlot.D3D12','ScottPlot.D3D12.WinUI','ScottPlot.D3D12.UWP')
$projects=@(
    "$repo\src\ScottPlot5\ScottPlot5\ScottPlot.csproj",
    "$repo\src\ScottPlot5\ScottPlot5 Controls\ScottPlot.WPF\ScottPlot.WPF.csproj",
    "$gpu\Core\ScottPlot.D3D12.Core.csproj","$gpu\WPF\ScottPlot.D3D12.csproj",
    "$gpu\WinUI\ScottPlot.D3D12.WinUI.csproj","$gpu\UWP\ScottPlot.D3D12.UWP.csproj")
$vswhere=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
$msbuild=Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if(-not(Test-Path -LiteralPath $msbuild)){throw 'Visual Studio MSBuild is required for WinUI/UWP packaging.'}
New-Item -ItemType Directory -Path $release,(Join-Path $release 'packages') -Force | Out-Null
Push-Location $PSScriptRoot # global.json pins the SDK.
try {
    if(-not $SkipGpuTests){
        & dotnet test "$gpu\Tests\ScottPlot.D3D12.FullGpu.Tests.csproj" -c Release -p:NuGetAudit=false -m:1 --results-directory "$release\tests" --logger 'trx;LogFileName=hardware.trx' *> "$release\tests.log"
        if($LASTEXITCODE -ne 0){throw "Hardware tests failed: $release\tests.log"}
    }
    for($i=0;$i -lt $projects.Count;$i++){
        $id=$ids[$i]
        $common=@("/p:Version=$Version","/p:PackageVersion=$Version","/p:ForkIdPrefix=$Prefix",'/p:Configuration=Release','/p:BuildInParallel=false','/p:NuGetAudit=false','/p:UseArtifactsOutput=true',"/p:ArtifactsPath=$release\clean-$id","/p:PackageOutputPath=$release\packages",'/m:1','/nologo')
        if($i -ge 4){& $msbuild $projects[$i] /restore /t:Pack @common *> "$release\$id-pack.log"}
        else{& dotnet msbuild $projects[$i] /restore /t:Pack @common *> "$release\$id-pack.log"}
        if($LASTEXITCODE -ne 0){throw "Pack failed: $release\$id-pack.log"}
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $manifest=foreach($id in $ids){
        $packageId="$Prefix$id"
        $file=Get-Item -LiteralPath "$release\packages\$packageId.$Version.nupkg"
        $zip=[System.IO.Compression.ZipFile]::OpenRead($file.FullName)
        try{
            $entry=$zip.Entries | Where-Object {$_.Name -eq "$packageId.nuspec"}
            if(-not $entry){throw "nuspec missing in $($file.Name)"}
            $reader=New-Object System.IO.StreamReader($entry.Open())
            $nuspec=$reader.ReadToEnd(); $reader.Close()
        }
        finally{$zip.Dispose()}
        $nuspecId=[regex]::Match($nuspec,'<id>([^<]+)</id>').Groups[1].Value
        $nuspecVersion=[regex]::Match($nuspec,'<version>([^<]+)</version>').Groups[1].Value
        if($nuspecId -ne $packageId){throw "nuspec id mismatch in $($file.Name): $nuspecId"}
        if($nuspecVersion -ne $Version){throw "nuspec version mismatch in $($file.Name): $nuspecVersion"}
        foreach($dep in [regex]::Matches($nuspec,'<dependency id="([^"]+)" version="([^"]+)"')){
            $depId=$dep.Groups[1].Value
            if($depId -like 'ScottPlot*' -and $depId -notlike "$Prefix*"){throw "unprefixed ScottPlot dependency in $($file.Name): $depId"}
        }
        $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        [pscustomobject]@{File=$file.Name;Id=$packageId;Version=$Version;Bytes=$file.Length;SHA256=$hash}
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath "$release\package-manifest.json" -Encoding utf8
    if($PackOnly){
        "Pack-only complete: $release\packages"
    }
    else{
        foreach($id in $ids){
            $file=Join-Path $release "packages\$Prefix$id.$Version.nupkg"
            & dotnet nuget push $file --source $FeedUrl --api-key $ApiKey *> "$release\$id-push.log"
            if($LASTEXITCODE -ne 0){throw "Push failed for $($Prefix)$id : $release\$id-push.log"}
        }
        "Published $($Prefix)* $Version to $FeedUrl. Evidence: $release"
    }
}
finally{Pop-Location}
