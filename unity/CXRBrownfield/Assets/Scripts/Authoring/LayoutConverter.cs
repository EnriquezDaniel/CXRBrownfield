using System;
using System.Collections.Generic;
using UnityEngine;

// Converts Gemini-generated FullTerrainData into an editable (EnvironmentDef, BuildingDef list).
//
// Decisions encoded here (§9.1 of build plan):
// - generated_objects (box buildings) → objectInstances with their object_type as prefabType.
//   Fallback prefabType = "massing_box" when object_type is missing. NOT converted to tiled BuildingDefs.
// - generated_buildings → one BuildingDef each (rectangular square-tile grid) + BuildingInstance,
//   with a window pair on every exposed wall face from the second storey up (BuildingWindows), stored
//   as ordinary hosted decor so the tile editor can erase or repaint them.
// - Coordinates: normalized from canvas space [0, canvasW) × [0, canvasH) to world meters.
//   terrainWidthM  = site_width_ft  × FT_TO_M
//   terrainHeightM = site_height_ft × FT_TO_M
// - Conversion runs Unity-side; caller POSTs result to /api/environments.
//
// Coordinate convention (RESOLVED — was the build-plan audit's open item):
//   The LLM schema labels coordinates [ymin, xmin, ymax, xmax] / center_point [y, x]: index [0] runs
//   DOWN the sketch (0 at the top edge, north) and index [1] runs ACROSS it (0 at the left edge,
//   west). This converter maps index [0] → Unity X and index [1] → Unity Z and TURNS THE PLAN HALF A
//   TURN so the top of the sketch (north) lands at +X and its left edge (west) at +Z:
//       X = (1 − c0 / canvasW) · terrainWidthM,   Z = (1 − c1 / canvasH) · terrainHeightM.
//   Viewed top-down with +X up the screen, north is up and west is on the left, the way the sketch
//   was drawn. The index swap on its own is a REFLECTION of the ground plane, which reverses the
//   rotation sense (the prompt's rotation_y_deg is counterclockwise as drawn; Unity yaw is
//   clockwise-positive from above), and the half turn adds 180°, so a sketch rotation θ becomes a
//   Unity yaw of 180° − θ (MapRotation). Applied consistently to buildings, generated objects and
//   prefab instances; every polyline and box goes through the same Frame, and SiteFit.BoundaryToCanvas
//   is the exact inverse for a site drawn in Unity (SiteFitTests pins the round trip).
//   When a lot_boundary is present the whole plan is then shifted so the parcel's bbox corner sits
//   at the origin and the terrain is sized to that bbox (+ margin): an inset parcel hugs the ground
//   instead of floating at the far end of it.
//   A building's bounding_box is its axis-aligned box on the canvas, and the instance is then yawed,
//   so at a quarter turn (90° / 270°) the grid is built TRANSPOSED (IsQuarterTurn) and the rotation
//   swings it back onto the box; without that a 90° building crosses its box instead of filling it,
//   which on a narrow strip pushes it out of the parcel. Residual (documented, not fixed): at yaws
//   that are not multiples of 90° the box is a rotated AABB, so the tile grid over-sizes.

public static class LayoutConverter
{
    public struct ConversionResult
    {
        public EnvironmentDef Environment;
        public List<BuildingDef> Buildings;
    }

    // Sketch-frame rotation (counterclockwise as drawn, per the prompt) → Unity yaw. The [y,x]→(X,Z)
    // transpose reflects the ground plane (ψ = −θ) and the half turn of the plan adds 180°, so
    // ψ = 180° − θ, normalized to [0,360). A building drawn axis-aligned (θ = 0) therefore carries a
    // 180° yaw and its corner pivot sits at the footprint's max corner.
    // Public: the EditMode tests (separate assembly) pin this convention.
    public static float MapRotation(float sketchDeg) => Mathf.Repeat(180f - sketchDeg, 360f);

    // True for a yaw within a degree of 90 or 270: the building's own long side runs across the
    // canvas box's long side, so the tile grid must be built transposed (see the header).
    public static bool IsQuarterTurn(float sketchDeg) => Mathf.Abs(Mathf.Repeat(sketchDeg, 180f) - 90f) < 1f;

    // Canvas → world transform shared by every category (see the file header). offX / offZ carry
    // the parcel-hugging shift; they are zero without a lot boundary.
    private struct Frame
    {
        public float canvasW, canvasH, terrainW, terrainH, offX, offZ;

        public float X(float c0) => (1f - c0 / canvasW) * terrainW - offX;
        public float Z(float c1) => (1f - c1 / canvasH) * terrainH - offZ;

        // Real extents of a canvas box, independent of the turn.
        public float ExtentX(float c0Min, float c0Max) => (c0Max - c0Min) / canvasW * terrainW;
        public float ExtentZ(float c1Min, float c1Max) => (c1Max - c1Min) / canvasH * terrainH;
    }

    // `windows` is the rule for the generated buildings' upper-floor window pairs (BuildingWindows):
    // null = BuildingWindows.DefaultRule; a rule with an empty prefabKey adds none.
    public static ConversionResult Convert(FullTerrainData src, string environmentName = null,
                                           BuildingWindows.Rule? windows = null)
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

        var frame = new Frame
        {
            canvasW = canvasW, canvasH = canvasH,
            terrainW = terrainWidthM, terrainH = terrainHeightM,
            offX = 0f, offZ = 0f,
        };

        // Hug the terrain rectangle to the parcel: when a boundary exists, shift the plan so the
        // parcel bbox's min corner is the origin and size the ground to that bbox (+ small margin),
        // so the visible terrain tracks the lot instead of the full canvas. Everything below goes
        // through the same shifted frame, so content stays on-terrain. With no boundary we keep the
        // real site dimensions and no shift (legacy behavior).
        float terrSizeW = terrainWidthM, terrSizeL = terrainHeightM;
        var lotBoundary = ConvertLotBoundary(src, frame);
        if (lotBoundary != null)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var p in lotBoundary)
            {
                if (p[0] < minX) minX = p[0];
                if (p[0] > maxX) maxX = p[0];
                if (p[1] < minZ) minZ = p[1];
                if (p[1] > maxZ) maxZ = p[1];
            }
            frame.offX = minX;
            frame.offZ = minZ;
            foreach (var p in lotBoundary) { p[0] -= minX; p[1] -= minZ; }
            const float margin = 2f;
            terrSizeW = (maxX - minX) + margin;
            terrSizeL = (maxZ - minZ) + margin;
        }

        var buildings = new List<BuildingDef>();
        var bldgInsts = new List<BuildingInstance>();
        var objInsts  = new List<ObjectInstance>();

        ConvertGeneratedBuildings(src, frame, buildings, bldgInsts, windows ?? BuildingWindows.DefaultRule);
        ConvertGeneratedObjects(src, frame, objInsts);
        ConvertPrefabInstances(src, frame, objInsts);

        var env = new EnvironmentDef
        {
            id      = Guid.NewGuid().ToString("D"),
            name    = environmentName ?? "Generated Environment",
            version = 1,
            tags    = new List<string> { "generated" },
            site    = new SiteDef
            {
                terrainSize    = new[] { terrSizeW, terrSizeL },
                terrainZones   = ConvertTerrainZones(src, frame),
                paths          = ConvertPaths(src, frame),
                fences         = ConvertFences(src, frame),
                surfaceStrokes = new List<SurfaceStrokeDef>(),
                heightStrokes  = new List<HeightStrokeDef>(),
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
        FullTerrainData src, Frame f,
        List<BuildingDef> buildings, List<BuildingInstance> bldgInsts, BuildingWindows.Rule windows)
    {
        if (src.generated_buildings == null) return;

        foreach (var gb in src.generated_buildings)
        {
            if (gb?.bounding_box == null || gb.bounding_box.Length < 4) continue;

            string bldgId = Guid.NewGuid().ToString("D");
            var def = BuildingDefFromGeneratedBuilding(gb, bldgId, f, windows);
            buildings.Add(def);

            float worldX = (gb.center_point != null && gb.center_point.Length >= 1) ? f.X(gb.center_point[0]) : 0f;
            float worldZ = (gb.center_point != null && gb.center_point.Length >= 2) ? f.Z(gb.center_point[1]) : 0f;

            // A tile grid is CORNER-pivoted: TileSpawner puts cell (gridX, gridZ) at
            // ((gridX + 0.5)·cell, (gridZ + 0.5)·cell) local, and the def's grid starts at (0, 0),
            // so the root's origin is the footprint's min corner in the building's own frame.
            // center_point is the footprint's CENTRE (site_parsing.md: "center_point must be the
            // exact center of the bounding box"), so the instance has to sit half a footprint back
            // from it, along the building's own rotated axes, or the building lands offset by half
            // its own size. (Under the plan's half turn an axis-aligned building has a 180° yaw, so
            // that pivot ends up at the world-space max corner; the grid still fills the box.)
            float yaw = MapRotation(gb.rotation_y_deg);
            Vector3 corner = Quaternion.Euler(0f, yaw, 0f) * FootprintHalfExtent(def);

            // Sign word from the model, carried by the placed instance and facing world east
            // (BuildingSigns.StartFace turns the compass into the building-local wall through the
            // yaw). No word, no compass: the instance carries neither, and the def never does.
            string signText = BuildingSigns.NormalizeText(gb.sign);

            bldgInsts.Add(new BuildingInstance
            {
                instanceId  = Guid.NewGuid().ToString("D"),
                buildingId  = bldgId,
                position    = new[] { worldX - corner.x, 0f, worldZ - corner.z },
                rotationY   = yaw,
                scale       = 1f,
                included    = true,
                signs       = signText == null ? null : new List<BuildingSignEntry> { new() { text = signText } },
                signCompass = signText == null ? null : BuildingSigns.DefaultCompass,
            });
        }
    }

    // Half the tile grid's XZ extent in metres, in the building's own (unrotated) frame: the vector
    // from the corner-pivot origin to the footprint centre. Zero for a def with no tiles.
    private static Vector3 FootprintHalfExtent(BuildingDef def)
    {
        if (def?.tiles == null || def.tiles.Count == 0) return Vector3.zero;
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (var t in def.tiles)
        {
            if (t == null) continue;
            if (t.gridX < minX) minX = t.gridX;
            if (t.gridX > maxX) maxX = t.gridX;
            if (t.gridZ < minZ) minZ = t.gridZ;
            if (t.gridZ > maxZ) maxZ = t.gridZ;
        }
        if (minX > maxX || minZ > maxZ) return Vector3.zero;
        float cell = def.gridCellSize > 0f ? def.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        return new Vector3((maxX - minX + 1) * cell * 0.5f, 0f, (maxZ - minZ + 1) * cell * 0.5f);
    }

    private static void ConvertGeneratedObjects(FullTerrainData src, Frame f, List<ObjectInstance> objInsts)
    {
        if (src.generated_objects == null) return;

        foreach (var go in src.generated_objects)
        {
            if (go?.center_point == null || go.center_point.Length < 2) continue;

            float worldX = f.X(go.center_point[0]);
            float worldZ = f.Z(go.center_point[1]);

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

    private static void ConvertPrefabInstances(FullTerrainData src, Frame f, List<ObjectInstance> objInsts)
    {
        if (src.prefab_instances == null) return;

        foreach (var pi in src.prefab_instances)
        {
            if (pi?.center_point == null || pi.center_point.Length < 2) continue;

            objInsts.Add(new ObjectInstance
            {
                instanceId = Guid.NewGuid().ToString("D"),
                prefabType = pi.prefab_type,
                position   = new[] { f.X(pi.center_point[0]), 0f, f.Z(pi.center_point[1]) },
                rotationY  = MapRotation(pi.rotation_deg),
                scale      = pi.scale_multiplier > 0f ? pi.scale_multiplier : 1f,
                included   = true,
            });
        }
    }

    private static List<TerrainZoneDef> ConvertTerrainZones(FullTerrainData src, Frame f)
    {
        var zones = new List<TerrainZoneDef>();
        if (src.terrain_zones == null) return zones;

        foreach (var tz in src.terrain_zones)
        {
            if (tz?.bounding_box == null || tz.bounding_box.Length < 4) continue;
            // The half turn swaps which canvas edge is the world min, so re-order after mapping.
            float xa = f.X(tz.bounding_box[0]), xb = f.X(tz.bounding_box[2]);
            float za = f.Z(tz.bounding_box[1]), zb = f.Z(tz.bounding_box[3]);
            zones.Add(new TerrainZoneDef
            {
                terrainType = tz.terrain_type,
                rectMeters  = new[]
                {
                    Mathf.Min(xa, xb), Mathf.Min(za, zb),
                    Mathf.Max(xa, xb), Mathf.Max(za, zb),
                }
            });
        }
        return zones;
    }

    // site_scale.lot_boundary ([y,x] normalized polygon) → world-meter [x,z] polygon stored on the
    // SiteDef so WorldRenderer can mask the terrain to the parcel. Same canvas→meters transform as
    // every other coordinate (the caller applies the parcel-hugging shift afterwards).
    // Returns null for a missing / degenerate boundary so terrain stays a full rectangle (legacy).
    private static float[][] ConvertLotBoundary(FullTerrainData src, Frame f)
    {
        var boundary = src.site_scale?.lot_boundary;
        if (boundary == null || boundary.Length < 3) return null;

        var pts = new List<float[]>(boundary.Length);
        foreach (var p in boundary)
        {
            if (p == null || p.Length < 2) continue;
            pts.Add(new[] { f.X(p[0]), f.Z(p[1]) });
        }
        return pts.Count >= 3 ? pts.ToArray() : null;
    }

    // Generated paths → editable PathDefs. Same canvas→meters transform as zones/placement;
    // width is given in feet (like target_dimensions_ft) → meters.
    private static List<PathDef> ConvertPaths(FullTerrainData src, Frame f)
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
                pts.Add(new[] { f.X(p[0]), f.Z(p[1]) });
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

    // Generated fences → editable FenceDefs. Same canvas→meters transform as paths; height is given
    // in feet → meters.
    private static List<FenceDef> ConvertFences(FullTerrainData src, Frame f)
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
                pts.Add(new[] { f.X(p[0]), f.Z(p[1]) });
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

    private static BuildingDef BuildingDefFromGeneratedBuilding(GeneratedBuilding gb, string id, Frame f,
                                                                BuildingWindows.Rule windows)
    {
        float footprintW = f.ExtentX(gb.bounding_box[0], gb.bounding_box[2]);
        float footprintD = f.ExtentZ(gb.bounding_box[1], gb.bounding_box[3]);

        // The box is axis-aligned on the canvas and the instance is yawed afterwards. At a quarter
        // turn the box's X extent is the building's own depth and vice versa, so build the grid
        // transposed; the rotation then lays it back over the box. (corner_angles are given in the
        // building's own frame, so they land on the intended corners too.)
        if (IsQuarterTurn(gb.rotation_y_deg))
        {
            float t = footprintW; footprintW = footprintD; footprintD = t;
        }

        float cellSize = AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        int tilesWide  = Mathf.Max(1, Mathf.RoundToInt(footprintW / cellSize));
        int tilesDeep  = Mathf.Max(1, Mathf.RoundToInt(footprintD / cellSize));
        // Honor the model's inferred floor count; fall back to 2 (the prompt's own ambiguous default).
        int floors     = gb.floors > 0 ? gb.floors : 2;

        var tiles = new List<TileDef>();
        for (int fl = 0; fl < floors; fl++)
        {
            for (int x = 0; x < tilesWide; x++)
            {
                for (int z = 0; z < tilesDeep; z++)
                {
                    tiles.Add(new TileDef
                    {
                        gridX         = x,
                        gridZ         = z,
                        floor         = fl,
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
            // Only a letter the designer notes assigned survives; anything else is "no style". The
            // server already returns a warning for a bad value, so the converter stays quiet.
            style           = BuildingStyles.Normalize(gb.style),
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

        // Window pairs on every exposed wall face from the second storey up. After the bend so the
        // provisional seat already follows the deformed faces (the render paths reseat them again).
        BuildingWindows.Populate(def, windows);

        return def;
    }
}
