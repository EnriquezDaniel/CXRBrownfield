using UnityEngine;

// The three ground height brushes (Shape ground tool). Pure/static so the live brush in
// EditController, the authoritative replay in WorldRenderer.ApplyHeightmap, and the EditMode tests
// all run exactly the same arithmetic: a stroke must look the same while you drag it as it does
// after a reload or an undo.
//
// Heights are Unity's normalized heightmap values in [0, 1] over a fixed RANGE_METERS, with flat
// ground at BASE_NORMALIZED (0.5) so the brush can dig as far as it can build. WorldRenderer parks
// the Terrain at BASE_WORLD_Y so that base plane sits at world y = 0, where every environment
// authored before sculpting existed already sits.

public enum HeightBrushKind { Raise, Smooth, Flatten }

// A window of TerrainData heights: `heights` is [z, x] (row = z), exactly what GetHeights returns.
// Sample (x0 + i, z0 + j) sits at terrain-local meters (x0 + i) / (res - 1) * sizeX (heightmap
// samples are vertex centered, unlike alphamap cells which sit at (i + 0.5) / res).
public struct HeightWindow
{
    public float[,] heights;
    public int   x0, z0;         // window origin in samples
    public int   res;            // full heightmap resolution (samples per side)
    public float sizeX, sizeZ;   // terrain size in meters

    public int Width  => heights != null ? heights.GetLength(1) : 0;
    public int Height => heights != null ? heights.GetLength(0) : 0;
    public float MetersX(int localX) => (x0 + localX) / (float)(res - 1) * sizeX;
    public float MetersZ(int localZ) => (z0 + localZ) / (float)(res - 1) * sizeZ;
}

public static class HeightBrush
{
    public const int   RESOLUTION      = 257;                   // heightmapResolution the renderer enforces
    public const float RANGE_METERS    = 30f;                   // TerrainData.size.y: 15 m up, 15 m down
    public const float BASE_NORMALIZED = 0.5f;                  // flat ground
    public const float BASE_WORLD_Y    = -RANGE_METERS * BASE_NORMALIZED;   // -15: Terrain transform y
    public const float MIN_RADIUS      = 0.5f;
    public const float MAX_RADIUS      = 30f;
    // Samples laid closer than radius * this fold into the previous stored sample (MergeAmount), so
    // holding still stores one sample and a drag stores about four per radius of travel.
    public const float MERGE_FRACTION  = 0.25f;

    public static float NormalizedFromMeters(float metersAboveBase) =>
        Mathf.Clamp01(BASE_NORMALIZED + metersAboveBase / RANGE_METERS);

    public static float MetersFromNormalized(float normalized) =>
        (normalized - BASE_NORMALIZED) * RANGE_METERS;

    public static string KindKey(HeightBrushKind kind) => kind switch
    {
        HeightBrushKind.Smooth  => "smooth",
        HeightBrushKind.Flatten => "flatten",
        _                       => "raise",
    };

    // Unknown or missing keys fall back to Raise, the default the data type declares.
    public static HeightBrushKind ParseKind(string key)
    {
        if (string.Equals(key, "smooth",  System.StringComparison.OrdinalIgnoreCase)) return HeightBrushKind.Smooth;
        if (string.Equals(key, "flatten", System.StringComparison.OrdinalIgnoreCase)) return HeightBrushKind.Flatten;
        return HeightBrushKind.Raise;
    }

    // Smoothstep weight: 1 at the center (t = 0) easing to 0 at the rim (t = 1), t = dist / radius.
    public static float Falloff(float t)
    {
        if (t <= 0f) return 1f;
        if (t >= 1f) return 0f;
        float u = 1f - t;
        return u * u * (3f - 2f * u);
    }

    // Combines two samples laid at (nearly) the same spot. Raise is additive, so amounts sum. Smooth
    // and Flatten lerp toward a target, so two weights compose as 1 - (1 - a)(1 - b), clamped.
    public static float MergeAmount(HeightBrushKind kind, float a, float b) =>
        kind == HeightBrushKind.Raise ? a + b : Mathf.Clamp01(1f - (1f - a) * (1f - b));

    // Sample box a disc at terrain-local (cx, cz) touches, plus one sample of padding so Smooth's
    // 3x3 reads stay inside, clamped to [0, res). False when the disc misses the terrain entirely.
    public static bool SampleWindow(float cx, float cz, float radius, float sizeX, float sizeZ, int res,
                                    out int x0, out int z0, out int w, out int h)
    {
        x0 = z0 = w = h = 0;
        if (res < 2 || sizeX <= 0f || sizeZ <= 0f) return false;
        float sx = (res - 1) / sizeX, sz = (res - 1) / sizeZ;     // samples per meter
        int xMin = Mathf.FloorToInt((cx - radius) * sx) - 1;
        int xMax = Mathf.CeilToInt ((cx + radius) * sx) + 1;
        int zMin = Mathf.FloorToInt((cz - radius) * sz) - 1;
        int zMax = Mathf.CeilToInt ((cz + radius) * sz) + 1;
        if (xMax < 0 || zMax < 0 || xMin > res - 1 || zMin > res - 1) return false;
        x0 = Mathf.Max(0, xMin);
        z0 = Mathf.Max(0, zMin);
        w  = Mathf.Min(res - 1, xMax) - x0 + 1;
        h  = Mathf.Min(res - 1, zMax) - z0 + 1;
        return w > 0 && h > 0;
    }

    // One brush sample. cx/cz are terrain-local meters. `amount` is meters for Raise and a 0..1
    // weight for Smooth/Flatten. `clip` is a WORLD-meter polygon (site.lotBoundary) and clipOffX/Z
    // the terrain's world position, so the test can convert; null clip = no clipping.
    public static void Stamp(ref HeightWindow win, HeightBrushKind kind, float cx, float cz, float radius,
                             float amount, float targetNormalized, float[][] clip, float clipOffX, float clipOffZ)
    {
        switch (kind)
        {
            case HeightBrushKind.Smooth:  Smooth (ref win, cx, cz, radius, amount, clip, clipOffX, clipOffZ); break;
            case HeightBrushKind.Flatten: Flatten(ref win, cx, cz, radius, targetNormalized, amount, clip, clipOffX, clipOffZ); break;
            default:                      Raise  (ref win, cx, cz, radius, amount, RANGE_METERS, clip, clipOffX, clipOffZ); break;
        }
    }

    // h += amountMeters / rangeMeters * falloff, clamped to [0, 1]. Negative amounts dig.
    public static void Raise(ref HeightWindow win, float cx, float cz, float radius, float amountMeters,
                             float rangeMeters, float[][] clip, float ox, float oz)
    {
        if (win.heights == null || radius <= 0f || rangeMeters <= 0f) return;
        float delta = amountMeters / rangeMeters;
        if (!DiscBox(ref win, cx, cz, radius, out int xa, out int xb, out int za, out int zb)) return;

        for (int z = za; z <= zb; z++)
        {
            float mz = win.MetersZ(z);
            for (int x = xa; x <= xb; x++)
            {
                float mx = win.MetersX(x);
                float f  = Weight(mx, mz, cx, cz, radius, clip, ox, oz);
                if (f <= 0f) continue;
                win.heights[z, x] = Mathf.Clamp01(win.heights[z, x] + delta * f);
            }
        }
    }

    // h = lerp(h, target, weight * falloff). Repeated stamps converge on the target.
    public static void Flatten(ref HeightWindow win, float cx, float cz, float radius, float targetNormalized,
                               float weight, float[][] clip, float ox, float oz)
    {
        if (win.heights == null || radius <= 0f) return;
        targetNormalized = Mathf.Clamp01(targetNormalized);
        if (!DiscBox(ref win, cx, cz, radius, out int xa, out int xb, out int za, out int zb)) return;

        for (int z = za; z <= zb; z++)
        {
            float mz = win.MetersZ(z);
            for (int x = xa; x <= xb; x++)
            {
                float mx = win.MetersX(x);
                float f  = Weight(mx, mz, cx, cz, radius, clip, ox, oz);
                if (f <= 0f) continue;
                win.heights[z, x] = Mathf.Lerp(win.heights[z, x], targetNormalized, Mathf.Clamp01(weight * f));
            }
        }
    }

    // h = lerp(h, avg3x3(h), weight * falloff). The average is read from a copy taken before any
    // write, so the result does not depend on scan order. Neighbor indices clamp to the window
    // edge, so the terrain border never pulls toward zero and a flat field stays flat.
    public static void Smooth(ref HeightWindow win, float cx, float cz, float radius, float weight,
                              float[][] clip, float ox, float oz)
    {
        if (win.heights == null || radius <= 0f) return;
        if (!DiscBox(ref win, cx, cz, radius, out int xa, out int xb, out int za, out int zb)) return;

        int w = win.Width, h = win.Height;
        var src = (float[,])win.heights.Clone();

        for (int z = za; z <= zb; z++)
        {
            float mz = win.MetersZ(z);
            for (int x = xa; x <= xb; x++)
            {
                float mx = win.MetersX(x);
                float f  = Weight(mx, mz, cx, cz, radius, clip, ox, oz);
                if (f <= 0f) continue;

                float sum = 0f;
                for (int dz = -1; dz <= 1; dz++)
                {
                    int zz = Mathf.Clamp(z + dz, 0, h - 1);
                    for (int dx = -1; dx <= 1; dx++)
                        sum += src[zz, Mathf.Clamp(x + dx, 0, w - 1)];
                }
                win.heights[z, x] = Mathf.Lerp(src[z, x], sum / 9f, Mathf.Clamp01(weight * f));
            }
        }
    }

    // Falloff at a sample, or 0 when it is outside the disc or outside the clip polygon.
    private static float Weight(float mx, float mz, float cx, float cz, float radius,
                                float[][] clip, float ox, float oz)
    {
        float dx = mx - cx, dz = mz - cz;
        float d  = Mathf.Sqrt(dx * dx + dz * dz);
        if (d > radius) return 0f;
        if (clip != null && !EnvironmentScale.PointInPolygon(mx + ox, mz + oz, clip)) return 0f;
        return Falloff(d / radius);
    }

    // Window-local index box the disc can touch, intersected with the window. False when empty.
    private static bool DiscBox(ref HeightWindow win, float cx, float cz, float radius,
                                out int xa, out int xb, out int za, out int zb)
    {
        xa = xb = za = zb = 0;
        if (win.res < 2 || win.sizeX <= 0f || win.sizeZ <= 0f) return false;
        float sx = (win.res - 1) / win.sizeX, sz = (win.res - 1) / win.sizeZ;
        xa = Mathf.Max(0,             Mathf.FloorToInt((cx - radius) * sx) - win.x0);
        xb = Mathf.Min(win.Width - 1, Mathf.CeilToInt ((cx + radius) * sx) - win.x0);
        za = Mathf.Max(0,             Mathf.FloorToInt((cz - radius) * sz) - win.z0);
        zb = Mathf.Min(win.Height - 1, Mathf.CeilToInt((cz + radius) * sz) - win.z0);
        return xa <= xb && za <= zb;
    }
}
