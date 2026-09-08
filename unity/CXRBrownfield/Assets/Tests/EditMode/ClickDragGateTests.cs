using NUnit.Framework;
using UnityEngine;

// ClickDragGate lives in the CXRAuthoring assembly. These pin the click-vs-drag rules the tile
// editor's Add tool relies on: a press commits one tile on release unless the cursor travelled past
// the threshold, in which case the press is a drag and the release commits nothing.
[TestFixture]
public class ClickDragGateTests
{
    [Test]
    public void PressAndReleaseWithoutMovement_IsClick()
    {
        var g = new ClickDragGate();
        g.Press(new Vector2(100f, 100f));
        Assert.IsTrue(g.Armed);
        Assert.IsFalse(g.Dragging);
        Assert.IsTrue(g.Release());
        Assert.IsFalse(g.Armed);
    }

    [Test]
    public void MovementUnderThreshold_IsStillClick()
    {
        var g = new ClickDragGate(5f);
        g.Press(new Vector2(100f, 100f));
        Assert.IsFalse(g.Update(new Vector2(103f, 101f)));   // ~3.2 px
        Assert.IsFalse(g.Update(new Vector2(100f, 105f)));   // exactly 5 px: not past the threshold
        Assert.IsFalse(g.Dragging);
        Assert.IsTrue(g.Release());
    }

    [Test]
    public void CrossingThreshold_ReportsDragOnce_AndReleaseIsNotClick()
    {
        var g = new ClickDragGate(5f);
        g.Press(new Vector2(100f, 100f));
        Assert.IsFalse(g.Update(new Vector2(102f, 100f)));
        Assert.IsTrue(g.Update(new Vector2(110f, 100f)));    // the frame the press becomes a drag
        Assert.IsTrue(g.Dragging);
        Assert.IsFalse(g.Update(new Vector2(150f, 140f)));   // only once per press
        Assert.IsFalse(g.Update(new Vector2(100f, 100f)));   // wandering back does not undo a drag
        Assert.IsFalse(g.Release());
        Assert.IsFalse(g.Armed);
        Assert.IsFalse(g.Dragging);
    }

    [Test]
    public void ReleaseWhenNotArmed_IsNotClick()
    {
        var g = new ClickDragGate();
        Assert.IsFalse(g.Release());
        Assert.IsFalse(g.Update(new Vector2(500f, 500f)));   // ignored without a press
        Assert.IsFalse(g.Dragging);
    }

    [Test]
    public void Cancel_ClearsEverything()
    {
        var g = new ClickDragGate();
        g.Press(new Vector2(10f, 10f));
        g.Update(new Vector2(50f, 50f));
        g.Cancel();
        Assert.IsFalse(g.Armed);
        Assert.IsFalse(g.Dragging);
        Assert.IsFalse(g.Release());
    }

    [Test]
    public void NewPress_ResetsAPreviousDrag()
    {
        var g = new ClickDragGate();
        g.Press(new Vector2(0f, 0f));
        g.Update(new Vector2(40f, 0f));
        Assert.IsTrue(g.Dragging);
        g.Press(new Vector2(200f, 200f));
        Assert.IsFalse(g.Dragging);
        Assert.AreEqual(new Vector2(200f, 200f), g.PressPos);
        Assert.IsTrue(g.Release());
    }
}
