using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

// TileFaces: which named face of a tile points a building-local direction, and whether that face
// is exposed (nothing fills the neighbouring cell against it). Shared by the tile editor's
// whole-face tools and the sign placer.
[TestFixture]
public class TileFacesTests
{
    private const float CS = 4f;

    private static readonly TileFit Pillar = new TileFit(new Vector3(0.5f, 1f, 0.5f), Vector3.zero);

    private static TileDef Tile(int x, int z, int floor = 0, int yaw = 0, string shape = "square") =>
        new TileDef { gridX = x, gridZ = z, floor = floor, shapeId = shape, rotation = yaw };

    [Test]
    public void FacePointing_UnrotatedSquare_FollowsTheBaselineConvention()
    {
        var t = Tile(0, 0);
        Assert.AreEqual("north",  TileFaces.FacePointing(t, Vector3.forward));
        Assert.AreEqual("south",  TileFaces.FacePointing(t, Vector3.back));
        Assert.AreEqual("east",   TileFaces.FacePointing(t, Vector3.right));
        Assert.AreEqual("west",   TileFaces.FacePointing(t, Vector3.left));
        Assert.AreEqual("top",    TileFaces.FacePointing(t, Vector3.up));
        Assert.AreEqual("bottom", TileFaces.FacePointing(t, Vector3.down));
    }

    [Test]
    public void FacePointing_QuarterTurnedSquare_NamesTheTurnedFace()
    {
        // Yaw 90 (clockwise from above) carries local +Z to +X, so the "north" face now points east.
        var t = Tile(0, 0, yaw: 90);
        Assert.AreEqual("north", TileFaces.FacePointing(t, Vector3.right));
        Assert.AreEqual("west",  TileFaces.FacePointing(t, Vector3.forward));
    }

    [Test]
    public void FacePointing_NoFaceWithin25Degrees_IsNull()
    {
        var t = Tile(0, 0, yaw: 45);
        Assert.IsNull(TileFaces.FacePointing(t, Vector3.forward), "a 45 degree tile has no face on the axis");
        Assert.IsNull(TileFaces.FacePointing(null, Vector3.forward));
    }

    [Test]
    public void IsExposed_NoNeighbour_True()
    {
        var a = Tile(0, 0);
        var tiles = new List<TileDef> { a, Tile(0, 1, floor: 1) };   // the tile above is not a +Z neighbour
        Assert.IsTrue(TileFaces.IsExposed(tiles, a, Vector3.forward, CS, null));
        Assert.IsTrue(TileFaces.IsExposed(tiles, a, Vector3.right, CS, null));
        Assert.IsTrue(TileFaces.IsExposed(null, a, Vector3.forward, CS, null), "no tile list means no occlusion");
    }

    [Test]
    public void IsExposed_SquareNeighbour_False()
    {
        var a = Tile(0, 0);
        var tiles = new List<TileDef> { a, Tile(0, 1), Tile(0, 0, floor: 1) };
        Assert.IsFalse(TileFaces.IsExposed(tiles, a, Vector3.forward, CS, null), "a square fills the shared boundary");
        Assert.IsFalse(TileFaces.IsExposed(tiles, a, Vector3.up, CS, null), "the floor above covers the top");
        Assert.IsTrue(TileFaces.IsExposed(tiles, a, Vector3.back, CS, null));
    }

    [Test]
    public void IsExposed_PillarNeighbour_KeepsTheFaceAcrossTheGap()
    {
        var a = Tile(0, 0);
        var tiles = new List<TileDef> { a, Tile(0, 1, shape: "pillar") };
        TileFit FitFor(string s) => s == "pillar" ? Pillar : TileFit.Full;
        Assert.IsTrue(TileFaces.IsExposed(tiles, a, Vector3.forward, CS, FitFor), "a pillar leaves the boundary open");
        Assert.IsFalse(TileFaces.IsExposed(tiles, a, Vector3.forward, CS, null), "without fits every shape is a full cube");
    }
}
