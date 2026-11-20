#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or updates AGS-Copy: the Windows service, the configuration program and the tray icon.
.DESCRIPTION
    Run from the package folder. Updating keeps jobs, credentials and history
    (they live in %ProgramData%\AGS-Copy).
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\AGS-Copy"
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'AGS-Copy'
$Source = $PSScriptRoot

if (-not (Test-Path (Join-Path $Source 'AgsCopy.Service.exe'))) {
    throw "AgsCopy.Service.exe not found next to this script: run install.ps1 from the package folder."
}

Write-Host "Installing AGS-Copy in $InstallDir"

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host 'Stopping the service (running jobs are interrupted)...'
    Stop-Service -Name $ServiceName -Force
}
Get-Process -Name 'AGS-Copy' -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Get-ChildItem -Path $Source -Exclude 'install.ps1', 'uninstall.ps1' |
    Copy-Item -Destination $InstallDir -Recurse -Force
Copy-Item -Path (Join-Path $Source 'uninstall.ps1') -Destination $InstallDir -Force

$exe = Join-Path $InstallDir 'AgsCopy.Service.exe'
if (-not $service) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'AGS-Copy' `
        -Description 'Scheduled copies and backups with robocopy.' -StartupType Automatic | Out-Null
}
else {
    & sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
}
# Restart the service if it crashes.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Start-Service -Name $ServiceName

# Tray icon at every logon, for every user.
$gui = Join-Path $InstallDir 'AGS-Copy.exe'
New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'AGS-Copy' `
    -Value "`"$gui`" --tray" -PropertyType String -Force | Out-Null

# Start menu shortcut.
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\AGS-Copy.lnk")
$link.TargetPath = $gui
$link.WorkingDirectory = $InstallDir
$link.Description = 'AGS-Copy'
$link.Save()

# Tray icon now, unelevated, for the user running the installer.
& schtasks.exe /Create /TN 'AGS-Copy tray (install)' /TR "`"$gui`" --tray" /SC ONCE /ST 00:00 /RL LIMITED /IT /F 2>$null | Out-Null
& schtasks.exe /Run /TN 'AGS-Copy tray (install)' 2>$null | Out-Null
& schtasks.exe /Delete /TN 'AGS-Copy tray (install)' /F 2>$null | Out-Null

Write-Host 'AGS-Copy installed. Open it from the Start menu or the tray icon.'
