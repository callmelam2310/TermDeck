; Inno Setup script for TermDeck. Built by build-installer.ps1, which passes:
;   /DAppVersion=x.y.z   /DSourceDir=<publish folder>   /DOutputDir=<dist folder>

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\installer-staging"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "TermDeck"
#define AppExe "TermDeck.exe"

[Setup]
; Keep AppId stable across versions so upgrades replace the existing install.
AppId={{7C2E4B1A-3D5F-4E8A-9B61-2F0C8D4A7E93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=TermDeck
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Ask "install for all users (admin) or just me (no admin)".
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=TermDeck-Setup-{#AppVersion}
SetupIconFile=..\src\TermDeck\Assets\TermDeck.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Close a running TermDeck before replacing files.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The staging folder is a plain publish (no portable.txt), so the installed app keeps its
; settings in %APPDATA%\TermDeck and run history inside each project folder.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; User data (%APPDATA%\TermDeck, %LOCALAPPDATA%\TermDeck\webview and each project's .termdeck folder)
; is intentionally left in place on uninstall.
