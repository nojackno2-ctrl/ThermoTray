# ThermoTray

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

- 顯示 CPU 套件 / Tctl-Tdie 與主要 GPU Core 的實際溫度。
- 感測器、權限或驅動程式不支援時顯示「無法取得」，而不是 `0 °C` 或估算值。
- 每秒一次背景取樣；取樣熱路徑避免建立暫存陣列，托盤圖示也只在顯示數字改變時重繪。
- 系統匣使用兩個獨立圖示，分別顯示 CPU 與 GPU 溫度；每個圖示都有較大的單一數字與帶標籤的提示文字。三位數（100 °C 以上）會自動縮小字級以免被裁切。
- 沒有可讀取 GPU 感測器的機器（例如純內顯且驅動程式不提供數值）會隱藏 GPU 卡片與托盤圖示，不會永久顯示無法解決的警告。
- 只允許單一執行個體；重複啟動會喚醒既有視窗，而不是產生第二組托盤圖示與第二個硬體輪詢。
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
dotnet publish .\src\ThermoTray\ThermoTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\win-x64
```

版本號的唯一來源是 `Directory.Build.props` 的 `<Version>`；安裝指令碼與 GitHub Actions 都從該處讀取，改版時只需修改一個位置。

### 建立安裝檔

安裝 [Inno Setup](https://jrsoftware.org/isinfo.php)，以 Inno Setup Compiler 開啟 `installer\ThermoTray.iss` 並編譯（命令列可用 `ISCC.exe /DAppVersion=1.0.0 installer\ThermoTray.iss` 指定版本）。它會使用 `publish\win-x64` 的輸出，安裝檔生成於 `artifacts\installer`。安裝精靈安裝到目前使用者的 LocalAppData，本身不需系統管理員權限；啟動 ThermoTray 時才會顯示 UAC。登入自動啟動請在程式內勾選「隨 Windows 啟動」。安裝精靈使用英文；已安裝的 ThermoTray 本身可切換繁體中文與英文。

### 溫度正確性說明

溫度的正確性上限受主機板 BIOS、CPU/GPU 驅動程式與裝置本身提供的感測器影響。ThermoTray 的保證是：只顯示讀到的感測器數值；沒有可靠讀值時不顯示數字。部分筆電、VM、遠端工作階段或沒有驅動程式的 GPU 可能沒有可用數值。

## English

ThermoTray is a lightweight Windows CPU/GPU temperature monitor. It reads physical sensors through LibreHardwareMonitor and never invents, estimates, or presents an old reading as a current one.

**Prerequisites for CPU temperature:** LibreHardwareMonitor 0.9.6 reads CPU registers through the [PawnIO](https://pawnio.eu/) kernel driver instead of WinRing0, and PawnIO grants its device to elevated processes only. ThermoTray therefore requests administrator rights in its application manifest and shows Windows UAC on every normal launch; declining the prompt prevents the application from starting. AMD Ryzen otherwise reports `Core (Tctl/Tdie)` as a constant 0, which ThermoTray rejects. NVIDIA GPU readings go through NVAPI and do not require PawnIO. "Start with Windows" registers an `RL HIGHEST` logon task for prompt-free elevated startup; `HKCU\Run` is no longer used.

It samples in the background every second, avoids temporary arrays in the sampling hot path, and redraws a tray icon only when its displayed digits change. It shows CPU and GPU values in separate tray icons and labelled tooltips, shrinks the icon font so three-digit readings are not clipped, hides the GPU card and icon entirely on machines that never report a GPU temperature, and allows only one running instance (a second launch raises the existing window). It supports English and Traditional Chinese, can hide to the tray, and can start with the current Windows user.

Build it with Visual Studio 2022 / .NET 8 using `ThermoTray.sln` and run `dotnet test .\ThermoTray.sln -c Release` for the unit tests covering sensor ranking, temperature validation, tray formatting, and localization. `<Version>` in `Directory.Build.props` is the single source of the product version; the installer script and CI both read it from there. Packaging instructions are above.
