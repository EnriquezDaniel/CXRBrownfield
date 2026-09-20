using UnityEngine;

// Math for the headset viewer's locomotion (VRLocomotion): where the rig starts, snap turns that
// pivot about the head, and where a teleport puts the rig. Pure/static so the EditMode tests pin
// the conventions without a headset. Stick walking reuses WalkKinematics.
//
// Units: metres, degrees. North is +X and west is +Z (LayoutConverter turns the sketch half a
// turn), so east is -Z and "looking south" is facing -X.
public static class VRLocomotionMath
{
    public const float SnapTurnDeg      = 45f;
    public const float SnapFireAt       = 0.7f;   // stick deflection that fires a turn
    public const float SnapRearmBelow   = 0.3f;   // stick must come back under this before the next turn
    public const float TeleportAimAt    = 0.7f;   // right stick forward past this shows the ray
    public const float TeleportReleaseBelow = 0.3f;
    public const float MaxSurfaceTiltDeg = 45f;   // teleport targets must face up within this
    public const float SpawnInset       = 2f;     // metres in from the corner, so you start on the site
    public const float SouthYawDeg      = 270f;   // facing -X

    // Where a fresh viewer stands in an environment: the north-east corner of its first Site plot
    // (the area drawn for generation), looking south. An env with no usable plot falls back to the
    // north-east corner of its whole ground rect.
    public static void SpawnPose(EnvironmentDef env, out Vector3 feet, out float yawDeg)
    {
        if (env?.sites != null)
            foreach (var plot in env.sites)
                if (TryPlotSpawn(plot, out feet)) { yawDeg = SouthYawDeg; return; }
        SpawnPose(env?.site, out feet, out yawDeg);
    }

    // North-east corner of a plot polygon ([[x, z], ...] world metres): the boundary point nearest
    // the bounding box's max-X, min-Z corner, moved SpawnInset towards the centroid so you start
    // inside the plot. A plot need not be a rectangle, so the box corner itself may lie outside it.
    public static bool TryPlotSpawn(SitePlotDef plot, out Vector3 feet)
    {
        feet = Vector3.zero;
        var b = plot?.boundary;
        if (b == null) return false;

        float maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, cx = 0f, cz = 0f;
        int n = 0;
        foreach (var p in b)
        {
            if (p == null || p.Length < 2 || !IsFinite(p[0]) || !IsFinite(p[1])) continue;
            maxX = Mathf.Max(maxX, p[0]);
            minZ = Mathf.Min(minZ, p[1]);
            cx += p[0]; cz += p[1];
            n++;
        }
        if (n < 3) return false;
        cx /= n; cz /= n;

        float bestD = float.PositiveInfinity, vx = 0f, vz = 0f;
        foreach (var p in b)
        {
            if (p == null || p.Length < 2 || !IsFinite(p[0]) || !IsFinite(p[1])) continue;
            float dx = p[0] - maxX, dz = p[1] - minZ, d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; vx = p[0]; vz = p[1]; }
        }

        var toCentre = new Vector2(cx - vx, cz - vz);
        float reach  = toCentre.magnitude;
        if (reach > 1e-4f) toCentre *= Mathf.Min(SpawnInset, reach * 0.5f) / reach;
        feet = new Vector3(vx + toCentre.x, 0f, vz + toCentre.y);
        return true;
    }

    // Feet position (y = 0, the caller grounds it) and yaw at the north-east corner of the env's
    // ground rect, looking south: terrainOrigin (min corner, null ⇒ world origin) plus terrainSize
    // [x_m, z_m]. The inset never crosses the middle of a tiny site.
    public static void SpawnPose(SiteDef site, out Vector3 feet, out float yawDeg)
    {
        yawDeg = SouthYawDeg;
        float ox = 0f, oz = 0f, sx = 0f, sz = 0f;
        if (site != null)
        {
            var o = site.terrainOrigin;
            if (o != null && o.Length >= 2 && IsFinite(o[0]) && IsFinite(o[1])) { ox = o[0]; oz = o[1]; }
            var s = site.terrainSize;
            if (s != null && s.Length >= 2 && IsFinite(s[0]) && IsFinite(s[1]))
            {
                sx = Mathf.Max(0f, s[0]);
                sz = Mathf.Max(0f, s[1]);
            }
        }
        float insetX = Mathf.Min(SpawnInset, sx * 0.5f);
        float insetZ = Mathf.Min(SpawnInset, sz * 0.5f);
        feet = new Vector3(ox + sx - insetX, 0f, oz + insetZ);   // north = max X, east = min Z
    }

    // One frame of the snap-turn latch. Returns the turn to apply now (±SnapTurnDeg or 0). `armed`
    // drops when a turn fires and comes back once the stick returns near centre, so holding the
    // stick over turns once.
    public static float SnapTurn(float stickX, ref bool armed)
    {
        float mag = Mathf.Abs(stickX);
        if (!armed)
        {
            if (mag < SnapRearmBelow) armed = true;
            return 0f;
        }
        if (mag < SnapFireAt) return 0f;
        armed = false;
        return stickX > 0f ? SnapTurnDeg : -SnapTurnDeg;
    }

    // Rig position after yawing the rig by `deg` about the vertical axis through the head, so the
    // view turns in place instead of swinging around the playspace centre.
    public static Vector3 RotateRigAboutHead(Vector3 rigPos, Vector3 headPos, float deg)
    {
        Vector3 offset = rigPos - headPos;
        offset.y = 0f;
        Vector3 turned = Quaternion.Euler(0f, deg, 0f) * offset;
        return new Vector3(headPos.x + turned.x, rigPos.y, headPos.z + turned.z);
    }

    // True for a surface you can stand on: its normal is within MaxSurfaceTiltDeg of straight up.
    public static bool IsTeleportSurface(Vector3 normal)
    {
        if (normal.sqrMagnitude < 1e-8f) return false;
        return Vector3.Angle(normal, Vector3.up) <= MaxSurfaceTiltDeg;
    }

    // Rig position that puts the head's XZ over `target` with the rig floor at target.y. The head
    // keeps its offset inside the playspace, so you land where you pointed wherever you stand in
    // the room.
    public static Vector3 TeleportRigPosition(Vector3 target, Vector3 rigPos, Vector3 headPos)
    {
        return new Vector3(target.x - (headPos.x - rigPos.x), target.y, target.z - (headPos.z - rigPos.z));
    }

    // Rig yaw that makes the head look along `wantYawDeg`, given the head's current world yaw.
    public static float RigYawForHeadYaw(float rigYawDeg, float headYawDeg, float wantYawDeg) =>
        Mathf.Repeat(rigYawDeg + Mathf.DeltaAngle(headYawDeg, wantYawDeg), 360f);

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
