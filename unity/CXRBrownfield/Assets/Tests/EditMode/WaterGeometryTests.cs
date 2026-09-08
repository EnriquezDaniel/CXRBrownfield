using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

[TestFixture]
public class WaterGeometryTests
{
    private static readonly List<Vector2> Square = new() { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };

    [Test]
    public void Kind_RiverIsCaseInsensitive_PondNeedsThreePoints()
    {
        Assert.IsTrue(WaterGeometry.IsRiver("River"));
        Assert.IsFalse(WaterGeometry.IsRiver("pond"));
        Assert.IsFalse(WaterGeometry.IsRiver(null));
        Assert.AreEqual(3, WaterGeometry.MinPoints("pond"));
        Assert.AreEqual(2, WaterGeometry.MinPoints("river"));
        Assert.IsFalse(WaterGeometry.HasGeometry(new WaterBodyDef { kind = "pond", points = new[] { new[] { 0f, 0f }, new[] { 1f, 1f } } }));
        Assert.IsTrue(WaterGeometry.HasGeometry(new WaterBodyDef { kind = "river", points = new[] { new[] { 0f, 0f }, new[] { 1f, 1f } } }));
        Assert.IsFalse(WaterGeometry.HasGeometry(null));
    }

    [Test]
    public void SurfaceY_IsHeldAboveTheBed_AndUnderTheHeightmapCeiling()
    {
        Assert.AreEqual(-1.5f, WaterGeometry.BedY(1.5f), 1e-6f);
        Assert.AreEqual(-1.5f + WaterGeometry.MIN_DEPTH, WaterGeometry.MinSurfaceY(1.5f), 1e-6f);
        Assert.AreEqual(0f,    WaterGeometry.ClampSurfaceY(0f, 1.5f), 1e-6f, "level with flat ground is fine");
        Assert.AreEqual(-1f,   WaterGeometry.ClampSurfaceY(-1f, 1.5f), 1e-6f, "sunk into the hole is fine");
        Assert.AreEqual(WaterGeometry.MinSurfaceY(1.5f), WaterGeometry.ClampSurfaceY(-9f, 1.5f), 1e-6f, "never below the bed");
        Assert.AreEqual(WaterGeometry.MAX_SURFACE_Y, WaterGeometry.ClampSurfaceY(99f, 1.5f), 1e-6f);
        Assert.AreEqual(-0.5f, WaterGeometry.SurfaceY(new WaterBodyDef { surfaceY = -0.5f, depth = 2f }), 1e-6f);
        Assert.AreEqual(0f, WaterGeometry.SurfaceY(null), 1e-6f);
    }

    [Test]
    public void SignedDistanceToPolygon_NegativeInside_PositiveOutside()
    {
        Assert.AreEqual(-5f, WaterGeometry.SignedDistanceToPolygon(5f, 5f, Square), 1e-4f);
        Assert.AreEqual(-1f, WaterGeometry.SignedDistanceToPolygon(1f, 5f, Square), 1e-4f);
        Assert.AreEqual( 3f, WaterGeometry.SignedDistanceToPolygon(13f, 5f, Square), 1e-4f);
        Assert.AreEqual( 0f, WaterGeometry.SignedDistanceToPolygon(10f, 5f, Square), 1e-4f);
    }

    [Test]
    public void SignedDistanceToRibbon_UsesHalfWidth()
    {
        var line = new List<Vector2> { new(0, 0), new(20, 0) };
        Assert.AreEqual(-2f, WaterGeometry.SignedDistanceToRibbon(10f, 1f, line, 3f), 1e-4f);
        Assert.AreEqual( 2f, WaterGeometry.SignedDistanceToRibbon(10f, 5f, line, 3f), 1e-4f);
        Assert.AreEqual( 2f, WaterGeometry.SignedDistanceToRibbon(25f, 0f, line, 3f), 1e-4f);
    }

    [Test]
    public void RiverCenterline_MatchesPathRecipe()
    {
        var ctrl = new List<Vector2> { new(0, 0), new(10, 0), new(10, 10) };
        var expected = PathGeometry.Smooth(PathGeometry.RoundCorners(ctrl, 3f), 0.5f);
        var actual   = WaterGeometry.RiverCenterline(ctrl, 6f, 0.5f);
        Assert.AreEqual(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
            Assert.AreEqual(0f, Vector2.Distance(expected[i], actual[i]), 1e-5f, $"point {i}");
    }

    [Test]
    public void RibbonEdges_SitHalfWidthEitherSide()
    {
        var dense = new List<Vector2> { new(0, 0), new(5, 0), new(10, 0) };
        var left = new List<Vector2>(); var right = new List<Vector2>();
        WaterGeometry.RibbonEdges(dense, 4f, left, right);
        Assert.AreEqual(3, left.Count);
        Assert.AreEqual(3, right.Count);
        Assert.AreEqual(2f, Mathf.Abs(left[1].y), 1e-5f);
        Assert.AreEqual(2f, Mathf.Abs(right[1].y), 1e-5f);
        Assert.AreNotEqual(Mathf.Sign(left[1].y), Mathf.Sign(right[1].y));
    }

    [Test]
    public void ShoreSamples_PondIsDenseRing_RiverIsBothEdges()
    {
        var pond = new WaterBodyDef { kind = "pond", points = new[] { new[] { 0f, 0f }, new[] { 10f, 0f }, new[] { 10f, 10f }, new[] { 0f, 10f } } };
        var shore = WaterGeometry.ShoreSamples(pond, 1f);
        Assert.GreaterOrEqual(shore.Count, 40);
        foreach (var p in shore)
            Assert.AreEqual(0f, WaterGeometry.DistanceToRing(p.x, p.y, Square), 1e-4f, "pond shore samples lie on the ring");

        var river = new WaterBodyDef { kind = "river", width = 4f, smoothing = 0f, points = new[] { new[] { 0f, 0f }, new[] { 20f, 0f } } };
        var rs = WaterGeometry.ShoreSamples(river);
        Assert.Greater(rs.Count, 4);
        foreach (var p in rs) Assert.AreEqual(2f, Mathf.Abs(p.y), 1e-4f, "river shore samples sit half a width from the centerline");

        Assert.IsEmpty(WaterGeometry.ShoreSamples(new WaterBodyDef { kind = "pond", points = new[] { new[] { 0f, 0f } } }));
    }

    [Test]
    public void PondMesh_IsFlatAtSurface_AndFillsTheRing()
    {
        var mesh = WaterGeometry.BuildPondMesh(Square, 2.5f);
        Assert.IsNotNull(mesh);
        Assert.AreEqual(6, mesh.triangles.Length);
        foreach (var v in mesh.vertices) Assert.AreEqual(2.5f, v.y, 1e-5f);
        foreach (var n in mesh.normals) Assert.Greater(n.y, 0.99f);
        Assert.IsNull(WaterGeometry.BuildPondMesh(new List<Vector2> { new(0, 0), new(1, 0) }, 0f));
    }

    [Test]
    public void PondMesh_SelfIntersectingRing_StillRendersViaFan()
    {
        var bowtie = new List<Vector2> { new(0, 0), new(10, 10), new(10, 0), new(0, 10) };
        var mesh = WaterGeometry.BuildPondMesh(bowtie, 0f);
        Assert.IsNotNull(mesh);
        Assert.Greater(mesh.triangles.Length, 0);
    }

    [Test]
    public void RiverMesh_IsFlatAtSurface()
    {
        var dense = WaterGeometry.RiverCenterline(new List<Vector2> { new(0, 0), new(30, 0), new(30, 30) }, 6f, 0.5f);
        var mesh = WaterGeometry.BuildRiverMesh(dense, 6f, -1.25f);
        Assert.IsNotNull(mesh);
        foreach (var v in mesh.vertices) Assert.AreEqual(-1.25f, v.y, 1e-5f);
        Assert.IsNull(WaterGeometry.BuildRiverMesh(new List<Vector2> { new(0, 0) }, 6f, 0f));
    }
}
