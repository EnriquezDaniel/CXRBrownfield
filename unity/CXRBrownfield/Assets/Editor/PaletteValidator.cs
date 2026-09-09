using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// The user-facing half of the palette safety net (the automatic half is PaletteGuard.cs).
//
// Validate is READ-ONLY: it never calls SetDirty or ApplyModifiedProperties, and it deliberately has
// no auto-fix. Silently rewriting entry data is the disease, not the cure — e.g. "repairing"
// widthFraction 0 -> 0.8 would be a real mutation, and TileBuildingEditor already falls back to
// DEFAULT_DECOR_FRACTION when it reads a 0.
//
// Restore IS destructive, by design and by hand: it is dialog-confirmed, shows both entry counts,
// warns about unsaved in-memory edits, and snapshots the current file before overwriting it.
public static class PaletteValidator
{
    // Per-asset key field + object references that must not be null. Taken from the class
    // declarations in Assets/Scripts/. FencePalette.postPrefab is optional by design, so it is
    // absent here; DecorPalette has no direct object reference (prefabKey is cross-checked instead).
    private struct Spec
    {
        public string   Key;
        public string[] Refs;
    }

    private static readonly Dictionary<string, Spec> Specs = new Dictionary<string, Spec>
    {
        { "PrefabRegistry",      new Spec { Key = "key",        Refs = new[] { "prefab" } } },
        { "TerrainRegistry",     new Spec { Key = "key",        Refs = new[] { "terrainLayer" } } },
        { "TileShapePalette",    new Spec { Key = "shapeId",    Refs = new[] { "prefab" } } },
        { "MaterialPalette",     new Spec { Key = "materialId", Refs = new[] { "material" } } },
        { "PathMaterialPalette", new Spec { Key = "id",         Refs = new[] { "material" } } },
        { "FencePalette",        new Spec { Key = "fenceType",  Refs = new[] { "panelPrefab" } } },
        { "DecorPalette",        new Spec { Key = "decorId",    Refs = new string[0] } },
        { "WaterPalette",        new Spec { Key = "id",         Refs = new[] { "material" } } },
        // wallMaterialId is a string id into MaterialPalette, cross-checked below like DecorPalette's prefabKey.
        { "BuildingStylePalette", new Spec { Key = "styleId",   Refs = new string[0] } },
    };

    // Not wired into OnValidate: it would spam the console on every import and domain reload, and it
    // needs PrefabRegistry loaded, which isn't guaranteed mid-import. The menu command plus
    // PaletteSnapshots' automatic count-drop tripwire covers the failure mode that actually happens.
    [MenuItem("Tools/CXR/Palettes/Validate", priority = 20)]
    public static void Validate()
    {
        var errors        = 0;
        var registryKeys  = RegistryKeys();
        var materialIds   = MaterialIds();

        foreach (var path in PaletteGuard.Paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var obj  = PaletteGuard.Load(path);
            if (obj == null)
            {
                Debug.LogError($"[PaletteValidator] MISSING ASSET: {path}");
                errors++;
                continue;
            }

            var spec    = Specs[name];
            var entries = new SerializedObject(obj).FindProperty("entries");
            if (entries == null || !entries.isArray)
            {
                Debug.LogError($"[PaletteValidator] {name}: no 'entries' array.", obj);
                errors++;
                continue;
            }
            if (entries.arraySize == 0)
            {
                Debug.LogError($"[PaletteValidator] {name}: entries is EMPTY.", obj);
                errors++;
                continue;
            }

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < entries.arraySize; i++)
            {
                var entry = entries.GetArrayElementAtIndex(i);
                var key   = ReadString(entry, spec.Key);

                if (string.IsNullOrWhiteSpace(key))
                {
                    Debug.LogError($"[PaletteValidator] {name}[{i}]: blank '{spec.Key}'.", obj);
                    errors++;
                }
                else if (seen.TryGetValue(key, out var first))
                {
                    Debug.LogError($"[PaletteValidator] {name}[{i}]: duplicate {spec.Key} '{key}' " +
                                   $"(first seen at [{first}]).", obj);
                    errors++;
                }
                else
                {
                    seen[key] = i;
                }

                foreach (var refName in spec.Refs)
                {
                    if (entry.FindPropertyRelative(refName)?.objectReferenceValue != null) continue;
                    Debug.LogError($"[PaletteValidator] {name}[{i}] '{key}': '{refName}' is NULL.", obj);
                    errors++;
                }

                if (name == "TileShapePalette")
                    CheckTileShape(entry, i, key, obj);

                if (name == "DecorPalette")
                {
                    // A duplicate prefabKey is LEGAL here (a tall and a short window share one prefab),
                    // so only presence and resolution against the registry are checked.
                    var prefabKey = ReadString(entry, "prefabKey");
                    if (string.IsNullOrWhiteSpace(prefabKey))
                    {
                        Debug.LogError($"[PaletteValidator] DecorPalette[{i}] '{key}': prefabKey is BLANK.", obj);
                        errors++;
                    }
                    else if (registryKeys != null && !registryKeys.Contains(prefabKey))
                    {
                        Debug.LogError($"[PaletteValidator] DecorPalette[{i}] '{key}': prefabKey " +
                                       $"'{prefabKey}' is not in PrefabRegistry.", obj);
                        errors++;
                    }
                }

                if (name == "BuildingStylePalette")
                {
                    // The letter must be one the prompt and BuildingStyles know, and its material id
                    // must resolve in MaterialPalette or the style silently renders as default.
                    if (BuildingStyles.Normalize(key) == null)
                    {
                        Debug.LogError($"[PaletteValidator] BuildingStylePalette[{i}]: styleId '{key}' " +
                                       "is not one of A to F.", obj);
                        errors++;
                    }
                    var wallId = ReadString(entry, "wallMaterialId");
                    if (string.IsNullOrWhiteSpace(wallId))
                    {
                        Debug.LogError($"[PaletteValidator] BuildingStylePalette[{i}] '{key}': wallMaterialId is BLANK.", obj);
                        errors++;
                    }
                    else if (materialIds != null && !materialIds.Contains(wallId))
                    {
                        Debug.LogError($"[PaletteValidator] BuildingStylePalette[{i}] '{key}': wallMaterialId " +
                                       $"'{wallId}' is not in MaterialPalette.", obj);
                        errors++;
                    }
                }
            }

            Debug.Log($"[PaletteValidator] {name}: {entries.arraySize} entries checked.", obj);
        }

        Debug.Log(errors == 0
            ? "[PaletteValidator] OK — no problems found."
            : $"[PaletteValidator] {errors} problem(s) found. See the errors above.");
    }

    [MenuItem("Tools/CXR/Palettes/Snapshot Now", priority = 21)]
    private static void SnapshotNow()
    {
        foreach (var path in PaletteGuard.Paths) PaletteSnapshots.Capture(path);
        Debug.Log($"[PaletteGuard] snapshots written to {PaletteSnapshots.Root}");
    }

    [MenuItem("Tools/CXR/Palettes/Open Snapshot Folder", priority = 22)]
    private static void OpenSnapshotFolder()
    {
        Directory.CreateDirectory(PaletteSnapshots.Root);
        EditorUtility.RevealInFinder(PaletteSnapshots.Root);
    }

    [MenuItem("Tools/CXR/Palettes/Restore from Snapshot…", priority = 40)]
    private static void RestoreFromSnapshot()
    {
        var source = EditorUtility.OpenFilePanel("Restore palette snapshot", PaletteSnapshots.Root, "yaml");
        if (string.IsNullOrEmpty(source)) return;

        // ".../PaletteSnapshots/DecorPalette/20260813-134812_006entries.yaml" -> "DecorPalette"
        var name   = new DirectoryInfo(Path.GetDirectoryName(source)).Name;
        var target = PaletteGuard.Paths.FirstOrDefault(
            p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            EditorUtility.DisplayDialog("Restore palette snapshot",
                $"'{name}' is not one of the guarded palettes.\n\nPick a file from inside one of the " +
                $"per-palette folders under:\n{PaletteSnapshots.Root}", "OK");
            return;
        }

        var obj     = PaletteGuard.Load(target);
        var current = PaletteGuard.EntryCount(obj);
        var isDirty = obj != null && EditorUtility.IsDirty(obj);

        var confirmed = EditorUtility.DisplayDialog("Restore palette snapshot",
            $"Overwrite\n    {target}   ({current} entries)\nwith\n    {Path.GetFileName(source)}" +
            (isDirty ? "\n\nWARNING: this asset has UNSAVED in-memory edits. They will be lost." : "") +
            "\n\nThe current file is snapshotted first, so this is undoable.",
            "Restore", "Cancel");
        if (!confirmed) return;

        PaletteSnapshots.Capture(target);                      // back up what we are about to clobber
        File.Copy(source, Path.GetFullPath(target), true);
        AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceUpdate);
        Debug.Log($"[PaletteGuard] restored {target} from {source}", PaletteGuard.Load(target));
    }

    // Per-face painting contract (TileSpawner.ApplyFaceMaterial / TileBuildingEditor.FaceFromHit): the
    // prefab must expose one material slot AND one submesh per faceNames entry, plus a MeshCollider so
    // a click resolves to the exact submesh (the only way to reach non-axis faces like "curve"). The
    // square/wedge/curve prefabs once shipped with a single slot, so only faceNames[0] could render —
    // this is the check that would have caught it. Read-only; the fix is
    // Tools → CXR → Tiles → Rebuild per-face tile prefabs.
    private static void CheckTileShape(SerializedProperty entry, int i, string key, UnityEngine.Object obj)
    {
        var faceNames = entry.FindPropertyRelative("faceNames");
        int faces = faceNames != null ? faceNames.arraySize : 0;
        if (faces == 0)
        {
            Debug.LogWarning($"[PaletteValidator] TileShapePalette[{i}] '{key}': " +
                             "faceNames is empty — face materials can't be resolved.", obj);
            return;
        }

        var prefab = entry.FindPropertyRelative("prefab")?.objectReferenceValue as GameObject;
        if (prefab == null) return;   // the null-ref error is already reported by the caller

        var renderer = prefab.GetComponentInChildren<Renderer>(true);
        var filter   = prefab.GetComponentInChildren<MeshFilter>(true);
        var skinned  = renderer as SkinnedMeshRenderer;
        Mesh mesh    = filter != null ? filter.sharedMesh : skinned != null ? skinned.sharedMesh : null;
        int slots    = renderer != null ? renderer.sharedMaterials.Length : 0;
        const string fix = " Run Tools → CXR → Tiles → Rebuild per-face tile prefabs.";

        if (slots != faces)
            Debug.LogWarning($"[PaletteValidator] TileShapePalette[{i}] '{key}': faceNames has {faces} " +
                             $"name(s) but the prefab renderer has {slots} material slot(s) — only faces " +
                             $"with a slot can be painted per-face.{fix}", obj);
        if (mesh == null)
            Debug.LogWarning($"[PaletteValidator] TileShapePalette[{i}] '{key}': prefab mesh is null on " +
                             $"disk (ProBuilder rebuilds at load) — submesh count can't be verified.{fix}", obj);
        else if (mesh.subMeshCount != faces)
            Debug.LogWarning($"[PaletteValidator] TileShapePalette[{i}] '{key}': faceNames has {faces} " +
                             $"name(s) but the mesh has {mesh.subMeshCount} submesh(es).{fix}", obj);
        if (prefab.GetComponentInChildren<MeshCollider>(true) == null)
            Debug.LogWarning($"[PaletteValidator] TileShapePalette[{i}] '{key}': no MeshCollider — clicks " +
                             $"fall back to the surface normal, so only axis-aligned faces can be detected.{fix}", obj);

        // Sub-cell fit (TileFit): extents are cell fractions in (0, 1]; anchors are -1..1 per axis.
        var extents = entry.FindPropertyRelative("cellExtents");
        var anchor  = entry.FindPropertyRelative("cellAnchor");
        if (extents != null)
        {
            Vector3 e = extents.vector3Value;
            if (e.x > 1f || e.y > 1f || e.z > 1f)
                Debug.LogWarning($"[PaletteValidator] TileShapePalette[{i}] '{key}': cellExtents {e} has a " +
                                 "component above 1 — the shape would overflow its cell.", obj);
        }
        if (anchor != null)
        {
            Vector3 a = anchor.vector3Value;
            if (Mathf.Abs(a.x) > 1f || Mathf.Abs(a.y) > 1f || Mathf.Abs(a.z) > 1f)
                Debug.LogWarning($"[PaletteValidator] TileShapePalette[{i}] '{key}': cellAnchor {a} is outside " +
                                 "-1..1 — the shape would sit outside its cell.", obj);
        }
    }

    private static string ReadString(SerializedProperty entry, string field)
    {
        return entry.FindPropertyRelative(field)?.stringValue;
    }

    // Material ids of the CXR MaterialPalette, by path (PaletteGuard.Paths) so ProBuilder's
    // "Material Palette.asset" in the same folder is never picked up.
    private static HashSet<string> MaterialIds()
    {
        var palette = PaletteGuard.Load("Assets/Resources/MaterialPalette.asset");
        if (palette == null) return null;

        var ids     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new SerializedObject(palette).FindProperty("entries");
        for (var i = 0; entries != null && i < entries.arraySize; i++)
            ids.Add(entries.GetArrayElementAtIndex(i).FindPropertyRelative("materialId")?.stringValue ?? "");
        return ids;
    }

    private static HashSet<string> RegistryKeys()
    {
        var registry = PaletteGuard.Load("Assets/Resources/PrefabRegistry.asset");
        if (registry == null) return null;

        var keys    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new SerializedObject(registry).FindProperty("entries");
        for (var i = 0; entries != null && i < entries.arraySize; i++)
            keys.Add(entries.GetArrayElementAtIndex(i).FindPropertyRelative("key")?.stringValue ?? "");
        return keys;
    }
}
