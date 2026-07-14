using System;
using System.Collections.Generic;
using UnityEngine;

// Converts Gemini-generated FullTerrainData into an editable (EnvironmentDef, BuildingDef list).
//
// Decisions encoded here (§9.1 of build plan):
// - generated_objects (box buildings) → objectInstances with their object_type as prefabType.
//   Fallback prefabType = "massing_box" when object_type is missing. NOT converted to tiled BuildingDefs.
// - generated_buildings → one BuildingDef each (rectangular square-tile grid) + BuildingInstance.
// - Coordinates: normalized from canvas space [0, canvasW) × [0, canvasH) to world meters.
//   terrainWidthM  = site_width_ft  × FT_TO_M
//   terrainHeightM = site_height_ft × FT_TO_M
// - Conversion runs Unity-side; caller POSTs result to /api/environments.
//
// Coordinate convention (RESOLVED — was the build-plan audit's open item):
//   The LLM schema labels coordinates [ymin, xmin, ymax, xmax] / center_point [y, x]; this converter
//   (and the Python visualize_output) map index [0] → Unity X and index [1] → Unity Z. That swap is a
//   REFLECTION of the ground plane, which reverses rotation sense: a rotation_y_deg of θ, defined in
//   the prompt as counterclockwise as drawn on the sketch, becomes a Unity yaw of −θ (Unity yaw is
//   clockwise-positive viewed from above). MapRotation applies that consistently to buildings,
//   generated objects, and prefab instances; positions/footprints were already transposed.
//   Residual (documented, not fixed): a rotated building's bounding_box is its rotated AABB, so the
//   tile-grid footprint over-sizes at yaws that are not multiples of 90°.

public static class LayoutConverter
{
    public struct ConversionResult
    {
        public EnvironmentDef Environment;
        public List<BuildingDef> Buildings;
    }

    // Sketch-frame rotation (counterclockwise as drawn, per the prompt) → Unity yaw. The [y,x]→(X,Z)
    // transpose reflects the ground plane, so the rotation sense flips: ψ = −θ, normalized to [0,360).
    // Public: the EditMode tests (separate assembly) pin this convention.
    public static float MapRotation(float sketchDeg) => Mathf.Repeat(-sketchDeg, 360f);

    public static ConversionResult Convert(FullTerrainData src, string environmentName = null)
    {
        if (src == null) throw new ArgumentNullException(nameof(src));
        if (src.site_scale == null)
            throw new ArgumentException("site_scale is null", nameof(src));

        int[] canvas = src.site_scale.normalized_canvas;
        float canvasW = (canvas != null && canvas.Length >= 3) ? canvas[2] : 1000f;
        float canvasH = (canvas != null && canvas.Length >= 4) ? canvas[3] : 1000f;

        float terrainWidthM  = src.site_scale.site_width_ft  > 0f
            ? src.site_scale.site_width_ft  * AuthoringConventions.FT_TO_M : canvasW;
        float terrainHeightM = src.site_scale.site_height_ft > 0f
            ? src.site_scale.site_height_ft * AuthoringConventions.FT_TO_M : canvasH;

        var buildings = new List<BuildingDef>();
        var bldgInsts = new List<BuildingInstance>();
        var objInsts  = new List<ObjectInstance>();

        ConvertGeneratedBuildings(src, canvasW, canvasH, terrainWidthM, terrainHeightM, buildings, bldgInsts);
        ConvertGeneratedObjects(src, canvasW, canvasH, terrainWidthM, terrainHeightM, objInsts);
        ConvertPrefabInstances(src, canvasW, canvasH, terrainWidthM, terrainHeightM, objInsts);

        var terrainZones = ConvertTerrainZones(src, canvasW, canvasH, terrainWidthM, terrainHeightM);
        var lotBoundary  = ConvertLotBoundary(src, canvasW, canvasH, terrainWidthM, terrainHeightM);

        // Hug the terrain rectangle to the parcel: when a boundary exists, size the ground to its
        // extent (+ small margin) so the visible terrain tracks the lot instead of the full canvas.
        // All content is already in absolute meters and the LLM places it inside the parcel, so it
        // stays on-terrain. With no boundary we keep the real site dimensions (legacy behavior).
        float terrSizeW = terrainWidthM, terrSizeL = terrainHeightM;
        if (lotBoundary != null)
        {
            float maxX = 0f, maxZ = 0f;
            foreach (var p in lotBoundary)
            {
                if (p == null || p.Length < 2) continue;
                if (p[0] > maxX) maxX = p[0];
                if (p[1] > maxZ) maxZ = p[1];
            }
            const float margin = 2f;
            if (maxX > 0f) terrSizeW = maxX + margin;
            if (maxZ > 0f) terrSizeL = maxZ + margin;
        }

        var env = new EnvironmentDef
        {
            id      = Guid.NewGuid().ToString("D"),
            name    = environmentName ?? "Generated Environment",
            version = 1,
            tags    = new List<string> { "generated" },
            site    = new SiteDef
            {
                terrainSize    = new[] { terrSizeW, terrSizeL },
                terrainZones   = terrainZones,
                paths          = ConvertPaths(src, canvasW, canvasH, terrainWidthM, terrainHeightM),
                fences         = ConvertFences(src, canvasW, canvasH, terrainWidthM, terrainHeightM),
                surfaceStrokes = new List<SurfaceStrokeDef>(),
                scaleNote      = src.site_scale.scale_note,
                lotBoundary    = lotBoundary,
                outsideTerrainType = "water",
            },
            buildingInstances = bldgInsts,
            objectInstances   = objInsts,
        };

        return new ConversionResult { Environment = env, Buildings = buildings };
    }

    // --- private helpers ---

    private static void ConvertGeneratedBuildings(
        FullTerrainData src, float canvasW, float canvasH,
        float terrainWidthM, float terrainHeightM,
        List<BuildingDef> buildings, List<BuildingInstance> bldgInsts)
    {
        if (src.generated_buildings == null) return;

        foreach (var gb in src.generated_buildings)
        {
            if (gb?.bounding_box == null || gb.bounding_box.Length < 4) continue;

            string bldgId = Guid.NewGuid().ToString("D");
            buildings.Add(BuildingDefFromGeneratedBuilding(gb, bldgId, canvasW, canvasH, terrainWidthM, terrainHeightM));

            float worldX = (gb.center_point != null && gb.center_point.Length >= 1)
                ? (gb.center_point[0] / canvasW) * terrainWidthM : 0f;
            float worldZ = (gb.center_point != null && gb.center_point.Length >= 2)
                ? (gb.center_point[1] / canvasH) * terrainHeightM : 0f;

            bldgInsts.Add(new BuildingInstance
            {
                instanceId = Guid.NewGuid().ToString("D"),
                buildingId = bldgId,
                position   = new[] { worldX, 0f, worldZ },
                rotationY  = MapRotation(gb.rotation_y_deg),
                scale      = 1f,
                included   = true,
            });
        }
    }

    private static void ConvertGeneratedObjects(
        FullTerrainData src, float canvasW, float canvasH,
        float terrainWidthM, float terrainHeightM,
        List<ObjectInstance> objInsts)
    {
        if (src.generated_objects == null) return;

        foreach (var go in src.generated_objects)
        {
            if (go?.center_point == null || go.center_point.Length < 2) continue;

            float worldX = (go.center_point[0] / canvasW) * terrainWidthM;
            float worldZ = (go.center_point[1] / canvasH) * terrainHeightM;

            // Carry the target box dimensions (ft → m) so WorldRenderer can size the massing box.
            // Unity convention: X = width, Y = height, Z = depth (site_parsing.md target_dimensions_ft).
            float[] boxSizeMeters = null;
            var dims = go.target_dimensions_ft;
            if (dims != null && (dims.width_ft > 0f || dims.height_ft > 0f || dims.depth_ft > 0f))
            {
                boxSizeMeters = new[]
                {
                    dims.width_ft  * AuthoringConventions.FT_TO_M,
                    dims.height_ft * AuthoringConventions.FT_TO_M,
                    dims.depth_ft  * AuthoringConventions.FT_TO_M,
                };
            }

            objInsts.Add(new ObjectInstance
            {
                instanceId    = Guid.NewGuid().ToString("D"),
                prefabType    = string.IsNullOrWhiteSpace(go.object_type) ? "massing_box" : go.object_type,
                position      = new[] { worldX, 0f, worldZ },
                rotationY     = MapRotation(go.rotation_y_deg),
                scale         = 1f,
                boxSizeMeters = boxSizeMeters,
                included      = true,
            });
        }
    }

    private static void ConvertPrefabInstances(
        FullTerrainData src, float canvasW, float canvasH,
        float terrainWidthM, float terrainHeightM,
        List<ObjectInstance> objInsts)
    {
        if (src.prefab_instances == null) return;

        foreach (var pi in src.prefab_instances)
        {
            if (pi?.center_point == null || pi.center_point.Length < 2) continue;

            float worldX = (pi.center_point[0] / canvasW) * terrainWidthM;
            float worldZ = (pi.center_point[1] / canvasH) * terrainHeightM;

            objInsts.Add(new ObjectInstance
            {
                instanceId = Guid.NewGuid().ToString("D"),
                prefabType = pi.prefab_type,
                position   = new[] { worldX, 0f, worldZ },
                rotationY  = MapRotation(pi.rotation_deg),
                scale      = pi.scale_multiplier > 0f ? pi.scale_multiplier : 1f,
                included   = true,
            });
        }
    }

    private static List<TerrainZoneDef> ConvertTerrainZones(
        FullTerrainData src, float canvasW, float canvasH,
        float terrainWidthM, float terrainHeightM)
    {
        var zones = new List<TerrainZoneDef>();
        if (src.terrain_zones == null) return zones;

        foreach (var tz in src.terrain_zones)
        {
            if (tz?.bounding_box == null || tz.bounding_box.Length < 4) continue;
            zones.Add(new TerrainZoneDef
            {
                terrainType = tz.terrain_type,
                rectMeters  = new[]
                {
                    (tz.bounding_box[0] / canvasW) * terrainWidthM,
                    (tz.bounding_box[1] / canvasH) * terrainHeightM,
                    (tz.bounding_box[2] / canvasW) * terrainWidthM,
                    (tz.bounding_box[3] / canvasH) * terrainHeightM,
                }
            });
        }
        return zones;
    }

    // site_scale.lot_boundary ([y,x] normalized polygon) → world-meter [x,z] polygon stored on the
    // SiteDef so WorldRenderer can mask the terrain to the parcel. Same canvas→meters transform as
    // every other coordinate (index [0] → X, index [1] → Z; see file-header transpose caveat).
    // Returns null for a missing / degenerate boundary so terrain stays a full rectangle (legacy).
    private static float[][] ConvertLotBoundary(
        FullTerrainData src, float canvasW, float canvasH,
        float terrainWidthM, float terrainHeightM)
    {
        var boundary = src.site_scale?.lot_boundary;
        if (boundary == null || boundary.Length < 3) return null;

        var pts = new List<float[]>(boundary.Length);
        foreach (var p in boundary)
        {
            if (p == null || p.Length < 2) continue;
            pts.Add(new[]
            {
                (p[0] / canvasW) * terrainWidthM,
                (p[1] / canvasH) * terrainHeightM,
            });
        }
        return pts.Count >= 3 ? pts.ToArray() : null;
    }

    // Generated paths → editable PathDefs. Same canvas→meters transform as zones/placement
    // (index [0] → X, index [1] → Z); width is given in feet (like target_dimensions_ft) → meters.
    // See the file-header coordinate-transpose caveat — applies here identically.
    private static List<PathDef> ConvertPaths(
        FullTerrainData src, float canvasW, float canvasH,
        float terrainWidthM, float terrainHeightM)
    {
        var paths = new List<PathDef>();
        if (src.paths == null) return paths;

        foreach (var gp in src.paths)
        {
            if (gp?.points == null || gp.points.Length < 2) continue;

            var pts = new List<float[]>(gp.points.Length);
            foreach (var p in gp.points)
            {
                if (p == null || p.Length < 2) continue;
                pts.Add(new[]
                {
                    (p[0] / canvasW) * terrainWidthM,
                    (p[1] / canvasH) * terrainHeightM,
                });
            }
            if (pts.Count < 2) continue;

            paths.Add(new PathDef
            {
                id        = Guid.NewGuid().ToString("D"),
                material  = gp.path_material,
                width     = gp.width_ft > 0f ? gp.width_ft * AuthoringConventions.FT_TO_M : 1.5f,
                points    = pts.ToArray(),
                smoothing = gp.smoothing >= 0f ? Mathf.Clamp01(gp.smoothing)
                                               : DefaultSmoothingFor(gp.path_material),
            });
        }
        return paths;
    }

    // Generated fences → editable FenceDefs. Same canvas→meters transform as paths (see ConvertPaths
    // and the file-header coordinate-transpose caveat); height is given in feet → meters.
    private static List<FenceDef> ConvertFences(
        FullTerrainData src, float canvasW, float canvasH,
        float terrainWidthM, float terrainHeightM)
    {
        var fences = new List<FenceDef>();
        if (src.fences == null) return fences;

        foreach (var gf in src.fences)
        {
            if (gf?.points == null || gf.points.Length < 2) continue;

            var pts = new List<float[]>(gf.points.Length);
            foreach (var p in gf.points)
            {
                if (p == null || p.Length < 2) continue;
                pts.Add(new[]
                {
                    (p[0] / canvasW) * terrainWidthM,
                    (p[1] / canvasH) * terrainHeightM,
                });
            }
            if (pts.Count < 2) continue;

            fences.Add(new FenceDef
            {
                id        = Guid.NewGuid().ToString("D"),
                fenceType = gf.fence_type,
                points    = pts.ToArray(),
                // height 0 ⇒ WorldRenderer falls back to the FencePalette default for this type.
                height    = gf.height_ft > 0f ? gf.height_ft * AuthoringConventions.FT_TO_M : 0f,
                smoothing = gf.smoothing >= 0f ? Mathf.Clamp01(gf.smoothing) : 0f,
            });
        }
        return fences;
    }

    // When the LLM omits a smoothing hint, pick a sensible curvature from the surface type: paved
    // roads/sidewalks read as crisp urban geometry, dirt trails flow, brick paver paths gently round.
    private static float DefaultSmoothingFor(string material)
    {
        switch ((material ?? "").ToLowerInvariant())
        {
            case "dirt":           return 0.85f;
            case "brick":          return 0.30f;
            case "pavement_light": return 0.20f;
            case "pavement_dark":  return 0.10f;
            case "asphalt":        return 0.10f;
            default:               return 0.50f;
        }
    }

    private static BuildingDef BuildingDefFromGeneratedBuilding(
        GeneratedBuilding gb, string id,
        float canvasW, float canvasH,
        float terrainWidthM, float terrainHeightM)
    {
        float footprintW = ((gb.bounding_box[2] - gb.bounding_box[0]) / canvasW) * terrainWidthM;
        float footprintD = ((gb.bounding_box[3] - gb.bounding_box[1]) / canvasH) * terrainHeightM;

        float cellSize = AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        int tilesWide  = Mathf.Max(1, Mathf.RoundToInt(footprintW / cellSize));
        int tilesDeep  = Mathf.Max(1, Mathf.RoundToInt(footprintD / cellSize));
        // Honor the model's inferred floor count; fall back to 2 (the prompt's own ambiguous default).
        int floors     = gb.floors > 0 ? gb.floors : 2;

        var tiles = new List<TileDef>();
        for (int f = 0; f < floors; f++)
        {
            for (int x = 0; x < tilesWide; x++)
            {
                for (int z = 0; z < tilesDeep; z++)
                {
                    tiles.Add(new TileDef
                    {
                        gridX         = x,
                        gridZ         = z,
                        floor         = f,
                        shapeId       = "square",
                        rotation      = 0,
                        faceMaterials = null,
                    });
                }
            }
        }

        var def = new BuildingDef
        {
            id              = id,
            name            = gb.area_name ?? "Generated Building",
            version         = 1,
            tags            = new List<string> { "generated" },
            gridCellSize    = cellSize,
            floors          = floors,
            floorHeight     = AuthoringConventions.DEFAULT_FLOOR_HEIGHT,
            tiles           = tiles,
            embeddedObjects = new List<EmbeddedObjectDef>(),
        };

        // Optional acute/obtuse corners: bend each non-zero footprint corner through the SAME shared
        // field the interactive Skew tool uses, so generated and hand-authored angled buildings match.
        if (gb.corner_angles != null)
        {
            int n = Mathf.Min(gb.corner_angles.Length, 4);
            for (int i = 0; i < n; i++)
                if (Mathf.Abs(gb.corner_angles[i]) > 0.01f)
                    TileDeformField.ApplyCornerBend(def, (TileDeformField.Corner)i, gb.corner_angles[i]);
        }

        return def;
    }
}
