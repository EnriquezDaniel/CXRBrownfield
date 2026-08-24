using System.Collections.Generic;
using UnityEngine;

// Shared vertex-editing math for closed ground polygons (the lot/parcel tool and the site tool).
// Pure data: works on [x, z] vertex lists, no scene access.
public static class PolygonEdit
{
    // Finds where a click lands on the closed outline of `pts` and, when within `tol` meters of it,
    // reports the insert position: `index` is where the new vertex goes (between index-1 and the old
    // index), `pt` is the projection onto the nearest segment. Returns false when the polygon is
    // degenerate or the click is too far from every edge.
    public static bool TryInsertVertex(IList<Vector2> pts, Vector2 click, float tol, out int index, out Vector2 pt)
    {
        index = -1;
        pt = click;
        if (pts == null || pts.Count < 3 || tol <= 0f) return false;

        float bestD = float.MaxValue; int bestSeg = -1; Vector2 bestPt = click;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % n];
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(click - a, ab) / len2);
            Vector2 proj = a + t * ab;
            float d = Vector2.Distance(click, proj);
            if (d < bestD) { bestD = d; bestSeg = i; bestPt = proj; }
        }
        if (bestSeg < 0 || bestD > tol) return false;

        index = bestSeg + 1;
        pt = bestPt;
        return true;
    }

    // Edge-pick tolerance scaled to the polygon's size: 4% of the bbox diagonal, clamped to a
    // usable hand range. Mirrors the lot tool's historical feel.
    public static float PickTolerance(float bboxWidth, float bboxLength)
    {
        float diag = Mathf.Sqrt(bboxWidth * bboxWidth + bboxLength * bboxLength);
        return Mathf.Clamp(diag * 0.04f, 2f, 30f);
    }
}
