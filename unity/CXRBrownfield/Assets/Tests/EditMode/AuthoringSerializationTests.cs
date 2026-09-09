using NUnit.Framework;
using Newtonsoft.Json;

// Serialization contracts for the Authoring data types -- the stroke-point rounding converter
// (RoundedPointArrayConverter on SurfaceStrokeDef.points at 2 decimals and HeightStrokeDef.points
// at 3) and the height stroke's defaults for records saved before a field existed.
[TestFixture]
public class AuthoringSerializationTests
{
    // ---- HeightStrokeDef ----

    [Test]
    public void HeightStrokePoints_RoundToThreeDecimals_OnSerialize()
    {
        var s = new HeightStrokeDef
        {
            id = "h1", brush = "raise", radius = 4f,
            points = new[] { new[] { 1.23456f, 7.89123f, 0.123456f } }
        };
        string json = JsonConvert.SerializeObject(s);
        StringAssert.Contains("1.235", json);
        StringAssert.Contains("0.123", json);
        StringAssert.DoesNotContain("1.23456", json);
        StringAssert.DoesNotContain("0.123456", json);

        var back = JsonConvert.DeserializeObject<HeightStrokeDef>(json);
        Assert.AreEqual(1.235f, back.points[0][0], 1e-5f);
        Assert.AreEqual(7.891f, back.points[0][1], 1e-5f);
        Assert.AreEqual(0.123f, back.points[0][2], 1e-5f);
    }

    [Test]
    public void HeightStrokePoints_SerializeIsIdempotent_AfterFirstRounding()
    {
        var s = new HeightStrokeDef
        {
            id = "h2", brush = "smooth", radius = 2.5f,
            points = new[] { new[] { 12.345678f, -3.987654f, 0.0166667f }, new[] { 0.101f, 250.4949f, 0.25f } }
        };
        string once  = JsonConvert.SerializeObject(s);
        string twice = JsonConvert.SerializeObject(JsonConvert.DeserializeObject<HeightStrokeDef>(once));
        Assert.AreEqual(once, twice, "re-serializing a rounded record must be byte-identical");
    }

    [Test]
    public void HeightStroke_MissingKeys_LoadWithDefaults()
    {
        var back = JsonConvert.DeserializeObject<HeightStrokeDef>("{\"id\":\"h\",\"radius\":2,\"points\":[[1,2,0.5]]}");
        Assert.AreEqual("raise", back.brush);
        Assert.IsTrue(back.clipToLot);
        Assert.AreEqual(0f, back.targetHeight, 1e-6f);
        Assert.AreEqual(0.5f, back.points[0][2], 1e-6f);
    }

    [Test]
    public void SiteDef_OldGradePointKeys_AreIgnored_AndHeightStrokesLoadNull()
    {
        // Every record saved before the height brush carries these two keys; they must not throw,
        // and a site without heightStrokes must load as flat (null).
        var site = JsonConvert.DeserializeObject<SiteDef>(
            "{\"terrainSize\":[10,10],\"gradePoints\":null,\"maxGradeHeight\":30.0,\"surfaceStrokes\":null}");
        Assert.IsNull(site.heightStrokes);
        Assert.AreEqual(10f, site.terrainSize[0], 1e-6f);
    }

    // ---- SurfaceStrokeDef ----

    [Test]
    public void StrokePoints_RoundToTwoDecimals_OnSerialize()
    {
        var s = new SurfaceStrokeDef
        {
            id = "s1", terrainType = "grass", radius = 2f,
            points = new[] { new[] { 1.23456f, 7.89123f } }
        };
        string json = JsonConvert.SerializeObject(s);
        StringAssert.Contains("1.23", json);
        StringAssert.DoesNotContain("1.23456", json);
        StringAssert.DoesNotContain("7.89123", json);

        var back = JsonConvert.DeserializeObject<SurfaceStrokeDef>(json);
        Assert.AreEqual(1.23f, back.points[0][0], 1e-5f);
        Assert.AreEqual(7.89f, back.points[0][1], 1e-5f);
    }

    [Test]
    public void StrokePoints_SerializeIsIdempotent_AfterFirstRounding()
    {
        var s = new SurfaceStrokeDef
        {
            id = "s2", terrainType = "concrete", radius = 1.5f,
            points = new[]
            {
                new[] { 12.345678f, -3.987654f },
                new[] { 0.101f, 250.4949f },
            }
        };
        string once  = JsonConvert.SerializeObject(s);
        string twice = JsonConvert.SerializeObject(JsonConvert.DeserializeObject<SurfaceStrokeDef>(once));
        Assert.AreEqual(once, twice, "re-serializing a rounded record must be byte-identical");
    }

    [Test]
    public void StrokePoints_Null_RoundTrips()
    {
        var s = new SurfaceStrokeDef { id = "s3", terrainType = "grass", radius = 1f, points = null };
        var back = JsonConvert.DeserializeObject<SurfaceStrokeDef>(JsonConvert.SerializeObject(s));
        Assert.IsNull(back.points);
    }

    [Test]
    public void PathAndFencePoints_KeepFullPrecision()
    {
        // The converter is scoped to SurfaceStrokeDef.points only.
        var p = new PathDef { id = "p1", material = "brick", width = 2f,
                              points = new[] { new[] { 1.234567f, 0f } } };
        StringAssert.Contains("1.234567", JsonConvert.SerializeObject(p));

        var f = new FenceDef { id = "f1", fenceType = "picket",
                               points = new[] { new[] { 8.7654321f, 0f } } };
        StringAssert.Contains("8.765432", JsonConvert.SerializeObject(f));
    }

    // ---- WaterBodyDef ----

    [Test]
    public void WaterBody_RoundTrips_WithPointsRoundedToTwoDecimals()
    {
        var w = new WaterBodyDef
        {
            id = "w1", kind = "river", material = "murky", width = 5.5f, smoothing = 0.3f,
            surfaceY = -0.4f, depth = 2.25f, bankWidth = 3f, clipToLot = false,
            points = new[] { new[] { 1.23456f, 7.89123f }, new[] { 10f, 20f } },
        };
        string json = JsonConvert.SerializeObject(w);
        StringAssert.Contains("1.23", json);
        StringAssert.DoesNotContain("1.23456", json);

        var back = JsonConvert.DeserializeObject<WaterBodyDef>(json);
        Assert.AreEqual("w1", back.id);
        Assert.AreEqual("river", back.kind);
        Assert.AreEqual("murky", back.material);
        Assert.AreEqual(5.5f, back.width, 1e-6f);
        Assert.AreEqual(0.3f, back.smoothing, 1e-6f);
        Assert.AreEqual(-0.4f, back.surfaceY, 1e-6f);
        Assert.AreEqual(2.25f, back.depth, 1e-6f);
        Assert.AreEqual(3f, back.bankWidth, 1e-6f);
        Assert.IsFalse(back.clipToLot);
        Assert.AreEqual(1.23f, back.points[0][0], 1e-5f);
        Assert.AreEqual(7.89f, back.points[0][1], 1e-5f);
        Assert.AreEqual(json, JsonConvert.SerializeObject(back), "idempotent after the first rounding");
    }

    [Test]
    public void WaterBody_MissingKeys_LoadWithDefaults()
    {
        var back = JsonConvert.DeserializeObject<WaterBodyDef>("{\"id\":\"w\",\"points\":[[1,2],[3,4],[5,6]]}");
        Assert.AreEqual("pond", back.kind);
        Assert.AreEqual("lake", back.material);
        Assert.AreEqual(6f, back.width, 1e-6f);
        Assert.AreEqual(1.5f, back.depth, 1e-6f);
        Assert.AreEqual(2f, back.bankWidth, 1e-6f);
        Assert.AreEqual(0f, back.surfaceY, 1e-6f);
        Assert.IsTrue(back.clipToLot);
    }

    [Test]
    public void SiteDef_WithoutWaterBodies_LoadsNull_AndRoundTripsAList()
    {
        var site = JsonConvert.DeserializeObject<SiteDef>("{\"terrainSize\":[10,10]}");
        Assert.IsNull(site.waterBodies);

        site.waterBodies = new System.Collections.Generic.List<WaterBodyDef>
        {
            new WaterBodyDef { id = "a", points = new[] { new[] { 0f, 0f }, new[] { 1f, 0f }, new[] { 1f, 1f } } },
        };
        var back = JsonConvert.DeserializeObject<SiteDef>(JsonConvert.SerializeObject(site));
        Assert.AreEqual(1, back.waterBodies.Count);
        Assert.AreEqual("a", back.waterBodies[0].id);
        Assert.AreEqual(3, back.waterBodies[0].points.Length);
    }

    // ---- BuildingInstance.sign* ----

    [Test]
    public void BuildingInstance_MissingSignKeys_LoadAsNeverEdited()
    {
        // Every record saved before signs moved to the instance: no word, no compass (so the def's
        // legacy sign applies), not pinned.
        var inst = JsonConvert.DeserializeObject<BuildingInstance>(
            "{\"instanceId\":\"i1\",\"buildingId\":\"b1\",\"position\":[0,0,0],\"rotationY\":180,\"scale\":1,\"included\":true}");
        Assert.IsNull(inst.signText);
        Assert.IsNull(inst.signCompass);
        Assert.IsFalse(inst.signPinned);
        Assert.AreEqual(0, inst.signHostFloor);
    }

    [Test]
    public void BuildingInstance_SignFields_RoundTrip()
    {
        var inst = new BuildingInstance
        {
            instanceId = "i1", buildingId = "b1", position = new[] { 1f, 0f, 2f }, rotationY = 90f, scale = 1f,
            signText = "CAFE", signCompass = "north", signPinned = true, signHostX = 3, signHostZ = 0, signHostFloor = 1,
        };
        var back = JsonConvert.DeserializeObject<BuildingInstance>(JsonConvert.SerializeObject(inst));
        Assert.AreEqual("CAFE", back.signText);
        Assert.AreEqual("north", back.signCompass);
        Assert.IsTrue(back.signPinned);
        Assert.AreEqual(3, back.signHostX);
        Assert.AreEqual(0, back.signHostZ);
        Assert.AreEqual(1, back.signHostFloor);
    }

    // ---- EnvironmentDef.generation (provenance) ----

    [Test]
    public void EnvironmentDef_WithoutGeneration_LoadsNull()
    {
        var env = JsonConvert.DeserializeObject<EnvironmentDef>("{\"id\":\"e1\",\"name\":\"Plan\",\"version\":1}");
        Assert.IsNull(env.generation, "records saved before the field existed load with no provenance");
        StringAssert.Contains("\"generation\":null", JsonConvert.SerializeObject(env));
    }

    [Test]
    public void EnvironmentDef_Generation_RoundTrips()
    {
        var env = new EnvironmentDef
        {
            id = "e1", name = "Plan", version = 1,
            generation = new GenerationDef
            {
                sketch = "plan.png", notes = "Rite Aid style C",
                briefJson = "{\"buildings\":[{\"ref\":\"b1\",\"name\":\"Rite Aid\"}]}",
                briefReportJson = "{\"satisfied\":[\"b1\"],\"missing\":[]}",
                model = "layout-model", briefModel = "brief-model", created = "2026-09-09T12:00:00Z",
            },
        };
        var back = JsonConvert.DeserializeObject<EnvironmentDef>(JsonConvert.SerializeObject(env));
        Assert.AreEqual("plan.png", back.generation.sketch);
        Assert.AreEqual("Rite Aid style C", back.generation.notes);
        StringAssert.Contains("Rite Aid", back.generation.briefJson);
        StringAssert.Contains("satisfied", back.generation.briefReportJson);
        Assert.AreEqual("layout-model", back.generation.model);
        Assert.AreEqual("brief-model", back.generation.briefModel);
        Assert.AreEqual("2026-09-09T12:00:00Z", back.generation.created);
    }
}
