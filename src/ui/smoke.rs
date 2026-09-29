//! Exercises the real native renderer using isolated fixture data. This module is
//! opt-in and is never included in release packages.
use super::*;
use anyhow::Context as _;

const FRAME_SETTLE: Duration = Duration::from_millis(650);
const RUN_DEADLINE: Duration = Duration::from_secs(15);

impl SidePeek {
    pub fn start_smoke(&mut self, window: &Window, cx: &mut Context<Self>) {
        if !smoke_enabled() {
            return;
        }
        self.dock.pinned = true;
        let directory = self.store.directory().join("screenshots");
        cx.spawn_in(window, async move |this, cx| {
            let result: anyhow::Result<()> = async {
                std::fs::create_dir_all(&directory)?;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| {
                    this.add_note(window, cx);
                    let editor = &this.note_editors[0];
                    editor.title.update(cx, |state, cx| {
                        state.replace_all("GPUI 便签验证", window, cx)
                    });
                    editor.content.update(cx, |state, cx| {
                        state.replace_all("中文输入与自动保存\n第二行内容", window, cx)
                    });
                })?;
                smol::Timer::after(FRAME_SETTLE + SAVE_DEBOUNCE).await;
                this.update_in(cx, |this, window, _| -> anyhow::Result<()> {
                    this.flush();
                    let saved = this
                        .store
                        .load::<Vec<Note>>(store::NOTES_FILE)?
                        .context("Notes were not saved")?;
                    anyhow::ensure!(
                            saved
                                .iter()
                                .any(|n| n.title == "GPUI 便签验证"
                                    && n.content.contains("第二行内容")),
                            "Input change was not persisted"
                        );
                    capture(this, window, &directory, "notes")?;
                    Ok(())
                })??;
                this.update_in(cx, |this, window, cx| {
                    this.complete_note(0, false, window, cx);
                    this.show_history = true;
                    cx.notify();
                })?;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "history")?;
                    this.complete_note(0, true, window, cx);
                    this.data.commands.push(CommandItem {
                        title: "输出验证".into(),
                        description: "测试标准输出与参数".into(),
                        command_text: "echo %1".into(),
                        parameters: vec![Parameter {
                            label: "内容".into(),
                            prompt_default_value: "SidePeek-GPUI".into(),
                            ..Default::default()
                        }],
                        ..Default::default()
                    });
                    this.data.tools.push(ToolItem {
                        title: "记事本".into(),
                        description: "Windows 文本编辑器".into(),
                        exe_path: "notepad.exe".into(),
                        ..Default::default()
                    });
                    this.data.clipboard.push(sidepeek::model::ClipboardItem {
                        text: "剪贴板历史示例\n支持搜索、复制和排序".into(),
                        captured_at: timestamp(),
                        ..Default::default()
                    });
                    this.select_tab(Tab::Commands, window, cx);
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "commands")?;
                    this.open_editor(
                        Tab::Commands,
                        Some(this.data.commands.len() - 1),
                        window,
                        cx,
                    );
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "command-editor")?;
                    this.save_editor(cx);
                    anyhow::ensure!(this.editor.is_none(), "Command editor did not save");
                    this.flush();
                    let saved = this
                        .store
                        .load::<Vec<CommandItem>>(store::COMMANDS_FILE)?
                        .context("Command editor did not persist its data")?;
                    anyhow::ensure!(
                        saved
                            .last()
                            .is_some_and(|command| command.command_text == "echo %1"
                                && command.parameters[0].prompt_default_value == "SidePeek-GPUI"),
                        "Saved command or parameters changed"
                    );
                    this.run_command(this.data.commands.len() - 1, window, cx);
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "parameters")?;
                    let command = this
                        .data
                        .commands
                        .last()
                        .context("Missing smoke command")?
                        .clone();
                    this.start_run(command, vec!["SidePeek-GPUI".into()], window, cx);
                    Ok(())
                })??;
                let deadline = Instant::now() + RUN_DEADLINE;
                loop {
                    smol::Timer::after(FRAME_SETTLE).await;
                    let finished = this.update_in(cx, |this, _, _| {
                        this.runs.iter().all(|r| r.control.is_none())
                    })?;
                    if finished {
                        break;
                    }
                    anyhow::ensure!(Instant::now() < deadline, "Command did not finish");
                }
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    anyhow::ensure!(
                        this.runs
                            .last()
                            .is_some_and(|r| r.output.contains("SidePeek-GPUI")
                                && r.status == text::FINISHED),
                        "Command output missing"
                    );
                    capture(this, window, &directory, "output")?;
                    this.select_tab(Tab::Tools, window, cx);
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "tools")?;
                    this.open_editor(Tab::Tools, None, window, cx);
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "tool-editor")?;
                    this.save_editor(cx);
                    anyhow::ensure!(
                        this.editor.is_some(),
                        "Empty tool was saved without validation"
                    );
                    this.editor = None;
                    this.select_tab(Tab::Clipboard, window, cx);
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "clipboard")?;
                    this.select_tab(Tab::Settings, window, cx);
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "settings")?;
                    this.select_tab(Tab::Notes, window, cx);
                    apply_theme(ThemeMode::Dark, window, cx);
                    cx.notify();
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "dark")?;
                    this.dock.set_expanded(false, Instant::now());
                    cx.notify();
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, cx| -> anyhow::Result<()> {
                    capture(this, window, &directory, "collapsed")?;
                    anyhow::ensure!(
                        !this.dock.expanded && !this.dock.is_animating(),
                        "Dock animation did not finish"
                    );
                    this.dock.set_expanded(true, Instant::now());
                    cx.notify();
                    Ok(())
                })??;
                smol::Timer::after(FRAME_SETTLE).await;
                this.update_in(cx, |this, window, _| -> anyhow::Result<()> {
                    capture(this, window, &directory, "reexpanded")?;
                    anyhow::ensure!(
                        this.dock.expanded && !this.dock.is_animating(),
                        "Dock did not expand again"
                    );
                    this.flush();
                    anyhow::ensure!(!this.save_failed, "Final save failed");
                    Ok(())
                })??;
                Ok(())
            }
            .await;
            let _ = this.update_in(cx, |this, window, cx| {
                let report = match result {
                    Ok(()) => "PASS".to_string(),
                    Err(error) => format!("FAIL: {error:#}"),
                };
                let _ = std::fs::write(this.store.directory().join("smoke-result.txt"), &report);
                log::info!(target: LOG_TAG, "Smoke test: {report}");
                window.blur(cx);
                window.remove_window();
                cx.defer(|cx| cx.quit());
            });
        })
        .detach();
    }
}

fn capture(
    this: &SidePeek,
    window: &mut Window,
    directory: &std::path::Path,
    name: &str,
) -> anyhow::Result<()> {
    let viewport = window.viewport_size();
    let scale = window.scale_factor();
    // A native resize can succeed while GPUI keeps the old logical viewport. Check
    // both coordinate systems to catch clipped content and invisible trigger bars.
    anyhow::ensure!(
        (viewport.width.as_f32() * scale - this.dock.current.width).abs() <= 1.0
            && (viewport.height.as_f32() * scale - this.dock.current.height).abs() <= 1.0,
        "GPUI viewport differs from dock bounds for {name}: viewport={viewport:?}, scale={scale}, dock={:?}",
        this.dock.current
    );
    let image = window.render_to_image()?;
    anyhow::ensure!(
        image.width() == this.dock.current.width.round() as u32
            && image.height() == this.dock.current.height.round() as u32,
        "Native render surface differs from dock bounds for {name}"
    );
    image.save(directory.join(format!("{name}.png")))?;
    Ok(())
}
