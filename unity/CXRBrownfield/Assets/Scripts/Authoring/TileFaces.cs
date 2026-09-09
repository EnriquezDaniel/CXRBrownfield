using System;
using System.Collections.Generic;
using UnityEngine;

// Which named face of a tile points a given building-local direction, and whether that face is part
// of the building's outer skin. The tile editor's whole-face tools and the sign placer
// (BuildingSigns) share these so "the exposed east side of the building" means one thing.
// Convention (TileFaceGeometry.BaselineDir): +Z=north, +X=east, -Z=south, -X=west, +Y=top, -Y=bottom.
public static class TileFaces
{
    public static readonly string[] Names = { "north", "south", "east", "west", "top", "bottom" };

    public static Quaternion Rotation(TileDef t) =>
        Quaternion.Euler(t.rotationX, t.rotation, t.rotationZ);

    // Named face of this tile that points the given building-local direction (accounts for the
    // tile's full rotation), or null if none lines up — e.g. a wedge with no axis-aligned face there.
    public static string FacePointing(TileDef t, Vector3 dirLocal)
    {
        if (t == null) return null;
        Quaternion rot = Rotation(t);
        string best = null;
        float bestDot = 0.9f;
        foreach (var name in Names)
        {
            float dot = Vector3.Dot(rot * TileFaceGeometry.BaselineDir(name), dirLocal);
            if (dot > bestDot) { bestDot = dot; best = name; }
        }
        return best;
    }

    // A tile face is part of the building's outer skin when no tile occupies the neighbouring cell
    // in that direction (walls = same-floor neighbour; top/bottom = the floor above/below), or when
    // either side leaves a gap at the shared boundary: a pillar beside a square, or a slab under a
    // square, keeps its face across the gap (TileFit.FaceCovered). fitFor resolves a shape's TileFit
    // (null = every shape fills its cell).
    public static bool IsExposed(IList<TileDef> tiles, TileDef t, Vector3 dirLocal, float cellSize,
                                 Func<string, TileFit> fitFor)
    {
        if (tiles == null || t == null) return true;
        int nx = t.gridX + Mathf.RoundToInt(dirLocal.x);
        int nf = t.floor + Mathf.RoundToInt(dirLocal.y);
        int nz = t.gridZ + Mathf.RoundToInt(dirLocal.z);
        foreach (var o in tiles)
        {
            if (o == null || ReferenceEquals(o, t)) continue;
            if (o.gridX == nx && o.gridZ == nz && o.floor == nf)
                return !TileFit.FaceCovered(Fit(fitFor, t.shapeId), Rotation(t),
                                            Fit(fitFor, o.shapeId), Rotation(o), dirLocal, cellSize);
        }
        return true;
    }

    private static TileFit Fit(Func<string, TileFit> fitFor, string shapeId) =>
        fitFor != null ? fitFor(shapeId) : TileFit.Full;
}
