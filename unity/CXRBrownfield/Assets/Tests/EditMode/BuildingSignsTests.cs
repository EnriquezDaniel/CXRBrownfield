using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

// BuildingSigns: the sign word rules, compass to wall and back through an instance yaw, what a
// placed building's sign is (instance first, legacy def second), every place the two-tile plate can
// sit, the nudge steps, and where the plate lands with its fallbacks.
[TestFixture]
public class BuildingSignsTests
{
    private const float CS = 4f;

    private static void AssertVec(Vector3 expect, Vector3 actual, string what) =>
        Assert.Less((expect - actual).magnitude, 1e-3f, $"{what}: expected {expect}, got {actual}");

    // A rectangular square-tile grid, the shape LayoutConverter builds. The legacy sign fields sit
    // on the def so the 4-arg TryPlace (records saved before signs moved) stays covered.
    private static BuildingDef Grid(int wide, int deep, int floors = 1, string text = "SIGN", string face = "north")
    {
        var tiles = new List<TileDef>();
        for (int f = 0; f < floors; f++)
            for (int x = 0; x < wide; x++)
                for (int z = 0; z < deep; z++)
                    tiles.Add(new TileDef { gridX = x, gridZ = z, floor = f, shapeId = "square" });
        return new BuildingDef { id = "b", name = "B", gridCellSize = CS, floors = floors, tiles = tiles,
                                 signText = text, signFace = face };
    }

    private static BuildingSigns.Spec Spec(string face, string text = "SIGN", bool pinned = false, int hostX = 0, int hostZ = 0, int hostFloor = 0) =>
        new BuildingSigns.Spec { text = text, face = face, pinned = pinned, hostX = hostX, hostZ = hostZ, hostFloor = hostFloor };

    private static void RemoveTile(BuildingDef def, int x, int z, int floor) =>
        def.tiles.RemoveAll(t => t.gridX == x && t.gridZ == z && t.floor == floor);

    // ---- text ----

    [Test]
    public void NormalizeText_UppercasesTrimsAndCollapsesSpaces()
    {
        Assert.AreEqual("ICECREAM",  BuildingSigns.NormalizeText(" icecream\n"));
        Assert.AreEqual("ICE CREAM", BuildingSigns.NormalizeText("Ice   Cream"));
        // Apostrophe, hyphen and ampersand survive; the 16 character cap cuts before "GRILL" and
        // the dangling space is trimmed.
        Assert.AreEqual("JOE'S BAR-B-Q &", BuildingSigns.NormalizeText("Joe's Bar-B-Q & Grill!!"));
    }

    [Test]
    public void NormalizeText_CapsAtMaxCharsAndDropsPunctuation()
    {
        string s = BuildingSigns.NormalizeText("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        Assert.AreEqual(BuildingSigns.MaxChars, s.Length);
        Assert.AreEqual("THEATER", BuildingSigns.NormalizeText("\"Theater\"."));
    }

    [Test]
    public void NormalizeText_BlankOrEmptyIsNull()
    {
        Assert.IsNull(BuildingSigns.NormalizeText(null));
        Assert.IsNull(BuildingSigns.NormalizeText(""));
        Assert.IsNull(BuildingSigns.NormalizeText("   "));
        Assert.IsNull(BuildingSigns.NormalizeText("!!!"));
    }

    [Test]
    public void NormalizeFace_AcceptsWallsOnly()
    {
        Assert.AreEqual("north", BuildingSigns.NormalizeFace(" North "));
        Assert.IsNull(BuildingSigns.NormalizeFace("top"));
        Assert.IsNull(BuildingSigns.NormalizeFace(null));
        Assert.AreEqual("west", BuildingSigns.NormalizeCompass("WEST"));
    }

    // ---- facing ----

    [Test]
    public void FaceTowardWorldDir_East_ByInstanceYaw()
    {
        // World east is -Z (north = +X). The local north wall is +Z, so it points east after a half
        // turn; a quarter turn clockwise (yaw 90) carries the local east wall (+X) onto -Z.
        Assert.AreEqual("south", BuildingSigns.FaceTowardWorldDir(0f,   BuildingSigns.WorldEast));
        Assert.AreEqual("east",  BuildingSigns.FaceTowardWorldDir(90f,  BuildingSigns.WorldEast));
        Assert.AreEqual("north", BuildingSigns.FaceTowardWorldDir(180f, BuildingSigns.WorldEast));
        Assert.AreEqual("west",  BuildingSigns.FaceTowardWorldDir(270f, BuildingSigns.WorldEast));
    }

    [Test]
    public void FaceTowardWorldDir_NearestWinsOffAxis()
    {
        Assert.AreEqual("north", BuildingSigns.FaceTowardWorldDir(200f, BuildingSigns.WorldEast));
        Assert.AreEqual("west",  BuildingSigns.FaceTowardWorldDir(240f, BuildingSigns.WorldEast));
    }

    [Test]
    public void CompassDir_MatchesWorldAxes()
    {
        // North is +X (top of the sketch), so east is -Z and west +Z (LayoutConverter header).
        AssertVec(Vector3.right,   BuildingSigns.CompassDir("north"), "north");
        AssertVec(Vector3.back,    BuildingSigns.CompassDir("east"),  "east");
        AssertVec(Vector3.left,    BuildingSigns.CompassDir("south"), "south");
        AssertVec(Vector3.forward, BuildingSigns.CompassDir("west"),  "west");
        AssertVec(BuildingSigns.WorldEast, BuildingSigns.CompassDir(BuildingSigns.DefaultCompass), "the default compass is world east");
        AssertVec(Vector3.zero,    BuildingSigns.CompassDir("up"),    "not a compass point");
    }

    [Test]
    public void CompassOfFace_InvertsFaceTowardWorldDir()
    {
        foreach (float yaw in new[] { 0f, 90f, 180f, 270f, 200f })
            foreach (var compass in BuildingSigns.Compass)
            {
                string face = BuildingSigns.FaceTowardWorldDir(yaw, BuildingSigns.CompassDir(compass));
                Assert.AreEqual(compass, BuildingSigns.CompassOfFace(face, yaw), $"yaw {yaw}, {compass} -> {face}");
            }
        // The generated sample: an axis-aligned block carries a 180 yaw, so its north wall faces east.
        Assert.AreEqual("east", BuildingSigns.CompassOfFace("north", 180f));
        Assert.IsNull(BuildingSigns.CompassOfFace("top", 0f));
    }

    // ---- what a building's sign is ----

    [Test]
    public void SpecFor_InstanceFieldsWin()
    {
        var def  = Grid(5, 4, text: "OLD", face: "north");
        var inst = new BuildingInstance { rotationY = 180f, signText = " cafe ", signCompass = "north",
                                          signPinned = true, signHostX = 2, signHostZ = 3, signHostFloor = 0 };
        var spec = BuildingSigns.SpecFor(inst, def);
        Assert.AreEqual("CAFE", spec.text);
        // Yaw 180: the local west wall (-X) turns to +X, world north.
        Assert.AreEqual("west", spec.face);
        Assert.IsTrue(spec.pinned);
        Assert.AreEqual(2, spec.hostX);
        Assert.AreEqual(3, spec.hostZ);
        Assert.AreEqual(0, spec.hostFloor);
    }

    [Test]
    public void SpecFor_LegacyDefFallsBackWhenNeverEdited()
    {
        var def  = Grid(5, 4, text: "OLD", face: "north");
        var inst = new BuildingInstance { rotationY = 180f };
        var spec = BuildingSigns.SpecFor(inst, def);
        Assert.AreEqual("OLD", spec.text);
        Assert.AreEqual("north", spec.face, "the legacy face is already building-local");
        Assert.IsFalse(spec.pinned);
        Assert.AreEqual("OLD", BuildingSigns.SpecFor(null, def).text, "no instance at all: the def's own sign");
    }

    [Test]
    public void SpecFor_ClearedInstanceHasNoSign()
    {
        var def  = Grid(5, 4, text: "OLD", face: "north");
        var inst = new BuildingInstance { signText = null, signCompass = "east" };
        Assert.IsNull(BuildingSigns.SpecFor(inst, def).text, "a cleared sign keeps its compass so the legacy word stays gone");
        Assert.IsNull(BuildingSigns.SpecFor(new BuildingInstance(), Grid(5, 4, text: null)).text);
    }

    [Test]
    public void EffectiveCompass_InstanceThenLegacyFaceThenDefault()
    {
        var def = Grid(5, 4, text: "OLD", face: "north");
        Assert.AreEqual("west", BuildingSigns.EffectiveCompass(new BuildingInstance { signCompass = "west" }, def));
        Assert.AreEqual("east", BuildingSigns.EffectiveCompass(new BuildingInstance { rotationY = 180f }, def), "the legacy north wall faces east after a half turn");
        Assert.AreEqual("east", BuildingSigns.EffectiveCompass(new BuildingInstance(), Grid(5, 4, text: null)));
        Assert.AreEqual("east", BuildingSigns.EffectiveCompass(null, null));
    }

    // ---- where it can go ----

    [Test]
    public void Slots_ListsEveryExposedPairOnEveryFloor()
    {
        var slots = BuildingSigns.Slots(Grid(5, 4, floors: 2), "north", CS, null);
        Assert.AreEqual(8, slots.Count, "four adjacent pairs on the five-wide north row, on each of two floors");
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(0, slots[i].floor);
            Assert.AreEqual(3, slots[i].side, "the exposed north row is z = 3");
            Assert.AreEqual(i, slots[i].along);
            Assert.AreEqual(i, slots[i].a.gridX);
            Assert.AreEqual(i + 1, slots[i].b.gridX);
            Assert.AreEqual(1, slots[i + 4].floor);
        }
        AssertVec(new Vector3(1f * CS, CS * 0.5f, 4f * CS), slots[0].center, "midpoint of the two face centres");
    }

    [Test]
    public void Slots_SkipsCoveredTilesAndUnknownFaces()
    {
        // 6 wide, one row. A tile in front of x = 2 covers it, leaving pairs (0,1), (3,4), (4,5).
        var def = Grid(6, 1);
        def.tiles.Add(new TileDef { gridX = 2, gridZ = 1, floor = 0, shapeId = "square" });
        var slots = BuildingSigns.Slots(def, "north", CS, null);
        Assert.AreEqual(3, slots.Count);
        Assert.AreEqual(0, slots[0].along);
        Assert.AreEqual(3, slots[1].along);
        Assert.AreEqual(4, slots[2].along);
        Assert.AreEqual(0, BuildingSigns.Slots(def, "top", CS, null).Count);
    }

    [Test]
    public void Nearest_PicksClosestCentre()
    {
        var slots = BuildingSigns.Slots(Grid(5, 4), "north", CS, null);
        var near  = BuildingSigns.Nearest(slots, new Vector3(4f * CS + 0.3f, 1f, 4f * CS));
        Assert.AreEqual(3, near.along, "the pair x 3,4 is centred at x = 4 cells");
        Assert.AreEqual(0, BuildingSigns.Nearest(slots, new Vector3(-10f, 0f, 0f)).along);
    }

    [Test]
    public void TryFindSlot_MatchesTileAOnTheFloor()
    {
        var slots = BuildingSigns.Slots(Grid(5, 4, floors: 2), "north", CS, null);
        Assert.IsTrue(BuildingSigns.TryFindSlot(slots, 1, 2, 3, out var s));
        Assert.AreEqual(1, s.floor);
        Assert.AreEqual(2, s.along);
        Assert.IsFalse(BuildingSigns.TryFindSlot(slots, 0, 4, 3, out _), "x = 4 has no neighbour to its right");
        Assert.IsFalse(BuildingSigns.TryFindSlot(slots, 2, 0, 3, out _), "no such floor");
    }

    // ---- nudges ----

    [Test]
    public void Step_RightMovesTowardTheViewersRight()
    {
        // Facing the north wall from outside (looking -Z), the viewer's right is -X: a Right nudge
        // lowers the along coordinate.
        var def = Grid(5, 4);
        Assert.IsTrue(BuildingSigns.TryAutoSlot(def, "north", CS, null, out var auto));
        Assert.AreEqual(1, auto.along);
        Assert.AreEqual(0, BuildingSigns.Step(def, "north", auto, +1, 0, CS, null).along);
        Assert.AreEqual(2, BuildingSigns.Step(def, "north", auto, -1, 0, CS, null).along);

        // Facing the east wall (looking -X), the viewer's right is +Z.
        var tall = Grid(3, 6);
        Assert.IsTrue(BuildingSigns.TryAutoSlot(tall, "east", CS, null, out var east));
        Assert.AreEqual(2, east.along);
        Assert.AreEqual(3, BuildingSigns.Step(tall, "east", east, +1, 0, CS, null).along);
        Assert.AreEqual(1, BuildingSigns.Step(tall, "east", east, -1, 0, CS, null).along);
    }

    [Test]
    public void Step_UpKeepsThePlaceAlongTheWall()
    {
        var def = Grid(5, 4, floors: 2);
        Assert.IsTrue(BuildingSigns.TryAutoSlot(def, "north", CS, null, out var auto));
        var up = BuildingSigns.Step(def, "north", auto, 0, +1, CS, null);
        Assert.AreEqual(1, up.floor);
        Assert.AreEqual(1, up.along);
        Assert.AreEqual(3, up.side);
        var down = BuildingSigns.Step(def, "north", up, 0, -1, CS, null);
        Assert.AreEqual(0, down.floor);
        Assert.AreEqual(1, down.along);
        var top = BuildingSigns.Step(def, "north", up, 0, +1, CS, null);
        Assert.AreEqual(1, top.floor, "no third floor: stays put");
    }

    [Test]
    public void Step_UpTakesTheNearestPairWhenThatPlaceIsGone()
    {
        var def = Grid(5, 4, floors: 2);
        RemoveTile(def, 1, 3, 1);   // floor 1 north row keeps x 0, 2, 3, 4: pairs (2,3), (3,4)
        Assert.IsTrue(BuildingSigns.TryAutoSlot(def, "north", CS, null, out var auto));
        var up = BuildingSigns.Step(def, "north", auto, 0, +1, CS, null);
        Assert.AreEqual(1, up.floor);
        Assert.AreEqual(2, up.along);
    }

    [Test]
    public void Step_StaysPutAtTheEndOfTheRun()
    {
        var def = Grid(2, 1);
        Assert.IsTrue(BuildingSigns.TryAutoSlot(def, "north", CS, null, out var only));
        Assert.IsTrue(BuildingSigns.SameSlot(only, BuildingSigns.Step(def, "north", only, +1, 0, CS, null)));
        Assert.IsTrue(BuildingSigns.SameSlot(only, BuildingSigns.Step(def, "north", only, -1, 0, CS, null)));
        Assert.IsTrue(BuildingSigns.SameSlot(only, BuildingSigns.Step(def, "north", only, 0, +1, CS, null)));
        Assert.IsTrue(BuildingSigns.SameSlot(only, BuildingSigns.Step(def, "top", only, +1, 0, CS, null)));
    }

    // ---- placement ----

    [Test]
    public void TryPlace_FiveWide_CentresTheLeftBiasedPairOnTheNorthRow()
    {
        var def = Grid(5, 4);
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, CS, null, out var p));
        Assert.AreEqual(1, p.tileA.gridX);
        Assert.AreEqual(2, p.tileB.gridX);
        Assert.AreEqual(3, p.tileA.gridZ, "the exposed north row is z = 3");
        Assert.AreEqual(0, p.floor);
        Assert.AreEqual("north", p.face);
        Assert.IsFalse(p.pinLost);
        Assert.IsFalse(p.faceFallback);

        float band = BuildingSigns.BandFrac * CS;
        AssertVec(Vector3.forward, p.normal, "normal");
        AssertVec(Vector3.up,      p.up,     "up");
        AssertVec(Vector3.right,   p.right,  "right runs from tile A to tile B");
        AssertVec(new Vector3(2f * CS, CS - BuildingSigns.TopMargin - band * 0.5f, 4f * CS), p.center, "center");
        Assert.AreEqual(2f * CS, p.width, 1e-3f);
        Assert.AreEqual(band, p.height, 1e-3f);
    }

    [Test]
    public void TryPlace_EighteenWide_PicksTilesEightAndNine()
    {
        var def = Grid(18, 4, floors: 7);
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, CS, null, out var p));
        Assert.AreEqual(8, p.tileA.gridX);
        Assert.AreEqual(9, p.tileB.gridX);
        Assert.AreEqual(0, p.floor, "ground floor even on a tall block");
    }

    [Test]
    public void TryPlace_EastWall_RunsAlongZ()
    {
        var def = Grid(3, 6, face: "east");
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, CS, null, out var p));
        Assert.AreEqual(2, p.tileA.gridX, "the east column is x = 2");
        Assert.AreEqual(2, p.tileA.gridZ);
        Assert.AreEqual(3, p.tileB.gridZ);
        AssertVec(Vector3.right, p.normal, "normal");
        AssertVec(Vector3.forward, p.right, "right runs +Z from A to B");
    }

    [Test]
    public void TryPlace_PinnedPairIsUsed()
    {
        var def = Grid(5, 4, floors: 2);
        Assert.AreEqual(BuildingSigns.Skip.None,
            BuildingSigns.TryPlace(def, Spec("north", pinned: true, hostX: 3, hostZ: 3, hostFloor: 1), CS, null, out var p));
        Assert.AreEqual(3, p.tileA.gridX);
        Assert.AreEqual(4, p.tileB.gridX);
        Assert.AreEqual(1, p.floor, "pinned to the first floor up");
        Assert.AreEqual(2f * CS - BuildingSigns.TopMargin - BuildingSigns.BandFrac * CS * 0.5f, p.center.y, 1e-3f);
        Assert.IsFalse(p.pinLost);
        Assert.IsFalse(p.faceFallback);
    }

    [Test]
    public void TryPlace_LostPinFallsBackToTheAutoSpotAndSaysSo()
    {
        var def = Grid(5, 4);
        // x = 4 has no neighbour to its right, and there is no floor 1 at all.
        Assert.AreEqual(BuildingSigns.Skip.None,
            BuildingSigns.TryPlace(def, Spec("north", pinned: true, hostX: 4, hostZ: 3, hostFloor: 0), CS, null, out var p));
        Assert.IsTrue(p.pinLost);
        Assert.AreEqual(1, p.tileA.gridX);
        Assert.AreEqual(2, p.tileB.gridX);
        Assert.AreEqual("north", p.face);
        Assert.IsFalse(p.faceFallback);

        Assert.AreEqual(BuildingSigns.Skip.None,
            BuildingSigns.TryPlace(def, Spec("north", pinned: true, hostX: 1, hostZ: 3, hostFloor: 1), CS, null, out var q));
        Assert.IsTrue(q.pinLost);
        Assert.AreEqual(0, q.floor);
    }

    [Test]
    public void TryPlace_NoRoomOnTheChosenWallFallsBackToAnotherWallAndSaysSo()
    {
        // One tile wide: the north wall is a single tile, the east wall runs four tiles.
        var def = Grid(1, 4);
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, CS, null, out var p));
        Assert.IsTrue(p.faceFallback);
        Assert.AreEqual("east", p.face, "the first other wall with room, in WallFaces order");
        Assert.AreEqual(0, p.tileA.gridX);
        Assert.AreEqual(1, p.tileA.gridZ);
        Assert.AreEqual(2, p.tileB.gridZ);
        AssertVec(Vector3.right, p.normal, "normal");
        Assert.IsFalse(p.pinLost);

        // A pin on a wall with no room at all is both lost and moved.
        Assert.AreEqual(BuildingSigns.Skip.None,
            BuildingSigns.TryPlace(def, Spec("north", pinned: true, hostX: 0, hostZ: 3), CS, null, out var q));
        Assert.IsTrue(q.pinLost);
        Assert.IsTrue(q.faceFallback);
    }

    [Test]
    public void TryPlace_EveryRunTooShort_FallsBackToTheSouthWall()
    {
        // 3 wide, blocker in front of the middle tile: two single-tile runs on the north side; the
        // east and west sides are one tile each; the south row is open across all three.
        var def = Grid(3, 1);
        def.tiles.Add(new TileDef { gridX = 1, gridZ = 1, floor = 0, shapeId = "square" });
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, CS, null, out var p));
        Assert.IsTrue(p.faceFallback);
        Assert.AreEqual("south", p.face);
        Assert.AreEqual(0, p.tileA.gridX);
        Assert.AreEqual(1, p.tileB.gridX);
    }

    [Test]
    public void TryPlace_NoWallHasRoom_TooNarrow()
    {
        Assert.AreEqual(BuildingSigns.Skip.TooNarrow, BuildingSigns.TryPlace(Grid(1, 1), CS, null, out _));
        Assert.AreEqual(BuildingSigns.Skip.TooNarrow, BuildingSigns.TryPlace(Grid(1, 1), Spec("west", pinned: true), CS, null, out _));
    }

    [Test]
    public void TryPlace_BlockedCentre_TakesTheLongestOpenRun()
    {
        // 6 wide, north row z = 0 (deep = 1). A tile in front of x = 2 splits the row into runs of
        // 2 (x 0, 1) and 3 (x 3, 4, 5): the longer run wins and the pair centres left-biased on it.
        var def = Grid(6, 1);
        def.tiles.Add(new TileDef { gridX = 2, gridZ = 1, floor = 0, shapeId = "square" });
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, CS, null, out var p));
        Assert.AreEqual(3, p.tileA.gridX);
        Assert.AreEqual(4, p.tileB.gridX);
        Assert.IsFalse(p.faceFallback);
    }

    [Test]
    public void TryPlace_UsesTheLowestFloorPresent()
    {
        var def = Grid(4, 2, floors: 2);
        def.tiles.RemoveAll(t => t.floor == 0);   // a building that starts one storey up
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, CS, null, out var p));
        Assert.AreEqual(1, p.floor);
        Assert.AreEqual(2f * CS - BuildingSigns.TopMargin - BuildingSigns.BandFrac * CS * 0.5f, p.center.y, 1e-3f);
    }

    [Test]
    public void TryPlace_NoTextOrFace_Skips()
    {
        Assert.AreEqual(BuildingSigns.Skip.NoText, BuildingSigns.TryPlace(Grid(5, 4, text: null), CS, null, out _));
        Assert.AreEqual(BuildingSigns.Skip.NoText, BuildingSigns.TryPlace(Grid(5, 4, text: "  "), CS, null, out _));
        Assert.AreEqual(BuildingSigns.Skip.NoFace, BuildingSigns.TryPlace(Grid(5, 4, face: null), CS, null, out _));
        Assert.AreEqual(BuildingSigns.Skip.NoFace, BuildingSigns.TryPlace(Grid(5, 4, face: "top"), CS, null, out _));
        Assert.AreEqual(BuildingSigns.Skip.NoText, BuildingSigns.TryPlace(null, CS, null, out _));
        Assert.AreEqual(BuildingSigns.Skip.NoText, BuildingSigns.TryPlace(Grid(5, 4), default, CS, null, out _));
    }

    [Test]
    public void TryPlace_NoTiles_TooNarrow()
    {
        var def = Grid(0, 0);
        Assert.AreEqual(BuildingSigns.Skip.TooNarrow, BuildingSigns.TryPlace(def, CS, null, out _));
    }

    [Test]
    public void TryPlace_SpecFromInstance_EndToEnd()
    {
        // A generated block (yaw 180, east compass) shows its sign on the local north wall; aiming
        // it north puts it on the local west wall, which runs along Z.
        var def  = Grid(5, 4, text: null, face: null);
        var inst = new BuildingInstance { rotationY = 180f, signText = "CAFE", signCompass = "east" };
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, BuildingSigns.SpecFor(inst, def), CS, null, out var p));
        Assert.AreEqual("north", p.face);
        inst.signCompass = "north";
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(def, BuildingSigns.SpecFor(inst, def), CS, null, out var q));
        Assert.AreEqual("west", q.face);
        AssertVec(Vector3.left, q.normal, "normal");
        Assert.AreEqual(0, q.tileA.gridX);
    }
}
