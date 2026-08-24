using NUnit.Framework;
using UnityEngine;

// WalkKinematics lives in the CXRAuthoring assembly. These pin the walkthrough's conventions: mouse
// up looks up, yaw wraps, pitch clamps, W+D is no faster than W, Shift swaps in the run speed, and
// gravity keeps the walker stuck to the ground while grounded.
[TestFixture]
public class WalkKinematicsTests
{
    // ---- IntegrateLook ----

    [Test]
    public void IntegrateLook_ZeroDelta_LeavesAnglesAlone()
    {
        float yaw = 45f, pitch = 10f;
        WalkKinematics.IntegrateLook(ref yaw, ref pitch, Vector2.zero, 0.1f);
        Assert.AreEqual(45f, yaw,   0.001f);
        Assert.AreEqual(10f, pitch, 0.001f);
    }

    [Test]
    public void IntegrateLook_MouseRightTurnsRight_MouseUpLooksUp()
    {
        float yaw = 0f, pitch = 0f;
        WalkKinematics.IntegrateLook(ref yaw, ref pitch, new Vector2(10f, 10f), 0.5f);
        Assert.AreEqual(5f,  yaw,   0.001f);   // +x delta → yaw increases (clockwise from above)
        Assert.AreEqual(-5f, pitch, 0.001f);   // +y delta → pitch decreases (Unity: negative pitch looks up)
    }

    [Test]
    public void IntegrateLook_WrapsYawInto0To360()
    {
        float yaw = 359f, pitch = 0f;
        WalkKinematics.IntegrateLook(ref yaw, ref pitch, new Vector2(2f, 0f), 1f);
        Assert.AreEqual(1f, yaw, 0.001f);
        yaw = 1f;
        WalkKinematics.IntegrateLook(ref yaw, ref pitch, new Vector2(-2f, 0f), 1f);
        Assert.AreEqual(359f, yaw, 0.001f);
    }

    [Test]
    public void IntegrateLook_ClampsPitch()
    {
        float yaw = 0f, pitch = 0f;
        WalkKinematics.IntegrateLook(ref yaw, ref pitch, new Vector2(0f, 100000f), 1f);
        Assert.AreEqual(-WalkKinematics.PitchLimitDeg, pitch, 0.001f);
        WalkKinematics.IntegrateLook(ref yaw, ref pitch, new Vector2(0f, -100000f), 1f);
        Assert.AreEqual(WalkKinematics.PitchLimitDeg, pitch, 0.001f);
    }

    // ---- PlanarVelocity ----

    [Test]
    public void PlanarVelocity_NoInput_IsZero()
    {
        Assert.AreEqual(Vector3.zero, WalkKinematics.PlanarVelocity(0f, 0f, 123f, false, 1.4f, 3f));
    }

    [Test]
    public void PlanarVelocity_ForwardFollowsYaw()
    {
        AssertVec(new Vector3(0f, 0f, 1.4f),  WalkKinematics.PlanarVelocity(1f, 0f, 0f,   false, 1.4f, 3f));
        AssertVec(new Vector3(1.4f, 0f, 0f),  WalkKinematics.PlanarVelocity(1f, 0f, 90f,  false, 1.4f, 3f));
        AssertVec(new Vector3(0f, 0f, -1.4f), WalkKinematics.PlanarVelocity(1f, 0f, 180f, false, 1.4f, 3f));
        AssertVec(new Vector3(0f, 0f, -1.4f), WalkKinematics.PlanarVelocity(-1f, 0f, 0f,  false, 1.4f, 3f));   // S backs up
        AssertVec(new Vector3(1.4f, 0f, 0f),  WalkKinematics.PlanarVelocity(0f, 1f, 0f,   false, 1.4f, 3f));   // D strafes right
    }

    [Test]
    public void PlanarVelocity_DiagonalIsNotFaster()
    {
        var v = WalkKinematics.PlanarVelocity(1f, 1f, 30f, false, 1.4f, 3f);
        Assert.AreEqual(1.4f, v.magnitude, 0.001f);
        Assert.AreEqual(0f, v.y, 0.001f);
    }

    [Test]
    public void PlanarVelocity_RunUsesRunSpeed()
    {
        Assert.AreEqual(3f,   WalkKinematics.PlanarVelocity(1f, 0f, 0f, true,  1.4f, 3f).magnitude, 0.001f);
        Assert.AreEqual(1.4f, WalkKinematics.PlanarVelocity(1f, 0f, 0f, false, 1.4f, 3f).magnitude, 0.001f);
    }

    // ---- StepVertical ----

    [Test]
    public void StepVertical_Airborne_AccumulatesGravity()
    {
        Assert.AreEqual(-0.981f, WalkKinematics.StepVertical(0f, false, -9.81f, 2f, 0.1f), 0.0001f);
        Assert.AreEqual(-1.962f, WalkKinematics.StepVertical(-0.981f, false, -9.81f, 2f, 0.1f), 0.0001f);
    }

    [Test]
    public void StepVertical_GroundedAndFalling_HoldsStickVelocity()
    {
        Assert.AreEqual(-2f, WalkKinematics.StepVertical(-12f, true, -9.81f, 2f, 0.1f), 0.0001f);
        Assert.AreEqual(-2f, WalkKinematics.StepVertical(0f,   true, -9.81f, 2f, 0.1f), 0.0001f);
    }

    [Test]
    public void StepVertical_GroundedButRising_StillFallsUnderGravity()
    {
        // Leaves room for a future jump: an upward velocity is not clamped to the stick value.
        Assert.AreEqual(3f - 0.981f, WalkKinematics.StepVertical(3f, true, -9.81f, 2f, 0.1f), 0.0001f);
    }

    // ---- heights ----

    [Test]
    public void EyeY_IsFeetPlusEyeHeight()
    {
        Assert.AreEqual(11.7f, WalkKinematics.EyeY(10f), 0.001f);
        Assert.AreEqual(1.7f, WalkKinematics.EyeHeight, 0.001f);
    }

    [Test]
    public void FellThrough_OnlyPastTolerance()
    {
        Assert.IsFalse(WalkKinematics.FellThrough(10f, 10f));
        Assert.IsFalse(WalkKinematics.FellThrough(9.6f, 10f));
        Assert.IsTrue (WalkKinematics.FellThrough(9.4f, 10f));
        Assert.IsFalse(WalkKinematics.FellThrough(12f, 10f));   // standing on a roof is fine
    }

    [Test]
    public void ShouldRescue_GroundedWalker_IsNeverRescued()
    {
        // Standing on a real collider below the heightmap sample (floor below grade, or a
        // sample/collider mismatch) must not teleport — that loop was the walk-mode view bounce.
        Assert.IsFalse(WalkKinematics.ShouldRescue(true, 9.4f, 10f));
        Assert.IsFalse(WalkKinematics.ShouldRescue(true, 0f, 100f));
    }

    [Test]
    public void ShouldRescue_AirborneOnlyPastTolerance()
    {
        Assert.IsTrue (WalkKinematics.ShouldRescue(false, 9.4f, 10f));
        Assert.IsFalse(WalkKinematics.ShouldRescue(false, 9.6f, 10f));   // within tolerance: still falling normally
        Assert.IsFalse(WalkKinematics.ShouldRescue(false, 12f, 10f));    // above ground (stepped off a roof) is fine
    }

    private static void AssertVec(Vector3 expected, Vector3 actual)
    {
        Assert.AreEqual(expected.x, actual.x, 0.001f, "x");
        Assert.AreEqual(expected.y, actual.y, 0.001f, "y");
        Assert.AreEqual(expected.z, actual.z, 0.001f, "z");
    }
}
