using System.Collections.Generic;
using NUnit.Framework;

// BuildingCopies: the name a pasted building gets and the def clone behind it.
public class BuildingCopiesTests
{
    // ---- NextCopyName ----

    [Test]
    public void NextCopyName_FirstCopy_IsTwo()
    {
        Assert.AreEqual("Coffee Shop 2", BuildingCopies.NextCopyName("Coffee Shop", new[] { "Coffee Shop" }));
    }

    [Test]
    public void NextCopyName_SkipsTakenNumbers()
    {
        var taken = new[] { "Coffee Shop", "Coffee Shop 2", "Coffee Shop 3" };
        Assert.AreEqual("Coffee Shop 4", BuildingCopies.NextCopyName("Coffee Shop", taken));
    }

    [Test]
    public void NextCopyName_OfANumberedCopy_CountsOn()
    {
        var taken = new[] { "Coffee Shop", "Coffee Shop 2" };
        Assert.AreEqual("Coffee Shop 3", BuildingCopies.NextCopyName("Coffee Shop 2", taken));
    }

    [Test]
    public void NextCopyName_KeepsARealNumberInTheName()
    {
        Assert.AreEqual("Unit 13", BuildingCopies.NextCopyName("Unit 12", new[] { "Unit 12" }));
    }

    [Test]
    public void NextCopyName_IgnoresCaseAndPadding()
    {
        var taken = new[] { "coffee shop", " COFFEE SHOP 2 " };
        Assert.AreEqual("Coffee Shop 3", BuildingCopies.NextCopyName("Coffee Shop", taken));
    }

    [Test]
    public void NextCopyName_GroupPaste_NumbersEachCopy()
    {
        // Two linked sheds pasted together: the first name handed out is taken for the second.
        var taken = new List<string> { "Shed" };
        string a = BuildingCopies.NextCopyName("Shed", taken); taken.Add(a);
        string b = BuildingCopies.NextCopyName("Shed", taken);
        Assert.AreEqual("Shed 2", a);
        Assert.AreEqual("Shed 3", b);
    }

    [Test]
    public void NextCopyName_BlankSource_UsesFallback()
    {
        Assert.AreEqual("Building 2", BuildingCopies.NextCopyName("  ", null));
        Assert.AreEqual("Building 2", BuildingCopies.NextCopyName(null, new string[0]));
    }

    [Test]
    public void SplitNumber_LeavesOtherNamesWhole()
    {
        Assert.AreEqual(("42", 1), BuildingCopies.SplitNumber("42"));
        Assert.AreEqual(("Block B2", 1), BuildingCopies.SplitNumber("Block B2"));
        Assert.AreEqual(("Shop 0", 1), BuildingCopies.SplitNumber("Shop 0"));
        Assert.AreEqual(("Shop", 7), BuildingCopies.SplitNumber(" Shop 7 "));
    }

    // ---- IsNameTaken ----

    [Test]
    public void IsNameTaken_SkipsTheBuildingItself()
    {
        var all = new List<(string id, string name)> { ("a", "Coffee Shop"), ("b", "Bakery") };
        Assert.IsFalse(BuildingCopies.IsNameTaken("Coffee Shop", "a", all));
        Assert.IsTrue(BuildingCopies.IsNameTaken("coffee shop ", "b", all));
        Assert.IsFalse(BuildingCopies.IsNameTaken("Florist", "b", all));
    }

    // ---- CloneDef ----

    private static BuildingDef Source() => new BuildingDef
    {
        id = "src", name = "Coffee Shop", version = 7, gridCellSize = 4f, floors = 2, floorHeight = 3f,
        style = "B", tags = new List<string> { "static", "corner" },
        tiles = new List<TileDef>
        {
            new TileDef { gridX = 0, gridZ = 0, floor = 0, shapeId = "cube" },
            new TileDef { gridX = 1, gridZ = 0, floor = 0, shapeId = "cube" },
        },
        embeddedObjects = new List<EmbeddedObjectDef>
        {
            new EmbeddedObjectDef { instanceId = "decor-1", prefabType = "AC", localPos = new[] { 1f, 2f, 3f } },
        },
    };

    [Test]
    public void CloneDef_IsANewHiddenRecord()
    {
        var copy = BuildingCopies.CloneDef(Source(), "new-id", "Coffee Shop 2");
        Assert.AreEqual("new-id", copy.id);
        Assert.AreEqual("Coffee Shop 2", copy.name);
        Assert.AreEqual(1, copy.version);
        Assert.IsTrue(copy.hiddenCopy);
        Assert.AreEqual("B", copy.style);
        Assert.AreEqual(2, copy.tiles.Count);
        CollectionAssert.AreEqual(new[] { "corner" }, copy.tags, "the server kind tag is dropped");
    }

    [Test]
    public void CloneDef_SharesNothingWithTheSource()
    {
        var src  = Source();
        var copy = BuildingCopies.CloneDef(src, "new-id", "Coffee Shop 2");

        copy.tiles.RemoveAt(0);
        copy.tiles[0].shapeId = "slab";
        copy.embeddedObjects[0].localPos[0] = 99f;

        Assert.AreEqual(2, src.tiles.Count);
        Assert.AreEqual("cube", src.tiles[1].shapeId);
        Assert.AreEqual(1f, src.embeddedObjects[0].localPos[0]);
        Assert.AreEqual("src", src.id);
        Assert.IsFalse(src.hiddenCopy);
    }

    [Test]
    public void CloneDef_ReissuesDecorIds()
    {
        var copy = BuildingCopies.CloneDef(Source(), "new-id", "Coffee Shop 2");
        Assert.AreNotEqual("decor-1", copy.embeddedObjects[0].instanceId);
        Assert.IsFalse(string.IsNullOrEmpty(copy.embeddedObjects[0].instanceId));
        Assert.AreEqual("AC", copy.embeddedObjects[0].prefabType);
    }

    [Test]
    public void CloneDef_NullSource_IsNull()
    {
        Assert.IsNull(BuildingCopies.CloneDef(null, "x", "y"));
    }
}
