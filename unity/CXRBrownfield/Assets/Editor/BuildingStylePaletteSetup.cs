using UnityEditor;
using UnityEngine;

// Creates the guarded BuildingStylePalette asset with the six seed styles. Idempotent: an existing
// palette only gains the seed letters it is missing (appended, never a whole-array rewrite, per
// .claude/rules/palette-assets.md), and an existing entry's material id is never touched, so a
// custom mapping survives a re-run. PaletteGuard snapshots the asset on import like the other eight.
public static class BuildingStylePaletteSetup
{
    public const string PalettePath = "Assets/Resources/BuildingStylePalette.asset";

    // Letter -> MaterialPalette id. Six clearly different reads: brick, stone, concrete, wood,
    // steel, plaster. The palette is the place to swap these for a project's own materials.
    private static readonly (string id, string wallMaterialId)[] Seeds =
    {
        ("A", "RedBricks"),
        ("B", "CobbleStoneWall"),
        ("C", "ConcreteBricks"),
        ("D", "WoodWall"),
        ("E", "SteelWall"),
        ("F", "PlasterBricks"),
    };

    [MenuItem("Tools/CXR/Palettes/Create Building Style Palette (seeded)")]
    public static void CreateSeeded()
    {
        int added = Seed();
        AssetDatabase.SaveAssets();
        Debug.Log($"[BuildingStylePaletteSetup] {PalettePath} ready ({added} entries added).");
    }

    // Returns the number of entries appended. Public so it can be driven from a script or test.
    public static int Seed()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");

        var palette = AssetDatabase.LoadAssetAtPath<BuildingStylePalette>(PalettePath);
        if (palette == null)
        {
            palette = ScriptableObject.CreateInstance<BuildingStylePalette>();
            AssetDatabase.CreateAsset(palette, PalettePath);
        }

        // Resolved by path (PaletteGuard.Paths), never FindAssets("t:MaterialPalette"), which also
        // hits ProBuilder's "Material Palette.asset" in the same folder.
        var materials = AssetDatabase.LoadAssetAtPath<MaterialPalette>("Assets/Resources/MaterialPalette.asset");

        int added = 0;
        foreach (var (id, wallMaterialId) in Seeds)
        {
            if (materials != null && !materials.Has(wallMaterialId))
                Debug.LogWarning($"[BuildingStylePaletteSetup] Seed style {id} points at '{wallMaterialId}', " +
                                 "which MaterialPalette does not have. Fix the id in the Inspector.");
            if (palette.Has(id)) continue;
            palette.entries.Add(new BuildingStylePalette.Entry { styleId = id, wallMaterialId = wallMaterialId });
            added++;
        }
        if (added > 0) EditorUtility.SetDirty(palette);
        return added;
    }
}
