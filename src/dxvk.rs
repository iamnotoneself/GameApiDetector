use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

pub const MARKER_NAME: &str = ".dxvk_by_gad.txt";
pub const CANDIDATES: [&str; 5] = ["d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll"];

pub fn is_dxvk_file(path: &str) -> bool {
    if path.is_empty() || !Path::new(path).is_file() {
        return false;
    }
    match crate::util::file_version_strings(path) {
        Some((_, company, product)) => {
            company.to_lowercase().contains("dxvk") || product.to_lowercase().contains("dxvk")
        }
        None => false,
    }
}

pub fn installed_files(game_dir: &str) -> Vec<String> {
    let mut result = Vec::new();
    if game_dir.is_empty() {
        return result;
    }
    for name in CANDIDATES {
        let path = Path::new(game_dir).join(name);
        if is_dxvk_file(&path.to_string_lossy()) {
            result.push(name.to_string());
        }
    }
    result
}

pub fn probe(exe_path: &str) -> String {
    let dir = safe_dir(exe_path);
    if dir.is_empty() {
        return String::new();
    }
    let files = installed_files(&dir);
    if files.is_empty() {
        return String::new();
    }
    format!("DXVK（{}）", files.join(" + "))
}

pub fn is_installed(exe_path: &str) -> bool {
    !probe(exe_path).is_empty()
}

pub fn safe_dir(exe_path: &str) -> String {
    if exe_path.is_empty() {
        return String::new();
    }
    match Path::new(exe_path).parent() {
        Some(p) => p.to_string_lossy().to_string(),
        None => String::new(),
    }
}

fn marker_path(game_dir: &str) -> PathBuf {
    Path::new(game_dir).join(MARKER_NAME)
}

fn read_marker(game_dir: &str) -> BTreeMap<String, String> {
    let mut records = BTreeMap::new();
    if let Ok(text) = fs::read_to_string(marker_path(game_dir)) {
        for line in text.lines() {
            let parts: Vec<&str> = line.split('|').collect();
            if parts.len() == 2 && !parts[0].is_empty() {
                records.insert(parts[0].to_lowercase(), parts[1].to_string());
            }
        }
    }
    records
}

fn write_marker(game_dir: &str, records: &BTreeMap<String, String>) {
    let mut text = String::new();
    for (key, value) in records {
        text.push_str(key);
        text.push('|');
        text.push_str(value);
        text.push_str("\r\n");
    }
    let _ = fs::write(marker_path(game_dir), text);
}

pub fn restore(game_dir: &str) -> (usize, Vec<String>) {
    let mut errors = Vec::new();
    if game_dir.is_empty() || !Path::new(game_dir).is_dir() {
        return (0, errors);
    }
    let records = read_marker(game_dir);
    let mut restored = 0usize;

    for (name, _) in &records {
        let dst = Path::new(game_dir).join(name);
        let bak = Path::new(game_dir).join(format!("{}.dxvk_bak", name));
        let dst_s = dst.to_string_lossy().to_string();
        if bak.is_file() {
            if dst.is_file() {
                let _ = fs::remove_file(&dst);
            }
            match fs::rename(&bak, &dst) {
                Ok(_) => restored += 1,
                Err(e) => errors.push(format!("{}：{}", name, e)),
            }
        } else if dst.is_file() && is_dxvk_file(&dst_s) {
            match fs::remove_file(&dst) {
                Ok(_) => restored += 1,
                Err(e) => errors.push(format!("{}：{}", name, e)),
            }
        }
    }

    for name in CANDIDATES {
        if records.contains_key(&name.to_lowercase()) {
            continue;
        }
        let dst = Path::new(game_dir).join(name);
        let bak = Path::new(game_dir).join(format!("{}.dxvk_bak", name));
        if !is_dxvk_file(&dst.to_string_lossy()) {
            continue;
        }
        if bak.is_file() {
            let _ = fs::remove_file(&dst);
            match fs::rename(&bak, &dst) {
                Ok(_) => restored += 1,
                Err(e) => errors.push(format!("{}：{}", name, e)),
            }
        } else {
            match fs::remove_file(&dst) {
                Ok(_) => restored += 1,
                Err(e) => errors.push(format!("{}：{}", name, e)),
            }
        }
    }

    let _ = fs::remove_file(marker_path(game_dir));
    (restored, errors)
}

pub fn install(game_dir: &str, src_dir: &str, files: &[String]) -> (Vec<String>, Vec<String>) {
    let mut installed = Vec::new();
    let mut errors = Vec::new();
    let mut records = read_marker(game_dir);

    for file in files {
        let src = Path::new(src_dir).join(file);
        let dst = Path::new(game_dir).join(file);
        let bak = Path::new(game_dir).join(format!("{}.dxvk_bak", file));
        if !src.is_file() {
            errors.push(format!("{}：源文件不存在", file));
            continue;
        }
        let mut had_backup = bak.is_file();
        if dst.is_file() && !had_backup {
            if let Err(e) = fs::rename(&dst, &bak) {
                errors.push(format!("{}：{}", file, e));
                continue;
            }
            had_backup = true;
        }
        match fs::copy(&src, &dst) {
            Ok(_) => {
                installed.push(file.clone());
                records.insert(
                    file.to_lowercase(),
                    if had_backup { "1".to_string() } else { "0".to_string() },
                );
            }
            Err(e) => errors.push(format!("{}：{}", file, e)),
        }
    }

    write_marker(game_dir, &records);
    (installed, errors)
}
