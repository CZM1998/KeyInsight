#define MyAppName "KeyInsight"
#ifndef MyAppVersion
  #define MyAppVersion "2.1.1"
#endif
#define MyAppPublisher "CZM1998"
#define MyAppURL "https://github.com/CZM1998/KeyInsight"
#define MyAppExeName "KeyInsight.exe"

[Setup]
AppId={{8B2E4C6A-1D3F-4A5B-9C7D-2E4F6A8B0C1D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=KeyInsight-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=yes
AppMutex=KeyInsight_SingleInstance
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "..\KeyInsight.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\KeyInsight"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall KeyInsight"; Filename: "{uninstallexe}"
Name: "{autodesktop}\KeyInsight"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch KeyInsight"; Flags: nowait postinstall skipifsilent
