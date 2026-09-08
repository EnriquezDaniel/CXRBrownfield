using System;
using System.Collections.Generic;
using UnityEngine;

// Maps a water surface id (WaterBodyDef.material) to the Material used for its flat mesh. One of
// the guarded palette assets (Assets/Resources/WaterPalette.asset, see PaletteGuard): append-only,
// never rewrite the entries array. Seeded by Tools → CXR → Palettes → Create Water Palette (seeded)
// with "clear", "lake", "murky" and "dark" transparent unlit materials; add textured variants in
// the Inspector.
[CreateAssetMenu(fileName = "WaterPalette", menuName = "CXR/WaterPalette")]
public class WaterPalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string   id;         // e.g. "clear", "lake", "murky", "dark"
        public Material material;   // USER WIRES THIS IN INSPECTOR (or the seeding menu does)
    }

    public List<Entry> entries = new();

    public Material GetMaterial(string id)
    {
        foreach (var e in entries)
            if (e != null && string.Equals(e.id, id, StringComparison.OrdinalIgnoreCase)) return e.material;
        Debug.LogError($"[WaterPalette] Water material '{id}' not found.");
        return null;
    }

    public bool Has(string id)
    {
        foreach (var e in entries)
            if (e != null && string.Equals(e.id, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
