using UnityEngine;

// Deform-aware face frames for tile faces. Where TileDeformField warps tile MESHES, this answers the
// same question for tile FACES: given a tile (with its rotation and optional TileDeform), which plane
// does a named face actually occupy in building-local space? Decor placement (DecorPlacement) seats
// props against this frame, so a prop painted on a skewed wall or sloped roof lands on the real
// deformed surface instead of the nominal axis-aligned cube face.
//
// Geometry note: WarpVertex applies (dx, dz) independent of Y and dyTop as a pure vertical lift, so
// WALL faces stay exactly planar under any deform (the quad's vertical plane is set by its two bottom
// corners; the top corners differ only by a lift within that plane). Only top/bottom faces can go
// non-planar (bilinear cage) — there the average plane through the 4 corners is used, which matches
// the visual surface closely for the gentle slopes the Skew tool produces.
public static class TileFaceGeometry
{
    // The plane + extents of one deformed tile face, in building-local meters.
    public struct FaceFrame
    {
        public Vector3 center;   // centroid of the 4 deformed face corners
        public Vector3 normal;   // outward unit normal of the (average) face plane
        public Vector3 up;       // DecorAlignment.FaceUp(normal, isRoof) — in-plane reference "up"
        public Vector3 right;    // in-plane across axis (sign arbitrary; used only for extents)
        public float   width;    // SAFE width: the shorter of the bottom/top edges projected on right
        public float   height;   // uTop - uBottom: the vertical band guaranteed inside the face
        public float   uBottom;  // signed offset along up from center to the safe bottom line
        public float   uTop;     // signed offset along up from center to the safe top line
        public bool    isRoof;   // abs(normal.y) > 0.7, matching the Decorate tool's classification
    }

    // Building-local outward direction each named face points to in the tile's unrotated frame.
    // Convention (shared with TileBuildingEditor.FaceFromNormal and TileDeformField.FaceQuads):
    // +Z=north, +X=east, -Z=south, -X=west, +Y=top, -Y=bottom.
    public static Vector3 BaselineDir(string face) => face switch
    {
        "north"  => Vector3.forward,
        "south"  => Vector3.back,
        "east"   => Vector3.right,
        "west"   => Vector3.left,
        "top"    => Vector3.up,
        "bottom" => Vector3.down,
        _        => Vector3.zero,
    };

    // Corner-post ordering matches TileDeform: plan corners [0]=(-x,-z) [1]=(+x,-z) [2]=(+x,+z)
    // [3]=(-x,+z); +4 = the top vertex of the same post. Per face: (bl, br, tl, tr) as seen from
    // OUTSIDE, chosen so bl/br are the face's bottom edge and bl/tl share one vertical post — the
    // frame math only relies on that pairing (normal sign is fixed against BaselineDir below).
    // Top/bottom use +Z as their "up" edge, matching DecorAlignment.FaceUp's roof reference.
    private static bool FaceQuad(string face, out int bl, out int br, out int tl, out int tr)
    {
        switch (face)
        {
            case "north":  bl = 3; br = 2; tl = 7; tr = 6; return true;
            case "east":   bl = 2; br = 1; tl = 6; tr = 5; return true;
            case "south":  bl = 1; br = 0; tl = 5; tr = 4; return true;
            case "west":   bl = 0; br = 3; tl = 4; tr = 7; return true;
            case "top":    bl = 4; br = 5; tl = 7; tr = 6; return true;
            case "bottom": bl = 1; br = 0; tl = 2; tr = 3; return true;
            default:       bl = br = tl = tr = -1;         return false;
        }
    }

    // Computes the deformed frame of `face` on `tile` in building-local meters. False for unknown
    // face names (e.g. the Decorate tool's "wall" fallback) or a degenerate (collapsed) face.
    // For a plain unrotated tile this reproduces the legacy cell math exactly: center = cellCenter
    // + axis*cellSize/2, width = height = cellSize, uBottom/uTop = ∓cellSize/2.
    public static bool TryGetFaceFrame(TileDef tile, string face, float cellSize, out FaceFrame frame)
    {
        frame = default;
        if (tile == null || cellSize <= 0f) return false;
        if (!FaceQuad(face, out int bl, out int br, out int tl, out int tr)) return false;

        // The 8 undeformed corner posts in cell-local meters (cell centered on the origin, floor
        // plane at y = -h) — the same convention as TileDeformField.BuildDeformedMesh.
        float h = cellSize * 0.5f;
        float[] xs = { -h, +h, +h, -h };
        float[] zs = { -h, -h, +h, +h };

        // Tile rotation, mirroring TileSpawner: a deformed SQUARE is built procedurally in grid space
        // (rotation ignored — see TileSpawner.SpawnDeformedBox); everything else rotates the cell.
        bool ignoreRotation = tile.deform != null && tile.shapeId == "square";
        Quaternion rot = ignoreRotation
            ? Quaternion.identity
            : Quaternion.Euler(tile.rotationX, tile.rotation, tile.rotationZ);

        Vector3 Corner(int k)
        {
            int i = k & 3;
            var p = new Vector3(xs[i], k < 4 ? -h : +h, zs[i]);
            return TileDeformField.WarpVertex(tile.deform, rot * p, cellSize);
        }

        Vector3 cellCenter = new Vector3((tile.gridX + 0.5f) * cellSize,
                                         (tile.floor + 0.5f) * cellSize,
                                         (tile.gridZ + 0.5f) * cellSize);
        Vector3 cBL = cellCenter + Corner(bl);
        Vector3 cBR = cellCenter + Corner(br);
        Vector3 cTL = cellCenter + Corner(tl);
        Vector3 cTR = cellCenter + Corner(tr);

        Vector3 center = (cBL + cBR + cTL + cTR) * 0.25f;

        // Average plane through the 4 corners (exact for walls — see header note). The normal's sign
        // is fixed against the face's rotated baseline axis so it always points OUTWARD.
        Vector3 rightRaw = (cBR + cTR - cBL - cTL) * 0.5f;
        Vector3 upRaw    = (cTL + cTR - cBL - cBR) * 0.5f;
        Vector3 n        = Vector3.Cross(upRaw, rightRaw);
        if (n.sqrMagnitude < 1e-8f) return false;
        n.Normalize();
        if (Vector3.Dot(n, rot * BaselineDir(face)) < 0f) n = -n;

        bool    isRoof = Mathf.Abs(n.y) > 0.7f;
        Vector3 up     = DecorAlignment.FaceUp(n, isRoof);
        Vector3 right  = Vector3.Cross(up, n).normalized;

        // Safe extents: project the corners into the frame about the center. The shorter of the
        // bottom/top edges bounds the width (a trapezoid's short edge wins), and the highest bottom
        // corner / lowest top corner bound the vertical band — so a prop fit inside (width × height)
        // never pokes past the deformed face edges (shrink-to-fit).
        float rBL = Vector3.Dot(cBL - center, right), rBR = Vector3.Dot(cBR - center, right);
        float rTL = Vector3.Dot(cTL - center, right), rTR = Vector3.Dot(cTR - center, right);
        float uBL = Vector3.Dot(cBL - center, up),    uBR = Vector3.Dot(cBR - center, up);
        float uTL = Vector3.Dot(cTL - center, up),    uTR = Vector3.Dot(cTR - center, up);

        frame.center  = center;
        frame.normal  = n;
        frame.isRoof  = isRoof;
        frame.up      = up;
        frame.right   = right;
        frame.width   = Mathf.Min(Mathf.Abs(rBR - rBL), Mathf.Abs(rTR - rTL));
        frame.uBottom = Mathf.Max(uBL, uBR);
        frame.uTop    = Mathf.Min(uTL, uTR);
        frame.height  = frame.uTop - frame.uBottom;

        return frame.width > 1e-3f && frame.height > 1e-3f;
    }
}
