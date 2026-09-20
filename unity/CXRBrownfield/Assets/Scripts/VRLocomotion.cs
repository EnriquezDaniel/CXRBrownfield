using UnityEngine;
using UnityEngine.InputSystem;

// Moving through the scene in the headset viewer (VRViewer). Sits on the XR Origin:
//   left stick   walk, relative to where you look (click the stick to jog)
//   right stick  sideways = snap turn 45 degrees about your head
//                forward  = aim a teleport ray from the right hand, let go to jump
// Teleport lands on any surface that faces up (ground, paths, floors, roofs). A CharacterController
// under the head keeps walls solid and follows slopes and steps, like the desktop Walk mode.
//
// The rig transform is the playspace floor centre. The head moves inside it, so every move is
// computed about the head (VRLocomotionMath), never about the rig. Stick walking reuses
// WalkKinematics. Input is read from inline actions bound by usage, so no action asset is needed
// and any OpenXR controller profile that reports a thumbstick works.
[RequireComponent(typeof(CharacterController))]
public class VRLocomotion : MonoBehaviour
{
    // USER WIRES THIS IN INSPECTOR (optional, each falls back to a scene lookup in Awake):
    [SerializeField] private WorldRenderer worldRenderer;
    [SerializeField] private Transform     head;          // the XR camera

    [Header("Walking")]
    [SerializeField] private float walkSpeed   = 1.4f;    // m/s, same as the desktop walkthrough
    [SerializeField] private float runSpeed    = 3.0f;
    [SerializeField] private float gravity     = -9.81f;
    [SerializeField] private float stepOffset  = 0.4f;
    [SerializeField] private float slopeLimit  = 50f;
    [SerializeField] private float stickDeadzone = 0.15f;

    [Header("Teleport")]
    [SerializeField] private float teleportRange = 30f;   // metres

    private const float GROUND_LIFT    = 0.05f;
    private const float STICK_VELOCITY = 2f;
    private const float MIN_BODY       = 0.5f;            // capsule height floor when crouched or untracked
    private const int   IGNORE_RAYCAST = 2;
    private static readonly Color ValidColor   = new(0.2f, 0.9f, 0.4f, 1f);
    private static readonly Color InvalidColor = new(0.95f, 0.25f, 0.2f, 1f);

    private CharacterController _cc;
    private InputAction _move, _turn, _run, _rightPos, _rightRot, _leftPos, _leftRot;
    private Transform    _space;                          // parent of the head: tracked poses are local to it
    private Transform    _leftHand, _rightHand;
    private LineRenderer _ray;
    private Material     _mat;
    private bool  _placed, _snapArmed = true, _aiming, _aimValid;
    private Vector3 _aimPoint;
    private float _vy;

    public bool IsPlaced => _placed;

    private void Awake()
    {
        if (worldRenderer == null) worldRenderer = FindFirstObjectByType<WorldRenderer>();
        if (head == null)
        {
            var cam = GetComponentInChildren<Camera>();
            if (cam != null) head = cam.transform;
        }
        _space = head != null && head.parent != null ? head.parent : transform;

        gameObject.layer = IGNORE_RAYCAST;                // the teleport ray must never hit our own capsule
        _cc = GetComponent<CharacterController>();
        _cc.radius          = WalkKinematics.CapsuleRadius;
        _cc.skinWidth       = 0.05f;
        _cc.minMoveDistance = 0f;
        _cc.stepOffset      = stepOffset;
        _cc.slopeLimit      = slopeLimit;

        _move     = new InputAction("Move",     InputActionType.Value,       "<XRController>{LeftHand}/{Primary2DAxis}");
        _turn     = new InputAction("Turn",     InputActionType.Value,       "<XRController>{RightHand}/{Primary2DAxis}");
        _run      = new InputAction("Run",      InputActionType.Button,      "<XRController>{LeftHand}/{Primary2DAxisClick}");
        _rightPos = new InputAction("RightPos", InputActionType.PassThrough, "<XRController>{RightHand}/pointerPosition");
        _rightRot = new InputAction("RightRot", InputActionType.PassThrough, "<XRController>{RightHand}/pointerRotation");
        _leftPos  = new InputAction("LeftPos",  InputActionType.PassThrough, "<XRController>{LeftHand}/pointerPosition");
        _leftRot  = new InputAction("LeftRot",  InputActionType.PassThrough, "<XRController>{LeftHand}/pointerRotation");

        BuildVisuals();
    }

    private void OnEnable()
    {
        foreach (var a in Actions()) a.Enable();
    }

    private void OnDisable()
    {
        foreach (var a in Actions()) a.Disable();
        SetAiming(false);
    }

    private void OnDestroy()
    {
        foreach (var a in Actions()) a.Dispose();
        if (_mat != null) Destroy(_mat);
    }

    private InputAction[] Actions() => new[] { _move, _turn, _run, _rightPos, _rightRot, _leftPos, _leftRot };

    // Stand the viewer at the north-east corner of the env's first Site plot (its ground rect when
    // it has none), looking south. SyncClient calls this the first time the active environment
    // renders; later syncs leave you where you walked to.
    public void PlaceAtSpawn(EnvironmentDef env)
    {
        VRLocomotionMath.SpawnPose(env, out Vector3 feet, out float yaw);
        feet.y = (worldRenderer != null ? worldRenderer.SampleTerrainSurfaceY(feet.x, feet.z) : 0f) + GROUND_LIFT;

        float headYaw = head != null ? head.eulerAngles.y : transform.eulerAngles.y;
        float rigYaw  = VRLocomotionMath.RigYawForHeadYaw(transform.eulerAngles.y, headYaw, yaw);

        _cc.enabled = false;                              // a CharacterController ignores transform writes while enabled
        transform.rotation = Quaternion.Euler(0f, rigYaw, 0f);
        Vector3 headPos = head != null ? head.position : transform.position;
        transform.position = VRLocomotionMath.TeleportRigPosition(feet, transform.position, headPos);
        _cc.enabled = true;

        _vy = 0f;
        _placed = true;
        Debug.Log($"[VRLocomotion] Placed at {transform.position:F1}, facing {yaw:F0} degrees");
    }

    private void Update()
    {
        UpdateHands();
        if (!_placed || head == null) return;             // nothing to stand on until an environment renders

        FitCapsuleToHead();

        Vector2 right = _turn.ReadValue<Vector2>();
        UpdateTeleport(right);
        if (!_aiming && Mathf.Abs(right.x) > Mathf.Abs(right.y))
        {
            float deg = VRLocomotionMath.SnapTurn(right.x, ref _snapArmed);
            if (deg != 0f) Turn(deg);
        }
        else
        {
            // Keep the latch honest while aiming or idle so a turn cannot fire the moment you let go.
            VRLocomotionMath.SnapTurn(0f, ref _snapArmed);
        }

        Walk(Time.deltaTime);
    }

    // -----------------------------------------------------------------------
    // Walking
    // -----------------------------------------------------------------------

    private void Walk(float dt)
    {
        Vector2 stick = _move.ReadValue<Vector2>();
        if (stick.magnitude < stickDeadzone) stick = Vector2.zero;

        Vector3 planar = WalkKinematics.PlanarVelocity(stick.y, stick.x, head.eulerAngles.y,
                                                       _run.IsPressed(), walkSpeed, runSpeed);
        _vy = WalkKinematics.StepVertical(_vy, _cc.isGrounded, gravity, STICK_VELOCITY, dt);
        _cc.Move((planar + Vector3.up * _vy) * dt);

        // Same safety net as the desktop walkthrough: only an airborne body that dropped under
        // the terrain fell through a gap and goes back on the ground.
        if (worldRenderer == null) return;
        Vector3 feet     = FeetWorld();
        float   terrainY = worldRenderer.SampleTerrainSurfaceY(feet.x, feet.z);
        if (WalkKinematics.ShouldRescue(_cc.isGrounded, feet.y, terrainY))
        {
            MoveRig(new Vector3(transform.position.x, terrainY + GROUND_LIFT, transform.position.z));
            _vy = 0f;
        }
    }

    // The capsule stands under the head, wherever the user is in their room, and is as tall as they
    // are right now, so ducking under something works.
    private void FitCapsuleToHead()
    {
        Vector3 local  = transform.InverseTransformPoint(head.position);
        float   height = Mathf.Max(MIN_BODY, Mathf.Max(local.y, _cc.radius * 2f));
        _cc.height = height;
        _cc.center = new Vector3(local.x, height * 0.5f, local.z);
    }

    private Vector3 FeetWorld() => transform.TransformPoint(new Vector3(_cc.center.x, 0f, _cc.center.z));

    private void Turn(float deg)
    {
        Vector3 pos = VRLocomotionMath.RotateRigAboutHead(transform.position, head.position, deg);
        _cc.enabled = false;
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, transform.eulerAngles.y + deg, 0f));
        _cc.enabled = true;
    }

    private void MoveRig(Vector3 pos)
    {
        _cc.enabled = false;
        transform.position = pos;
        _cc.enabled = true;
    }

    // -----------------------------------------------------------------------
    // Teleport
    // -----------------------------------------------------------------------

    private void UpdateTeleport(Vector2 right)
    {
        if (!_aiming)
        {
            if (right.y > VRLocomotionMath.TeleportAimAt && right.y > Mathf.Abs(right.x)) SetAiming(true);
            else return;
        }

        if (right.y < VRLocomotionMath.TeleportReleaseBelow)
        {
            if (_aimValid)
            {
                Vector3 target = _aimPoint + Vector3.up * GROUND_LIFT;
                MoveRig(VRLocomotionMath.TeleportRigPosition(target, transform.position, head.position));
                _vy = 0f;
            }
            SetAiming(false);
            return;
        }

        // Aim from the right hand; with no tracked controller, from the head.
        Transform src = _rightHand.gameObject.activeSelf ? _rightHand : head;
        var ray = new Ray(src.position, src.forward);
        Vector3 end = ray.GetPoint(teleportRange);
        _aimValid = false;

        float best = float.MaxValue;
        foreach (var h in Physics.RaycastAll(ray, teleportRange))
        {
            // Water surfaces are walked through, not on (same rule as the desktop Walk ghost).
            if (h.collider.gameObject.layer == WorldRenderer.WaterLayer) continue;
            if (h.distance >= best) continue;
            best = h.distance;
            end = h.point;
            _aimPoint = h.point;
            _aimValid = VRLocomotionMath.IsTeleportSurface(h.normal);
        }

        _ray.SetPosition(0, src.position);
        _ray.SetPosition(1, end);
        Color c = _aimValid ? ValidColor : InvalidColor;
        _ray.startColor = c;
        _ray.endColor   = c;
    }

    private void SetAiming(bool on)
    {
        _aiming = on;
        if (!on) _aimValid = false;
        if (_ray != null) _ray.enabled = on;
    }

    // -----------------------------------------------------------------------
    // Hands and ray
    // -----------------------------------------------------------------------

    private void UpdateHands()
    {
        PoseHand(_leftHand,  _leftPos,  _leftRot);
        PoseHand(_rightHand, _rightPos, _rightRot);
    }

    // An untracked controller reports a zero pose; hide its stand-in instead of parking it on the floor.
    private static void PoseHand(Transform hand, InputAction pos, InputAction rot)
    {
        Vector3    p = pos.ReadValue<Vector3>();
        Quaternion r = rot.ReadValue<Quaternion>();
        bool tracked = p != Vector3.zero && (r.x != 0f || r.y != 0f || r.z != 0f || r.w != 0f);
        if (hand.gameObject.activeSelf != tracked) hand.gameObject.SetActive(tracked);
        if (tracked) hand.SetLocalPositionAndRotation(p, r);
    }

    private void BuildVisuals()
    {
        var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit")
                 ?? Shader.Find("Sprites/Default");
        _mat = new Material(sh) { color = Color.white };

        _leftHand  = BuildHand("Left Hand");
        _rightHand = BuildHand("Right Hand");

        var rayGO = new GameObject("Teleport Ray") { layer = IGNORE_RAYCAST };
        rayGO.transform.SetParent(transform, false);
        _ray = rayGO.AddComponent<LineRenderer>();
        _ray.useWorldSpace     = true;
        _ray.positionCount     = 2;
        _ray.startWidth        = 0.01f;
        _ray.endWidth          = 0.01f;
        _ray.sharedMaterial    = _mat;
        _ray.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _ray.receiveShadows    = false;
        _ray.enabled           = false;
    }

    // Small block along the pointing direction. No collider, no shadows.
    private Transform BuildHand(string name)
    {
        var root = new GameObject(name) { layer = IGNORE_RAYCAST };
        root.transform.SetParent(_space, false);

        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.layer = IGNORE_RAYCAST;
        block.transform.SetParent(root.transform, false);
        block.transform.localScale    = new Vector3(0.04f, 0.04f, 0.12f);
        block.transform.localPosition = new Vector3(0f, 0f, -0.04f);
        Destroy(block.GetComponent<Collider>());
        var r = block.GetComponent<Renderer>();
        r.sharedMaterial    = _mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows    = false;

        root.SetActive(false);
        return root.transform;
    }
}
