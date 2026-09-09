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
            sites = new List<SitePlotDef>
            {
                new SitePlotDef { id = "s1", name = "Site 1",
                                  boundary = new[] { new[] { 60f, 10f }, new[] { 90f, 10f }, new[] { 90f, 40f }, new[] { 60f, 40f } } },
            },
        };
    }

    // ---- Drawn site plots travel with the environment ----
    // Regression: sites were the one polygon the scale/translate pass skipped, so moving a host
    // left its site outlines (and the fills fitted into them) behind at the old coordinates.

    [Test]
    public void Translate_MovesSiteBoundaries()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        var b = env.sites[0].boundary;
        Assert.AreEqual(65f, b[0][0], 1e-4f);
        Assert.AreEqual(3f,  b[0][1], 1e-4f);
        Assert.AreEqual(95f, b[2][0], 1e-4f);
        Assert.AreEqual(33f, b[2][1], 1e-4f);
    }

    [Test]
    public void ScaleXZ_ScalesSiteBoundariesAboutPivot()
    {
        var env = Env();
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, new Vector2(10f, 10f)));
        var b = env.sites[0].boundary;
        // (60,10): x' = 10 + (60-10)*2 = 110; z' = 10 + (10-10)*3 = 10.
        Assert.AreEqual(110f, b[0][0], 1e-4f);
        Assert.AreEqual(10f,  b[0][1], 1e-4f);
        // (90,40): x' = 10 + 80*2 = 170; z' = 10 + 30*3 = 100.
        Assert.AreEqual(170f, b[2][0], 1e-4f);
        Assert.AreEqual(100f, b[2][1], 1e-4f);
    }

    [Test]
    public void NullSites_AndNullBoundary_AreLeftAlone()
    {
        var env = Env();
        env.sites = null;
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, Vector2.zero));
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        Assert.IsNull(env.sites, "records without the field must round-trip unchanged");

        env = Env();
        env.sites.Add(null);
        env.sites.Add(new SitePlotDef { id = "empty", boundary = null });
        Assert.IsTrue(EnvironmentScale.ScaleEnvironmentXZ(env, 2f, 3f, Vector2.zero));
        Assert.IsTrue(EnvironmentScale.TranslateEnvironmentXZ(env, 5f, -7f));
        Assert.IsNull(env.sites[2].boundary);
    }

    // ---- Site placement: the terrain corner ----

    [Test]
    public void TerrainCorner_ReadsOriginOrFallsBackToZero()
    {
        EnvironmentScale.TerrainCorner(Env().site, out float ox, out float oz);
        Assert.AreEqual(4f, ox, 1e-4f); Assert.AreEqual(6f, oz, 1e-4f);

        EnvironmentScale.TerrainCorner(null, out ox, out oz);
        Assert.AreEqual(0f, ox); Assert.AreEqual(0f, oz);

        var site = new SiteDef { terrainOrigin = null };
        EnvironmentScale.TerrainCorner(site, out ox, out oz);
        Assert.AreEqual(0f, ox); Assert.AreEqual(0f, oz);

        site.terrainOrigin = new[] { 7f };
        EnvironmentScale.TerrainCorner(site, out ox, out oz);
        Assert.AreEqual(0f, ox, "a short array counts as unset");

        site.terrainOrigin = new[] { float.NaN, 3f };
        EnvironmentScale.TerrainCorner(site, out ox, out oz);
        Assert.AreEqual(0f, ox); Assert.AreEqual(0f, oz);
    }

    [Test]
    public void EffectiveLotPolygon_RectangleStartsAtTerrainOrigin()
    {
        var env = Env();
        env.site.lotBoundary = null;
        var poly = EnvironmentScale.EffectiveLotPolygon(env.site);
        Assert.AreEqual(4, poly.Length);
        Assert.AreEqual(4f,   poly[0][0], 1e-4f); Assert.AreEqual(6f,  poly[0][1], 1e-4f);
        Assert.AreEqual(104f, poly[1][0], 1e-4f); Assert.AreEqual(6f,  poly[1][1], 1e-4f);
        Assert.AreEqual(104f, poly[2][0], 1e-4f); Assert.AreEqual(56f, poly[2][1], 1e-4f);
        Assert.AreEqual(4f,   poly[3][0], 1e-4f); Assert.AreEqual(56f, poly[3][1], 1e-4f);
    }

    [Test]
    public void EffectiveLotPolygon_NullOriginStartsAtZero_ExplicitBoundaryWins()
    {
        var env = Env();
        env.site.lotBoundary = null;
        env.site.terrainOrigin = null;
        var poly = EnvironmentScale.EffectiveLotPolygon(env.site);
        Assert.AreEqual(0f,   poly[0][0], 1e-4f); Assert.AreEqual(0f,  poly[0][1], 1e-4f);
        Assert.AreEqual(100f, poly[2][0], 1e-4f); Assert.AreEqual(50f, poly[2][1], 1e-4f);

        env = Env();
        Assert.AreSame(env.site.lotBoundary, EnvironmentScale.EffectiveLotPolygon(env.site));
    }

    [Test]
    public void SnapCornerDelta_LandsCornerOnWholeMeters()
    {
        EnvironmentScale.SnapCornerDelta(4.3f, 6.5f, 2.5f, -1.2f, snap: true, out float dx, out float dz);
        Assert.AreEqual(2.7f, dx, 1e-4f, "4.3 + 2.5 = 6.8 rounds to 7, so the delta is 2.7");
        Assert.AreEqual(-1.5f, dz, 1e-4f, "6.5 - 1.2 = 5.3 rounds to 5, so the delta is -1.5");
    }

    [Test]
    public void SnapCornerDelta_FreeWhenOff_ZeroWhenNonFinite()
    {
        EnvironmentScale.SnapCornerDelta(4.3f, 6.5f, 2.5f, -1.2f, snap: false, out float dx, out float dz);
        Assert.AreEqual(2.5f, dx, 1e-4f); Assert.AreEqual(-1.2f, dz, 1e-4f);
        EnvironmentScale.SnapCornerDelta(0f, 0f, float.NaN, float.PositiveInfinity, snap: true, out dx, out dz);
        Assert.AreEqual(0f, dx); Assert.AreEqual(0f, dz);
    }

    [Test]
    public void FitTerrainRect_OriginIsMinMinusMargin_SizeIsExtentPlusTwoMargins()
    {
        Assert.IsTrue(EnvironmentScale.FitTerrainRect(-10f, 20f, 30f, 50f, 2f,
                                                      out float ox, out float oz, out float w, out float l));
        Assert.AreEqual(-12f, ox, 1e-4f); Assert.AreEqual(18f, oz, 1e-4f);
        Assert.AreEqual(44f,  w,  1e-4f); Assert.AreEqual(34f, l,  1e-4f);
    }

    [Test]
    public void FitTerrainRect_RejectsDegenerate()
    {
        Assert.IsFalse(EnvironmentScale.FitTerrainRect(10f, 0f, 10f, 5f, 2f, out _, out _, out _, out _));
        Assert.IsFalse(EnvironmentScale.FitTerrainRect(0f, 5f, 10f, 5f, 2f, out _, out _, out _, out _));
        Assert.IsFalse(EnvironmentScale.FitTerrainRect(float.NaN, 0f, 10f, 5f, 2f, out _, out _, out _, out _));
        Assert.IsFalse(EnvironmentScale.FitTerrainRect(0f, 0f, float.PositiveInfinity, 5f, 2f, out _, out _, out _, out _));
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
