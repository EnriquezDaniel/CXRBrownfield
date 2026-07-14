using System;
using System.Collections.Generic;
using UnityEngine;

// Maps a fence-type id (FenceDef.fenceType) to the prefabs and metrics used to build a fence run.
// The fence analogue of PathMaterialPalette: a clear, self-contained list of the fence vocabulary
// shared with the generation prompt (e.g. "picket", "lattice", "chain_link", "wood_privacy",
// "wrought_iron"). Holds prefab refs directly (not PrefabRegistry keys) so the palette is the single
// place a fence type is wired.
[CreateAssetMenu(fileName = "FencePalette", menuName = "CXR/FencePalette")]
public class FencePalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string     fenceType;        // e.g. "picket", "lattice", "chain_link"
        public GameObject panelPrefab;      // USER WIRES — one fence panel, modeled along +X (run dir), base at y=0
        public GameObject postPrefab;       // USER WIRES — optional post placed at each joint/corner (may be null)
        public float      panelLength = 2f; // modeled length of one panel in meters (centerline resample spacing)
        public float      height     = 1.2f;// default fence height in meters (used when FenceDef.height <= 0)
        public bool       scalePanelToFit = true; // stretch each panel along its run axis to span its gap exactly
    }

    public List<Entry> entries = new();

    // Case-insensitive lookup. Logs + returns null when absent (mirrors PathMaterialPalette.GetMaterial),
    // so a fence with an unknown type degrades to a warning instead of crashing the render.
    public Entry Get(string fenceType)
    {
        foreach (var e in entries)
            if (string.Equals(e.fenceType, fenceType, StringComparison.OrdinalIgnoreCase)) return e;
        Debug.LogError($"[FencePalette] Fence type '{fenceType}' not found.");
        return null;
    }
}
