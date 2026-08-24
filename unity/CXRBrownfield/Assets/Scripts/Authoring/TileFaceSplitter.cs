using System;
using System.Collections.Generic;
using UnityEngine;

// Splits a single-submesh tile mesh into ONE SUBMESH PER PAINTABLE FACE and names each face, so the
// result honours the TileShapePalette contract ("faceNames in submesh order, one material slot per
// face") that TileSpawner.ApplyFaceMaterial and TileBuildingEditor.FaceFromHit rely on. Tile prefabs
// authored as a built-in Cube, a ProBuilder prism, or an imported FBX all arrive with one submesh, so
// only faceNames[0] could ever be painted; this is the geometry half of the fix (the editor tool
// Assets/Editor/TileFaceSetup.cs drives it and writes the per-face prefabs + palette entries).
//
// Pure function over raw arrays (no asset or editor dependency) so it is unit-testable and can be fed
// a non-readable FBX mesh that the editor tool read in edit mode.
//
// Algorithm (validated on the project's three tile meshes):
//   1. Each triangle's geometric normal is taken in the CELL frame — root-local, then rotated by the
//      shape's palette defaultRotation — so names mean what the user sees on a placed tile.
//   2. Triangles cluster into planar GROUPS: same normal (within PlanarAngleDeg) AND same plane
//      offset — so a wall triangulated with T-junctions is one group, but two parallel faces on
//      different planes (a stepped roof) are two.
//   3. Groups that share a mesh edge AND differ by < MergeAngleDeg merge into one SURFACE, so a
//      faceted curve chains into a single face while cube/prism faces (>= 90° apart) stay separate.
//      Exactly axis-aligned groups never merge: a flat wall that runs tangent into an arc (the
//      curved-corner tile) stays its own paintable face.
//   4. A surface whose normals span > CurvedSpanDeg is the curved face (`curvedName`); otherwise it is
//      named by its dominant axis (TileFaceGeometry convention: +Z north, +X east, -Z south, -X west,
//      +Y top, -Y bottom) when that axis dominates by AxisMinDot, else `diagonalName`. Duplicate names
//      get _2, _3 … suffixes.
//   5. Submeshes are emitted in canonical order (north, east, south, west, top, bottom), then the rest.
public static class TileFaceSplitter
{
    public const float PlanarAngleDeg = 5f;    // triangles within this angle form one planar group
    public const float MergeAngleDeg  = 45f;   // edge-sharing groups closer than this chain into one surface
    public const float CurvedSpanDeg  = 30f;   // a surface spanning more than this is a curved face
    public const float AxisMinDot     = 0.75f; // |dominant component| needed to take an axis name (≈41°;
                                               // a 45° plan-wedge face at 0.707 is "diagonal", a 26.6°
                                               // roof slope at 0.894 is still "east"/"west")
    public const float AxisFlatDot    = 0.9999f; // exactly axis-aligned (< 1°): a standalone wall/cap that
                                                 // never merges into a curved surface

    public static readonly string[] CanonicalOrder = { "north", "east", "south", "west", "top", "bottom" };

    public struct FaceInfo
    {
        public string  name;
        public Vector3 normal;          // cell-frame, triangle-count weighted average
        public int     triangleCount;
        public float   spanDeg;         // max angle between member planar groups (0 for a flat face)
    }

    public sealed class Result
    {
        public Mesh           mesh;       // one submesh per face, in faceNames order
        public List<string>   faceNames;  // parallel to submeshes
        public List<FaceInfo> faces;      // diagnostics (same order)
    }

    // `vertices`/`triangles` are the source mesh arrays (any transform); `toRootLocal` maps them into
    // the prefab root's local space (pass Matrix4x4.identity when already there); `cellFrameRot` is
    // the shape's defaultRotation, used ONLY for naming (the emitted vertices stay root-local).
    // `normals`/`uvs` may be null or mismatched in length — then normals are recalculated / uvs skipped.
    public static Result Split(Vector3[] vertices, int[] triangles, Vector3[] normals, Vector2[] uvs,
                               Matrix4x4 toRootLocal, Quaternion cellFrameRot,
                               string curvedName = "curve", string diagonalName = "diagonal",
                               string meshName = "TileFaces")
    {
        if (vertices == null || triangles == null) throw new ArgumentNullException();

        // --- root-local positions (and normals via the inverse-transpose) ---
        var pos = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++) pos[i] = toRootLocal.MultiplyPoint3x4(vertices[i]);

        Vector3[] outNormals = null;
        if (normals != null && normals.Length == vertices.Length)
        {
            Matrix4x4 nm = toRootLocal.inverse.transpose;
            outNormals = new Vector3[normals.Length];
            for (int i = 0; i < normals.Length; i++) outNormals[i] = nm.MultiplyVector(normals[i]).normalized;
        }

        // Tolerances scale with the mesh (the curved-corner FBX is 0.02 m across, the cube 1 m).
        float extent = 0f;
        foreach (var p in pos) extent = Mathf.Max(extent, Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z));
        if (extent <= 0f) extent = 1f;
        float invQuantum = 1e5f / extent;          // edge-key quantisation: 1e-5 of the mesh extent
        float planeEps   = 1e-3f * extent;         // same-plane test: 1e-3 of the mesh extent

        // --- 1+2: planar groups = same normal (within PlanarAngleDeg) AND same plane offset. Plane
        //        offset rather than edge connectivity, so a wall triangulated with T-junctions (the
        //        curved-corner FBX) stays ONE face, while two parallel faces on different planes (a
        //        stepped roof) stay separate. Normals are taken in the cell frame for naming; the plane
        //        offset is rotation-invariant so it can use the same frame. ---
        var groups = new List<Group>();
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            Vector3 a = pos[triangles[t]], b = pos[triangles[t + 1]], c = pos[triangles[t + 2]];
            Vector3 n = Vector3.Cross(b - a, c - a);
            // Normalise by hand: Vector3.normalized returns ZERO below a fixed 1e-5 magnitude, which a
            // thin sliver on a 0.02 m mesh easily undercuts — and a zero normal is "0° from everything"
            // to Vector3.Angle, so it would bridge unrelated faces. Only truly zero-area (relative to
            // the mesh size) triangles are dropped.
            float len = n.magnitude;
            if (len < 1e-9f * extent * extent) continue;       // degenerate — dropped
            n = cellFrameRot * (n / len);
            float d = Vector3.Dot(cellFrameRot * a, n);

            Group g = null;
            foreach (var cand in groups)
                if (Vector3.Angle(cand.normal, n) < PlanarAngleDeg && Mathf.Abs(cand.planeOffset - d) < planeEps)
                { g = cand; break; }
            if (g == null) { g = new Group { normal = n, planeOffset = d }; groups.Add(g); }

            g.triangles.Add(t / 3);
            g.edges.Add(EdgeKey(a, b, invQuantum)); g.edges.Add(EdgeKey(b, c, invQuantum)); g.edges.Add(EdgeKey(c, a, invQuantum));
        }

        // --- 3: merge edge-sharing, gently-angled groups into surfaces (union-find) ---
        var parent = new int[groups.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        // An exactly axis-aligned flat group is a wall/cap in its own right and never joins a curved
        // surface — the curved-corner tile's flat east/south walls run tangent into its arc (≈11°
        // between them), and would otherwise be swallowed by the chain.
        for (int i = 0; i < groups.Count; i++)
            for (int j = i + 1; j < groups.Count; j++)
                if (!IsAxisFlat(groups[i].normal) && !IsAxisFlat(groups[j].normal) &&
                    Vector3.Angle(groups[i].normal, groups[j].normal) < MergeAngleDeg &&
                    groups[i].edges.Overlaps(groups[j].edges))
                    parent[Find(j)] = Find(i);

        var surfaces = new List<Surface>();
        var byRoot   = new Dictionary<int, Surface>();
        for (int i = 0; i < groups.Count; i++)
        {
            int r = Find(i);
            if (!byRoot.TryGetValue(r, out var s)) { s = new Surface(); byRoot[r] = s; surfaces.Add(s); }
            s.groups.Add(groups[i]);
        }

        // --- 4: name ---
        foreach (var s in surfaces)
        {
            Vector3 avg = Vector3.zero; int count = 0; float span = 0f;
            foreach (var g in s.groups)
            {
                avg += g.normal * g.triangles.Count; count += g.triangles.Count;
                foreach (var h in s.groups) span = Mathf.Max(span, Vector3.Angle(g.normal, h.normal));
            }
            s.normal = avg.sqrMagnitude > 0f ? avg.normalized : Vector3.zero;
            s.triangleCount = count;
            s.span = span;
            s.name = NameForNormal(s.normal, span, curvedName, diagonalName);
        }
        DisambiguateNames(surfaces);

        // --- 5: canonical order ---
        surfaces.Sort((x, y) =>
        {
            int cx = Array.IndexOf(CanonicalOrder, x.name), cy = Array.IndexOf(CanonicalOrder, y.name);
            if (cx < 0) cx = int.MaxValue; if (cy < 0) cy = int.MaxValue;
            return cx != cy ? cx.CompareTo(cy) : x.order.CompareTo(y.order);
        });

        // --- build the mesh ---
        var mesh = new Mesh { name = meshName };
        if (vertices.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = pos;
        if (uvs != null && uvs.Length == vertices.Length) mesh.uv = uvs;
        mesh.subMeshCount = surfaces.Count;
        for (int si = 0; si < surfaces.Count; si++)
        {
            var tris = new List<int>(surfaces[si].triangleCount * 3);
            foreach (var g in surfaces[si].groups)
                foreach (int tri in g.triangles)
                {
                    tris.Add(triangles[tri * 3]);
                    tris.Add(triangles[tri * 3 + 1]);
                    tris.Add(triangles[tri * 3 + 2]);
                }
            mesh.SetTriangles(tris, si);
        }
        if (outNormals != null) mesh.normals = outNormals; else mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var result = new Result { mesh = mesh, faceNames = new List<string>(), faces = new List<FaceInfo>() };
        foreach (var s in surfaces)
        {
            result.faceNames.Add(s.name);
            result.faces.Add(new FaceInfo { name = s.name, normal = s.normal, triangleCount = s.triangleCount, spanDeg = s.span });
        }
        return result;
    }

    // Name for a surface's cell-frame normal: curved faces by span, flat faces by dominant axis.
    public static string NameForNormal(Vector3 n, float spanDeg, string curvedName = "curve", string diagonalName = "diagonal")
    {
        if (spanDeg > CurvedSpanDeg) return curvedName;
        float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
        float best = Mathf.Max(ax, ay, az);
        if (best < AxisMinDot) return diagonalName;
        if (best == ay) return n.y >= 0f ? "top"   : "bottom";
        if (best == ax) return n.x >= 0f ? "east"  : "west";
        return               n.z >= 0f ? "north" : "south";
    }

    // -----------------------------------------------------------------------

    private sealed class Group
    {
        public Vector3 normal;
        public float   planeOffset;   // dot(point, normal) — all members lie on this plane
        public readonly List<int> triangles = new List<int>();
        public readonly HashSet<(Vector3Int, Vector3Int)> edges = new HashSet<(Vector3Int, Vector3Int)>();
    }

    private sealed class Surface
    {
        public readonly List<Group> groups = new List<Group>();
        public Vector3 normal;
        public int     triangleCount;
        public float   span;
        public string  name;
        public int     order;   // first-appearance index, for a stable sort among non-canonical names
    }

    private static bool IsAxisFlat(Vector3 n) =>
        Mathf.Max(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z)) >= AxisFlatDot;

    private static Vector3Int Quantize(Vector3 p, float inv) =>
        new Vector3Int(Mathf.RoundToInt(p.x * inv), Mathf.RoundToInt(p.y * inv), Mathf.RoundToInt(p.z * inv));

    private static (Vector3Int, Vector3Int) EdgeKey(Vector3 a, Vector3 b, float inv)
    {
        var qa = Quantize(a, inv); var qb = Quantize(b, inv);
        return Less(qa, qb) ? (qa, qb) : (qb, qa);
    }

    private static bool Less(Vector3Int a, Vector3Int b) =>
        a.x != b.x ? a.x < b.x : a.y != b.y ? a.y < b.y : a.z < b.z;

    // Two flat surfaces can legitimately map to the same axis (e.g. a stepped shape); keep both
    // paintable by suffixing the later ones, largest-first so the main face keeps the plain name.
    private static void DisambiguateNames(List<Surface> surfaces)
    {
        for (int i = 0; i < surfaces.Count; i++) surfaces[i].order = i;
        var ordered = new List<Surface>(surfaces);
        ordered.Sort((x, y) => y.triangleCount.CompareTo(x.triangleCount));
        var used = new Dictionary<string, int>();
        foreach (var s in ordered)
        {
            if (used.TryGetValue(s.name, out int n)) { used[s.name] = n + 1; s.name = $"{s.name}_{n + 1}"; }
            else used[s.name] = 1;
        }
    }
}
