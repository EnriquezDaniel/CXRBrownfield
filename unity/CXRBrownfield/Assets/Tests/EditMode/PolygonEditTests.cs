using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

[TestFixture]
public class PolygonEditTests
{
    private static List<Vector2> Square() => new List<Vector2>
    {
        new Vector2(0f, 0f),
        new Vector2(10f, 0f),
        new Vector2(10f, 10f),
        new Vector2(0f, 10f),
    };

    [Test]
    public void TryInsertVertex_ProjectsOntoNearestEdge()
    {
        // Click near the middle of the bottom edge (segment 0 -> 1).
        Assert.IsTrue(PolygonEdit.TryInsertVertex(Square(), new Vector2(5f, 0.5f), 2f, out int index, out Vector2 pt));
        Assert.AreEqual(1, index);                 // inserted between vertex 0 and old vertex 1
        Assert.AreEqual(5f, pt.x, 1e-4f);
        Assert.AreEqual(0f, pt.y, 1e-4f);          // projected onto the edge
    }

    [Test]
    public void TryInsertVertex_ClosingEdge()
    {
        // Click near the closing edge (last vertex -> first vertex, x = 0).
        Assert.IsTrue(PolygonEdit.TryInsertVertex(Square(), new Vector2(0.4f, 5f), 2f, out int index, out Vector2 pt));
        Assert.AreEqual(4, index);                 // appended after the last vertex
        Assert.AreEqual(0f, pt.x, 1e-4f);
        Assert.AreEqual(5f, pt.y, 1e-4f);
    }

    [Test]
    public void TryInsertVertex_TooFarOrDegenerate()
    {
        Assert.IsFalse(PolygonEdit.TryInsertVertex(Square(), new Vector2(50f, 50f), 2f, out _, out _));
        Assert.IsFalse(PolygonEdit.TryInsertVertex(null, new Vector2(5f, 0f), 2f, out _, out _));
        Assert.IsFalse(PolygonEdit.TryInsertVertex(new List<Vector2> { Vector2.zero, Vector2.one }, new Vector2(0.5f, 0.5f), 2f, out _, out _));
        Assert.IsFalse(PolygonEdit.TryInsertVertex(Square(), new Vector2(5f, 0.5f), 0f, out _, out _));
    }

    [Test]
    public void PickTolerance_ScalesWithSizeAndClamps()
    {
        Assert.AreEqual(2f, PolygonEdit.PickTolerance(1f, 1f), 1e-4f);          // tiny -> floor
        Assert.AreEqual(30f, PolygonEdit.PickTolerance(4000f, 4000f), 1e-4f);   // huge -> ceiling
        float mid = PolygonEdit.PickTolerance(300f, 400f);                       // diag 500 -> 20
        Assert.AreEqual(20f, mid, 1e-4f);
    }
}
