; Typryt installer (Inno Setup 6).
; Build with installer\build_release.ps1 (it builds the engine and the app first), or compile by hand:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\setup.iss   (after publishing the app to publish\app with its engine\ folder)
; Installs per user (no administrator rights) into %LOCALAPPDATA%\Programs\Typryt.
; Settings, learned words and stats live in %APPDATA%\Typryt. They are kept when Typryt is uninstalled, unless the user
; answers Yes to the question the uninstaller asks (a silent uninstall always keeps them).

#define MyAppName "Typryt"
#define MyAppVersion "1.0.2"
#define MyAppPublisher "Gokul P"
#define MyAppExe "Typryt.exe"

[Setup]
; Never change the AppId: it is how an upgrade finds the earlier installation.
AppId={{6F1B2C7E-3A4D-4B8E-9C21-7D5E0A1F3B62}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Iamgokul7/typryt
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
LicenseFile=..\LICENSE.txt
SetupIconFile=..\csharp-app\Assets\typryt.ico
OutputDir=Output
OutputBaseFilename=Typryt-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start Typryt when Windows starts"; GroupDescription: "Options:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; The same startup entry the Settings switch writes (current user only).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Typryt"; ValueData: """{app}\{#MyAppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[InstallDelete]
; An upgrade starts from a clean engine folder, so no file of an earlier engine stays behind.
Type: filesandordirs; Name: "{app}\engine"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Start {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
{ Stops the Typryt app and its engine, and only those that run from the install folder: matched by their executable path,
  never by name alone (the uninstaller itself is left running), so another program (or a copy of Typryt somewhere else) is left alone. }
procedure StopTyprytFrom(const Folder: String);
var
  Script, Params: String;
  ResultCode: Integer;
begin
  Script := '$f=''' + Folder + '\*''; Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like $f -and $_.Name -notlike ''unins*'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }';
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;

function InstallFolderOfEarlierVersion: String;
var
  Key, Value: String;
begin
  Result := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1B2C7E-3A4D-4B8E-9C21-7D5E0A1F3B62}_is1';
  if RegQueryStringValue(HKCU, Key, 'InstallLocation', Value) then
    Result := RemoveBackslashUnlessRoot(Value);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Earlier: String;
begin
  Result := '';
  StopTyprytFrom(ExpandConstant('{app}'));
  Earlier := InstallFolderOfEarlierVersion;
  if (Earlier <> '') and (CompareText(Earlier, ExpandConstant('{app}')) <> 0) then
    StopTyprytFrom(Earlier);
end;

function InitializeUninstall: Boolean;
begin
  StopTyprytFrom(ExpandConstant('{app}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
  Pointed: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { The startup entry the app's own Settings switch writes: removed with the app, if it points into this folder. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Typryt', Pointed) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Pointed)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Typryt');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{userappdata}\Typryt');
    if DirExists(Data) and (not UninstallSilent) then
      if MsgBox('Also delete your settings and personal dictionary?' + #13#10 + #13#10 + Data,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
