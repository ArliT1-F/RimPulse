; Inno Setup 6 script for a real Windows installer. Prepared, not built in CI on Linux.
;
; Compile on Windows after publishing:
;   dotnet publish src/RimPulse.App -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64
;   iscc packaging\windows\installer.iss /DMyAppVersion=0.7.0 /DPublishDir=artifacts\publish\win-x64
;
; No LicenseFile is set because the upstream repository does not publish a LICENSE file yet. Add one
; with a real license decision before distributing an installer.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish\win-x64"
#endif

#define MyAppName "RimPulse"
#define MyAppPublisher "RimPulse contributors"
#define MyAppURL "https://github.com/ArliT1-F/RimPulse"
#define MyAppExeName "RimPulse.exe"

[Setup]
AppId={{0C8602AF-F94B-44F6-BA5C-3E2CAA2C9B01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\packages
OutputBaseFilename=RimPulseSetup-{#MyAppVersion}-win-x64
SetupIconFile={#SourcePath}\rimpulse.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
