# Editing: controls, tools, and workflow

Everything here runs in Play mode in `BasicModel`. Panels are IMGUI: `LibraryBrowser` (left),
`EditController` / `TileBuildingEditor` (right rail), `ModelRequesterUI`, `BakePass` (bottom-center).

## Workflow: generate → edit → save

1. `python server/server.py`
2. Play mode → **Generate Layout** → select sketch → layout converts and saves to the library.
3. **LibraryBrowser** → **Load** an environment.
4. **EditController** → place objects, transform, double-click a building to tile-edit.
   **Buildings tab → New** seeds the def with a 3×3 floor-0 block of `square` tiles centered on the
   origin cell (`gridX/gridZ ∈ {-1,0,1}`, `LibraryBrowser.BlankBuilding`) and opens the tile editor
   in the **Add** tool, anchored at the view center on the ground and snapped to the cell grid, so
   the new building is visible at once and lands where it was edited on Esc. An editor opened on a
   building with zero tiles also starts in Add.
5. **LibraryBrowser → Save** to persist.
6. **BakePass → Bake** to combine meshes for VR / lightweight play.

No server or API key handy? **Generate rail → Samples → Local sample** loads
`Assets/Resources/DummyLayout.json`, a hand-authored reading of the Home Longfellow sketch
(`samples/HomeLongfellowSample1.jpg`) on the real 286 × 133 ft parcel. It arrives as an editable,
unsaved environment in the Loaded list, so steps 3 to 6 work on it like any other. It honours the
same **Sites** target Generate uses: pick a site and the sample is projected into that plot
(`SiteFit.ProjectIntoSite`) instead of loading at the origin.

`Assets/Tests/EditMode/DummyLayoutSampleTests.cs` pins the sample's geometry, its orientation, and
its fit into a site, and keeps it a valid reference for what a layout response should look like.

### Where the terrain sits

`site.terrainSize` is the ground's extent and `site.terrainOrigin` its min corner in world meters
(null ⇒ the world origin, which is where every environment authored before that field sits). Both are
applied by `WorldRenderer.ApplyTerrainSize` whenever an environment becomes active, and both travel
with the environment through `EnvironmentScale` — the size scales directly while the origin, being a
position, scales about the pivot and shifts on a translate.

`SiteFit.ProjectIntoSite` seeds `terrainOrigin` with the site's corner, so an environment fitted into
a host's drawn site brings its ground with it. Only one environment owns the terrain at a time, so
while such an environment is active the ground sits over its site and the host is a backdrop without
ground under it; make the host active again and the terrain returns to it.

The terrain's Y is not a site field: `WorldRenderer.EnsureHeightSetup` always parks the `Terrain` at
y = -15 with a 30 m height range, so the flat base plane (normalized 0.5) sits at world y = 0 and the
**Shape ground** brush can dig as far as it can build (see "Shape ground details" below).

## Edit-mode controls

| Key / action | Effect |
|---|---|
| Right-drag / middle-drag / scroll / WASD | Orbit / pan / zoom (or rotate placement ghost) / pan pivot |
| Left-click | Select object or building instance. Clicking a **fence** jumps into that fence's editor (see below) |
| Left-drag (Browse) | **Box-select** objects (see below) |
| Shift/Ctrl + click | Add/remove an instance in a multi-selection (Browse or Transform); move/rotate/scale then apply to all selected, each about its own pivot. Shift/Ctrl+click also ignores fences so you can reach an object behind one |
| Tab | Cycle overlapping hits at the last click (repeated clicks also cycle) |
| Double-click building | Enter tile edit mode |
| G / R / T | Grab / rotate / scale selected |
| Shift (during any rotate) | Snap rotation to 15° (R-drag, gizmo ring, panel Y slider are free otherwise) |
| Gizmo handles | Arrows = move on X/Z, center pad = free move, ring = rotate, top cube = scale (works without G/R/T) |
| Right-panel sliders | Rotation (0–360°) and scale (0.1–5) of the selected instance |
| Right-panel **Pivot** (Rotate tool, multi-selection) | **Each object** (default; every instance spins about its own pivot) or **selection center** (yaw orbits positions around the group's XZ centroid — same math as whole-env rotate). Applies to R-drag, gizmo ring, Y slider; X/Z rotation stays per-instance |
| Right-panel **Skew shape** (selected building) | Whole-building deform of the `BuildingDef`: **Bend corner** (acute/obtuse footprint corner) or **Slope edge** (shed roof). **Apply** stacks onto the current shape, **Reset** clears all deform; both re-render, `PutBuilding`, and are undoable. Writes `TileDeform` via the shared `TileDeformField` (same field AI generation uses), so it round-trips like a tile edit |
| Arrow keys | Nudge (Transform mode) |
| Ctrl+C / Ctrl+V | Copy selected instance(s) / paste at the cursor (see below) |
| Delete / Backspace | Remove selected instance or tile. While editing a fence: removes the selected control dot, or with no dot selected (the state right after clicking a fence) **deletes the whole fence**; a 2-point fence always deletes. Undoable |
| Escape | Cancel / deselect / exit mode |
| Enter | Confirm transform (or finish a polyline in **Draw paths**) |
| `[` / `]` | Shrink / grow the active brush by 10% (**Paint objects**, **Paint ground**, **Shape ground**) |
| **Walk** (command bar) | Enter the first-person walkthrough (see below) |

## Walkthrough (first person)

**Walk** sits at the right end of the top command bar (`UIShell`; logic in
`WalkthroughController`, math in `Authoring/WalkKinematics`). It is a view, not an edit mode: the
current mode, selection, and an open tile editor are frozen while you walk and resume untouched.

| Key / action | Effect |
|---|---|
| **Walk** button | Placing phase: an amber person marker follows the cursor over the ground (terrain, paths, roofs; walls are skipped), facing the orbit camera's yaw. The panels hide and the bar shows a hint strip |
| Left-click (placing) | Start walking there, at 1.7 m eye height. Cursor locks and hides |
| Esc (placing) | Cancel, nothing changes |
| W / A / S / D | Move relative to where you look, 1.4 m/s (an adult walk) |
| Shift | Run, 3.0 m/s (a jog). Both speeds are Inspector fields on `WalkthroughController` |
| Mouse | Look. Pitch clamps at ±85° |
| Left-click (walking) | Re-locks the cursor if the OS or editor released it |
| Esc (walking), or the app losing focus | Back to the editor view. The orbit pivot moves to where you stopped (ground height) and the orbit yaw adopts your heading; pitch and zoom are kept |

Notes:

- The walker is a `CharacterController` capsule (1.8 m × 0.3 m, step offset 0.4 m, slope limit
  50°, gravity) on the **Ignore Raycast** layer, so it collides with terrain, tile buildings, paths,
  and props but is never picked by the editor's raycasts. Slipping through a collider gap snaps it
  back onto the terrain (`WorldRenderer.SampleTerrainSurfaceY`).
- **Backdrop environments become solid** while walking (`WorldRenderer.SetBackdropCollidersSolid`),
  so you can walk through a locked twin; their colliders go back to off on exit. The dim tint stays.
- Nothing is written to the environment or undo history. The scale grid overlay keeps drawing.

Details:

- **Box-select** — drag a rubber-band rectangle; on release every **object** instance whose
  on-screen footprint overlaps the box becomes the selection. Plain drag replaces, Shift/Ctrl+drag
  adds. Buildings, fences, and paths are never picked up. Starts only after ~5 px of travel, so a
  plain click still selects / cycles / double-clicks; a press on a gizmo handle drags the gizmo
  (hovering its pick radius still lets a box start). Not available in Transform mode (LMB drag is
  the free-drag move/rotate/scale there) or on a locked twin.
- **Click a fence** — the rail switches to **Terrain**, the "Fences (n)" list opens with the row
  marked `(editing)`, and control-point handles appear. An object in front of the fence wins.
  Panel colliders are the bare art mesh (~59% of a stretched panel's face is solid picket), so a
  click that slips through a gap is caught by a screen-space fallback against the panel's projected
  face.
- **Copy/paste** — group centroid anchored at the cursor's ground point (camera pivot when over
  UI). Clipboard is cross-environment data copies: copy from a locked twin works, pasting into a
  locked env refuses; pasted buildings share the original `BuildingDef`. The pasted set becomes the
  selection; one undo step.

## Tile edit mode (double-click a building)

| Key / action | Effect |
|---|---|
| Q / E | **Add** tool: rotate hovered/placement yaw −90° / +90°. **Select** tool: rotate selected tile ±15° on the active axis |
| X / Y / Z | **Select** tool: choose the active rotate axis |
| Shift/Ctrl + click | **Select** tool: toggle a tile in/out of a multi-selection (or use "Select floor / Select all") |
| Left-click | **Add** places one tile in the hovered cell, on release |
| Left-drag | **Add** paints each cell dragged over once the cursor moves a few pixels, **Select** keeps adding hovered tiles, **Paint** paints each face dragged over, **Decorate** places the active decor on each face dragged over |
| **Paint** | Assign a `MaterialPalette` material to a tile face |
| **Decorate** | Place props (doors/windows/vents) from a `DecorPalette` decor onto tile faces — the prop analogue of Paint. Each prop auto-centers, fits to `widthFraction`×`heightFraction` of the cell (aspect preserved), seats flush at its `anchor`. Decors stack; only re-painting the same decor replaces it; a `replacesOtherDecor` decor clears other face-claiming decor instead. The panel summary shows `• stacks` / `• clears face`. **Erase** drags remove painted props **one per click** (the nearest), so a stack peels off a prop at a time. The decor's `surface` filter keeps walls and roofs to the right prop types. Saved as `BuildingDef.embeddedObjects` (field glossary in [unity-scene-wiring.md](unity-scene-wiring.md)) |
| **Optional** (Decorate) | Third Decorate mode next to Place / Erase. Click a prop to flip its `optional` flag (the VR viewer skips optional decor, see [Optional content](#optional-content-vr-detail-level)). Optional props show a translucent blue tint in the editor. One flip per click, no drag. Undo: "Mark decor optional" / "Mark decor required" |
| **Whole face** (Paint & Decorate) | Act on the **entire building side**: resolve the clicked face's building-local axis, then process every exposed (un-occluded) tile face pointing that way across all floors — Paint assigns the material to the whole side, Decorate places the decor on each exposed face, Optional flips every prop hosted on that side (any required → all optional; all optional → all required) |
| **Pillar / Slab** (Add) | Sub-cell shapes next to Square / Wedge / Quartercurve. **Pillar** is a 2×2 m post, 4 m tall, centered in its cell. **Slab** is a 4×4 m plate, 2 m thick, resting on the floor of its cell. Both claim the whole cell (one tile per cell), so a square placed on the floor above a slab leaves a 2 m gap. The placement ghost shows the true footprint; the hover quad still marks the whole cell. Select-tool tilt keeps the anchor: a slab tipped on its side still rests on the floor, a tipped pillar becomes a centered beam. Skew (Bend corner / Slope edge) warps them like any tile. Whole-face Paint / Decorate treat a face as hidden only when it reaches the shared boundary against a neighbour that fills its side, so a pillar's faces beside a square and a slab's top under a square stay paintable. Sizes come from `TileShapePalette` (`cellExtents` / `cellAnchor`, see [unity-scene-wiring.md](unity-scene-wiring.md)) |
| Delete / Backspace | Remove selected tile |

## Site / lot sizing (right panel → "Site")

The terrain is sized to the input lot and freely re-shapeable. The **rectangle** is the terrain canvas
(`site.terrainSize`, meters — drives the in-scene `Terrain` via `WorldRenderer.ApplyTerrainSize`);
the **parcel polygon** (`site.lotBoundary`) masks everything outside it to `outsideTerrainType`
("water"). Both round-trip through the server and are emitted by layout generation —
`LayoutConverter` traces the parcel from the sketch and sizes `terrainSize` to the parcel's bounding
box (+ margin). Geometry helpers: `EnvironmentScale` in `Assets/Scripts/Authoring/`
(`EffectiveLotPolygon`, `PointInPolygon`, `ContentBounds`, `ScaleEnvironmentXZ`, `ClampInsidePolygon`).

| Control | Use |
|---|---|
| **Size (m) fields + Apply** | Type width × length and Apply. Content stays at its world coordinates (fitting a layout into a region is the site-fill pipeline's job, `SiteFit`) |
| **Edit lot (handles)** | `EditMode.EditLot`. **Rectangle** sub-mode: drag the far corner / far-edge handles to resize from the origin corner. **Parcel** sub-mode: drag vertices, click an edge to insert one, Delete removes the selected one (min 3); **Reset parcel to rectangle** drops the boundary. A draped amber "Lot frame" line shows the parcel; a live preview tracks the drag |
| **Fit terrain to lot** / **Fit lot to content** | Snap `terrainSize` to the parcel's extent, or grow it to enclose all placed content (+ margin) |
| **Clamp items to lot** | Appears when instances fall outside the parcel; projects each back just inside (`ClampInsidePolygon`) |

## Sites (Generate tab → "Sites")

A **site** is a polygon drawn anywhere inside the active (host) environment that a sketch-generated
scene can fill. All site controls live in the **Generate rail** (there is no Sites block on the
Terrain rail). Sites are a list on the environment (`EnvironmentDef.sites`, `SitePlotDef`: id, name,
boundary in host meters, optional `fillEnvironmentId`); each draws a cyan "Site frame" outline
(a terrain-draped ribbon mesh, so it hugs grade at a constant world width instead of
billboarding at the camera). Corners land on the terrain surface under the pointer, not on the
flat y=0 plane the other draw tools use.
Any site change (draw, reshape, rename, delete, fill or clear a fill) auto-saves the host
environment to the server after about a second, so other clients loading the environment see the
same sites. Drawing the first site with nothing loaded creates the working environment's server
record automatically.

| Control | Use |
|---|---|
| **New site** | Press and drag on the ground to size the plot (axis-aligned rectangle, Shift for a square); release commits it. A live readout shows the size in m and ft. A bare click does nothing. The tool stays on so you can drag the next site; Esc mid-drag drops the rectangle, Esc again leaves the tool |
| **Site rows** | Select a site; shows `empty` or the name of the scene filling it |
| **Rename** | Renames the selected site |
| **Edit boundary** | Drag corners, click an edge to insert one, Delete removes the selected corner (min 3). A linked fill re-fits live |
| **Clear fill** | Unlinks the generated scene (it stays in the library) |
| **Delete site** | Removes the outline; a linked scene stays in the library |

The list doubles as the generation target: selecting a row selects the site and targets it
(default target is "New environment (whole scene)"). Sites can be drawn, reshaped, renamed and
deleted without loading a sketch; drawing with nothing loaded auto-creates a blank working
environment (same as the placement tools). The selected site shows a real-world size readout
(bbox in m and ft — what a generation is scaled to).

**Filling a site**: in the Generate rail, pick the site in the **Sites** list, then Generate. Unity sends the drawn boundary and its real dimensions with the sketch
(`lot_boundary` + `site_width_ft`/`site_height_ft`, see [server-api.md](server-api.md)), so the LLM
lays out for that parcel. The result is saved as its own generated environment, linked to the site,
and rendered inside it: a deep copy is scaled + translated into the site's bounding box
(`SiteFit.ProjectIntoSite`) and shown as a locked backdrop; its ground paint composites into the
host terrain clipped to the site polygon. Generating into an occupied site replaces the link (the
old scene survives in the library). To edit a fill's content, open its record from the library like
any generated environment; the fill re-fits next time the host loads. Site fills are not published
over Live Share / VR sync yet.

## Terrain editor (right panel → "Terrain editor")

| Tool | Use |
|---|---|
| **Draw paths** | Pick a path material + width. **Straight**: click to drop waypoints, Enter or double-click to finish. **Freehand**: hold LMB and drag. Paths render as textured mesh ribbons (`PathDef` in `site.paths`); the list has a ✕ per path |
| **Draw fences** | Fence analogue of Draw paths (same straight/freehand drawing, snap-to-ends, edit/✕ list). Pick a `fence_type` from the **FencePalette** and a height; the centerline (`FenceDef` in `site.fences`) is repeated as panel prefabs end-to-end with posts at joints, draped onto the terrain. Preview shows an amber centerline; panels appear on finish. **Edit** drags control-point handles (click the line to insert, Delete to remove) — or click the fence in the scene (enters the same editor; clicking a *different* fence retargets it) |
| **Draw water** | Ponds and rivers (`WaterBodyDef` in `site.waterBodies`). **Pond**: click each corner, Enter or double-click closes the ring (3+ corners). **River**: the Draw paths gesture (straight clicks or a freehand drag) plus a width and smoothing. Presets **Puddle / Pond / River / Lake** set depth, bank, width and color. The surface is one flat translucent mesh; the bed is carved into the ground automatically. **Edit** (rail list or click the water in the scene) drags corner handles, click the outline to insert, Delete removes a handle or the whole body. Details below |
| **Paint objects** | Pick a prefab (trees/greenery), radius, density; hold LMB and drag to scatter with random rotation/scale. **Erase** removes painted instances within the radius. Writes `ObjectInstance`s |
| **Paint ground** | Paint a terrain type into the splatmap as `site.surfaceStrokes` (layered over the rectangular `terrainZones`). Details below |
| **Shape ground** | Height brush. **Raise/Lower** (signed rate in m/s, applied while the button is held, so holding still keeps going; negative digs), **Smooth** (blends toward the 3×3 neighborhood), **Flatten** (levels toward the height under the cursor at the press). Radius 0.5 to 30 m with a smoothstep falloff; **Stay inside lot** clips to the parcel; **Reset to flat** clears every stroke (undoable). Stored as `site.heightStrokes`. Details below |

Paint ground details:

- Footprint **Round** / **Square** + size. `radius` is the half-extent for both, so a square's side
  is `2 × radius`.
- **Freehand** drags; **Straight** drags start → end for one run (the fence gesture — a bare click
  paints nothing, Esc mid-drag abandons the run but stays in the tool). Both paint continuously
  while the button is held: the straight run rasterizes as it grows and re-fits when you swing or
  pull back, because `WorldRenderer` snapshots the ground under the run and restores-then-restamps
  each update (append-only stamping would smear a fan). A straight drag shows a `length · angle`
  readout, a brush outline at both ends, and snaps its start onto a nearby existing run's end
  (highlighted) so runs chain flush.
- **Brush angle** (square only): **Fixed** (default) pins every stamp to one angle — 0–90° slider
  (a square is 90°-symmetric), or sample it with **From selection** (selected building's/object's
  yaw) or **From lot edge** (parcel edge nearest the cursor) — or **Auto**, where each stamp takes
  its segment's heading. **Snap run angle** snaps the run to `brushAngle + k × increment`
  (15/30/45/90°) so run and stamps share one rotated site grid.
- **Shift** = no snapping at all (bypasses angle snap and snap-to-nearest-painted-end; the fence
  tool's convention, the opposite of Shift in the transform tools).
- Stored per stroke as `SurfaceStrokeDef.shape` and `angleDeg` (`"circle"` / `< 0` = auto by
  default, so older strokes load as round and auto).

Shape ground details:

- **Range**: 15 m up and 15 m down from the flat base. `TerrainData.size.y` is 30, flat ground is
  normalized 0.5, and the `Terrain` sits at y = -15 so the base plane is world y = 0. The heightmap
  is 257 × 257; `WorldRenderer.EnsureHeightSetup` enforces all three once per session (editing the
  shared `New TerrainAlt` asset the first time, 65 → 257).
- **Time based**: every frame the button is held stamps `rate × dt` (Raise/Lower) or
  `strength × dt` (Smooth/Flatten) at the cursor with a smoothstep falloff to the rim. Frames
  within `radius × 0.25` of the last stored sample fold into it (`HeightBrush.MergeAmount`: raise
  sums, weights compose as `1 - (1-a)(1-b)`), so a hold stores one `[x, z, amount]` sample and a
  drag about four per radius. The live stamp lands at the stored sample's center, so the preview
  matches the replay.
- **Replay**: `WorldRenderer.ApplyHeightmap` fills the base and replays `site.heightStrokes` in
  order on load, undo, active-env switch, and after each stroke commits (`HeightStrokeDef`: brush,
  radius, `targetHeight` for flatten, `clipToLot`, points at 1 mm). Math lives in
  `Authoring/HeightBrush` (unit tested); the live stamp is `WorldRenderer.StampHeightLive`.
- **Release** re-renders the environment so objects, buildings, paths, fences and frames re-seat
  on the new surface. One undo step per stroke. `Stay inside lot` is evaluated against the parcel
  at replay, like the splat mask, so reshaping the lot later can expose or hide part of a stroke.
- Height strokes scale and translate with the environment (`EnvironmentScale`: footprint like a
  surface stroke; raise amounts and the flatten target follow Y). A site fill's height strokes are
  carried but not composited into the host heightmap.
- The brush ring and the other two brushes' outlines drape on the terrain, and all three brushes
  pick the cursor on the terrain surface (`GroundPointOnTerrain`), so they land under the pointer
  on hills.

Draw water details:

- **Kinds**: a `pond` is a closed ring of clicked corners; a `river` is a centerline with `width`
  and `smoothing`, built exactly like a path ribbon (`RoundCorners` then `Smooth`) so the carve,
  the preview and the mesh agree. Points are world meters like height strokes.
- **Heights** are both measured from the flat ground height (world y = 0, the height brush's base
  plane). **Depth** is how far the bed is dug below it, so on flat ground it is the depth of the
  hole. **Water level** (`surfaceY`) is where the surface sits: 0 is level with flat ground,
  negative sinks it into the hole, positive lifts it (up to the heightmap's 15 m headroom). The
  level is held at least 2 cm above the bed (`WaterGeometry.ClampSurfaceY`), so lowering Depth
  pushes it up. Ground above the surface around the edge is cut down to meet it.
- **Bed**: `WorldRenderer.ApplyHeightmap` replays the height strokes, then shapes the ground for
  each body so the outline *is* the shoreline: at the outline the ground sits 3 cm below the
  surface; inward it slopes down to the bed over `bankWidth`; outward it slopes from the
  surrounding ground down to the shore over `bankWidth` (smoothstep both ways, `min` semantics, so
  ground is never raised and a deeper hand-dug spot inside is kept). Nothing is stored: delete the
  body and the hole fills in, change the depth and the bed follows. **Stay inside lot** clips the
  carve to the parcel like a height stroke. Only the active env carves (backdrops render their
  water at the live ground height).
- **Preview while drawing** drapes on the ground (a river ribbon hugs the terrain, a pond fill
  floats just above the highest point of its outline) because the true surface sits below the
  un-dug ground and would be invisible; the commit digs the bed and the real mesh appears. While
  editing, the preview is the real mesh at the carved level.
- **Presets** set the drawing defaults: Puddle (5 cm deep, 0.3 m bank, clear), Pond (1.5 m, 2 m,
  lake), River (6 m wide, 1.2 m deep, 2 m bank, lake), Lake (4 m, 6 m, dark). Colors come from the
  **WaterPalette** (`clear`, `lake`, `murky`, `dark`: flat URP Unlit transparent materials, no
  waves or reflections, cheap on Quest).
- **Keys**: Esc drops a half-drawn shape, then leaves the tool; Backspace removes the last point;
  Enter or a double-click finishes. In edit mode: drag a dot, click the outline to insert, Delete
  removes the selected dot (a pond keeps at least three, a river two) or the whole body when none
  is selected. Sliders and the color list commit live; the world re-renders when the mouse is
  released. One undo step per draw, delete, insert, drag or slider gesture.
- **Rendering**: one mesh per body (`WaterGeometry.BuildPondMesh` via `PolygonTriangulator`, or
  `PathMesh.Build` flat for a river), on Unity's built-in **Water** layer with a plain
  `MeshCollider` and a `WaterMarker`. The walkthrough walker ignores that layer
  (`Physics.IgnoreLayerCollision` in `WorldRenderer.Awake`) and its placement ghost skips it, so
  you wade down into the bed rather than walk on the surface. A locked backdrop's water keeps its
  own alpha under the dim tint. Math lives in `Authoring/WaterGeometry`, `WaterCarve` and
  `PolygonTriangulator` (unit tested).
- Water bodies scale and translate with the environment (`EnvironmentScale`: footprint like a
  path; width and bank isotropic; depth and water level follow Y) and count toward Fit lot to content.

Paths, fences, water bodies, scattered objects, surface strokes, and height strokes all live inside the
environment JSON, so they Save/Load and round-trip through the server like everything else. Layout generation can also emit
`paths` / `fences` (→ `PathDef` / `FenceDef`), so generated and hand-drawn terrain are interchangeable.

## Multiple environments

**Load** adds an environment without unloading the others — several render at once, overlaid at
their shared origin (positions are world coordinates). Only **one environment is active**
(editable/saveable); the rest are locked backdrops (colliders disabled, dimmed). The **Loaded (n)**
list shows every loaded env — **Edit** makes one active, **Close** unloads it (no server delete).
Selection, placement, transform, tile-edit, **Save**, **Save As**, and **Bake** operate on the active
env only; the active env paints the shared terrain.

`WorldRenderer` keeps one root + instance map per env id (`_envRenders`) and a single `_activeEnvId`;
`LibraryBrowser` keeps a `LoadedEnv` list with one `_active`; `EditController` edits
`LibraryBrowser.CurrentEnvironment`, which resolves to the active env.

## Locked environments (digital twin)

`EnvironmentDef.locked` is a persistent flag (round-trips through the server; shown in list rows via
`EnvironmentSummary.locked`). Lock from the Loaded-list row (**Lock** is one click; **Unlock…** asks
to confirm; persists immediately via PUT). A locked env **can still be active** — it owns and paints
the shared terrain — but is **read-only**: all edit rails show a locked notice; G/R/T, gizmo drags,
Delete, double-click tile-edit, calibration, include-toggles, undo/redo, dirty-marking, Live-Share
auto-save, **Save**, Archive, and Delete all refuse. **Save As** / **Duplicate** produce an *unlocked*
copy — the sanctioned "design on top of the twin" flow. Known limitation: `BuildingDef`s are global,
so a def shared with the twin can still be edited from another env or the Buildings tab.

## Optional content (VR detail level)

`optional` is a persistent bool on `ObjectInstance`, `BuildingInstance` and `EmbeddedObjectDef`
(default false, so every record saved before the field existed loads as required). Optional items
are the ones the low-performance client may leave out: the VR viewer never spawns them
(`WorldRenderer.skipOptional`, turned on by `SyncClient`, see [vr-live-sync.md](vr-live-sync.md)).
The desktop editor always renders everything. `included` still wins: an Off item never renders
anywhere, whatever its optional flag. The rules live in `Authoring/OptionalContent.cs`.

Where the flag is set:

| Control | Effect |
|---|---|
| Library → Contents row → **Opt** | Flips one building or object instance. Sits next to the On/Off include toggle. Undo: "Mark optional" / "Mark required" |
| Library → **Mark optional** / **Mark required** | Applies to the whole scene selection (box select works). Label flips to "required" once every selected item is optional. The same button sits above "Delete selected" in the right rail |
| Library → **Type optional** / **Type required** | Every object sharing the primary selection's `prefabType`, across the whole place (all the trees at once). Undo: "Mark type optional" / "Mark type required" |
| Tile editor → Decorate → **Optional** | Flips face decor on the shared `BuildingDef` (so every copy of that building, in every place). **Whole face** flips a side. Saved with "Save changes" |

**Low detail** (library header toggle) previews what the headset shows: every optional item goes
inactive in the editor, backdrops and site fills included. Nothing is rebuilt and editing stays on.
A hidden item behaves exactly like an Off item: it keeps its list row and can be selected there,
but has no gizmo and cannot be clicked or box-selected in the scene until Low detail is off.
Marking or unmarking never re-renders; the renderer re-stamps the instance's `InstanceMarker` and
applies the preview state. Paste keeps the flag (a copy of a background prop is still a background
prop). Nothing is optional by default.
