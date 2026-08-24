---
paths:
  - "unity/CXRBrownfield/Assets/Scripts/UIShell.cs"
  - "unity/CXRBrownfield/Assets/Scripts/LibraryBrowser.cs"
  - "unity/CXRBrownfield/Assets/Scripts/EditController.cs"
  - "unity/CXRBrownfield/Assets/Scripts/TileBuildingEditor.cs"
  - "unity/CXRBrownfield/Assets/Scripts/ModelRequesterUI.cs"
  - "unity/CXRBrownfield/Assets/Scripts/UITheme.cs"
  - "unity/CXRBrownfield/Assets/Scripts/UITips.cs"
---

# Runtime UI conventions (IMGUI)

The tool panels are plain IMGUI (`OnGUI` / `GUILayout`) skinned by `UITheme.cs`; there is no Canvas
or prefab UI. When adding or changing a control:

- **Every clickable control goes through a `UITheme` helper and carries a tooltip** from `UITips.cs`
  (one `const string` per control, grouped by panel): `UITheme.Button`, `ToggleButton`, `Checkbox`,
  `ListItem` (rows inside a scrolled list), `PrimaryButton` / `SecondaryButton` / `GhostButton` /
  `DangerButton`, `Chip`, `ListRowLabel`, `Foldout`, `ThumbCell`, `StateRow`,
  `Segmented(sel, labels, tips)`, `CommandBar`, `RowToggle` (quiet on/off inside list rows),
  `DragButton` (press-and-drag value nudge). Never call `GUILayout.Button` / `GUILayout.Toggle`
  directly in a panel — a grep for those in the five panel files (`UIShell`, `LibraryBrowser`,
  `EditController`, `TileBuildingEditor`, `ModelRequesterUI`) must stay empty.
- **Tooltip plumbing:** each panel's `OnGUI` calls `UITheme.CaptureTooltip()` right before its
  `GUILayout.EndArea()` (IMGUI only exposes `GUI.tooltip` to the OnGUI that drew the control).
  `UIShell.OnGUI` sets `GUI.depth = -10` so it runs last and draws the single hover card via
  `UITheme.DrawTooltipOverlay()` (0.4 s delay, dark card, clamped to the screen, works on disabled
  controls). A new panel needs the `CaptureTooltip()` line; nothing else.
- **Tooltip copy** (house style): say what the click does plus anything you'd otherwise learn by
  trying it (a key that does the same, why the button is off, what gets kept). One to three short
  sentences of varied length, plain words, no em dashes, colons only when unavoidable, no bold, no
  filler or sign-off sentence, no "not X but Y" phrasing.
- **Scroll views:** use the no-horizontal-bar overload
  `GUILayout.BeginScrollView(s, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, …)` and draw
  rows with `ListItem` / `ListRowLabel` / `Note` (they shrink and clip) so long ids and filenames
  never summon a horizontal scrollbar.
- **Labels:** words, not glyphs (`Refresh`, `Delete`, `Archive`), except the conventional `★`
  favorite, `−` / `+` steppers, `×` for a row's inline delete, and `▲` / `▼` up/down steppers
  (floor selector) — all of which rely on their tooltips.
- Keep new styles on the `UITheme` button fill / edge / hover tokens (`Btn`, `BtnHover`, `BtnLine`,
  `TintHover`, `AccentInk` hover); they were chosen so buttons separate from the paper panel.
