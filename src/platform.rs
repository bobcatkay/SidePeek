//! Windows services stay outside GPUI render callbacks and never log user content.
#[cfg(windows)]
mod windows;
#[cfg(windows)]
pub use windows::*;

#[cfg(not(windows))]
compile_error!("SidePeek currently targets Windows 11; build for x86_64-pc-windows-msvc.");
