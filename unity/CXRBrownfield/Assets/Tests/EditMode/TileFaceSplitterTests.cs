using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class TileFaceSplitterTests
{
    // Helpers ---------------------------------------------------------------
    // The splitter takes each triangle's geometric normal as cross(b-a, c-a) (Unity's front-face
    // convention, what RecalculateNormals does), so fixtures pass the intended OUTWARD direction and
    // the helper winds the triangles to match — the tests then exercise naming, not hand-winding.

    private static void Tri(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) (b, c) = (c, b);
        int i = v.Count; v.Add(a); v.Add(b); v.Add(c);
        t.AddRange(new[] { i, i + 1, i + 2 });
    }

    private static void Quad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
    {
        Tri(v, t, a, b, c, outward);
        Tri(v, t, a, c, d, outward);
    }

    private static TileFaceSplitter.Result Split(List<Vector3> v, List<int> t, Quaternion? cellRot = null) =>
        TileFaceSplitter.Split(v.ToArray(), t.ToArray(), null, null, Matrix4x4.identity, cellRot ?? Quaternion.identity);

    private static int TriCount(TileFaceSplitter.Result r, string face)
    {
        int i = r.faceNames.IndexOf(face);
        return i < 0 ? 0 : r.faces[i].triangleCount;
    }

    private static Vector3 Normal(TileFaceSplitter.Result r, string face) => r.faces[r.faceNames.IndexOf(face)].normal;

    private static (List<Vector3>, List<int>) Cube()
    {
        var v = new List<Vector3>(); var t = new List<int>();
        float h = 0.5f;
        Quad(v, t, new(-h,-h, h), new(-h, h, h), new( h, h, h), new( h,-h, h), Vector3.forward);  // north
        Quad(v, t, new( h,-h,-h), new( h, h,-h), new( h, h, h), new( h,-h, h), Vector3.right);    // east
        Quad(v, t, new( h,-h,-h), new( h, h,-h), new(-h, h,-h), new(-h,-h,-h), Vector3.back);     // south
        Quad(v, t, new(-h,-h,-h), new(-h, h,-h), new(-h, h, h), new(-h,-h, h), Vector3.left);     // west
        Quad(v, t, new(-h, h,-h), new(-h, h, h), new( h, h, h), new( h, h,-h), Vector3.up);       // top
        Quad(v, t, new(-h,-h, h), new(-h,-h,-h), new( h,-h,-h), new( h,-h, h), Vector3.down);     // bottom
        return (v, t);
    }

    // Tests -----------------------------------------------------------------

    [Test]
    public void Cube_SplitsIntoSixCanonicalFaces()
    {
        var (v, t) = Cube();
        var r = Split(v, t);

        Assert.AreEqual(6, r.mesh.subMeshCount);
        CollectionAssert.AreEqual(TileFaceSplitter.CanonicalOrder, r.faceNames);   // present AND in canonical order
        foreach (var f in r.faces) Assert.AreEqual(2, f.triangleCount, f.name);
        Assert.Greater(Vector3.Dot(Normal(r, "north"), Vector3.forward), 0.99f);
        Assert.Greater(Vector3.Dot(Normal(r, "top"),   Vector3.up),      0.99f);
        Assert.Greater(Vector3.Dot(Normal(r, "west"),  Vector3.left),    0.99f);
        for (int si = 0; si < 6; si++) Assert.AreEqual(6, r.mesh.GetTriangles(si).Length, "2 triangles per submesh");
    }

    [Test]
    public void Cube_CellFrameRotation_NamesInTheRotatedFrame()
    {
        var (v, t) = Cube();
        // The palette's defaultRotation is applied before naming: after a 90° yaw, the prefab's +Z face
        // points +X in the cell frame, so it must be called "east" — and the mesh itself is NOT rotated.
        var r = Split(v, t, Quaternion.Euler(0f, 90f, 0f));
        CollectionAssert.AreEqual(TileFaceSplitter.CanonicalOrder, r.faceNames);
        var bounds = r.mesh.bounds;
        Assert.AreEqual(0.5f, bounds.extents.x, 1e-4f);
        // Submesh "east" is the prefab's +Z quad: its vertices all sit at z = +0.5.
        foreach (int idx in r.mesh.GetTriangles(r.faceNames.IndexOf("east")))
            Assert.AreEqual(0.5f, r.mesh.vertices[idx].z, 1e-4f);
    }

    // The wedge tile: a gable prism — end triangles on ±Z, two slopes meeting at a ridge along Z, flat
    // bottom. Positions are the ones stored in Assets/Prefabs/Tiles/wedge.prefab.
    [Test]
    public void GablePrism_SlopesLandOnEastWest_NoTopFace()
    {
        var v = new List<Vector3>(); var t = new List<int>();
        float h = 0.5f;
        Tri (v, t, new(-h,-h,-h), new( h,-h,-h), new(0, h,-h),                    Vector3.back);          // south end
        Quad(v, t, new( h,-h,-h), new( h,-h, h), new(0, h, h), new(0, h,-h),      new Vector3(1, 0.5f, 0)); // +X slope
        Tri (v, t, new( h,-h, h), new(-h,-h, h), new(0, h, h),                    Vector3.forward);       // north end
        Quad(v, t, new(-h,-h, h), new(-h,-h,-h), new(0, h,-h), new(0, h, h),      new Vector3(-1, 0.5f, 0)); // -X slope
        Quad(v, t, new(-h,-h,-h), new(-h,-h, h), new( h,-h, h), new( h,-h,-h),    Vector3.down);          // bottom

        var r = Split(v, t);

        CollectionAssert.AreEqual(new[] { "north", "east", "south", "west", "bottom" }, r.faceNames);
        Assert.AreEqual(2, TriCount(r, "east"),  "the +X slope is one face");
        Assert.AreEqual(2, TriCount(r, "west"),  "the -X slope is one face");
        Assert.AreEqual(1, TriCount(r, "north"));
        Assert.AreEqual(1, TriCount(r, "south"));
        Assert.IsFalse(r.faceNames.Contains("top"), "a ridge has no flat top");
        // Slopes are tilted (≈26.6° off the X axis) but still axis-named, not "diagonal".
        Assert.That(Normal(r, "east").x, Is.InRange(0.85f, 0.95f));
        Assert.That(Normal(r, "west").x, Is.InRange(-0.95f, -0.85f));
    }

    // A quarter-cylinder corner: flat walls on +Z and -X meeting at the inner corner, a faceted arc
    // bulging toward +X/-Z, and flat top/bottom caps. All arc segments must chain into ONE "curve" face.
    [Test]
    public void QuarterCurve_ArcChainsIntoOneCurveFace()
    {
        var v = new List<Vector3>(); var t = new List<int>();
        const int segs = 8; float h = 0.5f;
        Vector3 corner = new(-h, 0f, h);                 // inner corner, arc radius 1
        Vector3 ArcPt(int i, float y)
        {
            float a = Mathf.Lerp(0f, 90f, (float)i / segs) * Mathf.Deg2Rad;   // 0° → +X, 90° → -Z
            return corner + new Vector3(Mathf.Cos(a), y, -Mathf.Sin(a));
        }
        for (int i = 0; i < segs; i++)                                          // arc wall, radial outward
        {
            Vector3 radial = (ArcPt(i, 0) + ArcPt(i + 1, 0)) * 0.5f - corner;
            Quad(v, t, ArcPt(i, -h), ArcPt(i, h), ArcPt(i + 1, h), ArcPt(i + 1, -h), radial);
        }
        Quad(v, t, corner + new Vector3(0,-h,0), corner + new Vector3(0,h,0), ArcPt(0, h), ArcPt(0, -h), Vector3.forward);  // north wall (+Z)
        Quad(v, t, ArcPt(segs, -h), ArcPt(segs, h), corner + new Vector3(0,h,0), corner + new Vector3(0,-h,0), Vector3.left); // west wall (-X)
        for (int i = 0; i < segs; i++)                                          // top + bottom fans
        {
            Tri(v, t, corner + new Vector3(0, h, 0), ArcPt(i + 1, h), ArcPt(i, h), Vector3.up);
            Tri(v, t, corner + new Vector3(0,-h, 0), ArcPt(i, -h), ArcPt(i + 1, -h), Vector3.down);
        }

        var r = Split(v, t);

        Assert.AreEqual(1, r.faceNames.Count(n => n == "curve"), "exactly one curved face");
        Assert.AreEqual(segs * 2, TriCount(r, "curve"), "every arc segment belongs to the curve");
        Assert.Greater(r.faces[r.faceNames.IndexOf("curve")].spanDeg, TileFaceSplitter.CurvedSpanDeg);
        CollectionAssert.AreEqual(new[] { "north", "west", "top", "bottom", "curve" }, r.faceNames);
        Assert.AreEqual(segs, TriCount(r, "top"));
        Assert.AreEqual(segs, TriCount(r, "bottom"));
        Assert.AreEqual(r.faceNames.Count, r.mesh.subMeshCount);
    }

    // The real curvedcorner.fbx: a square with ONE rounded corner (radius ≈ 0.6 of the side at +X/-Z).
    // The flat east/south walls run tangent into the arc, so the chain rule must NOT swallow them —
    // exactly axis-aligned walls stay their own faces and only the arc segments form "curve".
    [Test]
    public void RoundedCorner_TangentFlatWallsStaySeparateFromArc()
    {
        var v = new List<Vector3>(); var t = new List<int>();
        const int segs = 4; float h = 0.5f, r = 0.6f;
        Vector3 center = new(h - r, 0f, -h + r);          // arc centre (SE corner pulled in by r)
        Vector3 ArcPt(int i, float y)                      // 0° → +X side wall, 90° → -Z side wall
        {
            float a = Mathf.Lerp(0f, 90f, (float)i / segs) * Mathf.Deg2Rad;
            return center + new Vector3(r * Mathf.Cos(a), y, -r * Mathf.Sin(a));
        }
        // Plan profile, clockwise from NW: NW → NE → (east wall) → arc start … arc end → (south wall) → SW
        var plan = new List<Vector3> { new(-h, 0, h), new(h, 0, h), ArcPt(0, 0) };
        for (int i = 1; i <= segs; i++) plan.Add(ArcPt(i, 0));
        plan.Add(new(-h, 0, -h));
        for (int i = 0; i < plan.Count; i++)                                     // walls
        {
            Vector3 p = plan[i], q = plan[(i + 1) % plan.Count];
            Vector3 outward = Vector3.Cross(Vector3.up, p - q);                     // right-hand of the CW edge
            Quad(v, t, p + Vector3.down * h, p + Vector3.up * h, q + Vector3.up * h, q + Vector3.down * h, outward);
        }
        Vector3 c0 = new(0f, 0f, 0f);                                              // caps: fans from the centre
        for (int i = 0; i < plan.Count; i++)
        {
            Vector3 p = plan[i], q = plan[(i + 1) % plan.Count];
            Tri(v, t, c0 + Vector3.up * h,   p + Vector3.up * h,   q + Vector3.up * h,   Vector3.up);
            Tri(v, t, c0 + Vector3.down * h, p + Vector3.down * h, q + Vector3.down * h, Vector3.down);
        }

        var r2 = Split(v, t);

        CollectionAssert.AreEqual(new[] { "north", "east", "south", "west", "top", "bottom", "curve" }, r2.faceNames);
        Assert.AreEqual(segs * 2, TriCount(r2, "curve"), "only the arc segments are the curve");
        Assert.AreEqual(2, TriCount(r2, "east"),  "the flat east wall stays separate although tangent to the arc");
        Assert.AreEqual(2, TriCount(r2, "south"), "the flat south wall stays separate although tangent to the arc");
        Assert.AreEqual(plan.Count, TriCount(r2, "top"));
    }

    [Test]
    public void DuplicateAxisFaces_GetSuffixes_LargestKeepsPlainName()
    {
        // Two separate +Y slabs (a stepped roof), not edge-connected: both face "top".
        var v = new List<Vector3>(); var t = new List<int>();
        Quad(v, t, new(0,0,0), new(0,0,1), new(2,0,1), new(2,0,0), Vector3.up);   // big (2 tris)
        Tri (v, t, new(5,1,0), new(5,1,1), new(6,1,0),             Vector3.up);   // small (1 tri)
        var r = Split(v, t);
        CollectionAssert.AreEqual(new[] { "top", "top_2" }, r.faceNames);
        Assert.AreEqual(2, TriCount(r, "top"));
        Assert.AreEqual(1, TriCount(r, "top_2"));
    }

    [Test]
    public void NameForNormal_Convention()
    {
        Assert.AreEqual("north",    TileFaceSplitter.NameForNormal(Vector3.forward, 0f));
        Assert.AreEqual("south",    TileFaceSplitter.NameForNormal(Vector3.back, 0f));
        Assert.AreEqual("east",     TileFaceSplitter.NameForNormal(Vector3.right, 0f));
        Assert.AreEqual("west",     TileFaceSplitter.NameForNormal(Vector3.left, 0f));
        Assert.AreEqual("top",      TileFaceSplitter.NameForNormal(Vector3.up, 0f));
        Assert.AreEqual("bottom",   TileFaceSplitter.NameForNormal(Vector3.down, 0f));
        Assert.AreEqual("diagonal", TileFaceSplitter.NameForNormal(new Vector3(1, 0, -1).normalized, 0f));   // 45° plan wedge
        Assert.AreEqual("curve",    TileFaceSplitter.NameForNormal(Vector3.right, 90f));
    }
}
