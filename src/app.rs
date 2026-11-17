use std::collections::HashSet;
use std::path::Path;
use std::sync::mpsc::{channel, Receiver};
use std::time::{Duration, Instant};

use eframe::egui;

use crate::dxvk;
use crate::library::{GameExe, GameInfo, GameLibrary, SavedGames};
use crate::process::{self, Row, API_NO_EXE, API_NOT_RUNNING};

const ACCENT: egui::Color32 = egui::Color32::from_rgb(13, 110, 105);
const ACCENT_HOVER: egui::Color32 = egui::Color32::from_rgb(9, 92, 88);
const SURFACE: egui::Color32 = egui::Color32::from_rgb(247, 249, 251);
const BORDER: egui::Color32 = egui::Color32::from_rgb(226, 232, 240);
const MUTED: egui::Color32 = egui::Color32::from_rgb(100, 116, 139);
const TEXT: egui::Color32 = egui::Color32::from_rgb(30, 41, 59);

enum Msg {
    Row(Row),
    Done,
}

struct LaunchState {
    game: String,
    exe: String,
    work_dir: String,
    preset: usize,
    args: String,
    kill_existing: bool,
    auto_detect: bool,
}

struct DxvkState {
    game: String,
    game_dir: String,
    src_dir: String,
    exe_name: String,
    checked: [bool; 5],
    state_text: String,
    is32: bool,
}

struct Watch {
    exe_name: String,
    dir: String,
    until: Instant,
    baseline: HashSet<u32>,
}

enum DialogState {
    None,
    LocalGames,
    Launch(LaunchState),
    Dxvk(DxvkState),
}

pub struct App {
    rows: Vec<Row>,
    selected: Option<usize>,
    search: String,
    status: String,
    scanning: bool,
    rx: Option<Receiver<Msg>>,
    dialog: DialogState,
    local_games: Vec<GameInfo>,
    watch: Option<Watch>,
    confirm_restore: Option<(String, String)>,
    local_dirty: bool,
}

impl App {
    pub fn new(cc: &eframe::CreationContext<'_>) -> Self {
        let mut visuals = egui::Visuals::light();
        visuals.panel_fill = egui::Color32::WHITE;
        visuals.window_fill = egui::Color32::WHITE;
        visuals.extreme_bg_color = SURFACE;
        visuals.faint_bg_color = SURFACE;
        visuals.override_text_color = Some(TEXT);
        visuals.selection.bg_fill = ACCENT.linear_multiply(0.18);
        visuals.widgets.inactive.bg_fill = egui::Color32::WHITE;
        visuals.widgets.inactive.weak_bg_fill = egui::Color32::WHITE;
        visuals.widgets.inactive.bg_stroke = egui::Stroke::new(1.0, BORDER);
        visuals.widgets.hovered.bg_stroke = egui::Stroke::new(1.0, ACCENT);
        cc.egui_ctx.set_visuals(visuals);

        let mut style = (*cc.egui_ctx.style()).clone();
        style.spacing.item_spacing = egui::vec2(8.0, 6.0);
        style.spacing.button_padding = egui::vec2(10.0, 5.0);
        style.text_styles.insert(
            egui::TextStyle::Body,
            egui::FontId::new(14.0, egui::FontFamily::Proportional),
        );
        style.text_styles.insert(
            egui::TextStyle::Button,
            egui::FontId::new(14.0, egui::FontFamily::Proportional),
        );
        cc.egui_ctx.set_style(style);

        let mut app = App {
            rows: Vec::new(),
            selected: None,
            search: String::new(),
            status: "正在准备…".to_string(),
            scanning: false,
            rx: None,
            dialog: DialogState::None,
            local_games: SavedGames::load(),
            watch: None,
            confirm_restore: None,
            local_dirty: false,
        };
        app.start_scan(&cc.egui_ctx);
        app
    }

    fn start_scan(&mut self, ctx: &egui::Context) {
        if self.scanning {
            return;
        }
        self.scanning = true;
        self.rows.clear();
        self.selected = None;
        self.status = "正在扫描本地游戏库…".to_string();

        let (tx, rx) = channel();
        self.rx = Some(rx);
        let ctx = ctx.clone();

        std::thread::spawn(move || {
            let games = GameLibrary::load_all();
            let procs = process::enumerate_processes();
            let mut index = 0i32;
            for game in games {
                let mut row = Row::default();
                row.is_game = true;
                row.game_known = true;
                row.platform = game.platform.clone();
                row.path = GameExe::locate(&game);

                if !row.path.is_empty() {
                    row.name = Path::new(&row.path)
                        .file_stem()
                        .map(|s| s.to_string_lossy().to_string())
                        .unwrap_or_default();
                    row.game_name = if game.name.is_empty() {
                        row.name.clone()
                    } else {
                        game.name.clone()
                    };
                    row.arch = "—".to_string();
                    row.api = API_NOT_RUNNING.to_string();
                    row.dxvk = dxvk::probe(&row.path);
                } else {
                    row.game_name = if game.name.is_empty() {
                        Path::new(game.root.trim_end_matches('\\'))
                            .file_stem()
                            .map(|s| s.to_string_lossy().to_string())
                            .unwrap_or_default()
                    } else {
                        game.name.clone()
                    };
                    row.name = row.game_name.clone();
                    row.arch = "—".to_string();
                    row.api = API_NO_EXE.to_string();
                    row.error =
                        "未能在安装目录中找到游戏主程序，可点击「本地游戏」手动添加".to_string();
                }

                if row.path.is_empty() && row.platform != "本地" {
                    continue;
                }
                row.pid = -1 - index;
                index += 1;

                if !row.path.is_empty() && !row.name.is_empty() {
                    for (pid, name) in &procs {
                        if !name.eq_ignore_ascii_case(&row.name) {
                            continue;
                        }
                        let live_path = process::read_path(*pid);
                        if !live_path.is_empty() && !live_path.eq_ignore_ascii_case(&row.path) {
                            continue;
                        }
                        let live = process::inspect(*pid);
                        row.run_pid = *pid;
                        row.merge_live(&live);
                        break;
                    }
                }

                if tx.send(Msg::Row(row)).is_err() {
                    break;
                }
                ctx.request_repaint();
            }
            let _ = tx.send(Msg::Done);
            ctx.request_repaint();
        });
    }

    fn poll_scan(&mut self, ctx: &egui::Context) {
        let mut finished = false;
        if let Some(rx) = &self.rx {
            loop {
                match rx.try_recv() {
                    Ok(Msg::Row(row)) => self.rows.push(row),
                    Ok(Msg::Done) => {
                        finished = true;
                        break;
                    }
                    Err(_) => break,
                }
            }
        }
        if finished {
            self.rx = None;
            self.scanning = false;
            self.sort_rows();
            self.status = "扫描完成".to_string();
            self.local_games = SavedGames::load();
        } else if self.scanning {
            ctx.request_repaint_after(Duration::from_millis(80));
        }
    }

    fn sort_rows(&mut self) {
        self.rows.sort_by(|a, b| {
            b.running()
                .cmp(&a.running())
                .then_with(|| {
                    let left = a.game_text().to_lowercase();
                    let right = b.game_text().to_lowercase();
                    left.cmp(&right)
                })
        });
    }

    fn filtered(&self) -> Vec<usize> {
        let needle = self.search.trim().to_lowercase();
        let mut result = Vec::new();
        for (i, row) in self.rows.iter().enumerate() {
            if needle.is_empty() {
                result.push(i);
                continue;
            }
            let haystack = format!(
                "{} {} {} {} {}",
                row.game_text(),
                row.platform_text(),
                row.api,
                row.dxvk,
                row.path
            )
            .to_lowercase();
            if haystack.contains(&needle) {
                result.push(i);
            }
        }
        result
    }

    fn rescan_entry(&mut self, index: usize) {
        if index >= self.rows.len() {
            return;
        }
        let mut pid = self.rows[index].run_pid;
        if pid == 0 {
            pid = process::find_running_pid(&self.rows[index].path, &self.rows[index].name);
        }
        if pid == 0 {
            self.rows[index].reset_live();
            self.status = "该游戏当前未运行，可点击「启动游戏」后重新扫描".to_string();
            return;
        }
        let live = process::inspect(pid);
        self.rows[index].run_pid = pid;
        self.rows[index].merge_live(&live);
        let name = self.rows[index].game_text();
        let api = self.rows[index].api.clone();
        self.status = format!("已重新检测 {}（{}）", name, api);
    }

    fn selected_row(&self) -> Option<&Row> {
        self.selected.and_then(|i| self.rows.get(i))
    }

    fn launch_game(&mut self) {
        let row = match self.selected_row() {
            Some(r) => r,
            None => {
                self.status = "请先在列表中选择要启动的游戏".to_string();
                return;
            }
        };
        if row.path.is_empty() || !Path::new(&row.path).is_file() {
            self.status = "未找到该游戏的可执行文件，请先在「本地游戏」中手动添加".to_string();
            return;
        }
        if row.run_pid > 0 {
            self.status = "该游戏已经在运行。若要重新检测，请点击「扫描游戏」".to_string();
            return;
        }
        let dir = row.install_dir();
        let path = row.path.clone();
        match process::start(&path, "", &dir) {
            Ok(_) => {
                self.status = format!("已启动 {}，进入游戏后点击「扫描游戏」检测图形 API", row.game_text())
            }
            Err(err) => self.status = format!("启动失败：{}", err),
        }
    }

    fn open_launch_dialog(&mut self) {
        let row = match self.selected_row() {
            Some(r) => r,
            None => {
                self.status = "请先在列表中选择要启动的游戏".to_string();
                return;
            }
        };
        if row.path.is_empty() || !Path::new(&row.path).is_file() {
            self.status = "未找到该游戏的可执行文件，请先在「本地游戏」中手动添加".to_string();
            return;
        }
        self.dialog = DialogState::Launch(LaunchState {
            game: row.game_text(),
            exe: row.path.clone(),
            work_dir: row.install_dir(),
            preset: 0,
            args: "-vulkan".to_string(),
            kill_existing: false,
            auto_detect: true,
        });
    }

    fn open_dxvk_dialog(&mut self) {
        let row = match self.selected_row() {
            Some(r) => r,
            None => {
                self.status = "请先在列表中选择要安装 DXVK 的游戏".to_string();
                return;
            }
        };
        if row.path.is_empty() || !Path::new(&row.path).is_file() {
            self.status = "无法获取该游戏的可执行文件路径".to_string();
            return;
        }
        if dxvk::is_installed(&row.path) {
            let dir = dxvk::safe_dir(&row.path);
            self.confirm_restore = Some((row.game_text(), dir));
            return;
        }
        let is32 = row.arch.contains("32");
        let mut checked = [true; 5];
        checked[0] = false;
        self.dialog = DialogState::Dxvk(DxvkState {
            game: row.game_text(),
            game_dir: row.install_dir(),
            src_dir: default_dxvk_source(is32),
            exe_name: row.name.clone(),
            checked,
            state_text: String::new(),
            is32,
        });
    }

    fn confirm_restore_now(&mut self, dir: &str) {
        let (restored, errors) = dxvk::restore(dir);
        let mut text = if restored > 0 {
            format!("已还原 {} 个文件，游戏将回到原生 Direct3D。", restored)
        } else {
            "未找到可还原的 DXVK 文件。".to_string()
        };
        if !errors.is_empty() {
            text.push_str(&format!("\r\n失败：{}", errors.join("；")));
        }
        self.status = text;
        if let Some(index) = self.selected {
            if index < self.rows.len() {
                let path = self.rows[index].path.clone();
                self.rows[index].dxvk = dxvk::probe(&path);
            }
        }
    }

    fn start_watch(&mut self, exe: &str) {
        let name = Path::new(exe)
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_default();
        let dir = dxvk::safe_dir(exe);
        let baseline: HashSet<u32> = process::enumerate_processes()
            .iter()
            .map(|(pid, _)| *pid)
            .collect();
        self.watch = Some(Watch {
            exe_name: name,
            dir,
            until: Instant::now() + Duration::from_secs(30),
            baseline,
        });
    }

    fn tick_watch(&mut self, ctx: &egui::Context) {
        let (exe_name, until, baseline) = match &self.watch {
            Some(w) => (w.exe_name.clone(), w.until, w.baseline.clone()),
            None => return,
        };

        let mut done = false;
        if Instant::now() > until {
            self.status =
                "30 秒内未检测到 Vulkan；游戏可能不支持该参数，或需要从平台启动".to_string();
            done = true;
        } else {
            let mut hit: Option<(u32, Row)> = None;
            for (pid, name) in process::enumerate_processes() {
                if baseline.contains(&pid) || !name.eq_ignore_ascii_case(&exe_name) {
                    continue;
                }
                let live = process::inspect(pid);
                if live.has_graphics() {
                    hit = Some((pid, live));
                    break;
                }
            }
            if let Some((pid, live)) = hit {
                if let Some(index) = self
                    .rows
                    .iter()
                    .position(|r| !live.path.is_empty() && r.path.eq_ignore_ascii_case(&live.path))
                {
                    self.rows[index].run_pid = pid;
                    self.rows[index].merge_live(&live);
                    self.selected = Some(index);
                }
                if live.api.starts_with("Vulkan") {
                    self.status = format!("Vulkan 已生效：{}（{}）", live.name, live.key_modules);
                    done = true;
                } else {
                    self.status = format!("当前图形 API：{}", live.api);
                }
            }
        }

        if done {
            self.watch = None;
        } else {
            ctx.request_repaint_after(Duration::from_millis(600));
        }
    }

    fn detail_text(&self) -> String {
        let row = match self.selected_row() {
            Some(r) => r,
            None => return default_detail(),
        };
        let mut lines: Vec<String> = Vec::new();
        lines.push(format!("游戏：{}", row.game_text()));
        lines.push(format!("平台：{}", row.platform_text()));
        if row.run_pid > 0 {
            lines.push(format!("运行状态：正在运行（PID {}）", row.run_pid));
        } else {
            lines.push("运行状态：未运行".to_string());
        }
        lines.push(format!("图形 API：{}", row.api));
        lines.push(format!(
            "DXVK：{}",
            if row.dxvk.is_empty() { "未安装".to_string() } else { row.dxvk.clone() }
        ));
        lines.push(format!("架构：{}", row.arch));
        if !row.title.is_empty() {
            lines.push(format!("窗口标题：{}", row.title));
        }
        lines.push(format!("安装目录：{}", row.install_dir()));
        lines.push(format!("可执行文件：{}", row.path));
        if !row.key_modules.is_empty() {
            lines.push(format!("已加载图形模块：{}", row.key_modules));
        }
        if !row.error.is_empty() {
            lines.push(format!("提示：{}", row.error));
        }
        lines.join("\n")
    }

    fn status_line(&self) -> String {
        let running = self.rows.iter().filter(|r| r.running()).count();
        format!(
            "{}　|　本地游戏 {} 个（运行中 {}）　|　本地已保存 {} 个地址　|　双击游戏可重新检测",
            self.status,
            self.rows.len(),
            running,
            self.local_games.len()
        )
    }
}

fn default_detail() -> String {
    vec![
        "列表直接来自本地游戏库：Steam / Epic / GOG / EA / Ubisoft / Battle.net / Xbox，以及「本地游戏」中手动添加的第三方游戏。",
        "扫描只按游戏主程序路径匹配是否正在运行，不会把其他系统进程当作游戏展示。",
        "",
        "查看图形 API：先启动游戏，再点击「扫描游戏」；运行中的游戏会显示实际加载的图形 API，未运行的显示「未运行」。",
        "判定方式：读取该游戏进程已加载的图形运行库。",
        "  Vulkan      -> vulkan-1.dll",
        "  DirectX 12  -> d3d12.dll / d3d12core.dll",
        "  DirectX 11  -> d3d11.dll",
        "  OpenGL      -> opengl32.dll",
        "  DirectX 9   -> d3d9.dll",
        "",
        "DXVK 状态：检查游戏目录内 d3d8 / d3d9 / d3d10core / d3d11 / dxgi 的版本信息是否为 DXVK。",
    ]
    .join("\n")
}

fn default_dxvk_source(is32: bool) -> String {
    let candidates = [
        "D:\\BaiduNetdiskDownload\\不用但是有用\\dxvk-3.1.1",
        "C:\\dxvk",
        "D:\\dxvk",
    ];
    for root in candidates {
        let dir = Path::new(root).join(if is32 { "x32" } else { "x64" });
        if dir.is_dir() {
            return dir.to_string_lossy().to_string();
        }
        if Path::new(root).is_dir() {
            return root.to_string();
        }
    }
    String::new()
}

fn dxvk_badge(text: &str) -> String {
    let count = text.matches(" + ").count() + 1;
    format!("DXVK（{} 个 DLL）", count)
}

fn short_path(path: &str) -> String {
    let parts: Vec<&str> = path
        .split(['\\', '/'])
        .filter(|s| !s.is_empty())
        .collect();
    if parts.len() <= 3 {
        return truncate_chars(path, 32);
    }
    format!(
        "…\\{}",
        truncate_chars(&parts[parts.len() - 2..].join("\\"), 30)
    )
}

fn truncate_chars(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        text.to_string()
    } else {
        format!("{}…", text.chars().take(max).collect::<String>())
    }
}

fn api_color(api: &str) -> egui::Color32 {
    if api.starts_with("Vulkan") {
        egui::Color32::from_rgb(12, 132, 96)
    } else if api.starts_with("DirectX 12") {
        egui::Color32::from_rgb(37, 99, 235)
    } else if api.starts_with("DirectX 11") {
        egui::Color32::from_rgb(59, 130, 246)
    } else if api.starts_with("OpenGL") {
        egui::Color32::from_rgb(147, 51, 234)
    } else if api.starts_with("DirectX 10") {
        egui::Color32::from_rgb(217, 119, 6)
    } else if api.starts_with("DirectX 9") {
        egui::Color32::from_rgb(120, 113, 108)
    } else {
        MUTED
    }
}

fn primary_button(text: &str) -> egui::Button<'static> {
    egui::Button::new(egui::RichText::new(text.to_string()).color(egui::Color32::WHITE))
        .fill(ACCENT)
        .corner_radius(6)
        .min_size(egui::vec2(0.0, 30.0))
}

fn secondary_button(text: &str) -> egui::Button<'static> {
    egui::Button::new(egui::RichText::new(text.to_string()).color(TEXT))
        .fill(egui::Color32::WHITE)
        .stroke(egui::Stroke::new(1.0, BORDER))
        .corner_radius(6)
        .min_size(egui::vec2(0.0, 30.0))
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.poll_scan(ctx);
        self.tick_watch(ctx);

        egui::TopBottomPanel::top("toolbar")
            .exact_height(58.0)
            .frame(egui::Frame::new().fill(egui::Color32::WHITE).inner_margin(egui::Margin::symmetric(12, 14)))
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    let scan = ui.add_enabled(!self.scanning, primary_button("扫描游戏"));
                    if scan.clicked() {
                        self.start_scan(ctx);
                    }
                    if ui
                        .add_enabled(!self.rows.is_empty(), secondary_button("以 Vulkan 启动"))
                        .clicked()
                    {
                        self.open_launch_dialog();
                    }
                    if ui
                        .add_enabled(!self.rows.is_empty(), secondary_button("安装 DXVK"))
                        .clicked()
                    {
                        self.open_dxvk_dialog();
                    }
                    ui.add_space(6.0);
                    if ui.add(secondary_button("本地游戏")).clicked() {
                        self.local_games = SavedGames::load();
                        self.dialog = DialogState::LocalGames;
                    }
                    if ui
                        .add_enabled(!self.rows.is_empty(), secondary_button("启动游戏"))
                        .clicked()
                    {
                        self.launch_game();
                    }

                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.add_sized(
                            [220.0, 28.0],
                            egui::TextEdit::singleline(&mut self.search)
                                .hint_text("搜索游戏名 / 平台 / API"),
                        );
                    });
                });
            });

        egui::TopBottomPanel::bottom("status")
            .exact_height(30.0)
            .frame(egui::Frame::new().fill(egui::Color32::WHITE).inner_margin(egui::Margin::symmetric(14, 0)))
            .show(ctx, |ui| {
                ui.centered_and_justified(|ui| {
                    ui.label(egui::RichText::new(self.status_line()).size(12.5).color(MUTED));
                });
            });

        let available = ctx.available_rect().height();
        let detail_height = 210.0_f32.min((available * 0.38).max(160.0));
        egui::TopBottomPanel::bottom("detail")
            .exact_height(detail_height)
            .frame(
                egui::Frame::new()
                    .fill(SURFACE)
                    .inner_margin(egui::Margin::symmetric(14, 10)),
            )
            .show(ctx, |ui| {
                egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| {
                    ui.label(
                        egui::RichText::new(self.detail_text())
                            .size(13.0)
                            .color(TEXT)
                            .family(egui::FontFamily::Monospace),
                    );
                });
            });

        egui::CentralPanel::default()
            .frame(
                egui::Frame::new()
                    .fill(egui::Color32::WHITE)
                    .inner_margin(egui::Margin { left: 14, right: 6, top: 4, bottom: 0 }),
            )
            .show(ctx, |ui| {
                self.draw_table(ui);
            });

        self.draw_dialogs(ctx);
    }
}

impl App {
    fn draw_table(&mut self, ui: &mut egui::Ui) {
        use egui_extras::{Column, TableBuilder};

        let visible = self.filtered();
        let mut clicked: Option<usize> = None;
        let mut double_clicked: Option<usize> = None;

        TableBuilder::new(ui)
            .striped(true)
            .resizable(false)
            .sense(egui::Sense::click())
            .cell_layout(egui::Layout::left_to_right(egui::Align::Center))
            .column(Column::initial(200.0).at_least(140.0))
            .column(Column::initial(82.0).at_least(60.0))
            .column(Column::initial(78.0).at_least(60.0))
            .column(Column::initial(180.0).at_least(120.0))
            .column(Column::initial(220.0).at_least(120.0).clip(true))
            .column(Column::remainder().at_least(140.0).clip(true))
            .header(26.0, |mut header| {
                for title in ["游戏", "平台", "运行状态", "图形 API", "DXVK"] {
                    header.col(|ui| {
                        ui.label(egui::RichText::new(title).strong().size(13.0).color(TEXT));
                    });
                }
                header.col(|ui| {
                    ui.label(egui::RichText::new("安装目录").strong().size(13.0).color(TEXT));
                });
            })
            .body(|mut body| {
                body.rows(28.0, visible.len(), |mut row| {
                    let index = visible[row.index()];
                    let entry = &self.rows[index];
                    row.set_selected(self.selected == Some(index));

                    row.col(|ui| {
                        ui.label(egui::RichText::new(entry.game_text()).size(13.5));
                    });
                    row.col(|ui| {
                        ui.label(egui::RichText::new(entry.platform_text()).size(13.0).color(MUTED));
                    });
                    row.col(|ui| {
                        let (text, color) = if entry.run_pid > 0 {
                            (format!("运行中 {}", entry.run_pid), egui::Color32::from_rgb(12, 132, 96))
                        } else {
                            ("未运行".to_string(), MUTED)
                        };
                        ui.label(egui::RichText::new(text).size(13.0).color(color));
                    });
                    row.col(|ui| {
                        ui.label(
                            egui::RichText::new(entry.api.clone())
                                .size(13.0)
                                .color(api_color(&entry.api)),
                        );
                    });
                    row.col(|ui| {
                        if entry.dxvk.is_empty() {
                            ui.label(egui::RichText::new("未安装").size(13.0).color(MUTED));
                        } else {
                            ui.label(
                                egui::RichText::new(dxvk_badge(&entry.dxvk))
                                    .size(13.0)
                                    .color(egui::Color32::from_rgb(12, 132, 96)),
                            )
                            .on_hover_text(&entry.dxvk);
                        }
                    });
                    row.col(|ui| {
                        let dir = entry.install_dir();
                        ui.add(
                            egui::Label::new(
                                egui::RichText::new(short_path(&dir)).size(12.5).color(MUTED),
                            )
                            .truncate(),
                        )
                        .on_hover_ui(|ui| {
                            ui.label(egui::RichText::new(&dir).size(12.0));
                            ui.label(
                                egui::RichText::new(&entry.name).size(12.0).color(MUTED),
                            );
                        });
                    });

                    let response = row.response();
                    if response.clicked() {
                        clicked = Some(index);
                    }
                    if response.double_clicked() {
                        double_clicked = Some(index);
                    }
                });
            });

        if let Some(index) = clicked {
            self.selected = Some(index);
        }
        if let Some(index) = double_clicked {
            self.selected = Some(index);
            self.rescan_entry(index);
        }
    }

    fn draw_dialogs(&mut self, ctx: &egui::Context) {
        let mut dialog = std::mem::replace(&mut self.dialog, DialogState::None);
        let mut keep = true;
        match &mut dialog {
            DialogState::None => {}
            DialogState::LocalGames => {
                let mut open = true;
                let mut close = false;
                let mut add_path: Option<String> = None;
                let mut remove_path: Option<String> = None;
                let mut launch: Option<String> = None;

                egui::Window::new("本地游戏")
                    .open(&mut open)
                    .collapsible(false)
                    .resizable(true)
                    .default_width(560.0)
                    .show(ctx, |ui| {
                        ui.label(
                            egui::RichText::new(
                                "手动添加第三方游戏的可执行文件，添加后会在扫描时一并检测。",
                            )
                            .size(12.5)
                            .color(MUTED),
                        );
                        ui.add_space(6.0);
                        egui::ScrollArea::vertical()
                            .max_height(260.0)
                            .auto_shrink([false, false])
                            .show(ui, |ui| {
                                if self.local_games.is_empty() {
                                    ui.label(egui::RichText::new("暂无手动添加的游戏").color(MUTED));
                                }
                                for game in &self.local_games {
                                    ui.horizontal(|ui| {
                                        if ui
                                            .add(secondary_button("启动"))
                                            .clicked()
                                        {
                                            launch = Some(game.exe_path.clone());
                                        }
                                        if ui
                                            .add(secondary_button("移除"))
                                            .clicked()
                                        {
                                            remove_path = Some(game.exe_path.clone());
                                        }
                                        ui.label(egui::RichText::new(&game.name).size(13.5));
                                        ui.label(
                                            egui::RichText::new(&game.exe_path).size(12.0).color(MUTED),
                                        );
                                    });
                                    ui.separator();
                                }
                            });
                        ui.add_space(6.0);
                        ui.horizontal(|ui| {
                            if ui.add(primary_button("添加游戏…")).clicked() {
                                if let Some(path) = rfd::FileDialog::new()
                                    .add_filter("可执行文件", &["exe"])
                                    .pick_file()
                                {
                                    add_path = Some(path.to_string_lossy().to_string());
                                }
                            }
                            if ui.add(secondary_button("关闭")).clicked() {
                                close = true;
                            }
                        });
                    });

                if let Some(path) = add_path {
                    let stem = Path::new(&path)
                        .file_stem()
                        .map(|s| s.to_string_lossy().to_string())
                        .unwrap_or_default();
                    if SavedGames::add(&path, &stem, "本地") {
                        self.local_games = SavedGames::load();
                        self.local_dirty = true;
                        self.status = format!("已添加本地游戏：{}", stem);
                    }
                }
                if let Some(path) = remove_path {
                    if SavedGames::remove(&path) {
                        self.local_games = SavedGames::load();
                        self.local_dirty = true;
                        self.status = "已移除该本地游戏".to_string();
                    }
                }
                if let Some(path) = launch {
                    let dir = dxvk::safe_dir(&path);
                    match process::start(&path, "", &dir) {
                        Ok(_) => self.status = "已启动所选游戏".to_string(),
                        Err(err) => self.status = format!("启动失败：{}", err),
                    }
                }
                if close || !open {
                    keep = false;
                    if self.local_dirty {
                        self.local_dirty = false;
                        self.start_scan(ctx);
                    }
                }
            }
            DialogState::Launch(state) => {
                let mut open = true;
                let mut do_launch = false;
                let mut cancel = false;

                egui::Window::new(format!("以 Vulkan 启动 - {}", state.game))
                    .open(&mut open)
                    .collapsible(false)
                    .resizable(false)
                    .default_width(620.0)
                    .show(ctx, |ui| {
                        egui::Grid::new("launch_grid")
                            .num_columns(2)
                            .spacing([10.0, 8.0])
                            .show(ui, |ui| {
                                ui.label("程序：");
                                ui.add_sized(
                                    [470.0, 24.0],
                                    egui::TextEdit::singleline(&mut state.exe.clone()).interactive(false),
                                );
                                ui.end_row();

                                ui.label("工作目录：");
                                ui.add_sized([470.0, 24.0], egui::TextEdit::singleline(&mut state.work_dir));
                                ui.end_row();

                                ui.label("参数预设：");
                                egui::ComboBox::from_id_salt("preset")
                                    .selected_text(match state.preset {
                                        1 => "-force-vulkan（Unity 等引擎）",
                                        2 => "--vulkan（部分 Linux 移植游戏）",
                                        3 => "自定义（手动输入）",
                                        _ => "-vulkan（通用写法）",
                                    })
                                    .show_ui(ui, |ui| {
                                        if ui
                                            .selectable_value(&mut state.preset, 0, "-vulkan（通用写法）")
                                            .clicked()
                                        {
                                            state.args = "-vulkan".to_string();
                                        }
                                        if ui
                                            .selectable_value(
                                                &mut state.preset,
                                                1,
                                                "-force-vulkan（Unity 等引擎）",
                                            )
                                            .clicked()
                                        {
                                            state.args = "-force-vulkan".to_string();
                                        }
                                        if ui
                                            .selectable_value(
                                                &mut state.preset,
                                                2,
                                                "--vulkan（部分 Linux 移植游戏）",
                                            )
                                            .clicked()
                                        {
                                            state.args = "--vulkan".to_string();
                                        }
                                        ui.selectable_value(&mut state.preset, 3, "自定义（手动输入）");
                                    });
                                ui.end_row();

                                ui.label("启动参数：");
                                ui.horizontal(|ui| {
                                    ui.add_sized([300.0, 24.0], egui::TextEdit::singleline(&mut state.args));
                                    if ui.add(secondary_button("复制参数")).clicked() {
                                        ui.ctx().copy_text(state.args.clone());
                                    }
                                });
                                ui.end_row();
                            });

                        ui.add_space(6.0);
                        ui.label(
                            egui::RichText::new(
                                "1. 只有游戏本身支持 Vulkan 时该方法才会生效，例如《赛博朋克2077》《DOOM》《荒野大镖客2》。\n2. 参数写法因引擎而异，可依次尝试 -vulkan / -force-vulkan / --vulkan。\n3. Steam / Epic / Xbox 游戏建议在平台「启动项」中填写参数，本窗口负责直接启动 exe。\n4. 本方式不修改任何游戏文件，可随时回退。",
                            )
                            .size(12.0)
                            .color(MUTED),
                        );
                        ui.add_space(6.0);
                        ui.checkbox(&mut state.kill_existing, "结束当前已运行的该进程后再启动");
                        ui.checkbox(&mut state.auto_detect, "启动后自动检测 30 秒，确认 Vulkan 是否生效");

                        ui.add_space(8.0);
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            if ui.add(secondary_button("取消")).clicked() {
                                cancel = true;
                            }
                            if ui.add(primary_button("启动")).clicked() {
                                do_launch = true;
                            }
                        });
                    });

                if cancel || !open {
                    keep = false;
                } else if do_launch {
                    if !Path::new(&state.exe).is_file() {
                        self.status = "程序文件不存在，请确认路径是否正确".to_string();
                    } else {
                        if state.kill_existing {
                            let name = Path::new(&state.exe)
                                .file_stem()
                                .map(|s| s.to_string_lossy().to_string())
                                .unwrap_or_default();
                            let pid = process::find_running_pid(&state.exe, &name);
                            if pid > 0 {
                                if let Err(err) = process::kill(pid) {
                                    self.status = format!("结束原进程失败：{}", err);
                                }
                            }
                            if let Some(index) = self.selected {
                                self.rows[index].reset_live();
                            }
                        }
                        match process::start(&state.exe, &state.args, &state.work_dir) {
                            Ok(_) => {
                                self.status = format!("已启动 {}", state.game);
                                if state.auto_detect {
                                    self.start_watch(&state.exe);
                                }
                                keep = false;
                            }
                            Err(err) => {
                                self.status = format!(
                                    "启动失败：{}。可尝试右键以管理员身份运行本程序",
                                    err
                                );
                            }
                        }
                    }
                }
            }
            DialogState::Dxvk(state) => {
                let mut open = true;
                let mut close = false;
                let mut install = false;
                let mut restore = false;
                let mut browse = false;

                egui::Window::new(format!("安装 DXVK（Direct3D 转 Vulkan） - {}", state.game))
                    .open(&mut open)
                    .collapsible(false)
                    .resizable(false)
                    .default_width(640.0)
                    .show(ctx, |ui| {
                        egui::Grid::new("dxvk_grid")
                            .num_columns(2)
                            .spacing([10.0, 8.0])
                            .show(ui, |ui| {
                                ui.label("游戏目录：");
                                ui.add_sized(
                                    [520.0, 24.0],
                                    egui::TextEdit::singleline(&mut state.game_dir.clone())
                                        .interactive(false),
                                );
                                ui.end_row();

                                ui.label("DXVK 目录：");
                                ui.horizontal(|ui| {
                                    ui.add_sized([400.0, 24.0], egui::TextEdit::singleline(&mut state.src_dir));
                                    if ui.add(secondary_button("浏览…")).clicked() {
                                        browse = true;
                                    }
                                });
                                ui.end_row();
                            });

                        ui.add_space(4.0);
                        ui.label(
                            egui::RichText::new(
                                "复制到游戏根目录的文件（DX11 需 d3d11.dll + dxgi.dll，DX9 需 d3d9.dll）：",
                            )
                            .size(12.5)
                            .color(TEXT),
                        );
                        for (i, name) in dxvk::CANDIDATES.iter().enumerate() {
                            ui.checkbox(&mut state.checked[i], *name);
                        }

                        ui.add_space(4.0);
                        ui.label(
                            egui::RichText::new(
                                "警告：使用反作弊的联机游戏（EAC / BattlEye / VAC 等）请勿安装，可能被判定为篡改客户端。\nDXVK 只能把 DirectX 8/9/10/11 转为 Vulkan，DX12 游戏无效。",
                            )
                            .size(12.0)
                            .color(egui::Color32::from_rgb(178, 34, 34)),
                        );
                        ui.add_space(4.0);
                        ui.label(
                            egui::RichText::new(
                                "1. 同名原文件会自动备份为 *.dxvk_bak，可用「恢复原版」还原。\n2. 安装前请关闭游戏；安装后重启游戏，再扫描确认是否显示 Vulkan。\n3. Steam / Epic 的「验证文件完整性」会覆盖这些 DLL，需要重新安装。",
                            )
                            .size(12.0)
                            .color(MUTED),
                        );
                        if !state.state_text.is_empty() {
                            ui.add_space(4.0);
                            ui.label(
                                egui::RichText::new(&state.state_text)
                                    .size(12.5)
                                    .color(egui::Color32::from_rgb(20, 110, 60)),
                            );
                        }

                        ui.add_space(8.0);
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            if ui.add(secondary_button("关闭")).clicked() {
                                close = true;
                            }
                            if ui.add(secondary_button("恢复原版")).clicked() {
                                restore = true;
                            }
                            if ui.add(primary_button("安装到游戏目录")).clicked() {
                                install = true;
                            }
                        });
                    });

                if browse {
                    if let Some(dir) = rfd::FileDialog::new().pick_folder() {
                        state.src_dir = dir.to_string_lossy().to_string();
                    }
                }
                if restore {
                    let dir = state.game_dir.clone();
                    if dxvk::installed_files(&dir).is_empty()
                        && !Path::new(&dir).join(".dxvk_by_gad.txt").exists()
                    {
                        state.state_text = "游戏目录中未发现 DXVK 文件，无需还原。".to_string();
                    } else if process::is_running(&state.exe_name) {
                        state.state_text = "游戏仍在运行，请先关闭游戏后再恢复。".to_string();
                    } else {
                        let (restored, errors) = dxvk::restore(&dir);
                        state.state_text = format!("已恢复 {} 个文件。", restored);
                        if !errors.is_empty() {
                            state.state_text.push_str(&format!("失败：{}", errors.join("；")));
                        }
                        if let Some(index) = self.selected {
                            let path = self.rows[index].path.clone();
                            self.rows[index].dxvk = dxvk::probe(&path);
                        }
                    }
                }
                if install {
                    let mut files: Vec<String> = Vec::new();
                    for (i, name) in dxvk::CANDIDATES.iter().enumerate() {
                        if state.checked[i] {
                            files.push(name.to_string());
                        }
                    }
                    if files.is_empty() {
                        state.state_text = "请至少勾选一个要复制的 DLL。".to_string();
                    } else if !Path::new(&state.game_dir).is_dir() {
                        state.state_text = "游戏目录不存在。".to_string();
                    } else if !Path::new(&state.src_dir).is_dir() {
                        state.state_text = "DXVK 目录不存在，请重新选择。".to_string();
                    } else if (files.iter().any(|f| f == "d3d11.dll" || f == "d3d10core.dll"))
                        && !files.iter().any(|f| f == "dxgi.dll")
                    {
                        state.state_text = "DX10 / DX11 游戏必须同时复制 dxgi.dll。".to_string();
                    } else if state.is32 && state.src_dir.to_lowercase().contains("x64") {
                        state.state_text = "当前游戏是 32 位进程，应使用 x32 目录的 DLL。".to_string();
                    } else if process::is_running(&state.exe_name) {
                        state.state_text = "游戏仍在运行，请先关闭游戏后再安装。".to_string();
                    } else {
                        let (installed, errors) =
                            dxvk::install(&state.game_dir, &state.src_dir, &files);
                        let mut text = format!("已安装 {} 个文件。", installed.len());
                        if !installed.is_empty() {
                            text.push_str("\n请重启游戏，再扫描确认是否显示 Vulkan。");
                        }
                        if !errors.is_empty() {
                            text.push_str(&format!("\n失败：{}", errors.join("；")));
                        }
                        state.state_text = text;
                        if let Some(index) = self.selected {
                            let path = self.rows[index].path.clone();
                            self.rows[index].dxvk = dxvk::probe(&path);
                        }
                    }
                }
                if close || !open {
                    keep = false;
                }
            }
        }

        if keep {
            self.dialog = dialog;
        }

        if let Some((game, dir)) = self.confirm_restore.clone() {
            let mut yes = false;
            let mut no = false;
            egui::Window::new("还原 DXVK")
                .collapsible(false)
                .resizable(false)
                .default_width(460.0)
                .show(ctx, |ui| {
                    ui.label(format!("将从以下目录移除 DXVK DLL，并还原备份的原文件：\n{}", dir));
                    ui.label(egui::RichText::new(&game).size(12.0).color(MUTED));
                    ui.add_space(8.0);
                    ui.horizontal(|ui| {
                        if ui.add(primary_button("继续还原")).clicked() {
                            yes = true;
                        }
                        if ui.add(secondary_button("取消")).clicked() {
                            no = true;
                        }
                    });
                });
            if yes {
                self.confirm_restore = None;
                self.confirm_restore_now(&dir);
            } else if no {
                self.confirm_restore = None;
            }
        }
    }
}
