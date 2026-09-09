using NUnit.Framework;
using System;
using System.Collections.Generic;
using UnityEngine;

// Spec for the generated buildings' upper-floor window pairs (BuildingWindows.Populate): which faces
// get one, what the hosted decor record carries, and that the render paths can reseat it.
[TestFixture]
public class BuildingWindowsTests
{
    private const float CS = 4f;

    private static readonly string[] Walls = { "north", "east", "south", "west" };

    private static BuildingDef Grid(int wide, int deep, int floors)
    {
        var tiles = new List<TileDef>();
        for (int f = 0; f < floors; f++)
            for (int x = 0; x < wide; x++)
                for (int z = 0; z < deep; z++)
                    tiles.Add(new TileDef { gridX = x, gridZ = z, floor = f, shapeId = "square" });
        return new BuildingDef { gridCellSize = CS, floors = floors, tiles = tiles,
                                 embeddedObjects = new List<EmbeddedObjectDef>() };
    }

    private static Vector3 Pos(EmbeddedObjectDef e) => new Vector3(e.localPos[0], e.localPos[1], e.localPos[2]);

    private static int Count(BuildingDef b, Func<EmbeddedObjectDef, bool> pred)
    {
        int n = 0;
        foreach (var e in b.embeddedObjects) if (pred(e)) n++;
        return n;
    }

    [Test]
    public void Populate_TwoByTwoThreeFloors_OnePairPerExposedUpperWallFace()
    {
        var b = Grid(2, 2, 3);
        int added = BuildingWindows.Populate(b, BuildingWindows.DefaultRule);

        // 8 perimeter faces per floor, floors 1 and 2 only.
        Assert.AreEqual(16, added);
        Assert.AreEqual(16, b.embeddedObjects.Count);
        Assert.AreEqual(0, Count(b, e => e.hostFloor == 0), "the ground floor stays bare");
        Assert.AreEqual(8, Count(b, e => e.hostFloor == 1));
        Assert.AreEqual(8, Count(b, e => e.hostFloor == 2));
    }

    [Test]
    public void Populate_InteriorTileGetsNothing()
    {
        var b = Grid(3, 3, 2);
        BuildingWindows.Populate(b, BuildingWindows.DefaultRule);

        Assert.AreEqual(12, b.embeddedObjects.Count, "12 perimeter faces on the one upper floor");
        Assert.AreEqual(0, Count(b, e => e.hostGridX == 1 && e.hostGridZ == 1), "the centre tile has no exposed wall");
        // A corner tile shows two faces, an edge tile one.
        Assert.AreEqual(2, Count(b, e => e.hostGridX == 0 && e.hostGridZ == 0));
        Assert.AreEqual(1, Count(b, e => e.hostGridX == 1 && e.hostGridZ == 0));
    }

    [Test]
    public void Populate_RecordMatchesTheDecorateTool()
    {
        var b = Grid(1, 1, 2);
        var rule = BuildingWindows.DefaultRule;
        rule.widthFrac = 0f; rule.heightFrac = 0f;   // a palette entry saved before the fields existed
        BuildingWindows.Populate(b, rule);

        Assert.AreEqual(4, b.embeddedObjects.Count);
        foreach (var e in b.embeddedObjects)
        {
            Assert.AreEqual(BuildingWindows.DefaultPrefabKey, e.prefabType);
            Assert.IsTrue(e.fillsFace);
            Assert.IsFalse(e.exclusive);
            Assert.IsTrue(e.optional, "generated windows are skipped by the low-detail VR viewer");
            Assert.Contains(e.hostFace, Walls);
            Assert.AreEqual(1, e.hostFloor);
            Assert.AreEqual(BuildingWindows.DefaultFraction, e.decorWidthFrac,  1e-6f, "0 falls back like the Decorate tool");
            Assert.AreEqual(BuildingWindows.DefaultFraction, e.decorHeightFrac, 1e-6f);
            Assert.AreEqual((int)DecorAlignment.Anchor.Bottom, e.decorAnchor);
            Assert.IsTrue(DecorPlacement.IsReseatable(e), "render paths reseat it with the real prefab bounds");
            Assert.IsFalse(string.IsNullOrEmpty(e.instanceId));
            Assert.IsNotNull(e.localPos);
            Assert.AreEqual(3, e.localPos.Length);
            Assert.Greater(e.scale, 0f);
        }
    }

    [Test]
    public void Populate_ProvisionalSeatSitsOnTheHostFace()
    {
        var b = Grid(1, 1, 2);
        BuildingWindows.Populate(b, BuildingWindows.DefaultRule);
        var north = b.embeddedObjects.Find(e => e.hostFace == "north");

        // Face centre of cell (0,0) on floor 1, plus a small push out along +Z.
        Vector3 faceCenter = new Vector3(0.5f * CS, 1.5f * CS, CS);
        Vector3 p = Pos(north);
        Assert.Greater(p.z, faceCenter.z, "seated outside the wall");
        Assert.Less(p.z - faceCenter.z, 1f, "close to the wall");
        Assert.AreEqual(faceCenter.x, p.x, 1e-4f);
        Assert.Less(p.y, faceCenter.y, "Bottom anchor sits below the face centre");
        Assert.Greater(p.y, CS, "and above the floor line");
    }

    [Test]
    public void Populate_IsIdempotentAndKeepsHandPlacedPairs()
    {
        var b = Grid(2, 1, 2);
        b.embeddedObjects.Add(new EmbeddedObjectDef
        {
            instanceId = "hand", prefabType = "windowpair",   // case differs, same kind
            hostGridX = 0, hostGridZ = 0, hostFloor = 1, hostFace = "north", fillsFace = true,
            localPos = new[] { 1f, 2f, 3f },
        });

        int first = BuildingWindows.Populate(b, BuildingWindows.DefaultRule);
        Assert.AreEqual(5, first, "6 exposed faces, one already carries a pair");
        Assert.AreEqual(1, Count(b, e => e.hostGridX == 0 && e.hostFloor == 1 && e.hostFace == "north"));
        Assert.AreEqual("hand", b.embeddedObjects[0].instanceId, "the hand-placed record is untouched");

        int second = BuildingWindows.Populate(b, BuildingWindows.DefaultRule);
        Assert.AreEqual(0, second);
        Assert.AreEqual(6, b.embeddedObjects.Count);
    }

    [Test]
    public void Populate_OtherDecorOnTheFaceStacks()
    {
        var b = Grid(1, 1, 2);
        b.embeddedObjects.Add(new EmbeddedObjectDef
        {
            instanceId = "fe", prefabType = "FireEscape",
            hostGridX = 0, hostGridZ = 0, hostFloor = 1, hostFace = "east", fillsFace = true,
            localPos = new[] { 0f, 0f, 0f },
        });
        BuildingWindows.Populate(b, BuildingWindows.DefaultRule);
        Assert.AreEqual(5, b.embeddedObjects.Count, "a different decor does not block the window");
    }

    [Test]
    public void Populate_OneFloorOrEmptyKey_AddsNothing()
    {
        var one = Grid(3, 3, 1);
        Assert.AreEqual(0, BuildingWindows.Populate(one, BuildingWindows.DefaultRule));
        Assert.AreEqual(0, one.embeddedObjects.Count);

        var off = BuildingWindows.DefaultRule;
        off.prefabKey = "";
        var tall = Grid(2, 2, 3);
        Assert.AreEqual(0, BuildingWindows.Populate(tall, off));
        Assert.AreEqual(0, tall.embeddedObjects.Count);

        Assert.AreEqual(0, BuildingWindows.Populate(null, BuildingWindows.DefaultRule));
        Assert.AreEqual(0, BuildingWindows.Populate(new BuildingDef(), BuildingWindows.DefaultRule));
    }

    [Test]
    public void Populate_NullEmbeddedListIsCreated()
    {
        var b = Grid(1, 1, 2);
        b.embeddedObjects = null;
        Assert.AreEqual(4, BuildingWindows.Populate(b, BuildingWindows.DefaultRule));
        Assert.AreEqual(4, b.embeddedObjects.Count);
    }

    [Test]
    public void Populate_RotatedTileStillGetsItsFourWalls()
    {
        var b = Grid(1, 1, 2);
        foreach (var t in b.tiles) t.rotation = 90;
        BuildingWindows.Populate(b, BuildingWindows.DefaultRule);

        Assert.AreEqual(4, b.embeddedObjects.Count);
        var faces = new HashSet<string>();
        foreach (var e in b.embeddedObjects) faces.Add(e.hostFace);
        Assert.AreEqual(4, faces.Count, "one record per named face");
    }

    [Test]
    public void Populate_ReseatWithARealBasisLandsOnTheFace()
    {
        var b = Grid(1, 1, 2);
        BuildingWindows.Populate(b, BuildingWindows.DefaultRule);
        var east = b.embeddedObjects.Find(e => e.hostFace == "east");

        // A window-ish prop: 1.0 wide, 1.5 tall, 0.1 thick, pivot at its centre.
        var basis = DecorAlignment.AnalyzeProp(Vector3.zero, new Vector3(0.5f, 0.75f, 0.05f),
                                               DecorAlignment.MountAxis.Auto, false);
        Assert.IsTrue(DecorPlacement.TryReseat(b.tiles.Find(t => t.floor == 1), east, CS, basis));

        Vector3 p = Pos(east);
        Assert.Greater(p.x, CS, "outside the east wall of cell (0,0)");
        Assert.Less(p.x - CS, 0.2f, "flush against it");
        Assert.AreEqual(0.5f * CS, p.z, 1e-4f);
        Assert.Greater(east.scale, 0f);
    }
}
