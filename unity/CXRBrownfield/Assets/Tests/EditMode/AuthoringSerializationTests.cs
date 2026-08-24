using NUnit.Framework;
using Newtonsoft.Json;

// Serialization contracts for the Authoring data types -- currently the stroke-point rounding
// converter (RoundedPointArrayConverter on SurfaceStrokeDef.points).
[TestFixture]
public class AuthoringSerializationTests
{
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
}
