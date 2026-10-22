#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppId={{4D9EF4DB-080D-418A-8249-C31D854725BB}
AppName=DbPilot
AppVersion={#AppVersion}
AppPublisher=DbPilot
DefaultDirName={localappdata}\Programs\DbPilot
DefaultGroupName=DbPilot
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=DbPilot-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\DbPilot\Assets\AppIcon.ico
UninstallDisplayIcon={app}\DbPilot.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\DbPilot"; Filename: "{app}\DbPilot.exe"
Name: "{autodesktop}\DbPilot"; Filename: "{app}\DbPilot.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Run]
Filename: "{app}\DbPilot.exe"; Description: "Launch DbPilot"; Flags: nowait postinstall skipifsilent
