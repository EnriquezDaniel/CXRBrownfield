using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// FenceLinker lives in the CXRAuthoring assembly (referenced by this test asmdef). These tests pin
// down the fence-network linking rules: T-junctions split the touched fence, X-crossings split both
// fences, junctions weld onto nearby control points, shared corners and collinear overlap never
// split, and no split may ever produce a sliver.
[TestFixture]
public class FenceLinkerTests
{
    private static FenceDef Fence(string id, params Vector2[] pts)
    {
        var jag = new float[pts.Length][];
        for (int i = 0; i < pts.Length; i++) jag[i] = new[] { pts[i].x, pts[i].y };
        return new FenceDef { id = id, fenceType = "picket", smoothing = 0f, height = 1.2f, points = jag };
    }

    private static List<Vector2> Poly(FenceDef f) =>
        f.points.Select(p => new Vector2(p[0], p[1])).ToList();

    private static float Length(FenceDef f)
    {
        var pts = Poly(f);
        float len = 0f;
        for (int i = 1; i < pts.Count; i++) len += Vector2.Distance(pts[i - 1], pts[i]);
        return len;
    }

    private static bool HasEndpoint(FenceDef f, Vector2 p, float tol = 1e-3f)
    {
        var pts = Poly(f);
        return Vector2.Distance(pts[0], p) < tol || Vector2.Distance(pts[pts.Count - 1], p) < tol;
    }

    [Test]
    public void Link_NoIntersections_AppendsSingle()
    {
        var fences = new List<FenceDef> { Fence("a", new(0, 0), new(10, 0)) };
        var added = FenceLinker.Link(fences, new List<Vector2> { new(0, 5), new(10, 5) }, "chain_link", 0.25f, 2f);

        Assert.AreEqual(2, fences.Count);
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual("chain_link", added[0].fenceType);
        Assert.AreEqual(0.25f, added[0].smoothing);
        Assert.AreEqual(2f, added[0].height);
        Assert.AreEqual(2, added[0].points.Length);
        Assert.That(Vector2.Distance(Poly(added[0])[0], new Vector2(0, 5)), Is.LessThan(1e-4f));
        Assert.That(Vector2.Distance(Poly(added[0])[1], new Vector2(10, 5)), Is.LessThan(1e-4f));
    }

    [Test]
    public void Link_TJunction_SplitsExistingIntoTwo()
    {
        var fences = new List<FenceDef> { Fence("old", new(0, 0), new(10, 0)) };
        var added = FenceLinker.Link(fences, new List<Vector2> { new(5, 5), new(5, 0) }, "picket", 0f, 0f);

        Assert.AreEqual(3, fences.Count);       // two halves + the new fence
        Assert.AreEqual(1, added.Count);        // the new run stays whole (its endpoint is the junction)

        var halves = fences.Where(f => f != added[0]).ToList();
        Assert.AreEqual("old", halves[0].id);   // first half keeps the original id
        Assert.AreNotEqual("old", halves[1].id);
        foreach (var h in halves)
        {
            Assert.IsTrue(HasEndpoint(h, new Vector2(5, 0)), "each half must end/start at the junction");
            Assert.AreEqual("picket", h.fenceType);
            Assert.AreEqual(1.2f, h.height);
        }
    }

    [Test]
    public void Link_XCrossing_SplitsBoth()
    {
        var fences = new List<FenceDef> { Fence("old", new(0, 0), new(10, 0)) };
        var added = FenceLinker.Link(fences, new List<Vector2> { new(5, -5), new(5, 5) }, "picket", 0f, 0f);

        Assert.AreEqual(4, fences.Count);
        Assert.AreEqual(2, added.Count);
        foreach (var f in fences)
            Assert.IsTrue(HasEndpoint(f, new Vector2(5, 0)), $"{f.id} must have an endpoint at the junction");
    }

    [Test]
    public void Link_MultiCrossing_SplitsIntoOrderedRuns()
    {
        // One horizontal run crossing two vertical fences → run splits into 3 ordered pieces,
        // each vertical into 2.
        var fences = new List<FenceDef>
        {
            Fence("v1", new(3, -5), new(3, 5)),
            Fence("v2", new(9, -5), new(9, 5)),
        };
        var added = FenceLinker.Link(fences, new List<Vector2> { new(0, 0), new(12, 0) }, "picket", 0f, 0f);

        Assert.AreEqual(7, fences.Count);   // 2 + 2 halves + 3 new runs
        Assert.AreEqual(3, added.Count);

        // New runs arrive ordered along the drawn polyline.
        Assert.That(Poly(added[0])[0].x, Is.EqualTo(0f).Within(1e-3f));
        Assert.That(Poly(added[0])[1].x, Is.EqualTo(3f).Within(1e-3f));
        Assert.That(Poly(added[1])[0].x, Is.EqualTo(3f).Within(1e-3f));
        Assert.That(Poly(added[1])[1].x, Is.EqualTo(9f).Within(1e-3f));
        Assert.That(Poly(added[2])[0].x, Is.EqualTo(9f).Within(1e-3f));
        Assert.That(Poly(added[2])[1].x, Is.EqualTo(12f).Within(1e-3f));
    }

    [Test]
    public void Link_ZigzagCrossingSameFenceTwice_SplitsIntoThree()
    {
        var fences = new List<FenceDef> { Fence("old", new(0, 0), new(20, 0)) };
        // V-shape dipping below the horizontal fence: crosses at x=5 (down) and x=15 (up).
        var added = FenceLinker.Link(fences,
            new List<Vector2> { new(0, 5), new(10, -5), new(20, 5) }, "picket", 0f, 0f);

        Assert.AreEqual(3, added.Count);        // new run split at both crossings
        Assert.AreEqual(6, fences.Count);       // old into 3 + new into 3
        Assert.AreEqual(3, fences.Count(f => !added.Contains(f)));
    }

    [Test]
    public void Link_WeldsToNearbyControlPoint()
    {
        // Existing fence has an interior control point at (5,0); a crossing 3cm away must reuse it.
        var fences = new List<FenceDef> { Fence("old", new(0, 0), new(5, 0), new(10, 0)) };
        FenceLinker.Link(fences, new List<Vector2> { new(5.03f, -5), new(5.03f, 5) }, "picket", 0f, 0f);

        // The old fence split exactly at (5,0) — each half has exactly 2 points, no near-duplicates.
        var oldPieces = fences.Where(f => Poly(f).All(p => Mathf.Abs(p.y) < 1e-3f)).ToList();
        Assert.AreEqual(2, oldPieces.Count);
        foreach (var h in oldPieces)
        {
            Assert.AreEqual(2, h.points.Length, "weld must reuse (5,0), not insert a near-duplicate");
            Assert.IsTrue(HasEndpoint(h, new Vector2(5, 0)));
        }
    }

    [Test]
    public void Link_SharedEndpoint_NoSplit()
    {
        var fences = new List<FenceDef> { Fence("old", new(0, 0), new(10, 0)) };
        var added = FenceLinker.Link(fences, new List<Vector2> { new(10, 0), new(10, 5) }, "picket", 0f, 0f);

        Assert.AreEqual(2, fences.Count);
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(2, fences[0].points.Length);   // old untouched
        Assert.AreEqual("old", fences[0].id);
    }

    [Test]
    public void Link_SliverCut_SkippedOnOldFence()
    {
        // Crossing 0.2m from the old fence's end: splitting there would leave a 0.2m sliver, so the
        // old fence is left whole; the new fence still splits (both its pieces are long).
        var fences = new List<FenceDef> { Fence("old", new(0, 0), new(10, 0)) };
        var added = FenceLinker.Link(fences, new List<Vector2> { new(9.8f, -5), new(9.8f, 5) }, "picket", 0f, 0f);

        var oldF = fences.First(f => f.id == "old");
        Assert.AreEqual(2, oldF.points.Length);        // not split
        Assert.AreEqual(2, added.Count);               // new fence split at the crossing
        foreach (var f in fences)
        {
            Assert.GreaterOrEqual(f.points.Length, 2);
            Assert.GreaterOrEqual(Length(f), FenceLinker.MinSeg - 1e-3f);
        }
    }

    [Test]
    public void Link_CollinearOverlap_NoSplit()
    {
        var fences = new List<FenceDef> { Fence("old", new(0, 0), new(10, 0)) };
        var added = FenceLinker.Link(fences, new List<Vector2> { new(2, 0), new(8, 0) }, "picket", 0f, 0f);

        Assert.AreEqual(2, fences.Count);
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(2, fences.First(f => f.id == "old").points.Length);
    }

    [Test]
    public void FindCuts_OrderedAlongCtrl()
    {
        var fences = new List<FenceDef>
        {
            Fence("v1", new(3, -5), new(3, 5)),
            Fence("v2", new(9, -5), new(9, 5)),
        };
        var cuts = FenceLinker.FindCuts(fences, new List<Vector2> { new(0, 0), new(12, 0) });

        Assert.AreEqual(2, cuts.Count);
        Assert.That(cuts[0].x, Is.EqualTo(3f).Within(1e-3f));
        Assert.That(cuts[1].x, Is.EqualTo(9f).Within(1e-3f));
    }

    [Test]
    public void Link_DegenerateCtrl_NoMutation()
    {
        var fences = new List<FenceDef> { Fence("old", new(0, 0), new(10, 0)) };
        var added = FenceLinker.Link(fences, new List<Vector2> { new(5, 5), new(5.01f, 5.01f) }, "picket", 0f, 0f);

        Assert.AreEqual(0, added.Count);
        Assert.AreEqual(1, fences.Count);
        Assert.AreEqual(2, fences[0].points.Length);
    }
}
