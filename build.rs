fn main() {
    if std::env::var_os("CARGO_CFG_WINDOWS").is_some() {
        let mut res = winresource::WindowsResource::new();
        res.set_icon("app.ico");
        if let Err(err) = res.compile() {
            println!("cargo:warning=嵌入图标失败: {}", err);
        }
    }
}
