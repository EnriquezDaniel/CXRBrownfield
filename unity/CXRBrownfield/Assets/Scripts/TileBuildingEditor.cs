using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// M3: Tile-level building editor. Activated by EditController when entering EditBuilding mode.
// Instantiates tile prefabs directly for live editing; updates BuildingDef.tiles in memory.
// Call Enter() to start, ExitAndGet() to finish and retrieve the updated def.
public class TileBuildingEditor : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TileShapePalette tileShapePalette; // USER WIRES THIS IN INSPECTOR
    [SerializeField] private MaterialPalette  materialPalette;  // USER WIRES THIS IN INSPECTOR
    [SerializeField] private Camera           mainCamera;       // USER WIRES THIS IN INSPECTOR
    [SerializeField] private PrefabRegistry   prefabRegistry;   // USER WIRES THIS IN INSPECTOR (Decorate tool)
    [SerializeField] private DecorPalette     decorPalette;     // USER WIRES THIS IN INSPECTOR (Decorate tool)

    [Header("Grid visuals")]
    [SerializeField] private Color gridColor         = new Color(0.3f, 0.8f, 1f, 0.4f);
    [SerializeField] private Color hoverColor        = new Color(1f, 1f, 0f, 0.5f);
    [SerializeField] private Color editableFillColor = new Color(0.3f, 0.8f, 1f, 0.06f);  // faint fill over the editable region
    [SerializeField] private Color lowerFloorColor   = new Color(1f, 0.85f, 0.4f, 0.4f);  // outline of the floor below

    // -----------------------------------------------------------------------
    // Public state
    // -----------------------------------------------------------------------

    public bool        IsActive    { get; private set; }
    public BuildingDef CurrentDef  => _bdef;

    // World anchor the editor was opened at (corner-pivot origin / yaw). Lets a caller place the
    // edited building as an instance exactly where the live preview sat, with no visual jump.
    public Vector3 AnchorPos  => _bldgWorldPos;
    public float   AnchorRotY => _bldgRotY;

    // Shared undo/redo history (owned by EditController, set after construction). All tile-level
    // mutations snapshot the BuildingDef through this before changing it. See EditHistory.cs.
    public EditHistory History { get; set; }

    // The floor currently being edited (0 = ground). Lets EditController frame the orbit camera on it.
    public int ActiveFloor => _activeFloor;

    // World-space center of the building footprint at `floor`'s surface height, plus a rough
    // horizontal radius — lets EditController frame/lift the orbit camera onto the floor being edited.
    public bool TryGetFocus(int floor, out Vector3 worldCenter, out float radius)
    {
        float cs = CellSize();
        worldCenter = _bldgWorldPos;
        radius      = cs * 4f;
        if (_bdef == null) return false;

        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        if (_bdef.tiles != null)
            foreach (var t in _bdef.tiles)
            {
                if (t.gridX < minX) minX = t.gridX;
                if (t.gridX > maxX) maxX = t.gridX;
                if (t.gridZ < minZ) minZ = t.gridZ;
                if (t.gridZ > maxZ) maxZ = t.gridZ;
            }
        if (minX > maxX) { minX = 0; maxX = 3; minZ = 0; maxZ = 3; }   // empty building: small default

        float cx = (minX + maxX + 1) * 0.5f * cs;
        float cz = (minZ + maxZ + 1) * 0.5f * cs;
        Vector3 local = new Vector3(cx, floor * cs, cz);
        worldCenter = _bldgWorldPos + Quaternion.Euler(0f, _bldgRotY, 0f) * local;
        radius      = Mathf.Max((maxX - minX + 1) * cs, (maxZ - minZ + 1) * cs, cs) * 0.5f;
        return true;
    }

    // -----------------------------------------------------------------------
    // Runtime state
    // -----------------------------------------------------------------------

    private BuildingDef  _bdef;
    private Vector3      _bldgWorldPos;
    private float        _bldgRotY;
    private Transform    _tileRoot;

    // key = "floor_x_z"
    private readonly Dictionary<string, GameObject> _tileGOs = new();

    // Live GameObjects for painted decorations, keyed by EmbeddedObjectDef.instanceId. Independent of
    // _tileGOs (decorations are not floor-specific and survive floor switches / RebuildVisuals).
    private readonly Dictionary<string, GameObject> _embGOs = new();

    // Sub-tools inside the building editor. Add = paint tiles onto the grid; Select = pick a
    // placed tile and rotate it on any axis (snap 15°); Paint = assign a material to a clicked face;
    // Decorate = "smart paint" decorative prefabs (windows/doors/vents/greenery) onto tile faces.
    // (Whole-building Skew — acute/obtuse corners, sloped roof — now lives on the building selection
    // panel in EditController, next to Move/Rotate/Scale.)
    private enum SubTool { Select, Add, Paint, Decorate }   // order = tab order (Select is leftmost + default)
    private SubTool _subTool = SubTool.Select;
    private bool _confirmClearFloor;            // floor-clear confirmation (destructive, asks first)

    // Decorate tool state — paints EmbeddedObjectDef entries onto tile faces from a DecorPalette.
    // Pick a decor, click a tile face: one prop auto-centers, fits, and seats flush (like the Paint
    // tool assigning a face material). Dragging paints each face under the cursor; re-painting a face
    // replaces its prop (one decor per face). Erase removes painted decorations.
    private string  _activeDecorId;
    private bool    _decorErase;
    private string  _decorLastTileKey;         // last tile decorated this stroke (one per tile per drag)

    private int    _activeFloor       = 0;
    private string _activeShapeId     = "square";
    private int    _activeTileRotation = 0;          // placement yaw for NEW tiles: 0 / 90 / 180 / 270
    private string _activeMaterialId;
    private string _activeFaceName    = "north";
    private bool   _wholeFace;                        // Paint/Decorate: act on every exposed tile face on the clicked building side at once
    private string _hoveredKey;
    private bool   _isDragging;

    // Select-tool state — supports multi-select. _primaryKey is the last tile clicked and drives
    // the panel readouts; rotation/delete apply to every key in _selectedKeys.
    private readonly HashSet<string> _selectedKeys   = new();
    private string                   _primaryKey;            // reference tile for slider/coords, or null
    private readonly List<GameObject> _highlightedGOs = new(); // GOs currently carrying the tint
    private int        _rotAxis      = 1;            // active rotate axis: 0=X, 1=Y, 2=Z
    private bool       _dragSelecting;               // Select tool: left-drag accumulates tiles into the selection
    private const float ROT_SNAP = 15f;              // all Select-tool rotation snaps to this

    // Fires when the user clicks "Save Changes" — EditController handles the actual PUT.
    public event Action OnSaveRequested;

    private static Material _glMat;
    private const int PANEL_W = UITheme.RightPanelWidth;

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public void Enter(BuildingDef bdef, Vector3 worldPos, float rotY)
    {
        if (IsActive) ExitAndDiscard();

        _bdef               = bdef;
        _bldgWorldPos       = worldPos;
        _bldgRotY           = rotY;
        _activeFloor        = 0;
        _activeTileRotation = 0;
        _activeMaterialId   = null;
        _subTool            = SubTool.Select;
        _selectedKeys.Clear();
        _primaryKey         = null;
        _highlightedGOs.Clear();
        IsActive            = true;

        var rootGO = new GameObject($"TileEdit_{bdef.name}");
        rootGO.transform.SetParent(transform, false);
        rootGO.transform.position = worldPos;
        rootGO.transform.rotation = Quaternion.Euler(0f, rotY, 0f);
        _tileRoot = rootGO.transform;

        _decorErase       = false;
        _wholeFace        = false;

        RebuildVisuals();
        RebuildEmbeddedVisuals();
    }

    public BuildingDef ExitAndGet()
    {
        var result = _bdef;
        Cleanup();
        return result;
    }

    // Swaps the working def for a restored copy (undo/redo) and rebuilds the live geometry. The
    // editor must already be active; the world position/rotation anchor is unchanged.
    public void ReloadDef(BuildingDef bdef)
    {
        if (!IsActive || bdef == null) return;
        _bdef = bdef;
        ClearSelection();
        _hoveredKey        = null;
        _confirmClearFloor = false;
        if (_activeFloor >= _bdef.floors) _activeFloor = Mathf.Max(0, _bdef.floors - 1);
        RebuildVisuals();
        RebuildEmbeddedVisuals();
    }

    public void ExitAndDiscard() => Cleanup();

    // Called each frame by EditController while active.
    public void HandleInput()
    {
        if (!IsActive || mainCamera == null) return;

        var mouse = Mouse.current;
        var kb    = Keyboard.current;
        if (mouse == null || kb == null) return;

        UpdateHover();

        bool lDown  = mouse.leftButton.wasPressedThisFrame;
        bool lHeld  = mouse.leftButton.isPressed;
        bool lUp    = mouse.leftButton.wasReleasedThisFrame;
        bool delKey = kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame;

        // A press that starts over either GUI panel must not begin a tile-add drag,
        // otherwise dragging off the panel into the scene paints tiles unintentionally.
        bool overUI = IsMouseOverUI();
        if (lDown && !overUI) _isDragging = true;
        if (lUp)              _isDragging = false;

        // Keyboard tile shortcuts are suppressed while a panel text field/slider has focus, so
        // typing never rotates, re-axes, or deletes a tile.
        bool typing = GUIUtility.keyboardControl != 0;
        bool ctrl   = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
        if (!typing && !ctrl)   // Ctrl held = undo/redo (handled by EditController) — don't eat Z as the Z axis
        {
            // X/Y/Z choose the active rotate axis for the Select tool.
            if (kb.xKey.wasPressedThisFrame) _rotAxis = 0;
            if (kb.yKey.wasPressedThisFrame) _rotAxis = 1;
            if (kb.zKey.wasPressedThisFrame) _rotAxis = 2;

            HandleRotateKeys(kb);
        }

        UpdateAddGhost(overUI);

        if (overUI) return;

        switch (_subTool)
        {
            case SubTool.Paint:
                // Whole-face: a single click paints the entire building side the face belongs to.
                // Otherwise click, or hold-and-drag across tiles, to paint each face under the cursor.
                if (_wholeFace) { if (lDown) { History?.RecordBefore(EditHistory.Scope.Building, "Paint side"); TryPaintWholeFace(); } }
                else if (lDown || (_isDragging && lHeld)) { History?.BeginGesture(EditHistory.Scope.Building, "Paint face"); TryPaintFace(); }
                break;

            case SubTool.Select:
                bool additive = kb.leftShiftKey.isPressed  || kb.rightShiftKey.isPressed ||
                                kb.leftCtrlKey.isPressed    || kb.rightCtrlKey.isPressed;
                HandleSelectDrag(lDown, lHeld, lUp, additive);
                break;

            case SubTool.Decorate:
                if (lDown) _decorLastTileKey = null;   // reset the per-stroke one-per-tile gate
                // Whole-face (place only): a single click fills every exposed face on the clicked
                // building side with one prop each. Erase and per-face placement stay drag-driven.
                if (_wholeFace && !_decorErase) { if (lDown) { History?.RecordBefore(EditHistory.Scope.Building, "Decorate side"); TryDecorateWholeFace(); } }
                else if (lDown || (_isDragging && lHeld))
                {
                    History?.BeginGesture(EditHistory.Scope.Building, _decorErase ? "Erase decor" : "Decorate");
                    HandleDecorate(lDown);
                }
                break;

            default: // Add
                if ((lDown || (_isDragging && lHeld)) && _hoveredKey != null)
                {
                    History?.BeginGesture(EditHistory.Scope.Building, "Add tiles");
                    AddTileAt(_hoveredKey);
                }
                break;
        }

        if (delKey && !typing) HandleDelete();
    }

    // Q/E: Select tool → rotate the selected tile ±15° on the active axis; otherwise the legacy
    // behavior — rotate the hovered tile ±90° (yaw), or cycle the new-tile placement yaw.
    private void HandleRotateKeys(Keyboard kb)
    {
        int dir = 0;
        if (kb.qKey.wasPressedThisFrame) dir = -1;
        if (kb.eKey.wasPressedThisFrame) dir = +1;
        if (dir == 0) return;

        if (_subTool == SubTool.Select && _selectedKeys.Count > 0)
            RotateSelectedAxis(dir * ROT_SNAP);
        else if (_hoveredKey != null && TileExistsAt(_hoveredKey))
            RotateTileAt(_hoveredKey, dir * 90);
        else
            _activeTileRotation = ((_activeTileRotation + dir * 90) % 360 + 360) % 360;
    }

    private void HandleDelete()
    {
        if (_subTool == SubTool.Select && _selectedKeys.Count > 0)
            RemoveSelectedTiles();   // records its own undo entry
        else if (_hoveredKey != null && TileExistsAt(_hoveredKey))
        {
            History?.RecordBefore(EditHistory.Scope.Building, "Delete tile");
            RemoveTileAt(_hoveredKey);
        }
    }

    // Select tool: a plain press selects the tile under the cursor (replacing the selection); then
    // holding and dragging across the grid keeps adding each tile the cursor passes over to the
    // selection. Shift/Ctrl+click toggles a single tile in/out. A plain press on empty clears it.
    // Rotation of the selection is via Q/E, the panel sliders, and the ±15/±90 buttons.
    private void HandleSelectDrag(bool lDown, bool lHeld, bool lUp, bool additive)
    {
        if (lDown)
        {
            string hitKey = RaycastTileKey();
            if (hitKey != null)
            {
                if (additive) ToggleSelected(hitKey);
                else          SetSelectionSingle(hitKey);
                _dragSelecting = true;   // arm drag-to-add for the rest of this press
            }
            else if (!additive)
            {
                ClearSelection();
            }
        }
        if (lUp) _dragSelecting = false;

        // While held, accumulate every tile dragged over (never deselects — Shift/Ctrl+click removes).
        if (_dragSelecting && lHeld)
        {
            string k = RaycastTileKey();
            if (k != null && _selectedKeys.Add(k))
            {
                _primaryKey = k;
                ApplySelectionHighlight();
            }
        }
    }

    private bool TileExistsAt(string key)
    {
        if (!TryParseKey(key, out int gx, out int gz, out int gf)) return false;
        if (_bdef?.tiles == null) return false;
        foreach (var t in _bdef.tiles)
            if (t.gridX == gx && t.gridZ == gz && t.floor == gf) return true;
        return false;
    }

    // Quick yaw turn (Q/E legacy, 90° steps). Preserves any X/Z tilt set via the Select tool.
    private void RotateTileAt(string key, int deltaDeg)
    {
        var t = FindTile(key);
        if (t == null) return;
        History?.RecordBefore(EditHistory.Scope.Building, "Rotate tile");
        t.rotation = ((t.rotation + deltaDeg) % 360 + 360) % 360;
        ApplyTileRotationToGO(key, t);
    }

    // -----------------------------------------------------------------------
    // Select tool — rotate the selected tile on any axis, snapped to 15°
    // -----------------------------------------------------------------------

    // Adds delta degrees to the active axis of every selected tile (each rotates about its own
    // centre) and snaps every axis to 15°.
    private void RotateSelectedAxis(float deltaDeg)
    {
        if (_selectedKeys.Count == 0) return;
        History?.RecordBefore(EditHistory.Scope.Building, "Rotate tiles");
        foreach (var key in _selectedKeys)
        {
            var t = FindTile(key);
            if (t == null) continue;
            Vector3 e = TileEuler(t);
            e[_rotAxis] = e[_rotAxis] + deltaDeg;
            SetTileEuler(key, t, SnapEuler(e));
        }
    }

    // Sets one axis of every selected tile to an absolute (already 15°-snapped) value. Used by the
    // panel sliders so the numeric controls and the drag/keys all drive the same data.
    private void SetSelectedAxisAbsolute(int axis, float degrees)
    {
        foreach (var key in _selectedKeys)
        {
            var t = FindTile(key);
            if (t == null) continue;
            Vector3 e = TileEuler(t);
            e[axis] = degrees;
            SetTileEuler(key, t, SnapEuler(e));
        }
    }

    private void ResetSelectedRotation()
    {
        if (_selectedKeys.Count == 0) return;
        History?.RecordBefore(EditHistory.Scope.Building, "Reset tile rotation");
        foreach (var key in _selectedKeys)
        {
            var t = FindTile(key);
            if (t != null) SetTileEuler(key, t, Vector3.zero);
        }
    }

    private void RemoveSelectedTiles()
    {
        if (_selectedKeys.Count == 0) return;
        History?.RecordBefore(EditHistory.Scope.Building, "Delete tiles");
        // Copy keys first: RemoveTileAt mutates _selectedKeys.
        foreach (var key in new List<string>(_selectedKeys)) RemoveTileAt(key);
        ClearSelection();
    }

    // -----------------------------------------------------------------------
    // Selection management (multi-select)
    // -----------------------------------------------------------------------

    private void SetSelectionSingle(string key)
    {
        _selectedKeys.Clear();
        _selectedKeys.Add(key);
        _primaryKey = key;
        ApplySelectionHighlight();
    }

    private void ToggleSelected(string key)
    {
        if (!_selectedKeys.Remove(key))
        {
            _selectedKeys.Add(key);
            _primaryKey = key;
        }
        else if (_primaryKey == key)
        {
            _primaryKey = FirstSelected();
        }
        ApplySelectionHighlight();
    }

    private void SelectAll(bool activeFloorOnly)
    {
        _selectedKeys.Clear();
        if (_bdef?.tiles != null)
            foreach (var t in _bdef.tiles)
                if (!activeFloorOnly || t.floor == _activeFloor)
                    _selectedKeys.Add(MakeKey(t.gridX, t.gridZ, t.floor));
        _primaryKey = FirstSelected();
        ApplySelectionHighlight();
    }

    private string FirstSelected()
    {
        foreach (var k in _selectedKeys) return k;
        return null;
    }

    private static Vector3 TileEuler(TileDef t) => new(t.rotationX, t.rotation, t.rotationZ);

    private void SetTileEuler(string key, TileDef t, Vector3 e)
    {
        t.rotationX = Mathf.Repeat(e.x, 360f);
        t.rotation  = Mathf.RoundToInt(Mathf.Repeat(e.y, 360f));
        t.rotationZ = Mathf.Repeat(e.z, 360f);
        ApplyTileRotationToGO(key, t);
    }

    private void ApplyTileRotationToGO(string key, TileDef t)
    {
        if (_tileGOs.TryGetValue(key, out var go) && go != null)
            go.transform.localRotation = Quaternion.Euler(t.rotationX, t.rotation, t.rotationZ);
    }

    // Returns the tile key under the cursor (via collider raycast), or null.
    private string RaycastTileKey()
    {
        if (Mouse.current == null) return null;
        Ray ray = mainCamera.ScreenPointToRay((Vector2)Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 500f)) return null;
        var m = hit.collider.GetComponentInParent<TileInstanceMarker>();
        return m == null ? null : MakeKey(m.gridX, m.gridZ, m.floor);
    }

    // Re-tints exactly the currently-selected tiles. Rebuilt from scratch each call so it stays
    // correct after add/remove/rebuild without tracking incremental changes.
    private void ApplySelectionHighlight()
    {
        ClearSelectionHighlight();
        _selMpb ??= new MaterialPropertyBlock();
        foreach (var key in _selectedKeys)
        {
            if (!_tileGOs.TryGetValue(key, out var go) || go == null) continue;
            _highlightedGOs.Add(go);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                _selMpb.Clear();
                r.GetPropertyBlock(_selMpb);
                _selMpb.SetColor(BaseColorProp, SELECT_TINT);
                _selMpb.SetColor(ColorProp,     SELECT_TINT);
                r.SetPropertyBlock(_selMpb);
            }
        }
    }

    private void ClearSelectionHighlight()
    {
        foreach (var go in _highlightedGOs)
            if (go != null)
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    r.SetPropertyBlock(null);
        _highlightedGOs.Clear();
    }

    private void ClearSelection()
    {
        ClearSelectionHighlight();
        _selectedKeys.Clear();
        _primaryKey    = null;
        _dragSelecting = false;
    }

    private TileDef FindTile(string key)
    {
        if (!TryParseKey(key, out int gx, out int gz, out int gf)) return null;
        if (_bdef?.tiles == null) return null;
        foreach (var t in _bdef.tiles)
            if (t.gridX == gx && t.gridZ == gz && t.floor == gf) return t;
        return null;
    }

    private static float SnapAngle(float deg)   => Mathf.Round(deg / ROT_SNAP) * ROT_SNAP;
    private static Vector3 SnapEuler(Vector3 e) => new(SnapAngle(e.x), SnapAngle(e.y), SnapAngle(e.z));

    private static MaterialPropertyBlock _selMpb;
    private static readonly int BaseColorProp = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorProp     = Shader.PropertyToID("_Color");
    private static readonly Color SELECT_TINT = new(1f, 0.55f, 0.1f);

    // -----------------------------------------------------------------------
    // Tile operations
    // -----------------------------------------------------------------------

    private void AddTileAt(string key)
    {
        if (!TryParseKey(key, out int gx, out int gz, out int gf)) return;
        if (_bdef.tiles == null) _bdef.tiles = new List<TileDef>();

        foreach (var t in _bdef.tiles)
            if (t.gridX == gx && t.gridZ == gz && t.floor == gf) return;   // already exists

        var tile = new TileDef { gridX = gx, gridZ = gz, floor = gf, shapeId = _activeShapeId, rotation = _activeTileRotation };
        _bdef.tiles.Add(tile);
        SpawnTileGO(tile);
    }

    private void RemoveTileAt(string key)
    {
        if (!TryParseKey(key, out int gx, out int gz, out int gf)) return;
        _bdef.tiles?.RemoveAll(t => t.gridX == gx && t.gridZ == gz && t.floor == gf);
        if (_tileGOs.TryGetValue(key, out var go))
        {
            DestroyObject(go);
            _tileGOs.Remove(key);
        }
        if (_selectedKeys.Remove(key))
        {
            if (_primaryKey == key) _primaryKey = FirstSelected();
            ApplySelectionHighlight();   // drop the just-removed tile from the tint set
        }
        _hoveredKey = null;
    }

    private void TryPaintFace()
    {
        if (string.IsNullOrEmpty(_activeMaterialId) || string.IsNullOrEmpty(_activeFaceName)) return;
        if (Mouse.current == null) return;
        Ray ray = mainCamera.ScreenPointToRay((Vector2)Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 500f)) return;

        var marker = hit.collider.GetComponentInParent<TileInstanceMarker>();
        if (marker == null) return;

        if (_bdef.tiles == null) return;
        foreach (var t in _bdef.tiles)
        {
            if (t.gridX != marker.gridX || t.gridZ != marker.gridZ || t.floor != marker.floor) continue;
            string key = MakeKey(t.gridX, t.gridZ, t.floor);
            _tileGOs.TryGetValue(key, out var go);

            // §5 PaintMaterial: paint the face that was actually clicked. Prefer the exact
            // submesh hit (MeshCollider), then the surface normal (works with box colliders
            // too), and only fall back to the panel's face picker when neither resolves.
            string face = FaceFromHit(hit, t)
                          ?? FaceFromNormal(go, hit.normal)
                          ?? _activeFaceName;

            if (t.faceMaterials == null) t.faceMaterials = new Dictionary<string, string>();
            t.faceMaterials[face] = _activeMaterialId;
            if (go != null)
                TileSpawner.ApplyFaceMaterial(go, t, face, _activeMaterialId, tileShapePalette, materialPalette);
            break;
        }
    }

    // Determines which named face was clicked from the hit normal. Transforming the world
    // normal by the inverse of the tile GameObject's rotation removes both the building and
    // the per-tile rotation, yielding the normal in the prefab's unrotated local frame — so
    // the returned face name maps to the correct submesh regardless of how the tile is turned.
    // Convention: +Z=north, +X=east, -Z=south, -X=west, +Y=top, -Y=bottom.
    private static string FaceFromNormal(GameObject tileGO, Vector3 worldNormal)
    {
        if (tileGO == null || worldNormal.sqrMagnitude < 1e-6f) return null;
        Vector3 n = Quaternion.Inverse(tileGO.transform.rotation) * worldNormal;
        float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
        if (ay >= ax && ay >= az) return n.y >= 0f ? "top"   : "bottom";
        if (ax >= az)             return n.x >= 0f ? "east"  : "west";
        return                            n.z >= 0f ? "north" : "south";
    }

    // Maps the raycast hit triangle to a named face. Requires a MeshCollider on the tile
    // prefab; returns null otherwise (the caller then uses the normal- or panel-based face).
    private string FaceFromHit(RaycastHit hit, TileDef tile)
    {
        var mc = hit.collider as MeshCollider;
        if (mc == null || mc.sharedMesh == null || hit.triangleIndex < 0) return null;
        var entry = tileShapePalette != null ? tileShapePalette.GetEntry(tile.shapeId) : null;
        if (entry?.faceNames == null) return null;

        var mesh = mc.sharedMesh;
        int hitIndex = hit.triangleIndex * 3;
        for (int si = 0; si < mesh.subMeshCount; si++)
        {
            var sub = mesh.GetSubMesh(si);
            if (hitIndex >= sub.indexStart && hitIndex < sub.indexStart + sub.indexCount)
                return si < entry.faceNames.Count ? entry.faceNames[si] : null;
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Whole-face selection — act on every exposed tile face on a building side
    // -----------------------------------------------------------------------

    private static readonly string[] FaceNames = { "north", "south", "east", "west", "top", "bottom" };

    // Building-local outward direction each named face points to in the tile's unrotated frame.
    // Convention matches FaceFromNormal: +Z=north, +X=east, -Z=south, -X=west, +Y=top, -Y=bottom.
    // Single owner of the convention: TileFaceGeometry (shared with deform-aware decor placement).
    private static Vector3 FaceBaselineDir(string face) => TileFaceGeometry.BaselineDir(face);

    private static Vector3 SnapToAxis(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ay >= ax && ay >= az) return new Vector3(0f, Mathf.Sign(v.y), 0f);
        if (ax >= az)             return new Vector3(Mathf.Sign(v.x), 0f, 0f);
        return                           new Vector3(0f, 0f, Mathf.Sign(v.z));
    }

    // Raycasts a tile under the cursor and returns the clicked face's building-local outward axis
    // (one of ±X/±Y/±Z). False when nothing paintable is hit.
    private bool RaycastFaceDir(out Vector3 dirLocal)
    {
        dirLocal = Vector3.zero;
        if (Mouse.current == null || mainCamera == null || _tileRoot == null) return false;
        Ray ray = mainCamera.ScreenPointToRay((Vector2)Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 500f)) return false;
        if (hit.collider.GetComponentInParent<TileInstanceMarker>() == null) return false;
        Vector3 localNormal = _tileRoot.InverseTransformDirection(hit.normal).normalized;
        dirLocal = SnapToAxis(localNormal);
        return dirLocal.sqrMagnitude > 0.5f;
    }

    // Named face of this tile that points the given building-local direction (accounts for the
    // tile's full rotation), or null if none lines up — e.g. a wedge with no axis-aligned face there.
    private static string FacePointing(TileDef t, Vector3 dirLocal)
    {
        Quaternion rot = Quaternion.Euler(t.rotationX, t.rotation, t.rotationZ);
        string best = null;
        float bestDot = 0.9f;
        foreach (var name in FaceNames)
        {
            float dot = Vector3.Dot(rot * FaceBaselineDir(name), dirLocal);
            if (dot > bestDot) { bestDot = dot; best = name; }
        }
        return best;
    }

    // A tile face is part of the building's outer skin when no tile occupies the neighbouring cell
    // in that direction (walls = same-floor neighbour; top/bottom = the floor above/below).
    private bool FaceExposed(TileDef t, Vector3 dirLocal)
    {
        if (_bdef?.tiles == null) return true;
        int nx = t.gridX + Mathf.RoundToInt(dirLocal.x);
        int nf = t.floor + Mathf.RoundToInt(dirLocal.y);
        int nz = t.gridZ + Mathf.RoundToInt(dirLocal.z);
        foreach (var o in _bdef.tiles)
            if (o.gridX == nx && o.gridZ == nz && o.floor == nf) return false;
        return true;
    }

    // Every (tile, faceName) on the building side facing dirLocal whose face is exposed — i.e. the
    // whole flat side of the building (across all floors) that the user clicked.
    private List<(TileDef tile, string face)> CollectFaceTiles(Vector3 dirLocal)
    {
        var list = new List<(TileDef, string)>();
        if (_bdef?.tiles == null) return list;
        foreach (var t in _bdef.tiles)
        {
            if (!FaceExposed(t, dirLocal)) continue;
            string face = FacePointing(t, dirLocal);
            if (face != null) list.Add((t, face));
        }
        return list;
    }

    // Whole-face Paint: assign the active material to every exposed tile face on the clicked side.
    private void TryPaintWholeFace()
    {
        if (string.IsNullOrEmpty(_activeMaterialId)) return;
        if (!RaycastFaceDir(out Vector3 dir)) return;
        foreach (var (t, face) in CollectFaceTiles(dir))
        {
            if (t.faceMaterials == null) t.faceMaterials = new Dictionary<string, string>();
            t.faceMaterials[face] = _activeMaterialId;
            string key = MakeKey(t.gridX, t.gridZ, t.floor);
            if (_tileGOs.TryGetValue(key, out var go) && go != null)
                TileSpawner.ApplyFaceMaterial(go, t, face, _activeMaterialId, tileShapePalette, materialPalette);
        }
    }

    private void ClearFloor(int floor)
    {
        History?.RecordBefore(EditHistory.Scope.Building, "Clear floor");
        var keys = new List<string>();
        foreach (var k in _tileGOs.Keys)
            if (TryParseKey(k, out _, out _, out int f) && f == floor) keys.Add(k);
        foreach (var k in keys) RemoveTileAt(k);
    }

    // Copies every tile (and painted decor) on the active floor up to the floor above, then makes
    // that the active floor — a fast way to stack repeated stories. Target cells that are already
    // filled are left untouched, so it's safe to run over a partially-built floor. Undoable.
    private void DuplicateFloorUp()
    {
        if (_bdef?.tiles == null) return;
        int src = _activeFloor, dst = _activeFloor + 1;

        var srcTiles = _bdef.tiles.FindAll(t => t.floor == src);
        if (srcTiles.Count == 0) return;   // nothing to copy

        History?.RecordBefore(EditHistory.Scope.Building, "Duplicate floor");

        // Destination cells that already hold a tile — skip these so we never stomp existing work.
        var occupied = new HashSet<(int, int)>();
        foreach (var t in _bdef.tiles)
            if (t.floor == dst) occupied.Add((t.gridX, t.gridZ));

        foreach (var t in srcTiles)
        {
            if (occupied.Contains((t.gridX, t.gridZ))) continue;
            _bdef.tiles.Add(new TileDef
            {
                gridX = t.gridX, gridZ = t.gridZ, floor = dst,
                shapeId = t.shapeId,
                rotation = t.rotation, rotationX = t.rotationX, rotationZ = t.rotationZ,
                faceMaterials = t.faceMaterials != null ? new Dictionary<string, string>(t.faceMaterials) : null,
                deform = t.deform,   // immutable cage data — replaced wholesale on edit, never mutated in place
            });
        }

        // Carry painted decor up too, re-homed to the new floor and shifted one cell in Y (localPos
        // is absolute building-local space). Skip decor whose host cell wasn't copied.
        if (_bdef.embeddedObjects != null)
        {
            float cs = CellSize();
            var srcDecor = _bdef.embeddedObjects.FindAll(e =>
                e != null && e.hostFloor == src && !occupied.Contains((e.hostGridX, e.hostGridZ)));
            foreach (var e in srcDecor)
                _bdef.embeddedObjects.Add(new EmbeddedObjectDef
                {
                    instanceId = Guid.NewGuid().ToString("D"),
                    prefabType = e.prefabType,
                    localPos   = e.localPos != null && e.localPos.Length >= 3
                                 ? new[] { e.localPos[0], e.localPos[1] + cs, e.localPos[2] } : e.localPos,
                    rotationX = e.rotationX, rotationY = e.rotationY, rotationZ = e.rotationZ,
                    scale = e.scale,
                    hostGridX = e.hostGridX, hostGridZ = e.hostGridZ, hostFloor = dst,
                    hostFace = e.hostFace, exclusive = e.exclusive, fillsFace = e.fillsFace,
                });
        }

        if (_bdef.floors <= dst) _bdef.floors = dst + 1;
        _activeFloor       = dst;      // follow the copy up so you keep building
        _confirmClearFloor = false;
        RebuildVisuals();
        RebuildEmbeddedVisuals();
    }

    // -----------------------------------------------------------------------
    // Hover detection (projects mouse onto floor plane in building-local space)
    // -----------------------------------------------------------------------

    private void UpdateHover()
    {
        _hoveredKey = null;
        if (_tileRoot == null || Mouse.current == null || mainCamera == null) return;

        float cs  = CellSize();
        Ray   ray = mainCamera.ScreenPointToRay((Vector2)Mouse.current.position.ReadValue());

        // The active floor's surface plane (tiles rest on it; local Y = floor·cs). A pure plane
        // intersection is intuitive over open grid, but the ray punches straight through any wall in
        // front of it, so on a tall building the intersection lands on the floor far BEHIND the
        // building — that's the "tile placed behind the building" bug.
        float yFloor   = _bldgWorldPos.y + _activeFloor * cs;
        var   plane    = new Plane(Vector3.up, new Vector3(0f, yFloor, 0f));
        bool  hasPlane = plane.Raycast(ray, out float planeDist);

        // Occlusion guard: if one of THIS building's tiles sits in front of the plane point, the
        // plane point is hidden — snap placement to that tile's column on the active floor instead.
        // Pointing at a lower roof then stacks a tile on top; pointing at a side wall extends the
        // active floor one cell outward along the hit face. (Restricted to _tileRoot's own tiles so
        // other scene geometry never hijacks the grid coordinates.)
        if (Physics.Raycast(ray, out RaycastHit hit, 500f)
            && (!hasPlane || hit.distance < planeDist - 0.01f)
            && hit.collider.transform.IsChildOf(_tileRoot))
        {
            var m = hit.collider.GetComponentInParent<TileInstanceMarker>();
            if (m != null)
            {
                int ogx = m.gridX, ogz = m.gridZ;
                Vector3 n = _tileRoot.InverseTransformDirection(hit.normal);
                if (m.floor == _activeFloor && Mathf.Abs(n.y) < 0.5f)   // side face of an active-floor tile
                {
                    ogx += Mathf.RoundToInt(n.x);
                    ogz += Mathf.RoundToInt(n.z);
                }
                _hoveredKey = MakeKey(ogx, ogz, _activeFloor);
                return;
            }
        }

        if (!hasPlane) return;
        Vector3 world = ray.GetPoint(planeDist);
        Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, _bldgRotY, 0f)) * (world - _bldgWorldPos);

        // No quadrant clamp — a building may grow in any direction; negative cells render correctly
        // (TileSpawner uses gridX/gridZ directly) and the grid overlay tracks the full min/max extent.
        int gx = Mathf.FloorToInt(local.x / cs);
        int gz = Mathf.FloorToInt(local.z / cs);
        if (!WithinGrowRange(gx, gz)) return;   // near-horizon ray — refuse rather than hover a far cell
        _hoveredKey = MakeKey(gx, gz, _activeFloor);
    }

    // Farthest a NEW cell may sit from the existing footprint, in cells (Chebyshev). The floor-plane
    // intersection above is unbounded, so a near-horizon ray can land thousands of meters out and one
    // Add-click silently plants a tile there — which then blows up everything derived from tile
    // min/max (camera framing, grid overlay, selection bounds). Any-direction growth stays allowed;
    // only cells nowhere near the building are rejected (no hover, so nothing can be placed).
    private const int MAX_GROW_CELLS = 4;

    private bool WithinGrowRange(int gx, int gz)
    {
        if (_bdef?.tiles == null || _bdef.tiles.Count == 0)   // empty building: anchor to the origin
            return Mathf.Abs(gx) <= MAX_GROW_CELLS && Mathf.Abs(gz) <= MAX_GROW_CELLS;
        foreach (var t in _bdef.tiles)
            if (Mathf.Abs(t.gridX - gx) <= MAX_GROW_CELLS && Mathf.Abs(t.gridZ - gz) <= MAX_GROW_CELLS)
                return true;
        return false;
    }

    // -----------------------------------------------------------------------
    // Grid overlay (GL, rendered for main camera only)
    // -----------------------------------------------------------------------

    private void OnRenderObject()
    {
        if (!IsActive || _bdef == null || _tileRoot == null) return;
        if (_subTool != SubTool.Add) return;   // grid + active-floor are Add-only concepts
        if (Camera.current != mainCamera) return;

        Material mat = GetGLMat();
        if (mat == null) return;
        mat.SetPass(0);

        GL.PushMatrix();
        GL.MultMatrix(_tileRoot.localToWorldMatrix);

        float cs    = CellSize();
        float yBase = _activeFloor * cs + 0.05f;   // active floor's surface (tiles rest on it)

        // Editable region = active-floor footprint (min AND max on both axes, so it covers negative
        // cells), grown by a 2-cell working margin, with a minimum 8×8 so an empty floor still shows
        // a grid to paint onto. Recomputed each frame, so the region visibly expands as tiles are added.
        int cMinX = 0, cMinZ = 0, cMaxX = 7, cMaxZ = 7;   // inclusive cell-index bounds (default 8×8)
        bool any = false;
        if (_bdef.tiles != null)
            foreach (var t in _bdef.tiles)
                if (t.floor == _activeFloor)
                {
                    if (!any) { cMinX = cMaxX = t.gridX; cMinZ = cMaxZ = t.gridZ; any = true; }
                    else
                    {
                        if (t.gridX < cMinX) cMinX = t.gridX;
                        if (t.gridX > cMaxX) cMaxX = t.gridX;
                        if (t.gridZ < cMinZ) cMinZ = t.gridZ;
                        if (t.gridZ > cMaxZ) cMaxZ = t.gridZ;
                    }
                }
        if (any) { cMinX -= 2; cMinZ -= 2; cMaxX += 2; cMaxZ += 2; }

        // Line endpoints span the inclusive cell range [cMin..cMax] → world [cMin·cs .. (cMax+1)·cs].
        float xLo = cMinX * cs, xHi = (cMaxX + 1) * cs;
        float zLo = cMinZ * cs, zHi = (cMaxZ + 1) * cs;

        // Faint fill over the whole editable region, so it's obvious where placement is allowed.
        GL.Begin(GL.QUADS);
        GL.Color(editableFillColor);
        GL.Vertex3(xLo, yBase, zLo);
        GL.Vertex3(xHi, yBase, zLo);
        GL.Vertex3(xHi, yBase, zHi);
        GL.Vertex3(xLo, yBase, zHi);
        GL.End();

        // Grid lines across the region.
        GL.Begin(GL.LINES);
        GL.Color(gridColor);
        for (int x = cMinX; x <= cMaxX + 1; x++) { GL.Vertex3(x * cs, yBase, zLo); GL.Vertex3(x * cs, yBase, zHi); }
        for (int z = cMinZ; z <= cMaxZ + 1; z++) { GL.Vertex3(xLo, yBase, z * cs); GL.Vertex3(xHi, yBase, z * cs); }
        GL.End();

        // Footprint of the floor below, projected up as faint outlines, so upper floors can be
        // aligned to the floor they sit on.
        if (_activeFloor > 0 && _bdef.tiles != null)
        {
            GL.Begin(GL.LINES);
            GL.Color(lowerFloorColor);
            foreach (var t in _bdef.tiles)
                if (t.floor == _activeFloor - 1)
                {
                    float x0 = t.gridX * cs, x1 = (t.gridX + 1) * cs;
                    float z0 = t.gridZ * cs, z1 = (t.gridZ + 1) * cs;
                    GL.Vertex3(x0, yBase, z0); GL.Vertex3(x1, yBase, z0);
                    GL.Vertex3(x1, yBase, z0); GL.Vertex3(x1, yBase, z1);
                    GL.Vertex3(x1, yBase, z1); GL.Vertex3(x0, yBase, z1);
                    GL.Vertex3(x0, yBase, z1); GL.Vertex3(x0, yBase, z0);
                }
            GL.End();
        }

        // Hover highlight quad
        if (_hoveredKey != null && TryParseKey(_hoveredKey, out int hx, out int hz, out int hf) && hf == _activeFloor)
        {
            GL.Begin(GL.QUADS);
            GL.Color(hoverColor);
            float x0 = hx * cs, x1 = (hx + 1) * cs;
            float z0 = hz * cs, z1 = (hz + 1) * cs;
            GL.Vertex3(x0, yBase + 0.01f, z0);
            GL.Vertex3(x1, yBase + 0.01f, z0);
            GL.Vertex3(x1, yBase + 0.01f, z1);
            GL.Vertex3(x0, yBase + 0.01f, z1);
            GL.End();
        }

        GL.PopMatrix();
    }

    // -----------------------------------------------------------------------
    // OnGUI (right panel, replaces EditController panel while active)
    // -----------------------------------------------------------------------

    private void OnGUI()
    {
        if (!IsActive || UIMode.Current != AppMode.Build) return;

        var rect = new Rect(Screen.width - PANEL_W - UITheme.Margin, UITheme.RailTop, PANEL_W, Screen.height - UITheme.RailTop - UITheme.Margin);
        UITheme.PanelBackground(rect);
        GUILayout.BeginArea(UITheme.Inset(rect));

        UITheme.Title("Building editor");
        UITheme.Note($"{_bdef?.name}   •   {_bdef?.tiles?.Count ?? 0} tiles");

        // Tool selector — one active sub-tool at a time; labels must stay aligned with SubTool order.
        int ts = UITheme.Segmented((int)_subTool, new[] { "Select", "Add", "Paint", "Extras" });
        if (ts != (int)_subTool) SetSubTool((SubTool)ts);

        UITheme.Divider();
        _panelScroll = GUILayout.BeginScrollView(_panelScroll, GUILayout.ExpandHeight(true));
        switch (_subTool)
        {
            case SubTool.Add:      DrawAddPanel();      break;
            case SubTool.Select:   DrawSelectPanel();   break;
            case SubTool.Paint:    DrawPaintPanel();    break;
            case SubTool.Decorate: DrawDecoratePanel(); break;
        }
        GUILayout.EndScrollView();

        // Save / exit footer (spec: "Save changes" primary · Esc).
        UITheme.Divider();
        if (UITheme.PrimaryButton("Save changes")) OnSaveRequested?.Invoke();
        UITheme.Note("Esc = save & exit");

        GUILayout.EndArea();
    }

    private void SetSubTool(SubTool tool)
    {
        if (_subTool == tool) return;
        // Entering or leaving Add toggles the non-active-floor dimming (applied in SpawnTileGO), so
        // respawn the tiles to add or drop the ghosted backdrop.
        bool dimChanged = _subTool == SubTool.Add || tool == SubTool.Add;
        _subTool = tool;
        _panelScroll = Vector2.zero;
        if (tool != SubTool.Paint)  _activeMaterialId = null;  // paint is keyed off an active material
        if (tool != SubTool.Select) ClearSelection();
        if (dimChanged) RebuildVisuals();
    }

    // ---- thumbnail grid helpers (shared by shape + material pickers) ----
    private Vector2 _panelScroll;
    private int _thumbCol;
    private const int ThumbCols = 3;
    private void BeginThumbGrid() { _thumbCol = 0; GUILayout.BeginHorizontal(); }
    private void EndThumbGrid()   { GUILayout.EndHorizontal(); }
    private bool ThumbCell(Texture tex, string label, bool selected)
    {
        if (_thumbCol >= ThumbCols) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); _thumbCol = 0; }
        _thumbCol++;
        return UITheme.Thumb(tex, label, selected, 76f);
    }

    // Floor selector (Add tool only). Clearing a floor is destructive, so it's de-emphasised (a small
    // red text link, not a button) and asks for confirmation before wiping the floor's tiles.
    private void DrawFloorSelector()
    {
        UITheme.Header($"Floor {_activeFloor}");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("▼", GUILayout.Width(34)) && _activeFloor > 0) { _activeFloor--; _confirmClearFloor = false; RebuildVisuals(); }
        if (GUILayout.Button("▲", GUILayout.Width(34))) { _activeFloor++; _confirmClearFloor = false; if (_activeFloor >= _bdef.floors) { History?.RecordBefore(EditHistory.Scope.Building, "Add floor"); _bdef.floors = _activeFloor + 1; } RebuildVisuals(); }
        GUILayout.FlexibleSpace();
        if (!_confirmClearFloor && UITheme.DangerButton("Clear floor…", GUILayout.Width(96)))
            _confirmClearFloor = true;
        GUILayout.EndHorizontal();

        // Stack a copy of this floor's tiles (+ decor) onto the floor above and move up to it.
        if (GUILayout.Button("Duplicate floor ↑")) DuplicateFloorUp();

        if (_confirmClearFloor)
        {
            UITheme.Note($"Delete every tile on floor {_activeFloor}? (Ctrl+Z to undo.)");
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (UITheme.GhostButton("Cancel", GUILayout.Width(72))) _confirmClearFloor = false;
            if (UITheme.DangerButton("Clear floor", GUILayout.Width(96))) { ClearFloor(_activeFloor); _confirmClearFloor = false; }
            GUILayout.EndHorizontal();
        }
    }

    private void DrawAddPanel()
    {
        // Floor controls live here: floors only matter while adding tiles (Select/Paint/Decorate act
        // on any tile you click, on any floor). The grid overlay and the dimmed backdrop track the
        // active floor too, so keeping all of it in one place avoids confusion.
        DrawFloorSelector();
        UITheme.Divider();

        UITheme.Note("Click or drag the grid to add tiles.");
        UITheme.Header("Tile shape");
        if (tileShapePalette != null)
        {
            BeginThumbGrid();
            foreach (var e in tileShapePalette.entries)
            {
                bool on = e.shapeId == _activeShapeId;
                if (ThumbCell(ThumbnailCache.GetPrefab(e.prefab), UITheme.PrettyId(e.shapeId), on)) _activeShapeId = e.shapeId;
            }
            EndThumbGrid();
        }

        UITheme.Header("New-tile yaw");
        GUILayout.BeginHorizontal();
        foreach (int r in new[] { 0, 90, 180, 270 })
        {
            bool on = _activeTileRotation == r;
            if (GUILayout.Toggle(on, $"{r}°", GUI.skin.button) && !on) _activeTileRotation = r;
        }
        GUILayout.EndHorizontal();
        UITheme.Note("Q / E = turn new tile ±90°");
    }

    private void DrawSelectPanel()
    {
        UITheme.Note("Click / drag to select • Shift/Ctrl+click = toggle");

        // Bulk-selection helpers
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Select floor")) SelectAll(activeFloorOnly: true);
        if (GUILayout.Button("Select all"))   SelectAll(activeFloorOnly: false);
        GUILayout.EndHorizontal();

        if (_selectedKeys.Count == 0)
        {
            UITheme.Note("No tiles selected.");
            return;
        }

        // Primary tile drives the readouts; rotations apply to the whole selection.
        var t = FindTile(_primaryKey);
        if (t == null) { _primaryKey = FirstSelected(); t = FindTile(_primaryKey); }
        if (t == null) { ClearSelection(); return; }

        UITheme.Header(_selectedKeys.Count == 1
            ? $"Tile  x{t.gridX} z{t.gridZ} F{t.floor}"
            : $"{_selectedKeys.Count} tiles  (ref x{t.gridX} z{t.gridZ} F{t.floor})");

        // Active rotate axis (also pickable with X/Y/Z keys)
        GUILayout.BeginHorizontal();
        GUILayout.Label("Axis", GUILayout.Width(40));
        DrawAxisToggle("X", 0);
        DrawAxisToggle("Y", 1);
        DrawAxisToggle("Z", 2);
        GUILayout.EndHorizontal();
        UITheme.Note("Q/E = ±15° • X/Y/Z pick axis");

        // Per-axis sliders (snap 15°) — show the reference tile, apply to all selected.
        DrawTileRotSlider(t, "X", 0);
        DrawTileRotSlider(t, "Y", 1);
        DrawTileRotSlider(t, "Z", 2);

        // Quick steps on the active axis
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("−90")) RotateSelectedAxis(-90f);
        if (GUILayout.Button("−15")) RotateSelectedAxis(-15f);
        if (GUILayout.Button("+15")) RotateSelectedAxis(+15f);
        if (GUILayout.Button("+90")) RotateSelectedAxis(+90f);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Reset"))    ResetSelectedRotation();
        if (GUILayout.Button("Delete"))   RemoveSelectedTiles();
        if (GUILayout.Button("Deselect")) ClearSelection();
        GUILayout.EndHorizontal();
    }

    private void DrawAxisToggle(string label, int axis)
    {
        bool on = _rotAxis == axis;
        if (GUILayout.Toggle(on, label, GUI.skin.button, GUILayout.Width(42)) && !on) _rotAxis = axis;
    }

    private void DrawTileRotSlider(TileDef t, string label, int axis)
    {
        float cur = Mathf.Repeat(TileEuler(t)[axis], 360f);
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{label}  {cur:F0}°", GUILayout.Width(56));
        float v = SnapAngle(GUILayout.HorizontalSlider(cur, 0f, 360f));
        GUILayout.EndHorizontal();
        if (Mathf.Abs(Mathf.DeltaAngle(v, cur)) > 0.01f)
        {
            History?.BeginGesture(EditHistory.Scope.Building, "Rotate tiles");   // coalesce the slider drag
            SetSelectedAxisAbsolute(axis, v);
        }
    }

    private void DrawPaintPanel()
    {
        UITheme.Note(_wholeFace
            ? "Click a face to paint the whole building side."
            : "Pick a material, then click a tile face.");

        _wholeFace = GUILayout.Toggle(_wholeFace, "Whole face (paint the entire side)", GUI.skin.button, GUILayout.Height(UITheme.RowH));

        UITheme.Header("Face material");
        if (materialPalette == null || materialPalette.entries.Count == 0)
            UITheme.Note("MaterialPalette is empty.");
        else
        {
            BeginThumbGrid();
            foreach (var e in materialPalette.entries)
            {
                bool on = e.materialId == _activeMaterialId;
                if (ThumbCell(ThumbnailCache.GetMaterial(e.material), UITheme.PrettyId(e.materialId), on)) _activeMaterialId = e.materialId;
            }
            EndThumbGrid();
        }

        UITheme.Header("Face fallback");
        UITheme.Note("Used only when a click can't detect the face.");
        string[] faces = { "north", "east", "south", "west", "top", "bottom" };
        int half = faces.Length / 2;
        for (int row = 0; row < 2; row++)
        {
            GUILayout.BeginHorizontal();
            for (int i = row * half; i < (row + 1) * half; i++)
            {
                bool on = _activeFaceName == faces[i];
                if (GUILayout.Toggle(on, faces[i], GUI.skin.button) && !on) _activeFaceName = faces[i];
            }
            GUILayout.EndHorizontal();
        }
    }

    // -----------------------------------------------------------------------
    // Decorate tool — smart-paint decorative prefabs onto tile faces
    // -----------------------------------------------------------------------

    private DecorPalette.Entry ActiveDecor()
    {
        if (decorPalette == null || decorPalette.entries == null) return null;
        var e = _activeDecorId != null ? decorPalette.Get(_activeDecorId) : null;
        if (e == null && decorPalette.entries.Count > 0)
        {
            e = decorPalette.entries[0];
            _activeDecorId = e?.decorId;
        }
        return e;
    }

    // Each qualifying frame: raycast a tile face and either erase the decoration there or place the
    // active decor on it. Placement is systematic — one prop per face, centered, fit, and seated
    // flush — and dragging paints each face under the cursor (one per tile per stroke, like Paint).
    private void HandleDecorate(bool lDown)
    {
        if (Mouse.current == null || mainCamera == null) return;
        Ray ray = mainCamera.ScreenPointToRay((Vector2)Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 500f)) return;

        if (_decorErase) { EraseDecorAt(hit); return; }

        var entry = ActiveDecor();
        if (entry == null) return;

        // Only paint onto this building's tiles, never onto already-placed decorations or the ground.
        var marker = hit.collider.GetComponentInParent<TileInstanceMarker>();
        if (marker == null) return;

        // One placement per tile per drag: re-place only when the cursor moves to a different tile,
        // so holding on one tile doesn't churn/flicker the GameObject every frame.
        string tileKey = MakeKey(marker.gridX, marker.gridZ, marker.floor);
        if (!lDown && tileKey == _decorLastTileKey) return;
        if (TryDecorateAt(hit, entry)) _decorLastTileKey = tileKey;
    }

    // Places the active decor on the clicked tile face: one prop, centered and fit to the entry's
    // width/height fraction of the cell, seated flush at its anchor, replacing any prior prop on that
    // face (one decor per face). Returns true when something was placed.
    private bool TryDecorateAt(RaycastHit hit, DecorPalette.Entry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.prefabKey) || _tileRoot == null) return false;

        var marker = hit.collider.GetComponentInParent<TileInstanceMarker>();
        if (marker == null) return false;
        int gx = marker.gridX, gz = marker.gridZ, gf = marker.floor;

        // Building-local frame: matches EmbeddedObjectDef.localPos (see WorldRenderer.RenderEmbeddedObjects).
        Vector3 localNormal = _tileRoot.InverseTransformDirection(hit.normal).normalized;

        // Classify the surface from the normal and apply the decor's face filter.
        bool isRoof = Mathf.Abs(localNormal.y) > 0.7f;
        if (entry.surface == DecorPalette.Surface.Wall && isRoof)  return false;
        if (entry.surface == DecorPalette.Surface.Roof && !isRoof) return false;

        // Face name (host key, one decor per face); robust to tile rotation via the existing resolvers.
        string key = MakeKey(gx, gz, gf);
        var tile   = FindTile(key);
        _tileGOs.TryGetValue(key, out var tileGO);
        string face = (tile != null ? FaceFromHit(hit, tile) : null)
                      ?? FaceFromNormal(tileGO, hit.normal)
                      ?? (isRoof ? "top" : "wall");

        PlaceFaceDecor(gx, gz, gf, face, localNormal, isRoof, entry);
        return true;
    }

    // Whole-face Decorate: place the active decor on every exposed tile face of the clicked building
    // side — e.g. windows across an entire wall in one click. Uses the same size/anchor math per face.
    private void TryDecorateWholeFace()
    {
        var entry = ActiveDecor();
        if (entry == null) return;
        if (!RaycastFaceDir(out Vector3 dir)) return;

        bool isRoof = Mathf.Abs(dir.y) > 0.7f;
        if (entry.surface == DecorPalette.Surface.Wall && isRoof)  return;
        if (entry.surface == DecorPalette.Surface.Roof && !isRoof) return;

        foreach (var (t, face) in CollectFaceTiles(dir))
            PlaceFaceDecor(t.gridX, t.gridZ, t.floor, face, dir, isRoof, entry);
    }

    // Core systematic placement shared by single-face and whole-face: seats one prop on a tile face,
    // sized to the entry's width/height fraction of the face (aspect preserved), anchored vertically,
    // and flush along the face normal. Replaces any prior decor on that face. Deform-aware: placement
    // is derived from the host face's ACTUAL (possibly skewed/sloped) plane via DecorPlacement — the
    // same function the render paths re-run when the building's deform changes, so paint-time and
    // render-time placement agree by construction and props follow later skews.
    private void PlaceFaceDecor(int gx, int gz, int gf, string face, Vector3 faceNormal, bool isRoof, DecorPalette.Entry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.prefabKey) || _tileRoot == null) return;

        // One decor per face: drop any prior prop on this tile face before placing the new one.
        ReplacePriorFill(gx, gz, gf, face);

        // Analyze the prop's mount basis (auto from mesh bounds, or a per-entry override) so it seats
        // flush and faces outward regardless of how its mesh / pivot is authored.
        TryAnalyzeProp(entry.prefabKey, entry.mountAxis, entry.flipMount, out var basis);

        float cs  = CellSize();
        var   emb = new EmbeddedObjectDef
        {
            instanceId = Guid.NewGuid().ToString("D"),
            prefabType = entry.prefabKey,
            hostGridX  = gx,
            hostGridZ  = gz,
            hostFloor  = gf,
            hostFace   = face,
            fillsFace  = true,
            // Placement rules, persisted so the render paths can reseat this prop against the host
            // tile's current TileDeform (see DecorPlacement.ReseatAll).
            decorWidthFrac     = entry.widthFraction,
            decorHeightFrac    = entry.heightFraction,
            decorAnchor        = (int)entry.anchor,
            decorSurfaceOffset = entry.surfaceOffset,
            decorMountAxis     = (int)entry.mountAxis,
            decorFlipMount     = entry.flipMount,
        };

        if (!DecorPlacement.TryReseat(FindTile(MakeKey(gx, gz, gf)), emb, cs, basis))
        {
            // Unresolvable face name (the "wall" fallback) or a missing tile: place with the legacy
            // cell formula against the raycast normal, and clear the rules so the def honestly stays
            // a baked-replay legacy def (never store rules the renderer can't re-derive).
            Vector3 cellCenter = new Vector3((gx + 0.5f) * cs, (gf + 0.5f) * cs, (gz + 0.5f) * cs);
            float   scale      = DecorAlignment.FitScaleBox(basis, cs, entry.widthFraction, entry.heightFraction);
            float   seat       = DecorAlignment.SeatDistance(basis, scale) + entry.surfaceOffset;
            Vector3 faceUp     = DecorAlignment.FaceUp(faceNormal, isRoof);
            float   anchorOff  = DecorAlignment.AnchorOffset(entry.anchor, basis.inPlaneHeight * scale, cs);
            Vector3 localPos   = cellCenter + faceNormal * (0.5f * cs + seat) + faceUp * anchorOff;
            Vector3 align      = DecorAlignment.AlignRotation(basis, faceNormal, isRoof, false, 0f).eulerAngles;

            emb.localPos  = new[] { localPos.x, localPos.y, localPos.z };
            emb.rotationX = align.x;
            emb.rotationY = align.y;
            emb.rotationZ = align.z;
            emb.scale     = scale;
            emb.decorWidthFrac     = 0f;
            emb.decorHeightFrac    = 0f;
            emb.decorAnchor        = 0;
            emb.decorSurfaceOffset = 0f;
            emb.decorMountAxis     = 0;
            emb.decorFlipMount     = false;
        }

        if (_bdef.embeddedObjects == null) _bdef.embeddedObjects = new List<EmbeddedObjectDef>();
        _bdef.embeddedObjects.Add(emb);
        SpawnEmbeddedGO(emb);
    }

    // Removes any prior decoration on the same tile face so a face keeps exactly one decor.
    private void ReplacePriorFill(int gx, int gz, int gf, string face)
    {
        if (_bdef?.embeddedObjects == null) return;
        for (int i = _bdef.embeddedObjects.Count - 1; i >= 0; i--)
        {
            var e = _bdef.embeddedObjects[i];
            if (e != null && e.fillsFace && e.hostGridX == gx && e.hostGridZ == gz && e.hostFloor == gf && e.hostFace == face)
                RemoveEmbedded(e.instanceId);
        }
    }

    // Prop mount bases per (prefabKey, mountAxis, flipMount), measured once per editor session —
    // reseating (RebuildEmbeddedVisuals) re-resolves bases on every rebuild.
    private readonly Dictionary<(string, int, bool), DecorAlignment.PropBasis> _propBasisCache = new();

    // Resolves the prop's mount basis (measure-and-cache; see DecorPlacement.MeasurePropBasis).
    // Returns false (and an identity-ish basis) for a missing prefab. Signature doubles as the
    // DecorPlacement.BasisProvider used by the reseat pre-pass.
    private bool TryAnalyzeProp(string prefabKey, DecorAlignment.MountAxis ov, bool flip, out DecorAlignment.PropBasis basis)
    {
        var key = (prefabKey, (int)ov, flip);
        if (_propBasisCache.TryGetValue(key, out basis)) return true;
        var prefab = prefabRegistry != null ? prefabRegistry.GetPrefab(prefabKey) : null;
        if (!DecorPlacement.MeasurePropBasis(prefab, ov, flip, out basis)) return false;
        _propBasisCache[key] = basis;
        return true;
    }

    // Removes every painted decoration whose GameObject is under the cursor (a direct hit, or within
    // a small radius of the hit point), from both the live scene and _bdef.embeddedObjects.
    private void EraseDecorAt(RaycastHit hit)
    {
        float radius = 1.5f;

        // Direct hit on a decoration GameObject.
        var marker = hit.collider.GetComponentInParent<InstanceMarker>();
        if (marker != null && _embGOs.ContainsKey(marker.instanceId)) { RemoveEmbedded(marker.instanceId); return; }

        // Otherwise remove any decorations near the hit point.
        if (_bdef?.embeddedObjects == null || _tileRoot == null) return;
        Vector3 localHit = _tileRoot.InverseTransformPoint(hit.point);
        float sq = radius * radius;
        for (int i = _bdef.embeddedObjects.Count - 1; i >= 0; i--)
        {
            var e = _bdef.embeddedObjects[i];
            if (e?.localPos == null || e.localPos.Length < 3) continue;
            Vector3 d = localHit - new Vector3(e.localPos[0], e.localPos[1], e.localPos[2]);
            if (d.sqrMagnitude <= sq) RemoveEmbedded(e.instanceId);
        }
    }

    private void RemoveEmbedded(string instanceId)
    {
        _bdef?.embeddedObjects?.RemoveAll(e => e != null && e.instanceId == instanceId);
        if (_embGOs.TryGetValue(instanceId, out var go))
        {
            DestroyObject(go);
            _embGOs.Remove(instanceId);
        }
    }

    // Instantiates one decoration as a child of _tileRoot, matching WorldRenderer.RenderEmbeddedObjects
    // exactly (rotationY composed on the prefab's authored orientation) so editor preview == runtime.
    private void SpawnEmbeddedGO(EmbeddedObjectDef emb)
    {
        if (emb?.localPos == null || emb.localPos.Length < 3 || _tileRoot == null) return;

        Vector3 localPos = new Vector3(emb.localPos[0], emb.localPos[1], emb.localPos[2]);
        float   scale    = emb.scale > 0f ? emb.scale : 1f;
        GameObject prefab = prefabRegistry != null ? prefabRegistry.GetPrefab(emb.prefabType) : null;

        // Full XYZ so the live preview matches WorldRenderer (props align flush to sloped/skewed faces).
        Quaternion embRot = Quaternion.Euler(emb.rotationX, emb.rotationY, emb.rotationZ);

        GameObject go;
        if (prefab == null)
        {
            Debug.LogWarning($"[TileBuildingEditor] Decorate prefab '{emb.prefabType}' not found in PrefabRegistry — spawning placeholder.");
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(_tileRoot, false);
            go.transform.localRotation = embRot;
            go.transform.localScale = Vector3.one * 0.5f;
            var r = go.GetComponent<Renderer>();
            if (r != null) r.material.color = Color.magenta;
        }
        else
        {
            go = Instantiate(prefab, _tileRoot);
            go.transform.localRotation = embRot * prefab.transform.rotation;
        }
        go.transform.localPosition = localPos;
        go.transform.localScale   *= scale;

        var marker = go.AddComponent<InstanceMarker>();
        marker.instanceId = emb.instanceId;
        marker.isBuilding = false;
        _embGOs[emb.instanceId] = go;
    }

    private void RebuildEmbeddedVisuals()
    {
        foreach (var kv in _embGOs) DestroyObject(kv.Value);
        _embGOs.Clear();
        if (_bdef?.embeddedObjects == null) return;
        // Reseat hosted decor against the tiles' current deform before spawning (covers Enter,
        // ReloadDef, and undo/redo of skew edits); SpawnEmbeddedGO then replays the fresh values.
        DecorPlacement.ReseatAll(_bdef, CellSize(), TryAnalyzeProp);
        foreach (var emb in _bdef.embeddedObjects) SpawnEmbeddedGO(emb);
    }

    private void DrawDecoratePanel()
    {
        UITheme.Note("Place objects onto tiles");

        if (prefabRegistry == null)        { UITheme.Note("PrefabRegistry is not wired."); return; }
        if (decorPalette == null || decorPalette.entries == null || decorPalette.entries.Count == 0)
        { UITheme.Note("DecorPalette is empty / not wired."); return; }

        // Place / Erase
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(!_decorErase, "Place", GUI.skin.button, GUILayout.Height(UITheme.RowH)) && _decorErase) _decorErase = false;
        if (GUILayout.Toggle(_decorErase,  "Erase", GUI.skin.button, GUILayout.Height(UITheme.RowH)) && !_decorErase) _decorErase = true;
        GUILayout.EndHorizontal();

        if (!_decorErase)
            // Whole-face: one click places the active decor on every exposed face of the clicked
            // building side (e.g. windows across a whole wall).
            _wholeFace = GUILayout.Toggle(_wholeFace, "Whole face", GUI.skin.button, GUILayout.Height(UITheme.RowH));

        // Decor picker
        UITheme.Header("Extras");
        var active = ActiveDecor();
        foreach (var e in decorPalette.entries)
        {
            if (e == null || string.IsNullOrEmpty(e.decorId)) continue;
            bool on = active != null && e.decorId == active.decorId;
            if (GUILayout.Toggle(on, $"{e.decorId}", GUI.skin.button) && !on)
                _activeDecorId = e.decorId;
        }

        if (active != null)
            UITheme.Note($"{active.widthFraction:0.##}×{active.heightFraction:0.##} of cell • anchor {active.anchor}");

        if (_decorErase)
            UITheme.Note("Click or drag across props to remove them.");
    }

    // -----------------------------------------------------------------------
    // Visuals
    // -----------------------------------------------------------------------

    private void RebuildVisuals()
    {
        _highlightedGOs.Clear();   // GOs below are about to be destroyed; drop stale refs
        foreach (var kv in _tileGOs) DestroyObject(kv.Value);
        _tileGOs.Clear();
        if (_bdef?.tiles == null) return;
        foreach (var t in _bdef.tiles) SpawnTileGO(t);
        if (_selectedKeys.Count > 0) ApplySelectionHighlight();   // re-tint the surviving selection
    }

    private void SpawnTileGO(TileDef tile)
    {
        if (tileShapePalette == null) return;
        var go = TileSpawner.Spawn(tile, _tileRoot, tileShapePalette, materialPalette, CellSize());
        if (go == null) return;

        var marker = go.AddComponent<TileInstanceMarker>();
        marker.gridX = tile.gridX; marker.gridZ = tile.gridZ; marker.floor = tile.floor;

        // The dim/translucent backdrop is an ADD-tool aid: it highlights the floor being painted onto
        // and is meaningless for the other tools, which operate on whatever tile you click on any
        // floor. So only ghost the non-active floors while adding; Select/Paint/Decorate see every
        // floor at full opacity. Re-applied on each floor change and tool switch (both RebuildVisuals).
        if (_subTool == SubTool.Add && tile.floor != _activeFloor) ApplyTranslucent(go, FLOOR_GHOST_TINT);

        _tileGOs[MakeKey(tile.gridX, tile.gridZ, tile.floor)] = go;
    }

    // -----------------------------------------------------------------------
    // Add-tool placement ghost
    // -----------------------------------------------------------------------

    private GameObject _addGhostGO;
    private string     _addGhostShapeId;   // shape the current ghost prefab was built from
    private bool       _addGhostInvalid;   // last tint applied: true = red (occupied cell)
    private static readonly Color GHOST_TINT         = new(0.4f, 0.8f, 1f, 0.45f);
    private static readonly Color GHOST_INVALID_TINT = new(1f, 0.35f, 0.3f, 0.5f);

    // Add tool only: a semi-transparent copy of the active tile shape hovering over the cell a
    // click would fill, previewing the shape and yaw before it's committed. It mirrors the real
    // tile's placement (TileSpawner convention) but carries no collider and is not part of _bdef,
    // so it never intercepts raycasts or gets saved. Hidden when not adding or off-grid; when the
    // hovered cell is already occupied it stays visible but turns red, so the cursor still gives
    // feedback (placement itself is suppressed by AddTileAt's duplicate guard).
    private void UpdateAddGhost(bool overUI)
    {
        bool show = _subTool == SubTool.Add && !overUI && tileShapePalette != null
                    && _hoveredKey != null
                    && TryParseKey(_hoveredKey, out _, out _, out _);

        if (!show) { DestroyAddGhost(); return; }
        TryParseKey(_hoveredKey, out int gx, out int gz, out int gf);

        bool invalid = TileExistsAt(_hoveredKey);

        // Rebuild only when the chosen shape changes — the ghost is a different prefab then.
        if (_addGhostGO == null || _addGhostShapeId != _activeShapeId)
        {
            DestroyAddGhost();
            var prefab = tileShapePalette.GetPrefab(_activeShapeId);
            if (prefab == null) return;
            _addGhostGO      = Instantiate(prefab, _tileRoot);
            _addGhostShapeId = _activeShapeId;
            _addGhostGO.name = "AddGhost";
            foreach (var c in _addGhostGO.GetComponentsInChildren<Collider>()) DestroyObject(c);
            ApplyTranslucent(_addGhostGO, GHOST_TINT);
            _addGhostInvalid = false;
        }

        // Re-tint only on a valid/invalid flip (cheap; avoids touching materials every frame).
        if (invalid != _addGhostInvalid)
        {
            TintGhost(invalid ? GHOST_INVALID_TINT : GHOST_TINT);
            _addGhostInvalid = invalid;
        }

        float cs = CellSize();
        // Cell center on every axis, matching TileSpawner.Spawn (Y = (floor+0.5)·cs).
        _addGhostGO.transform.localPosition = new Vector3((gx + 0.5f) * cs, (gf + 0.5f) * cs, (gz + 0.5f) * cs);
        // Match the committed tile (TileSpawner.Spawn): the tile yaw is composed on top of the shape's
        // baseline orientation, then FitToCell applies the same cell-fitting scale and geometry-center
        // re-anchoring, so the preview is exactly the size, facing, and placement a click will produce.
        _addGhostGO.transform.localRotation = Quaternion.Euler(0f, _activeTileRotation, 0f)
                                              * tileShapePalette.GetDefaultRotation(_activeShapeId);
        TileSpawner.FitToCell(_addGhostGO, cs);
    }

    private void DestroyAddGhost()
    {
        if (_addGhostGO != null) DestroyObject(_addGhostGO);
        _addGhostGO      = null;
        _addGhostShapeId = null;
        _addGhostInvalid = false;
    }

    // Recolors the live ghost's (already-translucent) material instances — used to flip between the
    // normal blue tint and the red "cell occupied" tint without rebuilding the ghost prefab.
    private void TintGhost(Color tint)
    {
        if (_addGhostGO == null) return;
        foreach (var r in _addGhostGO.GetComponentsInChildren<Renderer>())
            foreach (var m in r.materials)
            {
                if      (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
                else if (m.HasProperty("_Color"))     m.SetColor("_Color",     tint);
            }
    }

    // Dim, translucent tint for floors that aren't being edited (cool grey, low alpha).
    private static readonly Color FLOOR_GHOST_TINT = new(0.55f, 0.6f, 0.7f, 0.22f);

    // Clones each renderer's materials and switches them to alpha-blended transparency, tinted to
    // `tint`, so the object reads as see-through on both the Standard and URP Lit shaders. Shared by
    // the Add-tool placement ghost and the dimmed non-active floors.
    private static void ApplyTranslucent(GameObject go, Color tint)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = new Material(r.sharedMaterials[i]);
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);   // URP: 0 opaque, 1 transparent
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.EnableKeyword("_ALPHABLEND_ON");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                if      (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
                else if (m.HasProperty("_Color"))     m.SetColor("_Color",     tint);
                mats[i] = m;
            }
            r.materials = mats;
        }
    }

    // -----------------------------------------------------------------------
    // Cleanup
    // -----------------------------------------------------------------------

    private void Cleanup()
    {
        DestroyAddGhost();
        foreach (var kv in _tileGOs) DestroyObject(kv.Value);
        _tileGOs.Clear();
        foreach (var kv in _embGOs) DestroyObject(kv.Value);
        _embGOs.Clear();
        if (_tileRoot != null) DestroyObject(_tileRoot.gameObject);
        _tileRoot      = null;
        _bdef          = null;
        _selectedKeys.Clear();
        _primaryKey    = null;
        _highlightedGOs.Clear();
        _dragSelecting = false;
        IsActive       = false;
    }

    private static void DestroyObject(UnityEngine.Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else                       DestroyImmediate(obj);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private float CellSize() => (_bdef != null && _bdef.gridCellSize > 0f) ? _bdef.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;

    // True when the pointer is over either GUI panel: the tile editor's own right-side panel
    // or the LibraryBrowser's left panel (≈320px + margin), or the top command bar. Without these
    // guards, clicking a library button or a mode button while editing also drops a tile on the
    // ground plane behind it.
    private bool IsMouseOverUI()
    {
        if (Mouse.current == null) return false;
        Vector2 m = Mouse.current.position.ReadValue();
        if (m.x < LEFT_PANEL_W || m.x > Screen.width - PANEL_W - 10) return true;
        // Both panel guards are x-bands; the top command bar is centered between them, so without
        // its own rect test clicking a mode button also drops a tile on the plane behind it.
        return UIShell.BlocksScreenPoint(m);
    }

    private const int LEFT_PANEL_W = 340;

    private static string MakeKey(int x, int z, int floor) => $"{floor}_{x}_{z}";

    private static bool TryParseKey(string key, out int x, out int z, out int floor)
    {
        x = z = floor = 0;
        if (string.IsNullOrEmpty(key)) return false;
        var p = key.Split('_');
        return p.Length == 3 && int.TryParse(p[0], out floor) && int.TryParse(p[1], out x) && int.TryParse(p[2], out z);
    }

    private static Material GetGLMat()
    {
        if (_glMat != null) return _glMat;
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null) return null;
        _glMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        _glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _glMat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
        _glMat.SetInt("_ZWrite",   0);
        return _glMat;
    }
}
