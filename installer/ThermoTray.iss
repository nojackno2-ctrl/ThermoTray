; Inno Setup 安裝檔建置腳本
; 建置步驟：
; 1. 先執行自包含發行發布 (Self-contained Publish)：
;    dotnet publish .\src\ThermoTray\ThermoTray.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
; 2. 呼叫 ISCC 進行編譯：
;    ISCC.exe /DAppVersion=1.1.7 .\installer\ThermoTray.iss

#define AppName "ThermoTray"
; 可由 CI 或命令列參數 /DAppVersion=<version> 覆蓋預設版本號
#ifndef AppVersion
  #define AppVersion "1.1.7"
#endif
#define AppPublisher "ThermoTray"
#define AppURL "https://github.com/nojackno2-ctrl/ThermoTray"
#define AppExeName "ThermoTray.exe"
#define AppUserModelId "nojackno2.ThermoTray"

[Setup]
AppId={{1B57D245-2F65-4E07-B338-CC94F7C7E6CB}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
AppReadmeFile={app}\README.md
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoTextVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
; 安裝至使用者 AppData\Local 目錄，不需系統管理員安裝權限
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=no
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=ThermoTray-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} v{#AppVersion}
Uninstallable=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
CreateUninstallRegKey=yes
SetupLogging=yes
UninstallLogging=yes
; 檢查應用程式產生的全域互斥鎖，避免在 ThermoTray 執行中進行覆蓋安裝
AppMutex=Global\ThermoTray.Setup
; 關閉預設的 Restart Manager，改由 AppMutex 提示使用者結束程式
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 包含發布目錄下的所有檔案（排除 .pdb 偵錯檔）
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; AppUserModelID: "{#AppUserModelId}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; 舊版本 Run 登錄項清理標記
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ThermoTray"; Flags: deletevalue

; 安裝完成後啟動選項。使用 shellexec 觸發 UAC 管理員提權提示
[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch ThermoTray"; Flags: nowait postinstall skipifsilent shellexec
