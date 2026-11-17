use std::path::Path;
use std::process::Command;

use windows::core::{BOOL, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HMODULE, HWND, LPARAM};
use windows::Win32::System::ProcessStatus::{
    EnumProcesses, EnumProcessModulesEx, GetModuleBaseNameW, GetModuleFileNameExW, LIST_MODULES_ALL,
};
use windows::Win32::System::Threading::{
    IsWow64Process, OpenProcess, QueryFullProcessImageNameW, TerminateProcess,
    PROCESS_NAME_WIN32, PROCESS_QUERY_INFORMATION, PROCESS_QUERY_LIMITED_INFORMATION,
    PROCESS_VM_READ,
};
use windows::Win32::UI::WindowsAndMessaging::{EnumWindows, GetWindowTextW, GetWindowThreadProcessId};

pub const API_NONE: &str = "未检测到";
pub const API_UNREADABLE: &str = "无法读取";
pub const API_NOT_RUNNING: &str = "未运行";
pub const API_NO_EXE: &str = "缺少可执行文件";

#[derive(Clone, Debug, Default)]
pub struct Row {
    pub pid: i32,
    pub run_pid: u32,
    pub name: String,
    pub title: String,
    pub path: String,
    pub arch: String,
    pub api: String,
    pub key_modules: String,
    pub gfx_modules: Vec<String>,
    pub module_count: usize,
    pub error: String,
    pub has_window: bool,
    pub game_name: String,
    pub platform: String,
    pub is_game: bool,
    pub game_known: bool,
    pub dxvk: String,
}

impl Row {
    pub fn has_graphics(&self) -> bool {
        !self.api.is_empty() && self.api != API_NONE && self.api != API_UNREADABLE
    }

    pub fn running(&self) -> bool {
        self.run_pid > 0
    }

    pub fn game_text(&self) -> String {
        if !self.is_game {
            return String::new();
        }
        if self.game_known {
            if self.game_name.is_empty() {
                self.name.clone()
            } else {
                self.game_name.clone()
            }
        } else {
            format!("未知游戏 ({})", self.name)
        }
    }

    pub fn platform_text(&self) -> String {
        if !self.is_game {
            return String::new();
        }
        if self.platform.is_empty() {
            "未知".to_string()
        } else {
            self.platform.clone()
        }
    }

    pub fn install_dir(&self) -> String {
        match Path::new(&self.path).parent() {
            Some(p) => p.to_string_lossy().to_string(),
            None => String::new(),
        }
    }

    pub fn reset_live(&mut self) {
        self.run_pid = 0;
        self.api = API_NOT_RUNNING.to_string();
        self.arch = "—".to_string();
        self.title.clear();
        self.key_modules.clear();
        self.gfx_modules.clear();
        self.module_count = 0;
        self.has_window = false;
        self.error.clear();
    }

    pub fn merge_live(&mut self, live: &Row) {
        self.api = live.api.clone();
        self.arch = live.arch.clone();
        self.title = live.title.clone();
        self.key_modules = live.key_modules.clone();
        self.gfx_modules = live.gfx_modules.clone();
        self.module_count = live.module_count;
        self.has_window = live.has_window;
        self.error = live.error.clone();
        if !live.dxvk.is_empty() {
            self.dxvk = live.dxvk.clone();
        }
        if !live.path.is_empty() {
            self.path = live.path.clone();
        }
    }
}

const GFX_MODULES: [&str; 19] = [
    "vulkan-1.dll",
    "d3d12.dll",
    "d3d12core.dll",
    "d3d11.dll",
    "d3d11on12.dll",
    "d3d10.dll",
    "d3d10_1.dll",
    "d3d9.dll",
    "d3d9on12.dll",
    "opengl32.dll",
    "nvoglv64.dll",
    "nvoglv32.dll",
    "atioglxx.dll",
    "atio6axx.dll",
    "ig7icd64.dll",
    "ig7icd32.dll",
    "ig9icd64.dll",
    "ig9icd32.dll",
    "ig10icd64.dll",
];

fn is_gfx_module(name: &str) -> bool {
    GFX_MODULES.iter().any(|m| m.eq_ignore_ascii_case(name))
}

pub fn enumerate_processes() -> Vec<(u32, String)> {
    let mut result = Vec::new();
    let mut capacity = 4096usize;
    loop {
        let mut pids = vec![0u32; capacity];
        let mut needed: u32 = 0;
        let bytes = (pids.len() * std::mem::size_of::<u32>()) as u32;
        if unsafe { EnumProcesses(pids.as_mut_ptr(), bytes, &mut needed) }.is_err() {
            return result;
        }
        let count = needed as usize / std::mem::size_of::<u32>();
        if count >= capacity {
            capacity *= 2;
            continue;
        }
        pids.truncate(count);
        for pid in pids {
            if pid == 0 {
                continue;
            }
            if let Some(name) = process_name(pid) {
                result.push((pid, name));
            }
        }
        break;
    }
    result
}

pub fn process_name(pid: u32) -> Option<String> {
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 512];
        let len = GetModuleBaseNameW(handle, None, &mut buf);
        let _ = CloseHandle(handle);
        if len == 0 {
            return None;
        }
        Some(String::from_utf16_lossy(&buf[..len as usize]))
    }
}

pub fn read_path(pid: u32) -> String {
    unsafe {
        let handle = match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(h) => h,
            Err(_) => return String::new(),
        };
        let mut buf = [0u16; 1024];
        let mut size = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(
            handle,
            PROCESS_NAME_WIN32,
            PWSTR(buf.as_mut_ptr()),
            &mut size,
        )
        .is_ok();
        let _ = CloseHandle(handle);
        if !ok || size == 0 {
            return String::new();
        }
        String::from_utf16_lossy(&buf[..size as usize])
    }
}

pub fn bitness(pid: u32) -> String {
    unsafe {
        let handle = match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(h) => h,
            Err(_) => return "未知".to_string(),
        };
        let mut wow64 = BOOL(0);
        let ok = IsWow64Process(handle, &mut wow64).is_ok();
        let _ = CloseHandle(handle);
        if !ok {
            return "未知".to_string();
        }
        if wow64.as_bool() {
            "32 位".to_string()
        } else {
            "64 位".to_string()
        }
    }
}

struct TitleSearch {
    pid: u32,
    title: Option<String>,
}

unsafe extern "system" fn enum_windows_callback(hwnd: HWND, lparam: LPARAM) -> BOOL {
    let ctx = &mut *(lparam.0 as *mut TitleSearch);
    let mut window_pid: u32 = 0;
    GetWindowThreadProcessId(hwnd, Some(&mut window_pid));
    if window_pid != ctx.pid {
        return BOOL(1);
    }
    let mut buf = [0u16; 512];
    let len = GetWindowTextW(hwnd, &mut buf);
    if len > 0 {
        ctx.title = Some(String::from_utf16_lossy(&buf[..len as usize]));
        return BOOL(0);
    }
    BOOL(1)
}

pub fn window_title(pid: u32) -> String {
    let mut ctx = TitleSearch {
        pid,
        title: None,
    };
    unsafe {
        let _ = EnumWindows(
            Some(enum_windows_callback),
            LPARAM(&mut ctx as *mut TitleSearch as isize),
        );
    }
    ctx.title.unwrap_or_default()
}

pub fn module_names(pid: u32) -> Result<Vec<String>, String> {
    unsafe {
        let handle = OpenProcess(
            PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
            false,
            pid,
        )
        .map_err(|_| "无法打开进程（请尝试以管理员身份运行，或进程已退出）".to_string())?;

        let mut needed: u32 = 0;
        let _ = EnumProcessModulesEx(handle, std::ptr::null_mut(), 0, &mut needed, LIST_MODULES_ALL);
        if needed == 0 {
            let _ = CloseHandle(handle);
            return Err("无法枚举进程模块".to_string());
        }

        let count = needed as usize / std::mem::size_of::<isize>();
        let mut modules = vec![HMODULE::default(); count];
        let bytes = needed;
        if EnumProcessModulesEx(
            handle,
            modules.as_mut_ptr(),
            bytes,
            &mut needed,
            LIST_MODULES_ALL,
        )
        .is_err()
        {
            let _ = CloseHandle(handle);
            return Err("无法枚举进程模块（权限不足）".to_string());
        }

        let mut names = Vec::new();
        let mut buf = [0u16; 1024];
        for module in modules {
            let len = GetModuleFileNameExW(Some(handle), Some(module), &mut buf);
            if len > 0 {
                let full = String::from_utf16_lossy(&buf[..len as usize]);
                if let Some(name) = Path::new(&full).file_name() {
                    names.push(name.to_string_lossy().to_string());
                }
            }
        }
        let _ = CloseHandle(handle);
        Ok(names)
    }
}

pub fn detect_api(modules: &[String]) -> (String, String) {
    let has = |name: &str| modules.iter().any(|m| m.eq_ignore_ascii_case(name));
    let mut apis: Vec<&str> = Vec::new();
    if has("vulkan-1.dll") {
        apis.push("Vulkan");
    }
    if has("d3d12.dll") || has("d3d12core.dll") {
        apis.push("DirectX 12");
    }
    if has("d3d11.dll") || has("d3d11on12.dll") {
        apis.push("DirectX 11");
    }
    if has("opengl32.dll") {
        apis.push("OpenGL");
    }
    if has("d3d10.dll") || has("d3d10_1.dll") {
        apis.push("DirectX 10");
    }
    if has("d3d9.dll") || has("d3d9on12.dll") {
        apis.push("DirectX 9");
    }

    let key_modules = modules.join(", ");
    if apis.is_empty() {
        return (API_NONE.to_string(), key_modules);
    }
    let api = if apis.len() > 1 {
        format!("{}（另见 {}）", apis[0], apis[1..].join(" / "))
    } else {
        apis[0].to_string()
    };
    (api, key_modules)
}

pub fn inspect(pid: u32) -> Row {
    let mut row = Row::default();
    row.pid = pid as i32;
    row.run_pid = pid;
    row.name = process_name(pid).unwrap_or_else(|| "?".to_string());
    row.path = read_path(pid);
    row.arch = bitness(pid);
    row.title = window_title(pid);
    row.has_window = !row.title.is_empty();

    match module_names(pid) {
        Ok(mods) => {
            row.module_count = mods.len();
            let mut gfx: Vec<String> = mods
                .iter()
                .filter(|m| is_gfx_module(m))
                .cloned()
                .collect();
            gfx.sort_by(|a, b| a.to_lowercase().cmp(&b.to_lowercase()));
            gfx.dedup_by(|a, b| a.eq_ignore_ascii_case(b));
            row.gfx_modules = gfx;
            let (api, key) = detect_api(&row.gfx_modules);
            row.api = api;
            row.key_modules = key;
        }
        Err(err) => {
            row.error = err;
            row.api = API_UNREADABLE.to_string();
        }
    }
    row
}

pub fn find_running_pid(exe_path: &str, exe_name: &str) -> u32 {
    if exe_name.is_empty() {
        return 0;
    }
    for (pid, name) in enumerate_processes() {
        if !name.eq_ignore_ascii_case(exe_name) {
            continue;
        }
        let path = read_path(pid);
        if !exe_path.is_empty() && !path.is_empty() && !path.eq_ignore_ascii_case(exe_path) {
            continue;
        }
        return pid;
    }
    0
}

pub fn is_running(exe_name: &str) -> bool {
    if exe_name.is_empty() {
        return false;
    }
    enumerate_processes()
        .iter()
        .any(|(_, name)| name.eq_ignore_ascii_case(exe_name))
}

pub fn kill(pid: u32) -> Result<(), String> {
    unsafe {
        let handle = OpenProcess(
            PROCESS_QUERY_INFORMATION | windows::Win32::System::Threading::PROCESS_TERMINATE,
            false,
            pid,
        )
        .map_err(|e| e.message().to_string())?;
        let result = TerminateProcess(handle, 1);
        let _ = CloseHandle(handle);
        if result.is_err() {
            return Err("无法结束进程".to_string());
        }
        for _ in 0..30 {
            std::thread::sleep(std::time::Duration::from_millis(100));
            if !is_pid_alive(pid) {
                break;
            }
        }
        Ok(())
    }
}

fn is_pid_alive(pid: u32) -> bool {
    unsafe {
        match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(h) => {
                let _ = CloseHandle(h);
                true
            }
            Err(_) => false,
        }
    }
}

pub fn start(exe: &str, args: &str, working_dir: &str) -> Result<(), String> {
    let mut command = Command::new(exe);
    for arg in split_args(args) {
        command.arg(arg);
    }
    if !working_dir.is_empty() {
        command.current_dir(working_dir);
    } else if let Some(parent) = Path::new(exe).parent() {
        command.current_dir(parent);
    }
    command
        .spawn()
        .map(|_| ())
        .map_err(|e| e.to_string())
}

fn split_args(text: &str) -> Vec<String> {
    text.split_whitespace().map(|s| s.to_string()).collect()
}

pub fn start_elevated(exe: &str, args: &str, working_dir: &str) -> Result<(), String> {
    let mut cmd = format!("\"{}\"", exe);
    if !args.is_empty() {
        cmd.push(' ');
        cmd.push_str(args);
    }
    let mut command = Command::new("cmd");
    command.args(["/C", "start", "", &cmd]);
    if !working_dir.is_empty() {
        command.current_dir(working_dir);
    }
    command.spawn().map(|_| ()).map_err(|e| e.to_string())
}

pub fn shell_open_folder(path: &str) {
    let path = path.trim();
    if path.is_empty() {
        return;
    }
    let _ = Command::new("explorer").arg(path).spawn();
}
