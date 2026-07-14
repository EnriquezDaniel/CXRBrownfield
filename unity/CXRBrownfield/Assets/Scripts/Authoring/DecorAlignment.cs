using UnityEngine;

// Pure geometry for the Decorate tool's auto-alignment. Lives in CXRAuthoring (no PrefabRegistry /
// DecorPalette references — those are Assembly-CSharp) so callers measure the prefab's bounds
// and pass them in. The result is baked at paint time into EmbeddedObjectDef.rotationX/Y/Z + localPos,
// so the render paths (WorldRenderer.RenderEmbeddedObjects, TileBuildingEditor.SpawnEmbeddedGO) need
// no change.
//
// Model: a prop has a "mount" basis — depthLocal (the axis pointing OUT of the surface it presses
// against) and upLocal (the in-plane axis to keep aligned with the face's reference direction). We
// map (depthLocal -> faceNormal, upLocal -> faceUp) and seat the prop so its back face sits flush on
// the surface. For an axis-aligned vertical wall with the legacy basis (depth=+Z, up=+Y) this reduces
// to the old LookRotation(normal, up) — regression-safe.
public static class DecorAlignment
{
    // Names the prop-local axis that points OUT of the mounting surface (i.e. the back/mount face is
    // the opposite side). Auto infers it from the mesh bounds; the rest are explicit overrides.
    public enum MountAxis { Auto, PosX, NegX, PosY, NegY, PosZ, NegZ }

    // Vertical seat of a face-decoration within its tile cell. Center keeps the prop mid-face;
    // Bottom seats its base on the cell's bottom edge (doors); Top on the top edge.
    public enum Anchor { Center, Bottom, Top }

    public struct PropBasis
    {
        public Vector3 depthLocal;     // unit outward mount axis (prop-local, post-authored frame)
        public Vector3 upLocal;        // unit in-plane "up" axis (prop-local)
        public float   backOffset;     // signed pivot->backface distance along depthLocal (unscaled)
        public float   inPlaneMax;     // larger of the two in-plane FULL extents (unscaled)
        public float   inPlaneWidth;   // in-plane FULL extent across (the non-up in-plane axis), unscaled
        public float   inPlaneHeight;  // in-plane FULL extent along upLocal, unscaled
    }

    // Infers a prop's mount basis from bounds measured in the post-authored frame (i.e. with
    // prefab.transform.rotation applied, the frame embRot acts on). center/extents come straight from
    // a Bounds (extents are half-sizes). ov names the depth axis explicitly; Auto = thinnest axis.
    public static PropBasis AnalyzeProp(Vector3 center, Vector3 extents, MountAxis ov, bool flipMount)
    {
        Vector3 depth;
        if (ov == MountAxis.Auto)
        {
            // Thinnest axis = the side pressed against the surface (flat wall props: windows, vents).
            if (extents.x <= extents.y && extents.x <= extents.z)      depth = Vector3.right;
            else if (extents.y <= extents.x && extents.y <= extents.z) depth = Vector3.up;
            else                                                       depth = Vector3.forward;
        }
        else depth = AxisVec(ov);
        if (flipMount) depth = -depth;

        int di = AxisIndex(depth);
        // The two in-plane axis indices (everything that isn't the depth axis).
        int i1 = (di + 1) % 3, i2 = (di + 2) % 3;

        // upLocal: prefer the prop's vertical (Y) axis if it's in-plane (keeps wall props upright);
        // otherwise the larger-extent in-plane axis (roof / chimney-style props).
        int upi;
        if (i1 == 1 || i2 == 1) upi = 1;
        else upi = Component(extents, i1) >= Component(extents, i2) ? i1 : i2;
        int widthi = upi == i1 ? i2 : i1;   // the other in-plane axis (across the face)

        return new PropBasis
        {
            depthLocal    = depth,
            upLocal       = UnitAxis(upi),
            backOffset    = Vector3.Dot(center, depth) - Component(extents, di),
            inPlaneMax    = Mathf.Max(2f * Component(extents, i1), 2f * Component(extents, i2)),
            inPlaneWidth  = 2f * Component(extents, widthi),
            inPlaneHeight = 2f * Component(extents, upi),
        };
    }

    // The in-plane reference direction the prop's upLocal is aligned to, derived from the actual face
    // so tilted / skewed / sloped faces tilt the prop with them. Wall: world-up projected into the
    // face plane (vertical wall -> exactly world-up). Roof: world-forward projected into the plane.
    public static Vector3 FaceUp(Vector3 faceNormal, bool isRoof)
    {
        Vector3 n = faceNormal.normalized;
        Vector3 primary = isRoof ? Vector3.forward : Vector3.up;
        Vector3 v = Vector3.ProjectOnPlane(primary, n);
        if (v.sqrMagnitude < 1e-6f)
            v = Vector3.ProjectOnPlane(isRoof ? Vector3.right : Vector3.forward, n);
        return v.normalized;
    }

    // Rotation that maps (depthLocal -> faceNormal, upLocal -> FaceUp). randomYaw spins about the
    // face normal by yawDeg (caller-supplied so RNG stays out of this pure helper).
    public static Quaternion AlignRotation(PropBasis basis, Vector3 faceNormal, bool isRoof, bool randomYaw, float yawDeg)
    {
        Vector3 n = faceNormal.normalized;
        Quaternion propQ   = Quaternion.LookRotation(basis.depthLocal, basis.upLocal);
        Quaternion targetQ = Quaternion.LookRotation(n, FaceUp(n, isRoof));
        Quaternion embRot  = targetQ * Quaternion.Inverse(propQ);
        if (randomYaw) embRot = Quaternion.AngleAxis(yawDeg, n) * embRot;
        return embRot;
    }

    // Outward distance to push the prop along the face normal so its back face sits flush on the
    // surface (before adding the brush's surfaceOffset z-fight epsilon). scale = the prop's final scale.
    public static float SeatDistance(PropBasis basis, float scale) => -basis.backOffset * scale;

    // Fit-to-cell scale for fillTile: the larger IN-PLANE dimension spans one cell; depth is left
    // alone (so a thin window isn't shrunk by its thickness, and a tall prop isn't shrunk by depth).
    public static float FitScaleInPlane(PropBasis basis, float cellSize)
        => basis.inPlaneMax > 1e-4f ? cellSize / basis.inPlaneMax : 1f;

    // Uniform fit-to-box scale: the prop fits inside a (widthFrac x heightFrac) fraction of the cell
    // face while preserving its aspect ratio (the tighter of the two constraints wins). A door at
    // 0.6 x 0.95 stays door-shaped; a window at 0.5 x 0.5 fills a half-cell square.
    public static float FitScaleBox(PropBasis basis, float cellSize, float widthFrac, float heightFrac)
        => FitScaleBox(basis, cellSize, cellSize, widthFrac, heightFrac);

    // Rectangular-face variant for deformed faces: the fractions apply to the face's ACTUAL safe
    // extents (see TileFaceGeometry.FaceFrame), so props shrink to fit a trapezoid wall under a
    // sloped roof instead of assuming a full cellSize × cellSize square.
    public static float FitScaleBox(PropBasis basis, float faceWidth, float faceHeight, float widthFrac, float heightFrac)
    {
        float sW = basis.inPlaneWidth  > 1e-4f ? widthFrac  * faceWidth  / basis.inPlaneWidth  : float.MaxValue;
        float sH = basis.inPlaneHeight > 1e-4f ? heightFrac * faceHeight / basis.inPlaneHeight : float.MaxValue;
        float s  = Mathf.Min(sW, sH);
        return s < float.MaxValue && s > 1e-4f ? s : 1f;
    }

    // Shift along the face's up axis to seat a prop of the given (already-scaled) height at the
    // requested anchor within a cell of edge cellSize. Center = 0; Bottom seats the prop's base on
    // the cell's bottom edge; Top on the top edge.
    public static float AnchorOffset(Anchor anchor, float scaledHeight, float cellSize)
        => AnchorOffsetInBand(anchor, scaledHeight, -0.5f * cellSize, 0.5f * cellSize);

    // Band variant for deformed faces: seats the prop within an asymmetric safe band [uBottom, uTop]
    // (signed offsets along the face's up axis about its center — see TileFaceGeometry.FaceFrame).
    // Center = band midpoint; Bottom/Top seat the prop's edge on the band edge. With the symmetric
    // band ∓cellSize/2 this reduces exactly to the legacy AnchorOffset above.
    public static float AnchorOffsetInBand(Anchor anchor, float scaledHeight, float uBottom, float uTop)
    {
        return anchor switch
        {
            Anchor.Bottom => uBottom + 0.5f * scaledHeight,
            Anchor.Top    => uTop    - 0.5f * scaledHeight,
            _             => 0.5f * (uBottom + uTop),
        };
    }

    // -------------------------------------------------------------------------

    private static Vector3 AxisVec(MountAxis a) => a switch
    {
        MountAxis.PosX => Vector3.right,
        MountAxis.NegX => Vector3.left,
        MountAxis.PosY => Vector3.up,
        MountAxis.NegY => Vector3.down,
        MountAxis.PosZ => Vector3.forward,
        MountAxis.NegZ => Vector3.back,
        _              => Vector3.forward,
    };

    private static int AxisIndex(Vector3 axis)
        => Mathf.Abs(axis.x) > 0.5f ? 0 : Mathf.Abs(axis.y) > 0.5f ? 1 : 2;

    private static Vector3 UnitAxis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

    private static float Component(Vector3 v, int i) => i == 0 ? v.x : i == 1 ? v.y : v.z;
}
