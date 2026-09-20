using System;
using System.Collections.Generic;
using UnityEngine;

// Several environments can be loaded at once and each owns its ground (WorldRenderer keeps one
// Terrain per env). This is the pure side of that: the ground rectangle a site asks for, which
// loaded ground answers a "what is the surface here" query, and how far a backdrop's ground sits
// below the active one so two coincident flat grounds never z-fight.
public static class TerrainStack
{
    public const float MIN_SIZE_M = 1f;
    public const float MAX_SIZE_M = 4000f;

    // Each backdrop sits this much lower than the one above it, down to MAX_BIAS_M in total.
    public const float BIAS_STEP_M = 0.02f;
    public const float MAX_BIAS_M  = 0.10f;

    // A ground rectangle in world meters: min corner plus extent.
    public struct GroundRect
    {
        public float x, z, w, l;

        // Edges count as inside, so a point on the seam between two side-by-side grounds is on both.
        public bool Contains(float px, float pz) =>
            px >= x && px <= x + w && pz >= z && pz <= z + l;
    }

    // Width/length the terrain is given for a requested size: clamped to a sane band so a
    // malformed or zero terrainSize can't collapse or blow up the ground. False for a non-finite
    // or non-positive request (the caller leaves the terrain as it is).
    public static bool ClampSize(float width, float length, out float w, out float l)
    {
        w = l = 0f;
        if (float.IsNaN(width) || float.IsNaN(length) || float.IsInfinity(width) || float.IsInfinity(length)) return false;
        if (width <= 0f || length <= 0f) return false;
        w = Mathf.Clamp(width,  MIN_SIZE_M, MAX_SIZE_M);
        l = Mathf.Clamp(length, MIN_SIZE_M, MAX_SIZE_M);
        return true;
    }

    // The ground rectangle of a site: EnvironmentScale.TerrainCorner plus the clamped terrainSize,
    // exactly what the renderer gives the env's Terrain. False when the site has no usable size.
    public static bool TryRectOf(SiteDef site, out GroundRect rect)
    {
        rect = default;
        var ts = site?.terrainSize;
        if (ts == null || ts.Length < 2) return false;
        if (!ClampSize(ts[0], ts[1], out float w, out float l)) return false;
        EnvironmentScale.TerrainCorner(site, out float ox, out float oz);
        rect = new GroundRect { x = ox, z = oz, w = w, l = l };
        return true;
    }

    // World Y offset of a loaded env's ground. The active env is at 0. Backdrops step down by load
    // order (rank 0 = first backdrop), so no two grounds are coplanar; the whole env moves with
    // its ground, so nothing floats.
    public static float BiasY(bool active, int backdropRank)
    {
        if (active) return 0f;
        return -Mathf.Min(BIAS_STEP_M * (Mathf.Max(0, backdropRank) + 1), MAX_BIAS_M);
    }

    // Which ground answers a surface query at (x, z). `rects[i]` is ground i's rectangle (null
    // entries allowed: a ground with no usable rect), `sampleY(i)` its surface Y at the point.
    //   1. the active ground, when the point is inside its rectangle;
    //   2. else the highest surface among grounds whose rectangle holds the point;
    //   3. else the active ground anyway (its edge-clamped sample, what a single env always gave);
    //   4. else nothing: false, y = 0.
    public static bool TryPickGround(IReadOnlyList<GroundRect?> rects, int activeIndex, float x, float z,
                                     Func<int, float> sampleY, out int index, out float y)
    {
        index = -1; y = 0f;
        if (rects == null || sampleY == null) return false;
        bool hasActive = activeIndex >= 0 && activeIndex < rects.Count;

        if (hasActive && rects[activeIndex].HasValue && rects[activeIndex].Value.Contains(x, z))
        {
            index = activeIndex; y = sampleY(activeIndex);
            return true;
        }

        for (int i = 0; i < rects.Count; i++)
        {
            if (i == activeIndex || !rects[i].HasValue || !rects[i].Value.Contains(x, z)) continue;
            float s = sampleY(i);
            if (index < 0 || s > y) { index = i; y = s; }
        }
        if (index >= 0) return true;

        if (hasActive)
        {
            index = activeIndex; y = sampleY(activeIndex);
            return true;
        }
        return false;
    }
}
