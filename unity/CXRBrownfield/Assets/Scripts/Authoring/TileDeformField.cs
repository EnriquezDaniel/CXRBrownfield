using UnityEngine;

// Shared geometry helper that writes TileDeform offsets onto a BuildingDef's tiles to create
// acute/obtuse floor-plan corners and sloped roof edges, and builds the matching procedural prism
// mesh. Used by BOTH the interactive Skew tool (TileBuildingEditor) and AI generation
// (LayoutConverter), so hand-authored and generated angled buildings come out identical — mirroring
// how TileSpawner is the single shared geometry source for authoring + rendering.
//
// Lives in the Authoring assembly because it depends only on Authoring types (BuildingDef/TileDef/
// TileDeform) — that lets LayoutConverter (also Authoring) reach it, since an asmdef assembly can't
// reference the default Assembly-CSharp where TileSpawner lives.
//
// Seamlessness principle: every offset is a pure function of a tile's integer GRID-CORNER position,
// sampled independently per tile. Neighbouring tiles share corner posts (e.g. tile (x) corner +X ==
// tile (x+1) corner -X == grid corner (x+1)), so they read the same offset there and the wall stays
// gap-free without any cross-tile bookkeeping. All offsets are in CELL units (see TileDeform).
public static class TileDeformField
{
    public enum Corner  { SW, SE, NE, NW }   // building footprint corners (min/max of the tile grid)
    public enum Edge    { North, East, South, West }
    public enum Falloff { Linear, Smooth }

    // Bends one footprint corner to an acute/obtuse angle by shearing the footprint into a
    // TRAPEZOID: the wall running along X at the corner (south for SW/SE, north for NW/NE) stays put,
    // and the wall running along Z at the corner (west for SW/NW, east for SE/NE) tilts away from it
    // at a CONSTANT angle, holding that slant all the way to the far end of the building. The
    // opposite Z-wall is the straight anchor, so one face stays straight and the other leaves the
    // corner as a single straight slant (sharp corner, not a curve). `angleDeg` is the tilt of that
    // wall from square (sign flips which way it leans — acute vs obtuse). ADDS to any existing
    // deform, so multiple corners (and a slope) compose.
    public static void ApplyCornerBend(BuildingDef bdef, Corner corner, float angleDeg)
    {
        if (bdef?.tiles == null || bdef.tiles.Count == 0) return;
        if (!GridBounds(bdef, out int minX, out int maxX, out int minZ, out int maxZ)) return;

        int gx0 = minX, gx1 = maxX + 1, gz0 = minZ, gz1 = maxZ + 1;
        float spanX = Mathf.Max(1, gx1 - gx0);
        float t = Mathf.Tan(angleDeg * Mathf.Deg2Rad);

        bool south = corner == Corner.SW || corner == Corner.SE;  // kept X-wall is the south wall?
        bool west  = corner == Corner.SW || corner == Corner.NW;  // tilting Z-wall is the west wall?
        float xWallZ   = south ? gz0 : gz1;   // gz of the wall that stays straight
        float anchorX  = west  ? gx1 : gx0;   // gx of the straight anchor (opposite the tilting wall)
        float tiltSign = west  ? -1f : +1f;   // a positive angle swings the west wall toward -X

        foreach (var tile in bdef.tiles)
        {
            var d = EnsureDeform(tile);
            for (int i = 0; i < 4; i++)
            {
                Vector2 g = CornerGrid(tile, i);
                float distZ = Mathf.Abs(g.y - xWallZ);            // cells along Z from the kept wall
                float fracX = Mathf.Abs(g.x - anchorX) / spanX;   // 0 at the anchor edge, 1 at the tilt edge
                d.dx[i] += tiltSign * t * distZ * fracX;          // constant slope → straight slanted edge
            }
        }
    }

    // Slopes the top edge on one side of the building — a shed-roof tilt that flattens back inward
    // over `falloffTiles` rows. Only the TOP floor's tiles are touched, so interior floor seams stay
    // flat (bottoms always sit on the floor plane). `riseCells` is the height change (cell units) at
    // the far end of the edge; the near end stays put, so the roof line tilts across the face.
    public static void ApplySlopedEdge(BuildingDef bdef, Edge edge, float riseCells,
                                       float falloffTiles, Falloff curve = Falloff.Smooth)
    {
        if (bdef?.tiles == null || bdef.tiles.Count == 0 || falloffTiles <= 0f) return;
        if (!GridBounds(bdef, out int minX, out int maxX, out int minZ, out int maxZ)) return;
        int topFloor = TopFloor(bdef);

        int gx0 = minX, gx1 = maxX + 1, gz0 = minZ, gz1 = maxZ + 1;
        float spanX = Mathf.Max(1, gx1 - gx0);
        float spanZ = Mathf.Max(1, gz1 - gz0);

        foreach (var t in bdef.tiles)
        {
            if (t.floor != topFloor) continue;
            var d = EnsureDeform(t);
            for (int i = 0; i < 4; i++)
            {
                Vector2 g = CornerGrid(t, i);
                float along, inward;                       // along the edge (0..1), tiles inward from it
                switch (edge)
                {
                    case Edge.North: along = (g.x - gx0) / spanX; inward = gz1 - g.y; break;
                    case Edge.South: along = (g.x - gx0) / spanX; inward = g.y - gz0; break;
                    case Edge.East:  along = (g.y - gz0) / spanZ; inward = gx1 - g.x; break;
                    default:         along = (g.y - gz0) / spanZ; inward = g.x - gx0; break; // West
                }
                d.dyTop[i] += riseCells * along * Weight(inward, falloffTiles, curve);
            }
        }
    }

    // Clears all deform on a building (Skew tool "Reset").
    public static void ClearDeform(BuildingDef bdef)
    {
        if (bdef?.tiles == null) return;
        foreach (var t in bdef.tiles) t.deform = null;
    }

    // -----------------------------------------------------------------------
    // Procedural prism mesh (the geometry a deform describes)
    // -----------------------------------------------------------------------

    public const int FaceCount = 6;   // north, east, south, west, top, bottom

    // Per-face corner index lists into the 8-corner array (bottom 0..3, top 4..7). The two triangles
    // per quad are emitted reversed (see SetTriangles below) so the face is front-facing OUTWARD
    // under Unity's screen-space clockwise = front, back-cull convention (a camera viewing the +Z
    // face looks down −Z, so its screen-right is −X — the quad must wind the other way to stay front).
    // Corner order matches TileDeform: [0]=(-x,-z) [1]=(+x,-z) [2]=(+x,+z) [3]=(-x,+z); +4 = top.
    private static readonly int[][] FaceQuads =
    {
        new[] { 3, 7, 6, 2 },  // 0 north  (+Z)
        new[] { 2, 6, 5, 1 },  // 1 east   (+X)
        new[] { 1, 5, 4, 0 },  // 2 south  (-Z)
        new[] { 0, 4, 7, 3 },  // 3 west   (-X)
        new[] { 5, 6, 7, 4 },  // 4 top    (+Y)
        new[] { 0, 3, 2, 1 },  // 5 bottom (-Y)
    };

    // The deform is geometrically a BILINEAR CAGE over the unit cell: the 4 plan corners carry the
    // (dx, dz) lateral offsets and the dyTop top-rise, and any interior point blends them by area
    // weights. `u` runs along +X (corner 0→1), `v` along +Z (corner 0→3); corner order matches
    // TileDeform ([0]=(0,0) [1]=(1,0) [2]=(1,1) [3]=(0,1)). Both the box build below and the general
    // prefab warp (TileSpawner) read THIS function, so a skewed square and a skewed curve come out
    // seamless along a shared edge: an edge is a u- or v-isoline, so its offsets depend only on the
    // two grid-corner posts both tiles share (which the field already forces to agree). Offsets are
    // in cell units.
    public static void SampleOffset(TileDeform d, float u, float v,
                                    out float ox, out float oz, out float oyTop)
    {
        u = Mathf.Clamp01(u); v = Mathf.Clamp01(v);
        float w0 = (1f - u) * (1f - v), w1 = u * (1f - v), w2 = u * v, w3 = (1f - u) * v;
        ox    = Blend(d?.dx,    w0, w1, w2, w3);
        oz    = Blend(d?.dz,    w0, w1, w2, w3);
        oyTop = Blend(d?.dyTop, w0, w1, w2, w3);
    }

    // Warps one vertex through the cage. `p` is in cell-local meters: the cell is centered on the
    // origin and spans ±cellSize/2 on X/Z, with the floor plane at y = -cellSize/2 and the ceiling at
    // +cellSize/2. dyTop scales by the vertex's vertical fraction (0 at the floor, 1 at the ceiling)
    // so bottoms stay on the floor plane and floors keep stacking while only the roof line tilts.
    // A pure function of position, so every tile — box, wedge, curve, at any rotation — warps
    // identically wherever its geometry coincides with a neighbour's.
    public static Vector3 WarpVertex(TileDeform d, Vector3 p, float cellSize)
    {
        float h = cellSize * 0.5f;
        SampleOffset(d, p.x / cellSize + 0.5f, p.z / cellSize + 0.5f,
                     out float ox, out float oz, out float oyTop);
        float vfrac = Mathf.Clamp01((p.y + h) / cellSize);
        return new Vector3(p.x + ox * cellSize, p.y + oyTop * cellSize * vfrac, p.z + oz * cellSize);
    }

    private static float Blend(float[] a, float w0, float w1, float w2, float w3) =>
        a == null ? 0f : Off(a, 0) * w0 + Off(a, 1) * w1 + Off(a, 2) * w2 + Off(a, 3) * w3;

    // Builds the deformed-tile mesh for a SQUARE/box tile: the 8 displaced corners emitted as 6
    // four-vertex faces (24 verts) so each face owns its own normal and submesh (submesh order =
    // north/east/south/west/top/bottom, matching the square shape's faceNames so per-face painting
    // still resolves). Corners are warped through the shared cage so a box and a curve stay seamless.
    // Bottom corners stay on the floor plane (y = -h); dyTop lifts only the top vertices, so floors
    // keep stacking seamlessly while the roof line can slope. Vertices are in cell units, centered.
    // Non-square shapes are handled by TileSpawner, which warps the real prefab mesh through the same
    // cage instead of replacing it with this box.
    public static Mesh BuildDeformedMesh(TileDeform d, float cellSize)
    {
        float h = cellSize * 0.5f;
        float[] xs = { -h, +h, +h, -h };   // undeformed corner signs
        float[] zs = { -h, -h, +h, +h };

        var corners = new Vector3[8];
        for (int i = 0; i < 4; i++)
        {
            corners[i]     = WarpVertex(d, new Vector3(xs[i], -h, zs[i]), cellSize);  // bottom
            corners[i + 4] = WarpVertex(d, new Vector3(xs[i], +h, zs[i]), cellSize);  // top
        }

        var verts = new Vector3[FaceCount * 4];
        var uvs   = new Vector2[FaceCount * 4];
        var mesh  = new Mesh { name = "DeformedTile" };

        for (int f = 0; f < FaceCount; f++)
        {
            int[] q = FaceQuads[f];
            int b = f * 4;
            verts[b + 0] = corners[q[0]]; uvs[b + 0] = new Vector2(0f, 0f);
            verts[b + 1] = corners[q[1]]; uvs[b + 1] = new Vector2(0f, 1f);
            verts[b + 2] = corners[q[2]]; uvs[b + 2] = new Vector2(1f, 1f);
            verts[b + 3] = corners[q[3]]; uvs[b + 3] = new Vector2(1f, 0f);
        }

        mesh.vertices     = verts;
        mesh.uv           = uvs;
        mesh.subMeshCount = FaceCount;
        for (int f = 0; f < FaceCount; f++)
        {
            int b = f * 4;
            // Reversed winding (b, b+2, b+1 / b, b+3, b+2) → faces point outward in Unity's
            // left-handed, back-culled pipeline. RecalculateNormals then yields outward normals.
            mesh.SetTriangles(new[] { b, b + 2, b + 1, b, b + 3, b + 2 }, f);
        }
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Inclusive min/max of the tile grid across all floors. False when there are no tiles.
    public static bool GridBounds(BuildingDef bdef, out int minX, out int maxX, out int minZ, out int maxZ)
    {
        minX = minZ = int.MaxValue;
        maxX = maxZ = int.MinValue;
        if (bdef?.tiles == null) return false;
        foreach (var t in bdef.tiles)
        {
            if (t.gridX < minX) minX = t.gridX;
            if (t.gridX > maxX) maxX = t.gridX;
            if (t.gridZ < minZ) minZ = t.gridZ;
            if (t.gridZ > maxZ) maxZ = t.gridZ;
        }
        return maxX >= minX;
    }

    private static int TopFloor(BuildingDef bdef)
    {
        int f = 0;
        foreach (var t in bdef.tiles) if (t.floor > f) f = t.floor;
        return f;
    }

    // Grid-corner coordinate of tile corner i (order matches TileDeform: 0=-x-z 1=+x-z 2=+x+z 3=-x+z).
    private static Vector2 CornerGrid(TileDef t, int i)
    {
        int x = (i == 1 || i == 2) ? t.gridX + 1 : t.gridX;
        int z = (i == 2 || i == 3) ? t.gridZ + 1 : t.gridZ;
        return new Vector2(x, z);
    }

    private static TileDeform EnsureDeform(TileDef t)
    {
        t.deform ??= new TileDeform();
        t.deform.dx    = Ensure4(t.deform.dx);
        t.deform.dz    = Ensure4(t.deform.dz);
        t.deform.dyTop = Ensure4(t.deform.dyTop);
        return t.deform;
    }

    private static float[] Ensure4(float[] a)
    {
        if (a != null && a.Length == 4) return a;
        var r = new float[4];
        if (a != null) for (int i = 0; i < a.Length && i < 4; i++) r[i] = a[i];
        return r;
    }

    // Safe read of an optional length-4 offset array (treats null / short arrays as 0).
    private static float Off(float[] a, int i) => (a != null && i < a.Length) ? a[i] : 0f;

    // 1 at distance 0, easing to 0 at `falloff`, clamped beyond.
    private static float Weight(float dist, float falloff, Falloff curve)
    {
        if (falloff <= 0f) return 0f;
        float x = Mathf.Clamp01(dist / falloff);
        return curve == Falloff.Linear ? 1f - x : 1f - (x * x * (3f - 2f * x)); // smoothstep
    }
}
