using System.Collections.Generic;
using UnityEngine;

// The bed a water body digs into the heightmap. Derived from the WaterBodyDef every time
// WorldRenderer.ApplyHeightmap rebuilds the ground (after the height strokes replay), never
// stored: delete the body and the hole goes with it, change its depth and the bed follows. Both
// heights come straight from the body, measured from the flat ground height (world y = 0): the bed
// is `depth` below it, the surface at `surfaceY` (WaterGeometry.SurfaceY keeps it above the bed).
// Same HeightWindow / normalized-height contract as HeightBrush. Pure/static so the replay and the
// EditMode tests run the same arithmetic.
public static class WaterCarve
{
    // The ground at the outline sits this far below the surface, so the water visibly meets the
    // shore there instead of z-fighting with it.
    public const float SHORE_DROP = 0.03f;

    // Shapes the ground under and around `body` so the outline IS the shoreline: at the outline the
    // ground sits SHORE_DROP below the surface; inward it slopes down to the bed over bankWidth;
    // outward it slopes up to the existing ground over bankWidth. Everything is h = min(h, target),
    // so the carve never raises ground and a deeper hand-dug spot inside the body is kept.
    // `clipWorld` is the world meter parcel polygon (null = no clip); ox/oz the terrain's world
    // position.
    public static void Carve(ref HeightWindow win, WaterBodyDef body, float[][] clipWorld, float ox, float oz)
    {
        if (win.heights == null || win.res < 2 || win.sizeX <= 0f || win.sizeZ <= 0f) return;
        if (!WaterGeometry.HasGeometry(body)) return;

        bool river = WaterGeometry.IsRiver(body.kind);
        var ctrl = WaterGeometry.ControlPoints(body.points);
        for (int i = 0; i < ctrl.Count; i++) ctrl[i] = new Vector2(ctrl[i].x - ox, ctrl[i].y - oz);   // terrain-local
        List<Vector2> shape = river ? WaterGeometry.RiverCenterline(ctrl, body.width, body.smoothing) : ctrl;
        if (shape.Count < WaterGeometry.MinPoints(body.kind)) return;

        float halfWidth = river ? WaterGeometry.ClampWidth(body.width) * 0.5f : 0f;
        float bank      = Mathf.Clamp(body.bankWidth, 0f, WaterGeometry.MAX_BANK);
        float reach     = halfWidth + bank;
        float surfaceY  = WaterGeometry.SurfaceY(body);
        // The surface is at least MIN_DEPTH (> SHORE_DROP) above the bed, so the shore stays above it.
        float shoreN    = HeightBrush.NormalizedFromMeters(surfaceY - SHORE_DROP);
        float bedN      = HeightBrush.NormalizedFromMeters(WaterGeometry.BedY(body.depth));

        // Sample box the body plus its bank can touch, intersected with the window.
        if (!PathGeometry.PolylineBounds(shape, reach, out Vector2 bmin, out Vector2 bmax)) return;
        float spx = (win.res - 1) / win.sizeX, spz = (win.res - 1) / win.sizeZ;   // samples per meter
        int xa = Mathf.Max(0,              Mathf.FloorToInt(bmin.x * spx) - win.x0);
        int xb = Mathf.Min(win.Width - 1,  Mathf.CeilToInt (bmax.x * spx) - win.x0);
        int za = Mathf.Max(0,              Mathf.FloorToInt(bmin.y * spz) - win.z0);
        int zb = Mathf.Min(win.Height - 1, Mathf.CeilToInt (bmax.y * spz) - win.z0);
        if (xa > xb || za > zb) return;

        // Distance field over the box, built per segment so a long river only visits the cells
        // near each of its segments instead of testing every cell against every segment.
        int bw = xb - xa + 1, bh = zb - za + 1;
        var dist = new float[bh, bw];
        for (int z = 0; z < bh; z++) for (int x = 0; x < bw; x++) dist[z, x] = float.MaxValue;

        int segs = river ? shape.Count - 1 : shape.Count;
        for (int s = 0; s < segs; s++)
        {
            Vector2 a = shape[s], b = shape[(s + 1) % shape.Count];
            int sxa = Mathf.Max(xa, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - reach) * spx) - win.x0);
            int sxb = Mathf.Min(xb, Mathf.CeilToInt ((Mathf.Max(a.x, b.x) + reach) * spx) - win.x0);
            int sza = Mathf.Max(za, Mathf.FloorToInt((Mathf.Min(a.y, b.y) - reach) * spz) - win.z0);
            int szb = Mathf.Min(zb, Mathf.CeilToInt ((Mathf.Max(a.y, b.y) + reach) * spz) - win.z0);
            for (int z = sza; z <= szb; z++)
            {
                float mz = win.MetersZ(z);
                for (int x = sxa; x <= sxb; x++)
                {
                    float d = PathGeometry.PointSegmentDistance(new Vector2(win.MetersX(x), mz), a, b);
                    if (d < dist[z - za, x - xa]) dist[z - za, x - xa] = d;
                }
            }
        }

        for (int z = za; z <= zb; z++)
        {
            float mz = win.MetersZ(z);
            for (int x = xa; x <= xb; x++)
            {
                float d = dist[z - za, x - xa];
                // A river cell beyond every segment's reach is outside. A pond's interior can be
                // farther than the bank from any edge, so it still needs the ring test.
                if (river && d == float.MaxValue) continue;
                float mx = win.MetersX(x);
                bool inside = river ? d <= halfWidth : WaterGeometry.PointInRing(new Vector2(mx, mz), shape);
                if (!inside && d == float.MaxValue) continue;
                // Distance from the shoreline; a pond cell beyond every segment's reach is deep interior.
                float edge = river ? Mathf.Abs(d - halfWidth) : d;
                if (!inside && (bank <= 0f || edge >= bank)) continue;
                if (clipWorld != null && !EnvironmentScale.PointInPolygon(mx + ox, mz + oz, clipWorld)) continue;

                float h = win.heights[z, x];
                float target;
                if (inside)
                {
                    // Shore at the outline, bed once `bank` meters in (Falloff is 1 at t = 0, 0 at t = 1).
                    float t = bank <= 0f ? 1f : Mathf.Clamp01(edge / bank);
                    target = Mathf.Lerp(bedN, shoreN, HeightBrush.Falloff(t));
                }
                else
                    target = Mathf.Lerp(h, shoreN, HeightBrush.Falloff(edge / bank));   // existing ground down to the shore
                if (target < h) win.heights[z, x] = target;
            }
        }
    }
}
