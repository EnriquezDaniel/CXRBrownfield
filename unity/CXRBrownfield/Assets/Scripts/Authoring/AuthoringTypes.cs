using System;
using System.Collections.Generic;
using Newtonsoft.Json;

// Editable authoring schema — the persistent, interactive layer above the generation pipeline.
// Separate from DataTypes.cs (generation/ingest schema). Uses Newtonsoft.Json throughout.
// Conventions: 1 Unity unit = 1 meter. ft → m: multiply by AuthoringConventions.FT_TO_M.
// IDs: stable GUID strings. Building-local coords for tiles; world coords for environment instances.

public static class AuthoringConventions
{
    public const float FT_TO_M               = 0.3048f;
    public const float DEFAULT_GRID_CELL_SIZE = 4.0f;   // meters per tile cell
    public const float DEFAULT_FLOOR_HEIGHT   = 3.5f;   // meters
}

// Optional per-tile vertex deformation that turns a cube tile into a skewed/trapezoidal prism, so
// buildings can have non-90° (acute/obtuse) floor-plan corners and sloped face edges. Null = the
// tile renders as the normal cube (today's behavior). Works on ANY shape and at any tile rotation:
// a square becomes a procedural box prism, while non-square shapes (wedge, quarter-curve) have their
// real prefab mesh warped through the same offset cage (see TileDeformField.WarpVertex / TileSpawner),
// so curves keep their silhouette under skew instead of collapsing into a box.
//
// Offsets are in CELL units and apply to the tile's 4 vertical edges (plan corners), indexed in
// this fixed order: [0]=(-x,-z), [1]=(+x,-z), [2]=(+x,+z), [3]=(-x,+z). A tile occupies the unit
// cell [0..1] on x and z (× cellSize); these offsets push each corner post off that square and the
// interior bilinearly blends them. Because neighbouring tiles share corner posts, writing offsets as
// a smooth function of grid-corner position (see TileDeformField) keeps the wall gap-free.
[Serializable]
public class TileDeform
{
    public float[] dx;     // length 4: lateral X offset of each vertical edge (cell units)
    public float[] dz;     // length 4: lateral Z offset of each vertical edge (cell units)
    public float[] dyTop;  // length 4: height offset of each corner's TOP vertex (cell units); the
                           //           bottom vertices stay on the floor plane so floors keep stacking
}

[Serializable]
public class TileDef
{
    public int gridX;
    public int gridZ;
    public int floor;
    public string shapeId;              // "square", "wedge", "quarter_curve"
    // Per-tile orientation in euler degrees. `rotation` is the Y (yaw) axis and is kept as the
    // primary/legacy field (old data and quick 90° turns use it); rotationX/rotationZ add tilt on
    // the other two axes. The effective rotation is Quaternion.Euler(rotationX, rotation, rotationZ).
    // The Select tool snaps all three to 15° increments.
    public int rotation;
    public float rotationX;
    public float rotationZ;
    // face name → material id (e.g. "north" → "brick_red"). Null = use default material.
    public Dictionary<string, string> faceMaterials;
    // Optional skew/trapezoid geometry (acute/obtuse corners, sloped edges). Null = plain cube tile.
    public TileDeform deform;
}

[Serializable]
public class EmbeddedObjectDef
{
    public string instanceId;
    public string prefabType;
    public float[] localPos;            // [x, y, z] in building-local space
    public float rotationX;             // euler degrees; rotationY kept as the primary/legacy field.
    public float rotationY;             // Full XYZ lets smart-painted props align flush to sloped/
    public float rotationZ;             // skewed faces; old data (X=Z=0) keeps the yaw-only behavior.
    public float scale;
    // Smart-paint host tracking + placement rules (all default/empty for legacy & generated data).
    // hostFace == null means "no recorded host" — such props don't count toward per-tile constraints.
    public int    hostGridX;            // host tile grid coords (building-local)
    public int    hostGridZ;
    public int    hostFloor;
    public string hostFace;             // "north"/"east"/.../"top"; the face the prop was painted on
    public bool   exclusive;            // locks the host tile: no other decor may be painted on it
    public bool   fillsFace;            // a tile-sized prop seated on a face; re-painting it replaces it
                                        // (decors stack per face — see TileBuildingEditor.ReplaceConflictingDecor)
    // Deform-aware placement rules, captured from the DecorPalette entry at paint time so render
    // paths can RE-DERIVE localPos/rotation/scale from the host tile's current TileDeform
    // (DecorPlacement.TryReseat / ReseatAll). decorWidthFrac <= 0 (the default for all legacy and
    // generated data) means "no rules recorded" — the baked localPos/rotationXYZ are replayed
    // verbatim, exactly as before.
    public float decorWidthFrac;        // fraction of the face width the prop may span (0 = legacy)
    public float decorHeightFrac;       // fraction of the face height
    public int   decorAnchor;           // (int)DecorAlignment.Anchor: 0 Center, 1 Bottom, 2 Top
    public float decorSurfaceOffset;    // z-fight push along the face normal (meters)
    public int   decorMountAxis;        // (int)DecorAlignment.MountAxis (Auto = 0)
    public bool  decorFlipMount;
    // True = skipped by the low-performance VR viewer (WorldRenderer.skipOptional). Records saved
    // before this field existed load as required (false).
    public bool  optional;
}

[Serializable]
public class BuildingDef
{
    public string id;
    public string name;
    public int version;
    public List<string> tags;
    public float gridCellSize;
    public int floors;
    public float floorHeight;
    public List<TileDef> tiles;
    public List<EmbeddedObjectDef> embeddedObjects;
}

[Serializable]
public class BuildingInstance
{
    public string instanceId;
    public string buildingId;           // ref to BuildingDef.id
    public float[] position;            // [x, y, z] world space
    public float rotationX;             // euler degrees; rotationY kept as the primary/legacy field
    public float rotationY;
    public float rotationZ;
    public float scale;
    public bool included;
    // True = skipped by the low-performance VR viewer (WorldRenderer.skipOptional). Records saved
    // before this field existed load as required (false).
    public bool optional;
}

[Serializable]
public class ObjectInstance
{
    public string instanceId;
    public string prefabType;
    public float[] position;            // [x, y, z] world space
    public float rotationX;             // euler degrees; rotationY kept as the primary/legacy field
    public float rotationY;
    public float rotationZ;
    public float scale;
    // Optional non-uniform massing-box size [x, y, z] in meters (from generated_objects'
    // target_dimensions_ft). Null for normal prefabs, which use the uniform `scale` instead.
    public float[] boxSizeMeters;
    public bool included;
    // True only for instances scattered by the terrain editor's object brush. The eraser brush
    // deletes these and leaves layout-generated / pre-existing objects (default false) untouched.
    public bool brushPainted;
    // True = skipped by the low-performance VR viewer (WorldRenderer.skipOptional). Records saved
    // before this field existed load as required (false).
    public bool optional;
}

[Serializable]
public class TerrainZoneDef
{
    public string terrainType;
    public float[] rectMeters;          // [x0, z0, x1, z1] in meters
}

[Serializable]
public class PathDef
{
    public string id;                   // stable GUID for selection/deletion
    public string material;             // key into PathMaterialPalette (e.g. "brick", "dirt")
    public float width;                 // path width in meters
    public float[][] points;            // [[x, z], ...] sparse control points in meters
    public float smoothing = 0.5f;      // 0 = crisp/polyline corners, 1 = max spline curvature
}

// A fence run: a polyline (same point convention as PathDef) along which WorldRenderer.RenderFences
// repeats a panel prefab end-to-end (with posts at the joints), draped onto the terrain. The fence
// type chooses which prefabs/panel-length/height to use from the FencePalette. Like PathDef, the
// control points are the single source of truth — the segment GameObjects are derived geometry
// rebuilt on every render, never stored.
[Serializable]
public class FenceDef
{
    public string id;                   // stable GUID for selection/deletion
    public string fenceType;            // key into FencePalette (e.g. "picket", "lattice")
    public float[][] points;            // [[x, z], ...] sparse control points in meters
    public float smoothing = 0f;        // 0 = crisp/polyline corners (typical for fences), 1 = curvy
    public float height = 0f;           // optional height override in meters; 0 ⇒ FencePalette default
}

// Ground-surface stroke: a brush footprint of `radius` swept along `points`, rasterized into the
// terrain splatmap by WorldRenderer.PaintTerrain (alongside the rectangular TerrainZoneDefs).
// Drawn either freehand (many sampled points) or as a straight run (two points).
[Serializable]
public class SurfaceStrokeDef
{
    public string id;                   // stable GUID
    public string terrainType;          // key into TerrainRegistry (e.g. "grass", "concrete")
    public float radius;                // brush half-extent in meters (disc radius / half the square's side)
    // Rounded to 2 decimals (~1 cm) on serialize: strokes rasterize into ~0.5-1 m alphamap cells,
    // and full-precision floats made surfaceStrokes ~65% of a large environment's JSON (save
    // payloads, undo snapshots, disk records). Scoped to this member only.
    [JsonConverter(typeof(RoundedPointArrayConverter))]
    public float[][] points;            // [[x, z], ...] stroke centerline in meters
    // Brush footprint: "circle" (default) or "square". Square stamps rotate to each segment's
    // heading so a run drawn at any angle keeps clean parallel edges. Anything unrecognized (or a
    // missing key in older JSON) rasterizes as a circle.
    public string shape = "circle";
    // Fixed stamp angle in degrees, pinning every stamp to one orientation (e.g. a plaza laid on the
    // same grid as the buildings around it) instead of following the run. < 0 ⇒ auto: each stamp
    // takes its segment's heading. Only meaningful for "square" — a disc has no orientation.
    // See BrushGeometry.ResolveStampAngleRad.
    public float angleDeg = -1f;
}

// Height stroke: brush samples replayed in order onto the heightmap by WorldRenderer.ApplyHeightmap.
// Each sample is [x, z, amount] in world meters. `amount` is signed meters for "raise" and a 0..1
// blend weight for "smooth" / "flatten". Amounts are per sample (rate * dt, merged), so replay is
// frame rate independent and the live brush preview equals what a reload rebuilds. Samples closer
// than radius * HeightBrush.MERGE_FRACTION to the last stored one fold into it (HeightBrush.
// MergeAmount), so a long hold stores one sample rather than hundreds.
[Serializable]
public class HeightStrokeDef
{
    public string id;                   // stable GUID
    public string brush = "raise";      // "raise" | "smooth" | "flatten" (HeightBrush.KindKey)
    public float radius;                // meters; smoothstep falloff from center to rim
    public float targetHeight;          // flatten only: meters above the base plane, sampled at press
    public bool clipToLot = true;       // stamp only inside site.lotBoundary (evaluated at replay)
    // 3 decimals (1 mm): amounts from a single fast frame can be a few millimeters.
    [JsonConverter(typeof(RoundedPointArrayConverter), 3)]
    public float[][] points;            // [[x, z, amount], ...]
}

// A flat water body: a pond (closed ring) or a river (centerline ribbon). Rendered as one level
// translucent mesh at surfaceY, with its bed carved into the shared heightmap at replay (never
// stored as height strokes, so deleting the body removes the hole). Both heights are measured from
// the flat ground height (world y = 0, HeightBrush's base plane): the bed sits `depth` below it and
// the surface at `surfaceY`, which can never be below the bed. Points are world meters like
// HeightStrokeDef.points. Records saved before the field carry null waterBodies.
[Serializable]
public class WaterBodyDef
{
    public string id;                   // stable GUID
    public string kind = "pond";        // "pond" (closed ring) | "river" (centerline ribbon)
    public string material = "lake";    // WaterPalette entry id
    [JsonConverter(typeof(RoundedPointArrayConverter))]
    public float[][] points;            // [[x, z], ...] world meters: ring (pond) or centerline (river)
    public float width = 6f;            // river only, meters
    public float smoothing = 0.5f;      // river only: 0 = crisp corners, 1 = flowing (PathGeometry.Smooth)
    public float surfaceY = 0f;         // water surface, meters relative to the flat ground height (0 = level with it)
    public float depth = 1.5f;          // bed below the flat ground height, meters (surfaceY >= -depth + MIN_DEPTH)
    public float bankWidth = 2f;        // carve blend outside the outline, meters
    public bool clipToLot = true;       // carve only inside site.lotBoundary (evaluated at replay)
}

[Serializable]
public class SiteDef
{
    public float[] terrainSize;         // [width_m, length_m]
    // Optional world position of the terrain's min corner, [x_m, z_m]. null/short ⇒ the origin,
    // which is what every environment authored before this field used and still round-trips as.
    // An environment projected into a host's site (SiteFit.ProjectIntoSite) carries the site's
    // corner here, so when it becomes the active env the ground moves under it instead of staying
    // at the origin with the content floating off it. Scales and translates with the environment.
    public float[] terrainOrigin;
    public List<TerrainZoneDef> terrainZones;
    public List<PathDef> paths;
    public List<FenceDef> fences;       // nullable: old JSON without the field still loads (consumers null-guard)
    public List<SurfaceStrokeDef> surfaceStrokes;
    // Ground height sculpted with the Shape ground brush, replayed in order onto a flat base by
    // WorldRenderer.ApplyHeightmap. null/empty ⇒ flat; records saved before the field load with null.
    public List<HeightStrokeDef> heightStrokes;
    // Ponds and rivers (WaterBodyDef). null ⇒ none; records saved before the field load with null.
    public List<WaterBodyDef> waterBodies;
    public string scaleNote;
    // Parcel outline in world meters: [[x,z], ...] (same convention as paths/rectMeters).
    // null or <3 points ⇒ full-rectangle terrain (legacy behavior). When set, WorldRenderer
    // masks the terrain to this polygon and paints everything outside it as outsideTerrainType.
    public float[][] lotBoundary;
    public string outsideTerrainType = "water";  // TerrainRegistry key used outside the parcel
}

// A drawn plot inside a host environment that a generated child environment can fill. The boundary
// lives in HOST world meters (same [x,z] convention as lotBoundary/paths) and is not origin-anchored.
// The fill stays its own EnvironmentDef record on the server; on load it is deep-copied, scaled and
// translated into this boundary's bounding box (SiteFit.ProjectIntoSite) and rendered as a managed
// backdrop. null fillEnvironmentId = empty site.
[Serializable]
public class SitePlotDef
{
    public string id;                 // stable GUID
    public string name;               // user label, "Site 1" default
    public float[][] boundary;        // [[x, z], ...] host-world meters, >= 3 points
    public string fillEnvironmentId;  // generated child env record id; null = empty site
}

[Serializable]
public class EnvironmentDef
{
    public string id;
    public string name;
    public int version;
    public List<string> tags;
    // Persistent read-only flag for a "digital twin" backdrop: a locked env can still be
    // loaded and made active (it owns/paints the shared terrain), but every mutation —
    // edit tools, Save, auto-save, Archive — refuses until it is unlocked. Distinct from
    // WorldRenderer's transient backdrop dim/collider lock, which is just "not active".
    // Round-trips through the server JSON like every other field; old records default false.
    public bool locked;
    public SiteDef site;
    public List<BuildingInstance> buildingInstances;
    public List<ObjectInstance> objectInstances;
    // Drawn plots that generated child environments can fill (see SitePlotDef). nullable: old JSON
    // without the field still loads (consumers null-guard, same precedent as SiteDef.fences).
    public List<SitePlotDef> sites;
}

// Lightweight summary returned by GET /api/environments (list endpoint)
[Serializable]
public class EnvironmentSummary
{
    public string id;
    public string name;
    public int version;
    public List<string> tags;
    public string kind;                 // "user" | "generated"
    public string updated;              // ISO 8601 timestamp
    public bool favorite;               // server-managed; pins the row to the top of the list
    public bool locked;                 // read-only "digital twin" flag (see EnvironmentDef.locked)
}

// Lightweight summary returned by GET /api/buildings (list endpoint)
[Serializable]
public class BuildingSummary
{
    public string id;
    public string name;
    public int version;
    public List<string> tags;
    public string kind;                 // "static" | "cached"
    public string updated;              // ISO 8601 timestamp
    public bool favorite;               // server-managed; pins the row to the top of the list
}

// Serializes a [[x, z], ...] float point array with each value rounded to `decimals` places
// (default 2, ~1 cm). Reading is a plain array read, so the first serialize after loading old
// full-precision data rounds it and every later round-trip is byte-identical (idempotent). Applied
// per-member via [JsonConverter] (SurfaceStrokeDef.points at 2, HeightStrokeDef.points at 3) --
// never registered globally.
public class RoundedPointArrayConverter : JsonConverter
{
    private readonly int _decimals;

    public RoundedPointArrayConverter() : this(2) { }
    public RoundedPointArrayConverter(int decimals) { _decimals = Math.Max(0, decimals); }

    public override bool CanConvert(Type objectType) => objectType == typeof(float[][]);

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        var points = (float[][])value;
        if (points == null) { writer.WriteNull(); return; }
        writer.WriteStartArray();
        foreach (var p in points)
        {
            if (p == null) { writer.WriteNull(); continue; }
            writer.WriteStartArray();
            foreach (var v in p) writer.WriteValue((float)Math.Round(v, _decimals));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        => serializer.Deserialize<float[][]>(reader);
}
