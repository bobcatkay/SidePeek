use anyhow::{Context, Result};
use chrono::Local;
use log::{LevelFilter, Log, Metadata, Record};
use std::{
    fs::{self, File, OpenOptions},
    io::Write,
    path::Path,
    sync::Mutex,
};

struct FileLogger {
    file: Mutex<File>,
}
impl Log for FileLogger {
    fn enabled(&self, metadata: &Metadata<'_>) -> bool {
        metadata.target().starts_with(crate::resources::LOG_TAG)
            && metadata.level() <= log::Level::Info
    }
    fn log(&self, record: &Record<'_>) {
        if self.enabled(record.metadata())
            && let Ok(mut file) = self.file.lock()
        {
            let _ = writeln!(
                file,
                "{} [{}] [{}] {}",
                Local::now().to_rfc3339(),
                record.level(),
                record.target(),
                record.args()
            );
        }
    }
    fn flush(&self) {
        if let Ok(mut file) = self.file.lock() {
            let _ = file.flush();
        }
    }
}
pub fn init(directory: &Path) -> Result<()> {
    fs::create_dir_all(directory)?;
    let file = OpenOptions::new()
        .create(true)
        .append(true)
        .open(directory.join(format!("sidepeek-{}.log", Local::now().format("%Y-%m-%d"))))?;
    log::set_logger(Box::leak(Box::new(FileLogger {
        file: Mutex::new(file),
    })))
    .context("Unable to initialize logger")?;
    log::set_max_level(LevelFilter::Info);
    std::panic::set_hook(Box::new(|panic| {
        #[cfg(feature = "smoke-test")]
        if std::env::var_os("SIDEPEEK_SMOKE_TEST").is_some() {
            eprintln!("{panic}");
        }
        // Panic payloads can contain user content. Keep the location, never the payload.
        log::error!(target: crate::resources::LOG_TAG, "Unhandled panic at {:?}", panic.location());
    }));
    Ok(())
}
