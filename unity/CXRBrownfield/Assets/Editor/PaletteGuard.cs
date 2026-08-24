using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Editor-only safety net for the seven CXR registry / palette ScriptableObjects, which have silently
// lost entries before (a DecorPalette entry was overwritten in place at its list slot rather than a new
// one being appended). Three defences, none of which touch a runtime script:
//
//   PaletteAutoSave  — Inspector / MCP edits sit DIRTY IN MEMORY until something calls SaveAssets. In
//                      this project AssetDatabase.Refresh + domain reloads fire constantly, and a
//                      reimport of a dirty asset discards the in-memory edits. Flushes within ~1 s.
//   PaletteSnapshots — copies the raw .asset YAML aside on every write, whoever wrote it, so any loss
//                      is one menu command away from being undone.
//   the tripwire     — an entry count that DROPS logs a loud error and pins an unprunable RECOVER copy.
//
// NOTHING here writes an .asset except AssetDatabase.SaveAssetIfDirty (exactly the bytes Unity writes
// on Ctrl+S). Restoring is a separate, dialog-confirmed menu command in PaletteValidator.cs.
public static class PaletteGuard
{
    // Resolved by PATH, never by AssetDatabase.FindAssets("t:MaterialPalette") — that filter also
    // matches Assets/Resources/Material Palette.asset (note the space), which is ProBuilder's
    // UnityEditor.ProBuilder.MaterialPalette living in the very same folder.
    public static readonly string[] Paths =
    {
        "Assets/Resources/PrefabRegistry.asset",
        "Assets/Resources/TerrainRegistry.asset",
        "Assets/Resources/TileShapePalette.asset",
        "Assets/Resources/MaterialPalette.asset",
        "Assets/Resources/PathMaterialPalette.asset",
        "Assets/Resources/FencePalette.asset",
        "Assets/Resources/DecorPalette.asset",
    };

    public static bool IsGuardedPath(string path)
    {
        return Paths.Any(g => string.Equals(g, path, StringComparison.OrdinalIgnoreCase));
    }

    public static ScriptableObject Load(string path)
    {
        return AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
    }

    // Type-agnostic entry count — all seven expose `public List<Entry> entries`, so one
    // SerializedProperty walk covers every palette without a per-type switch. -1 = unknown.
    public static int EntryCount(UnityEngine.Object obj)
    {
        if (obj == null) return -1;
        var entries = new SerializedObject(obj).FindProperty("entries");
        return entries != null && entries.isArray ? entries.arraySize : -1;
    }
}

// Flushes dirty palettes to disk so an edit can never be lost to a crash, a domain reload, or an
// AssetDatabase.Refresh that reimports the file out from under the in-memory version.
//
// Deliberately NOT an OnValidate() on the seven ScriptableObject classes: OnValidate fires during
// deserialization/import, so saving from it means writing an asset from inside the import pipeline.
// Polling also catches edits from EVERY route — Inspector, MCP assets-modify, script-execute —
// which Undo.postprocessModifications would not.
[InitializeOnLoad]
internal static class PaletteAutoSave
{
    private const double IntervalSeconds = 1.0;
    private static double _nextTick;

    static PaletteAutoSave()
    {
        EditorApplication.update                  += Tick;
        AssemblyReloadEvents.beforeAssemblyReload += Flush;
    }

    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup < _nextTick) return;
        _nextTick = EditorApplication.timeSinceStartup + IntervalSeconds;
        Flush();
    }

    private static void Flush()
    {
        // Never re-enter the AssetDatabase mid-import or mid-compile.
        if (EditorApplication.isUpdating || EditorApplication.isCompiling ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;

        foreach (var path in PaletteGuard.Paths)
        {
            var obj = PaletteGuard.Load(path);
            if (obj == null || !EditorUtility.IsDirty(obj)) continue;   // IsDirty so the log is quiet
            AssetDatabase.SaveAssetIfDirty(obj);
            Debug.Log($"[PaletteGuard] auto-saved {path}", obj);
        }
    }
}

// Fires AFTER the bytes land on disk, whoever wrote them: the Inspector, PaletteAutoSave, MCP
// assets-modify, a raw file write + AssetDatabase.Refresh, or a git checkout. A pure observer.
internal sealed class PaletteSnapshotPostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(string[] imported, string[] deleted,
                                               string[] moved,    string[] movedFrom)
    {
        foreach (var path in imported)
            if (PaletteGuard.IsGuardedPath(path)) PaletteSnapshots.Capture(path);

        foreach (var path in deleted)
            if (PaletteGuard.IsGuardedPath(path))
                Debug.LogError($"[PaletteGuard] GUARDED ASSET DELETED: {path}\n" +
                               $"  Snapshots are at: {PaletteSnapshots.Root}");
    }
}

// Snapshots the RAW .asset YAML, not an EditorJsonUtility dump: JSON serializes object references as
// instanceIDs, which are not stable across sessions, so restoring would need a hand-rolled GUID
// resolver. The .asset is already plain text with GUID+fileID references (m_SerializationMode: 2), so
// a byte copy restores exactly, is greppable, and has no custom serialization to get wrong.
public static class PaletteSnapshots
{
    public const int KeepPerAsset = 40;

    private const string RecoverPrefix = "RECOVER_";
    private const string EntriesSuffix = "entries";

    // Outside Assets/ on purpose: no .meta, no import, no AssetDatabase churn. Gitignored.
    public static string Root
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PaletteSnapshots")); }
    }

    public static void Capture(string assetPath)
    {
        var fullPath = Path.GetFullPath(assetPath);
        if (!File.Exists(fullPath)) return;

        var name = Path.GetFileNameWithoutExtension(assetPath);      // "DecorPalette"
        var dir  = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);

        var bytes = File.ReadAllBytes(fullPath);

        // RECOVER_ copies are pinned evidence of a past loss — never the baseline, never pruned.
        var prior = new DirectoryInfo(dir).GetFiles("*.yaml")
                        .Where(f => !f.Name.StartsWith(RecoverPrefix, StringComparison.Ordinal))
                        .OrderByDescending(f => f.Name)
                        .ToList();

        // A plain Refresh reimports without changing the bytes — don't snapshot that.
        if (prior.Count > 0 && File.ReadAllBytes(prior[0].FullName).SequenceEqual(bytes)) return;

        var count    = PaletteGuard.EntryCount(PaletteGuard.Load(assetPath));
        var previous = prior.Count > 0 ? ParseCount(prior[0].Name) : -1;
        var stamp    = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        File.WriteAllBytes(Path.Combine(dir, $"{stamp}_{count:D3}{EntriesSuffix}.yaml"), bytes);

        // The tripwire lives here because this is the one place both counts are known.
        if (previous >= 0 && count >= 0 && count < previous)
        {
            var rescue = Path.Combine(dir, $"{RecoverPrefix}{stamp}_{previous:D3}{EntriesSuffix}.yaml");
            File.Copy(prior[0].FullName, rescue, true);
            Debug.LogError(
                $"[PaletteGuard] {name}: entry count DROPPED {previous} -> {count}.\n" +
                $"  Last good copy kept at: {rescue}\n" +
                $"  Restore via  Tools > CXR > Palettes > Restore from Snapshot…",
                PaletteGuard.Load(assetPath));
        }

        foreach (var old in prior.Skip(KeepPerAsset - 1)) old.Delete();
    }

    // "20260813-134812_006entries.yaml" -> 6
    private static int ParseCount(string fileName)
    {
        var start = fileName.IndexOf('_');
        var end   = fileName.IndexOf(EntriesSuffix, StringComparison.Ordinal);
        if (start < 0 || end <= start) return -1;
        return int.TryParse(fileName.Substring(start + 1, end - start - 1), out var n) ? n : -1;
    }
}
