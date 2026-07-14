using System;
using UnityEngine;
using UnityEngine.InputSystem;

// Runtime transform gizmo (§5 Transform: Unity's editor gizmos are unavailable at
// runtime, so this is the minimal on-plane drag handle). Hit-tested against the
// ground plane through the selection's center.
//
// Rendering uses LineRenderers + a cube mesh, NOT GL/OnRenderObject: this project
// runs Unity 6 URP with Render Graph enabled, which never invokes OnRenderObject,
// so immediate-mode GL drawing silently renders nothing. The line material uses
// ZTest Always so handles stay visible through the selected object.
//
// Handles:
//   - X arrow (red) / Z arrow (blue)  → axis-constrained move
//   - center pad (yellow square)      → free move on the ground plane
//   - three rings (R/G/B)             → rotate around world X / Y / Z (snap via rotationSnap)
//   - top cube (purple)               → uniform scale (drag up/down)
// A yellow wire box around the selection bounds is drawn as part of the gizmo.
//
// EditController owns it: call Tick() once per frame from Update (not self-driven,
// so input ordering between controller and gizmo stays deterministic), subscribe to
// the delta events, and call SetTarget/Clear on selection changes.
public class TransformGizmo : MonoBehaviour
{
    public enum Handle { None, MoveX, MoveY, MoveZ, MoveFree, RotateX, RotateY, RotateZ, Scale }

    // Only one tool's handles are shown / pickable at a time (set by the controller).
    public enum Mode { Move, Rotate, Scale }
    private Mode _mode = Mode.Move;

    public void SetMode(Mode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        if (_drag == Handle.None) _hover = Handle.None;
    }

    // Rotation drags snap to this increment (degrees) when > 0; <= 0 = free rotation. The
    // caller may retune it every frame (EditController: 15° only while Shift is held).
    public float rotationSnap = 15f;

    public event Action<Vector3> MoveDelta;    // world-space ground-plane delta
    public event Action<Vector3> RotateDelta;  // per-axis euler-degree delta (one axis non-zero)
    public event Action<float>   ScaleDelta;   // additive uniform-scale delta
    public event Action          DragEnded;    // commit point (caller marks dirty)

    public bool IsDragging    => _drag != Handle.None;
    public bool IsInteracting => _drag != Handle.None || _hover != Handle.None;

    private Camera      _cam;
    private GameObject  _target;
    private Renderer[]  _renderers;            // cached on SetTarget (no per-frame GetComponents)
    private Handle      _hover = Handle.None;
    private Handle      _drag  = Handle.None;

    private Bounds  _bounds;
    private Vector3 _center;
    private float   _size = 2f;
    private Vector3 _prevHit;                  // ground-plane hit (move drag)
    private Vector2 _prevMouse;                // screen position (scale drag)

    // Rotate drag: accumulate raw in-plane angle; when rotationSnap > 0 emit only snapped
    // increments so the applied rotation lands on multiples of rotationSnap without losing
    // sub-step motion, otherwise emit the raw angle (free rotation).
    private Vector3 _rotAxis;                  // world axis of the active rotate ring
    private Vector3 _rotPrevVec;               // previous in-plane vector (hit − center)
    private float   _rotRaw;                   // raw accumulated degrees since drag start
    private float   _rotEmitted;               // degrees already emitted

    private const float SCALE_SENS = 0.005f;   // scale units per pixel of vertical drag
    private const float PICK_PX    = 14f;      // screen-space pick radius for the scale cube
    private const int   RING_SEGS  = 48;

    private static readonly Color COL_X     = new(0.9f, 0.25f, 0.25f);
    private static readonly Color COL_Y     = new(0.35f, 0.85f, 0.35f);
    private static readonly Color COL_Z     = new(0.25f, 0.45f, 0.95f);
    private static readonly Color COL_PAD   = new(0.95f, 0.85f, 0.3f);
    private static readonly Color COL_RINGX = new(0.95f, 0.4f, 0.4f);   // rotate about X
    private static readonly Color COL_RINGY = new(0.3f, 0.9f, 0.4f);    // rotate about Y
    private static readonly Color COL_RINGZ = new(0.4f, 0.55f, 0.95f);  // rotate about Z
    private static readonly Color COL_SCALE = new(0.85f, 0.5f, 0.95f);
    private static readonly Color COL_BOX   = Color.yellow;

    // Visuals (built lazily, all under one root so show/hide is a single SetActive)
    private GameObject   _visualRoot;
    private LineRenderer _xLine, _yLine, _zLine, _padLine, _ringXLine, _ringYLine, _ringZLine, _stemLine, _boxLine;
    private Transform    _scaleCube;
    private Material     _lineMat, _cubeMat;

    // Reused position buffers (no per-frame allocations)
    private readonly Vector3[] _arrowPts = new Vector3[5];
    private readonly Vector3[] _padPts   = new Vector3[4];
    private readonly Vector3[] _ringXPts = new Vector3[RING_SEGS];
    private readonly Vector3[] _ringYPts = new Vector3[RING_SEGS];
    private readonly Vector3[] _ringZPts = new Vector3[RING_SEGS];
    private readonly Vector3[] _stemPts  = new Vector3[2];
    private readonly Vector3[] _boxPts   = new Vector3[16];

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public void SetTarget(GameObject go, Camera cam)
    {
        _target    = go;
        _cam       = cam;
        _renderers = go != null ? go.GetComponentsInChildren<Renderer>() : null;
        if (_drag == Handle.None) _hover = Handle.None;
    }

    // Multi-select: frame the combined bounds of several objects. Deltas still emit once;
    // the controller applies them to every selected instance (each about its own pivot).
    public void SetTargets(System.Collections.Generic.List<GameObject> gos, Camera cam)
    {
        _cam = cam;
        _target = null;
        var rends = new System.Collections.Generic.List<Renderer>();
        if (gos != null)
            foreach (var go in gos)
            {
                if (go == null) continue;
                if (_target == null) _target = go;   // first live GO anchors the null-checks
                rends.AddRange(go.GetComponentsInChildren<Renderer>());
            }
        _renderers = rends.ToArray();
        if (_drag == Handle.None) _hover = Handle.None;
    }

    public void Clear()
    {
        _target    = null;
        _renderers = null;
        _hover     = Handle.None;
        _drag      = Handle.None;
        if (_visualRoot != null) _visualRoot.SetActive(false);
    }

    public void Tick()
    {
        if (_target == null || _cam == null || Mouse.current == null)
        {
            _hover = Handle.None;
            _drag  = Handle.None;
            if (_visualRoot != null) _visualRoot.SetActive(false);
            return;
        }

        RecomputeFrame();
        Vector2 mp = Mouse.current.position.ReadValue();

        if (_drag == Handle.None) _hover = Pick(mp);

        if (Mouse.current.leftButton.wasPressedThisFrame && _hover != Handle.None)
            BeginDrag(_hover, mp);

        if (_drag != Handle.None && Mouse.current.leftButton.isPressed)
            ContinueDrag(mp);

        if (_drag != Handle.None && Mouse.current.leftButton.wasReleasedThisFrame)
        {
            _drag = Handle.None;
            DragEnded?.Invoke();
        }

        UpdateVisuals();
    }

    private void OnDisable()
    {
        if (_visualRoot != null) _visualRoot.SetActive(false);
    }

    private void OnDestroy()
    {
        if (_visualRoot != null) Destroy(_visualRoot);
        if (_lineMat    != null) Destroy(_lineMat);
        if (_cubeMat    != null) Destroy(_cubeMat);
    }

    // -----------------------------------------------------------------------
    // Geometry
    // -----------------------------------------------------------------------

    private void RecomputeFrame()
    {
        Bounds b = default;
        bool first = true;
        if (_renderers != null)
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
        if (first) b = new Bounds(_target.transform.position, Vector3.one);

        _bounds = b;
        _center = b.center;
        _size   = Mathf.Max(b.size.magnitude * 0.5f, 2f);
    }

    private Vector3 ScaleHandlePos() => _center + Vector3.up * (_size * 1.15f);

    private bool GroundHit(Vector2 mp, out Vector3 hit)
    {
        var plane = new Plane(Vector3.up, _center);
        Ray ray = _cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
        if (plane.Raycast(ray, out float d)) { hit = ray.GetPoint(d); return true; }
        hit = Vector3.zero;
        return false;
    }

    private Handle Pick(Vector2 mp) => _mode switch
    {
        Mode.Move   => PickMove(mp),
        Mode.Rotate => PickRotate(mp),
        Mode.Scale  => PickScale(mp),
        _           => Handle.None,
    };

    private Handle PickScale(Vector2 mp)
    {
        // Scale cube sits above the ground plane → screen-space test
        Vector3 sp = _cam.WorldToScreenPoint(ScaleHandlePos());
        if (sp.z > 0f && Vector2.Distance(mp, new Vector2(sp.x, sp.y)) < PICK_PX)
            return Handle.Scale;
        return Handle.None;
    }

    private Handle PickMove(Vector2 mp)
    {
        // Ground-plane handles (center pad + X/Z arrows) take priority near the center/axes.
        if (GroundHit(mp, out Vector3 hit))
        {
            var h2 = new Vector2(hit.x, hit.z);
            var c2 = new Vector2(_center.x, _center.z);
            float tol = Mathf.Max(_size * 0.12f, 0.4f);
            if (Vector2.Distance(h2, c2) < _size * 0.2f)                return Handle.MoveFree;
            if (DistPointSeg(h2, c2, c2 + Vector2.right * _size) < tol) return Handle.MoveX;
            if (DistPointSeg(h2, c2, c2 + Vector2.up    * _size) < tol) return Handle.MoveZ;
        }

        // Vertical (Y) arrow goes above the plane → screen-space segment test.
        Vector3 a = _cam.WorldToScreenPoint(_center);
        Vector3 b = _cam.WorldToScreenPoint(_center + Vector3.up * _size);
        if (a.z > 0f && b.z > 0f && DistPointSeg(mp, new Vector2(a.x, a.y), new Vector2(b.x, b.y)) < PICK_PX)
            return Handle.MoveY;
        return Handle.None;
    }

    private Handle PickRotate(Vector2 mp)
    {
        // Pick the nearest of the three axis circles under the cursor.
        float eX = RingError(mp, Vector3.right);
        float eY = RingError(mp, Vector3.up);
        float eZ = RingError(mp, Vector3.forward);
        float best = Mathf.Min(eX, Mathf.Min(eY, eZ));
        if (best == float.MaxValue) return Handle.None;
        if (best == eY) return Handle.RotateY;
        if (best == eX) return Handle.RotateX;
        return Handle.RotateZ;
    }

    // World hit of the cursor ray on a vertical plane through the center that faces the
    // camera — used to read a Y delta for the vertical move arrow.
    private bool VerticalHit(Vector2 mp, out Vector3 hit)
    {
        Vector3 n = Vector3.ProjectOnPlane(_cam.transform.forward, Vector3.up);
        if (n.sqrMagnitude < 1e-4f) n = Vector3.forward;   // camera looking straight down
        var plane = new Plane(n.normalized, _center);
        Ray ray = _cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
        if (plane.Raycast(ray, out float d)) { hit = ray.GetPoint(d); return true; }
        hit = Vector3.zero;
        return false;
    }

    // Screen-space-ish error of the cursor against the radius-_size ring in the plane
    // with the given world normal. Returns MaxValue when the ring isn't reliably pickable
    // (cursor off the ring, or the view ray grazes the ring's plane near-parallel).
    private float RingError(Vector2 mp, Vector3 normal)
    {
        Ray ray = _cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
        if (Mathf.Abs(Vector3.Dot(ray.direction.normalized, normal)) < 0.15f) return float.MaxValue;
        var plane = new Plane(normal, _center);
        if (!plane.Raycast(ray, out float d)) return float.MaxValue;
        float err = Mathf.Abs(Vector3.Distance(ray.GetPoint(d), _center) - _size);
        return err < Mathf.Max(_size * 0.12f, 0.4f) ? err : float.MaxValue;
    }

    // World hit of the cursor ray on the rotation plane (normal through center).
    private bool RingHit(Vector2 mp, Vector3 normal, out Vector3 hit)
    {
        var plane = new Plane(normal, _center);
        Ray ray = _cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
        if (plane.Raycast(ray, out float d)) { hit = ray.GetPoint(d); return true; }
        hit = Vector3.zero;
        return false;
    }

    private static Vector3 RotAxisOf(Handle h) => h switch
    {
        Handle.RotateX => Vector3.right,
        Handle.RotateY => Vector3.up,
        Handle.RotateZ => Vector3.forward,
        _              => Vector3.zero,
    };

    private static float DistPointSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
        return Vector2.Distance(p, a + ab * t);
    }

    // -----------------------------------------------------------------------
    // Drag
    // -----------------------------------------------------------------------

    private void BeginDrag(Handle h, Vector2 mp)
    {
        _drag = h;
        switch (h)
        {
            case Handle.MoveX:
            case Handle.MoveZ:
            case Handle.MoveFree:
                if (GroundHit(mp, out Vector3 hit)) _prevHit = hit;
                else _drag = Handle.None;
                break;
            case Handle.MoveY:
                if (VerticalHit(mp, out Vector3 vhit)) _prevHit = vhit;
                else _drag = Handle.None;
                break;
            case Handle.RotateX:
            case Handle.RotateY:
            case Handle.RotateZ:
                _rotAxis = RotAxisOf(h);
                if (RingHit(mp, _rotAxis, out Vector3 rhit))
                {
                    _rotPrevVec = rhit - _center;
                    _rotRaw     = 0f;
                    _rotEmitted = 0f;
                }
                else _drag = Handle.None;
                break;
            case Handle.Scale:
                _prevMouse = mp;
                break;
        }
    }

    private void ContinueDrag(Vector2 mp)
    {
        switch (_drag)
        {
            case Handle.MoveX:
            case Handle.MoveZ:
            case Handle.MoveFree:
            {
                if (!GroundHit(mp, out Vector3 hit)) return;
                Vector3 d = hit - _prevHit;
                _prevHit = hit;
                if      (_drag == Handle.MoveX) d = new Vector3(d.x, 0f, 0f);
                else if (_drag == Handle.MoveZ) d = new Vector3(0f, 0f, d.z);
                else                            d.y = 0f;
                if (d != Vector3.zero) MoveDelta?.Invoke(d);
                break;
            }
            case Handle.MoveY:
            {
                if (!VerticalHit(mp, out Vector3 hit)) return;
                Vector3 d = new Vector3(0f, hit.y - _prevHit.y, 0f);
                _prevHit = hit;
                if (d != Vector3.zero) MoveDelta?.Invoke(d);
                break;
            }
            case Handle.RotateX:
            case Handle.RotateY:
            case Handle.RotateZ:
            {
                if (!RingHit(mp, _rotAxis, out Vector3 hit)) return;
                Vector3 cur = hit - _center;
                // SignedAngle about +Y matches the previous atan2/yaw convention.
                _rotRaw += Vector3.SignedAngle(_rotPrevVec, cur, _rotAxis);
                _rotPrevVec = cur;

                float target = rotationSnap > 0f ? Mathf.Round(_rotRaw / rotationSnap) * rotationSnap : _rotRaw;
                float emit   = target - _rotEmitted;
                _rotEmitted  = target;
                if (Mathf.Abs(emit) > 0.0001f) RotateDelta?.Invoke(_rotAxis * emit);
                break;
            }
            case Handle.Scale:
            {
                float d = (mp.y - _prevMouse.y) * SCALE_SENS;
                _prevMouse = mp;
                if (Mathf.Abs(d) > 0.00001f) ScaleDelta?.Invoke(d);
                break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Visuals (LineRenderers + cube; pipeline-independent)
    // -----------------------------------------------------------------------

    private void EnsureVisuals()
    {
        if (_visualRoot != null) return;

        // Internal-Colored: vertex-color unlit with exposed blend/cull/depth props —
        // the canonical runtime line material. ZTest Always keeps the gizmo on top.
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        _lineMat = ConfigureMat(new Material(shader));
        _cubeMat = ConfigureMat(new Material(shader));

        _visualRoot = new GameObject("TransformGizmoVisuals");
        _visualRoot.transform.SetParent(transform, false);

        _xLine     = MakeLine(_arrowPts.Length);
        _yLine     = MakeLine(_arrowPts.Length);
        _zLine     = MakeLine(_arrowPts.Length);
        _padLine   = MakeLine(_padPts.Length,   loop: true);
        _ringXLine = MakeLine(_ringXPts.Length, loop: true);
        _ringYLine = MakeLine(_ringYPts.Length, loop: true);
        _ringZLine = MakeLine(_ringZPts.Length, loop: true);
        _stemLine  = MakeLine(_stemPts.Length);
        _boxLine   = MakeLine(_boxPts.Length);

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "ScaleHandle";
        Destroy(cube.GetComponent<BoxCollider>());   // must not block selection raycasts
        cube.transform.SetParent(_visualRoot.transform, false);
        var mr = cube.GetComponent<MeshRenderer>();
        mr.sharedMaterial    = _cubeMat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;
        _scaleCube = cube.transform;
    }

    private static Material ConfigureMat(Material m)
    {
        m.hideFlags = HideFlags.HideAndDontSave;
        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_Cull"))     m.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
        if (m.HasProperty("_ZWrite"))   m.SetInt("_ZWrite",   0);
        if (m.HasProperty("_ZTest"))    m.SetInt("_ZTest",    (int)UnityEngine.Rendering.CompareFunction.Always);
        m.renderQueue = 4000;  // overlay: after all opaque + transparent scene geometry
        return m;
    }

    private LineRenderer MakeLine(int positions, bool loop = false)
    {
        var go = new GameObject("GizmoLine");
        go.transform.SetParent(_visualRoot.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace     = true;
        lr.loop              = loop;
        lr.positionCount     = positions;
        lr.sharedMaterial    = _lineMat;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        lr.numCapVertices    = 0;
        lr.numCornerVertices = 0;
        return lr;
    }

    private void UpdateVisuals()
    {
        EnsureVisuals();
        _visualRoot.SetActive(true);

        Vector3 c = _center + Vector3.up * 0.05f;
        float   s = _size;
        float   w = Mathf.Max(s * 0.02f, 0.05f);   // line width scales with the gizmo

        bool move = _mode == Mode.Move, rot = _mode == Mode.Rotate, scale = _mode == Mode.Scale;

        // Only the active tool's handles are visible.
        _xLine.enabled = move; _yLine.enabled = move; _zLine.enabled = move; _padLine.enabled = move;
        _ringXLine.enabled = rot; _ringYLine.enabled = rot; _ringZLine.enabled = rot;
        _stemLine.enabled = scale; _scaleCube.gameObject.SetActive(scale);

        if (move)
        {
            FillArrow(_arrowPts, c, Vector3.right, Vector3.forward, s);
            Apply(_xLine, _arrowPts, w, Col(Handle.MoveX, COL_X));

            FillArrow(_arrowPts, _center, Vector3.up, Vector3.right, s);
            Apply(_yLine, _arrowPts, w, Col(Handle.MoveY, COL_Y));

            FillArrow(_arrowPts, c, Vector3.forward, Vector3.right, s);
            Apply(_zLine, _arrowPts, w, Col(Handle.MoveZ, COL_Z));

            float ps = s * 0.18f;
            _padPts[0] = c + new Vector3(-ps, 0f, -ps);
            _padPts[1] = c + new Vector3( ps, 0f, -ps);
            _padPts[2] = c + new Vector3( ps, 0f,  ps);
            _padPts[3] = c + new Vector3(-ps, 0f,  ps);
            Apply(_padLine, _padPts, w, Col(Handle.MoveFree, COL_PAD));
        }

        if (rot)
        {
            // Y ring keeps the small up-offset to avoid z-fighting with the ground; the two
            // vertical rings sit on the true center.
            FillRing(_ringYPts, c,       Vector3.right,   Vector3.forward, s);
            Apply(_ringYLine, _ringYPts, w, Col(Handle.RotateY, COL_RINGY));
            FillRing(_ringXPts, _center, Vector3.forward, Vector3.up,      s);
            Apply(_ringXLine, _ringXPts, w, Col(Handle.RotateX, COL_RINGX));
            FillRing(_ringZPts, _center, Vector3.right,   Vector3.up,      s);
            Apply(_ringZLine, _ringZPts, w, Col(Handle.RotateZ, COL_RINGZ));
        }

        if (scale)
        {
            Vector3 sh = ScaleHandlePos();
            _stemPts[0] = _center;
            _stemPts[1] = sh;
            Apply(_stemLine, _stemPts, w, Col(Handle.Scale, COL_SCALE));

            _scaleCube.position   = sh;
            _scaleCube.localScale = Vector3.one * (s * 0.16f);
            Color cubeCol = Col(Handle.Scale, COL_SCALE);
            if      (_cubeMat.HasProperty("_Color"))     _cubeMat.SetColor("_Color",     cubeCol);
            else if (_cubeMat.HasProperty("_BaseColor")) _cubeMat.SetColor("_BaseColor", cubeCol);
        }

        FillBoundsBox(_boxPts, _bounds);
        Apply(_boxLine, _boxPts, w * 0.7f, COL_BOX);
    }

    private static void Apply(LineRenderer lr, Vector3[] pts, float width, Color col)
    {
        lr.SetPositions(pts);
        lr.widthMultiplier = width;
        lr.startColor = col;
        lr.endColor   = col;
    }

    private Color Col(Handle h, Color baseCol)
    {
        if (_drag == h) return Color.yellow;
        if (_drag == Handle.None && _hover == h) return Color.Lerp(baseCol, Color.white, 0.6f);
        return baseCol;
    }

    // Circle of radius s around center, spanned by the in-plane basis (u, v).
    private static void FillRing(Vector3[] pts, Vector3 center, Vector3 u, Vector3 v, float s)
    {
        for (int i = 0; i < pts.Length; i++)
        {
            float a = i * 2f * Mathf.PI / pts.Length;
            pts[i] = center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * s;
        }
    }

    // Strip: center→tip, then the two arrowhead barbs (retraces the tip; fine for thin lines)
    private static void FillArrow(Vector3[] pts, Vector3 c, Vector3 dir, Vector3 side, float s)
    {
        Vector3 tip = c + dir * s;
        float hs = s * 0.12f;
        pts[0] = c;
        pts[1] = tip;
        pts[2] = tip - dir * hs + side * hs * 0.6f;
        pts[3] = tip;
        pts[4] = tip - dir * hs - side * hs * 0.6f;
    }

    // Single 16-point strip covering all 12 box edges (4 edges retraced)
    private static void FillBoundsBox(Vector3[] pts, Bounds b)
    {
        Vector3 mn = b.min, mx = b.max;
        Vector3 b0 = new(mn.x, mn.y, mn.z), b1 = new(mx.x, mn.y, mn.z);
        Vector3 b2 = new(mx.x, mn.y, mx.z), b3 = new(mn.x, mn.y, mx.z);
        Vector3 t0 = new(mn.x, mx.y, mn.z), t1 = new(mx.x, mx.y, mn.z);
        Vector3 t2 = new(mx.x, mx.y, mx.z), t3 = new(mn.x, mx.y, mx.z);
        pts[0]  = b0; pts[1]  = b1; pts[2]  = b2; pts[3]  = b3;
        pts[4]  = b0; pts[5]  = t0; pts[6]  = t1; pts[7]  = b1;
        pts[8]  = t1; pts[9]  = t2; pts[10] = b2; pts[11] = t2;
        pts[12] = t3; pts[13] = b3; pts[14] = t3; pts[15] = t0;
    }
}
