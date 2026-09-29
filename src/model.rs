use chrono::{DateTime, Local, Months, NaiveDateTime, TimeZone};
use serde::{Deserialize, Serialize};
use serde_repr::{Deserialize_repr, Serialize_repr};
use std::collections::BTreeMap;

use crate::resources::{DEFAULT_COMMAND_ACCENT, DEFAULT_TOOL_ACCENT, PALETTE, text};

pub const DEFAULT_EXPAND_DELAY_MS: u64 = 1000;
pub const DEFAULT_COLLAPSE_DELAY_MS: u64 = 450;
pub const MIN_EXPAND_DELAY_MS: u64 = 100;
pub const MAX_EXPAND_DELAY_MS: u64 = 3000;
pub const MIN_COLLAPSE_DELAY_MS: u64 = 150;
pub const MAX_COLLAPSE_DELAY_MS: u64 = 2000;
pub const DEFAULT_HISTORY_MONTHS: u32 = 12;
pub const MAX_HISTORY_MONTHS: u32 = 60;
pub const MAX_FUNCTION_KEY: u32 = 12;
pub const MAX_CLIPBOARD_ITEMS: usize = 200;
pub const MAX_CLIPBOARD_BYTES: usize = 1024 * 1024;
pub const HISTORY_DAYS: [Option<i64>; 5] = [Some(7), Some(30), Some(90), Some(365), None];

type ExtraFields = BTreeMap<String, serde_json::Value>;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize_repr, Deserialize_repr)]
#[repr(u8)]
pub enum DockEdge {
    Left = 0,
    Top = 1,
    #[default]
    Right = 2,
    Bottom = 3,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize_repr, Deserialize_repr)]
#[repr(u8)]
pub enum ThemeMode {
    #[default]
    Light = 0,
    Dark = 1,
    System = 2,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize_repr, Deserialize_repr)]
#[repr(u8)]
pub enum ParameterMode {
    #[default]
    Prompt = 0,
    Choices = 1,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Hotkey {
    pub control: bool,
    pub alt: bool,
    pub shift: bool,
    pub key: String,
    #[serde(flatten)]
    pub extra: ExtraFields,
}
impl Default for Hotkey {
    fn default() -> Self {
        Self {
            control: true,
            alt: true,
            shift: false,
            key: "S".into(),
            extra: ExtraFields::new(),
        }
    }
}
impl Hotkey {
    pub fn valid(&self) -> bool {
        let key = self.key.trim().to_ascii_uppercase();
        (self.control || self.alt || self.shift)
            && ((key.len() == 1 && key.as_bytes()[0].is_ascii_uppercase())
                || key
                    .strip_prefix('F')
                    .and_then(|k| k.parse::<u32>().ok())
                    .is_some_and(|n| (1..=MAX_FUNCTION_KEY).contains(&n)))
    }
    pub fn display(&self) -> String {
        let mut parts = Vec::new();
        if self.control {
            parts.push(text::CTRL);
        }
        if self.alt {
            parts.push(text::ALT);
        }
        if self.shift {
            parts.push(text::SHIFT);
        }
        parts.push(&self.key);
        parts.join(" + ")
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Settings {
    pub dock_edge: DockEdge,
    pub dock_display_device_name: String,
    pub expand_delay_ms: u64,
    pub collapse_delay_ms: u64,
    pub note_history_months: u32,
    pub theme: ThemeMode,
    pub start_with_windows: bool,
    pub hotkey: Hotkey,
    #[serde(flatten)]
    pub extra: ExtraFields,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            dock_edge: DockEdge::Right,
            dock_display_device_name: String::new(),
            expand_delay_ms: DEFAULT_EXPAND_DELAY_MS,
            collapse_delay_ms: DEFAULT_COLLAPSE_DELAY_MS,
            note_history_months: DEFAULT_HISTORY_MONTHS,
            theme: ThemeMode::Light,
            start_with_windows: false,
            hotkey: Hotkey::default(),
            extra: ExtraFields::new(),
        }
    }
}
impl Settings {
    pub fn normalize(&mut self) {
        if !matches!(self.dock_edge, DockEdge::Left | DockEdge::Right) {
            self.dock_edge = DockEdge::Right;
        }
        self.expand_delay_ms = self
            .expand_delay_ms
            .clamp(MIN_EXPAND_DELAY_MS, MAX_EXPAND_DELAY_MS);
        self.collapse_delay_ms = self
            .collapse_delay_ms
            .clamp(MIN_COLLAPSE_DELAY_MS, MAX_COLLAPSE_DELAY_MS);
        self.note_history_months = self.note_history_months.clamp(1, MAX_HISTORY_MONTHS);
        self.hotkey.key = self.hotkey.key.trim().to_ascii_uppercase();
        if !self.hotkey.valid() {
            self.hotkey = Hotkey::default();
        }
    }
    pub fn valid(&self) -> bool {
        (MIN_EXPAND_DELAY_MS..=MAX_EXPAND_DELAY_MS).contains(&self.expand_delay_ms)
            && (MIN_COLLAPSE_DELAY_MS..=MAX_COLLAPSE_DELAY_MS).contains(&self.collapse_delay_ms)
            && (1..=MAX_HISTORY_MONTHS).contains(&self.note_history_months)
            && self.hotkey.valid()
    }
}

pub fn timestamp() -> String {
    Local::now().to_rfc3339()
}

/// WPF writes local DateTime values without a UTC offset. Preserve the stored text
/// and interpret those values in local time when filtering or pruning history.
pub fn parse_timestamp(value: &str) -> Option<DateTime<Local>> {
    DateTime::parse_from_rfc3339(value)
        .map(|d| d.with_timezone(&Local))
        .ok()
        .or_else(|| {
            NaiveDateTime::parse_from_str(value, "%Y-%m-%dT%H:%M:%S%.f")
                .ok()
                .and_then(|d| Local.from_local_datetime(&d).earliest())
        })
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Note {
    pub title: String,
    pub content: String,
    pub color_hex: String,
    pub is_pinned: bool,
    pub updated_at: String,
    pub completed_at: Option<String>,
    #[serde(flatten)]
    pub extra: ExtraFields,
}
impl Default for Note {
    fn default() -> Self {
        Self {
            title: text::NEW_NOTE.into(),
            content: String::new(),
            color_hex: PALETTE[0].into(),
            is_pinned: false,
            updated_at: timestamp(),
            completed_at: None,
            extra: ExtraFields::new(),
        }
    }
}
impl Note {
    pub fn completed_time(&self) -> Option<DateTime<Local>> {
        parse_timestamp(self.completed_at.as_deref().unwrap_or(&self.updated_at))
    }
}

pub fn prune_history(notes: &mut Vec<Note>, months: u32, now: DateTime<Local>) -> bool {
    let Some(cutoff) = now.checked_sub_months(Months::new(months)) else {
        return false;
    };
    let previous_len = notes.len();
    // Unknown timestamps are retained: a parse failure must never delete a note.
    notes.retain(|note| note.completed_time().is_none_or(|time| time >= cutoff));
    notes.len() != previous_len
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct ParameterChoice {
    pub label: String,
    pub value: String,
    pub is_default: bool,
    #[serde(flatten)]
    pub extra: ExtraFields,
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Parameter {
    pub label: String,
    pub prompt_default_value: String,
    pub mode: ParameterMode,
    pub choices: Vec<ParameterChoice>,
    #[serde(flatten)]
    pub extra: ExtraFields,
}
impl Parameter {
    pub fn default_value(&self) -> String {
        match self.mode {
            ParameterMode::Prompt => self.prompt_default_value.clone(),
            ParameterMode::Choices => self
                .choices
                .iter()
                .find(|choice| choice.is_default)
                .or_else(|| self.choices.first())
                .map(|choice| choice.value.clone())
                .unwrap_or_default(),
        }
    }
    pub fn valid(&self) -> bool {
        !self.label.trim().is_empty()
            && (self.mode == ParameterMode::Prompt
                || (!self.choices.is_empty()
                    && self
                        .choices
                        .iter()
                        .all(|c| !c.label.trim().is_empty() && !c.value.trim().is_empty())))
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct CommandItem {
    pub title: String,
    pub description: String,
    pub silent_execution: bool,
    pub glyph: String,
    pub accent_hex: String,
    pub command_text: String,
    pub parameters: Vec<Parameter>,
    #[serde(flatten)]
    pub extra: ExtraFields,
}
impl Default for CommandItem {
    fn default() -> Self {
        Self {
            title: String::new(),
            description: String::new(),
            silent_execution: false,
            glyph: "terminal".into(),
            accent_hex: DEFAULT_COMMAND_ACCENT.into(),
            command_text: String::new(),
            parameters: Vec::new(),
            extra: ExtraFields::new(),
        }
    }
}
impl CommandItem {
    pub fn build_lines(&self, values: &[String]) -> Vec<String> {
        self.command_text
            .lines()
            .filter(|line| !line.trim().is_empty())
            .map(|line| replace_parameters(line, values))
            .collect()
    }
}

/// Replace each original placeholder once. Substituted values can contain percent
/// signs; rescanning them would expand user input and corrupt cmd environment variables.
pub fn replace_parameters(line: &str, values: &[String]) -> String {
    let mut result = String::new();
    let mut remaining = line;
    while let Some(start) = remaining.find('%') {
        result.push_str(&remaining[..start]);
        remaining = &remaining[start..];
        let digits = remaining[1..]
            .bytes()
            .take_while(u8::is_ascii_digit)
            .count();
        let end = 1 + digits;
        let value = remaining[1..end]
            .parse::<usize>()
            .ok()
            .and_then(|index| index.checked_sub(1))
            .and_then(|index| values.get(index));
        result.push_str(value.map(String::as_str).unwrap_or(&remaining[..end]));
        remaining = &remaining[end..];
    }
    result.push_str(remaining);
    result
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct ToolItem {
    pub title: String,
    pub description: String,
    pub glyph: String,
    pub accent_hex: String,
    pub exe_path: String,
    pub arguments: String,
    #[serde(flatten)]
    pub extra: ExtraFields,
}
impl Default for ToolItem {
    fn default() -> Self {
        Self {
            title: String::new(),
            description: String::new(),
            glyph: "app".into(),
            accent_hex: DEFAULT_TOOL_ACCENT.into(),
            exe_path: String::new(),
            arguments: String::new(),
            extra: ExtraFields::new(),
        }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct ClipboardItem {
    pub text: String,
    pub captured_at: String,
    #[serde(flatten)]
    pub extra: ExtraFields,
}

pub fn capture_text(items: &mut Vec<ClipboardItem>, last_text: &mut String, value: String) -> bool {
    if value.trim().is_empty() || value == *last_text || value.len() > MAX_CLIPBOARD_BYTES {
        return false;
    }
    items.retain(|item| item.text != value);
    *last_text = value.clone();
    items.insert(
        0,
        ClipboardItem {
            text: value,
            captured_at: timestamp(),
            ..Default::default()
        },
    );
    items.truncate(MAX_CLIPBOARD_ITEMS);
    true
}

pub fn move_item<T>(items: &mut Vec<T>, from: usize, to: usize) -> bool {
    if from == to || from >= items.len() || to >= items.len() {
        return false;
    }
    let item = items.remove(from);
    items.insert(to, item);
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn legacy_settings_and_unknown_fields_round_trip() {
        let settings: Settings = serde_json::from_str(r#"{"DockEdge":0,"Theme":2,"DockDisplayDeviceName":"\\\\.\\DISPLAY2","Hotkey":{"Control":true,"Alt":false,"Shift":true,"Key":"F12"},"FutureSetting":42}"#).unwrap();
        assert_eq!(settings.dock_edge, DockEdge::Left);
        assert_eq!(settings.theme, ThemeMode::System);
        assert!(settings.hotkey.valid());
        let value = serde_json::to_value(settings).unwrap();
        assert_eq!(value["DockEdge"], 0);
        assert_eq!(value["FutureSetting"], 42);
    }
    #[test]
    fn placeholders_are_multi_digit_non_recursive_and_leave_environment_variables() {
        let values: Vec<String> = (1..=12).map(|i| format!("arg{i}")).collect();
        assert_eq!(
            replace_parameters("%1 %12 %2 %0 %99 %TEMP% 中文", &values),
            "arg1 arg12 arg2 %0 %99 %TEMP% 中文"
        );
        assert_eq!(
            replace_parameters("%1 %2", &["%2".into(), "ok".into()]),
            "%2 ok"
        );
    }
    #[test]
    fn legacy_dates_and_invalid_dates_do_not_lose_history() {
        assert!(parse_timestamp("2026-09-06T12:34:56.1234567").is_some());
        let mut notes = vec![
            Note {
                completed_at: Some("not a date".into()),
                ..Default::default()
            },
            Note {
                completed_at: Some("2000-01-01T00:00:00".into()),
                ..Default::default()
            },
        ];
        assert!(prune_history(
            &mut notes,
            DEFAULT_HISTORY_MONTHS,
            Local::now()
        ));
        assert_eq!(notes.len(), 1);
    }
    #[test]
    fn clipboard_deduplicates_caps_and_suppresses_self_copy() {
        let mut items = Vec::new();
        let mut last = String::new();
        for i in 0..=MAX_CLIPBOARD_ITEMS {
            assert!(capture_text(&mut items, &mut last, i.to_string()));
        }
        assert_eq!(items.len(), MAX_CLIPBOARD_ITEMS);
        assert!(!capture_text(
            &mut items,
            &mut last,
            MAX_CLIPBOARD_ITEMS.to_string()
        ));
        assert!(capture_text(&mut items, &mut last, "1".into()));
        assert_eq!(items.iter().filter(|i| i.text == "1").count(), 1);
        assert_eq!(items[0].text, "1");
    }
    #[test]
    fn movement_retains_all_items_and_ignores_invalid_indices() {
        let mut items = vec!["a", "b", "c"];
        assert!(move_item(&mut items, 0, 2));
        assert_eq!(items, ["b", "c", "a"]);
        assert!(!move_item(&mut items, 9, 0));
    }
}
