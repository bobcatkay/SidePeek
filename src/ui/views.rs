use super::*;

const RUN_PREVIEW_COUNT: usize = 3;
const MEMORY_PERCENT_SCALE: f32 = 100.0;

impl Render for SidePeek {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if !self.dock.expanded && !self.dock.is_animating() {
            return div()
                .id("trigger")
                .size_full()
                .rounded(px(CARD_RADIUS))
                .bg(cx.theme().primary)
                .cursor_pointer()
                .on_click(cx.listener(|this, _, _, cx| {
                    this.dock.set_expanded(true, Instant::now());
                    cx.notify();
                }))
                .into_any_element();
        }
        let editing = self.editor.is_some() || self.run_prompt.is_some() || self.confirm.is_some();
        let page = if self.confirm.is_some() {
            self.render_confirmation(cx)
        } else if self.editor.is_some() {
            self.render_editor(cx)
        } else if self.run_prompt.is_some() {
            self.render_run_prompt(cx)
        } else if self.current_run.is_some() {
            self.render_output(cx)
        } else {
            match self.tab {
                Tab::Notes => self.render_notes(cx),
                Tab::Commands => self.render_commands(cx),
                Tab::Tools => self.render_tools(cx),
                Tab::Clipboard => self.render_clipboard(cx),
                Tab::Settings => self.render_settings(cx),
            }
        };
        let status = if self.save_failed {
            text::SAVE_FAILED.to_string()
        } else {
            self.status.clone()
        };
        // Lay out the content at its expanded size while the native viewport animates.
        // This prevents text wrapping and card reflow on every animation frame.
        let scale = window.scale_factor();
        let mut panel = column()
            .absolute()
            .left(px((self.dock.expanded_rect.x - self.dock.current.x) / scale))
            .top(px((self.dock.expanded_rect.y - self.dock.current.y) / scale))
            .w(px(self.dock.expanded_rect.width / scale))
            .h(px(self.dock.expanded_rect.height / scale))
            .p(px(SPACE_LG))
            .bg(cx.theme().background.opacity(SURFACE_OPACITY))
            .text_color(cx.theme().foreground)
            .font_family("Microsoft YaHei UI")
            .text_size(px(BODY_SIZE))
            .child(
                row()
                    .justify_between()
                    .flex_shrink_0()
                    .child(
                        row()
                            .child(Icon::new(IconName::PanelRight).text_color(cx.theme().primary))
                            .child(
                                div()
                                    .font_weight(FontWeight::BOLD)
                                    .text_size(px(TITLE_SIZE))
                                    .child(text::APP),
                            ),
                    )
                    .child(
                        row()
                            .gap(px(SPACE_XS))
                            .child(
                                icon_button("pin-window", IconName::Star, text::PIN_WINDOW)
                                    .selected(self.dock.pinned)
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.dock.pinned = !this.dock.pinned;
                                        cx.notify();
                                    })),
                            )
                            .child(
                                icon_button("settings", IconName::Settings, text::SETTINGS)
                                    .disabled(editing)
                                    .selected(self.tab == Tab::Settings)
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.select_tab(Tab::Settings, window, cx)
                                    })),
                            )
                            .child(
                                icon_button("collapse", IconName::PanelRightClose, text::COLLAPSE)
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.dock.set_expanded(false, Instant::now());
                                        cx.notify();
                                    })),
                            ),
                    ),
            )
            .child(
                row()
                    .gap(px(SPACE_XS))
                    .p(px(SPACE_XS))
                    .rounded(px(CARD_RADIUS))
                    .bg(cx.theme().secondary)
                    .flex_shrink_0()
                    .children(Tab::MAIN.into_iter().enumerate().map(|(index, tab)| {
                        Button::new(("tab", index))
                            .small()
                            .flex_1()
                            .label(tab.label())
                            .selected(self.tab == tab)
                            .disabled(editing)
                            .on_click(cx.listener(move |this, _, window, cx| {
                                this.select_tab(tab, window, cx)
                            }))
                    })),
            )
            .when(
                !editing && self.current_run.is_none() && self.tab != Tab::Settings,
                |view| {
                    view.child(
                        Input::new(&self.search)
                            .prefix(IconName::Search)
                            .aria_label(text::SEARCH),
                    )
                },
            )
            .child(
                div()
                    .id(("body", self.tab as usize))
                    .flex_1()
                    .min_h(px(0.0))
                    .overflow_y_scroll()
                    .child(page),
            );
        if !status.is_empty() {
            panel = panel.child(
                column()
                    .gap(px(SPACE_XS))
                    .child(
                        div()
                            .text_size(px(SMALL_SIZE))
                            .text_color(if self.save_failed {
                                cx.theme().danger
                            } else {
                                cx.theme().muted_foreground
                            })
                            .child(status),
                    )
                    .when(self.save_failed, |view| {
                        view.child(
                            Button::new("retry-save")
                                .small()
                                .label(text::RETRY)
                                .on_click(cx.listener(|this, _, _, cx| {
                                    this.flush();
                                    cx.notify();
                                })),
                        )
                    }),
            );
        }
        if !editing && self.current_run.is_none() {
            panel = panel.child(column().gap(px(SPACE_XS)).children(
                self.runs.iter().rev().take(RUN_PREVIEW_COUNT).map(|run| {
                    let id = run.id;
                    Button::new(("run-result", id))
                        .ghost()
                        .small()
                        .label(format!("{} · {}", run.command.title, run.status))
                        .on_click(
                            cx.listener(move |this, _, window, cx| this.open_run(id, window, cx)),
                        )
                }),
            ));
        }
        panel = panel.child(
            row()
                .justify_between()
                .flex_shrink_0()
                .child(small(self.data.settings.hotkey.display(), cx))
                .child(small(
                    if self.dirty.is_empty() {
                        text::NOTE_SAVED
                    } else {
                        ""
                    },
                    cx,
                )),
        );
        div()
            .relative()
            .size_full()
            .overflow_hidden()
            .child(panel)
            .into_any_element()
    }
}

impl SidePeek {
    fn query(&self, cx: &App) -> String {
        self.search.read(cx).value().trim().to_lowercase()
    }
    fn matches(query: &str, title: &str, description: &str) -> bool {
        query.is_empty()
            || title.to_lowercase().contains(query)
            || description.to_lowercase().contains(query)
    }
    fn drag_handle(&self, tab: Tab, index: usize, label: String) -> impl IntoElement {
        div()
            .id(("drag", index))
            .cursor_grab()
            .p(px(SPACE_XS))
            .child(Icon::new(IconName::Menu).size(px(ICON_SIZE)))
            .on_drag(DragItem { tab, index, label }, |item, _, _, cx| {
                cx.new(|_| item.clone())
            })
    }
    fn section_header(&self, title: &'static str, tab: Tab, cx: &mut Context<Self>) -> Div {
        row()
            .justify_between()
            .child(
                div()
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_size(px(TITLE_SIZE))
                    .child(title),
            )
            .child(
                Button::new("add")
                    .primary()
                    .small()
                    .icon(IconName::Plus)
                    .label(text::ADD)
                    .on_click(cx.listener(move |this, _, window, cx| {
                        if tab == Tab::Notes {
                            this.add_note(window, cx);
                        } else {
                            this.open_editor(tab, None, window, cx);
                        }
                    })),
            )
    }
    fn render_notes(&self, cx: &mut Context<Self>) -> AnyElement {
        let query = self.query(cx);
        let mut content = column()
            .child(self.metrics(cx))
            .child(self.section_header(text::NOTES, Tab::Notes, cx))
            .child(
                row()
                    .child(
                        Button::new("active-notes")
                            .small()
                            .label(text::ACTIVE)
                            .selected(!self.show_history)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.show_history = false;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("completed-notes")
                            .small()
                            .label(format!("{} · {}", text::HISTORY, self.data.history.len()))
                            .selected(self.show_history)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.show_history = true;
                                cx.notify();
                            })),
                    ),
            );
        let mut visible = 0;
        if self.show_history {
            let cutoff = HISTORY_DAYS[self.history_range]
                .map(|days| chrono::Local::now() - chrono::Duration::days(days));
            content = content.child(
                row().gap(px(SPACE_XS)).children(
                    [
                        text::WEEK,
                        text::MONTH,
                        text::QUARTER,
                        text::YEAR,
                        text::ALL,
                    ]
                    .into_iter()
                    .enumerate()
                    .map(|(index, label)| {
                        Button::new(("range", index))
                            .small()
                            .label(label)
                            .selected(index == self.history_range)
                            .on_click(cx.listener(move |this, _, _, cx| {
                                this.history_range = index;
                                cx.notify();
                            }))
                    }),
                ),
            );
            for (index, note) in self.data.history.iter().enumerate() {
                if !Self::matches(&query, &note.title, &note.content)
                    || cutoff.is_some_and(|cutoff| {
                        note.completed_time().is_some_and(|date| date < cutoff)
                    })
                {
                    continue;
                }
                visible += 1;
                content = content.child(
                    card(cx)
                        .id(("history", index))
                        .child(
                            row()
                                .justify_between()
                                .child(div().flex_1().child(note.title.clone()))
                                .child(
                                    icon_button("restore", IconName::Undo, text::RESTORE).on_click(
                                        cx.listener(move |this, _, window, cx| {
                                            this.complete_note(index, true, window, cx)
                                        }),
                                    ),
                                )
                                .child(
                                    icon_button("delete", IconName::Delete, text::DELETE).on_click(
                                        cx.listener(move |this, _, _, cx| {
                                            this.confirm = Some(Confirm::Delete(Tab::Notes, index));
                                            cx.notify();
                                        }),
                                    ),
                                ),
                        )
                        .child(small(
                            note.content
                                .chars()
                                .take(MAX_VISIBLE_PREVIEW_CHARS)
                                .collect::<String>(),
                            cx,
                        ))
                        .child(small(
                            note.completed_time()
                                .map(|d| d.format("%Y-%m-%d %H:%M").to_string())
                                .unwrap_or_default(),
                            cx,
                        )),
                );
            }
        } else {
            for (index, (note, editor)) in
                self.data.notes.iter().zip(&self.note_editors).enumerate()
            {
                if !Self::matches(&query, &note.title, &note.content) {
                    continue;
                }
                visible += 1;
                content = content.child(
                    card(cx)
                        .id(("note", index))
                        .border_l(px(NOTE_STRIPE_WIDTH))
                        .border_color(accent(&note.color_hex, cx))
                        .on_drop(cx.listener(move |this, source: &DragItem, window, cx| {
                            if source.tab == Tab::Notes {
                                this.reorder(Tab::Notes, source.index, index, window, cx);
                            }
                        }))
                        .child(
                            row()
                                .gap(px(SPACE_XS))
                                .child(self.drag_handle(Tab::Notes, index, note.title.clone()))
                                .child(div().flex_1())
                                .child(
                                    icon_button("color", IconName::Palette, text::COLOR).on_click(
                                        cx.listener(move |this, _, _, cx| {
                                            let note = &mut this.data.notes[index];
                                            let color_index = resources::PALETTE
                                                .iter()
                                                .position(|c| *c == note.color_hex)
                                                .unwrap_or(0);
                                            note.color_hex = resources::PALETTE
                                                [(color_index + 1) % resources::PALETTE.len()]
                                            .into();
                                            this.mark_dirty(Dataset::Notes, true);
                                            cx.notify();
                                        }),
                                    ),
                                )
                                .child(
                                    icon_button(
                                        "pin",
                                        IconName::Star,
                                        if note.is_pinned {
                                            text::UNPIN
                                        } else {
                                            text::PIN
                                        },
                                    )
                                    .selected(note.is_pinned)
                                    .on_click(cx.listener(
                                        move |this, _, window, cx| {
                                            this.data.notes[index].is_pinned =
                                                !this.data.notes[index].is_pinned;
                                            if this.data.notes[index].is_pinned {
                                                this.reorder(Tab::Notes, index, 0, window, cx);
                                            }
                                            this.mark_dirty(Dataset::Notes, true);
                                            cx.notify();
                                        },
                                    )),
                                )
                                .child(
                                    icon_button("up", IconName::ArrowUp, text::PIN)
                                        .disabled(index == 0)
                                        .on_click(cx.listener(move |this, _, window, cx| {
                                            this.reorder(Tab::Notes, index, 0, window, cx)
                                        })),
                                )
                                .child(
                                    icon_button("complete", IconName::Check, text::COMPLETE)
                                        .on_click(cx.listener(move |this, _, window, cx| {
                                            this.complete_note(index, false, window, cx)
                                        })),
                                )
                                .child(
                                    icon_button("delete", IconName::Delete, text::DELETE).on_click(
                                        cx.listener(move |this, _, _, cx| {
                                            this.confirm = Some(Confirm::Delete(Tab::Notes, index));
                                            cx.notify();
                                        }),
                                    ),
                                ),
                        )
                        .child(
                            Input::new(&editor.title)
                                .appearance(false)
                                .aria_label(text::TITLE),
                        )
                        .child(
                            Textarea::new(&editor.content)
                                .appearance(false)
                                .h(px(CONTENT_HEIGHT))
                                .aria_label(text::CONTENT),
                        ),
                );
            }
        }
        if visible == 0 {
            content = content.child(empty(
                if !query.is_empty() {
                    text::NO_RESULTS
                } else if self.show_history {
                    text::EMPTY_HISTORY
                } else {
                    text::EMPTY_NOTES
                },
                if self.show_history {
                    ""
                } else {
                    text::EMPTY_NOTES_DETAIL
                },
                cx,
            ));
        }
        content.into_any_element()
    }
    fn render_commands(&self, cx: &mut Context<Self>) -> AnyElement {
        let query = self.query(cx);
        let mut cards = Vec::new();
        for (index, item) in self.data.commands.iter().enumerate() {
            if !Self::matches(&query, &item.title, &item.description) {
                continue;
            }
            let running = self.runs.iter().any(|r| {
                r.control.is_some()
                    && r.command.title == item.title
                    && r.command.command_text == item.command_text
            });
            cards.push(
                card(cx)
                    .id(("command", index))
                    .min_h(px(CARD_MIN_HEIGHT))
                    .on_drop(cx.listener(move |this, source: &DragItem, window, cx| {
                        if source.tab == Tab::Commands {
                            this.reorder(Tab::Commands, source.index, index, window, cx);
                        }
                    }))
                    .child(
                        row()
                            .justify_between()
                            .child(
                                Icon::new(icon_for(&item.glyph))
                                    .text_color(accent(&item.accent_hex, cx)),
                            )
                            .child(self.drag_handle(Tab::Commands, index, item.title.clone())),
                    )
                    .child(
                        Button::new("execute")
                            .ghost()
                            .label(item.title.clone())
                            .loading(running)
                            .disabled(running)
                            .on_click(cx.listener(move |this, _, window, cx| {
                                this.run_command(index, window, cx)
                            })),
                    )
                    .child(small(item.description.clone(), cx))
                    .child(
                        row()
                            .gap(px(SPACE_XS))
                            .justify_end()
                            .child(
                                icon_button("edit", IconName::Settings2, text::EDIT).on_click(
                                    cx.listener(move |this, _, window, cx| {
                                        this.open_editor(Tab::Commands, Some(index), window, cx)
                                    }),
                                ),
                            )
                            .child(
                                icon_button("top", IconName::ArrowUp, text::PIN)
                                    .disabled(index == 0)
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.reorder(Tab::Commands, index, 0, window, cx)
                                    })),
                            )
                            .child(
                                icon_button("delete", IconName::Delete, text::DELETE).on_click(
                                    cx.listener(move |this, _, _, cx| {
                                        this.confirm = Some(Confirm::Delete(Tab::Commands, index));
                                        cx.notify();
                                    }),
                                ),
                            ),
                    ),
            );
        }
        let no_cards = cards.is_empty();
        column()
            .child(self.section_header(text::COMMANDS, Tab::Commands, cx))
            .child(
                div()
                    .grid()
                    .grid_cols(GRID_COLUMNS as u16)
                    .gap(px(SPACE_MD))
                    .children(cards),
            )
            .when(no_cards, |view| {
                view.child(empty(
                    if query.is_empty() {
                        text::EMPTY_COMMANDS
                    } else {
                        text::NO_RESULTS
                    },
                    text::EMPTY_COMMANDS_DETAIL,
                    cx,
                ))
            })
            .into_any_element()
    }
    fn render_tools(&self, cx: &mut Context<Self>) -> AnyElement {
        let query = self.query(cx);
        let mut cards = Vec::new();
        for (index, item) in self.data.tools.iter().enumerate() {
            if !Self::matches(&query, &item.title, &item.description) {
                continue;
            }
            cards.push(
                card(cx)
                    .id(("tool", index))
                    .min_h(px(CARD_MIN_HEIGHT))
                    .on_drop(cx.listener(move |this, source: &DragItem, window, cx| {
                        if source.tab == Tab::Tools {
                            this.reorder(Tab::Tools, source.index, index, window, cx);
                        }
                    }))
                    .child(
                        row()
                            .justify_between()
                            .child(
                                Icon::new(icon_for(&item.glyph))
                                    .text_color(accent(&item.accent_hex, cx)),
                            )
                            .child(self.drag_handle(Tab::Tools, index, item.title.clone())),
                    )
                    .child(
                        Button::new("launch")
                            .ghost()
                            .label(item.title.clone())
                            .on_click(cx.listener(move |this, _, _, cx| {
                                let item = &this.data.tools[index];
                                this.report(native::shell_open(&item.exe_path, &item.arguments));
                                cx.notify();
                            })),
                    )
                    .child(small(item.description.clone(), cx))
                    .child(
                        row()
                            .gap(px(SPACE_XS))
                            .justify_end()
                            .child(
                                icon_button("edit", IconName::Settings2, text::EDIT).on_click(
                                    cx.listener(move |this, _, window, cx| {
                                        this.open_editor(Tab::Tools, Some(index), window, cx)
                                    }),
                                ),
                            )
                            .child(
                                icon_button("top", IconName::ArrowUp, text::PIN)
                                    .disabled(index == 0)
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.reorder(Tab::Tools, index, 0, window, cx)
                                    })),
                            )
                            .child(
                                icon_button("delete", IconName::Delete, text::DELETE).on_click(
                                    cx.listener(move |this, _, _, cx| {
                                        this.confirm = Some(Confirm::Delete(Tab::Tools, index));
                                        cx.notify();
                                    }),
                                ),
                            ),
                    ),
            );
        }
        let no_cards = cards.is_empty();
        column()
            .child(self.metrics(cx))
            .child(self.section_header(text::LAUNCHERS, Tab::Tools, cx))
            .child(
                div()
                    .grid()
                    .grid_cols(GRID_COLUMNS as u16)
                    .gap(px(SPACE_MD))
                    .children(cards),
            )
            .when(no_cards, |view| {
                view.child(empty(
                    if query.is_empty() {
                        text::EMPTY_TOOLS
                    } else {
                        text::NO_RESULTS
                    },
                    text::EMPTY_TOOLS_DETAIL,
                    cx,
                ))
            })
            .into_any_element()
    }
    fn render_clipboard(&self, cx: &mut Context<Self>) -> AnyElement {
        let query = self.query(cx);
        let mut visible = 0;
        let mut content = column()
            .child(
                row()
                    .justify_between()
                    .child(div().text_size(px(TITLE_SIZE)).child(text::CLIPBOARD))
                    .child(
                        Button::new("clear")
                            .small()
                            .label(text::CLEAR)
                            .disabled(self.data.clipboard.is_empty())
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.confirm = Some(Confirm::ClearClipboard);
                                cx.notify();
                            })),
                    ),
            )
            .child(small(text::CLIPBOARD_DETAIL, cx));
        for (index, item) in self.data.clipboard.iter().enumerate() {
            if !Self::matches(&query, &item.text, "") {
                continue;
            }
            visible += 1;
            content = content.child(
                card(cx)
                    .id(("clipboard", index))
                    .on_drop(cx.listener(move |this, source: &DragItem, window, cx| {
                        if source.tab == Tab::Clipboard {
                            this.reorder(Tab::Clipboard, source.index, index, window, cx);
                        }
                    }))
                    .child(
                        div().child(
                            item.text
                                .chars()
                                .take(MAX_VISIBLE_PREVIEW_CHARS)
                                .collect::<String>(),
                        ),
                    )
                    .child(
                        row()
                            .gap(px(SPACE_XS))
                            .child(self.drag_handle(Tab::Clipboard, index, text::CLIPBOARD.into()))
                            .child(
                                div().flex_1().child(small(
                                    parse_timestamp(&item.captured_at)
                                        .map(|d| d.format("%m-%d %H:%M").to_string())
                                        .unwrap_or_default(),
                                    cx,
                                )),
                            )
                            .child(icon_button("copy", IconName::Copy, text::COPY).on_click(
                                cx.listener(move |this, _, _, cx| {
                                    if let Some(item) = this.data.clipboard.get(index) {
                                        this.copy(item.text.clone(), cx);
                                    }
                                }),
                            ))
                            .child(
                                icon_button("top", IconName::ArrowUp, text::PIN)
                                    .disabled(index == 0)
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.reorder(Tab::Clipboard, index, 0, window, cx)
                                    })),
                            )
                            .child(
                                icon_button("delete", IconName::Delete, text::DELETE).on_click(
                                    cx.listener(move |this, _, _, cx| {
                                        this.confirm = Some(Confirm::Delete(Tab::Clipboard, index));
                                        cx.notify();
                                    }),
                                ),
                            ),
                    ),
            );
        }
        if visible == 0 {
            content = content.child(empty(
                if query.is_empty() {
                    text::EMPTY_CLIPBOARD
                } else {
                    text::NO_RESULTS
                },
                "",
                cx,
            ));
        }
        content.into_any_element()
    }
    fn metrics(&self, cx: &App) -> Div {
        row()
            .items_stretch()
            .child(
                card(cx)
                    .flex_1()
                    .gap(px(SPACE_XS))
                    .child(div().text_size(px(CLOCK_SIZE)).child(self.clock.clone()))
                    .child(small(self.date.clone(), cx)),
            )
            .child(
                card(cx)
                    .flex_1()
                    .gap(px(SPACE_SM))
                    .child(small(text::MEMORY, cx))
                    .when_some(self.memory, |view, (percent, used, total)| {
                        view.child(format!("{used:.1} / {total:.1} GB")).child(
                            div()
                                .w_full()
                                .h(px(SPACE_XS))
                                .rounded(px(SPACE_XS))
                                .bg(cx.theme().secondary)
                                .child(
                                    div()
                                        .h_full()
                                        .w(relative(percent as f32 / MEMORY_PERCENT_SCALE))
                                        .rounded(px(SPACE_XS))
                                        .bg(cx.theme().primary),
                                ),
                        )
                    }),
            )
    }
    fn render_confirmation(&self, cx: &mut Context<Self>) -> AnyElement {
        let clear = matches!(self.confirm, Some(Confirm::ClearClipboard));
        card(cx)
            .child(div().text_size(px(TITLE_SIZE)).child(if clear {
                text::CLEAR_CLIPBOARD
            } else {
                text::CONFIRM_DELETE
            }))
            .child(small(
                if clear {
                    text::CLEAR_CLIPBOARD_DETAIL
                } else {
                    text::DELETE_DETAIL
                },
                cx,
            ))
            .child(
                row()
                    .justify_end()
                    .child(
                        Button::new("cancel-delete")
                            .label(text::CANCEL)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.confirm = None;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("confirm-delete")
                            .danger()
                            .label(if clear { text::CLEAR } else { text::DELETE })
                            .on_click(
                                cx.listener(|this, _, window, cx| this.perform_delete(window, cx)),
                            ),
                    ),
            )
            .into_any_element()
    }
    fn render_output(&self, cx: &mut Context<Self>) -> AnyElement {
        let Some(run) = self.runs.iter().find(|r| Some(r.id) == self.current_run) else {
            return div().into_any_element();
        };
        let id = run.id;
        column()
            .child(
                div()
                    .text_size(px(TITLE_SIZE))
                    .child(run.command.title.clone()),
            )
            .child(small(run.status, cx))
            .child(
                Textarea::new(&run.output_state)
                    .readonly(true)
                    .h(px(OUTPUT_HEIGHT))
                    .aria_label(text::OUTPUT)
                    .font_family("Cascadia Mono"),
            )
            .child(
                row()
                    .flex_wrap()
                    .child(
                        Button::new("copy-output")
                            .label(text::COPY)
                            .on_click(cx.listener(move |this, _, _, cx| {
                                if let Some(run) = this.runs.iter().find(|r| r.id == id) {
                                    this.copy(run.output.clone(), cx);
                                }
                            })),
                    )
                    .child(
                        Button::new("stop")
                            .danger()
                            .disabled(run.control.is_none())
                            .label(text::STOP)
                            .on_click(cx.listener(move |this, _, _, cx| {
                                if let Some(run) = this.runs.iter_mut().find(|r| r.id == id)
                                    && let Some(control) = &run.control
                                {
                                    control.cancel();
                                    run.status = text::STOPPING;
                                }
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("close-output")
                            .label(text::CLOSE)
                            .on_click(cx.listener(move |this, _, _, cx| {
                                if let Some(run) = this.runs.iter_mut().find(|r| r.id == id) {
                                    if let Some(control) = &run.control {
                                        control.cancel();
                                        run.status = text::STOPPING;
                                        run.close_when_stopped = true;
                                    } else {
                                        this.current_run = None;
                                    }
                                }
                                cx.notify();
                            })),
                    ),
            )
            .into_any_element()
    }
}
fn empty(title: &'static str, description: &'static str, cx: &App) -> Div {
    column()
        .py(px(SPACE_XL))
        .items_center()
        .child(Icon::new(IconName::Inbox).text_color(cx.theme().muted_foreground))
        .child(title)
        .child(small(description, cx))
}
