using NUnit.Framework;
using UnityEngine;

// Spec for TileFit: how sub-cell shapes (pillar, slab) size, anchor and cover their cell, and how a
// full cube stays exactly the legacy tile. Sizes below assume the default 4 m cell.
[TestFixture]
public class TileFitTests
{
    private const float CS = 4f;
    private static readonly Quaternion NoRot = Quaternion.identity;
    private static readonly Quaternion TipX  = Quaternion.Euler(90f, 0f, 0f);

    private static readonly TileFit Pillar = new TileFit(new Vector3(0.5f, 1f, 0.5f), Vector3.zero);
    private static readonly TileFit Slab   = new TileFit(new Vector3(1f, 0.5f, 1f), new Vector3(0f, -1f, 0f));

    private static readonly Vector3[] Sides =
        { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

    private static void AssertVec(Vector3 expect, Vector3 actual, string what) =>
        Assert.Less((expect - actual).magnitude, 1e-4f, $"{what}: expected {expect}, got {actual}");

    // -----------------------------------------------------------------------
    // Full cube (legacy behaviour)
    // -----------------------------------------------------------------------

    [Test]
    public void Full_IsCellSizedCenteredAndCoversEverySide()
    {
        AssertVec(Vector3.one * CS, TileFit.Full.LocalSize(CS), "size");
        AssertVec(Vector3.zero, TileFit.Full.CenterOffset(NoRot, CS), "offset");
        AssertVec(Vector3.zero, TileFit.Full.CenterOffset(Quaternion.Euler(30f, 45f, 10f), CS), "offset at any rotation");
        Assert.IsTrue(TileFit.Full.IsFull);
        foreach (var d in Sides)
        {
            Assert.IsTrue(TileFit.Full.CoversSide(NoRot, d, CS), $"covers {d}");
            Assert.IsTrue(TileFit.Full.CoversSide(Quaternion.Euler(0f, 45f, 0f), d, CS), $"covers {d} when yawed");
        }
    }

    [Test]
    public void ZeroExtents_ReadAsFullCube()
    {
        // Palette entries authored before cellExtents existed serialize it as zero.
        var legacy = new TileFit(Vector3.zero, Vector3.zero);
        Assert.IsTrue(legacy.IsFull);
        AssertVec(Vector3.one * CS, legacy.LocalSize(CS), "size");
    }

    // -----------------------------------------------------------------------
    // Pillar: 2×4×2, centered
    // -----------------------------------------------------------------------

    [Test]
    public void Pillar_IsCenteredPostAndCoversNoSide()
    {
        AssertVec(new Vector3(2f, 4f, 2f), Pillar.LocalSize(CS), "size");
        AssertVec(Vector3.zero, Pillar.CenterOffset(NoRot, CS), "offset");
        Assert.IsFalse(Pillar.IsFull);
        foreach (var d in Sides) Assert.IsFalse(Pillar.CoversSide(NoRot, d, CS), $"pillar must not cover {d}");
    }

    [Test]
    public void Pillar_TippedOnX_BecomesCenteredBeam()
    {
        AssertVec(new Vector3(2f, 2f, 4f), Pillar.RotatedAabbSize(TipX, CS), "beam aabb");
        AssertVec(Vector3.zero, Pillar.CenterOffset(TipX, CS), "beam stays centered");
    }

    // -----------------------------------------------------------------------
    // Slab: 4×2×4, resting on the floor
    // -----------------------------------------------------------------------

    [Test]
    public void Slab_RestsOnFloor_CoversOnlyBottom()
    {
        AssertVec(new Vector3(4f, 2f, 4f), Slab.LocalSize(CS), "size");
        // Center sits 1 m below the cell center → bottom face on the floor plane (y = -2 from center).
        AssertVec(new Vector3(0f, -1f, 0f), Slab.CenterOffset(NoRot, CS), "offset");
        foreach (var d in Sides)
            Assert.AreEqual(d == Vector3.down, Slab.CoversSide(NoRot, d, CS), $"slab covers {d}?");
    }

    [Test]
    public void Slab_TippedOnX_FillsHeightAndStaysAnchored()
    {
        // 4×2×4 turned about X becomes 4×4×2: full height, so the floor anchor has no slack left.
        AssertVec(new Vector3(4f, 4f, 2f), Slab.RotatedAabbSize(TipX, CS), "tipped aabb");
        AssertVec(Vector3.zero, Slab.CenterOffset(TipX, CS), "tipped slab fills the height");
        // It reaches the floor, but as a 2 m thick wall it covers only half the floor area, so no side
        // of the cell counts as filled (a neighbour's face against it stays exposed).
        foreach (var d in Sides) Assert.IsFalse(Slab.CoversSide(TipX, d, CS), $"tipped slab must not cover {d}");
    }

    [Test]
    public void PrefabLocalSize_UndoesDefaultRotation()
    {
        // A shape authored lying down (defaultRotation 90° on X) needs its Y/Z targets swapped.
        var rot = Quaternion.Euler(90f, 0f, 0f);
        AssertVec(new Vector3(4f, 4f, 2f), Slab.PrefabLocalSize(rot, CS), "prefab-local target");
        AssertVec(new Vector3(4f, 2f, 4f), Slab.PrefabLocalSize(NoRot, CS), "identity target");
    }

    // -----------------------------------------------------------------------
    // Shared-face coverage (whole-face Paint / Decorate exposure rule)
    // -----------------------------------------------------------------------

    [Test]
    public void FaceCovered_SquareAgainstSquare_True()
    {
        Assert.IsTrue(TileFit.FaceCovered(TileFit.Full, NoRot, TileFit.Full, NoRot, Vector3.forward, CS));
        Assert.IsTrue(TileFit.FaceCovered(TileFit.Full, NoRot, TileFit.Full, NoRot, Vector3.up, CS));
    }

    [Test]
    public void FaceCovered_PillarBesideSquare_FalseBothWays()
    {
        Assert.IsFalse(TileFit.FaceCovered(Pillar, NoRot, TileFit.Full, NoRot, Vector3.forward, CS), "pillar's face stays paintable");
        Assert.IsFalse(TileFit.FaceCovered(TileFit.Full, NoRot, Pillar, NoRot, Vector3.back, CS), "square's face toward the pillar stays paintable");
    }

    [Test]
    public void FaceCovered_SlabUnderSquare_TopOpen_BottomClosed()
    {
        Assert.IsFalse(TileFit.FaceCovered(Slab, NoRot, TileFit.Full, NoRot, Vector3.up, CS), "2 m gap above the slab");
        Assert.IsTrue(TileFit.FaceCovered(Slab, NoRot, TileFit.Full, NoRot, Vector3.down, CS), "slab sits flush on the square below");
        Assert.IsFalse(TileFit.FaceCovered(TileFit.Full, NoRot, Slab, NoRot, Vector3.down, CS), "square above a slab has an open bottom");
    }
}
