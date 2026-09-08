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
                heightStrokes = new List<HeightStrokeDef>
                {
                    new HeightStrokeDef { id = "hr", brush = "raise",   radius = 2f, targetHeight = 3f,
                                          points = new[] { new[] { 5f, 5f, 2f } } },
                    new HeightStrokeDef { id = "hf", brush = "flatten", radius = 3f, targetHeight = 4f,
                                          points = new[] { new[] { 20f, 20f, 0.5f } } },
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

    // ---- Height strokes travel with the site ----
    // The footprint (radius, x/z) scales like a surface stroke; raise amounts and the flatten target
    // are heights, so only a uniform (calibration) scale touches them. Smooth/flatten weights never
    // change: they are blend factors, not meters.

    [Test]
    public void ScaleXZ_MovesHeightStrokeFootprint_LeavesAmountsAndTarget()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, new Vector2(10f, 10f)));

        var raise = env.site.heightStrokes[0];
        Assert.AreEqual(2f * Mathf.Sqrt(6f), raise.radius, 1e-4f, "radius is isotropic like a surface stroke");
        Assert.AreEqual(0f,  raise.points[0][0], 1e-4f);   // 10 + (5-10)*2
        Assert.AreEqual(-5f, raise.points[0][1], 1e-4f);   // 10 + (5-10)*3
        Assert.AreEqual(2f,  raise.points[0][2], 1e-4f, "an XZ resize leaves heights alone");
        Assert.AreEqual(3f,  raise.targetHeight, 1e-4f);
    }

    [Test]
    public void ScaleUniform_ScalesRaiseAmountsAndFlattenTarget_NotWeights()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.ScaleEnvironment(env, 2f, Vector2.zero));

        var raise = env.site.heightStrokes[0];
        Assert.AreEqual(4f, raise.points[0][2], 1e-4f, "raise amount is meters, so it scales");
        Assert.AreEqual(6f, raise.targetHeight, 1e-4f);
        Assert.AreEqual(10f, raise.points[0][0], 1e-4f);

        var flatten = env.site.heightStrokes[1];
        Assert.AreEqual(0.5f, flatten.points[0][2], 1e-4f, "a flatten weight is dimensionless");
        Assert.AreEqual(8f,   flatten.targetHeight, 1e-4f);
        Assert.AreEqual(6f,   flatten.radius, 1e-4f);
    }

    [Test]
    public void Translate_MovesHeightStrokePoints_LeavesAmount()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        var raise = env.site.heightStrokes[0];
        Assert.AreEqual(10f, raise.points[0][0], 1e-4f);
        Assert.AreEqual(-2f, raise.points[0][1], 1e-4f);
        Assert.AreEqual(2f,  raise.points[0][2], 1e-4f);
        Assert.AreEqual(2f,  raise.radius, 1e-4f);
    }

    [Test]
    public void NullHeightStrokes_AreLeftAlone()
    {
        var env = Env();
        env.site.heightStrokes = null;
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, Vector2.zero));
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        Assert.IsNull(env.site.heightStrokes, "records without the field must round-trip unchanged");
    }

    // ---- Water bodies ----

    private static WaterBodyDef River() => new()
    {
        id = "r", kind = "river", width = 4f, depth = 2f, bankWidth = 3f, surfaceY = 0.5f,
        points = new[] { new[] { 10f, 10f }, new[] { 30f, 10f } },
    };

    [Test]
    public void ScaleXZ_MovesWaterFootprint_ScalesWidthAndBankIso_LeavesHeights()
    {
        var env = Env();
        env.site.waterBodies = new List<WaterBodyDef> { River() };
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 4f, 1f, Vector2.zero));
        var w = env.site.waterBodies[0];
        Assert.AreEqual(40f, w.points[0][0], 1e-4f);
        Assert.AreEqual(10f, w.points[0][1], 1e-4f);
        Assert.AreEqual(4f * 2f, w.width, 1e-4f, "width scales by sqrt(fx * fz)");
        Assert.AreEqual(3f * 2f, w.bankWidth, 1e-4f);
        Assert.AreEqual(2f, w.depth, 1e-4f, "XZ resize leaves depth");
        Assert.AreEqual(0.5f, w.surfaceY, 1e-4f, "XZ resize leaves the surface height");
    }

    [Test]
    public void ScaleUniform_ScalesWaterDepthAndOffset()
    {
        var env = Env();
        env.site.waterBodies = new List<WaterBodyDef> { River() };
        Assert.IsTrue(EnvironmentScale.ScaleEnvironment(env, 2f, Vector2.zero));
        var w = env.site.waterBodies[0];
        Assert.AreEqual(4f, w.depth, 1e-4f);
        Assert.AreEqual(1f, w.surfaceY, 1e-4f);
        Assert.AreEqual(8f, w.width, 1e-4f);
    }

    [Test]
    public void Translate_MovesWaterPoints_LeavesSizes()
    {
        var env = Env();
        env.site.waterBodies = new List<WaterBodyDef> { River() };
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        var w = env.site.waterBodies[0];
        Assert.AreEqual(15f, w.points[0][0], 1e-4f);
        Assert.AreEqual(3f,  w.points[0][1], 1e-4f);
        Assert.AreEqual(4f, w.width, 1e-4f);
        Assert.AreEqual(2f, w.depth, 1e-4f);
    }

    [Test]
    public void ContentBounds_IncludesWater_RiverPaddedByHalfWidth()
    {
        var env = new EnvironmentDef { site = new SiteDef { waterBodies = new List<WaterBodyDef> { River() } } };
        Assert.IsTrue(EnvironmentScale.ContentBounds(env, null, out float minX, out float minZ, out float maxX, out float maxZ));
        Assert.AreEqual(8f,  minX, 1e-4f);
        Assert.AreEqual(8f,  minZ, 1e-4f);
        Assert.AreEqual(32f, maxX, 1e-4f);
        Assert.AreEqual(12f, maxZ, 1e-4f);

        var pond = new EnvironmentDef { site = new SiteDef { waterBodies = new List<WaterBodyDef> {
            new WaterBodyDef { kind = "pond", width = 99f, points = new[] { new[] { 0f, 0f }, new[] { 6f, 0f }, new[] { 6f, 6f } } } } } };
        Assert.IsTrue(EnvironmentScale.ContentBounds(pond, null, out minX, out minZ, out maxX, out maxZ));
        Assert.AreEqual(0f, minX, 1e-4f);
        Assert.AreEqual(6f, maxX, 1e-4f, "a pond's ring is its edge; width is ignored");
    }

    [Test]
    public void NullWaterBodies_AreLeftAlone()
    {
        var env = Env();
        env.site.waterBodies = null;
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, Vector2.zero));
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        Assert.IsNull(env.site.waterBodies);
    }
}
