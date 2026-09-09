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
// canvas is [0,1000]² with index [0] → Unity X and index [1] → Unity Z, turned half a turn: canvas
// 0 is the world MAX on each axis and canvas 1000 the world min, so the top of the sketch (north)
// lands at +X and its left edge (west) at +Z. Index [0] is the sketch's vertical axis (down the
// page) and carries site_width_ft; index [1] is the sketch's horizontal axis and carries
// site_height_ft. Because the site dims sent with the request equal the bbox extents,
// LayoutConverter maps the returned canvas back to bbox-local meters. ProjectIntoSite fits the
// child's OWN lot boundary bbox (not its terrain, which carries a margin) onto the site bbox, so the
// scale factors come out exactly 1 and the fit is a pure translation: buildings, whose size cannot
// follow a per-axis scale, keep the proportions the model drew.
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
        => TryComputeFit(siteBoundary, 0f, 0f, childWidthM, childLengthM, out fit);

    // General form: maps the child rectangle [childMinX, childMaxX] × [childMinZ, childMaxZ] onto the
    // site bbox, so a child whose content frame does not start at the origin still lands corner on
    // corner. False for a degenerate child rect or a scale outside the sanity clamp.
    public static bool TryComputeFit(float[][] siteBoundary,
                                     float childMinX, float childMinZ, float childMaxX, float childMaxZ,
                                     out FitResult fit)
    {
        fit = default;
        if (!BoundaryBounds(siteBoundary, out float minX, out float minZ, out float maxX, out float maxZ)) return false;
        float childWidthM = childMaxX - childMinX, childLengthM = childMaxZ - childMinZ;
        if (childWidthM <= 0f || childLengthM <= 0f) return false;
        if (float.IsNaN(childWidthM) || float.IsInfinity(childWidthM)) return false;
        if (float.IsNaN(childLengthM) || float.IsInfinity(childLengthM)) return false;

        float sx = (maxX - minX) / childWidthM;
        float sz = (maxZ - minZ) / childLengthM;
        if (sx < EnvironmentScale.MIN_FACTOR || sx > EnvironmentScale.MAX_FACTOR) return false;
        if (sz < EnvironmentScale.MIN_FACTOR || sz > EnvironmentScale.MAX_FACTOR) return false;

        // world = offset + local · scale, so the child's min corner must map to the site's min corner.
        fit = new FitResult
        {
            offsetX = minX - childMinX * sx,
            offsetZ = minZ - childMinZ * sz,
            scaleX  = sx,
            scaleZ  = sz,
        };
        return true;
    }

    // The rectangle a child environment's content lives in: its lot boundary bbox when it has one
    // (generated envs always do; their terrain carries a 2 m margin past the parcel that must NOT be
    // squeezed into the site), else the terrain rectangle from the origin.
    public static bool ChildFrame(EnvironmentDef child, out float minX, out float minZ, out float maxX, out float maxZ)
    {
        minX = minZ = maxX = maxZ = 0f;
        if (child?.site == null) return false;
        if (BoundaryBounds(child.site.lotBoundary, out minX, out minZ, out maxX, out maxZ)) return true;
        var ts = child.site.terrainSize;
        if (ts == null || ts.Length < 2) return false;
        minX = minZ = 0f; maxX = ts[0]; maxZ = ts[1];
        return maxX > 0f && maxZ > 0f;
    }

    // The drawn boundary normalized into its own bbox as server canvas points: [x-axis 0-1000,
    // z-axis 0-1000] per the index[0]→X / index[1]→Z convention, turned half a turn to match
    // LayoutConverter (the bbox MAX corner is canvas 0 on each axis, the min corner is 1000).
    // Values are rounded to whole numbers and clamped (the server's normalize_lot_boundary rejects
    // out-of-range). Null for a degenerate boundary.
    public static float[][] BoundaryToCanvas(float[][] boundary)
    {
        if (!BoundaryBounds(boundary, out float minX, out float minZ, out float maxX, out float maxZ)) return null;
        float w = maxX - minX, l = maxZ - minZ;
        var pts = new System.Collections.Generic.List<float[]>(boundary.Length);
        foreach (var p in boundary)
        {
            if (p == null || p.Length < 2) continue;
            float cx = Mathf.Clamp(Mathf.Round((maxX - p[0]) / w * CANVAS), 0f, CANVAS);
            float cz = Mathf.Clamp(Mathf.Round((maxZ - p[1]) / l * CANVAS), 0f, CANVAS);
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
    // (generated envs are origin-anchored) then translate so the child's content frame (ChildFrame)
    // lands corner on corner with the site bbox. Callers must pass a deep copy — the stored child
    // record must stay in its own frame. False (env untouched apart from a possibly-applied no-op)
    // when the fit is degenerate.
    public static bool ProjectIntoSite(EnvironmentDef childCopy, float[][] siteBoundary)
    {
        if (childCopy?.site?.terrainSize == null || childCopy.site.terrainSize.Length < 2) return false;
        if (!ChildFrame(childCopy, out float cMinX, out float cMinZ, out float cMaxX, out float cMaxZ)) return false;
        if (!TryComputeFit(siteBoundary, cMinX, cMinZ, cMaxX, cMaxZ, out var fit)) return false;

        // Generated environments are origin-anchored and usually leave terrainOrigin null. Seed it
        // so the scale + translate below carry it along with everything else and it ends up on the
        // site's corner: an env rendered here has its ground under it, not back at the origin.
        if (childCopy.site.terrainOrigin == null || childCopy.site.terrainOrigin.Length < 2)
            childCopy.site.terrainOrigin = new[] { 0f, 0f };

        // Warn when the request/response aspect drifted: the fit then stretches per-axis and
        // iso-scaled footprints (buildings, path widths) will slightly under/overfill.
        float ratio = fit.scaleZ > 1e-6f ? fit.scaleX / fit.scaleZ : float.PositiveInfinity;
        if (ratio < 0.9f || ratio > 1.1f)
            Debug.LogWarning($"SiteFit: non-uniform fit (scaleX {fit.scaleX:0.###} vs scaleZ {fit.scaleZ:0.###}); building footprints may not fill the site exactly.");

        if (!EnvironmentScale.ScaleEnvironmentXZ(childCopy, fit.scaleX, fit.scaleZ, Vector2.zero)) return false;
        return EnvironmentScale.TranslateEnvironmentXZ(childCopy, fit.offsetX, fit.offsetZ);
    }
}
