using System;
using System.Collections.Generic;
using UnityEngine;

// Presets for the tile editor's "Decorate" tool. Each entry is ONE decorative prefab (a door, a
// window, a vent, ...) plus the rules that seat it systematically on a tile face: which surface it
// targets, how much of the cell it spans (width/height fractions), and where it anchors vertically.
// You pick a decor and click a tile face — the prop auto-centers, fits, and seats itself flush, the
// same way the Paint tool assigns a face material. Prefab keys resolve through the SAME PrefabRegistry
// the renderer uses (WorldRenderer.RenderEmbeddedObjects), so painted decorations render identically
// at runtime. Parallels MaterialPalette (materialId -> Material).
//
// Decor STACKS by default: two different decors can share one tile face (a window and a fire escape).
// Set replacesOtherDecor on an entry that must own the whole face. Painting the same decor twice on a
// face always replaces it, so dragging never piles up duplicates.
[CreateAssetMenu(fileName = "DecorPalette", menuName = "CXR/DecorPalette")]
public class DecorPalette : ScriptableObject
{
    // Which tile surfaces a decor is allowed to paint. Classified from the clicked face's normal:
    // near-vertical normal = Wall, near-up normal = Roof. Any accepts both.
    public enum Surface { Wall, Roof, Any }

    [Serializable]
    public class Entry
    {
        [Tooltip("Name shown on this decor's button in the Decorate panel, e.g. \"door\", \"window\", " +
                 "\"vent\". Keep it unique — the tool selects a decor by this id. Two entries MAY share " +
                 "the same prefabKey (e.g. a tall and a short window) — they still count as the same " +
                 "kind when a face is repainted.")]
        public string  decorId;                 // picker label, e.g. "door", "window", "vent"

        [Tooltip("Key into the PrefabRegistry — must match a key there exactly, or the tool spawns a " +
                 "magenta placeholder cube and logs a warning. Stored on the saved decoration as " +
                 "EmbeddedObjectDef.prefabType, so the runtime renderer resolves the same prefab.")]
        public string  prefabKey;               // key into PrefabRegistry (== EmbeddedObjectDef.prefabType)

        [Tooltip("Which tile faces this decor may be painted on. The clicked face is classified from " +
                 "its normal: near-vertical = Wall, near-up = Roof (|normal.y| > 0.7). Wall = doors, " +
                 "windows, fire escapes. Roof = vents, AC units, skylights. Any = both. Clicking a face " +
                 "this filter rejects does nothing (no error).")]
        public Surface surface = Surface.Wall;   // face filter

        [Tooltip("Face-occupancy rule.\n\n" +
                 "OFF (default) — STACKS: this decor leaves any other decor already on the face alone, " +
                 "and other decor leaves it alone. Lets a window and a fire escape sit on the same face.\n\n" +
                 "ON — CLEARS THE FACE: painting this decor deletes other decor on that tile face (the " +
                 "old one-decor-per-face rule). Use it for full-face props like a storefront that must " +
                 "not have a window inside them. It only clears decor that ALSO has this box ticked — a " +
                 "stacking decor is protected either way.\n\n" +
                 "Painting the SAME decor on a face replaces it in both modes, so drags never duplicate.")]
        public bool replacesOtherDecor = false;

        // How much of the tile cell the prop spans, as fractions of the cell edge. Aspect ratio is
        // preserved: the prop is uniformly scaled to fit inside this width x height box.
        [Tooltip("How much of the tile cell's WIDTH the prop may span, as a fraction of the cell edge " +
                 "(0.8 = 80%). The prop is scaled UNIFORMLY to fit inside the width x height box, so " +
                 "its aspect ratio is preserved and one of the two fractions ends up slack.\n\n" +
                 "0 means \"not set\" and falls back to 0.8 — older palette entries serialize as 0.")]
        public float widthFraction  = 0.8f;

        [Tooltip("How much of the tile cell's HEIGHT the prop may span, as a fraction of the cell edge " +
                 "(0.8 = 80%). Paired with widthFraction as a fit box; aspect ratio is preserved.\n\n" +
                 "0 means \"not set\" and falls back to 0.8 — older palette entries serialize as 0.")]
        public float heightFraction = 0.8f;

        // Vertical seat within the cell. Bottom = doors (base on the floor edge); Center = windows.
        [Tooltip("Where the prop seats vertically within the tile cell.\n\n" +
                 "Bottom — base sits on the floor edge: doors, storefronts, stairs.\n" +
                 "Center — centered in the cell: windows, vents.\n" +
                 "Top — flush with the ceiling edge: signage, transoms.\n\n" +
                 "Only matters when the prop is shorter than the cell (heightFraction < 1).")]
        public DecorAlignment.Anchor anchor = DecorAlignment.Anchor.Center;

        // Auto-align override. Auto infers the prop's mount (back) axis from its mesh bounds (thinnest
        // axis); name an explicit axis for chunky props the heuristic misreads. flipMount inverts it
        // if the prop faces inward.
        [Tooltip("Which axis in the PREFAB's own space points out of its back — the side that goes " +
                 "against the wall.\n\n" +
                 "Auto (default) infers it from the mesh bounds: the thinnest axis is assumed to be the " +
                 "depth of a flat prop like a window. That misreads chunky props (a fire escape, an AC " +
                 "unit) that are not flat, so name the axis explicitly there.\n\n" +
                 "If the prop mounts sideways or lies down, this is the field to fix.")]
        public DecorAlignment.MountAxis mountAxis = DecorAlignment.MountAxis.Auto;

        [Tooltip("Invert the mount axis. Tick this when the prop ends up backwards — facing INTO the " +
                 "wall instead of out of it. The quickest fix once mountAxis picked the right axis but " +
                 "the wrong direction.")]
        public bool  flipMount = false;

        [Tooltip("Meters the prop is pushed out along the face normal so it does not z-fight with the " +
                 "wall (~0.03 is typical). Raise it if the prop's back clips into the tile; too large " +
                 "and the prop visibly floats off the surface.\n\n" +
                 "Also the lever for separating two STACKED decors that would otherwise overlap.")]
        public float surfaceOffset = 0.03f;      // push out along the normal to avoid z-fighting
    }

    [Tooltip("One entry per decor. Order sets the button order in the Decorate panel, and the FIRST " +
             "entry is the fallback the tool selects when nothing else is active. The entry named " +
             "WindowPair also sizes and seats the windows the layout generator puts on upper floors.")]
    public List<Entry> entries = new();

    public const string ResourceName = "DecorPalette";

    // The palette lives in Resources so a caller with no serialized slot (the layout converter's
    // window pass) still resolves it. Same precedent as BuildingStyleResolver.LoadDefault.
    public static DecorPalette LoadDefault() => Resources.Load<DecorPalette>(ResourceName);

    private static readonly HashSet<string> _warned = new();

    // The rule the layout generator follows for upper-floor window pairs (BuildingWindows): the
    // WindowPair entry's prefab, size fractions, anchor and offsets, always marked optional so the
    // VR low-detail viewer skips them. Falls back to BuildingWindows.DefaultRule, warning once, when
    // the palette or the entry is missing, so generation never silently loses its windows.
    public static BuildingWindows.Rule GeneratedWindowRule(DecorPalette palette)
    {
        var rule = BuildingWindows.DefaultRule;
        if (palette == null)
        {
            WarnOnce("palette", "[DecorPalette] No DecorPalette in Resources; generated windows use built-in defaults.");
            return rule;
        }
        var e = palette.Get(BuildingWindows.DefaultPrefabKey);
        if (e == null || string.IsNullOrEmpty(e.prefabKey))
        {
            WarnOnce("entry", $"[DecorPalette] No '{BuildingWindows.DefaultPrefabKey}' entry; generated windows use built-in defaults.");
            return rule;
        }
        rule.prefabKey     = e.prefabKey;
        rule.widthFrac     = e.widthFraction  > 0f ? e.widthFraction  : BuildingWindows.DefaultFraction;
        rule.heightFrac    = e.heightFraction > 0f ? e.heightFraction : BuildingWindows.DefaultFraction;
        rule.surfaceOffset = e.surfaceOffset;
        rule.anchor        = (int)e.anchor;
        rule.mountAxis     = (int)e.mountAxis;
        rule.flipMount     = e.flipMount;
        rule.optional      = true;
        return rule;
    }

    private static void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) Debug.LogWarning(message);
    }

    public Entry Get(string id)
    {
        if (entries == null || string.IsNullOrEmpty(id)) return null;
        foreach (var e in entries)
            if (e != null && string.Equals(e.decorId, id, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // Resolves an already-placed decoration (EmbeddedObjectDef.prefabType) back to its palette entry,
    // so the placement rule can read the OLD prop's occupancy flag. Returns null for decor whose prefab
    // is no longer in the palette (legacy / generated data) — callers treat that as stacking, i.e. the
    // prop is left alone rather than collaterally deleted. First match wins when entries share a key.
    public Entry EntryForPrefab(string prefabKey)
    {
        if (entries == null || string.IsNullOrEmpty(prefabKey)) return null;
        foreach (var e in entries)
            if (e != null && string.Equals(e.prefabKey, prefabKey, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }
}
