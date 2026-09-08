using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

[TestFixture]
public class PolygonTriangulatorTests
{
    private static float TriArea(IReadOnlyList<Vector2> ring, List<int> tris)
    {
        float sum = 0f;
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            Vector2 a = ring[tris[i]], b = ring[tris[i + 1]], c = ring[tris[i + 2]];
            sum += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
        }
        return sum;
    }

    private static float RingArea(IReadOnlyList<Vector2> ring)
    {
        var pts = new List<float[]>();
        foreach (var p in ring) pts.Add(new[] { p.x, p.y });
        return EnvironmentScale.PolygonArea(pts);
    }

    private static void AssertFacesUp(IReadOnlyList<Vector2> ring, List<int> tris)
    {
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            Vector3 a = new(ring[tris[i]].x, 0f, ring[tris[i]].y);
            Vector3 b = new(ring[tris[i + 1]].x, 0f, ring[tris[i + 1]].y);
            Vector3 c = new(ring[tris[i + 2]].x, 0f, ring[tris[i + 2]].y);
            Assert.Greater(Vector3.Cross(b - a, c - a).y, 0f, $"triangle {i / 3} must face +Y");
        }
    }

    [Test]
    public void Square_GivesTwoTriangles_FacingUp()
    {
        var ring = new List<Vector2> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var tris = PolygonTriangulator.Triangulate(ring);
        Assert.AreEqual(6, tris.Count);
        Assert.AreEqual(100f, TriArea(ring, tris), 1e-3f);
        AssertFacesUp(ring, tris);
    }

    [Test]
    public void ConcaveL_FillsExactly_NMinusTwoTriangles()
    {
        var ring = new List<Vector2> { new(0, 0), new(10, 0), new(10, 4), new(4, 4), new(4, 10), new(0, 10) };
        var tris = PolygonTriangulator.Triangulate(ring);
        Assert.AreEqual((ring.Count - 2) * 3, tris.Count);
        Assert.AreEqual(RingArea(ring), TriArea(ring, tris), 1e-3f);
        AssertFacesUp(ring, tris);
    }

    [Test]
    public void ClockwiseInput_GivesSameAreaAndStillFacesUp()
    {
        var ccw = new List<Vector2> { new(0, 0), new(10, 0), new(10, 4), new(4, 4), new(4, 10), new(0, 10) };
        var cw  = new List<Vector2>(ccw); cw.Reverse();
        var tris = PolygonTriangulator.Triangulate(cw);
        Assert.AreEqual((cw.Count - 2) * 3, tris.Count);
        Assert.AreEqual(RingArea(cw), TriArea(cw, tris), 1e-3f);
        AssertFacesUp(cw, tris);
    }

    [Test]
    public void Degenerate_ReturnsEmpty()
    {
        Assert.IsEmpty(PolygonTriangulator.Triangulate(null));
        Assert.IsEmpty(PolygonTriangulator.Triangulate(new List<Vector2> { new(0, 0), new(1, 1) }));
        Assert.IsEmpty(PolygonTriangulator.Triangulate(new List<Vector2> { new(0, 0), new(1, 1), new(2, 2), new(3, 3) }));
        Assert.AreEqual(0, PolygonTriangulator.ExpectedTriangles(new List<Vector2> { new(0, 0), new(1, 1) }));
    }

    [Test]
    public void RepeatedClosingPoint_AndDuplicates_AreTolerated()
    {
        var ring = new List<Vector2> { new(0, 0), new(0, 0), new(10, 0), new(10, 10), new(10, 10), new(0, 10), new(0, 0) };
        var tris = PolygonTriangulator.Triangulate(ring);
        Assert.AreEqual(2, PolygonTriangulator.ExpectedTriangles(ring));
        Assert.AreEqual(6, tris.Count);
        Assert.AreEqual(100f, TriArea(ring, tris), 1e-3f);
    }

    [Test]
    public void SelfIntersectingBowtie_ReturnsEmpty_SoCallerCanFallBack()
    {
        var ring = new List<Vector2> { new(0, 0), new(10, 10), new(10, 0), new(0, 10) };
        var tris = PolygonTriangulator.Triangulate(ring);
        // Either an empty result or a full-count result is acceptable; a partial fill is not.
        Assert.IsTrue(tris.Count == 0 || tris.Count == 6, $"partial fill: {tris.Count / 3} triangles");
    }
}
