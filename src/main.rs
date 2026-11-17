#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod dxvk;
mod library;
mod process;
mod util;

fn main() -> eframe::Result<()> {
    let mut viewport = eframe::egui::ViewportBuilder::default()
        .with_title("游戏图形 API 检测器")
        .with_inner_size([1180.0, 720.0])
        .with_min_inner_size([960.0, 560.0]);
    if let Some(icon) = util::window_icon() {
        viewport = viewport.with_icon(icon);
    }

    let options = eframe::NativeOptions {
        viewport,
        ..Default::default()
    };

    eframe::run_native(
        "游戏图形 API 检测器",
        options,
        Box::new(|cc| {
            util::install_fonts(&cc.egui_ctx);
            Ok(Box::new(app::App::new(cc)) as Box<dyn eframe::App>)
        }),
    )
}
