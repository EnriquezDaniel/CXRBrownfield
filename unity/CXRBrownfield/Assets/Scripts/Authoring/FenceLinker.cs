using System;
using System.Collections.Generic;
using UnityEngine;

// Links fences into a network at commit time: where a newly drawn centerline T-joins or crosses an
// existing fence's control polyline, both are split so every junction becomes a shared run endpoint
// (FenceBuilder posts every run end, so a post lands exactly on the junction). Pure/static like
// FenceBuilder/PathGeometry so it can be unit tested without a scene. Operates on the sparse control
// polylines in XZ meters (Vector2 = (x, z)).
//
// Rules: a junction within Eps of an existing control point welds onto that point (never inserts a
// near-duplicate); a weld onto a polyline's own first/last point is a shared corner, not a split;
// cuts that would leave a piece shorter than MinSeg are skipped (no sliver fences); parallel or
// collinear contact is deliberately not a junction. Splits operate on the control polyline, so for
// smoothing > 0 the rendered curve near a cut changes slightly (fences default to smoothing 0).
public static class FenceLinker
{
    public const float Eps    = 0.05f; // junction weld epsilon (m)
    public const float MinSeg = 0.5f;  // pieces shorter than this are slivers and are not created (m)

    // Sine of the minimum contact angle for an endpoint T-junction; shallower is treated as
    // collinear overlap (drawing a fence along another one should not chop the old one up).
    private const float MinTJunctionSin = 0.1f;   // ≈ 5.7°

    // A pending split on a polyline: on control segment `seg` at parametric `t`, at world point `p`.
    private struct Cut
    {
        public int     seg;
        public float   t;
        public Vector2 p;
    }

    // A resolved split boundary: arc-length position along the polyline + the exact junction point.
    private struct Boundary
    {
        public float   arc;
        public Vector2 p;
    }

    // Junction points where `ctrl` would be split by Link (crossings with any fence), ordered along
    // `ctrl`. Used by the draw-mode ghost so the preview shows the same posts the commit creates.
    public static List<Vector2> FindCuts(IReadOnlyList<FenceDef> fences, IReadOnlyList<Vector2> ctrl,
                                         float eps = Eps, float minSeg = MinSeg)
    {
        var outPts = new List<Vector2>();
        var pts = Normalize(ctrl, eps);
        if (fences == null || pts.Count < 2) return outPts;

        var raw = new List<Cut>();
        foreach (var f in fences)
        {
            var poly = PolyOf(f);
            if (poly.Count < 2) continue;
            CollectCrossings(pts, poly, eps, raw, null);
        }
        foreach (var b in PrepareCuts(pts, raw, eps, minSeg)) outPts.Add(b.p);
        return outPts;
    }

    // Mutates `fences`: splits existing fences where `newCtrl` T-joins or crosses them, splits the
    // new run at each crossing, and appends the resulting run(s) (copying the given attributes).
    // Returns the appended defs (empty when newCtrl is degenerate — then nothing is mutated). The
    // caller wraps the whole call in one undo snapshot.
    public static List<FenceDef> Link(List<FenceDef> fences, IReadOnlyList<Vector2> newCtrl,
                                      string fenceType, float smoothing, float height,
                                      float eps = Eps, float minSeg = MinSeg)
    {
        var added = new List<FenceDef>();
        var pts = Normalize(newCtrl, eps);
        if (fences == null || pts.Count < 2) return added;

        Vector2 startDir = (pts[1] - pts[0]).normalized;
        Vector2 endDir   = (pts[pts.Count - 1] - pts[pts.Count - 2]).normalized;

        var newCuts = new List<Cut>();
        for (int fi = 0; fi < fences.Count; fi++)
        {
            var f = fences[fi];
            var poly = PolyOf(f);
            if (poly.Count < 2) continue;

            var fenceCuts = new List<Cut>();
            CollectCrossings(pts, poly, eps, newCuts, fenceCuts);
            // T-endpoint pass: robustness for a new endpoint that stops just short of the fence
            // line (the crossing pass already catches an endpoint that lands on it).
            AddEndpointCut(pts[0],              startDir, poly, eps, fenceCuts);
            AddEndpointCut(pts[pts.Count - 1],  endDir,   poly, eps, fenceCuts);

            var pieces = SplitAtCuts(poly, fenceCuts, eps, minSeg);
            if (pieces == null) continue;

            // Replace the fence in place with its pieces: the first keeps the original id (list
            // order and any selection stay sane), the rest get fresh ids; all copy the attributes.
            var repl = new List<FenceDef>(pieces.Count);
            for (int k = 0; k < pieces.Count; k++)
                repl.Add(new FenceDef
                {
                    id        = k == 0 ? f.id : Guid.NewGuid().ToString("D"),
                    fenceType = f.fenceType,
                    smoothing = f.smoothing,
                    height    = f.height,
                    points    = ToJagged(pieces[k]),
                });
            fences.RemoveAt(fi);
            fences.InsertRange(fi, repl);
            fi += repl.Count - 1;
        }

        var newPieces = SplitAtCuts(pts, newCuts, eps, minSeg) ?? new List<List<Vector2>> { pts };
        foreach (var piece in newPieces)
        {
            var def = new FenceDef
            {
                id        = Guid.NewGuid().ToString("D"),
                fenceType = fenceType,
                smoothing = smoothing,
                height    = height,
                points    = ToJagged(piece),
            };
            fences.Add(def);
            added.Add(def);
        }
        return added;
    }

    // ---- cut collection ----

    // Proper segment × segment crossings between polylines `a` and `b`; each hit is recorded on
    // both sides (either list may be null). The junction point is welded onto a nearby vertex of
    // either polyline so both sides split at the identical point.
    private static void CollectCrossings(List<Vector2> a, List<Vector2> b, float eps,
                                         List<Cut> cutsA, List<Cut> cutsB)
    {
        for (int i = 0; i < a.Count - 1; i++)
            for (int j = 0; j < b.Count - 1; j++)
            {
                if (!SegIntersect(a[i], a[i + 1], b[j], b[j + 1], eps, out float t, out float u, out Vector2 p))
                    continue;
                p = WeldToVertex(p, b, eps);
                p = WeldToVertex(p, a, eps);
                cutsA?.Add(new Cut { seg = i, t = t, p = p });
                cutsB?.Add(new Cut { seg = j, t = u, p = p });
            }
    }

    // Parametric intersection of a1→a2 and b1→b2. Parallel/collinear ⇒ no crossing. The [0,1]
    // ranges are padded by eps (in meters) so runs that stop a hair short of a fence still join.
    private static bool SegIntersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2, float eps,
                                     out float t, out float u, out Vector2 p)
    {
        t = 0f; u = 0f; p = default;
        Vector2 r = a2 - a1, s = b2 - b1;
        float rLen = r.magnitude, sLen = s.magnitude;
        if (rLen < 1e-6f || sLen < 1e-6f) return false;
        float denom = r.x * s.y - r.y * s.x;
        if (Mathf.Abs(denom) < 1e-9f) return false;

        Vector2 d = b1 - a1;
        t = (d.x * s.y - d.y * s.x) / denom;
        u = (d.x * r.y - d.y * r.x) / denom;
        float tPad = eps / rLen, uPad = eps / sLen;
        if (t < -tPad || t > 1f + tPad || u < -uPad || u > 1f + uPad) return false;

        t = Mathf.Clamp01(t);
        u = Mathf.Clamp01(u);
        p = a1 + t * r;
        return true;
    }

    // T-junction: project a new-run endpoint onto every fence segment; a contact within eps cuts
    // the fence there. Near-parallel contact is skipped — that's overlap, not a junction.
    private static void AddEndpointCut(Vector2 endpoint, Vector2 endDir, List<Vector2> poly,
                                       float eps, List<Cut> cuts)
    {
        for (int j = 0; j < poly.Count - 1; j++)
        {
            Vector2 ab = poly[j + 1] - poly[j];
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-9f) continue;
            float abLen = Mathf.Sqrt(len2);
            if (Mathf.Abs(endDir.x * ab.y - endDir.y * ab.x) / abLen < MinTJunctionSin) continue;

            float t = Mathf.Clamp01(Vector2.Dot(endpoint - poly[j], ab) / len2);
            Vector2 proj = poly[j] + t * ab;
            if (Vector2.Distance(endpoint, proj) <= eps)
                cuts.Add(new Cut { seg = j, t = t, p = WeldToVertex(proj, poly, eps) });
        }
    }

    private static Vector2 WeldToVertex(Vector2 p, List<Vector2> poly, float eps)
    {
        float best = eps; Vector2 welded = p;
        foreach (var v in poly)
        {
            float d = Vector2.Distance(p, v);
            if (d <= best) { best = d; welded = v; }
        }
        return welded;
    }

    // ---- splitting ----

    // Resolve raw cuts against `poly`: weld to vertices (a weld onto the first/last point is a
    // shared corner and is discarded), sort along the polyline, merge near-duplicates, and skip
    // cuts that would leave a piece shorter than minSeg. Returns the surviving boundaries in order.
    private static List<Boundary> PrepareCuts(List<Vector2> poly, List<Cut> raw, float eps, float minSeg)
    {
        var outList = new List<Boundary>();
        if (raw == null || raw.Count == 0 || poly.Count < 2) return outList;

        int n = poly.Count;
        var cum = new float[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Vector2.Distance(poly[i - 1], poly[i]);
        float total = cum[n - 1];

        var welded = new List<Boundary>();
        foreach (var c in raw)
        {
            if (c.seg < 0 || c.seg >= n - 1) continue;
            float arc = cum[c.seg] + Mathf.Clamp01(c.t) * (cum[c.seg + 1] - cum[c.seg]);
            Vector2 p = c.p;

            int bestV = -1; float bestD = eps;
            for (int v = 0; v < n; v++)
            {
                float d = Vector2.Distance(p, poly[v]);
                if (d <= bestD) { bestD = d; bestV = v; }
            }
            if (bestV == 0 || bestV == n - 1) continue;   // shared corner — no split
            if (bestV > 0) { p = poly[bestV]; arc = cum[bestV]; }

            welded.Add(new Boundary { arc = arc, p = p });
        }
        if (welded.Count == 0) return outList;
        welded.Sort((x, y) => x.arc.CompareTo(y.arc));

        float prev = 0f;
        foreach (var b in welded)
        {
            if (outList.Count > 0 && b.arc - outList[outList.Count - 1].arc <= eps) continue; // duplicate junction
            if (b.arc - prev < minSeg) continue;          // would leave a sliver behind it
            if (total - b.arc < minSeg) continue;         // would leave a sliver ahead of it
            outList.Add(b);
            prev = b.arc;
        }
        return outList;
    }

    // Split `poly` at the surviving cut boundaries. Each boundary point ends one piece and starts
    // the next. Returns null when no split applies (the polyline stays as it is).
    private static List<List<Vector2>> SplitAtCuts(List<Vector2> poly, List<Cut> raw, float eps, float minSeg)
    {
        var bounds = PrepareCuts(poly, raw, eps, minSeg);
        if (bounds.Count == 0) return null;

        int n = poly.Count;
        var cum = new float[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Vector2.Distance(poly[i - 1], poly[i]);

        var pieces = new List<List<Vector2>>();
        var cur = new List<Vector2> { poly[0] };
        int bi = 0;
        for (int i = 0; i < n - 1; i++)
        {
            while (bi < bounds.Count && bounds[bi].arc <= cum[i + 1] + 1e-5f)
            {
                Vector2 p = bounds[bi].p;
                if (Vector2.Distance(cur[cur.Count - 1], p) > 1e-5f) cur.Add(p);
                pieces.Add(cur);
                cur = new List<Vector2> { p };
                bi++;
            }
            if (Vector2.Distance(cur[cur.Count - 1], poly[i + 1]) > 1e-5f) cur.Add(poly[i + 1]);
        }
        pieces.Add(cur);

        pieces.RemoveAll(pc => pc.Count < 2);
        return pieces.Count >= 2 ? pieces : null;
    }

    // ---- conversions ----

    private static List<Vector2> PolyOf(FenceDef f)
    {
        var poly = new List<Vector2>();
        if (f?.points == null) return poly;
        foreach (var p in f.points)
            if (p != null && p.Length >= 2) poly.Add(new Vector2(p[0], p[1]));
        return poly;
    }

    private static List<Vector2> Normalize(IReadOnlyList<Vector2> pts, float eps)
    {
        var outPts = new List<Vector2>();
        if (pts == null) return outPts;
        foreach (var p in pts)
            if (outPts.Count == 0 || Vector2.Distance(outPts[outPts.Count - 1], p) > eps) outPts.Add(p);
        return outPts;
    }

    private static float[][] ToJagged(List<Vector2> pts)
    {
        var outPts = new float[pts.Count][];
        for (int i = 0; i < pts.Count; i++) outPts[i] = new[] { pts[i].x, pts[i].y };
        return outPts;
    }
}
