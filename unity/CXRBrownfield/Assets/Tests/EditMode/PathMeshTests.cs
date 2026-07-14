using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

// PathMesh lives in the CXRAuthoring assembly (referenced by this test asmdef).
[TestFixture]
public class PathMeshTests
{
    // Strip alone (caps disabled): 2 verts per centerline point, 2 triangles per segment.
    [Test]
    public void Build_TwoPointPath_StripHasFourVertsAndTwoTriangles()
    {
        var line = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(10, 0, 0) };
        Mesh mesh = PathMesh.Build(line, 4f, null, PathMesh.DefaultTileLength, capSegments: 0);

        Assert.IsNotNull(mesh);
        Assert.AreEqual(4, mesh.vertexCount);          // 2 per centerline point
        Assert.AreEqual(6, mesh.triangles.Length);     // one quad = two triangles
    }

    [Test]
    public void Build_ThreePointPath_StripHasSixVertsAndFourTriangles()
    {
        var line = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 0, 10) };
        Mesh mesh = PathMesh.Build(line, 3f, null, PathMesh.DefaultTileLength, capSegments: 0);

        Assert.IsNotNull(mesh);
        Assert.AreEqual(6, mesh.vertexCount);
        Assert.AreEqual(12, mesh.triangles.Length);    // two segments × two triangles
    }

    // Each rounded cap adds (capSegments + 2) verts and capSegments triangles; two caps per path.
    [Test]
    public void Build_RoundedCaps_AddExpectedGeometry()
    {
        var line = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(10, 0, 0) };
        const int caps = 6;
        Mesh mesh = PathMesh.Build(line, 4f, null, PathMesh.DefaultTileLength, capSegments: caps);

        int expectedVerts = 4 + 2 * (caps + 2);
        int expectedTris  = 6 + 2 * caps * 3;
        Assert.AreEqual(expectedVerts, mesh.vertexCount);
        Assert.AreEqual(expectedTris, mesh.triangles.Length);
    }

    [Test]
    public void Build_RibbonSpansRequestedWidth()
    {
        var line = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(10, 0, 0) };
        Mesh mesh = PathMesh.Build(line, 4f, null, PathMesh.DefaultTileLength, capSegments: 0);

        // verts[0] and verts[1] are the left/right edges at the first centerline point.
        float spread = Vector3.Distance(mesh.vertices[0], mesh.vertices[1]);
        Assert.AreEqual(4f, spread, 0.001f);
    }

    // The height callback is invoked per edge vertex so the ribbon can tilt across a side-slope.
    [Test]
    public void Build_HeightCallback_SetsEdgeVertexY()
    {
        var line = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(10, 0, 0) };
        // Ground slopes up with +z: edge at +z sits higher than the edge at -z.
        Mesh mesh = PathMesh.Build(line, 4f, (x, z) => z, PathMesh.DefaultTileLength, capSegments: 0);

        var v0 = mesh.vertices[0];
        var v1 = mesh.vertices[1];
        Assert.AreEqual(v0.z, v0.y, 0.001f);          // each edge Y matches its own terrain height
        Assert.AreEqual(v1.z, v1.y, 0.001f);
        Assert.AreNotEqual(v0.y, v1.y);               // ribbon tilts rather than lying flat
    }

    [Test]
    public void Build_DegenerateInput_ReturnsNull()
    {
        Assert.IsNull(PathMesh.Build(null, 2f));
        Assert.IsNull(PathMesh.Build(new List<Vector3> { Vector3.zero }, 2f));
    }
}
