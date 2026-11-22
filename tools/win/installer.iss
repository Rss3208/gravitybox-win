; GravityBox for Windows Inno Setup Script (Per-User, No UAC required)
#ifndef AppVersion
  #define AppVersion "1.1.3"
#endif
#ifndef StageDir
  #define StageDir "..\..\build\win-stage\GravityBox"
#endif

[Setup]
AppId={{8F6A2E31-7A4D-4F2B-9A3D-1C5E8F7B2D90}
AppName=GravityBox
AppVersion={#AppVersion}
AppVerName=GravityBox {#AppVersion}
AppPublisher=Rss3208 (ported from Enzoo404/gravitybox)
AppPublisherURL=https://github.com/Rss3208/gravitybox-win
AppSupportURL=https://github.com/Rss3208/gravitybox-win/issues
AppUpdatesURL=https://github.com/Rss3208/gravitybox-win/releases
LicenseFile=..\..\LICENSE
DefaultDirName={localappdata}\Programs\GravityBox
DefaultGroupName=GravityBox
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=..\..\成品
OutputBaseFilename=GravityBox-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\assets\AppIcon.ico
UninstallDisplayIcon={app}\GravityBox.exe
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GravityBox"; Filename: "{app}\GravityBox.exe"
Name: "{autodesktop}\GravityBox"; Filename: "{app}\GravityBox.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GravityBox.exe"; Description: "{cm:LaunchProgram,GravityBox}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\GravityBox.WinHelper.exe"; Parameters: "--restore-shortcuts"; Flags: runhidden; RunOnceId: "RestoreAntigravityShortcuts"
