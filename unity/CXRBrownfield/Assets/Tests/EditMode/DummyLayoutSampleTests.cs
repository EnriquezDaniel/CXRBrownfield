using NUnit.Framework;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

// Pins the bundled local sample (Assets/Resources/DummyLayout.json), the hand-authored reading of
// samples/HomeLongfellowSample1.jpg that the Generate rail's "Local sample" button loads offline.
// It is both the demo scene and the reference for what a correct layout response looks like, so it
// has to keep satisfying prompts/site_parsing.md AND keep converting to the geometry it was drawn
// as. These asserts fail loudly if either the sample or LayoutConverter drifts.
[TestFixture]
public class DummyLayoutSampleTests
{
    private const float SITE_WIDTH_FT  = 286f;   // canvas index [0] / sketch vertical / Unity X
    private const float SITE_HEIGHT_FT = 133f;   // canvas index [1] / sketch across   / Unity Z

    private static FullTerrainData Load()
    {
        var asset = Resources.Load<TextAsset>("DummyLayout");
        Assert.IsNotNull(asset, "Assets/Resources/DummyLayout.json is missing.");
        var data = JsonConvert.DeserializeObject<FullTerrainData>(asset.text);
        Assert.IsNotNull(data, "DummyLayout.json did not deserialize into FullTerrainData.");
        return data;
    }

    [Test]
    public void Sample_HasAllSevenCategories()
    {
        var d = Load();
        Assert.IsNotNull(d.site_scale,          "site_scale");
        Assert.IsNotNull(d.terrain_zones,       "terrain_zones");
        Assert.IsNotNull(d.paths,               "paths");
        Assert.IsNotNull(d.fences,              "fences");
        Assert.IsNotNull(d.generated_buildings, "generated_buildings");
        Assert.IsNotNull(d.generated_objects,   "generated_objects");
        Assert.IsNotNull(d.prefab_instances,    "prefab_instances");

        Assert.AreEqual(SITE_WIDTH_FT,  d.site_scale.site_width_ft,  0.001f);
        Assert.AreEqual(SITE_HEIGHT_FT, d.site_scale.site_height_ft, 0.001f);
        CollectionAssert.AreEqual(new[] { 0, 0, 1000, 1000 }, d.site_scale.normalized_canvas);
    }

    [Test]
    public void Sample_LotBoundary_IsAUsableParcel()
    {
        var b = Load().site_scale.lot_boundary;
        Assert.IsNotNull(b, "lot_boundary must never be null (site_parsing.md).");
        Assert.AreEqual(5, b.Length, "The traced Longfellow wedge has 5 vertices.");
        foreach (var v in b)
        {
            Assert.AreEqual(2, v.Length);
            foreach (float c in v)
            {
                Assert.GreaterOrEqual(c, 0f);
                Assert.LessOrEqual(c, 1000f);
            }
        }
    }

    [Test]
    public void Sample_EveryPlacement_FallsInsideTheLot()
    {
        var d    = Load();
        var poly = d.site_scale.lot_boundary;

        foreach (var z in d.terrain_zones)
            AssertBoxInside(poly, z.bounding_box, z.area_name);

        foreach (var g in d.generated_buildings)
        {
            AssertBoxInside(poly, g.bounding_box, g.area_name);
            AssertInside(poly, g.center_point[0], g.center_point[1], g.area_name + " center");
            // site_parsing.md: center_point must be the exact center of the bounding box.
            Assert.AreEqual((g.bounding_box[0] + g.bounding_box[2]) / 2, g.center_point[0], g.area_name + " center y");
            Assert.AreEqual((g.bounding_box[1] + g.bounding_box[3]) / 2, g.center_point[1], g.area_name + " center x");
        }

        foreach (var p in d.paths)
            foreach (var pt in p.points) AssertInside(poly, pt[0], pt[1], p.area_name);

        foreach (var f in d.fences)
            foreach (var pt in f.points) AssertInside(poly, pt[0], pt[1], f.area_name);

        foreach (var pi in d.prefab_instances)
        {
            AssertInside(poly, pi.center_point[0], pi.center_point[1], pi.area_name);
            AssertBoxInside(poly, pi.footprint_box, pi.area_name + " footprint");
        }
    }

    [Test]
    public void Sample_UsesTheCanonicalPathAndFenceVocabulary()
    {
        var d = Load();
        Assert.AreEqual(1, d.paths.Count);
        Assert.AreEqual("pavement_light", d.paths[0].path_material);
        Assert.AreEqual(1, d.fences.Count);
        Assert.AreEqual("chain_link", d.fences[0].fence_type);
        // Omitted in the JSON so the converter derives them; the field initializers must survive.
        Assert.Less(d.paths[0].smoothing,  0f);
        Assert.Less(d.fences[0].smoothing, 0f);
    }

    [Test]
    public void Sample_Converts_ToTheDrawnScene()
    {
        var result = LayoutConverter.Convert(Load(), "Home Longfellow Sample");
        var env    = result.Environment;

        Assert.AreEqual(2, result.Buildings.Count,      "two apartment wings");
        Assert.AreEqual(2, env.buildingInstances.Count);
        Assert.AreEqual(2, env.site.terrainZones.Count, "soccer pitch + north open area");
        Assert.AreEqual(1, env.site.paths.Count,        "the walkway");
        Assert.AreEqual(1, env.site.fences.Count,       "the pitch enclosure");
        Assert.AreEqual(1, env.objectInstances.Count,   "one tree, nothing invented");

        var west  = result.Buildings.Find(b => b.name == "Apt West Wing");
        var south = result.Buildings.Find(b => b.name == "Apt South Wing");
        Assert.IsNotNull(west);
        Assert.IsNotNull(south);
        Assert.AreEqual(24, west.tiles.Count,  "4 x 2 cells over 3 floors");
        Assert.AreEqual(54, south.tiles.Count, "3 x 6 cells over 3 floors");
        Assert.AreEqual(3, west.floors);
        Assert.AreEqual(3, south.floors);

        // Terrain hugs the parcel: bbox extent + LayoutConverter's 2 m margin.
        Assert.AreEqual(89.17f, env.site.terrainSize[0], 0.5f);
        Assert.AreEqual(42.38f, env.site.terrainSize[1], 0.5f);
        Assert.AreEqual("water", env.site.outsideTerrainType);
        Assert.IsNotNull(env.site.lotBoundary);
        Assert.AreEqual(5, env.site.lotBoundary.Length);

        Assert.AreEqual("tree", env.objectInstances[0].prefabType);
    }

    [Test]
    public void Sample_BuildingFootprints_AreCentredOnTheirCenterPoint()
    {
        var d      = Load();
        var result = LayoutConverter.Convert(d, "fit");
        float terrainW = SITE_WIDTH_FT  * AuthoringConventions.FT_TO_M;
        float terrainL = SITE_HEIGHT_FT * AuthoringConventions.FT_TO_M;

        var defsById = new Dictionary<string, BuildingDef>();
        foreach (var b in result.Buildings) defsById[b.id] = b;

        for (int i = 0; i < d.generated_buildings.Count; i++)
        {
            var gb   = d.generated_buildings[i];
            var inst = result.Environment.buildingInstances[i];
            var def  = defsById[inst.buildingId];

            // Tile grids are corner-pivoted, so the instance sits half a footprint back from the
            // drawn centre; adding the half-extent must land back on center_point.
            int maxX = 0, maxZ = 0;
            foreach (var t in def.tiles)
            {
                if (t.gridX > maxX) maxX = t.gridX;
                if (t.gridZ > maxZ) maxZ = t.gridZ;
            }
            float halfX = (maxX + 1) * def.gridCellSize * 0.5f;
            float halfZ = (maxZ + 1) * def.gridCellSize * 0.5f;

            float expectedX = gb.center_point[0] / 1000f * terrainW;
            float expectedZ = gb.center_point[1] / 1000f * terrainL;

            Assert.AreEqual(expectedX, inst.position[0] + halfX, 0.01f, gb.area_name + " centre X");
            Assert.AreEqual(expectedZ, inst.position[2] + halfZ, 0.01f, gb.area_name + " centre Z");
        }
    }

    // Orientation lock. Viewed top-down (+X right, +Z up) the sketch must read: wedge tip at the
    // EAST end, walkway a north-south strip at the WEST end, the fenced pitch along the SOUTH edge
    // hugging the lot's long straight side, Apt wings along the NORTH edge.
    //
    // The sample's canvas coordinates carry a 180 degree rotation against the raw LayoutConverter
    // mapping (BOTH indices flipped). That is a rotation, not a reflection — flipping only one index
    // mirrors the plan and puts the pitch on the wrong side of the lot. Do not "simplify" it to one.
    [Test]
    public void Sample_Orientation_TipEast_AptNorth()
    {
        var env = LayoutConverter.Convert(Load(), "orient").Environment;

        var pitch = env.site.terrainZones.Find(z => z.terrainType == "grass");
        var plaza = env.site.terrainZones.Find(z => z.terrainType == "plaza");
        var tree  = env.objectInstances[0];
        var walk  = env.site.paths[0];

        float pitchX = (pitch.rectMeters[0] + pitch.rectMeters[2]) * 0.5f;
        float pitchZ = (pitch.rectMeters[1] + pitch.rectMeters[3]) * 0.5f;
        float plazaX = (plaza.rectMeters[0] + plaza.rectMeters[2]) * 0.5f;

        // Tip end (tree, then the open area) is east of the pitch.
        Assert.Greater(tree.position[0], plazaX, "tree must sit east of the open area");
        Assert.Greater(plazaX, pitchX,           "open area must sit east of the pitch");

        // Walkway runs north-south at the west end, west of every building.
        foreach (var b in env.buildingInstances)
            Assert.Less(walk.points[0][0], b.position[0], "walkway must be west of the buildings");
        Assert.Greater(Mathf.Abs(walk.points[0][1] - walk.points[2][1]),
                       Mathf.Abs(walk.points[0][0] - walk.points[2][0]),
                       "walkway must run north-south, not east-west");

        // The fenced pitch hugs the lot's long south edge. This is the assert that fails if the
        // sample is ever mirrored on a single axis instead of rotated.
        float lotMinZ = float.MaxValue, lotMaxZ = float.MinValue;
        foreach (var v in env.site.lotBoundary)
        {
            if (v[1] < lotMinZ) lotMinZ = v[1];
            if (v[1] > lotMaxZ) lotMaxZ = v[1];
        }
        Assert.Less(pitchZ, (lotMinZ + lotMaxZ) * 0.5f, "the fenced pitch belongs on the south side");

        // The L's upright arm runs alongside the pitch on its north side. (The South Wing is the
        // L's wide bottom arm and legitimately straddles the mid-line, so it is not asserted here.)
        var westInst = env.buildingInstances[0];
        var westDef  = null as BuildingDef;
        foreach (var b in LayoutConverter.Convert(Load(), "orient").Buildings)
            if (b.name == "Apt West Wing") westDef = b;
        Assert.IsNotNull(westDef);
        float westCentreZ = westInst.position[2] + EnvironmentScale.BuildingFootprint(westDef, westInst).y * 0.5f;
        Assert.Greater(westCentreZ, pitchZ, "the west wing runs along the pitch's north side");
    }

    // The Generate rail's "Local sample" button routes the converted env through
    // SiteFit.ProjectIntoSite when a site is targeted, so the sample lands in the drawn plot
    // instead of at the origin. Boundary below is the real "Site 1" drawn in the saved
    // "Home Longfellow Site" record (bbox 117.3..204.4 x 84.1..124.5 m).
    [Test]
    public void Sample_ProjectedIntoASite_LandsInsideThatSite()
    {
        var boundary = new[] {
            new[] { 204.441f,   84.08857f },
            new[] { 143.595947f, 124.505775f },
            new[] { 117.716736f, 84.4015656f },
            new[] { 117.2868f,  101.24382f },
        };
        Assert.IsTrue(SiteFit.BoundaryBounds(boundary, out float minX, out float minZ, out float maxX, out float maxZ));

        var env = LayoutConverter.Convert(Load(), "fill").Environment;
        Assert.IsTrue(SiteFit.ProjectIntoSite(env, boundary), "the sample must fit this site");

        // Terrain now spans exactly the site bbox...
        Assert.AreEqual(maxX - minX, env.site.terrainSize[0], 0.01f);
        Assert.AreEqual(maxZ - minZ, env.site.terrainSize[1], 0.01f);

        // ...AND sits on the site's corner rather than the world origin. Without this the ground
        // is the right size in the wrong place and the whole layout floats beside it.
        Assert.IsNotNull(env.site.terrainOrigin, "ProjectIntoSite must seed terrainOrigin");
        Assert.AreEqual(minX, env.site.terrainOrigin[0], 0.01f);
        Assert.AreEqual(minZ, env.site.terrainOrigin[1], 0.01f);

        // And every placement moved with it, out of the origin corner and into the plot.
        foreach (var b in env.buildingInstances) AssertInSiteBounds(b.position, minX, minZ, maxX, maxZ, "building");
        foreach (var o in env.objectInstances)   AssertInSiteBounds(o.position, minX, minZ, maxX, maxZ, "object");
        foreach (var pt in env.site.paths[0].points)
        {
            Assert.GreaterOrEqual(pt[0], minX - 1f);
            Assert.LessOrEqual(pt[0], maxX + 1f);
        }
        foreach (var v in env.site.lotBoundary) AssertInSiteBounds(new[] { v[0], 0f, v[1] }, minX, minZ, maxX, maxZ, "lot vertex");
        // The pitch fence has to travel with the pitch, not stay at the origin.
        foreach (var v in env.site.fences[0].points) AssertInSiteBounds(new[] { v[0], 0f, v[1] }, minX, minZ, maxX, maxZ, "fence point");
        foreach (var z in env.site.terrainZones)
        {
            AssertInSiteBounds(new[] { z.rectMeters[0], 0f, z.rectMeters[1] }, minX, minZ, maxX, maxZ, "zone min");
            AssertInSiteBounds(new[] { z.rectMeters[2], 0f, z.rectMeters[3] }, minX, minZ, maxX, maxZ, "zone max");
        }
    }

    private static void AssertInSiteBounds(float[] pos, float minX, float minZ, float maxX, float maxZ, string what)
    {
        Assert.GreaterOrEqual(pos[0], minX - 1f, what + " X below the site");
        Assert.LessOrEqual(pos[0], maxX + 1f, what + " X past the site");
        Assert.GreaterOrEqual(pos[2], minZ - 1f, what + " Z below the site");
        Assert.LessOrEqual(pos[2], maxZ + 1f, what + " Z past the site");
    }

    // --- helpers ---

    private static void AssertBoxInside(float[][] poly, int[] box, string label)
    {
        AssertInside(poly, box[0], box[1], label + " [ymin,xmin]");
        AssertInside(poly, box[0], box[3], label + " [ymin,xmax]");
        AssertInside(poly, box[2], box[1], label + " [ymax,xmin]");
        AssertInside(poly, box[2], box[3], label + " [ymax,xmax]");
    }

    // Canvas coords are [y, x] with index [0] -> X and index [1] -> Z, the same transpose
    // LayoutConverter applies, so EnvironmentScale.PointInPolygon reads them directly.
    private static void AssertInside(float[][] poly, float c0, float c1, string label)
    {
        Assert.IsTrue(EnvironmentScale.PointInPolygon(c0, c1, poly),
                      label + " (" + c0 + ", " + c1 + ") falls outside lot_boundary.");
    }
}
