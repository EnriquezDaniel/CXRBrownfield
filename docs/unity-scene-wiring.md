# Unity scene wiring and ScriptableObject assets

Project: `unity/CXRBrownfield/` (Unity 6000.3.10f1). Main scene `Assets/Scenes/BasicModel.unity`;
`VRViewer.unity` is the headset/viewer scene (see [vr-live-sync.md](vr-live-sync.md)).

## Inspector wiring

Every `// USER WIRES THIS IN INSPECTOR:` comment marks a `[SerializeField]` that must be assigned by hand.

| GameObject | Component | Required assignments |
|---|---|---|
| `WorldRenderer` | `WorldRenderer` | `Terrain`, `PrefabRegistry`, `TerrainRegistry`, `TileShapePalette`, `MaterialPalette` (tile-based building rendering), `PathMaterialPalette` (path ribbons), `FencePalette` (fence runs), `BuildingGenerator` (legacy bay massing — used only when a def has tiles but `TileShapePalette` is unassigned; a def with **no** tiles renders as a neutral translucent pad over cell (0,0) instead, still selectable / double-click-editable) |
| `LibraryBrowser` | `LibraryBrowser` | `LibraryClient`, `WorldRenderer` |
| `LibraryClient` | `LibraryClient` | `serverBaseUrl = http://localhost:5002` (a headset needs the host PC's LAN IP) |
| `EditController` | `EditController` | `LibraryBrowser`, `LibraryClient`, `WorldRenderer`, `TileBuildingEditor`, `PrefabRegistry`, `PathMaterialPalette` (path tool), `FencePalette` (fence tool — borrowed from `WorldRenderer` if unset), `TerrainRegistry` (ground-surface tool); optional: camera |
| `TileBuildingEditor` | `TileBuildingEditor` | `TileShapePalette`, `MaterialPalette`, main camera; `PrefabRegistry` + `DecorPalette` (Decorate tool) |
| `ModelRequesterUI` | `ModelRequesterUI` | `ModelRequester`, `WorldGenerator` (legacy), `WorldRenderer`, `LibraryClient`, `LibraryBrowser`; layout-source buttons: `uploadImageButton`, `refreshInputsButton`, `inputDropdown`, `generateFromImageButton`, `testLocalSampleButton`, `testServerSampleButton` |
| `BakePass` | `BakePass` | `WorldRenderer` |
| `Main Camera` | `WalkthroughController` | optional (all auto-resolve): `EditController`, `WorldRenderer`, main camera. Tunables: `walkSpeed` 1.4 m/s, `runSpeed` 3.0 m/s, `lookSens`, `stepOffset`, `slopeLimit`, `solidBackdrops` |

## The seven ScriptableObject assets

All live in `Assets/Resources/` and are **guarded** — read `.claude/rules/palette-assets.md`
before editing any of them. Keys are the string ids the LLM JSON and the editors use.

| Asset | Create menu | One entry per | Notes |
|---|---|---|---|
| **PrefabRegistry** | `Assets → Create → Prefab Registry` | `prefab_type` key in the LLM JSON | `PrefabRegistryExporter` syncs the key list into the LLM prompt. |
| **TerrainRegistry** | `Assets → Create → Terrain → TerrainRegistry` | `terrain_type` (`"grass"`, `"concrete"`, `"sand"`, …) | Each entry is a `TerrainLayer`; also feeds the Paint-ground brush. |
| **TileShapePalette** | `Assets → Create → CXR → TileShapePalette` | tile shape (`"square"`, `"wedge"`, `"quartercurve"`) | Prefabs are **generated** — see below. |
| **MaterialPalette** | `Assets → Create → CXR → MaterialPalette` | material id used in `faceMaterials` (`"brick_red"`, `"glass"`, `"roof_tar"`, …) | Not to be confused with ProBuilder's `Assets/Resources/Material Palette.asset` (with a space). |
| **PathMaterialPalette** | `Assets → Create → CXR → PathMaterialPalette` | path surface id (`path_material`; canonical: `"pavement_dark"`, `"pavement_light"`, `"brick"`, `"dirt"`, `"asphalt"`) | Used by the path tool and generation. Ships all five canonical ids plus the two tool-facing aliases `"street"` and `"sidewalk"`; several share one material, since the project has no separate brick or asphalt surface yet. |
| **FencePalette** | `Assets → Create → CXR → FencePalette` | fence type (`fence_type`; canonical: `"picket"`, `"lattice"`, `"chain_link"`, `"wood_privacy"`, `"wrought_iron"`) | Entry fields below. Ships all five canonical types plus the original `"Fence"` that existing saved runs reference; all six share one panel/post pair today and differ only in default height. Unknown type → warning, fence skipped (same as a missing path material). |
| **DecorPalette** | `Assets → Create → CXR → DecorPalette` | decor preset for the tile editor's Decorate tool (`"door"`, `"window"`, `"vent"`, …) | The prop analogue of MaterialPalette. Entry fields below; every field has a `[Tooltip]`. |

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
`fillsFace`) and renders via `WorldRenderer.RenderEmbeddedObjects`.

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
(a rounded-corner block) `[north,east,south,west,top,bottom,curve]`.
