using System;
using System.Collections.Generic;
using UnityEngine;

// Builds a top-facing ribbon mesh from a (dense) polyline centerline + width. Used by WorldRenderer
// to render PathDefs (sidewalks, roads, trails) over the terrain. Pure/static so it can be unit
// tested.
//
// The centerline should already be finely resampled (see PathGeometry.Smooth) so the straight
// segments between points are short enough to hug the terrain. Each ribbon EDGE vertex samples the
// terrain at its own XZ via `heightAt`, so the ribbon tilts to lie flat across a side-slope instead
// of one edge burying into the hill while the other floats. Rounded end caps stop paths terminating
// in a blunt rectangle; corners use a clamped true miter so width stays constant without spikes.
public static class PathMesh
{
    // World-meters of path length that one V tile covers, so textures repeat naturally along a path.
    public const float DefaultTileLength = 4f;
    public const int   DefaultCapSegments = 6;     // arc subdivisions per rounded end cap (0 = blunt)
    public const float DefaultMiterLimit  = 4f;     // max miter extension as a multiple of half-width

    // `heightAt(x,z)` returns the final world Y for a vertex at that XZ (terrain height + lift). When
    // null, each vertex falls back to the Y carried by its nearest centerline point (flat ribbons /
    // unit tests). Caller is responsible for folding any z-fighting epsilon into `heightAt`.
    public static Mesh Build(IReadOnlyList<Vector3> centerline, float width,
                             Func<float, float, float> heightAt = null,
                             float tileLength = DefaultTileLength,
                             int capSegments = DefaultCapSegments,
                             float miterLimit = DefaultMiterLimit)
    {
        if (centerline == null || centerline.Count < 2) return null;

        int n    = centerline.Count;
        float hw = Mathf.Max(0.01f, width) * 0.5f;
        if (tileLength <= 0f) tileLength = DefaultTileLength;

        var verts = new List<Vector3>(n * 2 + 2 * (capSegments + 2));
        var uvs   = new List<Vector2>(verts.Capacity);
        var tris  = new List<int>((n - 1) * 6 + 2 * Mathf.Max(0, capSegments) * 3);

        float Y(float x, float z, float fallback) => heightAt != null ? heightAt(x, z) : fallback;

        // ---- ribbon strip: two edge verts per centerline point -------------
        float runLength = 0f;
        var rightDir = new Vector3[n];   // remembered for the end caps
        for (int i = 0; i < n; i++)
        {
            Vector3 a = (i > 0)     ? Flat(centerline[i]     - centerline[i - 1]) : Vector3.zero;
            Vector3 b = (i < n - 1) ? Flat(centerline[i + 1] - centerline[i])     : Vector3.zero;
            a = a.sqrMagnitude > 1e-8f ? a.normalized : Vector3.zero;
            b = b.sqrMagnitude > 1e-8f ? b.normalized : Vector3.zero;

            Vector3 right; float offset;
            if (a == Vector3.zero)      { right = Perp(b); offset = hw; }   // first point
            else if (b == Vector3.zero) { right = Perp(a); offset = hw; }   // last point
            else
            {
                // True miter: bisector of the two edge normals, extended so the constant-width edge
                // passes through it. Clamp the extension so very sharp corners don't shoot a spike.
                Vector3 na = Perp(a), nb = Perp(b);
                Vector3 m = na + nb;
                if (m.sqrMagnitude < 1e-6f) { right = na; offset = hw; }    // ~180° reversal
                else
                {
                    m.Normalize();
                    float d = Vector3.Dot(m, na);
                    offset = Mathf.Abs(d) > 1e-3f ? hw / d : hw;
                    offset = Mathf.Min(offset, hw * miterLimit);
                    right  = m;
                }
            }
            rightDir[i] = right;

            if (i > 0) runLength += Vector3.Distance(centerline[i], centerline[i - 1]);
            float v = runLength / tileLength;

            Vector3 c = centerline[i];
            Vector3 left  = c - right * offset;
            Vector3 rgt   = c + right * offset;
            left.y = Y(left.x, left.z, c.y);
            rgt.y  = Y(rgt.x,  rgt.z,  c.y);

            verts.Add(left); uvs.Add(new Vector2(0f, v));   // 2*i
            verts.Add(rgt);  uvs.Add(new Vector2(1f, v));   // 2*i + 1
        }

        for (int i = 0; i < n - 1; i++)
        {
            int a = 2 * i, b = 2 * i + 1, c = 2 * i + 2, d = 2 * i + 3;
            // Winding chosen so the ribbon's normal points +Y (visible from above).
            tris.Add(a); tris.Add(c); tris.Add(b);
            tris.Add(b); tris.Add(c); tris.Add(d);
        }

        // ---- rounded end caps ---------------------------------------------
        if (capSegments > 0)
        {
            float vStart = uvs[0].y, vEnd = uvs[uvs.Count - 1].y;
            Vector3 fwdStart = (centerline[1] - centerline[0]); fwdStart.y = 0f;
            fwdStart = fwdStart.sqrMagnitude > 1e-8f ? fwdStart.normalized : Vector3.forward;
            Vector3 fwdEnd = (centerline[n - 1] - centerline[n - 2]); fwdEnd.y = 0f;
            fwdEnd = fwdEnd.sqrMagnitude > 1e-8f ? fwdEnd.normalized : Vector3.forward;

            // Start: bulge backward, sweep from the left edge (-right) through -fwd to the right edge.
            AddCap(verts, uvs, tris, centerline[0],     -rightDir[0],     -fwdStart, hw, capSegments, vStart, Y);
            // End: bulge forward, sweep from the right edge (+right) through +fwd to the left edge.
            AddCap(verts, uvs, tris, centerline[n - 1],  rightDir[n - 1],  fwdEnd,   hw, capSegments, vEnd,   Y);
        }

        var mesh = new Mesh { name = "PathRibbon" };
        if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Back-compat / convenience: build a flat ribbon (no terrain draping) directly from a Vector3
    // centerline whose Y is already set. Kept so existing call sites/tests compile unchanged.
    public static Mesh Build(IReadOnlyList<Vector3> centerline, float width, float tileLength)
        => Build(centerline, width, null, tileLength, DefaultCapSegments, DefaultMiterLimit);

    // A terrain-draped disc (triangle fan) used to blend path intersections into clean junctions.
    // `heightAt(x,z)` gives the world Y for each vertex so the patch hugs the ground like the ribbons.
    public static Mesh BuildDisc(float cx, float cz, float radius, Func<float, float, float> heightAt, int segments = 18)
    {
        if (radius <= 0f || segments < 3) return null;
        var verts = new List<Vector3>(segments + 1);
        var uvs   = new List<Vector2>(segments + 1);
        var tris  = new List<int>(segments * 3);

        float Y(float x, float z) => heightAt != null ? heightAt(x, z) : 0f;
        verts.Add(new Vector3(cx, Y(cx, cz), cz));
        uvs.Add(new Vector2(0.5f, 0.5f));
        for (int i = 0; i < segments; i++)
        {
            float a = 2f * Mathf.PI * i / segments;
            float x = cx + Mathf.Cos(a) * radius, z = cz + Mathf.Sin(a) * radius;
            verts.Add(new Vector3(x, Y(x, z), z));
            uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a)));
        }
        for (int i = 0; i < segments; i++)
            AddTriFacingUp(verts, tris, 0, i + 1, (i + 1) % segments + 1);

        var mesh = new Mesh { name = "PathJunction" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddCap(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                               Vector3 center, Vector3 sideAxis, Vector3 outward,
                               float hw, int segments, float v, Func<float, float, float, float> Y)
    {
        int centerIdx = verts.Count;
        verts.Add(new Vector3(center.x, Y(center.x, center.z, center.y), center.z));
        uvs.Add(new Vector2(0.5f, v));

        int firstArc = verts.Count;
        for (int k = 0; k <= segments; k++)
        {
            float theta = Mathf.PI * k / segments;                 // 0..π
            Vector3 dir = Mathf.Cos(theta) * sideAxis + Mathf.Sin(theta) * outward;
            Vector3 p = center + dir * hw;
            p.y = Y(p.x, p.z, center.y);
            verts.Add(p);
            uvs.Add(new Vector2(k / (float)segments, v));
        }
        for (int k = 0; k < segments; k++)
            AddTriFacingUp(verts, tris, centerIdx, firstArc + k, firstArc + k + 1);
    }

    // Emit a triangle wound so its normal points +Y, regardless of the input vertex order.
    private static void AddTriFacingUp(List<Vector3> verts, List<int> tris, int i0, int i1, int i2)
    {
        Vector3 nrm = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
        if (nrm.y >= 0f) { tris.Add(i0); tris.Add(i1); tris.Add(i2); }
        else             { tris.Add(i0); tris.Add(i2); tris.Add(i1); }
    }

    private static Vector3 Perp(Vector3 dir) => new Vector3(dir.z, 0f, -dir.x);
    private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
}
