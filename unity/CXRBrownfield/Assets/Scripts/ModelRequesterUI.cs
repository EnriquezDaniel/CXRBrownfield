using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;
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

    [Header("Panel")]
    [SerializeField] private int panelWidth = 360;   // top-center panel, between the left/right tool panels

    // Names of uploaded input images, shown as a selectable list.
    private readonly List<string> _inputNames = new List<string>();
    private int _selectedInput = -1;

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
        // Generate is one mode of the docked right rail (Direction B). Only draw under that command.
        if (UIMode.Current != AppMode.Generate) return;

        float w = UITheme.RightPanelWidth;
        float x = Screen.width - w - UITheme.Margin;
        var rect = new Rect(x, UITheme.RailTop, w, Screen.height - UITheme.RailTop - UITheme.Margin);
        UITheme.PanelBackground(rect);
        GUILayout.BeginArea(UITheme.Inset(rect));

        UITheme.Title("Sketch → Generate");
        UITheme.Note(_status);

        _bodyScroll = GUILayout.BeginScrollView(_bodyScroll);
        DrawServerSection();
        DrawGenerateSection();
        DrawSamplesSection();
        DrawSearchSection();
        DrawProgressSection();
        DrawOutputSection();
        GUILayout.EndScrollView();

        GUILayout.EndArea();
    }

    private void DrawServerSection()
    {
        UITheme.Header("Server");
        if (GUILayout.Button("Health Check", GUILayout.Height(UITheme.RowH)))
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
            _inputScroll = GUILayout.BeginScrollView(_inputScroll, GUILayout.Height(90));
            for (int i = 0; i < _inputNames.Count; i++)
            {
                bool sel = i == _selectedInput;
                bool now = GUILayout.Toggle(sel, _inputNames[i], GUI.skin.button, GUILayout.Height(UITheme.RowH));
                if (now && !sel) _selectedInput = i;
            }
            GUILayout.EndScrollView();
        }

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Upload Image")) OnUploadImageClicked();
        if (GUILayout.Button("↻", GUILayout.Width(30))) RefreshInputs();
        GUILayout.EndHorizontal();

        GUI.enabled = _selectedInput >= 0 && _selectedInput < _inputNames.Count;
        if (UITheme.PrimaryButton("Generate 3D scene"))
            OnGenerateFromImageClicked();
        GUI.enabled = true;

        if (UITheme.GhostButton("Pick a sketch from disk…"))
            OnGenerateLayoutClicked();
    }

    private void DrawSamplesSection()
    {
        UITheme.Header("Samples");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Local Sample"))  OnTestLocalSampleClicked();
        if (GUILayout.Button("Server Sample")) OnTestServerSampleClicked();
        GUILayout.EndHorizontal();
    }

    private void DrawSearchSection()
    {
        UITheme.Header("Model Search");
        GUILayout.BeginHorizontal();
        _searchQuery = GUILayout.TextField(_searchQuery, GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Search", GUILayout.Width(64))) OnSearchClicked();
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
        _resultsScroll = GUILayout.BeginScrollView(_resultsScroll, GUILayout.Height(100));
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
        UpdateStatusText($"Generating layout from '{imageName}'...");
        UpdateResultsText($"Generating layout from '{imageName}' via Claude...");
        SetProgressVisible(true);
        UpdateProgress(0f, $"Generating from {imageName}...");
        modelRequester.GenerateLayoutFromImage(imageName);
    }

    // Load the bundled local sample (Resources/DummyLayout) into the scene as an editable env,
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

        RenderLayoutLocalOnly(data, "LocalSample");
    }

    // Load a sample environment that already exists on the server, via the LibraryBrowser load path
    // so it shows up in the Loaded list like any other environment.
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
                var pick = list.Find(e => e.name != null && e.name.ToLower().Contains("sample")) ?? list[0];
                libraryBrowser.LoadEnvironmentById(pick.id);
                UpdateStatusText($"Loading server sample '{pick.name ?? pick.id}'...");
            },
            err => UpdateStatusText($"Server sample error: {err}"));
    }

    // Convert a layout and load it into the LibraryBrowser as an editable, unsaved environment.
    private void RenderLayoutLocalOnly(FullTerrainData data, string envName)
    {
        if (data == null) { UpdateStatusText("Local sample: no layout data."); return; }
        var conv = LayoutConverter.Convert(data, envName);
        var buildingDefs = new Dictionary<string, BuildingDef>();
        foreach (var b in conv.Buildings)
            if (!string.IsNullOrEmpty(b.id)) buildingDefs[b.id] = b;
        libraryBrowser.AdoptLocalEnvironment(conv.Environment, buildingDefs);
        UpdateStatusText($"Loaded local sample '{envName}' ({conv.Buildings.Count} building(s)). Editable — press Save to persist.");
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
        try
        {
            var envelope = JsonConvert.DeserializeObject<NewtonLayoutEnvelope>(responseJson);
            layoutData = envelope?.layout;
            sketchPath = envelope?.selected_sketch;
            warnings   = envelope?.warnings;
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
        UpdateResultsText($"Layout Generated\nTerrain Zones: {zoneCount}\nPrefabs: {prefabCount}\nSketch: {sketchPath}{warnText}");

        // New path: convert → save → render via WorldRenderer + LibraryClient.
        // Falls back to WorldGenerator if new components are not wired.
        if (worldRenderer != null && libraryClient != null)
        {
            StartCoroutine(SaveAndRenderLayout(layoutData, sketchPath));
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

    // Converts, saves all buildings + the environment, then renders.
    private IEnumerator SaveAndRenderLayout(FullTerrainData data, string sketchPath)
    {
        UpdateStatusText("Converting layout...");

        string envName = string.IsNullOrEmpty(sketchPath)
            ? "Generated Environment"
            : System.IO.Path.GetFileNameWithoutExtension(sketchPath);

        var conv = LayoutConverter.Convert(data, envName);

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

        if (libraryBrowser != null)
            libraryBrowser.AdoptGeneratedEnvironment(conv.Environment, buildingDefs);
        else
            worldRenderer.RenderEnvironment(conv.Environment, buildingDefs);

        UpdateStatusText($"Saved + loaded '{conv.Environment.name}' ({total} building(s)).");
    }

    // Full-content key so only fully identical generated buildings collapse into one cached def.
    // Matches the server's dedup semantics: geometry AND materials AND embedded decor must agree.
    private static string BuildingStructuralKey(BuildingDef b)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(b.floors).Append('|').Append(b.gridCellSize).Append('|').Append(b.floorHeight).Append('|');
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
    }
}