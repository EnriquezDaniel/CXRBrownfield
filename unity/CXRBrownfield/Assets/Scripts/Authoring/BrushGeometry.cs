using UnityEngine;

// Angle math for the ground-surface brush (SurfaceStrokeDef). Pure/static so it can be unit tested
// without a scene, and so the authoring tool (live preview / snapping) and the renderer (splatmap
// rasterization) resolve every angle through exactly the same code — a stroke must look the same
// while you drag it as it does after a reload.
//
// All headings are in radians using the XZ-plane convention atan2(dz, dx), matching
// WorldRenderer.WalkStroke. Authored angles are in degrees because that is what the UI shows.
public static class BrushGeometry
{
    // A square footprint is 90°-symmetric — rotating it by 90° gives back the same shape — so every
    // distinct orientation lives in [0, 90). Folding sampled angles (a building's yaw of 210°, a lot
    // edge at -60°) into that range keeps the UI slider honest and comparisons meaningful.
    public static float NormalizeSquareAngleDeg(float deg)
    {
        if (float.IsNaN(deg) || float.IsInfinity(deg)) return 0f;
        deg %= 90f;
        if (deg < 0f) deg += 90f;
        return deg;
    }

    // The angle one stamp is laid down at: the stroke's fixed angle when it has one, otherwise the
    // heading of the segment the stamp sits on (auto-align, which keeps a run's edges parallel).
    // `angleDeg` < 0 is the "auto" sentinel — the default, and what strokes saved before the fixed
    // angle existed deserialize to.
    public static float ResolveStampAngleRad(float angleDeg, float segmentHeadingRad) =>
        angleDeg >= 0f ? angleDeg * Mathf.Deg2Rad : segmentHeadingRad;

    // Snap a heading to the nearest `phaseRad + k * incrementDeg`. The phase is what makes snapping
    // rotation-aware: with the brush fixed at 30° and a 90° increment, runs land on 30/120/210/300°,
    // so the run and the square stamps share one rotated grid. `incrementDeg` <= 0 returns the
    // heading untouched (snapping off).
    public static float SnapHeadingRad(float headingRad, float phaseRad, float incrementDeg)
    {
        if (incrementDeg <= 0f) return headingRad;
        float stepRad = incrementDeg * Mathf.Deg2Rad;
        // Measure relative to the phase, round to the nearest step, then put the phase back. Mathf
        // handles the ±π wrap for free because rounding is done on the (unwrapped) offset.
        float k = Mathf.Round((headingRad - phaseRad) / stepRad);
        return phaseRad + k * stepRad;
    }
}
