using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Building signs: the word a placed building carries and where its plate goes. One sign per
// placed building (BuildingInstance.signText, signCompass, signPinned, signHost*), two tiles wide,
// in the top band of one floor, on the building-local wall that points the chosen world compass
// direction once the instance is yawed. A never-edited instance falls back to the def's legacy
// signText / signFace (SpecFor). The spot is either the centred pair on the wall's main run (auto)
// or a pinned pair the user slid it to; a pin that no longer exists, or a wall with no room, falls
// back so the sign never silently vanishes (TryPlace reports which fallback happened).
//
// Pure rules only (no scene objects), so the EditMode tests cover the placement; BuildingSignSpawner
// (Assets/Scripts) turns a Placement into the plate and the TextMeshPro text.
public static class BuildingSigns
{
    public const int   SignTiles = 2;      // tiles the plate spans along the wall
    public const float BandFrac  = 0.35f;  // plate height as a fraction of the cell (1.4 m on a 4 m cell)
    public const float TopMargin = 0.15f;  // metres between the floor's top edge and the plate
    public const int   MaxChars  = 16;     // mirrors server.py BUILDING_SIGN_MAX_CHARS

    // World east. The converted plan has north at +X and west at +Z (LayoutConverter header), so
    // east is -Z. Generated signs face this way.
    public static readonly Vector3 WorldEast = Vector3.back;
    public const string DefaultCompass = "east";

    public static readonly string[] WallFaces = { "north", "east", "south", "west" };
    // World compass points, the order the panel shows them. Same words as the wall faces, but a
    // compass names a world direction and a face names a building-local wall.
    public static readonly string[] Compass = { "north", "east", "south", "west" };

    // Trim, collapse runs of whitespace to one space, uppercase, keep letters, digits, spaces and
    // & ' -, cap at MaxChars. Null when nothing usable is left, which means "no sign" downstream.
    public static string NormalizeText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder();
        bool pendingSpace = false;
        foreach (char c in raw.Trim().ToUpperInvariant())
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (!(char.IsLetterOrDigit(c) || c == '&' || c == '\'' || c == '-')) continue;
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        string s = sb.ToString();
        if (s.Length > MaxChars) s = s.Substring(0, MaxChars);
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    // "north" / "east" / "south" / "west" normalized, or null for anything else.
    public static string NormalizeFace(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().ToLowerInvariant();
        return Array.IndexOf(WallFaces, s) >= 0 ? s : null;
    }

    public static string NormalizeCompass(string raw) => NormalizeFace(raw);

    // World direction a compass word names. North is +X, so east is -Z, south -X, west +Z.
    public static Vector3 CompassDir(string compass) => NormalizeCompass(compass) switch
    {
        "north" => Vector3.right,
        "east"  => Vector3.back,
        "south" => Vector3.left,
        "west"  => Vector3.forward,
        _       => Vector3.zero,
    };

    // The building-local wall face that points closest to worldDir once the building is turned by
    // its instance yaw (degrees about Y). Only the yaw matters; a tilted instance is treated as level.
    public static string FaceTowardWorldDir(float instanceYawDeg, Vector3 worldDir)
    {
        Quaternion yaw = Quaternion.Euler(0f, instanceYawDeg, 0f);
        string best = WallFaces[0];
        float bestDot = float.NegativeInfinity;
        foreach (var face in WallFaces)
        {
            float dot = Vector3.Dot(yaw * TileFaceGeometry.BaselineDir(face), worldDir);
            if (dot > bestDot) { bestDot = dot; best = face; }
        }
        return best;
    }

    // The compass point a building-local wall points closest to once the building is yawed: the
    // inverse of FaceTowardWorldDir. Null for anything that is not a wall face.
    public static string CompassOfFace(string face, float instanceYawDeg)
    {
        string f = NormalizeFace(face);
        if (f == null) return null;
        Vector3 world = Quaternion.Euler(0f, instanceYawDeg, 0f) * TileFaceGeometry.BaselineDir(f);
        string best = Compass[0];
        float bestDot = float.NegativeInfinity;
        foreach (var c in Compass)
        {
            float dot = Vector3.Dot(CompassDir(c), world);
            if (dot > bestDot) { bestDot = dot; best = c; }
        }
        return best;
    }

    // ---- what a building's sign is ----

    // Everything the placement needs, resolved to the building's own frame.
    public struct Spec
    {
        public string text;        // normalized word, null = no sign
        public string face;        // building-local wall, null = none
        public bool   pinned;      // hostX/Z/Floor name tile A of the pair; false = auto spot
        public int    hostX, hostZ, hostFloor;
    }

    // The sign an instance shows. Instance fields win once the instance has a compass; a
    // never-edited instance shows its def's legacy sign at the auto spot; else no sign.
    public static Spec SpecFor(BuildingInstance inst, BuildingDef def)
    {
        string compass = inst != null ? NormalizeCompass(inst.signCompass) : null;
        if (compass != null)
        {
            return new Spec
            {
                text      = NormalizeText(inst.signText),
                face      = FaceTowardWorldDir(inst.rotationY, CompassDir(compass)),
                pinned    = inst.signPinned,
                hostX     = inst.signHostX,
                hostZ     = inst.signHostZ,
                hostFloor = inst.signHostFloor,
            };
        }
        if (def != null && NormalizeText(def.signText) != null)
            return new Spec { text = NormalizeText(def.signText), face = NormalizeFace(def.signFace) };
        return default;
    }

    // The compass the panel shows: the instance's own, else the legacy def wall turned by the yaw,
    // else the default east.
    public static string EffectiveCompass(BuildingInstance inst, BuildingDef def)
    {
        string compass = inst != null ? NormalizeCompass(inst.signCompass) : null;
        if (compass != null) return compass;
        if (def != null && NormalizeText(def.signText) != null)
        {
            string fromFace = CompassOfFace(def.signFace, inst != null ? inst.rotationY : 0f);
            if (fromFace != null) return fromFace;
        }
        return DefaultCompass;
    }

    // ---- where it can go ----

    // One place a plate can sit: two adjacent exposed tiles on one floor of one wall.
    public struct Slot
    {
        public int     floor;
        public int     side;      // coordinate across the wall (Z for north/south, X for east/west)
        public int     along;     // coordinate of tile A along the wall; B is along + 1
        public TileDef a, b;      // the two host tiles, along ascending
        public TileFaceGeometry.FaceFrame frameA, frameB;
        public Vector3 center;    // building-local midpoint of the two face centres
    }

    // Where the plate sits, in building-local metres. `right` runs along the wall from the first
    // tile to the second; `up` is the wall's in-plane up; `normal` points out of the wall.
    public struct Placement
    {
        public Vector3 center;
        public Vector3 normal;
        public Vector3 up;
        public Vector3 right;
        public float   width;
        public float   height;
        public int     floor;        // the floor the plate hangs on
        public TileDef tileA;        // the two host tiles, in `right` order
        public TileDef tileB;
        public string  face;         // the wall actually used (differs from the spec's on a fallback)
        public bool    pinLost;      // the pinned pair no longer exists, so the auto spot is shown
        public bool    faceFallback; // the chosen wall had no room, so another wall is shown
    }

    public enum Skip { None, NoText, NoFace, TooNarrow }

    // Exposed tiles with a face pointing out of `face`, grouped by (floor, side) and listed along
    // the wall. The raw material for both the auto rule and the slot list.
    private static Dictionary<(int floor, int side), List<(int along, TileDef tile, string faceName)>>
        ExposedRows(BuildingDef def, string face, float cellSize, Func<string, TileFit> fitFor, int? onlyFloor)
    {
        var rows = new Dictionary<(int floor, int side), List<(int along, TileDef tile, string faceName)>>();
        if (def?.tiles == null) return rows;
        Vector3 dir = TileFaceGeometry.BaselineDir(face);
        bool alongX = Mathf.Abs(dir.z) > Mathf.Abs(dir.x);
        foreach (var t in def.tiles)
        {
            if (t == null) continue;
            if (onlyFloor.HasValue && t.floor != onlyFloor.Value) continue;
            string faceName = TileFaces.FacePointing(t, dir);
            if (faceName == null) continue;
            if (!TileFaces.IsExposed(def.tiles, t, dir, cellSize, fitFor)) continue;
            int side  = alongX ? t.gridZ : t.gridX;
            int along = alongX ? t.gridX : t.gridZ;
            var key = (t.floor, side);
            if (!rows.TryGetValue(key, out var list)) rows[key] = list = new();
            list.Add((along, t, faceName));
        }
        foreach (var list in rows.Values) list.Sort((x, y) => x.along.CompareTo(y.along));
        return rows;
    }

    private static bool TryMakeSlot(int floor, int side,
                                    (int along, TileDef tile, string faceName) a,
                                    (int along, TileDef tile, string faceName) b,
                                    float cellSize, Func<string, TileFit> fitFor, out Slot slot)
    {
        slot = default;
        TileFit fitA = fitFor != null ? fitFor(a.tile.shapeId) : TileFit.Full;
        TileFit fitB = fitFor != null ? fitFor(b.tile.shapeId) : TileFit.Full;
        if (!TileFaceGeometry.TryGetFaceFrame(a.tile, a.faceName, cellSize, fitA, out var fa)) return false;
        if (!TileFaceGeometry.TryGetFaceFrame(b.tile, b.faceName, cellSize, fitB, out var fb)) return false;
        slot = new Slot
        {
            floor = floor, side = side, along = a.along,
            a = a.tile, b = b.tile, frameA = fa, frameB = fb,
            center = (fa.center + fb.center) * 0.5f,
        };
        return true;
    }

    // Every place the plate can sit on `face`, on every floor, ordered by floor, side, along.
    public static List<Slot> Slots(BuildingDef def, string face, float cellSize, Func<string, TileFit> fitFor)
    {
        var result = new List<Slot>();
        string f = NormalizeFace(face);
        if (f == null || cellSize <= 0f) return result;
        var rows = ExposedRows(def, f, cellSize, fitFor, null);
        var keys = new List<(int floor, int side)>(rows.Keys);
        keys.Sort((x, y) => x.floor != y.floor ? x.floor.CompareTo(y.floor) : x.side.CompareTo(y.side));
        foreach (var key in keys)
        {
            var row = rows[key];
            for (int i = 0; i + 1 < row.Count; i++)
            {
                if (row[i + 1].along != row[i].along + 1) continue;
                if (TryMakeSlot(key.floor, key.side, row[i], row[i + 1], cellSize, fitFor, out var slot))
                    result.Add(slot);
            }
        }
        return result;
    }

    // The generated spot: the building's lowest floor, the side with the most exposed tiles (the
    // main wall on that side, lowest coordinate on ties), the longest contiguous run inside it,
    // the centred pair (left-biased on odd runs).
    public static bool TryAutoSlot(BuildingDef def, string face, float cellSize, Func<string, TileFit> fitFor, out Slot slot)
    {
        slot = default;
        string f = NormalizeFace(face);
        if (f == null || cellSize <= 0f || def?.tiles == null || def.tiles.Count == 0) return false;

        int floor = int.MaxValue;
        foreach (var t in def.tiles) if (t != null && t.floor < floor) floor = t.floor;
        var rows = ExposedRows(def, f, cellSize, fitFor, floor);
        if (rows.Count == 0) return false;

        int bestSide = 0, bestCount = -1;
        foreach (var kv in rows)
            if (kv.Value.Count > bestCount || (kv.Value.Count == bestCount && kv.Key.side < bestSide))
            { bestSide = kv.Key.side; bestCount = kv.Value.Count; }
        var row = rows[(floor, bestSide)];

        int runStart = 0, runLen = 0, bestStart = 0, bestLen = 0;
        for (int i = 0; i < row.Count; i++)
        {
            if (i > 0 && row[i].along == row[i - 1].along + 1) runLen++;
            else { runStart = i; runLen = 1; }
            if (runLen > bestLen) { bestLen = runLen; bestStart = runStart; }
        }
        if (bestLen < SignTiles) return false;

        int first = bestStart + (bestLen - SignTiles) / 2;   // centred, left-biased on odd runs
        return TryMakeSlot(floor, bestSide, row[first], row[first + 1], cellSize, fitFor, out slot);
    }

    public static bool SameSlot(Slot x, Slot y) =>
        x.floor == y.floor && x.side == y.side && x.along == y.along;

    // The slot whose first tile is (hostX, hostZ) on `floor`, if the wall still has it.
    public static bool TryFindSlot(List<Slot> slots, int floor, int hostX, int hostZ, out Slot slot)
    {
        slot = default;
        if (slots == null) return false;
        foreach (var s in slots)
            if (s.floor == floor && s.a != null && s.a.gridX == hostX && s.a.gridZ == hostZ) { slot = s; return true; }
        return false;
    }

    // The slot whose centre is closest to a building-local point (a drag hit on the wall plane).
    public static Slot Nearest(List<Slot> slots, Vector3 localPoint)
    {
        Slot best = default;
        float bestD = float.PositiveInfinity;
        if (slots == null) return best;
        foreach (var s in slots)
        {
            float d = (s.center - localPoint).sqrMagnitude;
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    // One nudge. dRight steps toward the viewer's right when facing the wall from outside (so
    // "Right" on the panel moves the plate right on screen for someone looking at it); dFloor
    // steps up or down a floor, keeping the position along the wall when that pair exists and
    // taking the closest pair on that floor otherwise. Returns `current` when nothing valid exists.
    public static Slot Step(BuildingDef def, string face, Slot current, int dRight, int dFloor,
                            float cellSize, Func<string, TileFit> fitFor)
    {
        string f = NormalizeFace(face);
        if (f == null) return current;
        var slots = Slots(def, f, cellSize, fitFor);
        if (slots.Count == 0) return current;

        if (dFloor != 0)
        {
            int target = current.floor + dFloor;
            bool any = false;
            Slot best = default;
            float bestD = float.PositiveInfinity;
            foreach (var s in slots)
            {
                if (s.floor != target) continue;
                if (s.side == current.side && s.along == current.along) return s;
                float d = (s.center - current.center).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; any = true; }
            }
            return any ? best : current;
        }

        if (dRight != 0)
        {
            Vector3 normal      = TileFaceGeometry.BaselineDir(f);
            bool    alongX      = Mathf.Abs(normal.z) > Mathf.Abs(normal.x);
            Vector3 alongAxis   = alongX ? Vector3.right : Vector3.forward;
            Vector3 viewerRight = Vector3.Cross(Vector3.up, -normal);
            int sign   = Vector3.Dot(viewerRight, alongAxis) >= 0f ? 1 : -1;
            int target = current.along + dRight * sign;
            foreach (var s in slots)
                if (s.floor == current.floor && s.side == current.side && s.along == target) return s;
        }
        return current;
    }

    // ---- where it goes ----

    private static Placement PlacementFor(Slot s, float cellSize, string face)
    {
        float band = BandFrac * cellSize;
        var fa = s.frameA;
        var fb = s.frameB;
        Vector3 up = (fa.up + fb.up).normalized;
        // Top band: the lower of the two safe top lines, less the margin, less half the plate.
        float uTop = Mathf.Min(fa.uTop, fb.uTop);
        return new Placement
        {
            center = s.center + up * (uTop - TopMargin - band * 0.5f),
            normal = (fa.normal + fb.normal).normalized,
            up     = up,
            right  = (fb.center - fa.center).normalized,
            width  = fa.width + fb.width,
            height = band,
            floor  = s.floor,
            tileA  = s.a,
            tileB  = s.b,
            face   = face,
        };
    }

    // Resolves the plate for a spec. Order: the pinned pair while it still exists → the auto spot
    // on the spec's wall → the auto spot on another wall → TooNarrow (the sign stays stored but
    // shows nothing). The fallbacks are reported on the Placement so the panel can say so.
    public static Skip TryPlace(BuildingDef def, Spec spec, float cellSize, Func<string, TileFit> fitFor, out Placement p)
    {
        p = default;
        if (def == null || cellSize <= 0f) return Skip.NoText;
        if (NormalizeText(spec.text) == null) return Skip.NoText;
        string face = NormalizeFace(spec.face);
        if (face == null) return Skip.NoFace;
        if (def.tiles == null || def.tiles.Count == 0) return Skip.TooNarrow;

        bool pinLost = false, faceFallback = false;
        string used = face;
        Slot slot = default;
        bool found = false;

        if (spec.pinned)
        {
            found = TryFindSlot(Slots(def, face, cellSize, fitFor), spec.hostFloor, spec.hostX, spec.hostZ, out slot);
            pinLost = !found;
        }
        if (!found) found = TryAutoSlot(def, face, cellSize, fitFor, out slot);
        if (!found)
        {
            foreach (var other in WallFaces)
            {
                if (other == face) continue;
                if (TryAutoSlot(def, other, cellSize, fitFor, out slot)) { found = true; used = other; faceFallback = true; break; }
            }
        }
        if (!found) return Skip.TooNarrow;

        p = PlacementFor(slot, cellSize, used);
        p.pinLost      = pinLost;
        p.faceFallback = faceFallback;
        return Skip.None;
    }

    // Legacy entry: the def's own signText / signFace at the auto spot (records saved before signs
    // moved to the instance, and the tests that pin down the generated placement).
    public static Skip TryPlace(BuildingDef def, float cellSize, Func<string, TileFit> fitFor, out Placement p) =>
        TryPlace(def, SpecFor(null, def), cellSize, fitFor, out p);
}
