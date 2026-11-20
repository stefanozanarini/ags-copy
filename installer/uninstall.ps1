#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes AGS-Copy. Jobs, encrypted credentials, history and logs in %ProgramData%\AGS-Copy
    are kept unless -RemoveData is given.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\AGS-Copy",
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'AGS-Copy'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null
}
Get-Process -Name 'AGS-Copy' -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'AGS-Copy' -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\AGS-Copy.lnk" -ErrorAction SilentlyContinue

# The script may be running from the install folder: remove it once this process ends.
Start-Process -FilePath cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$InstallDir`"" -WindowStyle Hidden

if ($RemoveData) {
    Remove-Item -Recurse -Force "$env:ProgramData\AGS-Copy" -ErrorAction SilentlyContinue
    Write-Host 'AGS-Copy removed, with its jobs and history.'
}
else {
    Write-Host "AGS-Copy removed. Jobs and history are kept in $env:ProgramData\AGS-Copy (use -RemoveData to delete them)."
}
