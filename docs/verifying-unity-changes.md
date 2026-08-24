# Verifying Unity changes

## Running the unit tests

`Assets/Tests/EditMode/` (asmdef `EditModeTests`, references `CXRAuthoring` + NUnit + Newtonsoft,
`UNITY_INCLUDE_TESTS`). They cover the pure-logic assembly `Assets/Scripts/Authoring/` —
`BrushGeometry`, `DecorAlignment`, `DecorPlacement`, `FenceBuilder`, `FenceLinker`, `LayoutConverter`,
`PathGeometry`, `PathMesh`, `TileDeform`, `TileFaceSplitter`. Run them with **Window → General →
Test Runner → EditMode** in the editor, or the MCP `tests-run` tool (`mcp__ai-game-developer__tests-run`).
Put new pure logic in `Authoring/` (no MonoBehaviours) so it stays testable.

## Two MCP gotchas

- **MCP-driven play mode never ticks `Update`.** Setting `EditorApplication.isPlaying = true` through
  `script-execute` starts play mode, but the player loop stays frozen (`Time.frameCount` stuck at 2)
  when the Game View isn't repainting (editor window in the background); `QueuePlayerLoopUpdate()`
  does not unstick it. Coroutines partially complete, `MonoBehaviour.Update` never runs, so anything
  reading `Mouse.current` / `Keyboard.current` per frame can't be exercised. Interaction features
  (drag gestures, hotkeys, tool modes in `EditController`) need a human to press Play with the
  editor focused. Verify in two layers instead: (1) compile + symbol check — `assets-refresh`, then
  reflect over the type in `script-execute` to confirm new members exist; (2) unit-test the pure
  logic (math, hit-tests, data transforms) by building a throwaway rig in **edit mode** and invoking
  private methods via reflection — `EditController` is not `[ExecuteInEditMode]`, so `AddComponent`ing
  one in edit mode is side-effect free. Then hand the feel/visual check to the user explicitly.
- **`assets-refresh` times out when the edit triggers a domain reload** (the plugin connection dies
  mid-call and often needs re-enrolling). Unity still completes the import — confirm out-of-band via
  fresh `.cs.meta` files and the mtime on `Library/ScriptAssemblies/*.dll`. Don't retry the call.

## Offline compile check (editor / MCP unreachable)

An open editor locks the project for batchmode, so instead build a csc response file and run Unity's
bundled Roslyn. Unity 6000.3.10f1 lives at `C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Data`
(`<Data>` below).

```
<Data>\NetCoreRuntime\dotnet.exe <Data>\DotNetSdkRoslyn\csc.dll @compile.rsp
```

**Assembly-CSharp** (`compile.rsp`):

- Sources: all `Assets/**/*.cs` **excluding** `Editor|Tests|Authoring|Plugins` folders
  (`Assets/Scripts/Authoring` is its own asmdef `CXRAuthoring`; `Assets/Tests/EditMode` is
  `EditModeTests`) and **excluding `Assets/Materials/Variety/zzz/`** (those two scripts use
  `UnityEditor` / `AssetDatabase`).
- References: `Library\ScriptAssemblies\*.dll` (exclude `Assembly-CSharp*`, `EditModeTests`),
  `<Data>\Managed\UnityEngine\UnityEngine*.dll`, `<Data>\NetStandard\ref\2.1.0\netstandard.dll`,
  and Newtonsoft from `Library\PackageCache\com.unity.nuget.newtonsoft-json@*\Runtime\Newtonsoft.Json.dll`.
- Flags: `-t:library -nowarn:0169,0414,0649,1701,1702`; do **not** define `UNITY_EDITOR`.
- Exit 0 with only the pre-existing CS0618/CS0108 warnings = clean (the standing CS0108 is
  `TileBuildingEditor.DestroyObject` hiding `Object.DestroyObject`).

**Assembly-CSharp-Editor** (`Assets/Editor/**`): same recipe, plus `-r:"<Data>\Managed\UnityEditor.dll"`
and `-define:UNITY_EDITOR`; exclude only `Assembly-CSharp-Editor.dll` / `EditModeTests.dll` from the
ScriptAssemblies references (keep `Assembly-CSharp.dll` — editor code references runtime types).

Gotchas:

- In Git Bash `$(pwd)` yields `/c/...`, which csc reads as a relative path (CS2001). Use `$(pwd -W)`
  for Windows-style paths in the response file.
- `strings` isn't installed; `grep -c <TypeName> <dll>` checks a type compiled in, and
  `tr -d '\000' < dll | grep -o -a '<menu path>'` reads the UTF-16 `#US` heap (e.g. to confirm a
  `[MenuItem]` path made it into the assembly).
