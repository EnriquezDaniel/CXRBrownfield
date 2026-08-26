using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// Covers the geometry behind the draped lot/site outlines and the drag-rectangle site gesture.
// The sampling and width numbers matter because WorldRenderer's committed frame and
// EditController's live preview both read them: if they drift, the outline changes on commit.
public class PolygonFrameTests
{
    private static List<Vector2> Rect(float w, float l) => new()
    {
        new Vector2(0f, 0f), new Vector2(w, 0f), new Vector2(w, l), new Vector2(0f, l),
    };

    // ---- RectCorners ----

    [Test]
    public void RectCorners_SameRectangleAndWindingFromEveryDragDirection()
    {
        var expected = new[]
        {
            new Vector2(10f, 20f), new Vector2(40f, 20f), new Vector2(40f, 60f), new Vector2(10f, 60f),
        };
        var drags = new[]
        {
            (new Vector2(10f, 20f), new Vector2(40f, 60f)),   // +x +z
            (new Vector2(40f, 20f), new Vector2(10f, 60f)),   // -x +z
            (new Vector2(10f, 60f), new Vector2(40f, 20f)),   // +x -z
            (new Vector2(40f, 60f), new Vector2(10f, 20f)),   // -x -z
        };
        foreach (var (a, b) in drags)
        {
            var got = PolygonFrame.RectCorners(a, b, square: false);
            Assert.AreEqual(4, got.Length);
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(expected[i].x, got[i].x, 1e-4f, $"drag {a}->{b} corner {i}.x");
                Assert.AreEqual(expected[i].y, got[i].y, 1e-4f, $"drag {a}->{b} corner {i}.y");
            }
        }
    }

    [Test]
    public void RectCorners_SquareMatchesTheLongerAxisAndStillGrowsTowardTheCursor()
    {
        // Drag left and down: 30 m in x, 4 m in z. Squaring must extend z, not flip the rectangle
        // onto the other side of the press point.
        var press  = new Vector2(100f, 100f);
        var cursor = new Vector2(70f, 96f);
        var got    = PolygonFrame.RectCorners(press, cursor, square: true);

        PolygonFrame.Bbox(got, out float w, out float l);
        Assert.AreEqual(30f, w, 1e-4f);
        Assert.AreEqual(30f, l, 1e-4f);

        // The press corner stays a corner, and the far corner is on the cursor's side of it.
        Assert.AreEqual(70f,  got[0].x, 1e-4f);
        Assert.AreEqual(70f,  got[0].y, 1e-4f);
        Assert.AreEqual(100f, got[2].x, 1e-4f);
        Assert.AreEqual(100f, got[2].y, 1e-4f);
    }

    [Test]
    public void RectCorners_ZeroDragCollapsesToAPointWithNoArea()
    {
        var got = PolygonFrame.RectCorners(new Vector2(5f, 5f), new Vector2(5f, 5f), square: false);
        Assert.IsTrue(PolygonFrame.Bbox(got, out float w, out float l));
        Assert.AreEqual(0f, w, 1e-5f);
        Assert.AreEqual(0f, l, 1e-5f);
    }

    // ---- Spacing / Width ----

    [Test]
    public void Spacing_StaysInsideItsClampAtBothEnds()
    {
        Assert.AreEqual(PolygonFrame.MinSpacing, PolygonFrame.Spacing(0f), 1e-5f);
        Assert.AreEqual(PolygonFrame.MinSpacing, PolygonFrame.Spacing(12f), 1e-5f);
        Assert.AreEqual(PolygonFrame.MaxSpacing, PolygonFrame.Spacing(1e6f), 1e-5f);
    }

    [Test]
    public void Spacing_KeepsAHugeRingInsideThePointBudget()
    {
        // A 5 km perimeter is the pathological case the budget exists for.
        const float perimeter = 5000f;
        var ring = PolygonFrame.DenseRing(Rect(1250f, 1250f), PolygonFrame.Spacing(perimeter), closed: true);
        Assert.LessOrEqual(ring.Count, PolygonFrame.MaxRingPoints + 8);
    }

    [Test]
    public void Width_IsMonotonicInDiagonalAndClampedAtBothEnds()
    {
        Assert.AreEqual(PolygonFrame.MinWidth, PolygonFrame.Width(0f), 1e-5f);
        Assert.AreEqual(PolygonFrame.MinWidth, PolygonFrame.Width(10f), 1e-5f);
        Assert.AreEqual(PolygonFrame.MaxWidth, PolygonFrame.Width(100000f), 1e-5f);
        Assert.Less(PolygonFrame.Width(150f), PolygonFrame.Width(250f));
    }

    // ---- DenseRing ----

    [Test]
    public void DenseRing_ClosedRepeatsTheFirstCornerExactlyOnceAtTheEnd()
    {
        var corners = Rect(20f, 20f);
        var ring = PolygonFrame.DenseRing(corners, 5f, closed: true);

        Assert.AreEqual(corners[0], ring[ring.Count - 1]);
        int firstCount = 0;
        foreach (var p in ring) if ((p - corners[0]).sqrMagnitude < 1e-8f) firstCount++;
        Assert.AreEqual(2, firstCount, "start corner appears once at the head and once closing the ring");
    }

    [Test]
    public void DenseRing_OpenEndsOnTheLastCornerWithoutClosing()
    {
        var corners = Rect(20f, 20f);
        var ring = PolygonFrame.DenseRing(corners, 5f, closed: false);
        Assert.AreEqual(corners[corners.Count - 1], ring[ring.Count - 1]);
    }

    [Test]
    public void DenseRing_NeverLeavesAGapWiderThanTheRequestedSpacing()
    {
        var corners = new List<Vector2> { new(0f, 0f), new(37f, 0f), new(37f, 11f), new(4f, 23f) };
        var ring = PolygonFrame.DenseRing(corners, 3f, closed: true);
        for (int i = 1; i < ring.Count; i++)
            Assert.LessOrEqual(Vector2.Distance(ring[i - 1], ring[i]), 3f + 1e-3f, $"gap at {i}");
    }

    [Test]
    public void DenseRing_RejectsRunsTooShortToFormTheRequestedShape()
    {
        var two = new List<Vector2> { new(0f, 0f), new(10f, 0f) };
        Assert.AreEqual(0, PolygonFrame.DenseRing(two, 2f, closed: true).Count);
        Assert.Greater(PolygonFrame.DenseRing(two, 2f, closed: false).Count, 0);
        Assert.AreEqual(0, PolygonFrame.DenseRing(new List<Vector2> { new(0f, 0f) }, 2f, closed: false).Count);
        Assert.AreEqual(0, PolygonFrame.DenseRing(null, 2f, closed: true).Count);
    }

    // ---- Bbox / Perimeter ----

    [Test]
    public void BboxAndPerimeterMeasureTheCornerRun()
    {
        var corners = Rect(30f, 40f);
        Assert.IsTrue(PolygonFrame.Bbox(corners, out float w, out float l));
        Assert.AreEqual(30f, w, 1e-4f);
        Assert.AreEqual(40f, l, 1e-4f);
        Assert.AreEqual(50f, PolygonFrame.BboxDiagonal(corners), 1e-4f);
        Assert.AreEqual(140f, PolygonFrame.Perimeter(corners, closed: true), 1e-3f);
        Assert.AreEqual(100f, PolygonFrame.Perimeter(corners, closed: false), 1e-3f);
        Assert.IsFalse(PolygonFrame.Bbox(new List<Vector2>(), out _, out _));
    }
}
