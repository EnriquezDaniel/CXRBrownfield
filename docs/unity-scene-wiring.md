# Unity scene wiring and ScriptableObject assets

Project: `unity/CXRBrownfield/` (Unity 6000.3.10f1). Main scene `Assets/Scenes/BasicModel.unity`;
`VRViewer.unity` is the headset/viewer scene (see [vr-live-sync.md](vr-live-sync.md)).

## Inspector wiring

Every `// USER WIRES THIS IN INSPECTOR:` comment marks a `[SerializeField]` that must be assigned by hand.

| GameObject | Component | Required assignments |
|---|---|---|
| `WorldRenderer` | `WorldRenderer` | `Terrain`, `PrefabRegistry`, `TerrainRegistry`, `TileShapePalette`, `MaterialPalette` (tile-based building rendering), `PathMaterialPalette` (path ribbons), `FencePalette` (fence runs), `WaterPalette` (water surfaces; optional, falls back to `Resources/WaterPalette`), `BuildingStylePalette` (style letter → wall material; optional, falls back to `Resources/BuildingStylePalette`), `BuildingGenerator` (legacy bay massing — used only when a def has tiles but `TileShapePalette` is unassigned; a def with **no** tiles renders as a neutral translucent pad over cell (0,0) instead, still selectable / double-click-editable). The `Terrain` must share its `TerrainData` with the scene's `TerrainCollider` (true in `BasicModel`; `VRViewer`'s collider still points at `New Terrain.asset` and needs re-pointing for VR walking on shaped ground). `WorldRenderer.EnsureHeightSetup` forces that asset to a 257 heightmap, 30 m height range and parks the Terrain at y = -15, so it shows as modified once after the first run. `skipOptional` (Detail header) is the spawn gate for `optional` items; leave it false in `BasicModel`, `SyncClient` sets it at runtime in `VRViewer` |
| `LibraryBrowser` | `LibraryBrowser` | `LibraryClient`, `WorldRenderer` |
| `LibraryClient` | `LibraryClient` | `serverBaseUrl = http://localhost:5002` (a headset needs the host PC's LAN IP) |
| `EditController` | `EditController` | `LibraryBrowser`, `LibraryClient`, `WorldRenderer`, `TileBuildingEditor`, `PrefabRegistry`, `PathMaterialPalette` (path tool), `FencePalette` (fence tool — borrowed from `WorldRenderer` if unset), `TerrainRegistry` (ground-surface tool); optional: camera |
| `TileBuildingEditor` | `TileBuildingEditor` | `TileShapePalette`, `MaterialPalette`, main camera; `PrefabRegistry` + `DecorPalette` (Decorate tool); `BuildingStylePalette` (Style row; optional, falls back to `Resources/BuildingStylePalette`) |
| `ModelRequesterUI` | `ModelRequesterUI` | `ModelRequester`, `WorldGenerator` (legacy), `WorldRenderer`, `LibraryClient`, `LibraryBrowser`; layout-source buttons: `uploadImageButton`, `refreshInputsButton`, `inputDropdown`, `generateFromImageButton`, `testLocalSampleButton`, `testServerSampleButton` |
| `BakePass` | `BakePass` | `WorldRenderer` |
| `GameManager` (`VRViewer` only) | `SyncClient` | `LibraryClient`, `WorldRenderer`; `pollIntervalSeconds`, `showStatusOverlay`, `skipOptional` (default on: the viewer never spawns items marked optional) |
| `Main Camera` | `WalkthroughController` | optional (all auto-resolve): `EditController`, `WorldRenderer`, main camera. Tunables: `walkSpeed` 1.4 m/s, `runSpeed` 3.0 m/s, `lookSens`, `stepOffset`, `slopeLimit`, `solidBackdrops` |

## The nine ScriptableObject assets

All live in `Assets/Resources/` and are **guarded** — read `.claude/rules/palette-assets.md`
before editing any of them. Keys are the string ids the LLM JSON and the editors use.

| Asset | Create menu | One entry per | Notes |
|---|---|---|---|
| **PrefabRegistry** | `Assets → Create → Prefab Registry` | `prefab_type` key in the LLM JSON | `PrefabRegistryExporter` syncs the key list into the LLM prompt. |
| **TerrainRegistry** | `Assets → Create → Terrain → TerrainRegistry` | `terrain_type` (`"grass"`, `"pavement"`, `"asphalt"`, `"plaza"`, `"sand"`, `"dirt"`, `"planting_bed"`, …) | Each entry is a `TerrainLayer`; also feeds the Paint-ground brush. |
| **TileShapePalette** | `Assets → Create → CXR → TileShapePalette` | tile shape (`"square"`, `"wedge"`, `"quartercurve"`) | Prefabs are **generated** — see below. |
| **MaterialPalette** | `Assets → Create → CXR → MaterialPalette` | material id used in `faceMaterials` (`"brick_red"`, `"glass"`, `"roof_tar"`, …) | Not to be confused with ProBuilder's `Assets/Resources/Material Palette.asset` (with a space). |
| **PathMaterialPalette** | `Assets → Create → CXR → PathMaterialPalette` | path surface id (`path_material`; canonical: `"pavement_dark"`, `"pavement_light"`, `"brick"`, `"dirt"`, `"asphalt"`) | Used by the path tool and generation. Ships all five canonical ids plus the two tool-facing aliases `"street"` and `"sidewalk"`; several share one material, since the project has no separate brick or asphalt surface yet. |
| **FencePalette** | `Assets → Create → CXR → FencePalette` | fence type (`fence_type`; canonical: `"picket"`, `"lattice"`, `"chain_link"`, `"wood_privacy"`, `"wrought_iron"`) | Entry fields below. Ships all five canonical types plus the original `"Fence"` that existing saved runs reference; all six share one panel/post pair today and differ only in default height. Unknown type → warning, fence skipped (same as a missing path material). |
| **DecorPalette** | `Assets → Create → CXR → DecorPalette` | decor preset for the tile editor's Decorate tool (`"door"`, `"window"`, `"vent"`, …) | The prop analogue of MaterialPalette. Entry fields below; every field has a `[Tooltip]`. |
| **WaterPalette** | `Tools → CXR → Palettes → Create Water Palette (seeded)` (or `Assets → Create → CXR → WaterPalette`) | water surface id (`WaterBodyDef.material`: `"clear"`, `"lake"`, `"murky"`, `"dark"`) | Entry = `id` + `material`. The seeding menu creates four flat URP Unlit transparent materials in `Assets/Materials/Water/` and appends any missing seed id; it never rewrites existing entries. `WorldRenderer` loads it from `Resources` when its slot is empty, so `VRViewer` needs no wiring. Unknown id → error, magenta placeholder material. |
| **BuildingStylePalette** | `Tools → CXR → Palettes → Create Building Style Palette (seeded)` (or `Assets → Create → CXR → BuildingStylePalette`) | facade style letter (`BuildingDef.style`: `"A"` to `"F"`, from the designer notes) | Entry = `styleId` + `wallMaterialId` (an id in **MaterialPalette**). The seeding menu appends any missing letter with A RedBricks, B CobbleStoneWall, C ConcreteBricks, D WoodWall, E SteelWall, F PlasterBricks and never touches an existing entry, so re-point the ids in the Inspector to build your own set. At spawn `TileSpawner` paints the material on every wall face (all faces except `top` / `bottom`) that has no `faceMaterials` entry, so hand paint wins. `WorldRenderer` and `TileBuildingEditor` load it from `Resources` when their slot is empty. Unknown letter or missing material → one console warning, walls keep the default. |

### FencePalette entry

`panelPrefab` — one fence panel **modeled along +X with its base at y=0** (the renderer repeats and
stretches it) · `postPrefab` (optional, placed at each joint/corner) · `panelLength` (m, centerline
resample spacing) · `height` (m, default when `FenceDef.height` ≤ 0) · `scalePanelToFit` (stretch
each panel along its run axis to span its gap). The existing `Fence_0` / `FanceConnector` park
prefabs can seed a first type; new types need their own panel prefabs.

### DecorPalette entry

`prefabKey` (must exist in PrefabRegistry) · `surface` filter (`Wall` / `Roof` / `Any`) ·
`widthFraction` / `heightFraction` (how much of the cell face the prop spans, aspect preserved) ·
`anchor` (`Center` / `Bottom` / `Top`; doors anchor `Bottom`) · optional `mountAxis` / `flipMount`
auto-align overrides · `surfaceOffset` (z-fight epsilon) · `replacesOtherDecor` (occupancy flag).

Placement semantics (all in `TileBuildingEditor.ReplaceConflictingDecor`; the renderer has no per-face
dedup): decors **stack** — different decors coexist on one face, only re-painting the **same** decor
replaces it, so drags never pile up duplicates. `replacesOtherDecor` makes an entry own the whole
face: it clears other decor there, but only decor that *also* has the flag; a stacking decor is never
displaced. Painted decor persists as `BuildingDef.embeddedObjects` (`hostGridX/Z/Floor`, `hostFace`,
`fillsFace`, `optional`) and renders via `WorldRenderer.RenderEmbeddedObjects`, which skips
`optional` entries when `skipOptional` is on (the VR viewer).

The entry whose `decorId` is **WindowPair** also drives the windows the layout generator puts on
every exposed wall face from the second storey up (`BuildingWindows`, called from
`LayoutConverter`): its `prefabKey`, fractions, anchor, mount overrides and `surfaceOffset` are
copied onto each generated record, always as `optional`. `ModelRequesterUI` reads the palette
through `DecorPalette.LoadDefault()` (Resources), so no Inspector slot is needed. Remove or rename
the entry and generation falls back to built-in defaults (prefab `WindowPair`, 0.8 fractions,
Bottom anchor) with one console warning.

A building's sign persists on the placed instance: `BuildingInstance.signText` (normalized
uppercase word, null = none), `signCompass` (`north` / `east` / `south` / `west`, the world direction
it faces; north is +X), `signPinned` plus `signHostX` / `signHostZ` / `signHostFloor` (the first tile
of the pair the user slid it to; unpinned = the centred pair). `LayoutConverter` writes the word and
`east` for a generated instance. `BuildingDef.signText` / `signFace` are legacy read-only fields
from records saved before that change; `BuildingSigns.SpecFor` uses them for an instance whose
`signCompass` is still null, and the first panel edit moves the sign onto the instance.
`BuildingSigns` (Authoring) resolves the plate from the spec: the compass becomes the building-local
wall through the instance yaw, the pinned pair is used while it exists, else the centred pair on
that wall's main run, else the first other wall with room; the plate is a band `BandFrac` (0.35) of
the cell tall under the floor's top edge. `BuildingSignSpawner` builds it under the building root as
`Sign/Plate` (a collider-free quad, URP Lit near black) and `Sign/Text` (a `TextMeshPro` 3D text,
white bold, auto-sized, the TMP Settings default font `LiberationSans SDF`). No inspector wiring:
`WorldRenderer.RenderTiledBuilding` and the tile editor call the spawner with the same spec, and
`WorldRenderer.RespawnBuildingSign` swaps just the sign GO after a panel edit or a drag step.

## TileShapePalette: per-face tile prefabs are generated

Each tile shape needs a prefab with **one submesh + one material slot per named face** and a
**MeshCollider** on that same mesh, with `faceNames` in submesh order. That is what per-face painting
relies on (`TileSpawner.ApplyFaceMaterial` writes material slot *i*; `TileBuildingEditor.FaceFromHit`
reads the hit submesh). Hand-made prefabs (built-in Cube, ProBuilder prism, imported FBX) arrive with
a single submesh, so only `faceNames[0]` could ever be painted — the original bug.

Procedure:

1. Keep hand-authored **source** prefabs in `Assets/Prefabs/Tiles/`. Edit those.
2. Run **`Tools → CXR → Tiles → Rebuild per-face tile prefabs`** (`Assets/Editor/TileFaceSetup.cs`).
   For every palette entry it reads the source geometry (ProBuilder and non-readable FBX included),
   splits it into one submesh per face with `TileFaceSplitter` (`Assets/Scripts/Authoring/`,
   unit-tested: planar groups by normal + plane offset; edge-connected near-parallel groups chain
   into one `curve` face; exactly axis-aligned walls never merge into a curve), writes
   `Assets/Prefabs/Tiles/PerFace/<shapeId>.prefab` + `<shapeId>_faces.asset` (mesh, Git LFS), and
   re-points **only** that entry's `prefab` and `faceNames` in place (snapshot before, Validate
   after — the guarded-asset protocol is built in). The generated prefab's importer `userData`
   remembers its source, so re-runs regenerate from the originals.
3. `Tools → CXR → Palettes → Validate` warns when a shape's slot/submesh count doesn't match its
   `faceNames` or it lacks a MeshCollider.

Face names follow `TileFaceGeometry.BaselineDir` in the *cell frame* (after `defaultRotation`):
`north`(+Z) `east`(+X) `south`(−Z) `west`(−X) `top` `bottom`, then non-axis faces (`curve`,
`diagonal`); duplicates are suffixed `_2`. Current results: square `[north,east,south,west,top,bottom]`;
wedge (a gable prism — the slopes are `east`/`west`) `[north,east,south,west,bottom]`; quartercurve
(a rounded-corner block) `[north,east,south,west,top,bottom,curve]`; pillar and slab (cubes)
`[north,east,south,west,top,bottom]`.

### Sub-cell shapes (`cellExtents` / `cellAnchor`)

A shape may fill only part of its 4 m cell. Two extra fields on each `TileShapePalette` entry describe
how (read through `TileFit` in `Assets/Scripts/Authoring/`):

| Entry | `cellExtents` (fraction of the cell, X Y Z) | `cellAnchor` (-1 min side · 0 centered · +1 max side) | Result |
|---|---|---|---|
| `square`, `wedge`, `quartercurve` | (0,0,0) = full cube | (0,0,0) | unchanged |
| `pillar` | (0.5, 1, 0.5) | (0, 0, 0) | 2×2 m post, 4 m tall, centered in the cell |
| `slab` | (1, 0.5, 1) | (0, -1, 0) | 4×4 m plate, 2 m thick, resting on the floor of the cell |

`TileSpawner.PlaceInCell` owns the placement math for the renderer, the editor's tiles, its placement
ghost and its rotate tools: fit the prefab to the extents box, rotate about the box's own center, then
slide the rotated bounds to the anchor. So a slab tipped 90° on X still rests on the floor and a tipped
pillar becomes a centered beam. `TileFaceGeometry.TryGetFaceFrame` takes the same `TileFit`, so decor
seats on the real pillar or slab surface, and `TileFit.FaceCovered` decides which faces the whole-face
tools treat as hidden (only a face that reaches the shared boundary against a neighbour that fills its
side). A pillar or slab still claims its whole cell key: one tile per cell.

Source prefabs for cube-based shapes (`pillar.prefab`, `slab.prefab`) keep the scale on a **child**
GameObject under an identity root. `TileFaceSetup.GatherGeometry` flattens geometry relative to the
root and drops the root's own scale, so a scaled root would bake a unit cube and the thumbnail and
placement ghost would read as a cube. `Tools → CXR → Palettes → Validate` warns when an extents
component is above 1 or an anchor is outside -1..1.
