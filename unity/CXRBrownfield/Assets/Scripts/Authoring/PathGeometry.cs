using System.Collections.Generic;
using UnityEngine;

// Centerline processing for PathDefs: turns the sparse control points the user drew (or the LLM
// emitted) into a dense, smooth, evenly-spaced 2-D centerline before PathMesh builds the ribbon.
// Pure/static so it can be unit tested without a scene. Operates in XZ meters (Vector2 = (x, z)).
//
// Two responsibilities:
//   • Smooth   — centripetal Catmull-Rom spline resampled at a fixed arc-length step. The
//                `smoothing` knob (0..1) blends the spline toward the straight polyline, so 0 keeps
//                crisp street corners and 1 gives flowing trail curves. Both hand-drawn and
//                AI-generated paths run through this at render time.
//   • Simplify — Ramer–Douglas–Peucker decimation, used by the authoring layer to denoise freehand
//                strokes into a compact set of control points.
public static class PathGeometry
{
    public const float DefaultStep = 0.6f;   // meters between resampled centerline points
    private const int   MaxPoints  = 4000;   // hard cap so a huge path can't explode vertex count

    // Resample `points` into a dense centerline. `smoothing` in [0,1]: 0 -> straight (just resampled
    // polyline), 1 -> full centripetal Catmull-Rom curve. Endpoints are always preserved.
    // `roundFit` picks the subdivision count nearest to segLen/step instead of ceiling it, so sample
    // spacing can stretch up to 1.5x `step` as well as shrink — fences use this so panels fit a drawn
    // run at their natural length; path ribbons keep the ceil default (spacing never exceeds `step`).
    public static List<Vector2> Smooth(IReadOnlyList<Vector2> points, float smoothing, float step = DefaultStep, bool roundFit = false)
    {
        if (points == null || points.Count < 2) return CopyOrEmpty(points);
        if (step <= 0f) step = DefaultStep;
        smoothing = Mathf.Clamp01(smoothing);

        // Drop consecutive duplicates — they break the centripetal parameterization (zero-length knot).
        var ctrl = Dedup(points);
        if (ctrl.Count < 2) return ctrl;

        // Pure polyline resample when smoothing is off or there aren't enough points to fit a spline.
        if (smoothing <= 0f || ctrl.Count < 3)
            return ResamplePolyline(ctrl, step, roundFit);

        var outPts = new List<Vector2> { ctrl[0] };
        int n = ctrl.Count;
        for (int i = 0; i < n - 1; i++)
        {
            // Phantom endpoints (clamp) so the curve passes through the first/last control points.
            Vector2 p0 = ctrl[Mathf.Max(i - 1, 0)];
            Vector2 p1 = ctrl[i];
            Vector2 p2 = ctrl[i + 1];
            Vector2 p3 = ctrl[Mathf.Min(i + 2, n - 1)];

            float segLen = Vector2.Distance(p1, p2);
            int subdiv = Subdivisions(segLen, step, roundFit);
            for (int s = 1; s <= subdiv; s++)
            {
                float u = (float)s / subdiv;                         // 0..1 along this segment
                Vector2 curve  = CentripetalCatmullRom(p0, p1, p2, p3, u);
                Vector2 linear = Vector2.LerpUnclamped(p1, p2, u);    // the straight chord
                outPts.Add(Vector2.LerpUnclamped(linear, curve, smoothing));
                if (outPts.Count >= MaxPoints) return outPts;
            }
        }
        return outPts;
    }

    // Replace each interior corner sharper than `minTurnDeg` with a tangent circular arc of radius
    // `radius`, clamped to half the shorter adjacent segment so the fillet always fits. This caps the
    // turn radius at >= the clamped radius, so a ribbon of half-width <= radius never folds back over
    // its inner edge (and the miter never spikes) at the corner. Run on the SPARSE control points
    // before Smooth — both the polyline resample (smoothing 0) and the Catmull-Rom (smoothing 1) then
    // follow the arc. Endpoints pass through unchanged. Pure/static for unit testing.
    public static List<Vector2> RoundCorners(IReadOnlyList<Vector2> points, float radius,
                                             float minTurnDeg = 30f, int arcSegments = 6)
    {
        if (points == null || points.Count < 3 || radius <= 0f) return CopyOrEmpty(points);
        var ctrl = Dedup(points);
        if (ctrl.Count < 3) return ctrl;

        float minTurnRad = minTurnDeg * Mathf.Deg2Rad;
        int n = ctrl.Count;
        var outPts = new List<Vector2> { ctrl[0] };
        for (int i = 1; i < n - 1; i++)
        {
            Vector2 p0 = ctrl[i - 1], p1 = ctrl[i], p2 = ctrl[i + 1];
            Vector2 inDir = p1 - p0, outDir = p2 - p1;
            float inLen = inDir.magnitude, outLen = outDir.magnitude;
            if (inLen < 1e-4f || outLen < 1e-4f) { outPts.Add(p1); continue; }
            inDir /= inLen; outDir /= outLen;

            // Turn angle = exterior angle between the two edge directions.
            float turn = Mathf.Acos(Mathf.Clamp(Vector2.Dot(inDir, outDir), -1f, 1f));
            if (turn < minTurnRad) { outPts.Add(p1); continue; }   // gentle: leave the corner crisp

            // Interior half-angle; setback = distance from the corner to each tangent point.
            float halfInterior = (Mathf.PI - turn) * 0.5f;          // (0, π/2)
            float tanHalf = Mathf.Tan(halfInterior);
            float r = Mathf.Min(radius, 0.5f * inLen, 0.5f * outLen);
            float setback = r / Mathf.Max(tanHalf, 1e-4f);
            // If the setback would still overrun a short segment, shrink the radius to fit.
            float maxSetback = Mathf.Min(inLen, outLen) * 0.5f;
            if (setback > maxSetback) { setback = maxSetback; r = setback * tanHalf; }

            Vector2 tIn  = p1 - inDir  * setback;   // tangent point on the incoming edge
            Vector2 tOut = p1 + outDir * setback;   // tangent point on the outgoing edge

            // Arc center: inward along the bisector of the two tangent points.
            Vector2 bis = (tIn - p1).normalized + (tOut - p1).normalized;
            if (bis.sqrMagnitude < 1e-8f) { outPts.Add(p1); continue; }   // ~straight, nothing to round
            bis.Normalize();
            float centerDist = r / Mathf.Max(Mathf.Sin(halfInterior), 1e-4f);
            Vector2 center = p1 + bis * centerDist;

            float a0 = Mathf.Atan2(tIn.y - center.y, tIn.x - center.x);
            float a1 = Mathf.Atan2(tOut.y - center.y, tOut.x - center.x);
            // Sweep the short way around the corner.
            float delta = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, a1 * Mathf.Rad2Deg) * Mathf.Deg2Rad;

            outPts.Add(tIn);
            int seg = Mathf.Max(1, arcSegments);
            for (int s = 1; s < seg; s++)
            {
                float a = a0 + delta * s / seg;
                outPts.Add(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
            outPts.Add(tOut);
        }
        outPts.Add(ctrl[n - 1]);
        return outPts;
    }

    // Ramer–Douglas–Peucker: drop points that lie within `tolerance` meters of the line between the
    // retained neighbours. Always keeps the first and last point. Used to denoise freehand strokes.
    public static List<Vector2> Simplify(IReadOnlyList<Vector2> points, float tolerance)
    {
        if (points == null || points.Count < 3 || tolerance <= 0f) return CopyOrEmpty(points);
        var keep = new bool[points.Count];
        keep[0] = keep[points.Count - 1] = true;
        RdpRecurse(points, 0, points.Count - 1, tolerance, keep);

        var outPts = new List<Vector2>();
        for (int i = 0; i < points.Count; i++) if (keep[i]) outPts.Add(points[i]);
        return outPts;
    }

    // ---- internals ---------------------------------------------------------

    private static void RdpRecurse(IReadOnlyList<Vector2> pts, int lo, int hi, float tol, bool[] keep)
    {
        if (hi <= lo + 1) return;
        float maxDist = -1f; int idx = -1;
        for (int i = lo + 1; i < hi; i++)
        {
            float d = PerpDistance(pts[i], pts[lo], pts[hi]);
            if (d > maxDist) { maxDist = d; idx = i; }
        }
        if (maxDist > tol && idx > 0)
        {
            keep[idx] = true;
            RdpRecurse(pts, lo, idx, tol, keep);
            RdpRecurse(pts, idx, hi, tol, keep);
        }
    }

    private static float PerpDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-9f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        Vector2 proj = a + t * ab;
        return Vector2.Distance(p, proj);
    }

    private static int Subdivisions(float segLen, float step, bool roundFit) =>
        Mathf.Max(1, roundFit ? Mathf.RoundToInt(segLen / step)
                              : Mathf.CeilToInt(segLen / step));

    private static List<Vector2> ResamplePolyline(IReadOnlyList<Vector2> pts, float step, bool roundFit)
    {
        var outPts = new List<Vector2> { pts[0] };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            float segLen = Vector2.Distance(pts[i], pts[i + 1]);
            int subdiv = Subdivisions(segLen, step, roundFit);
            for (int s = 1; s <= subdiv; s++)
            {
                outPts.Add(Vector2.LerpUnclamped(pts[i], pts[i + 1], (float)s / subdiv));
                if (outPts.Count >= MaxPoints) return outPts;
            }
        }
        return outPts;
    }

    // Barry–Goldman pyramidal evaluation of a centripetal (alpha=0.5) Catmull-Rom segment p1->p2.
    private static Vector2 CentripetalCatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float u)
    {
        const float alpha = 0.5f;
        float t0 = 0f;
        float t1 = t0 + Mathf.Pow(Mathf.Max(Vector2.Distance(p0, p1), 1e-4f), alpha);
        float t2 = t1 + Mathf.Pow(Mathf.Max(Vector2.Distance(p1, p2), 1e-4f), alpha);
        float t3 = t2 + Mathf.Pow(Mathf.Max(Vector2.Distance(p2, p3), 1e-4f), alpha);
        float t  = Mathf.Lerp(t1, t2, u);

        Vector2 a1 = Lerp(p0, p1, t0, t1, t);
        Vector2 a2 = Lerp(p1, p2, t1, t2, t);
        Vector2 a3 = Lerp(p2, p3, t2, t3, t);
        Vector2 b1 = Lerp(a1, a2, t0, t2, t);
        Vector2 b2 = Lerp(a2, a3, t1, t3, t);
        return Lerp(b1, b2, t1, t2, t);
    }

    private static Vector2 Lerp(Vector2 a, Vector2 b, float ta, float tb, float t)
    {
        if (Mathf.Abs(tb - ta) < 1e-6f) return a;
        float k = (t - ta) / (tb - ta);
        return a + (b - a) * k;
    }

    private static List<Vector2> Dedup(IReadOnlyList<Vector2> pts)
    {
        var outPts = new List<Vector2> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
            if (Vector2.Distance(pts[i], outPts[outPts.Count - 1]) > 1e-4f) outPts.Add(pts[i]);
        return outPts;
    }

    private static List<Vector2> CopyOrEmpty(IReadOnlyList<Vector2> pts)
    {
        var outPts = new List<Vector2>();
        if (pts != null) outPts.AddRange(pts);
        return outPts;
    }
}
