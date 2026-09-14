#define MyAppName "M-Pesa Message Puller"
#define MyAppVersion "1.4.2"
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
OutputBaseFilename=MPesaSetup-{#MyAppVersion}
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
Name: "setupdatabase"; Description: "Create or update the database schema on the configured SQL Server"; GroupDescription: "Database setup:"; Flags: checkedonce

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MPesaMessagePuller"; ValueData: "{app}\{#MyAppExeName}"; Tasks: startup; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Setup-Database.ps1"" -ServerInstance ""{code:GetDatabaseServer}"" -DatabaseName ""{code:GetDatabaseName}"" -SqlUser ""{code:GetDatabaseUser}"" -SqlPassword ""{code:GetDatabasePassword}"" -AppSettingsPath ""{app}\appsettings.json"" -ConfigureOnly"; StatusMsg: "Saving the database connection..."; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Setup-Database.ps1"" -ServerInstance ""{code:GetDatabaseServer}"" -DatabaseName ""{code:GetDatabaseName}"" -SqlUser ""{code:GetDatabaseUser}"" -SqlPassword ""{code:GetDatabasePassword}"""; Description: "Create or update the configured database"; Tasks: setupdatabase; Flags: postinstall skipifsilent waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DatabasePage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  DatabasePage := CreateInputQueryPage(
    wpSelectDir,
    'Database Connection',
    'Configure the SQL Server that will receive M-Pesa messages',
    'Enter the remote SQL Server details. Use an address such as 192.168.1.20,1433 or SERVERNAME\INSTANCE.');
  DatabasePage.Add('SQL Server / Instance:', False);
  DatabasePage.Add('Database name:', False);
  DatabasePage.Add('SQL login:', False);
  DatabasePage.Add('SQL password:', True);
  DatabasePage.Values[0] := 'Server\MSSQLServer';
  DatabasePage.Values[1] := 'Restaurant';
  DatabasePage.Values[2] := 'sa';
  DatabasePage.Values[3] := '123456';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = DatabasePage.ID then
  begin
    if Trim(DatabasePage.Values[0]) = '' then
    begin
      MsgBox('Enter the SQL Server name, IP address, or IP address and port.', mbError, MB_OK);
      Result := False;
    end
    else if Trim(DatabasePage.Values[1]) = '' then
    begin
      MsgBox('Enter the database name.', mbError, MB_OK);
      Result := False;
    end
    else if Trim(DatabasePage.Values[2]) = '' then
    begin
      MsgBox('Enter the SQL login.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function EscapeCommandLineValue(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '"', '\"', True);
end;

function GetDatabaseServer(Param: String): String;
begin
  Result := EscapeCommandLineValue(DatabasePage.Values[0]);
end;

function GetDatabaseName(Param: String): String;
begin
  Result := EscapeCommandLineValue(DatabasePage.Values[1]);
end;

function GetDatabaseUser(Param: String): String;
begin
  Result := EscapeCommandLineValue(DatabasePage.Values[2]);
end;

function GetDatabasePassword(Param: String): String;
begin
  Result := EscapeCommandLineValue(DatabasePage.Values[3]);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Log('Application diagnostic logs are written to %LOCALAPPDATA%\\MPesa\\logs.');
end;
