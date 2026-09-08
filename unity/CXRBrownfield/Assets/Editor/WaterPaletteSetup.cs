using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Creates the guarded WaterPalette asset and its seed materials. Idempotent: existing materials
// are left alone, an existing palette only gains the seed ids it is missing (appended, never a
// whole-array rewrite, per .claude/rules/palette-assets.md). Run once per project from the menu;
// PaletteGuard snapshots the asset on import like the other seven.
public static class WaterPaletteSetup
{
    public const string PalettePath = "Assets/Resources/WaterPalette.asset";
    public const string MaterialDir = "Assets/Materials/Water";

    // id, file name, color (alpha = opacity). Flat unlit colors chosen to read as water without
    // any reflection or texture, and to stay cheap on the Quest quality tier.
    private static readonly (string id, string file, Color color)[] Seeds =
    {
        ("clear", "Water_Clear", new Color(0.35f, 0.65f, 0.90f, 0.70f)),
        ("lake",  "Water_Lake",  new Color(0.15f, 0.40f, 0.70f, 0.80f)),
        ("murky", "Water_Murky", new Color(0.30f, 0.45f, 0.30f, 0.85f)),
        ("dark",  "Water_Dark",  new Color(0.05f, 0.12f, 0.20f, 0.85f)),
    };

    [MenuItem("Tools/CXR/Palettes/Create Water Palette (seeded)")]
    public static void CreateSeeded()
    {
        int added = Seed();
        AssetDatabase.SaveAssets();
        Debug.Log($"[WaterPaletteSetup] {PalettePath} ready ({added} entries added).");
    }

    // Returns the number of entries appended. Public so it can be driven from a script or test.
    public static int Seed()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder(MaterialDir)) AssetDatabase.CreateFolder("Assets/Materials", "Water");
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");

        var palette = AssetDatabase.LoadAssetAtPath<WaterPalette>(PalettePath);
        if (palette == null)
        {
            palette = ScriptableObject.CreateInstance<WaterPalette>();
            AssetDatabase.CreateAsset(palette, PalettePath);
        }

        int added = 0;
        foreach (var (id, file, color) in Seeds)
        {
            var mat = EnsureMaterial(Path.Combine(MaterialDir, file + ".mat").Replace('\\', '/'), file, color);
            if (palette.Has(id)) continue;
            palette.entries.Add(new WaterPalette.Entry { id = id, material = mat });
            added++;
        }
        if (added > 0) EditorUtility.SetDirty(palette);
        return added;
    }

    // A flat URP Unlit transparent material (alpha blend, no depth write, no shadows).
    private static Material EnsureMaterial(string path, string name, Color color)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        var mat = new Material(shader) { name = name };
        if (mat.HasProperty("_Surface"))   mat.SetFloat("_Surface", 1f);      // transparent
        if (mat.HasProperty("_Blend"))     mat.SetFloat("_Blend", 0f);        // alpha
        if (mat.HasProperty("_SrcBlend"))  mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (mat.HasProperty("_DstBlend"))  mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_ZWrite"))    mat.SetFloat("_ZWrite", 0f);
        if (mat.HasProperty("_Cull"))      mat.SetFloat("_Cull", (float)CullMode.Back);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color", color);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.renderQueue = (int)RenderQueue.Transparent;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
