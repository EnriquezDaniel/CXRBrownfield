using System.Collections.Generic;
using NUnit.Framework;

// TerrainStack lives in the CXRAuthoring assembly. These pin how several loaded grounds share the
// world: the rectangle a site asks for, which ground answers a surface query, and the small drop
// that keeps a backdrop's ground under the active one.
[TestFixture]
public class TerrainStackTests
{
    private static TerrainStack.GroundRect Rect(float x, float z, float w, float l) =>
        new TerrainStack.GroundRect { x = x, z = z, w = w, l = l };

    // ---- TryRectOf / ClampSize ----

    [Test]
    public void TryRectOf_NullOrigin_SitsAtWorldOrigin()
    {
        var site = new SiteDef { terrainSize = new[] { 140f, 20f } };
        Assert.IsTrue(TerrainStack.TryRectOf(site, out var r));
        Assert.AreEqual(0f, r.x); Assert.AreEqual(0f, r.z);
        Assert.AreEqual(140f, r.w); Assert.AreEqual(20f, r.l);
    }

    [Test]
    public void TryRectOf_HonoursTerrainOrigin()
    {
        var site = new SiteDef { terrainSize = new[] { 40f, 30f }, terrainOrigin = new[] { 100f, -50f } };
        Assert.IsTrue(TerrainStack.TryRectOf(site, out var r));
        Assert.AreEqual(100f, r.x); Assert.AreEqual(-50f, r.z);
    }

    [Test]
    public void TryRectOf_NaNOrigin_FallsBackToWorldOrigin()
    {
        var site = new SiteDef { terrainSize = new[] { 40f, 30f }, terrainOrigin = new[] { float.NaN, 5f } };
        Assert.IsTrue(TerrainStack.TryRectOf(site, out var r));
        Assert.AreEqual(0f, r.x); Assert.AreEqual(0f, r.z);
    }

    [Test]
    public void TryRectOf_ClampsSizeLikeTheTerrain()
    {
        var site = new SiteDef { terrainSize = new[] { 0.2f, 9000f } };
        Assert.IsTrue(TerrainStack.TryRectOf(site, out var r));
        Assert.AreEqual(TerrainStack.MIN_SIZE_M, r.w);
        Assert.AreEqual(TerrainStack.MAX_SIZE_M, r.l);
    }

    [Test]
    public void TryRectOf_NoUsableSize_IsFalse()
    {
        Assert.IsFalse(TerrainStack.TryRectOf(null, out _));
        Assert.IsFalse(TerrainStack.TryRectOf(new SiteDef(), out _));
        Assert.IsFalse(TerrainStack.TryRectOf(new SiteDef { terrainSize = new[] { 0f, 10f } }, out _));
        Assert.IsFalse(TerrainStack.TryRectOf(new SiteDef { terrainSize = new[] { float.NaN, 10f } }, out _));
    }

    [Test]
    public void Contains_EdgesAreInside()
    {
        var r = Rect(10f, 20f, 30f, 40f);
        Assert.IsTrue(r.Contains(10f, 20f));
        Assert.IsTrue(r.Contains(40f, 60f));
        Assert.IsFalse(r.Contains(40.01f, 30f));
        Assert.IsFalse(r.Contains(20f, 19.99f));
    }

    // ---- BiasY ----

    [Test]
    public void BiasY_ActiveIsZero_BackdropsStepDown()
    {
        Assert.AreEqual(0f, TerrainStack.BiasY(true, 3));
        Assert.AreEqual(-0.02f, TerrainStack.BiasY(false, 0), 1e-6f);
        Assert.AreEqual(-0.04f, TerrainStack.BiasY(false, 1), 1e-6f);
    }

    [Test]
    public void BiasY_IsCapped()
    {
        Assert.AreEqual(-TerrainStack.MAX_BIAS_M, TerrainStack.BiasY(false, 50), 1e-6f);
        Assert.AreEqual(-0.02f, TerrainStack.BiasY(false, -4), 1e-6f);   // bad rank reads as the first backdrop
    }

    // ---- TryPickGround ----

    [Test]
    public void PickGround_ActiveWinsInsideItsRect_EvenWhenLower()
    {
        var rects = new List<TerrainStack.GroundRect?> { Rect(0, 0, 100, 100), Rect(0, 0, 100, 100) };
        float[] ys = { 5f, 0f };
        Assert.IsTrue(TerrainStack.TryPickGround(rects, 1, 50f, 50f, i => ys[i], out int idx, out float y));
        Assert.AreEqual(1, idx);
        Assert.AreEqual(0f, y);
    }

    [Test]
    public void PickGround_OutsideActive_HighestBackdropHoldingThePointWins()
    {
        var rects = new List<TerrainStack.GroundRect?>
        {
            Rect(0, 0, 10, 10),        // active, elsewhere
            Rect(100, 0, 50, 50),
            Rect(100, 0, 50, 50),
            Rect(500, 500, 10, 10),    // higher, but does not hold the point
        };
        float[] ys = { 0f, 1f, 3f, 9f };
        Assert.IsTrue(TerrainStack.TryPickGround(rects, 0, 120f, 20f, i => ys[i], out int idx, out float y));
        Assert.AreEqual(2, idx);
        Assert.AreEqual(3f, y);
    }

    [Test]
    public void PickGround_NoRectHoldsThePoint_FallsBackToActive()
    {
        var rects = new List<TerrainStack.GroundRect?> { Rect(0, 0, 10, 10), Rect(100, 0, 10, 10) };
        float[] ys = { 2f, 7f };
        Assert.IsTrue(TerrainStack.TryPickGround(rects, 0, -50f, -50f, i => ys[i], out int idx, out float y));
        Assert.AreEqual(0, idx);
        Assert.AreEqual(2f, y);
    }

    [Test]
    public void PickGround_NoActive_UsesBackdropOrNothing()
    {
        var rects = new List<TerrainStack.GroundRect?> { null, Rect(0, 0, 10, 10) };
        float[] ys = { 4f, 6f };
        Assert.IsTrue(TerrainStack.TryPickGround(rects, -1, 5f, 5f, i => ys[i], out int idx, out float y));
        Assert.AreEqual(1, idx);
        Assert.AreEqual(6f, y);

        Assert.IsFalse(TerrainStack.TryPickGround(rects, -1, 50f, 50f, i => ys[i], out idx, out y));
        Assert.AreEqual(-1, idx);
        Assert.AreEqual(0f, y);
    }

    [Test]
    public void PickGround_EmptyOrNull_IsFalse()
    {
        Assert.IsFalse(TerrainStack.TryPickGround(null, 0, 0f, 0f, i => 0f, out _, out _));
        Assert.IsFalse(TerrainStack.TryPickGround(new List<TerrainStack.GroundRect?>(), 0, 0f, 0f, i => 0f, out _, out _));
    }
}
