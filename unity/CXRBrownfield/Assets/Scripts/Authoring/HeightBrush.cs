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
//
// Stroke samples. A stroke is a list of samples; frames laid close together fold into one sample
// (MergeSample), and the replay must reproduce every frame that folded in:
//   Raise    [x, z, meters]           frames sum; one stamp of the sum is the same as the frames.
//   Flatten  [x, z, weight]           weight in [0, 1) composes as 1 - (1-a)(1-b). Per cell the
//                                     stamp lerps by 1 - (1-weight)^falloff, which composes the same
//                                     way, so one merged stamp equals the frames it came from.
//   Smooth   [x, z, weightSum, passes] a 3x3 blur has no closed form, so the sample keeps the
//                                     number of frames and the replay runs that many passes of
//                                     weightSum / passes. Legacy 3-element samples replay one pass.

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
    // Samples laid closer than radius * this fold into the previous stored sample (MergeSample), so
    // holding still stores one sample and a drag stores about four per radius of travel.
    public const float MERGE_FRACTION  = 0.25f;
    // A flatten sample's composed weight never reaches 1: samples are saved at 3 decimals, and a
    // weight that rounded up to exactly 1 would snap the whole disc to the target with a hard edge.
    // At this cap the remaining gap at the center is 0.1% (3 cm over the full range), which is flat.
    public const float FLATTEN_MAX_WEIGHT = 0.999f;
    // The most one frame of Smooth or Flatten may blend (a weight above 1 has no meaning).
    public const float MAX_FRAME_WEIGHT = 1f;

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

    // ---- Stroke samples ----

    // A fresh sample for one frame laid at (x, z). Smooth carries its pass count.
    public static float[] NewSample(HeightBrushKind kind, float x, float z, float amount)
    {
        amount = ClampFrameAmount(kind, amount);
        return kind == HeightBrushKind.Smooth
            ? new[] { x, z, amount, 1f }
            : new[] { x, z, kind == HeightBrushKind.Flatten ? Mathf.Min(amount, FLATTEN_MAX_WEIGHT) : amount };
    }

    // Folds one more frame into `sample` and returns the amount the live stamp must apply THIS frame
    // so the terrain lands exactly where the replay of the merged sample will. Raise and Smooth sum,
    // so that is the frame's own amount; Flatten composes and caps at FLATTEN_MAX_WEIGHT, so it is
    // the increment that takes the composed weight from its old value to the new one (0 at the cap).
    public static float MergeSample(HeightBrushKind kind, float[] sample, float amount)
    {
        if (sample == null || sample.Length < 3) return 0f;
        amount = ClampFrameAmount(kind, amount);
        switch (kind)
        {
            case HeightBrushKind.Smooth:
                sample[2] += amount;
                if (sample.Length >= 4) sample[3] += 1f;
                return amount;
            case HeightBrushKind.Flatten:
            {
                float before = Mathf.Clamp(sample[2], 0f, FLATTEN_MAX_WEIGHT);
                float after  = Mathf.Min(FLATTEN_MAX_WEIGHT, 1f - (1f - before) * (1f - amount));
                sample[2] = after;
                // (1 - before) * (1 - inc) == (1 - after)  =>  inc = 1 - (1 - after) / (1 - before)
                return Mathf.Clamp01(1f - (1f - after) / (1f - before));
            }
            default:
                sample[2] += amount;
                return amount;
        }
    }

    // Combines two amounts laid at (nearly) the same spot: Raise sums; Flatten composes two weights
    // as 1 - (1 - a)(1 - b), capped. Smooth also keeps a pass count, so it merges via MergeSample.
    public static float MergeAmount(HeightBrushKind kind, float a, float b) => kind switch
    {
        HeightBrushKind.Flatten => Mathf.Min(FLATTEN_MAX_WEIGHT, Mathf.Clamp01(1f - (1f - a) * (1f - b))),
        _                       => a + b,
    };

    // Number of blur passes a stored sample stands for (Smooth only; anything else is one stamp).
    public static int SamplePasses(HeightBrushKind kind, float[] sample)
    {
        if (kind != HeightBrushKind.Smooth || sample == null || sample.Length < 4) return 1;
        return Mathf.Max(1, Mathf.RoundToInt(sample[3]));
    }

    // Replays one stored sample at terrain-local (cx, cz): the stamp(s) that reproduce every frame
    // folded into it. Callers convert the sample's world x/z to terrain-local before calling.
    public static void ReplaySample(ref HeightWindow win, HeightBrushKind kind, float[] sample, float cx, float cz,
                                    float radius, float targetNormalized, float[][] clip, float clipOffX, float clipOffZ)
    {
        if (sample == null || sample.Length < 3) return;
        int passes = SamplePasses(kind, sample);
        float amount = passes > 1 ? sample[2] / passes : sample[2];
        for (int i = 0; i < passes; i++)
            Stamp(ref win, kind, cx, cz, radius, amount, targetNormalized, clip, clipOffX, clipOffZ);
    }

    // Raise amounts are signed meters; Smooth and Flatten weights are one frame's blend in [0, 1].
    private static float ClampFrameAmount(HeightBrushKind kind, float amount) =>
        kind == HeightBrushKind.Raise ? amount : Mathf.Clamp(amount, 0f, MAX_FRAME_WEIGHT);

    // ---- Stamps ----

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

    // h = lerp(h, target, 1 - (1 - weight)^falloff). At the center that is the weight itself; toward
    // the rim it eases to 0. Written this way so stamps compose exactly: two stamps a then b equal
    // one stamp of 1 - (1-a)(1-b), which is how MergeSample folds frames. Repeated stamps converge
    // on the target.
    public static void Flatten(ref HeightWindow win, float cx, float cz, float radius, float targetNormalized,
                               float weight, float[][] clip, float ox, float oz)
    {
        if (win.heights == null || radius <= 0f) return;
        targetNormalized = Mathf.Clamp01(targetNormalized);
        // Fraction of the gap a center cell keeps; the cap means a stamp never snaps a disc hard.
        float keep = 1f - Mathf.Clamp(weight, 0f, FLATTEN_MAX_WEIGHT);
        if (!DiscBox(ref win, cx, cz, radius, out int xa, out int xb, out int za, out int zb)) return;

        for (int z = za; z <= zb; z++)
        {
            float mz = win.MetersZ(z);
            for (int x = xa; x <= xb; x++)
            {
                float mx = win.MetersX(x);
                float f  = Weight(mx, mz, cx, cz, radius, clip, ox, oz);
                if (f <= 0f) continue;
                win.heights[z, x] = Mathf.Lerp(win.heights[z, x], targetNormalized, 1f - Mathf.Pow(keep, f));
            }
        }
    }

    // h = lerp(h, avg3x3(h), weight * falloff). The average is read from a copy of the disc box
    // (plus one sample of margin) taken before any write, so the result does not depend on scan
    // order. Neighbor indices clamp to the window edge, so the terrain border never pulls toward
    // zero and a flat field stays flat.
    public static void Smooth(ref HeightWindow win, float cx, float cz, float radius, float weight,
                              float[][] clip, float ox, float oz)
    {
        if (win.heights == null || radius <= 0f) return;
        if (!DiscBox(ref win, cx, cz, radius, out int xa, out int xb, out int za, out int zb)) return;

        int w = win.Width, h = win.Height;
        int sx0 = Mathf.Max(0, xa - 1), sx1 = Mathf.Min(w - 1, xb + 1);
        int sz0 = Mathf.Max(0, za - 1), sz1 = Mathf.Min(h - 1, zb + 1);
        var src = new float[sz1 - sz0 + 1, sx1 - sx0 + 1];
        for (int z = sz0; z <= sz1; z++)
            for (int x = sx0; x <= sx1; x++)
                src[z - sz0, x - sx0] = win.heights[z, x];

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
                    int zz = Mathf.Clamp(z + dz, 0, h - 1) - sz0;
                    for (int dx = -1; dx <= 1; dx++)
                        sum += src[zz, Mathf.Clamp(x + dx, 0, w - 1) - sx0];
                }
                win.heights[z, x] = Mathf.Lerp(src[z - sz0, x - sx0], sum / 9f, Mathf.Clamp01(weight * f));
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
