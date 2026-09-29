use super::*;

pub(super) struct NoteEditor {
    pub title: Entity<InputState>,
    pub content: Entity<TextareaState>,
    _subscriptions: Vec<Subscription>,
}
pub(super) struct ChoiceEditor {
    source: ParameterChoice,
    label: Entity<InputState>,
    value: Entity<InputState>,
}
pub(super) struct ParameterEditor {
    source: Parameter,
    label: Entity<InputState>,
    default_value: Entity<InputState>,
    choices: Vec<ChoiceEditor>,
}
enum EditorSource {
    Command(CommandItem),
    Tool(ToolItem),
}
pub(super) struct Editor {
    index: Option<usize>,
    source: EditorSource,
    title: Entity<InputState>,
    description: Entity<InputState>,
    body: Entity<TextareaState>,
    path: Entity<InputState>,
    arguments: Entity<InputState>,
    glyph: String,
    color: String,
    silent: bool,
    parameters: Vec<ParameterEditor>,
    error: String,
}
pub(super) struct RunPrompt {
    command: CommandItem,
    values: Vec<Entity<InputState>>,
    error: String,
}
pub(super) struct SettingsEditor {
    source: Settings,
    expand: Entity<InputState>,
    collapse: Entity<InputState>,
    history: Entity<InputState>,
    key: Entity<InputState>,
    error: String,
}
impl SettingsEditor {
    pub(super) fn set_startup(&mut self, enabled: bool) {
        self.source.start_with_windows = enabled;
    }

    pub fn new(settings: &Settings, window: &mut Window, cx: &mut App) -> Self {
        Self {
            source: settings.clone(),
            expand: input(
                &settings.expand_delay_ms.to_string(),
                text::EXPAND_DELAY,
                window,
                cx,
            ),
            collapse: input(
                &settings.collapse_delay_ms.to_string(),
                text::COLLAPSE_DELAY,
                window,
                cx,
            ),
            history: input(
                &settings.note_history_months.to_string(),
                text::HISTORY_MONTHS,
                window,
                cx,
            ),
            key: input(&settings.hotkey.key, text::HOTKEY, window, cx),
            error: String::new(),
        }
    }
}
impl ChoiceEditor {
    fn new(source: ParameterChoice, window: &mut Window, cx: &mut App) -> Self {
        Self {
            label: input(&source.label, text::CHOICE_LABEL, window, cx),
            value: input(&source.value, text::CHOICE_VALUE, window, cx),
            source,
        }
    }
    fn value(&self, cx: &App) -> ParameterChoice {
        ParameterChoice {
            label: self.label.read(cx).value().to_string(),
            value: self.value.read(cx).value().to_string(),
            ..self.source.clone()
        }
    }
}
impl ParameterEditor {
    fn new(source: Parameter, window: &mut Window, cx: &mut App) -> Self {
        Self {
            label: input(&source.label, text::PARAMETER_LABEL, window, cx),
            default_value: input(
                &source.prompt_default_value,
                text::DEFAULT_VALUE,
                window,
                cx,
            ),
            choices: source
                .choices
                .iter()
                .cloned()
                .map(|choice| ChoiceEditor::new(choice, window, cx))
                .collect(),
            source,
        }
    }
    fn value(&self, cx: &App) -> Parameter {
        Parameter {
            label: self.label.read(cx).value().to_string(),
            prompt_default_value: self.default_value.read(cx).value().to_string(),
            choices: self.choices.iter().map(|c| c.value(cx)).collect(),
            ..self.source.clone()
        }
    }
}
impl RunPrompt {
    pub fn new(command: CommandItem, window: &mut Window, cx: &mut App) -> Self {
        Self {
            values: command
                .parameters
                .iter()
                .map(|p| input(&p.default_value(), text::PROMPT, window, cx))
                .collect(),
            command,
            error: String::new(),
        }
    }
}

impl SidePeek {
    pub(super) fn rebuild_notes(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.note_editors.clear();
        for (index, note) in self.data.notes.iter().enumerate() {
            let title = input(&note.title, text::TITLE, window, cx);
            let content = textarea(&note.content, text::CONTENT, window, cx);
            let title_subscription = cx.subscribe_in(
                &title,
                window,
                move |this, state, event: &InputEvent, _, cx| {
                    if matches!(event, InputEvent::Change)
                        && let Some(note) = this.data.notes.get_mut(index)
                    {
                        note.title = state.read(cx).value().to_string();
                        note.updated_at = timestamp();
                        this.mark_dirty(Dataset::Notes, false);
                        cx.notify();
                    }
                },
            );
            let content_subscription = cx.subscribe_in(
                &content,
                window,
                move |this, state, event: &InputEvent, _, cx| {
                    if matches!(event, InputEvent::Change)
                        && let Some(note) = this.data.notes.get_mut(index)
                    {
                        note.content = state.read(cx).value().to_string();
                        note.updated_at = timestamp();
                        this.mark_dirty(Dataset::Notes, false);
                        cx.notify();
                    }
                },
            );
            self.note_editors.push(NoteEditor {
                title,
                content,
                _subscriptions: vec![title_subscription, content_subscription],
            });
        }
    }
    pub(super) fn add_note(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let index = self
            .data
            .notes
            .iter()
            .take_while(|note| note.is_pinned)
            .count();
        self.data.notes.insert(
            index,
            Note {
                color_hex: resources::PALETTE[self.data.notes.len() % resources::PALETTE.len()]
                    .into(),
                ..Default::default()
            },
        );
        self.show_history = false;
        self.search
            .update(cx, |state, cx| state.set_value("", window, cx));
        self.rebuild_notes(window, cx);
        self.mark_dirty(Dataset::Notes, true);
        self.note_editors[index]
            .title
            .update(cx, |state, cx| state.focus(window, cx));
        cx.notify();
    }
    pub(super) fn complete_note(
        &mut self,
        index: usize,
        restore: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if restore {
            if index >= self.data.history.len() {
                return;
            }
            let mut note = self.data.history.remove(index);
            note.completed_at = None;
            note.updated_at = timestamp();
            self.data.notes.insert(0, note);
            self.show_history = false;
        } else {
            if index >= self.data.notes.len() {
                return;
            }
            let mut note = self.data.notes.remove(index);
            note.completed_at = Some(timestamp());
            note.is_pinned = false;
            self.data.history.insert(0, note);
            prune_history(
                &mut self.data.history,
                self.data.settings.note_history_months,
                chrono::Local::now(),
            );
        }
        self.rebuild_notes(window, cx);
        self.mark_dirty(Dataset::Notes, true);
        cx.notify();
    }
    pub(super) fn open_editor(
        &mut self,
        tab: Tab,
        index: Option<usize>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let source = match tab {
            Tab::Commands => EditorSource::Command(
                index
                    .and_then(|i| self.data.commands.get(i))
                    .cloned()
                    .unwrap_or_default(),
            ),
            Tab::Tools => EditorSource::Tool(
                index
                    .and_then(|i| self.data.tools.get(i))
                    .cloned()
                    .unwrap_or_default(),
            ),
            _ => return,
        };
        let (title, description, glyph, color) = match &source {
            EditorSource::Command(item) => (
                &item.title,
                &item.description,
                &item.glyph,
                &item.accent_hex,
            ),
            EditorSource::Tool(item) => (
                &item.title,
                &item.description,
                &item.glyph,
                &item.accent_hex,
            ),
        };
        let (body, silent, parameters) = match &source {
            EditorSource::Command(item) => (
                item.command_text.clone(),
                item.silent_execution,
                item.parameters.clone(),
            ),
            _ => (String::new(), false, Vec::new()),
        };
        let (path, arguments) = match &source {
            EditorSource::Tool(item) => (item.exe_path.clone(), item.arguments.clone()),
            _ => (String::new(), String::new()),
        };
        self.editor = Some(Editor {
            index,
            title: input(title, text::TITLE, window, cx),
            description: input(description, text::DESCRIPTION, window, cx),
            body: textarea(&body, text::COMMAND_TEXT, window, cx),
            path: input(&path, text::EXE_PATH, window, cx),
            arguments: input(&arguments, text::ARGUMENTS, window, cx),
            glyph: glyph.clone(),
            color: color.clone(),
            silent,
            parameters: parameters
                .into_iter()
                .map(|p| ParameterEditor::new(p, window, cx))
                .collect(),
            error: String::new(),
            source,
        });
        self.dock.set_expanded(true, Instant::now());
        cx.notify();
    }
    pub(super) fn save_editor(&mut self, cx: &mut Context<Self>) {
        let Some(editor) = &mut self.editor else {
            return;
        };
        let title = editor.title.read(cx).value().trim().to_string();
        if title.is_empty() {
            editor.error = text::VALIDATE_TITLE.into();
            cx.notify();
            return;
        }
        let description = editor.description.read(cx).value().to_string();
        match &editor.source {
            EditorSource::Command(source) => {
                let command_text = editor.body.read(cx).value().to_string();
                if command_text.trim().is_empty() {
                    editor.error = text::VALIDATE_COMMAND.into();
                    cx.notify();
                    return;
                }
                let parameters: Vec<_> = editor.parameters.iter().map(|p| p.value(cx)).collect();
                if !parameters.iter().all(Parameter::valid) {
                    editor.error = text::VALIDATE_PARAMETER.into();
                    cx.notify();
                    return;
                }
                let command = CommandItem {
                    title,
                    description,
                    glyph: editor.glyph.clone(),
                    accent_hex: editor.color.clone(),
                    command_text,
                    silent_execution: editor.silent,
                    parameters,
                    ..source.clone()
                };
                if let Some(index) = editor.index {
                    self.data.commands[index] = command;
                } else {
                    self.data.commands.push(command);
                }
                self.mark_dirty(Dataset::Commands, true);
            }
            EditorSource::Tool(source) => {
                let exe_path = editor
                    .path
                    .read(cx)
                    .value()
                    .trim()
                    .trim_matches('"')
                    .to_string();
                if exe_path.is_empty() {
                    editor.error = text::VALIDATE_PATH.into();
                    cx.notify();
                    return;
                }
                let tool = ToolItem {
                    title,
                    description,
                    glyph: editor.glyph.clone(),
                    accent_hex: editor.color.clone(),
                    exe_path,
                    arguments: editor.arguments.read(cx).value().to_string(),
                    ..source.clone()
                };
                if let Some(index) = editor.index {
                    self.data.tools[index] = tool;
                } else {
                    self.data.tools.push(tool);
                }
                self.mark_dirty(Dataset::Tools, true);
            }
        }
        log::info!(target: LOG_TAG, "Item editor saved");
        self.editor = None;
        cx.notify();
    }
    fn browse_executable(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let picker = cx.prompt_for_paths(PathPromptOptions {
            files: true,
            directories: false,
            multiple: false,
            prompt: Some(text::EXE_PATH.into()),
        });
        cx.spawn_in(window, async move |this, cx| match picker.await {
            Ok(Ok(Some(paths))) => {
                if let Some(path) = paths.first() {
                    let value = path.to_string_lossy().into_owned();
                    let _ = this.update_in(cx, |this, window, cx| {
                        if let Some(editor) = &this.editor {
                            editor
                                .path
                                .update(cx, |state, cx| state.set_value(value, window, cx));
                        }
                    });
                }
            }
            Ok(Ok(None)) => {}
            _ => {
                let _ = this.update_in(cx, |this, _, cx| {
                    this.status = text::NATIVE_FAILED.into();
                    cx.notify();
                });
            }
        })
        .detach();
    }
    pub(super) fn render_editor(&self, cx: &mut Context<Self>) -> AnyElement {
        let editor = self.editor.as_ref().expect("editor present");
        let is_command = matches!(editor.source, EditorSource::Command(_));
        let mut content = column()
            .child(
                row()
                    .justify_between()
                    .child(
                        div()
                            .text_size(px(TITLE_SIZE))
                            .child(if editor.index.is_some() {
                                text::EDIT
                            } else {
                                text::ADD
                            }),
                    )
                    .child(
                        icon_button("cancel-editor", IconName::Close, text::CANCEL).on_click(
                            cx.listener(|this, _, _, cx| {
                                this.editor = None;
                                cx.notify();
                            }),
                        ),
                    ),
            )
            .child(field(text::TITLE, Input::new(&editor.title)))
            .child(field(text::DESCRIPTION, Input::new(&editor.description)))
            .child(field(
                text::ICON,
                row()
                    .flex_wrap()
                    .children(resources::ICONS.iter().enumerate().map(|(index, glyph)| {
                        let glyph = *glyph;
                        icon_button(("icon", index), icon_for(glyph), text::ICON)
                            .selected(editor.glyph == glyph)
                            .on_click(cx.listener(move |this, _, _, cx| {
                                if let Some(editor) = &mut this.editor {
                                    editor.glyph = glyph.into();
                                }
                                cx.notify();
                            }))
                    })),
            ))
            .child(field(
                text::COLOR,
                row()
                    .flex_wrap()
                    .children(resources::PALETTE.iter().enumerate().map(|(index, color)| {
                        let color = *color;
                        Button::new(("color", index))
                            .small()
                            .icon(Icon::new(IconName::CircleCheck).text_color(accent(color, cx)))
                            .selected(editor.color == color)
                            .accessibility_label(format!("{} {color}", text::COLOR))
                            .on_click(cx.listener(move |this, _, _, cx| {
                                if let Some(editor) = &mut this.editor {
                                    editor.color = color.into();
                                }
                                cx.notify();
                            }))
                    })),
            ));
        if is_command {
            content = content
                .child(field(
                    text::COMMAND_TEXT,
                    Textarea::new(&editor.body).h(px(EDITOR_HEIGHT)),
                ))
                .child(small(text::COMMAND_HINT, cx))
                .child(
                    Checkbox::new("silent")
                        .label(text::SILENT)
                        .checked(editor.silent)
                        .on_click(cx.listener(|this, checked, _, cx| {
                            if let Some(editor) = &mut this.editor {
                                editor.silent = *checked;
                            }
                            cx.notify();
                        })),
                )
                .child(
                    row().justify_between().child(text::PARAMETERS).child(
                        Button::new("add-parameter")
                            .small()
                            .label(text::ADD_PARAMETER)
                            .on_click(cx.listener(|this, _, window, cx| {
                                if let Some(editor) = &mut this.editor {
                                    editor.parameters.push(ParameterEditor::new(
                                        Parameter::default(),
                                        window,
                                        cx,
                                    ));
                                }
                                cx.notify();
                            })),
                    ),
                );
            for (index, parameter) in editor.parameters.iter().enumerate() {
                let mut parameter_view = card(cx)
                    .id(("parameter", index))
                    .child(
                        row()
                            .child(div().flex_1().child(
                                Input::new(&parameter.label).aria_label(text::PARAMETER_LABEL),
                            ))
                            .child(
                                icon_button("remove", IconName::Delete, text::DELETE).on_click(
                                    cx.listener(move |this, _, _, cx| {
                                        if let Some(editor) = &mut this.editor {
                                            editor.parameters.remove(index);
                                        }
                                        cx.notify();
                                    }),
                                ),
                            ),
                    )
                    .child(
                        row().children(
                            [
                                (ParameterMode::Prompt, text::PROMPT),
                                (ParameterMode::Choices, text::CHOICES),
                            ]
                            .into_iter()
                            .enumerate()
                            .map(|(mode_id, (mode, label))| {
                                Button::new(("mode", mode_id))
                                    .small()
                                    .label(label)
                                    .selected(parameter.source.mode == mode)
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        if let Some(editor) = &mut this.editor {
                                            editor.parameters[index].source.mode = mode;
                                        }
                                        cx.notify();
                                    }))
                            }),
                        ),
                    );
                if parameter.source.mode == ParameterMode::Prompt {
                    parameter_view = parameter_view.child(field(
                        text::DEFAULT_VALUE,
                        Input::new(&parameter.default_value),
                    ));
                } else {
                    for (choice_index, choice) in parameter.choices.iter().enumerate() {
                        parameter_view = parameter_view.child(
                            column()
                                .id(("choice", choice_index))
                                .gap(px(SPACE_SM))
                                .child(Input::new(&choice.label).aria_label(text::CHOICE_LABEL))
                                .child(Input::new(&choice.value).aria_label(text::CHOICE_VALUE))
                                .child(
                                    row()
                                        .justify_between()
                                        .child(
                                            Checkbox::new("default")
                                                .label(text::DEFAULT_CHOICE)
                                                .checked(choice.source.is_default)
                                                .on_click(cx.listener(
                                                    move |this, checked, _, cx| {
                                                        if let Some(editor) = &mut this.editor {
                                                            for (i, choice) in editor.parameters
                                                                [index]
                                                                .choices
                                                                .iter_mut()
                                                                .enumerate()
                                                            {
                                                                choice.source.is_default =
                                                                    i == choice_index && *checked;
                                                            }
                                                        }
                                                        cx.notify();
                                                    },
                                                )),
                                        )
                                        .child(
                                            icon_button(
                                                "remove-choice",
                                                IconName::Delete,
                                                text::DELETE,
                                            )
                                            .on_click(
                                                cx.listener(move |this, _, _, cx| {
                                                    if let Some(editor) = &mut this.editor {
                                                        editor.parameters[index]
                                                            .choices
                                                            .remove(choice_index);
                                                    }
                                                    cx.notify();
                                                }),
                                            ),
                                        ),
                                ),
                        );
                    }
                    parameter_view = parameter_view.child(
                        Button::new("add-choice")
                            .small()
                            .label(text::ADD_CHOICE)
                            .on_click(cx.listener(move |this, _, window, cx| {
                                if let Some(editor) = &mut this.editor {
                                    let choices = &mut editor.parameters[index].choices;
                                    choices.push(ChoiceEditor::new(
                                        ParameterChoice {
                                            is_default: choices.is_empty(),
                                            ..Default::default()
                                        },
                                        window,
                                        cx,
                                    ));
                                }
                                cx.notify();
                            })),
                    );
                }
                content = content.child(parameter_view);
            }
        } else {
            content = content
                .child(field(
                    text::EXE_PATH,
                    column()
                        .gap(px(SPACE_SM))
                        .child(Input::new(&editor.path))
                        .child(Button::new("browse").small().label(text::BROWSE).on_click(
                            cx.listener(|this, _, window, cx| this.browse_executable(window, cx)),
                        )),
                ))
                .child(field(text::ARGUMENTS, Input::new(&editor.arguments)));
        }
        content
            .child(
                div()
                    .text_color(cx.theme().danger)
                    .child(editor.error.clone()),
            )
            .child(
                row()
                    .justify_end()
                    .child(
                        Button::new("cancel")
                            .label(text::CANCEL)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.editor = None;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("save")
                            .primary()
                            .label(text::SAVE)
                            .on_click(cx.listener(|this, _, _, cx| this.save_editor(cx))),
                    ),
            )
            .into_any_element()
    }
    pub(super) fn render_run_prompt(&self, cx: &mut Context<Self>) -> AnyElement {
        let prompt = self.run_prompt.as_ref().expect("prompt present");
        let mut content = column().child(
            div()
                .text_size(px(TITLE_SIZE))
                .child(prompt.command.title.clone()),
        );
        for (index, parameter) in prompt.command.parameters.iter().enumerate() {
            let mut field_view = column().gap(px(SPACE_SM)).child(parameter.label.clone());
            if parameter.mode == ParameterMode::Prompt {
                field_view = field_view
                    .child(Input::new(&prompt.values[index]).aria_label(parameter.label.clone()));
            } else {
                field_view = field_view.child(
                    column()
                        .gap(px(SPACE_XS))
                        .children(parameter.choices.iter().enumerate().map(
                            |(choice_index, choice)| {
                                let value = choice.value.clone();
                                Button::new(("choice", choice_index))
                                    .label(choice.label.clone())
                                    .selected(
                                        prompt.values[index].read(cx).value().as_ref() == value,
                                    )
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        if let Some(prompt) = &this.run_prompt {
                                            prompt.values[index].update(cx, |state, cx| {
                                                state.set_value(value.clone(), window, cx)
                                            });
                                        }
                                        cx.notify();
                                    }))
                            },
                        )),
                );
            }
            content = content.child(field_view.id(("run-param", index)));
        }
        content
            .child(
                div()
                    .text_color(cx.theme().danger)
                    .child(prompt.error.clone()),
            )
            .child(
                row()
                    .justify_end()
                    .child(
                        Button::new("cancel-run")
                            .label(text::CANCEL)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.run_prompt = None;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("run")
                            .primary()
                            .label(text::RUN)
                            .on_click(cx.listener(|this, _, window, cx| {
                                if let Some(prompt) = &this.run_prompt {
                                    let values = prompt
                                        .values
                                        .iter()
                                        .map(|v| v.read(cx).value().to_string())
                                        .collect();
                                    let command = prompt.command.clone();
                                    this.start_run(command, values, window, cx);
                                }
                            })),
                    ),
            )
            .into_any_element()
    }
    fn save_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(editor) = &mut self.settings_editor else {
            return;
        };
        let mut next = editor.source.clone();
        let values = (
            editor.expand.read(cx).value().parse::<u64>(),
            editor.collapse.read(cx).value().parse::<u64>(),
            editor.history.read(cx).value().parse::<u32>(),
        );
        let (Ok(expand), Ok(collapse), Ok(history)) = values else {
            editor.error = text::VALIDATE_SETTINGS.into();
            cx.notify();
            return;
        };
        next.expand_delay_ms = expand;
        next.collapse_delay_ms = collapse;
        next.note_history_months = history;
        next.hotkey.key = editor.key.read(cx).value().trim().to_ascii_uppercase();
        if !next.valid() {
            editor.error = text::VALIDATE_SETTINGS.into();
            cx.notify();
            return;
        }
        let previous = self.data.settings.clone();
        let result = (|| -> anyhow::Result<()> {
            if next.hotkey != previous.hotkey {
                self.native.set_hotkey(next.hotkey.clone())?;
            }
            native::set_startup(next.start_with_windows)?;
            self.store.save(store::SETTINGS_FILE, &next)?;
            Ok(())
        })();
        if let Err(error) = result {
            // Keep disk and OS settings consistent when the hotkey is occupied or a write fails.
            let _ = self.native.set_hotkey(previous.hotkey);
            let _ = native::set_startup(previous.start_with_windows);
            editor.error = text::NATIVE_FAILED.into();
            log::error!(target: LOG_TAG, "Settings update failed: {error}");
            cx.notify();
            return;
        }
        self.data.settings = next;
        apply_theme(self.data.settings.theme, window, cx);
        if let Ok(display) = native::select_display(&self.data.settings.dock_display_device_name) {
            self.dock.place(display, &self.data.settings);
            self.report(native::set_bounds(self.hwnd, self.dock.current, true));
            window.bounds_changed(cx);
        }
        if prune_history(
            &mut self.data.history,
            self.data.settings.note_history_months,
            chrono::Local::now(),
        ) {
            self.mark_dirty(Dataset::Notes, true);
        }
        self.settings_editor = Some(SettingsEditor::new(&self.data.settings, window, cx));
        self.status = text::SETTINGS_SAVED.into();
        cx.notify();
    }
    pub(super) fn render_settings(&self, cx: &mut Context<Self>) -> AnyElement {
        let Some(editor) = &self.settings_editor else {
            return div().into_any_element();
        };
        column()
            .child(
                row()
                    .justify_between()
                    .child(div().text_size(px(TITLE_SIZE)).child(text::SETTINGS))
                    .child(small(text::VERSION, cx)),
            )
            .child(field(
                text::DOCK_EDGE,
                row().children(
                    [(DockEdge::Left, text::LEFT), (DockEdge::Right, text::RIGHT)]
                        .into_iter()
                        .enumerate()
                        .map(|(index, (edge, label))| {
                            Button::new(("edge", index))
                                .label(label)
                                .selected(editor.source.dock_edge == edge)
                                .on_click(cx.listener(move |this, _, _, cx| {
                                    if let Some(editor) = &mut this.settings_editor {
                                        editor.source.dock_edge = edge;
                                    }
                                    cx.notify();
                                }))
                        }),
                ),
            ))
            .child(field(
                text::DISPLAY,
                column()
                    .gap(px(SPACE_XS))
                    .children(
                        native::displays()
                            .iter()
                            .enumerate()
                            .map(|(index, display)| {
                                let name = display.name.clone();
                                let label = if display.primary {
                                    format!("{} · {}", text::PRIMARY_DISPLAY, name)
                                } else {
                                    name.clone()
                                };
                                Button::new(("display", index))
                                    .label(label)
                                    .selected(
                                        editor.source.dock_display_device_name == name
                                            || (editor.source.dock_display_device_name.is_empty()
                                                && display.primary),
                                    )
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        if let Some(editor) = &mut this.settings_editor {
                                            editor.source.dock_display_device_name = name.clone();
                                        }
                                        cx.notify();
                                    }))
                            }),
                    ),
            ))
            .child(field(
                text::THEME,
                row().children(
                    [
                        (ThemeMode::Light, text::LIGHT),
                        (ThemeMode::Dark, text::DARK),
                        (ThemeMode::System, text::SYSTEM),
                    ]
                    .into_iter()
                    .enumerate()
                    .map(|(index, (mode, label))| {
                        Button::new(("theme", index))
                            .label(label)
                            .selected(editor.source.theme == mode)
                            .on_click(cx.listener(move |this, _, _, cx| {
                                if let Some(editor) = &mut this.settings_editor {
                                    editor.source.theme = mode;
                                }
                                cx.notify();
                            }))
                    }),
                ),
            ))
            .child(field(text::EXPAND_DELAY, Input::new(&editor.expand)))
            .child(field(text::COLLAPSE_DELAY, Input::new(&editor.collapse)))
            .child(field(text::HISTORY_MONTHS, Input::new(&editor.history)))
            .child(
                Checkbox::new("startup")
                    .label(text::STARTUP)
                    .checked(editor.source.start_with_windows)
                    .on_click(cx.listener(|this, value, _, cx| {
                        if let Some(editor) = &mut this.settings_editor {
                            editor.source.start_with_windows = *value;
                        }
                        cx.notify();
                    })),
            )
            .child(field(
                text::HOTKEY,
                column()
                    .gap(px(SPACE_SM))
                    .child(
                        row()
                            .child(
                                Checkbox::new("ctrl")
                                    .label(text::CTRL)
                                    .checked(editor.source.hotkey.control)
                                    .on_click(cx.listener(|this, value, _, cx| {
                                        if let Some(editor) = &mut this.settings_editor {
                                            editor.source.hotkey.control = *value;
                                        }
                                        cx.notify();
                                    })),
                            )
                            .child(
                                Checkbox::new("alt")
                                    .label(text::ALT)
                                    .checked(editor.source.hotkey.alt)
                                    .on_click(cx.listener(|this, value, _, cx| {
                                        if let Some(editor) = &mut this.settings_editor {
                                            editor.source.hotkey.alt = *value;
                                        }
                                        cx.notify();
                                    })),
                            )
                            .child(
                                Checkbox::new("shift")
                                    .label(text::SHIFT)
                                    .checked(editor.source.hotkey.shift)
                                    .on_click(cx.listener(|this, value, _, cx| {
                                        if let Some(editor) = &mut this.settings_editor {
                                            editor.source.hotkey.shift = *value;
                                        }
                                        cx.notify();
                                    })),
                            ),
                    )
                    .child(Input::new(&editor.key))
                    .child(small(text::HOTKEY_HINT, cx)),
            ))
            .child(
                div()
                    .text_color(cx.theme().danger)
                    .child(editor.error.clone()),
            )
            .child(
                row()
                    .justify_end()
                    .child(
                        Button::new("reset-settings")
                            .label(text::CANCEL)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.settings_editor = None;
                                this.select_tab(Tab::Notes, window, cx);
                            })),
                    )
                    .child(
                        Button::new("save-settings")
                            .primary()
                            .label(text::SAVE)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.save_settings(window, cx)),
                            ),
                    ),
            )
            .child(
                Button::new("open-data")
                    .ghost()
                    .label(text::OPEN_DATA)
                    .on_click(cx.listener(|this, _, _, cx| {
                        this.report(native::open_directory(this.store.directory()));
                        cx.notify();
                    })),
            )
            .into_any_element()
    }
}
fn field(label: &'static str, control: impl IntoElement) -> Div {
    column()
        .gap(px(SPACE_SM))
        .child(div().text_size(px(SMALL_SIZE)).child(label))
        .child(control)
}
