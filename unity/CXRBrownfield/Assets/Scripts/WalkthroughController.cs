using UnityEngine;
using UnityEngine.InputSystem;

// First-person walkthrough of the loaded scene, from the desktop editor (BasicModel). The Walk
// button on the command bar (UIShell) starts a PLACING phase: a person-sized ghost follows the
// cursor over the ground; left-click drops the walker there, facing the orbit camera's yaw. While
// WALKING the main camera sits 1.7 m above a CharacterController capsule: WASD moves, the mouse
// looks, Shift runs, Esc (or losing focus) returns to the editor view with the orbit pivot moved to
// wherever you stopped. Editing is frozen, not torn down: EditController early-outs on IsEngaged
// and every panel hides, so the selection, mode and an open tile editor all come back untouched.
//
// The math (look integration, WASD velocity, vertical step) is WalkKinematics in the Authoring
// assembly, covered by WalkKinematicsTests. Nothing here touches environment data or EditHistory.
public class WalkthroughController : MonoBehaviour
{
    public enum Phase { Off, Placing, Walking }

    // USER WIRES THIS IN INSPECTOR (optional — each falls back to a scene lookup in Awake):
    [SerializeField] private EditController editController;
    [SerializeField] private WorldRenderer  worldRenderer;
    [SerializeField] private Camera         mainCamera;

    [Header("Walker")]
    [SerializeField] private float walkSpeed      = 1.4f;    // m/s, an unhurried adult walk (5 km/h)
    [SerializeField] private float runSpeed       = 3.0f;    // m/s, a jog (about 11 km/h)
    [SerializeField] private float lookSens       = 0.12f;   // degrees of turn per mouse pixel
    [SerializeField] private float gravity        = -9.81f;  // m/s^2
    [SerializeField] private float stepOffset     = 0.4f;    // m, curbs and single stair risers
    [SerializeField] private float slopeLimit     = 50f;     // degrees
    // Backdrop (non-active) environments normally have every collider off so editor picks ignore
    // them. While walking they are made solid, or you would fall through the twin you came to see.
    [SerializeField] private bool  solidBackdrops = true;

    // ---- static state the rest of the UI reads (same pattern as UIMode / UIShell._active) ----
    public static Phase Current { get; private set; } = Phase.Off;
    public static bool  IsEngaged => Current != Phase.Off;       // panels hide, EditController freezes
    public static bool  IsWalking => Current == Phase.Walking;
    // Frame on which a phase ended (Esc, click, focus loss). Update order between MonoBehaviours is
    // undefined, so EditController also checks this to keep its own Esc handlers from seeing the
    // same keystroke that ended the walk.
    public static int   LastTransitionFrame { get; private set; } = -1;
    public static bool  TransitionedThisFrame => LastTransitionFrame == Time.frameCount;

    private const float GROUND_LIFT      = 0.05f;   // spawn/teleport slightly above the surface
    private const float STICK_VELOCITY   = 2f;      // m/s held downward while grounded (see WalkKinematics)
    private const int   LOOK_SKIP_FRAMES = 2;       // swallow the cursor-warp delta right after locking
    private static readonly Color GhostColor = new(1f, 0.72f, 0.2f, 1f);   // amber, like the other previews

    private GameObject          _ghost;
    private Material            _ghostMat;
    private GameObject          _walker;
    private CharacterController _cc;
    private float _yaw, _pitch, _vy;
    private int   _skipLookFrames;
    private bool  _backdropsMadeSolid;
    private bool  _ghostOnGround;
    private Vector3 _ghostPoint;

    private static Vector3  MousePos => Mouse.current != null ? (Vector3)(Vector2)Mouse.current.position.ReadValue() : Vector3.zero;
    private static bool     LMBDown  => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
    private static Keyboard KB       => Keyboard.current;

    // Statics survive a play-mode entry when domain reload is off; start every run in Off.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { Current = Phase.Off; LastTransitionFrame = -1; }

    private void Awake()
    {
        if (mainCamera     == null) mainCamera     = Camera.main;
        if (editController == null) editController = FindFirstObjectByType<EditController>();
        if (worldRenderer  == null) worldRenderer  = FindFirstObjectByType<WorldRenderer>();
    }

    private void OnDisable() { Abort(); }

    private void OnDestroy()
    {
        Abort();
        if (_ghost    != null) Destroy(_ghost);
        if (_walker   != null) Destroy(_walker);
        if (_ghostMat != null) Destroy(_ghostMat);
    }

    // Unity releases the cursor lock when the app loses focus; end the walk too, so the user never
    // comes back to a hidden cursor and a frozen editor.
    private void OnApplicationFocus(bool focus) { if (!focus && Current == Phase.Walking) EndWalking(); }

    private void Update()
    {
        switch (Current)
        {
            case Phase.Placing: UpdatePlacing(); break;
            case Phase.Walking: UpdateWalking(); break;
        }
    }

    // After every Update (EditController's orbit camera is frozen while walking, so nothing else
    // writes the camera; LateUpdate keeps the view glued to the capsule after the physics move).
    private void LateUpdate() { if (Current == Phase.Walking) ApplyCamera(); }

    // -----------------------------------------------------------------------
    // Phase transitions
    // -----------------------------------------------------------------------

    // Walk button. Shows the ghost under the cursor and waits for a click on the ground.
    public void BeginPlacing()
    {
        if (IsEngaged || mainCamera == null) return;
        GUIUtility.keyboardControl = 0;   // a focused text field must not swallow Esc / WASD
        EnsureGhost();
        _ghost.SetActive(false);
        _ghostOnGround = false;
        Transition(Phase.Placing);
    }

    // Esc during placing: back to the editor with nothing changed.
    public void Cancel()
    {
        if (Current != Phase.Placing) return;
        if (_ghost != null) _ghost.SetActive(false);
        Transition(Phase.Off);
    }

    // Drop the walker with its feet at `feet`, looking along `yawDeg`, and take over the camera.
    public void BeginWalking(Vector3 feet, float yawDeg)
    {
        if (Current == Phase.Walking || mainCamera == null) return;
        if (_ghost != null) _ghost.SetActive(false);

        EnsureWalker();
        _cc.stepOffset = stepOffset;
        _cc.slopeLimit = slopeLimit;
        Teleport(feet + Vector3.up * GROUND_LIFT);
        _walker.SetActive(true);

        _yaw = yawDeg; _pitch = 0f; _vy = 0f;
        _skipLookFrames = LOOK_SKIP_FRAMES;

        if (solidBackdrops && worldRenderer != null)
        {
            worldRenderer.SetBackdropCollidersSolid(true);
            _backdropsMadeSolid = true;
        }

        GUIUtility.keyboardControl = 0;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;

        Transition(Phase.Walking);
        ApplyCamera();   // first frame already looks through the walker's eyes
    }

    // Esc while walking: hand the camera back with the orbit pivot where you stopped, facing the
    // way you were looking (pitch and distance are kept).
    public void EndWalking()
    {
        if (Current != Phase.Walking) return;
        if (_walker != null && editController != null)
        {
            Vector3 feet    = _walker.transform.position;
            float   groundY = worldRenderer != null ? worldRenderer.SampleTerrainSurfaceY(feet.x, feet.z) : feet.y;
            editController.SetOrbitPivot(new Vector3(feet.x, groundY, feet.z), _yaw);
        }
        if (_backdropsMadeSolid)
        {
            if (worldRenderer != null) worldRenderer.SetBackdropCollidersSolid(false);
            _backdropsMadeSolid = false;
        }
        if (_walker != null) _walker.SetActive(false);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;
        Transition(Phase.Off);
    }

    private void Abort()
    {
        if (Current == Phase.Walking) EndWalking();
        else if (Current == Phase.Placing) Cancel();
    }

    private static void Transition(Phase next)
    {
        Current = next;
        LastTransitionFrame = Time.frameCount;
    }

    // -----------------------------------------------------------------------
    // Placing
    // -----------------------------------------------------------------------

    private void UpdatePlacing()
    {
        if (KB != null && KB.escapeKey.wasPressedThisFrame) { Cancel(); return; }
        if (Mouse.current == null || _ghost == null) return;

        Vector3 mouse = MousePos;
        // The command bar still occupies its rect (it shows the hint strip), so a click there is UI.
        _ghostOnGround = !UIShell.BlocksScreenPoint(mouse) && TryGroundPoint(mouse, out _ghostPoint);

        float yaw = editController != null ? editController.CameraYaw : 0f;
        if (_ghostOnGround)
            _ghost.transform.SetPositionAndRotation(_ghostPoint, Quaternion.Euler(0f, yaw, 0f));
        _ghost.SetActive(_ghostOnGround);

        if (_ghostOnGround && LMBDown) BeginWalking(_ghostPoint, yaw);
    }

    // Nearest upward-facing surface under the cursor (terrain, path ribbon, roof, prop top; walls
    // are skipped), else the flat ground plane lifted to the terrain height. Ghost and walker sit on
    // the Ignore Raycast layer, so they can never be their own hit.
    private bool TryGroundPoint(Vector3 mouse, out Vector3 point)
    {
        point = Vector3.zero;
        Ray   ray   = mainCamera.ScreenPointToRay(mouse);
        float best  = float.MaxValue;
        bool  found = false;
        foreach (var h in Physics.RaycastAll(ray, 1000f))
        {
            // Water surfaces (Water layer) are walked through, not on, so the placement ghost lands
            // on the bed under them, not on the surface.
            if (h.collider.gameObject.layer == WorldRenderer.WaterLayer) continue;
            if (h.normal.y < 0.5f || h.distance >= best) continue;
            best = h.distance; point = h.point; found = true;
        }
        if (found) return true;

        var plane = new Plane(Vector3.up, 0f);
        if (!plane.Raycast(ray, out float d)) return false;
        point = ray.GetPoint(d);
        if (worldRenderer != null) point.y = worldRenderer.SampleTerrainSurfaceY(point.x, point.z);
        return true;
    }

    // -----------------------------------------------------------------------
    // Walking
    // -----------------------------------------------------------------------

    private void UpdateWalking()
    {
        if (KB != null && KB.escapeKey.wasPressedThisFrame) { EndWalking(); return; }
        if (_cc == null || _walker == null) { EndWalking(); return; }

        float dt = Time.deltaTime;

        if (Mouse.current != null)
        {
            // Clicking back into the view re-locks the cursor if the OS or editor released it.
            if (LMBDown && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
                _skipLookFrames  = LOOK_SKIP_FRAMES;
            }
            Vector2 look = Mouse.current.delta.ReadValue();   // pixels this frame, not per second
            if (_skipLookFrames > 0) _skipLookFrames--;
            else WalkKinematics.IntegrateLook(ref _yaw, ref _pitch, look, lookSens);
        }

        float fwd = 0f, right = 0f; bool run = false;
        if (KB != null)
        {
            if (KB.wKey.isPressed) fwd   += 1f;
            if (KB.sKey.isPressed) fwd   -= 1f;
            if (KB.dKey.isPressed) right += 1f;
            if (KB.aKey.isPressed) right -= 1f;
            run = KB.leftShiftKey.isPressed || KB.rightShiftKey.isPressed;
        }

        Vector3 planar = WalkKinematics.PlanarVelocity(fwd, right, _yaw, run, walkSpeed, runSpeed);
        _vy = WalkKinematics.StepVertical(_vy, _cc.isGrounded, gravity, STICK_VELOCITY, dt);
        _cc.Move((planar + Vector3.up * _vy) * dt);

        // Safety net: slipped through a gap between colliders, back onto the terrain. Gated on
        // being airborne — a grounded walker is on a real surface, and teleporting it up to the
        // heightmap sample would bounce the view wherever sample and collider disagree.
        if (worldRenderer != null)
        {
            Vector3 p        = _walker.transform.position;
            float   terrainY = worldRenderer.SampleTerrainSurfaceY(p.x, p.z);
            if (WalkKinematics.ShouldRescue(_cc.isGrounded, p.y, terrainY))
            {
                Teleport(new Vector3(p.x, terrainY + GROUND_LIFT, p.z));
                _vy = 0f;
            }
        }
    }

    private void ApplyCamera()
    {
        if (mainCamera == null || _walker == null) return;
        Vector3 feet = _walker.transform.position;
        mainCamera.transform.SetPositionAndRotation(
            new Vector3(feet.x, WalkKinematics.EyeY(feet.y), feet.z),
            Quaternion.Euler(_pitch, _yaw, 0f));
    }

    // -----------------------------------------------------------------------
    // Runtime objects
    // -----------------------------------------------------------------------

    private void EnsureWalker()
    {
        if (_walker != null) return;
        _walker = new GameObject("Walker") { layer = 2 };   // Ignore Raycast: editor picks never hit it
        _cc = _walker.AddComponent<CharacterController>();
        _cc.height          = WalkKinematics.CapsuleHeight;
        _cc.radius          = WalkKinematics.CapsuleRadius;
        _cc.center          = new Vector3(0f, WalkKinematics.CapsuleHeight * 0.5f, 0f);   // transform = feet
        _cc.skinWidth       = 0.05f;
        _cc.minMoveDistance = 0f;
    }

    // CharacterController ignores transform writes while enabled; toggle it around a teleport.
    private void Teleport(Vector3 feet)
    {
        _cc.enabled = false;
        _walker.transform.position = feet;
        _cc.enabled = true;
    }

    // Person-sized amber capsule with a short nose block so the facing reads. No colliders (they
    // would block the ground raycast), no shadows, Ignore Raycast layer.
    private void EnsureGhost()
    {
        if (_ghost != null) return;
        if (_ghostMat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
            _ghostMat = new Material(sh) { color = GhostColor };
        }
        _ghost = new GameObject("WalkGhost") { layer = 2 };

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);   // 2 m tall at scale 1
        body.transform.SetParent(_ghost.transform, false);
        body.transform.localScale    = new Vector3(0.6f, WalkKinematics.CapsuleHeight * 0.5f, 0.6f);
        body.transform.localPosition = new Vector3(0f, WalkKinematics.CapsuleHeight * 0.5f, 0f);

        var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
        nose.transform.SetParent(_ghost.transform, false);
        nose.transform.localScale    = new Vector3(0.16f, 0.16f, 0.3f);
        nose.transform.localPosition = new Vector3(0f, WalkKinematics.EyeHeight, 0.38f);

        foreach (var col in _ghost.GetComponentsInChildren<Collider>(true)) Destroy(col);
        foreach (var r in _ghost.GetComponentsInChildren<Renderer>(true))
        {
            r.sharedMaterial    = _ghostMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows    = false;
            r.gameObject.layer  = 2;
        }
    }
}
