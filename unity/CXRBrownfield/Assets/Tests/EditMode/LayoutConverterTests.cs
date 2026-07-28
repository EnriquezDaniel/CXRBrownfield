using NUnit.Framework;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

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

        float halfM = 300f * AuthoringConventions.FT_TO_M * 0.5f;
        Assert.AreEqual("grass", zone.terrainType);
        Assert.AreEqual(0f,    zone.rectMeters[0], 0.001f);
        Assert.AreEqual(0f,    zone.rectMeters[1], 0.001f);
        Assert.AreEqual(halfM, zone.rectMeters[2], 0.001f);
        Assert.AreEqual(halfM, zone.rectMeters[3], 0.001f);
    }

    [Test]
    public void Convert_LotBoundary_NormalizesToMetersXZ()
    {
        var data = MinimalData(300f, 300f);
        // [y, x] normalized triangle; index [0] → X, index [1] → Z (file-header convention).
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
        Assert.AreEqual(0f,          boundary[0][0], 0.001f);  // x
        Assert.AreEqual(0f,          boundary[0][1], 0.001f);  // z
        Assert.AreEqual(fullM,       boundary[1][0], 0.001f);  // x = (1000/1000)*W
        Assert.AreEqual(0f,          boundary[1][1], 0.001f);
        Assert.AreEqual(fullM * 0.5f, boundary[2][0], 0.001f); // x = (500/1000)*W
        Assert.AreEqual(fullM,       boundary[2][1], 0.001f);  // z = (1000/1000)*H
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
        // Sketch yaw 45° CCW-as-drawn → Unity yaw −45° = 315° (the [y,x]→(X,Z) transpose
        // reflects the ground plane, flipping rotation sense — see LayoutConverter header).
        Assert.AreEqual(315f, binst.rotationY, 0.001f);
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
        // Sketch 90° CCW-as-drawn → Unity yaw 270° (reflection flips rotation sense).
        Assert.AreEqual(270f, inst.rotationY, 0.001f);
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
        // (500/1000)*W for X and (250/1000)*H for Z, mirroring the zone/placement transform.
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
        Assert.AreEqual(0.25f * H, path.points[1][1], 0.001f);  // Z from canvas index [1]
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
        // (500/1000)*W for X and (250/1000)*H for Z.
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
        Assert.AreEqual(0.25f * H, fence.points[1][1], 0.001f);  // Z from canvas index [1]
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

    // --- Rotation mapping: sketch frame (CCW as drawn) → Unity yaw (CW from above) ---

    [Test]
    public void MapRotation_FlipsSignAndNormalizes()
    {
        Assert.AreEqual(0f,   LayoutConverter.MapRotation(0f),    0.001f);   // axis-aligned regression
        Assert.AreEqual(330f, LayoutConverter.MapRotation(30f),   0.001f);
        Assert.AreEqual(270f, LayoutConverter.MapRotation(90f),   0.001f);
        Assert.AreEqual(30f,  LayoutConverter.MapRotation(-30f),  0.001f);   // negative input normalizes
        Assert.AreEqual(0f,   LayoutConverter.MapRotation(360f),  0.001f);   // full turn wraps to 0
        Assert.AreEqual(180f, LayoutConverter.MapRotation(180f),  0.001f);   // half turn is its own mirror
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
        Assert.AreEqual(330f, result.Environment.buildingInstances[0].rotationY, 0.001f);
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
        Assert.AreEqual(270f, result.Environment.objectInstances[0].rotationY, 0.001f);
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
            embeddedObjects = new List<EmbeddedObjectDef>()
        };

        string json = JsonConvert.SerializeObject(original, Formatting.Indented);
        var deserialized = JsonConvert.DeserializeObject<BuildingDef>(json);

        Assert.AreEqual(original.id, deserialized.id);
        Assert.AreEqual(original.name, deserialized.name);
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
