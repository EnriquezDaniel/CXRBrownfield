using System.Collections.Generic;
using UnityEngine;

// Geometry for the draped polygon outlines that mark a lot or a drawn site: how finely to sample a
// ring so it hugs terrain grade, how wide to draw the band, and the axis-aligned rectangle a
// press-drag-release gesture sizes. Pure/static so the numbers live in one place — WorldRenderer's
// committed frame and EditController's live preview both call these, which is what keeps the
// preview from changing thickness the moment it commits.
//
// Everything is XZ in world meters (Vector2.y IS world z), matching SitePlotDef.boundary.
public static class PolygonFrame
{
    // Sampling: distance between ring points. Derived from the ring's own perimeter, never from the
    // terrain size — a small plot on a big terrain still needs metre-scale samples or the chords
    // between them cut through every rise. MaxRingPoints caps the vertex budget for huge parcels.
    public const float MinSpacing    = 1f;
    public const float MaxSpacing    = 4f;
    public const int   MaxRingPoints = 1500;

    // Band width, from the polygon's own bbox diagonal so a small plot gets a thin stripe and a
    // large one stays visible when zoomed out.
    public const float MinWidth     = 0.4f;
    public const float MaxWidth     = 2f;
    public const float WidthPerDiag = 0.006f;

    public static float Spacing(float perimeter) =>
        Mathf.Clamp(perimeter / MaxRingPoints, MinSpacing, MaxSpacing);

    public static float Width(float bboxDiag) =>
        Mathf.Clamp(bboxDiag * WidthPerDiag, MinWidth, MaxWidth);

    // Axis-aligned bbox extents of a corner run. False (both zero) when there is nothing to measure.
    public static bool Bbox(IReadOnlyList<Vector2> corners, out float width, out float length)
    {
        width = length = 0f;
        if (corners == null || corners.Count == 0) return false;

        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var c in corners)
        {
            if (c.x < minX) minX = c.x;
            if (c.x > maxX) maxX = c.x;
            if (c.y < minZ) minZ = c.y;
            if (c.y > maxZ) maxZ = c.y;
        }
        width  = maxX - minX;
        length = maxZ - minZ;
        return true;
    }

    public static float BboxDiagonal(IReadOnlyList<Vector2> corners) =>
        Bbox(corners, out float w, out float l) ? Mathf.Sqrt(w * w + l * l) : 0f;

    // Total edge length. `closed` counts the edge from the last corner back to the first.
    public static float Perimeter(IReadOnlyList<Vector2> corners, bool closed)
    {
        if (corners == null || corners.Count < 2) return 0f;
        int n = corners.Count;
        int edges = closed ? n : n - 1;
        float sum = 0f;
        for (int i = 0; i < edges; i++) sum += Vector2.Distance(corners[i], corners[(i + 1) % n]);
        return sum;
    }

    // Subdivides every edge to at most `spacing` metres so the outline can be draped onto the
    // terrain without chords cutting through it. A closed ring repeats the first corner at the end,
    // which is what turns PathMesh.Build's open ribbon into a closed band.
    // Needs 3 corners closed, 2 open; anything less returns an empty list.
    public static List<Vector2> DenseRing(IReadOnlyList<Vector2> corners, float spacing, bool closed)
    {
        var pts = new List<Vector2>();
        if (corners == null) return pts;
        int n = corners.Count;
        if (n < (closed ? 3 : 2)) return pts;

        spacing = Mathf.Max(0.01f, spacing);
        int edges = closed ? n : n - 1;
        for (int i = 0; i < edges; i++)
        {
            Vector2 a = corners[i], b = corners[(i + 1) % n];
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / spacing));
            for (int s = 0; s < steps; s++)   // excludes the endpoint; the next edge contributes it
                pts.Add(Vector2.Lerp(a, b, s / (float)steps));
        }
        pts.Add(closed ? corners[0] : corners[n - 1]);
        return pts;
    }

    // The axis-aligned rectangle a drag from `a` to `b` sizes, wound consistently regardless of
    // drag direction. `square` (Shift) matches the shorter axis to the longer one, keeping its sign
    // so the rectangle still grows toward the cursor.
    public static Vector2[] RectCorners(Vector2 a, Vector2 b, bool square)
    {
        float dx = b.x - a.x, dz = b.y - a.y;
        if (square)
        {
            float m = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
            dx = dx < 0f ? -m : m;
            dz = dz < 0f ? -m : m;
        }
        float x0 = Mathf.Min(a.x, a.x + dx), x1 = Mathf.Max(a.x, a.x + dx);
        float z0 = Mathf.Min(a.y, a.y + dz), z1 = Mathf.Max(a.y, a.y + dz);
        return new[]
        {
            new Vector2(x0, z0),
            new Vector2(x1, z0),
            new Vector2(x1, z1),
            new Vector2(x0, z1),
        };
    }
}
