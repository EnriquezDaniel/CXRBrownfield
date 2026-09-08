# CXR Visualization Tool — developer notes

Sketch → Flask server (`:5002`) → LLM layout JSON → Unity tile-building scene you edit, save, bake,
and walk through in VR. Detail lives in `docs/`; path-scoped rules in `.claude/rules/`.

```
layout_prompt.py, site_presets.py, prompts/   LLM layout generation (repo root, imported by the server)
input/  layouts/                              uploaded sketches · raw LLM layout JSON per sketch
server/server.py  server/data/                Flask API (the only server file) · JSON record store
unity/CXRBrownfield/                          Unity 6000.3.10f1; scene Assets/Scenes/BasicModel.unity
docs/                                         reference docs (index at the bottom)
```

## Run

```bash
pip install -r requirements.txt        # repo root
# .env at repo root: ANTHROPIC_API_KEY=...   (Claude default; Gemini = GOOGLE_API_KEY + CURRENT_PROVIDER in layout_prompt.py)
python server/server.py                # port 5002; M2U_DEBUG=1 for Flask debug
curl http://localhost:5002/health && curl http://localhost:5002/api/environments
```

Unity: open `unity/CXRBrownfield`, scene `BasicModel`, Play. `LibraryClient.serverBaseUrl` =
`http://localhost:5002`. Wiring for every `// USER WIRES THIS IN INSPECTOR:` field:
`docs/unity-scene-wiring.md`.

## Verify changes

- Unit tests: `Assets/Tests/EditMode` (asmdef `EditModeTests`) cover the pure-logic assembly
  `Assets/Scripts/Authoring/` (`CXRAuthoring`). Run via Test Runner → EditMode or MCP `tests-run`.
  New pure logic (math, geometry, data transforms) goes in `Authoring/` so it stays testable.
- Editor / MCP locked (an open editor blocks batchmode): compile-check with Unity's bundled Roslyn —
  recipe in `docs/verifying-unity-changes.md`.
- MCP-driven play mode never ticks `Update`, so gestures/hotkeys can't be exercised from here:
  verify in two layers (reflect over the type, then test the logic with an edit-mode rig) and hand
  the feel check to the user. `assets-refresh` times out on a domain reload — confirm via
  `Library/ScriptAssemblies/*.dll` mtimes, don't retry.

## Hard rules

- **`server/data/` is never edited by hand** — go through the CRUD endpoints (`.claude/rules/server-data.md`).
- **The 8 guarded assets in `Assets/Resources/`** (`PrefabRegistry`, `TerrainRegistry`,
  `TileShapePalette`, `MaterialPalette`, `PathMaterialPalette`, `FencePalette`, `DecorPalette`,
  `WaterPalette`):
  append-only by index, never `Write`/`Edit` the `.asset` while Unity is open, Snapshot before /
  Validate after, never drop a field. Full protocol + recovery: `.claude/rules/palette-assets.md`.
- Only `Assets/Resources/*.asset` is out of Git LFS; terrains, lightmaps, font atlases stay in.
- **IMGUI panels**: every control via a `UITheme` helper with a `UITips` tooltip; no raw
  `GUILayout.Button`/`Toggle` in the five panel files (`.claude/rules/imgui-ui.md`).
- **Tile prefabs are generated**, not hand-authored: edit the source in `Assets/Prefabs/Tiles/`,
  then `Tools → CXR → Tiles → Rebuild per-face tile prefabs` (`docs/unity-scene-wiring.md`).
- Never `AssetDatabase.FindAssets("t:MaterialPalette")` — it also hits ProBuilder's
  `Material Palette.asset`; resolve by path via `PaletteGuard.Paths`.
- UI/tooltip copy style: plain words, no em dashes, no filler, no "not X but Y".

## Code map (`unity/CXRBrownfield/Assets`)

| Where | What |
|---|---|
| `Scripts/WorldRenderer.cs` | Renders envs (terrain, paths, fences, tile buildings, props); one root per env (`_envRenders`), one `_activeEnvId` |
| `Scripts/LibraryBrowser.cs` | Left panel: library, Load/Edit/Close, Save/Save As, `locked` twins, **Live** publish (`PublishLive`, debounced auto-save), `optional` marks + **Low detail** preview (what the VR viewer skips) |
| `Scripts/EditController.cs` | Right rail: select/box-select, G/R/T + gizmo, copy/paste, site/lot tools, paths/fences/paint-objects/paint-ground, undo (`EditHistory`) |
| `Scripts/EditController.Water.cs` | Draw water / Edit water (partial class of `EditController`): ponds and rivers with a derived bed |
| `Scripts/TileBuildingEditor.cs` | Double-click a building: Add/Select/Paint/Decorate tools, whole-face mode |
| `Scripts/WalkthroughController.cs` | **Walk** (command bar): click-to-place first-person walkthrough, WASD + mouse, Shift run, Esc back; freezes editing via `IsEngaged` |
| `Scripts/UIShell.cs`, `UITheme.cs`, `UITips.cs` | IMGUI shell, theme helpers, tooltip strings |
| `Scripts/SyncClient.cs`, `XRBootstrap.cs`, `Scenes/VRViewer.unity` | Read-only viewer that polls `/api/active`; VR is opt-in per scene |
| `Scripts/Authoring/` (`CXRAuthoring`) | Pure logic: `LayoutConverter`, `EnvironmentScale`, `TileFaceSplitter`, `TileFaceGeometry`, `TileFit` (sub-cell shapes: pillar, slab), `FenceBuilder`, `PathMesh`, `BrushGeometry`, `DecorPlacement`, `TileDeformField`, `WalkKinematics`, `SiteFit` (fit a generated env into a drawn site), `PolygonEdit`, `WaterGeometry` / `WaterCarve` / `PolygonTriangulator` (water meshes and the derived bed), `OptionalContent` (items the VR viewer skips: render predicate + bulk marks), data types |
| `Editor/` | `PaletteGuard` (palette safety net), `PaletteValidator` (`Tools → CXR → Palettes → …`), `WaterPaletteSetup` (`Tools → CXR → Palettes → Create Water Palette (seeded)`), `TileFaceSetup` (per-face tile rebuild), `BuildMenu` (`Build → Desktop / VR Quest / VR PCVR`) |
| `Resources/` | The eight guarded ScriptableObject assets |
| `Tests/EditMode/` | NUnit tests for `Authoring/` |

## Docs

| File | Contents |
|---|---|
| `docs/server-api.md` | Run/smoke-test, data layout by kind, endpoint contracts (`layout/generate`, `active`) |
| `docs/unity-scene-wiring.md` | Inspector wiring table, the eight ScriptableObject assets and their entry fields, per-face tile prefab procedure |
| `docs/editing-controls.md` | Generate → edit → save workflow, full edit-mode and tile-edit key tables, site/lot sizing, terrain editor tools, multiple + locked environments |
| `docs/vr-live-sync.md` | `/api/active` contract, host Live toggle, `SyncClient`, `VRViewer` scene, XR opt-in, Build menu |
| `docs/verifying-unity-changes.md` | Running EditMode tests, MCP gotchas, offline csc compile recipe |
