using NUnit.Framework;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

[TestFixture]
public class LayoutConverterTests
{
    private static FullTerrainData MinimalData(
        float siteWidthFt  = 300f,
        float siteHeightFt = 300f,
        int canvasW = 1000,
        int canvasH = 1000)
    {
        return new FullTerrainData
        {
            site_scale = new SiteScale
            {
                normalized_canvas = new[] { 0, 0, canvasW, canvasH },
                site_width_ft  = siteWidthFt,
                site_height_ft = siteHeightFt,
                scale_note     = "unit test"
            },
            terrain_zones      = new List<TerrainZone>(),
            generated_buildings = new List<GeneratedBuilding>(),
            generated_objects  = new List<GeneratedObject>(),
            prefab_instances   = new List<PrefabInstance>()
        };
    }

    [Test]
    public void Convert_MinimalData_ReturnsValidEnvironment()
    {
        var result = LayoutConverter.Convert(MinimalData(), "Test Env");

        Assert.IsNotNull(result.Environment);
        Assert.IsNotNull(result.Buildings);
        Assert.AreEqual("Test Env", result.Environment.name);
        Assert.AreEqual(1, result.Environment.version);
        Assert.IsFalse(string.IsNullOrEmpty(result.Environment.id));
        Assert.AreEqual(0, result.Buildings.Count);
        Assert.AreEqual(0, result.Environment.buildingInstances.Count);
        Assert.AreEqual(0, result.Environment.objectInstances.Count);
    }

    [Test]
    public void Convert_DefaultName_UsedWhenNoneProvided()
    {
        var result = LayoutConverter.Convert(MinimalData());
        Assert.AreEqual("Generated Environment", result.Environment.name);
    }

    [Test]
    public void Convert_TerrainSizeInMeters_MatchesFtToM()
    {
        float expectedW = 300f * AuthoringConventions.FT_TO_M;
        float expectedH = 300f * AuthoringConventions.FT_TO_M;

        var result = LayoutConverter.Convert(MinimalData(300f, 300f));

        Assert.AreEqual(expectedW, result.Environment.site.terrainSize[0], 0.001f);
        Assert.AreEqual(expectedH, result.Environment.site.terrainSize[1], 0.001f);
    }

    [Test]
    public void Convert_BuildingStyle_NormalizesLetterAndDropsUnknown()
    {
        var data = MinimalData();
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name = "Styled", bounding_box = new[] { 0, 0, 100, 100 }, center_point = new[] { 50, 50 },
            floors = 1, style = "b",
        });
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name = "Bad letter", bounding_box = new[] { 200, 0, 300, 100 }, center_point = new[] { 250, 50 },
            floors = 1, style = "Z",
        });
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name = "Unstyled", bounding_box = new[] { 400, 0, 500, 100 }, center_point = new[] { 450, 50 },
            floors = 1,
        });

        var result = LayoutConverter.Convert(data);

        Assert.AreEqual("B",  result.Buildings.Find(b => b.name == "Styled").style);
        Assert.IsNull(result.Buildings.Find(b => b.name == "Bad letter").style, "unknown letters are ignored");
        Assert.IsNull(result.Buildings.Find(b => b.name == "Unstyled").style);
        foreach (var b in result.Buildings)
            foreach (var t in b.tiles)
                Assert.IsNull(t.faceMaterials, "style is resolved at spawn time, never baked into faceMaterials");
    }

    // A 2 x 2 tile footprint: 1000 ft on a 1000 canvas is 0.3048 m per unit, so 26 units is 7.9 m,
    // which rounds to two 4 m cells.
    private static GeneratedBuilding TwoByTwo(int floors) => new GeneratedBuilding
    {
        area_name = "Flats", bounding_box = new[] { 0, 0, 26, 26 }, center_point = new[] { 13, 13 },
        floors = floors,
    };

    [Test]
    public void Convert_GeneratedBuilding_UpperFloorsGetWindowPairs()
    {
        var data = MinimalData(siteWidthFt: 1000f, siteHeightFt: 1000f);
        data.generated_buildings.Add(TwoByTwo(3));

        var bdef = LayoutConverter.Convert(data).Buildings[0];

        Assert.AreEqual(4, bdef.tiles.FindAll(t => t.floor == 0).Count, "2 x 2 footprint");
        Assert.AreEqual(16, bdef.embeddedObjects.Count, "8 perimeter faces on each of floors 1 and 2");
        Assert.IsTrue(bdef.embeddedObjects.TrueForAll(e => e.prefabType == BuildingWindows.DefaultPrefabKey));
        Assert.IsTrue(bdef.embeddedObjects.TrueForAll(e => e.optional), "skipped by the low-detail VR viewer");
        Assert.IsTrue(bdef.embeddedObjects.TrueForAll(e => e.hostFloor >= 1), "none on the ground floor");
        Assert.IsTrue(bdef.embeddedObjects.TrueForAll(e => DecorPlacement.IsReseatable(e)));
    }

    [Test]
    public void Convert_GeneratedBuilding_OneFloorOrDisabledRule_NoWindows()
    {
        var data = MinimalData(siteWidthFt: 1000f, siteHeightFt: 1000f);
        data.generated_buildings.Add(TwoByTwo(1));
        Assert.AreEqual(0, LayoutConverter.Convert(data).Buildings[0].embeddedObjects.Count);

        var tall = MinimalData(siteWidthFt: 1000f, siteHeightFt: 1000f);
        tall.generated_buildings.Add(TwoByTwo(3));
        var off = BuildingWindows.DefaultRule;
        off.prefabKey = "";
        Assert.AreEqual(0, LayoutConverter.Convert(tall, null, off).Buildings[0].embeddedObjects.Count);
    }

    [Test]
    public void Convert_GeneratedBuilding_RuleFieldsReachTheRecord()
    {
        var data = MinimalData(siteWidthFt: 1000f, siteHeightFt: 1000f);
        data.generated_buildings.Add(TwoByTwo(2));
        var rule = new BuildingWindows.Rule
        {
            prefabKey = "Window", widthFrac = 0.5f, heightFrac = 0.6f, surfaceOffset = 0.05f,
            anchor = (int)DecorAlignment.Anchor.Center, mountAxis = (int)DecorAlignment.MountAxis.PosZ,
            flipMount = true, optional = false,
        };

        var bdef = LayoutConverter.Convert(data, "Env", rule).Buildings[0];

        Assert.AreEqual(8, bdef.embeddedObjects.Count);
        var e = bdef.embeddedObjects[0];
        Assert.AreEqual("Window", e.prefabType);
        Assert.AreEqual(0.5f,  e.decorWidthFrac,     1e-6f);
        Assert.AreEqual(0.6f,  e.decorHeightFrac,    1e-6f);
        Assert.AreEqual(0.05f, e.decorSurfaceOffset, 1e-6f);
        Assert.AreEqual((int)DecorAlignment.Anchor.Center,    e.decorAnchor);
        Assert.AreEqual((int)DecorAlignment.MountAxis.PosZ,   e.decorMountAxis);
        Assert.IsTrue(e.decorFlipMount);
        Assert.IsFalse(e.optional);
    }

    [Test]
    public void Convert_GeneratedBuilding_WindowsSurviveJsonRoundTrip()
    {
        var data = MinimalData(siteWidthFt: 1000f, siteHeightFt: 1000f);
        data.generated_buildings.Add(TwoByTwo(2));
        var bdef = LayoutConverter.Convert(data).Buildings[0];

        string json = JsonConvert.SerializeObject(bdef);
        var back = JsonConvert.DeserializeObject<BuildingDef>(json);

        Assert.AreEqual(bdef.embeddedObjects.Count, back.embeddedObjects.Count);
        for (int i = 0; i < bdef.embeddedObjects.Count; i++)
        {
            var a = bdef.embeddedObjects[i]; var b = back.embeddedObjects[i];
            Assert.AreEqual(a.instanceId, b.instanceId);
            Assert.AreEqual(a.hostFace, b.hostFace);
            Assert.AreEqual(a.hostGridX, b.hostGridX);
            Assert.AreEqual(a.hostGridZ, b.hostGridZ);
            Assert.AreEqual(a.hostFloor, b.hostFloor);
            Assert.AreEqual(a.decorWidthFrac, b.decorWidthFrac, 1e-6f);
            Assert.AreEqual(a.optional, b.optional);
            Assert.AreEqual(a.localPos[0], b.localPos[0], 1e-5f);
        }
    }

    [Test]
    public void Convert_BuildingSign_NormalizesWordAndFacesEast()
    {
        var data = MinimalData();
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name = "Shop", bounding_box = new[] { 0, 0, 100, 100 }, center_point = new[] { 50, 50 },
            floors = 1, rotation_y_deg = 0, sign = " ice cream ",
        });
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name = "Turned", bounding_box = new[] { 200, 0, 300, 100 }, center_point = new[] { 250, 50 },
            floors = 1, rotation_y_deg = 90, sign = "THEATER",
        });
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name = "Unnamed", bounding_box = new[] { 400, 0, 500, 100 }, center_point = new[] { 450, 50 },
            floors = 1,
        });

        var result = LayoutConverter.Convert(data);

        // The sign belongs to the placed instance, facing world east; the def never carries one.
        var shopDef  = result.Buildings.Find(b => b.name == "Shop");
        var shop     = result.Environment.buildingInstances.Find(i => i.buildingId == shopDef.id);
        Assert.AreEqual("ICE CREAM", shop.signText, "trimmed and uppercased");
        Assert.AreEqual("east", shop.signCompass);
        Assert.IsFalse(shop.signPinned, "generated signs sit at the centred spot");
        Assert.IsNull(shopDef.signText, "the def carries no sign of its own");
        Assert.IsNull(shopDef.signFace);
        // Sketch rotation 0 is a 180 yaw (MapRotation), which turns the local north (+Z) wall to
        // world -Z, east.
        Assert.AreEqual("north", BuildingSigns.SpecFor(shop, shopDef).face);

        var turnedDef = result.Buildings.Find(b => b.name == "Turned");
        var turned    = result.Environment.buildingInstances.Find(i => i.buildingId == turnedDef.id);
        Assert.AreEqual("THEATER", turned.signText);
        Assert.AreEqual("east", BuildingSigns.SpecFor(turned, turnedDef).face, "a quarter turn (yaw 90) puts the local east wall on world east");

        var unnamedDef = result.Buildings.Find(b => b.name == "Unnamed");
        var unnamed    = result.Environment.buildingInstances.Find(i => i.buildingId == unnamedDef.id);
        Assert.IsNull(unnamed.signText);
        Assert.IsNull(unnamed.signCompass, "no word, no compass");
        Assert.IsNull(BuildingSigns.SpecFor(unnamed, unnamedDef).text);
    }

    [Test]
    public void Convert_NonSquareSite_PairsCanvasYWithWidthAndXWithHeight()
    {
        // A 374 x 64 ft strip (Westchester Bronx River Site A). Canvas index [0] is the sketch's
        // vertical axis and spans site_width_ft -> Unity X; index [1] spans site_height_ft -> Z.
        // A 40 x 40 ft building is therefore 107 units tall by 625 units wide on the canvas.
        var data = MinimalData(374f, 64f);
        data.site_scale.lot_boundary = new[]
        {
            new float[] { 0f, 0f }, new float[] { 1000f, 0f }, new float[] { 1000f, 1000f }, new float[] { 0f, 1000f },
        };
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name      = "Kiosk",
            bounding_box   = new[] { 0, 0, 107, 625 },
            center_point   = new[] { 53, 312 },
            rotation_y_deg = 0f,
            floors         = 1,
        });

        var result = LayoutConverter.Convert(data);
        float wM = 374f * AuthoringConventions.FT_TO_M, hM = 64f * AuthoringConventions.FT_TO_M;

        // Terrain hugs the parcel plus the 2 m margin, long axis on X.
        Assert.AreEqual(wM + 2f, result.Environment.site.terrainSize[0], 0.01f);
        Assert.AreEqual(hM + 2f, result.Environment.site.terrainSize[1], 0.01f);
        // Half turn: canvas [0,0] (top-left, north-west) is the world max corner, [1000,1000] the origin.
        Assert.AreEqual(wM, result.Environment.site.lotBoundary[0][0], 0.01f);
        Assert.AreEqual(hM, result.Environment.site.lotBoundary[0][1], 0.01f);
        Assert.AreEqual(0f, result.Environment.site.lotBoundary[2][0], 0.01f);
        Assert.AreEqual(0f, result.Environment.site.lotBoundary[2][1], 0.01f);

        // 12.2 m x 12.2 m footprint -> a square 3 x 3 grid of 4 m tiles, not a 1 x 16 sliver.
        var def = result.Buildings[0];
        Assert.AreEqual(9, def.tiles.Count);
        int maxX = 0, maxZ = 0;
        foreach (var t in def.tiles) { if (t.gridX > maxX) maxX = t.gridX; if (t.gridZ > maxZ) maxZ = t.gridZ; }
        Assert.AreEqual(2, maxX);
        Assert.AreEqual(2, maxZ);
    }

    [Test]
    public void IsQuarterTurn_OnlyNear90And270()
    {
        Assert.IsTrue(LayoutConverter.IsQuarterTurn(90f));
        Assert.IsTrue(LayoutConverter.IsQuarterTurn(270f));
        Assert.IsTrue(LayoutConverter.IsQuarterTurn(-90f));
        Assert.IsTrue(LayoutConverter.IsQuarterTurn(89.5f));
        Assert.IsFalse(LayoutConverter.IsQuarterTurn(0f));
        Assert.IsFalse(LayoutConverter.IsQuarterTurn(180f));
        Assert.IsFalse(LayoutConverter.IsQuarterTurn(45f));
        Assert.IsFalse(LayoutConverter.IsQuarterTurn(120f));
    }

    [Test]
    public void Convert_QuarterTurnBuilding_GridIsTransposedSoTheYawLandsItOnItsBox()
    {
        // On the 374 x 64 ft strip the model answers rotation 90 for a building whose long wall runs
        // up the page (its long side along canvas y). Its box is 214 x 600 canvas units = 80 x 38 ft
        // = 24.4 x 11.7 m: 6 tiles along Unity X, 3 along Z once rendered. The grid itself must be
        // 3 wide x 6 deep so the 90 deg Unity yaw (180 - 90) swings it onto the box; an untransposed
        // 6 x 3 grid rotated a quarter turn would stick 24 m across the 19 m strip.
        var data = MinimalData(374f, 64f);
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name      = "Daycare",
            bounding_box   = new[] { 0, 0, 214, 600 },
            center_point   = new[] { 107, 300 },
            rotation_y_deg = 90f,
            floors         = 1,
        });

        var result = LayoutConverter.Convert(data);
        var def    = result.Buildings[0];
        var inst   = result.Environment.buildingInstances[0];
        Assert.AreEqual(90f, inst.rotationY, 0.001f);

        int maxX = 0, maxZ = 0;
        foreach (var t in def.tiles) { if (t.gridX > maxX) maxX = t.gridX; if (t.gridZ > maxZ) maxZ = t.gridZ; }
        Assert.AreEqual(2, maxX, "3 tiles wide in the building's own frame");
        Assert.AreEqual(5, maxZ, "6 tiles deep in the building's own frame");

        // Rendered footprint = the corner-pivot grid rotated by the yaw about the instance position.
        // Local [0,12] x [0,24] under a 90 deg yaw maps to world X in [pos.x, pos.x + 24] and
        // Z in [pos.z - 12, pos.z], so it must be centred on the model's center_point (turned half
        // a turn: canvas 0 is the world max on each axis).
        float cx = (1f - 107f / 1000f) * 374f * AuthoringConventions.FT_TO_M;
        float cz = (1f - 300f / 1000f) * 64f  * AuthoringConventions.FT_TO_M;
        float fpW = (maxX + 1) * def.gridCellSize, fpD = (maxZ + 1) * def.gridCellSize;   // 12, 24
        Vector3 c0 = Quaternion.Euler(0f, inst.rotationY, 0f) * new Vector3(0f, 0f, 0f);
        Vector3 c1 = Quaternion.Euler(0f, inst.rotationY, 0f) * new Vector3(fpW, 0f, fpD);
        float worldMinX = inst.position[0] + Mathf.Min(c0.x, c1.x), worldMaxX = inst.position[0] + Mathf.Max(c0.x, c1.x);
        float worldMinZ = inst.position[2] + Mathf.Min(c0.z, c1.z), worldMaxZ = inst.position[2] + Mathf.Max(c0.z, c1.z);
        Assert.AreEqual(24f, worldMaxX - worldMinX, 0.01f, "long side lies along X, the strip's long axis");
        Assert.AreEqual(12f, worldMaxZ - worldMinZ, 0.01f, "short side across Z fits the 19.4 m strip");
        Assert.AreEqual(cx, (worldMinX + worldMaxX) * 0.5f, 0.01f);
        Assert.AreEqual(cz, (worldMinZ + worldMaxZ) * 0.5f, 0.01f);
    }

    [Test]
    public void Convert_TerrainZone_NormalizesCoordinates()
    {
        var data = MinimalData();
        data.terrain_zones.Add(new TerrainZone
        {
            terrain_type  = "grass",
            bounding_box  = new[] { 0, 0, 500, 500 }   // half of 1000×1000 canvas
        });

        var result = LayoutConverter.Convert(data);
        var zone   = result.Environment.site.terrainZones[0];

        float fullM = 300f * AuthoringConventions.FT_TO_M;
        float halfM = fullM * 0.5f;
        // The top-left quarter of the sketch is the +X/+Z quarter of the world (half turn), and the
        // rect stays min-first after the mapping flips the ends.
        Assert.AreEqual("grass", zone.terrainType);
        Assert.AreEqual(halfM, zone.rectMeters[0], 0.001f);
        Assert.AreEqual(halfM, zone.rectMeters[1], 0.001f);
        Assert.AreEqual(fullM, zone.rectMeters[2], 0.001f);
        Assert.AreEqual(fullM, zone.rectMeters[3], 0.001f);
    }

    [Test]
    public void Convert_LotBoundary_NormalizesToMetersXZ()
    {
        var data = MinimalData(300f, 300f);
        // [y, x] normalized triangle; index [0] → X, index [1] → Z, turned half a turn
        // (file-header convention): canvas 0 is the world max, canvas 1000 the world 0.
        data.site_scale.lot_boundary = new[]
        {
            new float[] { 0f,    0f    },
            new float[] { 1000f, 0f    },
            new float[] { 500f,  1000f },
        };

        var result   = LayoutConverter.Convert(data);
        var boundary = result.Environment.site.lotBoundary;
        float fullM  = 300f * AuthoringConventions.FT_TO_M;

        Assert.IsNotNull(boundary);
        Assert.AreEqual(3, boundary.Length);
        Assert.AreEqual(fullM,       boundary[0][0], 0.001f);  // x = (1 - 0/1000)*W
        Assert.AreEqual(fullM,       boundary[0][1], 0.001f);  // z = (1 - 0/1000)*H
        Assert.AreEqual(0f,          boundary[1][0], 0.001f);  // x = (1 - 1000/1000)*W
        Assert.AreEqual(fullM,       boundary[1][1], 0.001f);
        Assert.AreEqual(fullM * 0.5f, boundary[2][0], 0.001f); // x = (1 - 500/1000)*W
        Assert.AreEqual(0f,          boundary[2][1], 0.001f);  // z = (1 - 1000/1000)*H
    }

    [Test]
    public void Convert_LotBoundary_NullWhenMissingOrDegenerate()
    {
        // Missing boundary → null (full-rectangle / legacy behavior).
        Assert.IsNull(LayoutConverter.Convert(MinimalData()).Environment.site.lotBoundary);

        // Fewer than 3 vertices → null.
        var data = MinimalData();
        data.site_scale.lot_boundary = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 100f, 100f },
        };
        Assert.IsNull(LayoutConverter.Convert(data).Environment.site.lotBoundary);
    }

    [Test]
    public void Convert_GeneratedBuilding_CreatesBuildingDefAndInstance()
    {
        var data = MinimalData();
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name     = "Community Hall",
            bounding_box  = new[] { 200, 200, 400, 400 },
            center_point  = new[] { 300, 300 },
            rotation_y_deg = 45f
        });

        var result = LayoutConverter.Convert(data);

        Assert.AreEqual(1, result.Buildings.Count);
        Assert.AreEqual(1, result.Environment.buildingInstances.Count);

        var bdef = result.Buildings[0];
        Assert.AreEqual("Community Hall", bdef.name);
        Assert.IsNotNull(bdef.tiles);
        Assert.Greater(bdef.tiles.Count, 0);
        Assert.AreEqual(AuthoringConventions.DEFAULT_GRID_CELL_SIZE, bdef.gridCellSize, 0.001f);
        Assert.AreEqual(AuthoringConventions.DEFAULT_FLOOR_HEIGHT, bdef.floorHeight, 0.001f);

        var binst = result.Environment.buildingInstances[0];
        Assert.AreEqual(bdef.id, binst.buildingId);
        // Sketch yaw 45° CCW-as-drawn → Unity yaw 180° − 45° = 135° (the [y,x]→(X,Z) transpose
        // reflects the ground plane, flipping rotation sense, and the plan's half turn adds 180°
        // — see LayoutConverter header).
        Assert.AreEqual(135f, binst.rotationY, 0.001f);
        Assert.IsTrue(binst.included);
    }

    [Test]
    public void Convert_GeneratedBuilding_TileGridCoversFootprint()
    {
        var data = MinimalData(siteWidthFt: 1000f, siteHeightFt: 1000f);
        // bounding box 200 wide × 200 tall on a 1000 canvas → 0.2 of terrain
        // terrain = 1000ft * FT_TO_M ≈ 304.8m; 0.2 * 304.8 ≈ 60.96m; /4m cell ≈ 15 tiles each axis
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name    = "Big Hall",
            bounding_box = new[] { 0, 0, 200, 200 },
            center_point = new[] { 100, 100 }
        });

        var result = LayoutConverter.Convert(data);
        var bdef   = result.Buildings[0];

        // 2 floors × tilesWide × tilesDeep
        Assert.AreEqual(2, bdef.floors);
        Assert.Greater(bdef.tiles.Count, 0);
        Assert.IsTrue(bdef.tiles.TrueForAll(t => t.shapeId == "square"));
        Assert.IsTrue(bdef.tiles.TrueForAll(t => t.rotation == 0));
    }

    [Test]
    public void Convert_PrefabInstance_BecomesObjectInstance()
    {
        var data = MinimalData();
        data.prefab_instances.Add(new PrefabInstance
        {
            prefab_type      = "oak_tree",
            center_point     = new[] { 500, 500 },
            rotation_deg     = 90f,
            scale_multiplier = 2f
        });

        var result = LayoutConverter.Convert(data);

        Assert.AreEqual(1, result.Environment.objectInstances.Count);
        var inst = result.Environment.objectInstances[0];
        Assert.AreEqual("oak_tree", inst.prefabType);
        // Sketch 90° CCW-as-drawn → Unity yaw 180° − 90° = 90° (reflection flips rotation sense,
        // the half turn adds 180°).
        Assert.AreEqual(90f, inst.rotationY, 0.001f);
        Assert.AreEqual(2f,  inst.scale, 0.001f);
        Assert.IsTrue(inst.included);
        Assert.IsFalse(string.IsNullOrEmpty(inst.instanceId));
    }

    [Test]
    public void Convert_PrefabInstance_ZeroScale_DefaultsToOne()
    {
        var data = MinimalData();
        data.prefab_instances.Add(new PrefabInstance
        {
            prefab_type      = "bench",
            center_point     = new[] { 100, 100 },
            scale_multiplier = 0f
        });

        var result = LayoutConverter.Convert(data);
        Assert.AreEqual(1f, result.Environment.objectInstances[0].scale, 0.001f);
    }

    [Test]
    public void Convert_GeneratedObject_BecomesObjectInstanceWithObjectType()
    {
        var data = MinimalData();
        data.generated_objects.Add(new GeneratedObject
        {
            area_name   = "Shed",
            object_type = "storage_shed",
            center_point = new[] { 100, 100 },
            target_dimensions_ft = new TargetDimensionsFt { width_ft = 20, depth_ft = 20, height_ft = 15 }
        });

        var result = LayoutConverter.Convert(data);

        Assert.AreEqual(1, result.Environment.objectInstances.Count);
        Assert.AreEqual("storage_shed", result.Environment.objectInstances[0].prefabType);
        Assert.IsTrue(result.Environment.objectInstances[0].included);
    }

    [Test]
    public void Convert_GeneratedObject_MissingType_FallsBackToMassingBox()
    {
        var data = MinimalData();
        data.generated_objects.Add(new GeneratedObject
        {
            object_type  = "",
            center_point = new[] { 200, 200 },
            target_dimensions_ft = new TargetDimensionsFt()
        });

        var result = LayoutConverter.Convert(data);
        Assert.AreEqual("massing_box", result.Environment.objectInstances[0].prefabType);
    }

    [Test]
    public void Convert_NullData_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => LayoutConverter.Convert(null));
    }

    [Test]
    public void Convert_NullSiteScale_ThrowsArgumentException()
    {
        var data = MinimalData();
        data.site_scale = null;
        Assert.Throws<ArgumentException>(() => LayoutConverter.Convert(data));
    }

    [Test]
    public void Convert_AllIds_AreUniqueGuids()
    {
        var data = MinimalData();
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name    = "A",
            bounding_box = new[] { 0, 0, 100, 100 },
            center_point = new[] { 50, 50 }
        });
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name    = "B",
            bounding_box = new[] { 200, 200, 300, 300 },
            center_point = new[] { 250, 250 }
        });

        var result = LayoutConverter.Convert(data);

        var ids = new HashSet<string>
        {
            result.Environment.id,
            result.Environment.buildingInstances[0].instanceId,
            result.Environment.buildingInstances[1].instanceId,
            result.Buildings[0].id,
            result.Buildings[1].id,
        };
        Assert.AreEqual(5, ids.Count, "All generated IDs must be unique");
    }

    [Test]
    public void Convert_Path_NormalizesCoordinatesAndWidth()
    {
        // 300ft site → terrain meters; canvas 1000. A point at canvas [500, 250] maps to
        // (1 - 500/1000)*W for X and (1 - 250/1000)*H for Z, the same half-turned transform as
        // zones and placement.
        var data = MinimalData(300f, 300f);
        data.paths = new List<GeneratedPath>
        {
            new GeneratedPath
            {
                path_material = "brick",
                width_ft      = 6f,
                points        = new int[][] { new[] { 0, 0 }, new[] { 500, 250 }, new[] { 1000, 1000 } }
            }
        };

        var result = LayoutConverter.Convert(data);
        float W = 300f * AuthoringConventions.FT_TO_M;
        float H = 300f * AuthoringConventions.FT_TO_M;

        Assert.AreEqual(1, result.Environment.site.paths.Count);
        var path = result.Environment.site.paths[0];
        Assert.AreEqual("brick", path.material);
        Assert.AreEqual(6f * AuthoringConventions.FT_TO_M, path.width, 0.001f);
        Assert.IsFalse(string.IsNullOrEmpty(path.id), "Converted path must get a stable id");
        Assert.AreEqual(3, path.points.Length);
        Assert.AreEqual(0.5f * W, path.points[1][0], 0.001f);   // X from canvas index [0]
        Assert.AreEqual(0.75f * H, path.points[1][1], 0.001f);  // Z from canvas index [1], turned
    }

    [Test]
    public void Convert_NoPaths_ProducesEmptyList()
    {
        var result = LayoutConverter.Convert(MinimalData());
        Assert.IsNotNull(result.Environment.site.paths);
        Assert.AreEqual(0, result.Environment.site.paths.Count);
        Assert.IsNotNull(result.Environment.site.surfaceStrokes);
    }

    [Test]
    public void Convert_Fence_NormalizesCoordinatesAndHeight()
    {
        // Same canvas→meters transform as paths: a point at canvas [500, 250] maps to
        // (1 - 500/1000)*W for X and (1 - 250/1000)*H for Z.
        var data = MinimalData(300f, 300f);
        data.fences = new List<GeneratedFence>
        {
            new GeneratedFence
            {
                fence_type = "picket",
                height_ft  = 4f,
                points     = new int[][] { new[] { 0, 0 }, new[] { 500, 250 }, new[] { 1000, 1000 } }
            }
        };

        var result = LayoutConverter.Convert(data);
        float W = 300f * AuthoringConventions.FT_TO_M;
        float H = 300f * AuthoringConventions.FT_TO_M;

        Assert.AreEqual(1, result.Environment.site.fences.Count);
        var fence = result.Environment.site.fences[0];
        Assert.AreEqual("picket", fence.fenceType);
        Assert.AreEqual(4f * AuthoringConventions.FT_TO_M, fence.height, 0.001f);
        Assert.IsFalse(string.IsNullOrEmpty(fence.id), "Converted fence must get a stable id");
        Assert.AreEqual(3, fence.points.Length);
        Assert.AreEqual(0.5f * W, fence.points[1][0], 0.001f);   // X from canvas index [0]
        Assert.AreEqual(0.75f * H, fence.points[1][1], 0.001f);  // Z from canvas index [1], turned
    }

    [Test]
    public void Convert_FenceOmittedHeight_FallsBackToPaletteDefault()
    {
        // height_ft omitted (-1) ⇒ FenceDef.height stays 0 so WorldRenderer uses the palette default.
        var data = MinimalData(300f, 300f);
        data.fences = new List<GeneratedFence>
        {
            new GeneratedFence
            {
                fence_type = "lattice",
                points     = new int[][] { new[] { 0, 0 }, new[] { 1000, 1000 } }
            }
        };

        var result = LayoutConverter.Convert(data);
        Assert.AreEqual(1, result.Environment.site.fences.Count);
        Assert.AreEqual(0f, result.Environment.site.fences[0].height, 0.001f);
    }

    [Test]
    public void Convert_NoFences_ProducesEmptyList()
    {
        var result = LayoutConverter.Convert(MinimalData());
        Assert.IsNotNull(result.Environment.site.fences);
        Assert.AreEqual(0, result.Environment.site.fences.Count);
    }

    // --- Rotation mapping: sketch frame (CCW as drawn) → Unity yaw (CW from above), plus the
    // plan's half turn: yaw = 180 − θ ---

    [Test]
    public void MapRotation_FlipsSignAddsHalfTurnAndNormalizes()
    {
        Assert.AreEqual(180f, LayoutConverter.MapRotation(0f),    0.001f);   // axis-aligned carries the half turn
        Assert.AreEqual(150f, LayoutConverter.MapRotation(30f),   0.001f);
        Assert.AreEqual(90f,  LayoutConverter.MapRotation(90f),   0.001f);
        Assert.AreEqual(210f, LayoutConverter.MapRotation(-30f),  0.001f);   // negative input normalizes
        Assert.AreEqual(180f, LayoutConverter.MapRotation(360f),  0.001f);   // full turn wraps
        Assert.AreEqual(0f,   LayoutConverter.MapRotation(180f),  0.001f);   // a drawn half turn cancels ours
    }

    // The convention itself, in one place: the top-left of the sketch (north-west) is the world's
    // +X/+Z corner, an axis-aligned building carries the half turn, and an inset parcel is shifted so
    // its bbox corner sits at the origin with the terrain hugging it.
    [Test]
    public void Convert_TopOfSketchLandsAtPositiveX_LeftAtPositiveZ()
    {
        var data = MinimalData(300f, 200f);
        data.prefab_instances.Add(new PrefabInstance { prefab_type = "tree", center_point = new[] { 0, 0 } });
        data.prefab_instances.Add(new PrefabInstance { prefab_type = "bench", center_point = new[] { 1000, 1000 } });
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name = "Aligned", bounding_box = new[] { 100, 100, 300, 300 }, center_point = new[] { 200, 200 },
            rotation_y_deg = 0f, floors = 1,
        });

        var result = LayoutConverter.Convert(data);
        float W = 300f * AuthoringConventions.FT_TO_M, H = 200f * AuthoringConventions.FT_TO_M;
        var nw = result.Environment.objectInstances[0];
        var se = result.Environment.objectInstances[1];
        Assert.AreEqual(W, nw.position[0], 0.001f, "top of the sketch (north) is +X");
        Assert.AreEqual(H, nw.position[2], 0.001f, "left of the sketch (west) is +Z");
        Assert.AreEqual(0f, se.position[0], 0.001f);
        Assert.AreEqual(0f, se.position[2], 0.001f);

        var inst = result.Environment.buildingInstances[0];
        Assert.AreEqual(180f, inst.rotationY, 0.001f);
        // The grid extends from the pivot along the yawed axes, so the pivot is the world max corner
        // and the footprint is still centred on center_point.
        var def = result.Buildings[0];
        int maxX = 0, maxZ = 0;
        foreach (var t in def.tiles) { if (t.gridX > maxX) maxX = t.gridX; if (t.gridZ > maxZ) maxZ = t.gridZ; }
        Vector3 half = new Vector3((maxX + 1) * def.gridCellSize * 0.5f, 0f, (maxZ + 1) * def.gridCellSize * 0.5f);
        Vector3 centre = new Vector3(inst.position[0], 0f, inst.position[2]) + Quaternion.Euler(0f, inst.rotationY, 0f) * half;
        Assert.AreEqual(0.8f * W, centre.x, 0.001f);
        Assert.AreEqual(0.8f * H, centre.z, 0.001f);
    }

    [Test]
    public void Convert_InsetParcel_IsShiftedToHugTheOrigin()
    {
        // A parcel that only covers canvas y 200..600, x 100..500 (like the Bronx preset). After the
        // half turn it would sit at the far end of the ground; the shift brings its bbox corner to
        // the origin and sizes the terrain to the parcel plus the 2 m margin.
        var data = MinimalData(1000f, 1000f);
        data.site_scale.lot_boundary = new[]
        {
            new float[] { 200f, 100f }, new float[] { 200f, 500f }, new float[] { 600f, 500f }, new float[] { 600f, 100f },
        };
        data.prefab_instances.Add(new PrefabInstance { prefab_type = "tree", center_point = new[] { 200, 100 } });

        var result = LayoutConverter.Convert(data);
        float M = 1000f * AuthoringConventions.FT_TO_M;
        var lot = result.Environment.site.lotBoundary;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var v in lot)
        {
            minX = Mathf.Min(minX, v[0]); maxX = Mathf.Max(maxX, v[0]);
            minZ = Mathf.Min(minZ, v[1]); maxZ = Mathf.Max(maxZ, v[1]);
        }
        Assert.AreEqual(0f, minX, 0.001f);
        Assert.AreEqual(0f, minZ, 0.001f);
        Assert.AreEqual(0.4f * M, maxX, 0.001f);
        Assert.AreEqual(0.4f * M, maxZ, 0.001f);
        Assert.AreEqual(0.4f * M + 2f, result.Environment.site.terrainSize[0], 0.001f);
        Assert.AreEqual(0.4f * M + 2f, result.Environment.site.terrainSize[1], 0.001f);
        // The parcel's north-west corner (its top-left on the canvas) is the shifted bbox max.
        var tree = result.Environment.objectInstances[0];
        Assert.AreEqual(0.4f * M, tree.position[0], 0.001f);
        Assert.AreEqual(0.4f * M, tree.position[2], 0.001f);
    }

    [Test]
    public void Convert_AsymmetricBuildingRotation_MapsToUnityYaw()
    {
        // 2:1 asymmetric footprint at sketch yaw 30° — the case the old passthrough got wrong.
        var data = MinimalData();
        data.generated_buildings.Add(new GeneratedBuilding
        {
            area_name      = "Long Hall",
            bounding_box   = new[] { 200, 200, 400, 600 },   // 200 × 400 canvas units
            center_point   = new[] { 300, 400 },
            rotation_y_deg = 30f
        });

        var result = LayoutConverter.Convert(data);
        Assert.AreEqual(150f, result.Environment.buildingInstances[0].rotationY, 0.001f);
    }

    [Test]
    public void Convert_GeneratedObjectRotation_MapsToUnityYaw()
    {
        var data = MinimalData();
        data.generated_objects.Add(new GeneratedObject
        {
            object_type    = "storage_shed",
            center_point   = new[] { 100, 100 },
            rotation_y_deg = 90f,
            target_dimensions_ft = new TargetDimensionsFt { width_ft = 10, depth_ft = 20, height_ft = 12 }
        });

        var result = LayoutConverter.Convert(data);
        Assert.AreEqual(90f, result.Environment.objectInstances[0].rotationY, 0.001f);
    }
}

[TestFixture]
public class AuthoringTypesSerializationTests
{
    [Test]
    public void BuildingDef_RoundTripsViaNewtonsoft()
    {
        var original = new BuildingDef
        {
            id           = "test-guid",
            name         = "Test Building",
            version      = 1,
            tags         = new List<string> { "commercial" },
            gridCellSize = 4.0f,
            floors       = 2,
            floorHeight  = 3.5f,
            tiles        = new List<TileDef>
            {
                new TileDef
                {
                    gridX    = 0, gridZ = 0, floor = 0,
                    shapeId  = "square", rotation = 0,
                    faceMaterials = new Dictionary<string, string>
                    {
                        { "north", "brick_red" },
                        { "south", "glass" }
                    }
                }
            },
            embeddedObjects = new List<EmbeddedObjectDef>(),
            style        = "C",
            signText     = "PHARMACY",
            signFace     = "west",
        };

        string json = JsonConvert.SerializeObject(original, Formatting.Indented);
        var deserialized = JsonConvert.DeserializeObject<BuildingDef>(json);

        Assert.AreEqual(original.id, deserialized.id);
        Assert.AreEqual(original.name, deserialized.name);
        Assert.AreEqual("C", deserialized.style);
        Assert.AreEqual("PHARMACY", deserialized.signText, "legacy sign fields still round-trip");
        Assert.AreEqual("west", deserialized.signFace);
        var legacy = JsonConvert.DeserializeObject<BuildingDef>("{\"id\":\"old\",\"tiles\":[]}");
        Assert.IsNull(legacy.signText, "records saved before the sign fields load as no sign");
        Assert.IsNull(legacy.signFace);
        Assert.AreEqual(4.0f, deserialized.gridCellSize, 0.001f);
        Assert.AreEqual(1, deserialized.tiles.Count);
        Assert.AreEqual("brick_red", deserialized.tiles[0].faceMaterials["north"]);
        Assert.AreEqual("glass",     deserialized.tiles[0].faceMaterials["south"]);
    }

    [Test]
    public void EnvironmentDef_RoundTripsViaNewtonsoft()
    {
        float sizeM = 91.44f;
        var original = new EnvironmentDef
        {
            id      = "env-guid",
            name    = "Test Environment",
            version = 1,
            tags    = new List<string> { "test" },
            site    = new SiteDef
            {
                terrainSize  = new[] { sizeM, sizeM },
                terrainZones = new List<TerrainZoneDef>
                {
                    new TerrainZoneDef { terrainType = "grass", rectMeters = new[] { 0f, 0f, sizeM * 0.5f, sizeM * 0.5f } }
                },
                paths     = new List<PathDef>(),
                scaleNote = "test"
            },
            buildingInstances = new List<BuildingInstance>(),
            objectInstances   = new List<ObjectInstance>()
        };

        string json = JsonConvert.SerializeObject(original, Formatting.Indented);
        var deserialized = JsonConvert.DeserializeObject<EnvironmentDef>(json);

        Assert.AreEqual(original.id, deserialized.id);
        Assert.AreEqual(sizeM, deserialized.site.terrainSize[0], 0.001f);
        Assert.AreEqual(1, deserialized.site.terrainZones.Count);
        Assert.AreEqual("grass", deserialized.site.terrainZones[0].terrainType);
        Assert.AreEqual(sizeM * 0.5f, deserialized.site.terrainZones[0].rectMeters[2], 0.001f);
    }

    [Test]
    public void PathDef_Float2DArray_RoundTrips()
    {
        var path = new PathDef
        {
            material = "gravel",
            width    = 2.0f,
            points   = new float[][] { new[] { 0f, 0f }, new[] { 10f, 20f }, new[] { 30f, 5f } }
        };

        string json = JsonConvert.SerializeObject(path);
        var deserialized = JsonConvert.DeserializeObject<PathDef>(json);

        Assert.AreEqual(3,    deserialized.points.Length);
        Assert.AreEqual(10f,  deserialized.points[1][0], 0.001f);
        Assert.AreEqual(20f,  deserialized.points[1][1], 0.001f);
        Assert.AreEqual(30f,  deserialized.points[2][0], 0.001f);
    }

    [Test]
    public void PathDef_Id_RoundTrips()
    {
        var path = new PathDef
        {
            id       = "path-guid",
            material = "pavement_light",
            width    = 3.5f,
            points   = new float[][] { new[] { 1f, 2f }, new[] { 3f, 4f } }
        };

        string json = JsonConvert.SerializeObject(path);
        var deserialized = JsonConvert.DeserializeObject<PathDef>(json);

        Assert.AreEqual("path-guid", deserialized.id);
        Assert.AreEqual("pavement_light", deserialized.material);
        Assert.AreEqual(3.5f, deserialized.width, 0.001f);
        Assert.AreEqual(2, deserialized.points.Length);
    }

    [Test]
    public void SurfaceStrokeDef_RoundTrips()
    {
        var stroke = new SurfaceStrokeDef
        {
            id          = "stroke-guid",
            terrainType = "concrete",
            radius      = 4.5f,
            points      = new float[][] { new[] { 0f, 0f }, new[] { 5f, 5f }, new[] { 10f, 2f } },
            shape       = "square",
            angleDeg    = 30f
        };

        string json = JsonConvert.SerializeObject(stroke);
        var deserialized = JsonConvert.DeserializeObject<SurfaceStrokeDef>(json);

        Assert.AreEqual("stroke-guid", deserialized.id);
        Assert.AreEqual("concrete", deserialized.terrainType);
        Assert.AreEqual(4.5f, deserialized.radius, 0.001f);
        Assert.AreEqual(3, deserialized.points.Length);
        Assert.AreEqual(5f, deserialized.points[1][1], 0.001f);
        Assert.AreEqual("square", deserialized.shape);
        Assert.AreEqual(30f, deserialized.angleDeg, 0.001f);
    }

    // Environments saved before the square brush existed have no `shape` or `angleDeg` key; they must
    // keep rasterizing as round auto-angled discs rather than deserializing to null/0.
    [Test]
    public void SurfaceStrokeDef_LegacyJsonWithoutShape_DefaultsToCircle()
    {
        const string legacy = "{\"id\":\"s\",\"terrainType\":\"grass\",\"radius\":3.0," +
                              "\"points\":[[0.0,0.0],[4.0,0.0]]}";

        var deserialized = JsonConvert.DeserializeObject<SurfaceStrokeDef>(legacy);

        Assert.AreEqual("circle", deserialized.shape);
        Assert.AreEqual("grass", deserialized.terrainType);
        Assert.AreEqual(2, deserialized.points.Length);
        // Negative = auto (follow the run). 0 would wrongly mean "pinned axis-aligned".
        Assert.Less(deserialized.angleDeg, 0f);
    }

    [Test]
    public void SiteDef_SurfaceStrokes_RoundTripInsideEnvironment()
    {
        var env = new EnvironmentDef
        {
            id = "env", name = "e", version = 1, tags = new List<string>(),
            site = new SiteDef
            {
                terrainSize    = new[] { 100f, 100f },
                terrainZones   = new List<TerrainZoneDef>(),
                paths          = new List<PathDef> { new PathDef { id = "p", material = "dirt", width = 1f, points = new float[][] { new[] { 0f, 0f }, new[] { 1f, 1f } } } },
                surfaceStrokes = new List<SurfaceStrokeDef> { new SurfaceStrokeDef { id = "s", terrainType = "grass", radius = 2f, points = new float[][] { new[] { 0f, 0f } } } },
                scaleNote      = ""
            },
            buildingInstances = new List<BuildingInstance>(),
            objectInstances   = new List<ObjectInstance>()
        };

        string json = JsonConvert.SerializeObject(env);
        var deserialized = JsonConvert.DeserializeObject<EnvironmentDef>(json);

        Assert.AreEqual(1, deserialized.site.paths.Count);
        Assert.AreEqual("dirt", deserialized.site.paths[0].material);
        Assert.AreEqual(1, deserialized.site.surfaceStrokes.Count);
        Assert.AreEqual("grass", deserialized.site.surfaceStrokes[0].terrainType);
    }

    [Test]
    public void BuildingInstance_IncludedFlag_Serializes()
    {
        var inst = new BuildingInstance
        {
            instanceId = "inst-guid",
            buildingId = "bldg-guid",
            position   = new[] { 10f, 0f, 20f },
            rotationY  = 90f,
            scale      = 1f,
            included   = false
        };

        string json = JsonConvert.SerializeObject(inst);
        var deserialized = JsonConvert.DeserializeObject<BuildingInstance>(json);

        Assert.IsFalse(deserialized.included);
        Assert.AreEqual(10f, deserialized.position[0], 0.001f);
        Assert.AreEqual(20f, deserialized.position[2], 0.001f);
    }

    [Test]
    public void TileDef_NullFaceMaterials_RoundTrips()
    {
        var tile = new TileDef { gridX = 1, gridZ = 2, floor = 0, shapeId = "wedge", rotation = 90, faceMaterials = null };

        string json = JsonConvert.SerializeObject(tile);
        var deserialized = JsonConvert.DeserializeObject<TileDef>(json);

        Assert.IsNull(deserialized.faceMaterials);
        Assert.AreEqual("wedge", deserialized.shapeId);
        Assert.AreEqual(90, deserialized.rotation);
    }
}
