# ThermoTray

每張 CPU/GPU 卡片都能分別勾選使用率與溫度是否顯示在系統匣；選項會保存到設定檔，卡片本身仍會保留完整讀值。

[繁體中文](#繁體中文) | [English](#english)

## 繁體中文

ThermoTray 是 Windows 用的輕量 CPU / GPU 即時溫度監測器。它透過 [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 直接讀取硬體感測器，**從不產生或顯示虛構、推算或以舊值偽裝的溫度**。

### 必要條件：PawnIO 核心驅動程式

LibreHardwareMonitor 0.9.6 已改用 [PawnIO](https://pawnio.eu/) 取代 WinRing0 來存取 CPU 暫存器。**未安裝 PawnIO 時，AMD Ryzen 的 `Core (Tctl/Tdie)` 會固定回報 0**，ThermoTray 依規則將其判定為無效值並顯示「無法取得」。NVIDIA GPU 走 NVAPI，不需要此驅動程式，所以會出現「GPU 有溫度、CPU 沒有」的情況。

安裝 [PawnIO](https://pawnio.eu/)（Microsoft 認證簽章的核心驅動程式）後重新啟動 ThermoTray。

### 必要條件二：系統管理員權限

PawnIO 只把裝置開放給**已提權**的處理程序。ThermoTray 的執行檔預設要求系統管理員權限，每次一般啟動都會先顯示 Windows UAC；若拒絕授權，程式不會啟動。GPU 走 NVAPI，本身不受此限制。

「隨 Windows 啟動」會註冊 `RL HIGHEST` 的登入排程工作，登入後自動以系統管理員權限啟動且不重複顯示 UAC。由於 `HKCU\Run` 無法可靠啟動要求提權的程式，ThermoTray 不再使用該方式。

### 功能

- 顯示 CPU 套件 / Tctl-Tdie，以及每張 GPU 各自的 GPU Core 溫度。
- 感測器、權限或驅動程式不支援時顯示「無法取得」，而不是 `0 °C` 或估算值。
- 顯示 CPU 與每張 GPU 的使用率；0% 是合法的閒置狀態，讀不到時顯示「無法取得」。雙 GPU 會分別顯示各自的裝置名稱、使用率與溫度，不會交叉配對。
- 視窗開啟時每秒一次背景取樣，縮到系統匣後改為每兩秒一次（此時只有整數位的托盤圖示可見，減半的取樣率可直接減半閒置時的 CPU 用量）。
- 感測器清單、名稱比對與排序都在硬體出現時算好；每次取樣只讀值並建立目前各 GPU 的小型快照。托盤圖示只在顯示數字改變時重繪，筆刷全程重複使用。
- 關閉 LibreHardwareMonitor 每個感測器預設保留一天的歷史值；ThermoTray 只顯示當下數值，長時間常駐時該歷史會持續佔用記憶體並拖慢每次更新。
- 系統匣使用獨立圖示，分別顯示 CPU 與每張 GPU；每個圖示上方顯示使用率、下方顯示溫度，Tooltip 會標示 GPU 序號與裝置名稱。
- 托盤數字是把字型外框本身縮放到剛好填滿該行，因此無論一位或三位數都完整可見且盡可能大；`100 °C` 不會被裁成 `10`。圖示依通知區當下的實際尺寸繪製，並以兩倍解析度算圖後平均縮小，讓筆畫粗細均勻。
- 沒有可讀取 GPU 感測器的機器（例如純內顯且驅動程式不提供數值）會隱藏 GPU 卡片與托盤圖示，不會永久顯示無法解決的警告。
- 只允許單一執行個體；重複啟動會喚醒既有視窗，而不是產生第二組托盤圖示與第二個硬體輪詢。
- 重複啟動時會先問正在執行的版本：版本相同就直接顯示既有視窗並結束；啟動的是較新版本，會詢問是否關閉舊版並接手；啟動的是較舊版本，則提示並改為顯示執行中的新版視窗。1.1.3 之前的版本不支援自動接手，會提示請先從系統匣結束它。
- 安裝程式會偵測執行中的 ThermoTray 並要求先關閉：Windows 會鎖定執行中的執行檔，未提權的安裝程式也無法結束以系統管理員身分執行的程式。
- 關閉或最小化時隱藏到系統匣，按兩下圖示即可恢復。
- 繁體中文與英文介面，可選擇隨 Windows 啟動。
- Inno Setup 安裝指令碼與 GitHub Actions 建置流程均已包含。

### Visual Studio 建置

1. 安裝 Visual Studio 2022 與「.NET 桌面開發」工作負載，並安裝 .NET 8 SDK。
2. 開啟 `ThermoTray.sln`，還原 NuGet 套件後建置 `Release`。
3. 執行單元測試（涵蓋感測器排序、溫度有效性判定、托盤數字格式化與語言字串）：

```powershell
dotnet test .\ThermoTray.sln -c Release
```

4. 需要可攜的單一執行檔時，執行：

```powershell
dotnet restore .\ThermoTray.sln -r win-x64
dotnet publish .\src\ThermoTray\ThermoTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --no-restore -o .\publish\win-x64
```

版本號的唯一來源是 `Directory.Build.props` 的 `<Version>`；安裝指令碼與 GitHub Actions 都從該處讀取，改版時只需修改一個位置。

### 建立安裝檔

安裝 [Inno Setup](https://jrsoftware.org/isinfo.php)，以 Inno Setup Compiler 開啟 `installer\ThermoTray.iss` 並編譯（命令列可用 `ISCC.exe /DAppVersion=1.1.4 installer\ThermoTray.iss` 指定版本）。它會使用 `publish\win-x64` 的輸出，安裝檔生成於 `artifacts\installer`。安裝精靈安裝到目前使用者的 LocalAppData，本身不需系統管理員權限；啟動 ThermoTray 時才會顯示 UAC。登入自動啟動請在程式內勾選「隨 Windows 啟動」。安裝精靈使用英文；已安裝的 ThermoTray 本身可切換繁體中文與英文。

### 溫度正確性說明

溫度的正確性上限受主機板 BIOS、CPU/GPU 驅動程式與裝置本身提供的感測器影響。ThermoTray 的保證是：只顯示讀到的感測器數值；沒有可靠讀值時不顯示數字。部分筆電、VM、遠端工作階段或沒有驅動程式的 GPU 可能沒有可用數值。

### 安裝後快速驗證

1. 確認 PawnIO 已安裝，然後啟動 ThermoTray 並接受 UAC。
2. 等待一至兩秒，確認 CPU 與 GPU 卡片顯示使用率與溫度；0% 是合法的閒置狀態，沒有可靠讀值時應顯示「無法取得」，不應顯示 `0 °C` 冒充溫度。
3. 確認系統匣中 CPU 圖示位於 GPU 圖示左側。Windows 可能記住使用者手動拖曳過的圖示位置，因此必要時請在通知區重新排列。
4. 測試「關閉時縮小至系統匣」、再次啟動程式喚回視窗，以及「隨 Windows 啟動」。後者應建立 `ThermoTray` 的 `RL HIGHEST` 登入排程工作。

### 疑難排解

- GPU 有數值但 CPU 顯示「無法取得」：先確認 PawnIO 已安裝，再完全結束並重新啟動 ThermoTray；CPU 讀值需要提權處理程序。
- 拒絕 UAC：程式不會啟動，這是直接讀取 CPU 暫存器的必要條件。
- 只有內顯或 GPU 驅動程式沒有提供溫度：GPU 卡片與圖示會在幾次取樣後隱藏，CPU 仍可獨立使用。
- 若要提交問題，請附上 Windows 版本、CPU/GPU 型號、PawnIO 版本，以及是否已接受 UAC；不要用估算值取代缺失的感測器讀值。

## English

ThermoTray is a lightweight Windows CPU/GPU temperature monitor. It reads physical sensors through LibreHardwareMonitor and never invents, estimates, or presents an old reading as a current one.

**Prerequisites for CPU temperature:** LibreHardwareMonitor 0.9.6 reads CPU registers through the [PawnIO](https://pawnio.eu/) kernel driver instead of WinRing0, and PawnIO grants its device to elevated processes only. ThermoTray therefore requests administrator rights in its application manifest and shows Windows UAC on every normal launch; declining the prompt prevents the application from starting. AMD Ryzen otherwise reports `Core (Tctl/Tdie)` as a constant 0, which ThermoTray rejects. NVIDIA GPU readings go through NVAPI and do not require PawnIO. "Start with Windows" registers an `RL HIGHEST` logon task for prompt-free elevated startup; `HKCU\Run` is no longer used.

It samples in the background every second while its window is open and every two seconds once it is hidden in the tray, where only the whole-degree icons are readable. The sensor list, name matching, and ranking are resolved when hardware appears, and each sample keeps temperature and utilization paired to the same physical GPU. It also turns off LibreHardwareMonitor's per-sensor value history, which otherwise keeps a day of samples for every sensor in a process that is meant to run indefinitely. It redraws a tray icon only when its displayed digits change and reuses its brushes for the life of the process. It shows CPU utilization and temperature plus one independent card for every GPU in the main window; the tray has one independent icon for CPU and one for each GPU, with the GPU number and device name in its tooltip. Each icon places utilization above temperature. Tray digits are drawn by scaling the glyph outlines themselves to fill their line, so a one- or three-digit reading is equally complete and as large as the icon allows, and 100 °C can never appear as 10. Each icon is drawn for the notification area's current size at twice the resolution and averaged down, which keeps the strokes even. It hides GPU cards and icons entirely on machines that never report GPU telemetry, and allows only one running instance. A second launch asks the running instance which version it is: an identical version simply raises the existing window, a newer one offers to close the running instance and take over the tray, and an older one says so and raises the newer window instead. Instances older than 1.1.3 cannot hand the tray over, so the launch asks you to exit them from the notification area first, and Setup detects a running ThermoTray through a mutex and asks you to close it — Windows locks a running executable, and an unelevated installer cannot terminate a process running as administrator. It supports English and Traditional Chinese, can hide to the tray, and can start with the current Windows user.

Build it with Visual Studio 2022 / .NET 8 using `ThermoTray.sln` and run `dotnet test .\ThermoTray.sln -c Release` for the unit tests covering sensor ranking, temperature validation, tray formatting, and localization. `<Version>` in `Directory.Build.props` is the single source of the product version; the installer script and CI both read it from there. Packaging instructions are above.

After installation, accept UAC and wait one or two seconds for the first sample. CPU utilization and temperature plus one card per GPU are shown together; 0% is a valid idle reading, while missing data is shown as `Unavailable` and never as `0 °C` for temperature. Each card independently controls whether its utilization and temperature appear in the tray; the card values remain visible, while the icon and tooltip follow those choices. Each tray icon places utilization above temperature, and each GPU tooltip identifies its GPU number and device name. The CPU tray icon is registered to the left of the GPU icons unless Windows has preserved a manually rearranged notification-area order. The startup option creates a `ThermoTray` logon task with `RL HIGHEST` so it can start elevated without another UAC prompt.

For a tagged GitHub release, push a tag matching the version in `Directory.Build.props`, such as `v1.1.4`. The release workflow verifies the tag, publishes the self-contained `win-x64` build, creates the Inno Setup installer, creates a PDB-free portable ZIP, verifies the embedded `requireAdministrator` manifest, and publishes SHA-256 checksums with the release assets.
