using System;
using System.Collections.Generic;

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
    public bool   fillsFace;            // one tile-sized prop per face; re-painting the face replaces it
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

// One control point for the optional terrain heightmap: at world (x, z) meters the ground is raised
// to `height` meters. WorldRenderer.ApplyHeightmap interpolates a low-res heightmap from the set.
// Sparse — a few points describe a gentle grade; null/empty list ⇒ flat terrain (the default).
[Serializable]
public class GradePointDef
{
    public float x;        // world meters
    public float z;        // world meters
    public float height;   // target elevation in meters (0 = base plane)
}

[Serializable]
public class SiteDef
{
    public float[] terrainSize;         // [width_m, length_m]
    public List<TerrainZoneDef> terrainZones;
    public List<PathDef> paths;
    public List<FenceDef> fences;       // nullable: old JSON without the field still loads (consumers null-guard)
    public List<SurfaceStrokeDef> surfaceStrokes;
    public string scaleNote;
    // Optional gentle elevation. null/empty ⇒ flat (today's behavior); the generation pipeline never
    // sets it, so generated environments stay flat and JSON round-trips unchanged. WorldRenderer bakes
    // a low-res heightmap from these once per load/edit (objects & paths then drape onto it for free).
    public List<GradePointDef> gradePoints;
    public float maxGradeHeight = 30f;  // terrain Y range (m) used when grade points exist; caps slope
    // Parcel outline in world meters: [[x,z], ...] (same convention as paths/rectMeters).
    // null or <3 points ⇒ full-rectangle terrain (legacy behavior). When set, WorldRenderer
    // masks the terrain to this polygon and paints everything outside it as outsideTerrainType.
    public float[][] lotBoundary;
    public string outsideTerrainType = "water";  // TerrainRegistry key used outside the parcel
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
