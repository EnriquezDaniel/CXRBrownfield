"""
Reconcile a generated layout against the designer brief (brief_prompt.py) after the model returns.

Pure functions, standard library only, so the server can run them on every generation and the
tests in tests/test_reconcile.py can pin the behaviour without an API key.

What it does, in order:
  1. link every brief building to a layout entry (by brief_ref, then exact name, then a unique
     use-word match),
  2. split drawn blocks the model failed to split (equal whole-tile shares along the long axis,
     touching, mirroring Unity's LayoutConverter rounding),
  3. overwrite name, style and floors on linked buildings from the brief, and strip style letters
     the model picked on its own,
  4. link paths, fences and props, enforce material / width / type / height, apply exclusions,
  5. return a report of what was satisfied and what was missed. Nothing here is fatal.

Coordinates follow prompts/site_parsing.md: [y, x] on a 0-1000 canvas, y down the image spanning
site_width_ft, x across spanning site_height_ft. Unity gives a building
max(1, round(extent_m / 4)) tiles per axis (LayoutConverter.BuildingDefFromGeneratedBuilding), so
the split hands out whole tiles and neighbours share an edge.
"""
from __future__ import annotations

import copy
import re

TILE_M = 4.0
M_PER_FT = 0.3048
STYLE_IDS = ("A", "B", "C", "D", "E", "F")

# Words that name a building in prose but never identify one drawn block over another.
_GENERIC_WORDS = {"building", "block", "shop", "store", "house", "the", "and", "with", "use", "mixed"}


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

def strip_null_fields(site_data):
    """Drop keys whose value is null inside every entry of every top-level list, and inside
    site_scale. Optional layout fields are "omitted", never null, on Unity's typed parse."""
    if not isinstance(site_data, dict):
        return site_data
    for key, value in list(site_data.items()):
        if isinstance(value, list):
            for item in value:
                if isinstance(item, dict):
                    for k in [k for k, v in item.items() if v is None]:
                        del item[k]
        elif isinstance(value, dict):
            for k in [k for k, v in value.items() if v is None]:
                del value[k]
    return site_data


def tiles_for(units, ft_per_unit) -> int:
    """Whole 4 m tiles Unity assigns to an extent of `units` canvas units on an axis with
    `ft_per_unit` feet per unit. Python's round is half-to-even, the same as Mathf.RoundToInt."""
    return max(1, int(round(float(units) * float(ft_per_unit) * M_PER_FT / TILE_M)))


def is_quarter_turn(deg, tol=1.0) -> bool:
    try:
        r = float(deg) % 90.0
    except (TypeError, ValueError):
        return False
    return min(r, 90.0 - r) <= tol


def _tokens(text) -> set:
    if not isinstance(text, str):
        return set()
    return {t for t in re.split(r"[^a-z0-9]+", text.lower()) if len(t) >= 3 and t not in _GENERIC_WORDS}


def _bbox(entry):
    bb = entry.get("bounding_box") if isinstance(entry, dict) else None
    if isinstance(bb, (list, tuple)) and len(bb) == 4:
        try:
            return [float(v) for v in bb]
        except (TypeError, ValueError):
            return None
    return None


def _apply_box(entry, box, width_ft, height_ft):
    """Write an integer bounding box plus the derived center and area into an entry."""
    ymin, xmin, ymax, xmax = [int(round(v)) for v in box]
    entry["bounding_box"] = [ymin, xmin, ymax, xmax]
    entry["center_point"] = [(ymin + ymax) // 2, (xmin + xmax) // 2]
    if width_ft and height_ft:
        tall_ft = (ymax - ymin) / 1000.0 * float(width_ft)
        wide_ft = (xmax - xmin) / 1000.0 * float(height_ft)
        entry["approx_sq_ft"] = int(round(tall_ft * wide_ft))


# ---------------------------------------------------------------------------
# Split math
# ---------------------------------------------------------------------------

def split_box(bbox, count, width_ft, height_ft):
    """Split one drawn block into `count` touching pieces along its longer side in feet.

    Returns (pieces, info) where pieces is a list of [ymin, xmin, ymax, xmax] integer boxes and
    info = {"axis": 0 | 1, "total_tiles": T, "tiles": [...], "n_eff": N}. Returns (None, reason)
    when the box or the dimensions are unusable. Pieces differ by at most one tile and never
    extend past the block, so a block with fewer tiles than `count` yields fewer pieces.
    """
    try:
        ymin, xmin, ymax, xmax = [float(v) for v in bbox]
        count = int(count)
        fy = float(width_ft) / 1000.0
        fx = float(height_ft) / 1000.0
    except (TypeError, ValueError):
        return None, "unusable box or site dimensions"
    if count < 1 or fy <= 0 or fx <= 0 or ymax <= ymin or xmax <= xmin:
        return None, "unusable box or site dimensions"

    ly, lx = ymax - ymin, xmax - xmin
    axis = 0 if ly * fy >= lx * fx else 1          # the longer side in feet
    f = fy if axis == 0 else fx
    a_min, a_max = (ymin, ymax) if axis == 0 else (xmin, xmax)

    total = tiles_for(a_max - a_min, f)
    n_eff = min(count, total)
    tiles = [total // n_eff + (1 if i < total % n_eff else 0) for i in range(n_eff)]
    units_per_tile = TILE_M / (M_PER_FT * f)

    edges = [a_min]
    acc = 0
    for t in tiles[:-1]:
        acc += t
        edges.append(a_min + round(acc * units_per_tile))
    edges.append(a_max)                             # the last piece ends exactly at the block edge

    pieces = []
    for i in range(n_eff):
        lo, hi = edges[i], edges[i + 1]
        if axis == 0:
            pieces.append([int(round(lo)), int(round(xmin)), int(round(hi)), int(round(xmax))])
        else:
            pieces.append([int(round(ymin)), int(round(lo)), int(round(ymax)), int(round(hi))])
    return pieces, {"axis": axis, "total_tiles": total, "tiles": tiles, "n_eff": n_eff}


# ---------------------------------------------------------------------------
# Linking
# ---------------------------------------------------------------------------

def _entries(site_data, key):
    items = site_data.get(key)
    if not isinstance(items, list):
        items = []
        site_data[key] = items
    return [e for e in items if isinstance(e, dict)]


def _link_building(brief_bldg, pools, taken):
    """pools: list of (category, entries). Returns (category, entry) or (None, None)."""
    ref = brief_bldg["ref"]
    for cat, entries in pools:
        for e in entries:
            if id(e) not in taken and e.get("brief_ref") == ref:
                return cat, e
    name = brief_bldg["name"].strip().casefold()
    for cat, entries in pools:
        for e in entries:
            if id(e) not in taken and str(e.get("area_name") or "").strip().casefold() == name:
                return cat, e
    words = _tokens(brief_bldg.get("use")) | _tokens(brief_bldg["name"])
    if not words:
        return None, None
    hits = []
    for cat, entries in pools:
        for e in entries:
            if id(e) in taken:
                continue
            have = _tokens(e.get("area_name")) | _tokens(e.get("semantic_tag"))
            if words & have:
                hits.append((cat, e))
    if len(hits) == 1:
        return hits[0]
    return None, None


# ---------------------------------------------------------------------------
# Reconcile
# ---------------------------------------------------------------------------

def reconcile_brief(site_data, brief, width_ft=None, height_ft=None) -> dict:
    """Mutates site_data in place to honour the brief and returns the report.

    width_ft / height_ft default to site_scale (auto mode has no request dimensions).
    """
    report = {
        "satisfied": [], "missing": [], "extra_unmatched": [], "splits": [],
        "paths": [], "fences": [], "props": [],
        "ignored": list(brief.get("ignored") or []) if isinstance(brief, dict) else [],
        "unparsed": list(brief.get("unparsed") or []) if isinstance(brief, dict) else [],
        "warnings": [], "changed": False,
    }
    if not isinstance(site_data, dict) or not isinstance(brief, dict):
        return report

    scale = site_data.get("site_scale") if isinstance(site_data.get("site_scale"), dict) else {}
    width_ft = width_ft or scale.get("site_width_ft")
    height_ft = height_ft or scale.get("site_height_ft")

    buildings = _entries(site_data, "generated_buildings")
    objects = _entries(site_data, "generated_objects")
    pools = [("generated_buildings", buildings), ("generated_objects", objects)]
    brief_buildings = [b for b in (brief.get("buildings") or []) if isinstance(b, dict) and b.get("ref")]
    by_ref = {b["ref"]: b for b in brief_buildings}

    # 1. link
    linked = {}            # ref -> (category, entry)
    taken = set()          # id(entry) already claimed by a ref
    for b in brief_buildings:
        cat, e = _link_building(b, pools, taken)
        if e is not None:
            linked[b["ref"]] = (cat, e)
            taken.add(id(e))

    # 2. splits
    missing_reason = {}
    for s in brief.get("splits") or []:
        if not isinstance(s, dict) or not s.get("group"):
            continue
        group = s["group"]
        members = [b for b in brief_buildings if b.get("group") == group]
        count = max(int(s.get("count") or 0), len(members), 1)
        found = []
        seen = set()
        for m in members:
            hit = linked.get(m["ref"])
            if hit and id(hit[1]) not in seen:
                seen.add(id(hit[1]))
                found.append(hit)
        entry = {"group": group, "count": count, "found": len(found)}
        if not found:
            entry["outcome"] = "missing"
            report["splits"].append(entry)
            continue
        if len(found) >= count:
            entry["outcome"] = "satisfied"
            report["splits"].append(entry)
            continue
        if any(cat != "generated_buildings" for cat, _ in found):
            entry["outcome"] = "skipped_object"
            report["splits"].append(entry)
            continue
        boxes = [_bbox(e) for _, e in found]
        if any(bb is None for bb in boxes):
            entry["outcome"] = "skipped_bad_box"
            report["splits"].append(entry)
            continue
        if any(not is_quarter_turn(e.get("rotation_y_deg", 0)) for _, e in found):
            entry["outcome"] = "skipped_rotated"
            report["warnings"].append(f"Split group '{group}' skipped: the drawn block is rotated off the grid.")
            report["splits"].append(entry)
            continue
        union = [min(b[0] for b in boxes), min(b[1] for b in boxes),
                 max(b[2] for b in boxes), max(b[3] for b in boxes)]
        pieces, info = split_box(union, count, width_ft, height_ft)
        if pieces is None:
            entry["outcome"] = "skipped"
            entry["reason"] = info
            report["splits"].append(entry)
            continue

        template = found[0][1]
        insert_at = min(buildings.index(e) for _, e in found)
        for _, e in found:
            buildings.remove(e)
            taken.discard(id(e))
            for ref, (_, le) in list(linked.items()):
                if le is e:
                    del linked[ref]
        stripped_corners = bool(template.get("corner_angles"))
        for i, piece in enumerate(pieces):
            member = members[i] if i < len(members) else None
            new = copy.deepcopy(template)
            new.pop("sign", None)
            new.pop("corner_angles", None)
            new.pop("brief_ref", None)
            _apply_box(new, piece, width_ft, height_ft)
            if member is not None:
                new["area_name"] = member["name"]
                new["brief_ref"] = member["ref"]
                linked[member["ref"]] = ("generated_buildings", new)
                taken.add(id(new))
            else:
                new["area_name"] = f"{template.get('area_name') or 'Building'} {i + 1}"
            buildings.insert(insert_at + i, new)
        for m in members[len(pieces):]:
            missing_reason[m["ref"]] = "split_reduced"
        entry["outcome"] = "split" if info["n_eff"] >= count else "split_reduced"
        entry["tiles"] = info["tiles"]
        entry["axis"] = info["axis"]
        if stripped_corners:
            entry["corner_angles_stripped"] = True
            report["warnings"].append(f"Split group '{group}': corner angles dropped from the pieces.")
        if info["n_eff"] < count:
            report["warnings"].append(
                f"Split group '{group}': the block only has {info['total_tiles']} tiles, "
                f"so it was split into {info['n_eff']} pieces instead of {count}.")
        report["changed"] = True
        report["splits"].append(entry)
    # keep site_data's list in sync (we mutated the filtered copy in place, so rebuild)
    site_data["generated_buildings"] = buildings

    # 3. enforce facts on linked buildings, strip invented styles elsewhere
    for b in brief_buildings:
        hit = linked.get(b["ref"])
        if hit is None:
            report["missing"].append({"ref": b["ref"], "name": b["name"],
                                      "reason": missing_reason.get(b["ref"], "not_found")})
            continue
        cat, e = hit
        changed = False
        if e.get("area_name") != b["name"]:
            e["area_name"] = b["name"]; changed = True
        if e.get("brief_ref") != b["ref"]:
            e["brief_ref"] = b["ref"]; changed = True
        if cat == "generated_buildings":
            if b.get("style"):
                if e.get("style") != b["style"]:
                    e["style"] = b["style"]; changed = True
            elif "style" in e:
                del e["style"]; changed = True
            if b.get("floors") and e.get("floors") != b["floors"]:
                e["floors"] = b["floors"]; changed = True
        report["satisfied"].append(b["ref"])
        report["changed"] = report["changed"] or changed
    if brief_buildings:
        for e in buildings:
            if id(e) not in taken:
                report["extra_unmatched"].append(e.get("area_name") or "?")
                if e.get("style"):
                    del e["style"]
                    report["changed"] = True
                    report["warnings"].append(
                        f"Style letter dropped from '{e.get('area_name') or '?'}': the notes did not assign one.")
    for r in report["missing"]:
        report["warnings"].append(f"Brief building '{r['name']}' ({r['ref']}) is not in the layout ({r['reason']}).")

    # 4. paths, fences, props
    _reconcile_linear(site_data, brief, "paths", "paths", "path_material", "material",
                      ("width_ft", "width_ft"), report)
    _reconcile_linear(site_data, brief, "fences", "fences", "fence_type", "type",
                      ("height_ft", "height_ft"), report)
    _reconcile_props(site_data, brief, report)
    return report


def _reconcile_linear(site_data, brief, layout_key, brief_key, kind_field, brief_kind_field,
                      size_fields, report):
    """Paths and fences share one shape: a kind enum plus one numeric size."""
    layout_size, brief_size = size_fields
    entries = _entries(site_data, layout_key)
    items = [i for i in (brief.get(brief_key) or []) if isinstance(i, dict)]
    if not items:
        return
    wants = [i for i in items if not i.get("exclude")]
    excludes = [i for i in items if i.get("exclude")]

    removed = []
    for ex in excludes:
        kind = ex.get(brief_kind_field)
        if kind:
            victims = [e for e in entries if e.get(kind_field) == kind]
        elif not (ex.get("where") or "").strip():
            victims = list(entries)
        else:
            victims = []          # a placed exclusion with no kind: trust the model's reading
        for v in victims:
            entries.remove(v)
            removed.append(v.get("area_name") or "?")
        report[layout_key].append({"where": ex.get("where"), "kind": kind, "outcome": "excluded",
                                   "removed": len(victims)})
    if removed:
        report["changed"] = True
    site_data[layout_key] = entries

    taken = set()
    for idx, w in enumerate(wants):
        ref = w.get("ref") or f"{brief_key[:-1]}{idx + 1}"
        hit = next((e for e in entries if id(e) not in taken and e.get("brief_ref") == ref), None)
        kind = w.get(brief_kind_field)
        if hit is None and kind:
            same = [e for e in entries if id(e) not in taken and e.get(kind_field) == kind]
            if len(same) == 1:
                hit = same[0]
        if hit is None and len(wants) == 1:
            free = [e for e in entries if id(e) not in taken]
            if len(free) == 1:
                hit = free[0]
        out = {"where": w.get("where"), "kind": kind, "ref": ref}
        if hit is None:
            out["outcome"] = "missing"
            report["warnings"].append(f"Brief {brief_key[:-1]} '{w.get('where') or kind or '?'}' is not in the layout.")
        else:
            taken.add(id(hit))
            if kind and hit.get(kind_field) != kind:
                hit[kind_field] = kind; report["changed"] = True
            size = w.get(brief_size)
            if isinstance(size, (int, float)) and size > 0 and hit.get(layout_size) != size:
                hit[layout_size] = size; report["changed"] = True
            if hit.get("brief_ref") != ref:
                hit["brief_ref"] = ref; report["changed"] = True
            out["outcome"] = "satisfied"
        report[layout_key].append(out)


def _reconcile_props(site_data, brief, report):
    entries = _entries(site_data, "prefab_instances")
    items = [p for p in (brief.get("props") or []) if isinstance(p, dict) and p.get("type")]
    if not items:
        return

    def matches(entry, ptype):
        words = _tokens(ptype) or {ptype.lower()}
        have = _tokens(entry.get("prefab_type")) | _tokens(entry.get("area_name")) | _tokens(entry.get("semantic_tag"))
        have |= {str(entry.get("prefab_type") or "").lower()}
        return bool(words & have)

    for p in items:
        ptype = p["type"]
        if p.get("exclude"):
            victims = [e for e in entries if matches(e, ptype)]
            for v in victims:
                entries.remove(v)
            if victims:
                report["changed"] = True
            report["props"].append({"type": ptype, "outcome": "excluded", "removed": len(victims)})
            continue
        found = [e for e in entries if matches(e, ptype) or (p.get("ref") and e.get("brief_ref") == p["ref"])]
        out = {"type": ptype, "ref": p.get("ref"), "where": p.get("where"), "found": len(found)}
        if not found:
            out["outcome"] = "missing"
            report["warnings"].append(f"Brief prop '{ptype}' is not in the layout.")
        else:
            out["outcome"] = "satisfied"
            want = p.get("count")
            if isinstance(want, int) and want > 0 and len(found) < want:
                out["outcome"] = "fewer"
                report["warnings"].append(f"Brief asked for {want} '{ptype}' props; the layout has {len(found)}.")
        report["props"].append(out)
    site_data["prefab_instances"] = entries
