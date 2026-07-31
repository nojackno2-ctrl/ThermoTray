; Build the self-contained publish output first:
; dotnet publish .\src\ThermoTray\ThermoTray.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64

#define AppName "ThermoTray"
; Overridden by CI with /DAppVersion=<version> read from Directory.Build.props.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "ThermoTray"
#define AppExeName "ThermoTray.exe"

[Setup]
AppId={{1B57D245-2F65-4E07-B338-CC94F7C7E6CB}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=ThermoTray-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ThermoTray"; Flags: deletevalue

; shellexec is required: this installer runs unelevated and ThermoTray's manifest demands
; administrator rights, so a plain CreateProcess launch would fail with ERROR_ELEVATION_REQUIRED.
[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch ThermoTray"; Flags: nowait postinstall skipifsilent shellexec
