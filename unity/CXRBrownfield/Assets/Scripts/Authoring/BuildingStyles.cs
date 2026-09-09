using System;
using System.Collections.Generic;

// Building styles: the opaque letters A to F a designer assigns in the notes for a sketch
// ("Ice cream shop: style A"). The layout model copies a letter into GeneratedBuilding.style, the
// converter carries it to BuildingDef.style, and BuildingStylePalette (Assets/Scripts) maps it to a
// MaterialPalette id when the tiles spawn. The model never sees what a letter looks like.
//
// This class holds the pure rules: which letters exist and which faces a style paints. It has no
// Unity dependencies so the EditMode tests cover it.
public static class BuildingStyles
{
    public static readonly string[] Ids = { "A", "B", "C", "D", "E", "F" };

    // "a" / " B " -> "A" / "B". Null, blank, or anything outside A to F ("G", "AB") -> null, which
    // means "no style" everywhere downstream.
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().ToUpperInvariant();
        return Array.IndexOf(Ids, s) >= 0 ? s : null;
    }

    // Position of a normalized letter in Ids, or -1 for null / unknown.
    public static int IndexOf(string style) => style == null ? -1 : Array.IndexOf(Ids, style);

    // A style paints every face of a shape except its top and bottom, so a quarter-curve's curved
    // outer face counts as a wall the same way north / east / south / west do.
    public static bool IsWallFace(string face) =>
        !string.IsNullOrEmpty(face)
        && !string.Equals(face, "top",    StringComparison.OrdinalIgnoreCase)
        && !string.Equals(face, "bottom", StringComparison.OrdinalIgnoreCase);

    // The wall faces of a shape the style still owns. Hand-painted faces (TileDef.faceMaterials)
    // keep their paint. faceNames is the shape's submesh-order face list (TileShapePalette entry).
    public static List<string> UnpaintedWallFaces(IList<string> faceNames, TileDef tile)
    {
        var result = new List<string>();
        if (faceNames == null) return result;
        var painted = tile?.faceMaterials;
        foreach (var face in faceNames)
        {
            if (!IsWallFace(face)) continue;
            if (painted != null && painted.TryGetValue(face, out var id) && !string.IsNullOrEmpty(id)) continue;
            result.Add(face);
        }
        return result;
    }
}
