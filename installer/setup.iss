; Typryt installer (Inno Setup 6).
; Build with:  ISCC.exe installer\setup.iss   (after publishing the app to publish\app with its engine\ folder)
; Installs per user (no administrator rights) into %LOCALAPPDATA%\Programs\Typryt.
; Your settings, learned words, and stats live in %APPDATA%\Typryt and are kept when Typryt is uninstalled.

#define MyAppName "Typryt"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Gokul P"

[Setup]
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
OutputDir=Output
OutputBaseFilename=TyprytSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Typryt.exe
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}.0

[Tasks]
Name: "startup"; Description: "Start Typryt when Windows starts"; GroupDescription: "Options:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\Typryt.exe"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\Typryt.exe"; Tasks: desktopicon

[Registry]
; The same startup entry the Settings switch writes (current user only).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Typryt"; ValueData: """{app}\Typryt.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Typryt.exe"; Description: "Start {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Typryt.exe"; Flags: runhidden; RunOnceId: "StopTyprytApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM typryt-engine.exe"; Flags: runhidden; RunOnceId: "StopTyprytEngine"
