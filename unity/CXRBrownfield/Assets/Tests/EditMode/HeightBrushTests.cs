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
    public void Flatten_MovesTowardTarget_ByWeightAtTheCenter_EasingToTheRim()
    {
        var win = FlatWindow();
        HeightBrush.Flatten(ref win, 32f, 32f, 4f, 0.8f, 1f, null, 0f, 0f);
        Assert.AreEqual(0.8f, win.heights[32, 32], 1e-3f, "weight 1 lands on the target at the center (within the cap)");
        Assert.AreEqual(BASE, win.heights[32, 36], 1e-6f, "rim untouched");

        win = FlatWindow();
        HeightBrush.Flatten(ref win, 32f, 32f, 4f, 0.8f, 0.5f, null, 0f, 0f);
        Assert.AreEqual(0.65f, win.heights[32, 32], 1e-6f, "weight 0.5 halves the gap");
        // Halfway to the rim the falloff is 0.5, so the cell keeps 0.5^0.5 of its gap.
        Assert.AreEqual(BASE + 0.3f * (1f - Mathf.Sqrt(0.5f)), win.heights[32, 34], 1e-5f);
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
        Assert.AreEqual(0.8f, win.heights[32, 32], 1e-3f);

        win = FlatWindow();
        win.heights[32, 32] = 1f;
        HeightBrush.Stamp(ref win, HeightBrushKind.Smooth, 32f, 32f, 4f, 1f, 0f, null, 0f, 0f);
        Assert.Less(win.heights[32, 32], 1f);
    }

    // ---- Merge rule: frames folded into one stored sample must replay as those frames ----
    // This is the contract that keeps the ground from jumping on release: the live brush stamps
    // once per frame, the replay stamps the stored sample, and the two must agree everywhere.

    private static HeightWindow Bumpy()
    {
        var win = FlatWindow();
        for (int z = 0; z < RES; z++)
            for (int x = 0; x < RES; x++)
                win.heights[z, x] = BASE + 0.15f * Mathf.Sin(x * 0.7f) * Mathf.Cos(z * 0.5f);
        win.heights[32, 32] = 0.9f;
        win.heights[30, 33] = 0.2f;
        return win;
    }

    private static void AssertSame(HeightWindow a, HeightWindow b, float tol)
    {
        for (int z = 0; z < RES; z++)
            for (int x = 0; x < RES; x++)
                Assert.AreEqual(a.heights[z, x], b.heights[z, x], tol, $"cell [{z}, {x}]");
    }

    [Test]
    public void MergeAmount_RaiseSums_FlattenComposesAndCaps()
    {
        Assert.AreEqual(3f,   HeightBrush.MergeAmount(HeightBrushKind.Raise,   1f, 2f),   1e-6f);
        Assert.AreEqual(-1f,  HeightBrush.MergeAmount(HeightBrushKind.Raise,   1f, -2f),  1e-6f);
        Assert.AreEqual(0.75f, HeightBrush.MergeAmount(HeightBrushKind.Flatten, 0.5f, 0.5f), 1e-6f);
        Assert.AreEqual(HeightBrush.FLATTEN_MAX_WEIGHT, HeightBrush.MergeAmount(HeightBrushKind.Flatten, 1f, 0.5f), 1e-6f);
    }

    [Test]
    public void NewSample_ShapesByKind()
    {
        CollectionAssert.AreEqual(new[] { 1f, 2f, -3f }, HeightBrush.NewSample(HeightBrushKind.Raise, 1f, 2f, -3f));

        var flatten = HeightBrush.NewSample(HeightBrushKind.Flatten, 1f, 2f, 5f);
        Assert.AreEqual(3, flatten.Length);
        Assert.AreEqual(HeightBrush.FLATTEN_MAX_WEIGHT, flatten[2], 1e-6f, "a frame weight clamps to [0, 1], then the cap");

        var smooth = HeightBrush.NewSample(HeightBrushKind.Smooth, 1f, 2f, 0.05f);
        CollectionAssert.AreEqual(new[] { 1f, 2f, 0.05f, 1f }, smooth);
        Assert.AreEqual(1, HeightBrush.SamplePasses(HeightBrushKind.Smooth, smooth));
        Assert.AreEqual(1, HeightBrush.SamplePasses(HeightBrushKind.Raise, new[] { 0f, 0f, 1f, 7f }), "only Smooth counts passes");
    }

    [Test]
    public void MergeSample_Raise_SumsAndAppliesTheFrame()
    {
        var s = HeightBrush.NewSample(HeightBrushKind.Raise, 0f, 0f, 1f);
        Assert.AreEqual(2f, HeightBrush.MergeSample(HeightBrushKind.Raise, s, 2f), 1e-6f);
        Assert.AreEqual(3f, s[2], 1e-6f);
    }

    [Test]
    public void MergeSample_Smooth_SumsWeightAndCountsPasses()
    {
        var s = HeightBrush.NewSample(HeightBrushKind.Smooth, 0f, 0f, 0.1f);
        Assert.AreEqual(0.2f, HeightBrush.MergeSample(HeightBrushKind.Smooth, s, 0.2f), 1e-6f, "the live stamp applies the frame's own weight");
        HeightBrush.MergeSample(HeightBrushKind.Smooth, s, 0.3f);
        Assert.AreEqual(0.6f, s[2], 1e-6f);
        Assert.AreEqual(3, HeightBrush.SamplePasses(HeightBrushKind.Smooth, s));
    }

    [Test]
    public void MergeSample_Flatten_ComposesAndStopsAtTheCap()
    {
        var s = HeightBrush.NewSample(HeightBrushKind.Flatten, 0f, 0f, 0.5f);
        Assert.AreEqual(0.5f, HeightBrush.MergeSample(HeightBrushKind.Flatten, s, 0.5f), 1e-6f, "below the cap the frame applies as is");
        Assert.AreEqual(0.75f, s[2], 1e-6f);

        Assert.AreEqual(0.9f, HeightBrush.MergeSample(HeightBrushKind.Flatten, s, 0.9f), 1e-6f);
        Assert.AreEqual(0.975f, s[2], 1e-6f);

        float inc = HeightBrush.MergeSample(HeightBrushKind.Flatten, s, 0.999f);   // would compose to 0.999975
        Assert.AreEqual(HeightBrush.FLATTEN_MAX_WEIGHT, s[2], 1e-6f);
        Assert.AreEqual(1f - (1f - HeightBrush.FLATTEN_MAX_WEIGHT) / 0.025f, inc, 1e-4f, "the live increment lands exactly on the cap");
        Assert.AreEqual(0f, HeightBrush.MergeSample(HeightBrushKind.Flatten, s, 0.9f), 1e-6f, "at the cap the live stamp applies nothing");
    }

    [Test]
    public void MergedFlatten_EqualsTheFrames_Everywhere()
    {
        float[] frames = { 0.02f, 0.05f, 0.01f, 0.3f, 0.02f };   // uneven, like a stuttering frame rate
        var live = Bumpy();
        var sample = HeightBrush.NewSample(HeightBrushKind.Flatten, 32f, 32f, frames[0]);
        HeightBrush.Flatten(ref live, 32f, 32f, 4f, 0.8f, sample[2], null, 0f, 0f);
        for (int i = 1; i < frames.Length; i++)
            HeightBrush.Flatten(ref live, 32f, 32f, 4f, 0.8f,
                                HeightBrush.MergeSample(HeightBrushKind.Flatten, sample, frames[i]), null, 0f, 0f);

        var replay = Bumpy();
        HeightBrush.ReplaySample(ref replay, HeightBrushKind.Flatten, sample, 32f, 32f, 4f, 0.8f, null, 0f, 0f);
        AssertSame(live, replay, 1e-5f);
    }

    [Test]
    public void FlattenHeldPastTheCap_EqualsTheFrames_AndKeepsASoftRim()
    {
        var live = FlatWindow();
        var sample = HeightBrush.NewSample(HeightBrushKind.Flatten, 32f, 32f, 0.1f);
        HeightBrush.Flatten(ref live, 32f, 32f, 4f, 0.8f, sample[2], null, 0f, 0f);
        for (int i = 0; i < 200; i++)
        {
            float inc = HeightBrush.MergeSample(HeightBrushKind.Flatten, sample, 0.1f);
            if (inc > 0f) HeightBrush.Flatten(ref live, 32f, 32f, 4f, 0.8f, inc, null, 0f, 0f);
        }
        Assert.AreEqual(HeightBrush.FLATTEN_MAX_WEIGHT, sample[2], 1e-6f);
        Assert.AreEqual(0.8f, live.heights[32, 32], 1e-3f, "the center is level");
        Assert.Greater(live.heights[32, 35], 0.55f, "near the rim the ground still moved");
        Assert.Less(live.heights[32, 35], 0.75f, "but the rim stays a slope, never a hard step");

        var replay = FlatWindow();
        HeightBrush.ReplaySample(ref replay, HeightBrushKind.Flatten, sample, 32f, 32f, 4f, 0.8f, null, 0f, 0f);
        AssertSame(live, replay, 1e-5f);
    }

    [Test]
    public void MergedSmooth_ReplaysAsThePasses_Everywhere()
    {
        const int frames = 40;   // two thirds of a second held still: far more than one blur pass
        var live = Bumpy();
        var sample = HeightBrush.NewSample(HeightBrushKind.Smooth, 32f, 32f, 0.05f);
        HeightBrush.Smooth(ref live, 32f, 32f, 4f, sample[2], null, 0f, 0f);
        for (int i = 1; i < frames; i++)
            HeightBrush.Smooth(ref live, 32f, 32f, 4f,
                               HeightBrush.MergeSample(HeightBrushKind.Smooth, sample, 0.05f), null, 0f, 0f);
        Assert.AreEqual(frames, HeightBrush.SamplePasses(HeightBrushKind.Smooth, sample));
        Assert.AreEqual(2f, sample[2], 1e-4f);

        var replay = Bumpy();
        HeightBrush.ReplaySample(ref replay, HeightBrushKind.Smooth, sample, 32f, 32f, 4f, 0f, null, 0f, 0f);
        AssertSame(live, replay, 1e-5f);

        var onePass = Bumpy();
        HeightBrush.Smooth(ref onePass, 32f, 32f, 4f, 1f, null, 0f, 0f);
        Assert.AreNotEqual(onePass.heights[32, 32], live.heights[32, 32], "one pass of the summed weight is not the frames");
    }

    // The live brush (WorldRenderer.StampHeightLive) reads only the disc's sample window from the
    // terrain, stamps it and writes it back, once per frame. The replay stamps the whole heightmap
    // once per stored sample. Simulate both for a two-second hold and require the same ground
    // everywhere, rim included: this is the release "pop" the user sees if they ever disagree.
    private static void SimulateLiveFrames(ref HeightWindow full, HeightBrushKind kind, float[] sample,
                                           float cx, float cz, float radius, float target, int frames, float perFrame)
    {
        for (int i = 0; i < frames; i++)
        {
            float amount = i == 0 ? sample[2] : HeightBrush.MergeSample(kind, sample, perFrame);
            if (kind != HeightBrushKind.Raise && amount <= 0f) continue;
            Assert.IsTrue(HeightBrush.SampleWindow(cx, cz, radius, SIZE, SIZE, RES, out int x0, out int z0, out int w, out int h));
            var block = new float[h, w];
            for (int z = 0; z < h; z++) for (int x = 0; x < w; x++) block[z, x] = full.heights[z0 + z, x0 + x];
            var win = new HeightWindow { heights = block, x0 = x0, z0 = z0, res = RES, sizeX = SIZE, sizeZ = SIZE };
            HeightBrush.Stamp(ref win, kind, cx, cz, radius, amount, target, null, 0f, 0f);
            for (int z = 0; z < h; z++) for (int x = 0; x < w; x++) full.heights[z0 + z, x0 + x] = block[z, x];
        }
    }

    [TestCase(HeightBrushKind.Flatten)]
    [TestCase(HeightBrushKind.Smooth)]
    public void WindowedLiveFrames_MatchFullReplay_ToTheRim(HeightBrushKind kind)
    {
        const int frames = 120; const float perFrame = 1f / 60f;   // strength 1 for two seconds
        float cx = 32.3f, cz = 31.6f, radius = 6f, target = 0.8f;

        var live = Bumpy();
        var sample = HeightBrush.NewSample(kind, cx, cz, perFrame);
        SimulateLiveFrames(ref live, kind, sample, cx, cz, radius, target, frames, perFrame);

        var replay = Bumpy();
        HeightBrush.ReplaySample(ref replay, kind, sample, cx, cz, radius, target, null, 0f, 0f);
        AssertSame(live, replay, 1e-5f);

        // And the hold really reached the rim: a cell 80% of the way out moved by a visible amount.
        var untouched = Bumpy();
        Assert.Greater(Mathf.Abs(live.heights[32, 37] - untouched.heights[32, 37]), 1e-3f, "the rim moved while held");
    }

    [Test]
    public void LegacySmoothSample_ReplaysOnePass()
    {
        var legacy = new[] { 32f, 32f, 0.6f };
        Assert.AreEqual(1, HeightBrush.SamplePasses(HeightBrushKind.Smooth, legacy));

        var replay = Bumpy();
        HeightBrush.ReplaySample(ref replay, HeightBrushKind.Smooth, legacy, 32f, 32f, 4f, 0f, null, 0f, 0f);
        var once = Bumpy();
        HeightBrush.Smooth(ref once, 32f, 32f, 4f, 0.6f, null, 0f, 0f);
        AssertSame(replay, once, 1e-6f);
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
