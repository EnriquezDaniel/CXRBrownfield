using System;
using System.Collections.Generic;
using UnityEngine;

// Water tool (Terrain rail → "Draw water"): ponds and rivers as WaterBodyDefs in site.waterBodies.
//
// A pond is a closed ring (click corners, Enter / double-click closes); a river is a centerline
// with a width (the Draw paths gesture: straight clicks or a freehand drag). Both render as one
// flat translucent mesh at a surface height WorldRenderer derives from the lowest shore point, and
// both carve their bed into the heightmap at replay (WaterCarve), so a commit re-renders the
// environment the way a Shape ground stroke does. Edit mode mirrors the fence editor: drag handles,
// click the outline to insert, Delete removes a point or the body, sliders commit live.
//
// Keys and gestures live here, so they cannot be exercised from MCP play mode; the math is in
// Authoring/WaterGeometry and Authoring/WaterCarve, which the EditMode tests cover.
public partial class EditController
{
    // ---- DrawWater / EditWater state ----
    private string _waterKind      = WaterGeometry.KIND_POND;
    private string _waterMaterial  = "lake";
    private float  _waterWidth     = 6f;
    private float  _waterSmoothing = 0.5f;
    private float  _waterDepth     = 1.5f;
    private float  _waterBank      = 2f;
    private float  _waterLevel     = 0f;                   // surface height relative to the flat ground height
    private bool   _waterClipToLot = true;
    private bool   _waterFreehand;
    private readonly List<Vector3> _waterPts = new();     // corners / centerline being drawn (world XZ)
    private bool   _waterStroking;
    private float  _waterLastClickTime = -1f;
    private GameObject   _waterOutlineGO;                  // draped ring while a pond is being drawn or edited
    private MeshFilter   _waterOutlineMF;
    private GameObject   _waterFillGO;                     // the translucent surface preview
    private MeshFilter   _waterFillMF;
    private MeshRenderer _waterFillMR;
    private Material     _waterFillFallbackMat;
    private static readonly Color WATER_PREVIEW_COLOR = new(0.45f, 0.85f, 1f, 1f);
    // EditWater: reshape an existing body's points and settings in place.
    private string _waterEditId;
    private readonly List<Vector3> _waterEditPts = new();
    private int  _waterEditSel = -1;
    private bool _waterEditDragging;
    private bool _waterEditMoved;
    private bool _waterEditPendingRender;                  // a live slider edit is waiting for the mouse to release
    private readonly List<GameObject> _waterHandles = new();
    private bool    _showWaterList;
    private Vector2 _waterListScroll, _waterMatScroll;

    private static bool WaterIsRiver(string kind) => WaterGeometry.IsRiver(kind);

    // ---- Presets: defaults for the next body (never touch the surface offset) ----

    private void ApplyWaterPreset(int index)
    {
        switch (index)
        {
            case 0: _waterKind = WaterGeometry.KIND_POND;  _waterDepth = 0.05f; _waterBank = 0.3f; _waterMaterial = "clear"; break;
            case 1: _waterKind = WaterGeometry.KIND_POND;  _waterDepth = 1.5f;  _waterBank = 2f;   _waterMaterial = "lake";  break;
            case 2: _waterKind = WaterGeometry.KIND_RIVER; _waterDepth = 1.2f;  _waterBank = 2f;   _waterWidth = 6f; _waterMaterial = "lake"; break;
            case 3: _waterKind = WaterGeometry.KIND_POND;  _waterDepth = 4f;    _waterBank = 6f;   _waterMaterial = "dark";  break;
        }
        EnsureWaterMaterialValid();
        if (_mode == EditMode.DrawWater) ResetWaterDrawing();   // a half-drawn pond can't become a river
    }

    // Default the material to a real palette entry, never an id the user's palette lacks.
    private void EnsureWaterMaterialValid()
    {
        var pal = worldRenderer != null ? worldRenderer.WaterPalette : null;
        if (pal?.entries != null && pal.entries.Count > 0 && !pal.Has(_waterMaterial))
            _waterMaterial = pal.entries[0].id;
    }

    private WaterBodyDef WaterBodyFromSettings(List<Vector2> ctrl, string id)
    {
        var pts = new float[ctrl.Count][];
        for (int i = 0; i < ctrl.Count; i++) pts[i] = new[] { ctrl[i].x, ctrl[i].y };
        return new WaterBodyDef
        {
            id            = id,
            kind          = _waterKind,
            material      = _waterMaterial,
            points        = pts,
            width         = _waterWidth,
            smoothing     = _waterSmoothing,
            surfaceY      = WaterGeometry.ClampSurfaceY(_waterLevel, _waterDepth),
            depth         = _waterDepth,
            bankWidth     = _waterBank,
            clipToLot     = _waterClipToLot,
        };
    }

    // ---- DrawWater ----

    private void StartDrawWater()
    {
        ExitCurrentMode();
        // Auto-create a blank working environment when none is active (the site/path pattern). The
        // ensure can fire OnActiveEnvironmentSwitched, so the mode is armed after it.
        var env = libraryBrowser != null ? libraryBrowser.EnsureWorkingEnvironment() : null;
        if (env == null) return;
        _mode = EditMode.DrawWater;
        _waterPts.Clear();
        _waterStroking = false;
        _waterLastClickTime = -1f;
        EnsureWaterMaterialValid();
        EnsureWaterPreview();
    }

    private void UpdateDrawWater()
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env == null) { StopDrawWater(); return; }

        // Two-stage Esc, like the fence and site tools: mid-shape it drops the shape and keeps the
        // tool armed; with nothing in flight it leaves the tool.
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame)
        {
            if (_waterPts.Count > 0 || _waterStroking) { ResetWaterDrawing(); return; }
            StopDrawWater();
            return;
        }

        bool river = WaterIsRiver(_waterKind);
        Vector3 cursor = GroundPointOnTerrain();

        if (river && _waterFreehand)
        {
            if (LMBDown && !IsMouseOverUI()) { _waterStroking = true; _waterPts.Clear(); _waterPts.Add(cursor); }
            if (_waterStroking && LMBHeld &&
                (_waterPts.Count == 0 || Vector3.Distance(cursor, _waterPts[_waterPts.Count - 1]) > PATH_FREEHAND_STEP))
                _waterPts.Add(cursor);
            if (_waterStroking && LMBUp) { _waterStroking = false; FinishWater(); return; }
        }
        else
        {
            int minPts = WaterGeometry.MinPoints(_waterKind);
            if (LMBDown && !IsMouseOverUI())
            {
                float now = Time.time;
                // The first click of a double-click already dropped the last point.
                if (_waterPts.Count >= minPts && now - _waterLastClickTime < DOUBLE_CLICK_INTERVAL) { FinishWater(); return; }
                _waterLastClickTime = now;
                _waterPts.Add(cursor);
            }
            if (KB != null && !TypingInUI && KB.backspaceKey.wasPressedThisFrame && _waterPts.Count > 0)
                _waterPts.RemoveAt(_waterPts.Count - 1);
            if (KB != null && !TypingInUI && (KB.enterKey.wasPressedThisFrame || KB.numpadEnterKey.wasPressedThisFrame))
            { FinishWater(); return; }
        }

        bool rubberBand = (!(river && _waterFreehand) && _waterPts.Count >= 1) || (river && _waterFreehand && _waterStroking);
        var ctrl = WaterPts2D(_waterPts);
        if (rubberBand) ctrl.Add(new Vector2(cursor.x, cursor.z));
        UpdateWaterPreview(ctrl, bodyId: null);
    }

    private static List<Vector2> WaterPts2D(List<Vector3> pts)
    {
        var ctrl = new List<Vector2>(pts.Count + 1);
        foreach (var p in pts) ctrl.Add(new Vector2(p.x, p.z));
        return ctrl;
    }

    private void FinishWater()
    {
        bool river = WaterIsRiver(_waterKind);
        var raw  = WaterPts2D(_waterPts);
        var ctrl = river && _waterFreehand ? PathGeometry.Simplify(raw, PATH_SIMPLIFY_TOL) : raw;

        if (ctrl.Count >= WaterGeometry.MinPoints(_waterKind))
        {
            var env = libraryBrowser?.EnsureWorkingEnvironment();
            if (env?.site != null && !ActiveLocked)
            {
                _history?.RecordBefore(EditHistory.Scope.Environment, "Draw water");
                env.site.waterBodies ??= new List<WaterBodyDef>();
                env.site.waterBodies.Add(WaterBodyFromSettings(ctrl, Guid.NewGuid().ToString("D")));
                libraryBrowser?.MarkDirty();
                // The active env re-applies its heightmap on render, which carves the bed and
                // re-seats everything that drapes on the ground.
                worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
                RebindSelection();
            }
        }
        ResetWaterDrawing();   // the tool stays armed for the next body
    }

    // Drops the in-progress shape and hides its preview, leaving the tool armed.
    private void ResetWaterDrawing()
    {
        _waterPts.Clear();
        _waterStroking = false;
        _waterLastClickTime = -1f;
        HideWaterPreview();
    }

    private void StopDrawWater()
    {
        DestroyWaterPreview();
        _waterPts.Clear();
        _waterStroking = false;
        _mode = EditMode.Browse;
    }

    private void DeleteWater(string waterId)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        if (env?.site?.waterBodies == null || ActiveLocked) return;
        if (_mode == EditMode.EditWater && waterId == _waterEditId) StopEditWater();
        _history?.RecordBefore(EditHistory.Scope.Environment, "Delete water");
        env.site.waterBodies.RemoveAll(w => w != null && w.id == waterId);
        libraryBrowser?.MarkDirty();
        worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);   // the bed fills back in
        RebindSelection();
    }

    // ---- Preview: the committed mesh (WorldRenderer.BuildWaterMesh) at the provisional surface,
    // plus a draped ring for a pond so an open outline reads while it is still being clicked. ----

    private void EnsureWaterPreview()
    {
        EnsureOutlinePreview(ref _waterOutlineGO, ref _waterOutlineMF, WATER_PREVIEW_COLOR, "WaterOutlinePreview");
        if (_waterFillGO == null)
        {
            _waterFillGO = new GameObject("WaterFillPreview");
            _waterFillMF = _waterFillGO.AddComponent<MeshFilter>();
            _waterFillMR = _waterFillGO.AddComponent<MeshRenderer>();
            _waterFillMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _waterFillMR.receiveShadows = false;
        }
        if (_waterFillFallbackMat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            _waterFillFallbackMat = new Material(sh) { color = new Color(0.3f, 0.6f, 0.9f, 0.7f) };
        }
        _waterFillGO.SetActive(true);
    }

    // `bodyId` is the id of a committed body being edited, or null while drawing.
    //
    // Editing: the bed is already carved for this body, so the preview is the real mesh at its
    // surface height. Drawing: the surface usually sits at or below the ground until the commit
    // digs the bed, so a true-height preview would be buried and invisible. Instead the draw
    // preview drapes on the ground: a river ribbon hugs the terrain like a path preview, a pond
    // fill floats just above the highest point of its outline.
    private void UpdateWaterPreview(List<Vector2> ctrl, string bodyId)
    {
        if (_waterOutlineMF == null || _waterFillMF == null) return;
        bool river = WaterIsRiver(_waterKind);

        // Ring guide for ponds; the river's own ribbon is its guide.
        UpdateOutlinePreview(_waterOutlineMF, river ? new List<Vector2>() : ctrl, loop: ctrl.Count >= 3);

        var stale = _waterFillMF.sharedMesh;
        Mesh mesh = null;
        if (worldRenderer != null && ctrl.Count >= WaterGeometry.MinPoints(_waterKind))
        {
            var body = WaterBodyFromSettings(ctrl, bodyId);
            if (bodyId != null) mesh = worldRenderer.BuildWaterMesh(body, WaterGeometry.SurfaceY(body));
            else                mesh = BuildDrapedWaterPreview(body);
        }
        _waterFillMF.sharedMesh = mesh;
        if (stale != null) Destroy(stale);

        var mat = worldRenderer != null ? worldRenderer.GetWaterMaterial(_waterMaterial) : null;
        _waterFillMR.sharedMaterial = mat != null ? mat : _waterFillFallbackMat;
        _waterFillGO.SetActive(mesh != null);
    }

    // Draw-time preview that stays visible on un-dug ground (see UpdateWaterPreview). A hair above
    // the path surface so it never z-fights a path it crosses.
    private const float WATER_PREVIEW_LIFT = 0.01f;

    private Mesh BuildDrapedWaterPreview(WaterBodyDef body)
    {
        var ctrl = WaterGeometry.ControlPoints(body?.points);
        if (worldRenderer == null || body == null || ctrl.Count < WaterGeometry.MinPoints(body.kind)) return null;
        float Y(float x, float z) => worldRenderer.SamplePathSurfaceY(x, z) + WATER_PREVIEW_LIFT;

        if (WaterIsRiver(body.kind))
        {
            var dense = WaterGeometry.RiverCenterline(ctrl, body.width, body.smoothing);
            var centerline = new List<Vector3>(dense.Count);
            foreach (var d in dense) centerline.Add(new Vector3(d.x, Y(d.x, d.y), d.y));
            return PathMesh.Build(centerline, WaterGeometry.ClampWidth(body.width), Y);
        }

        // A pond fill has no interior vertices to drape, so it floats level just above the
        // highest point of its outline.
        float top = float.MinValue;
        foreach (var p in WaterGeometry.ShoreSamples(body)) top = Mathf.Max(top, Y(p.x, p.y));
        if (top == float.MinValue) top = Y(ctrl[0].x, ctrl[0].y);
        return WaterGeometry.BuildPondMesh(ctrl, top);
    }

    private void HideWaterPreview()
    {
        if (_waterOutlineMF != null) UpdateOutlinePreview(_waterOutlineMF, new List<Vector2>(), loop: false);
        if (_waterFillGO != null) _waterFillGO.SetActive(false);
    }

    private void DestroyWaterPreview()
    {
        if (_waterOutlineGO != null)
        {
            if (_waterOutlineMF != null && _waterOutlineMF.sharedMesh != null) Destroy(_waterOutlineMF.sharedMesh);
            Destroy(_waterOutlineGO); _waterOutlineGO = null; _waterOutlineMF = null;
        }
        if (_waterFillGO != null)
        {
            if (_waterFillMF != null && _waterFillMF.sharedMesh != null) Destroy(_waterFillMF.sharedMesh);
            Destroy(_waterFillGO); _waterFillGO = null; _waterFillMF = null; _waterFillMR = null;
        }
    }

    // ---- EditWater ----

    private WaterBodyDef CurrentEditWater() => FindWater(libraryBrowser?.CurrentEnvironment, _waterEditId);

    private static WaterBodyDef FindWater(EnvironmentDef env, string id)
    {
        var list = env?.site?.waterBodies;
        if (list == null || string.IsNullOrEmpty(id)) return null;
        return list.Find(w => w != null && w.id == id);
    }

    // True when `id` names a body in the ACTIVE, editable environment (ids are only unique per env).
    private bool IsEditableWater(string id) =>
        !ActiveLocked && !string.IsNullOrEmpty(id) && FindWater(libraryBrowser?.CurrentEnvironment, id) != null;

    // Scene click on a rendered body → its editor. The rail switches FIRST (UIMode.Set fires
    // ExitForModeSwitch, which would otherwise tear down the session being started).
    private void EnterEditWaterFromClick(string waterId)
    {
        Deselect();
        _showWaterList = true;
        UIMode.Set(AppMode.Terrain);
        StartEditWater(waterId);
    }

    private void StartEditWater(string id)
    {
        ExitCurrentMode();
        var body = FindWater(libraryBrowser?.CurrentEnvironment, id);
        if (body == null || ActiveLocked) { _mode = EditMode.Browse; return; }

        _mode = EditMode.EditWater;
        _waterEditId = id;
        _waterEditSel = -1;
        _waterEditDragging = false;
        _waterEditMoved = false;
        _waterEditPendingRender = false;
        AdoptWaterSettings(body);
        SeedWaterEditPoints(body);
        EnsureHandleMaterials();
        EnsureWaterPreview();
        RebuildWaterHandles();
        // The live preview is the single visible truth of the body while editing.
        SetCommittedWaterVisible(id, false);
        UpdateWaterEditPreview();
    }

    private void AdoptWaterSettings(WaterBodyDef body)
    {
        _waterKind      = WaterIsRiver(body.kind) ? WaterGeometry.KIND_RIVER : WaterGeometry.KIND_POND;
        _waterMaterial  = body.material;
        _waterWidth     = WaterGeometry.ClampWidth(body.width);
        _waterSmoothing = Mathf.Clamp01(body.smoothing);
        _waterDepth     = WaterGeometry.ClampDepth(body.depth);
        _waterBank      = Mathf.Clamp(body.bankWidth, 0f, WaterGeometry.MAX_BANK);
        _waterLevel     = WaterGeometry.ClampSurfaceY(body.surfaceY, _waterDepth);
        _waterClipToLot = body.clipToLot;
        EnsureWaterMaterialValid();
    }

    private void SeedWaterEditPoints(WaterBodyDef body)
    {
        _waterEditPts.Clear();
        if (body?.points != null)
            foreach (var p in body.points)
                if (p != null && p.Length >= 2) _waterEditPts.Add(new Vector3(p[0], 0f, p[1]));
    }

    // Re-sync after an undo/redo restored the environment under the open editor. False when the
    // body no longer exists.
    private bool ReseedEditWater()
    {
        var body = CurrentEditWater();
        if (body == null || !WaterGeometry.HasGeometry(body)) return false;
        _waterEditSel = -1; _waterEditDragging = false; _waterEditMoved = false; _waterEditPendingRender = false;
        AdoptWaterSettings(body);
        SeedWaterEditPoints(body);
        EnsureHandleMaterials();
        EnsureWaterPreview();
        SetCommittedWaterVisible(_waterEditId, false);
        RebuildWaterHandles();
        UpdateWaterEditPreview();
        return true;
    }

    private void UpdateEditWater()
    {
        if (KB != null && !TypingInUI && KB.escapeKey.wasPressedThisFrame) { StopEditWater(); return; }
        var body = CurrentEditWater();
        if (body == null) { StopEditWater(); return; }

        // A live slider edit (rail) commits its data at once but waits for the mouse to release
        // before the expensive re-render, so a slider drag does not rebuild the world every frame.
        if (_waterEditPendingRender && !LMBHeld)
        {
            _waterEditPendingRender = false;
            CommitEditWater(reRender: true);
            RebuildWaterHandles();
        }

        // Delete/Backspace: a picked point goes (keeping at least a triangle / a line); with no
        // point picked the key means the whole body, which is the state right after a scene click.
        bool delPressed = KB != null && !TypingInUI &&
                          (KB.deleteKey.wasPressedThisFrame || KB.backspaceKey.wasPressedThisFrame);
        if (delPressed && !ActiveLocked)
        {
            bool pointPicked = _waterEditSel >= 0 && _waterEditSel < _waterEditPts.Count;
            if (pointPicked && _waterEditPts.Count > WaterGeometry.MinPoints(_waterKind))
            {
                _history?.RecordBefore(EditHistory.Scope.Environment, "Edit water");
                _waterEditPts.RemoveAt(_waterEditSel);
                _waterEditSel = -1;
                CommitEditWater(reRender: true);
                RebuildWaterHandles();
            }
            else
            {
                DeleteWater(_waterEditId);   // tears this mode down; touch nothing below
                return;
            }
        }

        if (LMBDown && !IsMouseOverUI())
        {
            int hit = PickWaterHandle();
            if (hit >= 0)
            {
                _waterEditSel = hit;
                _waterEditDragging = true;
                _waterEditMoved = false;
                _history?.BeginGesture(EditHistory.Scope.Environment, "Edit water");
            }
            else if (TryInsertPointOnWater(GroundPointOnTerrain()))
            {
                _history?.RecordBefore(EditHistory.Scope.Environment, "Edit water");
                CommitEditWater(reRender: true);
                RebuildWaterHandles();
            }
            // Clicked another body → retarget the editor to it (the fence pattern). The return is
            // load-bearing: EnterEditWaterFromClick stops this session first.
            else if (TryPickWater(out string other) && other != _waterEditId)
            {
                EnterEditWaterFromClick(other);
                return;
            }
        }

        if (_waterEditDragging && LMBHeld && _waterEditSel >= 0 && _waterEditSel < _waterEditPts.Count)
        {
            Vector3 g = GroundPointOnTerrain();
            var nv = new Vector3(g.x, 0f, g.z);
            if ((nv - _waterEditPts[_waterEditSel]).sqrMagnitude > 1e-6f) _waterEditMoved = true;
            _waterEditPts[_waterEditSel] = nv;
        }
        if (LMBUp && _waterEditDragging)
        {
            _waterEditDragging = false;
            if (_waterEditMoved) CommitEditWater(reRender: true);
            _waterEditMoved = false;
        }

        UpdateWaterHandlePositions();
        UpdateWaterEditPreview();
    }

    private void UpdateWaterEditPreview() => UpdateWaterPreview(WaterPts2D(_waterEditPts), _waterEditId);

    // Writes the working points + settings back to the body. `reRender` replays the heightmap (the
    // bed follows the new shape) and rebuilds the env; the committed mesh is re-hidden while the
    // preview is still the visible truth.
    private void CommitEditWater(bool reRender = true)
    {
        var env = libraryBrowser?.CurrentEnvironment;
        var body = CurrentEditWater();
        if (env == null || body == null || _waterEditPts.Count < WaterGeometry.MinPoints(body.kind)) return;
        var pts = new float[_waterEditPts.Count][];
        for (int i = 0; i < _waterEditPts.Count; i++) pts[i] = new[] { _waterEditPts[i].x, _waterEditPts[i].z };
        body.points        = pts;
        body.material      = _waterMaterial;
        body.width         = _waterWidth;
        body.smoothing     = _waterSmoothing;
        body.surfaceY      = WaterGeometry.ClampSurfaceY(_waterLevel, _waterDepth);
        body.depth         = _waterDepth;
        body.bankWidth     = _waterBank;
        body.clipToLot     = _waterClipToLot;
        libraryBrowser?.MarkDirty();
        if (reRender)
        {
            worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs);
            RebindSelection();
            if (_mode == EditMode.EditWater) SetCommittedWaterVisible(_waterEditId, false);
        }
    }

    // Rail sliders call this on every changed frame: data commits at once (undo-grouped by the
    // gesture), the re-render waits for the release (UpdateEditWater).
    private void LiveCommitEditWater()
    {
        _history?.BeginGesture(EditHistory.Scope.Environment, "Edit water");
        CommitEditWater(reRender: false);
        _waterEditPendingRender = true;
    }

    private bool WaterEditSettingsDiffer(WaterBodyDef w) =>
        w != null && (Mathf.Abs(w.width - _waterWidth) > 0.001f ||
                      Mathf.Abs(w.smoothing - _waterSmoothing) > 0.001f ||
                      Mathf.Abs(w.surfaceY - WaterGeometry.ClampSurfaceY(_waterLevel, _waterDepth)) > 0.001f ||
                      Mathf.Abs(w.depth - _waterDepth) > 0.001f ||
                      Mathf.Abs(w.bankWidth - _waterBank) > 0.001f ||
                      w.clipToLot != _waterClipToLot ||
                      !string.Equals(w.material, _waterMaterial, StringComparison.OrdinalIgnoreCase));

    // Click on the outline inserts a point there: the closed-ring rule for a pond
    // (PolygonEdit), the open-polyline rule for a river.
    private bool TryInsertPointOnWater(Vector3 ground)
    {
        var click = new Vector2(ground.x, ground.z);
        var corners = WaterPts2D(_waterEditPts);
        if (!WaterIsRiver(_waterKind))
        {
            if (!PolygonFrame.Bbox(corners, out float w, out float l)) return false;
            if (!PolygonEdit.TryInsertVertex(corners, click, PolygonEdit.PickTolerance(Mathf.Max(1f, w), Mathf.Max(1f, l)),
                                             out int idx, out Vector2 pt)) return false;
            _waterEditPts.Insert(idx, new Vector3(pt.x, 0f, pt.y));
            _waterEditSel = idx;
            return true;
        }

        if (corners.Count < 2) return false;
        float bestD = float.MaxValue; int bestSeg = -1; Vector2 bestPt = click;
        for (int i = 0; i < corners.Count - 1; i++)
        {
            Vector2 a = corners[i], b = corners[i + 1];
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(click - a, ab) / len2);
            Vector2 proj = a + t * ab;
            float d = Vector2.Distance(click, proj);
            if (d < bestD) { bestD = d; bestSeg = i; bestPt = proj; }
        }
        float tol = Mathf.Max(_waterWidth, 2f);
        if (bestSeg < 0 || bestD > tol) return false;
        _waterEditPts.Insert(bestSeg + 1, new Vector3(bestPt.x, 0f, bestPt.y));
        _waterEditSel = bestSeg + 1;
        return true;
    }

    private void InsertPointLongestSegmentWater()
    {
        int n = _waterEditPts.Count;
        if (n < 2) return;
        bool closed = !WaterIsRiver(_waterKind);
        int segs = closed ? n : n - 1;
        int seg = 0; float best = -1f;
        for (int i = 0; i < segs; i++)
        {
            float d = Vector3.Distance(_waterEditPts[i], _waterEditPts[(i + 1) % n]);
            if (d > best) { best = d; seg = i; }
        }
        Vector3 mid = (_waterEditPts[seg] + _waterEditPts[(seg + 1) % n]) * 0.5f;
        _history?.RecordBefore(EditHistory.Scope.Environment, "Edit water");
        _waterEditPts.Insert(seg + 1, mid);
        _waterEditSel = seg + 1;
        CommitEditWater(reRender: true);
        RebuildWaterHandles();
    }

    // Hide/show every committed mesh for one body (a body renders as one GO, but stay id-driven
    // like the fence twin so a future split into several meshes keeps working).
    private void SetCommittedWaterVisible(string id, bool visible)
    {
        if (string.IsNullOrEmpty(id)) return;
        // Include inactive: the whole point of `visible: true` is to find the mesh this hid earlier.
        foreach (var wm in FindObjectsByType<WaterMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (wm != null && wm.waterId == id) wm.gameObject.SetActive(visible);
    }

    // Nearest active-env water body under the cursor, or false. The WaterMarker twin of TryPickFence.
    private bool TryPickWater(out string waterId)
    {
        waterId = null;
        if (mainCamera == null) return false;
        Ray ray = mainCamera.ScreenPointToRay(MousePos);
        float best = float.MaxValue;
        foreach (var h in Physics.RaycastAll(ray, 1000f))
        {
            var wm = h.collider.GetComponentInParent<WaterMarker>();
            if (wm != null && h.distance < best && IsEditableWater(wm.waterId))
            { best = h.distance; waterId = wm.waterId; }
        }
        return waterId != null;
    }

    // ---- Handles (sphere primitives, screen-space picked; the path handle materials) ----

    private int PickWaterHandle()
    {
        if (mainCamera == null) return -1;
        Vector2 m = new(MousePos.x, MousePos.y);
        float best = PATH_HANDLE_PICK_PX * PATH_HANDLE_PICK_PX; int idx = -1;
        for (int i = 0; i < _waterEditPts.Count; i++)
        {
            Vector3 sp = mainCamera.WorldToScreenPoint(WaterHandleWorldPos(i));
            if (sp.z < 0f) continue;
            float d2 = (new Vector2(sp.x, sp.y) - m).sqrMagnitude;
            if (d2 < best) { best = d2; idx = i; }
        }
        return idx;
    }

    // A handle floats above the higher of the ground and the water surface, so a corner on the
    // carved bank is never hidden under the surface.
    private Vector3 WaterHandleWorldPos(int i)
    {
        Vector3 p = _waterEditPts[i];
        float ground = worldRenderer != null ? worldRenderer.SampleTerrainSurfaceY(p.x, p.z) : 0f;
        float surface = WaterGeometry.ClampSurfaceY(_waterLevel, _waterDepth);
        return new Vector3(p.x, Mathf.Max(ground, surface) + 0.18f, p.z);
    }

    private void RebuildWaterHandles()
    {
        foreach (var h in _waterHandles) if (h != null) Destroy(h);
        _waterHandles.Clear();
        EnsureHandleMaterials();
        for (int i = 0; i < _waterEditPts.Count; i++)
        {
            var h = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            h.name = "WaterHandle";
            var col = h.GetComponent<Collider>(); if (col != null) Destroy(col);   // picked by screen-space
            h.transform.localScale = Vector3.one * 0.8f;
            _waterHandles.Add(h);
        }
        UpdateWaterHandlePositions();
    }

    private void UpdateWaterHandlePositions()
    {
        if (_waterHandles.Count != _waterEditPts.Count) { RebuildWaterHandles(); return; }
        for (int i = 0; i < _waterHandles.Count; i++)
        {
            if (_waterHandles[i] == null) continue;
            _waterHandles[i].transform.position = WaterHandleWorldPos(i);
            var r = _waterHandles[i].GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = i == _waterEditSel ? _pathHandleSelMat : _pathHandleMat;
        }
    }

    private void StopEditWater()
    {
        // A pending live edit's data is already committed; give it its re-render before leaving.
        bool pending = _waterEditPendingRender;
        _waterEditPendingRender = false;
        foreach (var h in _waterHandles) if (h != null) Destroy(h);
        _waterHandles.Clear();
        DestroyWaterPreview();
        string id = _waterEditId;
        _waterEditId = null;
        _waterEditSel = -1;
        _waterEditDragging = false;
        _waterEditMoved = false;
        _waterEditPts.Clear();
        _mode = EditMode.Browse;
        var env = libraryBrowser?.CurrentEnvironment;
        if (pending && env != null) { worldRenderer?.RenderEnvironment(env, libraryBrowser?.CurrentBuildingDefs); RebindSelection(); }
        else SetCommittedWaterVisible(id, true);
    }

    // ---- Rail: the "Draw water" block of the Terrain editor (called from DrawTerrainSection) ----

    private void DrawWaterSection(EnvironmentDef env)
    {
        GUILayout.BeginHorizontal();
        if (UITheme.ToggleButton(_mode == EditMode.DrawWater, "Draw water", UITips.DrawWater, GUILayout.Height(UITheme.RowH))
            && _mode != EditMode.DrawWater) StartDrawWater();
        if (_mode == EditMode.DrawWater && UITheme.Button("Done", UITips.DoneDrawWater, GUILayout.Width(54f))) StopDrawWater();
        GUILayout.EndHorizontal();

        bool drawing = _mode == EditMode.DrawWater;
        bool editing = _mode == EditMode.EditWater;
        if (drawing || editing)
        {
            bool river = WaterIsRiver(_waterKind);
            if (drawing)
            {
                int kindSel = river ? 1 : 0;
                int kindNew = UITheme.Segmented(kindSel, UITips.WaterKindLabels, UITips.WaterKindTips);
                if (kindNew != kindSel)
                {
                    _waterKind = kindNew == 1 ? WaterGeometry.KIND_RIVER : WaterGeometry.KIND_POND;
                    ResetWaterDrawing();
                    river = kindNew == 1;
                }
                GUILayout.BeginHorizontal();
                for (int i = 0; i < UITips.WaterPresetLabels.Length; i++)
                    if (UITheme.Button(UITips.WaterPresetLabels[i], UITips.WaterPresetTips[i])) ApplyWaterPreset(i);
                GUILayout.EndHorizontal();
                if (river)
                    _waterFreehand = UITheme.Checkbox(_waterFreehand,
                        _waterFreehand ? "  Freehand (drag)" : "  Straight (click; Enter/dbl-click ends)", UITips.WaterFreehand);
            }
            else if (UITheme.Button("Done editing water", UITips.DoneEditWater)) StopEditWater();

            _waterDepth  = UITheme.Slider("Depth", _waterDepth, WaterGeometry.MIN_DEPTH, 10f, UITips.WaterDepth, "0.00", " m");
            // The level can sink into the hole but never below its bed, so a shallower bed lifts it.
            float minLevel = WaterGeometry.MinSurfaceY(_waterDepth);
            _waterLevel  = UITheme.Slider("Water level", Mathf.Max(_waterLevel, minLevel), minLevel, WaterGeometry.MAX_SURFACE_Y,
                                          UITips.WaterLevel, "+0.00;-0.00", " m");
            _waterBank   = UITheme.Slider("Bank",  _waterBank,  0f, WaterGeometry.MAX_BANK,  UITips.WaterBank, "0.0", " m");
            if (river)
            {
                _waterWidth     = UITheme.Slider("Width", _waterWidth, WaterGeometry.MIN_WIDTH, WaterGeometry.MAX_WIDTH, UITips.WaterWidth, "0.0", " m");
                _waterSmoothing = UITheme.Slider("Smoothing", _waterSmoothing, 0f, 1f, UITips.WaterSmoothing, "0.00");
            }
            _waterClipToLot = UITheme.Checkbox(_waterClipToLot, "  Stay inside lot", UITips.WaterClipToLot);

            UITheme.Label($"Water: {_waterMaterial}", UITips.WaterMaterialLabel);
            var pal = worldRenderer != null ? worldRenderer.WaterPalette : null;
            if (pal?.entries != null && pal.entries.Count > 0)
            {
                float h = Mathf.Clamp(8 + pal.entries.Count * 26, 26, 130);
                _waterMatScroll = GUILayout.BeginScrollView(_waterMatScroll, false, false, GUIStyle.none,
                    GUI.skin.verticalScrollbar, GUILayout.Height(h));
                foreach (var e in pal.entries)
                {
                    if (e == null) continue;
                    bool sel = string.Equals(e.id, _waterMaterial, StringComparison.OrdinalIgnoreCase);
                    if (UITheme.ListItem(sel, e.id, UITips.WaterMaterial) && !sel) _waterMaterial = e.id;
                }
                GUILayout.EndScrollView();
            }
            else UITheme.Label("No water materials yet.", "Run Tools, CXR, Palettes, Create Water Palette (seeded) in the Unity menu to add the four stock colors.");

            if (drawing)
                UITheme.Label(river ? "Click points, Enter ends. Hover for help." : "Click corners, Enter closes. Hover for help.",
                              river ? UITips.WaterKindTips[1] : UITips.WaterKindTips[0]);
            else
            {
                if (UITheme.Button("Insert point (split longest segment)", UITips.InsertPoint)) InsertPointLongestSegmentWater();
                UITheme.Label("Drag dots, click the outline to add. Hover for help.", UITips.WaterEditHint);
                // Slider / material edits commit live to the body being edited; the re-render waits
                // for the mouse to release.
                var ew = CurrentEditWater();
                if (WaterEditSettingsDiffer(ew)) LiveCommitEditWater();
            }
        }

        // List of existing bodies with edit + delete, collapsed behind a foldout by default.
        int count = env?.site?.waterBodies?.Count ?? 0;
        if (count > 0)
            _showWaterList = UITheme.Foldout(_showWaterList, $"Water ({count})", UITips.WaterFold);
        if (count > 0 && _showWaterList)
        {
            _waterListScroll = GUILayout.BeginScrollView(_waterListScroll, false, false, GUIStyle.none,
                GUI.skin.verticalScrollbar, GUILayout.Height(Mathf.Min(120, 4 + count * 24)));
            for (int i = 0; i < env.site.waterBodies.Count; i++)
            {
                var w = env.site.waterBodies[i];
                if (w == null) continue;
                bool isEditing = editing && w.id == _waterEditId;
                string kindLabel = WaterIsRiver(w.kind) ? "river" : "pond";
                GUILayout.BeginHorizontal();
                int n = w.points?.Length ?? 0;
                string rowTip = WaterIsRiver(w.kind)
                    ? $"River, {w.width:0.0} m wide, {w.depth:0.00} m deep, {n} points, {w.material} water."
                    : $"Pond, {w.depth:0.00} m deep, {n} corners, {w.material} water.";
                GUILayout.Label(new GUIContent($"{i + 1}. {kindLabel} · {w.material}{(isEditing ? "  (editing)" : "")}", rowTip));
                GUILayout.FlexibleSpace();
                if (!isEditing && UITheme.Button("Edit", UITips.EditWater, GUILayout.Width(46f))) { StartEditWater(w.id); break; }
                if (UITheme.Button("Delete", UITips.DeleteWater, GUILayout.Width(60f))) { DeleteWater(w.id); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }
    }
}
