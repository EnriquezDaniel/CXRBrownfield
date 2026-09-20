# Editing: controls, tools, and workflow

Everything here runs in Play mode in `BasicModel`. Panels are IMGUI: `LibraryBrowser` (left),
`EditController` / `TileBuildingEditor` (right rail), `ModelRequesterUI`, `BakePass` (bottom-center).

## Workflow: generate → edit → save

1. `python server/server.py`
2. Play mode → **Generate Layout** → select sketch → layout converts and saves to the library.
   The **Notes** box under the sketch list is plain prose. The server first reads it into a
   structured brief (`brief_prompt.py`), hands both to the layout model, then enforces the brief on
   the result and reports what it missed (the Output text shows `Notes: 3 of 4 named building(s)
   placed, missing …`; the names also go to the Console). Notes can steer buildings (a name that
   becomes the library name, a style letter A to F, floors, a use, a position phrase), split one
   drawn block into several buildings, set path and fence materials, widths and types, and ask for
   or exclude props. Ground and water sentences are ignored on purpose. Examples:
   `The long block is three shops: a cafe, Rite Aid style C 4 floors, and a bakery.`
   `Brick sidewalk 8 ft along the avenue with a row of trees. Chain-link fence on the east edge. No benches.`
   The letters A to F map to wall materials in `BuildingStylePalette`; the model only ever sees
   the letter and never picks one on its own. A building the sketch or the notes name also gets a
   sign word (`sign`, e.g. `ICECREAM`) that Unity hangs on the wall facing east (see the **Sign**
   section under "Edit-mode controls" below; you can rename, re-aim and move it after generation, and add more signs for other tenants);
   the model chooses the word. A split block becomes equal touching pieces along its long
   side, in the order the notes list them. Notes are saved beside the sketch on the server when
   you generate, so they come back the next time you pick that sketch, and the notes, the brief
   and its report are stored on the generated place (`EnvironmentDef.generation`).
   Every generated building also arrives with a **window pair on each outward-facing wall face
   from the second storey up** (`BuildingWindows`, one per tile face, none on the ground floor or on
   faces that touch a neighbouring tile). They are ordinary Decorate props saved in
   `BuildingDef.embeddedObjects` from the `DecorPalette` **WindowPair** entry, marked `optional` so
   the low-detail VR viewer skips them. Erase or repaint them one face at a time with **Decorate**,
   or a whole side with **Whole face**. Tiles and floors you add later get no windows on their own.
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
`Assets/Resources/DummyLayout.json`, the model's reading of the Westchester Avenue sketch
(`samples/WestchesterSample1.jpg`, raw output in `layouts/WestchesterSample1.json`) on the real
459 × 66 ft strip: two fenced pickleball courts at the north end, then a 7-floor mixed-use
building split into a Movie Theater block (style B) and an Ice Cream Shop block (style A). It arrives as an editable,
unsaved environment in the Loaded list, so steps 3 to 6 work on it like any other. It honours the
same **Sites** target Generate uses: pick a site and the sample is projected into that plot
(`SiteFit.ProjectIntoSite`) instead of loading at the origin.

**Generate rail → Samples → Server sample** loads the most recently updated library place whose name
contains "sample". Today that is `WestchesterSample2` (sketch `samples/WestchesterSample2.jpg`,
hand-authored `layouts/WestchesterSample2.json`, no model call): a bike storage lot at the north end,
a community garden on a `planting_bed` ground zone with the four drawn plants as bushes, then a
Puerto Rican restaurant and a workshop for rent as two touching 3-floor style A blocks with
apartments above. It loads as its own editable place at the origin. Site A in the user place
"Westchester Bronx River Site" was reshaped to the same 459 × 66 ft preset, so either sample can be
generated or projected into it on request; nothing fills it by default.

`Assets/Tests/EditMode/DummyLayoutSampleTests.cs` pins the sample's geometry, its orientation, and
its fit into a site, and keeps it a valid reference for what a layout response should look like.

### Where the terrain sits

`site.terrainSize` is the ground's extent and `site.terrainOrigin` its min corner in world meters
(null ⇒ the world origin, which is where every environment authored before that field sits). Both are
applied by `WorldRenderer.ApplyTerrainSize` whenever an environment becomes active, and both travel
with the environment through `EnvironmentScale` — the size scales directly while the origin, being a
position, scales about the pivot and shifts on a translate.

Every stored XZ (instance positions, path, fence, stroke and water points, zone rects, lot and site
polygons) is in world meters, never relative to that corner. `WorldRenderer` places geometry at the
stored XZ as is; only the splat and heightmap rasterizers subtract the terrain position to reach
alphamap cells. Adding the corner to a stored position is how a moved building used to jump by the
site offset a second time.

`SiteFit.ProjectIntoSite` seeds `terrainOrigin` with the site's corner, so an environment fitted into
a host's drawn site brings its ground with it. Every loaded environment has its own ground (see
"Multiple environments"), so loading that environment next to its host shows both grounds, the
active one on top where they overlap. A site *fill* is different: it has no ground of its own, sits
on the host's terrain, and only its ground paint is composited into the host's splat.

**Moving a place.** The corner is editable from the Terrain rail's **Site** block: type it into the
**Origin (m)** fields and Apply, or turn on **Move site** and drag anywhere on the ground. Both bake
the move into the data through `EnvironmentScale.TranslateEnvironmentXZ` (instances, paths, fences,
water, strokes, the lot, the drawn site plots and `terrainOrigin`, which is seeded to 0,0 first when
the record predates it), so nothing new is stored and the VR viewer, the server and undo see an
ordinary edit. While the drag is held only transforms move (`WorldRenderer.PreviewEnvironmentOffset`
offsets the place's root, its site fills' roots and the place's own Terrain); the release commits, re-renders and
re-fits the fills. Every rebuild path clears that preview first, so an undo mid-drag can never bake an
offset root. The rectangle lot handles, `EnvironmentScale.EffectiveLotPolygon`, the out-of-lot checks
and the two **Fit** buttons all honour the corner: a fit sets both origin and size, so a parcel or
content in negative space gets ground under it.

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
| G / R / T | Grab / rotate / scale selected. Esc, Enter, or switching to another tool or mode all keep the change; Ctrl+Z takes it back. A building's height is an offset from the ground under it (0 = on the ground), so a building sunk or raised with the gizmo's Y arrow stays that far from the ground through save and reload |
| Shift (during any rotate) | Snap rotation to 15° (R-drag, gizmo ring, panel Y slider are free otherwise) |
| Gizmo handles | Arrows = move on X/Z, center pad = free move, ring = rotate, top cube = scale (works without G/R/T) |
| Right-panel sliders | Rotation (0–360°) and scale (0.1–5) of the selected instance |
| Right-panel **Pivot** (Rotate tool, multi-selection) | **Each object** (default; every instance spins about its own pivot) or **selection center** (yaw orbits positions around the group's XZ centroid). Applies to R-drag, gizmo ring, Y slider; X/Z rotation stays per-instance |
| Right-panel **Skew shape** (selected building) | Whole-building deform of the `BuildingDef`: **Bend corner** (acute/obtuse footprint corner) or **Slope edge** (shed roof). **Apply** stacks onto the current shape, **Reset** clears all deform; both re-render, `PutBuilding`, and are undoable. Writes `TileDeform` via the shared `TileDeformField` (same field AI generation uses), so it round-trips like a tile edit |
| Right-panel **Sign** (selected building, open by default) | The signs the placed building carries, one row per tenant (`BuildingInstance.signs`, a list of `BuildingSignEntry`, plus the shared `signCompass`; two copies of one def can differ). **North / East / South / West** is the world direction the row starts on; `BuildingSigns.StartFace` turns it into the building-local wall through the instance yaw, so a turned building still picks the right wall ("Aim signs"; pins keep their own wall). Each row has a word field, **▲ / ▼** to reorder, **Pin**, and **×** to remove. **Add sign** adds an empty row (off while one is still empty), **Add name** adds the building's name, and **Apply words** (or Enter in a field) commits every typed word as one undo step (uppercased, 16 characters max, "Set signs"). Layout is automatic (`BuildingSigns.Layout`): every plate is as wide as its word at one shared text size, in the top band of the lowest floor. The row fills the start wall's open stretches, longest first, in list order, left to right as seen from outside. Each stretch is shared out in proportion to plate width and every sign is centred in its share. Signs that do not fit spill onto the neighbour wall with more open wall (counterclockwise seen from above on a tie, which is the usual case), then on around the building the same way. Open wall is the exposed-face test the tile editor uses (`TileFaces.IsExposed`); a missing tile or a pillar ends a stretch. **Pin** holds a sign where it hangs and shows its move controls; the other signs space themselves around it, and Pin off returns it to the automatic row. **Move sign** arms a drag over the building: the plate snaps to half-tile steps and can go to another floor or another wall; Esc leaves. **Left / Right** nudge half a tile (right = the viewer's right facing the wall, gaps in the wall are hopped), **Up / Down** move a floor. A word wider than the longest open stretch shrinks alone ("Shrunk to fit."); under half size it is not drawn ("Too long for any wall. Not shown."). A sign with no wall left stays in the list ("No room on any wall. Not shown."), a pin whose wall is gone is laid out automatically ("Saved spot is gone. Placed automatically."), and a sign off the start wall says which wall it is on. Every edit is one Environment-scope undo step saved with the place (never `PutBuilding`), and only the `Signs` GO is respawned (`WorldRenderer.RespawnBuildingSign`). Undo while the section is open deselects the building (as with Transform); click it again to continue. Records saved with the older single sign (`signText`, `signPinned`, `signHostX/Z/Floor`) or the def's legacy `signText` / `signFace` read as a one-row list at the same spot (`BuildingSigns.EntriesFor`) until the first edit moves them into `signs` (`BuildingSigns.Adopt`). Plates are narrower than the old fixed two-tile plate |
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
  locked env refuses. The pasted set becomes the selection; one undo step.
  **A pasted building is its own building**: each one gets a cloned `BuildingDef` with a new id and
  the next free numbered name across the library (`Coffee Shop` → `Coffee Shop 2`; copying
  `Coffee Shop 2` gives `Coffee Shop 3`), so tile edits, paint, skew and style on the copy never
  touch the original. The signs are kept, pins included. The copy is held in memory and posted with the next
  environment save (Save, Save As, Live auto-save); tile edits and skew on it are held the same way,
  and closing without saving leaves no record behind. Copies carry `hiddenCopy` and stay out of the
  Buildings tab and the Place rail until renamed. Placing one library building twice from the Place
  rail still shares a single def. Rules live in `BuildingCopies` (`Authoring/`).
- **Building name** — the right rail shows the selected building's name with a **Rename** field.
  The name belongs to the `BuildingDef`, so every placement that shares it follows. Names must be
  unique; renaming a pasted copy lists it in the Buildings tab and the Place rail. Undoable
  (building scope).

## Tile edit mode (double-click a building)

| Key / action | Effect |
|---|---|
| Q / E | **Add** tool: rotate hovered/placement yaw −90° / +90°. **Select** tool: rotate selected tile ±15° on the active axis |
| X / Y / Z | **Select** tool: choose the active rotate axis |
| Shift/Ctrl + click | **Select** tool: toggle a tile in/out of a multi-selection (or use "Select floor / Select all") |
| Left-click | **Add** places one tile in the hovered cell, on release |
| Left-drag | **Add** paints each cell dragged over once the cursor moves a few pixels, **Select** keeps adding hovered tiles, **Paint** paints each face dragged over, **Decorate** places the active decor on each face dragged over |
| **Style** row (above the tools) | Facade style for the whole building: **None** or **A** to **F** (`BuildingDef.style`). Each letter's `BuildingStylePalette` wall material covers every wall face you have not painted; tops and bottoms keep the default. Switching re-skins at once, one undo step ("Set style"). Painted faces stay painted, and there is no way yet to hand a painted face back to the style. The layout generator sets the letter from the sketch notes |
| Signs | Drawn here exactly as in the world (same spawner, same layout) and laid out again after every tile change, so you see the row reflow while shaping tiles. Edited only from the selection panel's **Sign** section (they belong to the placed instance, not the def). A building opened from the library has no placed instance, so it shows no signs |
| **Paint** | Assign a `MaterialPalette` material to a tile face. Paint sits on top of the building's style |
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
| **Origin (m) fields + Apply** | World position of the ground's min corner (`site.terrainOrigin`). Apply moves the **whole place** so the corner lands there: everything on it comes along, one undo step ("Move site") |
| **Size (m) fields + Apply** | Type width × length and Apply. Content stays at its world coordinates (fitting a layout into a region is the site-fill pipeline's job, `SiteFit`) |
| **Move site** | `EditMode.MoveSite`. Press and drag anywhere on the ground; the place, its ground and its site fills follow as a live preview and the release bakes the move. The corner snaps to whole meters, Shift moves freely. Esc drops a drag in progress, Esc again leaves the tool. Refused on a locked place |
| **Edit lot (handles)** | `EditMode.EditLot`. **Rectangle** sub-mode: drag the far corner / far-edge handles to resize from the ground's corner (`terrainOrigin`). **Parcel** sub-mode: drag vertices, click an edge to insert one, Delete removes the selected one (min 3); **Reset parcel to rectangle** drops the boundary. A draped amber "Lot frame" line shows the parcel; a live preview tracks the drag |
| **Fit terrain to lot** / **Fit lot to content** | Move and resize the ground rectangle to hug the parcel's extent (+ 2 m), or to enclose all placed content (+ 5 m): `terrainOrigin` lands at the min corner minus the margin (`EnvironmentScale.FitTerrainRect`). Content stays where it is |
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
lays out for that parcel. **Orientation**: the sketch's vertical axis lands on Unity X and its
horizontal axis on Unity Z, turned half a turn so the top of the sketch (north) is +X and its left
edge (west) is +Z (`LayoutConverter`), so a site whose X extent is the longer one (`site_width_ft` >
`site_height_ft`) wants its long side drawn *up the page*. The rail shows this as a note under the
Sites list ("Draw the long side up the page: 374 ft tall by 64 ft across") plus a **Sketch**
selector: **Auto** (default) lets the server give a sketch a quarter turn when its long side runs the
other way from the site's, **As drawn** sends it untouched, **90 / 180 / 270** force a
counter-clockwise turn (`sketch_rotation` in the request). The server then resamples the image to the
parcel's true proportions so the model sees the real lot shape, and tells it the feet per canvas unit
on each axis. The result is saved as its own generated environment, linked to the site,
and rendered inside it: a deep copy is translated so its own parcel bbox sits on the site's bounding
box (`SiteFit.ProjectIntoSite`; the fit is by the child's `lotBoundary`, not its terrain, so the
2 m terrain margin never squeezes the content and the scale is exactly 1 for a generated child)
and shown as a locked backdrop; its ground paint composites into the
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
  is 257 × 257; `WorldRenderer.EnsureHeightSetup` enforces all three on each environment's ground
  copy when it is created. The shared `New TerrainAlt` asset is only a template and is never written.
- **Time based**: every frame the button is held stamps `rate × dt` (Raise/Lower) or
  `strength × dt` (Smooth/Flatten, strength 0.1 to 20, one frame capped at a weight of 1) at the
  cursor with a smoothstep falloff to the rim. Frames within `radius × 0.25` of the last stored
  sample fold into it (`HeightBrush.MergeSample`), so a hold stores one sample and a drag about
  four per radius. The live stamp lands at the stored sample's center, so the preview matches
  the replay.
- **Folding frames without changing the ground**: raise amounts sum. A flatten sample stores the
  composed weight `1 - (1-a)(1-b)` (capped at 0.999) and the stamp lerps each cell by
  `1 - (1-weight)^falloff`, which composes the same way, so one merged stamp equals the frames it
  came from all the way to the rim. A smooth sample is `[x, z, weightSum, passes]`: a 3×3 blur has
  no closed form, so the replay runs `passes` blur passes of `weightSum / passes` (a 3-element
  sample from before this format replays as one pass). Without this the replay of a held smooth
  collapsed to a single pass and most of the smoothing vanished on release.
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
- **Bed**: `WorldRenderer.ApplyHeightmap` shapes the ground for each body on the flat base, then
  replays the height strokes over it, so the brush always has the last word: a raise can fill a
  bed, a flatten can level a bank, and what the live preview showed next to water is what stays
  on release. The outline *is* the shoreline: at the outline the ground sits 3 cm below the
  surface; inward it slopes down to the bed over `bankWidth`; outward it slopes from the
  surrounding ground down to the shore over `bankWidth` (smoothstep both ways, `min` semantics, so
  the carve never raises ground). Nothing is stored: delete the body and the hole fills in, change
  the depth and the bed follows. Strokes made before a body was drawn replay over it too, so a
  flatten laid where a river is later drawn will partly fill that river. **Stay inside lot** clips
  the carve to the parcel like a height stroke. Every loaded env carves its own ground, backdrops
  included.
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

**Load** adds an environment without unloading the others — several render at once at their world
coordinates. Only **one environment is active** (editable/saveable); the rest are locked backdrops
(colliders disabled, dimmed). The **Loaded (n)** list shows every loaded env — **Edit** makes one
active, **Close** unloads it (no server delete). Selection, placement, transform, tile-edit, the
terrain tools, **Save**, **Save As**, and **Bake** operate on the active env only.

**Every loaded env keeps its own ground.** The scene's `Terrain` (`WorldRenderer.targetTerrain`) is a
template, hidden in Play. Each env gets a copy with its own `TerrainData` (`Terrain:<name>`, a sibling
of the env's root under the renderer), sized, placed, sculpted, carved and painted from that env's
site, and destroyed when the env closes. Switching the active env rebuilds nothing. Details:

- Grounds are drawn as they are, full rectangle and outside ring included; nothing is clipped or
  merged where two overlap. To keep two coincident flat grounds from flickering, each backdrop (env
  and ground together) sits 2 cm lower per load rank, down to 10 cm (`TerrainStack.BiasY`), so the
  active env's ground is the one you see. Backdrop ground is not dimmed and stays solid, so Walk
  and the headset can stand on it.
- Placement drapes each env on its own ground (`DrapeY`; a site fill uses its host's). "What is the
  ground here" queries from Walk, the headset and the cursor go through
  `WorldRenderer.SampleTerrainSurfaceY`: the active ground inside its rectangle, else the highest
  loaded ground that holds the point, else the active ground's edge (`TerrainStack.TryPickGround`,
  unit tested).
- With nothing loaded a flat, unpainted ground is shown (Play only).
- Each copy carries a full splat (1024² × 28 layers from the template). `WorldRenderer`'s
  **Env Alphamap Resolution** lowers it per scene; `VRViewer` uses 512.

`WorldRenderer` keeps one root, ground + instance map per env id (`_envRenders`) and a single
`_activeEnvId`; `LibraryBrowser` keeps a `LoadedEnv` list with one `_active`; `EditController` edits
`LibraryBrowser.CurrentEnvironment`, which resolves to the active env.

## Locked environments (digital twin)

`EnvironmentDef.locked` is a persistent flag (round-trips through the server; shown in list rows via
`EnvironmentSummary.locked`). Lock from the Loaded-list row (**Lock** is one click; **Unlock…** asks
to confirm; persists immediately via PUT). A locked env **can still be active** but is **read-only**: all edit rails show a locked notice; G/R/T, gizmo drags,
Delete, double-click tile-edit, calibration, include-toggles, undo/redo, dirty-marking, Live-Share
auto-save, **Save**, Archive, and Delete all refuse. **Save As** / **Duplicate** produce an *unlocked*
copy — the sanctioned "design on top of the twin" flow. Known limitation: `BuildingDef`s are global,
so a def shared with the twin can still be edited from another env or the Buildings tab. A building
pasted out of the twin is a separate def (see Copy/paste), so editing that copy is safe.

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
