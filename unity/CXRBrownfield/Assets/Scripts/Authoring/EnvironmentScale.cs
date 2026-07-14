using System.Collections.Generic;
using UnityEngine;

// Pure-data scale & measurement helpers for the real-world-scale tools (Measure tool, Scale
// Calibration, dimension readouts). Lives in the Authoring assembly because it operates only on the
// authoring data types (EnvironmentDef / SiteDef / *Instance / *Def) plus Unity math structs — no
// Terrain / GameObject / MonoBehaviour access — so EditController (Assembly-CSharp) and the
// generation pipeline (also Authoring) can both reach it. All coordinates are world METERS and the
// convention is 1 Unity unit = 1 meter (see AuthoringConventions).
public static class EnvironmentScale
{
    // Smallest measured distance we'll calibrate against; below this the factor is meaningless.
    public const float MIN_MEASURED = 0.01f;
    // Sanity clamp on a single calibration so a fat-fingered real-distance can't blow the scene up.
    public const float MIN_FACTOR = 0.01f;
    public const float MAX_FACTOR = 100f;

    // -------------------------------------------------------------------------
    // Measurement (read-only)
    // -------------------------------------------------------------------------

    // 2D distance in meters between two [x, z] points. Returns 0 for malformed input.
    public static float Distance(float[] a2, float[] b2)
    {
        if (a2 == null || b2 == null || a2.Length < 2 || b2.Length < 2) return 0f;
        float dx = b2[0] - a2[0];
        float dz = b2[1] - a2[1];
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Total length of an [x, z] polyline in meters.
    public static float PolylineLength(IList<float[]> pts)
    {
        if (pts == null || pts.Count < 2) return 0f;
        float total = 0f;
        for (int i = 1; i < pts.Count; i++) total += Distance(pts[i - 1], pts[i]);
        return total;
    }

    // Absolute polygon area (m²) of an [x, z] ring via the shoelace formula. <3 points ⇒ 0.
    public static float PolygonArea(IList<float[]> pts)
    {
        if (pts == null || pts.Count < 3) return 0f;
        float sum = 0f;
        for (int i = 0; i < pts.Count; i++)
        {
            float[] a = pts[i];
            float[] b = pts[(i + 1) % pts.Count];
            if (a == null || b == null || a.Length < 2 || b.Length < 2) continue;
            sum += a[0] * b[1] - b[0] * a[1];
        }
        return Mathf.Abs(sum) * 0.5f;
    }

    // -------------------------------------------------------------------------
    // Footprint readouts (W = X, D = Z, H = Y) in meters, before instance rotation.
    // -------------------------------------------------------------------------

    // Massing-box objects carry absolute meters in boxSizeMeters; uniform prefabs only have `scale`
    // (their real size needs the live prefab bounds, which the data layer can't see) ⇒ returns zero
    // there so the caller falls back to a live-bounds readout.
    public static Vector3 ObjectFootprint(ObjectInstance o)
    {
        if (o == null) return Vector3.zero;
        if (o.boxSizeMeters != null && o.boxSizeMeters.Length >= 3)
            return new Vector3(o.boxSizeMeters[0], o.boxSizeMeters[1], o.boxSizeMeters[2]) * Mathf.Max(o.scale <= 0f ? 1f : o.scale, 0f);
        return Vector3.zero;
    }

    // Building footprint from its tile grid extents × cell size, height from floors × floorHeight,
    // all multiplied by the instance scale. Returns zero when there are no tiles.
    public static Vector3 BuildingFootprint(BuildingDef def, BuildingInstance inst)
    {
        if (def == null || def.tiles == null || def.tiles.Count == 0) return Vector3.zero;
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue, maxFloor = 0;
        foreach (var t in def.tiles)
        {
            if (t == null) continue;
            if (t.gridX < minX) minX = t.gridX;
            if (t.gridX > maxX) maxX = t.gridX;
            if (t.gridZ < minZ) minZ = t.gridZ;
            if (t.gridZ > maxZ) maxZ = t.gridZ;
            if (t.floor > maxFloor) maxFloor = t.floor;
        }
        if (minX == int.MaxValue) return Vector3.zero;

        float cell = def.gridCellSize > 0f ? def.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        float fh   = def.floorHeight  > 0f ? def.floorHeight  : AuthoringConventions.DEFAULT_FLOOR_HEIGHT;
        int floors = Mathf.Max(def.floors, maxFloor + 1);
        float scale = inst != null && inst.scale > 0f ? inst.scale : 1f;

        float w = (maxX - minX + 1) * cell * scale;
        float d = (maxZ - minZ + 1) * cell * scale;
        float h = floors * fh * scale;
        return new Vector3(w, d, h);
    }

    // -------------------------------------------------------------------------
    // Scale Calibration: rescale the whole environment to true scale about a pivot.
    // -------------------------------------------------------------------------

    // Computes the calibration factor from a measured distance and the real distance it should be.
    // Returns false (factor unset) when the input is degenerate or out of the sanity range.
    public static bool TryComputeFactor(float measuredMeters, float realMeters, out float factor)
    {
        factor = 1f;
        if (measuredMeters < MIN_MEASURED) return false;
        if (realMeters <= 0f || float.IsNaN(realMeters) || float.IsInfinity(realMeters)) return false;
        float f = realMeters / measuredMeters;
        if (float.IsNaN(f) || float.IsInfinity(f) || f < MIN_FACTOR || f > MAX_FACTOR) return false;
        factor = f;
        return true;
    }

    // Multiplies every world-meter quantity in the environment by a uniform `factor`, scaling
    // positions about `pivot` (in [x, z] meters) so the pivot point stays put. Sizes/widths/radii
    // scale directly. Shared BuildingDef geometry (gridCellSize/floorHeight/tiles) is intentionally
    // NOT touched — multiple instances may share one def, and each instance's `scale` already carries
    // the resize. Returns false (no change) for an invalid factor. Safe against null/short arrays.
    public static bool ScaleEnvironment(EnvironmentDef env, float factor, Vector2 pivot)
    {
        if (float.IsNaN(factor) || float.IsInfinity(factor) || factor < MIN_FACTOR || factor > MAX_FACTOR) return false;
        return ScaleEnvironmentCore(env, factor, factor, factor, pivot);
    }

    // Non-uniform footprint resize: scales X by `fx` and Z by `fz` about `pivot` while preserving Y
    // (height). Used when the lot rectangle is resized per-axis with "scale content to fit" so the
    // layout fills the new lot. Isotropic quantities (path width, stroke radius, uniform object /
    // building scale) scale by the horizontal geometric mean √(fx·fz) so area is preserved sensibly.
    // Returns false (no change) for invalid factors.
    public static bool ScaleEnvironmentXZ(EnvironmentDef env, float fx, float fz, Vector2 pivot)
    {
        if (float.IsNaN(fx) || float.IsInfinity(fx) || fx < MIN_FACTOR || fx > MAX_FACTOR) return false;
        if (float.IsNaN(fz) || float.IsInfinity(fz) || fz < MIN_FACTOR || fz > MAX_FACTOR) return false;
        return ScaleEnvironmentCore(env, fx, 1f, fz, pivot);
    }

    // Shared core for the uniform and per-axis resizes. `fx`/`fy`/`fz` are the per-axis factors;
    // horizontal scalars use the geometric mean of fx & fz.
    private static bool ScaleEnvironmentCore(EnvironmentDef env, float fx, float fy, float fz, Vector2 pivot)
    {
        if (env == null) return false;
        if (Mathf.Approximately(fx, 1f) && Mathf.Approximately(fy, 1f) && Mathf.Approximately(fz, 1f)) return true;
        float iso = Mathf.Sqrt(Mathf.Max(fx * fz, 0f));   // isotropic (widths/radii/uniform scale)

        var site = env.site;
        if (site != null)
        {
            if (site.terrainSize != null && site.terrainSize.Length >= 2)
            {
                site.terrainSize[0] *= fx;
                site.terrainSize[1] *= fz;
            }

            if (site.terrainZones != null)
                foreach (var z in site.terrainZones)
                {
                    if (z?.rectMeters == null || z.rectMeters.Length < 4) continue;
                    z.rectMeters[0] = ScalarAbout(z.rectMeters[0], pivot.x, fx);
                    z.rectMeters[1] = ScalarAbout(z.rectMeters[1], pivot.y, fz);
                    z.rectMeters[2] = ScalarAbout(z.rectMeters[2], pivot.x, fx);
                    z.rectMeters[3] = ScalarAbout(z.rectMeters[3], pivot.y, fz);
                }

            if (site.paths != null)
                foreach (var p in site.paths)
                {
                    if (p == null) continue;
                    p.width *= iso;
                    ScalePointsXZ(p.points, pivot, fx, fz);
                }

            if (site.surfaceStrokes != null)
                foreach (var s in site.surfaceStrokes)
                {
                    if (s == null) continue;
                    s.radius *= iso;
                    ScalePointsXZ(s.points, pivot, fx, fz);
                }

            ScalePointsXZ(site.lotBoundary, pivot, fx, fz);
        }

        if (env.objectInstances != null)
            foreach (var o in env.objectInstances)
            {
                if (o == null) continue;
                ScalePosition(o.position, pivot, fx, fy, fz);
                if (o.boxSizeMeters != null)
                {
                    if (o.boxSizeMeters.Length >= 1) o.boxSizeMeters[0] *= fx;
                    if (o.boxSizeMeters.Length >= 2) o.boxSizeMeters[1] *= fy;
                    if (o.boxSizeMeters.Length >= 3) o.boxSizeMeters[2] *= fz;
                }
                else
                    o.scale *= iso;   // uniform prefab: footprint = bounds × scale
            }

        if (env.buildingInstances != null)
            foreach (var b in env.buildingInstances)
            {
                if (b == null) continue;
                ScalePosition(b.position, pivot, fx, fy, fz);
                b.scale *= iso;
            }

        return true;
    }

    // p' = pivot + (p - pivot) * factor, on a single axis.
    private static float ScalarAbout(float v, float pivot, float factor) => pivot + (v - pivot) * factor;

    // Scales a world position [x, y, z]: x/z about the pivot, y (height) by fy.
    private static void ScalePosition(float[] pos, Vector2 pivot, float fx, float fy, float fz)
    {
        if (pos == null) return;
        if (pos.Length >= 1) pos[0] = ScalarAbout(pos[0], pivot.x, fx);
        if (pos.Length >= 2) pos[1] *= fy;
        if (pos.Length >= 3) pos[2] = ScalarAbout(pos[2], pivot.y, fz);
    }

    // Scales a list of [x, z] points about the pivot (x by fx, z by fz).
    private static void ScalePointsXZ(float[][] pts, Vector2 pivot, float fx, float fz)
    {
        if (pts == null) return;
        foreach (var pt in pts)
        {
            if (pt == null || pt.Length < 2) continue;
            pt[0] = ScalarAbout(pt[0], pivot.x, fx);
            pt[1] = ScalarAbout(pt[1], pivot.y, fz);
        }
    }

    // -------------------------------------------------------------------------
    // Lot / parcel geometry — shared by the renderer (mask + frame), the editor
    // (lot tool, fit/clamp), and the generation pipeline so they agree on shape.
    // -------------------------------------------------------------------------

    // The parcel polygon to use for masking/framing/containment: the explicit lotBoundary when it has
    // ≥3 vertices, otherwise the four corners of the terrainSize rectangle [0,0]..[w,l]. Returns null
    // only when there is no usable rectangle either.
    public static float[][] EffectiveLotPolygon(SiteDef site)
    {
        if (site == null) return null;
        if (site.lotBoundary != null && site.lotBoundary.Length >= 3) return site.lotBoundary;
        var ts = site.terrainSize;
        if (ts == null || ts.Length < 2 || ts[0] <= 0f || ts[1] <= 0f) return null;
        float w = ts[0], l = ts[1];
        return new[]
        {
            new[] { 0f, 0f },
            new[] { w,  0f },
            new[] { w,  l  },
            new[] { 0f, l  },
        };
    }

    // Ray-cast (even-odd) point-in-polygon test. Polygon vertices are [x, z] in world meters; point is
    // (px, pz) in world meters. Shared by the lot-boundary mask (renderer) and the out-of-lot
    // containment checks (editor). A null/degenerate polygon (<3 verts) counts everything as inside.
    public static bool PointInPolygon(float px, float pz, float[][] poly)
    {
        if (poly == null || poly.Length < 3) return true;
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float[] a = poly[i], b = poly[j];
            if (a == null || a.Length < 2 || b == null || b.Length < 2) continue;
            float xi = a[0], zi = a[1], xj = b[0], zj = b[1];
            bool crosses = ((zi > pz) != (zj > pz)) &&
                           (px < (xj - xi) * (pz - zi) / (zj - zi) + xi);
            if (crosses) inside = !inside;
        }
        return inside;
    }

    // Axis-aligned XZ bounds (meters) of everything placed in the environment — building & object
    // instances (expanded by their footprint radius), path/stroke control points. Used by
    // "Fit lot to content". `buildingDefs` resolves each BuildingInstance.buildingId to its def for a
    // real footprint; missing defs fall back to the bare position. Returns false when nothing is found.
    public static bool ContentBounds(EnvironmentDef env, IReadOnlyDictionary<string, BuildingDef> buildingDefs,
                                     out float minX, out float minZ, out float maxX, out float maxZ)
    {
        minX = minZ = float.MaxValue;
        maxX = maxZ = float.MinValue;
        if (env == null) return false;

        if (env.buildingInstances != null)
            foreach (var b in env.buildingInstances)
            {
                if (b?.position == null || b.position.Length < 3) continue;
                float r = 0f;
                BuildingDef def = null;
                if (buildingDefs != null && b.buildingId != null) buildingDefs.TryGetValue(b.buildingId, out def);
                if (def != null)
                {
                    Vector3 fp = BuildingFootprint(def, b);   // already × instance scale
                    r = 0.5f * Mathf.Sqrt(fp.x * fp.x + fp.y * fp.y);  // half-diagonal (fp.y = depth)
                }
                Expand(b.position[0], b.position[2], r, ref minX, ref minZ, ref maxX, ref maxZ);
            }

        if (env.objectInstances != null)
            foreach (var o in env.objectInstances)
            {
                if (o?.position == null || o.position.Length < 3) continue;
                Vector3 fp = ObjectFootprint(o);
                float r = 0.5f * Mathf.Sqrt(fp.x * fp.x + fp.z * fp.z);
                Expand(o.position[0], o.position[2], r, ref minX, ref minZ, ref maxX, ref maxZ);
            }

        var site = env.site;
        if (site?.paths != null)
            foreach (var p in site.paths)
                ExpandPoints(p?.points, Mathf.Max(0f, p?.width ?? 0f) * 0.5f, ref minX, ref minZ, ref maxX, ref maxZ);
        if (site?.surfaceStrokes != null)
            foreach (var s in site.surfaceStrokes)
                ExpandPoints(s?.points, Mathf.Max(0f, s?.radius ?? 0f), ref minX, ref minZ, ref maxX, ref maxZ);

        return maxX >= minX && maxZ >= minZ;
    }

    // Average-of-vertices centroid (good enough for nudging a clamped point inward). Zero for empty.
    public static Vector2 PolygonCentroid(float[][] poly)
    {
        if (poly == null || poly.Length == 0) return Vector2.zero;
        Vector2 sum = Vector2.zero; int n = 0;
        foreach (var p in poly)
        {
            if (p == null || p.Length < 2) continue;
            sum += new Vector2(p[0], p[1]); n++;
        }
        return n > 0 ? sum / n : Vector2.zero;
    }

    // Projects a point (x, z meters) to just inside `poly`. Returns it unchanged when already inside;
    // otherwise snaps to the nearest boundary point and nudges slightly toward the centroid so it
    // lands strictly within. A degenerate polygon returns the point unchanged.
    public static Vector2 ClampInsidePolygon(Vector2 p, float[][] poly)
    {
        if (poly == null || poly.Length < 3) return p;
        if (PointInPolygon(p.x, p.y, poly)) return p;

        Vector2 best = p; float bestD = float.MaxValue;
        int n = poly.Length;
        for (int i = 0; i < n; i++)
        {
            float[] a = poly[i], b = poly[(i + 1) % n];
            if (a == null || a.Length < 2 || b == null || b.Length < 2) continue;
            Vector2 va = new(a[0], a[1]), vb = new(b[0], b[1]);
            Vector2 ab = vb - va;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - va, ab) / len2);
            Vector2 proj = va + t * ab;
            float d = (p - proj).sqrMagnitude;
            if (d < bestD) { bestD = d; best = proj; }
        }
        Vector2 dir = PolygonCentroid(poly) - best;
        if (dir.sqrMagnitude > 1e-6f) best += dir.normalized * 0.1f;
        return best;
    }

    private static void Expand(float x, float z, float r, ref float minX, ref float minZ, ref float maxX, ref float maxZ)
    {
        if (x - r < minX) minX = x - r;
        if (x + r > maxX) maxX = x + r;
        if (z - r < minZ) minZ = z - r;
        if (z + r > maxZ) maxZ = z + r;
    }

    private static void ExpandPoints(float[][] pts, float r, ref float minX, ref float minZ, ref float maxX, ref float maxZ)
    {
        if (pts == null) return;
        foreach (var pt in pts)
        {
            if (pt == null || pt.Length < 2) continue;
            Expand(pt[0], pt[1], r, ref minX, ref minZ, ref maxX, ref maxZ);
        }
    }
}
