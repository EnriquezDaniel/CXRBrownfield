using System.Collections.Generic;
using UnityEngine;

// M5: Mesh-combines all tile/building meshes grouped by material for VR-lightweight rendering.
// Authoring representation is hidden (not destroyed) so UnbakeAll() can restore it.
public class BakePass : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private WorldRenderer worldRenderer; // USER WIRES THIS IN INSPECTOR

    [Header("Settings")]
    [SerializeField] private bool logPerfBudget = true;

    private readonly List<GameObject> _bakedRoots = new();

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public void BakeAll()
    {
        foreach (var go in _bakedRoots)
            if (go != null) Destroy(go);
        _bakedRoots.Clear();

        Transform root = worldRenderer?.GetRoot();
        if (root == null) { Debug.LogWarning("[BakePass] WorldRenderer root not available."); return; }

        int totalDrawCalls = 0, totalVerts = 0;

        foreach (Transform building in root)
        {
            var renderers = new List<MeshRenderer>(building.GetComponentsInChildren<MeshRenderer>());
            if (renderers.Count == 0) continue;

            var groups = new Dictionary<Material, List<CombineInstance>>();
            foreach (var rend in renderers)
            {
                if (!rend.enabled) continue;
                var mf = rend.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                int subCount = Mathf.Min(rend.sharedMaterials.Length, mf.sharedMesh.subMeshCount);
                for (int si = 0; si < subCount; si++)
                {
                    var mat = rend.sharedMaterials[si];
                    if (mat == null) continue;
                    if (!groups.ContainsKey(mat)) groups[mat] = new List<CombineInstance>();
                    groups[mat].Add(new CombineInstance
                    {
                        mesh         = mf.sharedMesh,
                        subMeshIndex = si,
                        transform    = rend.transform.localToWorldMatrix,
                    });
                }
            }

            if (groups.Count == 0) continue;

            var bakedRoot = new GameObject($"{building.name}_Baked");
            bakedRoot.transform.SetParent(root, true);
            _bakedRoots.Add(bakedRoot);

            foreach (var kv in groups)
            {
                var mesh = new Mesh { name = $"{building.name}_{kv.Key.name}_combined" };
                mesh.CombineMeshes(kv.Value.ToArray(), true, true);
                mesh.RecalculateNormals();
                mesh.Optimize();

                var child = new GameObject($"Mat_{kv.Key.name}");
                child.transform.SetParent(bakedRoot.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                child.AddComponent<MeshRenderer>().sharedMaterial = kv.Key;

                totalDrawCalls++;
                totalVerts += mesh.vertexCount;
            }

            building.gameObject.SetActive(false);
        }

        if (logPerfBudget)
            Debug.Log($"[BakePass] Bake complete — draw calls: {totalDrawCalls}, verts: {totalVerts:N0}");
    }

    public void UnbakeAll()
    {
        foreach (var go in _bakedRoots)
            if (go != null) Destroy(go);
        _bakedRoots.Clear();

        Transform root = worldRenderer?.GetRoot();
        if (root == null) return;
        foreach (Transform child in root)
            child.gameObject.SetActive(true);
    }
}
