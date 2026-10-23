//! 主視窗繪製：以 Direct2D / DirectWrite 重現 Obsidian Tech 深色儀表板。
//!
//! 版面在每次繪製時重新計算（每秒最多一次），同一份程式碼也用於只量測高度的模式，
//! 因此視窗高度、點擊區域與實際畫面永遠一致。

use windows::core::{w, Result, PCWSTR};
use windows::Win32::Foundation::{HMODULE, HWND, RECT};
use windows::Win32::Graphics::Direct2D::Common::{D2D1_COLOR_F, D2D_RECT_F, D2D_SIZE_U};
use windows::Win32::Graphics::Direct2D::{
    ID2D1Bitmap, ID2D1Factory, ID2D1HwndRenderTarget, ID2D1RenderTarget, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
    D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, D2D1_CAP_STYLE_ROUND, D2D1_DRAW_TEXT_OPTIONS_NONE,
    D2D1_HWND_RENDER_TARGET_PROPERTIES, D2D1_LINE_JOIN_ROUND, D2D1_PRESENT_OPTIONS_NONE, D2D1_RENDER_TARGET_PROPERTIES,
    D2D1_ROUNDED_RECT, D2D1_STROKE_STYLE_PROPERTIES,
};
use windows::Win32::Graphics::DirectWrite::{
    IDWriteFactory, IDWriteTextFormat, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_WEIGHT,
    DWRITE_FONT_WEIGHT_BOLD, DWRITE_FONT_WEIGHT_MEDIUM, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_WEIGHT_SEMI_BOLD,
    DWRITE_TEXT_METRICS, DWRITE_TEXT_RANGE, DWRITE_WORD_WRAPPING_NO_WRAP, DWRITE_WORD_WRAPPING_WRAP,
};
use windows::Win32::Graphics::Gdi::{
    GetSysColor, COLOR_BTNFACE, COLOR_GRAYTEXT, COLOR_HIGHLIGHT, COLOR_HIGHLIGHTTEXT, COLOR_WINDOW, COLOR_WINDOWFRAME,
    COLOR_WINDOWTEXT,
};
use windows::Win32::Graphics::Imaging::{
    GUID_WICPixelFormat32bppPBGRA, IWICImagingFactory, WICBitmapDitherTypeNone, WICBitmapPaletteTypeCustom,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Accessibility::{HCF_HIGHCONTRASTON, HIGHCONTRASTW};
use windows::Win32::UI::WindowsAndMessaging::{
    DestroyIcon, GetClientRect, LoadImageW, SystemParametersInfoW, HICON, IMAGE_ICON, LR_DEFAULTCOLOR,
    SPI_GETHIGHCONTRAST, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS,
};
use windows_numerics::Vector2;

use crate::model::MainState;
use crate::reading::gauge_fraction;
use crate::win::WideString;

/// 視窗內容寬度（DIP），與舊版 WPF 視窗相同。
pub const CONTENT_WIDTH: f32 = 720.0;
const MARGIN: f32 = 24.0;
/// 裝置卡片清單的最大可視高度（超過時可捲動），與舊版 ScrollViewer 相同。
pub const MAXIMUM_CARDS_HEIGHT: f32 = 560.0;

/// 可互動的控制項。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Control {
    CpuUsageTray,
    CpuTemperatureTray,
    GpuUsageTray(usize),
    GpuTemperatureTray(usize),
    DriverLink,
    StartWithWindows,
    HideWhenClosed,
    LanguageChinese,
    LanguageEnglish,
}

impl Control {
    /// 是否為「顯示在工具列」切換開關（需要提示文字）。
    pub fn is_tray_toggle(self) -> bool {
        matches!(
            self,
            Control::CpuUsageTray
                | Control::CpuTemperatureTray
                | Control::GpuUsageTray(_)
                | Control::GpuTemperatureTray(_)
        )
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Rect {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

impl Rect {
    pub const fn new(x: f32, y: f32, width: f32, height: f32) -> Self {
        Self { x, y, width, height }
    }

    pub fn contains(&self, x: f32, y: f32) -> bool {
        x >= self.x && x < self.x + self.width && y >= self.y && y < self.y + self.height
    }

    pub fn bottom(&self) -> f32 {
        self.y + self.height
    }

    fn inflate(&self, amount: f32) -> Rect {
        Rect::new(self.x - amount, self.y - amount, self.width + 2.0 * amount, self.height + 2.0 * amount)
    }

    fn to_d2d(self) -> D2D_RECT_F {
        D2D_RECT_F { left: self.x, top: self.y, right: self.x + self.width, bottom: self.y + self.height }
    }
}

/// 點擊區域；位於卡片捲動區內的控制項另帶可視範圍。
#[derive(Clone, Copy, Debug)]
pub struct Hit {
    pub control: Control,
    pub rect: Rect,
    pub clip: Option<Rect>,
}

impl Hit {
    pub fn contains(&self, x: f32, y: f32) -> bool {
        self.rect.contains(x, y) && self.clip.is_none_or(|clip| clip.contains(x, y))
    }
}

/// 滑鼠與鍵盤互動狀態，只影響外觀。
#[derive(Clone, Copy, Debug, Default)]
pub struct Interaction {
    pub hover: Option<Control>,
    pub pressed: Option<Control>,
    pub focused: Option<Control>,
}

/// 色彩配置。
#[derive(Clone, Copy, Debug)]
pub struct Palette {
    window: D2D1_COLOR_F,
    panel: D2D1_COLOR_F,
    surface: D2D1_COLOR_F,
    surface_hover: D2D1_COLOR_F,
    border: D2D1_COLOR_F,
    border_subtle: D2D1_COLOR_F,
    accent: D2D1_COLOR_F,
    accent_text: D2D1_COLOR_F,
    text_primary: D2D1_COLOR_F,
    text_secondary: D2D1_COLOR_F,
    text_muted: D2D1_COLOR_F,
    icon_badge: D2D1_COLOR_F,
    icon_badge_border: D2D1_COLOR_F,
    version_badge: D2D1_COLOR_F,
    version_badge_border: D2D1_COLOR_F,
    footer: D2D1_COLOR_F,
    footer_border: D2D1_COLOR_F,
    track: D2D1_COLOR_F,
    badge_background: D2D1_COLOR_F,
    badge_border: D2D1_COLOR_F,
    pub high_contrast: bool,
}

const fn hex(value: u32) -> D2D1_COLOR_F {
    D2D1_COLOR_F {
        r: ((value >> 16) & 0xFF) as f32 / 255.0,
        g: ((value >> 8) & 0xFF) as f32 / 255.0,
        b: (value & 0xFF) as f32 / 255.0,
        a: 1.0,
    }
}

fn system_color(index: windows::Win32::Graphics::Gdi::SYS_COLOR_INDEX) -> D2D1_COLOR_F {
    let value = unsafe { GetSysColor(index) };
    D2D1_COLOR_F {
        r: (value & 0xFF) as f32 / 255.0,
        g: ((value >> 8) & 0xFF) as f32 / 255.0,
        b: ((value >> 16) & 0xFF) as f32 / 255.0,
        a: 1.0,
    }
}

impl Palette {
    pub const OBSIDIAN: Palette = Palette {
        window: hex(0x0B0E14),
        panel: hex(0x131822),
        surface: hex(0x1A212D),
        surface_hover: hex(0x222B3A),
        border: hex(0x242E3D),
        border_subtle: hex(0x1B2330),
        accent: hex(0x2DD4BF),
        accent_text: hex(0x0B0E14),
        text_primary: hex(0xF0F6FC),
        text_secondary: hex(0x94A3B8),
        text_muted: hex(0x64748B),
        icon_badge: hex(0x14202E),
        icon_badge_border: hex(0x24364D),
        version_badge: hex(0x161F2C),
        version_badge_border: hex(0x26354B),
        footer: hex(0x0F131C),
        footer_border: hex(0x1E2635),
        track: hex(0x242F3F),
        badge_background: hex(0x102827),
        badge_border: hex(0x1D4A46),
        high_contrast: false,
    };

    /// 依目前的高對比設定選擇配色；高對比模式改用系統色彩。
    pub fn current() -> Palette {
        if !is_high_contrast() {
            return Palette::OBSIDIAN;
        }

        let window = system_color(COLOR_WINDOW);
        let control = system_color(COLOR_BTNFACE);
        let border = system_color(COLOR_WINDOWFRAME);
        let text = system_color(COLOR_WINDOWTEXT);
        let gray = system_color(COLOR_GRAYTEXT);
        let highlight = system_color(COLOR_HIGHLIGHT);
        let highlight_text = system_color(COLOR_HIGHLIGHTTEXT);
        Palette {
            window,
            panel: window,
            surface: control,
            surface_hover: control,
            border,
            border_subtle: border,
            accent: highlight,
            accent_text: highlight_text,
            text_primary: text,
            text_secondary: text,
            text_muted: gray,
            icon_badge: window,
            icon_badge_border: border,
            version_badge: window,
            version_badge_border: border,
            footer: window,
            footer_border: border,
            track: control,
            badge_background: window,
            badge_border: highlight,
            high_contrast: true,
        }
    }

    pub fn window_background(&self) -> D2D1_COLOR_F {
        self.window
    }
}

fn is_high_contrast() -> bool {
    let mut contrast = HIGHCONTRASTW { cbSize: std::mem::size_of::<HIGHCONTRASTW>() as u32, ..Default::default() };
    let queried = unsafe {
        SystemParametersInfoW(
            SPI_GETHIGHCONTRAST,
            contrast.cbSize,
            Some(&mut contrast as *mut _ as *mut _),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        )
    };
    queried.is_ok() && contrast.dwFlags.contains(HCF_HIGHCONTRASTON)
}

struct Formats {
    title: IDWriteTextFormat,
    version: IDWriteTextFormat,
    subtitle: IDWriteTextFormat,
    badge: IDWriteTextFormat,
    device: IDWriteTextFormat,
    label: IDWriteTextFormat,
    value: IDWriteTextFormat,
    sensor_prefix: IDWriteTextFormat,
    sensor_source: IDWriteTextFormat,
    status: IDWriteTextFormat,
    link: IDWriteTextFormat,
    body: IDWriteTextFormat,
    small: IDWriteTextFormat,
}

/// 與裝置無關的繪圖資源（工廠與字型格式）。
pub struct Resources {
    pub d2d: ID2D1Factory,
    pub dwrite: IDWriteFactory,
    pub wic: IWICImagingFactory,
    formats: Formats,
}

fn family_exists(dwrite: &IDWriteFactory, name: PCWSTR) -> bool {
    unsafe {
        let mut collection = None;
        if dwrite.GetSystemFontCollection(&mut collection, false).is_err() {
            return false;
        }
        let Some(collection) = collection else {
            return false;
        };
        let mut index = 0;
        let mut exists = windows::core::BOOL(0);
        collection.FindFamilyName(name, &mut index, &mut exists).is_ok() && exists.as_bool()
    }
}

impl Resources {
    pub fn new(d2d: ID2D1Factory, dwrite: IDWriteFactory, wic: IWICImagingFactory) -> Result<Self> {
        let text_family = if family_exists(&dwrite, w!("Segoe UI Variable Text")) {
            w!("Segoe UI Variable Text")
        } else {
            w!("Segoe UI")
        };
        let display_family = if family_exists(&dwrite, w!("Segoe UI Variable Display")) {
            w!("Segoe UI Variable Display")
        } else {
            w!("Segoe UI")
        };

        let format = |family: PCWSTR, weight: DWRITE_FONT_WEIGHT, size: f32, wrap: bool| -> Result<IDWriteTextFormat> {
            unsafe {
                let format = dwrite.CreateTextFormat(
                    family,
                    None,
                    weight,
                    DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH_NORMAL,
                    size,
                    w!("zh-TW"),
                )?;
                format.SetWordWrapping(if wrap { DWRITE_WORD_WRAPPING_WRAP } else { DWRITE_WORD_WRAPPING_NO_WRAP })?;
                Ok(format)
            }
        };

        let formats = Formats {
            title: format(text_family, DWRITE_FONT_WEIGHT_SEMI_BOLD, 20.0, false)?,
            version: format(text_family, DWRITE_FONT_WEIGHT_MEDIUM, 11.0, false)?,
            subtitle: format(text_family, DWRITE_FONT_WEIGHT_NORMAL, 12.0, true)?,
            badge: format(text_family, DWRITE_FONT_WEIGHT_BOLD, 11.0, false)?,
            device: format(text_family, DWRITE_FONT_WEIGHT_SEMI_BOLD, 15.0, true)?,
            label: format(text_family, DWRITE_FONT_WEIGHT_MEDIUM, 12.0, false)?,
            value: format(display_family, DWRITE_FONT_WEIGHT_SEMI_BOLD, 26.0, false)?,
            sensor_prefix: format(text_family, DWRITE_FONT_WEIGHT_BOLD, 10.0, false)?,
            sensor_source: format(text_family, DWRITE_FONT_WEIGHT_NORMAL, 11.0, true)?,
            status: format(text_family, DWRITE_FONT_WEIGHT_NORMAL, 13.0, true)?,
            link: format(text_family, DWRITE_FONT_WEIGHT_SEMI_BOLD, 13.0, true)?,
            body: format(text_family, DWRITE_FONT_WEIGHT_NORMAL, 14.0, false)?,
            small: format(text_family, DWRITE_FONT_WEIGHT_NORMAL, 13.0, false)?,
        };

        Ok(Self { d2d, dwrite, wic, formats })
    }
}

/// 一次版面計算的結果。
pub struct Composition {
    /// 內容總高度（DIP）。
    pub height: f32,
    pub hits: Vec<Hit>,
    /// 卡片清單的完整高度與可視範圍，用於捲動。
    pub cards_content_height: f32,
    pub cards_viewport: Rect,
}

struct Composer<'a> {
    resources: &'a Resources,
    palette: &'a Palette,
    target: Option<&'a ID2D1RenderTarget>,
    interaction: &'a Interaction,
    locale: WideString,
    hits: Vec<Hit>,
    clip: Option<Rect>,
    /// 量測階段：只計算尺寸，不繪製也不記錄點擊區域。
    measuring: bool,
}

impl Composer<'_> {
    fn drawing(&self) -> Option<&ID2D1RenderTarget> {
        if self.measuring {
            None
        } else {
            self.target
        }
    }

    fn hit(&mut self, control: Control, rect: Rect) {
        if !self.measuring {
            self.hits.push(Hit { control, rect, clip: self.clip });
        }
    }

    fn fill(&self, rect: Rect, radius: f32, color: D2D1_COLOR_F) {
        if let Some(target) = self.drawing() {
            unsafe {
                if let Ok(brush) = target.CreateSolidColorBrush(&color, None) {
                    if radius > 0.0 {
                        target.FillRoundedRectangle(
                            &D2D1_ROUNDED_RECT { rect: rect.to_d2d(), radiusX: radius, radiusY: radius },
                            &brush,
                        );
                    } else {
                        target.FillRectangle(&rect.to_d2d(), &brush);
                    }
                }
            }
        }
    }

    fn stroke(&self, rect: Rect, radius: f32, color: D2D1_COLOR_F, width: f32) {
        if let Some(target) = self.drawing() {
            let inset = Rect::new(rect.x + width / 2.0, rect.y + width / 2.0, rect.width - width, rect.height - width);
            unsafe {
                if let Ok(brush) = target.CreateSolidColorBrush(&color, None) {
                    target.DrawRoundedRectangle(
                        &D2D1_ROUNDED_RECT { rect: inset.to_d2d(), radiusX: radius, radiusY: radius },
                        &brush,
                        width,
                        None,
                    );
                }
            }
        }
    }

    fn panel(&self, rect: Rect, radius: f32, background: D2D1_COLOR_F, border: D2D1_COLOR_F) {
        self.fill(rect, radius, background);
        self.stroke(rect, radius, border, 1.0);
    }

    fn focus_ring(&self, control: Control, rect: Rect, radius: f32) {
        if self.interaction.focused == Some(control) {
            self.stroke(rect.inflate(3.0), radius + 2.0, self.palette.accent, 2.0);
        }
    }

    /// 排版並（必要時）繪製文字，傳回實際寬高。
    #[allow(clippy::too_many_arguments)]
    fn text(
        &self,
        text: &str,
        format: &IDWriteTextFormat,
        x: f32,
        y: f32,
        max_width: f32,
        color: D2D1_COLOR_F,
        underline: bool,
    ) -> (f32, f32) {
        let units: Vec<u16> = text.encode_utf16().collect();
        unsafe {
            let Ok(layout) = self.resources.dwrite.CreateTextLayout(&units, format, max_width.max(1.0), 10_000.0)
            else {
                return (0.0, 0.0);
            };
            let range = DWRITE_TEXT_RANGE { startPosition: 0, length: units.len() as u32 };
            let _ = layout.SetLocaleName(self.locale.as_pcwstr(), range);
            if underline {
                let _ = layout.SetUnderline(true, range);
            }

            let mut metrics = DWRITE_TEXT_METRICS::default();
            let _ = layout.GetMetrics(&mut metrics);
            if let Some(target) = self.drawing() {
                if let Ok(brush) = target.CreateSolidColorBrush(&color, None) {
                    target.DrawTextLayout(Vector2 { X: x, Y: y }, &layout, &brush, D2D1_DRAW_TEXT_OPTIONS_NONE);
                }
            }

            (metrics.widthIncludingTrailingWhitespace, metrics.height)
        }
    }

    /// 30×16 的膠囊開關（顯示在工具列）。
    fn toggle(&mut self, control: Control, x: f32, y: f32, on: bool) {
        let rect = Rect::new(x, y, 30.0, 16.0);
        self.hit(control, rect.inflate(4.0));
        let palette = *self.palette;
        let hovered = self.interaction.hover == Some(control);
        let (track, border, thumb) = if on {
            (palette.accent, palette.accent, palette.accent_text)
        } else {
            (palette.surface_hover, if hovered { palette.accent } else { palette.border }, palette.text_secondary)
        };
        self.panel(rect, 8.0, track, border);
        let thumb_x = if on { x + 30.0 - 14.0 } else { x + 2.0 };
        self.fill(Rect::new(thumb_x, y + 2.0, 12.0, 12.0), 6.0, thumb);
        self.focus_ring(control, rect, 8.0);
    }

    /// 方形核取方塊與標籤，傳回總寬度。
    fn checkbox(&mut self, control: Control, x: f32, y: f32, height: f32, label: &str, checked: bool) -> f32 {
        let palette = *self.palette;
        let box_rect = Rect::new(x, y + (height - 18.0) / 2.0, 18.0, 18.0);
        let hovered = self.interaction.hover == Some(control);
        let pressed = self.interaction.pressed == Some(control);
        let (background, border) = if checked {
            (palette.accent, palette.accent)
        } else {
            (palette.surface, if hovered { palette.accent } else { palette.border })
        };
        let mut background = background;
        if pressed {
            background.a = 0.75;
        }
        self.panel(box_rect, 4.0, background, border);
        if checked {
            self.check_mark(box_rect, palette.accent_text);
        }

        // 標籤文字由呼叫端繪製；這裡只量測寬度以決定點擊範圍
        let measuring = self.measuring;
        self.measuring = true;
        let (label_width, label_height) =
            self.text(label, &self.resources.formats.body, 0.0, 0.0, 400.0, palette.text_primary, false);
        self.measuring = measuring;
        let width = 26.0 + label_width;
        let rect = Rect::new(x, y, width, height.max(label_height));
        self.hit(control, rect);
        self.focus_ring(control, rect, 4.0);
        width
    }

    fn check_mark(&self, box_rect: Rect, color: D2D1_COLOR_F) {
        let Some(target) = self.drawing() else {
            return;
        };
        unsafe {
            let Ok(brush) = target.CreateSolidColorBrush(&color, None) else {
                return;
            };
            let properties = D2D1_STROKE_STYLE_PROPERTIES {
                startCap: D2D1_CAP_STYLE_ROUND,
                endCap: D2D1_CAP_STYLE_ROUND,
                lineJoin: D2D1_LINE_JOIN_ROUND,
                ..Default::default()
            };
            let style = self.resources.d2d.CreateStrokeStyle(&properties, None).ok();
            let origin_x = box_rect.x + 1.0;
            let origin_y = box_rect.y + 1.0;
            let points = [(2.0, 8.0), (6.0, 12.0), (14.0, 3.0)];
            for pair in points.windows(2) {
                target.DrawLine(
                    Vector2 { X: origin_x + pair[0].0, Y: origin_y + pair[0].1 },
                    Vector2 { X: origin_x + pair[1].0, Y: origin_y + pair[1].1 },
                    &brush,
                    2.0,
                    style.as_ref(),
                );
            }
        }
    }
}

/// 計算並（若提供 target）繪製整個視窗內容。
#[allow(clippy::too_many_arguments)]
pub fn compose(
    resources: &Resources,
    palette: &Palette,
    target: Option<&ID2D1RenderTarget>,
    state: &MainState,
    interaction: &Interaction,
    scroll: f32,
    maximum_cards_height: f32,
    icon: Option<&ID2D1Bitmap>,
) -> Composition {
    let mut composer = Composer {
        resources,
        palette,
        target,
        interaction,
        locale: WideString::new(state.localizer.language()),
        hits: Vec::new(),
        clip: None,
        measuring: false,
    };
    let formats = &resources.formats;
    let content_width = CONTENT_WIDTH - 2.0 * MARGIN;

    // ── 頂部標題列 ──────────────────────────────────────────────
    let top = MARGIN;
    let (title_width, title_height) =
        composer.text("ThermoTray", &formats.title, MARGIN + 54.0, top, 400.0, palette.text_primary, false);
    let version_x = MARGIN + 54.0 + title_width + 10.0;
    composer.measuring = true;
    let (version_width, version_height) =
        composer.text(&state.version, &formats.version, 0.0, 0.0, 200.0, palette.text_secondary, false);
    composer.measuring = false;
    let badge_rect = Rect::new(
        version_x,
        top + (title_height - version_height - 4.0) / 2.0,
        version_width + 12.0,
        version_height + 2.0 + 2.0,
    );
    composer.panel(badge_rect, 5.0, palette.version_badge, palette.version_badge_border);
    composer.text(
        &state.version,
        &formats.version,
        version_x + 6.0,
        badge_rect.y + 2.0,
        200.0,
        palette.text_secondary,
        false,
    );
    let (_, subtitle_height) = composer.text(
        state.t("Subtitle"),
        &formats.subtitle,
        MARGIN + 54.0,
        top + title_height + 3.0,
        content_width - 54.0,
        palette.text_muted,
        false,
    );
    let header_height = (title_height + 3.0 + subtitle_height).max(40.0);
    let icon_rect = Rect::new(MARGIN, top + (header_height - 40.0) / 2.0, 40.0, 40.0);
    composer.panel(icon_rect, 10.0, palette.icon_badge, palette.icon_badge_border);
    if let (Some(target), Some(icon)) = (composer.drawing(), icon) {
        let destination = Rect::new(icon_rect.x + 9.0, icon_rect.y + 9.0, 22.0, 22.0).to_d2d();
        unsafe { target.DrawBitmap(icon, Some(&destination), 1.0, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, None) };
    }

    let mut y = top + header_height + 16.0;
    composer.fill(Rect::new(MARGIN, y, content_width, 1.0), 0.0, palette.border_subtle);
    y += 1.0 + 16.0;

    // ── 裝置卡片（可捲動） ─────────────────────────────────────
    let cards_top = y;
    composer.measuring = true;
    let cards_content_height = cards(&mut composer, state, cards_top, content_width);
    composer.measuring = false;
    let viewport_height = cards_content_height.min(maximum_cards_height.max(120.0));
    let viewport = Rect::new(0.0, cards_top, CONTENT_WIDTH, viewport_height);
    let scroll = scroll.clamp(0.0, (cards_content_height - viewport_height).max(0.0));
    let clipped = cards_content_height > viewport_height;
    if clipped {
        composer.clip = Some(viewport);
        if let Some(target) = composer.drawing() {
            unsafe { target.PushAxisAlignedClip(&viewport.to_d2d(), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE) };
        }
    }
    cards(&mut composer, state, cards_top - scroll, content_width);
    if clipped {
        if let Some(target) = composer.drawing() {
            unsafe { target.PopAxisAlignedClip() };
            // 捲動指示：右側細捲軸
            let track = Rect::new(CONTENT_WIDTH - MARGIN / 2.0 - 2.0, cards_top, 4.0, viewport_height);
            let thumb_height = (viewport_height * viewport_height / cards_content_height).max(24.0);
            let thumb_y =
                cards_top + (viewport_height - thumb_height) * scroll / (cards_content_height - viewport_height);
            composer.fill(track, 2.0, palette.track);
            composer.fill(Rect::new(track.x, thumb_y, 4.0, thumb_height), 2.0, palette.text_muted);
        }
        composer.clip = None;
    }
    y = cards_top + viewport_height;

    // ── 狀態提示 ───────────────────────────────────────────────
    if !state.status_message.is_empty() {
        y += 14.0;
        composer.measuring = true;
        let height = status_panel(&mut composer, state, y, content_width);
        composer.measuring = false;
        let rect = Rect::new(MARGIN, y, content_width, height);
        composer.panel(rect, 12.0, palette.panel, palette.border);
        composer.fill(Rect::new(MARGIN, y, 4.0, height), 2.0, palette.accent);
        composer.fill(Rect::new(MARGIN + 2.0, y, 2.0, height), 0.0, palette.accent);
        status_panel(&mut composer, state, y, content_width);
        y += height;
    }

    // ── 底部設定列 ─────────────────────────────────────────────
    y += 16.0;
    composer.fill(Rect::new(MARGIN, y, content_width, 1.0), 0.0, palette.border_subtle);
    y += 1.0 + 14.0;
    let footer_height = 10.0 + 28.0 + 10.0;
    composer.panel(Rect::new(MARGIN, y, content_width, footer_height), 10.0, palette.footer, palette.footer_border);
    footer(&mut composer, state, y + 10.0, content_width);
    y += footer_height + MARGIN;

    Composition { height: y, hits: composer.hits, cards_content_height, cards_viewport: viewport }
}

fn cards(composer: &mut Composer, state: &MainState, top: f32, width: f32) -> f32 {
    let mut y = top;
    y += card(
        composer,
        y,
        width,
        "CPU",
        &state.cpu_device_name,
        [
            Metric {
                label: state.t("CpuUsage"),
                value: &state.cpu_usage,
                accent: false,
                fraction: gauge_fraction(state.cpu_usage_value),
                control: Control::CpuUsageTray,
                on: state.settings.show_cpu_usage_in_tray,
            },
            Metric {
                label: state.t("CpuTemperature"),
                value: &state.cpu_temperature,
                accent: true,
                fraction: gauge_fraction(state.cpu_temperature_value),
                control: Control::CpuTemperatureTray,
                on: state.settings.show_cpu_temperature_in_tray,
            },
        ],
        &state.cpu_source,
    );

    if state.gpu_present {
        for (index, gpu) in state.gpus.iter().enumerate() {
            y += 12.0;
            let choice = state.gpu_tray_settings(&gpu.id);
            y += card(
                composer,
                y,
                width,
                &gpu.display_name,
                &gpu.device_name,
                [
                    Metric {
                        label: state.t("GpuUsage"),
                        value: &gpu.usage,
                        accent: false,
                        fraction: gauge_fraction(gpu.usage_value),
                        control: Control::GpuUsageTray(index),
                        on: choice.show_usage,
                    },
                    Metric {
                        label: state.t("GpuTemperature"),
                        value: &gpu.temperature,
                        accent: true,
                        fraction: gauge_fraction(gpu.temperature_value),
                        control: Control::GpuTemperatureTray(index),
                        on: choice.show_temperature,
                    },
                ],
                &gpu.source,
            );
        }
    }

    y - top
}

struct Metric<'a> {
    label: &'a str,
    value: &'a str,
    accent: bool,
    fraction: f32,
    control: Control,
    on: bool,
}

/// 裝置卡片：識別徽章與名稱、兩個指標磁貼、感測器來源。
fn card(
    composer: &mut Composer,
    top: f32,
    width: f32,
    badge: &str,
    device: &str,
    metrics: [Metric; 2],
    source: &str,
) -> f32 {
    let measuring = composer.measuring;
    composer.measuring = true;
    let height = card_content(composer, top, width, badge, device, &metrics, source);
    composer.measuring = measuring;

    let palette = *composer.palette;
    composer.panel(Rect::new(MARGIN, top, width, height), 12.0, palette.panel, palette.border);
    card_content(composer, top, width, badge, device, &metrics, source);
    height
}

fn card_content(
    composer: &mut Composer,
    top: f32,
    width: f32,
    badge: &str,
    device: &str,
    metrics: &[Metric; 2],
    source: &str,
) -> f32 {
    let palette = *composer.palette;
    let formats = &composer.resources.formats;
    let left = MARGIN + 1.0 + 18.0;
    let inner_width = width - 2.0 - 36.0;
    let mut y = top + 1.0 + 16.0;

    // 識別徽章與裝置名稱
    let measuring = composer.measuring;
    composer.measuring = true;
    let (badge_text_width, badge_text_height) =
        composer.text(badge, &formats.badge, 0.0, 0.0, 200.0, palette.accent, false);
    composer.measuring = true;
    let name_x = left + badge_text_width + 14.0 + 12.0;
    let (_, name_height) =
        composer.text(device, &formats.device, name_x, 0.0, left + inner_width - name_x, palette.text_primary, false);
    composer.measuring = measuring;
    let badge_height = badge_text_height + 4.0 + 2.0;
    let row_height = badge_height.max(name_height);
    let badge_rect =
        Rect::new(left, y + (row_height - badge_height) / 2.0, badge_text_width + 14.0 + 2.0, badge_height);
    composer.panel(badge_rect, 5.0, palette.badge_background, palette.badge_border);
    composer.text(badge, &formats.badge, left + 8.0, badge_rect.y + 3.0, 200.0, palette.accent, false);
    composer.text(
        device,
        &formats.device,
        name_x,
        y + (row_height - name_height) / 2.0,
        left + inner_width - name_x,
        palette.text_primary,
        false,
    );
    y += row_height + 14.0;

    // 指標磁貼
    let tile_width = (inner_width - 12.0) / 2.0;
    let mut tile_height: f32 = 0.0;
    for (index, metric) in metrics.iter().enumerate() {
        let x = left + index as f32 * (tile_width + 12.0);
        let measuring = composer.measuring;
        composer.measuring = true;
        let height = metric_tile(composer, x, y, tile_width, metric);
        composer.measuring = measuring;
        tile_height = tile_height.max(height);
    }
    for (index, metric) in metrics.iter().enumerate() {
        let x = left + index as f32 * (tile_width + 12.0);
        composer.panel(Rect::new(x, y, tile_width, tile_height), 8.0, palette.surface, palette.border);
        metric_tile(composer, x, y, tile_width, metric);
    }
    y += tile_height + 12.0;

    // 感測器來源
    y += 4.0;
    composer.fill(Rect::new(left, y, inner_width, 1.0), 0.0, palette.border_subtle);
    y += 1.0 + 10.0;
    let measuring = composer.measuring;
    composer.measuring = true;
    let (prefix_width, prefix_height) =
        composer.text("SENSOR: ", &formats.sensor_prefix, 0.0, 0.0, 200.0, palette.text_muted, false);
    let (_, source_height) =
        composer.text(source, &formats.sensor_source, 0.0, 0.0, inner_width - prefix_width, palette.text_muted, false);
    composer.measuring = measuring;
    let line_height = prefix_height.max(source_height);
    composer.text(
        "SENSOR: ",
        &formats.sensor_prefix,
        left,
        y + (source_height.min(16.0) - prefix_height).max(0.0) / 2.0 + 1.0,
        200.0,
        palette.text_muted,
        false,
    );
    composer.text(
        source,
        &formats.sensor_source,
        left + prefix_width,
        y,
        inner_width - prefix_width,
        palette.text_muted,
        false,
    );
    y += line_height.max(14.0) + 16.0 + 1.0;

    y - top
}

fn metric_tile(composer: &mut Composer, x: f32, top: f32, width: f32, metric: &Metric) -> f32 {
    let palette = *composer.palette;
    let formats = &composer.resources.formats;
    let left = x + 1.0 + 14.0;
    let inner_width = width - 2.0 - 28.0;
    let mut y = top + 1.0 + 12.0;

    let (_, label_height) =
        composer.text(metric.label, &formats.label, left, y, inner_width - 36.0, palette.text_secondary, false);
    let row_height = label_height.max(16.0);
    composer.toggle(metric.control, left + inner_width - 30.0, y + (row_height - 16.0) / 2.0, metric.on);
    y += row_height + 8.0;

    let color = if metric.accent { palette.accent } else { palette.text_primary };
    let (_, value_height) = composer.text(metric.value, &formats.value, left, y, inner_width, color, false);
    y += value_height + 8.0;

    composer.fill(Rect::new(left, y, inner_width, 4.0), 2.0, palette.track);
    if metric.fraction > 0.0 {
        composer.fill(Rect::new(left, y, (inner_width * metric.fraction).max(4.0), 4.0), 2.0, palette.accent);
    }
    y += 4.0 + 12.0 + 1.0;

    y - top
}

fn status_panel(composer: &mut Composer, state: &MainState, top: f32, width: f32) -> f32 {
    let palette = *composer.palette;
    let formats = &composer.resources.formats;
    let left = MARGIN + 4.0 + 16.0;
    let inner_width = width - 4.0 - 32.0;
    let mut y = top + 12.0;
    let (_, height) =
        composer.text(&state.status_message, &formats.status, left, y, inner_width, palette.text_secondary, false);
    y += height;

    if state.driver_action_visible {
        y += 8.0;
        let hovered = composer.interaction.hover == Some(Control::DriverLink);
        let color = if hovered { palette.text_primary } else { palette.accent };
        let (link_width, link_height) =
            composer.text(state.t("DriverDownload"), &formats.link, left, y, inner_width, color, true);
        let rect = Rect::new(left, y, link_width, link_height);
        composer.hit(Control::DriverLink, rect);
        composer.focus_ring(Control::DriverLink, rect, 3.0);
        y += link_height;
    }

    y + 12.0 - top
}

fn footer(composer: &mut Composer, state: &MainState, top: f32, width: f32) {
    let palette = *composer.palette;
    let formats = &composer.resources.formats;
    let row_height = 28.0;
    let mut x = MARGIN + 16.0;

    for (control, key, checked) in [
        (Control::StartWithWindows, "StartWithWindows", state.settings.start_with_windows),
        (Control::HideWhenClosed, "HideWhenClosed", state.settings.hide_when_closed),
    ] {
        let label = state.t(key);
        let measuring = composer.measuring;
        composer.measuring = true;
        let (_, label_height) = composer.text(label, &formats.body, 0.0, 0.0, 400.0, palette.text_primary, false);
        composer.measuring = measuring;
        let used = composer.checkbox(control, x, top, row_height, label, checked);
        composer.text(
            label,
            &formats.body,
            x + 26.0,
            top + (row_height - label_height) / 2.0,
            400.0,
            palette.text_primary,
            false,
        );
        x += used + 24.0;
    }

    // 語言分段選擇器
    let options = [
        (Control::LanguageChinese, "繁體中文", !state.localizer.is_english()),
        (Control::LanguageEnglish, "English", state.localizer.is_english()),
    ];
    let segment_width = 82.0;
    let selector_width = segment_width * options.len() as f32;
    let selector_x = MARGIN + width - 16.0 - selector_width;
    let selector = Rect::new(selector_x, top, selector_width, row_height);
    composer.panel(selector, 6.0, palette.surface, palette.border);
    for (index, (control, label, selected)) in options.into_iter().enumerate() {
        let rect = Rect::new(selector_x + index as f32 * segment_width, top, segment_width, row_height);
        let inner = rect.inflate(-3.0);
        let hovered = composer.interaction.hover == Some(control);
        if selected {
            composer.fill(inner, 4.0, palette.accent);
        } else if hovered {
            composer.fill(inner, 4.0, palette.surface_hover);
        }
        let measuring = composer.measuring;
        composer.measuring = true;
        let (label_width, label_height) =
            composer.text(label, &formats.small, 0.0, 0.0, segment_width, palette.text_primary, false);
        composer.measuring = measuring;
        let color = if selected { palette.accent_text } else { palette.text_primary };
        composer.text(
            label,
            &formats.small,
            rect.x + (segment_width - label_width) / 2.0,
            top + (row_height - label_height) / 2.0,
            segment_width,
            color,
            false,
        );
        composer.hit(control, rect);
        composer.focus_ring(control, rect, 6.0);
    }

    let measuring = composer.measuring;
    composer.measuring = true;
    let (language_width, language_height) =
        composer.text(state.t("Language"), &formats.small, 0.0, 0.0, 200.0, palette.text_secondary, false);
    composer.measuring = measuring;
    composer.text(
        state.t("Language"),
        &formats.small,
        selector_x - 8.0 - language_width,
        top + (row_height - language_height) / 2.0,
        200.0,
        palette.text_secondary,
        false,
    );
}

/// 與視窗綁定的 Direct2D 呈現目標與點陣圖。
pub struct WindowSurface {
    target: Option<ID2D1HwndRenderTarget>,
    icon: Option<ID2D1Bitmap>,
}

impl Default for WindowSurface {
    fn default() -> Self {
        Self::new()
    }
}

impl WindowSurface {
    pub fn new() -> Self {
        Self { target: None, icon: None }
    }

    pub fn resize(&mut self, width: u32, height: u32) {
        if let Some(target) = &self.target {
            unsafe {
                if target.Resize(&D2D_SIZE_U { width, height }).is_err() {
                    self.target = None;
                    self.icon = None;
                }
            }
        }
    }

    /// 裝置遺失或 DPI 改變時捨棄呈現目標，下次繪製時重建。
    pub fn reset(&mut self) {
        self.target = None;
        self.icon = None;
    }

    #[allow(clippy::too_many_arguments)]
    pub fn paint(
        &mut self,
        hwnd: HWND,
        dpi: u32,
        resources: &Resources,
        palette: &Palette,
        state: &MainState,
        interaction: &Interaction,
        scroll: f32,
        maximum_cards_height: f32,
    ) -> Option<Composition> {
        unsafe {
            let mut client = RECT::default();
            GetClientRect(hwnd, &mut client).ok()?;
            let size = D2D_SIZE_U {
                width: (client.right - client.left).max(1) as u32,
                height: (client.bottom - client.top).max(1) as u32,
            };

            // 視窗大小可能在巢狀訊息（例如 SetWindowPos 期間的 WM_SIZE）中改變，繪製前一律同步
            if let Some(target) = &self.target {
                let current = target.GetPixelSize();
                if current.width != size.width || current.height != size.height {
                    self.resize(size.width, size.height);
                }
            }

            if self.target.is_none() {
                let properties = D2D1_RENDER_TARGET_PROPERTIES::default();
                let hwnd_properties = D2D1_HWND_RENDER_TARGET_PROPERTIES {
                    hwnd,
                    pixelSize: size,
                    presentOptions: D2D1_PRESENT_OPTIONS_NONE,
                };
                let target = resources.d2d.CreateHwndRenderTarget(&properties, &hwnd_properties).ok()?;
                self.icon = load_icon_bitmap(resources, &target);
                self.target = Some(target);
            }

            let target = self.target.clone()?;
            target.SetDpi(dpi as f32, dpi as f32);
            target.BeginDraw();
            target.Clear(Some(&palette.window_background()));
            let render_target: ID2D1RenderTarget = windows::core::Interface::cast(&target).ok()?;
            let composition = compose(
                resources,
                palette,
                Some(&render_target),
                state,
                interaction,
                scroll,
                maximum_cards_height,
                self.icon.as_ref(),
            );
            if target.EndDraw(None, None).is_err() {
                // D2DERR_RECREATE_TARGET 等錯誤：下次重建
                self.reset();
            }
            Some(composition)
        }
    }
}

/// 從執行檔資源載入應用程式圖示並轉為 Direct2D 點陣圖。
fn load_icon_bitmap(resources: &Resources, target: &ID2D1HwndRenderTarget) -> Option<ID2D1Bitmap> {
    unsafe {
        let module: HMODULE = GetModuleHandleW(None).ok()?;
        let handle = LoadImageW(
            Some(module.into()),
            crate::win::make_int_resource(crate::win::APP_ICON_ID),
            IMAGE_ICON,
            64,
            64,
            LR_DEFAULTCOLOR,
        )
        .ok()?;
        let icon = HICON(handle.0);
        let source = resources.wic.CreateBitmapFromHICON(icon);
        let _ = DestroyIcon(icon);
        let source = source.ok()?;
        let converter = resources.wic.CreateFormatConverter().ok()?;
        converter
            .Initialize(
                &source,
                &GUID_WICPixelFormat32bppPBGRA,
                WICBitmapDitherTypeNone,
                None,
                0.0,
                WICBitmapPaletteTypeCustom,
            )
            .ok()?;
        target.CreateBitmapFromWicBitmap(&converter, None).ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rect_hit_testing_respects_the_clip() {
        let hit = Hit {
            control: Control::CpuUsageTray,
            rect: Rect::new(10.0, 10.0, 20.0, 20.0),
            clip: Some(Rect::new(0.0, 0.0, 100.0, 15.0)),
        };
        assert!(hit.contains(12.0, 12.0));
        assert!(!hit.contains(12.0, 20.0), "outside the visible card area");
        assert!(!hit.contains(40.0, 12.0));
    }

    #[test]
    fn only_tray_switches_get_the_show_in_tray_tooltip() {
        assert!(Control::GpuTemperatureTray(1).is_tray_toggle());
        assert!(!Control::StartWithWindows.is_tray_toggle());
        assert!(!Control::DriverLink.is_tray_toggle());
    }
}
