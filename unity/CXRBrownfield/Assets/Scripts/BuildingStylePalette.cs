using System;
using System.Collections.Generic;
using UnityEngine;

// Maps a building style letter (BuildingDef.style, "A" to "F", see BuildingStyles) to the
// MaterialPalette id painted on the building's wall faces. One of the guarded palette assets
// (Assets/Resources/BuildingStylePalette.asset, see PaletteGuard): append-only, never rewrite the
// entries array. Seeded by Tools → CXR → Palettes → Create Building Style Palette (seeded); point
// the ids at your own MaterialPalette entries in the Inspector to build a custom set.
[CreateAssetMenu(fileName = "BuildingStylePalette", menuName = "CXR/BuildingStylePalette")]
public class BuildingStylePalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("Style letter, A to F. The layout model only ever sees this letter.")]
        public string styleId;
        [Tooltip("MaterialPalette id painted on every wall face the style owns (north, east, south, west, curve). Tops and bottoms keep the tile's default material.")]
        public string wallMaterialId;
    }

    public List<Entry> entries = new();

    public Entry GetEntry(string styleId)
    {
        foreach (var e in entries)
            if (e != null && string.Equals(e.styleId, styleId, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // Null when the letter has no entry. Quiet on a miss: BuildingStyleResolver reports it once.
    public string GetWallMaterialId(string styleId) => GetEntry(styleId)?.wallMaterialId;

    public bool Has(string styleId) => GetEntry(styleId) != null;
}
