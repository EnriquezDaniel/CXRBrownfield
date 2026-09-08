using UnityEngine;

// How a tile SHAPE occupies its grid cell. Cells are cellSize cubes and most shapes fill the whole
// cube, but sub-cell shapes (a 2×2×4 pillar, a 4×4×2 slab) fill only part of it and sit at a fixed
// place inside it. A TileFit is authored per TileShapePalette entry (cellExtents / cellAnchor) and is
// read by TileSpawner (placement), TileFaceGeometry (face planes for decor) and the tile editor
// (which faces count as covered), so all three agree on where the geometry actually is.
//
// Frame: extents and anchor are expressed in the CELL frame — after the shape's defaultRotation,
// before the tile's own rotation — the same frame faceNames are named in. The tile's rotation turns
// the box about its own center; the anchor is then applied to the ROTATED box's axis-aligned bounds,
// so a slab anchored to the floor still rests on the floor when it is tipped on its side, and a
// centered pillar stays centered whichever way it is turned.
public struct TileFit
{
    // Fraction of the cell the shape spans per axis. A component <= 0 reads as 1, so an entry
    // authored before this field existed (serialized as zero) is still a full cube.
    public Vector3 extents;
    // Where the box sits inside the cell per axis: -1 pressed against the cell's min side (the floor
    // on Y), 0 centered, +1 against the max side. Meaningless on an axis the shape fills.
    public Vector3 anchor;

    public static readonly TileFit Full = new TileFit(Vector3.one, Vector3.zero);

    public TileFit(Vector3 extents, Vector3 anchor)
    {
        this.extents = extents;
        this.anchor  = anchor;
    }

    // Extents with the <= 0 → 1 default applied.
    public Vector3 Extents => new Vector3(Norm(extents.x), Norm(extents.y), Norm(extents.z));

    private static float Norm(float e) => e <= 0f ? 1f : e;

    public bool IsFull
    {
        get
        {
            Vector3 e = Extents;
            return Mathf.Approximately(e.x, 1f) && Mathf.Approximately(e.y, 1f) && Mathf.Approximately(e.z, 1f);
        }
    }

    // Size of the box in cell-frame meters.
    public Vector3 LocalSize(float cellSize) => Extents * cellSize;

    // The target box in the PREFAB's own local frame, i.e. before defaultRotation is applied. Exact
    // for the axis-aligned (90° multiple) corrections defaultRotation is used for.
    public Vector3 PrefabLocalSize(Quaternion defaultRotation, float cellSize)
    {
        Vector3 s = Quaternion.Inverse(defaultRotation) * LocalSize(cellSize);
        return new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
    }

    // Axis-aligned bounds size of the box after the tile's rotation, in building-local meters. The
    // half-size of a rotated box is |R|·e (element-wise absolute rotation matrix times half extents).
    public Vector3 RotatedAabbSize(Quaternion tileRot, float cellSize)
    {
        Vector3 e  = LocalSize(cellSize) * 0.5f;
        Vector3 rx = tileRot * Vector3.right;
        Vector3 ry = tileRot * Vector3.up;
        Vector3 rz = tileRot * Vector3.forward;
        return 2f * new Vector3(
            Mathf.Abs(rx.x) * e.x + Mathf.Abs(ry.x) * e.y + Mathf.Abs(rz.x) * e.z,
            Mathf.Abs(rx.y) * e.x + Mathf.Abs(ry.y) * e.y + Mathf.Abs(rz.y) * e.z,
            Mathf.Abs(rx.z) * e.x + Mathf.Abs(ry.z) * e.y + Mathf.Abs(rz.z) * e.z);
    }

    // Offset from the cell center to the box's geometry center, building-local, after the tile's
    // rotation: the rotated bounds slide along each axis by the anchor's share of the free space.
    // Zero for a full cube or a centered shape, so full-cell tiles place exactly as before.
    public Vector3 CenterOffset(Quaternion tileRot, float cellSize)
    {
        Vector3 aabb = RotatedAabbSize(tileRot, cellSize);
        return new Vector3(
            anchor.x * (cellSize - aabb.x) * 0.5f,
            anchor.y * (cellSize - aabb.y) * 0.5f,
            anchor.z * (cellSize - aabb.z) * 0.5f);
    }

    // True when the rotated box reaches the cell's boundary face in `dir` (one of ±X/±Y/±Z) AND spans
    // the full cell on the other two axes — i.e. it fills that side of the cell completely, so a
    // neighbour's face pressed against it is hidden. A pillar covers no side; a floor slab covers
    // only the bottom; a full cube covers all six at any rotation.
    public bool CoversSide(Quaternion tileRot, Vector3 dir, float cellSize)
    {
        Vector3 aabb = RotatedAabbSize(tileRot, cellSize);
        Vector3 c    = CenterOffset(tileRot, cellSize);
        Vector3 min  = c - aabb * 0.5f, max = c + aabb * 0.5f;
        float h   = cellSize * 0.5f;
        float tol = cellSize * 1e-3f;

        int a = DominantAxis(dir);
        float reach = dir[a] > 0f ? max[a] : -min[a];
        if (reach < h - tol) return false;
        for (int b = 0; b < 3; b++)
        {
            if (b == a) continue;
            if (min[b] > -h + tol || max[b] < h - tol) return false;
        }
        return true;
    }

    // A face shared between tile A (looking along `dir`) and its neighbour B is covered only when both
    // sides reach the shared boundary and fill it. Either shape falling short leaves a gap, and a
    // face across a gap is still part of the building's skin (paintable, decoratable).
    public static bool FaceCovered(TileFit a, Quaternion rotA, TileFit b, Quaternion rotB,
                                   Vector3 dir, float cellSize)
        => a.CoversSide(rotA, dir, cellSize) && b.CoversSide(rotB, -dir, cellSize);

    private static int DominantAxis(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ay >= ax && ay >= az) return 1;
        return ax >= az ? 0 : 2;
    }
}
