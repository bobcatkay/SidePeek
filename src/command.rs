use crate::{
    platform::{handle, owned, wide},
    resources::{LOG_TAG, text},
};
use anyhow::{Context, Result, bail};
use std::{
    fs::File,
    io::{BufRead, BufReader, Read},
    mem::size_of,
    os::windows::io::{AsRawHandle, OwnedHandle},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread,
    time::Duration,
};
use windows::{
    Win32::{
        Foundation::*,
        Globalization::*,
        Security::SECURITY_ATTRIBUTES,
        System::{JobObjects::*, Pipes::CreatePipe, Threading::*},
    },
    core::{PCWSTR, PWSTR},
};

const PROCESS_POLL_INTERVAL: Duration = Duration::from_millis(50);
const OUTPUT_CHUNK_BYTES: u64 = 16 * 1024;
const OUTPUT_QUEUE_CAPACITY: usize = 128;
const CANCEL_EXIT_CODE: u32 = 130;
pub const MAX_OUTPUT_BYTES: usize = 512 * 1024;
static SPAWN_LOCK: Mutex<()> = Mutex::new(());

#[derive(Debug)]
pub enum RunEvent {
    Output(String),
    Exit(u32),
    Finished { cancelled: bool, failed: bool },
}
pub struct CommandRun {
    pub events: Receiver<RunEvent>,
    cancellation: Arc<AtomicBool>,
}
impl CommandRun {
    pub fn start(lines: Vec<String>) -> Self {
        let (sender, events) = mpsc::sync_channel(OUTPUT_QUEUE_CAPACITY);
        let cancellation = Arc::new(AtomicBool::new(false));
        let flag = cancellation.clone();
        thread::spawn(move || {
            log::info!(target: LOG_TAG, "Command sequence started, count={}", lines.len());
            let mut failed = false;
            for line in lines {
                if flag.load(Ordering::Acquire) {
                    break;
                }
                match run_one(&line, &flag, &sender) {
                    Ok(code) => {
                        failed |= code != 0 && !flag.load(Ordering::Acquire);
                        let _ = sender.send(RunEvent::Exit(code));
                    }
                    Err(error) => {
                        // Diagnostics contain only OS errors, never command text or parameter values.
                        log::error!(target: LOG_TAG, "Command execution failed: {error}");
                        let _ = sender
                            .send(RunEvent::Output(format!("{}: {error}\n", text::RUN_FAILED)));
                        failed = true;
                    }
                }
            }
            let cancelled = flag.load(Ordering::Acquire);
            let _ = sender.send(RunEvent::Finished { cancelled, failed });
            log::info!(target: LOG_TAG, "Command sequence finished, cancelled={cancelled}, failed={failed}");
        });
        Self {
            events,
            cancellation,
        }
    }
    pub fn cancel(&self) {
        self.cancellation.store(true, Ordering::Release);
    }
}
impl Drop for CommandRun {
    fn drop(&mut self) {
        self.cancel();
    }
}

fn send_output(sender: &SyncSender<RunEvent>, cancelled: &AtomicBool, mut event: RunEvent) -> bool {
    loop {
        match sender.try_send(event) {
            Ok(()) => return true,
            Err(mpsc::TrySendError::Disconnected(_)) => return false,
            Err(mpsc::TrySendError::Full(value)) => {
                if cancelled.load(Ordering::Acquire) {
                    return false;
                }
                event = value;
                thread::sleep(PROCESS_POLL_INTERVAL);
            }
        }
    }
}

struct Process {
    process: OwnedHandle,
    job: OwnedHandle,
    output: File,
    pid: u32,
}

fn spawn_process(line: &str) -> Result<Process> {
    // CreateProcess inherits handles process-wide. Serialize the short setup window
    // so concurrent commands cannot inherit each other's pipe writers and delay EOF.
    let _spawn_guard = SPAWN_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    unsafe {
        let job = owned(CreateJobObjectW(None, PCWSTR::null())?);
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(
            handle(&job),
            JobObjectExtendedLimitInformation,
            &limits as *const _ as _,
            size_of_val(&limits) as u32,
        )?;
        let security = SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            bInheritHandle: TRUE,
            ..Default::default()
        };
        let (mut read, mut write) = (HANDLE::default(), HANDLE::default());
        CreatePipe(&mut read, &mut write, Some(&security), 0)?;
        let read = owned(read);
        let write = owned(write);
        SetHandleInformation(
            handle(&read),
            HANDLE_FLAG_INHERIT.0,
            HANDLE_FLAGS::default(),
        )?;
        let stdin = File::open("NUL")?;
        SetHandleInformation(
            HANDLE(stdin.as_raw_handle()),
            HANDLE_FLAG_INHERIT.0,
            HANDLE_FLAG_INHERIT,
        )?;
        let startup = STARTUPINFOW {
            cb: size_of::<STARTUPINFOW>() as u32,
            dwFlags: STARTF_USESTDHANDLES,
            hStdInput: HANDLE(stdin.as_raw_handle()),
            hStdOutput: handle(&write),
            hStdError: handle(&write),
            ..Default::default()
        };
        let system_root = std::env::var_os("SystemRoot").context("SystemRoot is unavailable")?;
        let executable = std::path::PathBuf::from(system_root)
            .join("System32")
            .join("cmd.exe");
        let executable_wide = wide(&executable.to_string_lossy());
        let mut command = wide(&format!("\"{}\" /d /s /c \"{line}\"", executable.display()));
        let mut information = PROCESS_INFORMATION::default();
        // Assign the suspended child before any instruction runs. Assigning a running
        // cmd.exe has a race: it can spawn grandchildren before it belongs to the job.
        CreateProcessW(
            PCWSTR(executable_wide.as_ptr()),
            PWSTR(command.as_mut_ptr()),
            None,
            None,
            true,
            CREATE_NO_WINDOW | CREATE_SUSPENDED,
            None,
            None,
            &startup,
            &mut information,
        )?;
        let process = owned(information.hProcess);
        let primary_thread = owned(information.hThread);
        if let Err(error) = AssignProcessToJobObject(handle(&job), handle(&process)) {
            let _ = TerminateProcess(handle(&process), CANCEL_EXIT_CODE);
            return Err(error.into());
        }
        if ResumeThread(handle(&primary_thread)) == u32::MAX {
            bail!("Unable to resume command process");
        }
        drop(write);
        let output = File::from(read);
        log::info!(target: LOG_TAG, "Command process started, pid={}", information.dwProcessId);
        Ok(Process {
            process,
            job,
            output,
            pid: information.dwProcessId,
        })
    }
}

fn run_one(line: &str, cancelled: &Arc<AtomicBool>, sender: &SyncSender<RunEvent>) -> Result<u32> {
    let Process {
        process,
        job,
        output,
        pid,
    } = spawn_process(line)?;
    let output_sender = sender.clone();
    let output_flag = cancelled.clone();
    let reader = thread::spawn(move || {
        let mut reader = BufReader::new(output);
        loop {
            let mut bytes = Vec::new();
            match reader
                .by_ref()
                .take(OUTPUT_CHUNK_BYTES)
                .read_until(b'\n', &mut bytes)
            {
                Ok(0) => break,
                Ok(_) => {
                    if !send_output(
                        &output_sender,
                        &output_flag,
                        RunEvent::Output(decode_output(&bytes)),
                    ) {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
    });
    unsafe {
        loop {
            if cancelled.load(Ordering::Acquire) {
                TerminateJobObject(handle(&job), CANCEL_EXIT_CODE)?;
                break;
            }
            let wait =
                WaitForSingleObject(handle(&process), PROCESS_POLL_INTERVAL.as_millis() as u32);
            if wait == WAIT_OBJECT_0 {
                break;
            }
            if wait == WAIT_FAILED {
                bail!("Unable to wait for command");
            }
        }
        // Kill-on-close releases pipe writers held by descendants and guarantees that
        // shutdown/interruption cannot leave commands running invisibly.
        let _ = WaitForSingleObject(handle(&process), INFINITE);
        let mut code = 0;
        GetExitCodeProcess(handle(&process), &mut code)?;
        drop(job);
        let _ = reader.join();
        log::info!(target: LOG_TAG, "Command process exited, pid={pid}, code={code}");
        Ok(code)
    }
}

fn decode_output(bytes: &[u8]) -> String {
    if let Ok(text) = std::str::from_utf8(bytes) {
        return text.to_string();
    }
    unsafe {
        let code_page = GetOEMCP();
        let length = MultiByteToWideChar(
            code_page,
            MULTI_BYTE_TO_WIDE_CHAR_FLAGS::default(),
            bytes,
            None,
        );
        if length <= 0 {
            return String::from_utf8_lossy(bytes).into_owned();
        }
        let mut output = vec![0; length as usize];
        MultiByteToWideChar(
            code_page,
            MULTI_BYTE_TO_WIDE_CHAR_FLAGS::default(),
            bytes,
            Some(&mut output),
        );
        String::from_utf16_lossy(&output)
    }
}

pub fn append_output(output: &mut String, value: &str) {
    output.push_str(value);
    if output.len() > MAX_OUTPUT_BYTES {
        let mut start = output.len() - MAX_OUTPUT_BYTES;
        while !output.is_char_boundary(start) {
            start += 1;
        }
        output.drain(..start);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    const TEST_TIMEOUT: Duration = Duration::from_secs(10);
    #[test]
    fn streams_stdout_stderr_and_returns_exit_code() {
        let run = CommandRun::start(vec![
            "echo sidepeek-test & echo error-test 1>&2 & exit /b 7".into(),
        ]);
        let mut output = String::new();
        let mut exit = None;
        loop {
            match run.events.recv_timeout(TEST_TIMEOUT).unwrap() {
                RunEvent::Output(value) => output.push_str(&value),
                RunEvent::Exit(code) => exit = Some(code),
                RunEvent::Finished { cancelled, failed } => {
                    assert!(!cancelled);
                    assert!(failed);
                    break;
                }
            }
        }
        assert!(output.contains("sidepeek-test"));
        assert!(output.contains("error-test"));
        assert_eq!(exit, Some(7));
    }
    #[test]
    fn cancellation_stops_sequence_and_child_process() {
        let run = CommandRun::start(vec![
            "echo started & ping 127.0.0.1 -n 60 >nul".into(),
            "echo MUST_NOT_RUN".into(),
        ]);
        let mut started = false;
        loop {
            match run.events.recv_timeout(TEST_TIMEOUT).unwrap() {
                RunEvent::Output(value) => {
                    assert!(!value.contains("MUST_NOT_RUN"));
                    if value.contains("started") {
                        started = true;
                        run.cancel();
                    }
                }
                RunEvent::Finished { cancelled, .. } => {
                    assert!(started && cancelled);
                    break;
                }
                _ => {}
            }
        }
    }
    #[test]
    fn output_limit_keeps_valid_unicode() {
        let mut output = "便签".repeat(MAX_OUTPUT_BYTES);
        append_output(&mut output, "完成");
        assert!(output.len() <= MAX_OUTPUT_BYTES);
        assert!(output.ends_with("完成"));
    }
}
