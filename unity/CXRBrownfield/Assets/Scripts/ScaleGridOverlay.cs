using UnityEngine;

// A toggleable, true-scale ground grid drawn with immediate-mode GL lines (so it shows in play mode
// and in a VR mirror, unlike Gizmos). Three tiers — 1 m (faint), 5 m (brighter), 10 m (boldest, with
// metre labels at intersections). The grid is centred on the camera and culled to a radius so the
// line count stays bounded regardless of site size; the 1 m tier drops out beyond a closer radius to
// keep it cheap for VR. Purely a viewing aid — it reads nothing from and writes nothing to the
// environment data. Toggle state lives in PlayerPrefs (a UI preference, not site data).
[RequireComponent(typeof(Camera))]
public class ScaleGridOverlay : MonoBehaviour
{
    private const string PrefKey = "cxr.scaleGrid.enabled";

    [SerializeField] private bool  enabledOverlay;
    [SerializeField] private float radius        = 100f;   // half-extent drawn around the camera (m)
    [SerializeField] private float fineRadius    = 30f;    // 1 m lines only drawn within this radius
    [SerializeField] private float groundY       = 0.02f;  // lift above the terrain to avoid z-fight

    private Camera   _cam;
    private Material _lineMat;

    private static readonly Color Fine   = new(1f, 1f, 1f, 0.06f);   // 1 m
    private static readonly Color Mid    = new(1f, 1f, 1f, 0.14f);   // 5 m
    private static readonly Color Major  = new(0.4f, 0.85f, 1f, 0.45f); // 10 m

    public bool OverlayEnabled
    {
        get => enabledOverlay;
        set { enabledOverlay = value; PlayerPrefs.SetInt(PrefKey, value ? 1 : 0); }
    }

    public void Toggle() => OverlayEnabled = !enabledOverlay;

    private void Awake()
    {
        _cam = GetComponent<Camera>();
        enabledOverlay = PlayerPrefs.GetInt(PrefKey, 0) == 1;
    }

    private void EnsureMaterial()
    {
        if (_lineMat != null) return;
        // Built-in unlit, vertex-coloured, alpha-blended material — the standard pattern for GL lines.
        var shader = Shader.Find("Hidden/Internal-Colored");
        _lineMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        _lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        _lineMat.SetInt("_ZWrite", 0);
    }

    private void OnRenderObject()
    {
        if (!enabledOverlay || _cam == null || Camera.current != _cam) return;
        EnsureMaterial();

        // Snap the drawn window to the camera's XZ position so the grid feels infinite while bounded.
        Vector3 c = _cam.transform.position;
        int cx = Mathf.RoundToInt(c.x);
        int cz = Mathf.RoundToInt(c.z);
        int r  = Mathf.CeilToInt(radius);

        _lineMat.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.LINES);

        int x0 = cx - r, x1 = cx + r, z0 = cz - r, z1 = cz + r;
        for (int x = x0; x <= x1; x++)
        {
            // 1 m (fine) lines only near the camera; 5 m / 10 m lines span the whole window.
            if (x % 5 != 0 && Mathf.Abs(x - cx) > fineRadius) continue;
            GL.Color(TierColor(x));
            GL.Vertex3(x, groundY, z0);
            GL.Vertex3(x, groundY, z1);
        }
        for (int z = z0; z <= z1; z++)
        {
            if (z % 5 != 0 && Mathf.Abs(z - cz) > fineRadius) continue;
            GL.Color(TierColor(z));
            GL.Vertex3(x0, groundY, z);
            GL.Vertex3(x1, groundY, z);
        }

        GL.End();
        GL.PopMatrix();
    }

    // Colour by tier: every 10 m is Major, every 5 m is Mid, otherwise Fine (1 m).
    private static Color TierColor(int v)
    {
        if (v % 10 == 0) return Major;
        if (v % 5  == 0) return Mid;
        return Fine;
    }

    private void OnGUI()
    {
        if (!enabledOverlay || _cam == null) return;
        // Label the 10 m intersections within the fine window so the scale is legible without clutter.
        Vector3 c = _cam.transform.position;
        int cx = Mathf.RoundToInt(c.x / 10f) * 10;
        int cz = Mathf.RoundToInt(c.z / 10f) * 10;
        int span = Mathf.Min(Mathf.CeilToInt(fineRadius / 10f) * 10, 60);
        var style = new GUIStyle(GUI.skin.label) { fontSize = 10, normal = { textColor = new Color(0.6f, 0.9f, 1f, 0.9f) } };

        for (int x = cx - span; x <= cx + span; x += 10)
            for (int z = cz - span; z <= cz + span; z += 10)
            {
                Vector3 sp = _cam.WorldToScreenPoint(new Vector3(x, groundY, z));
                if (sp.z <= 0f) continue;
                GUI.Label(new Rect(sp.x + 2f, Screen.height - sp.y - 6f, 70f, 16f), $"{x},{z}", style);
            }
    }

    private void OnDestroy()
    {
        if (_lineMat != null) Destroy(_lineMat);
    }
}
