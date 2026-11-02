<#
.SYNOPSIS
    Removes WALLNut from Windows. Keeps your settings unless you pass -RemoveSettings.
    Run from an administrator PowerShell.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'WALLNut'),
    [switch]$RemoveSettings
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'WALLNut'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this from an administrator PowerShell.'
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host 'Stopping and removing the WALLNut service...'
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
}

Write-Host 'Removing the tray app...'
Get-Process -Name 'WALLNut' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WALLNut' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\WALLNut.lnk') -ErrorAction SilentlyContinue

# This script may be running from the install folder; remove it after we exit.
if (Test-Path $InstallDir) {
    Write-Host "Removing $InstallDir..."
    Get-ChildItem $InstallDir -Exclude 'uninstall.ps1' | Remove-Item -Recurse -Force
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "timeout /t 2 >nul & rmdir /s /q `"$InstallDir`"" -WindowStyle Hidden
}

$config = Join-Path $env:ProgramData 'WALLNut'
if ($RemoveSettings) {
    Remove-Item $config -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Settings removed.'
} elseif (Test-Path $config) {
    Write-Host "Kept your settings in $config (use -RemoveSettings to delete them)."
}
Write-Host 'WALLNut has been removed.'
