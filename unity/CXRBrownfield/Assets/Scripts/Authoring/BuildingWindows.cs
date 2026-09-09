using System;
using System.Collections.Generic;
using UnityEngine;

// Windows on generated buildings: one window pair on every outward-facing wall face from the second
// storey up, written as the same hosted decor records the Decorate tool paints (EmbeddedObjectDef
// with a host face and placement rules). LayoutConverter runs this once per generated building; the
// render paths then seat each prop with the real prefab bounds (DecorPlacement.ReseatAll), and the
// tile editor can erase, move, or repaint them like any painted decor.
//
// The rules (prefab key, size fractions, anchor, offset) mirror one DecorPalette entry. This class
// lives in CXRAuthoring, which cannot see the palette asset, so the caller copies the entry into a
// Rule (DecorPalette.GeneratedWindowRule) and DefaultRule stands in when the entry is missing.
public static class BuildingWindows
{
    public const string DefaultPrefabKey = "WindowPair";   // DecorPalette entry id and PrefabRegistry key
    public const int    FirstFloor       = 1;              // 0-based: the second storey up
    public const float  DefaultFraction  = 0.8f;           // same 0 -> 0.8 fallback as the Decorate tool
    public const float  DefaultOffset    = 0.03f;          // metres off the wall, clear of z-fighting

    // Plain copy of the DecorPalette entry fields the converter needs (no Unity asset types).
    public struct Rule
    {
        public string prefabKey;      // empty = no windows
        public float  widthFrac;      // fraction of the face width, <= 0 -> DefaultFraction
        public float  heightFrac;     // fraction of the face height, <= 0 -> DefaultFraction
        public float  surfaceOffset;  // metres out along the face normal
        public int    anchor;         // (int)DecorAlignment.Anchor
        public int    mountAxis;      // (int)DecorAlignment.MountAxis
        public bool   flipMount;
        public bool   optional;       // true = the VR low-detail viewer skips them
    }

    public static Rule DefaultRule => new()
    {
        prefabKey     = DefaultPrefabKey,
        widthFrac     = DefaultFraction,
        heightFrac    = DefaultFraction,
        surfaceOffset = DefaultOffset,
        anchor        = (int)DecorAlignment.Anchor.Bottom,
        mountAxis     = (int)DecorAlignment.MountAxis.Auto,
        flipMount     = false,
        optional      = true,
    };

    // Adds one hosted decor per exposed wall face of every tile on floor >= FirstFloor that does
    // not already carry rule.prefabKey on that face, so a second run adds nothing and a hand-placed
    // pair is kept. Order is tile order then north/east/south/west, so the result is stable across
    // runs (ModelRequesterUI dedups generated defs on it). Returns how many were added. fitFor
    // resolves each shape's TileFit (null = full cells, right for generated square tiles).
    public static int Populate(BuildingDef def, Rule rule, Func<string, TileFit> fitFor = null)
    {
        if (def?.tiles == null || def.tiles.Count == 0 || string.IsNullOrEmpty(rule.prefabKey)) return 0;
        float cs = def.gridCellSize > 0f ? def.gridCellSize : AuthoringConventions.DEFAULT_GRID_CELL_SIZE;
        def.embeddedObjects ??= new List<EmbeddedObjectDef>();

        float widthFrac  = rule.widthFrac  > 0f ? rule.widthFrac  : DefaultFraction;
        float heightFrac = rule.heightFrac > 0f ? rule.heightFrac : DefaultFraction;

        // Stand-in basis until a render path measures the real prefab: a thin square plate, so
        // localPos is never null and sits against the wall, and a missing prefab still lands its
        // placeholder on the face.
        var basis = DecorAlignment.AnalyzeProp(Vector3.zero, new Vector3(0.5f, 0.5f, 0.05f),
                                               (DecorAlignment.MountAxis)rule.mountAxis, rule.flipMount);

        // DecorPlacement takes its own delegate type; adapt the Func the face test uses.
        DecorPlacement.FitProvider seatFit = fitFor != null ? shapeId => fitFor(shapeId) : null;

        var taken = new HashSet<(int, int, int, string)>();
        foreach (var e in def.embeddedObjects)
        {
            if (e == null || string.IsNullOrEmpty(e.hostFace)) continue;
            if (!string.Equals(e.prefabType, rule.prefabKey, StringComparison.OrdinalIgnoreCase)) continue;
            taken.Add((e.hostGridX, e.hostGridZ, e.hostFloor, e.hostFace));
        }

        int added = 0;
        // Snapshot: IsExposed walks def.tiles, and the loop appends to embeddedObjects, not tiles.
        var tiles = def.tiles;
        foreach (var t in tiles)
        {
            if (t == null || t.floor < FirstFloor) continue;
            foreach (var wall in BuildingSigns.WallFaces)
            {
                Vector3 dir  = TileFaceGeometry.BaselineDir(wall);
                string  face = TileFaces.FacePointing(t, dir);
                if (face == null) continue;
                if (!TileFaces.IsExposed(tiles, t, dir, cs, fitFor)) continue;
                if (taken.Contains((t.gridX, t.gridZ, t.floor, face))) continue;

                var emb = new EmbeddedObjectDef
                {
                    instanceId = Guid.NewGuid().ToString("D"),
                    prefabType = rule.prefabKey,
                    hostGridX  = t.gridX,
                    hostGridZ  = t.gridZ,
                    hostFloor  = t.floor,
                    hostFace   = face,
                    fillsFace  = true,
                    exclusive  = false,
                    optional   = rule.optional,
                    decorWidthFrac     = widthFrac,
                    decorHeightFrac    = heightFrac,
                    decorAnchor        = rule.anchor,
                    decorSurfaceOffset = rule.surfaceOffset,
                    decorMountAxis     = rule.mountAxis,
                    decorFlipMount     = rule.flipMount,
                };
                // No face frame (a shape with no such face) means nothing to hang the prop on.
                if (!DecorPlacement.TryReseat(t, emb, cs, basis, seatFit)) continue;

                def.embeddedObjects.Add(emb);
                taken.Add((t.gridX, t.gridZ, t.floor, face));
                added++;
            }
        }
        return added;
    }
}
