using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class DecorAlignmentTests
{
    // A flat wall prop: thin along Z, upright on Y, pivot at its center.
    private static DecorAlignment.PropBasis ThinZProp(Vector3 center = default)
        => DecorAlignment.AnalyzeProp(center, new Vector3(1f, 1.5f, 0.1f), DecorAlignment.MountAxis.Auto, false);

    [Test]
    public void Auto_PicksThinnestAxisAsDepth_AndVerticalAsUp()
    {
        var basis = ThinZProp();
        Assert.AreEqual(Vector3.forward, basis.depthLocal, "thinnest axis (Z) should be the outward depth axis");
        Assert.AreEqual(Vector3.up, basis.upLocal, "vertical (Y) in-plane axis should be up");
    }

    [Test]
    public void AlignRotation_AxisAlignedWall_MatchesLegacyLookRotation()
    {
        // Regression lock: legacy wall alignment was LookRotation(normal, up). For a thin-Z, +Y-up prop
        // on any axis-aligned vertical wall, the new path must reproduce it bit-for-bit.
        var basis = ThinZProp();
        foreach (var n in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
        {
            Quaternion got = DecorAlignment.AlignRotation(basis, n, isRoof: false, randomYaw: false, yawDeg: 0f);
            Quaternion legacy = Quaternion.LookRotation(n, Vector3.up);
            Assert.Less(Quaternion.Angle(got, legacy), 1e-3f, $"wall normal {n} should match legacy");
        }
    }

    [Test]
    public void AlignRotation_MapsDepthAxisOntoFaceNormal_OnTiltedWall()
    {
        var basis = ThinZProp();
        Vector3 n = new Vector3(0.3f, 0.2f, 1f).normalized;   // a skewed/tilted wall face
        Quaternion q = DecorAlignment.AlignRotation(basis, n, isRoof: false, randomYaw: false, yawDeg: 0f);
        Vector3 mappedDepth = q * basis.depthLocal;
        Assert.Less(Vector3.Angle(mappedDepth, n), 1e-2f, "prop depth axis should point along the face normal");
    }

    [Test]
    public void SeatDistance_CenterPivot_PushesOutByHalfDepth()
    {
        // Center-pivot, thin-Z prop: back face is half the depth behind the pivot, so it must be pushed
        // out by half-depth * scale to sit flush (legacy left it half-sunk into the wall).
        var basis = ThinZProp();
        Assert.AreEqual(0.1f * 2f, DecorAlignment.SeatDistance(basis, 2f), 1e-4f);
    }

    [Test]
    public void SeatDistance_BackPivot_IsZero()
    {
        // Pivot already on the back face (center offset = +half-depth outward): nothing to push.
        var basis = ThinZProp(center: new Vector3(0f, 0f, 0.1f));
        Assert.AreEqual(0f, DecorAlignment.SeatDistance(basis, 1f), 1e-4f);
    }

    [Test]
    public void FitScaleInPlane_UsesLargerInPlaneDimension_NotDepth()
    {
        // In-plane extents are X=2, Y=3 (full sizes); depth Z=0.2 must be ignored. cell/maxInPlane = 4/3.
        var basis = ThinZProp();
        Assert.AreEqual(4f / 3f, DecorAlignment.FitScaleInPlane(basis, 4f), 1e-4f);
    }

    [Test]
    public void FitScaleBox_TakesTighterOfWidthAndHeight_PreservingAspect()
    {
        // In-plane width X=2, height Y=3. Budget 0.5w x 0.5h of a 4-unit cell = 2.0 wide, 2.0 tall.
        // Width scale = 2.0/2 = 1.0; height scale = 2.0/3 ≈ 0.667. The tighter (height) wins.
        var basis = ThinZProp();
        Assert.AreEqual(2f / 3f, DecorAlignment.FitScaleBox(basis, 4f, 0.5f, 0.5f), 1e-4f);
    }

    [Test]
    public void AnchorOffset_BottomAndTop_SeatOnCellEdges()
    {
        // Cell edge 4, a prop scaled to height 2: slack from center to a seated edge = (4-2)/2 = 1.
        Assert.AreEqual( 0f, DecorAlignment.AnchorOffset(DecorAlignment.Anchor.Center, 2f, 4f), 1e-4f);
        Assert.AreEqual(-1f, DecorAlignment.AnchorOffset(DecorAlignment.Anchor.Bottom, 2f, 4f), 1e-4f);
        Assert.AreEqual( 1f, DecorAlignment.AnchorOffset(DecorAlignment.Anchor.Top,    2f, 4f), 1e-4f);
    }

    [Test]
    public void Override_NegY_MakesChunkyPropStandOnBase()
    {
        // A tall chunky prop (e.g. chimney): Auto would mis-pick the thinnest axis. NegY names the base.
        var basis = DecorAlignment.AnalyzeProp(Vector3.zero, new Vector3(0.5f, 2f, 0.5f),
                                               DecorAlignment.MountAxis.NegY, false);
        Assert.AreEqual(Vector3.down, basis.depthLocal, "NegY override should make -Y the outward mount axis");

        // On a flat roof (normal +Y) the prop's -Y (base) should map to +Y (stand upright).
        Quaternion q = DecorAlignment.AlignRotation(basis, Vector3.up, isRoof: true, randomYaw: false, yawDeg: 0f);
        Vector3 mappedDepth = q * basis.depthLocal;
        Assert.Less(Vector3.Angle(mappedDepth, Vector3.up), 1e-2f, "base should point up along the roof normal");
    }
}
