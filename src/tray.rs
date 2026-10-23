//! 系統匣圖示：為 CPU 與每張 GPU 各自繪製雙行（使用率在上、溫度在下）的數字圖示。

use windows::core::{w, Interface, Result, PCWSTR};
use windows::Win32::Foundation::{HWND, POINT};
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_COLOR_F, D2D1_FILL_MODE_WINDING, D2D1_PIXEL_FORMAT,
};
use windows::Win32::Graphics::Direct2D::{
    ID2D1Factory, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE, D2D1_FEATURE_LEVEL_DEFAULT, D2D1_RENDER_TARGET_PROPERTIES,
    D2D1_RENDER_TARGET_TYPE_SOFTWARE, D2D1_RENDER_TARGET_USAGE_NONE,
};
use windows::Win32::Graphics::DirectWrite::{
    IDWriteFactory, IDWriteFontFace, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_WEIGHT_BOLD,
    DWRITE_GLYPH_METRICS,
};
use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;
use windows::Win32::Graphics::Gdi::{
    CreateBitmap, CreateDIBSection, DeleteObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HBITMAP,
};
use windows::Win32::Graphics::Imaging::{
    GUID_WICPixelFormat32bppBGRA, GUID_WICPixelFormat32bppPBGRA, IWICImagingFactory, WICBitmapCacheOnDemand,
    WICBitmapDitherTypeNone, WICBitmapInterpolationModeHighQualityCubic, WICBitmapPaletteTypeCustom,
};
use windows::Win32::UI::HiDpi::{GetDpiForSystem, GetSystemMetricsForDpi};
use windows::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_MODIFY, NIM_SETVERSION,
    NOTIFYICONDATAW, NOTIFYICON_VERSION_4,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreateIconIndirect, CreatePopupMenu, DestroyIcon, DestroyMenu, PostMessageW, SetForegroundWindow,
    TrackPopupMenuEx, HICON, ICONINFO, MF_SEPARATOR, MF_STRING, SM_CXSMICON, TPM_BOTTOMALIGN, TPM_RETURNCMD,
    TPM_RIGHTBUTTON, WM_NULL,
};
use windows_numerics::Matrix3x2;

use crate::model::MainState;
use crate::win::copy_to_wide_buffer;

/// Windows 回報不合理的系統匣圖示尺寸時採用的最小尺寸（像素）。
pub const MINIMUM_ICON_SIZE: u32 = 16;

/// 超取樣倍率：先以兩倍尺寸繪製再縮小，改善 16 px 小圖示的字形平滑度。
pub const SUPERSAMPLE_FACTOR: u32 = 2;

/// 字形外框的 Em 大小；外框會再縮放以填滿每一行。
const OUTLINE_EM_SIZE: f32 = 64.0;

const MAXIMUM_TOOLTIP_LENGTH: usize = 127;

pub const MENU_OPEN: u32 = 1;
pub const MENU_EXIT: u32 = 2;

const CPU_ICON_ID: u32 = 1;

/// 使用率數字（上方）一律為白色；溫度數字依 CPU / GPU 使用不同強調色。
const USAGE_COLOR: D2D1_COLOR_F = rgb(245, 247, 250);
pub const CPU_COLOR: D2D1_COLOR_F = rgb(85, 214, 190);
const GPU_COLOR: D2D1_COLOR_F = rgb(116, 176, 255);

const fn rgb(red: u8, green: u8, blue: u8) -> D2D1_COLOR_F {
    D2D1_COLOR_F { r: red as f32 / 255.0, g: green as f32 / 255.0, b: blue as f32 / 255.0, a: 1.0 }
}

/// 一行文字的繪製區域。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LineRect {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

/// 超取樣後的畫布尺寸。
pub fn canvas_size(icon_size: u32) -> u32 {
    icon_size * SUPERSAMPLE_FACTOR
}

/// 將畫布分割為上方（使用率）與下方（溫度）兩行，兩行都在畫布內且互不重疊。
pub fn lines(canvas_size: u32) -> (LineRect, LineRect) {
    let canvas = canvas_size as f32;
    let margin = (canvas / 64.0).max(1.0);
    let line_height = (canvas - margin) / 2.0;
    let line_width = canvas - 2.0 * margin;
    (
        LineRect { x: margin, y: 0.0, width: line_width, height: line_height },
        LineRect { x: margin, y: canvas - line_height, width: line_width, height: line_height },
    )
}

/// 系統匣圖示的目標像素尺寸。
pub fn tray_icon_size() -> u32 {
    let side = unsafe { GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem()) };
    (side.max(0) as u32).max(MINIMUM_ICON_SIZE)
}

/// 組合提示文字，超過 Windows 上限時截斷。
pub fn build_tooltip(device_name: &str, usage: Option<(&str, &str)>, temperature: Option<(&str, &str)>) -> String {
    let mut tooltip = if device_name.trim().is_empty() { "CPU".to_string() } else { device_name.to_string() };
    if let Some((label, value)) = usage {
        tooltip.push_str(&format!(" | {label}: {value}"));
    }
    if let Some((label, value)) = temperature {
        tooltip.push_str(&format!(" | {label}: {value}"));
    }

    trim_tooltip(&tooltip)
}

fn trim_tooltip(tooltip: &str) -> String {
    let units: Vec<u16> = tooltip.encode_utf16().collect();
    if units.len() <= MAXIMUM_TOOLTIP_LENGTH {
        return tooltip.to_string();
    }

    let mut kept = String::from_utf16_lossy(&units[..MAXIMUM_TOOLTIP_LENGTH - 3]);
    // 避免在代理字組中間截斷
    kept = kept.trim_end_matches('\u{FFFD}').to_string();
    kept.push_str("...");
    kept
}

/// 以 Direct2D 將數字字形外框繪製成 HICON。
pub struct IconRenderer {
    d2d: ID2D1Factory,
    wic: IWICImagingFactory,
    font: IDWriteFontFace,
}

impl IconRenderer {
    pub fn new(d2d: ID2D1Factory, dwrite: &IDWriteFactory, wic: IWICImagingFactory) -> Result<Self> {
        let font = unsafe {
            let mut collection = None;
            dwrite.GetSystemFontCollection(&mut collection, false)?;
            let collection = collection.ok_or_else(windows::core::Error::empty)?;
            let mut index = 0;
            let mut exists = windows::core::BOOL(0);
            collection.FindFamilyName(w!("Segoe UI"), &mut index, &mut exists)?;
            if !exists.as_bool() {
                index = 0;
            }
            collection
                .GetFontFamily(index)?
                .GetFirstMatchingFont(DWRITE_FONT_WEIGHT_BOLD, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL)?
                .CreateFontFace()?
        };

        Ok(Self { d2d, wic, font })
    }

    /// 繪製圖示並傳回 HICON；呼叫者負責以 DestroyIcon 釋放。
    pub fn render(&self, usage: &str, temperature: &str, color: D2D1_COLOR_F, icon_size: u32) -> Result<HICON> {
        let pixels = self.render_pixels(usage, temperature, color, icon_size)?;
        unsafe { create_icon(&pixels, icon_size) }
    }

    /// 繪製圖示並傳回由上而下排列的 32 bpp BGRA（非預乘）像素。
    pub fn render_pixels(
        &self,
        usage: &str,
        temperature: &str,
        color: D2D1_COLOR_F,
        icon_size: u32,
    ) -> Result<Vec<u8>> {
        let canvas = canvas_size(icon_size);
        let (usage_line, temperature_line) = lines(canvas);

        unsafe {
            let bitmap =
                self.wic.CreateBitmap(canvas, canvas, &GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnDemand)?;
            let properties = D2D1_RENDER_TARGET_PROPERTIES {
                r#type: D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                pixelFormat: D2D1_PIXEL_FORMAT {
                    format: DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX: 96.0,
                dpiY: 96.0,
                usage: D2D1_RENDER_TARGET_USAGE_NONE,
                minLevel: D2D1_FEATURE_LEVEL_DEFAULT,
            };
            let target = self.d2d.CreateWicBitmapRenderTarget(&bitmap, &properties)?;
            target.SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            target.BeginDraw();
            target.Clear(Some(&D2D1_COLOR_F { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }));
            self.draw_digits(&target, usage, USAGE_COLOR, usage_line)?;
            self.draw_digits(&target, temperature, color, temperature_line)?;
            target.EndDraw(None, None)?;

            // 以高品質三次插值縮小至最終尺寸，並轉為非預乘 Alpha 供 CreateIconIndirect 使用
            let scaler = self.wic.CreateBitmapScaler()?;
            scaler.Initialize(&bitmap, icon_size, icon_size, WICBitmapInterpolationModeHighQualityCubic)?;
            let converter = self.wic.CreateFormatConverter()?;
            converter.Initialize(
                &scaler,
                &GUID_WICPixelFormat32bppBGRA,
                WICBitmapDitherTypeNone,
                None,
                0.0,
                WICBitmapPaletteTypeCustom,
            )?;
            let stride = icon_size * 4;
            let mut pixels = vec![0u8; (stride * icon_size) as usize];
            converter.CopyPixels(std::ptr::null(), stride, &mut pixels)?;
            Ok(pixels)
        }
    }

    /// 縮放字形外框使其置中並完整填入行矩形，防止數字超出邊界或被裁切。
    unsafe fn draw_digits(
        &self,
        target: &windows::Win32::Graphics::Direct2D::ID2D1RenderTarget,
        digits: &str,
        color: D2D1_COLOR_F,
        line: LineRect,
    ) -> Result<()> {
        if digits.is_empty() {
            return Ok(());
        }

        let codepoints: Vec<u32> = digits.chars().map(u32::from).collect();
        let mut glyphs = vec![0u16; codepoints.len()];
        self.font.GetGlyphIndices(codepoints.as_ptr(), codepoints.len() as u32, glyphs.as_mut_ptr())?;

        let mut metrics = vec![DWRITE_GLYPH_METRICS::default(); glyphs.len()];
        self.font.GetDesignGlyphMetrics(glyphs.as_ptr(), glyphs.len() as u32, metrics.as_mut_ptr(), false)?;
        let mut font_metrics = Default::default();
        self.font.GetMetrics(&mut font_metrics);
        let units_per_em = f32::from(font_metrics.designUnitsPerEm.max(1));
        let advances: Vec<f32> =
            metrics.iter().map(|metric| metric.advanceWidth as f32 * OUTLINE_EM_SIZE / units_per_em).collect();

        let path = self.d2d.CreatePathGeometry()?;
        let sink = path.Open()?;
        sink.SetFillMode(D2D1_FILL_MODE_WINDING);
        self.font.GetGlyphRunOutline(
            OUTLINE_EM_SIZE,
            glyphs.as_ptr(),
            Some(advances.as_ptr()),
            None,
            glyphs.len() as u32,
            false,
            false,
            &sink.cast::<windows::Win32::Graphics::Direct2D::Common::ID2D1SimplifiedGeometrySink>()?,
        )?;
        sink.Close()?;

        let ink = path.GetBounds(None)?;
        let ink_width = ink.right - ink.left;
        let ink_height = ink.bottom - ink.top;
        if ink_width <= 0.0 || ink_height <= 0.0 {
            return Ok(());
        }

        let scale = (line.width / ink_width).min(line.height / ink_height);
        let transform = Matrix3x2::translation(-(ink.left + ink_width / 2.0), -(ink.top + ink_height / 2.0))
            * Matrix3x2 { M11: scale, M12: 0.0, M21: 0.0, M22: scale, M31: 0.0, M32: 0.0 }
            * Matrix3x2::translation(line.x + line.width / 2.0, line.y + line.height / 2.0);
        let geometry = self.d2d.CreateTransformedGeometry(&path, &transform)?;
        let brush = target.CreateSolidColorBrush(&color, None)?;
        target.FillGeometry(&geometry, &brush, None);
        Ok(())
    }
}

/// 由 32 bpp BGRA（非預乘）像素建立 HICON。
unsafe fn create_icon(pixels: &[u8], size: u32) -> Result<HICON> {
    let header = BITMAPINFOHEADER {
        biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
        biWidth: size as i32,
        biHeight: -(size as i32),
        biPlanes: 1,
        biBitCount: 32,
        biCompression: BI_RGB.0,
        ..Default::default()
    };
    let info = BITMAPINFO { bmiHeader: header, ..Default::default() };
    let mut bits = std::ptr::null_mut();
    let color = CreateDIBSection(None, &info, DIB_RGB_COLORS, &mut bits, None, 0)?;
    std::ptr::copy_nonoverlapping(pixels.as_ptr(), bits as *mut u8, pixels.len());

    // 32 bpp 圖示以 Alpha 通道決定透明度，遮罩只需全零
    let mask_bits = vec![0u8; (size.div_ceil(16) * 2 * size) as usize];
    let mask = CreateBitmap(size as i32, size as i32, 1, 1, Some(mask_bits.as_ptr() as *const _));

    let info = ICONINFO { fIcon: true.into(), xHotspot: 0, yHotspot: 0, hbmMask: mask, hbmColor: color };
    let icon = CreateIconIndirect(&info);
    let _ = DeleteObject(HBITMAP(color.0).into());
    let _ = DeleteObject(mask.into());
    icon
}

/// 單一 NotifyIcon 的狀態。
struct TrayEntry {
    id: u32,
    added: bool,
    icon: Option<HICON>,
    icon_key: Option<String>,
    tooltip: String,
}

impl TrayEntry {
    fn new(id: u32) -> Self {
        Self { id, added: false, icon: None, icon_key: None, tooltip: String::new() }
    }
}

/// 系統匣中 CPU 與各 GPU 圖示的管理者。
pub struct TrayIcons {
    hwnd: HWND,
    callback_message: u32,
    renderer: IconRenderer,
    cpu: TrayEntry,
    gpus: Vec<(String, TrayEntry)>,
    next_id: u32,
}

impl TrayIcons {
    pub fn new(hwnd: HWND, callback_message: u32, renderer: IconRenderer) -> Self {
        Self {
            hwnd,
            callback_message,
            renderer,
            cpu: TrayEntry::new(CPU_ICON_ID),
            gpus: Vec::new(),
            next_id: CPU_ICON_ID + 1,
        }
    }

    fn allocate_id(&mut self) -> u32 {
        let id = self.next_id;
        self.next_id = self.next_id.wrapping_add(1).max(CPU_ICON_ID + 1);
        id
    }

    /// 依目前 GPU 清單重建每張 GPU 的獨立圖示。
    pub fn rebuild(&mut self, state: &MainState, restore_cpu_order: bool) {
        for (_, entry) in std::mem::take(&mut self.gpus) {
            self.remove(entry);
        }

        for gpu in &state.gpus {
            let id = self.allocate_id();
            self.gpus.push((gpu.id.clone(), TrayEntry::new(id)));
        }

        if restore_cpu_order && !self.gpus.is_empty() {
            // Windows 通常把較晚註冊的圖示放在左側；GPU 清單初次出現後重新註冊 CPU，
            // 讓預設順序維持 CPU 在左、各張 GPU 依序在右（使用者手動排列仍由 Windows 記憶）。
            let old = std::mem::replace(&mut self.cpu, TrayEntry::new(0));
            self.remove(old);
            let id = self.allocate_id();
            self.cpu = TrayEntry::new(id);
        }

        self.update(state);
    }

    /// 依顯示狀態更新所有圖示；數字與尺寸未變時不重新繪製。GPU 先註冊，CPU 最後註冊。
    pub fn update(&mut self, state: &MainState) {
        let size = tray_icon_size();

        let mut gpus = std::mem::take(&mut self.gpus);
        for (id, entry) in &mut gpus {
            let Some(gpu) = state.gpus.iter().find(|gpu| &gpu.id == id) else {
                continue;
            };
            let choice = state.gpu_tray_settings(id);
            let visible = state.gpu_present && (choice.show_usage || choice.show_temperature);
            let usage = if choice.show_usage { gpu.usage_tray_digits.as_str() } else { "" };
            let temperature = if choice.show_temperature { gpu.temperature_tray_digits.as_str() } else { "" };
            let tooltip = build_tooltip(
                &format!("{}: {}", gpu.display_name, gpu.device_name),
                choice.show_usage.then_some((state.t("GpuUsage"), gpu.usage.as_str())),
                choice.show_temperature.then_some((state.t("GpuTemperature"), gpu.temperature.as_str())),
            );
            self.apply(entry, visible, usage, temperature, GPU_COLOR, size, tooltip);
        }
        self.gpus = gpus;

        let show_usage = state.settings.show_cpu_usage_in_tray;
        let show_temperature = state.settings.show_cpu_temperature_in_tray;
        let usage = if show_usage { state.cpu_usage_tray_digits.as_str() } else { "" };
        let temperature = if show_temperature { state.cpu_tray_digits.as_str() } else { "" };
        let tooltip = build_tooltip(
            &state.cpu_device_name,
            show_usage.then_some((state.t("CpuUsage"), state.cpu_usage.as_str())),
            show_temperature.then_some((state.t("CpuTemperature"), state.cpu_temperature.as_str())),
        );
        let mut cpu = std::mem::replace(&mut self.cpu, TrayEntry::new(0));
        self.apply(&mut cpu, show_usage || show_temperature, usage, temperature, CPU_COLOR, size, tooltip);
        self.cpu = cpu;
    }

    /// 檔案總管重新啟動（TaskbarCreated）後，所有圖示都需要重新註冊。
    pub fn recreate(&mut self, state: &MainState) {
        self.cpu.added = false;
        for (_, entry) in &mut self.gpus {
            entry.added = false;
        }
        self.update(state);
    }

    #[allow(clippy::too_many_arguments)]
    fn apply(
        &self,
        entry: &mut TrayEntry,
        visible: bool,
        usage: &str,
        temperature: &str,
        color: D2D1_COLOR_F,
        size: u32,
        tooltip: String,
    ) {
        if !visible {
            if entry.added {
                self.notify(NIM_DELETE, entry, false);
                entry.added = false;
            }
            return;
        }

        let key = format!("{size}|{usage}|{temperature}");
        let icon_changed = entry.icon_key.as_deref() != Some(key.as_str());
        if icon_changed {
            if let Ok(icon) = self.renderer.render(usage, temperature, color, size) {
                if let Some(old) = entry.icon.replace(icon) {
                    unsafe {
                        let _ = DestroyIcon(old);
                    }
                }
                entry.icon_key = Some(key);
            }
        }

        let tooltip_changed = entry.tooltip != tooltip;
        entry.tooltip = tooltip;
        if !entry.added {
            entry.added = self.notify(NIM_ADD, entry, true);
            if entry.added {
                let mut data = self.data(entry);
                data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
                unsafe {
                    let _ = Shell_NotifyIconW(NIM_SETVERSION, &data);
                }
            }
        } else if icon_changed || tooltip_changed {
            self.notify(NIM_MODIFY, entry, true);
        }
    }

    fn data(&self, entry: &TrayEntry) -> NOTIFYICONDATAW {
        NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: self.hwnd,
            uID: entry.id,
            ..Default::default()
        }
    }

    fn notify(
        &self,
        message: windows::Win32::UI::Shell::NOTIFY_ICON_MESSAGE,
        entry: &TrayEntry,
        with_content: bool,
    ) -> bool {
        let mut data = self.data(entry);
        if with_content {
            data.uFlags = NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP;
            data.uCallbackMessage = self.callback_message;
            if let Some(icon) = entry.icon {
                data.uFlags |= NIF_ICON;
                data.hIcon = icon;
            }
            copy_to_wide_buffer(&entry.tooltip, &mut data.szTip);
        }

        unsafe { Shell_NotifyIconW(message, &data) }.as_bool()
    }

    fn remove(&self, mut entry: TrayEntry) {
        if entry.added {
            self.notify(NIM_DELETE, &entry, false);
        }
        if let Some(icon) = entry.icon.take() {
            unsafe {
                let _ = DestroyIcon(icon);
            }
        }
    }

    /// 顯示右鍵功能表並傳回所選命令。
    pub fn show_menu(&self, state: &MainState, at: POINT) -> u32 {
        unsafe {
            let Ok(menu) = CreatePopupMenu() else {
                return 0;
            };
            let open = crate::win::WideString::new(state.t("Open"));
            let exit = crate::win::WideString::new(state.t("Exit"));
            let _ = AppendMenuW(menu, MF_STRING, MENU_OPEN as usize, open.as_pcwstr());
            let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
            let _ = AppendMenuW(menu, MF_STRING, MENU_EXIT as usize, exit.as_pcwstr());

            // 必須先成為前景視窗，功能表才會在點擊其他地方時自動關閉
            let _ = SetForegroundWindow(self.hwnd);
            let command = TrackPopupMenuEx(
                menu,
                (TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN).0,
                at.x,
                at.y,
                self.hwnd,
                None,
            );
            let _ = PostMessageW(Some(self.hwnd), WM_NULL, Default::default(), Default::default());
            let _ = DestroyMenu(menu);
            command.0 as u32
        }
    }
}

impl Drop for TrayIcons {
    fn drop(&mut self) {
        for (_, entry) in std::mem::take(&mut self.gpus) {
            self.remove(entry);
        }
        let cpu = std::mem::replace(&mut self.cpu, TrayEntry::new(0));
        self.remove(cpu);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn both_lines_stay_inside_the_canvas_without_overlapping() {
        for icon_size in [16, 20, 24, 32, 48, 64] {
            let canvas = canvas_size(icon_size);
            let size = canvas as f32;
            let (usage, temperature) = lines(canvas);
            for line in [usage, temperature] {
                assert!(line.x >= 0.0 && line.y >= 0.0, "{line:?}");
                assert!(line.x + line.width <= size && line.y + line.height <= size + 0.001, "{line:?}");
                assert!(line.width > 0.0 && line.height > 0.0);
            }
            assert!(usage.y + usage.height <= temperature.y + 0.001, "{icon_size}");
        }
    }

    #[test]
    fn the_canvas_is_supersampled() {
        assert_eq!(canvas_size(16), 32);
    }

    #[test]
    fn tooltip_names_the_device_and_each_shown_metric() {
        assert_eq!(
            build_tooltip("AMD Ryzen", Some(("CPU usage", "8%")), Some(("CPU temperature", "54 °C"))),
            "AMD Ryzen | CPU usage: 8% | CPU temperature: 54 °C"
        );
        assert_eq!(build_tooltip(" ", None, Some(("t", "1"))), "CPU | t: 1");
    }

    fn renderer() -> IconRenderer {
        use windows::Win32::Graphics::Direct2D::{D2D1CreateFactory, D2D1_FACTORY_TYPE_SINGLE_THREADED};
        use windows::Win32::Graphics::DirectWrite::{DWriteCreateFactory, DWRITE_FACTORY_TYPE_SHARED};
        use windows::Win32::Graphics::Imaging::CLSID_WICImagingFactory;
        use windows::Win32::System::Com::{
            CoCreateInstance, CoInitializeEx, CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED,
        };

        unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
            let d2d = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None).unwrap();
            let dwrite: IDWriteFactory = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED).unwrap();
            let wic = CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER).unwrap();
            IconRenderer::new(d2d, &dwrite, wic).unwrap()
        }
    }

    /// 統計指定列範圍內不透明像素的數量與平均顏色。
    fn ink(pixels: &[u8], size: u32, rows: std::ops::Range<u32>) -> (usize, [f32; 3]) {
        let mut count = 0;
        let mut sum = [0f32; 3];
        for row in rows {
            for column in 0..size {
                let offset = ((row * size + column) * 4) as usize;
                if pixels[offset + 3] > 200 {
                    count += 1;
                    sum[0] += f32::from(pixels[offset + 2]);
                    sum[1] += f32::from(pixels[offset + 1]);
                    sum[2] += f32::from(pixels[offset]);
                }
            }
        }
        let average = if count == 0 { [0.0; 3] } else { sum.map(|value| value / count as f32) };
        (count, average)
    }

    /// 設定 THERMOTRAY_DUMP_ICONS 時輸出 BMP 供人工檢視。
    fn dump(pixels: &[u8], size: u32, name: &str) {
        let Some(directory) = std::env::var_os("THERMOTRAY_DUMP_ICONS") else {
            return;
        };
        let mut file = Vec::new();
        let data_size = size * size * 4;
        file.extend(b"BM");
        file.extend((54 + data_size).to_le_bytes());
        file.extend([0u8; 4]);
        file.extend(54u32.to_le_bytes());
        file.extend(40u32.to_le_bytes());
        file.extend((size as i32).to_le_bytes());
        file.extend((-(size as i32)).to_le_bytes());
        file.extend(1u16.to_le_bytes());
        file.extend(32u16.to_le_bytes());
        file.extend([0u8; 24]);
        file.extend(pixels);
        let _ = std::fs::write(std::path::Path::new(&directory).join(format!("{name}.bmp")), file);
    }

    #[test]
    fn icons_draw_white_usage_above_the_coloured_temperature() {
        let renderer = renderer();
        for size in [16, 24, 32] {
            let pixels = renderer.render_pixels("85", "62", CPU_COLOR, size).unwrap();
            dump(&pixels, size, &format!("cpu-{size}"));
            assert_eq!(pixels.len(), (size * size * 4) as usize);

            let (top_count, top_color) = ink(&pixels, size, 0..size / 2);
            let (bottom_count, bottom_color) = ink(&pixels, size, size / 2..size);
            assert!(top_count > 0 && bottom_count > 0, "{size}: both lines must have ink");
            assert!(top_color.iter().all(|&channel| channel > 200.0), "{size}: usage digits are white {top_color:?}");
            assert!(
                bottom_color[1] > bottom_color[0] + 60.0,
                "{size}: temperature digits use the accent {bottom_color:?}"
            );
        }
    }

    #[test]
    fn hidden_metrics_leave_their_line_transparent() {
        let renderer = renderer();
        let pixels = renderer.render_pixels("", "--", GPU_COLOR, 32).unwrap();
        dump(&pixels, 32, "gpu-placeholder");
        assert_eq!(ink(&pixels, 32, 0..14).0, 0);
        assert!(ink(&pixels, 32, 16..32).0 > 0);
    }

    #[test]
    fn icons_become_real_hicons() {
        let icon = renderer().render("1", "100", GPU_COLOR, 16).unwrap();
        assert!(!icon.is_invalid());
        unsafe { DestroyIcon(icon).unwrap() };
    }

    #[test]
    fn long_tooltips_are_trimmed_to_the_windows_limit() {
        let tooltip = build_tooltip(&"x".repeat(200), None, None);
        assert_eq!(tooltip.encode_utf16().count(), 127);
        assert!(tooltip.ends_with("..."));
    }
}
