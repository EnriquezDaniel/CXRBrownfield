using System;
using System.Collections.Generic;

// Rules for "optional" content: items an author marks as skippable so a low-performance client
// (the VR viewer) can leave them out. Shared by WorldRenderer (the render gate), EditController
// and LibraryBrowser (bulk marks) and TileBuildingEditor (face decor). Pure, so EditMode tests
// cover it. The flag itself lives on ObjectInstance, BuildingInstance and EmbeddedObjectDef.
public static class OptionalContent
{
    // The single render predicate. included=false always hides; optional only hides when the
    // renderer is in skip mode. Embedded decor has no `included` and passes true.
    public static bool ShouldRender(bool included, bool optional, bool skipOptional)
        => included && !(optional && skipOptional);

    // ---- Environment instances ----

    // Sets `optional` on the listed instances. Returns how many actually changed.
    public static int SetOptional(EnvironmentDef env, IEnumerable<(string id, bool isBuilding)> targets, bool optional)
    {
        if (env == null || targets == null) return 0;
        int changed = 0;
        foreach (var (id, isBuilding) in targets)
        {
            if (isBuilding)
            {
                var bi = FindBuilding(env, id);
                if (bi != null && bi.optional != optional) { bi.optional = optional; changed++; }
            }
            else
            {
                var oi = FindObject(env, id);
                if (oi != null && oi.optional != optional) { oi.optional = optional; changed++; }
            }
        }
        return changed;
    }

    // True when at least one listed instance does not already carry `optional`. Drives the
    // button label and lets callers skip a no-op undo entry.
    public static bool AnyDiffers(EnvironmentDef env, IEnumerable<(string id, bool isBuilding)> targets, bool optional)
    {
        if (env == null || targets == null) return false;
        foreach (var (id, isBuilding) in targets)
        {
            if (isBuilding) { var bi = FindBuilding(env, id); if (bi != null && bi.optional != optional) return true; }
            else            { var oi = FindObject(env, id);   if (oi != null && oi.optional != optional) return true; }
        }
        return false;
    }

    // Every ObjectInstance whose prefabType matches (case-insensitive). Returns the changed count.
    public static int SetByPrefabType(EnvironmentDef env, string prefabType, bool optional)
    {
        if (env?.objectInstances == null || string.IsNullOrEmpty(prefabType)) return 0;
        int changed = 0;
        foreach (var oi in env.objectInstances)
            if (oi != null && SameType(oi.prefabType, prefabType) && oi.optional != optional)
            { oi.optional = optional; changed++; }
        return changed;
    }

    public static bool AnyDiffersByPrefabType(EnvironmentDef env, string prefabType, bool optional)
    {
        if (env?.objectInstances == null || string.IsNullOrEmpty(prefabType)) return false;
        foreach (var oi in env.objectInstances)
            if (oi != null && SameType(oi.prefabType, prefabType) && oi.optional != optional) return true;
        return false;
    }

    // ---- Face decor (BuildingDef.embeddedObjects) ----

    // Decor hosted on one tile face: hostGridX/Z/Floor + hostFace, the same match the tile editor
    // uses to find conflicting decor. Legacy decor with hostFace == null never matches; it can
    // still be toggled by clicking it directly.
    public static List<EmbeddedObjectDef> DecorOnFace(BuildingDef bdef, int gx, int gz, int floor, string face)
    {
        var list = new List<EmbeddedObjectDef>();
        if (bdef?.embeddedObjects == null || string.IsNullOrEmpty(face)) return list;
        foreach (var e in bdef.embeddedObjects)
        {
            if (e == null || e.hostFace == null) continue;
            if (e.hostGridX == gx && e.hostGridZ == gz && e.hostFloor == floor &&
                string.Equals(e.hostFace, face, StringComparison.OrdinalIgnoreCase))
                list.Add(e);
        }
        return list;
    }

    // Whole-face rule: if any decor in the list is still required, mark them all optional; if
    // every one is already optional, mark them all required. Returns the value applied, or null
    // when the list is empty.
    public static bool? SetFaceOptional(IList<EmbeddedObjectDef> decor)
    {
        if (decor == null || decor.Count == 0) return null;
        bool anyRequired = false;
        foreach (var e in decor) if (e != null && !e.optional) { anyRequired = true; break; }
        bool value = anyRequired;
        foreach (var e in decor) if (e != null) e.optional = value;
        return value;
    }

    // ---- Helpers ----

    private static bool SameType(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static BuildingInstance FindBuilding(EnvironmentDef env, string id)
    {
        if (env.buildingInstances == null || string.IsNullOrEmpty(id)) return null;
        foreach (var b in env.buildingInstances) if (b != null && b.instanceId == id) return b;
        return null;
    }

    private static ObjectInstance FindObject(EnvironmentDef env, string id)
    {
        if (env.objectInstances == null || string.IsNullOrEmpty(id)) return null;
        foreach (var o in env.objectInstances) if (o != null && o.instanceId == id) return o;
        return null;
    }
}
