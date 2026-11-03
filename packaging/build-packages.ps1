# Linux (.deb, .rpm) and macOS (.pkg) installers. Loaded by build.ps1, which sets $Rid, $Version,
# $name, $work, $stage, $serviceDir, $trayDir, $repo and $Out before calling these functions.

# Writes a text file with Unix line endings (PowerShell scripts are stored with CRLF).
function Write-Unix([string]$path, [string]$text, [switch]$Executable) {
    [IO.File]::WriteAllText($path, ($text -replace "`r`n", "`n"))
    if ($Executable -and -not $IsWindows) { & chmod 755 $path }
}

# Package managers want 1.2.3~beta.1 for pre-releases, so they sort before 1.2.3.
function Get-PackageVersion { $Version -replace '-', '~' }

$packageSummary = 'UPS monitoring, alerts and safe shutdown for Network UPS Tools'
$packageDescription = @'
WALLNut watches the UPS units on your network (Synology, QNAP, or any
Network UPS Tools server), sends desktop, phone, email and webhook alerts
on power events, keeps history, and shuts this computer down safely when
the battery runs low.
'@

# ---- Linux ---------------------------------------------------------------------------

# The files both packages install, laid out as they'll be on disk.
function New-LinuxPackageRoot {
    $root = Join-Path $work 'pkgroot'
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
    $opt = Join-Path $root 'opt' 'wallnut'
    $unitDir = Join-Path $root 'usr' 'lib' 'systemd' 'system'
    $appsDir = Join-Path $root 'usr' 'share' 'applications'
    $autostartDir = Join-Path $root 'etc' 'xdg' 'autostart'
    New-Item -ItemType Directory -Force $opt, $unitDir, $appsDir, $autostartDir | Out-Null

    Copy-Item $serviceDir (Join-Path $opt 'service') -Recurse
    Copy-Item $trayDir (Join-Path $opt 'tray') -Recurse
    Copy-Item (Join-Path $stage 'wallnut.png') (Join-Path $opt 'tray' 'wallnut.png')
    Copy-Item (Join-Path $stage 'config.example.json'), (Join-Path $stage 'LICENSE') $opt
    $linux = Join-Path $repo 'packaging' 'linux'
    Copy-Item (Join-Path $linux 'package-setup.sh') $opt
    Copy-Item (Join-Path $linux 'wallnut.service') $unitDir
    Copy-Item (Join-Path $linux 'wallnut-tray.desktop') (Join-Path $appsDir 'wallnut.desktop')
    Copy-Item (Join-Path $linux 'wallnut-tray.desktop') (Join-Path $autostartDir 'wallnut-tray.desktop')
    if (-not $IsWindows) {
        & chmod 755 (Join-Path $opt 'package-setup.sh') (Join-Path $opt 'service' 'WallNut.Service') (Join-Path $opt 'tray' 'WALLNut')
    }
    return $root
}

function New-DebPackage([string]$root) {
    Step "Making $name.deb"
    $arch = @{ 'linux-x64' = 'amd64'; 'linux-arm64' = 'arm64' }[$Rid]
    $debian = Join-Path $root 'DEBIAN'
    New-Item -ItemType Directory -Force $debian | Out-Null
    $sizeKb = [math]::Ceiling(((Get-ChildItem $root -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB)
    $longDescription = ($packageDescription.Trim() -split "`r?`n" | ForEach-Object { " $_" }) -join "`n"

    # The programs are self-contained .NET, which still needs these system libraries.
    # The tray app's are only recommended, so a headless server can skip them.
    Write-Unix (Join-Path $debian 'control') @"
Package: wallnut
Version: $(Get-PackageVersion)
Architecture: $arch
Maintainer: WALLNut contributors <373569+Kcarpenter528@users.noreply.github.com>
Installed-Size: $sizeKb
Depends: libc6, libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3t64 | libssl3 | libssl1.1, libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66 | libicu63
Recommends: libx11-6, libice6, libsm6, libfontconfig1, libnotify-bin
Section: admin
Priority: optional
Homepage: https://github.com/Kcarpenter528/WALLNut
Description: $packageSummary
$longDescription

"@
    Write-Unix (Join-Path $debian 'postinst') @'
#!/bin/sh
set -e
if [ "$1" = configure ]; then /opt/wallnut/package-setup.sh configure; fi
'@ -Executable
    Write-Unix (Join-Path $debian 'prerm') @'
#!/bin/sh
set -e
case "$1" in remove|deconfigure) /opt/wallnut/package-setup.sh remove ;; esac
'@ -Executable
    Write-Unix (Join-Path $debian 'postrm') @'
#!/bin/sh
set -e
# "apt purge wallnut" also deletes settings and history; "apt remove" keeps them.
if [ "$1" = purge ]; then rm -rf /etc/wallnut /var/lib/wallnut; fi
if [ -d /run/systemd/system ]; then systemctl daemon-reload >/dev/null 2>&1 || true; fi
'@ -Executable

    # xz rather than the newer zstd default, so older Debian and Raspberry Pi OS can read it.
    & dpkg-deb --root-owner-group -Zxz --build $root (Join-Path $Out "$name.deb")
    if ($LASTEXITCODE) { throw 'dpkg-deb failed.' }
    Remove-Item $debian -Recurse -Force
}

function New-RpmPackage([string]$root) {
    Step "Making $name.rpm"
    $arch = @{ 'linux-x64' = 'x86_64'; 'linux-arm64' = 'aarch64' }[$Rid]
    $top = Join-Path $work 'rpmbuild'
    $spec = Join-Path $work 'wallnut.spec'
    # rpmbuild normally strips programs and works out dependencies itself. Stripping breaks .NET
    # single-file programs, so its post-processing is off and dependencies are listed by hand.
    Write-Unix $spec @"
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none
%define __strip /bin/true

Name:        wallnut
Version:     $(Get-PackageVersion)
Release:     1
Summary:     $packageSummary
License:     GPL-3.0-or-later
URL:         https://github.com/Kcarpenter528/WALLNut
AutoReqProv: no
Requires:    glibc, libstdc++, zlib, libicu, openssl-libs
Recommends:  libX11, libICE, libSM, fontconfig, libnotify

%description
$($packageDescription.Trim())

%install
mkdir -p %{buildroot}
cp -a $root/. %{buildroot}/

%files
/opt/wallnut
/usr/lib/systemd/system/wallnut.service
/usr/share/applications/wallnut.desktop
/etc/xdg/autostart/wallnut-tray.desktop

%post
/opt/wallnut/package-setup.sh configure

%preun
# 0 means the package is being removed, not upgraded.
if [ "`$1" -eq 0 ]; then /opt/wallnut/package-setup.sh remove; fi

%postun
if [ -d /run/systemd/system ]; then systemctl daemon-reload >/dev/null 2>&1 || true; fi
"@
    & rpmbuild -bb --quiet --target $arch --define "_topdir $top" --define '_rpmfilename wallnut.rpm' $spec
    if ($LASTEXITCODE) { throw 'rpmbuild failed.' }
    Copy-Item (Join-Path $top 'RPMS' 'wallnut.rpm') (Join-Path $Out "$name.rpm")
}

function New-LinuxPackages {
    $hasDeb = [bool](Get-Command dpkg-deb -ErrorAction SilentlyContinue)
    $hasRpm = [bool](Get-Command rpmbuild -ErrorAction SilentlyContinue)
    if (-not ($hasDeb -or $hasRpm)) { Write-Host '    (dpkg-deb and rpmbuild not found: no .deb or .rpm)'; return }
    $root = New-LinuxPackageRoot
    if ($hasDeb) { New-DebPackage $root } else { Write-Host '    (dpkg-deb not found: no .deb)' }
    if ($hasRpm) { New-RpmPackage $root } else { Write-Host '    (rpmbuild not found: no .rpm)' }
}

# ---- macOS ---------------------------------------------------------------------------

function New-MacPackage {
    if (-not (Get-Command pkgbuild -ErrorAction SilentlyContinue)) { Write-Host '    (not on a Mac: no .pkg)'; return }
    Step "Making $name.pkg"
    $id = 'io.github.kcarpenter528.wallnut'

    # The package unpacks the download to a temporary folder; its postinstall then runs the same
    # install.sh as a manual install, logs to /Library/Logs/WALLNut/install.log, and cleans up.
    $scripts = Join-Path $work 'pkg-scripts'
    New-Item -ItemType Directory -Force $scripts | Out-Null
    Write-Unix (Join-Path $scripts 'postinstall') @'
#!/bin/bash
src="/private/tmp/WALLNut-installer"
mkdir -p /Library/Logs/WALLNut
"$src/install.sh" > /Library/Logs/WALLNut/install.log 2>&1
code=$?
rm -rf "$src"
exit $code
'@ -Executable

    $components = Join-Path $work 'pkg'
    New-Item -ItemType Directory -Force $components | Out-Null
    & pkgbuild --root $stage --identifier $id --version (Get-PackageVersion) `
        --install-location /private/tmp/WALLNut-installer --scripts $scripts --ownership recommended `
        (Join-Path $components 'wallnut-component.pkg')
    if ($LASTEXITCODE) { throw 'pkgbuild failed.' }

    # The installer window: title, license page, and this Mac's CPU only.
    $resources = Join-Path $work 'pkg-resources'
    New-Item -ItemType Directory -Force $resources | Out-Null
    Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $resources 'LICENSE.txt')
    $distribution = Join-Path $work 'distribution.xml'
    $template = Get-Content (Join-Path $repo 'packaging' 'macos' 'distribution.xml') -Raw
    Write-Unix $distribution ($template -replace '__VERSION__', (Get-PackageVersion) `
        -replace '__ARCH__', ($Rid -eq 'osx-arm64' ? 'arm64' : 'x86_64') -replace '__ID__', $id)
    & productbuild --distribution $distribution --resources $resources --package-path $components (Join-Path $Out "$name.pkg")
    if ($LASTEXITCODE) { throw 'productbuild failed.' }
}
