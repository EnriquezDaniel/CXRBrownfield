using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

// Spec for deform-aware decor placement: TileFaceGeometry face frames + DecorPlacement reseating.
// Prop bases are built synthetically through the pure DecorAlignment.AnalyzeProp (no prefabs), the
// same way TileBuildingEditor/WorldRenderer derive them from measured renderer bounds.
[TestFixture]
public class DecorPlacementTests
{
    private const float CS = 4f;

    // A filled rectangular grid of plain square tiles, one or more floors (as in TileDeformTests).
    private static BuildingDef Grid(int wide, int deep, int floors = 1)
    {
        var tiles = new List<TileDef>();
        for (int f = 0; f < floors; f++)
            for (int x = 0; x < wide; x++)
                for (int z = 0; z < deep; z++)
                    tiles.Add(new TileDef { gridX = x, gridZ = z, floor = f, shapeId = "square" });
        return new BuildingDef { gridCellSize = CS, floors = floors, tiles = tiles };
    }

    private static TileDef Tile(BuildingDef b, int x, int z, int f = 0) =>
        b.tiles.Find(t => t.gridX == x && t.gridZ == z && t.floor == f);

    // A window-ish prop: 1.0 wide × 1.5 tall × 0.1 thick, pivot at bounds center. Auto mount picks
    // the thinnest axis (+Z) as depth; up = +Y; backOffset = -0.05.
    private static DecorAlignment.PropBasis WindowBasis() =>
        DecorAlignment.AnalyzeProp(Vector3.zero, new Vector3(0.5f, 0.75f, 0.05f),
                                   DecorAlignment.MountAxis.Auto, false);

    private static EmbeddedObjectDef HostedDecor(int gx, int gz, int gf, string face,
                                                 DecorAlignment.Anchor anchor = DecorAlignment.Anchor.Center) =>
        new EmbeddedObjectDef
        {
            instanceId = "test",
            prefabType = "window",
            localPos   = new[] { 0f, 0f, 0f },
            hostGridX  = gx, hostGridZ = gz, hostFloor = gf, hostFace = face,
            fillsFace  = true,
            decorWidthFrac     = 0.8f,
            decorHeightFrac    = 0.9f,
            decorAnchor        = (int)anchor,
            decorSurfaceOffset = 0.03f,
        };

    private static Vector3 CellCenter(int gx, int gz, int gf) =>
        new Vector3((gx + 0.5f) * CS, (gf + 0.5f) * CS, (gz + 0.5f) * CS);

    private static Vector3 Pos(EmbeddedObjectDef e) => new Vector3(e.localPos[0], e.localPos[1], e.localPos[2]);

    // -----------------------------------------------------------------------
    // TileFaceGeometry
    // -----------------------------------------------------------------------

    [Test]
    public void FaceFrame_PlainTile_MatchesLegacyCellMath()
    {
        var b = Grid(3, 2);
        Assert.IsTrue(TileFaceGeometry.TryGetFaceFrame(Tile(b, 2, 1), "north", CS, out var f));

        Vector3 expectCenter = CellCenter(2, 1, 0) + new Vector3(0f, 0f, CS * 0.5f);
        Assert.Less((f.center - expectCenter).magnitude, 1e-4f, "center = cell center + n*cs/2");
        Assert.Less((f.normal - Vector3.forward).magnitude, 1e-4f, "north face points +Z");
        Assert.Less((f.up - Vector3.up).magnitude, 1e-4f, "wall up = world up");
        Assert.AreEqual(CS, f.width,  1e-4f);
        Assert.AreEqual(CS, f.height, 1e-4f);
        Assert.AreEqual(-CS * 0.5f, f.uBottom, 1e-4f);
        Assert.AreEqual(+CS * 0.5f, f.uTop,    1e-4f);
        Assert.IsFalse(f.isRoof);
    }

    [Test]
    public void FaceFrame_CornerBend_WallPlanar_NormalYawsWithSkew()
    {
        // SW bend on a 1×6 strip: the west wall tilts in X at tan(30°) per cell of Z (see
        // TileDeformTests.CornerBend_ConstantSkew). Its face normal must yaw with the slant and the
        // frame must lie on the true deformed wall plane.
        var b = Grid(1, 6);
        TileDeformField.ApplyCornerBend(b, TileDeformField.Corner.SW, 30f);
        float t = Mathf.Tan(30f * Mathf.Deg2Rad);
        Vector3 expectNormal = new Vector3(-1f, 0f, -t).normalized;

        foreach (var tile in b.tiles)
        {
            Assert.IsTrue(TileFaceGeometry.TryGetFaceFrame(tile, "west", CS, out var f));
            Assert.Less(Mathf.Abs(f.normal.y), 1e-4f, "corner bend keeps walls vertical");
            Assert.Less((f.normal - expectNormal).magnitude, 1e-4f, "wall normal yaws with the slant");

            // Planarity: the 4 deformed west-face corners (recomputed via the public warp with the
            // same corner conventions) all lie on the frame's plane.
            float h = CS * 0.5f;
            Vector3 cc = CellCenter(tile.gridX, tile.gridZ, tile.floor);
            foreach (float y in new[] { -h, +h })
                foreach (float z in new[] { -h, +h })
                {
                    Vector3 c = cc + TileDeformField.WarpVertex(tile.deform, new Vector3(-h, y, z), CS);
                    Assert.Less(Mathf.Abs(Vector3.Dot(c - f.center, f.normal)), 1e-4f,
                                $"west face corner off-plane on tile z={tile.gridZ}");
                }
        }
    }

    [Test]
    public void FaceFrame_SlopedEdge_ShrinksSafeHeight()
    {
        // Lower the north roof edge by half a cell. On tile (3,3) the north face's top corners drop
        // by 0.375/0.5 cells (along = 3/4 and 4/4, weight 1 at the edge), so the safe band shrinks
        // to 2 m while the width stays a full cell and the bottom stays on the floor plane.
        var b = Grid(4, 4);
        TileDeformField.ApplySlopedEdge(b, TileDeformField.Edge.North, -0.5f, 2f);
        Assert.IsTrue(TileFaceGeometry.TryGetFaceFrame(Tile(b, 3, 3), "north", CS, out var f));

        Assert.AreEqual(CS, f.width, 1e-3f, "a vertical-only slope leaves the wall width alone");
        Assert.AreEqual(2f, f.height, 1e-3f, "safe height = cs - drop of the lower top corner");
        Assert.Less(f.height, CS);
        float bottomY = (f.center + f.up * f.uBottom).y;
        Assert.AreEqual(0f, bottomY, 1e-3f, "the safe bottom line stays on the floor plane");
    }

    [Test]
    public void FaceFrame_UnknownFace_ReturnsFalse()
    {
        var b = Grid(1, 1);
        Assert.IsFalse(TileFaceGeometry.TryGetFaceFrame(Tile(b, 0, 0), "wall", CS, out _),
                       "the Decorate tool's 'wall' fallback name must not resolve to a frame");
        Assert.IsFalse(TileFaceGeometry.TryGetFaceFrame(null, "north", CS, out _));
    }

    // -----------------------------------------------------------------------
    // DecorPlacement.TryReseat
    // -----------------------------------------------------------------------

    [Test]
    public void Reseat_PlainTile_ReproducesLegacyPaintFormula()
    {
        // Regression lock: on an undeformed tile the reseat math must equal the old PlaceFaceDecor
        // inline formula exactly (cellCenter + n*(cs/2 + seat) + up*anchorOff, legacy fit + anchor).
        var b     = Grid(3, 3, floors: 2);
        var basis = WindowBasis();
        var emb   = HostedDecor(1, 2, 1, "north", DecorAlignment.Anchor.Bottom);

        Assert.IsTrue(DecorPlacement.TryReseat(Tile(b, 1, 2, 1), emb, CS, basis));

        float legacyScale  = DecorAlignment.FitScaleBox(basis, CS, 0.8f, 0.9f);
        float legacySeat   = DecorAlignment.SeatDistance(basis, legacyScale) + 0.03f;
        float legacyAnchor = DecorAlignment.AnchorOffset(DecorAlignment.Anchor.Bottom,
                                                         basis.inPlaneHeight * legacyScale, CS);
        Vector3 legacyPos  = CellCenter(1, 2, 1)
                             + Vector3.forward * (0.5f * CS + legacySeat)
                             + Vector3.up * legacyAnchor;

        Assert.AreEqual(legacyScale, emb.scale, 1e-4f);
        Assert.Less((Pos(emb) - legacyPos).magnitude, 1e-4f);
        Vector3 legacyEuler = DecorAlignment.AlignRotation(basis, Vector3.forward, false, false, 0f).eulerAngles;
        Assert.AreEqual(legacyEuler.x, emb.rotationX, 1e-3f);
        Assert.AreEqual(legacyEuler.y, emb.rotationY, 1e-3f);
        Assert.AreEqual(legacyEuler.z, emb.rotationZ, 1e-3f);
    }

    [Test]
    public void Reseat_FollowsDeform_PropLandsOnDeformedPlane()
    {
        var b     = Grid(1, 6);
        var basis = WindowBasis();
        var emb   = HostedDecor(0, 3, 0, "west");

        Assert.IsTrue(DecorPlacement.TryReseat(Tile(b, 0, 3), emb, CS, basis));
        Vector3 before = Pos(emb);

        TileDeformField.ApplyCornerBend(b, TileDeformField.Corner.SW, 30f);
        Assert.IsTrue(DecorPlacement.TryReseat(Tile(b, 0, 3), emb, CS, basis));
        Vector3 after = Pos(emb);

        Assert.Greater((after - before).magnitude, 0.5f, "the prop must move with the skewed wall");

        // The prop sits at seat distance off the DEFORMED face plane, inside the safe band.
        Assert.IsTrue(TileFaceGeometry.TryGetFaceFrame(Tile(b, 0, 3), "west", CS, out var f));
        float seat = DecorAlignment.SeatDistance(basis, emb.scale) + emb.decorSurfaceOffset;
        Assert.AreEqual(seat, Vector3.Dot(after - f.center, f.normal), 1e-4f);
        float u = Vector3.Dot(after - f.center, f.up);
        float r = Vector3.Dot(after - f.center, f.right);
        Assert.LessOrEqual(Mathf.Abs(r), 0.5f * f.width + 1e-4f);
        Assert.GreaterOrEqual(u - 0.5f * basis.inPlaneHeight * emb.scale, f.uBottom - 1e-4f);
        Assert.LessOrEqual(u + 0.5f * basis.inPlaneHeight * emb.scale, f.uTop + 1e-4f);
    }

    [Test]
    public void Reseat_ShrinkToFit_UsesDeformedExtents()
    {
        var b     = Grid(4, 4);
        var basis = WindowBasis();

        var plain = HostedDecor(3, 3, 0, "north", DecorAlignment.Anchor.Bottom);
        Assert.IsTrue(DecorPlacement.TryReseat(Tile(b, 3, 3), plain, CS, basis));
        float plainScale = plain.scale;

        TileDeformField.ApplySlopedEdge(b, TileDeformField.Edge.North, -0.5f, 2f);
        var sloped = HostedDecor(3, 3, 0, "north", DecorAlignment.Anchor.Bottom);
        Assert.IsTrue(DecorPlacement.TryReseat(Tile(b, 3, 3), sloped, CS, basis));

        Assert.Less(sloped.scale, plainScale, "the prop shrinks to the shortened face");

        // Bottom anchor: the prop's base seats on the face's safe bottom line.
        Assert.IsTrue(TileFaceGeometry.TryGetFaceFrame(Tile(b, 3, 3), "north", CS, out var f));
        float u = Vector3.Dot(Pos(sloped) - f.center, f.up);
        Assert.AreEqual(f.uBottom, u - 0.5f * basis.inPlaneHeight * sloped.scale, 1e-4f);
    }

    // -----------------------------------------------------------------------
    // DecorPlacement.ReseatAll / backward compatibility
    // -----------------------------------------------------------------------

    private static bool AlwaysWindow(string prefabType, DecorAlignment.MountAxis axis, bool flip,
                                     out DecorAlignment.PropBasis basis)
    {
        basis = WindowBasis();
        return true;
    }

    [Test]
    public void ReseatAll_LegacyDefs_Untouched()
    {
        var b = Grid(2, 2);
        var legacyNoRules = new EmbeddedObjectDef
        {
            instanceId = "a", prefabType = "window",
            localPos = new[] { 1f, 2f, 3f }, rotationY = 45f,
            hostGridX = 0, hostGridZ = 0, hostFloor = 0, hostFace = "north",
            // decorWidthFrac == 0: pre-change JSON — replay verbatim.
        };
        var legacyNoHost = new EmbeddedObjectDef
        {
            instanceId = "b", prefabType = "window",
            localPos = new[] { 4f, 5f, 6f }, rotationY = 90f,
            decorWidthFrac = 0.8f, decorHeightFrac = 0.9f,
            // hostFace == null: freeform prop — replay verbatim.
        };
        b.embeddedObjects = new List<EmbeddedObjectDef> { legacyNoRules, legacyNoHost };

        DecorPlacement.ReseatAll(b, CS, AlwaysWindow);

        Assert.Less((Pos(legacyNoRules) - new Vector3(1f, 2f, 3f)).magnitude, 1e-6f);
        Assert.AreEqual(45f, legacyNoRules.rotationY, 1e-6f);
        Assert.Less((Pos(legacyNoHost) - new Vector3(4f, 5f, 6f)).magnitude, 1e-6f);
        Assert.AreEqual(90f, legacyNoHost.rotationY, 1e-6f);
    }

    [Test]
    public void Reseat_MissingHostOrUnknownFace_LeavesDefUntouched()
    {
        var basis = WindowBasis();

        var emb = HostedDecor(0, 0, 0, "north");
        emb.localPos = new[] { 9f, 9f, 9f };
        Assert.IsFalse(DecorPlacement.TryReseat(null, emb, CS, basis), "no host tile");
        Assert.Less((Pos(emb) - new Vector3(9f, 9f, 9f)).magnitude, 1e-6f);

        var b = Grid(1, 1);
        var wall = HostedDecor(0, 0, 0, "wall");
        wall.localPos = new[] { 9f, 9f, 9f };
        Assert.IsFalse(DecorPlacement.TryReseat(Tile(b, 0, 0), wall, CS, basis), "unresolvable face name");
        Assert.Less((Pos(wall) - new Vector3(9f, 9f, 9f)).magnitude, 1e-6f);

        // ReseatAll skips a hosted decor whose tile was deleted.
        var orphan = HostedDecor(5, 5, 0, "north");
        orphan.localPos = new[] { 9f, 9f, 9f };
        b.embeddedObjects = new List<EmbeddedObjectDef> { orphan };
        DecorPlacement.ReseatAll(b, CS, AlwaysWindow);
        Assert.Less((Pos(orphan) - new Vector3(9f, 9f, 9f)).magnitude, 1e-6f);
    }
}
