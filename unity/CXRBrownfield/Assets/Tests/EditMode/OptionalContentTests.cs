using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;

// The "optional" flag: the render predicate, the defaults for records saved before the field
// existed, and the bulk-mark helpers the panels call.
[TestFixture]
public class OptionalContentTests
{
    private static EnvironmentDef Env()
    {
        return new EnvironmentDef
        {
            id = "env", name = "Env",
            buildingInstances = new List<BuildingInstance>
            {
                new() { instanceId = "b1", buildingId = "house", position = new[] { 0f, 0f, 0f }, scale = 1f, included = true },
                new() { instanceId = "b2", buildingId = "house", position = new[] { 8f, 0f, 0f }, scale = 1f, included = true },
            },
            objectInstances = new List<ObjectInstance>
            {
                new() { instanceId = "o1", prefabType = "tree_oak",  position = new[] { 1f, 0f, 1f }, scale = 1f, included = true },
                new() { instanceId = "o2", prefabType = "Tree_Oak",  position = new[] { 2f, 0f, 1f }, scale = 1f, included = true },
                new() { instanceId = "o3", prefabType = "bench",     position = new[] { 3f, 0f, 1f }, scale = 1f, included = true },
            },
        };
    }

    // ---- ShouldRender ----

    [Test]
    public void ShouldRender_NotSkipping_OnlyIncludedMatters()
    {
        Assert.IsTrue (OptionalContent.ShouldRender(included: true,  optional: false, skipOptional: false));
        Assert.IsTrue (OptionalContent.ShouldRender(included: true,  optional: true,  skipOptional: false));
        Assert.IsFalse(OptionalContent.ShouldRender(included: false, optional: false, skipOptional: false));
        Assert.IsFalse(OptionalContent.ShouldRender(included: false, optional: true,  skipOptional: false));
    }

    [Test]
    public void ShouldRender_Skipping_HidesOptional_AndExcludedStillWins()
    {
        Assert.IsTrue (OptionalContent.ShouldRender(included: true,  optional: false, skipOptional: true));
        Assert.IsFalse(OptionalContent.ShouldRender(included: true,  optional: true,  skipOptional: true));
        Assert.IsFalse(OptionalContent.ShouldRender(included: false, optional: false, skipOptional: true));
        Assert.IsFalse(OptionalContent.ShouldRender(included: false, optional: true,  skipOptional: true));
    }

    // ---- Serialization defaults ----

    [Test]
    public void MissingOptionalKey_LoadsAsRequired_OnAllThreeTypes()
    {
        var oi = JsonConvert.DeserializeObject<ObjectInstance>(
            "{\"instanceId\":\"a\",\"prefabType\":\"tree\",\"position\":[0,0,0],\"included\":true}");
        var bi = JsonConvert.DeserializeObject<BuildingInstance>(
            "{\"instanceId\":\"b\",\"buildingId\":\"h\",\"position\":[0,0,0],\"included\":true}");
        var emb = JsonConvert.DeserializeObject<EmbeddedObjectDef>(
            "{\"instanceId\":\"e\",\"prefabType\":\"window\",\"localPos\":[0,1,0],\"hostFace\":\"north\"}");
        Assert.IsFalse(oi.optional);
        Assert.IsFalse(bi.optional);
        Assert.IsFalse(emb.optional);
        Assert.IsTrue(oi.included, "unrelated fields still load");
    }

    [Test]
    public void OptionalTrue_RoundTrips()
    {
        var oi = new ObjectInstance { instanceId = "a", prefabType = "tree", position = new[] { 0f, 0f, 0f }, included = true, optional = true };
        string json = JsonConvert.SerializeObject(oi);
        StringAssert.Contains("\"optional\":true", json);
        Assert.IsTrue(JsonConvert.DeserializeObject<ObjectInstance>(json).optional);

        var emb = new EmbeddedObjectDef { instanceId = "e", prefabType = "w", localPos = new[] { 0f, 0f, 0f }, optional = true };
        Assert.IsTrue(JsonConvert.DeserializeObject<EmbeddedObjectDef>(JsonConvert.SerializeObject(emb)).optional);
    }

    // ---- SetOptional / AnyDiffers ----

    [Test]
    public void SetOptional_MixedTargets_ReturnsChangedCount_AndSkipsUnknownIds()
    {
        var env = Env();
        var targets = new List<(string, bool)> { ("b1", true), ("o3", false), ("missing", false) };
        Assert.AreEqual(2, OptionalContent.SetOptional(env, targets, true));
        Assert.IsTrue(env.buildingInstances[0].optional);
        Assert.IsFalse(env.buildingInstances[1].optional);
        Assert.IsTrue(env.objectInstances[2].optional);
        Assert.IsFalse(env.objectInstances[0].optional);

        // Already set: nothing changes.
        Assert.AreEqual(0, OptionalContent.SetOptional(env, targets, true));
        Assert.IsFalse(OptionalContent.AnyDiffers(env, targets, true));
        Assert.IsTrue(OptionalContent.AnyDiffers(env, targets, false));
    }

    [Test]
    public void SetOptional_NullEnvOrTargets_IsSafe()
    {
        Assert.AreEqual(0, OptionalContent.SetOptional(null, new List<(string, bool)> { ("o1", false) }, true));
        Assert.AreEqual(0, OptionalContent.SetOptional(Env(), null, true));
        Assert.IsFalse(OptionalContent.AnyDiffers(null, null, true));
    }

    // ---- SetByPrefabType ----

    [Test]
    public void SetByPrefabType_MarksOnlyMatching_CaseInsensitive()
    {
        var env = Env();
        Assert.IsTrue(OptionalContent.AnyDiffersByPrefabType(env, "TREE_OAK", true));
        Assert.AreEqual(2, OptionalContent.SetByPrefabType(env, "TREE_OAK", true));
        Assert.IsTrue(env.objectInstances[0].optional);
        Assert.IsTrue(env.objectInstances[1].optional);
        Assert.IsFalse(env.objectInstances[2].optional, "bench untouched");
        Assert.IsFalse(OptionalContent.AnyDiffersByPrefabType(env, "tree_oak", true));

        // Back to required.
        Assert.AreEqual(2, OptionalContent.SetByPrefabType(env, "tree_oak", false));
        Assert.IsFalse(env.objectInstances[0].optional);
    }

    [Test]
    public void SetByPrefabType_EmptyType_DoesNothing()
    {
        var env = Env();
        Assert.AreEqual(0, OptionalContent.SetByPrefabType(env, "", true));
        Assert.AreEqual(0, OptionalContent.SetByPrefabType(env, null, true));
        Assert.AreEqual(0, OptionalContent.SetByPrefabType(env, "nothing_like_this", true));
    }

    // ---- Face decor ----

    private static BuildingDef Building()
    {
        return new BuildingDef
        {
            id = "house", name = "House",
            embeddedObjects = new List<EmbeddedObjectDef>
            {
                new() { instanceId = "w1", prefabType = "window", localPos = new[] { 0f, 1f, 0f }, hostGridX = 0, hostGridZ = 0, hostFloor = 0, hostFace = "north" },
                new() { instanceId = "w2", prefabType = "window", localPos = new[] { 0f, 1f, 0f }, hostGridX = 0, hostGridZ = 0, hostFloor = 0, hostFace = "North" },
                new() { instanceId = "d1", prefabType = "door",   localPos = new[] { 0f, 0f, 0f }, hostGridX = 0, hostGridZ = 0, hostFloor = 0, hostFace = "east" },
                new() { instanceId = "v1", prefabType = "vent",   localPos = new[] { 0f, 2f, 0f }, hostGridX = 1, hostGridZ = 0, hostFloor = 0, hostFace = "north" },
                new() { instanceId = "legacy", prefabType = "sign", localPos = new[] { 0f, 1f, 0f }, hostFace = null },
            },
        };
    }

    [Test]
    public void DecorOnFace_MatchesHostTileAndFace_IgnoresLegacyNullHost()
    {
        var b = Building();
        var north = OptionalContent.DecorOnFace(b, 0, 0, 0, "north");
        CollectionAssert.AreEquivalent(new[] { "w1", "w2" }, north.ConvertAll(e => e.instanceId));

        Assert.AreEqual(1, OptionalContent.DecorOnFace(b, 0, 0, 0, "east").Count);
        Assert.AreEqual(0, OptionalContent.DecorOnFace(b, 5, 5, 0, "north").Count);
        Assert.AreEqual(0, OptionalContent.DecorOnFace(b, 0, 0, 0, null).Count);
        Assert.AreEqual(0, OptionalContent.DecorOnFace(null, 0, 0, 0, "north").Count);
    }

    [Test]
    public void SetFaceOptional_AnyRequired_MakesAllOptional_ThenAllOptional_MakesAllRequired()
    {
        var b = Building();
        var north = OptionalContent.DecorOnFace(b, 0, 0, 0, "north");
        north[0].optional = true;   // mixed: one already optional

        Assert.AreEqual(true, OptionalContent.SetFaceOptional(north));
        Assert.IsTrue(north[0].optional);
        Assert.IsTrue(north[1].optional);
        Assert.IsFalse(b.embeddedObjects[2].optional, "other faces untouched");

        Assert.AreEqual(false, OptionalContent.SetFaceOptional(north));
        Assert.IsFalse(north[0].optional);
        Assert.IsFalse(north[1].optional);

        Assert.IsNull(OptionalContent.SetFaceOptional(new List<EmbeddedObjectDef>()));
        Assert.IsNull(OptionalContent.SetFaceOptional(null));
    }
}
