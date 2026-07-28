using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// FenceBuilder lives in the CXRAuthoring assembly (referenced by this test asmdef). These tests pin
// down the round-fit panel behavior: a run gets the panel count nearest to length/panelLength (min 1),
// panels stretch or shrink to tile the run exactly, and endpoints/corners land on the control points.
[TestFixture]
public class FenceBuilderTests
{
    [Test]
    public void Build_TenMeters_ThreeMeterPanels_StretchesToThree()
    {
        var placements = FenceBuilder.Build(new List<Vector2> { new(0, 0), new(10, 0) }, 0f, 3f);

        var panels = placements.Where(p => !p.isPost).ToList();
        var posts  = placements.Where(p => p.isPost).ToList();

        Assert.AreEqual(3, panels.Count);
        foreach (var p in panels) Assert.That(p.span, Is.EqualTo(10f / 3f).Within(1e-3f));
        Assert.AreEqual(4, posts.Count);
    }

    [Test]
    public void Build_ElevenMeters_ThreeMeterPanels_ShrinksToFour()
    {
        var placements = FenceBuilder.Build(new List<Vector2> { new(0, 0), new(11, 0) }, 0f, 3f);

        var panels = placements.Where(p => !p.isPost).ToList();
        Assert.AreEqual(4, panels.Count);
        foreach (var p in panels) Assert.That(p.span, Is.EqualTo(2.75f).Within(1e-3f));
    }

    [Test]
    public void Build_ShortRun_ClampsToOnePanel()
    {
        var placements = FenceBuilder.Build(new List<Vector2> { new(0, 0), new(0.8f, 0) }, 0f, 3f);

        var panels = placements.Where(p => !p.isPost).ToList();
        Assert.AreEqual(1, panels.Count);
        Assert.That(panels[0].span, Is.EqualTo(0.8f).Within(1e-3f));
    }

    [Test]
    public void Build_EndpointsExact()
    {
        var start = new Vector2(2, 5);
        var end   = new Vector2(9, 5);
        var placements = FenceBuilder.Build(new List<Vector2> { start, end }, 0f, 3f);

        var posts = placements.Where(p => p.isPost).ToList();
        Assert.That(Vector2.Distance(posts.First().pos, start), Is.LessThan(1e-3f));
        Assert.That(Vector2.Distance(posts.Last().pos,  end),   Is.LessThan(1e-3f));
    }

    [Test]
    public void Build_LShape_FitsEachLegIndependently_PostOnCorner()
    {
        // 10m leg + 7m leg with 3m panels: 3 + 2 panels; the corner control point gets a post and no
        // panel straddles it.
        var corner = new Vector2(10, 0);
        var ctrl = new List<Vector2> { new(0, 0), corner, new(10, 7) };
        var placements = FenceBuilder.Build(ctrl, 0f, 3f);

        var panels = placements.Where(p => !p.isPost).ToList();
        Assert.AreEqual(5, panels.Count);
        Assert.That(panels[0].span, Is.EqualTo(10f / 3f).Within(1e-3f));
        Assert.That(panels[4].span, Is.EqualTo(3.5f).Within(1e-3f));

        Assert.IsTrue(placements.Any(p => p.isPost && Vector2.Distance(p.pos, corner) < 1e-3f));
    }

    [Test]
    public void Build_PanelsTileRunWithoutGaps()
    {
        // Consecutive panel midpoints ± half-span must meet: end of panel i == start of panel i+1.
        var placements = FenceBuilder.Build(new List<Vector2> { new(0, 0), new(10, 0) }, 0f, 3f);
        var panels = placements.Where(p => !p.isPost).ToList();

        for (int i = 1; i < panels.Count; i++)
        {
            float prevEnd   = panels[i - 1].pos.x + panels[i - 1].span * 0.5f;
            float nextStart = panels[i].pos.x - panels[i].span * 0.5f;
            Assert.That(nextStart, Is.EqualTo(prevEnd).Within(1e-3f));
        }
    }
}
