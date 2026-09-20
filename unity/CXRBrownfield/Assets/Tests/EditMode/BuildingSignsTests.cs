using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

// BuildingSigns: the sign word rules, compass to wall and back through an instance yaw, what a
// placed building's signs are (the list, else the older single sign, else the legacy def), the open
// stretches of wall, how the row shares them and spills around the building, and pins.
[TestFixture]
public class BuildingSignsTests
{
    private const float CS = 4f;

    private static void AssertVec(Vector3 expect, Vector3 actual, string what) =>
        Assert.Less((expect - actual).magnitude, 1e-3f, $"{what}: expected {expect}, got {actual}");

    // A rectangular square-tile grid, the shape LayoutConverter builds. The legacy sign fields sit
    // on the def so records saved before signs moved to the instance stay covered.
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

    private const float PlateY = 3.15f;   // floor 0: top edge 4 m, less the 0.15 m margin, less half the 1.4 m band

    private static readonly TileFit Pillar = new TileFit(new Vector3(0.5f, 1f, 0.5f), Vector3.zero);
    private static TileFit FitFor(string s) => s == "pillar" ? Pillar : TileFit.Full;

    private static BuildingSignEntry E(string text) => new BuildingSignEntry { text = text };

    private static BuildingSignEntry Pinned(string text, string face, int floor, int side, int half) =>
        new BuildingSignEntry { text = text, pinned = true, pinFace = face, pinFloor = floor, pinSide = side, pinHalf = half };

    private static List<BuildingSigns.SignResult> Lay(BuildingDef def, string startFace, params BuildingSignEntry[] entries) =>
        BuildingSigns.Layout(def, entries, startFace, CS, null);

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

    // ---- what a building's signs are ----

    [Test]
    public void EntriesFor_TheListWinsOnceItExists()
    {
        var def  = Grid(5, 4, text: "OLD", face: "north");
        var inst = new BuildingInstance { signText = "STALE", signCompass = "east", signs = new List<BuildingSignEntry> { E("CAFE"), E("BIKES") } };
        Assert.AreSame(inst.signs, BuildingSigns.EntriesFor(inst, def));
        inst.signs.Clear();
        Assert.AreEqual(0, BuildingSigns.EntriesFor(inst, def).Count, "an empty list still owns the signs");
    }

    [Test]
    public void EntriesFor_SingleSignFields_MapThePinnedPairToItsMidpoint()
    {
        var def  = Grid(5, 4, text: "OLD", face: "north");
        // Compass east is world -Z; with no yaw that is the local south wall, the z = 0 row.
        var inst = new BuildingInstance { signText = " cafe ", signCompass = "east",
                                          signPinned = true, signHostX = 2, signHostZ = 0, signHostFloor = 0 };
        var entries = BuildingSigns.EntriesFor(inst, def);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("CAFE", entries[0].text);
        Assert.IsTrue(entries[0].pinned);
        Assert.AreEqual("south", entries[0].pinFace);
        Assert.AreEqual(0, entries[0].pinSide);
        Assert.AreEqual(6, entries[0].pinHalf);
        Assert.IsNull(inst.signs, "reading never migrates");

        var r = BuildingSigns.LayoutFor(inst, def, CS, null)[0];
        Assert.AreEqual(BuildingSigns.Skip.None, r.skip);
        AssertVec(new Vector3(12f, PlateY, 0f), r.p.center, "the old pair (tiles 2 and 3) met at x = 12");
        Assert.IsTrue(r.p.pinned);
    }

    [Test]
    public void EntriesFor_LegacyDefWord_AndClearedInstance()
    {
        var def = Grid(5, 4, text: "OLD", face: "north");
        var legacy = BuildingSigns.EntriesFor(new BuildingInstance { rotationY = 180f }, def);
        Assert.AreEqual(1, legacy.Count);
        Assert.AreEqual("OLD", legacy[0].text);
        Assert.IsFalse(legacy[0].pinned);
        Assert.AreEqual("OLD", BuildingSigns.EntriesFor(null, def)[0].text, "no instance at all: the def's own sign");

        var cleared = new BuildingInstance { signText = null, signCompass = "east" };
        Assert.AreEqual(0, BuildingSigns.EntriesFor(cleared, def).Count, "a cleared sign keeps its compass so the legacy word stays gone");
        Assert.AreEqual(0, BuildingSigns.EntriesFor(new BuildingInstance(), Grid(5, 4, text: null)).Count);
    }

    [Test]
    public void StartFace_CompassThroughYawThenLegacyFaceThenEast()
    {
        var def = Grid(5, 4, text: "OLD", face: "north");
        // Yaw 180: the local west wall (-X) turns to +X, world north.
        Assert.AreEqual("west", BuildingSigns.StartFace(new BuildingInstance { rotationY = 180f, signCompass = "north" }, def));
        Assert.AreEqual("north", BuildingSigns.StartFace(new BuildingInstance { rotationY = 180f }, def), "the legacy face is already building-local");
        Assert.AreEqual("south", BuildingSigns.StartFace(new BuildingInstance(), Grid(5, 4, text: null)), "east with no yaw is the local south wall");
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

    [Test]
    public void Adopt_MovesTheSignIntoTheListOnce()
    {
        var def  = Grid(5, 4, text: "OLD", face: "north");
        var inst = new BuildingInstance { rotationY = 180f };
        var before = BuildingSigns.LayoutFor(inst, def, CS, null)[0].p;

        BuildingSigns.Adopt(inst, def);
        Assert.AreEqual(1, inst.signs.Count);
        Assert.AreEqual("OLD", inst.signs[0].text);
        Assert.AreEqual("east", inst.signCompass);
        Assert.IsNull(inst.signText);
        var after = BuildingSigns.LayoutFor(inst, def, CS, null)[0].p;
        Assert.AreEqual(before.face, after.face, "adopting leaves the sign on the same wall");
        AssertVec(before.center, after.center, "and at the same spot");

        var list = inst.signs;
        inst.signs[0].text = "NEW";
        BuildingSigns.Adopt(inst, def);
        Assert.AreSame(list, inst.signs, "a migrated instance is left alone");
        Assert.AreEqual("NEW", inst.signs[0].text);
    }

    [Test]
    public void PlateWidth_FollowsTheWordAndHasAFloor()
    {
        Assert.AreEqual(2.8f, BuildingSigns.PlateWidth("SIGN", CS), 1e-4f);
        Assert.AreEqual(5.2f, BuildingSigns.PlateWidth("icecream", CS), 1e-4f);
        Assert.AreEqual(10f,  BuildingSigns.PlateWidth("ABCDEFGHIJKLMNOP", CS), 1e-4f);
        Assert.AreEqual(2f,   BuildingSigns.PlateWidth("A", CS), 1e-4f, "never under the minimum plate");
        Assert.AreEqual(1.4f, BuildingSigns.PlateWidth("SIGN", 2f), 1e-4f, "everything scales with the cell");
    }

    // ---- open wall ----

    [Test]
    public void Stretches_PlainGrid_OneRunPerFloor()
    {
        var s = BuildingSigns.Stretches(Grid(5, 4, floors: 2), "north", CS, null);
        Assert.AreEqual(2, s.Count);
        Assert.AreEqual(0, s[0].floor);
        Assert.AreEqual(1, s[1].floor);
        Assert.AreEqual(3, s[0].side, "the north row of a 4-deep grid");
        Assert.AreEqual(0f,  s[0].u0, 1e-4f);
        Assert.AreEqual(20f, s[0].u1, 1e-4f);
        Assert.AreEqual(5, s[0].hosts.Count);
        Assert.AreEqual(1, BuildingSigns.Stretches(Grid(5, 4, floors: 2), "north", CS, null, 1).Count);
        Assert.AreEqual(0, BuildingSigns.Stretches(Grid(5, 4), "up", CS, null).Count);
    }

    [Test]
    public void Stretches_AMissingTileSplitsTheRun()
    {
        var def = Grid(5, 4);
        RemoveTile(def, 2, 3, 0);
        var s = BuildingSigns.Stretches(def, "north", CS, null);
        Assert.AreEqual(3, s.Count);
        Assert.AreEqual(2, s[0].side, "the tile behind the hole shows its own north face");
        Assert.AreEqual(8f,  s[0].u0, 1e-4f);
        Assert.AreEqual(12f, s[0].u1, 1e-4f);
        Assert.AreEqual(0f,  s[1].u0, 1e-4f);
        Assert.AreEqual(8f,  s[1].u1, 1e-4f);
        Assert.AreEqual(12f, s[2].u0, 1e-4f);
        Assert.AreEqual(20f, s[2].u1, 1e-4f);
    }

    [Test]
    public void Stretches_APillarBreaksTheRun()
    {
        var def = Grid(3, 1);
        def.tiles.Find(t => t.gridX == 1).shapeId = "pillar";
        var s = BuildingSigns.Stretches(def, "north", CS, FitFor);
        Assert.AreEqual(3, s.Count);
        Assert.AreEqual(4f, s[0].u1, 1e-4f);
        Assert.AreEqual(5f, s[1].u0, 1e-4f);
        Assert.AreEqual(7f, s[1].u1, 1e-4f);
        Assert.AreEqual(8f, s[2].u0, 1e-4f);
    }

    // ---- the row ----

    [Test]
    public void Layout_LoneSign_CentredInTheTopBand()
    {
        var r = Lay(Grid(5, 4), "north", E("SIGN"))[0];
        Assert.AreEqual(BuildingSigns.Skip.None, r.skip);
        AssertVec(new Vector3(10f, PlateY, 16f), r.p.center, "centre");
        AssertVec(Vector3.forward, r.p.normal, "normal");
        AssertVec(Vector3.up, r.p.up, "up");
        Assert.AreEqual(2.8f, r.p.width, 1e-4f);
        Assert.AreEqual(1.4f, r.p.height, 1e-4f);
        Assert.AreEqual(1f, r.p.scale, 1e-4f);
        Assert.AreEqual("north", r.p.face);
        Assert.IsFalse(r.p.pinned);

        AssertVec(new Vector3(36f, PlateY, 16f), Lay(Grid(18, 4), "north", E("THEATER"))[0].p.center, "18 wide");
    }

    [Test]
    public void Layout_EastWall_RunsAlongZ()
    {
        var r = Lay(Grid(5, 4), "east", E("SIGN"))[0];
        AssertVec(new Vector3(20f, PlateY, 8f), r.p.center, "centre");
        AssertVec(Vector3.right, r.p.normal, "normal");
    }

    [Test]
    public void Layout_SharesTheWallByPlateWidth_LeftToRightSeenFromOutside()
    {
        float gap = BuildingSigns.GapFrac * CS;
        float wa = BuildingSigns.PlateWidth("CAFE", CS), wb = BuildingSigns.PlateWidth("BIKE WORKSHOP", CS);
        float shareA = 24f * (wa + gap) / (wa + wb + 2f * gap);

        // East wall: the viewer's right is +Z, so the first sign sits at the low end.
        var east = Lay(Grid(4, 6), "east", E("CAFE"), E("BIKE WORKSHOP"));
        Assert.AreEqual(shareA * 0.5f, east[0].p.u, 1e-3f);
        Assert.AreEqual(shareA + (24f - shareA) * 0.5f, east[1].p.u, 1e-3f);
        Assert.AreEqual(wb, east[1].p.width, 1e-4f, "plates keep their own width");

        // North wall: the viewer's right is -X, so the first sign sits at the high end.
        var north = Lay(Grid(6, 4), "north", E("CAFE"), E("BIKE WORKSHOP"));
        Assert.AreEqual(24f - shareA * 0.5f, north[0].p.u, 1e-3f);
        Assert.AreEqual((24f - shareA) * 0.5f, north[1].p.u, 1e-3f);
        Assert.Greater(north[0].p.center.x, north[1].p.center.x);
    }

    [Test]
    public void Layout_ThreeSigns_EveryPlateStaysInsideItsShare()
    {
        var r = Lay(Grid(6, 1), "east", E("A"), E("SIGN"), E("ICECREAM"));
        // The east wall of a 1-deep grid is one tile: only the first sign fits there.
        Assert.AreEqual("east", r[0].p.face);
        var south = Lay(Grid(6, 1), "south", E("A"), E("SIGN"), E("ICECREAM"));
        float gap = BuildingSigns.GapFrac * CS, edge = 0f;
        for (int i = 0; i < 3; i++)   // facing the south wall the viewer's right is +X, so the row runs up X
        {
            Assert.AreEqual("south", south[i].p.face);
            Assert.GreaterOrEqual(south[i].p.u - south[i].p.width * 0.5f, edge + gap * 0.5f - 1e-3f, $"sign {i} clears its neighbour");
            edge = south[i].p.u + south[i].p.width * 0.5f;
        }
        Assert.LessOrEqual(edge, 24f - gap * 0.5f + 1e-3f);
    }

    [Test]
    public void Layout_APinnedSignKeepsItsSpot_AndTheRestFlowAroundIt()
    {
        var r = Lay(Grid(6, 1), "east", Pinned("SIGN", "north", 0, 0, 6), E("CAFE"));
        Assert.IsTrue(r[0].p.pinned);
        Assert.AreEqual("north", r[0].p.face, "a pin remembers its own wall");
        Assert.AreEqual(12f, r[0].p.u, 1e-4f);

        // Same wall: the pin cuts the run into two 10.2 m pieces; the low one comes first on a tie.
        r = Lay(Grid(6, 1), "north", Pinned("SIGN", "north", 0, 0, 6), E("CAFE"));
        Assert.AreEqual(12f, r[0].p.u, 1e-4f);
        Assert.AreEqual(5.1f, r[1].p.u, 1e-3f);
        Assert.IsFalse(r[1].p.pinned);
    }

    [Test]
    public void Layout_ALostPinIsLaidOutAutomaticallyAndSaysSo()
    {
        var r = Lay(Grid(5, 4), "north", Pinned("SIGN", "north", 3, 3, 5))[0];
        Assert.AreEqual(BuildingSigns.Skip.None, r.skip);
        Assert.IsTrue(r.p.pinLost);
        Assert.IsFalse(r.p.pinned);
        AssertVec(new Vector3(10f, PlateY, 16f), r.p.center, "the automatic spot");
    }

    [Test]
    public void Layout_APinClampsToItsStretch_AndShrinksOnAShortOne()
    {
        var r = Lay(Grid(5, 4), "north", Pinned("SIGN", "north", 0, 3, 0))[0];
        Assert.AreEqual(1.4f, r.p.u, 1e-4f, "the plate stays on the wall");

        r = Lay(Grid(1, 1), "north", Pinned("ICECREAM", "north", 0, 0, 1))[0];
        Assert.AreEqual(4f, r.p.width, 1e-4f);
        Assert.AreEqual(4f / 5.2f, r.p.scale, 1e-4f);
        Assert.AreEqual(1.4f * 4f / 5.2f, r.p.height, 1e-4f);
        Assert.AreEqual(2f, r.p.u, 1e-4f);

        Assert.AreEqual(BuildingSigns.Skip.TooSmall, Lay(Grid(1, 1), "north", Pinned("ABCDEFGHIJKLMNOP", "north", 0, 0, 1))[0].skip);
    }

    [Test]
    public void Layout_Overflow_SpillsCounterclockwiseOnATie()
    {
        var r = Lay(Grid(2, 2), "north", E("ICECREAM"), E("ICECREAM"), E("ICECREAM"), E("ICECREAM"), E("ICECREAM"));
        Assert.AreEqual("north", r[0].p.face);
        Assert.AreEqual("west",  r[1].p.face);
        Assert.AreEqual("south", r[2].p.face);
        Assert.AreEqual("east",  r[3].p.face);
        Assert.AreEqual(BuildingSigns.Skip.NoRoom, r[4].skip, "every wall is full; the sign stays in the list");
    }

    // Box-shaped tiles show the same length of wall on opposite sides (a gap opens a face each
    // way), so the two neighbours tie and the row goes counterclockwise seen from above.
    [Test]
    public void FaceOrder_GoesCounterclockwiseOnATie()
    {
        CollectionAssert.AreEqual(new[] { "north", "west", "south", "east" }, BuildingSigns.FaceOrder(Grid(2, 2), "north", CS, null));
        CollectionAssert.AreEqual(new[] { "south", "east", "north", "west" }, BuildingSigns.FaceOrder(Grid(2, 2), "south", CS, null));
        var def = Grid(2, 2);
        def.tiles.Find(t => t.gridX == 0 && t.gridZ == 0).shapeId = "pillar";
        CollectionAssert.AreEqual(new[] { "east", "north", "west", "south" }, BuildingSigns.FaceOrder(def, "east", CS, FitFor));
        Assert.AreEqual(0, BuildingSigns.FaceOrder(def, "up", CS, null).Length);
    }

    [Test]
    public void Layout_ASignWithNoRoomDoesNotBlockLaterOnes()
    {
        // 2 x 1: north 8 m, west 4 m, south 8 m, east 4 m. A 5.2 m plate only fits the long walls.
        var r = Lay(Grid(2, 1), "north", E("ICECREAM"), E("ICECREAM"), E("ICECREAM"), E("SIGN"));
        Assert.AreEqual("north", r[0].p.face);
        Assert.AreEqual("south", r[1].p.face);
        Assert.AreEqual(BuildingSigns.Skip.NoRoom, r[2].skip);
        Assert.AreEqual(BuildingSigns.Skip.None, r[3].skip);
        Assert.AreEqual("east", r[3].p.face, "the row never goes back, so the short sign takes the next wall");
    }

    [Test]
    public void Layout_AWordWiderThanTheWallShrinksAlone_AndTooSmallIsSkipped()
    {
        var r = Lay(Grid(2, 1), "north", E("ABCDEFGHIJKLMNOP"), E("SIGN"));
        Assert.AreEqual(7.2f, r[0].p.width, 1e-4f, "the longest stretch less a gap at each end");
        Assert.AreEqual(0.72f, r[0].p.scale, 1e-4f);
        Assert.AreEqual(1f, r[1].p.scale, 1e-4f, "the others keep the shared size");

        Assert.AreEqual(BuildingSigns.Skip.TooSmall, Lay(Grid(1, 1), "north", E("ABCDEFGHIJKLMNOP"))[0].skip);
    }

    [Test]
    public void Layout_UsesTheLowestFloorPresent()
    {
        var def = Grid(5, 4, floors: 3);
        def.tiles.RemoveAll(t => t.floor == 0);
        var r = Lay(def, "north", E("SIGN"))[0];
        Assert.AreEqual(1, r.p.floor);
        Assert.AreEqual(PlateY + CS, r.p.center.y, 1e-3f);
    }

    [Test]
    public void Layout_EmptyRowsAndEmptyBuildings()
    {
        var r = Lay(Grid(5, 4), "north", E(null), E("SIGN"));
        Assert.AreEqual(BuildingSigns.Skip.NoText, r[0].skip);
        Assert.AreEqual(BuildingSigns.Skip.None, r[1].skip);
        AssertVec(new Vector3(10f, PlateY, 16f), r[1].p.center, "an empty row takes no wall");

        var empty = new BuildingDef { tiles = new List<TileDef>() };
        Assert.AreEqual(BuildingSigns.Skip.NoRoom, Lay(empty, "north", E("SIGN"))[0].skip);
        Assert.AreEqual(0, BuildingSigns.Layout(Grid(5, 4), null, "north", CS, null).Count);
    }

    [Test]
    public void LayoutFor_StartsOnTheWallTheCompassNamesThroughTheYaw()
    {
        var inst = new BuildingInstance { rotationY = 90f, signCompass = "east", signs = new List<BuildingSignEntry> { E("CAFE"), E("BIKES") } };
        var r = BuildingSigns.LayoutFor(inst, Grid(5, 4), CS, null);
        string wall = BuildingSigns.FaceTowardWorldDir(90f, BuildingSigns.WorldEast);
        Assert.AreEqual(wall, r[0].p.face);
        Assert.AreEqual(wall, r[1].p.face);
    }

    // ---- pins ----

    [Test]
    public void StepPin_RightMovesHalfATileTowardTheViewersRight()
    {
        var def = Grid(5, 4);
        // Facing the north wall from outside, the viewer's right is -X; facing the east wall it is +Z.
        Assert.AreEqual(5, BuildingSigns.StepPin(def, Pinned("SIGN", "north", 0, 3, 6), +1, 0, CS, null).half);
        Assert.AreEqual(7, BuildingSigns.StepPin(def, Pinned("SIGN", "north", 0, 3, 6), -1, 0, CS, null).half);
        Assert.AreEqual(5, BuildingSigns.StepPin(def, Pinned("SIGN", "east", 0, 4, 4), +1, 0, CS, null).half);
    }

    [Test]
    public void StepPin_SkipsStepsThatDoNotMoveThePlate_AndStaysPutAtTheEnd()
    {
        var def = Grid(5, 4);
        // A 5.2 m plate cannot centre under 2.6 m, so half steps 1 and 0 land on the same spot.
        Assert.AreEqual(1, BuildingSigns.StepPin(def, Pinned("ICECREAM", "north", 0, 3, 2), +1, 0, CS, null).half);
        Assert.AreEqual(1, BuildingSigns.StepPin(def, Pinned("ICECREAM", "north", 0, 3, 1), +1, 0, CS, null).half);
    }

    [Test]
    public void StepPin_HopsAGapInTheWall()
    {
        var def = Grid(6, 4);
        RemoveTile(def, 2, 3, 0);
        Assert.AreEqual(6, BuildingSigns.StepPin(def, Pinned("SIGN", "north", 0, 3, 7), +1, 0, CS, null).half);
        Assert.AreEqual(4, BuildingSigns.StepPin(def, Pinned("SIGN", "north", 0, 3, 6), +1, 0, CS, null).half, "across the hole at 8 to 12 m");
    }

    [Test]
    public void StepPin_UpKeepsThePlaceAlongTheWall_ElseTakesTheNearestSpot()
    {
        var def = Grid(5, 4, floors: 2);
        var up = BuildingSigns.StepPin(def, Pinned("SIGN", "north", 0, 3, 6), 0, +1, CS, null);
        Assert.AreEqual(1, up.floor);
        Assert.AreEqual(3, up.side);
        Assert.AreEqual(6, up.half);
        Assert.AreEqual(0, BuildingSigns.StepPin(def, Pinned("SIGN", "north", 0, 3, 6), 0, -1, CS, null).floor, "no floor below");

        // Upstairs the north row is gone from x = 2 on, so the wall there steps back to z = 12.
        for (int x = 2; x < 5; x++) RemoveTile(def, x, 3, 1);
        up = BuildingSigns.StepPin(def, Pinned("SIGN", "north", 0, 3, 6), 0, +1, CS, null);
        Assert.AreEqual(1, up.floor);
        Assert.AreEqual(2, up.side);
        Assert.AreEqual(6, up.half);
    }

    [Test]
    public void NearestPinSpot_TakesTheWallTheRayFaces()
    {
        var spots = BuildingSigns.PinSpots(Grid(5, 4), BuildingSigns.PlateWidth("SIGN", CS), CS, null);
        Assert.AreEqual(4 * 11 - 2 * 2, spots.Count, "half steps 0 to 5 tiles on the long walls, 0 to 4 on the short");

        Assert.IsTrue(BuildingSigns.NearestPinSpot(spots, new Ray(new Vector3(10.3f, 3f, 40f), Vector3.back), out var spot));
        Assert.AreEqual("north", spot.pin.face, "the south wall faces away from this ray");
        Assert.AreEqual(5, spot.pin.half);

        Assert.IsTrue(BuildingSigns.NearestPinSpot(spots, new Ray(new Vector3(40f, 3f, 7.8f), Vector3.left), out spot));
        Assert.AreEqual("east", spot.pin.face);
        Assert.AreEqual(4, spot.pin.half);

        Assert.IsFalse(BuildingSigns.NearestPinSpot(spots, new Ray(new Vector3(10f, 40f, 8f), Vector3.down), out _));
    }

    [Test]
    public void PinFromPlacement_AndDefaultPin_HoldTheAutomaticSpot()
    {
        var def = Grid(5, 4);
        var p = Lay(def, "north", E("SIGN"))[0].p;
        var pin = BuildingSigns.PinFromPlacement(p, CS);
        Assert.AreEqual("north", pin.face);
        Assert.AreEqual(3, pin.side);
        Assert.AreEqual(5, pin.half);

        Assert.IsTrue(BuildingSigns.DefaultPin(def, "north", BuildingSigns.PlateWidth("SIGN", CS), CS, null, out var dflt));
        Assert.IsTrue(BuildingSigns.SamePin(pin, dflt));

        var e = new BuildingSignEntry { text = "SIGN" };
        BuildingSigns.SetPin(e, pin);
        Assert.IsTrue(e.pinned);
        Assert.IsTrue(BuildingSigns.SamePin(pin, BuildingSigns.PinOf(e)));
        AssertVec(p.center, Lay(def, "south", e)[0].p.center, "pinning leaves the plate where it was");
    }
}
