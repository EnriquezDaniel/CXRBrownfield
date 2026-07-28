using System.Collections.Generic;
using UnityEngine;

// Turns a fence centerline (the sparse control points the user drew or the LLM emitted) into an
// ordered list of segment placements — one panel between consecutive samples and a post at each
// sample — that WorldRenderer.RenderFences instantiates. Pure/static (no scene access) so it can be
// unit tested like PathGeometry; the caller supplies the terrain height per placement.
//
// Reuses PathGeometry.Smooth to resample the centerline at ~panelLength spacing, so a fence follows
// curves and corners exactly like a path ribbon does. Operates in XZ meters (Vector2 = (x, z)).
public static class FenceBuilder
{
    public struct Placement
    {
        public Vector2 pos;     // world XZ (post location, or panel midpoint)
        public float   yawDeg;  // rotation about Y so the prefab's modeled +X axis aligns to the run
        public float   span;    // panel length in meters (distance between its two end samples); 0 for posts
        public bool    isPost;  // true ⇒ a post at a joint; false ⇒ a panel spanning to the next joint
    }

    // Build placements along `ctrl` (world XZ control points). `panelLength` is the desired panel /
    // post spacing in meters; `smoothing` (0..1) is fed straight to PathGeometry.Smooth. Returns an
    // empty list when there are fewer than two distinct points.
    public static List<Placement> Build(IReadOnlyList<Vector2> ctrl, float smoothing, float panelLength)
    {
        var outList = new List<Placement>();
        if (ctrl == null || ctrl.Count < 2) return outList;
        if (panelLength <= 0f) panelLength = 2f;

        // roundFit: pick the panel count nearest to segLen/panelLength (min 1) so panels stretch or
        // shrink toward their natural length instead of only compressing — 10 m of 3 m panels gives
        // 3 x 3.33 m, not 4 x 2.5 m. Endpoints/corners still land exactly on the control points.
        var dense = PathGeometry.Smooth(ctrl, smoothing, panelLength, roundFit: true);
        if (dense.Count < 2) return outList;

        for (int i = 0; i < dense.Count - 1; i++)
        {
            Vector2 a = dense[i];
            Vector2 b = dense[i + 1];
            Vector2 d = b - a;
            float span = d.magnitude;
            if (span < 1e-4f) continue;
            float yaw = YawForDirection(d);

            // Post at the start joint of this segment.
            outList.Add(new Placement { pos = a, yawDeg = yaw, span = 0f, isPost = true });
            // Panel spanning a→b, centered at the midpoint.
            outList.Add(new Placement { pos = (a + b) * 0.5f, yawDeg = yaw, span = span, isPost = false });
        }
        // Closing post at the final joint, oriented like the last segment.
        Vector2 last = dense[dense.Count - 1];
        Vector2 prev = dense[dense.Count - 2];
        outList.Add(new Placement { pos = last, yawDeg = YawForDirection(last - prev), span = 0f, isPost = true });

        return outList;
    }

    // Yaw (deg about Y) that maps a prefab modeled along +X onto world direction `dir`. Unity's
    // Y-rotation sends +X=(1,0,0) to (cosθ, 0, -sinθ), so θ = atan2(-dz, dx).
    private static float YawForDirection(Vector2 dir) =>
        Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg;
}
