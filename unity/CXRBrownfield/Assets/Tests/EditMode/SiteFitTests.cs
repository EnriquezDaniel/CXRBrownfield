using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

[TestFixture]
public class SiteFitTests
{
    // A parcel well away from the origin: bbox [200,300]..[320,380] (120 x 80 m).
    private static float[][] Boundary() => new[]
    {
        new[] { 200f, 300f },
        new[] { 320f, 300f },
        new[] { 320f, 380f },
        new[] { 200f, 380f },
    };

    private static EnvironmentDef SmallChild(float w = 120f, float l = 80f)
    {
        return new EnvironmentDef
        {
            id   = "child",
            name = "child",
            site = new SiteDef
            {
                terrainSize  = new[] { w, l },
                terrainZones = new List<TerrainZoneDef>
                {
                    new TerrainZoneDef { terrainType = "grass", rectMeters = new[] { 10f, 10f, 50f, 30f } },
                },
                paths = new List<PathDef>
                {
                    new PathDef { id = "p1", width = 3f, points = new[] { new[] { 0f, 0f }, new[] { 60f, 40f } } },
                },
                surfaceStrokes = new List<SurfaceStrokeDef>(),
                lotBoundary    = new[] { new[] { 0f, 0f }, new[] { 120f, 0f }, new[] { 120f, 80f }, new[] { 0f, 80f } },
            },
            buildingInstances = new List<BuildingInstance>
            {
                new BuildingInstance { instanceId = "b1", buildingId = "def1", position = new[] { 60f, 0f, 40f }, scale = 1f },
            },
            objectInstances = new List<ObjectInstance>
            {
                new ObjectInstance { instanceId = "o1", prefabType = "tree", position = new[] { 30f, 0f, 20f }, scale = 1f },
            },
        };
    }

    // ---- BoundaryBounds ----

    [Test]
    public void BoundaryBounds_ReturnsBbox()
    {
        Assert.IsTrue(SiteFit.BoundaryBounds(Boundary(), out float minX, out float minZ, out float maxX, out float maxZ));
        Assert.AreEqual(200f, minX, 1e-4f);
        Assert.AreEqual(300f, minZ, 1e-4f);
        Assert.AreEqual(320f, maxX, 1e-4f);
        Assert.AreEqual(380f, maxZ, 1e-4f);
    }

    [Test]
    public void BoundaryBounds_RejectsDegenerates()
    {
        Assert.IsFalse(SiteFit.BoundaryBounds(null, out _, out _, out _, out _));
        Assert.IsFalse(SiteFit.BoundaryBounds(new[] { new[] { 0f, 0f }, new[] { 10f, 10f } }, out _, out _, out _, out _));
        // Collinear on X: zero-width bbox.
        Assert.IsFalse(SiteFit.BoundaryBounds(new[] { new[] { 5f, 0f }, new[] { 5f, 10f }, new[] { 5f, 20f } }, out _, out _, out _, out _));
    }

    // ---- TryComputeFit ----

    [Test]
    public void TryComputeFit_MapsChildRectOntoBbox()
    {
        Assert.IsTrue(SiteFit.TryComputeFit(Boundary(), 60f, 40f, out var fit));
        // Child (0,0) -> bbox min corner.
        Assert.AreEqual(200f, fit.offsetX + 0f * fit.scaleX, 1e-3f);
        Assert.AreEqual(300f, fit.offsetZ + 0f * fit.scaleZ, 1e-3f);
        // Child (60,40) -> bbox max corner.
        Assert.AreEqual(320f, fit.offsetX + 60f * fit.scaleX, 1e-3f);
        Assert.AreEqual(380f, fit.offsetZ + 40f * fit.scaleZ, 1e-3f);
        // Child center -> bbox center.
        Assert.AreEqual(260f, fit.offsetX + 30f * fit.scaleX, 1e-3f);
        Assert.AreEqual(340f, fit.offsetZ + 20f * fit.scaleZ, 1e-3f);
    }

    [Test]
    public void TryComputeFit_RejectsBadInput()
    {
        Assert.IsFalse(SiteFit.TryComputeFit(null, 60f, 40f, out _));
        Assert.IsFalse(SiteFit.TryComputeFit(Boundary(), 0f, 40f, out _));
        Assert.IsFalse(SiteFit.TryComputeFit(Boundary(), -5f, 40f, out _));
        // Scale outside the sanity clamp: child 100000 m wide into a 120 m site.
        Assert.IsFalse(SiteFit.TryComputeFit(Boundary(), 100000f, 40f, out _));
    }

    // ---- BoundaryToCanvas / SiteDimsFeet ----

    [Test]
    public void BoundaryToCanvas_NormalizesIntoOwnBbox()
    {
        var canvas = SiteFit.BoundaryToCanvas(Boundary());
        Assert.IsNotNull(canvas);
        Assert.AreEqual(4, canvas.Length);
        Assert.AreEqual(0f,    canvas[0][0], 0.5f);   // (200,300) -> (0,0)
        Assert.AreEqual(0f,    canvas[0][1], 0.5f);
        Assert.AreEqual(1000f, canvas[2][0], 0.5f);   // (320,380) -> (1000,1000)
        Assert.AreEqual(1000f, canvas[2][1], 0.5f);
        foreach (var p in canvas)
        {
            Assert.GreaterOrEqual(p[0], 0f); Assert.LessOrEqual(p[0], 1000f);
            Assert.GreaterOrEqual(p[1], 0f); Assert.LessOrEqual(p[1], 1000f);
        }
    }

    [Test]
    public void BoundaryToCanvas_NullForDegenerate()
    {
        Assert.IsNull(SiteFit.BoundaryToCanvas(null));
        Assert.IsNull(SiteFit.BoundaryToCanvas(new[] { new[] { 0f, 0f }, new[] { 10f, 0f } }));
    }

    [Test]
    public void SiteDimsFeet_BboxExtentsInFeet()
    {
        Assert.IsTrue(SiteFit.SiteDimsFeet(Boundary(), out float wFt, out float hFt));
        Assert.AreEqual(120f / AuthoringConventions.FT_TO_M, wFt, 0.01f);
        Assert.AreEqual(80f  / AuthoringConventions.FT_TO_M, hFt, 0.01f);
    }

    // ---- Round-trip pin of the canvas convention (D4) ----
    // Drawn boundary -> request fields -> LayoutConverter must reproduce the same shape translated
    // to the origin. Any hand-rolled transpose elsewhere would mirror layouts; this test pins it.

    [Test]
    public void RoundTrip_BoundaryThroughLayoutConverter_ReproducesShapeAtOrigin()
    {
        // Irregular pentagon so a transpose bug cannot cancel out.
        var boundary = new[]
        {
            new[] { 210f, 305f },
            new[] { 315f, 300f },
            new[] { 320f, 370f },
            new[] { 260f, 380f },
            new[] { 200f, 350f },
        };
        var canvas = SiteFit.BoundaryToCanvas(boundary);
        Assert.IsTrue(SiteFit.SiteDimsFeet(boundary, out float wFt, out float hFt));
        Assert.IsTrue(SiteFit.BoundaryBounds(boundary, out float minX, out float minZ, out _, out _));

        var data = new FullTerrainData
        {
            site_scale = new SiteScale
            {
                normalized_canvas = new[] { 0, 0, 1000, 1000 },
                site_width_ft  = wFt,
                site_height_ft = hFt,
                lot_boundary   = canvas,
            },
            terrain_zones       = new List<TerrainZone>(),
            generated_buildings = new List<GeneratedBuilding>(),
            generated_objects   = new List<GeneratedObject>(),
            prefab_instances    = new List<PrefabInstance>(),
        };
        var result = LayoutConverter.Convert(data, "roundtrip");
        var got = result.Environment.site.lotBoundary;
        Assert.IsNotNull(got);
        Assert.AreEqual(boundary.Length, got.Length);

        // Canvas points are rounded to whole units: worst case 0.5/1000 of the bbox extent per axis.
        const float tol = 0.15f;
        for (int i = 0; i < boundary.Length; i++)
        {
            Assert.AreEqual(boundary[i][0] - minX, got[i][0], tol, $"vertex {i} X");
            Assert.AreEqual(boundary[i][1] - minZ, got[i][1], tol, $"vertex {i} Z");
        }
    }

    // ---- ProjectIntoSite ----

    [Test]
    public void ProjectIntoSite_LandsContentInsideBbox()
    {
        var child = SmallChild();   // same aspect as the site bbox: uniform fit
        Assert.IsTrue(SiteFit.ProjectIntoSite(child, Boundary()));

        // Building at child (60,40) = child center -> site bbox center (260,340).
        var b = child.buildingInstances[0];
        Assert.AreEqual(260f, b.position[0], 1e-3f);
        Assert.AreEqual(340f, b.position[2], 1e-3f);

        // Object at (30,20) -> (230,320).
        var o = child.objectInstances[0];
        Assert.AreEqual(230f, o.position[0], 1e-3f);
        Assert.AreEqual(320f, o.position[2], 1e-3f);

        // Zone rect and path points inside the bbox.
        var z = child.site.terrainZones[0].rectMeters;
        Assert.GreaterOrEqual(z[0], 200f); Assert.LessOrEqual(z[2], 320f);
        Assert.GreaterOrEqual(z[1], 300f); Assert.LessOrEqual(z[3], 380f);
        foreach (var p in child.site.paths[0].points)
        {
            Assert.GreaterOrEqual(p[0], 200f); Assert.LessOrEqual(p[0], 320f);
            Assert.GreaterOrEqual(p[1], 300f); Assert.LessOrEqual(p[1], 380f);
        }

        // Child lot boundary maps onto the site bbox corners.
        var lot = child.site.lotBoundary;
        Assert.AreEqual(200f, lot[0][0], 1e-3f);
        Assert.AreEqual(300f, lot[0][1], 1e-3f);
        Assert.AreEqual(320f, lot[2][0], 1e-3f);
        Assert.AreEqual(380f, lot[2][1], 1e-3f);

        // terrainSize scaled to the bbox extents (a scale, not a move, so it does change).
        Assert.AreEqual(120f, child.site.terrainSize[0], 1e-3f);
        Assert.AreEqual(80f,  child.site.terrainSize[1], 1e-3f);
    }

    [Test]
    public void ProjectIntoSite_RejectsDegenerates()
    {
        Assert.IsFalse(SiteFit.ProjectIntoSite(null, Boundary()));
        Assert.IsFalse(SiteFit.ProjectIntoSite(new EnvironmentDef(), Boundary()));
        Assert.IsFalse(SiteFit.ProjectIntoSite(SmallChild(), null));
    }
}
