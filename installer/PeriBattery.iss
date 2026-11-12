; Per-user installer for PeriBattery. No administrator rights required.
; Build with: ISCC.exe /DAppVersion=1.0.0 installer\PeriBattery.iss   (build.ps1 does this)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{7C0E3B5E-5B0C-4F0B-9E57-6C2B8A4D1F31}
AppName=PeriBattery
AppVersion={#AppVersion}
AppPublisher=PeriBattery
DefaultDirName={localappdata}\Programs\PeriBattery
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=PeriBattery-Setup-{#AppVersion}
SetupIconFile=..\src\PeriBattery\Assets\app.ico
UninstallDisplayIcon={app}\PeriBattery.exe
UninstallDisplayName=PeriBattery
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar PeriBattery con Windows"; GroupDescription: "Opciones:"

[Files]
Source: "..\publish\win-x64\PeriBattery.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The AppUserModelID on the Start menu shortcut is what lets Windows attribute toast notifications to the app.
Name: "{userprograms}\PeriBattery"; Filename: "{app}\PeriBattery.exe"; AppUserModelID: "PeriBattery.App"

[Registry]
; Optional autostart (same value the in-app toggle manages).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PeriBattery"; ValueData: """{app}\PeriBattery.exe"" --autostart"; Tasks: autostart
; Clean uninstall: remove the autostart value and the toast registration even if the app created them itself.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PeriBattery"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\PeriBattery.App"; Flags: dontcreatekey uninsdeletekey

[Run]
Filename: "{app}\PeriBattery.exe"; Description: "Iniciar PeriBattery ahora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM PeriBattery.exe /F"; Flags: runhidden; RunOnceId: "StopPeriBattery"

[UninstallDelete]
; Settings, last known levels and logs.
Type: filesandordirs; Name: "{userappdata}\PeriBattery"

[Code]
// Stop a running instance before its executable is replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM PeriBattery.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
  Result := '';
end;
