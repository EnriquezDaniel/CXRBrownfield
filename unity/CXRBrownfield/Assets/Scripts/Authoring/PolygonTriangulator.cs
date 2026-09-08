using System.Collections.Generic;
using UnityEngine;

// Ear clipping for a simple (non self-intersecting) polygon in the XZ plane, used to fill a pond
// outline with a flat mesh. Pure/static so the pond mesh can be unit tested without a scene.
//
// Triangles are wound to face +Y in Unity's left-handed frame, which for a ring given as (x, z)
// pairs means CLOCKWISE when the plane is viewed with x to the right and z up. Either input
// winding is accepted. Consecutive duplicate points and a repeated closing point are ignored.
// A ring that cannot be clipped (self-intersecting, or all points collinear) returns an EMPTY list
// rather than a partial fill, so the caller can fall back to a centroid fan.
public static class PolygonTriangulator
{
    private const float EPS = 1e-7f;

    public static List<int> Triangulate(IReadOnlyList<Vector2> ring)
    {
        var tris = new List<int>();
        if (ring == null || ring.Count < 3) return tris;

        // Working index list: skip consecutive duplicates and a closing point equal to the first.
        var idx = new List<int>(ring.Count);
        for (int i = 0; i < ring.Count; i++)
        {
            if (idx.Count > 0 && (ring[i] - ring[idx[idx.Count - 1]]).sqrMagnitude < EPS) continue;
            idx.Add(i);
        }
        while (idx.Count > 1 && (ring[idx[0]] - ring[idx[idx.Count - 1]]).sqrMagnitude < EPS)
            idx.RemoveAt(idx.Count - 1);
        if (idx.Count < 3) return tris;

        // Normalize to a positive signed area (counter-clockwise in the x-right / z-up view) so a
        // convex corner is always a positive cross product below.
        float area = 0f;
        for (int i = 0; i < idx.Count; i++)
        {
            Vector2 a = ring[idx[i]], b = ring[idx[(i + 1) % idx.Count]];
            area += a.x * b.y - b.x * a.y;
        }
        if (Mathf.Abs(area) < EPS) return tris;   // collinear
        if (area < 0f) idx.Reverse();

        int expected = idx.Count - 2;
        while (idx.Count > 3)
        {
            int n = idx.Count;
            bool clipped = false;
            for (int i = 0; i < n; i++)
            {
                int i0 = idx[(i + n - 1) % n], i1 = idx[i], i2 = idx[(i + 1) % n];
                Vector2 a = ring[i0], b = ring[i1], c = ring[i2];
                if (Cross(b - a, c - a) <= EPS) continue;     // reflex or flat corner: not an ear

                bool ear = true;
                for (int j = 0; j < n && ear; j++)
                {
                    int k = idx[j];
                    if (k == i0 || k == i1 || k == i2) continue;
                    if (PointInTriangle(ring[k], a, b, c)) ear = false;
                }
                if (!ear) continue;

                Emit(tris, i0, i1, i2);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) { tris.Clear(); return tris; }   // stuck: self-intersecting or degenerate
        }

        {
            Vector2 a = ring[idx[0]], b = ring[idx[1]], c = ring[idx[2]];
            if (Mathf.Abs(Cross(b - a, c - a)) > EPS) Emit(tris, idx[0], idx[1], idx[2]);
        }
        if (tris.Count != expected * 3) tris.Clear();
        return tris;
    }

    // Number of triangles a complete fill of `ring` has, after duplicate trimming. 0 when degenerate.
    public static int ExpectedTriangles(IReadOnlyList<Vector2> ring)
    {
        if (ring == null || ring.Count < 3) return 0;
        int n = 0;
        Vector2 last = default;
        for (int i = 0; i < ring.Count; i++)
        {
            if (n > 0 && (ring[i] - last).sqrMagnitude < EPS) continue;
            last = ring[i]; n++;
        }
        if (n > 1 && (ring[0] - last).sqrMagnitude < EPS) n--;
        return Mathf.Max(0, n - 2);
    }

    // The working ring is counter-clockwise in (x, z); Unity's front face is the clockwise order in
    // that view, so a CCW corner (i0, i1, i2) is emitted reversed to face +Y.
    private static void Emit(List<int> tris, int i0, int i1, int i2)
    {
        tris.Add(i0); tris.Add(i2); tris.Add(i1);
    }

    private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    // Inclusive of the boundary (a vertex lying on an ear's diagonal blocks that ear).
    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float c1 = Cross(b - a, p - a);
        float c2 = Cross(c - b, p - b);
        float c3 = Cross(a - c, p - c);
        return c1 >= -EPS && c2 >= -EPS && c3 >= -EPS;
    }
}
