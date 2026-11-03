<#
.SYNOPSIS
    Installs (or upgrades) WALLNut on Windows: the background service and the tray app.

.DESCRIPTION
    Run from an administrator PowerShell. Safe to run again to upgrade: your settings in
    C:\ProgramData\WALLNut\config.json are kept.

      - Builds self-contained copies of both programs (no .NET install needed to run them),
        unless -SourceDir points at already-built ones, or this script sits in a release bundle
        (next to "service" and "tray" folders).
      - Installs to C:\Program Files\WALLNut.
      - Registers the "WALLNut" Windows service: runs as SYSTEM, starts at boot, restarts if it crashes.
      - Creates C:\ProgramData\WALLNut\config.json if it doesn't exist (administrators only).
      - Starts the tray app for every user at login and adds a Start menu shortcut.

.EXAMPLE
    .\install.ps1 -NasHost 192.168.1.10
    First install, watching the Synology at 192.168.1.10 as the UPS that powers this PC.

.EXAMPLE
    .\install.ps1
    Upgrade, or first install with an example config to edit afterwards.
#>
[CmdletBinding()]
param(
    # Folder with already-built "service" and "tray" subfolders. Default: build from this repository.
    [string]$SourceDir,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'WALLNut'),

    # Used only when creating a new config file.
    [string]$NasHost,
    [string]$UpsName = 'ups',
    [string]$Username = 'monuser',
    [string]$Password = 'secret',
    [ValidateSet('PowersThisMachine', 'Monitor')]
    [string]$Role = 'PowersThisMachine',

    [switch]$NoTray,
    [switch]$NoStart,

    # Used by the setup program: the files are already in -InstallDir, so only register and
    # configure. Writes a log to install.log there and leaves starting the tray app to setup.
    [switch]$InPlace
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'WALLNut'
$ConfigDir = Join-Path $env:ProgramData 'WALLNut'
$ConfigFile = Join-Path $ConfigDir 'config.json'
$RepoRoot = Join-Path $PSScriptRoot '..\..'
# config.example.json: next to this script in a bundle or install, in packaging\ in the repository.
$ExampleConfig = @((Join-Path $PSScriptRoot 'config.example.json'), (Join-Path $RepoRoot 'packaging\config.example.json')) |
    Where-Object { Test-Path $_ } | Select-Object -First 1

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Note($text) { Write-Host "    $text" }

if ($InPlace) {
    Start-Transcript -Path (Join-Path $InstallDir 'install.log') -Force | Out-Null
    $SourceDir = $InstallDir
}
elseif (-not $SourceDir -and (Test-Path (Join-Path $PSScriptRoot 'service\WallNut.Service.exe'))) {
    $SourceDir = $PSScriptRoot   # a release bundle: already built
}

# ---- Checks --------------------------------------------------------------------------
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this from an administrator PowerShell (right-click > Run as administrator).'
}

# ---- Build ---------------------------------------------------------------------------
if (-not $SourceDir) {
    Step 'Building WALLNut (self-contained, Release)'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK is needed to build. Install it, or pass -SourceDir.' }
    $SourceDir = Join-Path ([IO.Path]::GetTempPath()) "wallnut-publish-$([guid]::NewGuid().ToString('N'))"
    $artifacts = Join-Path $SourceDir 'artifacts'
    $common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:PublishSingleFile=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '--artifacts-path', $artifacts, '--nologo', '-v', 'quiet')
    & dotnet publish (Join-Path $RepoRoot 'src\WallNut.Service') @common -o (Join-Path $SourceDir 'service')
    if ($LASTEXITCODE) { throw 'Building the service failed.' }
    if (-not $NoTray) {
        & dotnet publish (Join-Path $RepoRoot 'src\WallNut.App') @common -o (Join-Path $SourceDir 'tray')
        if ($LASTEXITCODE) { throw 'Building the tray app failed.' }
    }
}
$serviceSrc = Join-Path $SourceDir 'service'
$traySrc = Join-Path $SourceDir 'tray'
if ($InPlace) { $serviceSrc = Join-Path $InstallDir 'Service'; $traySrc = Join-Path $InstallDir 'Tray' }
if (-not (Test-Path (Join-Path $serviceSrc 'WallNut.Service.exe'))) { throw "No WallNut.Service.exe in $serviceSrc." }

# ---- Stop what's running (upgrade) ---------------------------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Step 'Stopping the running WALLNut service'
    Stop-Service -Name $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
Get-Process -Name 'WALLNut' -ErrorAction SilentlyContinue | Stop-Process -Force

# ---- Copy files ----------------------------------------------------------------------
$serviceDir = Join-Path $InstallDir 'Service'
$trayDir = Join-Path $InstallDir 'Tray'
if (-not $InPlace) { Step "Installing to $InstallDir" }
foreach ($pair in @(@($serviceSrc, $serviceDir), @($traySrc, $trayDir))) {
    if ($InPlace) { break }
    if (-not (Test-Path $pair[0])) { continue }
    New-Item -ItemType Directory -Force $pair[1] | Out-Null
    # Mirror, so files removed in a new version don't linger.
    & robocopy $pair[0] $pair[1] /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying files to $($pair[1]) failed (robocopy $LASTEXITCODE)." }
}
if (-not $InPlace) {
    Copy-Item $ExampleConfig (Join-Path $InstallDir 'config.example.json') -Force
    Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') (Join-Path $InstallDir 'uninstall.ps1') -Force
}
$serviceExe = Join-Path $serviceDir 'WallNut.Service.exe'
$trayExe = Join-Path $trayDir 'WALLNut.exe'

# ---- Settings ------------------------------------------------------------------------
Step "Settings: $ConfigFile"
New-Item -ItemType Directory -Force $ConfigDir | Out-Null
New-Item -ItemType Directory -Force (Join-Path $ConfigDir 'scripts') | Out-Null  # scripts for actions; admin-only like the settings
if (Test-Path $ConfigFile) {
    Note 'Keeping your existing settings.'
} elseif ($NasHost) {
    $config = [ordered]@{
        WallNut = [ordered]@{
            Endpoints = [ordered]@{
                nas = [ordered]@{
                    DisplayName = 'NAS UPS'; Host = $NasHost; Port = 3493; Ups = $UpsName
                    Username = $Username; Password = $Password; Role = $Role
                    PowerValue = 1; PollIntervalSeconds = 5; TimeoutSeconds = 10
                }
            }
            Shutdown = [ordered]@{ DryRun = $true }
        }
    }
    $config | ConvertTo-Json -Depth 6 | Set-Content -Path $ConfigFile -Encoding utf8
    Note "Created, watching $UpsName@$NasHost ($Role). Shutdown is in TEST MODE."
} else {
    @'
{
  // WALLNut settings. Add your UPS in the WALLNut app (Settings > UPS connections).
  // Every option is described in config.example.json in the program folder.
  "WallNut": {
    "Endpoints": {},
    "Shutdown": { "DryRun": true }
  }
}
'@ | Set-Content -Path $ConfigFile -Encoding utf8
    Note 'Created. Add your UPS in the WALLNut app: Settings > UPS connections.'
}
# Administrators and SYSTEM only: the file can hold passwords. (SIDs, so it works in any language.)
# 1. The folder gets exactly these two entries, marked to pass down to everything inside it.
& icacls $ConfigDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Null
if ($LASTEXITCODE) { throw "Couldn't restrict access to $ConfigDir." }
# 2. Files inside take their permissions from the folder. (Applying the folder rules to files
#    directly would leave them with no permissions at all; this also repairs installs that did.)
& icacls (Join-Path $ConfigDir '*') /reset /T /Q | Out-Null
if ($LASTEXITCODE) { throw "Couldn't set permissions on the files in $ConfigDir." }
# 3. Make sure the service account can really read it before going on.
$acl = (Get-Acl $ConfigFile).Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-18' }
if (-not $acl) { throw "SYSTEM can't read $ConfigFile after setting permissions." }
Note 'Readable by Administrators and SYSTEM only.'

# ---- Event Viewer source -------------------------------------------------------------
try {
    if (-not [Diagnostics.EventLog]::SourceExists($ServiceName)) { [Diagnostics.EventLog]::CreateEventSource($ServiceName, 'Application') }
} catch { Note "Couldn't register the Event Viewer source ($($_.Exception.Message)); the service will try itself." }

# ---- Windows service -----------------------------------------------------------------
Step 'Registering the WALLNut Windows service'
$binPath = "`"$serviceExe`""
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    & sc.exe config $ServiceName binPath= $binPath start= auto obj= LocalSystem DisplayName= 'WALLNut UPS Monitor' | Out-Null
} else {
    & sc.exe create $ServiceName binPath= $binPath start= auto obj= LocalSystem DisplayName= 'WALLNut UPS Monitor' | Out-Null
}
if ($LASTEXITCODE) { throw "Registering the service failed (sc.exe $LASTEXITCODE)." }
& sc.exe description $ServiceName 'Watches UPS devices over Network UPS Tools (NUT), sends alerts, and shuts this computer down safely when the battery runs out.' | Out-Null
# Restart after 5 s, 10 s, then 30 s if it ever stops unexpectedly; reset the count after a day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null
Note 'Runs as SYSTEM, starts at boot, restarts automatically if it stops unexpectedly.'

# ---- Check settings ------------------------------------------------------------------
Step 'Checking settings and UPS connections'
& $serviceExe --check
$checkCode = $LASTEXITCODE
if ($checkCode -eq 1) { Note "Fix the settings in $ConfigFile, then run: Restart-Service $ServiceName" }
elseif ($checkCode -eq 2) { Note 'A UPS could not be reached. The service will keep retrying; see the reason above.' }

# ---- Start ---------------------------------------------------------------------------
if (-not $NoStart) {
    Step 'Starting the service'
    Start-Service -Name $ServiceName
    (Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Note 'Running.'
}

# ---- Tray app ------------------------------------------------------------------------
if (-not $NoTray -and (Test-Path $trayExe)) {
    Step 'Setting up the tray app'
    New-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WALLNut' -Value "`"$trayExe`"" -PropertyType String -Force | Out-Null
    Note 'Starts for every user at login.'

    $lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\WALLNut.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $trayExe
    $shortcut.Arguments = '--open'
    $shortcut.WorkingDirectory = $trayDir
    $shortcut.Description = 'WALLNut UPS monitor'
    $shortcut.Save()
    Note 'Start menu shortcut: WALLNut.'

    # Start it now for you, as you rather than as administrator (explorer launches it un-elevated).
    # The setup program starts it itself (as the user who ran setup).
    if (-not $NoStart -and -not $InPlace) { Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$trayExe`"" }
}

# ---- Done ----------------------------------------------------------------------------
Step 'Done'
Note "Program:   $InstallDir"
Note "Settings:  $ConfigFile"
Note "Logs:      Event Viewer > Windows Logs > Application (source: $ServiceName)"
if ($InPlace) { Note 'Uninstall: Settings > Apps > WALLNut' } else { Note "Uninstall: & '$InstallDir\uninstall.ps1'" }
$cfg = Get-Content $ConfigFile -Raw
if ($cfg -match '"DryRun"\s*:\s*true') {
    Write-Host "`n    Shutdown is in TEST MODE: WALLNut logs 'would shut down' but won't do it." -ForegroundColor Yellow
    Write-Host '    Rehearse an outage first. Then set "DryRun": false and restart the service.' -ForegroundColor Yellow
    Write-Host '    Until then, keep your current UPS software (e.g. WinNUT) in charge of shutdowns.' -ForegroundColor Yellow
}
