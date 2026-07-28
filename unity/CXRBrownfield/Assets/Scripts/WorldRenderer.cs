using System;
using System.Collections.Generic;
using UnityEngine;

// Renders an EnvironmentDef onto a Unity terrain.
// Replaces WorldGenerator's rendering path with bug-fixed, schema-aware logic:
//   - Terrain zones use rectMeters (already in world meters), not hardcoded 0-1000 canvas coords.
//   - Object placement honors rotation_deg and scale_multiplier (consolidates ObjectPlacer).
//   - Building instances look up BuildingDef by ID and pass tile-grid dimensions to BuildingGenerator.
//   - Missing terrain keys produce a Debug.LogError and continue (never silently skip).
//   - Missing prefab_types spawn a magenta missing-texture placeholder so the gap stays visible/editable.
public class WorldRenderer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Terrain targetTerrain;               // USER WIRES THIS IN INSPECTOR
    [SerializeField] private PrefabRegistry prefabRegistry;       // USER WIRES THIS IN INSPECTOR
    [SerializeField] private TerrainRegistry terrainRegistry;     // USER WIRES THIS IN INSPECTOR
    [SerializeField] private BuildingGenerator buildingGenerator; // USER WIRES THIS IN INSPECTOR (massing fallback)
    [SerializeField] private TileShapePalette tileShapePalette;   // USER WIRES THIS IN INSPECTOR (tile-based building rendering)
    [SerializeField] private MaterialPalette materialPalette;     // USER WIRES THIS IN INSPECTOR (tile face materials)
    [SerializeField] private PathMaterialPalette pathMaterialPalette; // USER WIRES THIS IN INSPECTOR (path ribbon materials)
    [SerializeField] private FencePalette fencePalette;           // USER WIRES THIS IN INSPECTOR (fence segment prefabs)

    [Header("Path rendering")]
    [SerializeField] private float pathYEpsilon = 0.05f;          // lift above terrain to avoid z-fighting
    // Per-path micro-lift (m): each path rendered after the first is raised by stackIndex*this so two
    // overlapping/parallel ribbons are never coplanar (no z-fighting). ~4mm is invisible in scene.
    public const float PathStackStep = 0.004f;

    [Header("Settings")]
    [SerializeField] private float prefabScaleFactor = 1f;
    [SerializeField] private float defaultYRotation  = 90f;

    // Per-environment render state. Multiple environments can be rendered at once (overlaid at
    // their shared origin); only the active one is interactive — see SetActiveEnvironment.
    private class EnvRender
    {
        public EnvironmentDef env;                 // kept so SetActiveEnvironment can repaint terrain
        public Transform      root;
        public readonly Dictionary<string, GameObject> instanceToGO = new();
    }

    // env.id → its rendered geometry. Order is insertion order; not significant for overlay.
    private readonly Dictionary<string, EnvRender> _envRenders = new();
    // The single editable/saveable environment. Its colliders are enabled and it paints the
    // shared terrain; all other loaded environments are locked (colliders off) and dimmed.
    private string _activeEnvId;

    // Palettes/registries exposed so other tools (e.g. EditController) can reuse the same assets
    // wired here instead of requiring a second inspector assignment.
    public PathMaterialPalette PathMaterialPalette => pathMaterialPalette;
    public FencePalette        FencePalette        => fencePalette;
    public TerrainRegistry     TerrainRegistry     => terrainRegistry;
    public PrefabRegistry      PrefabRegistry      => prefabRegistry;

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    // Renders (or re-renders) one environment into its own root. By default the rendered
    // environment becomes the active (editable) one — it paints the shared terrain and its
    // colliders are enabled; pass makeActive:false to load it as a locked backdrop.
    public void RenderEnvironment(EnvironmentDef env, IReadOnlyDictionary<string, BuildingDef> buildingDefs,
                                  bool makeActive = true)
    {
        if (env == null) { Debug.LogError("[WorldRenderer] RenderEnvironment: env is null."); return; }

        var er = GetOrCreateEnvRender(env);
        er.env = env;
        ClearEnvRender(er);   // only this environment's geometry, never the others

        // If this env will paint the shared terrain, apply its size/heightmap BEFORE spawning
        // geometry: objects and path ribbons ground themselves via Terrain.SampleHeight, so
        // sampling the outgoing terrain (e.g. on undo of a grade/resize edit) leaves them
        // floating or buried. The tail SetActiveEnvironment re-applies cheaply (no-op on
        // no-change) and paints the splat.
        bool willBeActive = makeActive || _activeEnvId == null || env.id == _activeEnvId;
        if (willBeActive && env.site != null)
        {
            ApplyTerrainSize(env.site);
            ApplyHeightmap(env.site);
        }

        if (env.objectInstances != null)
            RenderObjectInstances(env.objectInstances, er);

        if (env.buildingInstances != null)
            RenderBuildingInstances(env.buildingInstances, buildingDefs ?? new Dictionary<string, BuildingDef>(), er);

        if (env.site?.paths != null)
            RenderPaths(env.site.paths, er);

        if (env.site?.fences != null)
            RenderFences(env.site.fences, er);

        RenderLotFrame(env.site, er);

        // Adopt as active if requested, or if nothing is active yet. Otherwise just refresh
        // lock/dim state so this freshly-rendered (or re-rendered) env reflects its role.
        if (makeActive || _activeEnvId == null) SetActiveEnvironment(env.id);
        else                                    RefreshLockStates();
    }

    // Marks one loaded environment as the editable/saveable one: repaints the shared terrain
    // from its site, enables its colliders, and locks + dims every other loaded environment.
    public void SetActiveEnvironment(string envId)
    {
        _activeEnvId = envId;
        if (envId != null && _envRenders.TryGetValue(envId, out var er) && er.env?.site != null)
        {
            ApplyTerrainSize(er.env.site);
            ApplyHeightmap(er.env.site);
            PaintTerrain(er.env.site);
        }
        RefreshLockStates();
    }

    // Sizes the in-scene Terrain to the environment's real-world site.terrainSize (meters) so the
    // visible ground is true scale (1 unit = 1 m) and every coordinate that's normalized against
    // terrainData.size (zones, strokes, lot mask, paths) lands correctly. The terrain's Y (height
    // range) is preserved — flat sites keep their existing height ceiling. Width/length are clamped
    // to a sane band so a malformed/zero terrainSize can't collapse or blow up the ground. Public so
    // EditController can re-apply after a Site Settings edit or Scale Calibration.
    public void ApplyTerrainSize(SiteDef site)
    {
        if (targetTerrain == null || site?.terrainSize == null || site.terrainSize.Length < 2) return;
        float w = site.terrainSize[0];
        float l = site.terrainSize[1];
        if (float.IsNaN(w) || float.IsNaN(l) || w <= 0f || l <= 0f) return;
        w = Mathf.Clamp(w, 1f, 4000f);
        l = Mathf.Clamp(l, 1f, 4000f);
        var tData = targetTerrain.terrainData;
        float y = tData.size.y > 0f ? tData.size.y : 1f;   // preserve the existing height range
        if (!Mathf.Approximately(tData.size.x, w) || !Mathf.Approximately(tData.size.z, l))
            tData.size = new Vector3(w, y, l);
    }

    // Lightweight live preview of a terrain resize from raw width/length (meters), without touching
    // any environment data — used by the editor's Lot tool while dragging a rectangle handle so the
    // ground plane tracks the drag. The committed size is written through ApplyTerrainSize on release.
    public void PreviewTerrainSize(float width, float length)
    {
        if (targetTerrain == null) return;
        if (float.IsNaN(width) || float.IsNaN(length) || width <= 0f || length <= 0f) return;
        float w = Mathf.Clamp(width,  1f, 4000f);
        float l = Mathf.Clamp(length, 1f, 4000f);
        var tData = targetTerrain.terrainData;
        float y = tData.size.y > 0f ? tData.size.y : 1f;
        if (!Mathf.Approximately(tData.size.x, w) || !Mathf.Approximately(tData.size.z, l))
            tData.size = new Vector3(w, y, l);
    }

    // Optional gentle elevation. With no grade points the terrain is flattened to the base plane
    // (today's behavior). With grade points, a LOW-RES heightmap is interpolated from them (inverse-
    // distance weighting) and baked once via SetHeights — objects (SampleHeight) and paths (heightAt)
    // then drape onto it automatically. Kept cheap (≤65² samples) and one-shot for VR. Public so the
    // editor can re-bake after an elevation edit. Call after ApplyTerrainSize.
    public void ApplyHeightmap(SiteDef site)
    {
        if (targetTerrain == null) return;
        var tData = targetTerrain.terrainData;
        var pts = site?.gradePoints;

        // No elevation ⇒ leave the scene terrain alone, UNLESS we previously baked a grade (e.g.
        // switching from a graded env to a flat one), in which case flatten back to the base plane.
        // This keeps a pristine, hand-authored scene heightmap untouched for flat environments.
        if (pts == null || pts.Count == 0)
        {
            if (_heightmapBaked)
            {
                int flatRes = tData.heightmapResolution;
                tData.SetHeights(0, 0, new float[flatRes, flatRes]);
                _heightmapBaked = false;
            }
            return;
        }

        // Keep the bake small for VR; a 65² heightmap is plenty for gentle, large-scale grade.
        int res = Mathf.Min(65, tData.heightmapResolution);
        if (res != tData.heightmapResolution) tData.heightmapResolution = res;
        res = tData.heightmapResolution;

        float maxH = site.maxGradeHeight > 0.01f ? site.maxGradeHeight : 30f;
        if (!Mathf.Approximately(tData.size.y, maxH))
            tData.size = new Vector3(tData.size.x, maxH, tData.size.z);

        float sizeX = tData.size.x, sizeZ = tData.size.z;
        var heights = new float[res, res];
        for (int zi = 0; zi < res; zi++)
        {
            float wz = (zi / (float)(res - 1)) * sizeZ;   // heightmap row index runs along Z
            for (int xi = 0; xi < res; xi++)
            {
                float wx = (xi / (float)(res - 1)) * sizeX;
                float h  = SampleGrade(pts, wx, wz);
                heights[zi, xi] = Mathf.Clamp01(h / maxH);
            }
        }
        tData.SetHeights(0, 0, heights);
        _heightmapBaked = true;
    }

    // Tracks whether we baked a non-flat heightmap, so a later flat env flattens back instead of
    // stomping a pristine scene-authored terrain.
    private bool _heightmapBaked;

    // Inverse-distance-weighted elevation (meters) at world (x, z) from the sparse grade points.
    private static float SampleGrade(List<GradePointDef> pts, float x, float z)
    {
        float wsum = 0f, hsum = 0f;
        foreach (var p in pts)
        {
            if (p == null) continue;
            float dx = x - p.x, dz = z - p.z;
            float d2 = dx * dx + dz * dz;
            if (d2 < 1e-4f) return p.height;       // sitting on a control point
            float w = 1f / d2;
            wsum += w;
            hsum += w * p.height;
        }
        return wsum > 0f ? hsum / wsum : 0f;
    }

    // Removes one environment's geometry (e.g. on close/archive). If it was the active one,
    // the caller is responsible for choosing a new active env (or leaving none).
    public void UnloadEnvironment(string envId)
    {
        if (envId == null || !_envRenders.TryGetValue(envId, out var er)) return;
        DestroyRoot(er);
        _envRenders.Remove(envId);
        if (_activeEnvId == envId) _activeEnvId = null;
    }

    // Clears every rendered environment.
    public void ClearRendered()
    {
        foreach (var er in _envRenders.Values) DestroyRoot(er);
        _envRenders.Clear();
        _activeEnvId = null;
    }

    // Exposed for EditController (selection) and BakePass (mesh combine). Returns the active
    // environment's root — the one being authored — so BakePass combines what's being edited.
    public Transform GetRoot() =>
        _activeEnvId != null && _envRenders.TryGetValue(_activeEnvId, out var er) ? er.root : null;

    // Resolves an instance id to its GameObject. The active environment is searched first
    // (edits only ever touch it); other loaded environments are a fallback.
    public GameObject GetInstanceGO(string id)
    {
        if (_activeEnvId != null && _envRenders.TryGetValue(_activeEnvId, out var active)
            && active.instanceToGO.TryGetValue(id, out var go)) return go;
        foreach (var er in _envRenders.Values)
            if (er.instanceToGO.TryGetValue(id, out var g)) return g;
        return null;
    }

    // -----------------------------------------------------------------------
    // Per-environment root + lock/dim management
    // -----------------------------------------------------------------------

    private EnvRender GetOrCreateEnvRender(EnvironmentDef env)
    {
        if (_envRenders.TryGetValue(env.id, out var er) && er.root != null) return er;
        er ??= new EnvRender();
        var go = new GameObject($"RenderedEnvironment:{env.name}");
        go.transform.SetParent(transform, false);
        er.root = go.transform;
        _envRenders[env.id] = er;
        return er;
    }

    private static void ClearEnvRender(EnvRender er)
    {
        er.instanceToGO.Clear();
        if (er.root == null) return;
        for (int i = er.root.childCount - 1; i >= 0; i--)
        {
            var child = er.root.GetChild(i);
            if (Application.isPlaying) Destroy(child.gameObject);
            else                       DestroyImmediate(child.gameObject);
        }
    }

    private static void DestroyRoot(EnvRender er)
    {
        if (er?.root == null) return;
        if (Application.isPlaying) Destroy(er.root.gameObject);
        else                       DestroyImmediate(er.root.gameObject);
        er.root = null;
        er.instanceToGO.Clear();
    }

    private void RefreshLockStates()
    {
        foreach (var kv in _envRenders)
            ApplyLockState(kv.Value, active: kv.Key == _activeEnvId);
    }

    // Locked (non-active) environments are non-interactive (colliders off) and visually dimmed
    // so it's clear which environment is being edited. The active one is restored to normal.
    private static readonly int BaseColorProp = Shader.PropertyToID("_BaseColor"); // URP Lit
    private static readonly int ColorProp     = Shader.PropertyToID("_Color");     // Built-in/legacy
    private static readonly Color LOCKED_TINT = new(0.55f, 0.6f, 0.7f);
    private MaterialPropertyBlock _dimBlock;

    private void ApplyLockState(EnvRender er, bool active)
    {
        if (er?.root == null) return;

        foreach (var col in er.root.GetComponentsInChildren<Collider>(true))
            col.enabled = active;

        foreach (var rend in er.root.GetComponentsInChildren<Renderer>(true))
        {
            if (active)
            {
                // Restore: clears any dim tint. EditController re-applies its selection
                // highlight after a re-render, so wiping the block here is safe.
                rend.SetPropertyBlock(null);
            }
            else
            {
                _dimBlock ??= new MaterialPropertyBlock();
                _dimBlock.Clear();
                rend.GetPropertyBlock(_dimBlock);
                _dimBlock.SetColor(BaseColorProp, LOCKED_TINT);
                _dimBlock.SetColor(ColorProp,     LOCKED_TINT);
                rend.SetPropertyBlock(_dimBlock);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Terrain painting — fixed: uses rectMeters (world meters) not canvas coords
    // -----------------------------------------------------------------------

    private void PaintTerrain(SiteDef site)
    {
        if (targetTerrain == null)  { Debug.LogError("[WorldRenderer] targetTerrain not assigned.");  return; }
        if (terrainRegistry == null){ Debug.LogError("[WorldRenderer] terrainRegistry not assigned."); return; }

        TerrainData tData = targetTerrain.terrainData;
        int res = tData.alphamapResolution;
        Vector3 terrainSize = tData.size;

        int layerCount = terrainRegistry.entries.Count;
        var layers     = new TerrainLayer[layerCount];
        var keyToIndex = new Dictionary<string, int>(layerCount);
        for (int i = 0; i < layerCount; i++)
        {
            layers[i] = terrainRegistry.entries[i].terrainLayer;
            keyToIndex[terrainRegistry.entries[i].key.ToLower()] = i;
        }
        tData.terrainLayers = layers;

        float[,,] map = new float[res, res, layerCount];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
                map[y, x, 0] = 1f;  // default to first layer

        // Rectangular zones (from generation) first, then freehand strokes (from the editor) on
        // top — both are pure functions of the data, so a reload reproduces the same splatmap.
        if (site.terrainZones != null)
            foreach (var zone in site.terrainZones)
            {
                if (zone?.rectMeters == null || zone.rectMeters.Length < 4) continue;

                string key = zone.terrainType?.ToLower() ?? "";
                if (!keyToIndex.TryGetValue(key, out int idx))
                {
                    Debug.LogError($"[WorldRenderer] Terrain type '{zone.terrainType}' not found in TerrainRegistry.");
                    continue;
                }

                // rectMeters is in world meters; normalize against actual terrain dimensions.
                int xStart = Mathf.Clamp(Mathf.RoundToInt((zone.rectMeters[0] / terrainSize.x) * res), 0, res);
                int yStart = Mathf.Clamp(Mathf.RoundToInt((zone.rectMeters[1] / terrainSize.z) * res), 0, res);
                int xEnd   = Mathf.Clamp(Mathf.RoundToInt((zone.rectMeters[2] / terrainSize.x) * res), 0, res);
                int yEnd   = Mathf.Clamp(Mathf.RoundToInt((zone.rectMeters[3] / terrainSize.z) * res), 0, res);

                for (int y = yStart; y < yEnd; y++)
                    for (int x = xStart; x < xEnd; x++)
                    {
                        for (int l = 0; l < layerCount; l++) map[y, x, l] = 0f;
                        map[y, x, idx] = 1f;
                    }
            }

        if (site.surfaceStrokes != null)
            foreach (var stroke in site.surfaceStrokes)
            {
                if (stroke?.points == null || stroke.points.Length < 1) continue;

                string key = stroke.terrainType?.ToLower() ?? "";
                if (!keyToIndex.TryGetValue(key, out int idx))
                {
                    Debug.LogError($"[WorldRenderer] Terrain type '{stroke.terrainType}' not found in TerrainRegistry.");
                    continue;
                }

                float radius = Mathf.Max(0.1f, stroke.radius);
                bool square = IsSquareShape(stroke.shape);
                float angleDeg = stroke.angleDeg;
                WalkStroke(stroke.points, radius * 0.5f, (center, dirRad) =>
                    StampIntoMap(map, res, layerCount, terrainSize, center, radius, idx, square,
                                 BrushGeometry.ResolveStampAngleRad(angleDeg, dirRad)));
            }

        // Parcel mask LAST so the lot edge wins over zones/strokes: every cell whose center falls
        // outside site.lotBoundary is repainted with outsideTerrainType (water/void). null or <3
        // points leaves the full rectangle untouched (legacy behavior).
        if (site.lotBoundary != null && site.lotBoundary.Length >= 3)
        {
            string outKey = (site.outsideTerrainType ?? "water").ToLower();
            if (keyToIndex.TryGetValue(outKey, out int outIdx))
            {
                for (int y = 0; y < res; y++)
                {
                    float zMeters = ((y + 0.5f) / res) * terrainSize.z;
                    for (int x = 0; x < res; x++)
                    {
                        float xMeters = ((x + 0.5f) / res) * terrainSize.x;
                        if (EnvironmentScale.PointInPolygon(xMeters, zMeters, site.lotBoundary)) continue;
                        for (int l = 0; l < layerCount; l++) map[y, x, l] = 0f;
                        map[y, x, outIdx] = 1f;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[WorldRenderer] outsideTerrainType '{site.outsideTerrainType}' " +
                                 "not found in TerrainRegistry; skipping lot-boundary mask.");
            }
        }

        tData.SetAlphamaps(0, 0, map);
    }

    // A square footprint's corners reach radius*√2, so its scan box must be that much wider than
    // a disc's. Kept as one constant so both stamp paths size their bounds identically.
    private const float SQUARE_REACH = 1.4143f;

    public static bool IsSquareShape(string shape) =>
        string.Equals(shape, "square", StringComparison.OrdinalIgnoreCase);

    // True when alphamap cell (x, y) falls inside the brush footprint centered at `centerMeters`
    // (whatever space the caller stamps in — world meters offline, terrain-local meters live).
    // Circles keep the normalized-index ellipse test so existing strokes rasterize bit-identically;
    // squares test an axis-box in meters, rotated by `dirRad`, giving a run clean parallel edges.
    private static bool InBrush(int x, int y, int cx, int cy, int rx, int ry, int res,
                                Vector3 terrainSize, Vector3 centerMeters,
                                float radius, bool square, float dirRad)
    {
        if (!square)
        {
            // Normalized ellipse test (cells are square in index space but the terrain may not be).
            float nx = rx > 0 ? (x - cx) / (float)rx : 0f;
            float ny = ry > 0 ? (y - cy) / (float)ry : 0f;
            return nx * nx + ny * ny <= 1f;
        }

        // Rotation is only meaningful in meters, so leave index space for the box test.
        float mx = ((x + 0.5f) / res) * terrainSize.x - centerMeters.x;
        float mz = ((y + 0.5f) / res) * terrainSize.z - centerMeters.z;
        float c = Mathf.Cos(dirRad), s = Mathf.Sin(dirRad);
        float lx =  mx * c + mz * s;   // rotate by -dirRad into the stamp's own frame
        float lz = -mx * s + mz * c;
        return Mathf.Abs(lx) <= radius && Mathf.Abs(lz) <= radius;
    }

    // Sets one filled brush footprint of `idx`'s layer (others zeroed) into a whole in-memory
    // alphamap. Center is in world meters; radius in meters. Used by the rasterizer in PaintTerrain.
    private static void StampIntoMap(float[,,] map, int res, int layerCount, Vector3 terrainSize,
                                     Vector3 centerMeters, float radius, int idx,
                                     bool square = false, float dirRad = 0f) =>
        StampIntoBlock(map, 0, 0, res, res, res, layerCount, terrainSize,
                       centerMeters, radius, idx, square, dirRad);

    // Sets one filled brush footprint into a *sub-block* of the alphamap: `bx0/by0` locate the
    // block's origin in alphamap cells and `bw/bh` are its dims, so the partial-update paths can
    // rasterize into a small window and push it with one SetAlphamaps. `centerMeters` must be in the
    // same space the block's indices were derived from (world meters offline, terrain-local live).
    private static void StampIntoBlock(float[,,] block, int bx0, int by0, int bw, int bh,
                                       int res, int layerCount, Vector3 terrainSize,
                                       Vector3 centerMeters, float radius, int idx,
                                       bool square, float dirRad)
    {
        float reach = square ? radius * SQUARE_REACH : radius;
        int cx = Mathf.RoundToInt((centerMeters.x / terrainSize.x) * res);
        int cy = Mathf.RoundToInt((centerMeters.z / terrainSize.z) * res);
        int rx = Mathf.CeilToInt((reach / terrainSize.x) * res);
        int ry = Mathf.CeilToInt((reach / terrainSize.z) * res);

        // The ellipse test is normalized against the scan box, so it must see disc-sized bounds.
        int erx = square ? rx : Mathf.CeilToInt((radius / terrainSize.x) * res);
        int ery = square ? ry : Mathf.CeilToInt((radius / terrainSize.z) * res);

        int yEnd = Mathf.Min(Mathf.Min(res, by0 + bh), cy + ry + 1);
        int xEnd = Mathf.Min(Mathf.Min(res, bx0 + bw), cx + rx + 1);
        for (int y = Mathf.Max(by0, cy - ry); y < yEnd; y++)
            for (int x = Mathf.Max(bx0, cx - rx); x < xEnd; x++)
            {
                if (!InBrush(x, y, cx, cy, erx, ery, res, terrainSize, centerMeters, radius, square, dirRad)) continue;
                for (int l = 0; l < layerCount; l++) block[y - by0, x - bx0, l] = 0f;
                block[y - by0, x - bx0, idx] = 1f;
            }
    }

    // Walks a stroke centerline and invokes `stamp(center, dirRad)` at every sample, inserting
    // intermediate samples no further apart than `step` so a fast drag — or a long straight run —
    // rasterizes as one continuous band. `dirRad` is the heading (atan2(dz, dx)) of the segment the
    // sample belongs to; square stamps rotate to it. A single-point stroke stamps once, axis-aligned.
    private static void WalkStroke(float[][] points, float step, Action<Vector3, float> stamp)
    {
        if (points == null || stamp == null) return;
        step = Mathf.Max(0.05f, step);

        var pts = new List<Vector3>(points.Length);
        foreach (var p in points)
            if (p != null && p.Length >= 2) pts.Add(new Vector3(p[0], 0f, p[1]));

        if (pts.Count == 0) return;
        if (pts.Count == 1) { stamp(pts[0], 0f); return; }

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            Vector3 a = pts[i], b = pts[i + 1];
            float dirRad = Mathf.Atan2(b.z - a.z, b.x - a.x);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / step));
            // Endpoints are inclusive, so a shared joint is stamped twice — harmless, the write is
            // idempotent, and it keeps every segment's own heading at its ends.
            for (int s = 0; s <= steps; s++) stamp(Vector3.Lerp(a, b, s / (float)steps), dirRad);
        }
    }

    // Index of `terrainType` in the live terrain's layer list, or -1 (logged) when unknown.
    private int ResolveLiveLayerIndex(string terrainType)
    {
        if (targetTerrain == null || terrainRegistry == null) return -1;
        int layerCount = targetTerrain.terrainData.terrainLayers?.Length ?? 0;
        for (int i = 0; i < terrainRegistry.entries.Count && i < layerCount; i++)
            if (string.Equals(terrainRegistry.entries[i].key, terrainType, StringComparison.OrdinalIgnoreCase))
                return i;
        Debug.LogError($"[WorldRenderer] Terrain type '{terrainType}' not found in TerrainRegistry.");
        return -1;
    }

    // Stamps a single brush footprint directly into the LIVE terrain alphamap for immediate feedback
    // during a drag. The stroke is also recorded in the data model, so PaintTerrain reproduces it
    // authoritatively on reload / active-env switch.
    public void StampSurfaceLive(Vector3 worldPos, float radius, string terrainType,
                                 bool square = false, float dirRad = 0f)
    {
        if (targetTerrain == null || terrainRegistry == null) return;

        TerrainData tData = targetTerrain.terrainData;
        int res = tData.alphamapResolution;
        int layerCount = tData.terrainLayers != null ? tData.terrainLayers.Length : 0;
        if (layerCount == 0) return;

        int idx = ResolveLiveLayerIndex(terrainType);
        if (idx < 0) return;

        Vector3 terrainPos  = targetTerrain.transform.position;
        Vector3 terrainSize = tData.size;
        radius = Mathf.Max(0.1f, radius);
        float reach = square ? radius * SQUARE_REACH : radius;

        // Terrain-local meters: the footprint test must match the space the indices are built from.
        var centerLocal = new Vector3(worldPos.x - terrainPos.x, 0f, worldPos.z - terrainPos.z);

        int cx = Mathf.RoundToInt((centerLocal.x / terrainSize.x) * res);
        int cy = Mathf.RoundToInt((centerLocal.z / terrainSize.z) * res);
        int rx = Mathf.CeilToInt((reach / terrainSize.x) * res);
        int ry = Mathf.CeilToInt((reach / terrainSize.z) * res);

        // The ellipse test is normalized against the scan box, so it must see disc-sized bounds.
        int erx = square ? rx : Mathf.CeilToInt((radius / terrainSize.x) * res);
        int ery = square ? ry : Mathf.CeilToInt((radius / terrainSize.z) * res);

        int x0 = Mathf.Clamp(cx - rx, 0, res - 1);
        int y0 = Mathf.Clamp(cy - ry, 0, res - 1);
        int w  = Mathf.Clamp(cx + rx + 1, 0, res) - x0;
        int h  = Mathf.Clamp(cy + ry + 1, 0, res) - y0;
        if (w <= 0 || h <= 0) return;

        float[,,] block = tData.GetAlphamaps(x0, y0, w, h);
        StampIntoBlock(block, x0, y0, w, h, res, layerCount, terrainSize,
                       centerLocal, radius, idx, square, dirRad);
        tData.SetAlphamaps(x0, y0, block);
    }

    // -----------------------------------------------------------------------
    // Live straight-run painting
    //
    // A straight run's geometry is *replaced* on every mouse move, not appended to — swing the
    // direction around mid-drag and an append-only stamp would leave a smeared fan behind. So the
    // pristine alphamap under the run is snapshotted once, and every update repaints that snapshot
    // and re-stamps the run's current shape into it: ground the run has moved off reverts, and the
    // whole window goes down in a single SetAlphamaps.
    // -----------------------------------------------------------------------

    private float[,,] _liveBase;                             // pristine snapshot, [y, x, layer]
    private float[,,] _liveWork;                             // scratch the run is stamped into
    private int  _liveX0, _liveY0, _liveW, _liveH;           // snapshot window, in alphamap cells
    private bool _liveRunActive;

    // Starts a live run. Pair with EndLiveSurfaceRun — without it the snapshot leaks and a later
    // run would restore stale ground.
    public void BeginLiveSurfaceRun()
    {
        _liveRunActive = true;
        _liveBase = _liveWork = null;
        _liveW = _liveH = 0;
    }

    // Repaints the run at its current shape. Cheap enough for every frame of a drag: one array copy
    // plus one SetAlphamaps over the run's bounding window (not the whole terrain).
    public void UpdateLiveSurfaceRun(SurfaceStrokeDef stroke)
    {
        if (!_liveRunActive || targetTerrain == null || stroke?.points == null) return;
        int idx = ResolveLiveLayerIndex(stroke.terrainType);
        if (idx < 0) return;

        TerrainData tData = targetTerrain.terrainData;
        int res = tData.alphamapResolution;
        int layerCount = tData.terrainLayers != null ? tData.terrainLayers.Length : 0;
        if (layerCount == 0) return;
        if (!StrokeCellRect(stroke, res, out int nx0, out int ny0, out int nw, out int nh)) return;

        // Grow the snapshot when the run leaves it. Put back what we painted first, so the enlarged
        // snapshot captures clean ground rather than our own band.
        bool contained = _liveBase != null && nx0 >= _liveX0 && ny0 >= _liveY0 &&
                         nx0 + nw <= _liveX0 + _liveW && ny0 + nh <= _liveY0 + _liveH;
        if (!contained)
        {
            RestoreLiveSurfaceRun();
            int ux0 = _liveBase == null ? nx0 : Mathf.Min(nx0, _liveX0);
            int uy0 = _liveBase == null ? ny0 : Mathf.Min(ny0, _liveY0);
            int ux1 = _liveBase == null ? nx0 + nw : Mathf.Max(nx0 + nw, _liveX0 + _liveW);
            int uy1 = _liveBase == null ? ny0 + nh : Mathf.Max(ny0 + nh, _liveY0 + _liveH);
            _liveX0 = ux0; _liveY0 = uy0; _liveW = ux1 - ux0; _liveH = uy1 - uy0;
            _liveBase = tData.GetAlphamaps(_liveX0, _liveY0, _liveW, _liveH);
            _liveWork = new float[_liveH, _liveW, layerCount];
        }

        Array.Copy(_liveBase, _liveWork, _liveBase.Length);   // start from clean ground every update

        Vector3 tPos = targetTerrain.transform.position, tSize = tData.size;
        float radius = Mathf.Max(0.1f, stroke.radius);
        bool square = IsSquareShape(stroke.shape);
        float angleDeg = stroke.angleDeg;
        WalkStroke(stroke.points, radius * 0.5f, (center, dirRad) =>
            StampIntoBlock(_liveWork, _liveX0, _liveY0, _liveW, _liveH, res, layerCount, tSize,
                           new Vector3(center.x - tPos.x, 0f, center.z - tPos.z), radius, idx,
                           square, BrushGeometry.ResolveStampAngleRad(angleDeg, dirRad)));

        tData.SetAlphamaps(_liveX0, _liveY0, _liveWork);
    }

    // Ends the run. `keepPaint` false wipes it back to the snapshot (cancelled drag); true leaves
    // the paint standing, which is what the committed stroke rasterizes to anyway.
    public void EndLiveSurfaceRun(bool keepPaint)
    {
        if (!keepPaint) RestoreLiveSurfaceRun();
        _liveRunActive = false;
        _liveBase = _liveWork = null;
        _liveW = _liveH = 0;
    }

    private void RestoreLiveSurfaceRun()
    {
        if (_liveBase == null || targetTerrain == null || _liveW <= 0 || _liveH <= 0) return;
        targetTerrain.terrainData.SetAlphamaps(_liveX0, _liveY0, _liveBase);
    }

    // Alphamap cell window a stroke can touch, clamped to the map. Terrain-local, matching the
    // indices the live stampers build.
    private bool StrokeCellRect(SurfaceStrokeDef stroke, int res, out int x0, out int y0, out int w, out int h)
    {
        x0 = y0 = w = h = 0;
        if (targetTerrain == null || stroke?.points == null) return false;

        Vector3 tPos = targetTerrain.transform.position, tSize = targetTerrain.terrainData.size;
        float reach = Mathf.Max(0.1f, stroke.radius) * (IsSquareShape(stroke.shape) ? SQUARE_REACH : 1f);

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in stroke.points)
        {
            if (p == null || p.Length < 2) continue;
            minX = Mathf.Min(minX, p[0]); maxX = Mathf.Max(maxX, p[0]);
            minZ = Mathf.Min(minZ, p[1]); maxZ = Mathf.Max(maxZ, p[1]);
        }
        if (minX > maxX) return false;

        // One cell of slack on each side so rounding in the stamper can't fall outside the window.
        int x1 = Mathf.Clamp(Mathf.CeilToInt (((maxX + reach - tPos.x) / tSize.x) * res) + 1, 0, res);
        int y1 = Mathf.Clamp(Mathf.CeilToInt (((maxZ + reach - tPos.z) / tSize.z) * res) + 1, 0, res);
        x0     = Mathf.Clamp(Mathf.FloorToInt(((minX - reach - tPos.x) / tSize.x) * res) - 1, 0, res);
        y0     = Mathf.Clamp(Mathf.FloorToInt(((minZ - reach - tPos.z) / tSize.z) * res) - 1, 0, res);
        w = x1 - x0; h = y1 - y0;
        return w > 0 && h > 0;
    }

    // -----------------------------------------------------------------------
    // Object instances — fixed: honors rotationY and scale (consolidates ObjectPlacer)
    // -----------------------------------------------------------------------

    private void RenderObjectInstances(List<ObjectInstance> instances, EnvRender er)
    {
        if (prefabRegistry == null) { Debug.LogError("[WorldRenderer] prefabRegistry not assigned."); return; }

        foreach (var inst in instances)
            SpawnOneObject(inst, er);
    }

    // Spawns a single object instance under er.root. Shared by the full render loop and the
    // incremental SpawnObjectInstance entry point used by the scatter brush.
    private void SpawnOneObject(ObjectInstance inst, EnvRender er)
    {
        if (inst == null || !inst.included) return;
        if (inst.position == null || inst.position.Length < 3) return;

        Transform root = er.root;
        GameObject prefab = prefabRegistry.GetPrefab(inst.prefabType);

        // Instance rotation is a delta applied on top of the prefab's authored orientation,
        // so prefabs modeled facing a particular direction keep that baseline. Instantiate
        // under root first (preserving the prefab's authored rotation/scale), then compose.
        // When the prefab_type has no registry entry we spawn a magenta missing-texture
        // placeholder instead of skipping, so the gap is visible and stays editable/saveable.
        GameObject go;
        Quaternion baseRot;
        if (prefab == null)
        {
            Debug.LogWarning($"[WorldRenderer] Prefab '{inst.prefabType}' not found in PrefabRegistry — spawning missing-texture placeholder.");
            go = CreateMissingPrefabPlaceholder(root, inst.prefabType);
            baseRot = Quaternion.identity;
        }
        else
        {
            go = Instantiate(prefab, root);
            baseRot = prefab.transform.rotation;
        }
        go.transform.rotation = Quaternion.Euler(inst.rotationX, inst.rotationY, inst.rotationZ) * baseRot;
        if (inst.boxSizeMeters != null && inst.boxSizeMeters.Length >= 3)
        {
            // Massing box: absolute X/Y/Z dimensions in meters (from target_dimensions_ft).
            // Assumes the prefab is a unit cube, so this sets the box's real size directly.
            go.transform.localScale = new Vector3(
                inst.boxSizeMeters[0], inst.boxSizeMeters[1], inst.boxSizeMeters[2]);
        }
        else
        {
            float scale = inst.scale > 0f ? inst.scale : 1f;
            go.transform.localScale *= scale * prefabScaleFactor;
        }

        // Position + terrain-snap. Grounding uses the rotated/scaled bounds, so it must
        // run after the transform is set. EditController calls the same method after
        // edit-mode rotate/scale so the live object matches this rendered/reloaded result.
        GroundObjectInstance(go, inst.position);

        var marker = go.AddComponent<InstanceMarker>();
        marker.instanceId = inst.instanceId;
        marker.isBuilding  = false;
        er.instanceToGO[inst.instanceId] = go;
    }

    // Incrementally spawns one object into the active environment's render without re-rendering
    // everything — used by the scatter brush so painting many trees stays responsive. The caller
    // is responsible for also adding `inst` to env.objectInstances so a reload reproduces it.
    public void SpawnObjectInstance(ObjectInstance inst)
    {
        if (_activeEnvId == null || !_envRenders.TryGetValue(_activeEnvId, out var er)) return;
        SpawnOneObject(inst, er);
    }

    // Removes one object instance's GameObject from the active environment's render (eraser).
    // The caller removes it from env.objectInstances.
    public void RemoveObjectInstance(string id)
    {
        if (id == null || _activeEnvId == null || !_envRenders.TryGetValue(_activeEnvId, out var er)) return;
        if (er.instanceToGO.TryGetValue(id, out var go))
        {
            if (go != null) Destroy(go);
            er.instanceToGO.Remove(id);
        }
    }

    // -----------------------------------------------------------------------
    // Path ribbons — PathDef polylines rendered as textured mesh strips on the terrain
    // -----------------------------------------------------------------------

    private void RenderPaths(List<PathDef> paths, EnvRender er)
    {
        if (paths == null || paths.Count == 0) return;
        if (pathMaterialPalette == null)
        {
            Debug.LogError("[WorldRenderer] pathMaterialPalette not assigned — cannot render paths.");
            return;
        }

        Vector3 terrainPos = targetTerrain != null ? targetTerrain.transform.position : Vector3.zero;

        var built = new List<(List<Vector2> dense, float width, string material, int stack)>();
        int stack = 0;   // per-path stack index: each rendered path lifts a hair more (see PathStackStep)
        foreach (var path in paths)
        {
            if (path?.points == null || path.points.Length < 2) continue;

            // Sparse control points (world XZ) -> smoothed, evenly-spaced dense centerline so the
            // ribbon reads as a clean curve and its short segments hug the terrain between samples.
            var ctrl = new List<Vector2>(path.points.Length);
            foreach (var p in path.points)
            {
                if (p == null || p.Length < 2) continue;
                ctrl.Add(new Vector2(terrainPos.x + p[0], terrainPos.z + p[1]));
            }
            if (ctrl.Count < 2) continue;

            float w = path.width > 0f ? path.width : 1.5f;
            // Each overlapping ribbon gets a unique micro-lift so coplanar paths can't z-fight.
            float lift = stack * PathStackStep;
            float HeightAt(float x, float z) => SamplePathSurfaceY(x, z) + lift;

            // Round sharp corners (radius = half-width) so the ribbon never spikes or folds on itself,
            // then smooth/resample into the dense centerline.
            var rounded = PathGeometry.RoundCorners(ctrl, w * 0.5f);
            var dense = PathGeometry.Smooth(rounded, path.smoothing);
            var centerline = new List<Vector3>(dense.Count);
            foreach (var d in dense)
                centerline.Add(new Vector3(d.x, HeightAt(d.x, d.y), d.y));
            if (centerline.Count < 2) continue;

            Mesh mesh = PathMesh.Build(centerline, w, HeightAt);
            if (mesh == null) continue;

            var go = new GameObject($"Path ({path.material})");
            go.transform.SetParent(er.root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = pathMaterialPalette.GetMaterial(path.material);
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.AddComponent<PathMarker>().pathId = path.id;

            built.Add((dense, w, path.material, stack));
            stack++;
        }

        RenderPathJunctions(built, er);
    }

    // Where two paths cross (or a path end touches another's centerline), drop a small terrain-draped
    // disc of the wider path's material on top so the overlapping ribbons read as one clean junction
    // instead of a seam. Built from the already-smoothed centerlines, so generated and hand-drawn
    // paths blend identically.
    private void RenderPathJunctions(List<(List<Vector2> dense, float width, string material, int stack)> built,
                                     EnvRender er)
    {
        if (built.Count < 2) return;
        const int MaxJunctions = 200;
        var centers = new List<Vector2>();
        var radii   = new List<float>();

        // Each junction sits a hair above the HIGHER of the two ribbons it bridges (stacks lift paths
        // by PathStackStep), so the patch hides the seam without z-fighting either ribbon underneath.
        void AddJunction(Vector2 p, float radius, string material, int topStack)
        {
            for (int i = 0; i < centers.Count; i++)
                if ((centers[i] - p).sqrMagnitude < radius * radius * 0.36f) return;   // merge nearby
            if (centers.Count >= MaxJunctions) return;
            centers.Add(p); radii.Add(radius);

            float lift = topStack * PathStackStep + 0.02f;
            System.Func<float, float, float> patchHeight = (x, z) => SamplePathSurfaceY(x, z) + lift;
            Mesh disc = PathMesh.BuildDisc(p.x, p.y, radius, patchHeight);
            if (disc == null) return;
            var go = new GameObject("Path junction");
            go.transform.SetParent(er.root, false);
            go.AddComponent<MeshFilter>().sharedMesh = disc;
            go.AddComponent<MeshRenderer>().sharedMaterial = pathMaterialPalette.GetMaterial(material);
        }

        for (int a = 0; a < built.Count; a++)
        for (int b = a + 1; b < built.Count; b++)
        {
            var A = built[a]; var B = built[b];
            bool aWider = A.width >= B.width;
            float radius = Mathf.Max(A.width, B.width) * 0.5f * 1.2f;
            string mat = aWider ? A.material : B.material;
            int topStack = Mathf.Max(A.stack, B.stack);

            // Segment/segment crossings (X-junctions).
            for (int i = 0; i < A.dense.Count - 1; i++)
            for (int j = 0; j < B.dense.Count - 1; j++)
                if (SegmentsIntersect(A.dense[i], A.dense[i + 1], B.dense[j], B.dense[j + 1], out Vector2 hit))
                    AddJunction(hit, radius, mat, topStack);

            // Endpoint-touches-centerline (T-junctions) the crossing test can miss.
            float tol = Mathf.Max(A.width, B.width) * 0.5f;
            TestEndpointTouch(A.dense, B.dense, tol, radius, mat, topStack, AddJunction);
            TestEndpointTouch(B.dense, A.dense, tol, radius, mat, topStack, AddJunction);
        }
    }

    private static void TestEndpointTouch(List<Vector2> ends, List<Vector2> line, float tol,
                                          float radius, string mat, int topStack,
                                          System.Action<Vector2, float, string, int> add)
    {
        foreach (var e in new[] { ends[0], ends[ends.Count - 1] })
            for (int j = 0; j < line.Count - 1; j++)
                if (PointSegmentDistance(e, line[j], line[j + 1]) <= tol) { add(e, radius, mat, topStack); break; }
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4, out Vector2 hit)
    {
        hit = default;
        Vector2 r = p2 - p1, s = p4 - p3;
        float denom = r.x * s.y - r.y * s.x;
        if (Mathf.Abs(denom) < 1e-9f) return false;          // parallel/collinear
        Vector2 qp = p3 - p1;
        float t = (qp.x * s.y - qp.y * s.x) / denom;
        float u = (qp.x * r.y - qp.y * r.x) / denom;
        if (t < 0f || t > 1f || u < 0f || u > 1f) return false;
        hit = p1 + t * r;
        return true;
    }

    private static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-9f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + t * ab);
    }

    // Final world Y for a path vertex at world (x, z): terrain surface + z-fight lift. Public so the
    // edit-mode live preview drapes its ribbon exactly like the committed render does.
    public float SamplePathSurfaceY(float x, float z)
    {
        float baseY = targetTerrain != null ? targetTerrain.transform.position.y : 0f;
        float h     = targetTerrain != null ? targetTerrain.SampleHeight(new Vector3(x, 0f, z)) : 0f;
        return baseY + h + pathYEpsilon;
    }

    // Material a path of `materialId` renders with — shared with the live preview for WYSIWYG.
    public Material GetPathMaterial(string materialId) => pathMaterialPalette != null ? pathMaterialPalette.GetMaterial(materialId) : null;

    // -----------------------------------------------------------------------
    // Fence runs — FenceDef polylines rendered as repeated panel/post prefabs along the terrain
    // -----------------------------------------------------------------------

    private void RenderFences(List<FenceDef> fences, EnvRender er)
    {
        if (fences == null || fences.Count == 0) return;
        if (fencePalette == null)
        {
            Debug.LogError("[WorldRenderer] fencePalette not assigned — cannot render fences.");
            return;
        }

        Vector3 terrainPos = targetTerrain != null ? targetTerrain.transform.position : Vector3.zero;

        foreach (var fence in fences)
        {
            if (fence?.points == null || fence.points.Length < 2) continue;
            var entry = fencePalette.Get(fence.fenceType);
            if (entry == null || entry.panelPrefab == null) continue;   // unknown type / no panel ⇒ skip (logged)

            // Sparse control points (world XZ), like RenderPaths.
            var ctrl = new List<Vector2>(fence.points.Length);
            foreach (var p in fence.points)
            {
                if (p == null || p.Length < 2) continue;
                ctrl.Add(new Vector2(terrainPos.x + p[0], terrainPos.z + p[1]));
            }
            if (ctrl.Count < 2) continue;

            float panelLen = entry.panelLength > 0f ? entry.panelLength : 2f;
            float height   = fence.height > 0f ? fence.height : (entry.height > 0f ? entry.height : 1.2f);
            var placements = FenceBuilder.Build(ctrl, fence.smoothing, panelLen);

            foreach (var pl in placements)
            {
                GameObject prefab = pl.isPost ? entry.postPrefab : entry.panelPrefab;
                if (prefab == null) continue;   // posts are optional

                var go = Instantiate(prefab, er.root);
                ApplyFencePlacement(go, prefab.transform.localScale, pl, entry, height);
                go.AddComponent<FenceMarker>().fenceId = fence.id;
            }
        }
    }

    // Position/rotate/scale one fence piece (panel or post) for a FenceBuilder placement. Shared with
    // the edit-mode ghost preview so the preview and the committed render can never drift. `baseScale`
    // is the prefab's authored localScale, passed explicitly so pooled preview instances don't
    // compound scale across frames.
    public void ApplyFencePlacement(GameObject go, Vector3 baseScale, in FenceBuilder.Placement pl,
                                    FencePalette.Entry entry, float height)
    {
        // Prefabs are modeled along +X (run direction) with their base at y=0; set the run yaw
        // directly (FenceBuilder computed it for the +X convention).
        var rot = Quaternion.Euler(0f, pl.yawDeg, 0f);
        go.transform.rotation = rot;

        // Stretch the panel to span its gap (X = run) and reach the fence height (Y); posts
        // only take the height scale. Thickness (Z) is preserved. The panel's modeled length and
        // X-center come from its measured mesh extent, not the pivot or entry.panelLength — art-pack
        // panels often pivot at one end, which would otherwise shift the whole run by half a panel.
        float baseLen  = entry.panelLength > 1e-4f ? entry.panelLength : 2f;
        float centerX  = 0f;
        if (!pl.isPost && entry.panelPrefab != null && TryGetPanelXExtent(entry.panelPrefab, out Vector2 ext))
        {
            baseLen = (ext.y - ext.x) * Mathf.Max(Mathf.Abs(baseScale.x), 1e-4f);
            centerX = (ext.x + ext.y) * 0.5f;
        }
        float baseHeight = entry.height > 0f ? entry.height : height;
        float sx = (!pl.isPost && entry.scalePanelToFit && baseLen > 1e-4f) ? pl.span / baseLen : 1f;
        float sy = baseHeight > 1e-4f ? height / baseHeight : 1f;
        go.transform.localScale = new Vector3(baseScale.x * sx, baseScale.y * sy, baseScale.z);

        // Drape onto the terrain: base sits at the surface under this piece. Panels sample both end
        // joints and sit at the lower one so their ends never float off a downhill slope (sinking
        // slightly into the uphill side reads far better than a gap); posts sample their own XZ.
        float y;
        if (!pl.isPost && pl.span > 1e-4f)
        {
            float th = pl.yawDeg * Mathf.Deg2Rad;
            var half = new Vector2(Mathf.Cos(th), -Mathf.Sin(th)) * (pl.span * 0.5f);
            y = Mathf.Min(SamplePathSurfaceY(pl.pos.x - half.x, pl.pos.y - half.y),
                          SamplePathSurfaceY(pl.pos.x + half.x, pl.pos.y + half.y));
        }
        else
        {
            y = SamplePathSurfaceY(pl.pos.x, pl.pos.y);
        }
        // Place by the mesh's X-center, not the pivot: shift the instance so the panel geometry is
        // centered on the segment midpoint — this is what makes a run start and end exactly at the
        // drawn points regardless of where the prefab's pivot sits.
        go.transform.position = new Vector3(pl.pos.x, y, pl.pos.y)
                              - rot * new Vector3(centerX * baseScale.x * sx, 0f, 0f);
    }

    // Cached local X extent (min, max) of a panel prefab's combined meshes, measured in the prefab
    // root's space (root scale excluded — the caller multiplies by baseScale). Lets fence placement
    // work from the actual geometry instead of assuming the pivot sits at the panel's X-center.
    private static readonly Dictionary<GameObject, Vector2> _panelXExtents = new();

    private static bool TryGetPanelXExtent(GameObject prefab, out Vector2 ext)
    {
        if (_panelXExtents.TryGetValue(prefab, out ext)) return ext.y > ext.x;

        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        var root = prefab.transform;
        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Bounds b = mesh.bounds;
            Matrix4x4 toRoot = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? b.min.x : b.max.x,
                    (c & 2) == 0 ? b.min.y : b.max.y,
                    (c & 4) == 0 ? b.min.z : b.max.z);
                float x = toRoot.MultiplyPoint3x4(corner).x;
                if (x < min) min = x;
                if (x > max) max = x;
            }
        }
        ext = max > min ? new Vector2(min, max) : Vector2.zero;
        _panelXExtents[prefab] = ext;
        return ext.y > ext.x;
    }

    // -----------------------------------------------------------------------
    // Lot / parcel boundary frame — a draped outline of the editable parcel so the lot reads as a
    // first-class object (it's the same polygon PaintTerrain masks the water against, or the terrain
    // rectangle when no explicit boundary is set). Pure authoring aid: a single terrain-following
    // LineRenderer, rebuilt with every render, no collider (never interferes with picking).
    // -----------------------------------------------------------------------

    [Header("Lot frame")]
    [SerializeField] private Color lotFrameColor = new(1f, 0.85f, 0.2f);  // amber, reads over grass & water
    [SerializeField] private float lotFrameLift  = 0.15f;                 // extra lift above the path plane

    private void RenderLotFrame(SiteDef site, EnvRender er)
    {
        var poly = EnvironmentScale.EffectiveLotPolygon(site);
        if (poly == null || poly.Length < 3 || er?.root == null) return;

        Vector3 terrainPos = targetTerrain != null ? targetTerrain.transform.position : Vector3.zero;

        // Densify each edge so the outline hugs any terrain grade between corners (cheap; flat sites
        // collapse to ~corner count). Spacing scales with the lot so big parcels don't over-sample.
        float diag = targetTerrain != null
            ? Mathf.Sqrt(targetTerrain.terrainData.size.x * targetTerrain.terrainData.size.x +
                         targetTerrain.terrainData.size.z * targetTerrain.terrainData.size.z)
            : 100f;
        float spacing = Mathf.Clamp(diag * 0.05f, 2f, 40f);

        var pts = new List<Vector3>();
        int n = poly.Length;
        for (int i = 0; i < n; i++)
        {
            float[] a = poly[i], b = poly[(i + 1) % n];
            if (a == null || a.Length < 2 || b == null || b.Length < 2) continue;
            Vector2 pa = new(terrainPos.x + a[0], terrainPos.z + a[1]);
            Vector2 pb = new(terrainPos.x + b[0], terrainPos.z + b[1]);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(pa, pb) / spacing));
            for (int s = 0; s < steps; s++)   // exclude endpoint; next edge contributes its start
            {
                Vector2 p = Vector2.Lerp(pa, pb, s / (float)steps);
                pts.Add(new Vector3(p.x, SamplePathSurfaceY(p.x, p.y) + lotFrameLift, p.y));
            }
        }
        if (pts.Count < 3) return;

        var go = new GameObject("Lot frame");
        go.transform.SetParent(er.root, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.positionCount = pts.Count;
        lr.SetPositions(pts.ToArray());
        lr.numCornerVertices = 2;
        lr.alignment = LineAlignment.View;
        float w = Mathf.Clamp(diag * 0.004f, 0.25f, 4f);
        lr.widthMultiplier = w;
        lr.material = LotFrameMaterial;
        lr.startColor = lr.endColor = lotFrameColor;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
    }

    private Material _lotFrameMaterial;
    private Material LotFrameMaterial
    {
        get
        {
            if (_lotFrameMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
                                ?? Shader.Find("Unlit/Color")
                                ?? Shader.Find("Sprites/Default");
                _lotFrameMaterial = new Material(shader) { name = "LotFrameLine" };
                if (_lotFrameMaterial.HasProperty("_BaseColor")) _lotFrameMaterial.SetColor("_BaseColor", lotFrameColor);
                if (_lotFrameMaterial.HasProperty("_Color"))     _lotFrameMaterial.SetColor("_Color", lotFrameColor);
            }
            return _lotFrameMaterial;
        }
    }

    // Lazily-built magenta material used to flag prefab_types that have no PrefabRegistry entry.
    // Mirrors Unity's own "missing shader" look so an unmapped prefab reads as broken at a glance.
    private Material _missingMaterial;
    private Material MissingMaterial
    {
        get
        {
            if (_missingMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                                ?? Shader.Find("Standard")
                                ?? Shader.Find("Sprites/Default");
                _missingMaterial = new Material(shader) { name = "MissingPrefabPlaceholder" };
                // Cover both URP (_BaseColor) and built-in (_Color) so it shows magenta either way.
                if (_missingMaterial.HasProperty("_BaseColor")) _missingMaterial.SetColor("_BaseColor", Color.magenta);
                if (_missingMaterial.HasProperty("_Color"))     _missingMaterial.SetColor("_Color", Color.magenta);
            }
            return _missingMaterial;
        }
    }

    // Spawns a 1m magenta cube standing in for a prefab_type with no PrefabRegistry entry.
    // Returned like a freshly instantiated prefab — caller composes rotation/scale and grounds it —
    // so a missing prefab still produces a visible, selectable, saveable instance.
    private GameObject CreateMissingPrefabPlaceholder(Transform parent, string prefabType)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"MISSING_PREFAB ({prefabType})";
        go.transform.SetParent(parent, false);
        var rend = go.GetComponent<Renderer>();
        if (rend != null) rend.sharedMaterial = MissingMaterial;
        return go;
    }

    // Places an object instance and snaps the bottom of its (rotated/scaled) renderer bounds
    // to the terrain surface, then applies position[1] as a vertical offset above that resting
    // height. Because the snap depends on the current rotation/scale, this is the single source
    // of truth for object placement — both initial render and edit-mode re-grounding go through
    // it, so the live edit, the save, and the reload all agree.
    public void GroundObjectInstance(GameObject go, float[] position)
    {
        if (go == null || position == null || position.Length < 3) return;

        Vector3 terrainPos = targetTerrain != null ? targetTerrain.transform.position : Vector3.zero;
        float   posX       = position[0];
        float   posZ       = position[2];
        float   height     = targetTerrain != null ? targetTerrain.SampleHeight(new Vector3(posX, 0f, posZ)) : 0f;

        go.transform.position = terrainPos + new Vector3(posX, height, posZ);

        Renderer rend = go.GetComponentInChildren<Renderer>();
        if (rend != null)
            go.transform.position += new Vector3(0f, go.transform.position.y - rend.bounds.min.y, 0f);

        // position[1] is a vertical offset above the terrain-snapped resting height.
        if (position.Length >= 2)
            go.transform.position += new Vector3(0f, position[1], 0f);
    }

    // -----------------------------------------------------------------------
    // Building instances — looks up BuildingDef to derive grid dimensions
    // -----------------------------------------------------------------------

    private void RenderBuildingInstances(List<BuildingInstance> instances,
                                          IReadOnlyDictionary<string, BuildingDef> buildingDefs, EnvRender er)
    {
        Transform root     = er.root;
        Vector3 terrainPos = targetTerrain != null ? targetTerrain.transform.position : Vector3.zero;
        Renderer bayRend   = buildingGenerator != null ? buildingGenerator.GetRenderer() : null;

        foreach (var inst in instances)
        {
            if (inst == null || !inst.included) continue;
            if (inst.position == null || inst.position.Length < 3) continue;

            if (!buildingDefs.TryGetValue(inst.buildingId, out BuildingDef bdef))
            {
                Debug.LogError($"[WorldRenderer] BuildingDef '{inst.buildingId}' not found. Skipping instance '{inst.instanceId}'.");
                continue;
            }

            float posX   = inst.position[0];
            float posY   = inst.position.Length >= 2 ? inst.position[1] : 0f;  // vertical offset above terrain
            float posZ   = inst.position[2];
            float height = targetTerrain != null ? targetTerrain.SampleHeight(new Vector3(posX, 0f, posZ)) : 0f;
            var worldPos = terrainPos + new Vector3(posX, height + posY, posZ);

            bool hasTiles = bdef.tiles != null && bdef.tiles.Count > 0;
            GameObject bldgRoot;

            if (hasTiles && tileShapePalette != null)
            {
                bldgRoot = RenderTiledBuilding(bdef, inst, worldPos, root);
            }
            else
            {
                if (hasTiles)
                    Debug.LogError($"[WorldRenderer] tileShapePalette not assigned — rendering tiled building '{bdef.name}' as bay massing.");
                if (buildingGenerator == null || bayRend == null)
                {
                    Debug.LogError($"[WorldRenderer] buildingGenerator/bay prefab unavailable. Skipping instance '{inst.instanceId}'.");
                    continue;
                }

                float cellSize = bdef.gridCellSize > 0f ? bdef.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
                int baysWide   = Mathf.Max(1, Mathf.RoundToInt((TileSpanX(bdef) * cellSize) / bayRend.bounds.size.x));
                int baysDeep   = Mathf.Max(1, Mathf.RoundToInt((TileSpanZ(bdef) * cellSize) / bayRend.bounds.size.z));
                int floors     = bdef.floors > 0 ? bdef.floors : 1;

                bldgRoot = buildingGenerator.Generate(root, worldPos, baysWide, baysDeep, floors,
                                                      new Vector3(inst.rotationX, inst.rotationY + defaultYRotation, inst.rotationZ), bdef.name);
            }

            if (bldgRoot != null)
            {
                var marker = bldgRoot.AddComponent<InstanceMarker>();
                marker.instanceId = inst.instanceId;
                marker.isBuilding  = true;
                er.instanceToGO[inst.instanceId] = bldgRoot;

                // M4: render embedded objects relative to this building instance
                RenderEmbeddedObjects(bdef, worldPos,
                    Quaternion.Euler(inst.rotationX, inst.rotationY, inst.rotationZ), bldgRoot.transform, er);
            }
        }
    }

    // Tile-based rendering: same corner-pivot convention as TileBuildingEditor, so the
    // edit-mode grid overlay lines up exactly with the rendered building.
    private GameObject RenderTiledBuilding(BuildingDef bdef, BuildingInstance inst, Vector3 worldPos, Transform parent)
    {
        var rootGO = new GameObject(string.IsNullOrEmpty(bdef.name) ? "Building" : bdef.name);
        rootGO.transform.SetParent(parent, false);
        rootGO.transform.position = worldPos;
        rootGO.transform.rotation = Quaternion.Euler(inst.rotationX, inst.rotationY, inst.rotationZ);
        if (inst.scale > 0f) rootGO.transform.localScale = Vector3.one * inst.scale;

        float cs = bdef.gridCellSize > 0f ? bdef.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;

        // Sanity check: a single stray tile (e.g. from the old unclamped floor-plane hover) makes the
        // whole building read as enormous — every bounds-derived size (framing, selection, massing
        // span) tracks tile min/max. Warn loudly so corrupted defs get noticed and repaired instead
        // of silently rendering kilometers wide.
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (var tile in bdef.tiles)
        {
            TileSpawner.Spawn(tile, rootGO.transform, tileShapePalette, materialPalette, cs);
            if (tile.gridX < minX) minX = tile.gridX;
            if (tile.gridX > maxX) maxX = tile.gridX;
            if (tile.gridZ < minZ) minZ = tile.gridZ;
            if (tile.gridZ > maxZ) maxZ = tile.gridZ;
        }
        const int SANE_SPAN_CELLS = 200;
        if (maxX - minX > SANE_SPAN_CELLS || maxZ - minZ > SANE_SPAN_CELLS)
            Debug.LogWarning($"[WorldRenderer] Building '{bdef.name}' ({bdef.id}) spans " +
                             $"{maxX - minX + 1}×{maxZ - minZ + 1} cells — it likely contains a stray " +
                             $"tile far from the footprint (tile extent X {minX}..{maxX}, Z {minZ}..{maxZ}).");

        return rootGO;
    }

    // Prop mount bases per (prefabType, mountAxis, flipMount), measured once — prefab bounds don't
    // change at runtime, and reseating runs on every render of every decorated building.
    private readonly Dictionary<(string, int, bool), DecorAlignment.PropBasis> _propBasisCache = new();

    private bool BasisFor(string prefabType, DecorAlignment.MountAxis axis, bool flip,
                          out DecorAlignment.PropBasis basis)
    {
        var key = (prefabType, (int)axis, flip);
        if (_propBasisCache.TryGetValue(key, out basis)) return true;
        var prefab = prefabRegistry != null ? prefabRegistry.GetPrefab(prefabType) : null;
        if (!DecorPlacement.MeasurePropBasis(prefab, axis, flip, out basis)) return false;
        _propBasisCache[key] = basis;
        return true;
    }

    private void RenderEmbeddedObjects(BuildingDef bdef, Vector3 bldgWorldPos, Quaternion bldgRot, Transform parent, EnvRender er)
    {
        if (bdef.embeddedObjects == null || prefabRegistry == null) return;

        // Deform-aware pre-pass: rewrite each hosted decor's localPos/rotation/scale from its host
        // tile's CURRENT TileDeform, so props follow the building whenever its skew changes. Legacy
        // defs (no decor rules) are untouched. Idempotent, so multiple instances sharing this bdef
        // are fine. EditController.AfterBuildingSkew relies on this running before its PutBuilding
        // (render-then-PUT) so the reseated values persist to the server.
        float cellSize = bdef.gridCellSize > 0f ? bdef.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        DecorPlacement.ReseatAll(bdef, cellSize, BasisFor);

        foreach (var emb in bdef.embeddedObjects)
        {
            if (emb?.localPos == null || emb.localPos.Length < 3) continue;
            GameObject prefab = prefabRegistry.GetPrefab(emb.prefabType);
            Vector3 localPos = new Vector3(emb.localPos[0], emb.localPos[1], emb.localPos[2]);
            Vector3 worldPos = bldgWorldPos + bldgRot * localPos;
            float   scale    = emb.scale > 0f ? emb.scale : 1f;

            // Compose on top of the prefab's authored orientation (see RenderObjectInstances).
            // A missing prefab_type spawns the magenta placeholder rather than skipping.
            GameObject go;
            // Full XYZ so smart-painted props stay aligned to sloped/skewed faces (legacy data is X=Z=0).
            Quaternion embRot = Quaternion.Euler(emb.rotationX, emb.rotationY, emb.rotationZ);
            if (prefab == null)
            {
                Debug.LogWarning($"[WorldRenderer] Embedded prefab '{emb.prefabType}' not found in PrefabRegistry — spawning missing-texture placeholder.");
                go = CreateMissingPrefabPlaceholder(parent, emb.prefabType);
                go.transform.position = worldPos;
                go.transform.rotation = bldgRot * embRot;
            }
            else
            {
                go = Instantiate(prefab, worldPos,
                    bldgRot * embRot * prefab.transform.rotation, parent);
            }
            go.transform.localScale *= scale;

            var marker = go.AddComponent<InstanceMarker>();
            marker.instanceId = emb.instanceId;
            marker.isBuilding  = false;
            er.instanceToGO[emb.instanceId] = go;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Count of unique grid columns spanned by floor-0 tiles
    private static int TileSpanX(BuildingDef bdef)
    {
        int max = 0;
        if (bdef.tiles != null)
            foreach (var t in bdef.tiles)
                if (t.floor == 0 && t.gridX + 1 > max) max = t.gridX + 1;
        return Mathf.Max(1, max);
    }

    // Count of unique grid rows spanned by floor-0 tiles
    private static int TileSpanZ(BuildingDef bdef)
    {
        int max = 0;
        if (bdef.tiles != null)
            foreach (var t in bdef.tiles)
                if (t.floor == 0 && t.gridZ + 1 > max) max = t.gridZ + 1;
        return Mathf.Max(1, max);
    }
}
