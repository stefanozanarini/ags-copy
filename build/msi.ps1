# Builds publish\AGS-Copy-<version>.msi on Windows (the GitHub workflow runs this too).
#   build\msi.ps1                 version from Directory.Build.props
#   build\msi.ps1 -Version 1.2.3
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version }

$pub = 'publish\AGS-Copy'
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
foreach ($p in 'src\AgsCopy.Service', 'src\AgsCopy.Watchdog', 'src\AgsCopy.App') {
    dotnet publish $p -c Release -r win-x64 --self-contained -p:Version=$Version -o $pub
    if ($LASTEXITCODE) { throw "dotnet publish $p failed" }
}
Remove-Item "$pub\*.pdb"

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { dotnet tool install --global wix --version 5.0.2 }
wix extension add -g WixToolset.Util.wixext/5.0.2
$out = "publish\AGS-Copy-$Version.msi"
wix build installer\AgsCopy.wxs -arch x64 -ext WixToolset.Util.wixext -d Version=$Version -bindpath pub=$pub -o $out
if ($LASTEXITCODE) { throw 'wix build failed' }
Get-Item $out
