; Inno Setup script for AV1 Studio (https://jrsoftware.org/isinfo.php)
; Build the app first (.\build.ps1), then compile this script with ISCC:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\AV1Studio.iss
; Output: installer\Output\AV1Studio-Setup-<version>.exe

#define AppName "AV1 Studio"
#define AppVersion "1.0.0"
#define AppPublisher "AV1 Studio contributors"
#define AppExe "AV1Studio.exe"
#define AppId "AV1Studio.AV1Studio"

[Setup]
AppId={{E181FDE2-B3F2-4387-B5DE-3B7445700219}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=AV1Studio-Setup-{#AppVersion}
SetupIconFile=..\src\AV1Studio\Assets\av1studio.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=..\LICENSE
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; AppUserModelID matches the one the app sets at runtime, so pinned shortcuts and the running window group together.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppId}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Settings, queue, logs and downloaded tools in %LOCALAPPDATA%\AV1 Studio are intentionally kept on
; uninstall (they may contain your queue). Delete that folder manually for a complete removal.
