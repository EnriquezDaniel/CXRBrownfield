using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// One-click generator for PER-FACE PAINTABLE tile prefabs.
//
// The tile editor's Paint tool assigns a material to one named face by writing that face's material
// SLOT on the tile renderer (TileSpawner.ApplyFaceMaterial), and FaceFromHit resolves the clicked face
// from the MeshCollider SUBMESH. That needs one submesh + one material slot per faceNames entry. The
// hand-authored tile prefabs (built-in Cube, ProBuilder prism, imported FBX) all have ONE submesh, so
// only faceNames[0] could ever render. This tool rebuilds each TileShapePalette entry's prefab into
// Assets/Prefabs/Tiles/PerFace/<shapeId>.prefab — a single GameObject with a MeshFilter/MeshRenderer/
// MeshCollider around a mesh split by TileFaceSplitter — and re-points the entry (prefab + faceNames)
// in place. The original prefabs are never modified; re-running regenerates from them (the source
// path is remembered in the generated prefab's importer userData), so edit the originals and re-run.
//
// TileShapePalette is a GUARDED asset: this tool snapshots it first, edits ONLY `prefab` and
// `faceNames` of each existing entry (never the entries array, shapeId, or defaultRotation), saves
// through AssetDatabase, and runs the validator afterwards — see CLAUDE.md.
public static class TileFaceSetup
{
    public const string OutputFolder = "Assets/Prefabs/Tiles/PerFace";
    public const string PalettePath  = "Assets/Resources/TileShapePalette.asset";
    private const string SourceKey   = "cxr.perface.source=";

    [MenuItem("Tools/CXR/Tiles/Rebuild per-face tile prefabs", priority = 60)]
    public static void Rebuild()
    {
        var palette = PaletteGuard.Load(PalettePath) as TileShapePalette;
        if (palette == null) { Debug.LogError($"[TileFaceSetup] {PalettePath} not found or not a TileShapePalette."); return; }

        PaletteSnapshots.Capture(PalettePath);
        EnsureFolder(OutputFolder);

        var so      = new SerializedObject(palette);
        var entries = so.FindProperty("entries");
        var report  = new StringBuilder("[TileFaceSetup] per-face tile prefabs:\n");
        int done = 0;

        for (int i = 0; i < entries.arraySize; i++)
        {
            var entry   = entries.GetArrayElementAtIndex(i);
            string shapeId = entry.FindPropertyRelative("shapeId").stringValue;
            var prefab  = entry.FindPropertyRelative("prefab").objectReferenceValue as GameObject;
            var defRot  = Quaternion.Euler(entry.FindPropertyRelative("defaultRotation").vector3Value);
            if (string.IsNullOrWhiteSpace(shapeId) || prefab == null)
            {
                Debug.LogError($"[TileFaceSetup] entry [{i}] '{shapeId}': blank shapeId or null prefab — skipped.", palette);
                continue;
            }

            string sourcePath = ResolveSource(AssetDatabase.GetAssetPath(prefab));
            try
            {
                if (!GatherGeometry(sourcePath, out var verts, out var tris, out var normals, out var uvs, out var seedMat))
                {
                    Debug.LogError($"[TileFaceSetup] '{shapeId}': no renderable mesh found in {sourcePath} — skipped.");
                    continue;
                }

                var split = TileFaceSplitter.Split(verts, tris, normals, uvs, Matrix4x4.identity, defRot,
                                                   meshName: $"{shapeId}_faces");
                string safe = Sanitize(shapeId);
                var mesh    = SaveMesh(split.mesh, $"{OutputFolder}/{safe}_faces.asset");
                string prefabPath = $"{OutputFolder}/{safe}.prefab";
                var newPrefab = SavePrefab(shapeId, mesh, seedMat, split.faceNames.Count, prefabPath, sourcePath);
                if (newPrefab == null) continue;

                // Guarded palette edit: only this entry's prefab + faceNames, in place.
                string before = $"prefab={AssetDatabase.GetAssetPath(prefab)} faces=[{string.Join(",", ReadNames(entry))}]";
                entry.FindPropertyRelative("prefab").objectReferenceValue = newPrefab;
                var names = entry.FindPropertyRelative("faceNames");
                names.arraySize = split.faceNames.Count;
                for (int f = 0; f < split.faceNames.Count; f++)
                    names.GetArrayElementAtIndex(f).stringValue = split.faceNames[f];
                string after = $"prefab={prefabPath} faces=[{string.Join(",", split.faceNames)}]";

                report.AppendLine($"  [{i}] {shapeId}  (source {sourcePath})");
                report.AppendLine($"       before: {before}");
                report.AppendLine($"       after:  {after}");
                foreach (var face in split.faces)
                    report.AppendLine($"       - {face.name,-10} tris={face.triangleCount,3}  n={face.normal}  span={face.spanDeg:F1}°");
                done++;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TileFaceSetup] '{shapeId}' failed: {ex}");
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssetIfDirty(palette);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(report.Append($"  {done}/{entries.arraySize} entries rebuilt.").ToString(), palette);
        PaletteValidator.Validate();
    }

    // A generated prefab remembers the prefab it was built from, so re-runs regenerate from the
    // hand-authored source instead of re-splitting the already-split output.
    private static string ResolveSource(string prefabPath)
    {
        if (!prefabPath.StartsWith(OutputFolder + "/", StringComparison.OrdinalIgnoreCase)) return prefabPath;
        var importer = AssetImporter.GetAtPath(prefabPath);
        string data  = importer != null ? importer.userData : null;
        if (!string.IsNullOrEmpty(data) && data.StartsWith(SourceKey, StringComparison.Ordinal))
        {
            string src = data.Substring(SourceKey.Length);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(src) != null) return src;
            Debug.LogWarning($"[TileFaceSetup] source '{src}' of {prefabPath} no longer exists; re-splitting the generated prefab itself.");
        }
        return prefabPath;
    }

    // Reads every MeshFilter / SkinnedMeshRenderer under the prefab root into ONE root-local triangle
    // soup. ProBuilder meshes are null on disk (rebuilt at load), so their mesh is generated on the
    // isolated prefab contents first — via reflection, so this file has no hard ProBuilder dependency.
    private static bool GatherGeometry(string prefabPath, out Vector3[] verts, out int[] tris,
                                       out Vector3[] normals, out Vector2[] uvs, out Material seedMat)
    {
        verts = null; tris = null; normals = null; uvs = null; seedMat = null;
        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            var vList = new List<Vector3>(); var tList = new List<int>();
            var nList = new List<Vector3>(); var uList = new List<Vector2>();
            bool anyNormals = true, anyUvs = true;
            Material seed = null;   // local stand-in: an `out` parameter can't be captured by Add()
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            var temps = new List<Mesh>();

            void Add(Mesh m, Transform t, Renderer r)
            {
                if (m == null) return;
                Matrix4x4 mtx = toRoot * t.localToWorldMatrix;
                Matrix4x4 nmx = mtx.inverse.transpose;
                Vector3[] mv; int[] mt; Vector3[] mn = null; Vector2[] mu = null;
                mv = m.vertices; mt = m.triangles;
                try { mn = m.normals; } catch { }
                try { mu = m.uv;      } catch { }
                int baseIndex = vList.Count;
                for (int i = 0; i < mv.Length; i++) vList.Add(mtx.MultiplyPoint3x4(mv[i]));
                foreach (int idx in mt) tList.Add(baseIndex + idx);
                if (mn != null && mn.Length == mv.Length) foreach (var n in mn) nList.Add(nmx.MultiplyVector(n).normalized);
                else anyNormals = false;
                if (mu != null && mu.Length == mv.Length) uList.AddRange(mu); else anyUvs = false;
                if (seed == null && r != null && r.sharedMaterial != null) seed = r.sharedMaterial;
            }

            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) TryRebuildProBuilder(mf.gameObject);
                Add(mf.sharedMesh, mf.transform, mf.GetComponent<Renderer>());
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh(); smr.BakeMesh(baked); temps.Add(baked);
                Add(baked, smr.transform, smr);
            }
            foreach (var t in temps) UnityEngine.Object.DestroyImmediate(t);

            if (tList.Count == 0) return false;
            seedMat = seed;
            verts = vList.ToArray(); tris = tList.ToArray();
            normals = anyNormals ? nList.ToArray() : null;
            uvs     = anyUvs     ? uList.ToArray() : null;
            return true;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    // ProBuilderMesh.ToMesh(MeshTopology.Triangles) + Refresh(RefreshMask.All), by reflection.
    private static void TryRebuildProBuilder(GameObject go)
    {
        var pbType = Type.GetType("UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder");
        if (pbType == null) return;
        var pb = go.GetComponent(pbType);
        if (pb == null) return;
        var maskType = pbType.Assembly.GetType("UnityEngine.ProBuilder.RefreshMask");
        pbType.GetMethod("ToMesh", new[] { typeof(MeshTopology) })?.Invoke(pb, new object[] { MeshTopology.Triangles });
        if (maskType != null)
            pbType.GetMethod("Refresh", new[] { maskType })?.Invoke(pb, new[] { Enum.Parse(maskType, "All") });
    }

    // Creates the mesh asset, or copies the new data into the existing one so its GUID/fileID (and
    // therefore the prefab's reference) survive re-runs.
    private static Mesh SaveMesh(Mesh fresh, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(fresh, path);
            return fresh;
        }
        string name = fresh.name;
        EditorUtility.CopySerialized(fresh, existing);
        existing.name = name;
        EditorUtility.SetDirty(existing);
        UnityEngine.Object.DestroyImmediate(fresh);
        return existing;
    }

    // One GameObject: MeshFilter + MeshRenderer (one slot per face, all the source look) + MeshCollider
    // on the same mesh (so FaceFromHit resolves the exact submesh — the only way to reach non-axis
    // faces like "curve"). Built in a preview scene so the user's open scene is never dirtied.
    private static GameObject SavePrefab(string shapeId, Mesh mesh, Material seedMat, int slots,
                                         string path, string sourcePath)
    {
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var go = new GameObject(shapeId);
            SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mats = new Material[slots];
            for (int i = 0; i < slots; i++) mats[i] = seedMat;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;

            var saved = PrefabUtility.SaveAsPrefabAsset(go, path, out bool ok);
            if (!ok || saved == null) { Debug.LogError($"[TileFaceSetup] failed to save {path}"); return null; }

            var importer = AssetImporter.GetAtPath(path);
            if (importer != null && importer.userData != SourceKey + sourcePath)
            {
                importer.userData = SourceKey + sourcePath;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    private static List<string> ReadNames(SerializedProperty entry)
    {
        var list = new List<string>();
        var names = entry.FindPropertyRelative("faceNames");
        for (int i = 0; names != null && i < names.arraySize; i++) list.Add(names.GetArrayElementAtIndex(i).stringValue);
        return list;
    }

    private static string Sanitize(string id)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return id;
    }
}
