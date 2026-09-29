use crate::{
    docking::{Display, Rect},
    model::Hotkey,
    resources::{LOG_TAG, text},
};
use anyhow::{Context, Result, bail};
use std::{
    cell::RefCell,
    ffi::c_void,
    mem::size_of,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::Path,
    sync::mpsc::{self, Receiver, Sender},
    thread,
    time::Duration,
};
use windows::{
    Win32::{
        Foundation::*,
        Graphics::{Dwm::*, Gdi::*},
        System::{
            LibraryLoader::GetModuleHandleW, Registry::*, SystemInformation::*, Threading::*,
        },
        UI::{HiDpi::*, Input::KeyboardAndMouse::*, Shell::*, WindowsAndMessaging::*},
    },
    core::{PCWSTR, w},
};

const BASE_DPI: f32 = 96.0;
const BYTES_PER_GIB: f64 = 1024.0 * 1024.0 * 1024.0;
const NATIVE_POLL_INTERVAL: Duration = Duration::from_millis(50);
const HOTKEY_ID: i32 = 0xB001;
const TRAY_ID: u32 = 1;
const TRAY_MESSAGE: u32 = WM_APP + 1;
const MENU_TOGGLE: usize = 1;
const MENU_SETTINGS: usize = 2;
const MENU_STARTUP: usize = 3;
const MENU_EXIT: usize = 4;
const MIN_SHELL_SUCCESS: isize = 32;
const ICON_RESOURCE_ID: usize = 1;
const RUN_KEY: PCWSTR = w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
const APP_VALUE: PCWSTR = w!("SidePeek");

pub fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}
pub fn owned(handle: HANDLE) -> OwnedHandle {
    unsafe { OwnedHandle::from_raw_handle(handle.0) }
}
pub fn handle(value: &OwnedHandle) -> HANDLE {
    HANDLE(value.as_raw_handle())
}

pub fn single_instance() -> Result<Option<OwnedHandle>> {
    // Share WPF's mutex name so both implementations cannot write the same JSON concurrently.
    unsafe {
        let mutex = CreateMutexW(None, false, w!("SidePeek_SingleInstance_Mutex"))?;
        let already_exists = GetLastError() == ERROR_ALREADY_EXISTS;
        let mutex = owned(mutex);
        Ok(if already_exists { None } else { Some(mutex) })
    }
}

pub fn displays() -> Vec<Display> {
    unsafe extern "system" fn collect(
        monitor: HMONITOR,
        _: HDC,
        _: *mut RECT,
        data: LPARAM,
    ) -> BOOL {
        unsafe {
            let list = &mut *(data.0 as *mut Vec<Display>);
            let mut info = MONITORINFOEXW::default();
            info.monitorInfo.cbSize = size_of::<MONITORINFOEXW>() as u32;
            if GetMonitorInfoW(monitor, &mut info.monitorInfo).as_bool() {
                let mut x = BASE_DPI as u32;
                let mut y = x;
                let _ = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut x, &mut y);
                let rect = info.monitorInfo.rcWork;
                list.push(Display {
                    name: String::from_utf16_lossy(
                        &info.szDevice[..info
                            .szDevice
                            .iter()
                            .position(|c| *c == 0)
                            .unwrap_or(info.szDevice.len())],
                    ),
                    work_area: Rect {
                        x: rect.left as f32,
                        y: rect.top as f32,
                        width: (rect.right - rect.left) as f32,
                        height: (rect.bottom - rect.top) as f32,
                    },
                    scale: x as f32 / BASE_DPI,
                    primary: info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0,
                });
            }
            TRUE
        }
    }
    let mut result = Vec::<Display>::new();
    unsafe {
        let _ = EnumDisplayMonitors(
            None,
            None,
            Some(collect),
            LPARAM(&mut result as *mut _ as isize),
        );
    }
    result
}
pub fn select_display(name: &str) -> Result<Display> {
    let items = displays();
    items
        .iter()
        .find(|d| d.name.eq_ignore_ascii_case(name))
        .or_else(|| items.iter().find(|d| d.primary))
        .or_else(|| items.first())
        .cloned()
        .context("No displays available")
}
pub fn cursor_position() -> Option<(f32, f32)> {
    let mut point = POINT::default();
    unsafe {
        GetCursorPos(&mut point)
            .ok()
            .map(|_| (point.x as f32, point.y as f32))
    }
}
pub fn configure_window(hwnd: isize) -> Result<()> {
    unsafe {
        let hwnd = HWND(hwnd as *mut c_void);
        // A dock must have no non-client resize border: even an invisible border
        // changes client dimensions and can consume the entire collapsed trigger.
        SetWindowLongW(hwnd, GWL_STYLE, (WS_POPUP | WS_VISIBLE).0 as i32);
        let style = GetWindowLongW(hwnd, GWL_EXSTYLE) as u32;
        SetWindowLongW(
            hwnd,
            GWL_EXSTYLE,
            ((style | WS_EX_TOOLWINDOW.0) & !WS_EX_APPWINDOW.0) as i32,
        );
        let corner = DWMWCP_ROUND;
        let _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            &corner as *const _ as _,
            size_of_val(&corner) as u32,
        );
        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED,
        )?;
    }
    Ok(())
}
pub fn set_bounds(hwnd: isize, rect: Rect, promote: bool) -> Result<()> {
    unsafe {
        SetWindowPos(
            HWND(hwnd as *mut c_void),
            if promote {
                HWND_TOPMOST
            } else {
                HWND::default()
            },
            rect.x.round() as i32,
            rect.y.round() as i32,
            rect.width.round().max(1.0) as i32,
            rect.height.round().max(1.0) as i32,
            SWP_NOACTIVATE
                | if promote {
                    SET_WINDOW_POS_FLAGS::default()
                } else {
                    SWP_NOZORDER
                },
        )?;
    }
    Ok(())
}
pub fn memory_usage() -> Option<(u32, f64, f64)> {
    let mut memory = MEMORYSTATUSEX {
        dwLength: size_of::<MEMORYSTATUSEX>() as u32,
        ..Default::default()
    };
    unsafe {
        GlobalMemoryStatusEx(&mut memory).ok()?;
    }
    Some((
        memory.dwMemoryLoad,
        (memory.ullTotalPhys - memory.ullAvailPhys) as f64 / BYTES_PER_GIB,
        memory.ullTotalPhys as f64 / BYTES_PER_GIB,
    ))
}
pub fn clipboard_sequence() -> u32 {
    unsafe { windows::Win32::System::DataExchange::GetClipboardSequenceNumber() }
}
pub fn shell_open(path: &str, arguments: &str) -> Result<()> {
    let file = wide(path);
    let params = wide(arguments);
    let result = unsafe {
        ShellExecuteW(
            None,
            w!("open"),
            PCWSTR(file.as_ptr()),
            PCWSTR(params.as_ptr()),
            None,
            SW_SHOWNORMAL,
        )
    };
    if result.0 as isize <= MIN_SHELL_SUCCESS {
        bail!("ShellExecute failed with code {}", result.0 as isize);
    }
    log::info!(target: LOG_TAG, "Shell launch succeeded");
    Ok(())
}
pub fn open_directory(path: &Path) -> Result<()> {
    shell_open(&path.to_string_lossy(), "")
}
pub fn show_error(message: &str) {
    let message = wide(message);
    unsafe {
        MessageBoxW(
            None,
            PCWSTR(message.as_ptr()),
            APP_VALUE,
            MB_OK | MB_ICONERROR,
        );
    }
}
pub fn startup_enabled() -> bool {
    let mut bytes = 0;
    unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            RUN_KEY,
            APP_VALUE,
            RRF_RT_REG_SZ,
            None,
            None,
            Some(&mut bytes),
        )
        .is_ok()
            && bytes > size_of::<u16>() as u32
    }
}
pub fn set_startup(enabled: bool) -> Result<()> {
    unsafe {
        let mut key = HKEY::default();
        RegCreateKeyExW(
            HKEY_CURRENT_USER,
            RUN_KEY,
            0,
            None,
            REG_OPTION_NON_VOLATILE,
            KEY_SET_VALUE,
            None,
            &mut key,
            None,
        )
        .ok()?;
        let result = if enabled {
            let path = std::env::current_exe()?;
            let value = wide(&format!("\"{}\"", path.display()));
            RegSetValueExW(
                key,
                APP_VALUE,
                0,
                REG_SZ,
                Some(std::slice::from_raw_parts(
                    value.as_ptr().cast(),
                    value.len() * size_of::<u16>(),
                )),
            )
            .ok()
        } else {
            let result = RegDeleteValueW(key, APP_VALUE);
            if result == ERROR_FILE_NOT_FOUND {
                Ok(())
            } else {
                result.ok()
            }
        };
        let _ = RegCloseKey(key);
        result?;
    }
    log::info!(target: LOG_TAG, "Startup setting updated, enabled={enabled}");
    Ok(())
}

#[derive(Debug)]
pub enum NativeEvent {
    Toggle,
    Settings,
    Startup,
    Quit,
    Failure,
}
enum NativeRequest {
    Hotkey(Hotkey, Sender<std::result::Result<(), String>>),
    Shutdown,
}
pub struct NativeServices {
    requests: Sender<NativeRequest>,
    pub events: Receiver<NativeEvent>,
    worker: Option<thread::JoinHandle<()>>,
}
thread_local! { static EVENT_SENDER: RefCell<Option<Sender<NativeEvent>>> = const { RefCell::new(None) }; }
fn emit(event: NativeEvent) {
    EVENT_SENDER.with(|sender| {
        if let Some(sender) = sender.borrow().as_ref() {
            let _ = sender.send(event);
        }
    });
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    unsafe {
        match message {
            WM_HOTKEY if wparam.0 == HOTKEY_ID as usize => emit(NativeEvent::Toggle),
            TRAY_MESSAGE => match lparam.0 as u32 {
                WM_LBUTTONDBLCLK => emit(NativeEvent::Toggle),
                WM_RBUTTONUP => {
                    if let Err(error) = tray_menu(hwnd) {
                        log::warn!(target: LOG_TAG, "Tray menu failed: {error}");
                    }
                }
                _ => {}
            },
            _ => return DefWindowProcW(hwnd, message, wparam, lparam),
        }
    }
    LRESULT(0)
}
unsafe fn tray_menu(hwnd: HWND) -> Result<()> {
    unsafe {
        let menu = CreatePopupMenu()?;
        let result = (|| -> Result<()> {
            for (id, label) in [
                (MENU_TOGGLE, text::TOGGLE),
                (MENU_SETTINGS, text::SETTINGS),
                (MENU_STARTUP, text::STARTUP),
                (MENU_EXIT, text::EXIT),
            ] {
                let label = wide(label);
                let flags = MF_STRING
                    | if id == MENU_STARTUP && startup_enabled() {
                        MF_CHECKED
                    } else {
                        MENU_ITEM_FLAGS::default()
                    };
                AppendMenuW(menu, flags, id, PCWSTR(label.as_ptr()))?;
            }
            let mut point = POINT::default();
            GetCursorPos(&mut point)?;
            let _ = SetForegroundWindow(hwnd);
            let selected = TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.x,
                point.y,
                0,
                hwnd,
                None,
            )
            .0 as usize;
            match selected {
                MENU_TOGGLE => emit(NativeEvent::Toggle),
                MENU_SETTINGS => emit(NativeEvent::Settings),
                MENU_STARTUP => emit(NativeEvent::Startup),
                MENU_EXIT => emit(NativeEvent::Quit),
                _ => {}
            }
            let _ = PostMessageW(hwnd, WM_NULL, WPARAM(0), LPARAM(0));
            Ok(())
        })();
        let _ = DestroyMenu(menu);
        result
    }
}
fn register_hotkey(hwnd: HWND, hotkey: &Hotkey) -> Result<()> {
    if !hotkey.valid() {
        bail!("Invalid hotkey");
    }
    let mut modifiers = MOD_NOREPEAT;
    if hotkey.control {
        modifiers |= MOD_CONTROL;
    }
    if hotkey.alt {
        modifiers |= MOD_ALT;
    }
    if hotkey.shift {
        modifiers |= MOD_SHIFT;
    }
    let key = hotkey.key.to_ascii_uppercase();
    let key = if let Some(number) = key.strip_prefix('F').and_then(|s| s.parse::<u32>().ok()) {
        VK_F1.0 as u32 + number - 1
    } else {
        key.as_bytes()[0] as u32
    };
    unsafe {
        RegisterHotKey(hwnd, HOTKEY_ID, modifiers, key)?;
    }
    Ok(())
}
impl NativeServices {
    pub fn start(hotkey: Hotkey) -> Result<Self> {
        let (event_tx, events) = mpsc::channel();
        let (requests, request_rx) = mpsc::channel();
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("sidepeek-native".into())
            .spawn(move || {
                EVENT_SENDER.with(|sender| *sender.borrow_mut() = Some(event_tx));
                unsafe {
                    let setup = (|| -> Result<(HWND, NOTIFYICONDATAW)> {
                        let instance = GetModuleHandleW(None)?;
                        let class = w!("SidePeekGpuiServices");
                        let window_class = WNDCLASSW {
                            lpfnWndProc: Some(window_proc),
                            hInstance: instance.into(),
                            lpszClassName: class,
                            ..Default::default()
                        };
                        if RegisterClassW(&window_class) == 0 {
                            bail!("Unable to register service window");
                        }
                        let hwnd = CreateWindowExW(
                            WINDOW_EX_STYLE::default(),
                            class,
                            APP_VALUE,
                            WINDOW_STYLE::default(),
                            0,
                            0,
                            0,
                            0,
                            None,
                            None,
                            instance,
                            None,
                        )?;
                        let icon = LoadIconW(instance, PCWSTR(ICON_RESOURCE_ID as *const u16))
                            .or_else(|_| LoadIconW(None, IDI_APPLICATION))?;
                        let mut tray = NOTIFYICONDATAW {
                            cbSize: size_of::<NOTIFYICONDATAW>() as u32,
                            hWnd: hwnd,
                            uID: TRAY_ID,
                            uFlags: NIF_ICON | NIF_TIP | NIF_MESSAGE,
                            uCallbackMessage: TRAY_MESSAGE,
                            hIcon: icon,
                            ..Default::default()
                        };
                        let tip = wide(text::APP);
                        tray.szTip[..tip.len()].copy_from_slice(&tip);
                        if !Shell_NotifyIconW(NIM_ADD, &tray).as_bool() {
                            let _ = DestroyWindow(hwnd);
                            bail!("Unable to create tray icon");
                        }
                        Ok((hwnd, tray))
                    })();
                    let (hwnd, tray) = match setup {
                        Ok(value) => {
                            let _ = ready_tx.send(Ok(()));
                            value
                        }
                        Err(error) => {
                            let _ = ready_tx.send(Err(error.to_string()));
                            return;
                        }
                    };
                    let mut current = hotkey;
                    if let Err(error) = register_hotkey(hwnd, &current) {
                        log::warn!(target: LOG_TAG, "Global hotkey unavailable: {error}");
                        emit(NativeEvent::Failure);
                    }
                    // Explorer recreates its notification area after a restart.
                    let taskbar_created = RegisterWindowMessageW(w!("TaskbarCreated"));
                    loop {
                        let mut msg = MSG::default();
                        while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                            if msg.message == taskbar_created {
                                let _ = Shell_NotifyIconW(NIM_ADD, &tray);
                            }
                            let _ = TranslateMessage(&msg);
                            DispatchMessageW(&msg);
                        }
                        match request_rx.recv_timeout(NATIVE_POLL_INTERVAL) {
                            Ok(NativeRequest::Shutdown)
                            | Err(mpsc::RecvTimeoutError::Disconnected) => break,
                            Ok(NativeRequest::Hotkey(next, reply)) => {
                                let _ = UnregisterHotKey(hwnd, HOTKEY_ID);
                                let result = register_hotkey(hwnd, &next);
                                if result.is_ok() {
                                    current = next;
                                } else {
                                    let _ = register_hotkey(hwnd, &current);
                                }
                                let _ = reply.send(result.map_err(|e| e.to_string()));
                            }
                            Err(mpsc::RecvTimeoutError::Timeout) => {}
                        }
                    }
                    let _ = UnregisterHotKey(hwnd, HOTKEY_ID);
                    let _ = Shell_NotifyIconW(NIM_DELETE, &tray);
                    let _ = DestroyWindow(hwnd);
                }
            })?;
        ready_rx
            .recv()
            .context("Native service initialization interrupted")?
            .map_err(anyhow::Error::msg)?;
        Ok(Self {
            requests,
            events,
            worker: Some(worker),
        })
    }
    pub fn set_hotkey(&self, hotkey: Hotkey) -> Result<()> {
        let (tx, rx) = mpsc::channel();
        self.requests.send(NativeRequest::Hotkey(hotkey, tx))?;
        rx.recv()
            .context("Hotkey registration interrupted")?
            .map_err(anyhow::Error::msg)
    }
}
impl Drop for NativeServices {
    fn drop(&mut self) {
        let _ = self.requests.send(NativeRequest::Shutdown);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
