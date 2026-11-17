use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use winreg::enums::*;
use winreg::RegKey;

#[derive(Clone, Debug, Default)]
pub struct GameInfo {
    pub name: String,
    pub platform: String,
    pub root: String,
    pub exe_path: String,
}

pub fn ensure_dir(path: &str) -> String {
    let mut value = path.trim().trim_matches('"').replace('/', "\\");
    if value.is_empty() {
        return String::new();
    }
    while value.len() > 3 && value.ends_with('\\') {
        value.pop();
    }
    if !value.ends_with('\\') {
        value.push('\\');
    }
    value
}

fn dir_of(path: &str) -> String {
    match Path::new(path).parent() {
        Some(p) => ensure_dir(&p.to_string_lossy()),
        None => String::new(),
    }
}

fn first_match(text: &str, pattern: &str) -> String {
    match regex::Regex::new(pattern) {
        Ok(re) => re
            .captures(text)
            .and_then(|c| c.get(1))
            .map(|m| m.as_str().to_string())
            .unwrap_or_default(),
        Err(_) => String::new(),
    }
}

fn unescape_vdf(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    let chars: Vec<char> = value.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '\\' && i + 1 < chars.len() && (chars[i + 1] == '\\' || chars[i + 1] == '"') {
            out.push(chars[i + 1]);
            i += 2;
        } else {
            out.push(chars[i]);
            i += 1;
        }
    }
    out
}

fn unescape_json(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    let chars: Vec<char> = value.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] != '\\' || i + 1 >= chars.len() {
            out.push(chars[i]);
            i += 1;
            continue;
        }
        let next = chars[i + 1];
        i += 2;
        match next {
            'n' => out.push('\n'),
            'r' => out.push('\r'),
            't' => out.push('\t'),
            'u' => {
                if i + 3 < chars.len() {
                    let hex: String = chars[i..i + 4].iter().collect();
                    if let Ok(code) = u32::from_str_radix(&hex, 16) {
                        if let Some(c) = char::from_u32(code) {
                            out.push(c);
                        }
                    }
                    i += 4;
                }
            }
            other => out.push(other),
        }
    }
    out
}

fn reg_string(key: &RegKey, name: &str) -> String {
    key.get_value::<String, _>(name).unwrap_or_default().trim().to_string()
}

fn sub_keys(key: &RegKey) -> Vec<String> {
    key.enum_keys().filter_map(|k| k.ok()).collect()
}

fn is_guid_like(value: &str) -> bool {
    if value.len() < 32 {
        return false;
    }
    value
        .chars()
        .all(|c| c.is_ascii_hexdigit() || c == '-' || c == '{' || c == '}')
}

fn file_description(exe: &str) -> String {
    match crate::util::file_version_strings(exe) {
        Some(info) => {
            if !info.0.trim().is_empty() {
                return info.0.trim().to_string();
            }
            Path::new(exe)
                .file_stem()
                .map(|s| s.to_string_lossy().to_string())
                .unwrap_or_else(|| exe.to_string())
        }
        None => Path::new(exe)
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| exe.to_string()),
    }
}

fn non_game_exe(stem: &str) -> bool {
    const LIST: [&str; 22] = [
        "java", "javaw", "python", "pythonw", "node", "chrome", "msedge", "firefox", "iexplore",
        "explorer", "notepad", "cmd", "powershell", "code", "devenv", "spotify", "vlc", "obs64",
        "discord", "werfault", "rundll32", "msiexec",
    ];
    LIST.contains(&stem)
}

fn open_hklm(path: &str, wow64: bool) -> Option<RegKey> {
    let flags = if wow64 {
        KEY_READ | KEY_WOW64_64KEY
    } else {
        KEY_READ | KEY_WOW64_32KEY
    };
    RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey_with_flags(path, flags).ok()
}

fn make_game(name: &str, platform: &str, root_path: &str, exe_path: &str) -> Option<GameInfo> {
    let root = ensure_dir(root_path);
    if root.is_empty() || !Path::new(&root).is_dir() {
        return None;
    }
    let trimmed = name.trim();
    let final_name = if trimmed.is_empty() {
        let stripped = root.trim_end_matches('\\');
        Path::new(stripped)
            .file_name()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_default()
    } else {
        trimmed.to_string()
    };
    Some(GameInfo {
        name: final_name,
        platform: platform.to_string(),
        root,
        exe_path: exe_path.to_string(),
    })
}

fn load_registry_roots(
    list: &mut Vec<GameInfo>,
    wow64: bool,
    sub_path: &str,
    platform: &str,
    dir_value: &str,
    name_value: &str,
) {
    let key = match open_hklm(sub_path, wow64) {
        Some(k) => k,
        None => return,
    };
    for sub_name in sub_keys(&key) {
        let sub = match key.open_subkey(&sub_name) {
            Ok(k) => k,
            Err(_) => continue,
        };
        let dir = reg_string(&sub, dir_value);
        if dir.is_empty() {
            continue;
        }
        let mut game_name = if name_value.is_empty() {
            String::new()
        } else {
            reg_string(&sub, name_value)
        };
        if game_name.is_empty() && !is_guid_like(&sub_name) {
            game_name = sub_name.clone();
        }
        if let Some(game) = make_game(&game_name, platform, &dir, "") {
            list.push(game);
        }
    }
}

fn load_steam(list: &mut Vec<GameInfo>) {
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let mut steam_path = String::new();
    if let Ok(key) = hkcu.open_subkey("Software\\Valve\\Steam") {
        steam_path = reg_string(&key, "SteamPath");
    }
    if steam_path.is_empty() {
        if let Ok(key) = hkcu.open_subkey("Software\\Wow6432Node\\Valve\\Steam") {
            steam_path = reg_string(&key, "SteamPath");
        }
    }
    if steam_path.is_empty() {
        return;
    }
    let steam_path = ensure_dir(&steam_path);
    if steam_path.is_empty() {
        return;
    }

    let mut libraries: Vec<String> = vec![steam_path.clone()];
    let vdf = Path::new(&steam_path).join("steamapps").join("libraryfolders.vdf");
    if let Ok(text) = fs::read_to_string(&vdf) {
        if let Ok(re) = regex::Regex::new(r#""path"\s*"([^"]*)""#) {
            for cap in re.captures_iter(&text) {
                let lib = ensure_dir(&unescape_vdf(&cap[1]));
                if lib.is_empty() {
                    continue;
                }
                if !libraries.iter().any(|x| x.eq_ignore_ascii_case(&lib)) {
                    libraries.push(lib);
                }
            }
        }
    }

    for library in libraries {
        load_steam_library(list, &library);
    }
}

fn load_steam_library(list: &mut Vec<GameInfo>, library: &str) {
    let steam_apps = Path::new(library).join("steamapps");
    if !steam_apps.is_dir() {
        return;
    }
    let common = steam_apps.join("common");
    let entries = match fs::read_dir(&steam_apps) {
        Ok(e) => e,
        Err(_) => return,
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let file_name = entry.file_name().to_string_lossy().to_string();
        if !file_name.starts_with("appmanifest_") || !file_name.ends_with(".acf") {
            continue;
        }
        let text = match fs::read_to_string(&path) {
            Ok(t) => t,
            Err(_) => continue,
        };
        let name = unescape_vdf(&first_match(&text, r#""name"\s*"([^"]*)""#));
        let install_dir = unescape_vdf(&first_match(&text, r#""installdir"\s*"([^"]*)""#));
        if install_dir.is_empty() {
            continue;
        }
        let root = ensure_dir(&common.join(&install_dir).to_string_lossy());
        if root.is_empty() || !Path::new(&root).is_dir() {
            continue;
        }
        list.push(GameInfo {
            name: if name.is_empty() { install_dir } else { name },
            platform: "Steam".to_string(),
            root,
            exe_path: String::new(),
        });
    }
}

fn load_epic(list: &mut Vec<GameInfo>) {
    let program_data = std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".to_string());
    let dir = Path::new(&program_data)
        .join("Epic")
        .join("EpicGamesLauncher")
        .join("Data")
        .join("Manifests");
    let entries = match fs::read_dir(&dir) {
        Ok(e) => e,
        Err(_) => return,
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().map(|e| e.to_string_lossy().to_lowercase()) != Some("item".to_string()) {
            continue;
        }
        let text = match fs::read_to_string(&path) {
            Ok(t) => t,
            Err(_) => continue,
        };
        let pattern: &str = r#""%s"\s*:\s*"((?:[^"\\]|\\.)*)""#;
        let name = unescape_json(&first_match(&text, &pattern.replace("%s", "DisplayName")));
        let install_location =
            unescape_json(&first_match(&text, &pattern.replace("%s", "InstallLocation")));
        let launch_exe =
            unescape_json(&first_match(&text, &pattern.replace("%s", "LaunchExecutable")));
        if install_location.is_empty() {
            continue;
        }
        let root = ensure_dir(&install_location);
        if root.is_empty() || !Path::new(&root).is_dir() {
            continue;
        }
        let fallback = install_location
            .trim_end_matches('\\')
            .rsplit('\\')
            .next()
            .unwrap_or("")
            .to_string();
        list.push(GameInfo {
            name: if name.is_empty() { fallback } else { name },
            platform: "Epic".to_string(),
            exe_path: if launch_exe.is_empty() {
                String::new()
            } else {
                Path::new(&root).join(&launch_exe).to_string_lossy().to_string()
            },
            root,
        });
    }
}

fn load_gog(list: &mut Vec<GameInfo>) {
    let key = match open_hklm("SOFTWARE\\WOW6432Node\\GOG.com\\Games", true) {
        Some(k) => k,
        None => match open_hklm("SOFTWARE\\GOG.com\\Games", false) {
            Some(k) => k,
            None => return,
        },
    };
    for sub_name in sub_keys(&key) {
        let sub = match key.open_subkey(&sub_name) {
            Ok(k) => k,
            Err(_) => continue,
        };
        let game_name = reg_string(&sub, "gameName");
        let path = reg_string(&sub, "path");
        let exe = reg_string(&sub, "exe");
        if path.is_empty() {
            continue;
        }
        let root = ensure_dir(&path);
        if root.is_empty() {
            continue;
        }
        let exe_path = if exe.is_empty() {
            String::new()
        } else if exe.len() > 1 && exe.as_bytes()[1] == b':' {
            exe.clone()
        } else {
            Path::new(&root)
                .join(exe.trim_start_matches('\\'))
                .to_string_lossy()
                .to_string()
        };
        list.push(GameInfo {
            name: if game_name.is_empty() { sub_name } else { game_name },
            platform: "GOG".to_string(),
            root,
            exe_path,
        });
    }
}

fn load_windows_games(list: &mut Vec<GameInfo>) {
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let children = match hkcu.open_subkey("System\\GameConfigStore\\Children") {
        Ok(k) => k,
        Err(_) => return,
    };
    for sub_name in sub_keys(&children) {
        let sub = match children.open_subkey(&sub_name) {
            Ok(k) => k,
            Err(_) => continue,
        };
        let exe_path = reg_string(&sub, "MatchedExeFullPath").replace('/', "\\");
        if exe_path.is_empty() || !Path::new(&exe_path).is_file() {
            continue;
        }
        let stem = Path::new(&exe_path)
            .file_stem()
            .map(|s| s.to_string_lossy().to_lowercase())
            .unwrap_or_default();
        if non_game_exe(&stem) {
            continue;
        }
        let root = dir_of(&exe_path);
        if root.is_empty() {
            continue;
        }
        list.push(GameInfo {
            name: file_description(&exe_path),
            platform: "Windows".to_string(),
            root,
            exe_path,
        });
    }
}

fn load_sub_folders(list: &mut Vec<GameInfo>, root: &Path, platform: &str) {
    let entries = match fs::read_dir(root) {
        Ok(e) => e,
        Err(_) => return,
    };
    for entry in entries.flatten() {
        if !entry.path().is_dir() {
            continue;
        }
        let leaf = entry.file_name().to_string_lossy().to_string();
        if leaf.is_empty() || leaf.eq_ignore_ascii_case("Content") {
            continue;
        }
        let folder = entry.path();
        let content = folder.join("Content");
        let target = if content.is_dir() { content } else { folder };
        if let Some(game) = make_game(&leaf, platform, &target.to_string_lossy(), "") {
            list.push(game);
        }
    }
}

fn load_xbox_uninstall(list: &mut Vec<GameInfo>, wow64: bool) {
    let key = match open_hklm(
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
        wow64,
    ) {
        Some(k) => k,
        None => return,
    };
    for sub_name in sub_keys(&key) {
        let sub = match key.open_subkey(&sub_name) {
            Ok(k) => k,
            Err(_) => continue,
        };
        let location = reg_string(&sub, "InstallLocation");
        if location.is_empty() {
            continue;
        }
        let lower = location.to_lowercase();
        if !lower.contains("\\windowsapps\\") && !lower.contains("\\xboxgames\\") {
            continue;
        }
        let game_name = reg_string(&sub, "DisplayName");
        let name = if game_name.is_empty() { sub_name } else { game_name };
        if let Some(game) = make_game(&name, "Xbox", &location, "") {
            list.push(game);
        }
    }
}

fn load_xbox(list: &mut Vec<GameInfo>) {
    load_xbox_uninstall(list, true);
    load_xbox_uninstall(list, false);
    for letter in b'C'..=b'Z' {
        let root = format!("{}:\\XboxGames", letter as char);
        if Path::new(&root).is_dir() {
            load_sub_folders(list, Path::new(&root), "Xbox");
        }
    }
}

fn load_ea(list: &mut Vec<GameInfo>) {
    load_registry_roots(list, true, "SOFTWARE\\WOW6432Node\\EA Games", "EA", "Install Dir", "DisplayName");
    load_registry_roots(list, false, "SOFTWARE\\EA Games", "EA", "Install Dir", "DisplayName");
}

fn load_ubisoft(list: &mut Vec<GameInfo>) {
    load_registry_roots(
        list,
        true,
        "SOFTWARE\\WOW6432Node\\Ubisoft\\Launcher\\Installs",
        "Ubisoft",
        "InstallDir",
        "",
    );
    load_registry_roots(
        list,
        false,
        "SOFTWARE\\Ubisoft\\Launcher\\Installs",
        "Ubisoft",
        "InstallDir",
        "",
    );
}

fn load_battlenet(list: &mut Vec<GameInfo>) {
    load_registry_roots(
        list,
        true,
        "SOFTWARE\\WOW6432Node\\Blizzard Entertainment",
        "Battle.net",
        "InstallPath",
        "",
    );
    load_registry_roots(
        list,
        false,
        "SOFTWARE\\Blizzard Entertainment",
        "Battle.net",
        "InstallPath",
        "",
    );
}

pub struct GameLibrary;

impl GameLibrary {
    pub fn load_all() -> Vec<GameInfo> {
        let mut list: Vec<GameInfo> = Vec::new();
        load_steam(&mut list);
        load_epic(&mut list);
        load_gog(&mut list);
        load_windows_games(&mut list);
        load_ea(&mut list);
        load_ubisoft(&mut list);
        load_battlenet(&mut list);
        load_xbox(&mut list);
        list.extend(SavedGames::load());

        let mut seen: HashSet<String> = HashSet::new();
        let mut result = Vec::new();
        for game in list {
            if game.root.is_empty() {
                continue;
            }
            if seen.insert(game.root.to_lowercase()) {
                result.push(game);
            }
        }
        result
    }

    pub fn match_path(exe_path: &str) -> Option<GameInfo> {
        if exe_path.is_empty() {
            return None;
        }
        let list = Self::load_all();
        for game in &list {
            if !game.exe_path.is_empty() && game.exe_path.eq_ignore_ascii_case(exe_path) {
                return Some(game.clone());
            }
        }
        let mut best: Option<&GameInfo> = None;
        for game in &list {
            if game.root.is_empty() {
                continue;
            }
            if let Some(current) = best {
                if game.root.len() <= current.root.len() {
                    continue;
                }
            }
            if crate::util::is_under(exe_path, &game.root) {
                best = Some(game);
            }
        }
        best.cloned()
    }
}

pub struct SavedGames;

impl SavedGames {
    pub fn file_path() -> PathBuf {
        let base = std::env::var("LOCALAPPDATA").unwrap_or_default();
        Path::new(&base).join("GameApiDetector").join("games.tsv")
    }

    pub fn load() -> Vec<GameInfo> {
        let mut list = Vec::new();
        let file = Self::file_path();
        let text = match fs::read_to_string(&file) {
            Ok(t) => t,
            Err(_) => return list,
        };
        let mut seen: HashSet<String> = HashSet::new();
        for line in text.lines() {
            if line.trim().is_empty() {
                continue;
            }
            let parts: Vec<&str> = line.split('\t').collect();
            if parts.len() < 3 {
                continue;
            }
            let platform = parts[0].trim().to_string();
            let name = parts[1].trim().to_string();
            let exe = parts[2].trim().to_string();
            if exe.is_empty() || !seen.insert(exe.to_lowercase()) {
                continue;
            }
            if !Path::new(&exe).is_file() {
                continue;
            }
            let stem = Path::new(&exe)
                .file_stem()
                .map(|s| s.to_string_lossy().to_string())
                .unwrap_or_default();
            list.push(GameInfo {
                name: if name.is_empty() { stem } else { name },
                platform: if platform.is_empty() { "本地".to_string() } else { platform },
                root: dir_of(&exe),
                exe_path: exe,
            });
        }
        list
    }

    pub fn add(exe_path: &str, name: &str, platform: &str) -> bool {
        let exe = exe_path.trim().to_string();
        if exe.is_empty() || !Path::new(&exe).is_file() {
            return false;
        }
        let mut list = Self::load();
        let mut changed = false;
        let mut found = false;
        for game in list.iter_mut() {
            if !game.exe_path.eq_ignore_ascii_case(&exe) {
                continue;
            }
            found = true;
            if !name.is_empty() && game.name != name {
                game.name = name.to_string();
                changed = true;
            }
            if !platform.is_empty() && game.platform != platform {
                game.platform = platform.to_string();
                changed = true;
            }
            break;
        }
        if !found {
            let stem = Path::new(&exe)
                .file_stem()
                .map(|s| s.to_string_lossy().to_string())
                .unwrap_or_default();
            list.push(GameInfo {
                name: if name.is_empty() { stem } else { name.to_string() },
                platform: if platform.is_empty() { "本地".to_string() } else { platform.to_string() },
                root: dir_of(&exe),
                exe_path: exe,
            });
            changed = true;
        }
        if changed {
            Self::write_all(&list);
        }
        changed
    }

    pub fn remove(exe_path: &str) -> bool {
        let mut list = Self::load();
        let before = list.len();
        list.retain(|g| !g.exe_path.eq_ignore_ascii_case(exe_path));
        if list.len() == before {
            return false;
        }
        Self::write_all(&list);
        true
    }

    fn write_all(list: &[GameInfo]) {
        let file = Self::file_path();
        if let Some(dir) = file.parent() {
            let _ = fs::create_dir_all(dir);
        }
        let mut out = String::new();
        for game in list {
            out.push_str(&game.platform.replace('\t', " "));
            out.push('\t');
            out.push_str(&game.name.replace('\t', " "));
            out.push('\t');
            out.push_str(&game.exe_path);
            out.push_str("\r\n");
        }
        let _ = fs::write(&file, out);
    }
}

pub struct GameExe;

impl GameExe {
    const NOISE: [&'static str; 16] = [
        "unins", "uninstall", "setup", "install", "vcredist", "dxsetup", "directx", "dotnet",
        "crash", "report", "updater", "update", "helper", "activation", "eula", "touchup",
    ];

    const SKIP_DIRS: [&'static str; 6] = ["_commonredist", "redist", "directx", "support", "docs", "tools"];

    pub fn locate(game: &GameInfo) -> String {
        if !game.exe_path.is_empty() && Path::new(&game.exe_path).is_file() {
            return game.exe_path.clone();
        }
        if game.root.is_empty() || !Path::new(&game.root).is_dir() {
            return String::new();
        }
        Self::search(Path::new(&game.root), &normalize(&game.name), 0)
    }

    fn search(dir: &Path, target: &str, depth: usize) -> String {
        let mut best = String::new();
        let mut best_size: u64 = 0;
        let folder = normalize(
            &dir.file_name()
                .map(|s| s.to_string_lossy().to_string())
                .unwrap_or_default(),
        );

        if let Ok(entries) = fs::read_dir(dir) {
            for entry in entries.flatten() {
                let path = entry.path();
                if !path.is_file() {
                    continue;
                }
                if path.extension().map(|e| e.to_string_lossy().to_lowercase()) != Some("exe".to_string()) {
                    continue;
                }
                let stem = normalize(
                    &path
                        .file_stem()
                        .map(|s| s.to_string_lossy().to_string())
                        .unwrap_or_default(),
                );
                if Self::is_noise(&stem) {
                    continue;
                }
                if !target.is_empty() && stem == target {
                    return path.to_string_lossy().to_string();
                }
                if !folder.is_empty() && stem == folder {
                    return path.to_string_lossy().to_string();
                }
                let size = entry.metadata().map(|m| m.len()).unwrap_or(0);
                if size > best_size || best.is_empty() {
                    best_size = size;
                    best = path.to_string_lossy().to_string();
                }
            }
        }
        if !best.is_empty() {
            return best;
        }
        if depth >= 2 {
            return String::new();
        }
        if let Ok(entries) = fs::read_dir(dir) {
            for entry in entries.flatten() {
                let path = entry.path();
                if !path.is_dir() {
                    continue;
                }
                let name = entry.file_name().to_string_lossy().to_lowercase();
                if Self::SKIP_DIRS.contains(&name.as_str()) {
                    continue;
                }
                let found = Self::search(&path, target, depth + 1);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        String::new()
    }

    fn is_noise(stem: &str) -> bool {
        Self::NOISE.iter().any(|n| stem.contains(n))
    }
}

fn normalize(value: &str) -> String {
    value
        .chars()
        .filter(|c| c.is_alphanumeric())
        .flat_map(|c| c.to_lowercase())
        .collect()
}
