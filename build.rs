fn main() {
    println!("cargo:rerun-if-changed=assets/SidePeek.ico");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        // GPUI already embeds the PerMonitorV2/Common-Controls manifest. A second
        // MANIFEST resource with the same ID makes the MSVC resource linker fail.
        winresource::WindowsResource::new()
            .set_icon("assets/SidePeek.ico")
            .set("ProductName", "SidePeek")
            .set("FileDescription", "SidePeek GPUI")
            .compile()
            .expect("Unable to compile SidePeek Windows resources");
    }
}
