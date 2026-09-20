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
(dims backdrops). Every published env renders with its own ground, as in the editor
(`docs/editing-controls.md`, "Multiple environments"); the viewer's `WorldRenderer` sets
**Env Alphamap Resolution** to 512 to keep each ground copy light on the headset. Falls back to the single-env contract if the payload has no
`loaded` list. Strictly fetch-and-render: no editing, no undo.

**Optional content.** `SyncClient.skipOptional` (default on, Inspector-tunable) calls
`WorldRenderer.SetSkipOptional(true)` before the first render, so objects, building instances and
face decor the author marked `optional` are never spawned on the viewer: no GameObjects, colliders
or draw calls. A mark on the host bumps the env version through the normal save / Live-share path
and the viewer re-fetches and re-renders with the gate applied. Untick it on a strong PCVR rig to
render everything. The host previews the same result with the library's **Low detail** toggle
(details in [editing-controls.md](editing-controls.md#optional-content-vr-detail-level)).

## Scene `Assets/Scenes/VRViewer.unity`

A duplicate of `BasicModel` with the editing components disabled (`LibraryBrowser`,
`TileBuildingEditor`, `BakePass`, `ModelRequester`, `Canvas`). `GameManager` keeps `LibraryClient` +
`WorldRenderer` + `SyncClient` (wired to both). `WorldRenderer.fencePalette` is unwired here and
resolves from `Resources`, like the water and building-style palettes. The legacy Main Camera is
disabled; the camera is the **XR Origin (VR)** rig:

```
XR Origin (VR)   XROrigin · XRBootstrap · CharacterController · VRLocomotion
  Camera Offset
    Main Camera  Camera · TrackedPoseDriver · VRStatusText
```

No XR Interaction Toolkit rig, interactors or action assets: `VRLocomotion` reads the controllers
through inline Input System actions bound by usage.

### Moving (`VRLocomotion`, math in `Authoring/VRLocomotionMath`)

| Control | Does |
|---|---|
| Left stick | Walk, relative to where you look. Click the stick to jog. Walls are solid, slopes and steps are followed (`CharacterController` kept under the head, `WalkKinematics`) |
| Right stick sideways | Snap turn 45 degrees about your head |
| Right stick forward, then let go | Teleport ray from the right hand. Green lands, red does not. Any surface facing up within 45 degrees counts: ground, paths, floors, roofs. Water is passed through |

The first time the active env renders, `SyncClient` stands you at the **north-east corner of the
env's first Site plot** (`EnvironmentDef.sites[0]`: the boundary point nearest max X / min Z, moved
2 m towards the plot's centroid), looking south (-X), on the terrain. An env with no plot uses the
north-east corner of its ground rect. Later syncs leave you where you are.
`SyncClient` also makes backdrop envs solid (`SetBackdropCollidersSolid`), since the viewer walks
through all of them.

### Status text (`VRStatusText`)

IMGUI and overlay canvases never reach the headset, so the old `OnGUI` status line is desktop-mirror
only. `VRStatusText` shows the server address and `SyncClient.Status` 1.5 m in front of the head
until the first env is on screen, then hides. Poll errors are also logged once per distinct error
(`adb logcat -s Unity`), and only one `/api/active` request is outstanding at a time.

### Server address on a headset

On the headset `localhost` is the headset. **Build → VR — Quest** bakes the address into the APK: it
writes `Assets/Resources/ViewerServerUrl.txt` (gitignored) with `http://<this PC's LAN IPv4>:5002`,
builds, and deletes the file, so no other build contains it. `LibraryClient.Awake` uses it when
present. To force another address (a hotspot, another host) set the EditorPrefs string
`CXR.ViewerServerUrl`. The PC's address changing means a rebuild. Player Settings allow plain
`http://` (`insecureHttpOption: AlwaysAllowed`) and force the Android INTERNET permission; without
those the LAN address is refused.

A viewer build reads the record with the data types it was built with. Building signs moved into
the `BuildingInstance.signs` list, so an APK built before that change shows no signs on places saved
after it. Rebuild the viewer to see them.

### Running it on a Quest

Android package id `com.IRL.brownfield` (the URP template id collided with another sideloaded app).
`adb` ships with the editor: `<Editor>/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe`.

1. Start the server, then **Build → VR — Quest**.
2. `adb install -r Builds/VR-Quest/CXR-VR.apk`
3. Launch **CXRBrownfield** from App Library → Unknown Sources. It starts with or without the
   Touch controllers: `Assets/Editor/QuestHandsOptionalManifest.cs` declares hand tracking as
   optional in the manifest, which is what stops the Quest's "Controllers required" dialog. No
   hand input is read, so without controllers you can only look around from the spawn point;
   walking, turning and teleport still need them.
4. Logs: `adb logcat -G 16M` once (the default buffer wraps within a minute), then
   `adb logcat -s Unity`. Look for `[LibraryClient] Server address from the build`,
   `[XRBootstrap] XR started`, `[SyncClient] Rendered`, `[VRLocomotion] Placed at`.

**Networks that isolate devices** (campus or managed networks; seen here with the PC on
`100.110.28.0/22` and the Quest on `100.110.64.0/22`, all traffic between them dropped): the LAN
address cannot work. Use USB instead: set EditorPrefs `CXR.ViewerServerUrl` to
`http://localhost:5002`, build, and run `adb reverse tcp:5002 tcp:5002` after each reconnect. A
Windows mobile hotspot (`http://192.168.137.1:5002`) is the untethered alternative. Test the route
from the headset before blaming the app:
`adb shell 'printf "GET /health HTTP/1.0\r\n\r\n" | toybox nc -w 4 <host> 5002; echo $?'`
(0 connects, 1 times out).

Verified on a Quest 3 (2026-09-19, over `adb reverse`): immersive launch, world scale and floor
height, render of the published env, spawn at the first Site plot's north-east corner, stick
walking with solid walls, snap turn, teleport, controller markers, and the status text with the
server unreachable. Not checked on device: roof teleport and stepping off, live re-sync while the
host edits, recovery of a running viewer when the server comes back, frame rate. In flat play mode
the viewer renders the published env and re-syncs on switch within the poll interval.

## VR is opt-in and scene-driven — the desktop build is never VR

XR Plug-in Management has **"Initialize XR on Startup" OFF** for every target. Only `VRViewer`
carries **`XRBootstrap`** (`Assets/Scripts/XRBootstrap.cs`), which manually inits the XR loader on
`Start` and stops it on teardown; with no headset/loader it logs a warning and runs flat. So
`BasicModel` (and any other scene) is a normal PC app. Quest (Android) is set up and verified on
device. PCVR (Windows) is not: the Standalone target has no loader and no interaction profile yet.

## Builds (`Build` menu, `Assets/Editor/BuildMenu.cs`)

Each bakes in its own scene + target, so the builds stay separate regardless of the shared Build
Settings list (which stays **BasicModel-only = the default PC build**):

| Menu item | Ships | Notes |
|---|---|---|
| **Build → Desktop (PC, Windows)** (`Ctrl+Shift+D`) | `BasicModel` only | No VR |
| **Build → VR — Quest (Android)** (`Ctrl+Shift+Q`) | `VRViewer` only | OpenXR for Android with **Meta Quest Support** and the Oculus Touch, Touch Plus and Touch Pro profiles. Bakes the server address (above). Leaves the editor on the Android platform |
| **Build → VR — PCVR (Windows)** | `VRViewer` only | **Requires** enabling OpenXR for the *Standalone* target in XR Plug-in Management first |

Remaining headset setup (in-editor, needs the device): for PCVR, enable **OpenXR** for the Standalone
target and add an interaction profile (Oculus Touch). Packages installed: `com.unity.xr.openxr`,
`com.unity.xr.interaction.toolkit`, `com.unity.xr.management`.
