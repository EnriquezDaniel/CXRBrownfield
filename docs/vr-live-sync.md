# VR walkthrough and live multi-client sync

A VR headset (or a second PC) mirrors the desktop host's **full loaded set** — the active env plus
any backdrop envs — in real time. The desktop host is the **administrator** (only it can add /
delete / edit; viewers are strictly read-only). Model: **publish → poll → re-render**. The host
publishes its loaded set; viewers poll and rebuild idempotently via `WorldRenderer.RenderEnvironment`.
No deltas, no conflict resolution.

## Server contract

One shared pointer file `server/data/active.json` (`{ envId, loadedIds }`):

- `GET /api/active` → `{ envId, version, name, updatedAt, loaded: [{envId, version, name, updatedAt}] }`.
  `loaded` is every published env; each `version` is read **live** from its env record, so a poller
  detects loads/closes (id list changes), an active switch (`envId` changes), and edits (version bump
  after the host PUTs). Archived/deleted ids are silently dropped (self-healing).
- `POST /api/active` `{ "envId": "<active id>", "loadedIds": [...] }` → publish. `loadedIds` is
  optional (defaults to just the active env — the old single-env contract); null/empty clears the
  pointer.

## Desktop host (`LibraryBrowser`)

A **Live** toggle in the Library header. When on, `PublishLive` posts the loaded set (persisted envs
only; never-saved envs are skipped until first Save) on every activate/load/close/save, and every
`MarkDirty` schedules a **debounced auto-save** (`AutoSaveDebounce`, ~1 s) so a PUT bumps the version
and viewers pick it up. Off = manual-save behavior. Only the active env is editable; backdrops are
shared as-is.

## Viewer (`SyncClient`, `Assets/Scripts/SyncClient.cs`)

Polls `GET /api/active` every `pollIntervalSeconds` (~1.5 s) and diffs the published set against what
it rendered (`_appliedVersions`): envs that dropped out are unloaded (`UnloadEnvironment`); new or
version-bumped envs are fetched and re-rendered (the env's building defs are evicted from the cache
first so edited buildings refresh); the host's active env is applied via `SetActiveEnvironment`
(paints terrain, dims backdrops). Falls back to the single-env contract if the payload has no
`loaded` list. Strictly fetch-and-render: no editing, no undo.

## Scene `Assets/Scenes/VRViewer.unity`

A duplicate of `BasicModel` (keeps all `WorldRenderer` registry wiring) with the editing components
disabled (`LibraryBrowser`, `TileBuildingEditor`, `BakePass`, `ModelRequester`, `Canvas`).
`GameManager` keeps `LibraryClient` + `WorldRenderer` + `SyncClient` (wired to both). An
**XR Origin (VR)** rig (OpenXR + XR Interaction Toolkit) is the camera; the legacy Main Camera is
disabled. On a headset, point `LibraryClient.serverBaseUrl` at the host PC's LAN IP (not localhost).
Verified in flat play mode: the viewer renders the published env and re-syncs on switch within the
poll interval.

## VR is opt-in and scene-driven — the desktop build is never VR

XR Plug-in Management has **"Initialize XR on Startup" OFF** for every target. Only `VRViewer`
carries **`XRBootstrap`** (`Assets/Scripts/XRBootstrap.cs`), which manually inits the XR loader on
`Start` and stops it on teardown; with no headset/loader it logs a warning and runs flat. So
`BasicModel` (and any other scene) is a normal PC app. Works for PCVR (Windows) and Quest (Android),
whichever OpenXR loader is assigned.

## Builds (`Build` menu, `Assets/Editor/BuildMenu.cs`)

Each bakes in its own scene + target, so the builds stay separate regardless of the shared Build
Settings list (which stays **BasicModel-only = the default PC build**):

| Menu item | Ships | Notes |
|---|---|---|
| **Build → Desktop (PC, Windows)** (`Ctrl+Shift+D`) | `BasicModel` only | No VR |
| **Build → VR — Quest (Android)** (`Ctrl+Shift+Q`) | `VRViewer` only | OpenXR already enabled for the Android target |
| **Build → VR — PCVR (Windows)** | `VRViewer` only | **Requires** enabling OpenXR for the *Standalone* target in XR Plug-in Management first |

Remaining headset setup (in-editor, needs the device): for PCVR, enable **OpenXR** for the Standalone
target and add an interaction profile (Oculus Touch). Packages installed: `com.unity.xr.openxr`,
`com.unity.xr.interaction.toolkit`, `com.unity.xr.management`.
