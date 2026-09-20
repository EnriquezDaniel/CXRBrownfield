using NUnit.Framework;
using UnityEngine;

// VRLocomotionMath lives in the CXRAuthoring assembly. These pin the headset viewer's conventions:
// you start at the site's north-east corner (max X, min Z) looking south (-X), a held stick snap
// turns once, a turn pivots about the head, and a teleport lands the head where you pointed.
[TestFixture]
public class VRLocomotionMathTests
{
    // ---- SpawnPose ----

    [Test]
    public void SpawnPose_NorthEastCorner_InsetAndFacingSouth()
    {
        var site = new SiteDef { terrainSize = new[] { 140f, 20f } };
        VRLocomotionMath.SpawnPose(site, out Vector3 feet, out float yaw);
        Assert.AreEqual(138f, feet.x, 0.001f);   // north = max X, inset 2 m
        Assert.AreEqual(2f,   feet.z, 0.001f);   // east = min Z, inset 2 m
        Assert.AreEqual(0f,   feet.y, 0.001f);
        Assert.AreEqual(270f, yaw,    0.001f);
        Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Assert.AreEqual(-1f, fwd.x, 0.001f);     // looking south is -X
        Assert.AreEqual(0f,  fwd.z, 0.001f);
    }

    [Test]
    public void SpawnPose_HonoursTerrainOrigin()
    {
        var site = new SiteDef { terrainSize = new[] { 40f, 30f }, terrainOrigin = new[] { 100f, -50f } };
        VRLocomotionMath.SpawnPose(site, out Vector3 feet, out _);
        Assert.AreEqual(138f, feet.x, 0.001f);
        Assert.AreEqual(-48f, feet.z, 0.001f);
    }

    [Test]
    public void SpawnPose_TinySite_InsetStopsAtTheMiddle()
    {
        var site = new SiteDef { terrainSize = new[] { 2f, 1f } };
        VRLocomotionMath.SpawnPose(site, out Vector3 feet, out _);
        Assert.AreEqual(1f,   feet.x, 0.001f);
        Assert.AreEqual(0.5f, feet.z, 0.001f);
    }

    [Test]
    public void SpawnPose_NullOrMalformedSite_IsTheOrigin()
    {
        VRLocomotionMath.SpawnPose((SiteDef)null, out Vector3 a, out float yaw);
        Assert.AreEqual(Vector3.zero, a);
        Assert.AreEqual(270f, yaw, 0.001f);

        var bad = new SiteDef { terrainSize = new[] { float.NaN, 10f }, terrainOrigin = new[] { 5f } };
        VRLocomotionMath.SpawnPose(bad, out Vector3 b, out _);
        Assert.AreEqual(Vector3.zero, b);
    }

    // ---- SpawnPose(EnvironmentDef) / TryPlotSpawn ----

    private static SitePlotDef Plot(params float[] xz)
    {
        var b = new float[xz.Length / 2][];
        for (int i = 0; i < b.Length; i++) b[i] = new[] { xz[i * 2], xz[i * 2 + 1] };
        return new SitePlotDef { boundary = b };
    }

    [Test]
    public void SpawnPose_Env_UsesTheFirstSitePlot_NotTheWholeGround()
    {
        var env = new EnvironmentDef
        {
            site  = new SiteDef { terrainSize = new[] { 200f, 200f } },
            sites = new System.Collections.Generic.List<SitePlotDef>
            {
                Plot(50f, 60f,  90f, 60f,  90f, 80f,  50f, 80f),    // first plot: x 50..90, z 60..80
                Plot(0f, 0f,  10f, 0f,  10f, 10f,  0f, 10f),
            },
        };
        VRLocomotionMath.SpawnPose(env, out Vector3 feet, out float yaw);
        Assert.AreEqual(270f, yaw, 0.001f);
        // Corner (90, 60) moved 2 m towards the centroid (70, 70).
        var want = new Vector2(90f, 60f) + (new Vector2(70f, 70f) - new Vector2(90f, 60f)).normalized * 2f;
        Assert.AreEqual(want.x, feet.x, 0.001f);
        Assert.AreEqual(want.y, feet.z, 0.001f);
        Assert.Less(feet.x, 90f);       // inside the plot
        Assert.Greater(feet.z, 60f);
    }

    [Test]
    public void SpawnPose_Env_NoUsablePlot_FallsBackToTheGroundRect()
    {
        var env = new EnvironmentDef
        {
            site  = new SiteDef { terrainSize = new[] { 140f, 20f } },
            sites = new System.Collections.Generic.List<SitePlotDef> { null, Plot(1f, 1f, 2f, 2f) },   // 2 points: not a polygon
        };
        VRLocomotionMath.SpawnPose(env, out Vector3 feet, out _);
        Assert.AreEqual(138f, feet.x, 0.001f);
        Assert.AreEqual(2f,   feet.z, 0.001f);

        VRLocomotionMath.SpawnPose((EnvironmentDef)null, out Vector3 none, out _);
        Assert.AreEqual(Vector3.zero, none);
    }

    [Test]
    public void TryPlotSpawn_SlantedPlot_PicksTheBoundaryPointNearestNorthEast()
    {
        // A diamond: the bounding box corner (10, -10) is outside it. Nearest vertices tie between
        // north (10, 0) and east (0, -10); the first one found wins, and the result is inside.
        var plot = Plot(10f, 0f,  0f, 10f,  -10f, 0f,  0f, -10f);
        Assert.IsTrue(VRLocomotionMath.TryPlotSpawn(plot, out Vector3 feet));
        Assert.AreEqual(8f, feet.x, 0.001f);    // (10, 0) moved 2 m towards the centroid (0, 0)
        Assert.AreEqual(0f, feet.z, 0.001f);
    }

    // ---- SnapTurn ----

    [Test]
    public void SnapTurn_FiresOncePerPush_RearmsNearCentre()
    {
        bool armed = true;
        Assert.AreEqual(0f,   VRLocomotionMath.SnapTurn(0.5f, ref armed), 0.001f);   // under the threshold
        Assert.AreEqual(45f,  VRLocomotionMath.SnapTurn(0.9f, ref armed), 0.001f);
        Assert.IsFalse(armed);
        Assert.AreEqual(0f,   VRLocomotionMath.SnapTurn(0.9f, ref armed), 0.001f);   // held: no repeat
        Assert.AreEqual(0f,   VRLocomotionMath.SnapTurn(0.5f, ref armed), 0.001f);   // not back far enough
        Assert.IsFalse(armed);
        Assert.AreEqual(0f,   VRLocomotionMath.SnapTurn(0.1f, ref armed), 0.001f);
        Assert.IsTrue(armed);
        Assert.AreEqual(-45f, VRLocomotionMath.SnapTurn(-0.8f, ref armed), 0.001f);  // left turns left
    }

    // ---- RotateRigAboutHead ----

    [Test]
    public void RotateRigAboutHead_HeadStaysPut()
    {
        var rig  = new Vector3(10f, 3f, 5f);
        var head = new Vector3(11f, 4.7f, 5f);     // standing 1 m north of the playspace centre
        Vector3 moved = VRLocomotionMath.RotateRigAboutHead(rig, head, 90f);

        // The head's offset from the rig turns with the rig, so the head's world spot must not move.
        Vector3 headLocal = head - rig;
        Vector3 headAfter = moved + Quaternion.Euler(0f, 90f, 0f) * headLocal;
        Assert.AreEqual(head.x, headAfter.x, 0.001f);
        Assert.AreEqual(head.z, headAfter.z, 0.001f);
        Assert.AreEqual(rig.y,  moved.y,     0.001f);   // height untouched
    }

    [Test]
    public void RotateRigAboutHead_HeadOverRig_DoesNotMoveTheRig()
    {
        var rig = new Vector3(2f, 0f, 2f);
        Vector3 moved = VRLocomotionMath.RotateRigAboutHead(rig, new Vector3(2f, 1.7f, 2f), 45f);
        Assert.AreEqual(rig.x, moved.x, 0.001f);
        Assert.AreEqual(rig.z, moved.z, 0.001f);
    }

    // ---- IsTeleportSurface ----

    [Test]
    public void IsTeleportSurface_FloorsAndGentleSlopesOnly()
    {
        Assert.IsTrue(VRLocomotionMath.IsTeleportSurface(Vector3.up));
        Assert.IsTrue(VRLocomotionMath.IsTeleportSurface(Quaternion.Euler(30f, 0f, 0f) * Vector3.up));
        Assert.IsFalse(VRLocomotionMath.IsTeleportSurface(Quaternion.Euler(60f, 0f, 0f) * Vector3.up));
        Assert.IsFalse(VRLocomotionMath.IsTeleportSurface(Vector3.right));   // wall
        Assert.IsFalse(VRLocomotionMath.IsTeleportSurface(Vector3.down));    // ceiling
        Assert.IsFalse(VRLocomotionMath.IsTeleportSurface(Vector3.zero));
    }

    // ---- TeleportRigPosition ----

    [Test]
    public void TeleportRigPosition_PutsTheHeadOverTheTarget_FloorAtTargetHeight()
    {
        var rig    = new Vector3(0f, 0f, 0f);
        var head   = new Vector3(0.5f, 1.6f, -0.25f);
        var target = new Vector3(20f, 3f, 8f);      // a roof
        Vector3 moved = VRLocomotionMath.TeleportRigPosition(target, rig, head);
        Vector3 headAfter = moved + (head - rig);
        Assert.AreEqual(20f, headAfter.x, 0.001f);
        Assert.AreEqual(8f,  headAfter.z, 0.001f);
        Assert.AreEqual(3f,  moved.y,     0.001f);
    }

    // ---- RigYawForHeadYaw ----

    [Test]
    public void RigYawForHeadYaw_CancelsWhereTheHeadIsTurned()
    {
        // Rig at 0, user physically looking 30 degrees right; to face south the rig turns 240.
        Assert.AreEqual(240f, VRLocomotionMath.RigYawForHeadYaw(0f, 30f, 270f), 0.001f);
        // Already looking the right way: rig yaw is unchanged.
        Assert.AreEqual(90f,  VRLocomotionMath.RigYawForHeadYaw(90f, 270f, 270f), 0.001f);
    }
}
