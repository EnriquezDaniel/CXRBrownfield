using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Runtime asset-preview thumbnails — the redesign's "big fix": every prefab and material shows as a
// recognizable image instead of a name. Each preview is rendered once to a RenderTexture, copied to
// a cached Texture2D, and reused for the rest of the session.
//
// Usage (inside OnGUI):
//   var tex = ThumbnailCache.GetPrefab(prefabGameObject);   // null until ready, then cached
//   var tex = ThumbnailCache.GetMaterial(material);
//   UITheme.Thumb(tex, label, selected);
//
// IMPORTANT: rendering a camera (SubmitRenderRequest / Camera.Render) swaps the active render
// target, which corrupts IMGUI if done during OnGUI's repaint (the whole UI disappears). So requests
// made from OnGUI are *queued* and rendered from a hidden runner's Update(); the first frame returns
// null (the tile shows its empty state) and the image appears once it's ready.
public static class ThumbnailCache
{
    const int Size  = 128;                 // square preview resolution
    const int Layer = 31;                  // hidden render layer for preview instances

    static readonly Dictionary<int, Texture2D> _prefabs   = new();
    static readonly Dictionary<int, Texture2D> _materials = new();

    struct Job { public bool isMat; public int key; public GameObject prefab; public Material mat; }
    static readonly Queue<Job>  _jobs   = new();
    static readonly HashSet<int> _queued = new();

    static Camera _cam;
    static Light  _light;
    static GameObject _rig;
    static Mesh _sphere;

    static void EnsureRig()
    {
        if (_cam != null) return;

        _rig = new GameObject("~ThumbnailRig") { hideFlags = HideFlags.HideAndDontSave };
        Object.DontDestroyOnLoad(_rig);
        _rig.AddComponent<ThumbnailRunner>();

        var camGO = new GameObject("cam") { hideFlags = HideFlags.HideAndDontSave };
        camGO.transform.SetParent(_rig.transform);
        _cam = camGO.AddComponent<Camera>();
        _cam.enabled = false;                         // driven manually via SubmitRenderRequest
        _cam.clearFlags = CameraClearFlags.SolidColor;
        // Opaque light tile background — a transparent clear can't be used: URP writes alpha 0 for
        // opaque geometry, which would make the whole preview invisible in IMGUI.
        _cam.backgroundColor = new Color(0.957f, 0.949f, 0.929f, 1f);  // ~ UITheme.Tile
        _cam.cullingMask = 1 << Layer;
        _cam.fieldOfView = 30f;
        _cam.nearClipPlane = 0.01f;
        _cam.farClipPlane = 5000f;

        var lightGO = new GameObject("light") { hideFlags = HideFlags.HideAndDontSave };
        lightGO.transform.SetParent(_rig.transform);
        _light = lightGO.AddComponent<Light>();
        _light.type = LightType.Directional;
        _light.cullingMask = 1 << Layer;
        _light.intensity = 1.4f;
        _light.transform.rotation = Quaternion.Euler(40f, -35f, 0f);

        // Fill from roughly opposite the key so the shadowed side of the probe stays readable —
        // the rig gets no scene ambient (everything is culled to the hidden layer).
        var fillGO = new GameObject("fill") { hideFlags = HideFlags.HideAndDontSave };
        fillGO.transform.SetParent(_rig.transform);
        var fill = fillGO.AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.cullingMask = 1 << Layer;
        fill.intensity = 0.55f;
        fill.color = new Color(0.95f, 0.97f, 1f);
        fill.transform.rotation = Quaternion.Euler(15f, 150f, 0f);
    }

    public static Texture2D GetPrefab(GameObject prefab)
    {
        if (prefab == null) return null;
        int key = prefab.GetInstanceID();
        if (_prefabs.TryGetValue(key, out var tex) && tex != null) return tex;
        EnsureRig();
        if (_queued.Add(key)) _jobs.Enqueue(new Job { isMat = false, key = key, prefab = prefab });
        return null;
    }

    public static Texture2D GetMaterial(Material mat)
    {
        if (mat == null) return null;
        int key = mat.GetInstanceID();
        if (_materials.TryGetValue(key, out var tex) && tex != null) return tex;
        EnsureRig();
        if (_queued.Add(key)) _jobs.Enqueue(new Job { isMat = true, key = key, mat = mat });
        return null;
    }

    // Called from ThumbnailRunner.Update() — safe to render cameras here (not during OnGUI).
    internal static void Pump(int maxPerFrame)
    {
        int n = 0;
        while (_jobs.Count > 0 && n < maxPerFrame)
        {
            var j = _jobs.Dequeue();
            n++;
            if (j.isMat) _materials[j.key] = RenderMaterial(j.mat);
            else         _prefabs[j.key]   = RenderPrefab(j.prefab);
            _queued.Remove(j.key);
        }
    }

    static Texture2D RenderPrefab(GameObject prefab)
    {
        if (prefab == null) return null;
        var inst = Object.Instantiate(prefab);
        inst.hideFlags = HideFlags.HideAndDontSave;
        SetLayerRecursive(inst, Layer);
        inst.transform.SetParent(_rig.transform);
        inst.transform.localPosition = Vector3.zero;
        // Preserve the prefab's authored root rotation — the renderer composes instance rotation
        // under it (see EditController.BaseRotationFor), so a preview forced to identity would show
        // a different orientation than the object actually gets placed at.
        inst.transform.localRotation = prefab.transform.localRotation;
        var tex = RenderObject(inst);
        Object.DestroyImmediate(inst);
        return tex;
    }

    static Texture2D RenderMaterial(Material mat)
    {
        if (mat == null) return null;
        if (_sphere == null)
        {
            var probe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphere = probe.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(probe);
        }
        var go = new GameObject("matprobe") { hideFlags = HideFlags.HideAndDontSave };
        SetLayerRecursive(go, Layer);
        go.transform.SetParent(_rig.transform);
        go.transform.localPosition = Vector3.zero;
        var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = _sphere;
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
        var tex = RenderObject(go);
        Object.DestroyImmediate(go);
        return tex;
    }

    // Frames the object's renderer bounds, renders the hidden camera once, and copies the result
    // into a cached Texture2D. Must be called from Update(), never from OnGUI.
    static Texture2D RenderObject(GameObject inst)
    {
        var bounds = ComputeBounds(inst);
        float radius = Mathf.Max(0.001f, bounds.extents.magnitude);
        float dist   = radius / Mathf.Sin(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;

        // 3/4 view from above-front (positive Y keeps the camera looking DOWN at the object,
        // matching how it's seen in the scene).
        var dir = new Vector3(0.6f, 0.45f, -1f).normalized;
        _cam.transform.position = bounds.center + dir * dist;
        _cam.transform.LookAt(bounds.center);

        var rt = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32);
        var prevActive = RenderTexture.active;

        // Cross-pipeline synchronous render (Unity 2022.2+/URP 14+); fall back to the built-in path.
        var req = new RenderPipeline.StandardRequest { destination = rt };
        if (RenderPipeline.SupportsRenderRequest(_cam, req))
            RenderPipeline.SubmitRenderRequest(_cam, req);
        else { _cam.targetTexture = rt; _cam.Render(); _cam.targetTexture = null; }

        RenderTexture.active = rt;
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
        // Force opaque so IMGUI's DrawTexture shows every pixel (URP may leave opaque alpha at 0).
        var cols = tex.GetPixels();
        for (int i = 0; i < cols.Length; i++) cols[i].a = 1f;
        tex.SetPixels(cols);
        tex.Apply();

        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);
        return tex;
    }

    static Bounds ComputeBounds(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>();
        if (rends == null || rends.Length == 0) return new Bounds(go.transform.position, Vector3.one);
        var b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        return b;
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform t in go.transform) SetLayerRecursive(t.gameObject, layer);
    }

    // Drop a single asset's cached preview (e.g. after a material edit) so it re-renders next time.
    public static void Invalidate(Object asset)
    {
        if (asset == null) return;
        int key = asset.GetInstanceID();
        _prefabs.Remove(key);
        _materials.Remove(key);
    }
}

// Hidden pump that renders queued thumbnails outside the IMGUI repaint.
public class ThumbnailRunner : MonoBehaviour
{
    void Update() => ThumbnailCache.Pump(3);
}
