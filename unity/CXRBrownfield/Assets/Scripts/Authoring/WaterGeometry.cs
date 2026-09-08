using System;
using System.Collections.Generic;
using UnityEngine;

// Shape math for water bodies (WaterBodyDef): the river centerline (built exactly like a path
// ribbon so the carve, the preview and the committed mesh agree), shore samples for the auto
// surface height, signed distances used by the bed carve, and the flat surface meshes. Pure/static
// so WorldRenderer, EditController's preview and the EditMode tests share one implementation.
// Everything is XZ in world meters (Vector2.y IS world z).
public static class WaterGeometry
{
    public const string KIND_POND  = "pond";
    public const string KIND_RIVER = "river";

    public const float MIN_WIDTH = 0.5f,  MAX_WIDTH = 40f;     // river ribbon
    // Bed below the flat ground height. MIN_DEPTH also bounds how shallow the water can be, and
    // must exceed WaterCarve.SHORE_DROP so the shore never sinks under the bed.
    public const float MIN_DEPTH = 0.05f, MAX_DEPTH = 12f;
    public const float MAX_BANK  = 15f;
    // Highest the surface can sit above the flat ground height: the heightmap's own headroom.
    public const float MAX_SURFACE_Y = HeightBrush.RANGE_METERS * (1f - HeightBrush.BASE_NORMALIZED);
    public const float SHORE_SPACING = 1f;                     // meters between shore samples

    public static bool IsRiver(string kind) =>
        string.Equals(kind, KIND_RIVER, StringComparison.OrdinalIgnoreCase);

    public static int MinPoints(string kind) => IsRiver(kind) ? 2 : 3;

    public static float ClampWidth(float w) => Mathf.Clamp(w, MIN_WIDTH, MAX_WIDTH);
    public static float ClampDepth(float d) => Mathf.Clamp(d, MIN_DEPTH, MAX_DEPTH);

    // World Y of the bed: `depth` below the flat ground height.
    public static float BedY(float depth) => -ClampDepth(depth);

    // Lowest surface a bed of `depth` allows: the water must be at least MIN_DEPTH deep.
    public static float MinSurfaceY(float depth) => BedY(depth) + MIN_DEPTH;

    public static float ClampSurfaceY(float surfaceY, float depth) =>
        Mathf.Clamp(surfaceY, MinSurfaceY(depth), MAX_SURFACE_Y);

    // The world Y a body's surface renders and carves at.
    public static float SurfaceY(WaterBodyDef body) =>
        body == null ? 0f : ClampSurfaceY(body.surfaceY, body.depth);

    // Control points as Vector2, skipping malformed entries.
    public static List<Vector2> ControlPoints(float[][] pts)
    {
        var list = new List<Vector2>(pts?.Length ?? 0);
        if (pts == null) return list;
        foreach (var p in pts)
            if (p != null && p.Length >= 2) list.Add(new Vector2(p[0], p[1]));
        return list;
    }

    public static bool HasGeometry(WaterBodyDef body) =>
        body != null && ControlPoints(body.points).Count >= MinPoints(body.kind);

    // Dense river centerline, the same recipe WorldRenderer.RenderPaths uses for a path ribbon:
    // round sharp corners to the half width, then resample/smooth.
    public static List<Vector2> RiverCenterline(IReadOnlyList<Vector2> ctrl, float width, float smoothing)
    {
        if (ctrl == null || ctrl.Count < 2) return new List<Vector2>();
        var rounded = PathGeometry.RoundCorners(ctrl, ClampWidth(width) * 0.5f);
        return PathGeometry.Smooth(rounded, Mathf.Clamp01(smoothing));
    }

    // The two ribbon edges of a dense centerline, offset by half the width along each point's
    // local normal (average of the adjacent segment normals).
    public static void RibbonEdges(IReadOnlyList<Vector2> dense, float width, List<Vector2> left, List<Vector2> right)
    {
        left.Clear(); right.Clear();
        if (dense == null || dense.Count < 2) return;
        float hw = ClampWidth(width) * 0.5f;
        int n = dense.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = i > 0     ? dense[i] - dense[i - 1] : Vector2.zero;
            Vector2 b = i < n - 1 ? dense[i + 1] - dense[i] : Vector2.zero;
            Vector2 dir = a.normalized + b.normalized;
            if (dir.sqrMagnitude < 1e-8f) dir = a.sqrMagnitude > 0f ? a : b;
            if (dir.sqrMagnitude < 1e-8f) continue;
            dir.Normalize();
            var nrm = new Vector2(-dir.y, dir.x);
            left.Add(dense[i] + nrm * hw);
            right.Add(dense[i] - nrm * hw);
        }
    }

    // Points along the water's edge, where the auto surface height reads the terrain: the dense
    // ring of a pond, both ribbon edges of a river. Empty when the body has no usable geometry.
    public static List<Vector2> ShoreSamples(WaterBodyDef body, float spacing = SHORE_SPACING)
    {
        var ctrl = ControlPoints(body?.points);
        if (body == null || ctrl.Count < MinPoints(body.kind)) return new List<Vector2>();
        if (!IsRiver(body.kind)) return PolygonFrame.DenseRing(ctrl, spacing, closed: true);

        var dense = RiverCenterline(ctrl, body.width, body.smoothing);
        var left = new List<Vector2>(dense.Count); var right = new List<Vector2>(dense.Count);
        RibbonEdges(dense, body.width, left, right);
        left.AddRange(right);
        return left;
    }

    // Even-odd point-in-ring test (Vector2 twin of EnvironmentScale.PointInPolygon).
    public static bool PointInRing(Vector2 p, IReadOnlyList<Vector2> ring)
    {
        if (ring == null || ring.Count < 3) return false;
        bool inside = false;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 a = ring[i], b = ring[j];
            bool crosses = ((a.y > p.y) != (b.y > p.y)) &&
                           (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x);
            if (crosses) inside = !inside;
        }
        return inside;
    }

    // Distance from (x, z) to the nearest edge of a closed ring.
    public static float DistanceToRing(float x, float z, IReadOnlyList<Vector2> ring)
    {
        if (ring == null || ring.Count == 0) return float.MaxValue;
        var p = new Vector2(x, z);
        float best = float.MaxValue;
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            float d = PathGeometry.PointSegmentDistance(p, ring[i], ring[(i + 1) % n]);
            if (d < best) best = d;
        }
        return best;
    }

    // Distance from (x, z) to the nearest segment of an open polyline.
    public static float DistanceToPolyline(float x, float z, IReadOnlyList<Vector2> line)
    {
        if (line == null || line.Count == 0) return float.MaxValue;
        var p = new Vector2(x, z);
        if (line.Count == 1) return Vector2.Distance(p, line[0]);
        float best = float.MaxValue;
        for (int i = 0; i < line.Count - 1; i++)
        {
            float d = PathGeometry.PointSegmentDistance(p, line[i], line[i + 1]);
            if (d < best) best = d;
        }
        return best;
    }

    // Negative inside the ring, positive outside; magnitude is the distance to the edge.
    public static float SignedDistanceToPolygon(float x, float z, IReadOnlyList<Vector2> ring)
    {
        float d = DistanceToRing(x, z, ring);
        return PointInRing(new Vector2(x, z), ring) ? -d : d;
    }

    // Negative inside the ribbon (within halfWidth of the centerline), positive outside.
    public static float SignedDistanceToRibbon(float x, float z, IReadOnlyList<Vector2> centerline, float halfWidth) =>
        DistanceToPolyline(x, z, centerline) - halfWidth;

    // Flat pond fill at height `y`. Ear clipping first; a ring it cannot clip (self-intersecting)
    // falls back to a fan around the centroid so something still renders. Null for < 3 points.
    public static Mesh BuildPondMesh(IReadOnlyList<Vector2> ring, float y)
    {
        if (ring == null || ring.Count < 3) return null;

        var verts = new List<Vector3>(ring.Count + 1);
        var uvs   = new List<Vector2>(ring.Count + 1);
        foreach (var p in ring)
        {
            verts.Add(new Vector3(p.x, y, p.y));
            uvs.Add(p * 0.1f);
        }

        var tris = PolygonTriangulator.Triangulate(ring);
        if (tris.Count == 0)
        {
            Vector2 c = Vector2.zero;
            foreach (var p in ring) c += p;
            c /= ring.Count;
            int ci = verts.Count;
            verts.Add(new Vector3(c.x, y, c.y));
            uvs.Add(c * 0.1f);
            for (int i = 0; i < ring.Count; i++)
                FaceUp(verts, tris, ci, i, (i + 1) % ring.Count);
        }
        else
        {
            // Triangulate already faces +Y; re-check so a future winding change can't flip the fill.
            var fixedTris = new List<int>(tris.Count);
            for (int i = 0; i + 2 < tris.Count; i += 3) FaceUp(verts, fixedTris, tris[i], tris[i + 1], tris[i + 2]);
            tris = fixedTris;
        }

        var mesh = new Mesh { name = "WaterPond" };
        if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Flat river ribbon at height `y` from a DENSE centerline (RiverCenterline). PathMesh.Build
    // with no height callback keeps every vertex at its centerline point's Y, so the ribbon is level.
    public static Mesh BuildRiverMesh(IReadOnlyList<Vector2> dense, float width, float y)
    {
        if (dense == null || dense.Count < 2) return null;
        var centerline = new List<Vector3>(dense.Count);
        foreach (var d in dense) centerline.Add(new Vector3(d.x, y, d.y));
        var mesh = PathMesh.Build(centerline, ClampWidth(width), null);
        if (mesh != null) mesh.name = "WaterRiver";
        return mesh;
    }

    private static void FaceUp(List<Vector3> verts, List<int> tris, int i0, int i1, int i2)
    {
        Vector3 nrm = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
        if (nrm.y >= 0f) { tris.Add(i0); tris.Add(i1); tris.Add(i2); }
        else             { tris.Add(i0); tris.Add(i2); tris.Add(i1); }
    }
}
