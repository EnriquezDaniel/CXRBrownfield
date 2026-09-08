using NUnit.Framework;
using UnityEngine;

// HeightBrush lives in the CXRAuthoring assembly. These pin the arithmetic the Shape ground tool
// shares between its live preview (EditController) and the authoritative replay
// (WorldRenderer.ApplyHeightmap): if the two ever disagree, the ground would jump on reload.
[TestFixture]
public class HeightBrushTests
{
    // 65 samples over 64 m: one sample per meter, so indices read as meters.
    private const int   RES  = 65;
    private const float SIZE = 64f;
    private const float BASE = HeightBrush.BASE_NORMALIZED;

    private static HeightWindow FlatWindow()
    {
        var h = new float[RES, RES];
        for (int z = 0; z < RES; z++)
            for (int x = 0; x < RES; x++)
                h[z, x] = BASE;
        return new HeightWindow { heights = h, x0 = 0, z0 = 0, res = RES, sizeX = SIZE, sizeZ = SIZE };
    }

    // ---- Falloff ----

    [Test]
    public void Falloff_IsOneAtCenter_ZeroAtRim_AndMonotonic()
    {
        Assert.AreEqual(1f, HeightBrush.Falloff(0f), 1e-6f);
        Assert.AreEqual(0f, HeightBrush.Falloff(1f), 1e-6f);
        Assert.AreEqual(0f, HeightBrush.Falloff(2f), 1e-6f);
        Assert.AreEqual(1f, HeightBrush.Falloff(-1f), 1e-6f);
        float prev = 1f;
        for (float t = 0.1f; t <= 1f; t += 0.1f)
        {
            float f = HeightBrush.Falloff(t);
            Assert.LessOrEqual(f, prev, $"falloff must not rise at t={t}");
            prev = f;
        }
        Assert.AreEqual(0.5f, HeightBrush.Falloff(0.5f), 1e-6f, "smoothstep is 0.5 at its midpoint");
    }

    // ---- Raise ----

    [Test]
    public void Raise_AddsAmountOverRangeAtCenter_ZeroAtRim()
    {
        var win = FlatWindow();
        HeightBrush.Raise(ref win, 32f, 32f, 4f, 3f, 30f, null, 0f, 0f);
        Assert.AreEqual(BASE + 0.1f, win.heights[32, 32], 1e-6f, "3 m over a 30 m range is 0.1");
        Assert.AreEqual(BASE + 0.1f * 0.5f, win.heights[32, 34], 1e-6f, "half radius gets half the amount");
        Assert.AreEqual(BASE, win.heights[32, 36], 1e-6f, "the rim is untouched");
        Assert.AreEqual(BASE, win.heights[32, 40], 1e-6f, "outside the disc is untouched");
    }

    [Test]
    public void Raise_IsRadiallySymmetric()
    {
        var win = FlatWindow();
        HeightBrush.Raise(ref win, 32f, 32f, 4f, 3f, 30f, null, 0f, 0f);
        float e = win.heights[32, 34];
        Assert.AreEqual(e, win.heights[32, 30], 1e-6f);
        Assert.AreEqual(e, win.heights[34, 32], 1e-6f);
        Assert.AreEqual(e, win.heights[30, 32], 1e-6f);
        Assert.Greater(e, BASE);
    }

    [Test]
    public void Raise_NegativeAmountLowers_AndClampsToUnitRange()
    {
        var win = FlatWindow();
        HeightBrush.Raise(ref win, 32f, 32f, 4f, -3f, 30f, null, 0f, 0f);
        Assert.AreEqual(BASE - 0.1f, win.heights[32, 32], 1e-6f);

        win = FlatWindow();
        HeightBrush.Raise(ref win, 32f, 32f, 4f, 100f, 30f, null, 0f, 0f);
        Assert.AreEqual(1f, win.heights[32, 32], 1e-6f, "cannot rise above the range");
        win = FlatWindow();
        HeightBrush.Raise(ref win, 32f, 32f, 4f, -100f, 30f, null, 0f, 0f);
        Assert.AreEqual(0f, win.heights[32, 32], 1e-6f, "cannot dig below the range");
    }

    [Test]
    public void Raise_RespectsClipPolygon_InWorldMeters()
    {
        // The window is terrain local; the terrain sits at world (100, 200). The clip keeps only
        // world x >= 132, i.e. local x >= 32, so the left half of the disc must stay flat.
        var clip = new[] { new[] { 132f, 0f }, new[] { 300f, 0f }, new[] { 300f, 300f }, new[] { 132f, 300f } };
        var win = FlatWindow();
        HeightBrush.Raise(ref win, 32f, 32f, 4f, 3f, 30f, clip, 100f, 200f);
        Assert.Greater(win.heights[32, 33], BASE, "inside the clip is raised");
        Assert.AreEqual(BASE, win.heights[32, 31], 1e-6f, "outside the clip is untouched");
    }

    // ---- Flatten ----

    [Test]
    public void Flatten_MovesTowardTarget_ByWeightTimesFalloff()
    {
        var win = FlatWindow();
        HeightBrush.Flatten(ref win, 32f, 32f, 4f, 0.8f, 1f, null, 0f, 0f);
        Assert.AreEqual(0.8f, win.heights[32, 32], 1e-6f, "weight 1 lands on the target at the center");
        Assert.AreEqual(BASE, win.heights[32, 36], 1e-6f, "rim untouched");

        win = FlatWindow();
        HeightBrush.Flatten(ref win, 32f, 32f, 4f, 0.8f, 0.5f, null, 0f, 0f);
        Assert.AreEqual(0.65f, win.heights[32, 32], 1e-6f, "weight 0.5 halves the gap");
    }

    [Test]
    public void Flatten_RepeatedStampsConverge()
    {
        var win = FlatWindow();
        for (int i = 0; i < 12; i++) HeightBrush.Flatten(ref win, 32f, 32f, 4f, 0.8f, 0.5f, null, 0f, 0f);
        Assert.AreEqual(0.8f, win.heights[32, 32], 1e-3f);
        Assert.AreEqual(0.8f, win.heights[32, 33], 5e-3f, "near the center converges too, just slower");
    }

    // ---- Smooth ----

    [Test]
    public void Smooth_LeavesFlatGroundUnchanged()
    {
        var win = FlatWindow();
        HeightBrush.Smooth(ref win, 32f, 32f, 4f, 1f, null, 0f, 0f);
        for (int z = 0; z < RES; z++)
            for (int x = 0; x < RES; x++)
                Assert.AreEqual(BASE, win.heights[z, x], 1e-6f);
    }

    [Test]
    public void Smooth_ReducesASpike_AndLiftsItsNeighbors()
    {
        var win = FlatWindow();
        win.heights[32, 32] = 1f;
        float before = Variance(win, 28, 36);
        HeightBrush.Smooth(ref win, 32f, 32f, 4f, 1f, null, 0f, 0f);
        Assert.AreEqual((1f + 8f * BASE) / 9f, win.heights[32, 32], 1e-6f, "the spike becomes its 3x3 average");
        Assert.Greater(win.heights[32, 33], BASE, "a neighbor is pulled up by the spike");
        Assert.Less(win.heights[32, 33], win.heights[32, 32], "but stays below the smoothed spike");
        Assert.Less(Variance(win, 28, 36), before, "variance inside the disc drops");
    }

    [Test]
    public void Smooth_AtTheTerrainCorner_DoesNotBleedZero()
    {
        var win = FlatWindow();
        HeightBrush.Smooth(ref win, 0f, 0f, 4f, 1f, null, 0f, 0f);
        Assert.AreEqual(BASE, win.heights[0, 0], 1e-6f);
        Assert.AreEqual(BASE, win.heights[0, 2], 1e-6f);
        Assert.AreEqual(BASE, win.heights[2, 0], 1e-6f);
    }

    private static float Variance(HeightWindow win, int lo, int hi)
    {
        float sum = 0f; int n = 0;
        for (int z = lo; z <= hi; z++) for (int x = lo; x <= hi; x++) { sum += win.heights[z, x]; n++; }
        float mean = sum / n, v = 0f;
        for (int z = lo; z <= hi; z++) for (int x = lo; x <= hi; x++) { float d = win.heights[z, x] - mean; v += d * d; }
        return v / n;
    }

    // ---- Stamp dispatch ----

    [Test]
    public void Stamp_DispatchesByKind()
    {
        var win = FlatWindow();
        HeightBrush.Stamp(ref win, HeightBrushKind.Raise, 32f, 32f, 4f, 3f, 0f, null, 0f, 0f);
        Assert.AreEqual(BASE + 0.1f, win.heights[32, 32], 1e-6f);

        win = FlatWindow();
        HeightBrush.Stamp(ref win, HeightBrushKind.Flatten, 32f, 32f, 4f, 1f, 0.8f, null, 0f, 0f);
        Assert.AreEqual(0.8f, win.heights[32, 32], 1e-6f);

        win = FlatWindow();
        win.heights[32, 32] = 1f;
        HeightBrush.Stamp(ref win, HeightBrushKind.Smooth, 32f, 32f, 4f, 1f, 0f, null, 0f, 0f);
        Assert.Less(win.heights[32, 32], 1f);
    }

    // ---- Merge rule: two frames folded into one stored sample must equal two stamps ----

    [Test]
    public void MergeAmount_RaiseSums_WeightsCompose()
    {
        Assert.AreEqual(3f,   HeightBrush.MergeAmount(HeightBrushKind.Raise,   1f, 2f),   1e-6f);
        Assert.AreEqual(-1f,  HeightBrush.MergeAmount(HeightBrushKind.Raise,   1f, -2f),  1e-6f);
        Assert.AreEqual(0.75f, HeightBrush.MergeAmount(HeightBrushKind.Flatten, 0.5f, 0.5f), 1e-6f);
        Assert.AreEqual(1f,   HeightBrush.MergeAmount(HeightBrushKind.Smooth,  1f, 0.5f), 1e-6f);
    }

    [Test]
    public void MergedFlatten_EqualsTwoSequentialStamps_AtTheCenter()
    {
        var twice = FlatWindow();
        HeightBrush.Flatten(ref twice, 32f, 32f, 4f, 0.8f, 0.5f, null, 0f, 0f);
        HeightBrush.Flatten(ref twice, 32f, 32f, 4f, 0.8f, 0.5f, null, 0f, 0f);

        var once = FlatWindow();
        HeightBrush.Flatten(ref once, 32f, 32f, 4f, 0.8f, HeightBrush.MergeAmount(HeightBrushKind.Flatten, 0.5f, 0.5f), null, 0f, 0f);

        Assert.AreEqual(twice.heights[32, 32], once.heights[32, 32], 1e-6f);
    }

    [Test]
    public void MergedRaise_EqualsTwoSequentialStamps_Everywhere()
    {
        var twice = FlatWindow();
        HeightBrush.Raise(ref twice, 32f, 32f, 4f, 1f, 30f, null, 0f, 0f);
        HeightBrush.Raise(ref twice, 32f, 32f, 4f, 2f, 30f, null, 0f, 0f);

        var once = FlatWindow();
        HeightBrush.Raise(ref once, 32f, 32f, 4f, HeightBrush.MergeAmount(HeightBrushKind.Raise, 1f, 2f), 30f, null, 0f, 0f);

        for (int z = 28; z <= 36; z++)
            for (int x = 28; x <= 36; x++)
                Assert.AreEqual(twice.heights[z, x], once.heights[z, x], 1e-6f);
    }

    // ---- SampleWindow ----

    [Test]
    public void SampleWindow_PadsByOneSample_AndClampsToTerrain()
    {
        Assert.IsTrue(HeightBrush.SampleWindow(32f, 32f, 4f, SIZE, SIZE, RES, out int x0, out int z0, out int w, out int h));
        Assert.AreEqual(27, x0); Assert.AreEqual(27, z0);
        Assert.AreEqual(11, w);  Assert.AreEqual(11, h);

        Assert.IsTrue(HeightBrush.SampleWindow(0f, 0f, 4f, SIZE, SIZE, RES, out x0, out z0, out w, out h));
        Assert.AreEqual(0, x0); Assert.AreEqual(0, z0);
        Assert.AreEqual(6, w);  Assert.AreEqual(6, h);

        Assert.IsTrue(HeightBrush.SampleWindow(64f, 64f, 4f, SIZE, SIZE, RES, out x0, out z0, out w, out h));
        Assert.AreEqual(59, x0); Assert.AreEqual(6, w);
    }

    [Test]
    public void SampleWindow_RejectsADiscOffTheTerrain()
    {
        Assert.IsFalse(HeightBrush.SampleWindow(-100f, 32f, 4f, SIZE, SIZE, RES, out _, out _, out _, out _));
        Assert.IsFalse(HeightBrush.SampleWindow(32f, 500f, 4f, SIZE, SIZE, RES, out _, out _, out _, out _));
        Assert.IsFalse(HeightBrush.SampleWindow(32f, 32f, 4f, 0f, SIZE, RES, out _, out _, out _, out _));
    }

    // ---- Keys and units ----

    [Test]
    public void KindKey_ParseKind_RoundTrip_AndFallback()
    {
        foreach (HeightBrushKind k in System.Enum.GetValues(typeof(HeightBrushKind)))
            Assert.AreEqual(k, HeightBrush.ParseKind(HeightBrush.KindKey(k)));
        Assert.AreEqual(HeightBrushKind.Flatten, HeightBrush.ParseKind("FLATTEN"));
        Assert.AreEqual(HeightBrushKind.Raise,   HeightBrush.ParseKind(null));
        Assert.AreEqual(HeightBrushKind.Raise,   HeightBrush.ParseKind("bogus"));
    }

    [Test]
    public void NormalizedFromMeters_ClampsToTheRange()
    {
        Assert.AreEqual(0.5f, HeightBrush.NormalizedFromMeters(0f),    1e-6f);
        Assert.AreEqual(1f,   HeightBrush.NormalizedFromMeters(15f),   1e-6f);
        Assert.AreEqual(0f,   HeightBrush.NormalizedFromMeters(-15f),  1e-6f);
        Assert.AreEqual(1f,   HeightBrush.NormalizedFromMeters(100f),  1e-6f);
        Assert.AreEqual(3f,   HeightBrush.MetersFromNormalized(0.6f),  1e-5f);
        Assert.AreEqual(-15f, HeightBrush.BASE_WORLD_Y, 1e-6f);
    }
}
