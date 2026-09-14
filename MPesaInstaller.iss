#define MyAppName "M-Pesa Message Puller"
#define MyAppVersion "1.1.7"
#define MyAppPublisher "M-Pesa"
#define MyAppExeName "MPesa.exe"

[Setup]
AppId={{5E8A1D3C-8CB4-4A8E-A32B-7B3B1DA8E6D2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\MPesa
DefaultGroupName={#MyAppName}
ArchitecturesInstallIn64BitMode=x64
OutputDir=installer
OutputBaseFilename=MPesaSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=mpesa.ico

[Files]
; Native dependencies such as Microsoft.Data.SqlClient.SNI.dll must be installed beside MPesa.exe.
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "appsettings.json"
Source: "mpesa.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "MPESAscript.sql"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "Setup-Database.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "startup"; Description: "Start M-Pesa Message Puller when Windows starts"; GroupDescription: "Windows startup:"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MPesaMessagePuller"; ValueData: "{app}\{#MyAppExeName}"; Tasks: startup; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Setup-Database.ps1"""; Description: "Create or update the Restaurant database"; Flags: postinstall skipifsilent waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Log('Application diagnostic logs are written to %LOCALAPPDATA%\\MPesa\\logs.');
end;
