; Universal TrackIR installer (Inno Setup 6).
;
; Installs for the current user only, so it never asks for admin and the app can update itself in place.
; Built by GitHub Actions:  ISCC /DAppVersion=0.2.0 /DSourceExe=..\out\UniversalTrackIR.exe installer\UniversalTrackIR.iss
;
; The in-app Update button downloads the new installer and runs it with /VERYSILENT. Setup closes the
; running app, swaps the files and starts it again. Settings in %APPDATA%\UniversalTrackIR are never touched.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\out\UniversalTrackIR.exe"
#endif

#define AppName "Universal TrackIR"
#define AppExe "UniversalTrackIR.exe"

[Setup]
; Never change AppId: it's how setup finds an existing install to upgrade.
AppId={{6D47020A-C538-470B-AC8B-79B87EAC7D03}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=pikkon38
AppPublisherURL=https://github.com/pikkon38/UniversalTrackIR
AppUpdatesURL=https://github.com/pikkon38/UniversalTrackIR/releases
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\UniversalTrackIR\app
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\UniversalTrackIR.ico
WizardStyle=modern
OutputDir=..\out
OutputBaseFilename=UniversalTrackIR-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
; Marks this copy as installed, so the app updates through setup instead of swapping its exe.
Source: "installed.marker"; DestDir: "{app}"; DestName: ".installed"; Flags: ignoreversion

[InstallDelete]
; Leftovers from Update.bat builds, which install a folder of files into the same place.
Type: files; Name: "{app}\UniversalTrackIR.dll"
Type: files; Name: "{app}\UniversalTrackIR.deps.json"
Type: files; Name: "{app}\UniversalTrackIR.runtimeconfig.json"
Type: files; Name: "{app}\*.old"

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Normal install: offer to launch. Silent install (in-app update): always relaunch.
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
; The TrackIR/FreeTrack client DLLs the app unpacks for games.
Type: filesandordirs; Name: "{localappdata}\UniversalTrackIR\clients"

[Code]
// On uninstall, stop pointing games at client DLLs that are about to be deleted.
// Only clears the entries if they still point at our folder (real TrackIR may have taken them back).
procedure ClearIfOurs(const SubKey: String);
var
  Value: String;
begin
  if RegQueryStringValue(HKCU, SubKey, 'Path', Value) then
    if Pos('universaltrackir', Lowercase(Value)) > 0 then
      RegDeleteValue(HKCU, SubKey, 'Path');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    ClearIfOurs('Software\NaturalPoint\NATURALPOINT\NPClient Location');
    ClearIfOurs('Software\Freetrack\FreeTrackClient');
  end;
end;
