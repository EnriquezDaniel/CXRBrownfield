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
    public void MarkDirty()
    {
        if (_active == null || _active.env.locked) return;   // locked twin: no dirty, no auto-save
        _active.dirty = true;
        // Live Share: schedule a debounced auto-save so viewers (VR / 2nd PC) pick up the edit.
        if (_liveShare) _autoSaveAt = Time.unscaledTime + AutoSaveDebounce;
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
    public void ReplaceActiveEnvironment(EnvironmentDef env)
    {
        if (_active == null || env == null) return;
        _active.env   = env;
        _active.dirty = true;
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
    private void SetActive(LoadedEnv le)
    {
        var prev    = _active;
        _active     = le;
        _showSaveAs = false;
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
        // Live Share: fire the debounced auto-save once edits settle, so a PUT bumps the env
        // version and viewers polling /api/active re-render the latest.
        if (_liveShare && _autoSaveAt >= 0f && Time.unscaledTime >= _autoSaveAt
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
        var rect = new Rect(UITheme.Margin, UITheme.RailTop, panelWidth, Screen.height - UITheme.RailTop - UITheme.Margin);
        UITheme.PanelBackground(rect);
        GUILayout.BeginArea(UITheme.Inset(rect));

        // Header row: title + Admin toggle + New (spec panel 2).
        GUILayout.BeginHorizontal();
        UITheme.Title("Library");
        GUILayout.FlexibleSpace();
        bool live = GUILayout.Toggle(_liveShare, "Live", GUI.skin.button, GUILayout.Height(UITheme.RowH));
        if (live != _liveShare) SetLiveShare(live);
        _adminEnabled = GUILayout.Toggle(_adminEnabled, "Admin", GUI.skin.button, GUILayout.Height(UITheme.RowH));
        if (UITheme.GhostButton("New", GUILayout.Height(UITheme.RowH))) { _showNewEnv = !_showNewEnv; _newEnvName = ""; }
        GUILayout.EndHorizontal();

        // Places / Buildings as a segmented control.
        int tabSel = UITheme.Segmented((int)_tab, new[] { "Places", "Buildings" });
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
            if (UITheme.PrimaryButton("Create", GUILayout.Width(72), GUILayout.Height(UITheme.RowH))) { CreateEnvironment(); _showNewEnv = false; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // Loaded set first (most relevant), then the searchable library of all places.
        if (_loaded.Count > 0) DrawLoadedListPanel();

        UITheme.Header("All places");
        GUILayout.BeginHorizontal();
        _envSearch = GUILayout.TextField(_envSearch, GUILayout.ExpandWidth(true));
        GUI.enabled = !_envBusy;
        if (GUILayout.Button("↻", GUILayout.Width(30))) RefreshEnvironments();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        _envListScroll = GUILayout.BeginScrollView(_envListScroll, GUILayout.Height(_active != null ? 150 : 320));
        foreach (var s in _envList)
        {
            if (!Matches(s.name ?? s.id, _envSearch)) continue;
            bool isLoaded = _loaded.Find(l => l.env.id == s.id) != null;
            GUILayout.BeginHorizontal();
            GUILayout.Label((isLoaded ? "• " : "") + (s.locked ? "🔒 " : "") + (s.name ?? s.id), GUILayout.ExpandWidth(true));
            GUI.enabled = !_envBusy;
            if (GUILayout.Button(isLoaded ? "Focus" : "Load", GUILayout.Width(56), GUILayout.Height(UITheme.RowH))) LoadEnvironment(s.id);
            if (GUILayout.Button(s.favorite ? "★" : "☆", GUILayout.Width(30), GUILayout.Height(UITheme.RowH)))
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
                if (GUILayout.Button("⌫", GUILayout.Width(30))) StartCoroutine(CoArchiveEnv(s.id));
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
        UITheme.Note("overlaid at one origin");
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
            if (UITheme.StateRow(title, state, isActive, muted: !isActive) && !isActive) toActivate = le;

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            // Lock is one click; Unlock asks for an inline confirm (mirrors the delete confirm).
            GUI.enabled = !_envBusy && le.persisted;
            if (!locked)
            {
                if (GUILayout.Button("Lock", GUILayout.Width(56))) SetLocked(le, true);
            }
            else if (_confirmUnlock != le)
            {
                if (GUILayout.Button("Unlock…", GUILayout.Width(64))) _confirmUnlock = le;
            }
            GUI.enabled = !_envBusy && !isActive;
            if (GUILayout.Button(locked ? "View" : "Edit", GUILayout.Width(56))) toActivate = le;
            GUI.enabled = !_envBusy;
            if (GUILayout.Button("Close", GUILayout.Width(56))) toClose = le;
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_confirmUnlock == le)
            {
                GUILayout.BeginHorizontal();
                UITheme.Note("Unlock the digital twin for editing?");
                GUI.enabled = !_envBusy;
                if (UITheme.DangerButton("Unlock", GUILayout.Width(64))) { SetLocked(le, false); _confirmUnlock = null; }
                GUI.enabled = true;
                if (UITheme.GhostButton("Cancel", GUILayout.Width(56))) _confirmUnlock = null;
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
        if (UITheme.PrimaryButton("Save", GUILayout.Height(UITheme.RowH), GUILayout.ExpandWidth(true))) SaveEnvironment();
        GUI.enabled = !_envBusy;
        if (UITheme.SecondaryButton("Save as…", GUILayout.Height(UITheme.RowH), GUILayout.Width(96))) { _showSaveAs = !_showSaveAs; _saveAsName = env.name; }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // Save-As inline dialog
        if (_showSaveAs)
        {
            GUILayout.BeginHorizontal();
            _saveAsName = GUILayout.TextField(_saveAsName, GUILayout.ExpandWidth(true));
            GUI.enabled = !string.IsNullOrWhiteSpace(_saveAsName) && !_envBusy;
            if (GUILayout.Button("OK", GUILayout.Width(40))) { SaveAsEnvironment(_saveAsName); _showSaveAs = false; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // Contents — building + object instance include toggles (read-only when locked).
        GUI.enabled = !env.locked;
        DrawInstanceList($"Buildings ({env.buildingInstances?.Count ?? 0})", env.buildingInstances?.Count > 0,
            ref _bInstScroll, 104, () =>
            {
                foreach (var bi in env.buildingInstances)
                {
                    string label = _active.buildings.TryGetValue(bi.buildingId, out var bd) ? bd.name : bi.buildingId;
                    if (DrawIncludeRow(label, bi.included, out bool next) && !env.locked) { editController?.RecordEnvironmentEdit("Toggle included"); bi.included = next; OnInstanceToggled(env); }
                }
            });

        DrawInstanceList($"Objects ({env.objectInstances?.Count ?? 0})", env.objectInstances?.Count > 0,
            ref _oInstScroll, 88, () =>
            {
                foreach (var oi in env.objectInstances)
                    if (DrawIncludeRow(oi.prefabType ?? oi.instanceId, oi.included, out bool next) && !env.locked) { editController?.RecordEnvironmentEdit("Toggle included"); oi.included = next; OnInstanceToggled(env); }
            });
        GUI.enabled = true;
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
        if (UITheme.SecondaryButton("Re-render", GUILayout.Width(74)))
            worldRenderer?.RenderEnvironment(env, le.buildings);
        GUI.enabled = !_envBusy && le.persisted;
        if (UITheme.SecondaryButton("Duplicate", GUILayout.Width(74)))
            DuplicateEnvironment(le);
        GUI.enabled = !_envBusy && le.persisted && !env.locked;
        if (UITheme.SecondaryButton("Archive", GUILayout.Width(62)))
            StartCoroutine(CoArchiveEnv(env.id));
        if (UITheme.DangerButton("Delete…", GUILayout.Width(62)))
            _confirmDelete = le;
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (_confirmDelete == le)
        {
            GUILayout.BeginHorizontal();
            UITheme.Note($"Delete “{env.name}”?");
            GUI.enabled = !_envBusy;
            if (UITheme.DangerButton("Delete", GUILayout.Width(56))) { StartCoroutine(CoArchiveEnv(env.id)); _confirmDelete = null; }
            GUI.enabled = true;
            if (UITheme.GhostButton("Cancel", GUILayout.Width(56))) _confirmDelete = null;
            GUILayout.EndHorizontal();
        }
    }

    // Shared section: header + a scrolled body of include rows (only drawn when non-empty).
    private void DrawInstanceList(string header, bool hasItems, ref Vector2 scroll, float height, Action body)
    {
        if (!hasItems) return;
        UITheme.Header(header);
        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(height));
        body();
        GUILayout.EndScrollView();
    }

    // A name + On/Off toggle row. Returns true (with the new value) when the toggle changed.
    private static bool DrawIncludeRow(string label, bool included, out bool next)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.ExpandWidth(true));
        next = GUILayout.Toggle(included, included ? "On" : "Off", GUI.skin.button, GUILayout.Width(46));
        GUILayout.EndHorizontal();
        return next != included;
    }

    private void OnInstanceToggled(EnvironmentDef env)
    {
        _active.dirty = true;
        worldRenderer?.RenderEnvironment(env, _active.buildings);
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
        if (UITheme.PrimaryButton("New", GUILayout.Width(64), GUILayout.Height(UITheme.RowH))) CreateBuilding();
        GUI.enabled = !_bldgBusy;
        if (GUILayout.Button("↻", GUILayout.Width(30), GUILayout.Height(UITheme.RowH))) RefreshBuildings();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        UITheme.Header("Buildings");
        _bldgListScroll = GUILayout.BeginScrollView(_bldgListScroll, GUILayout.Height(Screen.height - 240f));
        foreach (var s in _bldgList)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label((s.favorite ? "★ " : "") + (s.name ?? s.id), GUILayout.ExpandWidth(true));
            GUI.enabled = !_bldgBusy;
            if (GUILayout.Button("Edit", GUILayout.Width(56))) OpenBuildingForEdit(s.id);
            if (GUILayout.Button(s.favorite ? "★" : "☆", GUILayout.Width(30)))
            {
                var row = s; bool prev = row.favorite;
                row.favorite = !prev;   // optimistic flip for instant feedback
                libraryClient.ToggleFavoriteBuilding(row.id,
                    nowFav => { row.favorite = nowFav; SortBldgList(); },
                    err    => { row.favorite = prev; _bldgStatus = $"Favorite error: {err}"; });
            }
            if (_adminEnabled && GUILayout.Button("⌫", GUILayout.Width(30))) StartCoroutine(CoArchiveBldg(s.id));
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
            kind: "user");
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
            id  => { b.id = id; _newBldgName = ""; _bldgBusy = false; _bldgStatus = $"Created '{name}'."; RefreshBuildings(); AddBuildingDef(b); editController?.EditBuildingFromLibrary(b); },
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
            scaleNote      = ""
        },
        buildingInstances = new List<BuildingInstance>(),
        objectInstances   = new List<ObjectInstance>(),
    };

    private static BuildingDef BlankBuilding(string name) => new BuildingDef
    {
        id              = Guid.NewGuid().ToString("D"),
        name            = name,
        version         = 1,
        tags            = new List<string>(),
        gridCellSize    = AuthoringConventions.DEFAULT_GRID_CELL_SIZE,
        floors          = 1,
        floorHeight     = AuthoringConventions.DEFAULT_FLOOR_HEIGHT,
        tiles           = new List<TileDef>(),
        embeddedObjects = new List<EmbeddedObjectDef>(),
    };
}
