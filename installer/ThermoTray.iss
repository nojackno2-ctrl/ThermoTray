; Inno Setup 安裝檔建置腳本
; 建置步驟：
; 1. 先執行自包含發行發布 (Self-contained Publish)：
;    dotnet publish .\src\ThermoTray\ThermoTray.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
; 2. 呼叫 ISCC 進行編譯：
;    ISCC.exe /DAppVersion=1.1.6 .\installer\ThermoTray.iss

#define AppName "ThermoTray"
; 可由 CI 或命令列參數 /DAppVersion=<version> 覆蓋預設版本號
#ifndef AppVersion
  #define AppVersion "1.1.6"
#endif
#define AppPublisher "ThermoTray"
#define AppExeName "ThermoTray.exe"

[Setup]
AppId={{1B57D245-2F65-4E07-B338-CC94F7C7E6CB}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; 安裝至使用者 AppData\Local 目錄，不需系統管理員安裝權限
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
; 檢查應用程式產生的全域互斥鎖，避免在 ThermoTray 執行中進行覆蓋安裝
AppMutex=Global\ThermoTray.Setup
; 關閉預設的 Restart Manager，改由 AppMutex 提示使用者結束程式
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 包含發布目錄下的所有檔案（排除 .pdb 偵錯檔）
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 舊版本 Run 登錄項清理標記
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ThermoTray"; Flags: deletevalue

; 安裝完成後啟動選項。使用 shellexec 觸發 UAC 管理員提權提示
[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch ThermoTray"; Flags: nowait postinstall skipifsilent shellexec
