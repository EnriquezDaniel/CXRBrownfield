using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Left-panel library browser — two tabs:
//   Environments: list, new, load, save, save-as, duplicate, archive; instance include/exclude
//   Buildings:    list, new, edit (opens TileBuildingEditor), archive
public class LibraryBrowser : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private LibraryClient  libraryClient;  // USER WIRES THIS IN INSPECTOR
    [SerializeField] private WorldRenderer  worldRenderer;  // USER WIRES THIS IN INSPECTOR
    [SerializeField] private EditController editController; // USER WIRES THIS IN INSPECTOR

    [Header("UI")]
    [SerializeField] private int panelWidth = 320;

    // ---- tabs ----
    private enum Tab { Environments, Buildings }
    private Tab _tab = Tab.Environments;

    // One loaded environment. Several can be loaded and rendered at once (overlaid), but only
    // the active one (_active) is editable/saveable; the rest render as locked backdrops.
    private class LoadedEnv
    {
        public EnvironmentDef env;
        public readonly Dictionary<string, BuildingDef> buildings = new();
        public bool dirty;
        // False for an auto-created in-memory environment that hasn't been POSTed yet; Save then
        // creates it on the server (POST) instead of overwriting (PUT, which 404s for a new id).
        public bool persisted;
    }

    // ---- environment state ----
    private List<EnvironmentSummary> _envList = new();
    private readonly List<LoadedEnv> _loaded  = new();
    private LoadedEnv _active;

    // ---- site fills ----
    // A fill = a generated child environment rendered inside a host's SitePlotDef. The child record
    // stays pristine on the server; what renders is a deep copy projected into the site bbox
    // (SiteFit.ProjectIntoSite), keyed by a synthetic render id so one child can fill several sites.
    // Fills are managed backdrops: never rows in the Loaded list, never active, never published.
    private class SiteFill
    {
        public string hostEnvId, siteId, childEnvId;
        public string renderId;                    // childEnvId + "@" + siteId (WorldRenderer key)
        public string boundaryKey;                 // serialized boundary at fit time (refit detection)
        public EnvironmentDef projected;           // the deep copy actually rendered
        public readonly Dictionary<string, BuildingDef> buildings = new();
    }
    private readonly List<SiteFill> _fills = new();
    // Building defs cached before any environment is active (e.g. a building fetched for placement);
    // folded into the working environment when one is auto-created. See AddBuildingDef.
    private readonly Dictionary<string, BuildingDef> _pendingBuildings = new();
    private bool    _envBusy;
    private string  _envStatus = "Ready";
    private Vector2 _envListScroll, _loadedScroll, _bInstScroll, _oInstScroll;
    private string  _newEnvName  = "";
    private string  _saveAsName  = "";
    private bool    _showSaveAs;
    private string  _envSearch   = "";       // left-rail search filter (Places list)
    private bool    _showNewEnv;             // inline new-scene name field toggled from the header
    private LoadedEnv _confirmDelete;        // Loaded row whose admin delete-confirmation is open
    private LoadedEnv _confirmUnlock;        // Loaded list: row awaiting unlock confirmation
    private bool    _adminEnabled;           // per-env admin actions (archive / DrawAdminRow) are gated by this
    public  bool    AdminEnabled => _adminEnabled;
    private bool    _lowDetail;              // Low detail preview: optional items hidden, as the VR viewer shows them
    private Vector2 _manageScroll;

    // ---- building state ----
    private List<BuildingSummary> _bldgList = new();
    private bool    _bldgBusy;
    private string  _bldgStatus = "Ready";
    private Vector2 _bldgListScroll;
    private string  _newBldgName = "";

    // ---- public API used by EditController ----
    // All editing operates on the active environment only — that's what enforces "edit one at a time".
    public EnvironmentDef                           CurrentEnvironment  => _active?.env;
    public IReadOnlyDictionary<string, BuildingDef> CurrentBuildingDefs => _active?.buildings;
    // True when the active env carries the persistent read-only "digital twin" flag. A locked env
    // may be active (it owns the shared terrain) but every mutation path checks this and refuses.
    public bool IsActiveLocked => _active?.env?.locked == true;
    public void MarkDirty(bool autoSave = false)
    {
        if (_active == null || _active.env.locked) return;   // locked twin: no dirty, no auto-save
        _active.dirty = true;
        // Schedule the debounced auto-save when the edit must persist on its own (autoSave:
        // site changes) or when Live Share publishes every edit to viewers (VR / 2nd PC).
        if (autoSave || _liveShare) _autoSaveAt = Time.unscaledTime + AutoSaveDebounce;
    }

    // ---- Live Share (host publishing) ----
    // When on, the full loaded set (active env + backdrops) is published as the server's shared
    // pointer and the active env is auto-saved (debounced) on every edit so connected viewers
    // mirror the whole scene live. See SyncClient.
    private bool  _liveShare;
    private float _autoSaveAt = -1f;                 // unscaled time to fire the debounced publish (-1 = idle)
    private const float AutoSaveDebounce = 1.0f;     // coalesce a burst of edits into one save

    // Publish the current shared state: every persisted loaded env plus which one is active.
    // Never-saved envs can't be shared (no server id) and are skipped until their first Save.
    // Publishes a cleared pointer when nothing shareable is loaded, so viewers empty out too.
    private void PublishLive(Action<LibraryClient.ActivePointer> onSuccess = null)
    {
        if (!_liveShare) return;
        var loadedIds = new List<string>();
        foreach (var le in _loaded)
            if (le.persisted) loadedIds.Add(le.env.id);
        string activeId = _active != null && _active.persisted ? _active.env.id : null;
        libraryClient.SetActive(activeId, loadedIds, onSuccess);
    }

    public void AddBuildingDef(BuildingDef b)
    {
        if (b == null) return;
        if (_active != null) _active.buildings[b.id] = b;
        else                 _pendingBuildings[b.id] = b;   // folded in when a working env is created
    }

    // Resolve a building def by id across the active env's dict and the pre-env pending cache (used
    // when a building is edited standalone from the Buildings tab before any environment is active).
    public BuildingDef GetBuildingDef(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_active != null && _active.buildings.TryGetValue(id, out var b)) return b;
        return _pendingBuildings.TryGetValue(id, out var pb) ? pb : null;
    }

    // Swap the active environment's def for a restored copy (undo/redo). Keeps the same LoadedEnv
    // slot — building defs, persisted flag — so only the layout data changes. The caller re-renders.
    public void ReplaceActiveEnvironment(EnvironmentDef env, bool? sitesChanged = null)
    {
        if (_active == null || env == null) return;
        // An undo/redo that touches sites must reach the server like any direct site edit. The
        // undo path (EditController.RestoreEnvironment) already diffs the lists alongside its own
        // site compare and passes the result in; the serialize-both-sides compare only runs for
        // callers that didn't.
        bool changed = sitesChanged ?? (Newtonsoft.Json.JsonConvert.SerializeObject(_active.env.sites)
                                     != Newtonsoft.Json.JsonConvert.SerializeObject(env.sites));
        _active.env   = env;
        _active.dirty = true;
        if (changed) MarkDirty(autoSave: true);
        // Undo/redo can add, remove, reshape or relink sites — reconcile the fills to the
        // restored data (the caller's re-render then paints the composite ground).
        ResyncSiteFills();
    }

    // Load a server environment (by id) into the scene as a loaded+active env. Used by the
    // "Test Server Sample" button so the sample appears in the Loaded list like any other env.
    public void LoadEnvironmentById(string id) => LoadEnvironment(id);

    // Adopt an already-POSTed generated environment (env + its cached building defs) so it loads
    // exactly like a library environment: tracked in the Loaded list, active, editable and saveable.
    public void AdoptGeneratedEnvironment(EnvironmentDef env, IReadOnlyDictionary<string, BuildingDef> defs)
    {
        if (env == null) return;
        var buildings = defs != null ? new Dictionary<string, BuildingDef>(defs) : null;
        InstallEnv(env, buildings);
        _envStatus = $"Generated: {env.name}";
        RefreshEnvironments();
        RefreshBuildings();
    }

    // Adopt an in-memory environment that has never been on the server (e.g. the bundled local
    // sample). Loads exactly like any other env — tracked, active, editable — but is marked unsaved
    // so the first Save POSTs it (preserving its client id) rather than PUTting to a missing id.
    public void AdoptLocalEnvironment(EnvironmentDef env, IReadOnlyDictionary<string, BuildingDef> defs)
    {
        if (env == null) return;
        var buildings = defs != null ? new Dictionary<string, BuildingDef>(defs) : null;
        InstallEnv(env, buildings, persisted: false, dirty: true);
        _envStatus = $"Local sample: {env.name} (unsaved — press Save)";
    }

    // Returns the active environment, auto-creating a blank in-memory one if none is active so the
    // user can place buildings/objects straight away without first creating an environment. The
    // working env is unsaved (POSTed on first Save); Save As / Duplicate also persist it.
    public EnvironmentDef EnsureWorkingEnvironment()
    {
        // A locked (digital twin) active env is never the working env — fall through and create
        // a fresh one so e.g. a standalone tile-edit exit can't inject an instance into the twin.
        if (_active != null && !_active.env.locked) return _active.env;

        var env = BlankEnvironment("Untitled");
        var le  = new LoadedEnv { env = env, persisted = false, dirty = true };
        // Fold in any building defs cached before this env existed (e.g. one just fetched for placement).
        foreach (var kv in _pendingBuildings) le.buildings[kv.Key] = kv.Value;
        _pendingBuildings.Clear();
        _loaded.Add(le);
        worldRenderer?.RenderEnvironment(env, le.buildings, makeActive: true);   // renders + makes active
        SetActive(le);
        _envStatus = "New working environment (unsaved — press Save)";
        return env;
    }

    // Makes an already-rendered loaded environment the editable/saveable one: enables its
    // colliders + paints terrain (locking/dimming the rest) and clears any stale selection.

    // -----------------------------------------------------------------------
    // Site fills — generated child environments rendered inside a host's drawn sites
    // -----------------------------------------------------------------------

    private static string BoundaryKey(float[][] b) => Newtonsoft.Json.JsonConvert.SerializeObject(b);

    // Label for the Sites panel: the child env's name filling `siteId`, or null when unloaded.
    public string GetSiteFillName(string siteId)
    {
        var f = _fills.Find(x => x.siteId == siteId);
        return f?.projected?.name;
    }

    // Links a generated child env into a site and re-syncs. Overwriting an occupied site just
    // replaces the reference; the previous child record stays in the library. Caller records undo.
    public void AssignSiteFill(EnvironmentDef host, string siteId, string childEnvId)
    {
        var plot = host?.sites?.Find(s => s != null && s.id == siteId);
        if (plot == null) return;
        plot.fillEnvironmentId = childEnvId;
        MarkDirty(autoSave: true);
        ResyncSiteFills();
    }

    // Republishes the ground-paint overlays for one host from its live fills.
    private void PublishSiteOverlays(EnvironmentDef host)
    {
        if (host == null || worldRenderer == null) return;
        var overlays = new List<WorldRenderer.SiteOverlay>();
        foreach (var f in _fills)
        {
            if (f.hostEnvId != host.id || f.projected?.site == null) continue;
            var s = host.sites?.Find(x => x != null && x.id == f.siteId);
            if (s != null) overlays.Add(new WorldRenderer.SiteOverlay { site = f.projected.site, clip = s.boundary });
        }
        worldRenderer.SetSiteOverlays(host.id, overlays);
    }

    // Unlinks a site's fill (the child record survives in the library) and re-syncs.
    public void RemoveSiteFill(EnvironmentDef host, string siteId)
    {
        var plot = host?.sites?.Find(s => s != null && s.id == siteId);
        if (plot != null) plot.fillEnvironmentId = null;
        MarkDirty(autoSave: true);
        ResyncSiteFills();
    }

    // Re-reconciles the active host's fills (boundary edits, undo restores, assignment changes).
    public void ResyncSiteFills()
    {
        if (_active != null) StartCoroutine(SyncSiteFills(_active));
    }

    // One reconciler for everything fill-related: diffs the host's sites against the live fills,
    // unloads stale ones, loads + projects + renders new ones, then republishes the paint overlays
    // and repaints the composite splat via SetActiveEnvironment.
    private IEnumerator SyncSiteFills(LoadedEnv host)
    {
        if (host?.env == null || worldRenderer == null || libraryClient == null) yield break;
        string hostId = host.env.id;

        // Wanted: every site on this host with a fill reference and a usable boundary.
        var wanted = new List<SitePlotDef>();
        if (host.env.sites != null)
            foreach (var s in host.env.sites)
                if (s != null && !string.IsNullOrEmpty(s.fillEnvironmentId) &&
                    s.boundary != null && s.boundary.Length >= 3)
                    wanted.Add(s);

        // Fast path: nothing wanted and nothing loaded for this host means there is nothing to
        // unload, load, or composite, so skip the tail repaint entirely. (This coroutine runs
        // after every undo/redo via ResyncSiteFills, and its unconditional tail used to repaint
        // the full splat a second time on top of the re-render's own paint.)
        if (wanted.Count == 0 && !_fills.Exists(f => f.hostEnvId == hostId)) yield break;

        bool fillsChanged = false;

        // Drop fills that no longer match (site gone, child swapped, boundary reshaped).
        for (int i = _fills.Count - 1; i >= 0; i--)
        {
            var f = _fills[i];
            if (f.hostEnvId != hostId) continue;
            var s = wanted.Find(x => x.id == f.siteId);
            if (s != null && s.fillEnvironmentId == f.childEnvId && BoundaryKey(s.boundary) == f.boundaryKey) continue;
            worldRenderer.UnloadEnvironment(f.renderId);
            _fills.RemoveAt(i);
            fillsChanged = true;
        }

        // Load + project + render what's missing.
        foreach (var s in wanted)
        {
            if (_fills.Exists(f => f.hostEnvId == hostId && f.siteId == s.id)) continue;

            EnvironmentDef child = null; bool done = false;
            libraryClient.GetEnvironment(s.fillEnvironmentId,
                env => { child = env; done = true; },
                err => { Debug.LogWarning($"[LibraryBrowser] site fill '{s.name}': load failed: {err}"); done = true; });
            while (!done) yield return null;
            if (child == null) continue;

            var fill = new SiteFill
            {
                hostEnvId   = hostId,
                siteId      = s.id,
                childEnvId  = s.fillEnvironmentId,
                renderId    = s.fillEnvironmentId + "@" + s.id,
                boundaryKey = BoundaryKey(s.boundary),
            };

            var ids = new List<string>();
            if (child.buildingInstances != null)
                foreach (var bi in child.buildingInstances) ids.Add(bi.buildingId);
            yield return BuildingFetch.FetchInto(libraryClient, ids, fill.buildings);

            // Deep copy, then project into the site bbox; the stored record stays pristine.
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(child);
            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<EnvironmentDef>(json);
            copy.id = fill.renderId;   // synthetic render key: one child can fill several sites
            if (!SiteFit.ProjectIntoSite(copy, s.boundary))
            {
                Debug.LogWarning($"[LibraryBrowser] site fill '{s.name}': degenerate fit, skipped.");
                continue;
            }
            fill.projected = copy;
            _fills.Add(fill);
            fillsChanged = true;
            // Fills never take the terrain; their own lot frame would double-draw the site frame.
            worldRenderer.RenderEnvironment(copy, fill.buildings, makeActive: false, suppressLotFrame: true);
        }

        // Only recomposite when a fill actually changed: the overlay set and splat are already
        // current otherwise (every undo/redo with unchanged sites lands here).
        if (!fillsChanged) yield break;

        // Publish the ground-paint overlays for this host and repaint the composite splat.
        // Goes through PublishSiteOverlays so local (server-free) fills are not wiped out here.
        PublishSiteOverlays(host.env);
        if (_active?.env != null) worldRenderer.SetActiveEnvironment(_active.env.id);
    }

    // Unloads every fill belonging to `hostId` (host being closed).
    private void UnloadSiteFills(string hostId)
    {
        for (int i = _fills.Count - 1; i >= 0; i--)
        {
            if (_fills[i].hostEnvId != hostId) continue;
            worldRenderer?.UnloadEnvironment(_fills[i].renderId);
            _fills.RemoveAt(i);
        }
        worldRenderer?.SetSiteOverlays(hostId, null);
    }

    private void SetActive(LoadedEnv le)
    {
        var prev    = _active;
        _active     = le;
        _showSaveAs = false;
        // A pending debounced save must not fire against a different env than the one that armed it.
        _autoSaveAt = -1f;
        if (le != null) worldRenderer?.SetActiveEnvironment(le.env.id);
        // Live Share: when the editable env changes, republish the loaded set so viewers follow.
        PublishLive();
        // Only clear edit-mode selection on a genuine switch between two existing environments.
        // Establishing the first active env (e.g. auto-created mid-placement) must not interrupt.
        if (prev != null && prev != le) editController?.OnActiveEnvironmentSwitched();
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    private void Start()
    {
        if (libraryClient  == null) libraryClient  = FindFirstObjectByType<LibraryClient>();
        if (worldRenderer  == null) worldRenderer  = FindFirstObjectByType<WorldRenderer>();
        if (editController == null) editController = FindFirstObjectByType<EditController>();
        RefreshEnvironments();
    }

    private void Update()
    {
        // Fire the debounced auto-save once edits settle (armed by site changes or Live Share),
        // so a PUT bumps the env version and viewers polling /api/active re-render the latest.
        if (_autoSaveAt >= 0f && Time.unscaledTime >= _autoSaveAt
            && !_envBusy && _active != null && _active.dirty)
        {
            _autoSaveAt = -1f;
            SaveEnvironment();
        }
    }

    // Toggle host publishing. On enable, publishes the loaded set immediately (saving the active
    // env first if it has never been on the server). On disable, stops auto-saving; the last
    // published state remains the server's shared pointer for connected viewers.
    private void SetLiveShare(bool on)
    {
        _liveShare = on;
        _autoSaveAt = -1f;
        if (!on) return;
        if (_active == null) { _envStatus = "Live Share: load or create a scene first."; return; }
        if (_active.persisted) PublishLive(_ => _envStatus = "Live Share on.");
        else                   SaveEnvironment();   // POST first; success handler publishes the pointer
    }

    // -----------------------------------------------------------------------
    // Public refresh
    // -----------------------------------------------------------------------

    public void RefreshEnvironments()
    {
        _envStatus = "Refreshing...";
        libraryClient.GetEnvironments(
            list  => { _envList = list; SortEnvList(); _envStatus = $"{list.Count} environment(s)"; },
            err   => _envStatus = $"Error: {err}");
    }

    public void RefreshBuildings()
    {
        _bldgStatus = "Refreshing...";
        libraryClient.GetBuildings(
            list  => { _bldgList = list; SortBldgList(); _bldgStatus = $"{list.Count} building(s)"; },
            err   => _bldgStatus = $"Error: {err}");
    }

    // Favorites first, then alphabetical by name. Stable enough to keep the list tidy.
    private void SortEnvList() => _envList?.Sort((a, b) =>
        a.favorite != b.favorite ? (b.favorite ? 1 : -1)
                                 : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

    private void SortBldgList() => _bldgList?.Sort((a, b) =>
        a.favorite != b.favorite ? (b.favorite ? 1 : -1)
                                 : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

    // -----------------------------------------------------------------------
    // GUI root
    // -----------------------------------------------------------------------

    private void OnGUI()
    {
        if (WalkthroughController.IsEngaged) return;   // hidden during the first-person walkthrough

        var rect = new Rect(UITheme.Margin, UITheme.RailTop, panelWidth, Screen.height - UITheme.RailTop - UITheme.Margin);
        UITheme.PanelBackground(rect);
        GUILayout.BeginArea(UITheme.Inset(rect));

        // Header row: title + Admin toggle + New (spec panel 2).
        GUILayout.BeginHorizontal();
        UITheme.Title("Library");
        GUILayout.FlexibleSpace();
        bool live = UITheme.ToggleButton(_liveShare, "Live share", UITips.LiveShare, GUILayout.Height(UITheme.RowH));
        if (live != _liveShare) SetLiveShare(live);
        _adminEnabled = UITheme.ToggleButton(_adminEnabled, "Admin", UITips.Admin, GUILayout.Height(UITheme.RowH));
        bool low = UITheme.ToggleButton(_lowDetail, "Low detail", UITips.LowDetail, GUILayout.Height(UITheme.RowH));
        if (low != _lowDetail) SetLowDetail(low);
        if (UITheme.GhostButton("New", UITips.NewPlace, GUILayout.Height(UITheme.RowH))) { _showNewEnv = !_showNewEnv; _newEnvName = ""; }
        GUILayout.EndHorizontal();

        // Places / Buildings as a segmented control.
        int tabSel = UITheme.Segmented((int)_tab, new[] { "Places", "Buildings" }, UITips.LibraryTabs);
        if (tabSel != (int)_tab)
        {
            _tab = (Tab)tabSel;
            if (_tab == Tab.Buildings) RefreshBuildings();
        }

        switch (_tab)
        {
            case Tab.Environments: DrawEnvironmentsTab(); break;
            case Tab.Buildings:    DrawBuildingsTab();    break;
        }

        UITheme.CaptureTooltip();
        GUILayout.EndArea();
    }

    // Case-insensitive contains filter used by the list searches.
    private static bool Matches(string name, string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        (name ?? "").IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

    // -----------------------------------------------------------------------
    // Environments tab
    // -----------------------------------------------------------------------

    private void DrawEnvironmentsTab()
    {
        // Inline new-scene name field (revealed by the header "New").
        if (_showNewEnv)
        {
            GUILayout.BeginHorizontal();
            _newEnvName = GUILayout.TextField(_newEnvName, GUILayout.ExpandWidth(true));
            GUI.enabled = !_envBusy && !string.IsNullOrWhiteSpace(_newEnvName);
            if (UITheme.PrimaryButton("Create", UITips.CreatePlace, GUILayout.Width(72), GUILayout.Height(UITheme.RowH))) { CreateEnvironment(); _showNewEnv = false; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // Loaded set first (most relevant), then the searchable library of all places.
        if (_loaded.Count > 0) DrawLoadedListPanel();

        UITheme.Header("All places");
        GUILayout.BeginHorizontal();
        _envSearch = GUILayout.TextField(_envSearch, GUILayout.ExpandWidth(true));
        GUI.enabled = !_envBusy;
        if (UITheme.Button("Refresh", UITips.RefreshPlaces, GUILayout.Width(66))) RefreshEnvironments();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        _envListScroll = GUILayout.BeginScrollView(_envListScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(_active != null ? 150 : 320));
        foreach (var s in _envList)
        {
            if (!Matches(s.name ?? s.id, _envSearch)) continue;
            bool isLoaded = _loaded.Find(l => l.env.id == s.id) != null;
            GUILayout.BeginHorizontal();
            GUILayout.Label((isLoaded ? "• " : "") + (s.locked ? "🔒 " : "") + (s.name ?? s.id), GUILayout.ExpandWidth(true));
            GUI.enabled = !_envBusy;
            if (UITheme.Button(isLoaded ? "Focus" : "Load", isLoaded ? UITips.FocusPlace : UITips.LoadPlace, GUILayout.Width(56), GUILayout.Height(UITheme.RowH))) LoadEnvironment(s.id);
            if (UITheme.Button(s.favorite ? "★" : "☆", UITips.Favorite, GUILayout.Width(30), GUILayout.Height(UITheme.RowH)))
            {
                var row = s; bool prev = row.favorite;
                row.favorite = !prev;   // optimistic flip for instant feedback
                libraryClient.ToggleFavoriteEnvironment(row.id,
                    nowFav => { row.favorite = nowFav; SortEnvList(); },
                    err    => { row.favorite = prev; _envStatus = $"Favorite error: {err}"; });
            }
            if (_adminEnabled)
            {
                GUI.enabled = !_envBusy && !s.locked;   // locked twin: unlock before archiving
                if (UITheme.Button("Archive", UITips.ArchivePlace, GUILayout.Width(62))) StartCoroutine(CoArchiveEnv(s.id));
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        if (_active != null) DrawActiveEnvPanel();
        UITheme.Note(_envStatus);
    }

    // The set of currently-loaded environments. The active one is editable; the rest are locked
    // backdrops. Each row shows its state pill (Active · editing / Backdrop · locked) per the spec.
    private void DrawLoadedListPanel()
    {
        UITheme.Header($"Loaded · {_loaded.Count}");
        LoadedEnv toActivate = null, toClose = null;
        foreach (var le in _loaded)
        {
            bool isActive = le == _active;
            bool locked   = le.env.locked;
            string title = (locked ? "🔒 " : "") + (le.env.name ?? le.env.id) + (le.dirty ? " *" : "");
            string state = isActive
                ? (locked ? "Active · locked (digital twin)"
                          : $"Active · editing · {le.env.objectInstances?.Count ?? 0} objects")
                : (locked ? "Backdrop · locked twin" : "Backdrop · locked");
            // Clicking an inactive row makes it active; the trailing buttons handle Edit/close.
            if (UITheme.StateRow(title, state, isActive, UITips.LoadedRow, muted: !isActive) && !isActive) toActivate = le;

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            // Lock is one click; Unlock asks for an inline confirm (mirrors the delete confirm).
            GUI.enabled = !_envBusy && le.persisted;
            if (!locked)
            {
                if (UITheme.Button("Lock", UITips.Lock, GUILayout.Width(56))) SetLocked(le, true);
            }
            else if (_confirmUnlock != le)
            {
                if (UITheme.Button("Unlock…", UITips.UnlockAsk, GUILayout.Width(64))) _confirmUnlock = le;
            }
            GUI.enabled = !_envBusy && !isActive;
            if (UITheme.Button(locked ? "View" : "Edit", locked ? UITips.ViewPlace : UITips.EditPlace, GUILayout.Width(56))) toActivate = le;
            GUI.enabled = !_envBusy;
            if (UITheme.Button("Close", UITips.ClosePlace, GUILayout.Width(56))) toClose = le;
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_confirmUnlock == le)
            {
                GUILayout.BeginHorizontal();
                UITheme.Note("Unlock the digital twin for editing?");
                GUI.enabled = !_envBusy;
                if (UITheme.DangerButton("Unlock", UITips.UnlockConfirm, GUILayout.Width(64))) { SetLocked(le, false); _confirmUnlock = null; }
                GUI.enabled = true;
                if (UITheme.GhostButton("Cancel", UITips.Cancel, GUILayout.Width(56))) _confirmUnlock = null;
                GUILayout.EndHorizontal();
            }

            if (_adminEnabled) DrawAdminRow(le);
        }

        if (toActivate != null)
        {
            SetActive(toActivate);
            _envStatus = toActivate.env.locked ? $"Viewing (locked): {toActivate.env.name}" : $"Editing: {toActivate.env.name}";
        }
        if (toClose != null) CloseEnvironment(toClose);
    }

    private void DrawActiveEnvPanel()
    {
        var env = _active.env;
        UITheme.Divider();

        if (env.locked)
            UITheme.Note("🔒 Locked (digital twin) — read-only. Save As to make an editable copy.");

        // Save (primary) / Save as (secondary) footer. Re-render / duplicate / archive / delete
        // live on each Loaded row's admin actions (DrawAdminRow, Admin toggle).
        GUILayout.BeginHorizontal();
        GUI.enabled = _active.dirty && !_envBusy && !env.locked;
        if (UITheme.PrimaryButton("Save", UITips.Save, GUILayout.Height(UITheme.RowH), GUILayout.ExpandWidth(true))) SaveEnvironment();
        GUI.enabled = !_envBusy;
        if (UITheme.SecondaryButton("Save as…", UITips.SaveAs, GUILayout.Height(UITheme.RowH), GUILayout.Width(96))) { _showSaveAs = !_showSaveAs; _saveAsName = env.name; }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // Save-As inline dialog
        if (_showSaveAs)
        {
            GUILayout.BeginHorizontal();
            _saveAsName = GUILayout.TextField(_saveAsName, GUILayout.ExpandWidth(true));
            GUI.enabled = !string.IsNullOrWhiteSpace(_saveAsName) && !_envBusy;
            if (UITheme.Button("Save copy", UITips.SaveCopy, GUILayout.Width(86))) { SaveAsEnvironment(_saveAsName); _showSaveAs = false; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        DrawOptionalActions(env);

        // Contents — building + object instance rows: the name selects (see below), the On/Off
        // toggle sets `included`, the Opt toggle sets `optional`. All read-only when locked.
        // Clicking a name is deferred to after the lists are drawn: EditController.SelectInstanceFromLibrary
        // can switch the shell mode, which tears down the current tool and re-renders — not something
        // to do midway through a foreach over these same lists inside an open scroll view.
        string selId = null; bool selIsBuilding = false, selAdditive = false;
        string delId = null; bool delIsBuilding = false;

        GUI.enabled = !env.locked;
        DrawInstanceList($"Buildings ({env.buildingInstances?.Count ?? 0})", env.buildingInstances?.Count > 0,
            ref _bInstScroll, 104, () =>
            {
                foreach (var bi in env.buildingInstances)
                {
                    string label = _active.buildings.TryGetValue(bi.buildingId, out var bd) ? bd.name : bi.buildingId;
                    bool sel = editController != null && editController.IsInstanceSelected(bi.instanceId);
                    DrawIncludeRow(label, bi.included, bi.optional, sel, out bool incCh, out bool nextInc, out bool optCh, out bool nextOpt, out bool hit, out bool del);
                    if (incCh && !env.locked) { editController?.RecordEnvironmentEdit("Toggle included"); bi.included = nextInc; OnInstanceToggled(env); }
                    if (optCh && !env.locked) { editController?.RecordEnvironmentEdit(nextOpt ? "Mark optional" : "Mark required"); bi.optional = nextOpt; OnOptionalToggled(bi.instanceId, nextOpt); }
                    if (hit) { selId = bi.instanceId; selIsBuilding = true; selAdditive = Event.current.shift || Event.current.control; }
                    if (del) { delId = bi.instanceId; delIsBuilding = true; }
                }
            });

        DrawInstanceList($"Objects ({env.objectInstances?.Count ?? 0})", env.objectInstances?.Count > 0,
            ref _oInstScroll, 88, () =>
            {
                foreach (var oi in env.objectInstances)
                {
                    bool sel = editController != null && editController.IsInstanceSelected(oi.instanceId);
                    DrawIncludeRow(oi.prefabType ?? oi.instanceId, oi.included, oi.optional, sel, out bool incCh, out bool nextInc, out bool optCh, out bool nextOpt, out bool hit, out bool del);
                    if (incCh && !env.locked) { editController?.RecordEnvironmentEdit("Toggle included"); oi.included = nextInc; OnInstanceToggled(env); }
                    if (optCh && !env.locked) { editController?.RecordEnvironmentEdit(nextOpt ? "Mark optional" : "Mark required"); oi.optional = nextOpt; OnOptionalToggled(oi.instanceId, nextOpt); }
                    if (hit) { selId = oi.instanceId; selIsBuilding = false; selAdditive = Event.current.shift || Event.current.control; }
                    if (del) { delId = oi.instanceId; delIsBuilding = false; }
                }
            });
        GUI.enabled = true;

        // Deferred like the name click: DeleteInstance mutates the very lists the foreach above
        // iterates inside an open scroll view, so act only after both lists are closed.
        if (delId != null && !env.locked) editController?.DeleteInstance(delId, delIsBuilding);
        else if (selId != null) editController?.SelectInstanceFromLibrary(selId, selIsBuilding, selAdditive);
    }

    // Admin actions for one loaded environment — re-render / duplicate / archive / delete, drawn
    // under its Loaded row when the Admin toggle is on (replaces the old right-rail Manage command).
    // Delete asks first; with no hard-delete endpoint it removes via archive (recoverable).
    private void DrawAdminRow(LoadedEnv le)
    {
        var env = le.env;

        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        GUI.enabled = !_envBusy;
        if (UITheme.SecondaryButton("Re-render", UITips.Rerender, GUILayout.Width(74)))
            worldRenderer?.RenderEnvironment(env, le.buildings);
        GUI.enabled = !_envBusy && le.persisted;
        if (UITheme.SecondaryButton("Duplicate", UITips.Duplicate, GUILayout.Width(74)))
            DuplicateEnvironment(le);
        GUI.enabled = !_envBusy && le.persisted && !env.locked;
        if (UITheme.SecondaryButton("Archive", UITips.ArchivePlace, GUILayout.Width(62)))
            StartCoroutine(CoArchiveEnv(env.id));
        if (UITheme.DangerButton("Delete…", UITips.DeleteAsk, GUILayout.Width(62)))
            _confirmDelete = le;
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (_confirmDelete == le)
        {
            GUILayout.BeginHorizontal();
            UITheme.Note($"Delete “{env.name}”?");
            GUI.enabled = !_envBusy;
            if (UITheme.DangerButton("Delete", UITips.DeleteConfirm, GUILayout.Width(56))) { StartCoroutine(CoArchiveEnv(env.id)); _confirmDelete = null; }
            GUI.enabled = true;
            if (UITheme.GhostButton("Cancel", UITips.Cancel, GUILayout.Width(56))) _confirmDelete = null;
            GUILayout.EndHorizontal();
        }
    }

    // Shared section: header + a scrolled body of include rows (only drawn when non-empty).
    private void DrawInstanceList(string header, bool hasItems, ref Vector2 scroll, float height, Action body)
    {
        if (!hasItems) return;
        UITheme.Header(header);
        scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(height));
        body();
        GUILayout.EndScrollView();
    }

    // A clickable name + quiet On/Off toggle + quiet Opt toggle + × delete row. `includeChanged` /
    // `optionalChanged` report a toggle flip (with the new value); `clicked` reports a click on the
    // *name*, which selects the instance in the scene; `deleteClicked` reports the ×, which removes
    // the instance from the place.
    private static void DrawIncludeRow(string label, bool included, bool optional, bool selected,
                                       out bool includeChanged, out bool nextIncluded,
                                       out bool optionalChanged, out bool nextOptional,
                                       out bool clicked, out bool deleteClicked)
    {
        GUILayout.BeginHorizontal();
        clicked      = UITheme.ListRowLabel(label, selected, UITips.InstanceName, GUILayout.ExpandWidth(true));
        nextIncluded = UITheme.RowToggle(included, included ? "On" : "Off", UITips.IncludeToggle, GUILayout.Width(46), GUILayout.Height(22f));
        nextOptional = UITheme.RowToggle(optional, "Opt", UITips.OptionalToggle, GUILayout.Width(38), GUILayout.Height(22f));
        deleteClicked = UITheme.RowDeleteButton(UITips.DeleteInstance);
        GUILayout.EndHorizontal();
        includeChanged  = nextIncluded != included;
        optionalChanged = nextOptional != optional;
    }

    private void OnInstanceToggled(EnvironmentDef env)
    {
        _active.dirty = true;
        worldRenderer?.RenderEnvironment(env, _active.buildings);
    }

    // An optional flip changes nothing on screen unless Low detail is on, so no re-render: the
    // renderer just re-stamps the GO's marker and applies the preview state.
    private void OnOptionalToggled(string instanceId, bool optional)
    {
        MarkDirty();   // Live share auto-saves it like any other edit
        worldRenderer?.SetInstanceOptional(instanceId, optional);
        editController?.OnOptionalVisibilityChanged();
    }

    // Low detail preview: what the VR viewer shows (everything marked optional hidden). Editing
    // stays on; a hidden selected item just loses its gizmo until the preview is turned off.
    private void SetLowDetail(bool on)
    {
        _lowDetail = on;
        worldRenderer?.SetOptionalHidden(on);
        editController?.OnOptionalVisibilityChanged();
    }

    // "Mark optional" for the scene selection plus "Type optional" for every object sharing the
    // primary selection's prefab type. Labels flip to "required" when the targets are all optional
    // already. Both act through EditController (undo, dirty, marker update).
    private void DrawOptionalActions(EnvironmentDef env)
    {
        int    selN = editController != null ? editController.SelectionCount : 0;
        string type = editController?.PrimarySelectedPrefabType();
        bool allOpt     = selN > 0 && !OptionalContent.AnyDiffers(env, editController.SelectedInstances(), true);
        bool typeAllOpt = type != null && !OptionalContent.AnyDiffersByPrefabType(env, type, true);

        GUILayout.BeginHorizontal();
        GUI.enabled = selN > 0 && !env.locked;
        if (UITheme.SecondaryButton(allOpt ? "Mark required" : "Mark optional",
                                    allOpt ? UITips.MarkRequired : UITips.MarkOptional,
                                    GUILayout.Height(UITheme.RowH), GUILayout.ExpandWidth(true)))
            editController.SetSelectedOptional(!allOpt);
        GUI.enabled = type != null && !env.locked;
        if (UITheme.SecondaryButton(typeAllOpt ? "Type required" : "Type optional",
                                    typeAllOpt ? UITips.TypeRequired : UITips.TypeOptional,
                                    GUILayout.Height(UITheme.RowH), GUILayout.ExpandWidth(true)))
            editController.SetOptionalByPrefabType(type, !typeAllOpt);
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (selN > 0)
            UITheme.Note(type != null ? $"Selected {selN} · type {type}" : $"Selected {selN}");
    }

    // -----------------------------------------------------------------------
    // Buildings tab
    // -----------------------------------------------------------------------

    private void DrawBuildingsTab()
    {
        // New building name + create.
        GUILayout.BeginHorizontal();
        _newBldgName = GUILayout.TextField(_newBldgName, GUILayout.ExpandWidth(true));
        GUI.enabled = !_bldgBusy && !string.IsNullOrWhiteSpace(_newBldgName);
        if (UITheme.PrimaryButton("New", UITips.NewBuilding, GUILayout.Width(64), GUILayout.Height(UITheme.RowH))) CreateBuilding();
        GUI.enabled = !_bldgBusy;
        if (UITheme.Button("Refresh", UITips.RefreshBuildings, GUILayout.Width(66), GUILayout.Height(UITheme.RowH))) RefreshBuildings();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        UITheme.Header("Buildings");
        _bldgListScroll = GUILayout.BeginScrollView(_bldgListScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(Screen.height - 240f));
        foreach (var s in _bldgList)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label((s.favorite ? "★ " : "") + (s.name ?? s.id), GUILayout.ExpandWidth(true));
            GUI.enabled = !_bldgBusy;
            if (UITheme.Button("Edit", UITips.EditBuilding, GUILayout.Width(56))) OpenBuildingForEdit(s.id);
            if (UITheme.Button(s.favorite ? "★" : "☆", UITips.Favorite, GUILayout.Width(30)))
            {
                var row = s; bool prev = row.favorite;
                row.favorite = !prev;   // optimistic flip for instant feedback
                libraryClient.ToggleFavoriteBuilding(row.id,
                    nowFav => { row.favorite = nowFav; SortBldgList(); },
                    err    => { row.favorite = prev; _bldgStatus = $"Favorite error: {err}"; });
            }
            if (_adminEnabled && UITheme.Button("Archive", UITips.ArchiveBuilding, GUILayout.Width(62))) StartCoroutine(CoArchiveBldg(s.id));
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        UITheme.Note("Tip: double-click a placed building to tile-edit it.");
    }

    // -----------------------------------------------------------------------
    // Environment operations
    // -----------------------------------------------------------------------

    private void CreateEnvironment()
    {
        string name = _newEnvName.Trim();
        if (string.IsNullOrEmpty(name)) return;

        _envBusy = true; _envStatus = "Creating...";
        var env = BlankEnvironment(name);
        libraryClient.PostEnvironment(env,
            id   => { env.id = id; _newEnvName = ""; _envBusy = false; _envStatus = $"Created '{name}'."; RefreshEnvironments(); InstallEnv(env); },
            err  => { _envBusy = false; _envStatus = $"Create error: {err}"; },
            kind: "user");
    }

    // Load adds the environment to the loaded set (without unloading the others) and makes it the
    // active/editable one. If it's already loaded, just refocuses it.
    private void LoadEnvironment(string id)
    {
        var existing = _loaded.Find(l => l.env.id == id);
        if (existing != null) { SetActive(existing); _envStatus = $"Editing: {existing.env.name}"; return; }

        _envBusy = true; _envStatus = "Loading..."; _showSaveAs = false;
        libraryClient.GetEnvironment(id,
            env  =>
            {
                if (env == null || string.IsNullOrEmpty(env.id))
                {
                    _envBusy = false; _envStatus = "Load error: server returned an empty environment.";
                    return;
                }
                var le = new LoadedEnv { env = env, persisted = true, dirty = false };
                _loaded.Add(le);
                _envStatus = $"Loaded: {env.name}";
                StartCoroutine(FetchBuildingsAndRender(le));
            },
            err  => { _envBusy = false; _envStatus = $"Load error: {err}"; });
    }

    private IEnumerator FetchBuildingsAndRender(LoadedEnv le)
    {
        var env = le.env;
        var ids = new List<string>();
        if (env.buildingInstances != null)
            foreach (var bi in env.buildingInstances) ids.Add(bi.buildingId);

        yield return BuildingFetch.FetchInto(libraryClient, ids, le.buildings);

        // Render as a locked backdrop first, then promote to active so terrain/colliders update once.
        worldRenderer?.RenderEnvironment(env, le.buildings, makeActive: false);
        SetActive(le);
        _envStatus = $"Rendered: {env.name}"; _envBusy = false;
        yield return SyncSiteFills(le);   // auto-load any generated scenes filling this host's sites
    }

    private void SaveEnvironment()
    {
        if (_active == null) return;
        if (_active.env.locked) { _envStatus = "Locked (digital twin) — unlock to save, or Save As a copy."; return; }
        var le = _active;
        _envBusy = true;

        // An auto-created working env doesn't exist on the server yet — create it (POST), which
        // preserves its client id. Once persisted, subsequent saves overwrite it (PUT).
        if (!le.persisted)
        {
            libraryClient.PostEnvironment(le.env,
                id  => { le.env.id = id; le.persisted = true; le.dirty = false; _envStatus = "Saved."; _envBusy = false; RefreshEnvironments();
                         PublishLive(); },   // publish now that it has a server id
                err => { _envStatus = $"Save error: {err}"; _envBusy = false; },
                kind: "user");
            return;
        }

        libraryClient.PutEnvironment(le.env,
            ()  => { le.dirty = false; _envStatus = "Saved."; _envBusy = false; },
            err => { _envStatus = $"Save error: {err}"; _envBusy = false; });
    }

    private void SaveAsEnvironment(string newName) => SaveAsEnvironment(_active, newName);

    private void SaveAsEnvironment(LoadedEnv le, string newName)
    {
        if (le == null) return;
        _envBusy = true; _envStatus = "Saving as...";
        // Deep-copy via Newtonsoft; snapshot the building defs so the copy renders immediately.
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(le.env);
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<EnvironmentDef>(json);
        copy.id = Guid.NewGuid().ToString("D"); copy.name = newName; copy.version = 1;
        copy.locked = false;   // a copy of a locked twin is the sanctioned editable working copy
        var buildings = new Dictionary<string, BuildingDef>(le.buildings);
        libraryClient.PostEnvironment(copy,
            id  => { copy.id = id; _envBusy = false; _envStatus = $"Saved as '{newName}'."; RefreshEnvironments(); InstallEnv(copy, buildings); },
            err => { _envBusy = false; _envStatus = $"Save As error: {err}"; },
            kind: "user", dedupe: false);
    }

    private void DuplicateEnvironment(LoadedEnv le)
    {
        if (le == null) return;
        SaveAsEnvironment(le, le.env.name + " (copy)");
    }

    // Lock/unlock a loaded environment as a read-only "digital twin". Persists immediately (PUT)
    // so the flag survives reloads and reaches other clients; reverts the flag if the PUT fails.
    // Locking the active env aborts any in-progress tool and clears undo history so Ctrl+Z can't
    // resurrect a locked=false snapshot.
    private void SetLocked(LoadedEnv le, bool locked)
    {
        if (le == null || le.env.locked == locked) return;
        if (!le.persisted) { _envStatus = "Save the environment before locking it."; return; }

        le.env.locked = locked;
        if (locked && le == _active)
        {
            editController?.OnActiveEnvironmentSwitched();   // abort tools, deselect, clear history
            le.dirty = false; _autoSaveAt = -1f;             // cancel any pending auto-save
        }
        _envBusy = true; _envStatus = locked ? "Locking..." : "Unlocking...";
        libraryClient.PutEnvironment(le.env,
            ()  => { _envBusy = false; _envStatus = locked ? $"Locked: {le.env.name} (digital twin)" : $"Unlocked: {le.env.name}"; RefreshEnvironments(); },
            err => { le.env.locked = !locked; _envBusy = false; _envStatus = $"Lock error: {err}"; });
    }

    // Removes a loaded environment from the scene (does not delete it on the server). If it was
    // the active one, focus falls to another loaded environment, or none.
    private void CloseEnvironment(LoadedEnv le)
    {
        if (le == null) return;
        UnloadSiteFills(le.env.id);
        worldRenderer?.UnloadEnvironment(le.env.id);
        _loaded.Remove(le);
        bool republished = false;
        if (_active == le)
        {
            _active = null;
            if (_loaded.Count > 0) { SetActive(_loaded[0]); republished = true; }   // SetActive publishes
            else                   editController?.OnActiveEnvironmentSwitched();
        }
        // Closing a backdrop (or the last env) doesn't go through SetActive — republish so
        // viewers unload it too (an empty set clears the shared pointer).
        if (!republished) PublishLive();
        _envStatus = $"Closed: {le.env.name}";
    }

    private IEnumerator CoArchiveEnv(string id)
    {
        // Belt: a locked twin can't be archived from any path (the buttons are disabled too).
        var target = _loaded.Find(l => l.env.id == id);
        if (target?.env.locked == true || _envList?.Find(s => s.id == id)?.locked == true)
        {
            _envStatus = "Locked (digital twin) — unlock before archiving.";
            yield break;
        }
        _envBusy = true; _envStatus = "Archiving...";
        bool done = false;
        libraryClient.ArchiveEnvironment(id,
            ()  => done = true,
            err => { Debug.LogError($"[LibraryBrowser] archive env '{id}': {err}"); done = true; });
        while (!done) yield return null;
        _envBusy = false; _envStatus = "Archived.";
        var le = _loaded.Find(l => l.env.id == id);
        if (le != null) CloseEnvironment(le);
        RefreshEnvironments();
    }

    // -----------------------------------------------------------------------
    // Building operations
    // -----------------------------------------------------------------------

    private void CreateBuilding()
    {
        string name = _newBldgName.Trim();
        if (string.IsNullOrEmpty(name)) return;

        _bldgBusy = true; _bldgStatus = "Creating...";
        var b = BlankBuilding(name);
        libraryClient.PostBuilding(b,
            id  => { b.id = id; _newBldgName = ""; _bldgBusy = false; _bldgStatus = $"Created '{name}'."; RefreshBuildings(); AddBuildingDef(b); editController?.EditBuildingFromLibrary(b, isNew: true); },
            err => { _bldgBusy = false; _bldgStatus = $"Create error: {err}"; },
            kind: "static");
    }

    private void OpenBuildingForEdit(string id)
    {
        _bldgBusy = true; _bldgStatus = $"Loading {id}...";
        libraryClient.GetBuilding(id,
            b   => { _bldgBusy = false; _bldgStatus = $"Editing: {b.name}"; AddBuildingDef(b); editController?.EditBuildingFromLibrary(b); },
            err => { _bldgBusy = false; _bldgStatus = $"Load error: {err}"; });
    }

    private IEnumerator CoArchiveBldg(string id)
    {
        _bldgBusy = true; _bldgStatus = "Archiving...";
        bool done = false;
        libraryClient.ArchiveBuilding(id,
            ()  => done = true,
            err => { Debug.LogError($"[LibraryBrowser] archive building '{id}': {err}"); done = true; });
        while (!done) yield return null;
        _bldgBusy = false; _bldgStatus = "Archived.";
        RefreshBuildings();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Adds an environment to the loaded set and makes it active. Defaults to persisted + clean
    // (the POST-then-install path used by create / save-as); pass persisted:false, dirty:true for
    // an in-memory env that still needs its first Save (POST), e.g. the local sample.
    private void InstallEnv(EnvironmentDef env, Dictionary<string, BuildingDef> buildings = null,
                            bool persisted = true, bool dirty = false)
    {
        var le = new LoadedEnv { env = env, persisted = persisted, dirty = dirty };
        if (buildings != null) foreach (var kv in buildings) le.buildings[kv.Key] = kv.Value;
        _loaded.Add(le);
        worldRenderer?.RenderEnvironment(env, le.buildings, makeActive: false);
        SetActive(le);
        StartCoroutine(SyncSiteFills(le));   // e.g. a Save As copy carries its sites along
    }

    private static EnvironmentDef BlankEnvironment(string name) => new EnvironmentDef
    {
        id                = Guid.NewGuid().ToString("D"),
        name              = name,
        version           = 1,
        tags              = new List<string>(),
        site              = new SiteDef
        {
            terrainSize    = new float[] { 100f, 100f },
            terrainZones   = new List<TerrainZoneDef>(),
            paths          = new List<PathDef>(),
            surfaceStrokes = new List<SurfaceStrokeDef>(),
            heightStrokes  = new List<HeightStrokeDef>(),
            scaleNote      = ""
        },
        buildingInstances = new List<BuildingInstance>(),
        objectInstances   = new List<ObjectInstance>(),
    };

    // A new building starts as a 3×3 ground-floor block centered on the origin cell, so it is
    // visible the moment the editor opens and the user can see exactly where it sits. (A def with
    // zero tiles used to fall through to the legacy bay-massing fallback in WorldRenderer, which
    // drew an unrelated old prefab and hid where the building actually was.)
    private const int NEW_BUILDING_SEED_HALF_EXTENT = 1;   // cells each side of the origin cell → 3×3

    private static BuildingDef BlankBuilding(string name) => new BuildingDef
    {
        id              = Guid.NewGuid().ToString("D"),
        name            = name,
        version         = 1,
        tags            = new List<string>(),
        gridCellSize    = AuthoringConventions.DEFAULT_GRID_CELL_SIZE,
        floors          = 1,
        floorHeight     = AuthoringConventions.DEFAULT_FLOOR_HEIGHT,
        tiles           = SeedFootprint(NEW_BUILDING_SEED_HALF_EXTENT),
        embeddedObjects = new List<EmbeddedObjectDef>(),
    };

    // Square floor-0 footprint of plain "square" tiles spanning [-half..half] on both axes. Cells are
    // corner-pivot (cell (0,0) has its corner at the building origin), so this is the closest
    // integer-cell footprint to "centered on the origin"; negative cells are fully supported by the
    // editor, renderer, and placement ghost. Same per-tile defaults as LayoutConverter's seed loop.
    private static List<TileDef> SeedFootprint(int half)
    {
        var tiles = new List<TileDef>();
        for (int x = -half; x <= half; x++)
            for (int z = -half; z <= half; z++)
                tiles.Add(new TileDef { gridX = x, gridZ = z, floor = 0, shapeId = "square", rotation = 0, faceMaterials = null });
        return tiles;
    }
}
