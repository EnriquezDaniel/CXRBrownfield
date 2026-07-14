# CXR Visualization Tool — Developer Notes

## Running the server

```bash
cd server
python server.py
```

Server starts on **port 5002**. Dependencies: `pip install flask flask-cors`  
Gemini layout generation requires `layout_prompt.py` and a Gemini API key (see `CXR_Build_Plan.md`).

Data lives in `server/data/` — never modify by hand; use the CRUD endpoints. Records are split
by *kind* into subfolders (the file is still `<uuid>.json`; the kind is also stored as a tag):
```
server/data/environments/user/<id>.json        # user-authored
server/data/environments/generated/<id>.json    # from layout generation
server/data/buildings/static/<id>.json           # user-authored
server/data/buildings/cached/<id>.json            # from layout generation
server/data/_archive/...
```
POST `/api/environments` and `/api/buildings` accept a `"kind"` body field (defaults: `user` /
`static`); GET/PUT/archive resolve an id across subfolders; list endpoints accept `?kind=` and
return a `kind` per row. POST also makes names unique (`name`, `name (2)`, …).

Uploaded sketches and raw generated layouts live at the repo root:
```
input/<image>           # source images, via POST /api/inputs (GET lists, GET /<name> serves)
layouts/<image-stem>.json   # raw LLM layout JSON, one per source image
```
`POST /api/layout/generate` takes `{ "image": "<name>" }` (an uploaded input); `"image"` is
**required** — a missing name returns 400 (the old server-side file-dialog fallback hung headless
servers and was removed; `layout_prompt.py`'s CLI still has its dialog). Optional site fields:
`"site"` (preset name from GET /api/sites), or explicit `lot_boundary`/`site_width_ft`/`site_height_ft`.
The response now includes a `"warnings"` list — the server sanity-checks the LLM output (7 top-level
keys, usable `lot_boundary` — patched back from the request's boundary if the model drops it, fatal
502 only in auto mode — and center-point-inside-lot containment) but stays warn-first.

Quick smoke-test without Gemini:
```bash
curl -X POST http://localhost:5002/api/environments \
  -H "Content-Type: application/json" \
  -d '{"name":"Test","version":1,"tags":[],"site":{"terrainSize":[100,100],"terrainZones":[],"paths":[],"scaleNote":""},"buildingInstances":[],"objectInstances":[]}'
curl http://localhost:5002/api/environments
```

## Unity scene wiring

Every `// USER WIRES THIS IN INSPECTOR:` comment marks a `[SerializeField]` that must be assigned by hand.

| GameObject | Component | Required assignments |
|---|---|---|
| `WorldRenderer` | `WorldRenderer` | `Terrain`, `PrefabRegistry`, `TerrainRegistry`, `TileShapePalette`, `MaterialPalette` (tile-based building rendering), `PathMaterialPalette` (path ribbons), `FencePalette` (fence runs), `BuildingGenerator` (massing fallback when a building has no tiles) |
| `LibraryBrowser` | `LibraryBrowser` | `LibraryClient`, `WorldRenderer` |
| `LibraryClient` | `LibraryClient` | set `serverBaseUrl = http://localhost:5002` |
| `EditController` | `EditController` | `LibraryBrowser`, `LibraryClient`, `WorldRenderer`, `TileBuildingEditor`, `PrefabRegistry`, `PathMaterialPalette` (path tool), `FencePalette` (fence tool — borrowed from `WorldRenderer` if unset), `TerrainRegistry` (ground-surface tool); optional: camera |
| `TileBuildingEditor` | `TileBuildingEditor` | `TileShapePalette`, `MaterialPalette`, main camera; `PrefabRegistry` + `DecorPalette` (Decorate tool) |
| `ModelRequesterUI` | `ModelRequesterUI` | `ModelRequester`, `WorldGenerator` (legacy), `WorldRenderer`, `LibraryClient`, `LibraryBrowser`; layout-source buttons: `uploadImageButton`, `refreshInputsButton`, `inputDropdown`, `generateFromImageButton`, `testLocalSampleButton`, `testServerSampleButton` |
| `BakePass` | `BakePass` | `WorldRenderer` |

## ScriptableObject assets to create

- **PrefabRegistry** (`Assets → Create → Prefab Registry`): keys must match `prefab_type` in Gemini JSON. Use `PrefabRegistryExporter` to sync keys to the Gemini prompt.
- **TerrainRegistry** (`Assets → Create → Terrain → TerrainRegistry`): keys match `terrain_type` values (`"grass"`, `"concrete"`, …).
- **TileShapePalette** (`Assets → Create → CXR → TileShapePalette`): one entry per tile shape (`"square"`, `"wedge"`, `"quarter_curve"`). Each needs a **prefab with colliders** and a `faceNames` list matching submesh order, e.g. `["north","east","south","west","top","bottom"]`.
- **MaterialPalette** (`Assets → Create → CXR → MaterialPalette`): one entry per material ID used in `faceMaterials` (e.g. `"brick_red"`, `"glass"`, `"roof_tar"`).
- **PathMaterialPalette** (`Assets → Create → CXR → PathMaterialPalette`): one entry per path surface ID used by the path tool / generation `path_material` (canonical set: `"pavement_dark"`, `"pavement_light"`, `"brick"`, `"dirt"`, `"asphalt"`). For the ground-surface brush, add matching `TerrainLayer`s to **TerrainRegistry** (`"grass"`, `"concrete"`, `"sand"`, …).
- **FencePalette** (`Assets → Create → CXR → FencePalette`): one entry per fence type used by the fence tool / generation `fence_type` (canonical set: `"picket"`, `"lattice"`, `"chain_link"`, `"wood_privacy"`, `"wrought_iron"`). Each entry holds a `panelPrefab` (one fence panel **modeled along +X with its base at y=0** — the renderer repeats and stretches it), an optional `postPrefab` (placed at each joint/corner), a `panelLength` (m — the centerline resample spacing), a default `height` (m, used when `FenceDef.height` ≤ 0), and `scalePanelToFit` (stretch each panel along its run axis to span its gap). The existing `Fence_0` / `FanceConnector` park prefabs can seed a first type; new types need their own panel prefabs. A fence with an unknown type logs a warning and is skipped (same as a missing path material).
- **DecorPalette** (`Assets → Create → CXR → DecorPalette`): decor presets for the tile editor's **Decorate** tool — the prop analogue of **MaterialPalette**. One entry per decor (e.g. `"door"`, `"window"`, `"vent"`), each with a single prefab `prefabKey` (must exist in **PrefabRegistry**), a `surface` filter (`Wall`/`Roof`/`Any`), a `widthFraction`/`heightFraction` (how much of the cell face the prop spans, aspect preserved), an `anchor` (`Center`/`Bottom`/`Top` — e.g. doors anchor `Bottom`), optional `mountAxis`/`flipMount` auto-align overrides, and a `surfaceOffset` (z-fight epsilon). Placement is systematic: pick a decor, click a tile face → one prop auto-centers, fits, and seats flush; re-painting a face **replaces** its prop (one decor per face). Painted decorations persist as `BuildingDef.embeddedObjects` (with `hostGridX/Z/Floor`, `hostFace`, `fillsFace`) and render via `WorldRenderer.RenderEmbeddedObjects`.

## Edit mode controls

| Key / Action | Effect |
|---|---|
| Right-click drag | Orbit camera |
| Middle-click drag | Pan camera |
| Scroll wheel | Zoom (or rotate placement ghost) |
| WASD | Pan camera pivot |
| Left-click | Select object/building instance |
| Shift/Ctrl + click | Add/remove an instance to a multi-selection (Browse or Transform); move/rotate/scale then apply to all selected, each about its own pivot |
| Tab | Cycle overlapping hits at last click (repeated clicks also cycle) |
| Double-click building | Enter tile edit mode |
| G | Grab/move selected |
| R | Rotate selected |
| Shift (held during any rotate) | Snap rotation to 15° increments — R-drag, gizmo ring, and panel Y slider are free-rotating otherwise |
| T | Scale selected |
| Drag gizmo handles | Arrows = move on X/Z, center pad = free move, ring = rotate, top cube = scale (works without G/R/T) |
| Right-panel sliders | Set rotation (0–360°) and scale (0.1–5) of the selected instance directly |
| Right-panel **Pivot** toggle (Rotate tool, multi-selection) | Switch yaw rotation between **each object** (every instance spins in place about its own pivot — the default) and **selection center** (yaw orbits every instance's position around the group's XZ centroid, so the arrangement turns rigidly as one — same math as whole-env rotate). Applies to the R-drag, gizmo ring, and Y slider; X/Z rotation stays per-instance |
| Right-panel **Skew shape** (selected building) | Whole-building deform of the selected building's `BuildingDef` (acute/obtuse footprint corner via **Bend corner**, or a **Slope edge** shed roof) — a foldout under the Move/Rotate/Scale controls. **Apply** stacks onto the current shape, **Reset** clears all deform; both re-render, persist the def (PutBuilding), and are undoable. Writes `TileDeform` via the shared `TileDeformField` (same field AI generation uses), so it round-trips like a tile edit. (Moved here from the tile editor's old Skew sub-tool.) |
| Arrow keys | Nudge (Transform mode) |
| Q / E | Tile edit, **Add** tool: rotate hovered/placement yaw −90° / +90°. **Select** tool: rotate selected tile ±15° on the active axis |
| X / Y / Z | Tile edit, **Select** tool: choose the active rotate axis |
| Shift/Ctrl + click | Tile edit, **Select** tool: toggle a tile in/out of a multi-selection (or use the "Select floor / Select all" buttons) |
| Left-click + drag | Tile edit: **Add** paints tiles, **Select** keeps adding hovered tiles to the selection, **Paint** paints each face dragged over, **Decorate** places one decor per face dragged over |
| Tile edit, **Decorate** tool | Place decorative props (doors/windows/vents) onto tile faces from a `DecorPalette` decor — the prop analogue of the **Paint** tool. Pick a decor, then click (or drag across) tile faces: one prop per face auto-centers, fits to the decor's `widthFraction`×`heightFraction` of the cell (aspect preserved), and seats flush at its `anchor` (`Bottom` for doors, `Center` for windows). Re-painting a face **replaces** its prop (one decor per face); **Erase** drags to remove painted props. The decor's `surface` filter keeps walls and roofs to the right prop types. Saved as `BuildingDef.embeddedObjects`. |
| Tile edit, **Whole face** (Paint & Decorate) | Toggle on either tool to act on the **entire building side** in one click instead of a single tile. The clicked face's building-local axis is resolved, then every exposed (un-occluded) tile face pointing that way across all floors is processed at once: **Paint** assigns the active material to the whole side; **Decorate** places the active decor on each exposed face (e.g. windows across a whole wall). |
| Ctrl+C / Ctrl+V | Copy the selected instance(s) / paste them at the mouse cursor (group centroid anchored at the cursor's ground point; camera pivot when over UI). Clipboard is cross-environment data copies — copy from a locked twin works, pasting into a locked env refuses; pasted buildings share the original `BuildingDef`. Pasted set becomes the new selection; one undo step |
| Delete | Remove selected instance or tile |
| Escape | Cancel / deselect / exit mode |
| Enter | Confirm transform (or finish a polyline path in **Draw paths**) |

### Site / lot sizing (right panel → "Site")

The terrain is sized to the input lot and is freely re-shapeable. The **rectangle** is the terrain
canvas (`site.terrainSize`, meters — drives the in-scene `Terrain` via `WorldRenderer.ApplyTerrainSize`);
the **parcel polygon** (`site.lotBoundary`) masks everything outside it to `outsideTerrainType` ("water").
Both round-trip through the server and are emitted by layout generation — `LayoutConverter` traces the
parcel from the sketch and sizes `terrainSize` to the parcel's bounding box (+ margin) so the ground
hugs the lot instead of the full canvas. The shared geometry helpers live in `EnvironmentScale`
(`EffectiveLotPolygon`, `PointInPolygon`, `ContentBounds`, `ScaleEnvironmentXZ`, `ClampInsidePolygon`).

| Control | Use |
|---|---|
| **Size (m) fields + Apply** | Type width × length and Apply. Honors the **On resize** selector. |
| **On resize: Keep in place / Scale content** | Keep-in-place only resizes the ground; Scale-content rescales the whole layout per-axis to fill the new lot (`EnvironmentScale.ScaleEnvironmentXZ`). |
| **Edit lot (handles)** | In-scene `EditMode.EditLot` tool. **Rectangle** sub-mode: drag the far corner / far-edge handles to resize from the origin corner. **Parcel** sub-mode: drag polygon vertices, click an edge to insert a vertex, Delete removes the selected one (min 3); **Reset parcel to rectangle** drops the boundary. A draped amber "Lot frame" line shows the parcel; a live preview tracks the drag. |
| **Fit terrain to lot** / **Fit lot to content** | Snap `terrainSize` to the parcel's extent, or grow it to enclose all placed content (+ margin). |
| **Clamp items to lot** | Appears when instances fall outside the parcel; projects each back just inside (`ClampInsidePolygon`). |

### Terrain editor (right panel → "Terrain editor")

| Tool | Use |
|---|---|
| **Draw paths** | Pick a path material + width. **Straight** mode: click to drop waypoints, Enter or double-click to finish. **Freehand** mode: hold left-mouse and drag. Paths render as textured mesh ribbons (`PathDef` in `site.paths`); existing paths are listed with a ✕ to delete. |
| **Draw fences** | The fence analogue of **Draw paths** (same straight/freehand drawing, snap-to-ends, edit/✕-delete list). Pick a `fence_type` from the **FencePalette** and a height; the drawn centerline (`FenceDef` in `site.fences`) is repeated as panel prefabs end-to-end with posts at the joints, draped onto the terrain. The live preview shows the centerline as an amber line; the panels appear on finish. **Edit** drags control-point handles (click the line to insert, Delete to remove). |
| **Paint objects** | Pick a prefab (trees/greenery), radius, and density; hold left-mouse and drag to scatter with random rotation/scale. Toggle **Erase** to remove painted instances within the radius. Writes `ObjectInstance`s. |
| **Paint ground** | Pick a terrain type + radius; hold left-mouse and drag to paint that surface into the splatmap (stored as `site.surfaceStrokes`, layered over the rectangular `terrainZones`). |

Paths, fences, scattered objects, and surface strokes all live inside the environment JSON, so they
**Save/Load** and round-trip through the server exactly like everything else. Layout generation can
also emit paths and fences (`paths`/`fences` in the LLM output → `PathDef`/`FenceDef`), so generated
and hand-drawn terrain are interchangeable.

## Workflow: generate → edit → save

1. `python server.py`
2. Play mode → **Generate Layout** → select sketch → layout converts and saves to library
3. **LibraryBrowser** (left panel) → click **Load** on an environment
4. **EditController** (right panel) → place objects, transform, double-click buildings to tile-edit
5. **LibraryBrowser** → **Save** to persist
6. **BakePass** panel (bottom-center) → **Bake** to combine meshes for VR / lightweight play

### Multiple environments

**Load** adds an environment to the scene without unloading the others — several render at once,
overlaid at their shared origin (positions are world coordinates, so they can overlap). Only **one
environment is active (editable/saveable) at a time**; the rest are locked backdrops (colliders
disabled, dimmed). The **Loaded (n)** list shows every loaded env — **Edit** makes one active,
**Close** unloads it from the scene (without deleting it on the server). Selection, placement,
transform, tile-edit, **Save**, **Save As**, and **Bake** all operate on the active environment
only; the active env is the one that paints the shared terrain.

`WorldRenderer` keeps one root + instance map per env id (`_envRenders`) and a single `_activeEnvId`;
`LibraryBrowser` keeps a `LoadedEnv` list with one `_active`. `EditController` is unchanged in spirit —
it edits `LibraryBrowser.CurrentEnvironment`, which now resolves to the active env.

### Locked environments (digital twin)

Each environment has a persistent **`locked`** flag (`EnvironmentDef.locked`, round-trips through the
server JSON; shown in list rows via `EnvironmentSummary.locked`). Lock the digital-twin env from its
Loaded-list row (**Lock** is one click; **Unlock…** asks to confirm; persists immediately via PUT).
A locked env **can still be made active** — it owns and paints the shared terrain, exactly what a twin
backdrop needs — but it is **read-only**: all edit rails show a locked notice; G/R/T, gizmo drags,
Delete, double-click tile-edit, calibration, include-toggles, undo/redo, dirty-marking, Live-Share
auto-save, **Save**, Archive, and Delete all refuse. **Save As** / **Duplicate** produce an *unlocked*
copy — the sanctioned "design on top of the twin" flow. Known limitation: `BuildingDef`s are global,
so a def shared with the twin can still be edited from another env or the Buildings tab.

## VR walkthrough + live multi-client sync

A VR headset (or a second PC) can mirror the desktop host's **full loaded set** — the active env plus
any backdrop envs — in real time. The desktop host is the **administrator** (only it can add/delete/edit;
viewers are strictly read-only). The model is **publish → poll → re-render**: the host publishes its
loaded set; viewers poll and rebuild idempotently via `WorldRenderer.RenderEnvironment` (no deltas,
no conflict resolution).

**Server** — one shared pointer file `server/data/active.json` (`{ envId, loadedIds }`), exposed by:
- `GET /api/active` → `{ envId, version, name, updatedAt, loaded: [{envId, version, name, updatedAt}] }`.
  `loaded` is every published env; each `version` is read **live** from its env record, so a poller
  detects loads/closes (id list changes), an active switch (`envId` changes), and edits (a version
  bumps after the host PUTs). Archived/deleted ids are silently dropped (self-healing).
- `POST /api/active` `{ "envId": "<active id>", "loadedIds": [...] }` → publish. `loadedIds` optional
  (defaults to just the active env — old single-env contract); both null/empty clears the pointer.

**Desktop host (`LibraryBrowser`)** — a **Live** toggle in the Library header. When on, `PublishLive`
posts the loaded set (persisted envs only; never-saved envs are skipped until first Save) on every
activate/load/close/save, and every `MarkDirty` schedules a **debounced auto-save** (`AutoSaveDebounce`,
~1 s) so a PUT bumps the version and viewers pick it up. Off = unchanged manual-save behavior. The
single-active model is preserved — only the active env is editable; backdrops are shared as-is.

**Viewer (`SyncClient`)** — polls `GET /api/active` every `pollIntervalSeconds` (~1.5 s) and diffs
the published set against what it rendered (`_appliedVersions`): envs that dropped out are unloaded
(`UnloadEnvironment`), new/version-bumped envs are fetched + re-rendered (an env's building defs are
evicted from the cache first, so edited buildings refresh), and the host's active env is applied via
`SetActiveEnvironment` (paints terrain, dims backdrops). Falls back to the old single-env contract if
the server payload has no `loaded` list. Strictly fetch-and-render: no editing/undo.

**Scene `Assets/Scenes/VRViewer.unity`** — a duplicate of `BasicModel` (preserves all `WorldRenderer`
registry wiring) with the editing components disabled (`LibraryBrowser`, `TileBuildingEditor`, `BakePass`,
`ModelRequester`, `Canvas`). `GameManager` keeps `LibraryClient` + `WorldRenderer` + a new **`SyncClient`**
(wired to both). An **XR Origin (VR)** rig (OpenXR + XR Interaction Toolkit) is the camera; the legacy
Main Camera is disabled. Point `LibraryClient.serverBaseUrl` at the host PC's LAN IP (not localhost) on a
headset. Verified in flat play mode: the viewer renders the published env and re-syncs on switch within
the poll interval.

**VR is opt-in and scene-driven — the desktop build is never VR.** XR Plug-in Management has
**"Initialize XR on Startup" OFF** for every target, so nothing starts XR automatically. Only the
`VRViewer` scene carries **`XRBootstrap`** (`Assets/Scripts/XRBootstrap.cs`), which manually inits the
XR loader on `Start` and stops it on teardown; if no headset/loader is present it logs a warning and
runs flat. So `BasicModel` (and any other scene) is a normal PC app, and VR activates only when you run
`VRViewer`. Works for both PCVR (Windows) and Quest (Android), whichever OpenXR loader is assigned.

**Two builds via the `Build` menu** (`Assets/Editor/BuildMenu.cs`) — each bakes in its own scene + target,
so the builds stay separate regardless of the shared Build Settings list (which stays **BasicModel-only =
the default PC build**):
- **Build → Desktop (PC, Windows)** (`Ctrl+Shift+D`) — the PC app as before, ships only `BasicModel`, no VR.
- **Build → VR — Quest (Android)** (`Ctrl+Shift+Q`) — ships only `VRViewer`; OpenXR is already enabled for
  the Android target.
- **Build → VR — PCVR (Windows)** — ships only `VRViewer`; **requires** enabling OpenXR for the *Standalone*
  target in XR Plug-in Management first (the Android target already has it).

**Remaining headset setup (in-editor, needs the device):** for PCVR, enable **OpenXR** for the Standalone
target + add an interaction profile (Oculus Touch). On the headset, set `LibraryClient.serverBaseUrl` to the
host PC's LAN IP (not localhost). Packages installed: `com.unity.xr.openxr`,
`com.unity.xr.interaction.toolkit`, `com.unity.xr.management`.
