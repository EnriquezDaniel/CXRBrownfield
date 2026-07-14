using System;
using System.Collections.Generic;
using UnityEngine;

// Maps a path-surface id (PathDef.material) to the Material used for its mesh ribbon.
// Kept separate from MaterialPalette (building-face materials) so path surfaces are a clear,
// self-contained list. Keys are the canonical path vocabulary shared with the generation prompt:
// "pavement_dark", "pavement_light", "brick", "dirt", "asphalt".
[CreateAssetMenu(fileName = "PathMaterialPalette", menuName = "CXR/PathMaterialPalette")]
public class PathMaterialPalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string   id;         // e.g. "brick", "dirt", "pavement_dark"
        public Material material;   // USER WIRES THIS IN INSPECTOR
    }

    public List<Entry> entries = new();

    public Material GetMaterial(string id)
    {
        foreach (var e in entries)
            if (string.Equals(e.id, id, StringComparison.OrdinalIgnoreCase)) return e.material;
        Debug.LogError($"[PathMaterialPalette] Path material '{id}' not found.");
        return null;
    }
}
