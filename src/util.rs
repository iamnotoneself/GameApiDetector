use std::ffi::c_void;
use std::fs;
use std::path::Path;

use eframe::egui;
use windows::core::PCWSTR;
use windows::Win32::Storage::FileSystem::{
    GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW,
};

pub fn wide(text: &str) -> Vec<u16> {
    let mut v: Vec<u16> = text.encode_utf16().collect();
    v.push(0);
    v
}

pub fn is_under(path: &str, dir: &str) -> bool {
    if path.is_empty() || dir.is_empty() {
        return false;
    }
    let mut prefix = dir.to_string();
    if !prefix.ends_with('\\') {
        prefix.push('\\');
    }
    path.len() >= prefix.len() && path[..prefix.len()].eq_ignore_ascii_case(&prefix)
}

pub fn file_version_strings(exe: &str) -> Option<(String, String, String)> {
    if exe.is_empty() || !Path::new(exe).is_file() {
        return None;
    }
    unsafe {
        let path = wide(exe);
        let pcw = PCWSTR(path.as_ptr());
        let size = GetFileVersionInfoSizeW(pcw, None);
        if size == 0 {
            return None;
        }
        let mut buf = vec![0u8; size as usize];
        if GetFileVersionInfoW(pcw, None, size, buf.as_mut_ptr() as *mut c_void).is_err() {
            return None;
        }

        let mut ptr: *mut c_void = std::ptr::null_mut();
        let mut len: u32 = 0;
        let translation_key = wide("\\VarFileInfo\\Translation");
        if !VerQueryValueW(
            buf.as_ptr() as *const c_void,
            PCWSTR(translation_key.as_ptr()),
            &mut ptr,
            &mut len,
        )
        .as_bool()
        {
            return None;
        }
        if ptr.is_null() || len < 4 {
            return None;
        }
        let lang = *(ptr as *const u16);
        let codepage = *((ptr as *const u16).add(1));

        let read_field = |name: &str| -> String {
            let key = wide(&format!(
                "\\StringFileInfo\\{:04x}{:04x}\\{}",
                lang, codepage, name
            ));
            let mut field_ptr: *mut c_void = std::ptr::null_mut();
            let mut field_len: u32 = 0;
            if !VerQueryValueW(
                buf.as_ptr() as *const c_void,
                PCWSTR(key.as_ptr()),
                &mut field_ptr,
                &mut field_len,
            )
            .as_bool()
            {
                return String::new();
            }
            if field_ptr.is_null() || field_len == 0 {
                return String::new();
            }
            let slice = std::slice::from_raw_parts(field_ptr as *const u16, field_len as usize);
            let end = slice.iter().position(|&c| c == 0).unwrap_or(slice.len());
            String::from_utf16_lossy(&slice[..end])
        };

        Some((
            read_field("FileDescription"),
            read_field("CompanyName"),
            read_field("ProductName"),
        ))
    }
}

fn decode_png(bytes: &[u8]) -> Option<egui::IconData> {
    let decoder = png::Decoder::new(std::io::Cursor::new(bytes));
    let mut reader = decoder.read_info().ok()?;
    let mut buf = vec![0u8; reader.output_buffer_size().unwrap_or(0)];
    let info = reader.next_frame(&mut buf).ok()?;
    let width = info.width;
    let height = info.height;
    let data = &buf[..info.buffer_size()];
    let rgba = match info.color_type {
        png::ColorType::Rgba => data.to_vec(),
        png::ColorType::Rgb => data
            .chunks_exact(3)
            .flat_map(|p| [p[0], p[1], p[2], 255])
            .collect(),
        png::ColorType::GrayscaleAlpha => data
            .chunks_exact(2)
            .flat_map(|p| [p[0], p[0], p[0], p[1]])
            .collect(),
        png::ColorType::Grayscale => data.iter().flat_map(|&g| [g, g, g, 255]).collect(),
        _ => return None,
    };
    Some(egui::IconData {
        rgba,
        width,
        height,
    })
}

fn largest_ico_png(bytes: &[u8]) -> Option<&[u8]> {
    if bytes.len() < 6 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0 {
        return None;
    }
    let count = u16::from_le_bytes([bytes[4], bytes[5]]) as usize;
    let mut best: Option<(u32, usize, usize)> = None;
    for i in 0..count {
        let base = 6 + i * 16;
        if base + 16 > bytes.len() {
            break;
        }
        let size = u32::from_le_bytes([
            bytes[base + 8],
            bytes[base + 9],
            bytes[base + 10],
            bytes[base + 11],
        ]);
        let offset = u32::from_le_bytes([
            bytes[base + 12],
            bytes[base + 13],
            bytes[base + 14],
            bytes[base + 15],
        ]) as usize;
        if offset + 8 > bytes.len() {
            continue;
        }
        let is_png = bytes[offset] == 0x89
            && bytes[offset + 1] == b'P'
            && bytes[offset + 2] == b'N'
            && bytes[offset + 3] == b'G';
        if !is_png {
            continue;
        }
        if best.map(|(s, _, _)| size > s).unwrap_or(true) {
            best = Some((size, offset, size as usize));
        }
    }
    best.map(|(_, offset, size)| &bytes[offset..offset + size])
}

pub fn window_icon() -> Option<egui::IconData> {
    let bytes = include_bytes!("../app.ico");
    let png = largest_ico_png(bytes)?;
    decode_png(png)
}

pub fn install_fonts(ctx: &egui::Context) {
    const CANDIDATES: [&str; 6] = [
        "C:\\Windows\\Fonts\\msyh.ttc",
        "C:\\Windows\\Fonts\\msyh.ttf",
        "C:\\Windows\\Fonts\\simhei.ttf",
        "C:\\Windows\\Fonts\\Deng.ttf",
        "C:\\Windows\\Fonts\\simsun.ttc",
        "C:\\Windows\\Fonts\\msyhbd.ttc",
    ];
    for path in CANDIDATES {
        if let Ok(bytes) = fs::read(path) {
            let mut fonts = egui::FontDefinitions::default();
            fonts
                .font_data
                .insert("cjk".to_owned(), egui::FontData::from_owned(bytes).into());
            if let Some(list) = fonts.families.get_mut(&egui::FontFamily::Proportional) {
                list.insert(0, "cjk".to_owned());
            }
            if let Some(list) = fonts.families.get_mut(&egui::FontFamily::Monospace) {
                list.push("cjk".to_owned());
            }
            ctx.set_fonts(fonts);
            return;
        }
    }
}
