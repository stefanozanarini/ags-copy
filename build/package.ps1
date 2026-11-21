# Builds, on Windows (the GitHub workflow runs this too):
#   publish\AGS-Copy-<version>.msi            installer: services, tray, Start menu
#   publish\AGS-Copy-Portable-<version>.exe   one file, nothing to install
#   build\package.ps1                 version from Directory.Build.props
#   build\package.ps1 -Version 1.2.3
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version }

# Absolute: WiX resolves relative bind paths of <Files> from the .wxs folder.
$pub = Join-Path $root 'publish\AGS-Copy'
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
foreach ($p in 'src\AgsCopy.Service', 'src\AgsCopy.Watchdog', 'src\AgsCopy.App') {
    dotnet publish $p -c Release -r win-x64 --self-contained -p:Version=$Version -o $pub
    if ($LASTEXITCODE) { throw "dotnet publish $p failed" }
}
Remove-Item "$pub\*.pdb"

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { dotnet tool install --global wix --version 5.0.2 }
wix extension add -g WixToolset.Util.wixext/5.0.2
$out = "publish\AGS-Copy-$Version.msi"
wix build installer\AgsCopy.wxs -wx -arch x64 -ext WixToolset.Util.wixext -d Version=$Version -bindpath pub=$pub -o $out
if ($LASTEXITCODE) { throw 'wix build failed' }
# The self-contained runtime alone is tens of MB: a small package lost its files.
$msi = Get-Item $out
if ($msi.Length -lt 20MB) { throw "$out is only $($msi.Length) bytes: files are missing" }
$msi

# Portable: the GUI with the engine inside, as a single self-contained exe.
$portableDir = Join-Path $root 'publish\portable'
if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
dotnet publish src\AgsCopy.App -c Release -r win-x64 --self-contained -p:AgsPortable=true -p:Version=$Version -o $portableDir
if ($LASTEXITCODE) { throw 'dotnet publish (portable) failed' }
$portable = "publish\AGS-Copy-Portable-$Version.exe"
Move-Item -Force (Join-Path $portableDir 'AGS-Copy.exe') $portable
Remove-Item -Recurse -Force $portableDir
Get-Item $portable
