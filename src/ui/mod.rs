mod editors;
#[cfg(feature = "smoke-test")]
mod smoke;
mod views;

use editors::{Editor, NoteEditor, RunPrompt, SettingsEditor};
use gpui_kit::{
    component::{
        ActiveTheme, Disableable, Icon, IconName, Selectable, Sizable, Theme,
        ThemeMode as GpuiThemeMode,
        button::{Button, ButtonVariants},
        checkbox::Checkbox,
        input::{Input, InputEvent, InputState, Textarea, TextareaState},
    },
    prelude::FluentBuilder,
    *,
};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use sidepeek::{
    command::{CommandRun, RunEvent, append_output},
    docking::{self, Display, Dock},
    model::*,
    platform as native,
    resources::{self, LOG_TAG, layout::*, text},
    store::{self, Data, Store},
};
use std::{
    collections::BTreeSet,
    os::windows::io::OwnedHandle,
    time::{Duration, Instant},
};

const SAVE_DEBOUNCE: Duration = Duration::from_millis(600);
const SAVE_RETRY: Duration = Duration::from_secs(5);
const METRICS_INTERVAL: Duration = Duration::from_secs(1);
const CLIPBOARD_INTERVAL: Duration = Duration::from_millis(900);
const MAX_RUN_EVENTS_PER_TICK: usize = 128;
const MAX_RUN_HISTORY: usize = 20;
const MAX_VISIBLE_PREVIEW_CHARS: usize = 180;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Tab {
    Notes,
    Commands,
    Tools,
    Clipboard,
    Settings,
}
impl Tab {
    const MAIN: [Self; 4] = [Self::Notes, Self::Commands, Self::Tools, Self::Clipboard];
    fn label(self) -> &'static str {
        match self {
            Self::Notes => text::NOTES,
            Self::Commands => text::COMMANDS,
            Self::Tools => text::TOOLS,
            Self::Clipboard => text::CLIPBOARD,
            Self::Settings => text::SETTINGS,
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
enum Dataset {
    Notes,
    Commands,
    Tools,
    Clipboard,
    Settings,
}
#[derive(Clone)]
struct DragItem {
    tab: Tab,
    index: usize,
    label: String,
}
impl Render for DragItem {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .p(px(SPACE_MD))
            .rounded(px(CARD_RADIUS))
            .bg(cx.theme().secondary)
            .text_color(cx.theme().foreground)
            .child(self.label.clone())
    }
}
enum Confirm {
    Delete(Tab, usize),
    ClearClipboard,
}
struct Execution {
    id: usize,
    command: CommandItem,
    control: Option<CommandRun>,
    output: String,
    output_state: Entity<TextareaState>,
    status: &'static str,
    close_when_stopped: bool,
}

pub struct SidePeek {
    store: Store,
    data: Data,
    native: native::NativeServices,
    _instance: OwnedHandle,
    dock: Dock,
    hwnd: isize,
    tab: Tab,
    search: Entity<InputState>,
    note_editors: Vec<NoteEditor>,
    editor: Option<Editor>,
    settings_editor: Option<SettingsEditor>,
    run_prompt: Option<RunPrompt>,
    runs: Vec<Execution>,
    next_run_id: usize,
    current_run: Option<usize>,
    confirm: Option<Confirm>,
    show_history: bool,
    history_range: usize,
    dirty: BTreeSet<Dataset>,
    save_at: Option<Instant>,
    save_failed: bool,
    status: String,
    last_clipboard: String,
    clipboard_sequence: u32,
    clipboard_at: Instant,
    metrics_at: Instant,
    display_at: Instant,
    clock: String,
    date: String,
    memory: Option<(u32, f64, f64)>,
    bounds_failure_logged: bool,
    quitting: bool,
    _subscriptions: Vec<Subscription>,
    _timer: Task<()>,
}

impl SidePeek {
    pub fn new(
        store: Store,
        data: Data,
        display: Display,
        native: native::NativeServices,
        instance: OwnedHandle,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let hwnd = match HasWindowHandle::window_handle(window).map(|h| h.as_raw()) {
            Ok(RawWindowHandle::Win32(handle)) => handle.hwnd.get(),
            _ => 0,
        };
        let dock = Dock::new(display, &data.settings);
        // Place after the root view exists. Win32 resize callbacks run synchronously
        // and cannot reborrow an updating GPUI window, so synchronize its viewport here.
        cx.defer_in(window, |this, window, cx| {
            this.report(
                native::configure_window(this.hwnd)
                    .and_then(|_| native::set_bounds(this.hwnd, this.dock.current, true)),
            );
            window.bounds_changed(cx);
            cx.notify();
        });
        apply_theme(data.settings.theme, window, cx);
        let search = input("", text::SEARCH, window, cx);
        let search_subscription =
            cx.subscribe_in(&search, window, |_, _, event: &InputEvent, _, cx| {
                if matches!(event, InputEvent::Change) {
                    cx.notify();
                }
            });
        let appearance = cx.observe_window_appearance(window, |this, window, cx| {
            if this.data.settings.theme == ThemeMode::System {
                apply_theme(ThemeMode::System, window, cx);
            }
        });
        let close_handle = cx.weak_entity();
        window.on_window_should_close(cx, move |_, cx| {
            let _ = close_handle.update(cx, |this, cx| {
                this.dock.set_expanded(false, Instant::now());
                cx.notify();
            });
            false
        });
        let quit_subscription = cx.on_app_quit(|this, _| {
            this.flush();
            for run in &this.runs {
                if let Some(control) = &run.control {
                    control.cancel();
                }
            }
            async {}
        });
        let timer = cx.spawn_in(window, async move |this, cx| {
            let mut interval = docking::POLL_INTERVAL;
            loop {
                smol::Timer::after(interval).await;
                match this.update_in(cx, |this, window, cx| {
                    this.tick(window, cx);
                    if this.dock.is_animating() {
                        docking::ANIMATION_INTERVAL
                    } else {
                        docking::POLL_INTERVAL
                    }
                }) {
                    Ok(next) => interval = next,
                    Err(_) => break,
                }
            }
        });
        let now = Instant::now();
        let mut this = Self {
            last_clipboard: data
                .clipboard
                .first()
                .map(|i| i.text.clone())
                .unwrap_or_default(),
            dock,
            store,
            data,
            native,
            _instance: instance,
            hwnd,
            tab: Tab::Notes,
            search,
            note_editors: Vec::new(),
            editor: None,
            settings_editor: None,
            run_prompt: None,
            runs: Vec::new(),
            next_run_id: 0,
            current_run: None,
            confirm: None,
            show_history: false,
            history_range: HISTORY_DAYS.len() - 2,
            dirty: BTreeSet::new(),
            save_at: None,
            save_failed: false,
            status: String::new(),
            clipboard_sequence: 0,
            clipboard_at: now,
            metrics_at: now,
            display_at: now,
            clock: String::new(),
            date: String::new(),
            memory: None,
            bounds_failure_logged: false,
            quitting: false,
            _subscriptions: vec![search_subscription, appearance, quit_subscription],
            _timer: timer,
        };
        this.rebuild_notes(window, cx);
        this.refresh_metrics();
        if prune_history(
            &mut this.data.history,
            this.data.settings.note_history_months,
            chrono::Local::now(),
        ) {
            this.mark_dirty(Dataset::Notes, true);
        }
        this
    }
    fn mark_dirty(&mut self, dataset: Dataset, immediate: bool) {
        self.dirty.insert(dataset);
        self.save_at = Some(
            Instant::now()
                + if immediate {
                    Duration::ZERO
                } else {
                    SAVE_DEBOUNCE
                },
        );
    }
    fn flush(&mut self) {
        for dataset in self.dirty.clone() {
            let result = match dataset {
                Dataset::Notes => self.store.save_notes(&self.data.notes, &self.data.history),
                Dataset::Commands => self.store.save(store::COMMANDS_FILE, &self.data.commands),
                Dataset::Tools => self.store.save(store::TOOLS_FILE, &self.data.tools),
                Dataset::Clipboard => self.store.save(store::CLIPBOARD_FILE, &self.data.clipboard),
                Dataset::Settings => self.store.save(store::SETTINGS_FILE, &self.data.settings),
            };
            match result {
                Ok(()) => {
                    self.dirty.remove(&dataset);
                }
                Err(error) => {
                    log::error!(target: LOG_TAG, "Save failed for {dataset:?}: {error}");
                }
            }
        }
        self.save_failed = !self.dirty.is_empty();
        self.save_at = if self.save_failed {
            Some(Instant::now() + SAVE_RETRY)
        } else {
            None
        };
    }
    fn report(&mut self, result: anyhow::Result<()>) {
        if let Err(error) = result {
            log::error!(target: LOG_TAG, "System operation failed: {error}");
            self.status = text::NATIVE_FAILED.into();
        }
    }
    fn refresh_metrics(&mut self) {
        let now = chrono::Local::now();
        self.clock = now.format("%H:%M:%S").to_string();
        self.date = now.format("%Y / %m / %d").to_string();
        self.memory = native::memory_usage();
    }
    fn tick(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let now = Instant::now();
        while let Ok(event) = self.native.events.try_recv() {
            match event {
                native::NativeEvent::Toggle => {
                    self.dock.toggle();
                    if self.dock.expanded {
                        window.activate_window();
                    }
                }
                native::NativeEvent::Settings => {
                    self.select_tab(Tab::Settings, window, cx);
                    self.dock.set_expanded(true, now);
                    window.activate_window();
                }
                native::NativeEvent::Startup => {
                    let enabled = !native::startup_enabled();
                    match native::set_startup(enabled) {
                        Ok(()) => {
                            self.data.settings.start_with_windows = enabled;
                            if let Some(editor) = &mut self.settings_editor {
                                editor.set_startup(enabled);
                            }
                            self.mark_dirty(Dataset::Settings, true);
                        }
                        Err(e) => self.report(Err(e)),
                    }
                }
                native::NativeEvent::Quit => {
                    self.quitting = true;
                    for run in &self.runs {
                        if let Some(control) = &run.control {
                            control.cancel();
                        }
                    }
                }
                native::NativeEvent::Failure => self.status = text::NATIVE_FAILED.into(),
            }
            cx.notify();
        }
        let suspended = self.editor.is_some()
            || self.run_prompt.is_some()
            || self.confirm.is_some()
            || self.current_run.is_some()
            || self.tab == Tab::Settings
            || cx.has_active_drag();
        let expanded_before = self.dock.expanded;
        if self.dock.tick(
            native::cursor_position(),
            suspended,
            &self.data.settings,
            now,
        ) {
            let positioned = native::set_bounds(
                self.hwnd,
                self.dock.current,
                self.dock.expanded && !expanded_before,
            );
            match positioned {
                Ok(()) => {
                    // SetWindowPos updates DirectX immediately; GPUI's resize callback
                    // cannot re-enter this update, so refresh logical bounds explicitly.
                    window.bounds_changed(cx);
                    self.bounds_failure_logged = false;
                }
                Err(error) if !self.bounds_failure_logged => {
                    log::warn!(target: LOG_TAG, "Dock positioning failed: {error}");
                    self.bounds_failure_logged = true;
                }
                Err(_) => {}
            }
            cx.notify();
        }
        if now.duration_since(self.display_at) >= docking::DISPLAY_REFRESH_INTERVAL {
            self.display_at = now;
            if let Ok(display) =
                native::select_display(&self.data.settings.dock_display_device_name)
                && display != self.dock.display
            {
                self.dock.place(display, &self.data.settings);
                self.report(native::set_bounds(self.hwnd, self.dock.current, true));
                window.bounds_changed(cx);
                cx.notify();
            }
        }
        if self.dock.expanded && now.duration_since(self.metrics_at) >= METRICS_INTERVAL {
            self.metrics_at = now;
            self.refresh_metrics();
            cx.notify();
        }
        if now.duration_since(self.clipboard_at) >= CLIPBOARD_INTERVAL
            && self.confirm.is_none()
            && !smoke_enabled()
        {
            self.clipboard_at = now;
            let sequence = native::clipboard_sequence();
            if sequence != self.clipboard_sequence
                && let Some(value) = cx.read_from_clipboard().and_then(|item| item.text())
            {
                self.clipboard_sequence = sequence;
                if capture_text(&mut self.data.clipboard, &mut self.last_clipboard, value) {
                    self.mark_dirty(Dataset::Clipboard, true);
                    if self.tab == Tab::Clipboard {
                        cx.notify();
                    }
                }
            }
        }
        for run in &mut self.runs {
            let mut changed = false;
            let mut finished = false;
            if let Some(control) = &run.control {
                for event in control.events.try_iter().take(MAX_RUN_EVENTS_PER_TICK) {
                    changed = true;
                    match event {
                        RunEvent::Output(value) => append_output(&mut run.output, &value),
                        RunEvent::Exit(code) => {
                            append_output(&mut run.output, &format!("\n[exit: {code}]\n"))
                        }
                        RunEvent::Finished { cancelled, failed } => {
                            run.status = if cancelled {
                                text::STOPPED
                            } else if failed {
                                text::RUN_FAILED
                            } else {
                                text::FINISHED
                            };
                            finished = true;
                        }
                    }
                }
            }
            if finished {
                run.control = None;
                if run.close_when_stopped && self.current_run == Some(run.id) {
                    self.current_run = None;
                }
            }
            if changed {
                if self.current_run == Some(run.id) {
                    run.output_state.update(cx, |state, cx| {
                        state.set_value(run.output.clone(), window, cx);
                        let end = run.output.len();
                        state.set_selected_range(end..end, cx);
                    });
                }
                cx.notify();
            }
        }
        if self.save_at.is_some_and(|at| at <= now) {
            self.flush();
            cx.notify();
        }
        if self.quitting && self.runs.iter().all(|r| r.control.is_none()) {
            self.flush();
            if self.save_failed {
                self.quitting = false;
                cx.notify();
            } else {
                window.blur(cx);
                window.remove_window();
                cx.defer(|cx| cx.quit());
            }
        }
    }
    fn select_tab(&mut self, tab: Tab, window: &mut Window, cx: &mut Context<Self>) {
        // Editor drafts remain until Save/Cancel; changing tabs cannot silently discard them.
        if self.editor.is_some() || self.run_prompt.is_some() || self.confirm.is_some() {
            return;
        }
        self.tab = tab;
        self.current_run = None;
        self.status.clear();
        if tab == Tab::Settings && self.settings_editor.is_none() {
            self.settings_editor = Some(SettingsEditor::new(&self.data.settings, window, cx));
        }
        self.search
            .update(cx, |state, cx| state.set_value("", window, cx));
        cx.notify();
    }
    fn copy(&mut self, value: String, cx: &mut Context<Self>) {
        self.last_clipboard = value.clone();
        cx.write_to_clipboard(gpui_kit::ClipboardItem::new_string(value));
        self.clipboard_sequence = native::clipboard_sequence();
        self.status = text::COPIED.into();
        cx.notify();
    }
    fn start_run(
        &mut self,
        command: CommandItem,
        values: Vec<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let id = self.next_run_id;
        self.next_run_id += 1;
        let control = CommandRun::start(command.build_lines(&values));
        let output_state = textarea("", text::OUTPUT, window, cx);
        let silent = command.silent_execution;
        self.runs.push(Execution {
            id,
            command,
            control: Some(control),
            output: String::new(),
            output_state,
            status: text::RUNNING,
            close_when_stopped: false,
        });
        while self.runs.len() > MAX_RUN_HISTORY {
            let Some(index) = self
                .runs
                .iter()
                .position(|r| r.control.is_none() && Some(r.id) != self.current_run)
            else {
                break;
            };
            self.runs.remove(index);
        }
        self.run_prompt = None;
        if !silent {
            self.current_run = Some(id);
        }
        cx.notify();
    }
    fn open_run(&mut self, id: usize, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(run) = self.runs.iter_mut().find(|r| r.id == id) {
            run.output_state.update(cx, |state, cx| {
                state.set_value(run.output.clone(), window, cx);
                let end = run.output.len();
                state.set_selected_range(end..end, cx);
            });
            self.current_run = Some(id);
        }
        cx.notify();
    }
    fn run_command(&mut self, index: usize, window: &mut Window, cx: &mut Context<Self>) {
        let Some(command) = self.data.commands.get(index).cloned() else {
            return;
        };
        if command.parameters.is_empty() {
            self.start_run(command, Vec::new(), window, cx);
        } else {
            self.run_prompt = Some(RunPrompt::new(command, window, cx));
            cx.notify();
        }
    }
    fn perform_delete(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        match self.confirm.take() {
            Some(Confirm::Delete(Tab::Notes, index)) => {
                if self.show_history {
                    if index < self.data.history.len() {
                        self.data.history.remove(index);
                    }
                } else if index < self.data.notes.len() {
                    self.data.notes.remove(index);
                }
                self.rebuild_notes(window, cx);
                self.mark_dirty(Dataset::Notes, true);
            }
            Some(Confirm::Delete(Tab::Commands, index)) => {
                if index < self.data.commands.len() {
                    self.data.commands.remove(index);
                    self.mark_dirty(Dataset::Commands, true);
                }
            }
            Some(Confirm::Delete(Tab::Tools, index)) => {
                if index < self.data.tools.len() {
                    self.data.tools.remove(index);
                    self.mark_dirty(Dataset::Tools, true);
                }
            }
            Some(Confirm::Delete(Tab::Clipboard, index)) => {
                if index < self.data.clipboard.len() {
                    self.data.clipboard.remove(index);
                    self.mark_dirty(Dataset::Clipboard, true);
                }
            }
            Some(Confirm::ClearClipboard) => {
                self.data.clipboard.clear();
                self.mark_dirty(Dataset::Clipboard, true);
            }
            _ => {}
        }
        cx.notify();
    }
    fn reorder(
        &mut self,
        tab: Tab,
        from: usize,
        to: usize,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let changed = match tab {
            Tab::Notes => move_item(&mut self.data.notes, from, to),
            Tab::Commands => move_item(&mut self.data.commands, from, to),
            Tab::Tools => move_item(&mut self.data.tools, from, to),
            Tab::Clipboard => move_item(&mut self.data.clipboard, from, to),
            _ => false,
        };
        if changed {
            let dataset = match tab {
                Tab::Notes => {
                    self.rebuild_notes(window, cx);
                    Dataset::Notes
                }
                Tab::Commands => Dataset::Commands,
                Tab::Tools => Dataset::Tools,
                _ => Dataset::Clipboard,
            };
            self.mark_dirty(dataset, true);
            cx.notify();
        }
    }
}

fn input(
    value: &str,
    placeholder: &'static str,
    window: &mut Window,
    cx: &mut App,
) -> Entity<InputState> {
    cx.new(|cx| {
        InputState::new(window, cx)
            .default_value(value.to_string())
            .placeholder(placeholder)
    })
}
fn textarea(
    value: &str,
    placeholder: &'static str,
    window: &mut Window,
    cx: &mut App,
) -> Entity<TextareaState> {
    cx.new(|cx| {
        TextareaState::new(window, cx)
            .default_value(value.to_string())
            .placeholder(placeholder)
    })
}
fn apply_theme(mode: ThemeMode, window: &mut Window, cx: &mut App) {
    match mode {
        ThemeMode::Light => Theme::change(GpuiThemeMode::Light, Some(window), cx),
        ThemeMode::Dark => Theme::change(GpuiThemeMode::Dark, Some(window), cx),
        ThemeMode::System => Theme::sync_system_appearance(Some(window), cx),
    }
}
fn column() -> Div {
    div().flex().flex_col().gap(px(SPACE_MD))
}
fn row() -> Div {
    div().flex().items_center().gap(px(SPACE_SM))
}
fn card(cx: &App) -> Div {
    column()
        .p(px(SPACE_MD))
        .rounded(px(CARD_RADIUS))
        .border_1()
        .border_color(cx.theme().border)
        .bg(cx.theme().background)
}
fn small(value: impl Into<SharedString>, cx: &App) -> Div {
    div()
        .text_size(px(SMALL_SIZE))
        .text_color(cx.theme().muted_foreground)
        .child(value.into())
}
fn icon_button(id: impl Into<ElementId>, icon: IconName, label: &'static str) -> Button {
    Button::new(id)
        .ghost()
        .small()
        .icon(icon)
        .tooltip(label)
        .accessibility_label(label)
}
fn accent(value: &str, cx: &App) -> Hsla {
    u32::from_str_radix(value.trim_start_matches('#'), 16)
        .ok()
        .map(|value| rgb(value).into())
        .unwrap_or(cx.theme().primary)
}
fn icon_for(value: &str) -> IconName {
    match value {
        "terminal" | "\u{e756}" => IconName::SquareTerminal,
        "globe" | "\u{e968}" | "\u{e774}" | "\u{e909}" => IconName::Globe,
        "folder" | "\u{e8b7}" => IconName::Folder,
        "file" | "\u{e70b}" | "\u{e8a5}" => IconName::FileText,
        "settings" | "\u{e713}" => IconName::Settings,
        "calculator" | "\u{e1d0}" | "\u{e9d9}" => IconName::LayoutDashboard,
        "rocket" | "\u{e90f}" | "\u{e945}" => IconName::Play,
        "\u{e787}" => IconName::Calendar,
        "\u{e74d}" => IconName::Delete,
        "\u{e790}" => IconName::Palette,
        _ => IconName::Frame,
    }
}

fn smoke_enabled() -> bool {
    cfg!(feature = "smoke-test") && std::env::var_os("SIDEPEEK_SMOKE_TEST").is_some()
}
