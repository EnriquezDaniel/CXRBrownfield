using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

[TestFixture]
public class TileDeformTests
{
    // A filled rectangular grid of plain square tiles, one or more floors.
    private static BuildingDef Grid(int wide, int deep, int floors = 1)
    {
        var tiles = new List<TileDef>();
        for (int f = 0; f < floors; f++)
            for (int x = 0; x < wide; x++)
                for (int z = 0; z < deep; z++)
                    tiles.Add(new TileDef { gridX = x, gridZ = z, floor = f, shapeId = "square" });
        return new BuildingDef { gridCellSize = 4f, floors = floors, tiles = tiles };
    }

    // Grid-corner coord of tile corner i (mirrors TileDeformField.CornerGrid).
    private static Vector2Int CornerGrid(TileDef t, int i) =>
        new Vector2Int((i == 1 || i == 2) ? t.gridX + 1 : t.gridX,
                       (i == 2 || i == 3) ? t.gridZ + 1 : t.gridZ);

    [Test]
    public void CornerBend_SharedCorners_MatchExactly_NoGaps()
    {
        var b = Grid(5, 3, floors: 2);
        TileDeformField.ApplyCornerBend(b, TileDeformField.Corner.NE, 45f);

        // Collect the offset every tile assigns to each grid corner it touches; all must agree.
        var seenX = new Dictionary<Vector2Int, float>();
        var seenZ = new Dictionary<Vector2Int, float>();
        foreach (var t in b.tiles)
        {
            Assert.IsNotNull(t.deform, "every tile should carry a deform after a bend");
            for (int i = 0; i < 4; i++)
            {
                var g = CornerGrid(t, i);
                if (seenX.TryGetValue(g, out float px))
                {
                    Assert.AreEqual(px, t.deform.dx[i], 1e-5f, $"dx mismatch at shared corner {g}");
                    Assert.AreEqual(seenZ[g], t.deform.dz[i], 1e-5f, $"dz mismatch at shared corner {g}");
                }
                else { seenX[g] = t.deform.dx[i]; seenZ[g] = t.deform.dz[i]; }
            }
        }
    }

    [Test]
    public void CornerBend_ConstantSkew_StraightSlantToFarEnd()
    {
        // 1 wide × 6 deep. Bend SW: the west wall (x=0) tilts at a constant angle along Z; the east
        // wall (x=1) is the straight anchor.
        var b = Grid(1, 6);
        TileDeformField.ApplyCornerBend(b, TileDeformField.Corner.SW, 45f); // tan45 = 1

        foreach (var t in b.tiles)
        {
            // West edge (corners 0 and 3) is displaced; the displacement is linear in Z distance
            // from the south wall — a straight slant, NOT decaying.
            float expectNorthWest = -1f * (t.gridZ + 1); // corner 3 sits at grid z = gridZ+1, tan45=1
            float expectSouthWest = -1f * (t.gridZ);     // corner 0 sits at grid z = gridZ
            Assert.AreEqual(expectSouthWest, t.deform.dx[0], 1e-4f, "west wall slant must be constant-slope");
            Assert.AreEqual(expectNorthWest, t.deform.dx[3], 1e-4f, "west wall slant must reach the far end");
            // East edge (corners 1, 2) is the anchor — stays put.
            Assert.AreEqual(0f, t.deform.dx[1], 1e-4f, "east anchor edge stays straight");
            Assert.AreEqual(0f, t.deform.dx[2], 1e-4f, "east anchor edge stays straight");
        }
    }

    [Test]
    public void SlopedEdge_OnlyTopFloor_IsRaised()
    {
        var b = Grid(4, 4, floors: 3);
        TileDeformField.ApplySlopedEdge(b, TileDeformField.Edge.North, 1f, 2f);

        bool anyTopRaised = false;
        foreach (var t in b.tiles)
        {
            if (t.floor == 2) { if (t.deform != null) anyTopRaised |= AnyNonZero(t.deform.dyTop); }
            else Assert.IsTrue(t.deform == null || !AnyNonZero(t.deform.dyTop),
                               "only the top floor should slope, to keep interior floor seams flat");
        }
        Assert.IsTrue(anyTopRaised, "the top floor's north edge should be raised");
    }

    [Test]
    public void Converter_EmitsDeform_WhenCornerAnglesPresent()
    {
        var data = new FullTerrainData
        {
            site_scale = new SiteScale
            {
                normalized_canvas = new[] { 0, 0, 1000, 1000 },
                site_width_ft = 300f, site_height_ft = 300f, scale_note = "test"
            },
            terrain_zones = new List<TerrainZone>(),
            generated_objects = new List<GeneratedObject>(),
            prefab_instances = new List<PrefabInstance>(),
            generated_buildings = new List<GeneratedBuilding>
            {
                new GeneratedBuilding
                {
                    area_name = "Wedge",
                    bounding_box = new[] { 200, 100, 600, 500 },
                    center_point = new[] { 400, 300 },
                    floors = 2,
                    corner_angles = new[] { 0f, 0f, 45f, 0f },
                }
            }
        };

        var result = LayoutConverter.Convert(data, "Bend Env");
        Assert.AreEqual(1, result.Buildings.Count);
        Assert.IsTrue(result.Buildings[0].tiles.Exists(t => t.deform != null),
                      "a generated building with corner_angles should produce deformed tiles");
    }

    [Test]
    public void BuildDeformedMesh_Has6Submeshes()
    {
        var d = new TileDeform { dx = new float[4], dz = new float[4], dyTop = new float[4] };
        var mesh = TileDeformField.BuildDeformedMesh(d, 4f);
        Assert.AreEqual(6, mesh.subMeshCount);
        Assert.AreEqual(24, mesh.vertexCount);
    }

    [Test]
    public void SampleOffset_AtCorners_ReturnsExactCornerOffsets()
    {
        var d = new TileDeform { dx = new[] { 1f, 2f, 3f, 4f },
                                 dz = new[] { 5f, 6f, 7f, 8f },
                                 dyTop = new float[4] };
        var uv = new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) }; // corner order 0..3
        for (int i = 0; i < 4; i++)
        {
            TileDeformField.SampleOffset(d, uv[i].Item1, uv[i].Item2, out float ox, out float oz, out _);
            Assert.AreEqual(d.dx[i], ox, 1e-5f, $"corner {i} dx");
            Assert.AreEqual(d.dz[i], oz, 1e-5f, $"corner {i} dz");
        }
    }

    [Test]
    public void Warp_SharedEdge_IsSeamless_AcrossTiles()
    {
        // Two tiles side-by-side on X share a vertical edge. After a corner bend, the warp must place
        // both tiles' geometry at IDENTICAL world positions along that shared edge — for any vertex,
        // not just the grid-corner posts — or non-box shapes would tear at the seam.
        var b = Grid(2, 1);
        TileDeformField.ApplyCornerBend(b, TileDeformField.Corner.SW, 30f);
        const float cs = 4f, h = 2f;
        var left  = b.tiles.Find(t => t.gridX == 0);
        var right = b.tiles.Find(t => t.gridX == 1);
        var leftCenter  = new Vector3(0.5f * cs, 0f, 0.5f * cs);
        var rightCenter = new Vector3(1.5f * cs, 0f, 0.5f * cs);

        for (float worldZ = 0f; worldZ <= cs + 1e-3f; worldZ += 0.5f)
        {
            float zLocal = worldZ - 0.5f * cs;                 // same cell-local Z (shared center.z)
            var pa = leftCenter  + TileDeformField.WarpVertex(left.deform,  new Vector3(+h, 0f, zLocal), cs);
            var pc = rightCenter + TileDeformField.WarpVertex(right.deform, new Vector3(-h, 0f, zLocal), cs);
            Assert.Less((pa - pc).magnitude, 1e-4f, $"seam gap at world z={worldZ}");
        }
    }

    private static bool AnyNonZero(float[] a)
    {
        if (a == null) return false;
        foreach (var v in a) if (Mathf.Abs(v) > 1e-5f) return true;
        return false;
    }
}
