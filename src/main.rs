#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod ui;

use anyhow::{Context as _, Result};
use gpui_kit::{component::Root, *};
use sidepeek::{
    logging, platform as native,
    resources::{LOG_TAG, text},
    store::Store,
};

fn launch() -> Result<()> {
    let Some(instance) = native::single_instance()? else {
        return Ok(());
    };
    let directory = Store::default_directory()?;
    #[cfg(feature = "smoke-test")]
    if std::env::var_os("SIDEPEEK_SMOKE_TEST").is_some()
        && std::env::var_os("SIDEPEEK_DATA_DIR").is_none()
    {
        anyhow::bail!("Smoke testing requires an isolated SIDEPEEK_DATA_DIR");
    }
    logging::init(&directory.join("logs"))?;
    let store = Store::new(directory)?;
    let mut data = store.load_all()?;
    data.settings.start_with_windows = native::startup_enabled();
    let display = native::select_display(&data.settings.dock_display_device_name)?;
    let services = native::NativeServices::start(data.settings.hotkey.clone())?;
    log::info!(target: LOG_TAG, "SidePeek GPUI started, version={}", env!("CARGO_PKG_VERSION"));
    gpui_kit::application()
        .with_assets(gpui_kit::assets::Assets)
        .run(move |cx| {
            gpui_kit::init(cx);
            gpui_kit::component::set_locale("zh-CN");
            cx.spawn(async move |cx| {
                let options = WindowOptions {
                    titlebar: None,
                    kind: WindowKind::PopUp,
                    focus: false,
                    is_movable: false,
                    is_resizable: false,
                    is_minimizable: false,
                    window_background: WindowBackgroundAppearance::Blurred,
                    window_min_size: Some(size(px(1.0), px(1.0))),
                    app_id: Some(text::APP.into()),
                    ..Default::default()
                };
                let result = cx
                    .open_window(options, move |window, cx| {
                        window.set_window_title(text::APP);
                        let view = cx.new(|cx| {
                            ui::SidePeek::new(store, data, display, services, instance, window, cx)
                        });
                        #[cfg(feature = "smoke-test")]
                        view.update(cx, |view, cx| view.start_smoke(window, cx));
                        cx.new(|cx| Root::new(view, window, cx))
                    })
                    .context("Unable to open GPUI window");
                if let Err(error) = result {
                    log::error!(target: LOG_TAG, "Window initialization failed: {error}");
                    native::show_error(&error.to_string());
                    cx.update(|cx| cx.quit());
                }
            })
            .detach();
        });
    Ok(())
}

fn main() {
    if let Err(error) = launch() {
        log::error!(target: LOG_TAG, "Application initialization failed: {error}");
        native::show_error(&error.to_string());
    }
}
