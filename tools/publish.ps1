<#
.SYNOPSIS
    Publish the Tranquillity servers into ready-to-run folders and zip them (Windows counterpart of publish.sh).

.DESCRIPTION
    Runs `dotnet publish` for Robust (GridServer), the region server and optionally the money server into
    publish\<runtime>\<project>, then zips each folder to publish\<name>-<runtime>.zip. Run it from anywhere;
    it works from the repository root. Needs the .NET SDK named in global.json (10.0.110 or later 10.0.x).

.EXAMPLE
    .\tools\publish.ps1                       # Release, win-x64, RegionServer + GridServer, zipped
.EXAMPLE
    .\tools\publish.ps1 -Configuration Debug -IncludeMoneyServer
.EXAMPLE
    .\tools\publish.ps1 -Runtime linux-x64 -NoZip
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $Runtime = 'win-x64',

    [switch] $IncludeMoneyServer,

    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$projects = [ordered]@{
    'NGC-Tranquillity-RegionServer' = 'Source\OpenSim.Server.RegionServer\OpenSim.Server.RegionServer.csproj'
    'NGC-Tranquillity-Robust'       = 'Source\OpenSim.Server.GridServer\OpenSim.Server.GridServer.csproj'
}
if ($IncludeMoneyServer) {
    $projects['NGC-Tranquillity-MoneyServer'] = 'Source\OpenSim.Server.MoneyServer\OpenSim.Server.MoneyServer.csproj'
}

$outRoot = Join-Path $root "publish\$Runtime"
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

foreach ($name in $projects.Keys) {
    $project = $projects[$name]
    $out = Join-Path $outRoot ([IO.Path]::GetFileNameWithoutExtension($project))

    # A clean folder each time, so files from an earlier build never end up in the zip.
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }

    Write-Host "==> Publishing $project ($Configuration, $Runtime) to $out" -ForegroundColor Cyan
    dotnet publish $project -c $Configuration -r $Runtime --self-contained false -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project (exit code $LASTEXITCODE)" }

    if (-not $NoZip) {
        $zip = Join-Path $root "publish\$name-$Runtime.zip"
        if (Test-Path $zip) { Remove-Item -Force $zip }
        Write-Host "==> Zipping to $zip" -ForegroundColor Cyan
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
    }
}

Write-Host ""
Write-Host "Done. Folders are in $outRoot" -ForegroundColor Green
if (-not $NoZip) { Get-ChildItem (Join-Path $root 'publish') -Filter "*-$Runtime.zip" | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } }
