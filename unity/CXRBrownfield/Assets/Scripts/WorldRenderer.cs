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
    [SerializeField] private Terrain targetTerrain;               // USER WIRES THIS IN INSPECTOR (template: every loaded env gets its own copy, this one is hidden in Play)
    [SerializeField] private PrefabRegistry prefabRegistry;       // USER WIRES THIS IN INSPECTOR
    [SerializeField] private TerrainRegistry terrainRegistry;     // USER WIRES THIS IN INSPECTOR
    [SerializeField] private BuildingGenerator buildingGenerator; // USER WIRES THIS IN INSPECTOR (legacy bay massing — only used when a def has tiles but tileShapePalette is unassigned; empty defs render a neutral pad instead)
    [SerializeField] private TileShapePalette tileShapePalette;   // USER WIRES THIS IN INSPECTOR (tile-based building rendering)
    [SerializeField] private MaterialPalette materialPalette;     // USER WIRES THIS IN INSPECTOR (tile face materials)
    [SerializeField] private PathMaterialPalette pathMaterialPalette; // USER WIRES THIS IN INSPECTOR (path ribbon materials)
    [SerializeField] private FencePalette fencePalette;           // USER WIRES THIS IN INSPECTOR (fence segment prefabs)
    [SerializeField] private WaterPalette waterPalette;           // USER WIRES THIS IN INSPECTOR (water surface materials; falls back to Resources/WaterPalette)
    [SerializeField] private BuildingStylePalette buildingStylePalette; // USER WIRES THIS IN INSPECTOR (style letter → wall material; falls back to Resources/BuildingStylePalette)

    [Header("Path rendering")]
    [SerializeField] private float pathYEpsilon = 0.05f;          // lift above terrain to avoid z-fighting
    // Per-path micro-lift (m): each path rendered after the first is raised by stackIndex*this so two
    // overlapping/parallel ribbons are never coplanar (no z-fighting). ~4mm is invisible in scene.
    public const float PathStackStep = 0.004f;

    [Header("Settings")]
    [SerializeField] private float prefabScaleFactor = 1f;
    [SerializeField] private float defaultYRotation  = 90f;

    [Header("Detail")]
    // Spawn gate for the low-performance client: instances and decor marked `optional` are never
    // instantiated (no GOs, no colliders, no draw calls). SyncClient turns this on in the VRViewer
    // scene; the desktop editor leaves it false and uses SetOptionalHidden for a reversible preview.
    [SerializeField] private bool skipOptional = false;
    public bool SkipOptional => skipOptional;
    public void SetSkipOptional(bool on) => skipOptional = on;
    // Editor "Low detail" preview: optional GOs exist but are inactive. Applied at spawn time too,
    // so a rebuild (undo, include toggle, paste) comes back in the same visual state.
    private bool _optionalHidden;
    public  bool OptionalHidden => _optionalHidden;

    [Header("Ground")]
    // Alphamap resolution of each env's ground copy; 0 keeps the template's. Every loaded env owns
    // a full splat (one RGBA texture per four TerrainRegistry layers), so the headset viewer can
    // trade paint sharpness for memory here. Applied before the first paint.
    [SerializeField] private int envAlphamapResolution = 0;

    // Per-environment render state. Multiple environments can be rendered at once, each with its
    // own ground; only the active one is interactive — see SetActiveEnvironment.
    private class EnvRender
    {
        public EnvironmentDef env;                 // kept so a dirty ground can be repainted from its site
        public Transform      root;
        public readonly Dictionary<string, GameObject> instanceToGO = new();

        // This env's ground: a copy of the template Terrain with its own TerrainData, a SIBLING of
        // root under the renderer (ClearEnvRender empties root on every re-render, ApplyLockState
        // would switch a backdrop's TerrainCollider off, BakePass walks root). null for a site
        // fill, which sits on its host's ground (terrainHostId).
        public Terrain     terrain;
        public TerrainData terrainData;
        public string      terrainHostId;
        public bool        paintDirty;             // site-fill overlays changed; repaint on the next activation
        public int         loadSeq;                // load order, ranks the backdrops' ground bias
        public float       biasY;                  // TerrainStack.BiasY: 0 when active, a few cm down as a backdrop
        public Vector3     previewOffset;          // Move site drag, XZ only

        // ApplyLockState cache: the full-hierarchy component sweeps are expensive at scale and used
        // to run for EVERY loaded env on every render/activation. The lists are gathered once per
        // rebuild and the last-applied state short-circuits repeat applications (backdrops become
        // no-ops). Invalidated (arrays nulled) whenever children change: ClearEnvRender,
        // DestroyRoot, SpawnOneObject, RemoveObjectInstance.
        public Renderer[] lockRenderers;
        public Collider[] lockColliders;
        public int  lockCacheVersion;              // bumped on every cache rebuild; forces re-apply
        public int  lockAppliedVersion = -1;
        public bool lockAppliedActive;
        public bool lockAppliedSolid;

        public void InvalidateLockCache() { lockRenderers = null; lockColliders = null; }
    }

    // env.id → its rendered geometry and ground.
    private readonly Dictionary<string, EnvRender> _envRenders = new();
    private int _loadSeq;
    // The single editable/saveable environment. Its colliders are enabled and the terrain tools
    // edit its ground; all other loaded environments are locked (colliders off) and dimmed, with
    // their ground left in full color and a few cm lower (TerrainStack.BiasY).
    private string _activeEnvId;

    // Ground paint from site fills: for each host env id, the fills' SiteDefs (already projected
    // into host meters by SiteFit) plus the site polygon that clips them. Composited into the
    // host's splatmap after its own paint. Set by LibraryBrowser when it reconciles fills; cleared
    // by passing null/empty. The host repaints on the next SetActiveEnvironment (paintDirty).
    public struct SiteOverlay
    {
        public SiteDef   site;   // projected child ground data (zones/strokes in host meters)
        public float[][] clip;   // host-space site polygon the paint is clipped to
    }
    private readonly Dictionary<string, List<SiteOverlay>> _siteOverlays = new();

    public void SetSiteOverlays(string hostEnvId, List<SiteOverlay> overlays)
    {
        if (string.IsNullOrEmpty(hostEnvId)) return;
        if (overlays == null || overlays.Count == 0) _siteOverlays.Remove(hostEnvId);
        else                                         _siteOverlays[hostEnvId] = overlays;
        if (_envRenders.TryGetValue(hostEnvId, out var host)) host.paintDirty = true;
    }

    // Palettes/registries exposed so other tools (e.g. EditController) can reuse the same assets
    // wired here instead of requiring a second inspector assignment.
    public PathMaterialPalette PathMaterialPalette => pathMaterialPalette;
    // The Resources palettes below resolve even from an unwired slot (VRViewer, a fresh scene);
    // the inspector slot only overrides them.
    public FencePalette        FencePalette        =>
        fencePalette != null ? fencePalette : (fencePalette = Resources.Load<FencePalette>("FencePalette"));
    public WaterPalette        WaterPalette        =>
        waterPalette != null ? waterPalette : (waterPalette = Resources.Load<WaterPalette>("WaterPalette"));
    // Same Resources fallback: BuildingDef.style resolves in the VR viewer without any wiring.
    public BuildingStylePalette BuildingStylePalette =>
        buildingStylePalette != null ? buildingStylePalette : (buildingStylePalette = BuildingStyleResolver.LoadDefault());
    public TerrainRegistry     TerrainRegistry     => terrainRegistry;
    public PrefabRegistry      PrefabRegistry      => prefabRegistry;

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    // Renders (or re-renders) one environment into its own root, on its own ground. By default the
    // rendered environment becomes the active (editable) one; pass makeActive:false to load it as a
    // locked backdrop. terrainHostEnvId marks a site fill: it gets no ground of its own and drapes
    // on that host's terrain (the host composites the fill's paint through SetSiteOverlays).
    public void RenderEnvironment(EnvironmentDef env, IReadOnlyDictionary<string, BuildingDef> buildingDefs,
                                  bool makeActive = true, bool suppressLotFrame = false,
                                  bool skipTerrainPaint = false, string terrainHostEnvId = null)
    {
        if (env == null) { Debug.LogError("[WorldRenderer] RenderEnvironment: env is null."); return; }
        ClearPreviewOffsets();   // never rebuild children into a root a move preview has offset

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lapMark = 0;
        long Lap() { long now = sw.ElapsedMilliseconds; long d = now - lapMark; lapMark = now; return d; }

        var er = GetOrCreateEnvRender(env);
        er.env = env;
        er.terrainHostId = string.IsNullOrEmpty(terrainHostEnvId) ? null : terrainHostEnvId;
        ClearEnvRender(er);   // only this environment's geometry, never the others
        long tClear = Lap();

        // Geometry is spawned in data space: the root and the ground go back to their data
        // position (no backdrop bias) for the rebuild and get the bias again at the tail.
        ResetEnvOffsetForRebuild(er);

        // Size + heightmap go onto this env's OWN ground BEFORE spawning geometry: objects and
        // path ribbons ground themselves via Terrain.SampleHeight, so sampling the outgoing
        // heights (e.g. on undo of a grade/resize edit) leaves them floating or buried.
        // skipTerrainPaint (undo of an edit whose site is unchanged) skips size, heights and
        // splat: the ground is already in exactly this state. A ground created just now is
        // always built.
        UnityEngine.Profiling.Profiler.BeginSample("WR.Heightmap");
        bool ownGround = er.terrainHostId == null;
        bool groundIsNew = ownGround && EnsureEnvTerrain(er);
        bool rebuildGround = ownGround && er.terrain != null && env.site != null && (groundIsNew || !skipTerrainPaint);
        if (rebuildGround)
            using (GroundScope(er.terrain))
            {
                ApplyTerrainSize(env.site);
                ApplyHeightmap(env.site);
            }
        UnityEngine.Profiling.Profiler.EndSample();
        long tTerrain = Lap();

        // Everything below drapes on this env's ground (a fill: its host's), whichever env is active.
        using var drape = GroundScope(TerrainFor(er));

        UnityEngine.Profiling.Profiler.BeginSample("WR.Objects");
        if (env.objectInstances != null)
            RenderObjectInstances(env.objectInstances, er);
        UnityEngine.Profiling.Profiler.EndSample();
        long tObjects = Lap();

        UnityEngine.Profiling.Profiler.BeginSample("WR.Buildings");
        if (env.buildingInstances != null)
            RenderBuildingInstances(env.buildingInstances, buildingDefs ?? new Dictionary<string, BuildingDef>(), er);
        UnityEngine.Profiling.Profiler.EndSample();
        long tBuildings = Lap();

        UnityEngine.Profiling.Profiler.BeginSample("WR.Paths");
        if (env.site?.paths != null)
            RenderPaths(env.site.paths, er);
        UnityEngine.Profiling.Profiler.EndSample();
        long tPaths = Lap();

        UnityEngine.Profiling.Profiler.BeginSample("WR.Fences");
        if (env.site?.fences != null)
            RenderFences(env.site.fences, er);
        UnityEngine.Profiling.Profiler.EndSample();
        long tFences = Lap();

        UnityEngine.Profiling.Profiler.BeginSample("WR.Water");
        if (env.site?.waterBodies != null)
            RenderWater(env.site.waterBodies, er);
        UnityEngine.Profiling.Profiler.EndSample();
        long tWater = Lap();

        // Site fills pass suppressLotFrame:true — their own lot frame would double-draw the
        // host's site frame at the same spot.
        if (!suppressLotFrame)
        {
            RenderLotFrame(env.site, er);
            RenderSiteFrames(env, er);
        }
        long tFrames = Lap();
        drape.Dispose();

        // Splat last, once, on this env's own ground (site-fill overlays included).
        UnityEngine.Profiling.Profiler.BeginSample("WR.PaintTerrain");
        if (rebuildGround)
        {
            using (GroundScope(er.terrain)) PaintTerrain(env.site, env.id);
            er.paintDirty = false;
        }
        UnityEngine.Profiling.Profiler.EndSample();

        // Adopt as active if requested, or if nothing is active yet. Otherwise just refresh
        // lock/dim state and the ground bias so this freshly-rendered (or re-rendered) env
        // reflects its role.
        if (makeActive || _activeEnvId == null)
            SetActiveEnvironment(env.id);
        else
        {
            ApplyStackBias();
            RefreshLockStates();
        }
        long tActivate = Lap();

        PerfLog.Log(sw.ElapsedMilliseconds, 50,
            $"RenderEnvironment '{env.name}': total={sw.ElapsedMilliseconds}ms clear={tClear} " +
            $"terrain={tTerrain} objects={tObjects} buildings={tBuildings} paths={tPaths} " +
            $"fences={tFences} water={tWater} frames={tFrames} activate={tActivate}");
    }

    // Marks one loaded environment as the editable/saveable one: the terrain tools now edit its
    // ground, its colliders are enabled, and every other loaded environment is locked + dimmed
    // with its ground a few cm lower. Every env keeps its own ground, so a switch rebuilds nothing;
    // only a ground whose site-fill overlays changed since its last paint is repainted here.
    public void SetActiveEnvironment(string envId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _activeEnvId = envId;

        UnityEngine.Profiling.Profiler.BeginSample("WR.PaintTerrain");
        foreach (var er in _envRenders.Values)
        {
            if (!er.paintDirty) continue;
            er.paintDirty = false;
            if (er.terrain == null || er.env?.site == null) continue;
            using (GroundScope(er.terrain)) PaintTerrain(er.env.site, er.env.id);
        }
        UnityEngine.Profiling.Profiler.EndSample();
        long tPaint = sw.ElapsedMilliseconds;

        ApplyStackBias();
        UnityEngine.Profiling.Profiler.BeginSample("WR.LockStates");
        RefreshLockStates();
        UnityEngine.Profiling.Profiler.EndSample();
        PerfLog.Log(sw.ElapsedMilliseconds, 25,
            $"SetActiveEnvironment '{envId}': paint={tPaint}ms locks={sw.ElapsedMilliseconds - tPaint}ms");
    }

    // -----------------------------------------------------------------------
    // Per-environment ground
    //
    // targetTerrain is a template. Each loaded env (site fills excepted) owns a copy of it with its
    // own TerrainData, so several grounds show at once and none overwrites another; the shared
    // TerrainData asset is never written. Every terrain function below works on `Ground`: the
    // scoped terrain while a specific env is being built, else the active env's. That keeps the
    // public terrain API (what the editor's tools call) meaning "the active env's ground".
    // -----------------------------------------------------------------------

    private Terrain _scopeTerrain;
    private bool    _scopeSet;
    private Terrain _blankTerrain;       // flat, unpainted ground shown while nothing is loaded
    private TerrainData _blankData;

    private Terrain ActiveTerrain =>
        _activeEnvId != null && _envRenders.TryGetValue(_activeEnvId, out var er) ? TerrainFor(er) : null;

    // The terrain the terrain functions act on. A scope pins it (even to null: a fill whose host
    // is gone must not fall through to the active env's ground).
    private Terrain Ground => _scopeSet ? _scopeTerrain : ActiveTerrain;

    // An env's ground: its own, or for a site fill its host's. Resolved on every call, never
    // cached: the host's terrain can be destroyed and rebuilt under the fill.
    private Terrain TerrainFor(EnvRender er)
    {
        if (er == null) return null;
        if (er.terrainHostId == null) return er.terrain;
        return _envRenders.TryGetValue(er.terrainHostId, out var host) ? host.terrain : null;
    }

    private readonly struct GroundScopeToken : IDisposable
    {
        private readonly WorldRenderer _wr;
        private readonly Terrain _prev;
        private readonly bool    _prevSet;
        public GroundScopeToken(WorldRenderer wr, Terrain t)
        {
            _wr = wr; _prev = wr._scopeTerrain; _prevSet = wr._scopeSet;
            wr._scopeTerrain = t; wr._scopeSet = true;
        }
        public void Dispose() { _wr._scopeTerrain = _prev; _wr._scopeSet = _prevSet; }
    }
    private GroundScopeToken GroundScope(Terrain t) => new GroundScopeToken(this, t);

    // Creates the env's ground if it has none. True when a new one was made (the caller then
    // always builds it, even on a skipTerrainPaint render).
    private bool EnsureEnvTerrain(EnvRender er)
    {
        if (er.terrain != null) return false;
        er.terrain = CloneTemplate($"Terrain:{er.env?.name}", out er.terrainData);
        if (er.terrain == null) return false;
        if (_blankTerrain != null) _blankTerrain.gameObject.SetActive(false);
        return true;
    }

    private Terrain CloneTemplate(string goName, out TerrainData data)
    {
        data = null;
        if (targetTerrain == null || targetTerrain.terrainData == null) return null;

        var go = Instantiate(targetTerrain.gameObject, transform);
        go.name = goName;
        var t = go.GetComponent<Terrain>();
        data = Instantiate(targetTerrain.terrainData);
        data.name = goName;
        if (envAlphamapResolution > 0 && data.alphamapResolution != envAlphamapResolution)
            data.alphamapResolution = envAlphamapResolution;
        t.terrainData = data;
        if (go.TryGetComponent<TerrainCollider>(out var col)) col.terrainData = data;
        // Overlapping grounds are independent: never let Unity stitch their LODs as neighbors.
        t.allowAutoConnect = false;
        if (!Application.isPlaying)
        {
            // Edit-mode rigs (tests, script-execute) must never leave these in the open scene.
            go.hideFlags   = HideFlags.DontSave;
            data.hideFlags = HideFlags.DontSave;
        }
        using (GroundScope(t)) EnsureHeightSetup();
        go.SetActive(true);
        return t;
    }

    // Runtime TerrainData is not freed with its GameObject, so both go explicitly.
    private static void DestroyTerrain(ref Terrain t, ref TerrainData data)
    {
        if (t != null)
        {
            if (Application.isPlaying) Destroy(t.gameObject); else DestroyImmediate(t.gameObject);
        }
        if (data != null)
        {
            if (Application.isPlaying) Destroy(data); else DestroyImmediate(data);
        }
        t = null; data = null;
    }

    // With nothing loaded the scene still has a floor: a flat copy of the template painted with
    // the first registry layer. Rebuilt flat each time it comes back (nothing edits it on purpose,
    // but a terrain tool with no active env would land here).
    private void RefreshBlankGround()
    {
        bool anyGround = false;
        foreach (var er in _envRenders.Values) if (er.terrain != null) { anyGround = true; break; }
        if (anyGround)
        {
            if (_blankTerrain != null) _blankTerrain.gameObject.SetActive(false);
            return;
        }
        if (!Application.isPlaying) return;   // edit-mode rigs get no floor of their own

        if (_blankTerrain == null)
        {
            _blankTerrain = CloneTemplate("Terrain:(blank)", out _blankData);
            if (_blankTerrain == null) return;
        }
        _blankTerrain.gameObject.SetActive(true);
        var p = _blankTerrain.transform.position;
        _blankTerrain.transform.position = new Vector3(0f, p.y, 0f);
        _blankData.SetHeights(0, 0, FlatHeights(_blankData.heightmapResolution));
        if (terrainRegistry != null && terrainRegistry.entries.Count > 0)
        {
            _blankData.terrainLayers = new[] { terrainRegistry.entries[0].terrainLayer };
            int res = _blankData.alphamapResolution;
            var map = new float[res, res, 1];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    map[y, x, 0] = 1f;
            _blankData.SetAlphamaps(0, 0, map);
        }
    }

    // Ranks the backdrops by load order and drops each one's ground (and the env with it) a step
    // below the one above, so two coincident flat grounds never z-fight and the active env's
    // ground is the one you see. A fill takes its host's bias.
    private readonly List<EnvRender> _biasOrder = new();
    private void ApplyStackBias()
    {
        _biasOrder.Clear();
        foreach (var kv in _envRenders)
            if (kv.Value.terrainHostId == null && kv.Key != _activeEnvId) _biasOrder.Add(kv.Value);
        _biasOrder.Sort((a, b) => a.loadSeq.CompareTo(b.loadSeq));
        for (int i = 0; i < _biasOrder.Count; i++) _biasOrder[i].biasY = TerrainStack.BiasY(false, i);
        if (_activeEnvId != null && _envRenders.TryGetValue(_activeEnvId, out var active)) active.biasY = 0f;

        foreach (var er in _envRenders.Values)
        {
            if (er.terrainHostId != null)
                er.biasY = _envRenders.TryGetValue(er.terrainHostId, out var host) ? host.biasY : 0f;
            ApplyEnvOffset(er);
        }
    }

    // Places an env's root and ground from its data corner, its ground bias and any Move site
    // preview, together, so the env always moves rigidly with its ground.
    private void ApplyEnvOffset(EnvRender er)
    {
        var off = new Vector3(er.previewOffset.x, er.biasY, er.previewOffset.z);
        if (er.root != null) er.root.localPosition = off;
        if (er.terrain != null)
        {
            EnvironmentScale.TerrainCorner(er.env?.site, out float ox, out float oz);
            er.terrain.transform.position = new Vector3(ox + off.x, HeightBrush.BASE_WORLD_Y + off.y, oz + off.z);
        }
    }

    // Back to data space for a rebuild: children are spawned at stored world positions into the
    // root, so it may not carry the bias while that happens (the tail's ApplyStackBias restores
    // it). Ground sampling needs no such care: DrapeY ignores the terrain's Y.
    private void ResetEnvOffsetForRebuild(EnvRender er)
    {
        er.biasY = 0f;
        er.previewOffset = Vector3.zero;
        ApplyEnvOffset(er);
    }

    // Sizes the env's Terrain to the environment's real-world site.terrainSize (meters) so the
    // visible ground is true scale (1 unit = 1 m) and every coordinate that's normalized against
    // terrainData.size (zones, strokes, lot mask, paths) lands correctly. The terrain's Y (height
    // range) is preserved — it is owned by EnsureHeightSetup. Width/length are clamped
    // to a sane band so a malformed/zero terrainSize can't collapse or blow up the ground. Public so
    // EditController can re-apply after a Site Settings edit or Scale Calibration.
    public void ApplyTerrainSize(SiteDef site)
    {
        if (site?.terrainSize == null || site.terrainSize.Length < 2) return;
        SetTerrainSizeClamped(site.terrainSize[0], site.terrainSize[1]);
        ApplyTerrainOrigin(site);
    }

    // Moves the terrain's min corner to site.terrainOrigin (null ⇒ the world origin, which is where
    // every environment authored before that field sits). Sizing alone is not enough for an
    // environment projected into a host's site: its content is out at the site's coordinates, so a
    // terrain left at the origin would sit entirely beside it.
    //
    // Coordinate contract: every stored XZ (instance positions, path/fence/stroke points, zone
    // rects, lot and water polygons) is in WORLD meters, never relative to this corner. The corner
    // only says where the ground is. So placement uses the stored XZ as is, and only the splat /
    // heightmap rasterizers subtract the terrain position to reach alphamap cells. Adding the
    // corner to a stored position doubled it: an env installed while the host's ground was still at
    // the origin rendered right, then re-grounded a moved building at corner + world position.
    // Y is preserved — EnsureHeightSetup parks it at the height range's base, not a horizontal
    // placement.
    private void ApplyTerrainOrigin(SiteDef site)
    {
        var t = Ground;
        if (t == null) return;
        EnvironmentScale.TerrainCorner(site, out float x, out float z);
        var p = t.transform.position;
        if (!Mathf.Approximately(p.x, x) || !Mathf.Approximately(p.z, z))
            t.transform.position = new Vector3(x, p.y, z);
    }

    // Lightweight live preview of a terrain resize from raw width/length (meters), without touching
    // any environment data — used by the editor's Lot tool while dragging a rectangle handle so the
    // ground plane tracks the drag. The committed size is written through ApplyTerrainSize on release.
    public void PreviewTerrainSize(float width, float length) => SetTerrainSizeClamped(width, length);

    // -----------------------------------------------------------------------
    // Whole-environment move preview (Move site tool)
    //
    // While the user drags, nothing in the data moves: the env's root, its ground and the roots of
    // its site fills are offset as transforms, which is cheap and needs no rebuild. On release the
    // editor bakes the delta into the data (EnvironmentScale.TranslateEnvironmentXZ) and
    // re-renders. RenderEnvironment reuses a root without rebuilding it, so every rebuild path
    // clears the preview first: an offset root would shift every child spawned into it at its
    // stored world position.
    // -----------------------------------------------------------------------

    private readonly HashSet<string> _previewOffsetIds = new();

    // Offsets the rendered env `envId`, its ground and its site fills by `delta` from their data
    // positions. Absolute, not cumulative: pass the full drag delta each frame. Y is ignored (a
    // move is on the ground plane).
    public void PreviewEnvironmentOffset(string envId, Vector3 delta)
    {
        if (envId == null || !_envRenders.TryGetValue(envId, out var host) || host.root == null) return;
        var offset = new Vector3(delta.x, 0f, delta.z);
        foreach (var kv in _envRenders)
        {
            if (kv.Key != envId && kv.Value.terrainHostId != envId) continue;
            kv.Value.previewOffset = offset;
            ApplyEnvOffset(kv.Value);
            _previewOffsetIds.Add(kv.Key);
        }
    }

    // Puts every previewed root and ground back on its data position (the ground bias stays).
    // Cheap no-op when nothing is previewed; safe to call from any rebuild or unload path.
    public void ClearPreviewOffsets()
    {
        if (_previewOffsetIds.Count == 0) return;
        foreach (var id in _previewOffsetIds)
            if (_envRenders.TryGetValue(id, out var er))
            {
                er.previewOffset = Vector3.zero;
                ApplyEnvOffset(er);
            }
        _previewOffsetIds.Clear();
    }

    // Shared resize core: clamps to the sane band, preserves the height range, no-ops on no change.
    private void SetTerrainSizeClamped(float width, float length)
    {
        var t = Ground;
        if (t == null) return;
        if (!TerrainStack.ClampSize(width, length, out float w, out float l)) return;
        var tData = t.terrainData;
        float y = tData.size.y > 0f ? tData.size.y : 1f;   // preserve the existing height range
        if (!Mathf.Approximately(tData.size.x, w) || !Mathf.Approximately(tData.size.z, l))
            tData.size = new Vector3(w, y, l);
    }

    // -----------------------------------------------------------------------
    // Ground height (Shape ground brush)
    //
    // The heightmap contract: HeightBrush.RESOLUTION samples, HeightBrush.RANGE_METERS of range,
    // flat ground at the normalized base HeightBrush.BASE_NORMALIZED, and the Terrain parked at
    // HeightBrush.BASE_WORLD_Y so that base plane is world y = 0 (where every environment authored
    // before sculpting already sits). Every "sit on the ground" placement in this file goes through
    // DrapeY, which adds the park back, so it is invisible to callers.
    // -----------------------------------------------------------------------

    private void Awake()
    {
        // The scene terrain is only the template the per-env grounds are copied from.
        if (targetTerrain != null) targetTerrain.gameObject.SetActive(false);
        RefreshBlankGround();
        // Water surfaces are walked through, not on (see RenderWater).
        Physics.IgnoreLayerCollision(WaterLayer, WalkerLayer, true);
    }

    private void OnDestroy()
    {
        _destroying = true;
        ClearRendered();
        DestroyTerrain(ref _blankTerrain, ref _blankData);
    }

    // Applies the contract to a ground copy. Setting heightmapResolution resets the heights AND
    // the size, so the size is captured first and restored (with the fixed Y range) afterwards, and
    // the reset zeros (15 m below the base) are refilled flat. Only ever touches a runtime copy of
    // the TerrainData, never the shared asset.
    private void EnsureHeightSetup()
    {
        var terrain = Ground;
        if (terrain == null) return;
        var tData = terrain.terrainData;
        if (tData == null) return;

        Vector3 size = tData.size;
        bool resChanged = tData.heightmapResolution != HeightBrush.RESOLUTION;
        if (resChanged) tData.heightmapResolution = HeightBrush.RESOLUTION;

        if (resChanged || !Mathf.Approximately(tData.size.y, HeightBrush.RANGE_METERS) ||
            !Mathf.Approximately(tData.size.x, size.x) || !Mathf.Approximately(tData.size.z, size.z))
            tData.size = new Vector3(size.x, HeightBrush.RANGE_METERS, size.z);

        var p = terrain.transform.position;
        if (!Mathf.Approximately(p.y, HeightBrush.BASE_WORLD_Y))
            terrain.transform.position = new Vector3(p.x, HeightBrush.BASE_WORLD_Y, p.z);

        if (resChanged) tData.SetHeights(0, 0, FlatHeights(tData.heightmapResolution));
    }

    private static float[,] FlatHeights(int res)
    {
        var h = new float[res, res];
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
                h[z, x] = HeightBrush.BASE_NORMALIZED;
        return h;
    }

    // Replays site.heightStrokes in order onto a flat base and pushes the whole heightmap once. Runs
    // on every render / undo whose site changed, so it is the authoritative ground; the live
    // brush (StampHeightLive) previews exactly this. Objects, buildings, paths and fences drape via
    // DrapeY, so callers render geometry after this. Site-fill overlays
    // (_siteOverlays) composite surface paint only; a fill's height strokes are not applied.
    // Call after ApplyTerrainSize.
    public void ApplyHeightmap(SiteDef site)
    {
        var terrain = Ground;
        if (terrain == null) return;
        EnsureHeightSetup();
        var tData = terrain.terrainData;
        int res = tData.heightmapResolution;
        var heights = FlatHeights(res);

        Vector3 tPos = terrain.transform.position, size = tData.size;
        var win = new HeightWindow { heights = heights, x0 = 0, z0 = 0, res = res, sizeX = size.x, sizeZ = size.z };

        // Water beds are derived, never stored: each body digs to `depth` below the flat base and
        // shapes the shore around its surface height (both fields on the body, WaterCarve). They
        // are carved FIRST, so the height strokes win: the brush is the last word on the ground,
        // and a stroke laid over a bank or a bed keeps what the live preview showed. (Carving last
        // re-dug the bed on release and snapped the brush rim back wherever it touched water.)
        var water = site?.waterBodies;
        if (water != null && water.Count > 0)
            foreach (var body in water)
                if (WaterGeometry.HasGeometry(body))
                    WaterCarve.Carve(ref win, body, HeightClip(body.clipToLot, site), tPos.x, tPos.z);

        var strokes = site?.heightStrokes;
        if (strokes != null && strokes.Count > 0)
            foreach (var s in strokes) ReplayHeightStroke(ref win, s, site, tPos);
        tData.SetHeights(0, 0, heights);
    }

    private static void ReplayHeightStroke(ref HeightWindow win, HeightStrokeDef s, SiteDef site, Vector3 tPos)
    {
        if (s?.points == null) return;
        var kind = HeightBrush.ParseKind(s.brush);
        float radius = Mathf.Clamp(s.radius, 0.1f, 100f);
        float target = HeightBrush.NormalizedFromMeters(s.targetHeight);
        float[][] clip = HeightClip(s.clipToLot, site);
        foreach (var p in s.points)
        {
            if (p == null || p.Length < 3) continue;
            HeightBrush.ReplaySample(ref win, kind, p, p[0] - tPos.x, p[1] - tPos.z, radius, target, clip, tPos.x, tPos.z);
        }
    }

    // The lot polygon a stroke clips to, or null when it does not clip or the site has no parcel.
    private static float[][] HeightClip(bool clipToLot, SiteDef site) =>
        clipToLot && site?.lotBoundary != null && site.lotBoundary.Length >= 3 ? site.lotBoundary : null;

    // Stamps one brush sample into the LIVE heightmap during a drag: read the disc's sample window,
    // stamp, write it back. Plain SetHeights keeps SampleHeight, the TerrainCollider and the LOD
    // current every frame, and the window is only the disc, so it stays cheap. (If a 30 m brush
    // ever stutters, switch to SetHeightsDelayLOD here and SyncHeightmap on release.) The stroke is
    // also recorded in the data model, so ApplyHeightmap reproduces it on reload / undo.
    public void StampHeightLive(Vector3 worldPos, HeightBrushKind kind, float radius, float amount,
                                float targetHeightMeters, bool clipToLot, SiteDef site)
    {
        var terrain = Ground;
        if (terrain == null) return;
        var tData = terrain.terrainData;
        Vector3 tPos = terrain.transform.position, size = tData.size;
        int res = tData.heightmapResolution;
        float cx = worldPos.x - tPos.x, cz = worldPos.z - tPos.z;
        if (!HeightBrush.SampleWindow(cx, cz, radius, size.x, size.z, res, out int x0, out int z0, out int w, out int h)) return;

        float[,] block = tData.GetHeights(x0, z0, w, h);          // [h, w] = [z, x]
        var win = new HeightWindow { heights = block, x0 = x0, z0 = z0, res = res, sizeX = size.x, sizeZ = size.z };
        HeightBrush.Stamp(ref win, kind, cx, cz, radius, amount, HeightBrush.NormalizedFromMeters(targetHeightMeters),
                          HeightClip(clipToLot, site), tPos.x, tPos.z);
        tData.SetHeights(x0, z0, block);
    }

    // Ground height above the flat base plane at world (x, z), in meters. With the terrain parked
    // at BASE_WORLD_Y the base plane is world y = 0, so this is the surface Y itself; named for the
    // Flatten brush, whose target is sampled here at the press.
    public float SampleHeightAboveBase(float x, float z) => DrapeY(x, z);

    // Removes one environment's geometry and ground (e.g. on close/archive). If it was the active
    // one, the caller is responsible for choosing a new active env (or leaving none).
    public void UnloadEnvironment(string envId)
    {
        if (envId == null || !_envRenders.TryGetValue(envId, out var er)) return;
        ClearPreviewOffsets();
        DestroyRoot(er);
        _envRenders.Remove(envId);
        if (_activeEnvId == envId) _activeEnvId = null;
        ApplyStackBias();
        RefreshBlankGround();
    }

    // Clears every rendered environment.
    public void ClearRendered()
    {
        ClearPreviewOffsets();
        foreach (var er in _envRenders.Values) DestroyRoot(er);
        _envRenders.Clear();
        _activeEnvId = null;
        if (!_destroying) RefreshBlankGround();
    }
    private bool _destroying;

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
        er ??= new EnvRender { loadSeq = ++_loadSeq };
        var go = new GameObject($"RenderedEnvironment:{env.name}");
        go.transform.SetParent(transform, false);
        er.root = go.transform;
        _envRenders[env.id] = er;
        return er;
    }

    private static void ClearEnvRender(EnvRender er)
    {
        er.instanceToGO.Clear();
        er.InvalidateLockCache();
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
        if (er == null) return;
        DestroyTerrain(ref er.terrain, ref er.terrainData);
        if (er.root == null) return;
        if (Application.isPlaying) Destroy(er.root.gameObject);
        else                       DestroyImmediate(er.root.gameObject);
        er.root = null;
        er.instanceToGO.Clear();
        er.InvalidateLockCache();
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

    // Walkthrough: while walking, backdrop environments must be solid too (you walk through the
    // twin you are inspecting), so their colliders stay on; the dim tint is unchanged. Restored to
    // the normal "backdrops are click-through" state when the walk ends.
    private bool _backdropCollidersSolid;
    public void SetBackdropCollidersSolid(bool solid)
    {
        if (_backdropCollidersSolid == solid) return;
        _backdropCollidersSolid = solid;
        RefreshLockStates();
    }

    // Editor "Low detail" preview: hides (SetActive false) every spawned GO whose def is marked
    // optional, across every loaded env, backdrops and site fills included. Optional GOs stay in
    // instanceToGO so their ids still resolve for the library rows, selection and delete; the
    // selection code treats an inactive GO like an excluded instance (no gizmo). No rebuild.
    // ApplyLockState gathered its caches with includeInactive:true, so they stay valid and this
    // must NOT call InvalidateLockCache (that would trigger a full sweep for nothing).
    public void SetOptionalHidden(bool hidden)
    {
        if (_optionalHidden == hidden) return;
        _optionalHidden = hidden;
        foreach (var er in _envRenders.Values)
            foreach (var go in er.instanceToGO.Values)
                if (go != null && go.TryGetComponent<InstanceMarker>(out var m) && m.isOptional)
                    go.SetActive(!hidden);
    }

    // Cheap per-instance update after an optional-flag edit: re-stamps the marker and applies the
    // current preview state. Nothing else about the GO changes, so no re-render. Works for env
    // instances and for embedded decor (both live in instanceToGO).
    public void SetInstanceOptional(string id, bool optional)
    {
        var go = GetInstanceGO(id);
        if (go == null) return;
        if (go.TryGetComponent<InstanceMarker>(out var m)) m.isOptional = optional;
        go.SetActive(!(optional && _optionalHidden));
    }

    private void ApplyLockState(EnvRender er, bool active)
    {
        if (er?.root == null) return;

        // Gather the component lists once per rebuild instead of sweeping the whole hierarchy on
        // every call; the sweeps were the cost here at 200+ objects and thousands of tile GOs.
        if (er.lockRenderers == null || er.lockColliders == null)
        {
            er.lockColliders = er.root.GetComponentsInChildren<Collider>(true);
            er.lockRenderers = er.root.GetComponentsInChildren<Renderer>(true);
            er.lockCacheVersion++;   // fresh instances carry no dim block yet: force a re-apply
        }

        // Unchanged state on an unchanged hierarchy is a no-op (typical for backdrop envs).
        bool solid = _backdropCollidersSolid;
        if (er.lockAppliedVersion == er.lockCacheVersion &&
            er.lockAppliedActive == active && er.lockAppliedSolid == solid) return;
        er.lockAppliedVersion = er.lockCacheVersion;
        er.lockAppliedActive  = active;
        er.lockAppliedSolid   = solid;

        foreach (var col in er.lockColliders)
        {
            if (col == null) continue;   // destroyed between invalidations
            col.enabled = active || solid;
        }

        foreach (var rend in er.lockRenderers)
        {
            if (rend == null) continue;
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
                // The tint is opaque; a translucent water surface keeps its own alpha so a backdrop
                // pond dims without turning into a solid slab.
                Color tint = LOCKED_TINT;
                if (rend.GetComponent<WaterMarker>() != null && rend.sharedMaterial != null)
                {
                    var m = rend.sharedMaterial;
                    if (m.HasProperty(BaseColorProp)) tint.a = m.GetColor(BaseColorProp).a;
                    else if (m.HasProperty(ColorProp)) tint.a = m.GetColor(ColorProp).a;
                }
                _dimBlock.SetColor(BaseColorProp, tint);
                _dimBlock.SetColor(ColorProp,     tint);
                rend.SetPropertyBlock(_dimBlock);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Terrain painting — fixed: uses rectMeters (world meters) not canvas coords
    // -----------------------------------------------------------------------

    private void PaintTerrain(SiteDef site, string envId = null)
    {
        var terrain = Ground;
        if (terrain == null)        { Debug.LogError("[WorldRenderer] targetTerrain not assigned.");  return; }
        if (terrainRegistry == null){ Debug.LogError("[WorldRenderer] terrainRegistry not assigned."); return; }

        TerrainData tData = terrain.terrainData;
        int res = tData.alphamapResolution;
        Vector3 terrainSize = tData.size;
        // Site data is world meters; alphamap cells start at the terrain's corner (site.terrainOrigin).
        // The data corner, not the transform: a Move site preview may have the transform offset.
        EnvironmentScale.TerrainCorner(site, out float cornerX, out float cornerZ);
        Vector3 terrainPos  = new Vector3(cornerX, 0f, cornerZ);

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
        PaintZonesIntoMap(site.terrainZones, map, res, layerCount, terrainSize, terrainPos, keyToIndex, null);
        PaintStrokesIntoMap(site.surfaceStrokes, map, res, layerCount, terrainSize, terrainPos, keyToIndex, null);

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
                    float zMeters = terrainPos.z + ((y + 0.5f) / res) * terrainSize.z;
                    for (int x = 0; x < res; x++)
                    {
                        float xMeters = terrainPos.x + ((x + 0.5f) / res) * terrainSize.x;
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

        // Site fills: composite each fill's ground (zones + strokes, already projected into host
        // meters by SiteFit) clipped to its site polygon. Painted after the host's lot mask so the
        // fill reads on top of host ground; the clip keeps the paint inside the drawn site. The
        // fill's own lotBoundary/outsideTerrainType are deliberately ignored — applying them here
        // would flood the host with the child's outside type (water).
        if (envId != null && _siteOverlays.TryGetValue(envId, out var overlays))
            foreach (var ov in overlays)
            {
                if (ov.site == null || ov.clip == null || ov.clip.Length < 3) continue;
                PaintZonesIntoMap(ov.site.terrainZones, map, res, layerCount, terrainSize, terrainPos, keyToIndex, ov.clip);
                PaintStrokesIntoMap(ov.site.surfaceStrokes, map, res, layerCount, terrainSize, terrainPos, keyToIndex, ov.clip);
            }

        tData.SetAlphamaps(0, 0, map);
    }

    // Zone-rect fill for one zone list into the in-memory alphamap. `clip` (optional, world-meter
    // polygon) drops cells whose center falls outside it — used when compositing a site fill's
    // ground into the host splat. The rect scan is already bounded to the zone, so the clip test
    // only runs over that window.
    private void PaintZonesIntoMap(List<TerrainZoneDef> zones, float[,,] map, int res, int layerCount,
                                   Vector3 terrainSize, Vector3 terrainPos,
                                   Dictionary<string, int> keyToIndex, float[][] clip)
    {
        if (zones == null) return;
        foreach (var zone in zones)
        {
            if (zone?.rectMeters == null || zone.rectMeters.Length < 4) continue;

            string key = zone.terrainType?.ToLower() ?? "";
            if (!keyToIndex.TryGetValue(key, out int idx))
            {
                Debug.LogError($"[WorldRenderer] Terrain type '{zone.terrainType}' not found in TerrainRegistry.");
                continue;
            }

            // rectMeters is in world meters; shift to the terrain's corner, then normalize against
            // the actual terrain dimensions.
            int xStart = Mathf.Clamp(Mathf.RoundToInt(((zone.rectMeters[0] - terrainPos.x) / terrainSize.x) * res), 0, res);
            int yStart = Mathf.Clamp(Mathf.RoundToInt(((zone.rectMeters[1] - terrainPos.z) / terrainSize.z) * res), 0, res);
            int xEnd   = Mathf.Clamp(Mathf.RoundToInt(((zone.rectMeters[2] - terrainPos.x) / terrainSize.x) * res), 0, res);
            int yEnd   = Mathf.Clamp(Mathf.RoundToInt(((zone.rectMeters[3] - terrainPos.z) / terrainSize.z) * res), 0, res);

            for (int y = yStart; y < yEnd; y++)
                for (int x = xStart; x < xEnd; x++)
                {
                    if (clip != null && !EnvironmentScale.PointInPolygon(
                            terrainPos.x + ((x + 0.5f) / res) * terrainSize.x,
                            terrainPos.z + ((y + 0.5f) / res) * terrainSize.z, clip)) continue;
                    for (int l = 0; l < layerCount; l++) map[y, x, l] = 0f;
                    map[y, x, idx] = 1f;
                }
        }
    }

    // Stroke stamping for one stroke list; same optional clip as PaintZonesIntoMap.
    private void PaintStrokesIntoMap(List<SurfaceStrokeDef> strokes, float[,,] map, int res, int layerCount,
                                     Vector3 terrainSize, Vector3 terrainPos,
                                     Dictionary<string, int> keyToIndex, float[][] clip)
    {
        if (strokes == null) return;
        foreach (var stroke in strokes)
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
                StampIntoMap(map, res, layerCount, terrainSize,
                             new Vector3(center.x - terrainPos.x, 0f, center.z - terrainPos.z), radius, idx, square,
                             BrushGeometry.ResolveStampAngleRad(angleDeg, dirRad), clip, terrainPos));
        }
    }

    // A square footprint's corners reach radius*√2, so its scan box must be that much wider than
    // a disc's. Kept as one constant so both stamp paths size their bounds identically.
    private const float SQUARE_REACH = 1.4143f;

    public static bool IsSquareShape(string shape) =>
        string.Equals(shape, "square", StringComparison.OrdinalIgnoreCase);

    // True when alphamap cell (x, y) falls inside the brush footprint centered at `centerMeters`
    // (terrain-local meters: the caller has already subtracted the terrain's corner).
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
    // alphamap. Center is in terrain-local meters; radius in meters. Used by the rasterizer in
    // PaintTerrain. `clip` is a world-meter polygon, so `terrainPos` is needed to test cells against it.
    private static void StampIntoMap(float[,,] map, int res, int layerCount, Vector3 terrainSize,
                                     Vector3 centerMeters, float radius, int idx,
                                     bool square = false, float dirRad = 0f, float[][] clip = null,
                                     Vector3 terrainPos = default) =>
        StampIntoBlock(map, 0, 0, res, res, res, layerCount, terrainSize,
                       centerMeters, radius, idx, square, dirRad, clip, terrainPos);

    // Sets one filled brush footprint into a *sub-block* of the alphamap: `bx0/by0` locate the
    // block's origin in alphamap cells and `bw/bh` are its dims, so the partial-update paths can
    // rasterize into a small window and push it with one SetAlphamaps. `centerMeters` is terrain-local
    // (the space the block's indices are derived from); `clip` is world meters, hence `terrainPos`.
    private static void StampIntoBlock(float[,,] block, int bx0, int by0, int bw, int bh,
                                       int res, int layerCount, Vector3 terrainSize,
                                       Vector3 centerMeters, float radius, int idx,
                                       bool square, float dirRad, float[][] clip = null,
                                       Vector3 terrainPos = default)
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
                if (clip != null && !EnvironmentScale.PointInPolygon(
                        terrainPos.x + ((x + 0.5f) / res) * terrainSize.x,
                        terrainPos.z + ((y + 0.5f) / res) * terrainSize.z, clip)) continue;
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
        var terrain = Ground;
        if (terrain == null || terrainRegistry == null) return -1;
        int layerCount = terrain.terrainData.terrainLayers?.Length ?? 0;
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
        var terrain = Ground;
        if (terrain == null || terrainRegistry == null) return;

        TerrainData tData = terrain.terrainData;
        int res = tData.alphamapResolution;
        int layerCount = tData.terrainLayers != null ? tData.terrainLayers.Length : 0;
        if (layerCount == 0) return;

        int idx = ResolveLiveLayerIndex(terrainType);
        if (idx < 0) return;

        Vector3 terrainPos  = terrain.transform.position;
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
    private Terrain _liveTerrain;                            // the ground the run started on

    // Starts a live run. Pair with EndLiveSurfaceRun — without it the snapshot leaks and a later
    // run would restore stale ground. The run stays on the ground it began on, so a change of
    // active env mid-drag can't put its snapshot back into another env's splat.
    public void BeginLiveSurfaceRun()
    {
        _liveRunActive = true;
        _liveTerrain = Ground;
        _liveBase = _liveWork = null;
        _liveW = _liveH = 0;
    }

    // Repaints the run at its current shape. Cheap enough for every frame of a drag: one array copy
    // plus one SetAlphamaps over the run's bounding window (not the whole terrain).
    public void UpdateLiveSurfaceRun(SurfaceStrokeDef stroke)
    {
        if (!_liveRunActive || _liveTerrain == null || stroke?.points == null) return;
        using var scope = GroundScope(_liveTerrain);
        int idx = ResolveLiveLayerIndex(stroke.terrainType);
        if (idx < 0) return;

        TerrainData tData = _liveTerrain.terrainData;
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

        Vector3 tPos = _liveTerrain.transform.position, tSize = tData.size;
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
        _liveTerrain = null;
        _liveBase = _liveWork = null;
        _liveW = _liveH = 0;
    }

    private void RestoreLiveSurfaceRun()
    {
        if (_liveBase == null || _liveTerrain == null || _liveW <= 0 || _liveH <= 0) return;
        _liveTerrain.terrainData.SetAlphamaps(_liveX0, _liveY0, _liveBase);
    }

    // Alphamap cell window a stroke can touch, clamped to the map. Terrain-local, matching the
    // indices the live stampers build.
    private bool StrokeCellRect(SurfaceStrokeDef stroke, int res, out int x0, out int y0, out int w, out int h)
    {
        x0 = y0 = w = h = 0;
        var terrain = Ground;
        if (terrain == null || stroke?.points == null) return false;

        Vector3 tPos = terrain.transform.position, tSize = terrain.terrainData.size;
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
        if (inst == null || !OptionalContent.ShouldRender(inst.included, inst.optional, skipOptional)) return;
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
        marker.isOptional  = inst.optional;
        if (inst.optional && _optionalHidden) go.SetActive(false);   // Low detail preview persists across rebuilds
        er.instanceToGO[inst.instanceId] = go;
        er.InvalidateLockCache();
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
            er.InvalidateLockCache();
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
                ctrl.Add(new Vector2(p[0], p[1]));
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

        // AABB prefilter: junctions only exist where paths come within a ribbon width of each
        // other, so spatially separated pairs skip the O(samples^2) segment sweep entirely.
        // Each polyline's bounds are inflated by its full width, which covers every proximity
        // the tests below use (radius = 0.6*maxWidth, endpoint tol = 0.5*maxWidth).
        var boundsMin = new Vector2[built.Count];
        var boundsMax = new Vector2[built.Count];
        for (int p = 0; p < built.Count; p++)
            PathGeometry.PolylineBounds(built[p].dense, built[p].width, out boundsMin[p], out boundsMax[p]);

        UnityEngine.Profiling.Profiler.BeginSample("WR.Junctions");
        for (int a = 0; a < built.Count; a++)
        for (int b = a + 1; b < built.Count; b++)
        {
            if (!PathGeometry.BoundsOverlap(boundsMin[a], boundsMax[a], boundsMin[b], boundsMax[b])) continue;

            var A = built[a]; var B = built[b];
            bool aWider = A.width >= B.width;
            float radius = Mathf.Max(A.width, B.width) * 0.5f * 1.2f;
            string mat = aWider ? A.material : B.material;
            int topStack = Mathf.Max(A.stack, B.stack);

            // Segment/segment crossings (X-junctions), behind a cheap per-segment box precheck.
            for (int i = 0; i < A.dense.Count - 1; i++)
            for (int j = 0; j < B.dense.Count - 1; j++)
            {
                if (!PathGeometry.SegmentBoxesOverlap(A.dense[i], A.dense[i + 1], B.dense[j], B.dense[j + 1])) continue;
                if (PathGeometry.SegmentsIntersect(A.dense[i], A.dense[i + 1], B.dense[j], B.dense[j + 1], out Vector2 hit))
                    AddJunction(hit, radius, mat, topStack);
            }

            // Endpoint-touches-centerline (T-junctions) the crossing test can miss.
            float tol = Mathf.Max(A.width, B.width) * 0.5f;
            TestEndpointTouch(A.dense, B.dense, tol, radius, mat, topStack, AddJunction);
            TestEndpointTouch(B.dense, A.dense, tol, radius, mat, topStack, AddJunction);
        }
        UnityEngine.Profiling.Profiler.EndSample();
    }

    private static void TestEndpointTouch(List<Vector2> ends, List<Vector2> line, float tol,
                                          float radius, string mat, int topStack,
                                          System.Action<Vector2, float, string, int> add)
    {
        foreach (var e in new[] { ends[0], ends[ends.Count - 1] })
            for (int j = 0; j < line.Count - 1; j++)
                if (PathGeometry.PointSegmentDistance(e, line[j], line[j + 1]) <= tol) { add(e, radius, mat, topStack); break; }
    }

    // SegmentsIntersect / PointSegmentDistance moved to PathGeometry (Authoring) so the junction
    // prefilter is unit-testable alongside the math it guards.

    // Data-space Y of ONE env's ground at *world* (x, z): the env being rendered, else the active
    // one. Single source of truth for every "sit on the ground" placement — objects, buildings and
    // paths must all sample the same way, or they drape onto slightly different surfaces. Strictly
    // the env's own ground (edge-clamped outside its rectangle), never a neighbor's: an object that
    // grounded on a backdrop's hill while being dragged would jump on the next re-render.
    // SampleHeight returns a height relative to the terrain's base, which is parked at
    // BASE_WORLD_Y; the backdrop bias is left out on purpose, the env's root carries it.
    private float DrapeY(float x, float z)
    {
        var terrain = Ground;
        return terrain != null ? HeightBrush.BASE_WORLD_Y + terrain.SampleHeight(new Vector3(x, 0f, z)) : 0f;
    }

    // World Y of the ground you would stand on at *world* (x, z), across every loaded env: what
    // the walker, the headset and the cursor ask. The active env's ground answers inside its
    // rectangle, else the highest loaded ground that holds the point, else the active ground's
    // edge (TerrainStack.TryPickGround). Includes each ground's backdrop bias.
    public float SampleTerrainSurfaceY(float x, float z)
    {
        _pickRects.Clear();
        _pickTerrains.Clear();
        int activeIndex = -1;
        foreach (var kv in _envRenders)
        {
            var er = kv.Value;
            if (er.terrain == null) continue;
            if (kv.Key == _activeEnvId) activeIndex = _pickTerrains.Count;
            _pickTerrains.Add(er.terrain);
            _pickRects.Add(TerrainStack.TryRectOf(er.env?.site, out var rect) ? rect : (TerrainStack.GroundRect?)null);
        }
        // A fill can be the active env; its ground is its host's.
        if (activeIndex < 0 && ActiveTerrain != null) activeIndex = _pickTerrains.IndexOf(ActiveTerrain);

        _pickPoint = new Vector3(x, 0f, z);
        _pickSample ??= i => _pickTerrains[i].transform.position.y + _pickTerrains[i].SampleHeight(_pickPoint);
        return TerrainStack.TryPickGround(_pickRects, activeIndex, x, z, _pickSample, out _, out float y) ? y : 0f;
    }
    private readonly List<TerrainStack.GroundRect?> _pickRects = new();
    private readonly List<Terrain> _pickTerrains = new();
    private Vector3 _pickPoint;
    private Func<int, float> _pickSample;

    // Final Y for a path vertex at world (x, z): ground + z-fight lift. Public so the
    // edit-mode live preview drapes its ribbon exactly like the committed render does.
    public float SamplePathSurfaceY(float x, float z) => DrapeY(x, z) + pathYEpsilon;

    // -----------------------------------------------------------------------
    // Water bodies: one flat translucent mesh per WaterBodyDef at its surface height. The bed under
    // it was carved by ApplyHeightmap, so the surface is never coplanar with the ground inside the
    // outline and needs no z-fight lift. The mesh sits on Unity's built-in Water layer with a plain
    // (non-trigger: PhysX refuses concave trigger meshes) MeshCollider; EditController's RaycastAll
    // picks it, while the walkthrough walker (Ignore Raycast layer) is told not to collide with the
    // Water layer, so it wades down into the bed instead of standing on the surface.
    // -----------------------------------------------------------------------

    public const int WaterLayer  = 4;   // Unity built-in "Water"
    public const int WalkerLayer = 2;   // "Ignore Raycast": WalkthroughController's walker and ghost

    private void RenderWater(List<WaterBodyDef> bodies, EnvRender er)
    {
        if (bodies == null || bodies.Count == 0 || er?.root == null) return;
        var palette = WaterPalette;
        foreach (var body in bodies)
        {
            if (!WaterGeometry.HasGeometry(body)) continue;
            Mesh mesh = BuildWaterMesh(body, WaterGeometry.SurfaceY(body));
            if (mesh == null) continue;

            var go = new GameObject($"Water ({body.kind})") { layer = WaterLayer };
            go.transform.SetParent(er.root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            Material mat = palette != null ? palette.GetMaterial(body.material) : null;
            mr.sharedMaterial = mat != null ? mat : MissingMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.AddComponent<WaterMarker>().waterId = body.id;
        }
    }

    // The committed water mesh for a body at surface height `y`, in world meters. Public so the
    // editor's live preview builds the identical mesh and nothing changes on commit.
    public Mesh BuildWaterMesh(WaterBodyDef body, float y)
    {
        var ctrl = WaterGeometry.ControlPoints(body?.points);
        if (body == null || ctrl.Count < WaterGeometry.MinPoints(body.kind)) return null;
        if (WaterGeometry.IsRiver(body.kind))
            return WaterGeometry.BuildRiverMesh(WaterGeometry.RiverCenterline(ctrl, body.width, body.smoothing), body.width, y);
        return WaterGeometry.BuildPondMesh(ctrl, y);
    }

    public Material GetWaterMaterial(string materialId)
    {
        var palette = WaterPalette;
        return palette != null && palette.Has(materialId) ? palette.GetMaterial(materialId) : null;
    }

    // Material a path of `materialId` renders with — shared with the live preview for WYSIWYG.
    public Material GetPathMaterial(string materialId) => pathMaterialPalette != null ? pathMaterialPalette.GetMaterial(materialId) : null;

    // -----------------------------------------------------------------------
    // Fence runs — FenceDef polylines rendered as repeated panel/post prefabs along the terrain
    // -----------------------------------------------------------------------

    private void RenderFences(List<FenceDef> fences, EnvRender er)
    {
        if (fences == null || fences.Count == 0) return;
        if (FencePalette == null)   // property: falls back to Resources when the slot is unwired
        {
            Debug.LogError("[WorldRenderer] fencePalette not assigned — cannot render fences.");
            return;
        }

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
                ctrl.Add(new Vector2(p[0], p[1]));
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
    // rectangle when no explicit boundary is set). Pure authoring aid: a terrain-draped ribbon mesh,
    // rebuilt with every render, no collider (never interferes with picking).
    // -----------------------------------------------------------------------

    [Header("Lot frame")]
    [SerializeField] private Color lotFrameColor  = new(1f, 0.85f, 0.2f);    // amber, reads over grass & water
    [SerializeField] private Color siteFrameColor = new(0.25f, 0.85f, 0.95f); // cyan: drawn-site plot outlines
    [SerializeField] private float lotFrameLift  = 0.15f;                 // extra lift above the path plane

    private void RenderLotFrame(SiteDef site, EnvRender er) =>
        RenderPolygonFrame(EnvironmentScale.EffectiveLotPolygon(site), er, lotFrameColor, "Lot frame");

    // Drawn-site outlines: one frame per SitePlotDef under the HOST's root, in their own color so
    // plots read apart from the amber lot edge. Named "Site frame:<id>" for the editor's hide hook.
    private void RenderSiteFrames(EnvironmentDef env, EnvRender er)
    {
        if (env?.sites == null) return;
        foreach (var s in env.sites)
            if (s?.boundary != null && s.boundary.Length >= 3)
                RenderPolygonFrame(s.boundary, er, siteFrameColor, $"Site frame:{s.id}");
    }

    private void RenderPolygonFrame(float[][] poly, EnvRender er, Color color, string goName)
    {
        if (poly == null || poly.Length < 3 || er?.root == null) return;

        var corners = new List<Vector2>(poly.Length);
        foreach (var p in poly)
            if (p != null && p.Length >= 2) corners.Add(new Vector2(p[0], p[1]));
        if (corners.Count < 3) return;

        Mesh mesh = BuildPolygonFrameMesh(corners, closed: true, lift: lotFrameLift);
        if (mesh == null) return;

        var go = new GameObject(goName);
        go.transform.SetParent(er.root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = FrameMaterial(color);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        // Deliberately no collider: the frame is an authoring aid and must never be hit by picking.
    }

    // The draped band for one polygon outline, in WORLD XZ metres. A flat ribbon rather than a
    // LineRenderer: every edge vertex samples the terrain at its own XZ (PathMesh.Build), so the
    // band lies flat across a side-slope and hugs grade between corners instead of cutting chords
    // through it, and its width is a real world width instead of a camera-facing billboard.
    // Public so EditController's live preview builds the identical mesh and nothing changes on
    // commit. Returns null when the ring is degenerate.
    public Mesh BuildPolygonFrameMesh(IReadOnlyList<Vector2> cornersWorld, bool closed, float lift)
    {
        if (cornersWorld == null) return null;

        float spacing = PolygonFrame.Spacing(PolygonFrame.Perimeter(cornersWorld, closed));
        var ring = PolygonFrame.DenseRing(cornersWorld, spacing, closed);
        if (ring.Count < 2) return null;

        float HeightAt(float x, float z) => SamplePathSurfaceY(x, z) + lift;
        var centerline = new List<Vector3>(ring.Count);
        foreach (var p in ring) centerline.Add(new Vector3(p.x, HeightAt(p.x, p.y), p.y));

        float width = PolygonFrame.Width(PolygonFrame.BboxDiagonal(cornersWorld));
        return PathMesh.Build(centerline, width, HeightAt, capSegments: 0);
    }

    // Lift the frames sit at above the path surface, so the preview can match the committed band.
    public float FrameLift => lotFrameLift;

    // One unlit line material per frame color (lot amber, site cyan), lazily built and cached.
    private readonly Dictionary<Color, Material> _frameMaterials = new();
    private Material FrameMaterial(Color color)
    {
        if (_frameMaterials.TryGetValue(color, out var m) && m != null) return m;
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
                        ?? Shader.Find("Unlit/Color")
                        ?? Shader.Find("Sprites/Default");
        m = new Material(shader) { name = "PolygonFrameLine" };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color"))     m.SetColor("_Color", color);
        _frameMaterials[color] = m;
        return m;
    }

    // Same cached material the committed frame uses, for EditController's live preview.
    public Material GetFrameMaterial(Color color) => FrameMaterial(color);

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

        Vector3 worldXZ = new Vector3(position[0], 0f, position[2]);   // stored XZ is world meters
        float   groundY    = DrapeY(worldXZ.x, worldXZ.z);

        go.transform.position = new Vector3(worldXZ.x, groundY, worldXZ.z);

        // Snap the bottom of the *whole* prefab to the ground. This must be the union of every
        // renderer, not GetComponentInChildren's first depth-first hit: a multi-part prefab whose
        // first sub-mesh isn't its lowest geometry (an FBX whose railing exports before its base,
        // a tree split into canopy + trunk) would otherwise be pushed *down* by the difference and
        // end up buried. Inactive renderers stay excluded — a hidden LOD/variant child shouldn't
        // drag the object down.
        var rends = go.GetComponentsInChildren<Renderer>();
        if (rends.Length > 0)
        {
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            go.transform.position += new Vector3(0f, groundY - b.min.y, 0f);
        }

        // position[1] is a vertical offset above the terrain-snapped resting height.
        go.transform.position += new Vector3(0f, position[1], 0f);
    }

    // Building counterpart of GroundObjectInstance: same terrain snap, but *no* bounds snap —
    // a building's tile grid is authored from a corner pivot sitting exactly at worldPos (see
    // RenderTiledBuilding), so lifting it by its renderer bounds would desync the edit-mode grid
    // overlay. Kept next to the object version so both stay in step; EditController calls it to
    // re-ground a building after a move, matching what a re-render would produce.
    public void GroundBuildingInstance(GameObject go, float[] position)
    {
        if (go == null || position == null || position.Length < 3) return;

        Vector3 worldXZ = new Vector3(position[0], 0f, position[2]);   // stored XZ is world meters

        // position[1] is a vertical offset above the terrain surface, as for objects.
        go.transform.position = new Vector3(worldXZ.x,
                                            DrapeY(worldXZ.x, worldXZ.z) + position[1],
                                            worldXZ.z);
    }

    // -----------------------------------------------------------------------
    // Building instances — looks up BuildingDef to derive grid dimensions
    // -----------------------------------------------------------------------

    private void RenderBuildingInstances(List<BuildingInstance> instances,
                                          IReadOnlyDictionary<string, BuildingDef> buildingDefs, EnvRender er)
    {
        Transform root     = er.root;
        // Legacy bay-massing renderer, resolved lazily: only the palette-misconfiguration branch below
        // needs it, and BuildingGenerator.GetRenderer() logs on every call.
        Renderer bayRend   = null;
        bool bayRendTried  = false;

        foreach (var inst in instances)
        {
            if (inst == null || !OptionalContent.ShouldRender(inst.included, inst.optional, skipOptional)) continue;
            if (inst.position == null || inst.position.Length < 3) continue;

            if (!buildingDefs.TryGetValue(inst.buildingId, out BuildingDef bdef))
            {
                Debug.LogError($"[WorldRenderer] BuildingDef '{inst.buildingId}' not found. Skipping instance '{inst.instanceId}'.");
                continue;
            }

            float posY   = inst.position[1];   // vertical offset above the terrain surface
            // Stored XZ is world meters (see ApplyTerrainOrigin), which is also what SampleHeight takes.
            float worldX = inst.position[0];
            float worldZ = inst.position[2];
            var worldPos = new Vector3(worldX, DrapeY(worldX, worldZ) + posY, worldZ);

            bool hasTiles = bdef.tiles != null && bdef.tiles.Count > 0;
            GameObject bldgRoot;

            if (!hasTiles)
            {
                // An empty def (every tile deleted, or a legacy record) renders as a neutral pad — never
                // as the legacy bay massing, which drew an unrelated old prefab with a different pivot
                // and rotation and made it impossible to tell where the building actually was.
                bldgRoot = RenderEmptyBuildingPlaceholder(bdef, inst, worldPos, root);
            }
            else if (tileShapePalette != null)
            {
                bldgRoot = RenderTiledBuilding(bdef, inst, worldPos, root);
            }
            else
            {
                // Misconfiguration only: tiles exist but no TileShapePalette is wired. Fall back to the
                // legacy bay massing so the building is at least visible, and say so loudly.
                Debug.LogError($"[WorldRenderer] tileShapePalette not assigned — rendering tiled building '{bdef.name}' as bay massing.");
                if (!bayRendTried) { bayRend = buildingGenerator != null ? buildingGenerator.GetRenderer() : null; bayRendTried = true; }
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
                marker.isOptional  = inst.optional;
                er.instanceToGO[inst.instanceId] = bldgRoot;

                // M4: render embedded objects relative to this building instance
                RenderEmbeddedObjects(bdef, worldPos,
                    Quaternion.Euler(inst.rotationX, inst.rotationY, inst.rotationZ), bldgRoot.transform, er);

                // After the decor spawn so props instantiate under an active parent (Low detail preview).
                if (inst.optional && _optionalHidden) bldgRoot.SetActive(false);
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
        // The style letter resolves once per building; each tile paints it on its unpainted walls.
        string styleWallId = BuildingStyleResolver.WallMaterialId(bdef, BuildingStylePalette, materialPalette);

        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (var tile in bdef.tiles)
        {
            TileSpawner.Spawn(tile, rootGO.transform, tileShapePalette, materialPalette, cs, styleWallId);
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

        // The signs the placed instance carries (BuildingSigns.EntriesFor: its list, else the older
        // single sign, else the def's legacy one) hang on its walls; the tile editor draws the same.
        BuildingSignSpawner.Spawn(bdef, inst, rootGO.transform, cs, FitFor, inst.instanceId);

        return rootGO;
    }

    // Replaces just the signs under a rendered building (a Sign panel edit or one drag step), so the
    // world is not rebuilt for a few plates. No-op for an empty def: its root is the placeholder pad.
    public void RespawnBuildingSign(BuildingInstance inst, BuildingDef bdef)
    {
        if (inst == null || bdef?.tiles == null || bdef.tiles.Count == 0) return;
        var go = GetInstanceGO(inst.instanceId);
        if (go == null) return;
        var old = go.transform.Find(BuildingSignSpawner.RootName);
        if (old != null)
        {
            old.name = BuildingSignSpawner.RootName + " (old)";   // Destroy is deferred; keep Find honest
            if (Application.isPlaying) Destroy(old.gameObject); else DestroyImmediate(old.gameObject);
        }
        float cs = bdef.gridCellSize > 0f ? bdef.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        BuildingSignSpawner.Spawn(bdef, inst, go.transform, cs, FitFor, inst.instanceId);
    }

    // Neutral stand-in for a BuildingDef with no tiles: the same corner-pivot root as a tiled building
    // plus one flat translucent pad over cell (0,0), so the instance stays visible, click-selectable
    // (the pad keeps its BoxCollider; the caller attaches the InstanceMarker), deletable, and
    // double-click-editable — and reads unmistakably as "empty", not as some unrelated prefab.
    private static readonly Color EMPTY_BUILDING_TINT = new(0.78f, 0.80f, 0.84f, 0.45f);

    private GameObject RenderEmptyBuildingPlaceholder(BuildingDef bdef, BuildingInstance inst, Vector3 worldPos, Transform parent)
    {
        var rootGO = new GameObject((string.IsNullOrEmpty(bdef.name) ? "Building" : bdef.name) + " (empty)");
        rootGO.transform.SetParent(parent, false);
        rootGO.transform.position = worldPos;
        rootGO.transform.rotation = Quaternion.Euler(inst.rotationX, inst.rotationY, inst.rotationZ);
        if (inst.scale > 0f) rootGO.transform.localScale = Vector3.one * inst.scale;

        float cs = bdef.gridCellSize > 0f ? bdef.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        const float PAD_H = 0.15f;

        var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
        pad.name = "EmptyBuildingPad";
        pad.transform.SetParent(rootGO.transform, false);
        pad.transform.localPosition = new Vector3(cs * 0.5f, PAD_H * 0.5f, cs * 0.5f);
        pad.transform.localScale    = new Vector3(cs * 0.98f, PAD_H, cs * 0.98f);

        var rend = pad.GetComponent<Renderer>();
        if (rend != null)
        {
            // Start from the same shader chain MissingMaterial uses, but NOT MissingMaterial itself —
            // magenta means "prefab missing", and an empty building is a valid, expected state.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Standard")
                            ?? Shader.Find("Sprites/Default");
            if (shader != null) rend.sharedMaterial = new Material(shader);
            TileBuildingEditor.ApplyTranslucent(pad, EMPTY_BUILDING_TINT);
        }
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

    // Sub-cell fit per shape (pillar, slab), so reseated decor lands on the real tile surface.
    // Public for EditController's Sign section, which resolves the plate the same way.
    public TileFit FitFor(string shapeId) =>
        tileShapePalette != null ? tileShapePalette.GetFit(shapeId) : TileFit.Full;

    private void RenderEmbeddedObjects(BuildingDef bdef, Vector3 bldgWorldPos, Quaternion bldgRot, Transform parent, EnvRender er)
    {
        if (bdef.embeddedObjects == null || prefabRegistry == null) return;

        // Deform-aware pre-pass: rewrite each hosted decor's localPos/rotation/scale from its host
        // tile's CURRENT TileDeform, so props follow the building whenever its skew changes. Legacy
        // defs (no decor rules) are untouched. Idempotent, so multiple instances sharing this bdef
        // are fine. EditController.AfterBuildingSkew relies on this running before its PutBuilding
        // (render-then-PUT) so the reseated values persist to the server.
        float cellSize = bdef.gridCellSize > 0f ? bdef.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        DecorPlacement.ReseatAll(bdef, cellSize, BasisFor, FitFor);

        foreach (var emb in bdef.embeddedObjects)
        {
            if (emb?.localPos == null || emb.localPos.Length < 3) continue;
            if (!OptionalContent.ShouldRender(true, emb.optional, skipOptional)) continue;   // VR viewer skips optional decor
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
            marker.isEmbedded  = true;   // lives on the BuildingDef, not in env.objectInstances
            marker.isOptional  = emb.optional;
            if (emb.optional && _optionalHidden) go.SetActive(false);
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
