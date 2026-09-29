use crate::{
    model::{DockEdge, Settings},
    resources::{LOG_TAG, layout},
};
use std::time::{Duration, Instant};

pub const POLL_INTERVAL: Duration = Duration::from_millis(25);
pub const ANIMATION_INTERVAL: Duration = Duration::from_millis(15);
pub const ANIMATION_DURATION: Duration = Duration::from_millis(220);
pub const DISPLAY_REFRESH_INTERVAL: Duration = Duration::from_secs(2);
const CUBIC_EXPONENT: i32 = 3;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Rect {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}
impl Rect {
    pub fn contains(&self, x: f32, y: f32) -> bool {
        x >= self.x && x < self.x + self.width && y >= self.y && y < self.y + self.height
    }
    pub fn interpolate(self, other: Self, progress: f32) -> Self {
        let k = 1.0 - (1.0 - progress.clamp(0.0, 1.0)).powi(CUBIC_EXPONENT);
        Self {
            x: self.x + (other.x - self.x) * k,
            y: self.y + (other.y - self.y) * k,
            width: self.width + (other.width - self.width) * k,
            height: self.height + (other.height - self.height) * k,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct Display {
    pub name: String,
    pub work_area: Rect,
    pub scale: f32,
    pub primary: bool,
}

pub fn dock_rects(display: &Display, edge: DockEdge) -> (Rect, Rect) {
    let area = display.work_area;
    let width = (layout::PANEL_WIDTH * display.scale)
        .min(area.width)
        .max(1.0);
    let strip = (layout::TRIGGER_WIDTH * display.scale).min(width);
    let height = (area.height * layout::TRIGGER_RATIO).max(1.0);
    let right = edge != DockEdge::Left;
    (
        Rect {
            x: area.x + if right { area.width - width } else { 0.0 },
            y: area.y,
            width,
            height: area.height,
        },
        Rect {
            x: area.x + if right { area.width - strip } else { 0.0 },
            y: area.y + (area.height - height) / 2.0,
            width: strip,
            height,
        },
    )
}

pub struct Dock {
    pub expanded: bool,
    pub pinned: bool,
    pub expanded_rect: Rect,
    pub collapsed_rect: Rect,
    pub current: Rect,
    pub display: Display,
    hover_since: Option<Instant>,
    outside_since: Option<Instant>,
    animation: Option<(Instant, Rect, Rect)>,
}
impl Dock {
    pub fn new(display: Display, settings: &Settings) -> Self {
        let (expanded_rect, collapsed_rect) = dock_rects(&display, settings.dock_edge);
        Self {
            expanded: true,
            pinned: false,
            current: expanded_rect,
            display,
            expanded_rect,
            collapsed_rect,
            hover_since: None,
            outside_since: None,
            animation: None,
        }
    }
    pub fn place(&mut self, display: Display, settings: &Settings) {
        let (expanded, collapsed) = dock_rects(&display, settings.dock_edge);
        self.display = display;
        self.expanded_rect = expanded;
        self.collapsed_rect = collapsed;
        self.current = if self.expanded { expanded } else { collapsed };
        self.animation = None;
        self.reset_hover();
        log::info!(target: LOG_TAG, "Dock placement updated, edge={:?}", settings.dock_edge);
    }
    pub fn set_expanded(&mut self, expanded: bool, now: Instant) {
        if self.expanded == expanded {
            return;
        }
        self.expanded = expanded;
        self.reset_hover();
        self.animation = Some((
            now,
            self.current,
            if expanded {
                self.expanded_rect
            } else {
                self.collapsed_rect
            },
        ));
        log::debug!(target: LOG_TAG, "Dock expanded={expanded}");
    }
    pub fn toggle(&mut self) {
        self.set_expanded(!self.expanded, Instant::now());
    }
    pub fn is_animating(&self) -> bool {
        self.animation.is_some()
    }
    pub fn reset_hover(&mut self) {
        self.hover_since = None;
        self.outside_since = None;
    }
    pub fn tick(
        &mut self,
        cursor: Option<(f32, f32)>,
        suspended: bool,
        settings: &Settings,
        now: Instant,
    ) -> bool {
        let before = self.current;
        if suspended || cursor.is_none() {
            self.reset_hover();
        } else if let Some((x, y)) = cursor {
            if !self.expanded {
                if self.collapsed_rect.contains(x, y) {
                    let start = *self.hover_since.get_or_insert(now);
                    if now.duration_since(start) >= Duration::from_millis(settings.expand_delay_ms)
                    {
                        self.set_expanded(true, now);
                    }
                } else {
                    self.hover_since = None;
                }
            } else if self.pinned || self.expanded_rect.contains(x, y) {
                self.outside_since = None;
            } else {
                let start = *self.outside_since.get_or_insert(now);
                if now.duration_since(start) >= Duration::from_millis(settings.collapse_delay_ms) {
                    self.set_expanded(false, now);
                }
            }
        }
        if let Some((started, from, target)) = self.animation {
            let progress =
                now.duration_since(started).as_secs_f32() / ANIMATION_DURATION.as_secs_f32();
            self.current = from.interpolate(target, progress);
            if progress >= 1.0 {
                self.current = target;
                self.animation = None;
            }
        }
        before != self.current
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn display() -> Display {
        Display {
            name: "test".into(),
            work_area: Rect {
                x: -1920.0,
                y: 0.0,
                width: 1920.0,
                height: 1040.0,
            },
            scale: 1.5,
            primary: false,
        }
    }
    #[test]
    fn placement_respects_negative_monitor_origin_and_dpi() {
        let (expanded, collapsed) = dock_rects(&display(), DockEdge::Right);
        assert_eq!(expanded.x + expanded.width, 0.0);
        assert_eq!(expanded.width, layout::PANEL_WIDTH * display().scale);
        assert_eq!(
            collapsed.y + collapsed.height / 2.0,
            display().work_area.height / 2.0
        );
    }
    #[test]
    fn hover_requires_continuous_dwell_and_suspension_resets_it() {
        let settings = Settings::default();
        let mut dock = Dock::new(display(), &settings);
        let start = Instant::now();
        dock.set_expanded(false, start);
        let inside = Some((dock.collapsed_rect.x, dock.collapsed_rect.y));
        dock.tick(inside, false, &settings, start);
        dock.tick(
            None,
            true,
            &settings,
            start + Duration::from_millis(settings.expand_delay_ms),
        );
        assert!(!dock.expanded);
        let next = start + Duration::from_secs(2);
        dock.tick(inside, false, &settings, next);
        assert!(!dock.expanded);
        dock.tick(
            inside,
            false,
            &settings,
            next + Duration::from_millis(settings.expand_delay_ms),
        );
        assert!(dock.expanded);
    }
    #[test]
    fn pinned_panel_does_not_collapse_but_toggle_still_works() {
        let settings = Settings::default();
        let mut dock = Dock::new(display(), &settings);
        dock.pinned = true;
        let now = Instant::now();
        dock.tick(Some((1.0, 1.0)), false, &settings, now);
        dock.tick(
            Some((1.0, 1.0)),
            false,
            &settings,
            now + Duration::from_secs(2),
        );
        assert!(dock.expanded);
        dock.toggle();
        assert!(!dock.expanded);
    }
}
