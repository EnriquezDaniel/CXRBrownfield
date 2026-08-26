using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

[TestFixture]
public class EnvironmentScaleTests
{
    private static EnvironmentDef Env()
    {
        return new EnvironmentDef
        {
            site = new SiteDef
            {
                terrainSize   = new[] { 100f, 50f },
                terrainOrigin = new[] { 4f, 6f },
                terrainZones = new List<TerrainZoneDef>
                {
                    new TerrainZoneDef { terrainType = "grass", rectMeters = new[] { 10f, 10f, 30f, 20f } },
                },
                paths = new List<PathDef>
                {
                    new PathDef { id = "p", width = 4f, points = new[] { new[] { 0f, 0f }, new[] { 10f, 10f } } },
                },
                surfaceStrokes = new List<SurfaceStrokeDef>
                {
                    new SurfaceStrokeDef { radius = 2f, points = new[] { new[] { 5f, 5f } } },
                },
                fences = new List<FenceDef>
                {
                    new FenceDef { id = "f", fenceType = "chain_link", height = 3f,
                                   points = new[] { new[] { 0f, 0f }, new[] { 10f, 10f } } },
                    // height 0 = "use the FencePalette default"; a sentinel, not a measurement.
                    new FenceDef { id = "fd", fenceType = "picket", height = 0f,
                                   points = new[] { new[] { 20f, 20f } } },
                },
                lotBoundary = new[] { new[] { 0f, 0f }, new[] { 100f, 0f }, new[] { 100f, 50f }, new[] { 0f, 50f } },
            },
            buildingInstances = new List<BuildingInstance>
            {
                new BuildingInstance { instanceId = "b", position = new[] { 20f, 0f, 10f }, scale = 1f },
            },
            objectInstances = new List<ObjectInstance>
            {
                new ObjectInstance { instanceId = "o", position = new[] { 40f, 2f, 30f }, scale = 1f },
            },
        };
    }

    // ---- ScaleEnvironmentXZ ----

    [Test]
    public void ScaleXZ_ScalesPositionsAboutPivot()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, new Vector2(10f, 10f)));
        // Building (20,10): x' = 10 + (20-10)*2 = 30; z' = 10 + (10-10)*3 = 10.
        Assert.AreEqual(30f, env.buildingInstances[0].position[0], 1e-4f);
        Assert.AreEqual(10f, env.buildingInstances[0].position[2], 1e-4f);
        // Object y (height) untouched.
        Assert.AreEqual(2f, env.objectInstances[0].position[1], 1e-4f);
        // terrainSize scales directly (not about the pivot).
        Assert.AreEqual(200f, env.site.terrainSize[0], 1e-4f);
        Assert.AreEqual(150f, env.site.terrainSize[1], 1e-4f);
    }

    [Test]
    public void ScaleXZ_IsoQuantitiesUseGeometricMean()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 4f, 1f, Vector2.zero));
        float iso = Mathf.Sqrt(4f * 1f);   // 2
        Assert.AreEqual(4f * iso, env.site.paths[0].width, 1e-4f);
        Assert.AreEqual(2f * iso, env.site.surfaceStrokes[0].radius, 1e-4f);
        Assert.AreEqual(iso, env.buildingInstances[0].scale, 1e-4f);
    }

    [Test]
    public void ScaleXZ_RejectsOutOfRangeFactors()
    {
        var env = Env();
        Assert.IsFalse(EnvironmentScale.ScaleEnvironmentXZ(env, 0f, 1f, Vector2.zero));
        Assert.IsFalse(EnvironmentScale.ScaleEnvironmentXZ(env, 1f, 1000f, Vector2.zero));
        Assert.IsFalse(EnvironmentScale.ScaleEnvironmentXZ(env, float.NaN, 1f, Vector2.zero));
        // Unchanged by the failed calls.
        Assert.AreEqual(20f, env.buildingInstances[0].position[0], 1e-4f);
    }

    // ---- TranslateEnvironmentXZ ----

    [Test]
    public void Translate_OffsetsEveryPositionLeavesSizesAlone()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 200f, 300f));

        Assert.AreEqual(220f, env.buildingInstances[0].position[0], 1e-4f);
        Assert.AreEqual(310f, env.buildingInstances[0].position[2], 1e-4f);
        Assert.AreEqual(240f, env.objectInstances[0].position[0], 1e-4f);
        Assert.AreEqual(330f, env.objectInstances[0].position[2], 1e-4f);
        Assert.AreEqual(2f,   env.objectInstances[0].position[1], 1e-4f);   // height untouched

        var z = env.site.terrainZones[0].rectMeters;
        Assert.AreEqual(210f, z[0], 1e-4f); Assert.AreEqual(310f, z[1], 1e-4f);
        Assert.AreEqual(230f, z[2], 1e-4f); Assert.AreEqual(320f, z[3], 1e-4f);

        Assert.AreEqual(200f, env.site.paths[0].points[0][0], 1e-4f);
        Assert.AreEqual(300f, env.site.paths[0].points[0][1], 1e-4f);
        Assert.AreEqual(205f, env.site.surfaceStrokes[0].points[0][0], 1e-4f);
        Assert.AreEqual(200f, env.site.lotBoundary[0][0], 1e-4f);
        Assert.AreEqual(300f, env.site.lotBoundary[0][1], 1e-4f);

        // Sizes, widths, radii, scales untouched.
        Assert.AreEqual(100f, env.site.terrainSize[0], 1e-4f);
        Assert.AreEqual(4f,   env.site.paths[0].width, 1e-4f);
        Assert.AreEqual(2f,   env.site.surfaceStrokes[0].radius, 1e-4f);
        Assert.AreEqual(1f,   env.buildingInstances[0].scale, 1e-4f);
    }

    [Test]
    public void Translate_HandlesNullsAndNoOp()
    {
        Assert.IsFalse(EnvironmentScale.TranslateEnvironmentXZ(null, 1f, 1f));
        Assert.IsFalse(EnvironmentScale.TranslateEnvironmentXZ(Env(), float.NaN, 0f));
        var env = Env();
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 0f, 0f));
        Assert.AreEqual(20f, env.buildingInstances[0].position[0], 1e-4f);
    }

    // ---- PointInPolygon ----

    [Test]
    public void PointInPolygon_InsideOutsideAndDegenerate()
    {
        var square = new[] { new[] { 0f, 0f }, new[] { 10f, 0f }, new[] { 10f, 10f }, new[] { 0f, 10f } };
        Assert.IsTrue(EnvironmentScale.PointInPolygon(5f, 5f, square));
        Assert.IsFalse(EnvironmentScale.PointInPolygon(15f, 5f, square));
        Assert.IsFalse(EnvironmentScale.PointInPolygon(-1f, -1f, square));
        // Documented behavior: degenerate polygon counts everything as inside.
        Assert.IsTrue(EnvironmentScale.PointInPolygon(999f, 999f, null));
        Assert.IsTrue(EnvironmentScale.PointInPolygon(999f, 999f, new[] { new[] { 0f, 0f } }));
    }

    // ---- Fences travel with the rest of the site ----
    // Regression: fences were the one SiteDef polyline the scale/translate pass skipped, so a site
    // fill, a "scale content to fit" lot resize, or a scale calibration left every fence behind at
    // its old coordinates while the ground and buildings under it moved.

    [Test]
    public void ScaleXZ_MovesFencePointsAndHeight()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, new Vector2(10f, 10f)));

        var fence = env.site.fences[0];
        // (0,0): x' = 10 + (0-10)*2 = -10; z' = 10 + (0-10)*3 = -20.
        Assert.AreEqual(-10f, fence.points[0][0], 1e-4f);
        Assert.AreEqual(-20f, fence.points[0][1], 1e-4f);
        Assert.AreEqual(10f, fence.points[1][0], 1e-4f);
        Assert.AreEqual(10f, fence.points[1][1], 1e-4f);
        // Height is isotropic, like path width: geometric mean of the two axis factors.
        Assert.AreEqual(3f * Mathf.Sqrt(6f), fence.height, 1e-4f);
        // The 0 sentinel must survive so the FencePalette default still applies.
        Assert.AreEqual(0f, env.site.fences[1].height, 1e-4f);
    }

    [Test]
    public void Translate_MovesFencePointsAndLeavesHeight()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));

        var fence = env.site.fences[0];
        Assert.AreEqual(5f,  fence.points[0][0], 1e-4f);
        Assert.AreEqual(-7f, fence.points[0][1], 1e-4f);
        Assert.AreEqual(15f, fence.points[1][0], 1e-4f);
        Assert.AreEqual(3f,  fence.points[1][1], 1e-4f);
        Assert.AreEqual(3f,  fence.height, 1e-4f, "a translation changes no extent");
    }

    // ---- terrainOrigin travels with the environment ----
    // It is a position, not an extent: it scales ABOUT the pivot (terrainSize scales directly) and
    // it shifts on a translate (terrainSize does not). Getting that backwards puts the ground in the
    // wrong place for any environment projected into a site.

    [Test]
    public void ScaleXZ_TerrainOriginScalesAboutPivot()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, new Vector2(10f, 10f)));
        // (4,6): x' = 10 + (4-10)*2 = -2;  z' = 10 + (6-10)*3 = -2.
        Assert.AreEqual(-2f, env.site.terrainOrigin[0], 1e-4f);
        Assert.AreEqual(-2f, env.site.terrainOrigin[1], 1e-4f);
    }

    [Test]
    public void Translate_MovesTerrainOriginButNotSize()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        Assert.AreEqual(9f,  env.site.terrainOrigin[0], 1e-4f);
        Assert.AreEqual(-1f, env.site.terrainOrigin[1], 1e-4f);
        Assert.AreEqual(100f, env.site.terrainSize[0], 1e-4f, "a translation changes no extent");
        Assert.AreEqual(50f,  env.site.terrainSize[1], 1e-4f);
    }

    [Test]
    public void NullTerrainOrigin_IsLeftAlone()
    {
        var env = Env();
        env.site.terrainOrigin = null;
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, Vector2.zero));
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        Assert.IsNull(env.site.terrainOrigin, "records without the field must round-trip unchanged");
    }
}
