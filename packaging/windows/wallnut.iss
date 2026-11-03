; WALLNut setup program (Inno Setup 6). Built by packaging/build.ps1:
;   iscc /DAppVersion=1.2.3 /DStageDir=<folder with Service\ and Tray\> /DOutputDir=<dist> wallnut.iss
; The files are copied by setup; install.ps1 -InPlace then registers the service, creates the
; settings file (administrators only) and sets up the tray app, exactly like a scripted install.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif
#ifndef StageDir
  #error Pass /DStageDir=<folder containing Service and Tray>
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{6F1D3A52-8C4B-4E8A-9B57-2D7C0A1E4B10}
AppName=WALLNut
AppVersion={#AppVersion}
AppVerName=WALLNut {#AppVersion}
AppPublisher=WALLNut contributors
AppPublisherURL=https://github.com/Kcarpenter528/WALLNut
AppSupportURL=https://github.com/Kcarpenter528/WALLNut/issues
AppComments=A wall of data for all of your NUTs: UPS monitoring, alerts and safe shutdown.
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\WALLNut
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=WALLNut-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\WallNut.App\Assets\wallnut.ico
UninstallDisplayIcon={app}\Tray\WALLNut.exe
UninstallDisplayName=WALLNut
LicenseFile=..\..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Setup stops the service and tray app itself (PrepareToInstall), so no "close apps" page.
CloseApplications=no
SetupLogging=yes

[Files]
Source: "{#StageDir}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\Tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\config.example.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[InstallDelete]
; Leftovers from older versions are removed with the folders (setup then copies the new files).
Type: filesandordirs; Name: "{app}\Service"
Type: filesandordirs; Name: "{app}\Tray"

[UninstallDelete]
Type: files; Name: "{app}\install.log"

[Run]
Filename: "{app}\Tray\WALLNut.exe"; Parameters: "--page=settings"; Description: "Open WALLNut to add your UPS"; \
  Flags: postinstall nowait skipifsilent runasoriginaluser
; Silent installs (upgrades, IT tools) skip the page above, so start the tray app quietly instead.
Filename: "{app}\Tray\WALLNut.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" -InstallDir ""{app}"" -KeepFiles"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
procedure StopEverything();
var
  Code: Integer;
begin
  // Ignore failures: on a first install there is nothing to stop.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop WALLNut', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM WALLNut.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // Give the service a moment to let go of its files.
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "$s = Get-Service WALLNut -ErrorAction SilentlyContinue; if ($s) { $s.WaitForStatus(''Stopped'', ''00:00:30'') }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopEverything();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Params: String;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Setting up the WALLNut service...';
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\install.ps1') +
            '" -InPlace -InstallDir "' + ExpandConstant('{app}') + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    MsgBox('WALLNut was copied, but setting up the service didn''t finish (code ' + IntToStr(Code) + ').' + #13#10#13#10 +
           'The details are in ' + ExpandConstant('{app}\install.log') + '.', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if DirExists(ExpandConstant('{commonappdata}\WALLNut')) and not UninstallSilent() then
    if MsgBox('Also delete your WALLNut settings and history?' + #13#10#13#10 +
              'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\WALLNut'), True, True, True);
end;
