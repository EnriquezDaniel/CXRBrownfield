using System.Collections.Generic;
using UnityEngine;

// Resolves BuildingDef.style to the MaterialPalette id TileSpawner paints on the building's wall
// faces. The one place both WorldRenderer and TileBuildingEditor go through, so a letter with no
// palette entry, or an entry pointing at a material the MaterialPalette lacks, warns once per
// distinct miss instead of once per tile face.
public static class BuildingStyleResolver
{
    private static readonly HashSet<string> _warned = new();

    public const string ResourceName = "BuildingStylePalette";

    // The palette lives in Resources so an unwired inspector slot (VRViewer, a fresh scene) still
    // resolves; callers use their serialized slot first and this as the fallback.
    public static BuildingStylePalette LoadDefault() => Resources.Load<BuildingStylePalette>(ResourceName);

    // Null = paint nothing (no style, unknown letter, missing palette or material).
    public static string WallMaterialId(BuildingDef def, BuildingStylePalette styles, MaterialPalette materials)
    {
        string style = BuildingStyles.Normalize(def?.style);
        if (style == null) return null;

        if (styles == null)
        {
            WarnOnce("palette", "[BuildingStyle] No BuildingStylePalette wired or found in Resources; " +
                                "styled buildings render with the default material.");
            return null;
        }
        string id = styles.GetWallMaterialId(style);
        if (string.IsNullOrEmpty(id))
        {
            WarnOnce("style:" + style, $"[BuildingStyle] Style '{style}' has no entry in BuildingStylePalette; " +
                                       $"'{def.name}' renders with the default material.");
            return null;
        }
        if (materials == null || !materials.Has(id))
        {
            WarnOnce("material:" + id, $"[BuildingStyle] Style '{style}' points at material '{id}', which " +
                                       "MaterialPalette lacks; the style is ignored.");
            return null;
        }
        return id;
    }

    private static void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) Debug.LogWarning(message);
    }
}
