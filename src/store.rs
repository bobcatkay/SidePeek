use crate::{
    model::*,
    resources::{LOG_TAG, text},
};
use anyhow::{Context, Result, bail};
use serde::{Serialize, de::DeserializeOwned};
use std::{
    fs,
    io::Write,
    path::{Path, PathBuf},
};
use tempfile::NamedTempFile;

pub const NOTES_FILE: &str = "notes.json";
pub const HISTORY_FILE: &str = "completed-notes.json";
pub const COMMANDS_FILE: &str = "commands.json";
pub const TOOLS_FILE: &str = "tools.json";
pub const CLIPBOARD_FILE: &str = "clipboard.json";
pub const SETTINGS_FILE: &str = "settings.json";
const NOTES_JOURNAL: &str = "notes-transaction.json";
const BACKUP_DIRECTORY: &str = "backup-before-gpui";

#[derive(Clone)]
pub struct Store {
    directory: PathBuf,
}

#[derive(Default)]
pub struct Data {
    pub notes: Vec<Note>,
    pub history: Vec<Note>,
    pub commands: Vec<CommandItem>,
    pub tools: Vec<ToolItem>,
    pub clipboard: Vec<ClipboardItem>,
    pub settings: Settings,
}

#[derive(Serialize, serde::Deserialize)]
struct NotesTransaction {
    notes: Vec<Note>,
    history: Vec<Note>,
}

impl Store {
    pub fn new(directory: PathBuf) -> Result<Self> {
        fs::create_dir_all(&directory).context("Unable to create data directory")?;
        let store = Self { directory };
        store.recover_notes()?;
        Ok(store)
    }
    pub fn default_directory() -> Result<PathBuf> {
        if let Some(path) = std::env::var_os("SIDEPEEK_DATA_DIR") {
            return Ok(path.into());
        }
        Ok(
            PathBuf::from(std::env::var_os("APPDATA").context("APPDATA is unavailable")?)
                .join("SidePeek"),
        )
    }
    pub fn directory(&self) -> &Path {
        &self.directory
    }
    pub fn load<T: DeserializeOwned>(&self, file: &str) -> Result<Option<T>> {
        let path = self.directory.join(file);
        if !path.exists() {
            return Ok(None);
        }
        let bytes = fs::read(&path).with_context(|| format!("Unable to read {file}"))?;
        // Some older JSON editors save UTF-8 with a BOM, which serde_json rejects.
        let bytes = bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(&bytes);
        serde_json::from_slice(bytes)
            .with_context(|| format!("Invalid {file}; original file has been preserved"))
    }
    pub fn load_all(&self) -> Result<Data> {
        let mut data = Data {
            notes: self.load(NOTES_FILE)?.unwrap_or_else(|| {
                vec![Note {
                    title: text::WELCOME_TITLE.into(),
                    content: text::WELCOME_CONTENT.into(),
                    ..Default::default()
                }]
            }),
            history: self.load(HISTORY_FILE)?.unwrap_or_default(),
            commands: self.load(COMMANDS_FILE)?.unwrap_or_default(),
            tools: self.load(TOOLS_FILE)?.unwrap_or_default(),
            clipboard: self.load(CLIPBOARD_FILE)?.unwrap_or_default(),
            settings: self.load(SETTINGS_FILE)?.unwrap_or_default(),
        };
        data.settings.normalize();
        data.clipboard.truncate(MAX_CLIPBOARD_ITEMS);
        log::info!(target: LOG_TAG, "Loaded notes={}, history={}, commands={}, tools={}, clipboard={}", data.notes.len(), data.history.len(), data.commands.len(), data.tools.len(), data.clipboard.len());
        Ok(data)
    }
    fn backup(&self, file: &str) -> Result<()> {
        let source = self.directory.join(file);
        let destination = self.directory.join(BACKUP_DIRECTORY).join(file);
        if source.exists() && !destination.exists() {
            let backup_directory = destination.parent().context("Invalid backup directory")?;
            fs::create_dir_all(backup_directory)?;
            // Publish only a complete, flushed backup. A failed copy must not leave a
            // partial file that would make the next save skip the migration backup.
            let mut output = NamedTempFile::new_in(backup_directory)?;
            std::io::copy(&mut fs::File::open(source)?, &mut output)?;
            output.as_file().sync_all()?;
            output.persist_noclobber(destination)?;
        }
        Ok(())
    }
    pub fn save<T: Serialize>(&self, file: &str, value: &T) -> Result<()> {
        if Path::new(file).file_name().and_then(|s| s.to_str()) != Some(file) {
            bail!("Invalid data filename");
        }
        self.backup(file)?;
        self.atomic_write(file, value)
    }
    fn atomic_write<T: Serialize>(&self, file: &str, value: &T) -> Result<()> {
        let mut temp = NamedTempFile::new_in(&self.directory)?;
        serde_json::to_writer_pretty(&mut temp, value)?;
        temp.write_all(b"\n")?;
        temp.as_file().sync_all()?;
        temp.persist(self.directory.join(file))
            .with_context(|| format!("Unable to replace {file}"))?;
        Ok(())
    }
    pub fn save_notes(&self, notes: &[Note], history: &[Note]) -> Result<()> {
        self.backup(NOTES_FILE)?;
        self.backup(HISTORY_FILE)?;
        // Completing/restoring crosses two legacy files. A durable journal makes that
        // move recoverable after a crash between either file replacement.
        let transaction = NotesTransaction {
            notes: notes.to_vec(),
            history: history.to_vec(),
        };
        self.atomic_write(NOTES_JOURNAL, &transaction)?;
        self.recover_notes()
    }
    fn recover_notes(&self) -> Result<()> {
        let Some(transaction) = self.load::<NotesTransaction>(NOTES_JOURNAL)? else {
            return Ok(());
        };
        self.atomic_write(NOTES_FILE, &transaction.notes)?;
        self.atomic_write(HISTORY_FILE, &transaction.history)?;
        fs::remove_file(self.directory.join(NOTES_JOURNAL))?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_backup_is_preserved_across_repeated_writes() {
        let temp = tempfile::tempdir().unwrap();
        fs::write(temp.path().join(NOTES_FILE), b"[]").unwrap();
        let store = Store::new(temp.path().into()).unwrap();
        store.save_notes(&[Note::default()], &[]).unwrap();
        store.save_notes(&[], &[Note::default()]).unwrap();
        assert_eq!(
            fs::read(temp.path().join(BACKUP_DIRECTORY).join(NOTES_FILE)).unwrap(),
            b"[]"
        );
        assert!(
            store
                .load::<Vec<Note>>(NOTES_FILE)
                .unwrap()
                .unwrap()
                .is_empty()
        );
        assert_eq!(
            store
                .load::<Vec<Note>>(HISTORY_FILE)
                .unwrap()
                .unwrap()
                .len(),
            1
        );
    }
    #[test]
    fn interrupted_archive_is_recovered_before_loading() {
        let temp = tempfile::tempdir().unwrap();
        let store = Store::new(temp.path().into()).unwrap();
        store.save(NOTES_FILE, &vec![Note::default()]).unwrap();
        store
            .atomic_write(
                NOTES_JOURNAL,
                &NotesTransaction {
                    notes: vec![],
                    history: vec![Note::default()],
                },
            )
            .unwrap();
        let recovered = Store::new(temp.path().into()).unwrap();
        assert!(
            recovered
                .load::<Vec<Note>>(NOTES_FILE)
                .unwrap()
                .unwrap()
                .is_empty()
        );
        assert_eq!(
            recovered
                .load::<Vec<Note>>(HISTORY_FILE)
                .unwrap()
                .unwrap()
                .len(),
            1
        );
    }
    #[test]
    fn malformed_data_is_reported_without_replacement() {
        let temp = tempfile::tempdir().unwrap();
        fs::write(temp.path().join(COMMANDS_FILE), b"{broken").unwrap();
        let store = Store::new(temp.path().into()).unwrap();
        assert!(store.load_all().is_err());
        assert_eq!(
            fs::read(temp.path().join(COMMANDS_FILE)).unwrap(),
            b"{broken"
        );
    }
}
