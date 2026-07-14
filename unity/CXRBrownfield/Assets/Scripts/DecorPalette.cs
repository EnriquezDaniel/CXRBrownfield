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
[CreateAssetMenu(fileName = "DecorPalette", menuName = "CXR/DecorPalette")]
public class DecorPalette : ScriptableObject
{
    // Which tile surfaces a decor is allowed to paint. Classified from the clicked face's normal:
    // near-vertical normal = Wall, near-up normal = Roof. Any accepts both.
    public enum Surface { Wall, Roof, Any }

    [Serializable]
    public class Entry
    {
        public string  decorId;                 // picker label, e.g. "door", "window", "vent"
        public string  prefabKey;               // key into PrefabRegistry (== EmbeddedObjectDef.prefabType)
        public Surface surface = Surface.Wall;   // face filter

        // How much of the tile cell the prop spans, as fractions of the cell edge. Aspect ratio is
        // preserved: the prop is uniformly scaled to fit inside this width x height box.
        public float widthFraction  = 0.8f;
        public float heightFraction = 0.8f;
        // Vertical seat within the cell. Bottom = doors (base on the floor edge); Center = windows.
        public DecorAlignment.Anchor anchor = DecorAlignment.Anchor.Center;

        // Auto-align override. Auto infers the prop's mount (back) axis from its mesh bounds (thinnest
        // axis); name an explicit axis for chunky props the heuristic misreads. flipMount inverts it
        // if the prop faces inward.
        public DecorAlignment.MountAxis mountAxis = DecorAlignment.MountAxis.Auto;
        public bool  flipMount = false;
        public float surfaceOffset = 0.03f;      // push out along the normal to avoid z-fighting
    }

    public List<Entry> entries = new();

    public Entry Get(string id)
    {
        if (entries == null || string.IsNullOrEmpty(id)) return null;
        foreach (var e in entries)
            if (e != null && string.Equals(e.decorId, id, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }
}
