using NUnit.Framework;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

// Pins the bundled local sample (Assets/Resources/DummyLayout.json), the layout model's reading of
// samples/WestchesterSample1.jpg that the Generate rail's "Local sample" button loads offline. It
// started as the output of POST /api/layout/generate for the `westchester_avenue` preset (459 x 66
// ft strip, north at the top of the sketch) with designer notes, archived raw as
// layouts/WestchesterSample1.json, then hand-trimmed: the street trees and benches the notes had
// invited (the sketch draws none) were removed, and the two block boxes were resized to whole
// 4 m tiles so they meet flush instead of overlapping by a rounded tile. It is both the demo scene
// and the reference for what a correct layout response looks like, so it has to keep satisfying
// prompts/site_parsing.md AND keep converting to the geometry it was drawn as. These asserts fail
// loudly if either the sample or LayoutConverter drifts.
//
// The strip, read north (top of sketch, +X) to south: two fenced pickleball courts, a 7-floor
// "Movie Theater" block, a 7-floor "Ice Cream Shop" block flush against it, with a sidewalk running
// the full length along the west edge (+Z). Nothing else: no props the sketch did not draw.
// Facade styles were added by hand afterwards (shop "A", theater "B") to show the style letters
// the notes can assign; BuildingStylePalette maps them to wall materials.
[TestFixture]
public class DummyLayoutSampleTests
{
    private const float SITE_WIDTH_FT  = 459f;   // canvas index [0] / sketch vertical / Unity X
    private const float SITE_HEIGHT_FT = 66f;    // canvas index [1] / sketch across   / Unity Z

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
    public void Sample_LotBoundary_IsTheFullCanvasStrip()
    {
        var b = Load().site_scale.lot_boundary;
        Assert.IsNotNull(b, "lot_boundary must never be null (site_parsing.md).");
        Assert.AreEqual(4, b.Length, "The westchester_avenue preset is a full-canvas rectangle.");
        foreach (var v in b)
        {
            Assert.AreEqual(2, v.Length);
            foreach (float c in v)
            {
                Assert.AreEqual(0f, c % 1000f, "the preset's corners sit on the canvas edges");
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
        Assert.AreEqual(2, d.paths.Count, "the street sidewalk and the walkway to the courts");
        foreach (var p in d.paths)
        {
            Assert.AreEqual("pavement_light", p.path_material, p.area_name);
            // Omitted in the JSON so the converter derives it; the field initializer must survive.
            Assert.Less(p.smoothing, 0f, p.area_name + " smoothing");
        }
        Assert.AreEqual(1, d.fences.Count, "one enclosure around both courts");
        Assert.AreEqual("chain_link", d.fences[0].fence_type);
        Assert.AreEqual(10f, d.fences[0].height_ft, 0.001f);
        Assert.Less(d.fences[0].smoothing, 0f);
    }

    // The brief: one 7-floor mixed-use building, expressed as two touching generated_buildings so
    // the ground-floor program stays named per block. No box primitives.
    [Test]
    public void Sample_ReadsTheMixedUseProgram()
    {
        var d = Load();
        Assert.AreEqual(2, d.generated_buildings.Count);
        Assert.AreEqual(0, d.generated_objects.Count, "no massing boxes; both blocks are tile buildings");

        var theater = d.generated_buildings.Find(b => b.area_name == "Movie Theater");
        var shop    = d.generated_buildings.Find(b => b.area_name == "Ice Cream Shop");
        Assert.IsNotNull(theater);
        Assert.IsNotNull(shop);
        Assert.AreEqual(7, theater.floors);
        Assert.AreEqual(7, shop.floors);
        Assert.AreEqual(0, theater.rotation_y_deg);
        Assert.AreEqual(0, shop.rotation_y_deg);

        // Facade styles, as the notes assigned them: the shop is style A, the theater style B.
        Assert.AreEqual("A", shop.style);
        Assert.AreEqual("B", theater.style);

        // Sign words: one uppercase word per named building (site_parsing.md, "sign").
        Assert.AreEqual("ICECREAM", shop.sign);
        Assert.AreEqual("THEATER", theater.sign);

        // Theater in the middle, shop at the south end (high y), sharing a wall on the canvas.
        Assert.Less(theater.bounding_box[0], shop.bounding_box[0], "theater is north of the shop");
        Assert.AreEqual(theater.bounding_box[2], shop.bounding_box[0], "the blocks touch");
        Assert.AreEqual(theater.bounding_box[1], shop.bounding_box[1], "same west face");
        Assert.AreEqual(theater.bounding_box[3], shop.bounding_box[3], "same east face");

        // Courts are asphalt zones north of both blocks.
        var courts = d.terrain_zones.FindAll(z => z.terrain_type == "asphalt");
        Assert.AreEqual(2, courts.Count, "two pickleball courts");
        foreach (var c in courts)
            Assert.Less(c.bounding_box[2], theater.bounding_box[0], c.area_name + " sits north of the building");
    }

    [Test]
    public void Sample_Converts_ToTheDrawnScene()
    {
        var result = LayoutConverter.Convert(Load(), "Westchester Sample");
        var env    = result.Environment;

        Assert.AreEqual(2, result.Buildings.Count,      "theater + ice cream blocks");
        Assert.AreEqual(2, env.buildingInstances.Count);
        Assert.AreEqual(5, env.site.terrainZones.Count, "sidewalk ground, courts ground, two courts, buffer grass");
        Assert.AreEqual(2, env.site.paths.Count,        "sidewalk + courts walkway");
        Assert.AreEqual(1, env.site.fences.Count,       "the court enclosure");
        Assert.AreEqual(0, env.objectInstances.Count,   "the sketch draws no props, so none are placed");

        var theater = result.Buildings.Find(b => b.name == "Movie Theater");
        var shop    = result.Buildings.Find(b => b.name == "Ice Cream Shop");
        Assert.IsNotNull(theater);
        Assert.IsNotNull(shop);
        Assert.AreEqual(504, theater.tiles.Count, "18 x 4 cells over 7 floors");
        Assert.AreEqual(140, shop.tiles.Count,    "5 x 4 cells over 7 floors");
        Assert.AreEqual(7, theater.floors);
        Assert.AreEqual(7, shop.floors);
        Assert.AreEqual("A", shop.style,    "style letter carried onto the def, resolved at spawn");
        Assert.AreEqual("B", theater.style);

        // Signs belong to the placed instances and face world east. Both blocks are drawn
        // axis-aligned, so they carry a 180 yaw and their local north (+Z) wall faces east (-Z).
        // Each north side is wide enough (18 and 5 tiles), so the plate lands on the centred pair
        // of ground-floor tiles.
        var shopInst    = env.buildingInstances.Find(i => i.buildingId == shop.id);
        var theaterInst = env.buildingInstances.Find(i => i.buildingId == theater.id);
        Assert.AreEqual("ICECREAM", shopInst.signText);
        Assert.AreEqual("THEATER", theaterInst.signText);
        Assert.AreEqual("east", shopInst.signCompass);
        Assert.IsNull(shop.signText, "the def carries no sign of its own");
        var shopSpec    = BuildingSigns.SpecFor(shopInst, shop);
        var theaterSpec = BuildingSigns.SpecFor(theaterInst, theater);
        Assert.AreEqual("north", shopSpec.face);
        Assert.AreEqual("north", theaterSpec.face);
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(shop, shopSpec, shop.gridCellSize, null, out var shopSign));
        Assert.AreEqual(BuildingSigns.Skip.None, BuildingSigns.TryPlace(theater, theaterSpec, theater.gridCellSize, null, out var theaterSign));
        Assert.AreEqual(1, shopSign.tileA.gridX);     Assert.AreEqual(2, shopSign.tileB.gridX);
        Assert.AreEqual(8, theaterSign.tileA.gridX);  Assert.AreEqual(9, theaterSign.tileB.gridX);
        Assert.AreEqual(3, shopSign.tileA.gridZ, "the north row of a 4-deep grid");
        Assert.AreEqual(0, shopSign.floor);

        // Terrain hugs the parcel: 459 x 66 ft in metres + LayoutConverter's 2 m margin.
        Assert.AreEqual(141.90f, env.site.terrainSize[0], 0.5f);
        Assert.AreEqual(22.12f,  env.site.terrainSize[1], 0.5f);
        Assert.AreEqual("water", env.site.outsideTerrainType);
        Assert.IsNotNull(env.site.lotBoundary);
        Assert.AreEqual(4, env.site.lotBoundary.Length);

        // Fence height came through in metres (10 ft).
        Assert.AreEqual(10f * AuthoringConventions.FT_TO_M, env.site.fences[0].height, 0.01f);
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
            // drawn centre along its own yawed axes; adding the yawed half-extent must land back on
            // center_point (turned half a turn: canvas 0 is the world max on each axis).
            int maxX = 0, maxZ = 0;
            foreach (var t in def.tiles)
            {
                if (t.gridX > maxX) maxX = t.gridX;
                if (t.gridZ > maxZ) maxZ = t.gridZ;
            }
            var half   = new Vector3((maxX + 1) * def.gridCellSize * 0.5f, 0f, (maxZ + 1) * def.gridCellSize * 0.5f);
            var centre = new Vector3(inst.position[0], 0f, inst.position[2]) + Quaternion.Euler(0f, inst.rotationY, 0f) * half;

            float expectedX = (1f - gb.center_point[0] / 1000f) * terrainW;
            float expectedZ = (1f - gb.center_point[1] / 1000f) * terrainL;

            Assert.AreEqual(expectedX, centre.x, 0.01f, gb.area_name + " centre X");
            Assert.AreEqual(expectedZ, centre.z, 0.01f, gb.area_name + " centre Z");
        }
    }

    // Orientation lock. Canvas index [0] (down the sketch, north at the top) lands on Unity X and
    // index [1] (across) on Unity Z, turned half a turn so north is +X and west is +Z. Viewed
    // top-down with +X up the screen the strip reads as drawn: courts at the top (high X), theater
    // in the middle, shop at the bottom closing the lot at X = 0, sidewalk a long strip along the
    // high-Z (west, screen-left) edge, west of every building.
    [Test]
    public void Sample_Orientation_CourtsNorth_SidewalkWest()
    {
        var result = LayoutConverter.Convert(Load(), "orient");
        var env    = result.Environment;

        var courts = env.site.terrainZones.FindAll(z => z.terrainType == "asphalt");
        Assert.AreEqual(2, courts.Count);

        var defsById = new Dictionary<string, BuildingDef>();
        foreach (var b in result.Buildings) defsById[b.id] = b;
        BuildingInstance theaterInst = null, shopInst = null;
        BuildingDef theaterDef = null, shopDef = null;
        foreach (var inst in env.buildingInstances)
        {
            var def = defsById[inst.buildingId];
            if (def.name == "Movie Theater") { theaterInst = inst; theaterDef = def; }
            if (def.name == "Ice Cream Shop") { shopInst = inst; shopDef = def; }
        }
        Assert.IsNotNull(theaterInst);
        Assert.IsNotNull(shopInst);

        WorldExtents(theaterInst, theaterDef, out float thMinX, out float thMaxX, out float thMinZ, out float thMaxZ);
        WorldExtents(shopInst, shopDef, out float shMinX, out float shMaxX, out _, out _);
        float lotMinX = float.MaxValue, lotMaxX = float.MinValue;
        foreach (var v in env.site.lotBoundary) { lotMinX = Mathf.Min(lotMinX, v[0]); lotMaxX = Mathf.Max(lotMaxX, v[0]); }

        // Courts north (high X) of the theater, theater north of the shop.
        foreach (var c in courts)
            Assert.Greater(c.rectMeters[0], thMaxX, "courts sit north of the theater");
        Assert.Greater(thMinX, shMinX, "theater is north of the shop");

        // The two blocks meet flush: their boxes were sized to whole 4 m tiles, so after rounding the
        // seam is a few centimetres, never a shared tile. The shop closes the south end of the lot.
        Assert.Less(Mathf.Abs(thMinX - shMaxX), 0.1f, "theater and shop share a wall, no overlapping tile");
        Assert.Less(shMinX - lotMinX, 2f, "the shop reaches the south end of the strip");

        // Sidewalk: the longest path, running north-south (along X) along the west (high Z) edge.
        PathDef walk = null;
        foreach (var p in env.site.paths)
            if (walk == null || p.points.Length > walk.points.Length) walk = p;
        Assert.IsNotNull(walk);
        var first = walk.points[0];
        var last  = walk.points[walk.points.Length - 1];
        Assert.Greater(Mathf.Abs(last[0] - first[0]), Mathf.Abs(last[1] - first[1]),
                       "sidewalk must run north-south, not east-west");
        Assert.Greater(Mathf.Abs(last[0] - first[0]), 0.9f * (lotMaxX - lotMinX), "sidewalk runs the full length");
        Assert.Greater(first[1], thMaxZ, "sidewalk must be west (+Z) of the theater");
        Assert.Greater(first[1], shMaxZ(shopInst, shopDef), "sidewalk must be west (+Z) of the shop");
        // Trees and benches line the sidewalk on the west side of the strip. (The model put the
        // trees on the building's west face, so compare against the block's centre line, not its wall.)
        float theaterCentreZ = (thMinZ + thMaxZ) * 0.5f;
        foreach (var o in env.objectInstances)
            Assert.Greater(o.position[2], theaterCentreZ, "trees and benches sit on the sidewalk side of the building");
    }

    private static float shMaxZ(BuildingInstance inst, BuildingDef def)
    {
        WorldExtents(inst, def, out _, out _, out _, out float maxZ);
        return maxZ;
    }

    // World-space XZ extents of a corner-pivoted tile grid under its instance yaw.
    private static void WorldExtents(BuildingInstance inst, BuildingDef def,
                                     out float minX, out float maxX, out float minZ, out float maxZ)
    {
        var fp = EnvironmentScale.BuildingFootprint(def, inst);
        var far = Quaternion.Euler(0f, inst.rotationY, 0f) * new Vector3(fp.x, 0f, fp.y);
        minX = Mathf.Min(inst.position[0], inst.position[0] + far.x);
        maxX = Mathf.Max(inst.position[0], inst.position[0] + far.x);
        minZ = Mathf.Min(inst.position[2], inst.position[2] + far.z);
        maxZ = Mathf.Max(inst.position[2], inst.position[2] + far.z);
    }

    // The Generate rail's "Local sample" button routes the converted env through
    // SiteFit.ProjectIntoSite when a site is targeted, so the sample lands in the drawn plot
    // instead of at the origin. Boundary below is the real "Site A" drawn in the saved
    // "Westchester Bronx River Site" record (bbox 105.5..219.5 x 163.2..182.6 m). It was drawn a
    // little short of the real parcel (114 x 19.4 m against 139.9 x 20.1 m), so the fit shrinks
    // the sample along X and barely along Z rather than being a pure translation.
    [Test]
    public void Sample_ProjectedIntoASite_LandsInsideThatSite()
    {
        var boundary = new[] {
            new[] { 105.5f, 163.2f },
            new[] { 219.5f, 163.2f },
            new[] { 219.5f, 182.6f },
            new[] { 105.5f, 182.6f },
        };
        Assert.IsTrue(SiteFit.BoundaryBounds(boundary, out float minX, out float minZ, out float maxX, out float maxZ));

        var env = LayoutConverter.Convert(Load(), "fill").Environment;
        float terrW = env.site.terrainSize[0], terrL = env.site.terrainSize[1];

        // The fit maps the sample's OWN parcel bbox onto the site bbox (by lotBoundary, not terrain).
        Assert.IsTrue(SiteFit.ChildFrame(env, out float cMinX, out float cMinZ, out float cMaxX, out float cMaxZ));
        Assert.IsTrue(SiteFit.TryComputeFit(boundary, cMinX, cMinZ, cMaxX, cMaxZ, out var fit));
        Assert.That(fit.scaleX, Is.InRange(0.78f, 0.86f), "114 m site over a 139.9 m parcel");
        Assert.That(fit.scaleZ, Is.InRange(0.93f, 1.0f),  "19.4 m site over a 20.1 m parcel");

        Assert.IsTrue(SiteFit.ProjectIntoSite(env, boundary), "the sample must fit this site");

        // Terrain keeps its 2 m margin past the parcel (scaled like everything else), so it is a
        // touch larger than the site bbox rather than squeezed into it...
        Assert.AreEqual(terrW * fit.scaleX, env.site.terrainSize[0], 0.01f);
        Assert.AreEqual(terrL * fit.scaleZ, env.site.terrainSize[1], 0.01f);
        Assert.Greater(env.site.terrainSize[0], maxX - minX);
        Assert.Greater(env.site.terrainSize[1], maxZ - minZ);

        // ...AND sits on the site's corner rather than the world origin. Without this the ground
        // is the right size in the wrong place and the whole layout floats beside it.
        Assert.IsNotNull(env.site.terrainOrigin, "ProjectIntoSite must seed terrainOrigin");
        Assert.AreEqual(fit.offsetX, env.site.terrainOrigin[0], 0.01f);
        Assert.AreEqual(fit.offsetZ, env.site.terrainOrigin[1], 0.01f);
        Assert.AreEqual(minX, env.site.terrainOrigin[0], 1.0f);
        Assert.AreEqual(minZ, env.site.terrainOrigin[1], 1.0f);

        // And every placement moved with it, out of the origin corner and into the plot.
        foreach (var b in env.buildingInstances) AssertInSiteBounds(b.position, minX, minZ, maxX, maxZ, "building");
        foreach (var o in env.objectInstances)   AssertInSiteBounds(o.position, minX, minZ, maxX, maxZ, "object");
        foreach (var p in env.site.paths)
            foreach (var pt in p.points)
            {
                Assert.GreaterOrEqual(pt[0], minX - 1f);
                Assert.LessOrEqual(pt[0], maxX + 1f);
            }
        foreach (var v in env.site.lotBoundary) AssertInSiteBounds(new[] { v[0], 0f, v[1] }, minX, minZ, maxX, maxZ, "lot vertex");
        // The court fence has to travel with the courts, not stay at the origin.
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
