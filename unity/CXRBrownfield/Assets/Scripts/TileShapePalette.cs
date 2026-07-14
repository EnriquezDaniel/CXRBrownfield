using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "TileShapePalette", menuName = "CXR/TileShapePalette")]
public class TileShapePalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string         shapeId;     // e.g. "square", "wedge", "quarter_curve"
        public GameObject     prefab;      // USER WIRES THIS IN INSPECTOR
        // Baseline orientation (Euler degrees) applied to the prefab before the tile's own rotation,
        // to correct prefabs authored facing the wrong way (e.g. the curved corner). Leave 0 for
        // shapes already authored correctly. The tile's rotation is composed on top of this.
        public Vector3        defaultRotation;
        // Submesh-order face names matching the prefab's material slots,
        // e.g. ["north","east","south","west","top","bottom"]
        public List<string>   faceNames;
    }

    public List<Entry> entries = new();

    public Entry GetEntry(string shapeId)
    {
        foreach (var e in entries)
            if (string.Equals(e.shapeId, shapeId, StringComparison.OrdinalIgnoreCase)) return e;
        Debug.LogError($"[TileShapePalette] Shape '{shapeId}' not found.");
        return null;
    }

    public GameObject GetPrefab(string shapeId)
    {
        var e = GetEntry(shapeId);
        if (e == null) return null;
        if (e.prefab == null) Debug.LogError($"[TileShapePalette] Prefab for '{shapeId}' is null.");
        return e.prefab;
    }

    // Baseline orientation correction for a shape (identity when the shape isn't found or unset).
    public Quaternion GetDefaultRotation(string shapeId)
    {
        var e = GetEntry(shapeId);
        return e == null ? Quaternion.identity : Quaternion.Euler(e.defaultRotation);
    }
}
