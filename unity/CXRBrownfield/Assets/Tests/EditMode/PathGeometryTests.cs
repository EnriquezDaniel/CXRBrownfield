using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

// PathGeometry lives in the CXRAuthoring assembly (referenced by this test asmdef).
[TestFixture]
public class PathGeometryTests
{
    [Test]
    public void Smooth_PreservesEndpoints()
    {
        var pts = new List<Vector2> { new(0, 0), new(5, 3), new(10, 0) };
        var outPts = PathGeometry.Smooth(pts, 1f);

        Assert.GreaterOrEqual(outPts.Count, 2);
        Assert.That(Vector2.Distance(outPts[0], pts[0]), Is.LessThan(0.01f));
        Assert.That(Vector2.Distance(outPts[outPts.Count - 1], pts[pts.Count - 1]), Is.LessThan(0.01f));
    }

    [Test]
    public void Smooth_StraightLine_StaysCollinear()
    {
        var pts = new List<Vector2> { new(0, 0), new(5, 0), new(10, 0) };
        var outPts = PathGeometry.Smooth(pts, 1f);

        foreach (var p in outPts) Assert.That(Mathf.Abs(p.y), Is.LessThan(0.01f));
    }

    [Test]
    public void Smooth_DensifiesSparseInput()
    {
        var pts = new List<Vector2> { new(0, 0), new(100, 0) };
        var outPts = PathGeometry.Smooth(pts, 1f, step: 1f);

        // Roughly one point per `step` meters along the 100m span.
        Assert.Greater(outPts.Count, 50);
    }

    [Test]
    public void Smooth_LCorner_CurveDeviatesFromPolyline()
    {
        var pts = new List<Vector2> { new(0, 0), new(10, 0), new(10, 10) };
        var curved = PathGeometry.Smooth(pts, 1f);

        // The spline still passes through the control points (interpolating), but between them it must
        // bow off the straight two-segment polyline — that bow is the smoothing.
        float maxDev = 0f;
        foreach (var p in curved)
        {
            float d = Mathf.Min(DistToSegment(p, pts[0], pts[1]), DistToSegment(p, pts[1], pts[2]));
            maxDev = Mathf.Max(maxDev, d);
        }
        Assert.Greater(maxDev, 0.05f);
    }

    [Test]
    public void Smooth_ZeroSmoothing_ResamplesButStaysOnPolyline()
    {
        var pts = new List<Vector2> { new(0, 0), new(10, 0), new(10, 10) };
        var outPts = PathGeometry.Smooth(pts, 0f, step: 1f);

        // Every output point must lie on one of the two original straight segments (no curve bow).
        foreach (var p in outPts)
        {
            float d = Mathf.Min(
                DistToSegment(p, pts[0], pts[1]),
                DistToSegment(p, pts[1], pts[2]));
            Assert.That(d, Is.LessThan(0.01f));
        }
    }

    [Test]
    public void Smooth_RoundFit_StretchesSpacingToNearestCount()
    {
        // 10m at step 3: round(10/3)=3 subdivisions of 3.333m — spacing stretches past the step.
        var pts = new List<Vector2> { new(0, 0), new(10, 0) };
        var outPts = PathGeometry.Smooth(pts, 0f, step: 3f, roundFit: true);

        Assert.AreEqual(4, outPts.Count);
        for (int i = 0; i < outPts.Count; i++)
            Assert.That(Vector2.Distance(outPts[i], new Vector2(10f / 3f * i, 0)), Is.LessThan(1e-3f));
    }

    [Test]
    public void Smooth_DefaultCeilFit_NeverExceedsStep()
    {
        // Guard: without roundFit the ceil behavior is unchanged — 10m at step 3 gives 4 subdivisions
        // of 2.5m (this locks in the path-ribbon resample the fences no longer share).
        var pts = new List<Vector2> { new(0, 0), new(10, 0) };
        var outPts = PathGeometry.Smooth(pts, 0f, step: 3f);

        Assert.AreEqual(5, outPts.Count);
        for (int i = 1; i < outPts.Count; i++)
            Assert.That(Vector2.Distance(outPts[i - 1], outPts[i]), Is.LessThanOrEqualTo(3f + 1e-3f));
    }

    [Test]
    public void Simplify_DropsCollinearMidpoints()
    {
        var pts = new List<Vector2> { new(0, 0), new(1, 0), new(2, 0), new(3, 0), new(3, 5) };
        var outPts = PathGeometry.Simplify(pts, 0.1f);

        // The three interior collinear points collapse to the corner; endpoints + corner remain.
        Assert.AreEqual(3, outPts.Count);
        Assert.AreEqual(new Vector2(0, 0), outPts[0]);
        Assert.AreEqual(new Vector2(3, 0), outPts[1]);
        Assert.AreEqual(new Vector2(3, 5), outPts[2]);
    }

    [Test]
    public void Simplify_KeepsCornersWithinTolerance()
    {
        var pts = new List<Vector2> { new(0, 0), new(5, 1f), new(10, 0) };
        var outPts = PathGeometry.Simplify(pts, 0.5f);

        // The 1m deviation exceeds the 0.5m tolerance, so the middle point is retained.
        Assert.AreEqual(3, outPts.Count);
    }

    [Test]
    public void RoundCorners_SharpCorner_SoftensTurnAndPreservesEndpoints()
    {
        // A 90° L-corner with wide arms so the fillet fits comfortably.
        var pts = new List<Vector2> { new(0, 0), new(10, 0), new(10, 10) };
        var outPts = PathGeometry.RoundCorners(pts, radius: 2f, minTurnDeg: 30f, arcSegments: 6);

        // Endpoints are untouched.
        Assert.That(Vector2.Distance(outPts[0], pts[0]), Is.LessThan(1e-3f));
        Assert.That(Vector2.Distance(outPts[outPts.Count - 1], pts[pts.Count - 1]), Is.LessThan(1e-3f));

        // The fillet replaced the single sharp vertex with several arc points.
        Assert.Greater(outPts.Count, pts.Count);

        // No interior vertex turns as sharply as the original 90°; each step is gentle.
        Assert.That(MaxTurnDeg(outPts), Is.LessThan(89f));

        // The hard corner (10,0) is no longer present (it was rounded away).
        foreach (var p in outPts)
            Assert.That(Vector2.Distance(p, new Vector2(10, 0)), Is.GreaterThan(0.1f));
    }

    [Test]
    public void RoundCorners_GentleCorner_Unchanged()
    {
        // ~11° bend — below the 30° threshold, so it passes through untouched.
        var pts = new List<Vector2> { new(0, 0), new(10, 0), new(20, 2) };
        var outPts = PathGeometry.RoundCorners(pts, radius: 2f, minTurnDeg: 30f);

        Assert.AreEqual(pts.Count, outPts.Count);
        for (int i = 0; i < pts.Count; i++)
            Assert.That(Vector2.Distance(outPts[i], pts[i]), Is.LessThan(1e-3f));
    }

    [Test]
    public void RoundCorners_ShortSegments_ClampsWithoutSpike()
    {
        // Arms (1m) shorter than the requested radius (5m): the fillet must clamp to fit, and no
        // output vertex may shoot far outside the input's bounding box (i.e. no miter-style spike).
        var pts = new List<Vector2> { new(0, 0), new(1, 0), new(1, 1) };
        var outPts = PathGeometry.RoundCorners(pts, radius: 5f, minTurnDeg: 30f);

        foreach (var p in outPts)
        {
            Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y));
            Assert.That(p.x, Is.InRange(-0.5f, 1.5f));
            Assert.That(p.y, Is.InRange(-0.5f, 1.5f));
        }
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-9f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + t * ab);
    }

    // Largest turn angle (degrees) at any interior vertex of a polyline.
    private static float MaxTurnDeg(List<Vector2> pts)
    {
        float max = 0f;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            Vector2 a = (pts[i] - pts[i - 1]).normalized;
            Vector2 b = (pts[i + 1] - pts[i]).normalized;
            if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f) continue;
            float turn = Mathf.Acos(Mathf.Clamp(Vector2.Dot(a, b), -1f, 1f)) * Mathf.Rad2Deg;
            max = Mathf.Max(max, turn);
        }
        return max;
    }
}
