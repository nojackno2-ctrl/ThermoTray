# ThermoTray 🌡️

**輕量、精準、絕不造假的 Windows 系統匣 CPU / GPU 溫度與使用率即時監測工具**

[![Release](https://img.shields.io/github/v/release/nojackno2-ctrl/ThermoTray?color=blue&label=最新版本)](https://github.com/nojackno2-ctrl/ThermoTray/releases)
[![Platform](https://img.shields.io/badge/平台-Windows%2010%20%7C%2011%20(x64)-0078D6?logo=windows)](https://github.com/nojackno2-ctrl/ThermoTray)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/授權-MIT-green)](https://github.com/nojackno2-ctrl/ThermoTray)

[繁體中文](#繁體中文) | [English](#english)

---

## 繁體中文

### 📖 簡介

**ThermoTray** 是一款專為 Windows 設計的輕量級 CPU 與 GPU 即時監控工具，常駐於系統工作列（通知區 / 系統匣）。

本專案的核心承諾：**只呈現真實硬體物理感測器讀值，絕不產生、推算、插值或以快取舊值偽裝溫度與負載**。當感測器、驅動程式或系統權限不足時，ThermoTray 會誠實回報「無法取得」，絕不以虛構的 `0 °C` 誤導使用者。

---

### ✨ 核心特色

- 📊 **獨立雙行系統匣圖示**
  - 為 CPU 以及每張實體 GPU 提供各自獨立的通知區圖示。
  - 每個圖示**上方顯示使用率 (%)**、**下方顯示溫度 (°C)**。
  - 滑鼠懸停（Tooltip）清晰標示 GPU 編號、裝置型號、使用率與溫度。
- 🎯 **自適應向量外框字型渲染**
  - 採用字型向量外框（Glyph Outlines）自適應縮放技術，數字自動填滿圖示可用區域。
  - 完整支援 1 至 3 位數數值（例如 `100`），文字絕不會遭到裁切。
  - 採用 2x 超高採樣（Supersampling）高解析度抗鋸齒算圖，微小圖示依然清晰銳利。
- 💻 **多 GPU / 雙顯卡獨立支援**
  - 完美支援筆記型電腦與多顯卡環境（例如 AMD 內顯 + NVIDIA 獨顯）。
  - 溫度與使用率嚴格依實體硬體配對，絕不跨裝置錯配數據。
  - 智慧狀態列提示：僅在所有 GPU 均無讀值時才提醒，不干擾正常運作的顯卡。
- 🎛️ **自由自訂系統匣顯示項目**
  - 主視窗每張 CPU / GPU 卡片皆可個別勾選使用率與溫度是否顯示於系統匣。
  - 設定自動持久化儲存，卡片本身持續保留完整即時讀值。
- ⚡ **極致輕量與節能優化**
  - **智慧取樣率**：主視窗開啟時每 1 秒取樣一次；縮小至系統匣常駐時自動降為每 2 秒一次，進一步降低 CPU 與筆電電池消耗。
  - **超低記憶體佔用**：停用底層 LibreHardwareMonitor 預設的 1 天歷史紀錄快取，杜絕常駐長時間運作下的記憶體膨脹。
  - **低資源繪製**：感測器拓撲快取、全域重複使用 GDI+ 筆刷與字型控制代碼，僅在顯示整數變化時重繪圖示。
- 🔄 **智慧單一執行個體與平滑交接**
  - 透過具名管道（Named Pipes）實現版本感知協商。
  - 重複啟動時自動喚醒既有視窗至前景；若啟動較新版本，會提示一鍵關閉舊版並無縫接手系統匣。
- 🛡️ **開機自啟動與管理員權限**
  - 採用 Windows 工作排程器（`RL HIGHEST`）登入觸發機制，開機自動提權啟動且**不跳出 UAC 提示**。
  - 針對筆電深度最佳化：拔除電源改用電池、長時間待機皆穩定常駐，不受 Windows 預設排程策略終止。
- 🌐 **雙語介面**
  - 完整支援繁體中文（預設）與英文（English），介面可隨時即時切換。

---

### ⚠️ 重要先決條件

#### 1. PawnIO 核心驅動程式（CPU 溫度必要條件）

LibreHardwareMonitor 0.9.6 已全面改用微軟認證簽章的 [PawnIO](https://pawnio.eu/) 核心驅動程式存取 CPU 暫存器（取代有安全漏洞風險的 WinRing0）。

> [!IMPORTANT]
> **未安裝 PawnIO 時，AMD Ryzen 的 `Core (Tctl/Tdie)` 等感測器將固定回報 0**。ThermoTray 依原則判定為無效值並顯示「無法取得」。
> NVIDIA GPU 走 NVAPI 通道，不需此驅動程式，因此會出現「GPU 有溫度、CPU 沒有」的現象。

👉 **解決方法**：請前往 [PawnIO 官方網站](https://pawnio.eu/) 下載並安裝驅動程式，完成後重新啟動 ThermoTray 即可。

#### 2. 系統管理員權限（Administrator Privileges）

PawnIO 基於安全性僅允許已提權（Elevated）的處理程序存取核心裝置。ThermoTray 執行檔預設要求系統管理員權限：
- **手動啟動**：每次啟動會顯示 Windows UAC 提示，請點選「是」；若拒絕授權，程式將無法讀取 CPU 暫存器。
- **開機自啟動**：在主視窗勾選「隨 Windows 啟動」，程式會自動建立最高權限登入排程工作，開機登入後自動以管理員權限啟動，**完全不會重複跳出 UAC 提示**。

---

### 📥 下載與安裝

請至 [GitHub Releases 最新發布頁面](https://github.com/nojackno2-ctrl/ThermoTray/releases/latest) 下載：

| 檔案類型 | 檔案名稱 | 說明 |
| :--- | :--- | :--- |
| **安裝版** | `ThermoTray-Setup-x.x.x.exe` | Inno Setup 安裝精靈，安裝至使用者的 LocalAppData，支援覆蓋升級與執行狀態偵測；固定建立開始功能表的啟動／解除安裝捷徑，可自行釘選到開始或工作列，桌面捷徑則為預設不勾選的選用項目。 |
| **可攜版** | `ThermoTray-x.x.x-win-x64-portable.zip` | 免安裝綠色壓縮檔，解壓縮至任意目錄即可直接執行 `ThermoTray.exe`。 |

---

### 🚀 快速上手與操作指引

1. **啟動程式**：
   - 執行 `ThermoTray.exe`，在 UAC 提示視窗中點選「是」。
   - 等待 1～2 秒完成初次硬體感測器探測，CPU 與 GPU 卡片將顯示即時使用率與溫度。
2. **系統匣圖示操作**：
   - **按兩下圖示** 或 **按一下圖示**：喚醒主視窗。
   - **右鍵點擊圖示**：開啟快顯功能表，可選擇「開啟 ThermoTray」或「結束」。
   - **滑鼠停留（Hover）**：顯示 Tooltip 浮動提示，包含裝置型號、目前使用率與溫度。
3. **主視窗設定項目**：
   - **顯示在工具列**（卡片內核取方塊）：個別控制是否在系統匣圖示上繪製該項目的數值。
   - **隨 Windows 啟動**：註冊開機免 UAC 的最高權限登入工作排程。
   - **關閉時縮小至系統匣**：點擊主視窗右上角關閉鈕 `X` 時，不結束程式，改為隱藏至系統匣。
   - **語言切換**：提供「繁體中文」與「English」即時切換。

---

### 🔍 常見問題與疑難排解 (FAQ)

<details>
<summary><b>Q1. 為什麼 GPU 有數值，但 CPU 顯示「無法取得」？</b></summary>

1. **未安裝 PawnIO 驅動**：請至 [PawnIO 官網](https://pawnio.eu/) 下載並安裝核心驅動程式。
2. **未取得系統管理員權限**：啟動時請確認接受 Windows UAC 提權要求。
3. 安裝 PawnIO 後，請將 ThermoTray 完全結束並重新啟動。
</details>

<details>
<summary><b>Q2. 為什麼某張 GPU（如 CPU 內顯）顯示「無法取得」？</b></summary>

部分筆電的內建顯示卡（iGPU）或虛擬機在驅動層面未向作業系統開放溫度感測介面。ThermoTray 堅持不偽造數值，因此會顯示「無法取得」；若整張卡片皆無可用感測器，數秒後卡片與托盤圖示會自動隱藏，不影響獨顯與 CPU 的監控。
</details>

<details>
<summary><b>Q3. 筆記型電腦拔掉充電線改用電池時，程式會消失嗎？</b></summary>

不會。ThermoTray 1.1.2 起採用了完整的排程工作設定，已關閉 Windows 預設的「使用電池時停止」、「閒置逾時終止」與「72 小時強制停止」限制，在筆電全天候運作下均可穩定常駐。
</details>

<details>
<summary><b>Q4. 如何調整系統匣圖示的左右排列順序？</b></summary>

ThermoTray 預設在註冊時將 CPU 圖示置於左側、GPU 置於右側。但 Windows 通知區允許使用者手動拖曳自訂圖示順序，若 Windows 已記憶您過去的拖曳偏好，您可以直接在工作列通知區中按住滑鼠左鍵拖曳圖示至理想位置。
</details>

---

### 🛠️ 開發與建置

#### 環境需求

- Windows 10 / 11 (x64)
- .NET 8.0 SDK 或更新版本
- Visual Studio 2022（包含「.NET 桌面開發」工作負載）
- [Inno Setup 6](https://jrsoftware.org/isinfo.php)（如需編譯安裝檔）

#### 建置步驟

```powershell
# 1. 複製儲存庫
git clone https://github.com/nojackno2-ctrl/ThermoTray.git
cd ThermoTray

# 2. 執行單元測試（涵蓋感測器排序、有效性判定、托盤排版與語系字串）
dotnet test .\ThermoTray.sln -c Release

# 3. 發布 win-x64 單一獨立執行檔
dotnet restore .\ThermoTray.sln -r win-x64
dotnet publish .\src\ThermoTray\ThermoTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --no-restore -o .\publish\win-x64

# 4. 編譯 Inno Setup 安裝檔 (輸出至 artifacts\installer\)
& "C:\Users\$env:USERNAME\AppData\Local\Programs\Inno Setup 6\ISCC.exe" /DAppVersion=1.1.7 .\installer\ThermoTray.iss

# 或一鍵執行標準發行封裝與驗證腳本：
.\scripts\package.ps1
```

> **版本號管理**：全專案的版本號單一來源為 `Directory.Build.props` 中的 `<Version>`，安裝指令碼與 GitHub Actions 自動建置流程皆自動讀取此處。

---

## English

### 📖 Overview

**ThermoTray** is a lightweight, accurate, real-time CPU and GPU temperature and utilization monitor for the Windows notification area (system tray).

Our core philosophy: **Strictly display physical hardware telemetry. Never fabricate, estimate, interpolate, or disguise cached historical values as current readings.** When hardware, drivers, or privileges do not support a sensor, ThermoTray reports `Unavailable` instead of misleading users with `0 °C`.

---

### ✨ Features

- 📊 **Independent Dual-Line System Tray Icons**
  - Dedicated notification-area icons for the CPU and each physical GPU.
  - **Upper line: Utilization (%)** | **Lower line: Temperature (°C)**.
  - Informative tooltips displaying device name, GPU index, utilization, and temperature.
- 🎯 **Adaptive Vector Outline Font Rendering**
  - Font glyph outlines scale dynamically to fill available icon bounds.
  - Full support for 1 to 3 digit readings (e.g., `100`), ensuring numbers are never clipped.
  - Rendered at 2x resolution with supersampling and averaged down for crisp, even strokes.
- 💻 **Multi-GPU / Hybrid Graphics Support**
  - Designed for dual-GPU laptops (e.g., AMD Radeon Graphics + NVIDIA GeForce RTX) and multi-GPU desktops.
  - Temperature and load remain strictly paired to their respective physical devices.
  - Smart status bar: warns only when all GPUs lack readings, avoiding false alarms when an integrated GPU lacks a sensor.
- 🎛️ **Granular Tray Customization**
  - Toggle tray visibility for utilization and temperature independently on every CPU/GPU card.
  - Settings persist automatically while main cards retain complete live readings.
- ⚡ **Ultra-Low Resource Footprint**
  - **Dynamic Sampling**: 1-second interval when the main window is open; automatically switches to 2 seconds when minimized to the tray to conserve CPU cycles and battery life.
  - **Zero Sensor History**: Disabled LibreHardwareMonitor's 1-day value history buffer to prevent long-term memory growth.
  - **Efficient Redraws**: Topology caching, reused GDI+ brushes and font handles, redrawing tray icons only when integer values change.
- 🔄 **Smart Single-Instance & Smooth Handover**
  - Named-pipe IPC version negotiation.
  - Re-launching brings the running window to the foreground. Launching a newer build offers a seamless one-click takeover.
- 🛡️ **UAC & Elevated Startup**
  - Autostart uses a Windows Task Scheduler `RL HIGHEST` logon task, launching elevated **without repeated UAC prompts**.
  - Optimized for laptops: survives AC-to-battery transitions and long uptimes without Task Scheduler timeouts.
- 🌐 **Bilingual Support**
  - Traditional Chinese (繁體中文) and English (en-US) with instant runtime switching.

---

### ⚠️ Prerequisites

#### 1. PawnIO Kernel Driver (Required for CPU Temperature)

LibreHardwareMonitor 0.9.6 accesses CPU registers via the Microsoft-attested [PawnIO](https://pawnio.eu/) driver (replacing the vulnerable WinRing0 driver).

> [!IMPORTANT]
> **Without PawnIO, AMD Ryzen sensors like `Core (Tctl/Tdie)` report 0**. ThermoTray rejects zero as invalid and displays `Unavailable`.
> NVIDIA GPUs use NVAPI directly in user mode and do not require PawnIO.

👉 **Fix**: Download and install the driver from the [official PawnIO website](https://pawnio.eu/), then restart ThermoTray.

#### 2. Administrator Privileges

PawnIO requires elevated process privileges to access hardware registers.
- Normal launches will prompt for Windows UAC; accept the prompt to permit sensor reading.
- Enabling "Start with Windows" registers a highest-privilege logon task that runs silently without prompting for UAC on each boot.

---

### 📥 Download & Installation

Visit the [GitHub Releases Page](https://github.com/nojackno2-ctrl/ThermoTray/releases/latest):

- **Setup Installer (`ThermoTray-Setup-x.x.x.exe`)**: Installs to `%LOCALAPPDATA%`, checks for running instances, supports clean upgrades, and always creates Start Menu launch/uninstall shortcuts that users can pin manually. The optional desktop shortcut is off by default.
- **Portable ZIP (`ThermoTray-x.x.x-win-x64-portable.zip`)**: Extract anywhere and launch `ThermoTray.exe`.

---

### 🛠️ Build from Source

```powershell
# Run unit tests
dotnet test .\ThermoTray.sln -c Release

# Publish self-contained single-file binary
dotnet restore .\ThermoTray.sln -r win-x64
dotnet publish .\src\ThermoTray\ThermoTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --no-restore -o .\publish\win-x64

# Or run the unified packaging & verification script:
.\scripts\package.ps1
```

---

### 📄 License & Acknowledgements

- **License**: MIT License
- **Telemetry Engine**: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- **Kernel Driver**: [PawnIO](https://pawnio.eu/)
- **Installer Framework**: [Inno Setup](https://jrsoftware.org/isinfo.php)
