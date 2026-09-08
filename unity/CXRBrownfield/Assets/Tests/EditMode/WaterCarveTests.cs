using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

// The derived water bed: WaterCarve.Carve on a flat HeightWindow. 33 samples over 32 m: one
// sample per meter, so cell (x, z) sits at terrain-local meters (x, z). Heights are measured from
// the flat ground height (0): the bed is `depth` below it, the surface at `surfaceY`.
[TestFixture]
public class WaterCarveTests
{
    private const int   RES  = 33;
    private const float SIZE = 32f;

    private static HeightWindow Flat()
    {
        var h = new float[RES, RES];
        for (int z = 0; z < RES; z++) for (int x = 0; x < RES; x++) h[z, x] = HeightBrush.BASE_NORMALIZED;
        return new HeightWindow { heights = h, x0 = 0, z0 = 0, res = RES, sizeX = SIZE, sizeZ = SIZE };
    }

    private static float Meters(in HeightWindow w, int x, int z) => HeightBrush.MetersFromNormalized(w.heights[z, x]);

    // 10 x 10 square from (8, 8) to (18, 18).
    private static WaterBodyDef SquarePond(float depth = 1f, float bank = 4f, float surfaceY = -0.25f) => new()
    {
        id = "w", kind = "pond", depth = depth, bankWidth = bank, surfaceY = surfaceY, clipToLot = false,
        points = new[] { new[] { 8f, 8f }, new[] { 18f, 8f }, new[] { 18f, 18f }, new[] { 8f, 18f } },
    };

    [Test]
    public void Carve_Pond_ShoreAtOutline_BedInside_BankOutside_BeyondUntouched()
    {
        var w = Flat();
        var pond = SquarePond(depth: 1f, bank: 4f, surfaceY: -0.25f);
        WaterCarve.Carve(ref w, pond, null, 0f, 0f);

        float bed   = -1f;                               // depth below the flat ground height
        float shore = -0.25f - WaterCarve.SHORE_DROP;    // just under the surface
        Assert.AreEqual(bed,   Meters(w, 13, 13), 1e-4f, "center of the pond (deeper than the bank reaches)");
        Assert.AreEqual(shore, Meters(w, 18, 13), 1e-4f, "the outline itself sits just under the surface");
        float innerSlope = Meters(w, 17, 13);   // 1 m inside the x = 18 edge
        Assert.Less(innerSlope, shore, "inner slope is below the shore");
        Assert.Greater(innerSlope, bed, "inner slope is above the bed");
        float corner = Meters(w, 9, 9);          // 1 m inside a corner, on the inner slope
        Assert.Less(corner, shore); Assert.Greater(corner, bed);
        float bankMid = Meters(w, 20, 13);       // 2 m outside the x = 18 edge, halfway across the bank
        Assert.Less(bankMid, 0f, "bank cell is lowered");
        Assert.Greater(bankMid, shore, "bank cell is above the shore");
        Assert.AreEqual(0f, Meters(w, 23, 13), 1e-4f, "beyond the bank is untouched");
        Assert.AreEqual(0f, Meters(w, 2, 2),   1e-4f, "far away is untouched");
    }

    [Test]
    public void Carve_SurfaceLevelWithFlatGround_StillDigsTheBed()
    {
        var w = Flat();
        WaterCarve.Carve(ref w, SquarePond(depth: 2f, bank: 1f, surfaceY: 0f), null, 0f, 0f);
        Assert.AreEqual(-2f, Meters(w, 13, 13), 1e-4f, "bed is depth below flat ground");
        Assert.AreEqual(-WaterCarve.SHORE_DROP, Meters(w, 18, 13), 1e-4f, "shore just under a surface at 0");
        Assert.AreEqual(0f, Meters(w, 20, 13), 1e-4f, "past the 1 m bank");
    }

    [Test]
    public void Carve_SurfaceBelowBed_IsClampedAboveIt()
    {
        var w = Flat();
        // surfaceY -5 with a 1 m bed is impossible; the surface is held MIN_DEPTH above the bed.
        WaterCarve.Carve(ref w, SquarePond(depth: 1f, bank: 1f, surfaceY: -5f), null, 0f, 0f);
        float surface = WaterGeometry.MinSurfaceY(1f);
        Assert.AreEqual(-1f + WaterGeometry.MIN_DEPTH, surface, 1e-6f);
        Assert.Greater(WaterGeometry.MIN_DEPTH, WaterCarve.SHORE_DROP, "the shore drop must fit inside the shallowest water");
        Assert.AreEqual(-1f, Meters(w, 13, 13), 1e-4f);
        float shore = Meters(w, 18, 13);
        Assert.AreEqual(surface - WaterCarve.SHORE_DROP, shore, 1e-4f);
        Assert.Greater(shore, -1f, "shore stays above the bed");
    }

    [Test]
    public void Carve_ZeroBank_IsAPlainStepToTheBed()
    {
        var w = Flat();
        WaterCarve.Carve(ref w, SquarePond(depth: 1f, bank: 0f), null, 0f, 0f);
        Assert.AreEqual(-1f, Meters(w, 13, 13), 1e-4f);
        Assert.AreEqual(-1f, Meters(w, 17, 13), 1e-4f, "no inner slope");
        Assert.AreEqual(0f,  Meters(w, 19, 13), 1e-4f, "no outer bank");
    }

    [Test]
    public void Carve_NeverRaises_KeepsADeeperHandDugSpot()
    {
        var w = Flat();
        w.heights[13, 13] = HeightBrush.NormalizedFromMeters(-5f);
        WaterCarve.Carve(ref w, SquarePond(depth: 1f), null, 0f, 0f);
        Assert.AreEqual(-5f, Meters(w, 13, 13), 1e-4f);
    }

    [Test]
    public void Carve_ClipToLot_LeavesOutsideCellsAlone()
    {
        var w = Flat();
        // Parcel covers x <= 13 only (world = terrain-local here).
        var lot = new[] { new[] { 0f, 0f }, new[] { 13f, 0f }, new[] { 13f, 32f }, new[] { 0f, 32f } };
        WaterCarve.Carve(ref w, SquarePond(depth: 1f, bank: 0f), lot, 0f, 0f);
        Assert.AreEqual(-1f, Meters(w, 10, 13), 1e-4f, "inside pond and inside lot");
        Assert.AreEqual(0f,  Meters(w, 16, 13), 1e-4f, "inside pond but outside lot");
    }

    [Test]
    public void Carve_TerrainOffset_ConvertsWorldPointsToLocal()
    {
        var w = Flat();
        var pond = SquarePond(depth: 1f, bank: 0f);
        // World points are 100 m over; with the terrain parked at x = 100 the pond lands at local 8..18.
        foreach (var p in pond.points) p[0] += 100f;
        WaterCarve.Carve(ref w, pond, null, 100f, 0f);
        Assert.AreEqual(-1f, Meters(w, 13, 13), 1e-4f);
        Assert.AreEqual(0f,  Meters(w, 13, 3),  1e-4f);
    }

    [Test]
    public void Carve_River_DigsAStripOfHalfWidth()
    {
        var w = Flat();
        var river = new WaterBodyDef
        {
            id = "r", kind = "river", width = 4f, smoothing = 0f, depth = 1f, bankWidth = 2f, surfaceY = -0.25f, clipToLot = false,
            points = new[] { new[] { 2f, 16f }, new[] { 30f, 16f } },
        };
        WaterCarve.Carve(ref w, river, null, 0f, 0f);
        float shore = -0.25f - WaterCarve.SHORE_DROP;
        Assert.AreEqual(-1f,   Meters(w, 16, 16), 1e-4f, "centerline (2 m from the edge = the full bank)");
        Assert.AreEqual(shore, Meters(w, 16, 18), 1e-4f, "edge of the ribbon (2 m off center) is the shore");
        float inner = Meters(w, 16, 17);
        Assert.Less(inner, shore); Assert.Greater(inner, -1f);
        float bank = Meters(w, 16, 19);
        Assert.Less(bank, 0f); Assert.Greater(bank, shore);
        Assert.AreEqual(0f, Meters(w, 16, 21), 1e-4f, "past the bank");
        Assert.AreEqual(0f, Meters(w, 16, 4),  1e-4f);
    }

    [Test]
    public void Carve_IgnoresBodiesWithoutGeometry()
    {
        var w = Flat();
        WaterCarve.Carve(ref w, new WaterBodyDef { kind = "pond", points = new[] { new[] { 1f, 1f } } }, null, 0f, 0f);
        WaterCarve.Carve(ref w, null, null, 0f, 0f);
        for (int z = 0; z < RES; z++) for (int x = 0; x < RES; x++)
            Assert.AreEqual(HeightBrush.BASE_NORMALIZED, w.heights[z, x], 1e-6f);
    }
}
