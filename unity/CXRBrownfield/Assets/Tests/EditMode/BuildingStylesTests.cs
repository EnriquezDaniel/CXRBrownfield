using NUnit.Framework;
using System.Collections.Generic;

// BuildingStyles: the letter rules (A to F) and which tile faces a style paints.
[TestFixture]
public class BuildingStylesTests
{
    private static readonly List<string> SquareFaces = new List<string> { "north", "east", "south", "west", "top", "bottom" };
    private static readonly List<string> CurveFaces  = new List<string> { "north", "east", "south", "west", "top", "bottom", "curve" };

    [Test]
    public void Normalize_AcceptsAnyCaseAndWhitespace()
    {
        Assert.AreEqual("A", BuildingStyles.Normalize("a"));
        Assert.AreEqual("B", BuildingStyles.Normalize(" B "));
        Assert.AreEqual("F", BuildingStyles.Normalize("f\n"));
    }

    [Test]
    public void Normalize_RejectsAnythingOutsideAToF()
    {
        Assert.IsNull(BuildingStyles.Normalize(null));
        Assert.IsNull(BuildingStyles.Normalize(""));
        Assert.IsNull(BuildingStyles.Normalize("   "));
        Assert.IsNull(BuildingStyles.Normalize("G"));
        Assert.IsNull(BuildingStyles.Normalize("AB"));
        Assert.IsNull(BuildingStyles.Normalize("1"));
    }

    [Test]
    public void IndexOf_MatchesIdsOrder()
    {
        Assert.AreEqual(0, BuildingStyles.IndexOf("A"));
        Assert.AreEqual(5, BuildingStyles.IndexOf("F"));
        Assert.AreEqual(-1, BuildingStyles.IndexOf(null));
        Assert.AreEqual(-1, BuildingStyles.IndexOf("Z"));
        Assert.AreEqual(6, BuildingStyles.Ids.Length);
    }

    [Test]
    public void UnpaintedWallFaces_UnpaintedSquare_GivesTheFourWalls()
    {
        var faces = BuildingStyles.UnpaintedWallFaces(SquareFaces, new TileDef { shapeId = "square" });
        CollectionAssert.AreEqual(new[] { "north", "east", "south", "west" }, faces);
    }

    [Test]
    public void UnpaintedWallFaces_SkipsHandPaintedWalls()
    {
        var tile = new TileDef
        {
            shapeId = "square",
            faceMaterials = new Dictionary<string, string> { { "north", "RedBricks" }, { "top", "RoofTiles" } },
        };
        var faces = BuildingStyles.UnpaintedWallFaces(SquareFaces, tile);
        CollectionAssert.AreEqual(new[] { "east", "south", "west" }, faces);
    }

    [Test]
    public void UnpaintedWallFaces_EmptyPaintEntryDoesNotCount()
    {
        // A null/empty material id means "default" (TileSpawner.ApplyFaceMaterial ignores it), so
        // the style still owns that face.
        var tile = new TileDef { faceMaterials = new Dictionary<string, string> { { "east", "" } } };
        var faces = BuildingStyles.UnpaintedWallFaces(SquareFaces, tile);
        CollectionAssert.Contains(faces, "east");
    }

    [Test]
    public void UnpaintedWallFaces_NeverYieldsTopOrBottom()
    {
        var faces = BuildingStyles.UnpaintedWallFaces(CurveFaces, new TileDef());
        CollectionAssert.DoesNotContain(faces, "top");
        CollectionAssert.DoesNotContain(faces, "bottom");
        CollectionAssert.Contains(faces, "curve", "the quarter-curve's outer face is a wall");
    }

    [Test]
    public void UnpaintedWallFaces_NullInputsAreSafe()
    {
        Assert.AreEqual(0, BuildingStyles.UnpaintedWallFaces(null, new TileDef()).Count);
        Assert.AreEqual(4, BuildingStyles.UnpaintedWallFaces(SquareFaces, null).Count);
    }
}
