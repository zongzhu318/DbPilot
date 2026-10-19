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
AppName=DbStudio
AppVersion={#AppVersion}
AppPublisher=DbStudio
DefaultDirName={localappdata}\Programs\DbStudio
DefaultGroupName=DbStudio
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=DbStudio-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\DbStudio\Assets\logo.ico
UninstallDisplayIcon={app}\DbStudio.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\DbStudio"; Filename: "{app}\DbStudio.exe"
Name: "{autodesktop}\DbStudio"; Filename: "{app}\DbStudio.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Run]
Filename: "{app}\DbStudio.exe"; Description: "Launch DbStudio"; Flags: nowait postinstall skipifsilent
