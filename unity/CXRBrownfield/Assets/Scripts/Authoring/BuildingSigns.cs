using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Building signs: the words a placed building carries and where their plates go. A building holds a
// list of signs, one per tenant (BuildingInstance.signs, signCompass). Every plate is as wide as its
// word at one shared text size and hangs in the top band of a floor. The row starts on the
// building-local wall that points the chosen world compass direction once the instance is yawed,
// shares each open stretch of wall in proportion to plate width, and spills around the building
// when the wall is full (Layout). Any sign can be pinned to a half-tile spot on any wall; the rest
// flow around it. Records saved with the older single-sign fields, or with the def's legacy
// signText / signFace, read as a one-entry list (EntriesFor) until the first edit (Adopt).
//
// Pure rules only (no scene objects), so the EditMode tests cover the placement; BuildingSignSpawner
// (Assets/Scripts) turns the Placements into plates and TextMeshPro text.
public static class BuildingSigns
{
    public const float BandFrac  = 0.35f;  // plate height as a fraction of the cell (1.4 m on a 4 m cell)
    public const float TopMargin = 0.15f;  // metres between the floor's top edge and the plate
    public const int   MaxChars  = 16;     // mirrors server.py BUILDING_SIGN_MAX_CHARS

    // Plate sizing, as fractions of the cell (metres on a 4 m cell in brackets).
    public const float PadFrac       = 0.05f;  // plate left bare around the text (0.2)
    public const float TextEmFrac    = 0.20f;  // text em at the shared size (0.8)
    public const float CharAdvanceEm = 0.75f;  // wall one bold capital takes, in ems (0.6 per letter)
    public const float MinPlateFrac  = 0.5f;   // narrowest plate (2)
    public const float GapFrac       = 0.1f;   // clear wall between plates and at a stretch's ends (0.4)
    public const float MinScale      = 0.5f;   // a sign shrunk below this is not drawn
    private const float JoinGap      = 0.05f;  // metres two neighbouring faces may miss by and still join

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

    // ---- what a building's signs are ----

    // The signs an instance shows, in list order. Never mutates: a migrated instance returns its own
    // list; a never-migrated one gets a fresh list built from the single-sign fields (the old pinned
    // pair maps to a pin at the same midpoint), else from the def's legacy word, else nothing.
    public static List<BuildingSignEntry> EntriesFor(BuildingInstance inst, BuildingDef def)
    {
        if (inst?.signs != null) return inst.signs;
        var list = new List<BuildingSignEntry>();
        string compass = inst != null ? NormalizeCompass(inst.signCompass) : null;
        if (compass != null)
        {
            string text = NormalizeText(inst.signText);
            if (text == null) return list;   // a cleared sign stays cleared
            var e = new BuildingSignEntry { text = text };
            if (inst.signPinned)
            {
                string face = FaceTowardWorldDir(inst.rotationY, CompassDir(compass));
                bool alongX = RunsAlongX(face);
                int along   = alongX ? inst.signHostX : inst.signHostZ;
                e.pinned   = true;
                e.pinFace  = face;
                e.pinFloor = inst.signHostFloor;
                e.pinSide  = alongX ? inst.signHostZ : inst.signHostX;
                e.pinHalf  = 2 * (along + 1);   // the line between tile A and tile B
            }
            list.Add(e);
            return list;
        }
        string legacy = def != null ? NormalizeText(def.signText) : null;
        if (legacy != null) list.Add(new BuildingSignEntry { text = legacy });
        return list;
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

    // The building-local wall the row starts on: the instance compass through the yaw, else the
    // legacy def wall as stored, else east through the yaw.
    public static string StartFace(BuildingInstance inst, BuildingDef def)
    {
        float yaw = inst != null ? inst.rotationY : 0f;
        string compass = inst != null ? NormalizeCompass(inst.signCompass) : null;
        if (compass != null) return FaceTowardWorldDir(yaw, CompassDir(compass));
        if (def != null && NormalizeText(def.signText) != null)
        {
            string face = NormalizeFace(def.signFace);
            if (face != null) return face;
        }
        return FaceTowardWorldDir(yaw, CompassDir(DefaultCompass));
    }

    // First edit on an instance still showing the single-sign fields or its def's legacy sign: the
    // list takes over from here on. No-op once migrated. Never called on load, so opening a record
    // does not change it.
    public static void Adopt(BuildingInstance inst, BuildingDef def)
    {
        if (inst == null || inst.signs != null) return;
        var entries = EntriesFor(inst, def);
        inst.signCompass   = EffectiveCompass(inst, def);
        inst.signs         = entries;
        inst.signText      = null;
        inst.signPinned    = false;
        inst.signHostX     = 0;
        inst.signHostZ     = 0;
        inst.signHostFloor = 0;
    }

    // Plate width for a word at the shared text size: the letters plus the bare edge, never under
    // the minimum plate.
    public static float PlateWidth(string text, float cellSize)
    {
        string t = NormalizeText(text);
        int chars = t != null ? t.Length : 0;
        float w = 2f * PadFrac * cellSize + chars * CharAdvanceEm * TextEmFrac * cellSize;
        return Mathf.Max(MinPlateFrac * cellSize, w);
    }

    // ---- open wall ----

    // One exposed tile face under a stretch: its frame and the span it covers along the wall.
    public struct Host
    {
        public TileDef tile;
        public TileFaceGeometry.FaceFrame frame;
        public float a0, a1;
    }

    // One unbroken run of open wall on one floor of one face, measured in building-local metres
    // along the wall axis (X for north/south walls, Z for east/west).
    public struct Stretch
    {
        public string face;
        public int    floor;
        public int    side;      // coordinate across the wall (Z for north/south, X for east/west)
        public float  u0, u1;
        public List<Host> hosts; // every exposed tile of the run (a cut piece keeps the whole run's)
        public float Length => u1 - u0;
    }

    private static bool RunsAlongX(string face)
    {
        Vector3 dir = TileFaceGeometry.BaselineDir(face);
        return Mathf.Abs(dir.z) > Mathf.Abs(dir.x);
    }

    private static Vector3 AlongAxis(string face) => RunsAlongX(face) ? Vector3.right : Vector3.forward;

    // +1 when the wall axis runs toward the right of someone facing the wall from outside, else -1.
    private static int ViewerSign(string face)
    {
        Vector3 viewerRight = Vector3.Cross(Vector3.up, -TileFaceGeometry.BaselineDir(face));
        return Vector3.Dot(viewerRight, AlongAxis(face)) >= 0f ? 1 : -1;
    }

    private static int LowestFloor(BuildingDef def)
    {
        int floor = int.MaxValue;
        if (def?.tiles != null)
            foreach (var t in def.tiles) if (t != null && t.floor < floor) floor = t.floor;
        return floor;
    }

    // Exposed tiles with a face pointing out of `face`, grouped by (floor, side) and listed along
    // the wall.
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

    // Every stretch of open wall on `face` (one floor, or all of them), ordered by floor, side, u0.
    // Neighbouring tiles join when their faces meet; a missing tile, a covered face, or a shape
    // narrower than its cell (a pillar) ends the stretch.
    public static List<Stretch> Stretches(BuildingDef def, string face, float cellSize,
                                          Func<string, TileFit> fitFor, int? floor = null)
    {
        var result = new List<Stretch>();
        string f = NormalizeFace(face);
        if (f == null || cellSize <= 0f) return result;
        Vector3 axis = AlongAxis(f);
        var rows = ExposedRows(def, f, cellSize, fitFor, floor);
        var keys = new List<(int floor, int side)>(rows.Keys);
        keys.Sort((x, y) => x.floor != y.floor ? x.floor.CompareTo(y.floor) : x.side.CompareTo(y.side));
        foreach (var key in keys)
        {
            bool open = false;
            Stretch cur = default;
            int prevAlong = 0;
            foreach (var (along, tile, faceName) in rows[key])
            {
                TileFit fit = fitFor != null ? fitFor(tile.shapeId) : TileFit.Full;
                if (!TileFaceGeometry.TryGetFaceFrame(tile, faceName, cellSize, fit, out var frame)) continue;
                float c = Vector3.Dot(frame.center, axis);
                var host = new Host { tile = tile, frame = frame, a0 = c - frame.width * 0.5f, a1 = c + frame.width * 0.5f };
                bool joins = open && along == prevAlong + 1 && Mathf.Abs(host.a0 - cur.u1) <= JoinGap;
                if (!joins)
                {
                    if (open) result.Add(cur);
                    cur = new Stretch { face = f, floor = key.floor, side = key.side, u0 = host.a0, u1 = host.a1, hosts = new List<Host>() };
                    open = true;
                }
                cur.u1 = Mathf.Max(cur.u1, host.a1);
                cur.hosts.Add(host);
                prevAlong = along;
            }
            if (open) result.Add(cur);
        }
        return result;
    }

    // ---- where the plates go ----

    // Where one plate sits, in building-local metres. `right` runs along the wall axis; `up` is the
    // wall's in-plane up; `normal` points out of the wall.
    public struct Placement
    {
        public Vector3 center;
        public Vector3 normal;
        public Vector3 up;
        public Vector3 right;
        public float   width;
        public float   height;
        public float   scale;    // 1 = the shared size; under 1 = shrunk to fit its wall
        public string  face;     // the wall the plate hangs on
        public int     floor;
        public int     side;
        public float   u;        // plate centre along the wall axis
        public bool    pinned;   // placed by its pin
        public bool    pinLost;  // the pinned spot is gone, so the sign was laid out automatically
    }

    public enum Skip { None, NoText, NoRoom, TooSmall }

    public struct SignResult
    {
        public Skip      skip;
        public Placement p;   // valid when skip == None
    }

    // The plate of `width` (already scaled) centred at `u` on a stretch: seated on the tiles it
    // covers, in the top band of the floor (the lowest safe top line among them, less the margin).
    private static Placement MakePlacement(Stretch s, float u, float width, float scale, float cellSize)
    {
        Vector3 axis = AlongAxis(s.face);
        float lo = u - width * 0.5f + 1e-3f, hi = u + width * 0.5f - 1e-3f;
        Vector3 normal = Vector3.zero, up = Vector3.zero, off = Vector3.zero;
        float uTop = float.PositiveInfinity;
        int count = 0;
        for (int pass = 0; pass < 2 && count == 0; pass++)
        {
            foreach (var h in s.hosts)
            {
                if (pass == 0 && (h.a1 < lo || h.a0 > hi)) continue;
                normal += h.frame.normal;
                up     += h.frame.up;
                off    += h.frame.center - axis * Vector3.Dot(h.frame.center, axis);
                uTop    = Mathf.Min(uTop, h.frame.uTop);
                count++;
            }
        }
        normal.Normalize();
        up.Normalize();
        float band = BandFrac * cellSize * scale;
        return new Placement
        {
            center = axis * u + off / Mathf.Max(1, count) + up * (uTop - TopMargin - band * 0.5f),
            normal = normal,
            up     = up,
            right  = axis,
            width  = width,
            height = band,
            scale  = scale,
            face   = s.face,
            floor  = s.floor,
            side   = s.side,
            u      = u,
        };
    }

    // Counterclockwise seen from above: north (+Z), west (-X), south, east.
    private static readonly string[] CounterClockwise = { "north", "west", "south", "east" };

    private static string[] FaceOrder(string start, Func<string, float> openMetres)
    {
        int i = Array.IndexOf(CounterClockwise, start);
        string ccw = CounterClockwise[(i + 1) % 4], opposite = CounterClockwise[(i + 2) % 4], cw = CounterClockwise[(i + 3) % 4];
        bool goCcw = openMetres(ccw) >= openMetres(cw) - 1e-4f;
        return goCcw ? new[] { start, ccw, opposite, cw } : new[] { start, cw, opposite, ccw };
    }

    // The order the row spills around the building: the start wall, then the neighbour with more
    // open wall on the lowest floor (counterclockwise seen from above on a tie), then on around
    // the same way.
    public static string[] FaceOrder(BuildingDef def, string startFace, float cellSize, Func<string, TileFit> fitFor)
    {
        string start = NormalizeFace(startFace);
        if (start == null) return new string[0];
        int lowest = LowestFloor(def);
        return FaceOrder(start, face =>
        {
            float sum = 0f;
            foreach (var s in Stretches(def, face, cellSize, fitFor, lowest)) sum += s.Length;
            return sum;
        });
    }

    private static bool TryResolvePin(List<Stretch> onFace, BuildingSignEntry e, float width, float cellSize,
                                      out Stretch stretch, out float u, out float scale)
    {
        stretch = default; scale = 1f;
        u = e.pinHalf * cellSize * 0.5f;
        foreach (var s in onFace)
        {
            if (s.floor != e.pinFloor || s.side != e.pinSide) continue;
            if (u < s.u0 - 1e-3f || u > s.u1 + 1e-3f) continue;
            stretch = s;
            float w = width;
            if (s.Length < w) { scale = s.Length / w; w = s.Length; }
            u = Mathf.Clamp(u, s.u0 + w * 0.5f, s.u1 - w * 0.5f);
            return true;
        }
        return false;
    }

    // Lays out every sign of a building. Pinned signs take their own spot first. The rest keep list
    // order: they fill the start wall's lowest floor (longest open stretch first), then spill around
    // the building (FaceOrder). Each stretch is shared out in proportion to plate width and every
    // sign is centred in its share, left to right as seen from outside. A word wider than the
    // longest stretch shrinks alone; under MinScale it is TooSmall. A sign with no wall left is
    // NoRoom. One result per entry, same order.
    public static List<SignResult> Layout(BuildingDef def, IList<BuildingSignEntry> entries, string startFace,
                                          float cellSize, Func<string, TileFit> fitFor)
    {
        int n = entries?.Count ?? 0;
        var results = new List<SignResult>(n);
        for (int i = 0; i < n; i++)
            results.Add(new SignResult { skip = NormalizeText(entries[i]?.text) == null ? Skip.NoText : Skip.NoRoom });
        string start = NormalizeFace(startFace);
        if (n == 0 || start == null || cellSize <= 0f || def?.tiles == null || def.tiles.Count == 0) return results;

        var byFace = new Dictionary<string, List<Stretch>>();
        List<Stretch> All(string face)
        {
            if (!byFace.TryGetValue(face, out var list)) byFace[face] = list = Stretches(def, face, cellSize, fitFor);
            return list;
        }
        int lowest = LowestFloor(def);
        float gap = GapFrac * cellSize;

        // Pins first. A pin whose wall is gone rejoins the automatic row at its place in the list.
        var pinLost = new bool[n];
        var spans   = new List<(string face, int floor, int side, float a, float b)>();
        var auto    = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (results[i].skip == Skip.NoText) continue;
            var e = entries[i];
            string pinFace = e.pinned ? NormalizeFace(e.pinFace) : null;
            if (!e.pinned) { auto.Add(i); continue; }
            float w = PlateWidth(e.text, cellSize);
            if (pinFace == null || !TryResolvePin(All(pinFace), e, w, cellSize, out var s, out float u, out float scale))
            {
                pinLost[i] = true;
                auto.Add(i);
                continue;
            }
            if (scale < MinScale) { results[i] = new SignResult { skip = Skip.TooSmall }; continue; }
            var p = MakePlacement(s, u, w * scale, scale, cellSize);
            p.pinned = true;
            results[i] = new SignResult { skip = Skip.None, p = p };
            spans.Add((s.face, s.floor, s.side, u - w * scale * 0.5f, u + w * scale * 0.5f));
        }
        if (auto.Count == 0) return results;

        // The open wall left for the row, wall by wall, longest stretch first on each.
        var order = FaceOrder(start, face =>
        {
            float sum = 0f;
            foreach (var s in All(face)) if (s.floor == lowest) sum += s.Length;
            return sum;
        });
        var flat = new List<Stretch>();
        foreach (var face in order)
        {
            var pieces = new List<Stretch>();
            foreach (var s in All(face))
            {
                if (s.floor != lowest) continue;
                var cut = new List<Stretch> { s };
                foreach (var sp in spans)
                {
                    if (sp.face != s.face || sp.floor != s.floor || sp.side != s.side) continue;
                    var next = new List<Stretch>();
                    foreach (var c in cut)
                    {
                        float a = sp.a - gap, b = sp.b + gap;
                        if (b <= c.u0 || a >= c.u1) { next.Add(c); continue; }
                        if (a > c.u0) { var l = c; l.u1 = a; next.Add(l); }
                        if (b < c.u1) { var r = c; r.u0 = b; next.Add(r); }
                    }
                    cut = next;
                }
                foreach (var c in cut) if (c.Length > 1e-3f) pieces.Add(c);
            }
            pieces.Sort((x, y) =>
            {
                if (Mathf.Abs(x.Length - y.Length) > 1e-4f) return y.Length.CompareTo(x.Length);
                return x.side != y.side ? x.side.CompareTo(y.side) : x.u0.CompareTo(y.u0);
            });
            flat.AddRange(pieces);
        }
        float longest = 0f;
        foreach (var s in flat) longest = Mathf.Max(longest, s.Length);

        // Order-keeping first fit: a sign goes on the current stretch or a later one, never back.
        var assigned = new List<(int index, float width, float scale)>[flat.Count];
        var used     = new float[flat.Count];
        for (int k = 0; k < flat.Count; k++) { assigned[k] = new(); used[k] = gap; }
        int cursor = 0;
        foreach (int i in auto)
        {
            float w = PlateWidth(entries[i].text, cellSize), scale = 1f;
            float room = longest - 2f * gap;
            if (flat.Count > 0 && w > room)
            {
                scale = room > 0f ? room / w : 0f;
                if (scale < MinScale) { results[i] = new SignResult { skip = Skip.TooSmall }; continue; }
                w = room;
            }
            for (int k = cursor; k < flat.Count; k++)
            {
                if (used[k] + w + gap > flat[k].Length + 1e-4f) continue;
                assigned[k].Add((i, w, scale));
                used[k] += w + gap;
                cursor = k;
                break;
            }
        }

        for (int k = 0; k < flat.Count; k++)
        {
            if (assigned[k].Count == 0) continue;
            var s = flat[k];
            float total = 0f;
            foreach (var a in assigned[k]) total += a.width + gap;
            int dir = ViewerSign(s.face);
            float at = dir > 0 ? s.u0 : s.u1;
            foreach (var a in assigned[k])
            {
                float share = s.Length * (a.width + gap) / total;
                var p = MakePlacement(s, at + dir * share * 0.5f, a.width, a.scale, cellSize);
                p.pinLost = pinLost[a.index];
                results[a.index] = new SignResult { skip = Skip.None, p = p };
                at += dir * share;
            }
        }
        return results;
    }

    public static List<SignResult> LayoutFor(BuildingInstance inst, BuildingDef def, float cellSize, Func<string, TileFit> fitFor) =>
        Layout(def, EntriesFor(inst, def), StartFace(inst, def), cellSize, fitFor);

    // ---- pins ----

    public struct Pin
    {
        public string face;
        public int    floor, side, half;
    }

    // One place a pinned plate can sit: a half-tile step on some open stretch, with the plate centre
    // it resolves to (clamped so the plate stays on the stretch).
    public struct PinSpot
    {
        public Pin     pin;
        public Vector3 center;
        public Vector3 normal;
    }

    public static Pin PinOf(BuildingSignEntry e) =>
        new Pin { face = NormalizeFace(e.pinFace), floor = e.pinFloor, side = e.pinSide, half = e.pinHalf };

    public static void SetPin(BuildingSignEntry e, Pin pin)
    {
        e.pinned   = true;
        e.pinFace  = pin.face;
        e.pinFloor = pin.floor;
        e.pinSide  = pin.side;
        e.pinHalf  = pin.half;
    }

    public static bool SamePin(Pin x, Pin y) =>
        x.face == y.face && x.floor == y.floor && x.side == y.side && x.half == y.half;

    // The pin that holds a plate where the layout put it (rounded to the nearest half tile).
    public static Pin PinFromPlacement(Placement p, float cellSize) =>
        new Pin { face = p.face, floor = p.floor, side = p.side, half = Mathf.RoundToInt(p.u / (cellSize * 0.5f)) };

    private static void AddSpots(List<PinSpot> spots, Stretch s, float width, float cellSize)
    {
        if (s.Length < width * MinScale) return;
        float half = cellSize * 0.5f;
        float w = Mathf.Min(width, s.Length), scale = w / width;
        int h0 = Mathf.CeilToInt((s.u0 - 1e-3f) / half), h1 = Mathf.FloorToInt((s.u1 + 1e-3f) / half);
        for (int h = h0; h <= h1; h++)
        {
            float u = Mathf.Clamp(h * half, s.u0 + w * 0.5f, s.u1 - w * 0.5f);
            var p = MakePlacement(s, u, w, scale, cellSize);
            spots.Add(new PinSpot
            {
                pin = new Pin { face = s.face, floor = s.floor, side = s.side, half = h },
                center = p.center, normal = p.normal,
            });
        }
    }

    // Every half-tile spot a plate of `width` can be pinned to, on every wall and floor.
    public static List<PinSpot> PinSpots(BuildingDef def, float width, float cellSize, Func<string, TileFit> fitFor)
    {
        var spots = new List<PinSpot>();
        foreach (var face in WallFaces)
            foreach (var s in Stretches(def, face, cellSize, fitFor))
                AddSpots(spots, s, width, cellSize);
        return spots;
    }

    // The spot a building-local ray (a drag) points at: among walls facing the ray, the spot whose
    // centre is closest to where the ray meets that spot's wall plane.
    public static bool NearestPinSpot(List<PinSpot> spots, Ray localRay, out PinSpot best)
    {
        best = default;
        float bestD = float.PositiveInfinity;
        if (spots == null) return false;
        foreach (var s in spots)
        {
            float facing = Vector3.Dot(localRay.direction, s.normal);
            if (facing >= -1e-4f) continue;
            float t = Vector3.Dot(s.center - localRay.origin, s.normal) / facing;
            if (t <= 0f) continue;
            float d = (localRay.GetPoint(t) - s.center).sqrMagnitude;
            if (d < bestD) { bestD = d; best = s; }
        }
        return bestD < float.PositiveInfinity;
    }

    // The spot a sign gets when it is pinned while not drawn: the middle of the longest lowest-floor
    // stretch on the first wall (FaceOrder) that can hold it.
    public static bool DefaultPin(BuildingDef def, string startFace, float width, float cellSize,
                                  Func<string, TileFit> fitFor, out Pin pin)
    {
        pin = default;
        int lowest = LowestFloor(def);
        foreach (var face in FaceOrder(def, startFace, cellSize, fitFor))
        {
            bool found = false;
            Stretch bestS = default;
            foreach (var s in Stretches(def, face, cellSize, fitFor, lowest))
                if (s.Length >= width * MinScale && (!found || s.Length > bestS.Length + 1e-4f)) { bestS = s; found = true; }
            if (!found) continue;
            pin = new Pin { face = face, floor = bestS.floor, side = bestS.side,
                            half = Mathf.RoundToInt((bestS.u0 + bestS.u1) * 0.5f / (cellSize * 0.5f)) };
            return true;
        }
        return false;
    }

    // One nudge of a pinned sign. dRight steps half a tile toward the viewer's right when facing the
    // wall from outside, skipping steps that leave the plate where it is (the clamped ends) and
    // hopping gaps in the wall; it stays on its own wall. dFloor steps a floor, keeping the place
    // along the wall when that wall exists there and taking the closest spot on that floor otherwise.
    // Returns the entry's own pin when nothing valid exists.
    public static Pin StepPin(BuildingDef def, BuildingSignEntry e, int dRight, int dFloor,
                              float cellSize, Func<string, TileFit> fitFor)
    {
        Pin current = PinOf(e);
        if (current.face == null || cellSize <= 0f) return current;
        float width = PlateWidth(e.text, cellSize);
        var onFace = Stretches(def, current.face, cellSize, fitFor);
        bool have = TryResolvePin(onFace, e, width, cellSize, out var curS, out float curU, out _);
        var probe = new BuildingSignEntry { text = e.text, pinned = true, pinFace = current.face,
                                            pinFloor = current.floor, pinSide = current.side, pinHalf = current.half };

        if (dFloor != 0)
        {
            probe.pinFloor = current.floor + dFloor;
            if (TryResolvePin(onFace, probe, width, cellSize, out var s, out _, out float sc) && sc >= MinScale)
                return PinOf(probe);
            var spots = new List<PinSpot>();
            foreach (var st in onFace) if (st.floor == probe.pinFloor) AddSpots(spots, st, width, cellSize);
            if (spots.Count == 0) return current;
            Vector3 from = have ? MakePlacement(curS, curU, Mathf.Min(width, curS.Length), 1f, cellSize).center
                                : AlongAxis(current.face) * (current.half * cellSize * 0.5f);
            from.y += dFloor * cellSize;
            PinSpot best = spots[0];
            float bestD = float.PositiveInfinity;
            foreach (var sp in spots)
            {
                float d = (sp.center - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = sp; }
            }
            return best.pin;
        }

        if (dRight != 0)
        {
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (var s in onFace)
                if (s.floor == current.floor && s.side == current.side) { lo = Mathf.Min(lo, s.u0); hi = Mathf.Max(hi, s.u1); }
            if (lo > hi) return current;
            int step = dRight * ViewerSign(current.face);
            float half = cellSize * 0.5f;
            for (int h = current.half + step; h * half >= lo - 1e-3f && h * half <= hi + 1e-3f; h += step)
            {
                probe.pinHalf = h;
                if (!TryResolvePin(onFace, probe, width, cellSize, out _, out float u, out float sc) || sc < MinScale) continue;
                if (have && Mathf.Abs(u - curU) < 1e-3f) continue;
                return PinOf(probe);
            }
        }
        return current;
    }
}
