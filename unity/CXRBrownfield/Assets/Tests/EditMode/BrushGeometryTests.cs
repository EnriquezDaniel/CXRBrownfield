using NUnit.Framework;
using UnityEngine;

// BrushGeometry lives in the CXRAuthoring assembly (referenced by this test asmdef). These are the
// angle rules the ground-surface brush shares between its live preview and the splatmap rasterizer —
// if the two ever disagree, a stroke would jump orientation on reload.
[TestFixture]
public class BrushGeometryTests
{
    // ---- NormalizeSquareAngleDeg: a square is 90°-symmetric ----

    [Test]
    public void NormalizeSquareAngle_FoldsFullTurnsIntoFirstQuadrant()
    {
        Assert.AreEqual(30f, BrushGeometry.NormalizeSquareAngleDeg(30f),  0.001f);
        Assert.AreEqual(30f, BrushGeometry.NormalizeSquareAngleDeg(120f), 0.001f);
        Assert.AreEqual(30f, BrushGeometry.NormalizeSquareAngleDeg(210f), 0.001f);
        Assert.AreEqual(30f, BrushGeometry.NormalizeSquareAngleDeg(300f), 0.001f);
    }

    [Test]
    public void NormalizeSquareAngle_HandlesNegativesAndBoundaries()
    {
        Assert.AreEqual(60f, BrushGeometry.NormalizeSquareAngleDeg(-30f), 0.001f);
        Assert.AreEqual(0f,  BrushGeometry.NormalizeSquareAngleDeg(90f),  0.001f);
        Assert.AreEqual(0f,  BrushGeometry.NormalizeSquareAngleDeg(-90f), 0.001f);
        Assert.AreEqual(0f,  BrushGeometry.NormalizeSquareAngleDeg(0f),   0.001f);
    }

    [Test]
    public void NormalizeSquareAngle_NonFiniteFallsBackToZero()
    {
        Assert.AreEqual(0f, BrushGeometry.NormalizeSquareAngleDeg(float.NaN), 0.001f);
        Assert.AreEqual(0f, BrushGeometry.NormalizeSquareAngleDeg(float.PositiveInfinity), 0.001f);
    }

    // ---- ResolveStampAngleRad: fixed angle wins, negative means auto ----

    [Test]
    public void ResolveStampAngle_NegativeAngle_FollowsSegmentHeading()
    {
        float heading = 45f * Mathf.Deg2Rad;
        Assert.AreEqual(heading, BrushGeometry.ResolveStampAngleRad(-1f, heading), 1e-5f);
    }

    [Test]
    public void ResolveStampAngle_FixedAngle_IgnoresSegmentHeading()
    {
        float heading = 45f * Mathf.Deg2Rad;
        Assert.AreEqual(30f * Mathf.Deg2Rad, BrushGeometry.ResolveStampAngleRad(30f, heading), 1e-5f);
    }

    [Test]
    public void ResolveStampAngle_ZeroIsFixedNotAuto()
    {
        // 0° is a legitimate fixed angle (axis-aligned); only a negative means "auto".
        float heading = 45f * Mathf.Deg2Rad;
        Assert.AreEqual(0f, BrushGeometry.ResolveStampAngleRad(0f, heading), 1e-5f);
    }

    // ---- SnapHeadingRad: the run's heading lands on the brush's grid ----

    [Test]
    public void SnapHeading_ZeroPhase_SnapsToNearestMultiple()
    {
        float snapped = BrushGeometry.SnapHeadingRad(43.7f * Mathf.Deg2Rad, 0f, 45f);
        Assert.AreEqual(45f, snapped * Mathf.Rad2Deg, 0.01f);

        snapped = BrushGeometry.SnapHeadingRad(20f * Mathf.Deg2Rad, 0f, 45f);
        Assert.AreEqual(0f, snapped * Mathf.Rad2Deg, 0.01f);
    }

    [Test]
    public void SnapHeading_PhaseShiftsTheWholeGrid()
    {
        // This is the rotation-aligned case: brush fixed at 30°, snapping every 90° => the run can
        // only land on 30/120/210/300, so the run and the square stamps share one rotated grid.
        float phase = 30f * Mathf.Deg2Rad;
        Assert.AreEqual(120f, BrushGeometry.SnapHeadingRad(100f * Mathf.Deg2Rad, phase, 90f) * Mathf.Rad2Deg, 0.01f);
        Assert.AreEqual(30f,  BrushGeometry.SnapHeadingRad(50f  * Mathf.Deg2Rad, phase, 90f) * Mathf.Rad2Deg, 0.01f);
        // 90° itself is NOT on the grid when the phase is 30.
        Assert.AreEqual(120f, BrushGeometry.SnapHeadingRad(90f * Mathf.Deg2Rad, phase, 90f) * Mathf.Rad2Deg, 0.01f);
    }

    [Test]
    public void SnapHeading_NonPositiveIncrement_IsNoOp()
    {
        float raw = 43.7f * Mathf.Deg2Rad;
        Assert.AreEqual(raw, BrushGeometry.SnapHeadingRad(raw, 0f, 0f),  1e-6f);
        Assert.AreEqual(raw, BrushGeometry.SnapHeadingRad(raw, 0f, -5f), 1e-6f);
    }

    [Test]
    public void SnapHeading_WrapsAcrossThePiBoundary()
    {
        // atan2 returns (-pi, pi]; a heading just under -180° must still snap onto the grid, and the
        // result must point the same direction even if it comes back outside that range.
        float snapped = BrushGeometry.SnapHeadingRad(-176f * Mathf.Deg2Rad, 0f, 45f);
        Assert.AreEqual(0f, Mathf.DeltaAngle(snapped * Mathf.Rad2Deg, 180f), 0.01f);

        snapped = BrushGeometry.SnapHeadingRad(179f * Mathf.Deg2Rad, 0f, 90f);
        Assert.AreEqual(0f, Mathf.DeltaAngle(snapped * Mathf.Rad2Deg, 180f), 0.01f);
    }

    [Test]
    public void SnapHeading_AlreadyOnGrid_IsUnchanged()
    {
        float onGrid = 90f * Mathf.Deg2Rad;
        Assert.AreEqual(90f, BrushGeometry.SnapHeadingRad(onGrid, 0f, 45f) * Mathf.Rad2Deg, 0.01f);
    }
}
