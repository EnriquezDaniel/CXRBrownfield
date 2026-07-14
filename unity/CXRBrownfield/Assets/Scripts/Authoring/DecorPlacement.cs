using UnityEngine;

// Deform-aware decor placement: the single source of the seat/fit/anchor math shared by the
// Decorate tool (paint time) and the render paths (reseat pre-pass). A hosted decor def stores
// WHERE it lives (hostGridX/Z/Floor + hostFace) and its placement RULES (decor* fields, captured
// from the DecorPalette entry at paint time); TryReseat re-derives localPos/rotationXYZ/scale from
// the host tile's CURRENT TileDeform via TileFaceGeometry. Render paths call ReseatAll before
// spawning, so decor follows the building whenever its skew changes (Skew tool, undo, server data)
// while the actual spawn code keeps replaying the baked fields exactly as before.
//
// Legacy data (decorWidthFrac == 0 or no hostFace — all pre-existing and generated JSON) is never
// touched: its baked localPos/rotation replay verbatim.
public static class DecorPlacement
{
    // True when this def carries reseat rules + a recorded host face (legacy data returns false).
    public static bool IsReseatable(EmbeddedObjectDef emb) =>
        emb != null && !string.IsNullOrEmpty(emb.hostFace) && emb.decorWidthFrac > 0f;

    // Recomputes emb.localPos / rotationXYZ / scale from the host tile's current deform and writes
    // them back into the def. False (def untouched) when the face frame can't be derived — unknown
    // face name, degenerate face, or a legacy def.
    public static bool TryReseat(TileDef host, EmbeddedObjectDef emb, float cellSize,
                                 DecorAlignment.PropBasis basis)
    {
        if (host == null || !IsReseatable(emb)) return false;
        if (!TileFaceGeometry.TryGetFaceFrame(host, emb.hostFace, cellSize, out var f)) return false;

        float heightFrac = emb.decorHeightFrac > 0f ? emb.decorHeightFrac : emb.decorWidthFrac;
        float scale      = DecorAlignment.FitScaleBox(basis, f.width, f.height,
                                                      emb.decorWidthFrac, heightFrac);
        float seat       = DecorAlignment.SeatDistance(basis, scale) + emb.decorSurfaceOffset;
        float anchorOff  = DecorAlignment.AnchorOffsetInBand((DecorAlignment.Anchor)emb.decorAnchor,
                                                             basis.inPlaneHeight * scale,
                                                             f.uBottom, f.uTop);
        Vector3 pos   = f.center + f.normal * seat + f.up * anchorOff;
        Vector3 euler = DecorAlignment.AlignRotation(basis, f.normal, f.isRoof, false, 0f).eulerAngles;

        emb.localPos  = new[] { pos.x, pos.y, pos.z };
        emb.rotationX = euler.x;
        emb.rotationY = euler.y;
        emb.rotationZ = euler.z;
        emb.scale     = scale;
        return true;
    }

    // Resolves a prop's mount basis per (prefabType, mountAxis, flip). Callers wrap MeasurePropBasis
    // with a prefab lookup + cache; returning false (prefab missing) skips the reseat for that item.
    public delegate bool BasisProvider(string prefabType, DecorAlignment.MountAxis axis,
                                       bool flip, out DecorAlignment.PropBasis basis);

    // Reseats every hosted decor on the def against the tiles' current deform. Deterministic and
    // idempotent, so re-running it per building instance is harmless. Silent no-op per item when
    // the host tile is gone, the prefab is missing, or the def is legacy.
    public static void ReseatAll(BuildingDef bdef, float cellSize, BasisProvider basisFor)
    {
        if (bdef?.embeddedObjects == null || bdef.tiles == null || basisFor == null) return;
        foreach (var emb in bdef.embeddedObjects)
        {
            if (!IsReseatable(emb)) continue;
            TileDef host = null;
            foreach (var t in bdef.tiles)
                if (t.gridX == emb.hostGridX && t.gridZ == emb.hostGridZ && t.floor == emb.hostFloor)
                { host = t; break; }
            if (host == null) continue;
            if (basisFor(emb.prefabType, (DecorAlignment.MountAxis)emb.decorMountAxis,
                         emb.decorFlipMount, out var basis))
                TryReseat(host, emb, cellSize, basis);
        }
    }

    // Measures the prop's bounds in the post-authored frame (prefab.transform.rotation applied — the
    // frame embRot acts on) and returns its mount basis. Measured off-screen to avoid a one-frame
    // flash of the throwaway instance. Returns false (and an identity-ish basis) for a missing prefab.
    public static bool MeasurePropBasis(GameObject prefab, DecorAlignment.MountAxis axis, bool flip,
                                        out DecorAlignment.PropBasis basis)
    {
        basis = DecorAlignment.AnalyzeProp(Vector3.zero, Vector3.one * 0.5f, axis, flip);
        if (prefab == null) return false;

        // Place at a known origin with the prefab's authored rotation/scale, so the world AABB is
        // expressed in the same frame embRot composes onto (world axes == prefabRot * meshLocal here).
        Vector3 origin = new Vector3(0f, -100000f, 0f);
        var temp = Object.Instantiate(prefab);
        temp.transform.SetPositionAndRotation(origin, prefab.transform.rotation);
        temp.transform.localScale = prefab.transform.localScale;

        Bounds bb = default; bool any = false;
        foreach (var r in temp.GetComponentsInChildren<Renderer>())
        {
            if (!any) { bb = r.bounds; any = true; }
            else      { bb.Encapsulate(r.bounds); }
        }
        DestroyNow(temp);
        if (!any) return false;

        // Bounds center relative to the prefab pivot (origin), so backOffset is measured about the pivot.
        basis = DecorAlignment.AnalyzeProp(bb.center - origin, bb.extents, axis, flip);
        return true;
    }

    // Destroys the throwaway measuring instance immediately in both edit and play mode (same
    // pattern as TileSpawner.DestroyNow) so it never lingers for a frame.
    private static void DestroyNow(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Object.Destroy(obj);
        else Object.DestroyImmediate(obj);
    }
}
