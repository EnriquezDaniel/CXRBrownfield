using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Read-only viewer brain (VR headset / second PC). Polls the server's shared pointer
// (GET /api/active) and mirrors the admin's full loaded set: every published environment is
// rendered (the active one plus backdrops), an env the host closes is unloaded, and an env
// whose version bumps is re-fetched and re-rendered. Strictly fetch-and-render: no editing,
// no EditController, no undo. Wire it to a LibraryClient + WorldRenderer in the VRViewer scene.
public class SyncClient : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private LibraryClient libraryClient;  // USER WIRES THIS IN INSPECTOR
    [SerializeField] private WorldRenderer worldRenderer;  // USER WIRES THIS IN INSPECTOR

    [Header("Polling")]
    [SerializeField] private float pollIntervalSeconds = 1.5f;
    [SerializeField] private bool  showStatusOverlay   = true;

    [Header("Detail")]
    // The viewer is the low-performance client: items the author marked optional are never
    // spawned (WorldRenderer.skipOptional). Untick on a strong PCVR rig to render everything. This
    // component exists only in the VRViewer scene, so the desktop editor is unaffected.
    [SerializeField] private bool skipOptional = true;

    // env id → version last rendered, so only new/changed envs are rebuilt and
    // envs that drop out of the published set are unloaded.
    private readonly Dictionary<string, int> _appliedVersions = new();
    private string _appliedActiveId;
    private bool   _fetching;
    private bool   _polling;     // a GET /api/active is outstanding; one at a time, or they stack up behind a dead server
    private string _lastError;   // last error logged, so a dead server logs once, not every poll
    // Each rendered env, so the headset rig can be stood on the active one the first time.
    private readonly Dictionary<string, EnvironmentDef> _envs = new();
    private VRLocomotion _locomotion;
    // Building defs cached across polls and shared by all envs. An env's ids are evicted just
    // before its re-render so an edited building def is refetched instead of served stale.
    private readonly Dictionary<string, BuildingDef> _buildings = new();

    public string Status { get; private set; } = "Starting…";
    // True once at least one environment is on screen (VRStatusText hides itself then).
    public bool HasRendered => _appliedVersions.Count > 0;
    public string ServerUrl => libraryClient != null ? libraryClient.ServerBaseUrl : "";

    private void Start()
    {
        if (libraryClient == null) libraryClient = FindFirstObjectByType<LibraryClient>();
        if (worldRenderer == null) worldRenderer = FindFirstObjectByType<WorldRenderer>();
        if (libraryClient == null || worldRenderer == null)
        {
            Status = "Missing LibraryClient/WorldRenderer reference.";
            Debug.LogError("[SyncClient] " + Status);
            enabled = false;
            return;
        }
        worldRenderer.SetSkipOptional(skipOptional);   // before the first RenderEnvironment
        // The viewer walks through every published env, so backdrops are solid too (the desktop
        // only does this while its Walk mode is on).
        worldRenderer.SetBackdropCollidersSolid(true);
        _locomotion = FindFirstObjectByType<VRLocomotion>();
        Debug.Log($"[SyncClient] Polling {ServerUrl}/api/active every {pollIntervalSeconds:F1} s");
        StartCoroutine(PollLoop());
    }

    private IEnumerator PollLoop()
    {
        var wait = new WaitForSeconds(Mathf.Max(0.25f, pollIntervalSeconds));
        while (true)
        {
            // Skip a poll while a fetch/render is mid-flight; pick up the latest on the next tick.
            if (!_fetching && !_polling)
            {
                _polling = true;
                libraryClient.GetActive(OnActive, OnPollError);
            }
            yield return wait;
        }
    }

    // Shown in the headset by VRStatusText and logged once per distinct error (adb logcat).
    private void OnPollError(string err)
    {
        _polling = false;
        Status = $"Sync error: {err}";
        if (err == _lastError) return;
        _lastError = err;
        Debug.LogWarning($"[SyncClient] {ServerUrl}/api/active failed: {err}");
    }

    private void OnActive(LibraryClient.ActivePointer ptr)
    {
        _polling = false;
        if (_lastError != null)
        {
            Debug.Log($"[SyncClient] Reached {ServerUrl} again.");
            _lastError = null;
        }
        if (ptr == null || _fetching) return;

        // Old-server fallback: a payload without a loaded list means just the single active env.
        var published = ptr.loaded;
        if (published == null)
        {
            published = new List<LibraryClient.LoadedEnvPointer>();
            if (!string.IsNullOrEmpty(ptr.envId))
                published.Add(new LibraryClient.LoadedEnvPointer { envId = ptr.envId, version = ptr.version, name = ptr.name });
        }

        // Envs we rendered that are no longer published → unload immediately (no fetch needed).
        var removeIds = new List<string>();
        foreach (var id in _appliedVersions.Keys)
            if (published.Find(p => p.envId == id) == null) removeIds.Add(id);
        foreach (var id in removeIds)
        {
            worldRenderer.UnloadEnvironment(id);
            _appliedVersions.Remove(id);
            _envs.Remove(id);
            Debug.Log($"[SyncClient] Unloaded '{id}' (no longer published)");
        }

        // Envs that are new or whose version bumped → need a fetch + re-render.
        var stale = new List<LibraryClient.LoadedEnvPointer>();
        foreach (var p in published)
            if (!string.IsNullOrEmpty(p.envId)
                && (!_appliedVersions.TryGetValue(p.envId, out int v) || v != p.version))
                stale.Add(p);

        string activeId = string.IsNullOrEmpty(ptr.envId) ? null : ptr.envId;
        if (stale.Count > 0)                                   StartCoroutine(SyncEnvs(stale, activeId));
        else if (removeIds.Count > 0 || activeId != _appliedActiveId) ApplyActive(activeId);
        else Status = InSyncStatus();
    }

    private IEnumerator SyncEnvs(List<LibraryClient.LoadedEnvPointer> stale, string activeId)
    {
        _fetching = true;
        foreach (var p in stale)
        {
            Status = $"Fetching {p.name}…";

            EnvironmentDef env = null; bool got = false; string error = null;
            libraryClient.GetEnvironment(p.envId,
                e   => { env = e; got = true; },
                err => { error = err; got = true; });
            while (!got) yield return null;
            if (env == null)                                                   // retry next poll
            {
                Status = $"Fetch error: {error}";
                Debug.LogWarning($"[SyncClient] Fetching '{p.name}' failed: {error}");
                continue;
            }

            var ids = new List<string>();
            if (env.buildingInstances != null)
                foreach (var bi in env.buildingInstances) ids.Add(bi.buildingId);
            // Evict this env's defs so edited buildings refresh; other envs' cache entries stay.
            foreach (var id in ids) _buildings.Remove(id);
            yield return BuildingFetch.FetchInto(libraryClient, ids, _buildings);

            worldRenderer.RenderEnvironment(env, _buildings, makeActive: false);
            _appliedVersions[p.envId] = p.version;
            _envs[p.envId] = env;
            Debug.Log($"[SyncClient] Rendered '{env.name}' v{p.version}");
        }
        ApplyActive(activeId);
        _fetching = false;
    }

    // Marks the host's active env active locally (paints the terrain, dims the backdrops) and
    // refreshes the status line. Safe when activeId is null or its fetch failed this cycle.
    private void ApplyActive(string activeId)
    {
        _appliedActiveId = activeId;
        if (activeId != null && _appliedVersions.ContainsKey(activeId))
        {
            worldRenderer.SetActiveEnvironment(activeId);
            // First time only: later syncs must not yank the viewer back to the corner.
            if (_locomotion != null && !_locomotion.IsPlaced && _envs.TryGetValue(activeId, out var env))
                _locomotion.PlaceAtSpawn(env);
        }
        Status = InSyncStatus();
    }

    private string InSyncStatus() =>
        _appliedVersions.Count == 0 ? "No environments published."
                                    : $"In sync: {_appliedVersions.Count} environment(s)";

    private void OnGUI()
    {
        if (!showStatusOverlay) return;
        GUI.Label(new Rect(10, 10, 700, 24), $"[Sync] {Status}");
    }
}
