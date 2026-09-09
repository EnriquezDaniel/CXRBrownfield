using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "MaterialPalette", menuName = "CXR/MaterialPalette")]
public class MaterialPalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string   materialId;   // e.g. "brick_red", "glass", "roof_tar"
        public Material material;     // USER WIRES THIS IN INSPECTOR
    }

    public List<Entry> entries = new();

    public Material GetMaterial(string materialId)
    {
        foreach (var e in entries)
            if (string.Equals(e.materialId, materialId, StringComparison.OrdinalIgnoreCase)) return e.material;
        Debug.LogError($"[MaterialPalette] Material '{materialId}' not found.");
        return null;
    }

    // Quiet presence check, for callers that want to report a miss once themselves.
    public bool Has(string materialId)
    {
        foreach (var e in entries)
            if (e != null && string.Equals(e.materialId, materialId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
