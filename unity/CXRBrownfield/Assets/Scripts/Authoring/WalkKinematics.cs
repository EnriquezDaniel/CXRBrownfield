using UnityEngine;

// Math for the first-person walkthrough (WalkthroughController): mouse look integration, WASD to a
// planar velocity, and the vertical step. Pure/static so the EditMode tests can pin down the
// conventions (yaw wraps, pitch clamps, diagonals are not faster, gravity sticks you to slopes)
// without a scene or a CharacterController.
//
// Units: metres and seconds (1 Unity unit = 1 m, see AuthoringConventions). Angles in degrees,
// Unity's camera convention: yaw about +Y, pitch positive = looking down.
public static class WalkKinematics
{
    public const float EyeHeight     = 1.7f;   // camera above the feet
    public const float CapsuleHeight = 1.8f;   // CharacterController height
    public const float CapsuleRadius = 0.3f;
    public const float PitchLimitDeg = 85f;    // never quite straight up/down, so yaw stays meaningful

    // Mouse delta in pixels (Input System: +x right, +y up) → new look angles. Moving the mouse up
    // looks up, i.e. pitch goes down. Yaw wraps to [0, 360); pitch clamps to ±PitchLimitDeg.
    public static void IntegrateLook(ref float yawDeg, ref float pitchDeg, Vector2 deltaPx, float sensDegPerPx)
    {
        yawDeg   = Mathf.Repeat(yawDeg + deltaPx.x * sensDegPerPx, 360f);
        pitchDeg = Mathf.Clamp(pitchDeg - deltaPx.y * sensDegPerPx, -PitchLimitDeg, PitchLimitDeg);
    }

    // WASD axes (-1..1; forward = W - S, right = D - A) rotated by the look yaw into a world-space
    // velocity on the ground plane. The input vector is clamped to unit length so W+D moves at the
    // same speed as W. `run` swaps the walk speed for the run speed.
    public static Vector3 PlanarVelocity(float forwardAxis, float rightAxis, float yawDeg, bool run, float walkSpeed, float runSpeed)
    {
        var local = new Vector3(rightAxis, 0f, forwardAxis);
        if (local.sqrMagnitude > 1f) local.Normalize();
        if (local.sqrMagnitude < 1e-8f) return Vector3.zero;
        float speed = run ? runSpeed : walkSpeed;
        return Quaternion.Euler(0f, yawDeg, 0f) * local * speed;
    }

    // One frame of vertical velocity. Airborne: gravity accumulates. Grounded and not rising: hold a
    // small downward "stick" velocity instead of zero, so CharacterController.isGrounded stays true
    // going down slopes and steps rather than flickering (which would make the walker hop).
    public static float StepVertical(float vy, bool grounded, float gravity, float stickVelocity, float dt)
    {
        if (grounded && vy <= 0f) return -Mathf.Abs(stickVelocity);
        return vy + gravity * dt;
    }

    public static float EyeY(float feetY) => feetY + EyeHeight;

    // True when the feet have dropped more than `tolerance` below the terrain sample at their XZ,
    // i.e. the walker slipped through a gap in the colliders and should be put back on the ground.
    public static bool FellThrough(float feetY, float terrainY, float tolerance = 0.5f) =>
        feetY < terrainY - tolerance;

    // Rescue gate for the fall-through safety net. A grounded walker is standing on a real
    // collider and must never be teleported, even where that surface sits below the terrain
    // heightmap sample (a floor below grade, or a sample/collider mismatch) — teleporting there
    // starts an up-down loop: lift to the sample height, fall back, trip the net again. Only an
    // airborne walker that has dropped past the tolerance genuinely fell through a gap.
    public static bool ShouldRescue(bool grounded, float feetY, float terrainY, float tolerance = 0.5f) =>
        !grounded && FellThrough(feetY, terrainY, tolerance);
}
