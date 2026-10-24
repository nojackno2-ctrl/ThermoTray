//! 應用程式生命週期：命令列、單一執行個體、主視窗訊息迴圈、背景輪詢與系統匣。

use std::cell::RefCell;
use std::sync::atomic::{AtomicBool, AtomicIsize, AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use windows::core::{w, Result, PCWSTR, PWSTR};
use windows::Win32::Foundation::{COLORREF, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows::Win32::Graphics::Direct2D::{D2D1CreateFactory, ID2D1Factory, D2D1_FACTORY_TYPE_SINGLE_THREADED};
use windows::Win32::Graphics::DirectWrite::{DWriteCreateFactory, IDWriteFactory, DWRITE_FACTORY_TYPE_SHARED};
use windows::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_CAPTION_COLOR, DWMWA_USE_IMMERSIVE_DARK_MODE};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, EndPaint, GetMonitorInfoW, InvalidateRect, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST,
    PAINTSTRUCT,
};
use windows::Win32::Graphics::Imaging::{CLSID_WICImagingFactory, IWICImagingFactory};
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Controls::{
    NMHDR, NMTTDISPINFOW, TOOLTIPS_CLASSW, TTF_SUBCLASS, TTM_ADDTOOLW, TTM_NEWTOOLRECTW, TTM_POP, TTM_SETMAXTIPWIDTH,
    TTN_GETDISPINFOW, TTS_ALWAYSTIP, TTS_NOPREFIX, TTTOOLINFOW, WM_MOUSELEAVE,
};
use windows::Win32::UI::HiDpi::{AdjustWindowRectExForDpi, GetDpiForWindow};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetKeyState, ReleaseCapture, SetCapture, TrackMouseEvent, TME_LEAVE, TRACKMOUSEEVENT, VK_RETURN, VK_SHIFT,
    VK_SPACE, VK_TAB,
};
use windows::Win32::UI::Shell::{NINF_KEY, NIN_SELECT};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetClientRect, GetMessageW, GetWindowLongPtrW,
    GetWindowRect, IsIconic, IsWindowVisible, LoadCursorW, LoadImageW, PostMessageW, PostQuitMessage, RegisterClassExW,
    RegisterWindowMessageW, SendMessageW, SetCursor, SetForegroundWindow, SetWindowLongPtrW, SetWindowPos, ShowWindow,
    TranslateMessage, CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CW_USEDEFAULT, GWLP_USERDATA, HICON, HMENU, HWND_TOPMOST,
    IDC_ARROW, IDC_HAND, IDYES, IMAGE_ICON, LR_DEFAULTCOLOR, LR_DEFAULTSIZE, MB_ICONINFORMATION, MB_ICONQUESTION,
    MB_ICONWARNING, MB_OK, MB_YESNO, MSG, SIZE_MINIMIZED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SWP_NOZORDER,
    SW_HIDE, SW_RESTORE, SW_SHOW, WINDOW_EX_STYLE, WM_APP, WM_CLOSE, WM_CONTEXTMENU, WM_CREATE, WM_DESTROY,
    WM_DPICHANGED, WM_ENDSESSION, WM_KEYDOWN, WM_LBUTTONDBLCLK, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE,
    WM_MOUSEWHEEL, WM_NCCREATE, WM_NOTIFY, WM_PAINT, WM_SETCURSOR, WM_SETTINGCHANGE, WM_SHOWWINDOW, WM_SIZE,
    WM_SYSCOLORCHANGE, WM_THEMECHANGED, WNDCLASSEXW, WS_CAPTION, WS_EX_TOPMOST, WS_MINIMIZEBOX, WS_OVERLAPPED,
    WS_POPUP, WS_SYSMENU,
};

use crate::instance::{
    decide, format_version, pipe_name, product_version, try_signal_legacy_instance, wait_for_process_exit,
    InstanceAction, InstanceClient, InstanceCoordinator, Version,
};
use crate::localizer::{format, Localizer};
use crate::model::{polling_interval, MainState};
use crate::sensors::{flatten, DriverStatus, HardwareSensorService, SensorError, DRIVER_DOWNLOAD_URL};
use crate::settings::SettingsService;
use crate::tray::{IconRenderer, TrayIcons, MENU_EXIT, MENU_OPEN};
use crate::ui::{
    compose, Composition, Control, Hit, Interaction, Palette, Resources, WindowSurface, CONTENT_WIDTH,
    MAXIMUM_CARDS_HEIGHT,
};
use crate::win::{self, message_box};

const CONNECT_TIMEOUT: Duration = Duration::from_secs(2);
const HANDOVER_TIMEOUT: Duration = Duration::from_secs(10);
const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(5);
const DRIVER_PROBE_INTERVAL: Duration = Duration::from_secs(30);

const WM_APP_SNAPSHOT: u32 = WM_APP + 1;
const WM_APP_SHOW: u32 = WM_APP + 2;
const WM_APP_EXIT: u32 = WM_APP + 3;
const WM_APP_TRAY: u32 = WM_APP + 4;
const WM_APP_STARTUP_FAILED: u32 = WM_APP + 5;

const WINDOW_CLASS: PCWSTR = w!("ThermoTray.MainWindow");
const TOOLTIP_TOOL_ID: usize = 1;

/// 系統匣圖示取得鍵盤焦點後按 Enter / 空白鍵時送出的通知。
const NIN_KEYSELECT: u32 = NIN_SELECT | NINF_KEY;

/// 提示工具以回呼方式取得文字（LPSTR_TEXTCALLBACKW）。
const TEXT_CALLBACK: *mut u16 = -1isize as *mut u16;

/// 主視窗控制代碼，供背景執行緒與管道伺服器投遞訊息。
static MAIN_WINDOW: AtomicIsize = AtomicIsize::new(0);

fn post(message: u32, wparam: usize) {
    let hwnd = MAIN_WINDOW.load(Ordering::Acquire);
    if hwnd != 0 {
        unsafe {
            let _ = PostMessageW(Some(HWND(hwnd as *mut _)), message, WPARAM(wparam), LPARAM(0));
        }
    }
}

/// 程式進入點；傳回處理程序結束代碼。
pub fn run() -> i32 {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let has = |flag: &str| arguments.iter().any(|argument| argument.eq_ignore_ascii_case(flag));

    if has("--diagnostics") {
        write_sensor_diagnostics();
        return 0;
    }

    // 開機最小化啟動不顯示主視窗，避免登入時視窗閃爍
    let started_minimized = has("--minimized");
    run_application(started_minimized).unwrap_or(1)
}

fn run_application(started_minimized: bool) -> Result<i32> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
    }

    let settings_service = SettingsService::new();
    let settings = settings_service.load();
    let messages = Localizer::new(&settings.language);

    // 先嘗試成為唯一執行個體；後續的 SHOW / EXIT 要求在主視窗建立後才會送達
    let coordinator = match claim_instance(&messages, started_minimized) {
        Some(coordinator) => coordinator,
        None => return Ok(0),
    };

    let d2d: ID2D1Factory = unsafe { D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None)? };
    let dwrite: IDWriteFactory = unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED)? };
    let wic: IWICImagingFactory = unsafe { CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER)? };
    let resources = Resources::new(d2d.clone(), dwrite.clone(), wic.clone())?;
    let renderer = IconRenderer::new(d2d, &dwrite, wic)?;

    let state = MainState::new(settings, win::decimal_separator());
    let (sender, receiver) = mpsc::channel();
    let app = App {
        hwnd: HWND::default(),
        state,
        settings_service,
        resources,
        palette: Palette::current(),
        surface: WindowSurface::new(),
        tray: None,
        renderer: Some(renderer),
        interaction: Interaction::default(),
        hits: Vec::new(),
        scroll: 0.0,
        cards_content_height: 0.0,
        cards_viewport_height: 0.0,
        receiver,
        poller: None,
        coordinator: Some(coordinator),
        startup_request_version: Arc::new(AtomicU64::new(0)),
        startup_gate: Arc::new(Mutex::new(())),
        shutting_down: false,
        shown_once: false,
        tracking_mouse: false,
        taskbar_created: unsafe { RegisterWindowMessageW(w!("TaskbarCreated")) },
        tooltip: None,
        tooltip_text: Vec::new(),
        gpu_icons_built: false,
    };

    let hwnd = create_window(app)?;
    let app = app_of(hwnd).ok_or_else(windows::core::Error::empty)?;
    app.borrow_mut().start(sender);

    if !started_minimized {
        app.borrow_mut().show_main_window();
    }

    let mut message = MSG::default();
    unsafe {
        while GetMessageW(&mut message, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    Ok(message.wParam.0 as i32)
}

/// 嘗試取得系統匣單一執行個體持有權，避免兩個圖示同時輪詢硬體。
fn claim_instance(messages: &Localizer, started_minimized: bool) -> Option<InstanceCoordinator> {
    let version = Some(product_version());
    let show: Arc<dyn Fn() + Send + Sync> = Arc::new(|| post(WM_APP_SHOW, 0));
    let exit: Arc<dyn Fn() + Send + Sync> = Arc::new(|| post(WM_APP_EXIT, 0));
    if let Some(coordinator) = InstanceCoordinator::try_claim(version, show.clone(), exit.clone()) {
        return Some(coordinator);
    }

    take_over_from_running_instance(messages, started_minimized, show, exit)
}

/// 已有 ThermoTray 執行中：透過管道協調顯示其視窗或由本執行個體接手。
fn take_over_from_running_instance(
    messages: &Localizer,
    started_minimized: bool,
    show: Arc<dyn Fn() + Send + Sync>,
    exit: Arc<dyn Fn() + Send + Sync>,
) -> Option<InstanceCoordinator> {
    let format_message = |key: &str, running: Option<Version>| {
        format(messages.get(key), &[&format_version(running), &format_version(Some(product_version()))])
    };

    let Some(mut client) = InstanceClient::try_connect(&pipe_name(), CONNECT_TIMEOUT) else {
        // 無法透過管道連線，代表對方是 1.1.3 之前的版本：觸發舊版事件讓它顯示視窗
        let raised = try_signal_legacy_instance();
        if !started_minimized {
            let text = if raised {
                format_message("LegacyInstanceRunning", None)
            } else {
                messages.get("AlreadyRunning").to_string()
            };
            message_box(None, &text, MB_OK | MB_ICONINFORMATION);
        }
        return None;
    };

    // 隨 Windows 啟動（--minimized）時絕不跳出對話方塊，直接無聲交給執行中的版本
    let action = if started_minimized {
        InstanceAction::ShowRunning
    } else {
        decide(client.running_version, Some(product_version()))
    };

    match action {
        InstanceAction::ReplaceRunning => {
            let question = format_message("ReplaceRunningInstance", client.running_version);
            if message_box(None, &question, MB_YESNO | MB_ICONQUESTION) == IDYES {
                let running_version = client.running_version;
                let running_process_id = client.running_process_id;
                let accepted = client.request_exit();
                drop(client);

                if accepted && wait_for_process_exit(running_process_id, HANDOVER_TIMEOUT) {
                    if let Some(coordinator) =
                        InstanceCoordinator::claim_after_handover(Some(product_version()), show, exit)
                    {
                        return Some(coordinator);
                    }
                }

                message_box(None, &format_message("ReplaceFailed", running_version), MB_OK | MB_ICONWARNING);
                return None;
            }
        }
        InstanceAction::ShowNewerRunning => {
            message_box(
                None,
                &format_message("NewerInstanceRunning", client.running_version),
                MB_OK | MB_ICONINFORMATION,
            );
        }
        InstanceAction::ShowRunning => {}
    }

    // 授予前景切換權限，否則對方的視窗只會在工作列閃爍
    unsafe {
        let _ = windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(client.running_process_id);
    }
    client.request_show();
    None
}

/// 背景輪詢回報的結果。
enum PollMessage {
    Snapshot(crate::reading::HardwareSnapshot, DriverStatus),
    Error(&'static str),
}

struct PollShared {
    stopped: Mutex<bool>,
    wake: Condvar,
    window_visible: AtomicBool,
}

struct Poller {
    shared: Arc<PollShared>,
    thread: Option<JoinHandle<()>>,
}

impl Poller {
    fn start(sender: Sender<PollMessage>) -> Poller {
        let shared = Arc::new(PollShared {
            stopped: Mutex::new(false),
            wake: Condvar::new(),
            window_visible: AtomicBool::new(false),
        });
        let thread_shared = shared.clone();
        let thread =
            std::thread::Builder::new().name("sensor-poller".into()).spawn(move || poll(&thread_shared, &sender)).ok();
        Poller { shared, thread }
    }

    fn set_window_visible(&self, visible: bool) {
        // 只影響下一次等待的間隔，與舊版 PeriodicTimer 調整週期的行為相同
        self.shared.window_visible.store(visible, Ordering::Relaxed);
    }

    /// 停止輪詢並在逾時內等待執行緒結束；逾時時處理程序結束也會一併終止 sidecar。
    fn stop(&mut self) {
        *self.shared.stopped.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = true;
        self.shared.wake.notify_all();
        if let Some(thread) = self.thread.take() {
            let deadline = Instant::now() + SHUTDOWN_TIMEOUT;
            while !thread.is_finished() && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(10));
            }
            if thread.is_finished() {
                let _ = thread.join();
            }
        }
    }
}

fn poll(shared: &PollShared, sender: &Sender<PollMessage>) {
    let mut service = HardwareSensorService::new();
    let mut driver = DriverStatus::default();
    let mut next_driver_probe = Instant::now();

    loop {
        if *shared.stopped.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) {
            break;
        }

        let message = match service.read() {
            Ok(snapshot) => {
                // 未安裝 PawnIO 時每 30 秒重新探測一次，安裝後不需重啟輪詢即可察覺
                if !driver.installed && Instant::now() >= next_driver_probe {
                    driver = service.driver_status();
                    next_driver_probe = Instant::now() + DRIVER_PROBE_INTERVAL;
                }
                PollMessage::Snapshot(snapshot, driver.clone())
            }
            Err(SensorError::Launch(_)) if !service_sidecar_exists() => PollMessage::Error("SidecarMissing"),
            Err(_) => PollMessage::Error("SensorError"),
        };

        if sender.send(message).is_err() {
            break;
        }
        post(WM_APP_SNAPSHOT, 0);

        let interval = polling_interval(shared.window_visible.load(Ordering::Relaxed));
        let stopped = shared.stopped.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let (stopped, _) = shared
            .wake
            .wait_timeout_while(stopped, interval, |stopped| !*stopped)
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if *stopped {
            break;
        }
    }

    service.close();
}

fn service_sidecar_exists() -> bool {
    std::env::current_exe()
        .ok()
        .and_then(|path| path.parent().map(|directory| directory.join(crate::sensors::SIDECAR_FILE_NAME)))
        .is_some_and(|path| path.exists())
        || cfg!(debug_assertions)
}

/// 主視窗擁有的所有狀態；只在 UI 執行緒上存取。
struct App {
    hwnd: HWND,
    state: MainState,
    settings_service: SettingsService,
    resources: Resources,
    palette: Palette,
    surface: WindowSurface,
    tray: Option<TrayIcons>,
    renderer: Option<IconRenderer>,
    interaction: Interaction,
    hits: Vec<Hit>,
    scroll: f32,
    cards_content_height: f32,
    cards_viewport_height: f32,
    receiver: Receiver<PollMessage>,
    poller: Option<Poller>,
    coordinator: Option<InstanceCoordinator>,
    startup_request_version: Arc<AtomicU64>,
    startup_gate: Arc<Mutex<()>>,
    shutting_down: bool,
    shown_once: bool,
    tracking_mouse: bool,
    taskbar_created: u32,
    tooltip: Option<HWND>,
    tooltip_text: Vec<u16>,
    gpu_icons_built: bool,
}

fn app_of(hwnd: HWND) -> Option<&'static RefCell<App>> {
    let pointer = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *const RefCell<App>;
    if pointer.is_null() {
        None
    } else {
        Some(unsafe { &*pointer })
    }
}

fn load_app_icon(size: i32) -> Option<HICON> {
    unsafe {
        let module = GetModuleHandleW(None).ok()?;
        let flags = if size == 0 { LR_DEFAULTCOLOR | LR_DEFAULTSIZE } else { LR_DEFAULTCOLOR };
        LoadImageW(Some(module.into()), win::make_int_resource(win::APP_ICON_ID), IMAGE_ICON, size, size, flags)
            .ok()
            .map(|handle| HICON(handle.0))
    }
}

fn create_window(app: App) -> Result<HWND> {
    unsafe {
        let module = GetModuleHandleW(None)?;
        let class = WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(window_procedure),
            hInstance: module.into(),
            hIcon: load_app_icon(32).unwrap_or_default(),
            hIconSm: load_app_icon(16).unwrap_or_default(),
            hCursor: LoadCursorW(None, IDC_ARROW)?,
            lpszClassName: WINDOW_CLASS,
            ..Default::default()
        };
        RegisterClassExW(&class);

        let app = Box::into_raw(Box::new(RefCell::new(app)));
        let style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            WINDOW_CLASS,
            w!("ThermoTray"),
            style,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            CONTENT_WIDTH as i32,
            600,
            None,
            None,
            Some(module.into()),
            Some(app as *const _),
        );
        match hwnd {
            Ok(hwnd) => Ok(hwnd),
            Err(error) => {
                drop(Box::from_raw(app));
                Err(error)
            }
        }
    }
}

extern "system" fn window_procedure(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    unsafe {
        if message == WM_NCCREATE {
            let create = &*(lparam.0 as *const CREATESTRUCTW);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize);
            if let Some(app) = app_of(hwnd) {
                app.borrow_mut().hwnd = hwnd;
            }
            MAIN_WINDOW.store(hwnd.0 as isize, Ordering::Release);
            return DefWindowProcW(hwnd, message, wparam, lparam);
        }

        let Some(cell) = app_of(hwnd) else {
            return DefWindowProcW(hwnd, message, wparam, lparam);
        };

        if message == WM_DESTROY {
            MAIN_WINDOW.store(0, Ordering::Release);
            if let Ok(mut app) = cell.try_borrow_mut() {
                app.release_services();
            }
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            drop(Box::from_raw(cell as *const RefCell<App> as *mut RefCell<App>));
            PostQuitMessage(0);
            return LRESULT(0);
        }

        // 對話方塊與功能表會巢狀分派訊息；正在處理中的訊息不可重入，交給預設處理
        let Ok(mut app) = cell.try_borrow_mut() else {
            return DefWindowProcW(hwnd, message, wparam, lparam);
        };
        match app.handle(message, wparam, lparam) {
            Some(result) => result,
            None => {
                drop(app);
                DefWindowProcW(hwnd, message, wparam, lparam)
            }
        }
    }
}

fn low_word(value: usize) -> u32 {
    (value & 0xFFFF) as u32
}

fn high_word(value: usize) -> u32 {
    ((value >> 16) & 0xFFFF) as u32
}

fn signed_point(value: isize) -> (i32, i32) {
    let x = (value & 0xFFFF) as u16 as i16 as i32;
    let y = ((value >> 16) & 0xFFFF) as u16 as i16 as i32;
    (x, y)
}

impl App {
    fn start(&mut self, sender: Sender<PollMessage>) {
        self.apply_window_theme();
        self.create_tooltip();
        if let Some(renderer) = self.renderer.take() {
            let mut tray = TrayIcons::new(self.hwnd, WM_APP_TRAY, renderer);
            tray.update(&self.state);
            self.tray = Some(tray);
        }

        self.poller = Some(Poller::start(sender));
        self.reconcile_startup_setting();
        self.fit_window_to_content(false);
    }

    fn handle(&mut self, message: u32, wparam: WPARAM, lparam: LPARAM) -> Option<LRESULT> {
        if message == self.taskbar_created && message != 0 {
            // 檔案總管重新啟動後所有系統匣圖示都會消失，需重新註冊
            if let Some(tray) = &mut self.tray {
                tray.recreate(&self.state);
            }
            return Some(LRESULT(0));
        }

        match message {
            WM_CREATE => Some(LRESULT(0)),
            WM_PAINT => {
                self.paint();
                Some(LRESULT(0))
            }
            WM_SIZE => {
                if wparam.0 as u32 == SIZE_MINIMIZED {
                    // 最小化時直接隱藏至系統匣
                    unsafe {
                        let _ = ShowWindow(self.hwnd, SW_HIDE);
                    }
                    self.sync_visibility();
                } else {
                    let (width, height) = (low_word(lparam.0 as usize), high_word(lparam.0 as usize));
                    self.surface.resize(width, height);
                    self.update_tooltip_rect();
                }
                Some(LRESULT(0))
            }
            WM_DPICHANGED => {
                let suggested = unsafe { &*(lparam.0 as *const RECT) };
                unsafe {
                    let _ = SetWindowPos(
                        self.hwnd,
                        None,
                        suggested.left,
                        suggested.top,
                        suggested.right - suggested.left,
                        suggested.bottom - suggested.top,
                        SWP_NOZORDER | SWP_NOACTIVATE,
                    );
                }
                self.surface.reset();
                self.fit_window_to_content(false);
                self.invalidate();
                Some(LRESULT(0))
            }
            WM_SHOWWINDOW => {
                if let Some(poller) = &self.poller {
                    poller.set_window_visible(wparam.0 != 0);
                }
                None
            }
            WM_CLOSE => {
                if self.state.settings.hide_when_closed && !self.shutting_down {
                    unsafe {
                        let _ = ShowWindow(self.hwnd, SW_HIDE);
                    }
                    self.sync_visibility();
                } else {
                    self.exit_application();
                }
                Some(LRESULT(0))
            }
            WM_ENDSESSION => {
                if wparam.0 != 0 {
                    self.release_services();
                }
                Some(LRESULT(0))
            }
            WM_MOUSEMOVE => {
                let (x, y) = self.to_dips(lparam.0);
                if !self.tracking_mouse {
                    let mut track = TRACKMOUSEEVENT {
                        cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                        dwFlags: TME_LEAVE,
                        hwndTrack: self.hwnd,
                        dwHoverTime: 0,
                    };
                    unsafe {
                        let _ = TrackMouseEvent(&mut track);
                    }
                    self.tracking_mouse = true;
                }
                self.set_hover(self.control_at(x, y));
                Some(LRESULT(0))
            }
            WM_MOUSELEAVE => {
                self.tracking_mouse = false;
                self.set_hover(None);
                Some(LRESULT(0))
            }
            WM_SETCURSOR => {
                if low_word(lparam.0 as usize) == 1 && self.interaction.hover.is_some() {
                    unsafe {
                        if let Ok(cursor) = LoadCursorW(None, IDC_HAND) {
                            SetCursor(Some(cursor));
                        }
                    }
                    return Some(LRESULT(1));
                }
                None
            }
            WM_LBUTTONDOWN => {
                let (x, y) = self.to_dips(lparam.0);
                self.interaction.pressed = self.control_at(x, y);
                self.interaction.focused = None;
                if self.interaction.pressed.is_some() {
                    unsafe {
                        SetCapture(self.hwnd);
                    }
                }
                self.invalidate();
                Some(LRESULT(0))
            }
            WM_LBUTTONUP => {
                let (x, y) = self.to_dips(lparam.0);
                let pressed = self.interaction.pressed.take();
                unsafe {
                    let _ = ReleaseCapture();
                }
                if pressed.is_some() && pressed == self.control_at(x, y) {
                    self.activate(pressed.unwrap_or(Control::DriverLink));
                }
                self.invalidate();
                Some(LRESULT(0))
            }
            WM_MOUSEWHEEL => {
                let delta = (high_word(wparam.0) as u16 as i16) as f32;
                self.scroll_by(-delta / 120.0 * 48.0);
                Some(LRESULT(0))
            }
            WM_KEYDOWN => self.key_down(wparam.0 as u16),
            WM_SETTINGCHANGE | WM_SYSCOLORCHANGE | WM_THEMECHANGED => {
                self.palette = Palette::current();
                self.apply_window_theme();
                self.invalidate();
                None
            }
            WM_NOTIFY => self.notify(lparam),
            WM_APP_SNAPSHOT => {
                self.drain_poll_results();
                Some(LRESULT(0))
            }
            WM_APP_SHOW => {
                self.show_main_window();
                Some(LRESULT(0))
            }
            WM_APP_EXIT => {
                self.exit_application();
                Some(LRESULT(0))
            }
            WM_APP_TRAY => {
                self.tray_event(wparam, lparam);
                Some(LRESULT(0))
            }
            WM_APP_STARTUP_FAILED => {
                let version = wparam.0 as u64;
                if version == self.startup_request_version.load(Ordering::SeqCst)
                    && self.state.settings.start_with_windows
                {
                    self.state.settings.start_with_windows = false;
                    self.save_settings();
                    self.state.status_message = self.state.t("StartupError").to_string();
                    self.fit_window_to_content(true);
                }
                Some(LRESULT(0))
            }
            _ => None,
        }
    }

    /// 由本身程式碼顯示或隱藏視窗時，WM_SHOWWINDOW 會在處理中巢狀送達而被略過，因此主動同步輪詢頻率。
    fn sync_visibility(&self) {
        if let Some(poller) = &self.poller {
            poller.set_window_visible(unsafe { IsWindowVisible(self.hwnd) }.as_bool());
        }
    }

    fn to_dips(&self, lparam: isize) -> (f32, f32) {
        let (x, y) = signed_point(lparam);
        let scale = self.scale();
        (x as f32 / scale, y as f32 / scale)
    }

    fn dpi(&self) -> u32 {
        let dpi = unsafe { GetDpiForWindow(self.hwnd) };
        if dpi == 0 {
            96
        } else {
            dpi
        }
    }

    fn scale(&self) -> f32 {
        self.dpi() as f32 / 96.0
    }

    fn invalidate(&self) {
        unsafe {
            let _ = InvalidateRect(Some(self.hwnd), None, false);
        }
    }

    fn control_at(&self, x: f32, y: f32) -> Option<Control> {
        self.hits.iter().find(|hit| hit.contains(x, y)).map(|hit| hit.control)
    }

    fn set_hover(&mut self, hover: Option<Control>) {
        if self.interaction.hover != hover {
            self.interaction.hover = hover;
            if let Some(tooltip) = self.tooltip {
                unsafe {
                    SendMessageW(tooltip, TTM_POP, None, None);
                }
            }
            self.invalidate();
        }
    }

    /// 卡片清單可視高度上限：不超過 560 DIP，也不超出螢幕工作區。
    fn maximum_cards_height(&self) -> f32 {
        let mut info = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        let monitor = unsafe { MonitorFromWindow(self.hwnd, MONITOR_DEFAULTTONEAREST) };
        let work_height = if unsafe { GetMonitorInfoW(monitor, &mut info) }.as_bool() {
            (info.rcWork.bottom - info.rcWork.top) as f32 / self.scale()
        } else {
            900.0
        };

        let composition = self.measure(f32::MAX);
        let other = composition.height - composition.cards_viewport.height;
        // 預留標題列空間
        (work_height - other - 48.0).clamp(120.0, MAXIMUM_CARDS_HEIGHT)
    }

    fn measure(&self, maximum_cards_height: f32) -> Composition {
        compose(
            &self.resources,
            &self.palette,
            None,
            &self.state,
            &self.interaction,
            self.scroll,
            maximum_cards_height,
            None,
        )
    }

    fn paint(&mut self) {
        let mut paint = PAINTSTRUCT::default();
        unsafe {
            BeginPaint(self.hwnd, &mut paint);
        }
        let maximum = self.maximum_cards_height();
        let dpi = self.dpi();
        if let Some(composition) = self.surface.paint(
            self.hwnd,
            dpi,
            &self.resources,
            &self.palette,
            &self.state,
            &self.interaction,
            self.scroll,
            maximum,
        ) {
            self.hits = composition.hits;
            self.cards_content_height = composition.cards_content_height;
            self.cards_viewport_height = composition.cards_viewport.height;
        }
        unsafe {
            let _ = EndPaint(self.hwnd, &paint);
        }
    }

    /// 依內容高度調整視窗（寬度固定，高度隨 GPU 數量與狀態訊息變化）。
    fn fit_window_to_content(&mut self, repaint: bool) {
        let maximum = self.maximum_cards_height();
        let composition = self.measure(maximum);
        self.hits = composition.hits;
        let scale = self.scale();
        let dpi = self.dpi();
        let style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        let mut frame = RECT {
            left: 0,
            top: 0,
            right: (CONTENT_WIDTH * scale).round() as i32,
            bottom: (composition.height * scale).round() as i32,
        };
        unsafe {
            let _ = AdjustWindowRectExForDpi(&mut frame, style, false, WINDOW_EX_STYLE(0), dpi);
        }
        let width = frame.right - frame.left;
        let height = frame.bottom - frame.top;

        let mut current = RECT::default();
        let mut client = RECT::default();
        unsafe {
            let _ = GetWindowRect(self.hwnd, &mut current);
            let _ = GetClientRect(self.hwnd, &mut client);
        }
        if current.right - current.left == width && current.bottom - current.top == height {
            if repaint {
                self.invalidate();
            }
            return;
        }

        // 保持上緣位置；若會超出工作區底部則往上移
        let mut info = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        let monitor = unsafe { MonitorFromWindow(self.hwnd, MONITOR_DEFAULTTONEAREST) };
        let mut top = current.top;
        if unsafe { GetMonitorInfoW(monitor, &mut info) }.as_bool() && top + height > info.rcWork.bottom {
            top = (info.rcWork.bottom - height).max(info.rcWork.top);
        }
        unsafe {
            let _ = SetWindowPos(self.hwnd, None, current.left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        self.update_tooltip_rect();
        self.invalidate();
    }

    fn center_on_screen(&self) {
        let mut info = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        let mut window = RECT::default();
        unsafe {
            let monitor = MonitorFromWindow(self.hwnd, MONITOR_DEFAULTTONEAREST);
            if !GetMonitorInfoW(monitor, &mut info).as_bool() || GetWindowRect(self.hwnd, &mut window).is_err() {
                return;
            }
            let width = window.right - window.left;
            let height = window.bottom - window.top;
            let work = info.rcWork;
            let x = work.left + ((work.right - work.left) - width) / 2;
            let y = (work.top + ((work.bottom - work.top) - height) / 2).max(work.top);
            let _ = SetWindowPos(self.hwnd, None, x, y, 0, 0, SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    fn scroll_by(&mut self, delta: f32) {
        let limit = (self.cards_content_height - self.cards_viewport_height).max(0.0);
        let scroll = (self.scroll + delta).clamp(0.0, limit);
        if (scroll - self.scroll).abs() > f32::EPSILON {
            self.scroll = scroll;
            self.invalidate();
        }
    }

    fn key_down(&mut self, key: u16) -> Option<LRESULT> {
        if key == VK_TAB.0 {
            let backwards = unsafe { GetKeyState(i32::from(VK_SHIFT.0)) } < 0;
            let controls: Vec<Control> = self.hits.iter().map(|hit| hit.control).collect();
            if controls.is_empty() {
                return Some(LRESULT(0));
            }
            let current =
                self.interaction.focused.and_then(|focused| controls.iter().position(|&control| control == focused));
            let next = match (current, backwards) {
                (None, false) => 0,
                (None, true) => controls.len() - 1,
                (Some(index), false) => (index + 1) % controls.len(),
                (Some(index), true) => (index + controls.len() - 1) % controls.len(),
            };
            self.interaction.focused = Some(controls[next]);
            self.ensure_visible(controls[next]);
            self.invalidate();
            return Some(LRESULT(0));
        }

        if key == VK_SPACE.0 || key == VK_RETURN.0 {
            if let Some(control) = self.interaction.focused {
                self.activate(control);
                return Some(LRESULT(0));
            }
        }

        None
    }

    /// 以鍵盤移動焦點時，將捲動區內的控制項捲入可視範圍。
    fn ensure_visible(&mut self, control: Control) {
        let Some(hit) = self.hits.iter().find(|hit| hit.control == control).copied() else {
            return;
        };
        let Some(clip) = hit.clip else {
            return;
        };
        if hit.rect.y < clip.y {
            self.scroll_by(hit.rect.y - clip.y - 8.0);
        } else if hit.rect.bottom() > clip.bottom() {
            self.scroll_by(hit.rect.bottom() - clip.bottom() + 8.0);
        }
    }

    fn activate(&mut self, control: Control) {
        match control {
            Control::CpuUsageTray => {
                self.state.settings.show_cpu_usage_in_tray = !self.state.settings.show_cpu_usage_in_tray;
                self.save_settings();
            }
            Control::CpuTemperatureTray => {
                self.state.settings.show_cpu_temperature_in_tray = !self.state.settings.show_cpu_temperature_in_tray;
                self.save_settings();
            }
            Control::GpuUsageTray(index) | Control::GpuTemperatureTray(index) => {
                let Some(id) = self.state.gpus.get(index).map(|gpu| gpu.id.clone()) else {
                    return;
                };
                let choice = self.state.gpu_tray_settings_mut(&id);
                if matches!(control, Control::GpuUsageTray(_)) {
                    choice.show_usage = !choice.show_usage;
                } else {
                    choice.show_temperature = !choice.show_temperature;
                }
                self.save_settings();
            }
            Control::DriverLink => {
                if !win::open_url(DRIVER_DOWNLOAD_URL) {
                    // 系統未關聯預設瀏覽器時顯示網址供手動複製
                    let text = format!("{}\n{}", self.state.t("OpenLinkError"), DRIVER_DOWNLOAD_URL);
                    message_box(Some(self.hwnd), &text, MB_OK | MB_ICONINFORMATION);
                }
                return;
            }
            Control::StartWithWindows => {
                let enabled = !self.state.settings.start_with_windows;
                self.startup_request_version.fetch_add(1, Ordering::SeqCst);
                let succeeded = {
                    let _gate = self.startup_gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
                    crate::startup::set_enabled(enabled).is_ok()
                };
                if succeeded {
                    self.state.settings.start_with_windows = enabled;
                    self.save_settings();
                } else {
                    self.state.status_message = self.state.t("StartupError").to_string();
                }
                self.fit_window_to_content(true);
            }
            Control::HideWhenClosed => {
                self.state.settings.hide_when_closed = !self.state.settings.hide_when_closed;
                self.save_settings();
            }
            Control::LanguageChinese | Control::LanguageEnglish => {
                let language = if control == Control::LanguageEnglish {
                    crate::localizer::ENGLISH_LANGUAGE
                } else {
                    crate::localizer::DEFAULT_LANGUAGE
                };
                if self.state.set_language(language) {
                    self.save_settings();
                    self.fit_window_to_content(true);
                }
            }
        }

        if let Some(tray) = &mut self.tray {
            tray.update(&self.state);
        }
        self.invalidate();
    }

    fn save_settings(&self) {
        // 儲存失敗（例如檔案被鎖定）不影響監測，下次變更時會再嘗試
        let _ = self.settings_service.save(&self.state.settings);
    }

    fn drain_poll_results(&mut self) {
        let mut gpu_list_changed = false;
        let mut received = false;
        while let Ok(message) = self.receiver.try_recv() {
            received = true;
            match message {
                PollMessage::Snapshot(snapshot, driver) => {
                    gpu_list_changed |= self.state.apply_snapshot(&snapshot, &driver).gpu_list_changed;
                }
                PollMessage::Error(key) => self.state.apply_sensor_error(key),
            }
        }
        if !received {
            return;
        }

        if let Some(tray) = &mut self.tray {
            if gpu_list_changed {
                tray.rebuild(&self.state, !self.gpu_icons_built || !self.state.gpus.is_empty());
                self.gpu_icons_built = true;
            } else {
                tray.update(&self.state);
            }
        }

        // 隱藏時仍更新讀值與系統匣；版面量測、調窗與重繪延至再次顯示。
        if unsafe { IsWindowVisible(self.hwnd) }.as_bool() {
            self.fit_window_to_content(true);
        }
    }

    fn tray_event(&mut self, wparam: WPARAM, lparam: LPARAM) {
        let event = low_word(lparam.0 as usize);
        if event == WM_CONTEXTMENU {
            let (x, y) = signed_point(wparam.0 as isize);
            let command = match &self.tray {
                Some(tray) => tray.show_menu(&self.state, POINT { x, y }),
                None => 0,
            };
            match command {
                MENU_OPEN => self.show_main_window(),
                MENU_EXIT => self.exit_application(),
                _ => {}
            }
        } else if event == WM_LBUTTONDBLCLK || event == NIN_KEYSELECT {
            self.show_main_window();
        }
    }

    /// 顯示並啟動主視窗，帶到最前景。
    fn show_main_window(&mut self) {
        if self.shutting_down {
            return;
        }

        unsafe {
            // 隱藏期間的 GPU 清單或狀態文字可能改變內容高度，顯示前先同步。
            self.fit_window_to_content(false);
            if !self.shown_once {
                self.center_on_screen();
                self.shown_once = true;
            }
            if IsIconic(self.hwnd).as_bool() {
                let _ = ShowWindow(self.hwnd, SW_RESTORE);
            } else if !IsWindowVisible(self.hwnd).as_bool() {
                let _ = ShowWindow(self.hwnd, SW_SHOW);
            }
            let _ = SetForegroundWindow(self.hwnd);
        }
        self.sync_visibility();
        self.invalidate();
    }

    /// 結束應用程式並釋放所有資源。
    fn exit_application(&mut self) {
        if self.shutting_down {
            return;
        }

        self.shutting_down = true;
        self.release_services();
        unsafe {
            let _ = DestroyWindow(self.hwnd);
        }
    }

    /// 停止輪詢、移除系統匣圖示並釋放互斥鎖。
    fn release_services(&mut self) {
        if let Some(mut poller) = self.poller.take() {
            poller.stop();
        }
        self.tray = None;
        self.coordinator = None;
    }

    /// 校驗並修復開機啟動排程（舊版排程缺少新定義標記時自動重建）。
    fn reconcile_startup_setting(&self) {
        if !self.state.settings.start_with_windows {
            return;
        }

        let version = self.startup_request_version.clone();
        let gate = self.startup_gate.clone();
        let requested = version.load(Ordering::SeqCst);
        let _ = std::thread::Builder::new().name("startup-reconcile".into()).spawn(move || {
            let succeeded = {
                let _gate = gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
                if requested != version.load(Ordering::SeqCst) || crate::startup::is_up_to_date() {
                    return;
                }
                crate::startup::set_enabled(true).is_ok()
            };
            if !succeeded && requested == version.load(Ordering::SeqCst) {
                post(WM_APP_STARTUP_FAILED, requested as usize);
            }
        });
    }

    /// 深色標題列；Windows 11 另將標題列底色設為視窗背景色。
    fn apply_window_theme(&self) {
        let dark: i32 = if self.palette.high_contrast { 0 } else { 1 };
        unsafe {
            let _ = DwmSetWindowAttribute(self.hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark as *const _ as *const _, 4);
            if !self.palette.high_contrast {
                let color = COLORREF(0x00140E0B);
                let _ = DwmSetWindowAttribute(self.hwnd, DWMWA_CAPTION_COLOR, &color as *const _ as *const _, 4);
            } else {
                // DWMWA_COLOR_DEFAULT
                let color = COLORREF(0xFFFFFFFF);
                let _ = DwmSetWindowAttribute(self.hwnd, DWMWA_CAPTION_COLOR, &color as *const _ as *const _, 4);
            }
        }
    }

    /// 單一涵蓋整個工作區的提示工具；文字依滑鼠下的控制項動態提供。
    fn create_tooltip(&mut self) {
        unsafe {
            let Ok(tooltip) = CreateWindowExW(
                WS_EX_TOPMOST,
                TOOLTIPS_CLASSW,
                PCWSTR::null(),
                WS_POPUP | windows::Win32::UI::WindowsAndMessaging::WINDOW_STYLE(TTS_ALWAYSTIP | TTS_NOPREFIX),
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                Some(self.hwnd),
                None::<HMENU>,
                GetModuleHandleW(None).ok().map(Into::into),
                None,
            ) else {
                return;
            };
            let _ = SetWindowPos(tooltip, Some(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            let mut info = self.tool_info();
            SendMessageW(tooltip, TTM_ADDTOOLW, None, Some(LPARAM(&mut info as *mut _ as isize)));
            SendMessageW(tooltip, TTM_SETMAXTIPWIDTH, None, Some(LPARAM(320)));
            self.tooltip = Some(tooltip);
        }
    }

    fn tool_info(&self) -> TTTOOLINFOW {
        let mut client = RECT::default();
        unsafe {
            let _ = GetClientRect(self.hwnd, &mut client);
        }
        TTTOOLINFOW {
            cbSize: std::mem::size_of::<TTTOOLINFOW>() as u32,
            uFlags: TTF_SUBCLASS,
            hwnd: self.hwnd,
            uId: TOOLTIP_TOOL_ID,
            rect: client,
            lpszText: PWSTR(TEXT_CALLBACK),
            ..Default::default()
        }
    }

    fn update_tooltip_rect(&self) {
        if let Some(tooltip) = self.tooltip {
            let mut info = self.tool_info();
            unsafe {
                SendMessageW(tooltip, TTM_NEWTOOLRECTW, None, Some(LPARAM(&mut info as *mut _ as isize)));
            }
        }
    }

    fn notify(&mut self, lparam: LPARAM) -> Option<LRESULT> {
        let header = unsafe { &*(lparam.0 as *const NMHDR) };
        if Some(header.hwndFrom) != self.tooltip || header.code != TTN_GETDISPINFOW {
            return None;
        }

        let text = match self.interaction.hover {
            Some(control) if control.is_tray_toggle() => self.state.t("ShowInTray"),
            _ => "",
        };
        self.tooltip_text = text.encode_utf16().chain(std::iter::once(0)).collect();
        let info = unsafe { &mut *(lparam.0 as *mut NMTTDISPINFOW) };
        info.lpszText = PWSTR(self.tooltip_text.as_mut_ptr());
        Some(LRESULT(0))
    }
}

/// `--diagnostics`：將 LibreHardwareMonitor 的原始感測器讀值寫入檔案以利排除故障。
fn write_sensor_diagnostics() {
    let directory = win::local_app_data().join("ThermoTray");
    let output = directory.join("sensor-diagnostics.txt");
    let result = (|| -> std::result::Result<String, String> {
        std::fs::create_dir_all(&directory).map_err(|error| error.to_string())?;
        let mut service = HardwareSensorService::new();
        let tree = service.read_raw().map_err(|error| error.to_string())?;
        let driver = service.driver_status();
        service.close();

        let mut report = String::new();
        report.push_str("ThermoTray raw temperature sensor diagnostics\r\n");
        report.push_str(&format!("Generated: {}\r\n", win::local_timestamp()));
        report.push_str(&format!("Version: {}\r\n", format_version(Some(product_version()))));
        if driver.installed {
            report.push_str(&format!("PawnIO kernel driver installed: True (version {})\r\n", driver.version));
        } else {
            report.push_str(&format!("PawnIO kernel driver installed: False - install from {DRIVER_DOWNLOAD_URL}\r\n"));
        }
        report.push_str(&format!(
            "Process elevated: {} (PawnIO grants its device to elevated processes only)\r\n",
            if win::is_elevated() { "True" } else { "False" }
        ));
        for line in flatten(&tree) {
            let value = line.value.map_or_else(|| "null".to_string(), |value| value.to_string());
            report.push_str(&format!(
                "{} | {} | {} | {} | {}\r\n",
                line.hardware_type, line.hardware_name, line.sensor_type, line.sensor_name, value
            ));
        }
        Ok(report)
    })();

    let content = match result {
        Ok(report) => report,
        Err(error) => format!("Diagnostics failed: {error}"),
    };
    // 無主控台的視窗程式；寫入失敗時只能忽略
    let _ = std::fs::write(output, content);
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::Win32::Graphics::Gdi::{GetUpdateRect, ValidateRect};

    #[test]
    fn hidden_poll_skips_layout_and_invalidation_then_refreshes_on_show() {
        // 使用獨立執行緒與螢幕外視窗，不啟動輪詢、系統匣或讀寫真實設定。
        std::thread::spawn(|| unsafe {
            CoInitializeEx(None, COINIT_APARTMENTTHREADED).ok().expect("初始化 COM");
            let d2d = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None).expect("建立 D2D");
            let dwrite = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED).expect("建立 DirectWrite");
            let wic = CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER).expect("建立 WIC");
            let hwnd = CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("STATIC"),
                w!("ThermoTray 測試"),
                WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
                -30000,
                -30000,
                100,
                100,
                None,
                None,
                None,
                None,
            )
            .expect("建立隱藏測試視窗");
            struct TestWindow(HWND);
            impl Drop for TestWindow {
                fn drop(&mut self) {
                    unsafe {
                        let _ = DestroyWindow(self.0);
                    }
                }
            }
            let window = TestWindow(hwnd);
            let (sender, receiver) = mpsc::channel();
            let mut app = App {
                hwnd,
                state: MainState::new(Default::default(), ".".into()),
                settings_service: SettingsService::at(
                    std::env::temp_dir().join("ThermoTray-unused-test-settings.json"),
                ),
                resources: Resources::new(d2d, dwrite, wic).expect("建立繪圖資源"),
                palette: Palette::current(),
                surface: WindowSurface::new(),
                tray: None,
                renderer: None,
                interaction: Interaction::default(),
                hits: Vec::new(),
                scroll: 0.0,
                cards_content_height: 0.0,
                cards_viewport_height: 0.0,
                receiver,
                poller: None,
                coordinator: None,
                startup_request_version: Arc::new(AtomicU64::new(0)),
                startup_gate: Arc::new(Mutex::new(())),
                shutting_down: false,
                shown_once: true,
                tracking_mouse: false,
                taskbar_created: 0,
                tooltip: None,
                tooltip_text: Vec::new(),
                gpu_icons_built: false,
            };
            let _ = ValidateRect(Some(hwnd), None);
            let mut before = RECT::default();
            GetWindowRect(hwnd, &mut before).expect("讀取原視窗大小");
            sender.send(PollMessage::Error("SensorError")).expect("傳送輪詢結果");
            app.drain_poll_results();
            assert_eq!(app.state.status_message, app.state.t("SensorError"));
            assert!(app.hits.is_empty(), "隱藏輪詢不應量測或建立控制項版面");
            assert!(!GetUpdateRect(hwnd, None, false).as_bool(), "隱藏輪詢不應 invalidation");
            let mut after = RECT::default();
            GetWindowRect(hwnd, &mut after).expect("讀取輪詢後大小");
            assert_eq!(before, after, "隱藏輪詢不應調窗");
            app.show_main_window();
            assert!(!app.hits.is_empty(), "再次顯示必須同步最新狀態的版面");
            assert!(IsWindowVisible(hwnd).as_bool());
            assert!(GetUpdateRect(hwnd, None, false).as_bool());
            drop(app);
            drop(window);
            windows::Win32::System::Com::CoUninitialize();
        })
        .join()
        .expect("視窗回歸測試");
    }
}
