using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// M2/M3/M4: Runtime edit state machine — Browse, PlaceObject, PlaceBuilding, Transform, EditBuilding.
// Selection feedback: TransformGizmo (LineRenderer-based handles + bounds box) and a
// MaterialPropertyBlock tint on the selected object's renderers.
// OnGUI panel sits on the right side of the screen; LibraryBrowser is on the left.
//
// Undo/redo: this controller owns the shared EditHistory and implements EditHistory.IHost. Every
// data-mutating edit (here and in TileBuildingEditor) records a snapshot before mutating; Ctrl+Z /
// Ctrl+Shift+Z restore them (see the Update hotkey block and the IHost region). History is in-memory
// and cleared whenever the active environment changes (see LibraryBrowser.SetActive → ClearHistory).
public class EditController : MonoBehaviour, EditHistory.IHost
{
    // -----------------------------------------------------------------------
    // Inspector references
    // -----------------------------------------------------------------------

    [Header("References")]
    [SerializeField] private LibraryBrowser     libraryBrowser;       // USER WIRES THIS IN INSPECTOR
    [SerializeField] private LibraryClient      libraryClient;        // USER WIRES THIS IN INSPECTOR
    [SerializeField] private WorldRenderer      worldRenderer;        // USER WIRES THIS IN INSPECTOR
    [SerializeField] private TileBuildingEditor tileBuildingEditor;   // USER WIRES THIS IN INSPECTOR
    [SerializeField] private PrefabRegistry     prefabRegistry;       // USER WIRES THIS IN INSPECTOR
    [SerializeField] private PathMaterialPalette pathMaterialPalette; // USER WIRES THIS IN INSPECTOR (path tool)
    [SerializeField] private FencePalette fencePalette;               // USER WIRES THIS IN INSPECTOR (fence tool); borrowed from WorldRenderer if unset
    [SerializeField] private TerrainRegistry    terrainRegistry;      // USER WIRES THIS IN INSPECTOR (surface tool)
    [SerializeField] private Camera             mainCamera;

    [Header("Camera")]
    [SerializeField] private float camSpeed     = 20f;
    [SerializeField] private float camOrbitSens = 0.3f;
    [SerializeField] private float camZoomSens  = 5f;
    [SerializeField] private float camMinDist   = 5f;
    [SerializeField] private float camMaxDist   = 300f;
    // When tile-editing a building, snap/lift the orbit camera onto the floor being edited
    // (recenter + re-distance on entry, lift pivot on floor change). Off = leave the camera put.
    [SerializeField] private bool  snapCameraOnTileEdit = false;

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private enum EditMode { Browse, PlaceObject, PlaceBuilding, Transform, EditBuilding, DrawPath, EditPath, DrawFence, EditFence, PaintObjects, PaintSurface, Measure, EditLot }
    private enum Tool { Move, Rotate, Scale }

    private EditMode _mode = EditMode.Browse;
    // Active transformation. Only this tool's gizmo handles and panel controls are shown;
    // defaults to Move ("position") per the single-tool-at-a-time design.
    private Tool _tool = Tool.Move;
    // Multi-selection rotation pivot: false = each instance spins about its own pivot (default);
    // true = yaw also orbits each instance's XZ position around the selection's centroid, so the
    // group rotates rigidly as one (same math as EnvRotateYaw, scoped to the selection). Toggled
    // from the Rotate panel; sticky across selections.
    private bool _rotateGroupPivot;
    private const float MOVE_STEP = 1f;   // panel translate button step (meters)

    // Selection. _selId/_selGO/_selBaseScale are the PRIMARY (last-clicked) selection: it drives
    // the panel readouts, gizmo framing, and revert origin. _extraSel holds every OTHER selected
    // instance; transforms apply to the primary and all extras, each rotating/scaling about its
    // own pivot (mirrors the tile editor's Shift/Ctrl+click multi-select).
    private string     _selId;
    private bool       _selIsBuilding;
    private GameObject _selGO;
    private Vector3    _selBaseScale = Vector3.one;  // GO localScale at instance scale 1 (preserves authored prefab scale)
    private struct Sel { public string id; public bool isBuilding; public GameObject go; public Vector3 baseScale; }
    private readonly List<Sel>        _extraSel  = new();
    private readonly List<GameObject> _gizmoGOs  = new();  // reused buffer for SetTargets

    // Cross-environment clipboard (Ctrl+C / Ctrl+V): deep-copied instance DATA (not GOs), so
    // entries survive env switches, edits, and deletion of the originals. BuildingDef is a
    // SHARED reference — pasted buildings keep the same buildingId (defs are never cloned).
    private class ClipEntry
    {
        public bool             isBuilding;
        public ObjectInstance   obj;    // deep clone (isBuilding == false)
        public BuildingInstance bldg;   // deep clone (isBuilding == true)
        public BuildingDef      def;    // shared ref for cross-env injection (may be null)
    }
    private readonly List<ClipEntry> _clipboard = new();
    // Whole-environment group selection: when set, the gizmo frames every instance and
    // move/rotate/scale transform the entire environment rigidly about its center (see
    // Env* handlers). Mutually exclusive with the per-instance selection (_selId/_extraSel).
    private bool       _envSelected;
    private float      _lastClickTime;
    private const float DOUBLE_CLICK_INTERVAL = 0.3f;

    // Overlap cycling
    private int _overlapIdx;
    private readonly List<(InstanceMarker m, GameObject go)> _overlapHits = new();

    // Camera
    private float  _camPitch = 40f, _camYaw = 45f, _camDist = 80f;
    private Vector3 _camPivot = new(50f, 0f, 50f);
    private bool   _rightDrag, _midDrag;
    private Vector3 _prevMouse;
    private int    _lastFocusedFloor = int.MinValue;   // tile-edit: last floor the camera was lifted to

    // Transform
    private bool    _transDragging;
    private Vector3 _transStartHit, _transOrigPos;
    private Vector3 _transOrigRot;          // euler degrees (X, Y, Z) captured on Enter
    private float   _transOrigScale;
    private const float ROT_SNAP_DEG = 15f; // rotation snaps to this increment while Shift is held (free otherwise)
    private Vector3 _transPrevMouse;   // drag-local; _prevMouse can't be used — UpdateCamera overwrites it every frame
    private float   _rotDragRaw, _rotDragEmitted;  // R-key yaw drag: accumulate raw, emit only snapped steps
    private TransformGizmo _gizmo;     // clickable move/rotate/scale handles, created in Start

    // Skew — whole-building deform (acute/obtuse corners, sloped roof) on the selected building's
    // BuildingDef via the shared TileDeformField. Lives next to the transform controls in the
    // building selection panel; mutates the def, re-renders, and persists like a tile edit.
    private enum SkewMode { BendCorner, SlopeEdge }
    private bool                    _showSkew;                              // panel foldout
    private SkewMode                _skewMode    = SkewMode.BendCorner;
    private TileDeformField.Corner  _skewCorner  = TileDeformField.Corner.NE;
    private TileDeformField.Edge    _skewEdge    = TileDeformField.Edge.North;
    private float                   _skewAngle   = 30f;   // degrees deviation from square (− acute / + obtuse)
    private float                   _skewRise    = 1f;    // roof-edge rise in cell units
    private float                   _skewFalloff = 3f;    // how far the deform reaches, in tiles
    private TileDeformField.Falloff _skewCurve   = TileDeformField.Falloff.Smooth;

    // PlaceObject
    private string     _placeType;
    private float      _placeRotY;
    private GameObject _placeGhost;
    private Vector3    _placeBaseEuler;   // prefab's authored rotation, kept and combined with scroll yaw

    // PlaceBuilding
    private string     _placeBldgDefId;
    private float      _placeBldgRotY;
    private GameObject _placeBldgGhost;
    private Vector3    _placeBldgGhostCenter;   // ghost center in building-local space (corner-pivot frame)
    private List<BuildingSummary> _bldgList       = new();
    private bool                  _bldgListLoading;
    private bool                  _bldgListFetched;   // auto-fetch once so the list isn't empty by default

    // EditBuilding — standalone (opened from Buildings tab, not a placed instance)
    private bool _standaloneEdit;
    // The rendered building instance hidden while its tiles are being edited, so the live
    // edit copy isn't duplicated/z-fought and its colliders don't intercept paint raycasts.
    private GameObject _editingHiddenGO;

    // Terrain editor — shared brush ghost (a flat ring on the terrain showing the brush radius)
    private LineRenderer _brushRing;

    // DrawPath
    private string _pathMaterial = "pavement_light";
    private float  _pathWidth     = 3f;
    private float  _pathSmoothing = 0.5f;               // 0 = crisp corners, 1 = flowing curves
    private bool   _pathFreehand;                       // false = straight/polyline (click waypoints)
    private readonly List<Vector3> _pathPts = new();    // world-space centerline being drawn (y≈0)
    private bool   _pathStroking;                       // freehand: LMB held
    private float  _pathLastClickTime;                  // double-click finishes a polyline
    // Live, true-width, terrain-draped ribbon preview (replaces the old thin LineRenderer guide).
    private GameObject   _pathPreviewGO;
    private MeshFilter   _pathPreviewFilter;
    private MeshRenderer _pathPreviewRenderer;
    private Material     _pathPreviewFallbackMat;
    private const float  PATH_FREEHAND_STEP = 0.25f;    // freehand capture spacing (m); RDP cleans it up
    private const float  PATH_SIMPLIFY_TOL  = 0.35f;    // RDP tolerance (m) applied to freehand strokes
    private const float  PATH_SNAP_DIST     = 2.0f;     // snap radius (m) to an existing path endpoint
    // EditPath: reshape an existing path's control points + props in place.
    private string _pathEditId;                          // id of the path being edited, or null
    private readonly List<Vector3> _pathEditPts = new(); // working control points (XZ; y unused)
    private int  _pathEditSel = -1;                       // selected control-point index, or -1
    private bool _pathEditDragging;
    private readonly List<GameObject> _pathHandles = new();
    private Material _pathHandleMat, _pathHandleSelMat;
    private const float PATH_HANDLE_PICK_PX = 18f;        // screen-space pick radius for handles

    // DrawFence — mirrors DrawPath: the same point-collection input (straight/freehand, snap, finish),
    // but commits a FenceDef and previews the centerline as a draped LineRenderer (panels appear on
    // finish). Reuses PathGeometry/PATH_* constants + the path handle materials.
    private string _fenceType = "picket";
    private float  _fenceHeight = 0f;                     // fence height (m); 0 ⇒ FencePalette default for the type
    private float  _fenceSmoothing = 0f;                  // 0 = crisp corners (typical), 1 = flowing curves
    private bool   _fenceFreehand;
    private readonly List<Vector3> _fencePts = new();     // world-space centerline being drawn (y≈0)
    private bool   _fenceStroking;
    private float  _fenceLastClickTime;
    private LineRenderer _fencePreviewLine;               // draped centerline guide while drawing/editing
    private Material     _fencePreviewMat;
    // EditFence: reshape an existing fence's control points in place.
    private string _fenceEditId;
    private readonly List<Vector3> _fenceEditPts = new();
    private int  _fenceEditSel = -1;
    private bool _fenceEditDragging;
    private readonly List<GameObject> _fenceHandles = new();

    // EditLot: resize the terrain rectangle or reshape the parcel polygon in place (shares the path
    // handle materials + pick radius). Rect mode drives site.terrainSize; polygon mode site.lotBoundary.
    private bool _lotPolygonMode;                          // false = resize rectangle, true = edit parcel polygon
    private readonly List<Vector3> _lotPts = new();        // working polygon vertices (XZ; y unused)
    private int  _lotSel = -1;                             // selected vertex (polygon) / handle (rect), or -1
    private bool _lotDragging;
    private bool _lotMoved;                                // did the active drag actually change anything
    private float _lotW = 100f, _lotL = 100f;             // working rectangle size during a rect drag
    private readonly List<GameObject> _lotHandles = new();
    private GameObject   _lotPreviewGO;
    private LineRenderer _lotPreviewLR;
    private Material      _lotPreviewMat;
    private bool _resizeScaleContent;                      // Site Settings: scale the layout with the lot vs keep in place

    // PaintObjects (scatter brush)
    private string _brushPrefab;
    private float  _brushRadius    = 5f;
    private float  _brushDensity   = 0.02f;             // scatter attempts per m² per application
    private float  _brushSpacing   = 3f;                // min distance (m) between placed objects; 0 = allow overlap
    private bool   _brushRandomRot = true;
    private float  _brushScaleMin  = 0.85f, _brushScaleMax = 1.2f;
    private readonly List<Vector2> _scatterNeighbors = new();  // reused buffer for spacing checks
    private bool   _brushErase;
    private Vector3 _brushLastApply;
    private bool    _brushApplied;                      // has an application happened this stroke

    // PaintSurface (freehand ground-type brush)
    private string _surfType   = "grass";
    private float  _surfRadius  = 5f;
    private readonly List<Vector3> _surfPts = new();    // current stroke centerline (committed on LMB up)
    private Vector3 _surfLastStamp;

    // Measure — non-destructive ruler: click ground points for live distance / polyline length /
    // polygon area at true scale. Writes nothing to the environment. The same two points feed Scale
    // Calibration (a measured distance + the real distance it should be → rescale the whole env).
    private readonly List<Vector3> _measurePts = new();  // placed ground points (world meters, y≈0)
    private Vector3      _measureCursor;                  // live cursor on the ground
    private LineRenderer _measureLine;                    // overlay polyline through points + cursor
    private const float  MEASURE_Y = 0.06f;              // lift the overlay above the ground
    private string       _calibRealStr = "";            // user-typed real distance for calibration (m)
    private GUIStyle     _measureLabelStyle;             // lazily-built floating-label style

    // Site Settings — editable real-world terrain dimensions + scale note. The text buffers re-sync
    // from the active environment whenever it changes (tracked by _siteFieldsEnvId) so the fields
    // always show the live values until the user edits + Applies.
    private string _siteWidthStr = "", _siteLenStr = "", _scaleNoteStr = "";
    private string _siteFieldsEnvId;

    // Dimension entry — exact W×D×H (meters) for a selected massing-box object. Buffers re-sync when
    // the selection changes (tracked by _dimFieldsId).
    private string _dimWStr = "", _dimDStr = "", _dimHStr = "";
    private string _dimFieldsId;

    // Elevation (basic) — sparse grade points for gentle terrain height. Optional; flat by default.
    private bool   _showElevation;
    private string _gradeXStr = "", _gradeZStr = "", _gradeHStr = "";

    // UI scrolls
    private Vector2 _prefabScroll, _bldgPickerScroll;
    private string  _placeSearch = "";
    private int     _placeFilter;            // 0 = All, 1 = Objects, 2 = Buildings (Place chips)
    private Vector2 _pathMatScroll, _brushPrefabScroll, _surfTypeScroll, _pathListScroll;
    private Vector2 _fenceTypeScroll, _fenceListScroll;
    private Vector2 _panelScroll;

    // Undo/redo history (data edits only; in-memory). Created in Start, shared with the tile editor.
    private EditHistory _history;

    private const int PANEL_W = UITheme.RightPanelWidth;

    // -----------------------------------------------------------------------
    // Input System helpers — abstracts Mouse.current / Keyboard.current calls.
    // Scroll: raw HID = 120 per click on Windows; dividing gives ≈ ±1 per tick.
    // -----------------------------------------------------------------------

    private static float   ScrollY  => Mouse.current    != null ? Mouse.current.scroll.ReadValue().y / 120f : 0f;
    private static Vector3 MousePos => Mouse.current    != null ? (Vector3)(Vector2)Mouse.current.position.ReadValue() : Vector3.zero;
    private static bool LMBDown  => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
    private static bool LMBHeld  => Mouse.current != null && Mouse.current.leftButton.isPressed;
    private static bool LMBUp    => Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;
    private static bool RMBDown  => Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
    private static bool RMBUp    => Mouse.current != null && Mouse.current.rightButton.wasReleasedThisFrame;
    private static bool MMBDown  => Mouse.current != null && Mouse.current.middleButton.wasPressedThisFrame;
    private static bool MMBUp    => Mouse.current != null && Mouse.current.middleButton.wasReleasedThisFrame;
    private static Keyboard KB   => Keyboard.current;

    // True while an IMGUI text field or slider in any panel holds keyboard focus. Scene keyboard
    // shortcuts (WASD, G/R/T, Delete, arrows, …) are suppressed while this is set, so typing a
    // name — or dragging a slider — never also drives the scene. Camera mouse-look is unaffected.
    private static bool TypingInUI => GUIUtility.keyboardControl != 0;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    private void Awake()
    {
        if (mainCamera == null) mainCamera = Camera.main;
    }

    private void Start()
    {
        if (libraryBrowser     == null) libraryBrowser     = FindFirstObjectByType<LibraryBrowser>();
        if (libraryClient      == null) libraryClient      = FindFirstObjectByType<LibraryClient>();
        if (worldRenderer      == null) worldRenderer      = FindFirstObjectByType<WorldRenderer>();
        if (tileBuildingEditor == null) tileBuildingEditor = FindFirstObjectByType<TileBuildingEditor>();
        if (tileBuildingEditor != null) tileBuildingEditor.OnSaveRequested += SaveCurrentEditingBuilding;

        _history = new EditHistory(this);
        if (tileBuildingEditor != null) tileBuildingEditor.History = _history;

        // Borrow the palette/registries from WorldRenderer when this component's own slots are
        // empty, so the terrain tools work without a second inspector assignment. (The path picker
        // reads pathMaterialPalette; the ground brush reads terrainRegistry.)
        if (pathMaterialPalette == null) pathMaterialPalette = worldRenderer?.PathMaterialPalette;
        if (fencePalette        == null) fencePalette        = worldRenderer?.FencePalette;
        if (terrainRegistry     == null) terrainRegistry     = worldRenderer?.TerrainRegistry;
        if (prefabRegistry      == null) prefabRegistry      = worldRenderer?.PrefabRegistry;

        _gizmo = gameObject.AddComponent<TransformGizmo>();
        _gizmo.MoveDelta   += OnGizmoMove;
        _gizmo.RotateDelta += OnGizmoRotate;
        _gizmo.ScaleDelta  += OnGizmoScale;
        _gizmo.DragEnded   += OnGizmoDragEnd;
    }

    private void OnDestroy()
    {
        if (tileBuildingEditor != null) tileBuildingEditor.OnSaveRequested -= SaveCurrentEditingBuilding;
    }

    // Central read-only gate: true while the active environment carries the persistent "digital
    // twin" locked flag. Every scene-input mutation path early-outs on this; the right-rail tool
    // panels swap to a locked notice. (Distinct from backdrop lock, which is just "not active".)
    private bool ActiveLocked => libraryBrowser != null && libraryBrowser.IsActiveLocked;

    private void Update()
    {
        // Undo / redo (Ctrl+Z, Ctrl+Shift+Z). Checked before any mode handler so the keystroke is
        // swallowed and never doubles as a scene shortcut (e.g. the tile editor's Z rotate-axis key).
        if (_history != null && KB != null && !TypingInUI &&
            (KB.leftCtrlKey.isPressed || KB.rightCtrlKey.isPressed) && KB.zKey.wasPressedThisFrame)
        {
            if (!ActiveLocked)   // still swallow the keystroke on a locked twin, just do nothing
            {
                if (KB.leftShiftKey.isPressed || KB.rightShiftKey.isPressed) _history.Redo();
                else                                                         _history.Undo();
            }
            return;
        }

        // Copy / paste (Ctrl+C, Ctrl+V). Copy is read-only and allowed on a locked twin; paste
        // refuses there. Gated to Browse/Transform — the only modes with an instance selection.
        if (KB != null && !TypingInUI &&
            (KB.leftCtrlKey.isPressed || KB.rightCtrlKey.isPressed) &&
            (_mode == EditMode.Browse || _mode == EditMode.Transform))
        {
            if (KB.cKey.wasPressedThisFrame) { CopySelection(); return; }
            if (KB.vKey.wasPressedThisFrame) { if (!ActiveLocked) PasteClipboard(); return; }
        }

        // Close any open undo gesture once the mouse is released, so a whole drag / brush stroke /
        // slider drag collapses to a single undo entry regardless of which mode/tool opened it.
        if (_history != null && (Mouse.current == null || !Mouse.current.leftButton.isPressed))
            _history.EndGesture();

        // Rotation snapping is Shift-held only; keep the gizmo ring in step (0 = free rotation).
        if (_gizmo != null) _gizmo.rotationSnap = RotSnapHeld ? ROT_SNAP_DEG : 0f;

        UpdateCamera();

        switch (_mode)
        {
            case EditMode.Browse:        UpdateBrowse();        break;
            case EditMode.PlaceObject:   UpdatePlaceObject();   break;
            case EditMode.PlaceBuilding: UpdatePlaceBuilding(); break;
            case EditMode.Transform:     UpdateTransform();     break;
            case EditMode.EditBuilding:  UpdateEditBuilding();  break;
            case EditMode.DrawPath:      UpdateDrawPath();      break;
            case EditMode.EditPath:      UpdateEditPath();      break;
            case EditMode.DrawFence:     UpdateDrawFence();     break;
            case EditMode.EditFence:     UpdateEditFence();     break;
            case EditMode.PaintObjects:  UpdatePaintObjects();  break;
            case EditMode.PaintSurface:  UpdatePaintSurface();  break;
            case EditMode.Measure:       UpdateMeasure();       break;
            case EditMode.EditLot:       UpdateEditLot();       break;
        }

        // Gizmo only renders while something is selected in Browse/Transform mode,
        // showing only the active tool's handles.
        if (_gizmo != null)
        {
            _gizmo.SetMode(GizmoMode());
            _gizmo.enabled = (_selGO != null || _envSelected) && !ActiveLocked &&
                             (_mode == EditMode.Browse || _mode == EditMode.Transform);
        }
    }

    // -----------------------------------------------------------------------
    // Camera (always active)
    // -----------------------------------------------------------------------

    private void UpdateCamera()
    {
        if (KB == null || Mouse.current == null) return;

        Vector3 mouse  = MousePos;
        bool    overUI = IsMouseOverUI();

        // Right-click orbit — only begin a drag when the press lands in the 3D view, not on a
        // panel (an in-progress orbit keeps going even if the cursor later moves over the UI).
        if (RMBDown && !overUI) { _rightDrag = true;  _prevMouse = mouse; }
        if (RMBUp)    _rightDrag = false;

        // Middle-click pan
        if (MMBDown && !overUI) { _midDrag = true;   _prevMouse = mouse; }
        if (MMBUp)    _midDrag = false;

        if (_rightDrag)
        {
            Vector3 d = mouse - _prevMouse;
            _camYaw   += d.x * camOrbitSens;
            _camPitch -= d.y * camOrbitSens;
            _camPitch  = Mathf.Clamp(_camPitch, 5f, 89f);
        }

        if (_midDrag)
        {
            Vector3 d     = mouse - _prevMouse;
            float   pan   = _camDist * 0.001f;
            Vector3 right = Quaternion.Euler(0f, _camYaw, 0f) * Vector3.right;
            Vector3 fwd   = Quaternion.Euler(0f, _camYaw, 0f) * Vector3.forward;
            _camPivot -= (right * d.x + fwd * d.y) * pan;
        }

        _prevMouse = mouse;

        // Scroll zoom (blocked over the UI so scrolling a list/scroll-view doesn't zoom the camera,
        // and in place modes — they use scroll for rotation)
        if (!overUI && _mode != EditMode.PlaceObject && _mode != EditMode.PlaceBuilding)
            _camDist = Mathf.Clamp(_camDist - ScrollY * camZoomSens, camMinDist, camMaxDist);

        // WASD pan (blocked while in Transform mode — arrow keys nudge there instead — and while
        // typing in a panel text field, so the movement keys edit text instead of moving the camera)
        if (_mode != EditMode.Transform && !TypingInUI)
        {
            float spd   = camSpeed * Time.deltaTime;
            Vector3 right = Quaternion.Euler(0f, _camYaw, 0f) * Vector3.right;
            Vector3 fwd   = Quaternion.Euler(0f, _camYaw, 0f) * Vector3.forward;
            if (KB.wKey.isPressed) _camPivot += fwd   * spd;
            if (KB.sKey.isPressed) _camPivot -= fwd   * spd;
            if (KB.aKey.isPressed) _camPivot -= right * spd;
            if (KB.dKey.isPressed) _camPivot += right * spd;
        }

        // Apply camera
        var rot = Quaternion.Euler(_camPitch, _camYaw, 0f);
        mainCamera.transform.position = _camPivot + rot * new Vector3(0f, 0f, -_camDist);
        mainCamera.transform.rotation = rot;
    }

    // -----------------------------------------------------------------------
    // Browse mode
    // -----------------------------------------------------------------------

    private void UpdateBrowse()
    {
        if (KB == null) return;

        if (_envSelected) { UpdateEnvSelected(); return; }

        // Re-renders (place, include-toggle, library load) replace the instantiated
        // GOs — re-resolve the selected instance so highlight/gizmo follow it.
        if (_selGO == null && !string.IsNullOrEmpty(_selId)) RebindSelection();

        _gizmo?.Tick();

        // Keyboard scene shortcuts — suppressed while a panel text field/slider is focused, so
        // typing (incl. Delete/Backspace) never edits or deletes the scene selection.
        if (!TypingInUI)
        {
            if (KB.escapeKey.wasPressedThisFrame) Deselect();

            if (_selGO != null)
            {
                if (KB.gKey.wasPressedThisFrame) EnterTransform(Tool.Move);
                if (KB.rKey.wasPressedThisFrame) EnterTransform(Tool.Rotate);
                if (KB.tKey.wasPressedThisFrame) EnterTransform(Tool.Scale);
                if (KB.deleteKey.wasPressedThisFrame || KB.backspaceKey.wasPressedThisFrame) DeleteSelected();
            }

            // Tab cycles overlapping hits from the last click (§5 Browse spec)
            if (KB.tabKey.wasPressedThisFrame && _overlapHits.Count > 1)
            {
                _overlapIdx = (_overlapIdx + 1) % _overlapHits.Count;
                var (cycleMarker, cycleGO) = _overlapHits[_overlapIdx];
                if (cycleMarker != null)
                    SetSelection(cycleMarker.instanceId, cycleMarker.isBuilding, cycleGO);
            }
        }

        if (_gizmo != null && _gizmo.IsInteracting) return;  // click belongs to a gizmo handle
        if (!LMBDown || IsMouseOverUI()) return;

        Ray ray = mainCamera.ScreenPointToRay(MousePos);

        _overlapHits.Clear();
        var seen = new HashSet<string>();
        foreach (var h in Physics.RaycastAll(ray, 1000f))
        {
            var m = h.collider.GetComponentInParent<InstanceMarker>();
            if (m != null && seen.Add(m.instanceId))
                _overlapHits.Add((m, m.gameObject));
        }

        if (_overlapHits.Count == 0)
        {
            if (!AdditiveHeld()) Deselect();   // additive click on empty keeps the selection
            return;
        }

        // Shift/Ctrl+click toggles the nearest hit in/out of the selection (no cycle, no edit).
        if (AdditiveHeld())
        {
            var (am, ago) = _overlapHits[0];
            ToggleSelection(am.instanceId, am.isBuilding, ago);
            return;
        }

        float now      = Time.time;
        bool isDouble  = (now - _lastClickTime) < DOUBLE_CLICK_INTERVAL && _overlapHits[0].m.instanceId == _selId;
        _lastClickTime = now;

        // Cycle overlapping objects on repeated single-clicks of the same spot
        if (!isDouble && _overlapHits.Count > 1 && _selId == _overlapHits[0].m.instanceId)
            _overlapIdx = (_overlapIdx + 1) % _overlapHits.Count;
        else if (!isDouble)
            _overlapIdx = 0;

        _overlapIdx = Mathf.Clamp(_overlapIdx, 0, _overlapHits.Count - 1);
        var (marker, go) = _overlapHits[_overlapIdx];
        SetSelection(marker.instanceId, marker.isBuilding, go);

        if (isDouble && _selIsBuilding && tileBuildingEditor != null)
            EnterEditBuilding();
    }

    // -----------------------------------------------------------------------
    // Whole-environment selection — group move/rotate/scale of every instance
    // -----------------------------------------------------------------------

    // Selects the whole active environment as a transform group. The gizmo frames every rendered
    // instance; the Move/Rotate/Scale tools then transform them all rigidly about the env center.
    private void SelectEnvironment()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null) return;
        Deselect();                 // drop any per-instance selection first
        _envSelected = true;
        _tool        = Tool.Move;
        _mode        = EditMode.Browse;
        RefreshEnvGizmoTargets();
    }

    private void DeselectEnv()
    {
        _envSelected = false;
        _tool        = Tool.Move;
        _gizmoGOs.Clear();
        _gizmo?.Clear();
    }

    // Re-resolves every instance's live GameObject (rebuilt on each re-render) and frames the
    // whole set with the gizmo. The gizmo centers on the combined bounds.
    private void RefreshEnvGizmoTargets()
    {
        _gizmoGOs.Clear();
        var env = libraryBrowser?.CurrentEnvironment;
        if (env != null)
        {
            if (env.buildingInstances != null)
                foreach (var b in env.buildingInstances)
                { var go = worldRenderer?.GetInstanceGO(b.instanceId); if (go != null) _gizmoGOs.Add(go); }
            if (env.objectInstances != null)
                foreach (var o in env.objectInstances)
                { var go = worldRenderer?.GetInstanceGO(o.instanceId); if (go != null) _gizmoGOs.Add(go); }
        }
        if (_gizmoGOs.Count == 0) { _gizmo?.Clear(); return; }
        _gizmo?.SetTargets(_gizmoGOs, mainCamera);
    }

    private void UpdateEnvSelected()
    {
        // Live GOs are destroyed/recreated on re-render — re-frame when the cached set goes stale.
        if (_gizmoGOs.Count == 0 || _gizmoGOs[0] == null) RefreshEnvGizmoTargets();

        _gizmo?.Tick();

        if (!TypingInUI)
        {
            if (KB.escapeKey.wasPressedThisFrame) { DeselectEnv(); return; }
            if (KB.gKey.wasPressedThisFrame) _tool = Tool.Move;
            if (KB.rKey.wasPressedThisFrame) _tool = Tool.Rotate;
            if (KB.tKey.wasPressedThisFrame) _tool = Tool.Scale;
        }

        if (_gizmo != null && _gizmo.IsInteracting) return;  // drag belongs to a gizmo handle
        if (!LMBDown || IsMouseOverUI()) return;

        // A bare click on an instance leaves env mode and selects that instance; clicking empty
        // space keeps the whole environment selected (Esc or the panel button deselects).
        if (TryPickInstance(out var marker, out var go))
        {
            DeselectEnv();
            SetSelection(marker.instanceId, marker.isBuilding, go);
        }
    }

    // XZ centroid of every instance — the pivot for group rotate/scale. It is invariant under
    // rotation/scale about itself, so recomputing it each step is stable; under a group move it
    // shifts with everything. Y is ignored (yaw/footprint-scale operate in the ground plane).
    private Vector3 EnvPivot(EnvironmentDef env)
    {
        Vector3 sum = Vector3.zero; int n = 0;
        if (env.buildingInstances != null)
            foreach (var b in env.buildingInstances)
                if (b.position != null && b.position.Length >= 3) { sum += new Vector3(b.position[0], 0f, b.position[2]); n++; }
        if (env.objectInstances != null)
            foreach (var o in env.objectInstances)
                if (o.position != null && o.position.Length >= 3) { sum += new Vector3(o.position[0], 0f, o.position[2]); n++; }
        return n > 0 ? sum / n : Vector3.zero;
    }

    // Translate the entire environment. Data is updated for every instance (incl. excluded ones
    // with no GO); live GOs shift by the same world delta for immediate feedback.
    private void EnvMove(EnvironmentDef env, Vector3 delta)
    {
        _history?.RecordBefore(EditHistory.Scope.Environment, "Move environment");
        if (env.buildingInstances != null)
            foreach (var b in env.buildingInstances) OffsetPos(b.position, delta);
        if (env.objectInstances != null)
            foreach (var o in env.objectInstances) OffsetPos(o.position, delta);
        foreach (var go in _gizmoGOs) if (go != null) go.transform.position += delta;
    }

    // Yaw the whole environment about its center: orbit each instance's XZ and add the yaw to its
    // own rotationY. `deg` arrives as emitted (gizmo snaps to 15° steps only while Shift is held;
    // the panel uses ±15° buttons).
    private void EnvRotateYaw(EnvironmentDef env, float deg)
    {
        if (Mathf.Abs(deg) < 0.0001f) return;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Rotate environment");
        Vector3 P = EnvPivot(env);
        Quaternion q = Quaternion.Euler(0f, deg, 0f);

        if (env.buildingInstances != null)
            foreach (var b in env.buildingInstances) { OrbitXZ(b.position, P, q); b.rotationY += deg; }
        if (env.objectInstances != null)
            foreach (var o in env.objectInstances) { OrbitXZ(o.position, P, q); o.rotationY += deg; }

        // RotateAround orbits the GO about P and spins its facing by deg in one step — matching the
        // data update above (Unity composes world-Y yaw outermost, so it equals rotationY + deg).
        foreach (var go in _gizmoGOs) if (go != null) go.transform.RotateAround(P, Vector3.up, deg);
    }

    // Scale the whole environment about its center by a multiplicative factor: each instance's XZ
    // distance from the center and its own scale are multiplied by `factor`.
    private void EnvScale(EnvironmentDef env, float factor)
    {
        factor = Mathf.Clamp(factor, 0.5f, 2f);   // per-step guard; drags emit factors near 1
        if (Mathf.Abs(factor - 1f) < 0.0001f) return;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Scale environment");
        Vector3 P = EnvPivot(env);

        if (env.buildingInstances != null)
            foreach (var b in env.buildingInstances) { ScaleXZ(b.position, P, factor); b.scale = Mathf.Max(0.1f, b.scale * factor); }
        if (env.objectInstances != null)
            foreach (var o in env.objectInstances) { ScaleXZ(o.position, P, factor); o.scale = Mathf.Max(0.1f, o.scale * factor); }

        foreach (var go in _gizmoGOs)
        {
            if (go == null) continue;
            Vector3 p = go.transform.position;
            go.transform.position   = new Vector3(P.x + (p.x - P.x) * factor, p.y, P.z + (p.z - P.z) * factor);
            go.transform.localScale *= factor;
        }
    }

    private static void OffsetPos(float[] p, Vector3 d)
    {
        if (p == null || p.Length < 3) return;
        p[0] += d.x; p[1] += d.y; p[2] += d.z;
    }
    private static void OrbitXZ(float[] p, Vector3 pivot, Quaternion q)
    {
        if (p == null || p.Length < 3) return;
        Vector3 off = q * new Vector3(p[0] - pivot.x, 0f, p[2] - pivot.z);
        p[0] = pivot.x + off.x; p[2] = pivot.z + off.z;
    }
    private static void ScaleXZ(float[] p, Vector3 pivot, float f)
    {
        if (p == null || p.Length < 3) return;
        p[0] = pivot.x + (p[0] - pivot.x) * f;
        p[2] = pivot.z + (p[2] - pivot.z) * f;
    }

    // Commit a discrete (panel-button) env edit: re-render so objects re-ground onto the terrain,
    // then re-frame the gizmo on the rebuilt GOs. Gizmo drags defer this to OnGizmoDragEnd.
    private void AfterEnvEdit(EnvironmentDef env)
    {
        if (env == null) return;
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        RefreshEnvGizmoTargets();
    }

    // -----------------------------------------------------------------------
    // Transform mode
    // -----------------------------------------------------------------------

    private void EnterTransform(Tool tool)
    {
        if (_selGO == null || ActiveLocked) return;
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null) return;

        Vector3 pos = GetInstancePos(env);
        _transOrigPos   = pos;
        _transOrigRot   = GetInstanceRot(env);
        _transOrigScale = GetInstanceScale(env);
        _tool           = tool;
        _transDragging  = false;
        _mode           = EditMode.Transform;
    }

    private TransformGizmo.Mode GizmoMode() => _tool switch
    {
        Tool.Rotate => TransformGizmo.Mode.Rotate,
        Tool.Scale  => TransformGizmo.Mode.Scale,
        _           => TransformGizmo.Mode.Move,
    };

    private void UpdateTransform()
    {
        if (KB == null) return;

        // Transform hotkeys — suppressed while typing in a panel text field/slider.
        if (!TypingInUI)
        {
            if (KB.escapeKey.wasPressedThisFrame)
            {
                // Escape exits Transform but keeps the edits (no revert); commit them like Enter does.
                libraryBrowser?.MarkDirty();
                _mode = EditMode.Browse;
                return;
            }
            if (KB.enterKey.wasPressedThisFrame || KB.numpadEnterKey.wasPressedThisFrame)
            {
                libraryBrowser?.MarkDirty();
                _mode = EditMode.Browse;
                return;
            }

            if (KB.gKey.wasPressedThisFrame) { _transDragging = false; _tool = Tool.Move; }
            if (KB.rKey.wasPressedThisFrame) { _transDragging = false; _tool = Tool.Rotate; }
            if (KB.tKey.wasPressedThisFrame) { _transDragging = false; _tool = Tool.Scale; }
        }

        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null) return;

        _gizmo?.Tick();

        // Re-select while transforming: a bare left-click on a *different* instance
        // (not on a gizmo handle, not over UI) switches the selection instead of
        // moving/rotating/scaling the current object. The gizmo handles remain the
        // only explicit way to transform — so clicking the selected object's body or
        // empty ground still free-drags as before.
        if (LMBDown && !IsMouseOverUI() && (_gizmo == null || !_gizmo.IsInteracting)
            && TryPickInstance(out var pick, out var pickGO))
        {
            if (AdditiveHeld())
            {
                ToggleSelection(pick.instanceId, pick.isBuilding, pickGO);
                return;              // consume this click so no transform drag begins
            }
            if (pick.instanceId != _selId)
            {
                SetSelection(pick.instanceId, pick.isBuilding, pickGO);
                EnterTransform(_tool);   // re-capture revert origin for the new target; keep the active tool
                return;                  // consume this click so no transform drag begins
            }
        }

        // Free-drag fallback (mouse anywhere); skipped while the gizmo owns the mouse.
        if (_gizmo == null || !_gizmo.IsInteracting)
            switch (_tool)
            {
                case Tool.Move:   HandleMove(env);   break;
                case Tool.Rotate: HandleRotate(env); break;
                case Tool.Scale:  HandleScale(env);  break;
            }

        // Arrow-key nudge (suppressed while typing — arrows move the text caret instead)
        if (!TypingInUI)
        {
            float nudge = 0.5f;
            Vector3 ndelta = Vector3.zero;
            if (KB.upArrowKey.wasPressedThisFrame)    ndelta =  Vector3.forward * nudge;
            if (KB.downArrowKey.wasPressedThisFrame)  ndelta =  Vector3.back    * nudge;
            if (KB.leftArrowKey.wasPressedThisFrame)  ndelta =  Vector3.left    * nudge;
            if (KB.rightArrowKey.wasPressedThisFrame) ndelta =  Vector3.right   * nudge;
            if (ndelta != Vector3.zero) ApplyPosDelta(env, ndelta);
        }
    }

    private void HandleMove(EnvironmentDef env)
    {
        Vector3 origin = GetInstancePos(env);
        var plane = new Plane(Vector3.up, origin);
        Ray ray   = mainCamera.ScreenPointToRay(MousePos);

        if (LMBDown && !IsMouseOverUI() && plane.Raycast(ray, out float d0))
        { _history?.BeginGesture(EditHistory.Scope.Environment, "Move"); _transStartHit = ray.GetPoint(d0); _transDragging = true; }

        if (_transDragging && LMBHeld && plane.Raycast(ray, out float d1))
        {
            Vector3 delta = ray.GetPoint(d1) - _transStartHit;
            _transStartHit = ray.GetPoint(d1);
            ApplyPosDelta(env, delta);
        }
        if (LMBUp) _transDragging = false;
    }

    // Both handlers use _transPrevMouse: they previously read _prevMouse, which
    // UpdateCamera overwrites with the current mouse position every frame *before*
    // these run — so the computed delta was always zero (rotate/scale appeared dead).
    // R-key free drag rotates around world Y (yaw). Accumulate the raw mouse-driven angle;
    // rotation is free unless Shift is held, in which case only snapped steps are emitted so
    // the result lands on 15° multiples without dropping sub-step motion. X/Z are set via the
    // gizmo rings or the panel sliders.
    private void HandleRotate(EnvironmentDef env)
    {
        if (LMBDown && !IsMouseOverUI())
        { _history?.BeginGesture(EditHistory.Scope.Environment, "Rotate"); _transDragging = true; _transPrevMouse = MousePos; _rotDragRaw = 0f; _rotDragEmitted = 0f; }
        if (LMBUp) _transDragging = false;
        if (_transDragging && LMBHeld)
        {
            _rotDragRaw += (MousePos.x - _transPrevMouse.x) * 0.5f;
            _transPrevMouse = MousePos;
            float target = SnapIf(_rotDragRaw);
            float emit   = target - _rotDragEmitted;
            _rotDragEmitted = target;
            if (Mathf.Abs(emit) > 0.0001f) ApplyRotDelta(env, new Vector3(0f, emit, 0f));
        }
    }

    private void HandleScale(EnvironmentDef env)
    {
        if (LMBDown && !IsMouseOverUI()) { _history?.BeginGesture(EditHistory.Scope.Environment, "Scale"); _transDragging = true; _transPrevMouse = MousePos; }
        if (LMBUp)    _transDragging = false;
        if (_transDragging && LMBHeld)
        {
            float delta = (MousePos.y - _transPrevMouse.y) * 0.005f;
            _transPrevMouse = MousePos;
            ApplyScaleDelta(env, delta);
        }
    }

    // Translate every selected instance by the same world delta.
    private void ApplyPosDelta(EnvironmentDef env, Vector3 delta)
    {
        // Discrete callers (arrow nudge, panel ± buttons) record one entry here; drag/gizmo callers
        // open a gesture first so this RecordBefore is a no-op for them (the gesture coalesces).
        _history?.RecordBefore(EditHistory.Scope.Environment, "Move");
        foreach (var s in AllSelected())
        {
            float[] p = s.isBuilding ? FindBI(env, s.id)?.position : FindOI(env, s.id)?.position;
            if (p == null || p.Length < 3) continue;
            p[0] += delta.x; p[1] += delta.y; p[2] += delta.z;
            if (s.go) s.go.transform.position += delta;
        }
    }

    // Adds a per-axis euler delta to every selected instance. Rotation is free by default; while
    // Shift is held the result snaps to ROT_SNAP_DEG on the changed axes (untouched axes keep any
    // free value they have). The GameObject is driven straight from the stored euler (not
    // transform.Rotate) so the live object always matches Quaternion.Euler(...) * authored base
    // used at render/save time.
    // Pivot: each instance spins about its own pivot; with the group-pivot toggle on and a
    // multi-selection, the yaw component also orbits each instance's XZ position around the
    // selection centroid (OrbitXZ, same math as EnvRotateYaw) so the group turns rigidly.
    private void ApplyRotDelta(EnvironmentDef env, Vector3 d)
    {
        bool group = _rotateGroupPivot && _extraSel.Count > 0 && Mathf.Abs(d.y) > 0.0001f;
        Vector3 pivot = group ? SelectionPivot(env) : Vector3.zero;
        Quaternion yaw = Quaternion.Euler(0f, d.y, 0f);
        foreach (var s in AllSelected())
        {
            if (group)
            {
                float[] p = s.isBuilding ? FindBI(env, s.id)?.position : FindOI(env, s.id)?.position;
                if (p != null && p.Length >= 3)
                {
                    float ox = p[0], oz = p[2];
                    OrbitXZ(p, pivot, yaw);
                    // Buildings aren't bounds-snapped, so shift the GO by the world delta here;
                    // objects get fully repositioned by the re-ground inside SetRotOne below.
                    if (s.go) s.go.transform.position += new Vector3(p[0] - ox, 0f, p[2] - oz);
                }
            }
            Vector3 r = GetRotFor(env, s.id, s.isBuilding) + d;
            if (RotSnapHeld)
                for (int a = 0; a < 3; a++)
                    if (Mathf.Abs(d[a]) > 0.0001f) r[a] = Snap(r[a]);
            SetRotOne(env, s, r);
        }
    }

    // XZ centroid of the selected instances' stored positions — the group-rotate pivot. Like
    // EnvPivot but scoped to the selection; invariant under rotation about itself, so
    // recomputing it every emitted step is stable.
    private Vector3 SelectionPivot(EnvironmentDef env)
    {
        Vector3 sum = Vector3.zero; int n = 0;
        foreach (var s in AllSelected())
        {
            float[] p = s.isBuilding ? FindBI(env, s.id)?.position : FindOI(env, s.id)?.position;
            if (p == null || p.Length < 3) continue;
            sum += new Vector3(p[0], 0f, p[2]); n++;
        }
        return n > 0 ? sum / n : Vector3.zero;
    }

    // Sets one euler axis to an absolute value on every selected instance, leaving each one's
    // other two axes untouched — used by the panel sliders (mirrors the tile editor). The value
    // arrives already Shift-snapped from the slider (SnapIf).
    private void ApplyRotAxisAbsolute(EnvironmentDef env, int axis, float degrees)
    {
        foreach (var s in AllSelected())
        {
            Vector3 r = GetRotFor(env, s.id, s.isBuilding);
            r[axis] = degrees;
            SetRotOne(env, s, r);
        }
    }

    // Rotation is free by default; holding Shift snaps applied values to ROT_SNAP_DEG.
    private static bool  RotSnapHeld  => KB != null && (KB.leftShiftKey.isPressed || KB.rightShiftKey.isPressed);
    private static float Snap(float deg)   => Mathf.Round(deg / ROT_SNAP_DEG) * ROT_SNAP_DEG;
    private static float SnapIf(float deg) => RotSnapHeld ? Snap(deg) : deg;

    private void ApplyScaleDelta(EnvironmentDef env, float delta)
    {
        foreach (var s in AllSelected())
        {
            float ns;
            if (s.isBuilding) { var i = FindBI(env, s.id); if (i == null) continue; i.scale = ns = Mathf.Max(0.1f, i.scale + delta); }
            else              { var i = FindOI(env, s.id); if (i == null) continue; i.scale = ns = Mathf.Max(0.1f, i.scale + delta); }
            // baseScale keeps the prefab's authored localScale and prefabScaleFactor;
            // Vector3.one * ns would discard both on the first scale edit.
            if (s.go) s.go.transform.localScale = s.baseScale * ns;
            RegroundOne(env, s.id, s.isBuilding, s.go);
        }
    }

    // Writes the euler to one instance's data and live GO. Objects compose the prefab's authored
    // orientation (see WorldRenderer); buildings carry no base. Objects re-ground afterwards.
    private void SetRotOne(EnvironmentDef env, Sel s, Vector3 r)
    {
        if (s.isBuilding) { var i = FindBI(env, s.id); if (i == null) return; i.rotationX = r.x; i.rotationY = r.y; i.rotationZ = r.z; }
        else              { var i = FindOI(env, s.id); if (i == null) return; i.rotationX = r.x; i.rotationY = r.y; i.rotationZ = r.z; }
        if (s.go) s.go.transform.rotation = Quaternion.Euler(r) * BaseRotationFor(env, s.id, s.isBuilding);
        RegroundOne(env, s.id, s.isBuilding, s.go);
    }

    // Objects are snapped to the terrain by WorldRenderer using their *rotated/scaled* bounds;
    // rotating or scaling in edit mode changes those bounds, so re-run the same grounding to
    // keep the live object matching the saved/reloaded result. No-op for buildings (placed
    // directly at worldPos, not bounds-snapped).
    private void RegroundOne(EnvironmentDef env, string id, bool isBuilding, GameObject go)
    {
        if (isBuilding || go == null || worldRenderer == null) return;
        var inst = FindOI(env, id);
        if (inst != null) worldRenderer.GroundObjectInstance(go, inst.position);
    }

    private void RegroundSelected(EnvironmentDef env) => RegroundOne(env, _selId, _selIsBuilding, _selGO);

    private void RevertTransform()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null) return;
        if (_selIsBuilding)
        {
            var i = FindBI(env, _selId); if (i == null) return;
            i.position = new[] { _transOrigPos.x, _transOrigPos.y, _transOrigPos.z };
            i.rotationX = _transOrigRot.x; i.rotationY = _transOrigRot.y; i.rotationZ = _transOrigRot.z;
            i.scale = _transOrigScale;
        }
        else
        {
            var i = FindOI(env, _selId); if (i == null) return;
            i.position = new[] { _transOrigPos.x, _transOrigPos.y, _transOrigPos.z };
            i.rotationX = _transOrigRot.x; i.rotationY = _transOrigRot.y; i.rotationZ = _transOrigRot.z;
            i.scale = _transOrigScale;
        }
        if (_selGO)
        {
            _selGO.transform.position   = _transOrigPos;
            _selGO.transform.rotation   = Quaternion.Euler(_transOrigRot) * SelectedBaseRotation(env);
            _selGO.transform.localScale = _selBaseScale * _transOrigScale;
            RegroundSelected(env);   // objects: re-snap to terrain for the restored rotation/scale
        }
    }

    // -----------------------------------------------------------------------
    // PlaceObject mode
    // -----------------------------------------------------------------------

    private void StartPlaceObject(string prefabType)
    {
        ExitCurrentMode();
        _placeType = prefabType; _placeRotY = 0f; _placeBaseEuler = Vector3.zero;
        _mode = EditMode.PlaceObject;
        if (prefabRegistry != null)
        {
            var prefab = prefabRegistry.GetPrefab(prefabType);
            if (prefab != null)
            {
                _placeBaseEuler = prefab.transform.localEulerAngles;   // keep the prefab's authored orientation
                _placeGhost = Instantiate(prefab); TintGhost(_placeGhost);
            }
        }
    }

    private void UpdatePlaceObject()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { StopPlaceObject(); return; }

        if (!IsMouseOverUI()) _placeRotY += ScrollY * 15f;   // don't rotate the ghost when scrolling a list
        Vector3 pos = GroundPoint();
        // The scroll yaw is a delta on top of the prefab's authored orientation; compose the
        // ghost the same way WorldRenderer composes the placed instance so preview == result.
        Quaternion rot = Quaternion.Euler(0f, _placeRotY, 0f) * Quaternion.Euler(_placeBaseEuler);
        if (_placeGhost)
        {
            // Rotation first — grounding snaps the *rotated* renderer bounds onto the terrain.
            _placeGhost.transform.rotation = rot;
            if (worldRenderer != null)
                worldRenderer.GroundObjectInstance(_placeGhost, new[] { pos.x, 0f, pos.z });
            else
                _placeGhost.transform.position = pos;
        }

        if (LMBDown && !IsMouseOverUI())
        {
            // Auto-create a working environment if none is loaded, so objects can be placed
            // without first creating one.
            var env = libraryBrowser?.EnsureWorkingEnvironment();
            if (env == null) return;
            _history?.RecordBefore(EditHistory.Scope.Environment, "Place object");
            // Store only the user yaw — the prefab's authored orientation is re-applied at
            // render time, so it must not be baked into the stored rotation (would double up).
            env.objectInstances.Add(new ObjectInstance
            {
                instanceId = Guid.NewGuid().ToString("D"),
                prefabType = _placeType,
                position   = new[] { pos.x, pos.y, pos.z },
                rotationX  = 0f,
                rotationY  = _placeRotY,
                rotationZ  = 0f,
                scale      = 1f,
                included   = true,
            });
            libraryBrowser?.MarkDirty();
            worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        }
    }

    private void StopPlaceObject()
    {
        if (_placeGhost) Destroy(_placeGhost); _placeGhost = null;
        _mode = EditMode.Browse;
    }

    // -----------------------------------------------------------------------
    // PlaceBuilding mode (M4)
    // -----------------------------------------------------------------------

    private void StartPlaceBuilding(string buildingDefId)
    {
        ExitCurrentMode();
        _placeBldgDefId = buildingDefId; _placeBldgRotY = 0f;
        _mode = EditMode.PlaceBuilding;
        _placeBldgGhost = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _placeBldgGhost.name = "BuildingGhost";
        _placeBldgGhost.transform.localScale = GhostBounds(buildingDefId, out _placeBldgGhostCenter);
        Destroy(_placeBldgGhost.GetComponent<BoxCollider>());
        TintGhost(_placeBldgGhost);
    }

    private void UpdatePlaceBuilding()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { StopPlaceBuilding(); return; }

        if (!IsMouseOverUI()) _placeBldgRotY += ScrollY * 15f;   // don't rotate the ghost when scrolling a list

        // §5 PlaceBuilding: ghost preview snaps to the building's grid
        Vector3 pos  = GroundPoint();
        float   cell = PlaceBldgCellSize();
        pos.x = Mathf.Round(pos.x / cell) * cell;
        pos.z = Mathf.Round(pos.z / cell) * cell;

        if (_placeBldgGhost)
        {
            // Tiled buildings have a corner pivot: the instance origin (root) sits at the snapped
            // placement point, and each tile renders at its literal (gridX+0.5,gridZ+0.5)·cs offset
            // from that root (see TileSpawner / WorldRenderer). The tiles need not start at grid
            // (0,0), so the ghost's center is precomputed in that same corner-pivot frame
            // (GhostBounds) and rotated about the root, lining the volume up with the geometry.
            Quaternion rot = Quaternion.Euler(0f, _placeBldgRotY, 0f);
            _placeBldgGhost.transform.rotation = rot;
            // Lift the root to the terrain surface — the renderer places the building at
            // terrain height (RenderBuildingInstances), so the ghost must sit there too.
            float groundY = worldRenderer != null ? worldRenderer.SamplePathSurfaceY(pos.x, pos.z) : 0f;
            _placeBldgGhost.transform.position = new Vector3(pos.x, groundY, pos.z) + rot * _placeBldgGhostCenter;
        }

        if (LMBDown && !IsMouseOverUI())
        {
            // Auto-create a working environment if none is loaded, so buildings can be placed
            // without first creating one.
            var env = libraryBrowser?.EnsureWorkingEnvironment();
            if (env == null) return;
            _history?.RecordBefore(EditHistory.Scope.Environment, "Place building");
            env.buildingInstances.Add(new BuildingInstance
            {
                instanceId = Guid.NewGuid().ToString("D"),
                buildingId = _placeBldgDefId,
                position   = new[] { pos.x, pos.y, pos.z },
                rotationY  = _placeBldgRotY,
                scale      = 1f,
                included   = true,
            });
            libraryBrowser?.MarkDirty();
            worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        }
    }

    private void StopPlaceBuilding()
    {
        if (_placeBldgGhost) Destroy(_placeBldgGhost); _placeBldgGhost = null;
        _mode = EditMode.Browse;
    }

    // -----------------------------------------------------------------------
    // Terrain editor: DrawPath / PaintObjects / PaintSurface
    // Shared raycasting via GroundPoint(); the gizmo is suppressed in these modes (see Update).
    // -----------------------------------------------------------------------

    // ---- DrawPath: freehand stroke or click-to-add polyline, rendered as a PathDef ribbon ----

    private void StartDrawPath()
    {
        ExitCurrentMode();
        _mode = EditMode.DrawPath;
        _pathPts.Clear();
        _pathStroking = false;
        // Default the selected material to a real palette entry — never a hard-coded id that may
        // not exist in the user's PathMaterialPalette (which would render as a missing material).
        if (pathMaterialPalette?.entries != null && pathMaterialPalette.entries.Count > 0 &&
            !pathMaterialPalette.entries.Exists(e => string.Equals(e.id, _pathMaterial, StringComparison.OrdinalIgnoreCase)))
            _pathMaterial = pathMaterialPalette.entries[0].id;
        HidePathPreview();
    }

    private void UpdateDrawPath()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { CancelPath(); return; }

        // Snap the cursor onto a nearby existing path (anywhere along it) so paths connect cleanly.
        Vector3 cursor = SnapToPath(GroundPoint());

        if (_pathFreehand)
        {
            if (LMBDown && !IsMouseOverUI()) { _pathStroking = true; _pathPts.Clear(); _pathPts.Add(cursor); }
            if (_pathStroking && LMBHeld &&
                (_pathPts.Count == 0 || Vector3.Distance(cursor, _pathPts[_pathPts.Count - 1]) > PATH_FREEHAND_STEP))
                _pathPts.Add(cursor);
            if (_pathStroking && LMBUp) { _pathStroking = false; FinishPath(); return; }
        }
        else
        {
            if (LMBDown && !IsMouseOverUI())
            {
                float now = Time.time;
                if (_pathPts.Count >= 2 && now - _pathLastClickTime < DOUBLE_CLICK_INTERVAL) { FinishPath(); return; }
                _pathLastClickTime = now;
                _pathPts.Add(cursor);
            }
            // Backspace removes the last placed waypoint (polyline mode).
            if (KB != null && !TypingInUI && KB.backspaceKey.wasPressedThisFrame && _pathPts.Count > 0)
                _pathPts.RemoveAt(_pathPts.Count - 1);
            if (KB != null && !TypingInUI && (KB.enterKey.wasPressedThisFrame || KB.numpadEnterKey.wasPressedThisFrame)) { FinishPath(); return; }
        }

        UpdatePathPreview(cursor);
    }

    // Rebuild the live preview as the actual smoothed, terrain-draped ribbon at true width, so the
    // path looks while drawing exactly as it will once committed (WYSIWYG).
    private void UpdatePathPreview(Vector3 cursor)
    {
        var ctrl = new List<Vector2>(_pathPts.Count + 1);
        foreach (var p in _pathPts) ctrl.Add(new Vector2(p.x, p.z));
        bool rubberBand = (!_pathFreehand && _pathPts.Count >= 1) || (_pathFreehand && _pathStroking);
        if (rubberBand) ctrl.Add(new Vector2(cursor.x, cursor.z));
        ShowRibbonPreview(ctrl);
    }

    // Build the live preview ribbon from a control-point set, smoothed/draped with the current
    // width/smoothing/material. Shared by draw mode and edit mode.
    private void ShowRibbonPreview(List<Vector2> ctrl)
    {
        if (ctrl == null || ctrl.Count < 2) { HidePathPreview(); return; }
        // Match the committed render: round sharp corners (radius = half-width) before smoothing.
        var rounded = PathGeometry.RoundCorners(ctrl, _pathWidth * 0.5f);
        var dense = PathGeometry.Smooth(rounded, _pathSmoothing);
        var centerline = new List<Vector3>(dense.Count);
        foreach (var d in dense) centerline.Add(new Vector3(d.x, PreviewY(d.x, d.y), d.y));
        Mesh mesh = PathMesh.Build(centerline, _pathWidth, PreviewY);
        if (mesh == null) { HidePathPreview(); return; }

        EnsurePathPreviewObjects();
        _pathPreviewFilter.mesh = mesh;
        var mat = worldRenderer != null ? worldRenderer.GetPathMaterial(_pathMaterial) : null;
        _pathPreviewRenderer.sharedMaterial = mat != null ? mat : _pathPreviewFallbackMat;
        _pathPreviewGO.SetActive(true);
    }

    private float PreviewY(float x, float z) =>
        worldRenderer != null ? worldRenderer.SamplePathSurfaceY(x, z) : 0.05f;

    private void EnsurePathPreviewObjects()
    {
        if (_pathPreviewGO != null) return;
        _pathPreviewGO = new GameObject("PathPreview");
        _pathPreviewFilter = _pathPreviewGO.AddComponent<MeshFilter>();
        _pathPreviewRenderer = _pathPreviewGO.AddComponent<MeshRenderer>();
        _pathPreviewRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _pathPreviewRenderer.receiveShadows = false;
        if (_pathPreviewFallbackMat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
            _pathPreviewFallbackMat = new Material(sh) { color = new Color(0.30f, 0.78f, 1f, 1f) };
        }
    }

    private void HidePathPreview()
    {
        if (_pathPreviewGO != null) _pathPreviewGO.SetActive(false);
    }

    // Snap to the nearest point ON any existing path's centerline (projected onto its segments, so
    // a new path can T-join mid-span — not just at endpoints) when within PATH_SNAP_DIST. Pass
    // `excludeId` so a path being edited can't snap one of its own control points onto itself.
    private Vector3 SnapToPath(Vector3 cursor, string excludeId = null)
    {
        var paths = libraryBrowser?.CurrentEnvironment?.site?.paths;
        if (paths == null) return cursor;
        Vector2 c = new Vector2(cursor.x, cursor.z);
        float best = PATH_SNAP_DIST * PATH_SNAP_DIST; Vector2 snap = c; bool found = false;
        foreach (var p in paths)
        {
            if (p?.points == null || p.points.Length < 1) continue;
            if (excludeId != null && p.id == excludeId) continue;
            // Single-point path: snap to it directly; otherwise project onto each segment.
            if (p.points.Length == 1)
            {
                var e = p.points[0];
                if (e == null || e.Length < 2) continue;
                float d2 = (new Vector2(e[0], e[1]) - c).sqrMagnitude;
                if (d2 < best) { best = d2; snap = new Vector2(e[0], e[1]); found = true; }
                continue;
            }
            for (int i = 0; i < p.points.Length - 1; i++)
            {
                var a = p.points[i]; var b = p.points[i + 1];
                if (a == null || a.Length < 2 || b == null || b.Length < 2) continue;
                Vector2 proj = ClosestPointOnSegment(c, new Vector2(a[0], a[1]), new Vector2(b[0], b[1]));
                float d2 = (proj - c).sqrMagnitude;
                if (d2 < best) { best = d2; snap = proj; found = true; }
            }
        }
        return found ? new Vector3(snap.x, cursor.y, snap.y) : cursor;
    }

    // Closest point on segment a->b to p (projection clamped to the segment).
    private static Vector2 ClosestPointOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-9f) return a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return a + t * ab;
    }

    private void FinishPath()
    {
        // Denoise freehand strokes into a compact control-point set; the renderer re-smooths them.
        var raw = new List<Vector2>(_pathPts.Count);
        foreach (var p in _pathPts) raw.Add(new Vector2(p.x, p.z));
        var ctrl = _pathFreehand ? PathGeometry.Simplify(raw, PATH_SIMPLIFY_TOL) : raw;

        if (ctrl.Count >= 2)
        {
            var env = libraryBrowser?.EnsureWorkingEnvironment();
            if (env?.site != null)
            {
                _history?.RecordBefore(EditHistory.Scope.Environment, "Draw path");
                env.site.paths ??= new List<PathDef>();
                var pts = new float[ctrl.Count][];
                for (int i = 0; i < ctrl.Count; i++) pts[i] = new[] { ctrl[i].x, ctrl[i].y };
                env.site.paths.Add(new PathDef
                {
                    id        = Guid.NewGuid().ToString("D"),
                    material  = _pathMaterial,
                    width     = _pathWidth,
                    points    = pts,
                    smoothing = _pathSmoothing,
                });
                libraryBrowser?.MarkDirty();
                worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
            }
        }
        _pathPts.Clear();
        HidePathPreview();
    }

    private void CancelPath() { _pathPts.Clear(); StopDrawPath(); }

    private void StopDrawPath()
    {
        if (_pathPreviewGO != null) { Destroy(_pathPreviewGO); _pathPreviewGO = null; _pathPreviewFilter = null; _pathPreviewRenderer = null; }
        _pathPts.Clear();
        _mode = EditMode.Browse;
    }

    private void DeletePath(string pathId)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env?.site?.paths == null) return;
        if (_mode == EditMode.EditPath && pathId == _pathEditId) StopEditPath();
        _history?.RecordBefore(EditHistory.Scope.Environment, "Delete path");
        env.site.paths.RemoveAll(p => p != null && p.id == pathId);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    // ---- EditPath: reshape an existing path's control points + props in place ----

    private PathDef CurrentEditPath()
    {
        var paths = libraryBrowser?.CurrentEnvironment?.site?.paths;
        if (paths == null || _pathEditId == null) return null;
        return paths.Find(p => p != null && p.id == _pathEditId);
    }

    private void StartEditPath(string id)
    {
        ExitCurrentMode();
        var paths = libraryBrowser?.CurrentEnvironment?.site?.paths;
        var path = paths?.Find(p => p != null && p.id == id);
        if (path == null) { _mode = EditMode.Browse; return; }

        _mode = EditMode.EditPath;
        _pathEditId = id;
        _pathEditSel = -1;
        _pathEditDragging = false;
        // Adopt the path's props so the shared width/smoothing/material controls edit THIS path.
        _pathWidth     = path.width > 0f ? path.width : 1.5f;
        _pathSmoothing = Mathf.Clamp01(path.smoothing);
        _pathMaterial  = path.material;
        _pathEditPts.Clear();
        if (path.points != null)
            foreach (var p in path.points)
                if (p != null && p.Length >= 2) _pathEditPts.Add(new Vector3(p[0], 0f, p[1]));
        RebuildPathHandles();
        // The live preview is the visible truth while editing; hide the committed ribbon so the two
        // coincident meshes don't z-fight (and so a mid-drag stale shape isn't shown underneath).
        SetCommittedPathVisible(id, false);
    }

    private void UpdateEditPath()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { StopEditPath(); return; }
        var path = CurrentEditPath();
        if (path == null) { StopEditPath(); return; }

        // Delete the selected control point (keep at least 2).
        if (KB != null && !TypingInUI && KB.deleteKey.wasPressedThisFrame &&
            _pathEditSel >= 0 && _pathEditSel < _pathEditPts.Count && _pathEditPts.Count > 2)
        {
            _history?.RecordBefore(EditHistory.Scope.Environment, "Edit path");
            _pathEditPts.RemoveAt(_pathEditSel);
            _pathEditSel = -1;
            CommitEditPath(reRender: false);
            RebuildPathHandles();
        }

        if (LMBDown && !IsMouseOverUI())
        {
            int hit = PickHandle();
            if (hit >= 0)
            {
                _pathEditSel = hit;
                _pathEditDragging = true;
                // Snapshot the pre-drag state once; the central EndGesture closes it on mouse up.
                _history?.BeginGesture(EditHistory.Scope.Environment, "Edit path");
            }
            else if (TryInsertPointOnPath(GroundPoint()))
            {
                _history?.RecordBefore(EditHistory.Scope.Environment, "Edit path");
                CommitEditPath(reRender: false);
                RebuildPathHandles();
            }
        }
        if (_pathEditDragging && LMBHeld && _pathEditSel >= 0 && _pathEditSel < _pathEditPts.Count)
        {
            Vector3 g = SnapToPath(GroundPoint(), excludeId: _pathEditId);
            _pathEditPts[_pathEditSel] = new Vector3(g.x, 0f, g.z);
        }
        if (LMBUp && _pathEditDragging)
        {
            _pathEditDragging = false;
            CommitEditPath(reRender: false);   // data only; the preview already shows the new shape
        }

        // Live preview + handle positions track the working points every frame.
        var ctrl = new List<Vector2>(_pathEditPts.Count);
        foreach (var p in _pathEditPts) ctrl.Add(new Vector2(p.x, p.z));
        ShowRibbonPreview(ctrl);
        UpdateHandlePositions();
    }

    // Write the working control points + current props back into the PathDef and persist/re-render.
    private void CommitEditPath(bool reRender = true)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        var path = CurrentEditPath();
        if (env == null || path == null || _pathEditPts.Count < 2) return;
        var pts = new float[_pathEditPts.Count][];
        for (int i = 0; i < _pathEditPts.Count; i++) pts[i] = new[] { _pathEditPts[i].x, _pathEditPts[i].z };
        path.points    = pts;
        path.width     = _pathWidth;
        path.smoothing = _pathSmoothing;
        path.material  = _pathMaterial;
        libraryBrowser?.MarkDirty();
        if (reRender) worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    // Insert a control point where the user clicked, into the nearest segment of the control polyline,
    // when the click lands reasonably close to the path. Returns true if a point was inserted.
    private bool TryInsertPointOnPath(Vector3 ground)
    {
        if (_pathEditPts.Count < 2) return false;
        var click = new Vector2(ground.x, ground.z);
        float bestD = float.MaxValue; int bestSeg = -1; Vector2 bestPt = click;
        for (int i = 0; i < _pathEditPts.Count - 1; i++)
        {
            Vector2 a = new(_pathEditPts[i].x, _pathEditPts[i].z);
            Vector2 b = new(_pathEditPts[i + 1].x, _pathEditPts[i + 1].z);
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(click - a, ab) / len2);
            Vector2 proj = a + t * ab;
            float d = Vector2.Distance(click, proj);
            if (d < bestD) { bestD = d; bestSeg = i; bestPt = proj; }
        }
        float tol = Mathf.Max(_pathWidth, 2f);
        if (bestSeg < 0 || bestD > tol) return false;
        _pathEditPts.Insert(bestSeg + 1, new Vector3(bestPt.x, 0f, bestPt.y));
        _pathEditSel = bestSeg + 1;
        return true;
    }

    private void InsertPointLongestSegment()
    {
        if (_pathEditPts.Count < 2) return;
        int seg = 0; float best = -1f;
        for (int i = 0; i < _pathEditPts.Count - 1; i++)
        {
            float d = Vector3.Distance(_pathEditPts[i], _pathEditPts[i + 1]);
            if (d > best) { best = d; seg = i; }
        }
        Vector3 mid = (_pathEditPts[seg] + _pathEditPts[seg + 1]) * 0.5f;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Edit path");
        _pathEditPts.Insert(seg + 1, mid);
        _pathEditSel = seg + 1;
        CommitEditPath(reRender: false);
        RebuildPathHandles();
    }

    // Toggle visibility of the committed path ribbon GameObject(s) with this id.
    private void SetCommittedPathVisible(string id, bool visible)
    {
        if (string.IsNullOrEmpty(id)) return;
        foreach (var pm in FindObjectsByType<PathMarker>(FindObjectsSortMode.None))
            if (pm != null && pm.pathId == id) pm.gameObject.SetActive(visible);
    }

    // Index of the control-point handle nearest the cursor in screen space (within the pick radius).
    private int PickHandle()
    {
        if (mainCamera == null) return -1;
        Vector2 m = new(MousePos.x, MousePos.y);
        float best = PATH_HANDLE_PICK_PX * PATH_HANDLE_PICK_PX; int idx = -1;
        for (int i = 0; i < _pathEditPts.Count; i++)
        {
            Vector3 sp = mainCamera.WorldToScreenPoint(HandleWorldPos(i));
            if (sp.z < 0f) continue;
            float d2 = (new Vector2(sp.x, sp.y) - m).sqrMagnitude;
            if (d2 < best) { best = d2; idx = i; }
        }
        return idx;
    }

    private Vector3 HandleWorldPos(int i)
    {
        Vector3 p = _pathEditPts[i];
        return new Vector3(p.x, PreviewY(p.x, p.z) + 0.15f, p.z);
    }

    private void RebuildPathHandles()
    {
        foreach (var h in _pathHandles) if (h != null) Destroy(h);
        _pathHandles.Clear();
        EnsureHandleMaterials();
        for (int i = 0; i < _pathEditPts.Count; i++)
        {
            var h = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            h.name = "PathHandle";
            var col = h.GetComponent<Collider>(); if (col != null) Destroy(col);  // pick by screen-space, not raycast
            h.transform.localScale = Vector3.one * 0.7f;
            _pathHandles.Add(h);
        }
        UpdateHandlePositions();
    }

    private void UpdateHandlePositions()
    {
        if (_pathHandles.Count != _pathEditPts.Count) { RebuildPathHandles(); return; }
        for (int i = 0; i < _pathHandles.Count; i++)
        {
            if (_pathHandles[i] == null) continue;
            _pathHandles[i].transform.position = HandleWorldPos(i);
            var r = _pathHandles[i].GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = i == _pathEditSel ? _pathHandleSelMat : _pathHandleMat;
        }
    }

    private void EnsureHandleMaterials()
    {
        if (_pathHandleMat != null) return;
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
        _pathHandleMat    = new Material(sh) { color = new Color(0.30f, 0.78f, 1f, 1f) };
        _pathHandleSelMat = new Material(sh) { color = new Color(1f, 0.85f, 0.20f, 1f) };
    }

    private void StopEditPath()
    {
        foreach (var h in _pathHandles) if (h != null) Destroy(h);
        _pathHandles.Clear();
        HidePathPreview();
        _pathEditId = null;
        _pathEditSel = -1;
        _pathEditDragging = false;
        _pathEditPts.Clear();
        _mode = EditMode.Browse;
        // Re-render so the committed ribbon returns (fresh from the edited PathDef) and unhides.
        var env = libraryBrowser?.CurrentEnvironment;
        if (env != null) worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    // -----------------------------------------------------------------------
    // DrawFence / EditFence — mirror the path tools: collect a centerline (straight clicks or freehand
    // drag), commit a FenceDef, and let WorldRenderer.RenderFences repeat panel/post prefabs along it.
    // The live preview is a draped LineRenderer of the centerline (panels appear once committed).
    // -----------------------------------------------------------------------

    // Quiet palette lookup (no error log) for per-frame preview / metrics use.
    private FencePalette.Entry FindFenceEntry(string type)
    {
        if (fencePalette?.entries == null) return null;
        foreach (var e in fencePalette.entries)
            if (e != null && string.Equals(e.fenceType, type, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    private float FencePanelLength() { var e = FindFenceEntry(_fenceType); return e != null && e.panelLength > 0f ? e.panelLength : 2f; }

    private void StartDrawFence()
    {
        ExitCurrentMode();
        _mode = EditMode.DrawFence;
        _fencePts.Clear();
        _fenceStroking = false;
        // Default the selected type to a real palette entry rather than a hard-coded id that may not
        // exist in the user's FencePalette (which would render nothing).
        if (fencePalette?.entries != null && fencePalette.entries.Count > 0 && FindFenceEntry(_fenceType) == null)
            _fenceType = fencePalette.entries[0].fenceType;
        // Adopt the type's default height so the height slider starts at a sensible value.
        var e = FindFenceEntry(_fenceType);
        if (e != null && _fenceHeight <= 0f) _fenceHeight = e.height;
        HideFencePreview();
    }

    private void UpdateDrawFence()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { CancelFence(); return; }

        Vector3 cursor = SnapToFenceEndpoint(GroundPoint());

        if (_fenceFreehand)
        {
            if (LMBDown && !IsMouseOverUI()) { _fenceStroking = true; _fencePts.Clear(); _fencePts.Add(cursor); }
            if (_fenceStroking && LMBHeld &&
                (_fencePts.Count == 0 || Vector3.Distance(cursor, _fencePts[_fencePts.Count - 1]) > PATH_FREEHAND_STEP))
                _fencePts.Add(cursor);
            if (_fenceStroking && LMBUp) { _fenceStroking = false; FinishFence(); return; }
        }
        else
        {
            if (LMBDown && !IsMouseOverUI())
            {
                float now = Time.time;
                if (_fencePts.Count >= 2 && now - _fenceLastClickTime < DOUBLE_CLICK_INTERVAL) { FinishFence(); return; }
                _fenceLastClickTime = now;
                _fencePts.Add(cursor);
            }
            if (KB != null && !TypingInUI && KB.backspaceKey.wasPressedThisFrame && _fencePts.Count > 0)
                _fencePts.RemoveAt(_fencePts.Count - 1);
            if (KB != null && !TypingInUI && (KB.enterKey.wasPressedThisFrame || KB.numpadEnterKey.wasPressedThisFrame)) { FinishFence(); return; }
        }

        var ctrl = new List<Vector2>(_fencePts.Count + 1);
        foreach (var p in _fencePts) ctrl.Add(new Vector2(p.x, p.z));
        bool rubberBand = (!_fenceFreehand && _fencePts.Count >= 1) || (_fenceFreehand && _fenceStroking);
        if (rubberBand) ctrl.Add(new Vector2(cursor.x, cursor.z));
        ShowFencePreview(ctrl);
    }

    // Draped centerline guide. Cheap (a single LineRenderer), unlike instantiating the whole fence
    // each frame; the full panels/posts render on commit.
    private void ShowFencePreview(List<Vector2> ctrl)
    {
        if (ctrl == null || ctrl.Count < 2) { HideFencePreview(); return; }
        var dense = PathGeometry.Smooth(ctrl, _fenceSmoothing, FencePanelLength());
        if (dense.Count < 2) { HideFencePreview(); return; }

        EnsureFencePreviewLine();
        _fencePreviewLine.positionCount = dense.Count;
        for (int i = 0; i < dense.Count; i++)
            _fencePreviewLine.SetPosition(i, new Vector3(dense[i].x, PreviewY(dense[i].x, dense[i].y) + 0.1f, dense[i].y));
        _fencePreviewLine.gameObject.SetActive(true);
    }

    private void EnsureFencePreviewLine()
    {
        if (_fencePreviewLine != null) return;
        var go = new GameObject("FencePreview");
        _fencePreviewLine = go.AddComponent<LineRenderer>();
        _fencePreviewLine.widthMultiplier = 0.15f;
        _fencePreviewLine.numCornerVertices = 2;
        _fencePreviewLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _fencePreviewLine.receiveShadows = false;
        if (_fencePreviewMat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            _fencePreviewMat = new Material(sh) { color = new Color(0.95f, 0.75f, 0.20f, 1f) };
        }
        _fencePreviewLine.material = _fencePreviewMat;
        _fencePreviewLine.startColor = _fencePreviewLine.endColor = new Color(0.95f, 0.75f, 0.20f, 1f);
    }

    private void HideFencePreview() { if (_fencePreviewLine != null) _fencePreviewLine.gameObject.SetActive(false); }

    // Snap to the nearest first/last control point of an existing fence when within PATH_SNAP_DIST.
    private Vector3 SnapToFenceEndpoint(Vector3 cursor)
    {
        var fences = libraryBrowser?.CurrentEnvironment?.site?.fences;
        if (fences == null) return cursor;
        float best = PATH_SNAP_DIST * PATH_SNAP_DIST; Vector3 snap = cursor; bool found = false;
        foreach (var f in fences)
        {
            if (f?.points == null || f.points.Length < 1) continue;
            foreach (int idx in new[] { 0, f.points.Length - 1 })
            {
                var e = f.points[idx];
                if (e == null || e.Length < 2) continue;
                float dx = e[0] - cursor.x, dz = e[1] - cursor.z;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) { best = d2; snap = new Vector3(e[0], cursor.y, e[1]); found = true; }
            }
        }
        return found ? snap : cursor;
    }

    private void FinishFence()
    {
        var raw = new List<Vector2>(_fencePts.Count);
        foreach (var p in _fencePts) raw.Add(new Vector2(p.x, p.z));
        var ctrl = _fenceFreehand ? PathGeometry.Simplify(raw, PATH_SIMPLIFY_TOL) : raw;

        if (ctrl.Count >= 2)
        {
            var env = libraryBrowser?.EnsureWorkingEnvironment();
            if (env?.site != null)
            {
                _history?.RecordBefore(EditHistory.Scope.Environment, "Draw fence");
                env.site.fences ??= new List<FenceDef>();
                var pts = new float[ctrl.Count][];
                for (int i = 0; i < ctrl.Count; i++) pts[i] = new[] { ctrl[i].x, ctrl[i].y };
                env.site.fences.Add(new FenceDef
                {
                    id        = Guid.NewGuid().ToString("D"),
                    fenceType = _fenceType,
                    points    = pts,
                    smoothing = _fenceSmoothing,
                    height    = _fenceHeight,
                });
                libraryBrowser?.MarkDirty();
                worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
            }
        }
        _fencePts.Clear();
        HideFencePreview();
    }

    private void CancelFence() { _fencePts.Clear(); StopDrawFence(); }

    private void StopDrawFence()
    {
        if (_fencePreviewLine != null) { Destroy(_fencePreviewLine.gameObject); _fencePreviewLine = null; }
        _fencePts.Clear();
        _mode = EditMode.Browse;
    }

    private void DeleteFence(string fenceId)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env?.site?.fences == null) return;
        if (_mode == EditMode.EditFence && fenceId == _fenceEditId) StopEditFence();
        _history?.RecordBefore(EditHistory.Scope.Environment, "Delete fence");
        env.site.fences.RemoveAll(f => f != null && f.id == fenceId);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    // ---- EditFence: reshape an existing fence's control points in place ----

    private FenceDef CurrentEditFence()
    {
        var fences = libraryBrowser?.CurrentEnvironment?.site?.fences;
        if (fences == null || _fenceEditId == null) return null;
        return fences.Find(f => f != null && f.id == _fenceEditId);
    }

    private void StartEditFence(string id)
    {
        ExitCurrentMode();
        var fences = libraryBrowser?.CurrentEnvironment?.site?.fences;
        var fence = fences?.Find(f => f != null && f.id == id);
        if (fence == null) { _mode = EditMode.Browse; return; }

        _mode = EditMode.EditFence;
        _fenceEditId = id;
        _fenceEditSel = -1;
        _fenceEditDragging = false;
        _fenceType      = fence.fenceType;
        _fenceSmoothing = Mathf.Clamp01(fence.smoothing);
        _fenceHeight    = fence.height;
        _fenceEditPts.Clear();
        if (fence.points != null)
            foreach (var p in fence.points)
                if (p != null && p.Length >= 2) _fenceEditPts.Add(new Vector3(p[0], 0f, p[1]));
        RebuildFenceHandles();
        // Hide the committed fence segments so the live preview is the single visible truth while editing.
        SetCommittedFenceVisible(id, false);
    }

    private void UpdateEditFence()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { StopEditFence(); return; }
        var fence = CurrentEditFence();
        if (fence == null) { StopEditFence(); return; }

        if (KB != null && !TypingInUI && KB.deleteKey.wasPressedThisFrame &&
            _fenceEditSel >= 0 && _fenceEditSel < _fenceEditPts.Count && _fenceEditPts.Count > 2)
        {
            _history?.RecordBefore(EditHistory.Scope.Environment, "Edit fence");
            _fenceEditPts.RemoveAt(_fenceEditSel);
            _fenceEditSel = -1;
            CommitEditFence(reRender: false);
            RebuildFenceHandles();
        }

        if (LMBDown && !IsMouseOverUI())
        {
            int hit = PickFenceHandle();
            if (hit >= 0)
            {
                _fenceEditSel = hit;
                _fenceEditDragging = true;
                _history?.BeginGesture(EditHistory.Scope.Environment, "Edit fence");
            }
            else if (TryInsertPointOnFence(GroundPoint()))
            {
                _history?.RecordBefore(EditHistory.Scope.Environment, "Edit fence");
                CommitEditFence(reRender: false);
                RebuildFenceHandles();
            }
        }
        if (_fenceEditDragging && LMBHeld && _fenceEditSel >= 0 && _fenceEditSel < _fenceEditPts.Count)
        {
            Vector3 g = SnapToFenceEndpoint(GroundPoint());
            _fenceEditPts[_fenceEditSel] = new Vector3(g.x, 0f, g.z);
        }
        if (LMBUp && _fenceEditDragging)
        {
            _fenceEditDragging = false;
            CommitEditFence(reRender: false);
        }

        var ctrl = new List<Vector2>(_fenceEditPts.Count);
        foreach (var p in _fenceEditPts) ctrl.Add(new Vector2(p.x, p.z));
        ShowFencePreview(ctrl);
        UpdateFenceHandlePositions();
    }

    private void CommitEditFence(bool reRender = true)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        var fence = CurrentEditFence();
        if (env == null || fence == null || _fenceEditPts.Count < 2) return;
        var pts = new float[_fenceEditPts.Count][];
        for (int i = 0; i < _fenceEditPts.Count; i++) pts[i] = new[] { _fenceEditPts[i].x, _fenceEditPts[i].z };
        fence.points    = pts;
        fence.fenceType = _fenceType;
        fence.smoothing = _fenceSmoothing;
        fence.height    = _fenceHeight;
        libraryBrowser?.MarkDirty();
        if (reRender) worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    private bool TryInsertPointOnFence(Vector3 ground)
    {
        if (_fenceEditPts.Count < 2) return false;
        var click = new Vector2(ground.x, ground.z);
        float bestD = float.MaxValue; int bestSeg = -1; Vector2 bestPt = click;
        for (int i = 0; i < _fenceEditPts.Count - 1; i++)
        {
            Vector2 a = new(_fenceEditPts[i].x, _fenceEditPts[i].z);
            Vector2 b = new(_fenceEditPts[i + 1].x, _fenceEditPts[i + 1].z);
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(click - a, ab) / len2);
            Vector2 proj = a + t * ab;
            float d = Vector2.Distance(click, proj);
            if (d < bestD) { bestD = d; bestSeg = i; bestPt = proj; }
        }
        float tol = Mathf.Max(FencePanelLength(), 2f);
        if (bestSeg < 0 || bestD > tol) return false;
        _fenceEditPts.Insert(bestSeg + 1, new Vector3(bestPt.x, 0f, bestPt.y));
        _fenceEditSel = bestSeg + 1;
        return true;
    }

    private void InsertPointLongestSegmentFence()
    {
        if (_fenceEditPts.Count < 2) return;
        int seg = 0; float best = -1f;
        for (int i = 0; i < _fenceEditPts.Count - 1; i++)
        {
            float d = Vector3.Distance(_fenceEditPts[i], _fenceEditPts[i + 1]);
            if (d > best) { best = d; seg = i; }
        }
        Vector3 mid = (_fenceEditPts[seg] + _fenceEditPts[seg + 1]) * 0.5f;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Edit fence");
        _fenceEditPts.Insert(seg + 1, mid);
        _fenceEditSel = seg + 1;
        CommitEditFence(reRender: false);
        RebuildFenceHandles();
    }

    private void SetCommittedFenceVisible(string id, bool visible)
    {
        if (string.IsNullOrEmpty(id)) return;
        foreach (var fm in FindObjectsByType<FenceMarker>(FindObjectsSortMode.None))
            if (fm != null && fm.fenceId == id) fm.gameObject.SetActive(visible);
    }

    private int PickFenceHandle()
    {
        if (mainCamera == null) return -1;
        Vector2 m = new(MousePos.x, MousePos.y);
        float best = PATH_HANDLE_PICK_PX * PATH_HANDLE_PICK_PX; int idx = -1;
        for (int i = 0; i < _fenceEditPts.Count; i++)
        {
            Vector3 sp = mainCamera.WorldToScreenPoint(FenceHandleWorldPos(i));
            if (sp.z < 0f) continue;
            float d2 = (new Vector2(sp.x, sp.y) - m).sqrMagnitude;
            if (d2 < best) { best = d2; idx = i; }
        }
        return idx;
    }

    private Vector3 FenceHandleWorldPos(int i)
    {
        Vector3 p = _fenceEditPts[i];
        return new Vector3(p.x, PreviewY(p.x, p.z) + 0.15f, p.z);
    }

    private void RebuildFenceHandles()
    {
        foreach (var h in _fenceHandles) if (h != null) Destroy(h);
        _fenceHandles.Clear();
        EnsureHandleMaterials();
        for (int i = 0; i < _fenceEditPts.Count; i++)
        {
            var h = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            h.name = "FenceHandle";
            var col = h.GetComponent<Collider>(); if (col != null) Destroy(col);
            h.transform.localScale = Vector3.one * 0.7f;
            _fenceHandles.Add(h);
        }
        UpdateFenceHandlePositions();
    }

    private void UpdateFenceHandlePositions()
    {
        if (_fenceHandles.Count != _fenceEditPts.Count) { RebuildFenceHandles(); return; }
        for (int i = 0; i < _fenceHandles.Count; i++)
        {
            if (_fenceHandles[i] == null) continue;
            _fenceHandles[i].transform.position = FenceHandleWorldPos(i);
            var r = _fenceHandles[i].GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = i == _fenceEditSel ? _pathHandleSelMat : _pathHandleMat;
        }
    }

    private void StopEditFence()
    {
        foreach (var h in _fenceHandles) if (h != null) Destroy(h);
        _fenceHandles.Clear();
        HideFencePreview();
        _fenceEditId = null;
        _fenceEditSel = -1;
        _fenceEditDragging = false;
        _fenceEditPts.Clear();
        _mode = EditMode.Browse;
        var env = libraryBrowser?.CurrentEnvironment;
        if (env != null) worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    // ---- EditLot: resize the terrain rectangle / reshape the parcel polygon with in-scene handles ----
    //
    // Two sub-modes share one tool. Rectangle mode shows three handles on the far corner & far edges
    // (the lot is anchored at world origin, like the Terrain) and drives site.terrainSize; the resize
    // honors the keep-in-place vs scale-content selector. Polygon mode drags/inserts/deletes vertices
    // of site.lotBoundary (the parcel that PaintTerrain masks the water against). A draped LineRenderer
    // preview is the live truth while editing; the committed "Lot frame" is hidden until commit.

    private void StartEditLot()
    {
        ExitCurrentMode();
        var env = libraryBrowser?.CurrentEnvironment;
        if (env?.site == null) { _mode = EditMode.Browse; return; }
        _mode = EditMode.EditLot;
        _lotSel = -1; _lotDragging = false; _lotMoved = false;
        // Default to whichever shape the env already has: a real parcel polygon, else the rectangle.
        _lotPolygonMode = env.site.lotBoundary != null && env.site.lotBoundary.Length >= 3;
        SeedLotWorking(env);
        EnsureHandleMaterials();
        EnsureLotPreview();
        SetLotFrameVisible(false);
        RebuildLotHandles();
    }

    private void SeedLotWorking(EnvironmentDef env)
    {
        _lotPts.Clear();
        var ts = env.site.terrainSize;
        _lotW = ts != null && ts.Length > 0 && ts[0] > 0f ? ts[0] : 100f;
        _lotL = ts != null && ts.Length > 1 && ts[1] > 0f ? ts[1] : 100f;
        if (_lotPolygonMode)
        {
            var poly = env.site.lotBoundary;
            if (poly != null)
                foreach (var p in poly)
                    if (p != null && p.Length >= 2) _lotPts.Add(new Vector3(p[0], 0f, p[1]));
            if (_lotPts.Count < 3) _lotPolygonMode = false;   // degenerate boundary ⇒ fall back to rect
        }
    }

    private void UpdateEditLot()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env?.site == null) { StopEditLot(); return; }
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { StopEditLot(); return; }

        // Polygon: delete the selected vertex (keep at least a triangle).
        if (_lotPolygonMode && KB != null && !TypingInUI && KB.deleteKey.wasPressedThisFrame &&
            _lotSel >= 0 && _lotSel < _lotPts.Count && _lotPts.Count > 3)
        {
            _history?.RecordBefore(EditHistory.Scope.Environment, "Edit lot");
            _lotPts.RemoveAt(_lotSel); _lotSel = -1;
            CommitLotPolygon(env);
            RebuildLotHandles();
        }

        if (LMBDown && !IsMouseOverUI())
        {
            int hit = PickLotHandle();
            if (hit >= 0) { _lotSel = hit; _lotDragging = true; _lotMoved = false; }
            else if (_lotPolygonMode && TryInsertLotVertex(GroundPoint()))
            {
                _history?.RecordBefore(EditHistory.Scope.Environment, "Edit lot");
                CommitLotPolygon(env);
                RebuildLotHandles();
            }
        }

        if (_lotDragging && LMBHeld && _lotSel >= 0)
        {
            Vector3 g = GroundPoint();
            if (_lotPolygonMode)
            {
                if (_lotSel < _lotPts.Count)
                {
                    var nv = new Vector3(g.x, 0f, g.z);
                    if ((nv - _lotPts[_lotSel]).sqrMagnitude > 1e-6f) _lotMoved = true;
                    _lotPts[_lotSel] = nv;
                }
            }
            else
            {
                float nw = _lotW, nl = _lotL;
                if      (_lotSel == 0) { nw = g.x; nl = g.z; }   // far corner
                else if (_lotSel == 1)   nw = g.x;               // +X edge
                else                     nl = g.z;               // +Z edge
                nw = Mathf.Clamp(nw, 1f, 4000f);
                nl = Mathf.Clamp(nl, 1f, 4000f);
                if (!Mathf.Approximately(nw, _lotW) || !Mathf.Approximately(nl, _lotL)) _lotMoved = true;
                _lotW = nw; _lotL = nl;
                worldRenderer?.PreviewTerrainSize(_lotW, _lotL);   // live plane resize; data committed on release
            }
        }

        if (LMBUp && _lotDragging)
        {
            _lotDragging = false;
            if (_lotMoved)
            {
                if (_lotPolygonMode) { _history?.RecordBefore(EditHistory.Scope.Environment, "Edit lot"); CommitLotPolygon(env); }
                else CommitLotRect(env);
            }
            _lotMoved = false;
        }

        UpdateLotHandlePositions();
        UpdateLotPreview();
    }

    // Writes the working rectangle into site.terrainSize on drag release. Honors the resize selector:
    // keep-in-place just sets the size; scale-content rescales the whole layout to fill the new lot.
    private void CommitLotRect(EnvironmentDef env)
    {
        var ts = env.site.terrainSize;
        float oldW = ts != null && ts.Length > 0 && ts[0] > 0f ? ts[0] : _lotW;
        float oldL = ts != null && ts.Length > 1 && ts[1] > 0f ? ts[1] : _lotL;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Resize lot");
        if (_resizeScaleContent && oldW > 0f && oldL > 0f)
            EnvironmentScale.ScaleEnvironmentXZ(env, _lotW / oldW, _lotL / oldL, Vector2.zero);
        else
            SetTerrainSize(env, _lotW, _lotL);
        worldRenderer?.ApplyTerrainSize(env.site);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        SyncSiteFields(env);
    }

    private void CommitLotPolygon(EnvironmentDef env)
    {
        if (_lotPts.Count < 3) return;
        var poly = new float[_lotPts.Count][];
        for (int i = 0; i < _lotPts.Count; i++) poly[i] = new[] { _lotPts[i].x, _lotPts[i].z };
        env.site.lotBoundary = poly;
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    // Insert a vertex where the user clicked on the (closed) parcel outline. Returns true on insert.
    private bool TryInsertLotVertex(Vector3 ground)
    {
        if (_lotPts.Count < 3) return false;
        var click = new Vector2(ground.x, ground.z);
        float bestD = float.MaxValue; int bestSeg = -1; Vector2 bestPt = click;
        for (int i = 0; i < _lotPts.Count; i++)
        {
            Vector2 a = new(_lotPts[i].x, _lotPts[i].z);
            Vector2 b = new(_lotPts[(i + 1) % _lotPts.Count].x, _lotPts[(i + 1) % _lotPts.Count].z);
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(click - a, ab) / len2);
            Vector2 proj = a + t * ab;
            float d = Vector2.Distance(click, proj);
            if (d < bestD) { bestD = d; bestSeg = i; bestPt = proj; }
        }
        float diag = Mathf.Sqrt(_lotW * _lotW + _lotL * _lotL);
        float tol  = Mathf.Clamp(diag * 0.04f, 2f, 30f);
        if (bestSeg < 0 || bestD > tol) return false;
        _lotPts.Insert(bestSeg + 1, new Vector3(bestPt.x, 0f, bestPt.y));
        _lotSel = bestSeg + 1;
        return true;
    }

    // Switch between resize-rectangle and edit-parcel sub-modes. Turning polygon on with no existing
    // boundary seeds one from the current rectangle so the user has vertices to drag.
    private void SetLotPolygonMode(EnvironmentDef env, bool polygon)
    {
        if (env?.site == null) return;
        if (polygon && (env.site.lotBoundary == null || env.site.lotBoundary.Length < 3))
        {
            _history?.RecordBefore(EditHistory.Scope.Environment, "Create parcel");
            var ts = env.site.terrainSize;
            float w = ts != null && ts.Length > 0 ? ts[0] : 100f;
            float l = ts != null && ts.Length > 1 ? ts[1] : 100f;
            env.site.lotBoundary = new[] { new[] { 0f, 0f }, new[] { w, 0f }, new[] { w, l }, new[] { 0f, l } };
            libraryBrowser?.MarkDirty();
            worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        }
        _lotPolygonMode = polygon;
        _lotSel = -1;
        SeedLotWorking(env);
        SetLotFrameVisible(false);
        RebuildLotHandles();
    }

    // Drop the parcel polygon so the whole rectangle is buildable terrain again.
    private void ClearLotBoundary(EnvironmentDef env)
    {
        if (env?.site == null) return;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Reset parcel to rectangle");
        env.site.lotBoundary = null;
        _lotPolygonMode = false;
        _lotSel = -1;
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        SeedLotWorking(env);
        SetLotFrameVisible(false);
        RebuildLotHandles();
    }

    private int LotHandleCount() => _lotPolygonMode ? _lotPts.Count : 3;

    // Rect handles: 0 = far corner (w,l), 1 = +X edge (w, l/2), 2 = +Z edge (w/2, l). Polygon: vertex i.
    private Vector2 LotHandleXZ(int i)
    {
        if (_lotPolygonMode) return new Vector2(_lotPts[i].x, _lotPts[i].z);
        return i switch
        {
            0 => new Vector2(_lotW, _lotL),
            1 => new Vector2(_lotW, _lotL * 0.5f),
            _ => new Vector2(_lotW * 0.5f, _lotL),
        };
    }

    private Vector3 LotHandleWorld(int i)
    {
        Vector2 p = LotHandleXZ(i);
        return new Vector3(p.x, PreviewY(p.x, p.y) + 0.18f, p.y);
    }

    private int PickLotHandle()
    {
        if (mainCamera == null) return -1;
        Vector2 m = new(MousePos.x, MousePos.y);
        float best = PATH_HANDLE_PICK_PX * PATH_HANDLE_PICK_PX; int idx = -1;
        for (int i = 0; i < LotHandleCount(); i++)
        {
            Vector3 sp = mainCamera.WorldToScreenPoint(LotHandleWorld(i));
            if (sp.z < 0f) continue;
            float d2 = (new Vector2(sp.x, sp.y) - m).sqrMagnitude;
            if (d2 < best) { best = d2; idx = i; }
        }
        return idx;
    }

    private void RebuildLotHandles()
    {
        foreach (var h in _lotHandles) if (h != null) Destroy(h);
        _lotHandles.Clear();
        EnsureHandleMaterials();
        int n = LotHandleCount();
        for (int i = 0; i < n; i++)
        {
            var h = GameObject.CreatePrimitive(_lotPolygonMode ? PrimitiveType.Sphere : PrimitiveType.Cube);
            h.name = "LotHandle";
            var col = h.GetComponent<Collider>(); if (col != null) Destroy(col);   // pick by screen-space
            h.transform.localScale = Vector3.one * 0.9f;
            _lotHandles.Add(h);
        }
        UpdateLotHandlePositions();
    }

    private void UpdateLotHandlePositions()
    {
        if (_lotHandles.Count != LotHandleCount()) { RebuildLotHandles(); return; }
        for (int i = 0; i < _lotHandles.Count; i++)
        {
            if (_lotHandles[i] == null) continue;
            _lotHandles[i].transform.position = LotHandleWorld(i);
            var r = _lotHandles[i].GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = i == _lotSel ? _pathHandleSelMat : _pathHandleMat;
        }
    }

    private void EnsureLotPreview()
    {
        var amber = new Color(1f, 0.85f, 0.2f, 1f);
        if (_lotPreviewMat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            _lotPreviewMat = new Material(sh) { name = "LotEditPreview" };
            if (_lotPreviewMat.HasProperty("_BaseColor")) _lotPreviewMat.SetColor("_BaseColor", amber);
            if (_lotPreviewMat.HasProperty("_Color"))     _lotPreviewMat.SetColor("_Color", amber);
        }
        if (_lotPreviewGO == null)
        {
            _lotPreviewGO = new GameObject("LotEditPreview");
            _lotPreviewLR = _lotPreviewGO.AddComponent<LineRenderer>();
            _lotPreviewLR.useWorldSpace = true;
            _lotPreviewLR.loop = true;
            _lotPreviewLR.numCornerVertices = 2;
            _lotPreviewLR.alignment = LineAlignment.View;
            _lotPreviewLR.material = _lotPreviewMat;
            _lotPreviewLR.startColor = _lotPreviewLR.endColor = amber;
            _lotPreviewLR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _lotPreviewLR.receiveShadows = false;
        }
        _lotPreviewGO.SetActive(true);
    }

    private void UpdateLotPreview()
    {
        if (_lotPreviewLR == null) return;
        var corners = new List<Vector2>();
        if (_lotPolygonMode) { foreach (var p in _lotPts) corners.Add(new Vector2(p.x, p.z)); }
        else
        {
            corners.Add(new Vector2(0f, 0f));
            corners.Add(new Vector2(_lotW, 0f));
            corners.Add(new Vector2(_lotW, _lotL));
            corners.Add(new Vector2(0f, _lotL));
        }
        if (corners.Count < 3) { _lotPreviewLR.positionCount = 0; return; }

        float diag = Mathf.Sqrt(_lotW * _lotW + _lotL * _lotL);
        float spacing = Mathf.Clamp(diag * 0.05f, 2f, 40f);
        var pts = new List<Vector3>();
        int n = corners.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = corners[i], b = corners[(i + 1) % n];
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / spacing));
            for (int s = 0; s < steps; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, s / (float)steps);
                pts.Add(new Vector3(p.x, PreviewY(p.x, p.y) + 0.2f, p.y));
            }
        }
        _lotPreviewLR.widthMultiplier = Mathf.Clamp(diag * 0.004f, 0.25f, 4f);
        _lotPreviewLR.positionCount = pts.Count;
        _lotPreviewLR.SetPositions(pts.ToArray());
    }

    // Hide/show the committed "Lot frame" LineRenderer under the active env root (so it doesn't
    // double-draw with the live edit preview).
    private void SetLotFrameVisible(bool visible)
    {
        var root = worldRenderer != null ? worldRenderer.GetRoot() : null;
        var f = root != null ? root.Find("Lot frame") : null;
        if (f != null) f.gameObject.SetActive(visible);
    }

    private void StopEditLot()
    {
        foreach (var h in _lotHandles) if (h != null) Destroy(h);
        _lotHandles.Clear();
        if (_lotPreviewGO != null) _lotPreviewGO.SetActive(false);
        _lotPts.Clear();
        _lotSel = -1; _lotDragging = false; _lotMoved = false;
        _mode = EditMode.Browse;
        // Re-render so the committed frame returns fresh (and the water mask reflects any new shape).
        var env = libraryBrowser?.CurrentEnvironment;
        if (env != null) worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    private void SetTerrainSize(EnvironmentDef env, float w, float l)
    {
        env.site.terrainSize ??= new float[2];
        if (env.site.terrainSize.Length < 2) env.site.terrainSize = new float[2];
        env.site.terrainSize[0] = Mathf.Clamp(w, 1f, 4000f);
        env.site.terrainSize[1] = Mathf.Clamp(l, 1f, 4000f);
    }

    private void SyncSiteFields(EnvironmentDef env)
    {
        if (env?.site?.terrainSize == null || env.site.terrainSize.Length < 2) return;
        _siteWidthStr = env.site.terrainSize[0].ToString("0.##");
        _siteLenStr   = env.site.terrainSize[1].ToString("0.##");
    }

    // ---- PaintObjects: Unity-style scatter brush (density, random rot/scale) + eraser ----

    private void StartPaintObjects(string prefabType)
    {
        ExitCurrentMode();
        _brushPrefab  = prefabType;
        // Default to the first registered prefab when none has been picked yet.
        if (string.IsNullOrEmpty(_brushPrefab) && prefabRegistry?.entries != null && prefabRegistry.entries.Count > 0)
            _brushPrefab = prefabRegistry.entries[0].key;
        _brushErase   = false;
        _brushApplied = false;
        _mode = EditMode.PaintObjects;
    }

    private void UpdatePaintObjects()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { StopPaintObjects(); return; }

        Vector3 center = GroundPoint();
        UpdateBrushRing(center, _brushRadius, _brushErase ? new Color(1f, 0.4f, 0.3f, 0.9f) : new Color(0.4f, 1f, 0.5f, 0.9f));

        if (IsMouseOverUI()) return;

        if (LMBDown) _brushApplied = false;   // reset travel gate at stroke start
        if (LMBHeld)
        {
            if (!_brushApplied || Vector3.Distance(center, _brushLastApply) >= _brushRadius * 0.5f)
            {
                // One undo entry per stroke: open the gesture before the first application (it stays
                // open until the central mouse-release handler closes it). Ensure the env exists
                // first so the very first scatter into a fresh scene is captured too.
                if (_brushErase) { _history?.BeginGesture(EditHistory.Scope.Environment, "Erase objects"); EraseAt(center); }
                else             { libraryBrowser?.EnsureWorkingEnvironment(); _history?.BeginGesture(EditHistory.Scope.Environment, "Paint objects"); ScatterAt(center); }
                _brushLastApply = center;
                _brushApplied   = true;
            }
        }
        if (LMBUp && _brushApplied) libraryBrowser?.MarkDirty();
    }

    private void ScatterAt(Vector3 center)
    {
        if (string.IsNullOrEmpty(_brushPrefab)) return;
        var env = libraryBrowser?.EnsureWorkingEnvironment();
        if (env == null) return;
        env.objectInstances ??= new List<ObjectInstance>();

        int attempts = Mathf.Max(1, Mathf.RoundToInt(_brushDensity * Mathf.PI * _brushRadius * _brushRadius));
        float sp2 = _brushSpacing * _brushSpacing;

        // For spacing, gather existing instances near the brush once so new objects don't overlap
        // ones placed earlier (this stroke or before). Candidates accepted this call are appended,
        // so they also space against each other.
        _scatterNeighbors.Clear();
        if (_brushSpacing > 0f)
        {
            float gather = _brushRadius + _brushSpacing;
            float gather2 = gather * gather;
            foreach (var oi in env.objectInstances)
            {
                if (oi?.position == null || oi.position.Length < 3) continue;
                float dx = oi.position[0] - center.x, dz = oi.position[2] - center.z;
                if (dx * dx + dz * dz <= gather2) _scatterNeighbors.Add(new Vector2(oi.position[0], oi.position[2]));
            }
        }

        for (int i = 0; i < attempts; i++)
        {
            // Uniform disc sampling: sqrt(u) keeps density even out to the edge.
            float ang = UnityEngine.Random.value * Mathf.PI * 2f;
            float rad = Mathf.Sqrt(UnityEngine.Random.value) * _brushRadius;
            Vector3 pos = center + new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad);

            if (_brushSpacing > 0f)
            {
                var p2 = new Vector2(pos.x, pos.z);
                bool tooClose = false;
                for (int n = 0; n < _scatterNeighbors.Count; n++)
                    if ((_scatterNeighbors[n] - p2).sqrMagnitude < sp2) { tooClose = true; break; }
                if (tooClose) continue;                 // reject overlapping candidate
                _scatterNeighbors.Add(p2);
            }

            var inst = new ObjectInstance
            {
                instanceId = Guid.NewGuid().ToString("D"),
                prefabType = _brushPrefab,
                position   = new[] { pos.x, 0f, pos.z },
                rotationX  = 0f,
                rotationY  = _brushRandomRot ? UnityEngine.Random.value * 360f : 0f,
                rotationZ  = 0f,
                scale      = UnityEngine.Random.Range(_brushScaleMin, _brushScaleMax),
                included   = true,
                brushPainted = true,
            };
            env.objectInstances.Add(inst);
            worldRenderer?.SpawnObjectInstance(inst);
        }
    }

    private void EraseAt(Vector3 center)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env?.objectInstances == null) return;
        float r2 = _brushRadius * _brushRadius;
        for (int i = env.objectInstances.Count - 1; i >= 0; i--)
        {
            var inst = env.objectInstances[i];
            if (inst?.position == null || inst.position.Length < 3) continue;
            if (!inst.brushPainted) continue;   // only erase brush-scattered objects, never layout/pre-existing
            float dx = inst.position[0] - center.x, dz = inst.position[2] - center.z;
            if (dx * dx + dz * dz <= r2)
            {
                worldRenderer?.RemoveObjectInstance(inst.instanceId);
                env.objectInstances.RemoveAt(i);
            }
        }
    }

    private void StopPaintObjects()
    {
        if (_brushRing) { Destroy(_brushRing.gameObject); _brushRing = null; }
        _mode = EditMode.Browse;
    }

    // ---- PaintSurface: freehand ground-type brush stamped into the splatmap + stored as a stroke ----

    private void StartPaintSurface(string terrainType)
    {
        ExitCurrentMode();
        _surfType = terrainType;
        // Default the surface type to a real TerrainRegistry key (not a hard-coded guess).
        if (terrainRegistry?.entries != null && terrainRegistry.entries.Count > 0 &&
            !terrainRegistry.entries.Exists(e => string.Equals(e.key, _surfType, StringComparison.OrdinalIgnoreCase)))
            _surfType = terrainRegistry.entries[0].key;
        _surfPts.Clear();
        _mode = EditMode.PaintSurface;
    }

    private void UpdatePaintSurface()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { StopPaintSurface(); return; }

        Vector3 center = GroundPoint();
        UpdateBrushRing(center, _surfRadius, new Color(0.95f, 0.85f, 0.35f, 0.9f));

        if (IsMouseOverUI()) return;

        if (LMBDown)
        {
            // The stroke data is committed on release (CommitSurfaceStroke), which runs after the
            // central gesture-end this frame — so snapshot the pre-stroke state here as a discrete
            // entry instead of using a gesture.
            libraryBrowser?.EnsureWorkingEnvironment();
            _history?.RecordBefore(EditHistory.Scope.Environment, "Paint ground");
            _surfPts.Clear();
            _surfPts.Add(center);
            _surfLastStamp = center;
            worldRenderer?.StampSurfaceDiscLive(center, _surfRadius, _surfType);
        }
        else if (LMBHeld && _surfPts.Count > 0)
        {
            if (Vector3.Distance(center, _surfLastStamp) >= _surfRadius * 0.5f)
            {
                _surfPts.Add(center);
                _surfLastStamp = center;
                worldRenderer?.StampSurfaceDiscLive(center, _surfRadius, _surfType);
            }
        }
        else if (LMBUp && _surfPts.Count > 0)
        {
            CommitSurfaceStroke();
        }
    }

    private void CommitSurfaceStroke()
    {
        var env = libraryBrowser?.EnsureWorkingEnvironment();
        if (env?.site != null && _surfPts.Count > 0)
        {
            env.site.surfaceStrokes ??= new List<SurfaceStrokeDef>();
            var pts = new float[_surfPts.Count][];
            for (int i = 0; i < _surfPts.Count; i++) pts[i] = new[] { _surfPts[i].x, _surfPts[i].z };
            env.site.surfaceStrokes.Add(new SurfaceStrokeDef
            {
                id          = Guid.NewGuid().ToString("D"),
                terrainType = _surfType,
                radius      = _surfRadius,
                points      = pts,
            });
            libraryBrowser?.MarkDirty();
        }
        _surfPts.Clear();
    }

    private void StopPaintSurface()
    {
        if (_surfPts.Count > 0) CommitSurfaceStroke();
        if (_brushRing) { Destroy(_brushRing.gameObject); _brushRing = null; }
        _mode = EditMode.Browse;
    }

    // -----------------------------------------------------------------------
    // Measure tool (+ Scale Calibration) — a non-destructive ruler at true scale. Click ground
    // points to read distance / running length / polygon area in meters; the first two points also
    // drive Scale Calibration (see ApplyCalibration). Renders an overlay polyline (LineRenderer) and
    // floating labels (OnGUI). Writes nothing to the environment — no undo entry, no MarkDirty.
    // -----------------------------------------------------------------------

    private void StartMeasure()
    {
        ExitCurrentMode();
        _mode = EditMode.Measure;
        _measurePts.Clear();
        _calibRealStr = "";
    }

    private void UpdateMeasure()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame)
        {
            if (_measurePts.Count > 0) _measurePts.Clear();   // first Esc clears, second exits the mode
            else StopMeasure();
            UpdateMeasureOverlay();
            return;
        }
        // Backspace removes the last placed point.
        if (KB != null && !TypingInUI && KB.backspaceKey.wasPressedThisFrame && _measurePts.Count > 0)
            _measurePts.RemoveAt(_measurePts.Count - 1);

        _measureCursor = GroundPoint();
        if (LMBDown && !IsMouseOverUI()) _measurePts.Add(_measureCursor);

        UpdateMeasureOverlay();
    }

    private void StopMeasure()
    {
        _measurePts.Clear();
        if (_measureLine) { Destroy(_measureLine.gameObject); _measureLine = null; }
        _mode = EditMode.Browse;
    }

    // Rebuilds the overlay polyline: placed points plus the live cursor as a trailing segment.
    private void UpdateMeasureOverlay()
    {
        if (_mode != EditMode.Measure || _measurePts.Count == 0)
        {
            if (_measureLine) _measureLine.positionCount = 0;
            return;
        }
        if (_measureLine == null) _measureLine = MakeLine("MeasureLine", new Color(1f, 0.85f, 0.2f, 1f), 0.12f);

        bool showCursor = !IsMouseOverUI();
        int count = _measurePts.Count + (showCursor ? 1 : 0);
        _measureLine.positionCount = count;
        for (int i = 0; i < _measurePts.Count; i++)
            _measureLine.SetPosition(i, new Vector3(_measurePts[i].x, MEASURE_Y, _measurePts[i].z));
        if (showCursor)
            _measureLine.SetPosition(count - 1, new Vector3(_measureCursor.x, MEASURE_Y, _measureCursor.z));
    }

    // The point list as [x, z] arrays for the EnvironmentScale math helpers.
    private List<float[]> MeasurePointsXZ(bool includeCursor)
    {
        var pts = new List<float[]>(_measurePts.Count + 1);
        foreach (var p in _measurePts) pts.Add(new[] { p.x, p.z });
        if (includeCursor && !IsMouseOverUI()) pts.Add(new[] { _measureCursor.x, _measureCursor.z });
        return pts;
    }

    // Floating labels: per-segment distance at each midpoint, drawn during OnGUI while measuring.
    private void DrawMeasureLabels()
    {
        if (_mode != EditMode.Measure || mainCamera == null) return;
        var pts = MeasurePointsXZ(includeCursor: true);
        if (pts.Count < 2) return;

        if (_measureLabelStyle == null)
            _measureLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.95f, 0.6f, 1f) },
            };

        for (int i = 1; i < pts.Count; i++)
        {
            float seg = EnvironmentScale.Distance(pts[i - 1], pts[i]);
            if (seg < 0.01f) continue;
            Vector3 mid = new((pts[i - 1][0] + pts[i][0]) * 0.5f, MEASURE_Y, (pts[i - 1][1] + pts[i][1]) * 0.5f);
            Vector3 sp  = mainCamera.WorldToScreenPoint(mid);
            if (sp.z <= 0f) continue;
            var r = new Rect(sp.x - 50f, Screen.height - sp.y - 10f, 100f, 20f);
            GUI.Label(r, $"{seg:0.00} m", _measureLabelStyle);
        }
    }

    // ---- shared line/ring helpers for the terrain tools ----

    private static LineRenderer MakeLine(string name, Color color, float width)
    {
        var go = new GameObject(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace      = true;
        lr.widthMultiplier    = Mathf.Max(0.05f, width);
        lr.numCornerVertices  = 2;
        lr.numCapVertices     = 2;
        lr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows     = false;
        var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (sh != null) lr.material = new Material(sh);
        lr.startColor = lr.endColor = color;
        return lr;
    }

    private void UpdateBrushRing(Vector3 center, float radius, Color color)
    {
        if (_brushRing == null) _brushRing = MakeLine("BrushRing", color, 0.15f);
        _brushRing.startColor = _brushRing.endColor = color;
        _brushRing.loop = true;
        const int seg = 48;
        _brushRing.positionCount = seg;
        for (int i = 0; i < seg; i++)
        {
            float a = (i / (float)seg) * Mathf.PI * 2f;
            _brushRing.SetPosition(i, center + new Vector3(Mathf.Cos(a) * radius, 0.05f, Mathf.Sin(a) * radius));
        }
    }

    // Ghost sized from the BuildingDef's tile footprint, with `center` returning the ghost's
    // center in the building's corner-pivot local frame (tiles may be authored away from grid
    // (0,0), so the footprint isn't assumed to start at the root). Generic massing cube as fallback.
    private Vector3 GhostBounds(string defId, out Vector3 center)
    {
        var defs = libraryBrowser?.CurrentBuildingDefs;
        if (defs == null || !defs.TryGetValue(defId, out var b))
        {
            center = new Vector3(4f, 2f, 4f);
            return new Vector3(8f, 4f, 8f);
        }

        float cs = b.gridCellSize > 0f ? b.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        float fh = b.floorHeight  > 0f ? b.floorHeight  : AuthoringConventions.DEFAULT_FLOOR_HEIGHT;

        int minX = 0, maxX = 0, minZ = 0, maxZ = 0, maxFloor = 0;
        bool any = false;
        if (b.tiles != null)
            foreach (var t in b.tiles)
            {
                if (!any) { minX = maxX = t.gridX; minZ = maxZ = t.gridZ; any = true; }
                if (t.gridX < minX) minX = t.gridX;
                if (t.gridX > maxX) maxX = t.gridX;
                if (t.gridZ < minZ) minZ = t.gridZ;
                if (t.gridZ > maxZ) maxZ = t.gridZ;
                if (t.floor > maxFloor) maxFloor = t.floor;
            }

        // Local extents of the rendered geometry: a tile at gridX occupies [gridX·cs,(gridX+1)·cs].
        int floors = Mathf.Max(any ? maxFloor + 1 : 1, b.floors > 0 ? b.floors : 1);
        Vector3 size = new Vector3((maxX - minX + 1) * cs, floors * fh, (maxZ - minZ + 1) * cs);
        center = new Vector3((minX * cs + (maxX + 1) * cs) * 0.5f,
                             size.y * 0.5f,
                             (minZ * cs + (maxZ + 1) * cs) * 0.5f);
        return size;
    }

    private float PlaceBldgCellSize()
    {
        var defs = libraryBrowser?.CurrentBuildingDefs;
        if (defs != null && defs.TryGetValue(_placeBldgDefId, out var b) && b.gridCellSize > 0f)
            return b.gridCellSize;
        return AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
    }

    // -----------------------------------------------------------------------
    // EditBuilding mode (M3)
    // -----------------------------------------------------------------------

    private void EnterEditBuilding()
    {
        // Locked twin: no tile editing — this also stops a shared BuildingDef being mutated
        // (and PUT back globally) from inside the locked environment.
        if (!_selIsBuilding || string.IsNullOrEmpty(_selId) || ActiveLocked) return;
        var env  = libraryBrowser?.CurrentEnvironment;
        var inst = FindBI(env, _selId);
        if (inst == null) return;
        var defs = libraryBrowser?.CurrentBuildingDefs;
        if (defs == null || !defs.TryGetValue(inst.buildingId, out var bdef)) return;

        // Switch the shell into Build first (this tears down the Browse rail), then enter the
        // editor — ordering avoids the mode-switch teardown clobbering the edit we're starting.
        UIMode.Set(AppMode.Build);

        Vector3 pos = inst.position != null && inst.position.Length >= 3
            ? new Vector3(inst.position[0], inst.position[1], inst.position[2]) : Vector3.zero;
        tileBuildingEditor.Enter(bdef, pos, inst.rotationY);

        // Hide the already-rendered instance: the tile editor renders its own editable copy at
        // the same place, so leaving the original visible duplicates the geometry and its
        // colliders steal paint raycasts (they carry no TileInstanceMarker).
        _editingHiddenGO = _selGO;
        if (_editingHiddenGO != null) _editingHiddenGO.SetActive(false);

        _standaloneEdit = false;
        _mode = EditMode.EditBuilding;
        FocusCameraOnEditedFloor(reframe: true);
    }

    // Opens a building from the library browser (not tied to a placed instance).
    public void EditBuildingFromLibrary(BuildingDef bdef)
    {
        if (tileBuildingEditor == null) return;
        ExitCurrentMode();
        UIMode.Set(AppMode.Build);
        tileBuildingEditor.Enter(bdef, _camPivot, 0f);
        _selId          = null;
        _standaloneEdit = true;
        _mode           = EditMode.EditBuilding;
        FocusCameraOnEditedFloor(reframe: true);
    }

    private void SaveCurrentEditingBuilding()
    {
        if (tileBuildingEditor == null || !tileBuildingEditor.IsActive) return;
        var bdef = tileBuildingEditor.CurrentDef;
        if (bdef == null) return;
        libraryClient?.PutBuilding(bdef,
            ()  => { Debug.Log($"[EditController] Building '{bdef.name}' saved."); },
            err => Debug.LogError($"[EditController] Save building failed: {err}"));
        libraryBrowser?.AddBuildingDef(bdef);
    }

    private void UpdateEditBuilding()
    {
        tileBuildingEditor?.HandleInput();

        // Follow the editor up/down: when the active floor changes, lift the orbit pivot to it so the
        // camera always frames the floor being edited (cheap — only on an actual floor change).
        if (tileBuildingEditor != null && tileBuildingEditor.IsActive
            && tileBuildingEditor.ActiveFloor != _lastFocusedFloor)
            FocusCameraOnEditedFloor(reframe: false);

        if (KB != null && KB.escapeKey.wasPressedThisFrame)
        {
            ExitEditBuilding(save: true);
            UIMode.Set(AppMode.Browse);   // return the shell to Browse after committing the edit
        }
    }

    // Frames / lifts the orbit camera onto the floor currently being tile-edited. `reframe` recenters
    // and re-distances on the whole footprint (used on entry); otherwise it only lifts the pivot to
    // the active floor's height, preserving the user's current framing while they switch floors.
    private void FocusCameraOnEditedFloor(bool reframe)
    {
        if (!snapCameraOnTileEdit) return;
        if (tileBuildingEditor == null || !tileBuildingEditor.IsActive) return;
        int floor = tileBuildingEditor.ActiveFloor;
        if (!tileBuildingEditor.TryGetFocus(floor, out Vector3 center, out float radius)) return;

        if (reframe)
        {
            _camPivot = center;
            float fov  = mainCamera != null ? mainCamera.fieldOfView : 60f;
            float dist = radius / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) + radius * 2f;
            _camDist   = Mathf.Clamp(dist, camMinDist, camMaxDist);
        }
        else
        {
            _camPivot.y = center.y;   // lift to the new floor surface, keep the user's XZ framing
        }
        _lastFocusedFloor = floor;
    }

    private void ExitEditBuilding(bool save)
    {
        // Restore the instance hidden on enter. On the save path the env re-renders below and
        // replaces it anyway; re-showing first keeps the discard path (and standalone) correct.
        if (_editingHiddenGO != null) { _editingHiddenGO.SetActive(true); _editingHiddenGO = null; }

        if (tileBuildingEditor == null) { _mode = EditMode.Browse; _standaloneEdit = false; return; }

        if (save && tileBuildingEditor.IsActive)
        {
            // Capture the editor's world anchor before ExitAndGet tears the live geometry down, so a
            // standalone building can be placed exactly where it was being edited (no visual jump).
            Vector3 anchorPos  = tileBuildingEditor.AnchorPos;
            float   anchorRotY = tileBuildingEditor.AnchorRotY;
            bool    standalone = _standaloneEdit;

            var updated = tileBuildingEditor.ExitAndGet();
            if (updated != null)
            {
                // KNOWN LIMITATION: BuildingDefs are global records shared across environments, so
                // this PUT mutates the def everywhere it is placed — including inside a locked
                // (digital twin) env that references the same buildingId. Entry into the tile editor
                // is blocked while a locked env is active, but a shared def can still be edited from
                // another env or the Buildings tab. Full protection needs per-building locks or
                // copy-on-write; out of scope for the pilot.
                libraryClient?.PutBuilding(updated,
                    ()  => Debug.Log($"[EditController] Building '{updated.name}' saved."),
                    err => Debug.LogError($"[EditController] Save building failed: {err}"));
                libraryBrowser?.AddBuildingDef(updated);

                if (standalone)
                {
                    // Standalone edit (Buildings tab): the tile editor's preview is destroyed on exit
                    // and there's no placed instance to fall back on, so without this the building
                    // simply vanishes. Drop it into a working environment as an instance so it stays
                    // visible and round-trips through Save/Load. Skip if it's already placed there
                    // (re-rendering the env reflects the edited def for the existing instances).
                    var env = libraryBrowser?.EnsureWorkingEnvironment();
                    if (env != null)
                    {
                        bool placed = env.buildingInstances != null &&
                                      env.buildingInstances.Exists(b => b != null && b.buildingId == updated.id);
                        if (!placed)
                        {
                            env.buildingInstances ??= new List<BuildingInstance>();
                            env.buildingInstances.Add(new BuildingInstance
                            {
                                instanceId = Guid.NewGuid().ToString("D"),
                                buildingId = updated.id,
                                position   = new[] { anchorPos.x, anchorPos.y, anchorPos.z },
                                rotationY  = anchorRotY,
                                scale      = 1f,
                                included   = true,
                            });
                        }
                        libraryBrowser?.MarkDirty();
                        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
                    }
                }
                else
                {
                    libraryBrowser?.MarkDirty();
                    var env = libraryBrowser?.CurrentEnvironment;
                    worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
                }
            }
        }
        else
        {
            tileBuildingEditor?.ExitAndDiscard();
        }
        _standaloneEdit = false;
        _mode = EditMode.Browse;
    }

    // -----------------------------------------------------------------------
    // Gizmo callbacks (handle drags work from both Browse and Transform modes)
    // -----------------------------------------------------------------------

    private void OnGizmoMove(Vector3 delta)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null || ActiveLocked) return;
        _history?.BeginGesture(EditHistory.Scope.Environment, "Move");   // one entry per gizmo drag
        if (_envSelected) EnvMove(env, delta);
        else              ApplyPosDelta(env, delta);
    }

    private void OnGizmoRotate(Vector3 deltaEuler)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null || ActiveLocked) return;
        _history?.BeginGesture(EditHistory.Scope.Environment, "Rotate");
        if (_envSelected) EnvRotateYaw(env, deltaEuler.y);   // env rotation is yaw-only
        else              ApplyRotDelta(env, deltaEuler);
    }

    private void OnGizmoScale(float delta)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null || ActiveLocked) return;
        _history?.BeginGesture(EditHistory.Scope.Environment, "Scale");
        if (_envSelected) EnvScale(env, 1f + delta);   // additive drag delta → multiplicative factor
        else              ApplyScaleDelta(env, delta);
    }

    private void OnGizmoDragEnd()
    {
        _history?.EndGesture();
        libraryBrowser?.MarkDirty();
        // Re-render so objects re-ground after a group move/rotate/scale, then re-frame the gizmo.
        if (_envSelected) AfterEnvEdit(libraryBrowser?.CurrentEnvironment);
    }

    // -----------------------------------------------------------------------
    // Selection helpers
    // -----------------------------------------------------------------------

    // Single-select: clears any multi-selection and makes this the sole (primary) selection.
    private void SetSelection(string id, bool isBuilding, GameObject go)
    {
        ClearExtraSelection();
        if (_selGO != null && _selGO != go) RemoveHighlight(_selGO);
        _selId = id; _selIsBuilding = isBuilding; _selGO = go;
        CacheBaseScale();
        ApplyHighlight(go);
        UpdateGizmoTargets();
    }

    // Shift/Ctrl+click: add the clicked instance to the selection, or remove it if already in.
    private void ToggleSelection(string id, bool isBuilding, GameObject go)
    {
        if (string.IsNullOrEmpty(_selId)) { SetSelection(id, isBuilding, go); return; }

        if (id == _selId)
        {
            // Removing the primary: promote the most-recent extra to primary, else clear all.
            RemoveHighlight(_selGO);
            if (_extraSel.Count > 0)
            {
                var p = _extraSel[_extraSel.Count - 1];
                _extraSel.RemoveAt(_extraSel.Count - 1);
                _selId = p.id; _selIsBuilding = p.isBuilding; _selGO = p.go; _selBaseScale = p.baseScale;
            }
            else { _selId = null; _selGO = null; }
            UpdateGizmoTargets();
            return;
        }

        int idx = _extraSel.FindIndex(s => s.id == id);
        if (idx >= 0)
        {
            if (_extraSel[idx].go != null) RemoveHighlight(_extraSel[idx].go);
            _extraSel.RemoveAt(idx);
        }
        else
        {
            // Demote the current primary into the extras, make the clicked instance the primary.
            _extraSel.Add(new Sel { id = _selId, isBuilding = _selIsBuilding, go = _selGO, baseScale = _selBaseScale });
            _selId = id; _selIsBuilding = isBuilding; _selGO = go;
            CacheBaseScale();
            ApplyHighlight(go);
        }
        UpdateGizmoTargets();
    }

    // Primary first, then extras. Used by every transform op so it touches the whole selection.
    private IEnumerable<Sel> AllSelected()
    {
        if (!string.IsNullOrEmpty(_selId))
            yield return new Sel { id = _selId, isBuilding = _selIsBuilding, go = _selGO, baseScale = _selBaseScale };
        foreach (var s in _extraSel) yield return s;
    }

    private void ClearExtraSelection()
    {
        foreach (var s in _extraSel) if (s.go != null && s.go != _selGO) RemoveHighlight(s.go);
        _extraSel.Clear();
    }

    // Frames the whole selection (primary + extras). Deltas still emit once from the gizmo.
    private void UpdateGizmoTargets()
    {
        _gizmoGOs.Clear();
        if (_selGO != null) _gizmoGOs.Add(_selGO);
        foreach (var s in _extraSel) if (s.go != null) _gizmoGOs.Add(s.go);
        if (_gizmoGOs.Count == 0) { _gizmo?.Clear(); return; }
        _gizmo?.SetTargets(_gizmoGOs, mainCamera);
    }

    // After WorldRenderer re-renders, the instantiated GOs are destroyed; re-resolve
    // every selected instance from the renderer. Keeps ids even when no GO exists
    // (excluded instances aren't rendered but must stay editable in the panel).
    private void RebindSelection()
    {
        if (string.IsNullOrEmpty(_selId) && _extraSel.Count == 0) return;
        var env = libraryBrowser?.CurrentEnvironment;

        for (int i = 0; i < _extraSel.Count; i++)
        {
            var s = _extraSel[i];
            s.go = worldRenderer?.GetInstanceGO(s.id);
            if (s.go != null)
            {
                s.baseScale = BaseScaleOf(s.go, env != null ? GetScaleFor(env, s.id, s.isBuilding) : 1f);
                ApplyHighlight(s.go);
            }
            _extraSel[i] = s;
        }

        _selGO = worldRenderer?.GetInstanceGO(_selId);
        if (_selGO != null)
        {
            CacheBaseScale();
            ApplyHighlight(_selGO);
        }
        UpdateGizmoTargets();
    }

    private void CacheBaseScale()
    {
        _selBaseScale = Vector3.one;
        if (_selGO == null) return;
        var env = libraryBrowser?.CurrentEnvironment;
        _selBaseScale = BaseScaleOf(_selGO, env != null ? GetInstanceScale(env) : 1f);
    }

    // GO localScale at instance scale 1 — preserves the prefab's authored scale + prefabScaleFactor.
    private static Vector3 BaseScaleOf(GameObject go, float instScale)
    {
        if (go == null) return Vector3.one;
        if (instScale <= 0f) instScale = 1f;
        return go.transform.localScale / instScale;
    }

    private void Deselect()
    {
        ClearExtraSelection();
        RemoveHighlight(_selGO);
        _selId = null; _selGO = null;
        _tool = Tool.Move;   // reset to the default (position) tool for the next selection
        _gizmo?.Clear();
        if (_mode == EditMode.Transform) _mode = EditMode.Browse;
    }

    private static bool AdditiveHeld() =>
        KB != null && (KB.leftShiftKey.isPressed || KB.rightShiftKey.isPressed ||
                       KB.leftCtrlKey.isPressed  || KB.rightCtrlKey.isPressed);

    // Called by LibraryBrowser when the active (editable) environment changes. Drops any
    // in-progress placement/transform/tile-edit and clears the selection so edits can't leak
    // onto the newly-focused environment (the previous one is now a locked backdrop).
    public void OnActiveEnvironmentSwitched()
    {
        switch (_mode)
        {
            case EditMode.PlaceObject:   StopPlaceObject();           break;
            case EditMode.PlaceBuilding: StopPlaceBuilding();         break;
            case EditMode.EditBuilding:  ExitEditBuilding(save: false); break;
            case EditMode.DrawPath:      StopDrawPath();              break;
            case EditMode.EditPath:      StopEditPath();              break;
            case EditMode.DrawFence:     StopDrawFence();             break;
            case EditMode.EditFence:     StopEditFence();             break;
            case EditMode.Measure:       StopMeasure();               break;
            case EditMode.EditLot:       StopEditLot();               break;
        }
        _mode = EditMode.Browse;
        DeselectEnv();
        Deselect();
        ClearHistory();   // undo history is per active environment (in-memory, per-session)
    }

    // -----------------------------------------------------------------------
    // Undo / redo host (EditHistory.IHost). Snapshots are JSON of the active EnvironmentDef or the
    // edited BuildingDef; restore replaces the live def and re-renders. See EditHistory.cs.
    // -----------------------------------------------------------------------

    public void ClearHistory() => _history?.Clear();

    // Lets other panels (e.g. LibraryBrowser's include/exclude toggles) record an undo entry for an
    // active-environment edit. Must be called immediately before the mutation.
    public void RecordEnvironmentEdit(string label) => _history?.RecordBefore(EditHistory.Scope.Environment, label);

    string EditHistory.IHost.ActiveContextId(EditHistory.Scope scope)
    {
        if (scope == EditHistory.Scope.Environment)
            return libraryBrowser?.CurrentEnvironment?.id;
        if (tileBuildingEditor != null && tileBuildingEditor.IsActive)
            return tileBuildingEditor.CurrentDef?.id;
        // Building-scope edits made from the selection panel (e.g. Skew) target the selected
        // building's def even though the tile editor isn't open.
        return FindBI(libraryBrowser?.CurrentEnvironment, _selIsBuilding ? _selId : null)?.buildingId;
    }

    string EditHistory.IHost.Serialize(EditHistory.Scope scope, string contextId)
    {
        if (string.IsNullOrEmpty(contextId)) return null;
        if (scope == EditHistory.Scope.Environment)
        {
            var env = libraryBrowser?.CurrentEnvironment;
            if (env == null || env.id != contextId) return null;
            return Newtonsoft.Json.JsonConvert.SerializeObject(env);
        }
        var bdef = libraryBrowser?.GetBuildingDef(contextId);
        return bdef == null ? null : Newtonsoft.Json.JsonConvert.SerializeObject(bdef);
    }

    void EditHistory.IHost.Restore(EditHistory.Scope scope, string contextId, string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        if (scope == EditHistory.Scope.Environment) RestoreEnvironment(contextId, json);
        else                                        RestoreBuilding(contextId, json);
    }

    private void RestoreEnvironment(string envId, string json)
    {
        if (libraryBrowser?.CurrentEnvironment == null || libraryBrowser.CurrentEnvironment.id != envId) return;
        var env = Newtonsoft.Json.JsonConvert.DeserializeObject<EnvironmentDef>(json);
        if (env == null) return;

        // An undo snapshot must never flip the lock state — re-stamp it from the live env so a
        // snapshot taken before locking can't restore locked=false (or vice versa).
        env.locked = libraryBrowser.CurrentEnvironment.locked;

        // Return to Browse before re-rendering: a live transform drag held stale origins, and an open
        // tile editor would otherwise duplicate the rebuilt building geometry. Selection GOs are about
        // to be destroyed by the re-render, so drop them too.
        if (tileBuildingEditor != null && tileBuildingEditor.IsActive) ExitEditBuilding(save: false);
        if (_mode != EditMode.Browse) _mode = EditMode.Browse;
        Deselect();
        DeselectEnv();

        libraryBrowser.ReplaceActiveEnvironment(env);
        worldRenderer?.RenderEnvironment(env, libraryBrowser.CurrentBuildingDefs);
    }

    private void RestoreBuilding(string buildingId, string json)
    {
        var bdef = Newtonsoft.Json.JsonConvert.DeserializeObject<BuildingDef>(json);
        if (bdef == null || libraryBrowser == null) return;

        libraryBrowser.AddBuildingDef(bdef);   // replace the def in the active env's building dict

        // If the tile editor is already open on this building, just swap its working def live. The
        // placed instance stays hidden behind the editable copy (as during normal editing); the env
        // re-renders when the user later exits, so we don't re-render it here (that would un-hide the
        // instance and duplicate the geometry).
        if (tileBuildingEditor != null && tileBuildingEditor.IsActive &&
            tileBuildingEditor.CurrentDef?.id == buildingId)
        {
            tileBuildingEditor.ReloadDef(bdef);
            return;
        }

        // Not currently editing this building: re-render the env first so placed instances reflect
        // the restored def.
        var env = libraryBrowser.CurrentEnvironment;
        if (env != null)
            worldRenderer?.RenderEnvironment(env, libraryBrowser.CurrentBuildingDefs);

        // Only re-open the tile editor when the undone edit belongs to a tile-edit session. A skew
        // applied from the selection panel re-renders in place; popping the editor would be jarring,
        // and the selected building GO is re-bound automatically next frame (Update→RebindSelection).
        if (_mode == EditMode.EditBuilding || _standaloneEdit)
            ReenterBuildingForUndo(buildingId, bdef);
    }

    // Opens the tile editor on a building whose def was just restored by undo/redo, anchoring on a
    // placed instance when one exists (so the geometry lines up) or as a standalone def otherwise.
    private void ReenterBuildingForUndo(string buildingId, BuildingDef bdef)
    {
        if (tileBuildingEditor == null) return;

        // Leave any other building's tile editor first (its def is already committed in the dict).
        if (tileBuildingEditor.IsActive) ExitEditBuilding(save: false);

        var env  = libraryBrowser?.CurrentEnvironment;
        var inst = env?.buildingInstances?.Find(b => b != null && b.buildingId == buildingId && b.included);
        if (inst != null)
        {
            var go = worldRenderer?.GetInstanceGO(inst.instanceId);
            SetSelection(inst.instanceId, true, go);
            EnterEditBuilding();
        }
        else
        {
            EditBuildingFromLibrary(bdef);
        }
    }

    private void DeleteSelected()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null || string.IsNullOrEmpty(_selId) || ActiveLocked) return;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Delete");
        foreach (var s in AllSelected())
        {
            if (s.isBuilding) env.buildingInstances?.RemoveAll(b => b.instanceId == s.id);
            else              env.objectInstances?.RemoveAll(o => o.instanceId == s.id);
        }
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        Deselect();
    }

    // -----------------------------------------------------------------------
    // Copy / paste (Ctrl+C, Ctrl+V) — see the Update hotkey block and _clipboard.
    // -----------------------------------------------------------------------

    // JSON round-trip deep copy (same Newtonsoft path as the undo snapshots): copies array
    // fields (position, boxSizeMeters) and survives future field additions unchanged.
    private static T CloneData<T>(T src) where T : class =>
        src == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<T>(
                                 Newtonsoft.Json.JsonConvert.SerializeObject(src));

    // Snapshot the current selection into the clipboard. Cloning here (not at paste) means
    // later edits/deletes of the originals — or unloading their env — can't affect a paste.
    private void CopySelection()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null || string.IsNullOrEmpty(_selId)) return;   // empty selection: keep old clipboard

        var entries = new List<ClipEntry>();
        foreach (var s in AllSelected())
        {
            if (s.isBuilding)
            {
                var bi = FindBI(env, s.id); if (bi == null) continue;
                entries.Add(new ClipEntry { isBuilding = true, bldg = CloneData(bi),
                                            def = libraryBrowser?.GetBuildingDef(bi.buildingId) });
            }
            else
            {
                var oi = FindOI(env, s.id); if (oi == null) continue;
                entries.Add(new ClipEntry { isBuilding = false, obj = CloneData(oi) });
            }
        }
        if (entries.Count == 0) return;   // nothing resolvable — don't clobber the clipboard
        _clipboard.Clear();
        _clipboard.AddRange(entries);
    }

    // Where the pasted group's centroid lands: the cursor's ground-plane hit, or the camera
    // pivot (center of the current view) when the cursor is over UI / above the horizon.
    private Vector3 PasteAnchor()
    {
        if (!IsMouseOverUI())
        {
            var plane = new Plane(Vector3.up, Vector3.zero);
            Ray ray   = mainCamera.ScreenPointToRay(MousePos);
            if (plane.Raycast(ray, out float d)) return ray.GetPoint(d);
        }
        return new Vector3(_camPivot.x, 0f, _camPivot.z);
    }

    // Paste the clipboard into the active environment, group centroid anchored at PasteAnchor().
    // XZ-only translation: position[1] is a render-time offset above the terrain-sampled resting
    // height (see WorldRenderer grounding), so vertical offsets and slope draping come for free.
    private void PasteClipboard()
    {
        if (_clipboard.Count == 0 || ActiveLocked) return;
        var env = libraryBrowser?.EnsureWorkingEnvironment();
        if (env == null) return;

        // Cross-env: make sure the target env knows every pasted building's def. Shared
        // reference, never overwrites an id the target already has (its copy may be newer) —
        // same mechanism as placing a library building (FetchAndPlace → AddBuildingDef).
        foreach (var e in _clipboard)
        {
            if (!e.isBuilding) continue;
            bool known = libraryBrowser?.GetBuildingDef(e.bldg.buildingId) != null;
            if (!known && e.def != null) libraryBrowser?.AddBuildingDef(e.def);
        }

        // Group centroid (XZ) from the stored source positions.
        float cx = 0f, cz = 0f; int n = 0;
        foreach (var e in _clipboard)
        {
            var p = e.isBuilding ? e.bldg.position : e.obj.position;
            if (p == null || p.Length < 3) continue;
            cx += p[0]; cz += p[2]; n++;
        }
        if (n > 0) { cx /= n; cz /= n; }
        Vector3 anchor = PasteAnchor();

        _history?.RecordBefore(EditHistory.Scope.Environment, "Paste");

        var pasted = new List<(string id, bool isBuilding)>();
        foreach (var e in _clipboard)
        {
            if (e.isBuilding)
            {
                if (libraryBrowser?.GetBuildingDef(e.bldg.buildingId) == null)
                {
                    Debug.LogWarning($"[EditController] Paste: no BuildingDef for '{e.bldg.buildingId}' — skipped.");
                    continue;
                }
                var copy = CloneData(e.bldg);   // clone again so repeated pastes never alias
                copy.instanceId = Guid.NewGuid().ToString("D");
                copy.position   = Offset(copy.position);
                copy.included   = true;
                (env.buildingInstances ??= new List<BuildingInstance>()).Add(copy);
                pasted.Add((copy.instanceId, true));
            }
            else
            {
                var copy = CloneData(e.obj);
                copy.instanceId   = Guid.NewGuid().ToString("D");
                copy.position     = Offset(copy.position);
                copy.included     = true;
                copy.brushPainted = false;   // a paste is a deliberate edit — eraser brush must not bulk-delete it
                (env.objectInstances ??= new List<ObjectInstance>()).Add(copy);
                pasted.Add((copy.instanceId, false));
            }
        }
        if (pasted.Count == 0) return;

        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);

        // The pasted group becomes the new selection, ready to move/rotate as one.
        Deselect();   // also commits Transform mode back to Browse
        _selId = pasted[0].id; _selIsBuilding = pasted[0].isBuilding; _selGO = null;
        for (int i = 1; i < pasted.Count; i++)
            _extraSel.Add(new Sel { id = pasted[i].id, isBuilding = pasted[i].isBuilding, go = null, baseScale = Vector3.one });
        RebindSelection();
        return;

        float[] Offset(float[] p) => p != null && p.Length >= 3
            ? new[] { anchor.x + (p[0] - cx), p[1], anchor.z + (p[2] - cz) }
            : new[] { anchor.x, 0f, anchor.z };
    }

    private void ExitCurrentMode()
    {
        if (_envSelected) DeselectEnv();
        switch (_mode)
        {
            case EditMode.PlaceObject:   StopPlaceObject();          break;
            case EditMode.PlaceBuilding: StopPlaceBuilding();        break;
            case EditMode.EditBuilding:  ExitEditBuilding(save:true); break;
            case EditMode.Transform:     RevertTransform(); _mode = EditMode.Browse; break;
            case EditMode.DrawPath:      StopDrawPath();             break;
            case EditMode.EditPath:      StopEditPath();             break;
            case EditMode.DrawFence:     StopDrawFence();            break;
            case EditMode.EditFence:     StopEditFence();            break;
            case EditMode.PaintObjects:  StopPaintObjects();         break;
            case EditMode.PaintSurface:  StopPaintSurface();         break;
            case EditMode.Measure:       StopMeasure();              break;
            case EditMode.EditLot:       StopEditLot();              break;
        }
    }

    // Called by UIShell when the top command bar switches modes. Drops any in-progress placement,
    // terrain tool, or tile edit (committing tile changes) so edits never leak across modes. The
    // current selection is intentionally preserved so Place→Browse keeps the selected instance.
    public void ExitForModeSwitch()
    {
        ExitCurrentMode();
        if (_mode != EditMode.Browse) _mode = EditMode.Browse;
    }

    // -----------------------------------------------------------------------
    // OnGUI (right side, hidden while TileBuildingEditor is active)
    // -----------------------------------------------------------------------

    // The right rail is one inspector whose content swaps by the active command (Direction B).
    // Build is owned by TileBuildingEditor when a building is open; Generate is owned by
    // ModelRequesterUI. EditController hosts Browse / Place / Terrain / Manage (+ the Build
    // empty-state before a building is opened).
    private void OnGUI()
    {
        var cmd = UIMode.Current;

        // Floating measurement labels draw on top of the scene whenever the ruler is active,
        // independent of which rail is showing (the tool is only reachable from the Terrain rail).
        if (_mode == EditMode.Measure) DrawMeasureLabels();

        // Build (with a building open) and Generate are drawn by the other panels.
        if (cmd == AppMode.Generate) return;
        if (cmd == AppMode.Build && tileBuildingEditor != null && tileBuildingEditor.IsActive) return;

        var rect = new Rect(Screen.width - PANEL_W - UITheme.Margin, UITheme.RailTop, PANEL_W, Screen.height - UITheme.RailTop - UITheme.Margin);
        UITheme.PanelBackground(rect);
        GUILayout.BeginArea(UITheme.Inset(rect));

        switch (cmd)
        {
            case AppMode.Browse:  DrawBrowseRail();  break;
            case AppMode.Place:   DrawPlaceRail();   break;
            case AppMode.Terrain: DrawTerrainRail(); break;
            case AppMode.Manage:  DrawManageRail();  break;
            case AppMode.Build:   DrawBuildEmptyRail(); break;
        }

        GUILayout.EndArea();
    }

    // Shared locked-twin notice drawn instead of a rail's editing controls. The rails are the one
    // funnel into every tool, so gating here (plus the scene-input early-outs) covers editing without
    // scattering checks through each tool panel. Do NOT swap this for a GUI.enabled=false wrap — the
    // rail bodies reset GUI.enabled internally, which would silently re-enable their controls.
    private void DrawLockedNotice()
    {
        UITheme.Header("🔒 Locked — digital twin");
        UITheme.Note("This place is read-only. Use Save As in the library to make an editable copy, or unlock it from the Loaded list.");
    }

    // Browse — selection inspector (panel 4 in the spec). Shows the whole-environment transform
    // entry and, when an instance is selected, its move/rotate/scale + delete controls.
    private void DrawBrowseRail()
    {
        UITheme.Title(string.IsNullOrEmpty(_selId) ? "Inspector" : (_selIsBuilding ? "Selected building" : "Selected object"));
        if (ActiveLocked) { DrawLockedNotice(); return; }
        if (string.IsNullOrEmpty(_selId))
            UITheme.Note("Click an object in the scene to select it.");

        _panelScroll = GUILayout.BeginScrollView(_panelScroll);
        DrawEnvSelectSection();
        if (!string.IsNullOrEmpty(_selId)) DrawSelectionSection();
        GUILayout.EndScrollView();
    }

    private void DrawPlaceRail()
    {
        UITheme.Title("Place things");
        if (ActiveLocked) { DrawLockedNotice(); return; }
        UITheme.Note("See it before you place it. Pick one, then click the ground.");
        _panelScroll = GUILayout.BeginScrollView(_panelScroll);
        DrawPlaceSection();
        GUILayout.EndScrollView();
    }

    private void DrawTerrainRail()
    {
        UITheme.Title("Terrain");
        if (ActiveLocked) { DrawLockedNotice(); return; }
        _panelScroll = GUILayout.BeginScrollView(_panelScroll);
        DrawTerrainSection();
        GUILayout.EndScrollView();
    }

    private void DrawBuildEmptyRail()
    {
        UITheme.Title("Building editor");
        if (ActiveLocked) { DrawLockedNotice(); return; }
        UITheme.Note("Double-click a building in the scene, or open one from the library, to shape and paint it.");
    }

    private void DrawManageRail()
    {
        UITheme.Title("Manage");
        _panelScroll = GUILayout.BeginScrollView(_panelScroll);
        if (libraryBrowser != null) libraryBrowser.DrawManageContent();
        else UITheme.Note("Library unavailable.");
        GUILayout.EndScrollView();
    }

    // Site Settings: the active environment's real-world terrain dimensions (meters) and scale note.
    // Editing terrainSize resizes the in-scene Terrain (WorldRenderer.ApplyTerrainSize) so the ground
    // is true scale. Undoable + persisted via the standard Environment-scope flow.
    private void DrawSiteSettingsSection()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        UITheme.Header("Site");
        if (env?.site == null)
        {
            UITheme.Note("Load an environment to set its real-world size.");
            return;
        }

        // Re-sync the text buffers when the active environment changes (or on first draw).
        if (_siteFieldsEnvId != env.id)
        {
            _siteFieldsEnvId = env.id;
            var ts = env.site.terrainSize;
            _siteWidthStr = ts != null && ts.Length > 0 ? ts[0].ToString("0.##") : "";
            _siteLenStr   = ts != null && ts.Length > 1 ? ts[1].ToString("0.##") : "";
            _scaleNoteStr = env.site.scaleNote ?? "";
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Size (m):", GUILayout.Width(70f));
        _siteWidthStr = GUILayout.TextField(_siteWidthStr ?? "", GUILayout.Width(70f));
        GUILayout.Label("×", GUILayout.Width(14f));
        _siteLenStr   = GUILayout.TextField(_siteLenStr ?? "", GUILayout.Width(70f));
        GUILayout.EndHorizontal();

        GUILayout.Label("Scale note:");
        _scaleNoteStr = GUILayout.TextField(_scaleNoteStr ?? "");

        bool okW = float.TryParse(_siteWidthStr, out float w) && w > 0f;
        bool okL = float.TryParse(_siteLenStr,   out float l) && l > 0f;
        if (GUILayout.Button("Apply size & note", GUILayout.Height(UITheme.RowH)))
        {
            if (okW && okL) ApplySiteSettings(env, w, l, _scaleNoteStr);
            else UITheme.Note("Enter positive width and length in meters.");
        }

        // On-resize behavior: shared by the numeric Apply above and the Lot tool's rectangle drag.
        GUILayout.BeginHorizontal();
        GUILayout.Label("On resize:", GUILayout.Width(70f));
        if (GUILayout.Toggle(!_resizeScaleContent, "Keep in place", GUI.skin.button)) _resizeScaleContent = false;
        if (GUILayout.Toggle(_resizeScaleContent, "Scale content", GUI.skin.button))  _resizeScaleContent = true;
        GUILayout.EndHorizontal();
        UITheme.Note("1 unit = 1 m. Keep-in-place just resizes the ground; Scale-content rescales the layout to fill it.");

        DrawLotToolSection(env);

        var grid = GridOverlay();
        if (grid != null)
        {
            bool on = GUILayout.Toggle(grid.OverlayEnabled, "  Real-scale grid (1 / 5 / 10 m)");
            if (on != grid.OverlayEnabled) grid.OverlayEnabled = on;
        }

        DrawElevationSection(env);
    }

    // Lot tool: in-scene handles to resize the terrain rectangle or reshape the parcel polygon, plus
    // auto-fit shortcuts and an out-of-lot containment check.
    private void DrawLotToolSection(EnvironmentDef env)
    {
        UITheme.Divider();
        UITheme.Header("Lot shape");

        GUILayout.BeginHorizontal();
        bool editing = _mode == EditMode.EditLot;
        if (GUILayout.Toggle(editing, "Edit lot (handles)", GUI.skin.button, GUILayout.Height(UITheme.RowH)) && !editing)
            StartEditLot();
        if (editing && GUILayout.Button("Done", GUILayout.Width(54f))) StopEditLot();
        GUILayout.EndHorizontal();

        if (editing)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode:", GUILayout.Width(48f));
            if (GUILayout.Toggle(!_lotPolygonMode, "Rectangle", GUI.skin.button)) { if (_lotPolygonMode) SetLotPolygonMode(env, false); }
            if (GUILayout.Toggle(_lotPolygonMode, "Parcel", GUI.skin.button))     { if (!_lotPolygonMode) SetLotPolygonMode(env, true); }
            GUILayout.EndHorizontal();
            UITheme.Note(_lotPolygonMode
                ? "Drag vertices; click an edge to add one; Delete removes the selected vertex (min 3)."
                : "Drag the far corner / edge handles to resize the lot from the origin corner.");
            if (env.site.lotBoundary != null && env.site.lotBoundary.Length >= 3 &&
                GUILayout.Button("Reset parcel to rectangle")) ClearLotBoundary(env);
        }

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Fit terrain to lot")) FitTerrainToLot(env);
        if (GUILayout.Button("Fit lot to content")) FitLotToContent(env);
        GUILayout.EndHorizontal();

        int outside = CountOutsideLot(env);
        if (outside > 0)
        {
            UITheme.Note($"⚠ {outside} item(s) outside the lot.");
            if (GUILayout.Button("Clamp items to lot")) ClampItemsToLot(env);
        }
    }

    // Shrinks/grows the terrain rectangle so it hugs the parcel polygon's extent (+ small margin).
    private void FitTerrainToLot(EnvironmentDef env)
    {
        var poly = env?.site?.lotBoundary;
        if (poly == null || poly.Length < 3) { UITheme.Note("No parcel polygon to fit to."); return; }
        float maxX = 0f, maxZ = 0f;
        foreach (var p in poly)
        {
            if (p == null || p.Length < 2) continue;
            if (p[0] > maxX) maxX = p[0];
            if (p[1] > maxZ) maxZ = p[1];
        }
        if (maxX <= 0f || maxZ <= 0f) return;
        const float margin = 2f;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Fit terrain to lot");
        SetTerrainSize(env, maxX + margin, maxZ + margin);
        worldRenderer?.ApplyTerrainSize(env.site);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        SyncSiteFields(env);
    }

    // Grows/shrinks the terrain rectangle to enclose all placed content (+ margin).
    private void FitLotToContent(EnvironmentDef env)
    {
        if (env == null) return;
        if (!EnvironmentScale.ContentBounds(env, libraryBrowser?.CurrentBuildingDefs,
                                            out _, out _, out float maxX, out float maxZ))
        { UITheme.Note("Nothing placed to fit to."); return; }
        const float margin = 5f;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Fit lot to content");
        SetTerrainSize(env, maxX + margin, maxZ + margin);
        worldRenderer?.ApplyTerrainSize(env.site);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        SyncSiteFields(env);
    }

    // Counts building + object instances whose origin falls outside the effective parcel polygon.
    private int CountOutsideLot(EnvironmentDef env)
    {
        var poly = EnvironmentScale.EffectiveLotPolygon(env?.site);
        if (poly == null) return 0;
        int n = 0;
        if (env.buildingInstances != null)
            foreach (var b in env.buildingInstances)
                if (b?.position != null && b.position.Length >= 3 &&
                    !EnvironmentScale.PointInPolygon(b.position[0], b.position[2], poly)) n++;
        if (env.objectInstances != null)
            foreach (var o in env.objectInstances)
                if (o?.position != null && o.position.Length >= 3 &&
                    !EnvironmentScale.PointInPolygon(o.position[0], o.position[2], poly)) n++;
        return n;
    }

    // Projects every out-of-lot instance back to just inside the parcel.
    private void ClampItemsToLot(EnvironmentDef env)
    {
        var poly = EnvironmentScale.EffectiveLotPolygon(env?.site);
        if (poly == null) return;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Clamp items to lot");
        void Clamp(float[] pos)
        {
            if (pos == null || pos.Length < 3) return;
            if (EnvironmentScale.PointInPolygon(pos[0], pos[2], poly)) return;
            Vector2 c = EnvironmentScale.ClampInsidePolygon(new Vector2(pos[0], pos[2]), poly);
            pos[0] = c.x; pos[2] = c.y;
        }
        if (env.buildingInstances != null) foreach (var b in env.buildingInstances) Clamp(b?.position);
        if (env.objectInstances != null)   foreach (var o in env.objectInstances)   Clamp(o?.position);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    // Optional gentle elevation: a sparse set of (x, z, height) grade points the renderer bakes into
    // a low-res heightmap. Flat by default; objects and paths drape onto the grade automatically.
    private void DrawElevationSection(EnvironmentDef env)
    {
        int count = env.site.gradePoints?.Count ?? 0;
        _showElevation = GUILayout.Toggle(_showElevation, $"  Elevation (basic) — {count} pts", GUI.skin.button);
        if (!_showElevation) return;

        GUILayout.BeginHorizontal();
        GUILayout.Label("X", GUILayout.Width(14f)); _gradeXStr = GUILayout.TextField(_gradeXStr ?? "", GUILayout.Width(48f));
        GUILayout.Label("Z", GUILayout.Width(14f)); _gradeZStr = GUILayout.TextField(_gradeZStr ?? "", GUILayout.Width(48f));
        GUILayout.Label("H", GUILayout.Width(14f)); _gradeHStr = GUILayout.TextField(_gradeHStr ?? "", GUILayout.Width(48f));
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Add grade point"))
        {
            if (float.TryParse(_gradeXStr, out float gx) && float.TryParse(_gradeZStr, out float gz) &&
                float.TryParse(_gradeHStr, out float gh))
                AddGradePoint(env, gx, gz, gh);
            else UITheme.Note("Enter numeric X, Z, and height (m).");
        }
        if (count > 0 && GUILayout.Button("Clear elevation (flat)")) ClearGradePoints(env);
        UITheme.Note("Sparse points → gentle slope (inverse-distance). Flat when empty. Bakes a low-res heightmap.");
    }

    private void AddGradePoint(EnvironmentDef env, float x, float z, float h)
    {
        _history?.RecordBefore(EditHistory.Scope.Environment, "Add grade point");
        env.site.gradePoints ??= new List<GradePointDef>();
        env.site.gradePoints.Add(new GradePointDef { x = x, z = z, height = h });
        RebakeElevation(env);
        _gradeHStr = "";
    }

    private void ClearGradePoints(EnvironmentDef env)
    {
        _history?.RecordBefore(EditHistory.Scope.Environment, "Clear elevation");
        env.site.gradePoints = null;
        RebakeElevation(env);
    }

    // Re-bakes the heightmap and re-renders so draped objects/paths resample the new ground.
    private void RebakeElevation(EnvironmentDef env)
    {
        worldRenderer?.ApplyHeightmap(env.site);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
    }

    // Lazily attaches the true-scale grid overlay to the main camera (a pure viewing aid).
    private ScaleGridOverlay GridOverlay()
    {
        if (mainCamera == null) return null;
        var g = mainCamera.GetComponent<ScaleGridOverlay>();
        if (g == null) g = mainCamera.gameObject.AddComponent<ScaleGridOverlay>();
        return g;
    }

    private void ApplySiteSettings(EnvironmentDef env, float width, float length, string note)
    {
        _history?.RecordBefore(EditHistory.Scope.Environment, "Edit site size");
        var ts = env.site.terrainSize;
        float oldW = ts != null && ts.Length > 0 && ts[0] > 0f ? ts[0] : width;
        float oldL = ts != null && ts.Length > 1 && ts[1] > 0f ? ts[1] : length;

        // Scale-content rescales the whole layout to fill the new lot; keep-in-place only resizes the
        // ground (content stays at its world coordinates). Both end at the requested terrainSize.
        if (_resizeScaleContent && oldW > 0f && oldL > 0f)
            EnvironmentScale.ScaleEnvironmentXZ(env, Mathf.Clamp(width, 1f, 4000f) / oldW,
                                                     Mathf.Clamp(length, 1f, 4000f) / oldL, Vector2.zero);
        else
            SetTerrainSize(env, width, length);
        env.site.scaleNote = note;

        worldRenderer?.ApplyTerrainSize(env.site);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);

        // Reflect any clamping back into the fields.
        SyncSiteFields(env);
    }

    // Terrain editor: paths, object scatter brush, and ground-surface brush. Each tool's
    // controls expand only while that tool is the active mode, keeping the panel compact.
    private void DrawTerrainSection()
    {
        DrawSiteSettingsSection();

        UITheme.Divider();
        UITheme.Header("Terrain editor");

        // ---- Draw paths ----
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_mode == EditMode.DrawPath, "Draw paths", GUI.skin.button, GUILayout.Height(UITheme.RowH))
            && _mode != EditMode.DrawPath) StartDrawPath();
        if (_mode == EditMode.DrawPath && GUILayout.Button("Done", GUILayout.Width(54f))) StopDrawPath();
        GUILayout.EndHorizontal();

        bool drawingPath = _mode == EditMode.DrawPath;
        bool editingPath = _mode == EditMode.EditPath;
        if (drawingPath || editingPath)
        {
            if (drawingPath)
                _pathFreehand = GUILayout.Toggle(_pathFreehand,
                    _pathFreehand ? "  Freehand (drag)" : "  Straight (click; Enter/dbl-click ends)");
            else if (GUILayout.Button("Done editing path")) StopEditPath();

            GUILayout.Label($"Width: {_pathWidth:0.0} m");
            _pathWidth = GUILayout.HorizontalSlider(_pathWidth, 0.5f, 12f);
            GUILayout.Label($"Smoothing: {_pathSmoothing:0.00}  (0 = crisp corners, 1 = flowing)");
            _pathSmoothing = GUILayout.HorizontalSlider(_pathSmoothing, 0f, 1f);
            UITheme.Note($"Material: {_pathMaterial}");
            if (pathMaterialPalette?.entries != null && pathMaterialPalette.entries.Count > 0)
            {
                float h = Mathf.Clamp(8 + pathMaterialPalette.entries.Count * 26, 26, 130);
                _pathMatScroll = GUILayout.BeginScrollView(_pathMatScroll, GUILayout.Height(h));
                foreach (var e in pathMaterialPalette.entries)
                {
                    bool sel = string.Equals(e.id, _pathMaterial, StringComparison.OrdinalIgnoreCase);
                    if (GUILayout.Toggle(sel, e.id, GUI.skin.button, GUILayout.Height(UITheme.RowH)) && !sel) _pathMaterial = e.id;
                }
                GUILayout.EndScrollView();
            }
            else UITheme.Note("Add entries to PathMaterialPalette and assign it in the inspector.");

            if (drawingPath)
                UITheme.Note("Backspace undoes the last point · cursor snaps to nearby path ends · Esc cancels.");
            else
            {
                if (GUILayout.Button("Insert point (split longest segment)")) InsertPointLongestSegment();
                UITheme.Note("Drag the dots to reshape · click the line to insert · Delete removes the selected dot · Esc finishes.");
                // Width / smoothing / material edits commit live to the path being edited.
                var ep = CurrentEditPath();
                if (ep != null && (Mathf.Abs(ep.width - _pathWidth) > 0.001f ||
                                   Mathf.Abs(ep.smoothing - _pathSmoothing) > 0.001f ||
                                   !string.Equals(ep.material, _pathMaterial, StringComparison.OrdinalIgnoreCase)))
                {
                    _history?.BeginGesture(EditHistory.Scope.Environment, "Edit path");
                    CommitEditPath(reRender: false);
                }
            }
        }

        // List of existing paths with edit + delete (visible whenever the active env has any).
        var env = libraryBrowser?.CurrentEnvironment;
        int pathCount = env?.site?.paths?.Count ?? 0;
        if (pathCount > 0)
        {
            UITheme.Note($"Paths ({pathCount}):");
            _pathListScroll = GUILayout.BeginScrollView(_pathListScroll, GUILayout.Height(Mathf.Min(120, 4 + pathCount * 24)));
            for (int i = 0; i < env.site.paths.Count; i++)
            {
                var p = env.site.paths[i];
                if (p == null) continue;
                bool isEditing = editingPath && p.id == _pathEditId;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{i + 1}. {p.material}{(isEditing ? "  (editing)" : "")}");
                GUILayout.FlexibleSpace();
                if (!isEditing && GUILayout.Button("Edit", GUILayout.Width(46f))) { StartEditPath(p.id); break; }
                if (GUILayout.Button("✕", GUILayout.Width(28f))) { DeletePath(p.id); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        // ---- Draw fences ----
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_mode == EditMode.DrawFence, "Draw fences", GUI.skin.button, GUILayout.Height(UITheme.RowH))
            && _mode != EditMode.DrawFence) StartDrawFence();
        if (_mode == EditMode.DrawFence && GUILayout.Button("Done", GUILayout.Width(54f))) StopDrawFence();
        GUILayout.EndHorizontal();

        bool drawingFence = _mode == EditMode.DrawFence;
        bool editingFence = _mode == EditMode.EditFence;
        if (drawingFence || editingFence)
        {
            if (drawingFence)
                _fenceFreehand = GUILayout.Toggle(_fenceFreehand,
                    _fenceFreehand ? "  Freehand (drag)" : "  Straight (click; Enter/dbl-click ends)");
            else if (GUILayout.Button("Done editing fence")) StopEditFence();

            string heightLabel = _fenceHeight > 0f ? $"{_fenceHeight:0.0} m" : "palette default";
            GUILayout.Label($"Height: {heightLabel}");
            _fenceHeight = GUILayout.HorizontalSlider(_fenceHeight, 0f, 4f);
            GUILayout.Label($"Smoothing: {_fenceSmoothing:0.00}  (0 = crisp corners, 1 = flowing)");
            _fenceSmoothing = GUILayout.HorizontalSlider(_fenceSmoothing, 0f, 1f);
            UITheme.Note($"Type: {_fenceType}");
            if (fencePalette?.entries != null && fencePalette.entries.Count > 0)
            {
                float h = Mathf.Clamp(8 + fencePalette.entries.Count * 26, 26, 130);
                _fenceTypeScroll = GUILayout.BeginScrollView(_fenceTypeScroll, GUILayout.Height(h));
                foreach (var e in fencePalette.entries)
                {
                    if (e == null) continue;
                    bool sel = string.Equals(e.fenceType, _fenceType, StringComparison.OrdinalIgnoreCase);
                    if (GUILayout.Toggle(sel, e.fenceType, GUI.skin.button, GUILayout.Height(UITheme.RowH)) && !sel) _fenceType = e.fenceType;
                }
                GUILayout.EndScrollView();
            }
            else UITheme.Note("Add entries to FencePalette and assign it in the inspector.");

            if (drawingFence)
                UITheme.Note("Backspace undoes the last point · cursor snaps to nearby fence ends · Esc cancels.");
            else
            {
                if (GUILayout.Button("Insert point (split longest segment)")) InsertPointLongestSegmentFence();
                UITheme.Note("Drag the dots to reshape · click the line to insert · Delete removes the selected dot · Esc finishes.");
                // Type / height / smoothing edits commit live to the fence being edited.
                var ef = CurrentEditFence();
                if (ef != null && (Mathf.Abs(ef.height - _fenceHeight) > 0.001f ||
                                   Mathf.Abs(ef.smoothing - _fenceSmoothing) > 0.001f ||
                                   !string.Equals(ef.fenceType, _fenceType, StringComparison.OrdinalIgnoreCase)))
                {
                    _history?.BeginGesture(EditHistory.Scope.Environment, "Edit fence");
                    CommitEditFence(reRender: true);
                }
            }
        }

        // List of existing fences with edit + delete (visible whenever the active env has any).
        int fenceCount = env?.site?.fences?.Count ?? 0;
        if (fenceCount > 0)
        {
            UITheme.Note($"Fences ({fenceCount}):");
            _fenceListScroll = GUILayout.BeginScrollView(_fenceListScroll, GUILayout.Height(Mathf.Min(120, 4 + fenceCount * 24)));
            for (int i = 0; i < env.site.fences.Count; i++)
            {
                var f = env.site.fences[i];
                if (f == null) continue;
                bool isEditing = editingFence && f.id == _fenceEditId;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{i + 1}. {f.fenceType}{(isEditing ? "  (editing)" : "")}");
                GUILayout.FlexibleSpace();
                if (!isEditing && GUILayout.Button("Edit", GUILayout.Width(46f))) { StartEditFence(f.id); break; }
                if (GUILayout.Button("✕", GUILayout.Width(28f))) { DeleteFence(f.id); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        // ---- Paint objects (scatter brush) ----
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_mode == EditMode.PaintObjects, "Paint objects", GUI.skin.button, GUILayout.Height(UITheme.RowH))
            && _mode != EditMode.PaintObjects) StartPaintObjects(_brushPrefab);
        if (_mode == EditMode.PaintObjects && GUILayout.Button("Done", GUILayout.Width(54f))) StopPaintObjects();
        GUILayout.EndHorizontal();

        if (_mode == EditMode.PaintObjects)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(!_brushErase, "Paint", GUI.skin.button)) _brushErase = false;
            if (GUILayout.Toggle(_brushErase, "Erase", GUI.skin.button))  _brushErase = true;
            GUILayout.EndHorizontal();
            UITheme.Note($"Prefab: {(_brushPrefab ?? "— pick below —")}");
            if (prefabRegistry?.entries != null && prefabRegistry.entries.Count > 0)
            {
                float h = Mathf.Clamp(8 + prefabRegistry.entries.Count * 26, 26, 130);
                _brushPrefabScroll = GUILayout.BeginScrollView(_brushPrefabScroll, GUILayout.Height(h));
                foreach (var e in prefabRegistry.entries)
                {
                    bool sel = string.Equals(e.key, _brushPrefab, StringComparison.OrdinalIgnoreCase);
                    if (GUILayout.Toggle(sel, e.key, GUI.skin.button, GUILayout.Height(UITheme.RowH)) && !sel) _brushPrefab = e.key;
                }
                GUILayout.EndScrollView();
            }
            else UITheme.Note("Add entries to PrefabRegistry and assign it in the inspector.");
            GUILayout.Label($"Radius: {_brushRadius:0.0} m");
            _brushRadius = GUILayout.HorizontalSlider(_brushRadius, 0.5f, 30f);
            GUILayout.Label($"Density: {_brushDensity:0.00} /m²");
            _brushDensity = GUILayout.HorizontalSlider(_brushDensity, 0.01f, 0.5f);
            GUILayout.Label(_brushSpacing > 0f ? $"Min spacing: {_brushSpacing:0.0} m" : "Min spacing: off (may overlap)");
            _brushSpacing = GUILayout.HorizontalSlider(_brushSpacing, 0f, 15f);
            _brushRandomRot = GUILayout.Toggle(_brushRandomRot, "  Random rotation");
            GUILayout.Label($"Scale: {_brushScaleMin:0.00} – {_brushScaleMax:0.00}");
            _brushScaleMin = GUILayout.HorizontalSlider(_brushScaleMin, 0.2f, 2f);
            _brushScaleMax = GUILayout.HorizontalSlider(_brushScaleMax, 0.2f, 3f);
            if (_brushScaleMax < _brushScaleMin) _brushScaleMax = _brushScaleMin;
            UITheme.Note("Hold left-mouse and drag to paint. Esc exits.");
        }

        // ---- Paint ground (surface brush) ----
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_mode == EditMode.PaintSurface, "Paint ground", GUI.skin.button, GUILayout.Height(UITheme.RowH))
            && _mode != EditMode.PaintSurface) StartPaintSurface(_surfType);
        if (_mode == EditMode.PaintSurface && GUILayout.Button("Done", GUILayout.Width(54f))) StopPaintSurface();
        GUILayout.EndHorizontal();

        if (_mode == EditMode.PaintSurface)
        {
            UITheme.Note($"Surface: {_surfType}");
            if (terrainRegistry?.entries != null && terrainRegistry.entries.Count > 0)
            {
                float h = Mathf.Clamp(8 + terrainRegistry.entries.Count * 26, 26, 130);
                _surfTypeScroll = GUILayout.BeginScrollView(_surfTypeScroll, GUILayout.Height(h));
                foreach (var e in terrainRegistry.entries)
                {
                    bool sel = string.Equals(e.key, _surfType, StringComparison.OrdinalIgnoreCase);
                    if (GUILayout.Toggle(sel, e.key, GUI.skin.button, GUILayout.Height(UITheme.RowH)) && !sel) _surfType = e.key;
                }
                GUILayout.EndScrollView();
            }
            else UITheme.Note("Add entries to TerrainRegistry and assign it in the inspector.");
            GUILayout.Label($"Radius: {_surfRadius:0.0} m");
            _surfRadius = GUILayout.HorizontalSlider(_surfRadius, 0.5f, 30f);
            UITheme.Note("Hold left-mouse and drag to paint. Esc exits.");
        }

        // ---- Measure & calibrate ----
        DrawMeasureSection();
    }

    // Measure tool panel: live distance / length / area readouts, plus Scale Calibration which
    // rescales the whole environment so a measured span equals a known real-world distance.
    private void DrawMeasureSection()
    {
        UITheme.Divider();
        UITheme.Header("Measure & scale");

        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_mode == EditMode.Measure, "Measure", GUI.skin.button, GUILayout.Height(UITheme.RowH))
            && _mode != EditMode.Measure) StartMeasure();
        if (_mode == EditMode.Measure && GUILayout.Button("Done", GUILayout.Width(54f))) StopMeasure();
        GUILayout.EndHorizontal();

        if (_mode != EditMode.Measure)
        {
            UITheme.Note("Click ground points to read distance, path length, and area at true scale (1 unit = 1 m).");
            return;
        }

        var pts = MeasurePointsXZ(includeCursor: false);
        float length = EnvironmentScale.PolylineLength(pts);
        GUILayout.Label($"Points: {pts.Count}");
        if (pts.Count >= 2)
        {
            float straight = EnvironmentScale.Distance(pts[0], pts[pts.Count - 1]);
            GUILayout.Label($"Straight (first→last): {straight:0.00} m");
            GUILayout.Label($"Path length: {length:0.00} m");
        }
        if (pts.Count >= 3)
            GUILayout.Label($"Area: {EnvironmentScale.PolygonArea(pts):0.00} m²");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Clear")) { _measurePts.Clear(); UpdateMeasureOverlay(); }
        GUILayout.EndHorizontal();
        UITheme.Note("Click to add points · Backspace removes last · Esc clears, then exits.");

        // ---- Scale Calibration (uses the first two points as the known span) ----
        if (pts.Count >= 2)
        {
            UITheme.Divider();
            UITheme.Note("Calibrate: rescale the whole environment so the measured span below equals its real length.");
            float measured = EnvironmentScale.Distance(pts[0], pts[1]);
            GUILayout.Label($"Measured span (points 1→2): {measured:0.00} m");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Real length (m):", GUILayout.Width(110f));
            _calibRealStr = GUILayout.TextField(_calibRealStr ?? "", GUILayout.Width(80f));
            GUILayout.EndHorizontal();

            bool parsed = float.TryParse(_calibRealStr, out float real) && real > 0f;
            if (parsed && EnvironmentScale.TryComputeFactor(measured, real, out float factor))
            {
                GUILayout.Label($"Scale factor: ×{factor:0.000}");
                if (GUILayout.Button("Apply scale to environment", GUILayout.Height(UITheme.RowH)))
                    ApplyCalibration(measured, real, new Vector2(pts[0][0], pts[0][1]));
            }
            else if (!string.IsNullOrEmpty(_calibRealStr))
            {
                UITheme.Note(measured < EnvironmentScale.MIN_MEASURED
                    ? "Measured span is too small — place the two points farther apart."
                    : "Enter a real length that yields a factor between ×0.01 and ×100.");
            }
        }
    }

    // Rescales the entire active environment about the first calibration point so the measured span
    // becomes `real` meters. One Environment-scope snapshot covers the whole multi-field mutation, so
    // a single Undo reverts everything. Persists via MarkDirty + re-render (existing PutEnvironment flow).
    private void ApplyCalibration(float measured, float real, Vector2 pivotXZ)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null) { Debug.LogWarning("[EditController] No active environment to calibrate."); return; }
        if (ActiveLocked) { Debug.LogWarning("[EditController] Locked (digital twin) — calibration refused."); return; }
        if (!EnvironmentScale.TryComputeFactor(measured, real, out float factor)) return;

        _history?.RecordBefore(EditHistory.Scope.Environment, "Calibrate scale");
        if (!EnvironmentScale.ScaleEnvironment(env, factor, pivotXZ)) return;

        // Calibration changes terrainSize → push it to the in-scene Terrain before repaint.
        worldRenderer?.ApplyTerrainSize(env.site);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);

        // Re-anchor the live measurement to the new scale so the readout immediately reflects truth.
        for (int i = 0; i < _measurePts.Count; i++)
        {
            Vector3 p = _measurePts[i];
            _measurePts[i] = new Vector3(pivotXZ.x + (p.x - pivotXZ.x) * factor, p.y,
                                         pivotXZ.y + (p.z - pivotXZ.y) * factor);
        }
        _calibRealStr = "";
        UpdateMeasureOverlay();
    }

    private void DrawPlaceSection()
    {
        // Active-placement banner.
        if (_mode == EditMode.PlaceObject)
        {
            UITheme.Note($"Placing '{_placeType}' — scroll to rotate");
            if (UITheme.GhostButton("Click ground · Esc to stop")) StopPlaceObject();
        }
        else if (_mode == EditMode.PlaceBuilding)
        {
            UITheme.Note("Placing building — scroll to rotate");
            if (UITheme.GhostButton("Click ground · Esc to stop")) StopPlaceBuilding();
        }

        // Search + category chips (real groups in our data: objects vs buildings).
        _placeSearch = GUILayout.TextField(_placeSearch);
        GUILayout.BeginHorizontal();
        if (UITheme.Chip("All",       _placeFilter == 0)) _placeFilter = 0;
        if (UITheme.Chip("Objects",   _placeFilter == 1)) _placeFilter = 1;
        if (UITheme.Chip("Buildings", _placeFilter == 2)) _placeFilter = 2;
        GUILayout.EndHorizontal();

        // Auto-load the building list the first time this panel is shown.
        if (!_bldgListFetched && !_bldgListLoading && libraryClient != null) FetchBuildingList();

        _prefabScroll = GUILayout.BeginScrollView(_prefabScroll);

        // Object prefabs as a thumbnail grid.
        if (_placeFilter != 2 && prefabRegistry?.entries != null)
        {
            UITheme.Header("Objects");
            BeginThumbGrid();
            foreach (var e in prefabRegistry.entries)
            {
                if (!Contains(e.key, _placeSearch)) continue;
                var tex = ThumbnailCache.GetPrefab(e.prefab);
                bool sel = _mode == EditMode.PlaceObject && e.key == _placeType;
                if (ThumbCell(tex, e.key, sel)) StartPlaceObject(e.key);
            }
            EndThumbGrid();
        }

        // Buildings (tile defs — no prefab preview, shown as labelled tiles).
        if (_placeFilter != 1)
        {
            GUILayout.BeginHorizontal();
            UITheme.Header("Buildings");
            GUILayout.FlexibleSpace();
            if (UITheme.GhostButton("↻", GUILayout.Width(30))) FetchBuildingList();
            GUILayout.EndHorizontal();
            if (_bldgListLoading) UITheme.Note("Loading…");
            else
            {
                BeginThumbGrid();
                foreach (var s in _bldgList)
                {
                    if (!Contains(s.name ?? s.id, _placeSearch)) continue;
                    if (ThumbCell(null, s.name ?? s.id, false)) FetchAndPlace(s.id);
                }
                EndThumbGrid();
            }
        }

        GUILayout.EndScrollView();
    }

    // ---- thumbnail grid helpers (3 columns to fit the right rail) ----
    private int _thumbCol;
    private const int ThumbCols = 3;
    private static bool Contains(string name, string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        (name ?? "").IndexOf(filter.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0;

    private void BeginThumbGrid() { _thumbCol = 0; GUILayout.BeginHorizontal(); }
    private void EndThumbGrid()   { GUILayout.EndHorizontal(); }
    private bool ThumbCell(Texture tex, string label, bool selected)
    {
        if (_thumbCol >= ThumbCols) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); _thumbCol = 0; }
        _thumbCol++;
        return UITheme.Thumb(tex, label, selected, 76f);
    }

    // "Select environment" entry point + group transform controls. Mirrors the per-instance
    // selection section but transforms every instance at once, about the environment center.
    private void DrawEnvSelectSection()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null) return;

        UITheme.Divider();
        UITheme.Header("Environment");

        if (!_envSelected)
        {
            if (GUILayout.Button("Select environment", GUILayout.Height(UITheme.RowH)))
                SelectEnvironment();
            return;
        }

        UITheme.Note("Whole environment selected • move/rotate/scale all (terrain stays put)");

        DrawToolSegmented();

        DrawEnvTransformControls(env);

        GUILayout.Space(2);
        if (GUILayout.Button("Deselect environment", GUILayout.Height(UITheme.RowH))) DeselectEnv();
    }

    private void DrawEnvTransformControls(EnvironmentDef env)
    {
        GUILayout.Space(4);
        switch (_tool)
        {
            case Tool.Move:
                GUILayout.Label($"Move all (step {MOVE_STEP:0.#})");
                DrawEnvMoveRow(env, "X", Vector3.right);
                DrawEnvMoveRow(env, "Z", Vector3.forward);
                DrawEnvMoveRow(env, "Y", Vector3.up);
                break;
            case Tool.Rotate:
                GUILayout.Label("Rotate all about center (Y, 15°)");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("⟲ −15°")) { EnvRotateYaw(env, -15f); AfterEnvEdit(env); }
                if (GUILayout.Button("⟳ +15°")) { EnvRotateYaw(env,  15f); AfterEnvEdit(env); }
                GUILayout.EndHorizontal();
                break;
            case Tool.Scale:
                GUILayout.Label("Scale all about center");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("− 10%")) { EnvScale(env, 0.9f); AfterEnvEdit(env); }
                if (GUILayout.Button("+ 10%")) { EnvScale(env, 1.1f); AfterEnvEdit(env); }
                GUILayout.EndHorizontal();
                break;
        }
    }

    private void DrawEnvMoveRow(EnvironmentDef env, string label, Vector3 axis)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(60f));
        if (GUILayout.Button("−", GUILayout.Width(40f))) { EnvMove(env, -axis * MOVE_STEP); AfterEnvEdit(env); }
        if (GUILayout.Button("+", GUILayout.Width(40f))) { EnvMove(env,  axis * MOVE_STEP); AfterEnvEdit(env); }
        GUILayout.EndHorizontal();
    }

    private void DrawSelectionSection()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null) return;

        UITheme.Divider();
        int total = 1 + _extraSel.Count;
        UITheme.Header(total > 1 ? $"Selected {total} instances"
                                 : $"Selected {(_selIsBuilding ? "building" : "object")}");
        if (total > 1) UITheme.Note("Move / rotate / scale apply to all • Shift/Ctrl+click to toggle");

        if (_selIsBuilding)
        {
            var inst = FindBI(env, _selId);
            if (inst == null) return;
            DrawIncludedToggle(env, inst.included, v => inst.included = v);
            if (tileBuildingEditor != null && GUILayout.Button("Edit tiles  (or double-click)"))
                EnterEditBuilding();
        }
        else
        {
            var inst = FindOI(env, _selId);
            if (inst == null) return;
            DrawIncludedToggle(env, inst.included, v => inst.included = v);
        }

        // Exact real-world dimensions (read-out, plus typed entry for massing-box objects).
        DrawDimensionSection(env);

        // Tool selector — only the active tool's controls are shown below.
        DrawToolSegmented();

        DrawTransformControls(env);

        // Whole-building shape skew sits right under the move/rotate/scale controls (buildings only).
        if (_selIsBuilding && total == 1) DrawBuildingSkewSection(env);

        GUILayout.Space(4);
        if (UITheme.DangerButton("Delete selected", GUILayout.Height(UITheme.RowH))) DeleteSelected();
    }

    // Included toggle shared by both selection branches; re-renders on change.
    private void DrawIncludedToggle(EnvironmentDef env, bool included, System.Action<bool> set)
    {
        bool inc = GUILayout.Toggle(included, "  Included in environment");
        if (inc != included)
        {
            _history?.RecordBefore(EditHistory.Scope.Environment, "Toggle included");
            set(inc);
            libraryBrowser?.MarkDirty();
            worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        }
    }

    // Exact real-world dimensions for the selection. Massing-box objects (boxSizeMeters, absolute
    // meters) get editable W/D/H fields; uniform prefabs and buildings get a read-out (resize via
    // Scale / Calibration). boxSizeMeters axis order is [width X, height Y, depth Z] (see WorldRenderer).
    private void DrawDimensionSection(EnvironmentDef env)
    {
        UITheme.Divider();
        UITheme.Header("Dimensions (m)");

        if (_selIsBuilding)
        {
            var bi = FindBI(env, _selId);
            BuildingDef def = null;
            var defs = libraryBrowser?.CurrentBuildingDefs;
            if (bi != null && defs != null) defs.TryGetValue(bi.buildingId, out def);
            Vector3 fp = EnvironmentScale.BuildingFootprint(def, bi);   // (W, D, H)
            if (fp == Vector3.zero) { UITheme.Note("Footprint unavailable."); return; }
            GUILayout.Label($"W {fp.x:0.0}  ·  D {fp.y:0.0}  ·  H {fp.z:0.0}");
            UITheme.Note("Resize with Scale below; cell size is set in the tile editor.");
            return;
        }

        var oi = FindOI(env, _selId);
        if (oi == null) return;

        if (oi.boxSizeMeters != null && oi.boxSizeMeters.Length >= 3)
        {
            if (_dimFieldsId != _selId)
            {
                _dimFieldsId = _selId;
                _dimWStr = oi.boxSizeMeters[0].ToString("0.##");   // X = width
                _dimDStr = oi.boxSizeMeters[2].ToString("0.##");   // Z = depth
                _dimHStr = oi.boxSizeMeters[1].ToString("0.##");   // Y = height
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("W", GUILayout.Width(14f)); _dimWStr = GUILayout.TextField(_dimWStr ?? "", GUILayout.Width(50f));
            GUILayout.Label("D", GUILayout.Width(14f)); _dimDStr = GUILayout.TextField(_dimDStr ?? "", GUILayout.Width(50f));
            GUILayout.Label("H", GUILayout.Width(14f)); _dimHStr = GUILayout.TextField(_dimHStr ?? "", GUILayout.Width(50f));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Set size", GUILayout.Height(UITheme.RowH)))
            {
                if (float.TryParse(_dimWStr, out float w) && w > 0f &&
                    float.TryParse(_dimDStr, out float d) && d > 0f &&
                    float.TryParse(_dimHStr, out float h) && h > 0f)
                    ApplyBoxSize(env, oi, w, h, d);
                else UITheme.Note("Enter positive meters for W, D, H.");
            }
            return;
        }

        // Uniform prefab: live render bounds are the true size; expose read-only (scale resizes it).
        var go = worldRenderer?.GetInstanceGO(_selId);
        var rends = go != null ? go.GetComponentsInChildren<Renderer>() : null;
        if (rends != null && rends.Length > 0)
        {
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            Vector3 s = b.size;
            GUILayout.Label($"W {s.x:0.0}  ·  D {s.z:0.0}  ·  H {s.y:0.0}");
        }
        UITheme.Note($"Scale {oi.scale:0.##} — resize with Scale, or Measure → Calibrate for true scale.");
    }

    private void ApplyBoxSize(EnvironmentDef env, ObjectInstance oi, float w, float h, float d)
    {
        _history?.RecordBefore(EditHistory.Scope.Environment, "Set object size");
        oi.boxSizeMeters[0] = w;   // X = width
        oi.boxSizeMeters[1] = h;   // Y = height
        oi.boxSizeMeters[2] = d;   // Z = depth
        // The massing box GO's localScale is exactly boxSizeMeters (see WorldRenderer), so update the
        // live object in place — keeps the current selection/gizmo intact (no full re-render needed).
        var go = worldRenderer?.GetInstanceGO(oi.instanceId);
        if (go != null) go.transform.localScale = new Vector3(w, h, d);
        libraryBrowser?.MarkDirty();
        _dimFieldsId = null;   // re-sync the fields next frame
    }

    // Move / Rotate / Scale as a segmented control (spec panel 4). Selecting enters Transform mode
    // for an instance, or just swaps the tool for a whole-environment selection.
    private void DrawToolSegmented()
    {
        int ts = UITheme.Segmented((int)_tool, new[] { "Move", "Rotate", "Scale" });
        if (ts != (int)_tool)
        {
            if (_envSelected) _tool = (Tool)ts;
            else              EnterTransform((Tool)ts);
        }
    }

    // Numeric transform interface: only the active tool's controls show, a drag-free
    // alternative to the gizmo. Deltas route through Apply*Delta so GO and data stay in sync.
    private void DrawTransformControls(EnvironmentDef env)
    {
        GUILayout.Space(4);
        switch (_tool)
        {
            case Tool.Move:   DrawMoveControls(env);   break;
            case Tool.Rotate: DrawRotateControls(env); break;
            case Tool.Scale:  DrawScaleControls(env);  break;
        }
    }

    // Translate along each world axis via −/+ step buttons (the gizmo arrows do the same).
    private void DrawMoveControls(EnvironmentDef env)
    {
        GUILayout.Label($"Position (step {MOVE_STEP:0.#})");
        DrawMoveAxisRow(env, "X", Vector3.right);
        DrawMoveAxisRow(env, "Y", Vector3.up);
        DrawMoveAxisRow(env, "Z", Vector3.forward);
    }

    private void DrawMoveAxisRow(EnvironmentDef env, string label, Vector3 axis)
    {
        float v = Vector3.Dot(GetInstancePos(env), axis);
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{label} {v:F1}", GUILayout.Width(60f));
        if (GUILayout.Button("−", GUILayout.Width(40f))) { ApplyPosDelta(env, -axis * MOVE_STEP); libraryBrowser?.MarkDirty(); }
        if (GUILayout.Button("+", GUILayout.Width(40f))) { ApplyPosDelta(env,  axis * MOVE_STEP); libraryBrowser?.MarkDirty(); }
        GUILayout.EndHorizontal();
    }

    private void DrawRotateControls(EnvironmentDef env)
    {
        GUILayout.Label("Rotation (hold Shift to snap 15°)");
        // Multi-selection only: single instances rotate identically either way. The toggle drives
        // every rotate path (R-drag, gizmo ring, Y slider), not just the panel.
        if (_extraSel.Count > 0)
        {
            _rotateGroupPivot = GUILayout.Toggle(_rotateGroupPivot,
                _rotateGroupPivot ? "Pivot: selection center" : "Pivot: each object", GUI.skin.button);
            UITheme.Note(_rotateGroupPivot
                ? "Yaw orbits the whole group around its shared center (positions move)."
                : "Each object spins in place around its own pivot.");
        }
        DrawRotAxisSlider(env, "X", 0);
        DrawRotAxisSlider(env, "Y", 1);
        DrawRotAxisSlider(env, "Z", 2);
    }

    private void DrawScaleControls(EnvironmentDef env)
    {
        float sc     = GetInstanceScale(env);
        float dispSc = Mathf.Clamp(sc, 0.1f, 5f);  // compare against the clamped value so an out-of-range scale isn't silently rescaled
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Scale {sc:F2}", GUILayout.Width(70f));
        float newSc = GUILayout.HorizontalSlider(dispSc, 0.1f, 5f);
        GUILayout.EndHorizontal();
        if (Mathf.Abs(newSc - dispSc) > 0.0001f)
        {
            _history?.BeginGesture(EditHistory.Scope.Environment, "Scale");   // coalesce the slider drag
            ApplyScaleDelta(env, newSc - sc);
            libraryBrowser?.MarkDirty();
        }
    }

    // One 0–360° slider for a single euler axis (0=X, 1=Y, 2=Z). Free by default; while Shift
    // is held the applied value snaps to ROT_SNAP_DEG (feeding it back makes the thumb step).
    private void DrawRotAxisSlider(EnvironmentDef env, string label, int axis)
    {
        Vector3 r = GetInstanceRot(env);
        float cur = Mathf.Repeat(r[axis], 360f);
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{label} {cur:F0}°", GUILayout.Width(50f));
        float snapped = SnapIf(GUILayout.HorizontalSlider(cur, 0f, 360f));
        GUILayout.EndHorizontal();
        if (Mathf.Abs(Mathf.DeltaAngle(snapped, cur)) > 0.01f)
        {
            _history?.BeginGesture(EditHistory.Scope.Environment, "Rotate");   // coalesce the slider drag
            // Group pivot: an absolute yaw is ambiguous across instances with different headings,
            // so treat the slider movement (relative to the primary, whose value it shows) as a
            // group delta — ApplyRotDelta orbits positions around the selection center.
            if (axis == 1 && _rotateGroupPivot && _extraSel.Count > 0)
                ApplyRotDelta(env, new Vector3(0f, Mathf.DeltaAngle(cur, snapped), 0f));
            else
                ApplyRotAxisAbsolute(env, axis, snapped);   // applies to the whole selection
            libraryBrowser?.MarkDirty();
        }
    }

    // -----------------------------------------------------------------------
    // Building skew — whole-building deform on the selected building's BuildingDef. Collapsible
    // foldout under the transform controls; mirrors the old tile-editor Skew tool but operates on the
    // selected placed building. Apply/Reset mutate the shared def, re-render, and persist it.
    // -----------------------------------------------------------------------

    private void DrawBuildingSkewSection(EnvironmentDef env)
    {
        UITheme.Divider();
        _showSkew = GUILayout.Toggle(_showSkew, _showSkew ? "▾  Skew shape" : "▸  Skew shape", GUI.skin.button);
        if (!_showSkew) return;

        UITheme.Note("Bend a footprint corner acute/obtuse, or slope a roof edge. Affects the whole building.");

        UITheme.Header("Mode");
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_skewMode == SkewMode.BendCorner, "Bend corner", GUI.skin.button) && _skewMode != SkewMode.BendCorner) _skewMode = SkewMode.BendCorner;
        if (GUILayout.Toggle(_skewMode == SkewMode.SlopeEdge,  "Slope edge",  GUI.skin.button) && _skewMode != SkewMode.SlopeEdge)  _skewMode = SkewMode.SlopeEdge;
        GUILayout.EndHorizontal();

        if (_skewMode == SkewMode.BendCorner)
        {
            UITheme.Header("Corner");
            GUILayout.BeginHorizontal();
            foreach (TileDeformField.Corner c in Enum.GetValues(typeof(TileDeformField.Corner)))
            {
                bool on = _skewCorner == c;
                if (GUILayout.Toggle(on, c.ToString(), GUI.skin.button) && !on) _skewCorner = c;
            }
            GUILayout.EndHorizontal();

            UITheme.Header($"Angle   {_skewAngle:F0}°");
            _skewAngle = Mathf.Round(GUILayout.HorizontalSlider(_skewAngle, -75f, 75f));
            UITheme.Note("Constant slant to the far end (trapezoid). − acute · + obtuse");
        }
        else
        {
            UITheme.Header("Edge");
            GUILayout.BeginHorizontal();
            foreach (TileDeformField.Edge e in Enum.GetValues(typeof(TileDeformField.Edge)))
            {
                bool on = _skewEdge == e;
                if (GUILayout.Toggle(on, e.ToString(), GUI.skin.button) && !on) _skewEdge = e;
            }
            GUILayout.EndHorizontal();

            UITheme.Header($"Rise   {_skewRise:F2} cells");
            _skewRise = GUILayout.HorizontalSlider(_skewRise, -2f, 2f);

            UITheme.Header($"Falloff   {_skewFalloff:F1} tiles");
            _skewFalloff = GUILayout.HorizontalSlider(_skewFalloff, 1f, 12f);

            GUILayout.BeginHorizontal();
            bool smooth = _skewCurve == TileDeformField.Falloff.Smooth;
            if (GUILayout.Toggle(smooth,  "Smooth", GUI.skin.button) && !smooth) _skewCurve = TileDeformField.Falloff.Smooth;
            if (GUILayout.Toggle(!smooth, "Linear", GUI.skin.button) && smooth)  _skewCurve = TileDeformField.Falloff.Linear;
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(2);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Apply", GUILayout.Height(UITheme.RowH))) ApplyBuildingSkew(env);
        if (GUILayout.Button("Reset", GUILayout.Height(UITheme.RowH))) ResetBuildingSkew(env);
        GUILayout.EndHorizontal();
        UITheme.Note("Apply stacks onto the current shape; Reset clears all skew.");
    }

    // The BuildingDef backing the selected building instance (the shared def in the active env's
    // building dict), or null when no building is selected.
    private BuildingDef SelectedBuildingDef(EnvironmentDef env)
    {
        if (!_selIsBuilding || string.IsNullOrEmpty(_selId)) return null;
        var inst = FindBI(env, _selId);
        var defs = libraryBrowser?.CurrentBuildingDefs;
        if (inst == null || defs == null) return null;
        return defs.TryGetValue(inst.buildingId, out var b) ? b : null;
    }

    private void ApplyBuildingSkew(EnvironmentDef env)
    {
        var bdef = SelectedBuildingDef(env);
        if (bdef == null) return;
        _history?.RecordBefore(EditHistory.Scope.Building, "Apply skew");
        if (_skewMode == SkewMode.BendCorner)
            TileDeformField.ApplyCornerBend(bdef, _skewCorner, _skewAngle);
        else
            TileDeformField.ApplySlopedEdge(bdef, _skewEdge, _skewRise, _skewFalloff, _skewCurve);
        AfterBuildingSkew(env, bdef);
    }

    private void ResetBuildingSkew(EnvironmentDef env)
    {
        var bdef = SelectedBuildingDef(env);
        if (bdef == null) return;
        _history?.RecordBefore(EditHistory.Scope.Building, "Reset skew");
        TileDeformField.ClearDeform(bdef);
        AfterBuildingSkew(env, bdef);
    }

    // Re-render so the new silhouette shows and persist the mutated def (it's a separate server
    // resource from the environment). The re-render destroys the selected GO; Update→RebindSelection
    // re-acquires it next frame, so the building stays selected.
    private void AfterBuildingSkew(EnvironmentDef env, BuildingDef bdef)
    {
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
        libraryBrowser?.AddBuildingDef(bdef);   // keep the in-memory dict copy in sync
        libraryClient?.PutBuilding(bdef,
            ()  => Debug.Log($"[EditController] Building '{bdef.name}' skewed & saved."),
            err => Debug.LogError($"[EditController] Save skewed building failed: {err}"));
    }

    // -----------------------------------------------------------------------
    // Building library (PlaceBuilding)
    // -----------------------------------------------------------------------

    private void FetchBuildingList()
    {
        _bldgListLoading = true; _bldgListFetched = true; _bldgList.Clear();
        libraryClient?.GetBuildings(
            list => { _bldgList = list; _bldgListLoading = false; },
            err  => { Debug.LogError($"[EditController] GetBuildings: {err}"); _bldgListLoading = false; });
    }

    private void FetchAndPlace(string bldgId)
    {
        libraryClient?.GetBuilding(bldgId,
            bdef => { libraryBrowser?.AddBuildingDef(bdef); StartPlaceBuilding(bdef.id); },
            err  => Debug.LogError($"[EditController] GetBuilding {bldgId}: {err}"));
    }

    // -----------------------------------------------------------------------
    // Utilities
    // -----------------------------------------------------------------------

    private Vector3 GroundPoint()
    {
        var plane = new Plane(Vector3.up, Vector3.zero);
        Ray ray   = mainCamera.ScreenPointToRay(MousePos);
        return plane.Raycast(ray, out float d) ? ray.GetPoint(d) : Vector3.zero;
    }

    private bool IsMouseOverUI()
    {
        float mx = MousePos.x;
        // Left = LibraryBrowser panel; right = this panel (both include their outer margin).
        float leftEdge  = UITheme.LeftPanelWidth + UITheme.Margin * 2f;
        float rightEdge = Screen.width - PANEL_W - UITheme.Margin * 2f;
        return mx < leftEdge || mx > rightEdge;
    }

    // Top-most instance under the cursor (nearest hit), or false when none. Lets the
    // user re-select a different object without first leaving the current mode.
    private bool TryPickInstance(out InstanceMarker marker, out GameObject go)
    {
        marker = null; go = null;
        Ray ray = mainCamera.ScreenPointToRay(MousePos);
        float best = float.MaxValue;
        foreach (var h in Physics.RaycastAll(ray, 1000f))
        {
            var m = h.collider.GetComponentInParent<InstanceMarker>();
            if (m != null && h.distance < best) { best = h.distance; marker = m; go = m.gameObject; }
        }
        return marker != null;
    }

    private Vector3 GetInstancePos(EnvironmentDef env)
    {
        float[] p = _selIsBuilding ? FindBI(env, _selId)?.position : FindOI(env, _selId)?.position;
        return (p != null && p.Length >= 3) ? new Vector3(p[0], p[1], p[2]) : Vector3.zero;
    }
    private Vector3 GetInstanceRot(EnvironmentDef env) => GetRotFor(env, _selId, _selIsBuilding);
    private Vector3 GetRotFor(EnvironmentDef env, string id, bool isBuilding)
    {
        if (isBuilding) { var i = FindBI(env, id); return i != null ? new Vector3(i.rotationX, i.rotationY, i.rotationZ) : Vector3.zero; }
        else            { var i = FindOI(env, id); return i != null ? new Vector3(i.rotationX, i.rotationY, i.rotationZ) : Vector3.zero; }
    }

    // The prefab's authored orientation, which the renderer composes under the instance
    // rotation; the live object must match. Buildings carry no registry prefab → identity.
    private Quaternion SelectedBaseRotation(EnvironmentDef env) => BaseRotationFor(env, _selId, _selIsBuilding);
    private Quaternion BaseRotationFor(EnvironmentDef env, string id, bool isBuilding)
    {
        if (isBuilding || prefabRegistry == null) return Quaternion.identity;
        var inst   = FindOI(env, id);
        var prefab = inst != null ? prefabRegistry.GetPrefab(inst.prefabType) : null;
        return prefab != null ? prefab.transform.rotation : Quaternion.identity;
    }
    private float GetInstanceScale(EnvironmentDef env) => GetScaleFor(env, _selId, _selIsBuilding);
    private float GetScaleFor(EnvironmentDef env, string id, bool isBuilding) =>
        isBuilding ? (FindBI(env, id)?.scale ?? 1f) : (FindOI(env, id)?.scale ?? 1f);

    private static BuildingInstance FindBI(EnvironmentDef env, string id)
    {
        if (env?.buildingInstances == null) return null;
        foreach (var b in env.buildingInstances) if (b.instanceId == id) return b;
        return null;
    }
    private static ObjectInstance FindOI(EnvironmentDef env, string id)
    {
        if (env?.objectInstances == null) return null;
        foreach (var o in env.objectInstances) if (o.instanceId == id) return o;
        return null;
    }

    // -----------------------------------------------------------------------
    // Selection highlight — MaterialPropertyBlock tint: no material instantiation,
    // and fully reverted by clearing the block (SetPropertyBlock(null)).
    // -----------------------------------------------------------------------

    private static readonly int BaseColorProp = Shader.PropertyToID("_BaseColor"); // URP Lit
    private static readonly int ColorProp     = Shader.PropertyToID("_Color");     // Built-in/legacy
    private static readonly Color HIGHLIGHT_TINT = new(1f, 0.72f, 0.2f);
    private static MaterialPropertyBlock _highlightBlock;

    private static void ApplyHighlight(GameObject go)
    {
        if (go == null) return;
        _highlightBlock ??= new MaterialPropertyBlock();
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            _highlightBlock.Clear();
            r.GetPropertyBlock(_highlightBlock);
            _highlightBlock.SetColor(BaseColorProp, HIGHLIGHT_TINT);
            _highlightBlock.SetColor(ColorProp,     HIGHLIGHT_TINT);
            r.SetPropertyBlock(_highlightBlock);
        }
    }

    private static void RemoveHighlight(GameObject go)
    {
        if (go == null) return;
        foreach (var r in go.GetComponentsInChildren<Renderer>())
            r.SetPropertyBlock(null);
    }

    private static void TintGhost(GameObject go)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = new Material(r.sharedMaterials[i]);
                if      (mats[i].HasProperty("_BaseColor")) mats[i].SetColor("_BaseColor", new Color(0.5f, 0.8f, 1f, 0.5f));
                else if (mats[i].HasProperty("_Color"))     mats[i].SetColor("_Color",     new Color(0.5f, 0.8f, 1f, 0.5f));
            }
            r.materials = mats;
        }
    }
}
