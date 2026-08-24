using UnityEngine;

// Pure math for fitting a generated child environment into a host site's polygon (SitePlotDef).
// Two jobs, both keyed off the boundary's axis-aligned bounding box in host world meters:
//   1. Request side: BoundaryToCanvas / SiteDimsFeet turn the drawn polygon into the
//      lot_boundary + site_width_ft/site_height_ft fields of POST /api/layout/generate, so the
//      LLM lays out for the real parcel shape and proportions.
//   2. Placement side: ProjectIntoSite scales + translates a DEEP COPY of the child EnvironmentDef
//      from its own origin-anchored frame into the site bbox (the stored record stays pristine).
//
// Coordinate convention (matches LayoutConverter, pinned by SiteFitTests round-trip): the server
// canvas is [0,1000]² with index [0] → Unity X and index [1] → Unity Z. Because the site dims sent
// with the request equal the bbox extents, LayoutConverter maps the returned canvas back to bbox-
// local meters and ProjectIntoSite's scale factors come out ≈ 1 (fit is then mostly translation).
public static class SiteFit
{
    public const float CANVAS = 1000f;   // server normalized canvas extent (both axes)

    // Placement transform for child-local → host-world: world = offset + local · scale (per axis).
    public struct FitResult
    {
        public float offsetX, offsetZ;
        public float scaleX, scaleZ;
    }

    // Axis-aligned bounds of a boundary polygon ([x, z] host meters). False for null, fewer than 3
    // usable vertices, or a zero-area bbox (degenerate line/point parcels are unusable).
    public static bool BoundaryBounds(float[][] poly, out float minX, out float minZ, out float maxX, out float maxZ)
    {
        minX = minZ = float.MaxValue;
        maxX = maxZ = float.MinValue;
        if (poly == null || poly.Length < 3) return false;
        int n = 0;
        foreach (var p in poly)
        {
            if (p == null || p.Length < 2) continue;
            if (float.IsNaN(p[0]) || float.IsInfinity(p[0]) || float.IsNaN(p[1]) || float.IsInfinity(p[1])) continue;
            if (p[0] < minX) minX = p[0];
            if (p[0] > maxX) maxX = p[0];
            if (p[1] < minZ) minZ = p[1];
            if (p[1] > maxZ) maxZ = p[1];
            n++;
        }
        return n >= 3 && maxX - minX > 1e-3f && maxZ - minZ > 1e-3f;
    }

    // Transform that maps a child environment of size childWidthM × childLengthM (origin-anchored,
    // as every generated env is) onto the site boundary's bbox. False when either side is degenerate
    // or the required scale falls outside EnvironmentScale's sanity clamp.
    public static bool TryComputeFit(float[][] siteBoundary, float childWidthM, float childLengthM, out FitResult fit)
    {
        fit = default;
        if (!BoundaryBounds(siteBoundary, out float minX, out float minZ, out float maxX, out float maxZ)) return false;
        if (childWidthM <= 0f || childLengthM <= 0f) return false;
        if (float.IsNaN(childWidthM) || float.IsInfinity(childWidthM)) return false;
        if (float.IsNaN(childLengthM) || float.IsInfinity(childLengthM)) return false;

        float sx = (maxX - minX) / childWidthM;
        float sz = (maxZ - minZ) / childLengthM;
        if (sx < EnvironmentScale.MIN_FACTOR || sx > EnvironmentScale.MAX_FACTOR) return false;
        if (sz < EnvironmentScale.MIN_FACTOR || sz > EnvironmentScale.MAX_FACTOR) return false;

        fit = new FitResult { offsetX = minX, offsetZ = minZ, scaleX = sx, scaleZ = sz };
        return true;
    }

    // The drawn boundary normalized into its own bbox as server canvas points: [x-axis 0-1000,
    // z-axis 0-1000] per the index[0]→X / index[1]→Z convention. Values are rounded to whole
    // numbers and clamped (the server's normalize_lot_boundary rejects out-of-range). Null for a
    // degenerate boundary.
    public static float[][] BoundaryToCanvas(float[][] boundary)
    {
        if (!BoundaryBounds(boundary, out float minX, out float minZ, out float maxX, out float maxZ)) return null;
        float w = maxX - minX, l = maxZ - minZ;
        var pts = new System.Collections.Generic.List<float[]>(boundary.Length);
        foreach (var p in boundary)
        {
            if (p == null || p.Length < 2) continue;
            float cx = Mathf.Clamp(Mathf.Round((p[0] - minX) / w * CANVAS), 0f, CANVAS);
            float cz = Mathf.Clamp(Mathf.Round((p[1] - minZ) / l * CANVAS), 0f, CANVAS);
            pts.Add(new[] { cx, cz });
        }
        return pts.Count >= 3 ? pts.ToArray() : null;
    }

    // Real-world dimensions of the boundary's bbox in feet, for site_width_ft / site_height_ft.
    // width = X extent, height = Z extent (matches LayoutConverter's terrainWidthM/terrainHeightM).
    public static bool SiteDimsFeet(float[][] boundary, out float widthFt, out float heightFt)
    {
        widthFt = heightFt = 0f;
        if (!BoundaryBounds(boundary, out float minX, out float minZ, out float maxX, out float maxZ)) return false;
        widthFt  = (maxX - minX) / AuthoringConventions.FT_TO_M;
        heightFt = (maxZ - minZ) / AuthoringConventions.FT_TO_M;
        return true;
    }

    // Projects a child environment into the site boundary's bbox IN PLACE: scale about the origin
    // (generated envs are origin-anchored) then translate to the bbox corner. Callers must pass a
    // deep copy — the stored child record must stay in its own frame. False (env untouched apart
    // from a possibly-applied no-op) when the fit is degenerate.
    public static bool ProjectIntoSite(EnvironmentDef childCopy, float[][] siteBoundary)
    {
        if (childCopy?.site?.terrainSize == null || childCopy.site.terrainSize.Length < 2) return false;
        float w = childCopy.site.terrainSize[0], l = childCopy.site.terrainSize[1];
        if (!TryComputeFit(siteBoundary, w, l, out var fit)) return false;

        // Warn when the request/response aspect drifted: the fit then stretches per-axis and
        // iso-scaled footprints (buildings, path widths) will slightly under/overfill.
        float ratio = fit.scaleZ > 1e-6f ? fit.scaleX / fit.scaleZ : float.PositiveInfinity;
        if (ratio < 0.9f || ratio > 1.1f)
            Debug.LogWarning($"SiteFit: non-uniform fit (scaleX {fit.scaleX:0.###} vs scaleZ {fit.scaleZ:0.###}); building footprints may not fill the site exactly.");

        if (!EnvironmentScale.ScaleEnvironmentXZ(childCopy, fit.scaleX, fit.scaleZ, Vector2.zero)) return false;
        return EnvironmentScale.TranslateEnvironmentXZ(childCopy, fit.offsetX, fit.offsetZ);
    }
}
