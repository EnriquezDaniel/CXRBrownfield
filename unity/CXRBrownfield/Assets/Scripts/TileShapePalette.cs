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
        // How much of the cell the shape fills per axis, as a fraction, in the cell frame (after
        // defaultRotation). Zero (the default for entries authored before this field) means a full
        // cube. A 2×2×4 pillar in a 4 m cell is (0.5, 1, 0.5); a 4×4×2 slab is (1, 0.5, 1).
        public Vector3        cellExtents;
        // Where a sub-cell shape sits inside the cell per axis: -1 against the min side (the floor on
        // Y), 0 centered, +1 against the max side. A floor slab is (0, -1, 0). See TileFit.
        public Vector3        cellAnchor;
    }

    public List<Entry> entries = new();

    public Entry GetEntry(string shapeId)
    {
        var e = FindEntry(shapeId);
        if (e == null) Debug.LogError($"[TileShapePalette] Shape '{shapeId}' not found.");
        return e;
    }

    private Entry FindEntry(string shapeId)
    {
        foreach (var e in entries)
            if (string.Equals(e.shapeId, shapeId, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // Sub-cell fit of a shape (a full cube for unknown shapes and for entries with zero extents).
    // Quiet on a miss: the prefab lookup already reports unknown shapes.
    public TileFit GetFit(string shapeId)
    {
        var e = FindEntry(shapeId);
        return e == null ? TileFit.Full : new TileFit(e.cellExtents, e.cellAnchor);
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
