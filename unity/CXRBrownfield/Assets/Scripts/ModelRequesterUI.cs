using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimpleFileBrowser;

/// <summary>
/// Runtime layout-generation / server panel. Rendered as a themed IMGUI panel (see UITheme) so it
/// matches the library + edit panels (LibraryBrowser, EditController) instead of the old scene-wired
/// uGUI Canvas. All server calls live here: health check, model search, sketch upload, layout
/// generation, and the local/server sample shortcuts.
/// </summary>
public class ModelRequesterUI : MonoBehaviour
{
    [Header("Model Requester")]
    public ModelRequester modelRequester;
    public WorldGenerator worldGenerator;
    public WorldRenderer  worldRenderer;   // USER WIRES THIS IN INSPECTOR (optional — enables library integration)
    public LibraryClient  libraryClient;   // USER WIRES THIS IN INSPECTOR (optional — enables library integration)
    public LibraryBrowser libraryBrowser;  // USER WIRES THIS IN INSPECTOR (optional — adopts generated envs as loaded)
    public EditController editController;  // optional — records the undo entry when a site is filled

    [Header("Panel")]
    [SerializeField] private int panelWidth = 360;   // top-center panel, between the left/right tool panels

    // Names of uploaded input images, shown as a selectable list.
    private readonly List<string> _inputNames = new List<string>();
    private int _selectedInput = -1;

    // Designer notes per sketch, keyed by stored name. Loaded from the server the first time a
    // sketch is selected, then edited in place, so switching sketches keeps an unsent edit. Sent
    // with every Generate (the server saves them beside the sketch).
    private readonly Dictionary<string, string> _notesByInput = new Dictionary<string, string>();
    private readonly HashSet<string> _notesRequested = new HashSet<string>();

    // Generate target: null = a new standalone environment (legacy); else the SitePlotDef id in the
    // active env the generated scene should fill. _generateTargetSiteId is the value captured at
    // click time so a selection change mid-generation can't retarget the result.
    private string _targetSiteId;
    private string _generateTargetSiteId;

    // Sketch orientation sent with a site-targeted request (index into SketchRotationValues).
    // "auto" lets the server turn a sketch whose long side runs the other way from the site's.
    private int _sketchRotation = 0;
    private static readonly string[] SketchRotationLabels = { "Auto", "As drawn", "90", "180", "270" };
    private static readonly string[] SketchRotationValues = { "auto", "0", "90", "180", "270" };
    private static readonly string[] SketchRotationTips =
    {
        UITips.SketchRotationAuto, UITips.SketchRotationAsDrawn,
        UITips.SketchRotation90, UITips.SketchRotation180, UITips.SketchRotation270,
    };

    // Which way to draw the sketch for a site: the sketch's vertical axis lands on Unity X
    // (site_width_ft), its horizontal axis on Unity Z (site_height_ft); LayoutConverter then turns
    // the plan half a turn so the top of the sketch is +X. Pure text for the rail.
    internal static string OrientationHint(float widthFt, float heightFt)
    {
        if (widthFt > heightFt * 1.15f)
            return $"Draw the long side up the page: {widthFt:0} ft tall by {heightFt:0} ft across. Auto turns a sketch drawn the other way.";
        if (heightFt > widthFt * 1.15f)
            return $"Draw the long side across the page: {heightFt:0} ft across by {widthFt:0} ft tall. Auto turns a sketch drawn the other way.";
        return $"Draw the lot about {widthFt:0} ft tall by {heightFt:0} ft across on the page.";
    }

    [Header("Debug")]
    [SerializeField] private bool useDummyLayout = false;

    // ---- IMGUI panel state (replaces the old wired Buttons/Text/Slider) ----
    private string  _status   = "Ready";
    private string  _results  = "Currently loaded model: None";
    private string  _searchQuery = "";
    private bool    _progressVisible = false;
    private float   _progress = 0f;
    private string  _progressMsg = "";
    private Vector2 _bodyScroll, _inputScroll, _resultsScroll;

    private ModelRequester.SearchResult lastSearchResult;
    private string currentLoadedModel = "None";

    private void Start()
    {
        // Find ModelRequester if not assigned
        if (modelRequester == null)
        {
            modelRequester = FindFirstObjectByType<ModelRequester>();
            Debug.Log($"[ModelRequesterUI] Found ModelRequester: {modelRequester != null}");
        }

        if (worldGenerator == null)
        {
            worldGenerator = FindFirstObjectByType<WorldGenerator>();
            Debug.Log($"[ModelRequesterUI] Found WorldGenerator: {worldGenerator != null}");
        }

        if (worldRenderer == null)
            worldRenderer = FindFirstObjectByType<WorldRenderer>();
        if (libraryClient == null)
            libraryClient = FindFirstObjectByType<LibraryClient>();
        if (libraryBrowser == null)
            libraryBrowser = FindFirstObjectByType<LibraryBrowser>();
        if (editController == null)
            editController = FindFirstObjectByType<EditController>();

        // Populate the uploaded-image list from the server if library integration is available.
        if (libraryClient != null)
            RefreshInputs();

        // Subscribe to events with null checks
        if (modelRequester != null)
        {
            if (modelRequester.OnSearchComplete != null)
                modelRequester.OnSearchComplete.AddListener(OnSearchResultsReceived);
            if (modelRequester.OnModelLoaded != null)
                modelRequester.OnModelLoaded.AddListener(OnModelLoaded);
            if (modelRequester.OnError != null)
                modelRequester.OnError.AddListener(OnErrorReceived);
            if (modelRequester.OnDownloadProgress != null)
                modelRequester.OnDownloadProgress.AddListener(OnDownloadProgress);
            if (modelRequester.OnHealthCheckComplete != null)
                modelRequester.OnHealthCheckComplete.AddListener(OnHealthCheckComplete);
            if (modelRequester.OnLayoutGenerated != null)
                modelRequester.OnLayoutGenerated.AddListener(OnLayoutGenerated);
            
            Debug.Log("[ModelRequesterUI] Successfully subscribed to ModelRequester events");
        }
        
        // Initialize progress UI
        SetProgressVisible(false);
        
        UpdateStatusText("Ready - Search a model or click 'Generate Layout' to run sketch prompting");
        UpdateResultsText($"Currently loaded model: {currentLoadedModel}");
        
        Debug.Log("[ModelRequesterUI] Initialization complete");
    }

    // -----------------------------------------------------------------------
    // IMGUI panel — mirrors the LibraryBrowser / EditController look (UITheme).
    // Anchored top-center so it sits between the left (library) and right (edit) panels.
    // -----------------------------------------------------------------------

    private void OnGUI()
    {
        if (WalkthroughController.IsEngaged) return;   // hidden during the first-person walkthrough
        // Generate is one mode of the docked right rail (Direction B). Only draw under that command.
        if (UIMode.Current != AppMode.Generate) return;

        float w = UITheme.RightPanelWidth;
        float x = Screen.width - w - UITheme.Margin;
        var rect = new Rect(x, UITheme.RailTop, w, Screen.height - UITheme.RailTop - UITheme.Margin);
        UITheme.PanelBackground(rect);
        GUILayout.BeginArea(UITheme.Inset(rect));

        UITheme.Title("Sketch → Generate");
        UITheme.Note(_status);

        _bodyScroll = GUILayout.BeginScrollView(_bodyScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
        DrawServerSection();
        DrawGenerateSection();
        DrawSamplesSection();
        DrawSearchSection();
        DrawProgressSection();
        DrawOutputSection();
        GUILayout.EndScrollView();

        UITheme.CaptureTooltip();
        GUILayout.EndArea();
    }

    private void DrawServerSection()
    {
        UITheme.Header("Server");
        if (UITheme.Button("Health check", UITips.HealthCheck, GUILayout.Height(UITheme.RowH)))
            OnHealthCheckClicked();
    }

    private void DrawGenerateSection()
    {
        UITheme.Header("Generate from Sketch");

        // Uploaded images, as a selectable list (replaces the old TMP_Dropdown).
        if (_inputNames.Count == 0)
        {
            UITheme.Note("No uploaded images. Upload a sketch below.");
        }
        else
        {
            _inputScroll = GUILayout.BeginScrollView(_inputScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(90));
            for (int i = 0; i < _inputNames.Count; i++)
            {
                bool sel = i == _selectedInput;
                bool now = UITheme.ListItem(sel, _inputNames[i], UITips.InputImage);
                if (now && !sel) _selectedInput = i;
            }
            GUILayout.EndScrollView();
        }

        GUILayout.BeginHorizontal();
        if (UITheme.Button("Upload image", UITips.UploadImage)) OnUploadImageClicked();
        if (UITheme.Button("Refresh", UITips.RefreshInputs, GUILayout.Width(66))) RefreshInputs();
        GUILayout.EndHorizontal();

        DrawNotesSection();
        DrawSitesTargetSection();

        GUI.enabled = _selectedInput >= 0 && _selectedInput < _inputNames.Count;
        if (UITheme.PrimaryButton("Generate 3D scene", UITips.GenerateScene))
            OnGenerateFromImageClicked();
        GUI.enabled = true;

        if (UITheme.GhostButton("Pick a sketch from disk…", UITips.PickFromDisk))
            OnGenerateLayoutClicked();
    }

    // Notes for the selected sketch: free prose the server first parses into a structured brief
    // (names, style letters, floors, splits, paths, fences, props) and then hands to the layout
    // model with the sketch; the server enforces the brief on the result and reports what it
    // missed. A raw TextArea is the only text entry IMGUI offers (no tooltip can attach to it, so
    // the label carries it); hotkeys already yield to it through UITheme.TypingInUI. The style
    // wraps, so the box grows with the text instead of scrolling a single line.
    private static GUIStyle _notesStyle;

    private void DrawNotesSection()
    {
        if (_selectedInput < 0 || _selectedInput >= _inputNames.Count) return;
        string name = _inputNames[_selectedInput];
        EnsureNotesLoaded(name);

        if (_notesStyle == null) _notesStyle = new GUIStyle(GUI.skin.textArea) { wordWrap = true };
        UITheme.Label("Notes", UITips.GenerateNotes);
        _notesByInput.TryGetValue(name, out string text);
        string edited = GUILayout.TextArea(text ?? "", _notesStyle, GUILayout.MinHeight(64f), GUILayout.ExpandWidth(true));
        if (!string.Equals(edited, text)) _notesByInput[name] = edited;
        UITheme.Note("Name buildings, give a style letter A to F and floors, split a drawn block into shops, describe paths, fences and trees. Example: The long block is three shops: a cafe, Rite Aid style C 4 floors, a bakery.");
    }

    // Fetch a sketch's saved notes once; a reply never overwrites text typed in the meantime.
    private void EnsureNotesLoaded(string name)
    {
        if (libraryClient == null || string.IsNullOrEmpty(name) || !_notesRequested.Add(name)) return;
        libraryClient.GetInputNotes(name,
            notes =>
            {
                if (!_notesByInput.TryGetValue(name, out string typed) || string.IsNullOrEmpty(typed))
                    _notesByInput[name] = notes ?? "";
            },
            err => Debug.LogWarning($"[ModelRequesterUI] Could not load notes for '{name}': {err}"));
    }

    // Sites live here, in the Generate rail only: one unified list. Selecting a row both selects
    // the site (its controls from EditController appear below) and makes it the generation target;
    // "New environment" stays the default. Sites can be drawn, reshaped, renamed and deleted from
    // here without ever loading a sketch — drawing with nothing loaded creates a working env.
    private void DrawSitesTargetSection()
    {
        var host  = libraryBrowser != null ? libraryBrowser.CurrentEnvironment : null;
        var sites = host?.sites;

        // One selection across rails: EditController's selection is the source of truth and the
        // generation target mirrors it (null = new standalone environment). This also adopts a
        // freshly drawn site as the target, and clears the target when the site is deleted or the
        // active environment switches.
        if (editController != null) _targetSiteId = editController.SelectedSiteId;
        else if (_targetSiteId != null && (sites == null || sites.Find(s => s != null && s.id == _targetSiteId) == null))
            _targetSiteId = null;

        UITheme.Header("Sites");
        if (host == null)
            UITheme.Note("No place loaded. Drawing a site creates a new one.");

        if (sites != null && sites.Count > 0)
        {
            if (UITheme.ListItem(_targetSiteId == null, "No site", UITips.SiteTargetNew) && _targetSiteId != null)
            {
                _targetSiteId = null;
                editController?.SelectSite(null);
            }
            string delSiteId = null;
            foreach (var s in sites)
            {
                if (s == null) continue;
                bool degenerate = !SiteFit.BoundaryBounds(s.boundary, out _, out _, out _, out _);
                string fillName = libraryBrowser?.GetSiteFillName(s.id);
                string status = string.IsNullOrEmpty(s.fillEnvironmentId) ? "empty" : fillName ?? "filled";
                string suffix = $" · {status}" +
                                (!string.IsNullOrEmpty(s.fillEnvironmentId) ? " (replaces current fill)" : "");
                bool sel = _targetSiteId == s.id;
                if (editController != null)
                {
                    // Single-line row: select + inline rename + boundary edit + delete (DrawSiteRow).
                    if (editController.DrawSiteRow(host, s, sel, !degenerate, suffix, out bool delSite))
                    {
                        _targetSiteId = s.id;
                        editController.SelectSite(s.id);
                    }
                    if (delSite) delSiteId = s.id;
                }
                else
                {
                    GUI.enabled = !degenerate;
                    if (UITheme.ListItem(sel, s.name + suffix, UITips.SiteTargetRow) && !sel) _targetSiteId = s.id;
                    GUI.enabled = true;
                }
            }
            // Deferred: DeleteSite mutates env.sites, which the foreach above iterates.
            if (delSiteId != null) editController?.DeleteSite(host, delSiteId);
        }

        if (editController != null)
        {
            editController.DrawSiteCreateControls(host);
            editController.DrawSelectedSiteControls(host);
        }

        // Orientation for the targeted site: which way to draw, and how the server may turn the sketch.
        var target = _targetSiteId != null && sites != null ? sites.Find(s => s != null && s.id == _targetSiteId) : null;
        if (target != null && SiteFit.SiteDimsFeet(target.boundary, out float siteWFt, out float siteHFt))
        {
            UITheme.Note(OrientationHint(siteWFt, siteHFt));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sketch:", GUILayout.Width(48f));
            _sketchRotation = UITheme.Segmented(_sketchRotation, SketchRotationLabels, SketchRotationTips);
            GUILayout.EndHorizontal();
        }

        if (_targetSiteId != null && host != null && host.locked)
            UITheme.Note("The active place is locked. Unlock it to fill a site.");
    }

    private void DrawSamplesSection()
    {
        UITheme.Header("Samples");
        GUILayout.BeginHorizontal();
        if (UITheme.Button("Local sample", UITips.LocalSample))  OnTestLocalSampleClicked();
        if (UITheme.Button("Server sample", UITips.ServerSample)) OnTestServerSampleClicked();
        GUILayout.EndHorizontal();
    }

    private void DrawSearchSection()
    {
        UITheme.Header("Model Search");
        GUILayout.BeginHorizontal();
        _searchQuery = GUILayout.TextField(_searchQuery, GUILayout.ExpandWidth(true));
        if (UITheme.Button("Search", UITips.ModelSearch, GUILayout.Width(64))) OnSearchClicked();
        GUILayout.EndHorizontal();
    }

    private void DrawProgressSection()
    {
        if (!_progressVisible) return;
        UITheme.Divider();
        int pct = Mathf.RoundToInt(_progress * 100f);
        UITheme.Note($"{_progressMsg} ({pct}%)");

        var r = GUILayoutUtility.GetRect(1, 14, GUILayout.ExpandWidth(true));
        GUI.Box(r, GUIContent.none);
        var fill = new Rect(r.x, r.y, r.width * Mathf.Clamp01(_progress), r.height);
        var prev = GUI.color; GUI.color = UITheme.Accent;
        GUI.DrawTexture(fill, Texture2D.whiteTexture);
        GUI.color = prev;
    }

    private void DrawOutputSection()
    {
        UITheme.Divider();
        UITheme.Header("Output");
        _resultsScroll = GUILayout.BeginScrollView(_resultsScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(100));
        UITheme.Note(_results);
        GUILayout.EndScrollView();
    }

    private void OnHealthCheckClicked()
    {
        UpdateStatusText("Testing server connection...");
        if (modelRequester != null)
        {
            modelRequester.TestServerConnectionButton();
        }
        else
        {
            UpdateStatusText("ERROR: ModelRequester not found!");
        }
    }
    
    private void OnHealthCheckComplete(bool success, string message)
    {
        if (success)
        {
            UpdateStatusText(message);
        }
        else
        {
            UpdateStatusText($"Health Check Failed: {message}");
        }
    }
    
    private void OnSearchClicked()
    {
        Debug.Log("OnSearchClicked");
        string query = string.IsNullOrWhiteSpace(_searchQuery) ? "" : _searchQuery.Trim();

        if (string.IsNullOrEmpty(query))
        {
            UpdateStatusText("ERROR: Please enter a search term!");
            return;
        }
        
        UpdateStatusText($"Searching and loading: {query}...");
        UpdateResultsText("Searching for model...");
        
        if (modelRequester != null)
        {
            Debug.Log("progress should be visible here?");
            SetProgressVisible(true);
            modelRequester.SearchAndLoadModel(query);
        }
        else
        {
            UpdateStatusText("ERROR: ModelRequester not found!");
        }
    }

    public void OnGenerateLayoutClicked()
    {
        if (useDummyLayout)
        {
            ApplyDummyLayout();
            return;
        }

        UpdateStatusText("Generating layout - select a sketch in the Python file picker...");
        UpdateResultsText("Waiting for sketch selection and layout generation...");

        if (modelRequester != null)
        {
            SetProgressVisible(true);
            UpdateProgress(0f, "Waiting for sketch selection...");
            modelRequester.GenerateLayoutFromSketchButton();
        }
        else
        {
            UpdateStatusText("ERROR: ModelRequester not found!");
        }
    }

    // -------- Layout source: upload image, pick, generate --------

    // Re-list the uploaded input images into the selectable list.
    public void RefreshInputs()
    {
        if (libraryClient == null) { UpdateStatusText("RefreshInputs: LibraryClient not assigned."); return; }
        libraryClient.GetInputs(
            names =>
            {
                _inputNames.Clear();
                if (names != null) _inputNames.AddRange(names);
                // Keep a valid selection: default to the first image, clamp if the list shrank.
                if (_inputNames.Count == 0) _selectedInput = -1;
                else if (_selectedInput < 0 || _selectedInput >= _inputNames.Count) _selectedInput = 0;
                UpdateStatusText($"{_inputNames.Count} uploaded image(s).");
            },
            err => UpdateStatusText($"List inputs error: {err}"));
    }

    // Pick a local image via the runtime file browser and upload it to the server's input/ folder.
    public void OnUploadImageClicked()
    {
        if (libraryClient == null) { UpdateStatusText("Upload: LibraryClient not assigned."); return; }

        FileBrowser.SetFilters(true, new FileBrowser.Filter("Images", ".png", ".jpg", ".jpeg", ".webp", ".bmp"));
        FileBrowser.SetDefaultFilter(".png");
        FileBrowser.ShowLoadDialog(
            paths =>
            {
                if (paths == null || paths.Length == 0) return;
                string path = paths[0];
                UpdateStatusText($"Uploading {System.IO.Path.GetFileName(path)}...");
                libraryClient.UploadInput(path,
                    storedName =>
                    {
                        UpdateStatusText($"Uploaded '{storedName}'.");
                        RefreshInputs();
                    },
                    err => UpdateStatusText($"Upload error: {err}"));
            },
            () => UpdateStatusText("Upload cancelled."),
            FileBrowser.PickMode.Files, false, null, null, "Select a sketch image", "Upload");
    }

    // Generate a layout (via Claude) from the image currently selected in the list.
    public void OnGenerateFromImageClicked()
    {
        if (modelRequester == null) { UpdateStatusText("ERROR: ModelRequester not found!"); return; }
        if (_inputNames.Count == 0)
        {
            UpdateStatusText("No uploaded image selected. Upload one first.");
            return;
        }
        int idx = _selectedInput;
        if (idx < 0 || idx >= _inputNames.Count) { UpdateStatusText("Select an image to generate from."); return; }

        string imageName = _inputNames[idx];

        // Site targeting: send the drawn boundary + its real dimensions so the layout is generated
        // for that parcel, and remember the target for SaveAndRenderLayout's assignment.
        _generateTargetSiteId = null;
        float[][] lotCanvas = null; float? widthFt = null, heightFt = null; string sketchRotation = null;
        var host = libraryBrowser != null ? libraryBrowser.CurrentEnvironment : null;
        var plot = host?.sites?.Find(s => s != null && s.id == _targetSiteId);
        if (_targetSiteId != null)
        {
            if (plot == null) { UpdateStatusText("Target site no longer exists. Pick a target again."); return; }
            if (host.locked)  { UpdateStatusText("The active place is locked. Unlock it to fill a site."); return; }
            lotCanvas = SiteFit.BoundaryToCanvas(plot.boundary);
            if (lotCanvas == null || !SiteFit.SiteDimsFeet(plot.boundary, out float wFt, out float hFt))
            { UpdateStatusText($"Site '{plot.name}' has a degenerate boundary. Reshape it first."); return; }
            widthFt = wFt; heightFt = hFt;
            sketchRotation = SketchRotationValues[Mathf.Clamp(_sketchRotation, 0, SketchRotationValues.Length - 1)];
            _generateTargetSiteId = plot.id;
        }

        // Always send the notes box, empty string included: the server keeps it beside the sketch,
        // and an empty string is how saved notes get cleared.
        _notesByInput.TryGetValue(imageName, out string notes);
        notes = (notes ?? "").Trim();

        UpdateStatusText($"Generating layout from '{imageName}'...");
        UpdateResultsText($"Generating layout from '{imageName}' via Claude...");
        SetProgressVisible(true);
        UpdateProgress(0f, $"Generating from {imageName}...");
        modelRequester.GenerateLayoutFromImage(imageName, lotCanvas, widthFt, heightFt, sketchRotation, notes);
    }

    // Load the bundled local sample (Resources/DummyLayout, the generated reading of
    // samples/WestchesterSample1.jpg) into the scene as an editable env,
    // exactly like a server environment — tracked in the Loaded list and active — but unsaved.
    public void OnTestLocalSampleClicked()
    {
        var asset = Resources.Load<TextAsset>("DummyLayout");
        if (asset == null)
        {
            UpdateStatusText("ERROR: Assets/Resources/DummyLayout.json not found.");
            return;
        }
        if (libraryBrowser == null)
        {
            UpdateStatusText("Local sample needs LibraryBrowser assigned.");
            return;
        }

        FullTerrainData data;
        try { data = JsonConvert.DeserializeObject<FullTerrainData>(asset.text); }
        catch (Exception e) { UpdateStatusText($"Local sample parse error: {e.Message}"); return; }

        // Honour the same Sites target Generate uses, so the sample lands in the drawn plot instead
        // of at the world origin. Resolved before converting so a stale target reports immediately.
        // A locked host is fine here: unlike a real fill this never writes to the host.
        var host = libraryBrowser.CurrentEnvironment;
        SitePlotDef plot = null;
        if (_targetSiteId != null)
        {
            plot = host?.sites?.Find(s => s != null && s.id == _targetSiteId);
            if (plot == null) { UpdateStatusText("Target site no longer exists. Pick a target again."); return; }
        }

        string envName = plot != null ? $"{host.name} - {plot.name}" : "Westchester Sample";
        RenderLayoutLocalOnly(data, envName, plot);
    }

    // Load a sample environment that already exists on the server, via the LibraryBrowser load path
    // so it shows up in the Loaded list like any other environment. Picks the most recently updated
    // place whose name contains "sample" (WestchesterSample2 today), else the first place listed.
    public void OnTestServerSampleClicked()
    {
        if (libraryClient == null || libraryBrowser == null)
        {
            UpdateStatusText("Server sample needs LibraryClient + LibraryBrowser assigned.");
            return;
        }
        UpdateStatusText("Loading a server sample environment...");
        libraryClient.GetEnvironments(
            list =>
            {
                if (list == null || list.Count == 0) { UpdateStatusText("No environments on the server."); return; }
                // The newest place named like "sample" wins, so a freshly stored sample beats older ones.
                EnvironmentSummary pick = null;
                System.DateTime pickTime = System.DateTime.MinValue;
                foreach (var e in list)
                {
                    if (e?.name == null || !e.name.ToLower().Contains("sample")) continue;
                    System.DateTime.TryParse(e.updated ?? "", null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out System.DateTime t);
                    if (pick == null || t > pickTime) { pick = e; pickTime = t; }
                }
                pick ??= list[0];
                libraryBrowser.LoadEnvironmentById(pick.id);
                UpdateStatusText($"Loading server sample '{pick.name ?? pick.id}'...");
            },
            err => UpdateStatusText($"Server sample error: {err}"));
    }

    // Convert a layout and load it into the LibraryBrowser as an editable, unsaved environment:
    // a real row in the Loaded list, active, selectable, tile-editable, Save-able.
    //
    // With `plot` set it is first scaled + translated into that site's bounding box
    // (SiteFit.ProjectIntoSite, the same fit a generated fill uses) so it lands where the site was
    // drawn. SiteFit also seeds site.terrainOrigin with the site's corner, so when this env takes
    // over as active the ground MOVES to the site rather than staying at the world origin with the
    // whole layout floating beside it. Nothing is saved and the host is never written to, so this
    // still works with no server running.
    private void RenderLayoutLocalOnly(FullTerrainData data, string envName, SitePlotDef plot = null)
    {
        if (data == null) { UpdateStatusText("Local sample: no layout data."); return; }
        var conv = LayoutConverter.Convert(data, envName, DecorPalette.GeneratedWindowRule(DecorPalette.LoadDefault()));

        if (plot != null && !SiteFit.ProjectIntoSite(conv.Environment, plot.boundary))
        {
            UpdateStatusText($"Site '{plot.name}' has a degenerate boundary. Reshape it first.");
            return;
        }

        var buildingDefs = new Dictionary<string, BuildingDef>();
        foreach (var b in conv.Buildings)
            if (!string.IsNullOrEmpty(b.id)) buildingDefs[b.id] = b;
        libraryBrowser.AdoptLocalEnvironment(conv.Environment, buildingDefs);

        string where = plot != null ? $" in site '{plot.name}'" : "";
        UpdateStatusText($"Loaded local sample '{envName}'{where} ({conv.Buildings.Count} building(s)). Editable, press Save to persist.");
    }

    private void ApplyDummyLayout()
    {
        var asset = Resources.Load<TextAsset>("DummyLayout");
        if (asset == null)
        {
            UpdateStatusText("ERROR: Assets/Resources/DummyLayout.json not found.");
            return;
        }

        UpdateStatusText("Applying dummy layout...");
        SetProgressVisible(true);
        UpdateProgress(0.1f, "Loading dummy data...");

        string envelopeJson = "{\"status\":\"success\",\"message\":\"Dummy layout.\",\"selected_sketch\":\"sample_output.json\",\"layout\":" + asset.text + "}";
        OnLayoutGenerated(envelopeJson);
    }
    
    private void OnSearchResultsReceived(ModelRequester.SearchResult results)
    {
        lastSearchResult = results;
        
        if (results.count == 0)
        {
            UpdateStatusText($"No models found for '{results.query}'");
            UpdateResultsText($"No models found for '{results.query}'\n\nTry searching for: cat, dog, house, tree");
            SetProgressVisible(false);
        }
        else
        {
            UpdateStatusText($"Found {results.count} models for '{results.query}' - Loading first result...");
            UpdateResultsText($"Loading: {results.models[0].name}...");
            SetProgressVisible(true);
            UpdateProgress(0f, "Starting download...");
        }
    }
    
    private void OnDownloadProgress(float progress, string status)
    {
        UpdateProgress(progress, status);
    }
    
    private void OnModelLoaded(string filePath)
    {
        string fileName = System.IO.Path.GetFileName(filePath);
        currentLoadedModel = fileName;
        
        SetProgressVisible(false);
        UpdateResultsText($"Model Loaded: {fileName}\nSaved to: {filePath}");
        UpdateStatusText($"Success! Model '{fileName}' is loaded and ready to use.");
    }

    private void OnLayoutGenerated(string responseJson)
    {
        SetProgressVisible(false);

        // Parse with Newtonsoft so float[][] in SiteScale.lot_boundary is handled correctly.
        // No JsonUtility fallback: it cannot deserialize float[][], so it silently dropped
        // lot_boundary/paths/fences and rendered outside the multi-env model. Fail loudly instead.
        FullTerrainData layoutData  = null;
        string          sketchPath  = null;
        List<string>    warnings    = null;
        SketchPrepInfo  sketchPrep  = null;
        GenerationDef   generation  = null;
        string          briefText   = "";
        try
        {
            var envelope = JsonConvert.DeserializeObject<NewtonLayoutEnvelope>(responseJson);
            layoutData = envelope?.layout;
            sketchPath = envelope?.selected_sketch;
            warnings   = envelope?.warnings;
            sketchPrep = envelope?.sketch_prep;
            generation = new GenerationDef
            {
                sketch          = sketchPath,
                notes           = envelope?.notes,
                briefJson       = envelope?.brief?.ToString(Formatting.None),
                briefReportJson = envelope?.brief_report?.ToString(Formatting.None),
                model           = envelope?.layout_model,
                briefModel      = envelope?.brief_model,
                created         = DateTime.UtcNow.ToString("o"),
            };
            briefText = DescribeBriefReport(envelope?.brief, envelope?.brief_report);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ModelRequesterUI] Layout parse FAILED — nothing applied. Raw JSON is archived under layouts/. {e}");
            UpdateStatusText("Layout parse FAILED — not applied. See Console.");
            UpdateResultsText($"Parse error: {e.Message}");
            return;
        }

        if (layoutData == null)
        {
            UpdateStatusText("Layout response missing 'layout' field.");
            return;
        }

        int zoneCount  = layoutData.terrain_zones?.Count   ?? 0;
        int prefabCount = layoutData.prefab_instances?.Count ?? 0;
        string warnText = "";
        if (warnings != null && warnings.Count > 0)
        {
            warnText = $"\nServer warnings: {warnings.Count} (see Console)";
            foreach (var w in warnings) Debug.LogWarning($"[ModelRequesterUI] layout warning: {w}");
        }
        // Say what the server did to the sketch so an unexpected orientation is explainable.
        string prepText = "";
        if (sketchPrep != null && (sketchPrep.rotation_deg != 0 || sketchPrep.resampled))
        {
            prepText = "\nSketch: ";
            if (sketchPrep.rotation_deg != 0)
                prepText += $"turned {sketchPrep.rotation_deg} deg{(sketchPrep.auto_rotated ? " (auto)" : "")}";
            if (sketchPrep.resampled)
                prepText += (sketchPrep.rotation_deg != 0 ? ", " : "") + "resampled to the site's proportions";
            Debug.Log($"[ModelRequesterUI] sketch prep: rotation {sketchPrep.rotation_deg} deg, auto {sketchPrep.auto_rotated}, resampled {sketchPrep.resampled}");
        }
        UpdateResultsText($"Layout Generated\nTerrain Zones: {zoneCount}\nPrefabs: {prefabCount}\nSketch: {sketchPath}{prepText}{briefText}{warnText}");

        // New path: convert → save → render via WorldRenderer + LibraryClient.
        // Falls back to WorldGenerator if new components are not wired.
        if (worldRenderer != null && libraryClient != null)
        {
            StartCoroutine(SaveAndRenderLayout(layoutData, sketchPath, generation));
        }
        else
        {
            if (worldGenerator != null)
                worldGenerator.ApplyLayoutData(layoutData);
            else
                Debug.LogWarning("[ModelRequesterUI] Assign WorldRenderer+LibraryClient for library integration, or WorldGenerator for legacy rendering.");
            UpdateStatusText("Layout applied. Assign WorldRenderer + LibraryClient for library integration.");
        }
    }

    // One Output line for the brief: how many named items the layout placed, and which it missed.
    // Missing items also go to the Console so the names survive the next status update.
    private static string DescribeBriefReport(JObject brief, JObject report)
    {
        if (report == null) return "";
        string error = report.Value<string>("error");
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogWarning($"[ModelRequesterUI] brief skipped: {error}");
            return "\nNotes: not parsed (see Console)";
        }
        int satisfied = report["satisfied"] is JArray s ? s.Count : 0;
        var missing = new List<string>();
        if (report["missing"] is JArray m)
            foreach (var item in m)
            {
                string name = item?.Value<string>("name");
                if (!string.IsNullOrEmpty(name)) missing.Add(name);
            }
        foreach (var name in missing)
            Debug.LogWarning($"[ModelRequesterUI] brief building not placed: {name}");
        int named = satisfied + missing.Count;
        string line = named == 0
            ? "\nNotes: read, no buildings named"
            : $"\nNotes: {satisfied} of {named} named building(s) placed";
        if (missing.Count > 0) line += $", missing {string.Join(", ", missing)}";
        return line;
    }

    // Converts, saves all buildings + the environment, then renders.
    private IEnumerator SaveAndRenderLayout(FullTerrainData data, string sketchPath, GenerationDef generation = null)
    {
        UpdateStatusText("Converting layout...");

        var host       = libraryBrowser != null ? libraryBrowser.CurrentEnvironment : null;
        var targetPlot = host?.sites?.Find(s => s != null && s.id == _generateTargetSiteId);

        string envName = string.IsNullOrEmpty(sketchPath)
            ? "Generated Environment"
            : System.IO.Path.GetFileNameWithoutExtension(sketchPath);
        // A site fill is named after its place in the host so the library row reads clearly.
        if (targetPlot != null) envName = $"{host.name} - {targetPlot.name}";

        var conv = LayoutConverter.Convert(data, envName, DecorPalette.GeneratedWindowRule(DecorPalette.LoadDefault()));
        conv.Environment.generation = generation;

        // Dedup identical building defs within this generation so repeated bays don't create
        // duplicate cached records. Duplicates remap their instances to the kept (canonical) def.
        var canonicalByKey   = new Dictionary<string, BuildingDef>();
        var dupIdToCanonical = new Dictionary<string, string>();
        var unique           = new List<BuildingDef>();
        foreach (var bldg in conv.Buildings)
        {
            string key = BuildingStructuralKey(bldg);
            if (canonicalByKey.TryGetValue(key, out var canon))
                dupIdToCanonical[bldg.id] = canon.id;
            else { canonicalByKey[key] = bldg; unique.Add(bldg); }
        }

        // Save the unique cached buildings first (env references them by ID).
        int total = unique.Count;
        int saved = 0;
        var clientToServerId = new Dictionary<string, string>(); // client id → server-confirmed id
        foreach (var bldg in unique)
        {
            string clientId = bldg.id;
            bool done = false;
            libraryClient.PostBuilding(bldg,
                id  => { bldg.id = id; clientToServerId[clientId] = id; done = true; },
                err => { Debug.LogError($"[ModelRequesterUI] PostBuilding failed: {err}"); done = true; },
                kind: "cached");
            while (!done) yield return null;
            saved++;
            UpdateStatusText($"Saving buildings... {saved}/{total}");
        }

        // Fix up building instance refs: collapse duplicates to canonical, then to server-confirmed IDs.
        foreach (var bi in conv.Environment.buildingInstances)
        {
            if (dupIdToCanonical.TryGetValue(bi.buildingId, out string canonId)) bi.buildingId = canonId;
            if (clientToServerId.TryGetValue(bi.buildingId, out string serverId)) bi.buildingId = serverId;
        }

        // Save environment as a generated env (server makes the name unique).
        bool envDone = false;
        libraryClient.PostEnvironment(conv.Environment,
            id  => { conv.Environment.id = id; envDone = true; },
            err => { Debug.LogError($"[ModelRequesterUI] PostEnvironment failed: {err}"); envDone = true; },
            kind: "generated",
            onName: name => { conv.Environment.name = name; });
        while (!envDone) yield return null;

        // Build lookup of the saved cached buildings and load the env like a library environment.
        var buildingDefs = new Dictionary<string, BuildingDef>();
        foreach (var bldg in unique)
            if (!string.IsNullOrEmpty(bldg.id))
                buildingDefs[bldg.id] = bldg;

        if (targetPlot != null && libraryBrowser != null)
        {
            // Fill the drawn site: the child stays its own saved record; the host only gains the
            // reference (undoable), and LibraryBrowser projects + renders the fill in place.
            editController?.RecordEnvironmentEdit("Fill site");
            libraryBrowser.AssignSiteFill(host, targetPlot.id, conv.Environment.id);
            libraryBrowser.RefreshEnvironments();
            UpdateStatusText($"Filled site '{targetPlot.name}' with '{conv.Environment.name}' ({total} building(s)).");
        }
        else if (libraryBrowser != null)
        {
            libraryBrowser.AdoptGeneratedEnvironment(conv.Environment, buildingDefs);
            UpdateStatusText($"Saved + loaded '{conv.Environment.name}' ({total} building(s)).");
        }
        else
        {
            worldRenderer.RenderEnvironment(conv.Environment, buildingDefs);
            UpdateStatusText($"Saved + loaded '{conv.Environment.name}' ({total} building(s)).");
        }
        _generateTargetSiteId = null;
    }

    // Full-content key so only fully identical generated buildings collapse into one cached def.
    // Matches the server's dedup semantics: geometry AND materials AND embedded decor must agree.
    private static string BuildingStructuralKey(BuildingDef b)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(b.floors).Append('|').Append(b.gridCellSize).Append('|').Append(b.floorHeight).Append('|');
        // Style is part of the look: two same-shaped blocks with different letters stay separate.
        sb.Append(b.style ?? "").Append('|');
        // Name and sign too: a split block yields siblings with identical tiles but different names
        // (Cafe, Rite Aid, Bakery); without these the second silently collapses into the first and
        // takes its name. The server's record signature keeps these fields as well.
        sb.Append(b.name ?? "").Append('|').Append(b.signText ?? "").Append('|').Append(b.signFace ?? "").Append('|');
        if (b.tiles != null)
            foreach (var t in b.tiles)
            {
                sb.Append(t.gridX).Append(',').Append(t.gridZ).Append(',').Append(t.floor).Append(',')
                  .Append(t.shapeId).Append(',').Append(t.rotation).Append(',')
                  .Append(t.rotationX).Append(',').Append(t.rotationZ).Append(',');
                if (t.faceMaterials != null)
                    foreach (var kv in t.faceMaterials.OrderBy(kv => kv.Key, System.StringComparer.Ordinal))
                        sb.Append(kv.Key).Append('=').Append(kv.Value).Append('&');
                sb.Append(';');
            }
        sb.Append("#emb#");
        if (b.embeddedObjects != null)
            foreach (var e in b.embeddedObjects)  // instanceId excluded: it is random per run
                sb.Append(e.prefabType).Append(',')
                  .Append(e.localPos != null ? string.Join("/", e.localPos) : "").Append(',')
                  .Append(e.rotationX).Append(',').Append(e.rotationY).Append(',').Append(e.rotationZ).Append(',')
                  .Append(e.scale).Append(',')
                  .Append(e.hostGridX).Append(',').Append(e.hostGridZ).Append(',').Append(e.hostFloor).Append(',')
                  .Append(e.hostFace).Append(',').Append(e.exclusive).Append(',').Append(e.fillsFace).Append(';');
        return sb.ToString();
    }

    private void OnErrorReceived(string errorMessage)
    {
        SetProgressVisible(false);
        UpdateStatusText($"ERROR: {errorMessage}");
        UpdateResultsText($"Error: {errorMessage}\n\nCurrently loaded: {currentLoadedModel}");
    }
    
    private void SetProgressVisible(bool visible)
    {
        _progressVisible = visible;
    }

    private void UpdateProgress(float progress, string statusMessage)
    {
        _progress    = progress;
        _progressMsg = statusMessage;
    }

    private void UpdateStatusText(string message)
    {
        _status = $"[{System.DateTime.Now:HH:mm:ss}] {message}";
        Debug.Log($"[ModelRequesterUI] {message}");
    }

    private void UpdateResultsText(string message)
    {
        _results = message;
    }

    // Newtonsoft-parsed response envelope — handles float[][] in SiteScale.lot_boundary.
    private class NewtonLayoutEnvelope
    {
        public string         status;
        public string         message;
        public string         selected_sketch;
        public FullTerrainData layout;
        public List<string>   warnings;
        public SketchPrepInfo sketch_prep;   // null when the server sent the sketch untouched
        public string         notes;         // the notes the server used (saved beside the sketch)
        public JObject        brief;         // structured reading of the notes; null without notes
        public JObject        brief_report;  // satisfied / missing / splits per the brief, or {error}
        public string         brief_model;   // model that parsed the brief; null when none ran
        public string         layout_model;  // model that produced the layout
    }

    // What the server did to the sketch before the model saw it (sketch_prep.py).
    private class SketchPrepInfo
    {
        public int  rotation_deg;
        public bool auto_rotated;
        public bool resampled;
    }
}