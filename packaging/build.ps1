<#
.SYNOPSIS
    Builds WALLNut release downloads for one platform. Runs on Windows, macOS and Linux (PowerShell 7).

.DESCRIPTION
    Publishes the service and tray app (self-contained: no .NET needed to run them), then packages:
      win-*    WALLNut-<version>-<rid>-setup.exe (Inno Setup) and WALLNut-<version>-<rid>.zip
      others   WALLNut-<version>-<rid>.tar.gz: extract, then  sudo ./install.sh
    Everything lands in dist/. Build each platform on its own OS (CI does) so file permissions
    and native parts are right.

.EXAMPLE
    ./packaging/build.ps1 -Rid win-x64 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Rid,
    [string]$Version = '0.1.0',
    [string]$Out = (Join-Path $PSScriptRoot '..' 'dist'),
    # Make only the .zip/.tar.gz (for example when Inno Setup isn't installed).
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$name = "WALLNut-$Version-$Rid"
$work = Join-Path $repo 'artifacts' 'release' $Rid
$stage = Join-Path $work $name
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# ---- Publish -------------------------------------------------------------------------
$windows = $Rid.StartsWith('win-')
$serviceDir = Join-Path $stage ($windows ? 'Service' : 'service')
$trayDir = Join-Path $stage ($windows ? 'Tray' : 'tray')
$common = @('-c', 'Release', '-r', $Rid, '--self-contained', '-p:PublishSingleFile=true', '-p:DebugType=none',
            "-p:Version=$Version", '--artifacts-path', (Join-Path $work 'build'), '--nologo')
if ($windows) { $common += '-p:IncludeNativeLibrariesForSelfExtract=true' }

Step "Publishing the service ($Rid, $Version)"
& dotnet publish (Join-Path $repo 'src' 'WallNut.Service') @common -o $serviceDir
if ($LASTEXITCODE) { throw 'Publishing the service failed.' }
Step "Publishing the tray app ($Rid, $Version)"
& dotnet publish (Join-Path $repo 'src' 'WallNut.App') @common -o $trayDir
if ($LASTEXITCODE) { throw 'Publishing the tray app failed.' }

# ---- Bundle --------------------------------------------------------------------------
Step 'Adding install scripts and documents'
Copy-Item (Join-Path $repo 'packaging' 'config.example.json') $stage
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $stage ($windows ? 'LICENSE.txt' : 'LICENSE'))
if ($windows) {
    Copy-Item (Join-Path $repo 'packaging' 'windows' 'install.ps1'), (Join-Path $repo 'packaging' 'windows' 'uninstall.ps1') $stage
} else {
    $os = $Rid.StartsWith('osx-') ? 'macos' : 'linux'
    Get-ChildItem (Join-Path $repo 'packaging' $os) | Copy-Item -Destination $stage
    Copy-Item (Join-Path $repo 'src' 'WallNut.App' 'Assets' 'wallnut.png') $stage
}
@"
WALLNut $Version ($Rid)
A wall of data for all of your NUTs: UPS monitoring, alerts and safe shutdown.

Install
$(if ($windows) { "  Easiest: run WALLNut-$Version-$Rid-setup.exe instead of this zip.
  Or, from an administrator PowerShell in this folder:  .\install.ps1" }
  else { "  In a terminal in this folder:  sudo ./install.sh" })

Then open WALLNut and add your UPS under Settings > UPS connections.
Shutdowns start in TEST MODE: nothing is shut down until you turn it off in Settings > Shutdown.

https://github.com/Kcarpenter528/WALLNut
"@ | Set-Content (Join-Path $stage 'README.txt') -Encoding utf8

# ---- Package -------------------------------------------------------------------------
if ($windows) {
    Step "Making $name.zip"
    $zip = Join-Path $Out "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

    if (-not $NoInstaller) {
        Step "Making $name-setup.exe"
        $iscc = @((Get-Command iscc -ErrorAction SilentlyContinue)?.Source,
                  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if (-not $iscc) { throw 'Inno Setup 6 is needed for the setup program (winget install JRSoftware.InnoSetup), or pass -NoInstaller.' }
        # Inno Setup's version fields want digits only (1.2.3), not 1.2.3-beta.1.
        $numeric = ($Version -split '[-+]')[0]
        & $iscc /Q "/DAppVersion=$Version" "/DNumericVersion=$numeric" "/DStageDir=$stage" "/DOutputDir=$Out" (Join-Path $repo 'packaging' 'windows' 'wallnut.iss')
        if ($LASTEXITCODE) { throw "Inno Setup failed ($LASTEXITCODE)." }
    }
} else {
    Step "Making $name.tar.gz"
    foreach ($f in @((Join-Path $serviceDir 'WallNut.Service'), (Join-Path $trayDir 'WALLNut'), (Join-Path $stage 'install.sh'), (Join-Path $stage 'uninstall.sh'))) {
        if ((Test-Path $f) -and -not $IsWindows) { & chmod 755 $f }
    }
    & tar -czf (Join-Path $Out "$name.tar.gz") -C $work $name
    if ($LASTEXITCODE) { throw 'tar failed.' }
}

Step 'Done'
Get-ChildItem $Out -Filter "$name*" | ForEach-Object { Write-Host ("    {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
