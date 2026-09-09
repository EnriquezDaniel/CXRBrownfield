"""
Brief pass: turn the designer's prose notes into a structured brief before the layout model runs.

The layout model still receives the raw notes (they carry nuance), but it also gets this JSON,
labelled authoritative, and the server enforces the hard facts in it after the layout comes back
(see layout_reconcile.py). That is what makes name, style, floors and split counts stop drifting
between runs: the model only has to get the matching right.

One cheap text-only call, structured output, no image. Never blocks a layout: any failure returns
brief=None with an error string and generation continues on the raw notes.
"""
from __future__ import annotations

import json
import os
import random
import time

from dotenv import load_dotenv

load_dotenv()

BRIEF_MODEL_ID = "claude-sonnet-5"
BRIEF_MAX_TOKENS = 4000
BRIEF_MAX_RETRIES = 3

STYLE_IDS = ["A", "B", "C", "D", "E", "F"]
PATH_MATERIALS = ["pavement_dark", "pavement_light", "brick", "dirt", "asphalt"]
FENCE_TYPES = ["picket", "lattice", "chain_link", "wood_privacy", "wrought_iron"]
PROP_ARRANGEMENTS = ["row", "cluster", "scatter"]
PROGRAM_UNITS = ["units", "sq_ft", "beds", "floors", "parking", "bays"]


def _nullable(t):
    # anyOf, not a type list: the API rejects `"type": ["string", "null"]` next to an enum.
    return {"anyOf": [{"type": t}, {"type": "null"}]}


def _nullable_enum(values):
    return {"anyOf": [{"type": "string", "enum": list(values)}, {"type": "null"}]}


def _obj(props, required=None):
    return {
        "type": "object",
        "properties": props,
        "required": list(props.keys()) if required is None else required,
        "additionalProperties": False,
    }


# Every property is required and optional facts are null, so the parser can never leave a key
# out and the reconcile code reads one shape. No min/max/minItems: the raw create path sends the
# schema as-is and the API rejects those keywords.
BRIEF_SCHEMA = _obj({
    "buildings": {"type": "array", "items": _obj({
        "ref":       {"type": "string"},
        "name":      {"type": "string"},
        "use":       _nullable("string"),
        "style":     _nullable_enum(STYLE_IDS),
        "floors":    _nullable("integer"),
        "size_hint": _nullable("string"),
        "where":     _nullable("string"),
        "group":     _nullable("string"),
    })},
    "splits": {"type": "array", "items": _obj({
        "group": {"type": "string"},
        "where": _nullable("string"),
        "count": {"type": "integer"},
    })},
    "program_totals": {"type": "array", "items": _obj({
        "use":      {"type": "string"},
        "quantity": {"type": "number"},
        "unit":     {"type": "string", "enum": PROGRAM_UNITS},
        "across":   {"type": "array", "items": {"type": "string"}},
    })},
    "paths": {"type": "array", "items": _obj({
        "where":    {"type": "string"},
        "material": _nullable_enum(PATH_MATERIALS),
        "width_ft": _nullable("number"),
        "exclude":  {"type": "boolean"},
    })},
    "fences": {"type": "array", "items": _obj({
        "where":     {"type": "string"},
        "type":      _nullable_enum(FENCE_TYPES),
        "height_ft": _nullable("number"),
        "exclude":   {"type": "boolean"},
    })},
    "props": {"type": "array", "items": _obj({
        "type":        {"type": "string"},
        "where":       _nullable("string"),
        "arrangement": _nullable_enum(PROP_ARRANGEMENTS),
        "count":       _nullable("integer"),
        "exclude":     {"type": "boolean"},
    })},
    "ignored":  {"type": "array", "items": {"type": "string"}},
    "unparsed": {"type": "array", "items": {"type": "string"}},
})

_EMPTY_BRIEF = {
    "buildings": [], "splits": [], "program_totals": [], "paths": [], "fences": [], "props": [],
    "ignored": [], "unparsed": [],
}


BRIEF_INSTRUCTIONS = """You read a designer's notes about a hand-drawn site sketch and turn them into a brief the layout \
generator can follow. You never see the sketch. The notes only annotate or split what the sketch \
already draws; they never add a building the sketch does not show.

Rules:
- buildings: one entry per building the designer wants in this layout. `ref` is "b1", "b2", ... in \
the order mentioned. `name` is what the designer calls it (a proper name like "Rite Aid" or a use \
like "Ice cream shop"); copy it exactly, title case. `use` is a one or two word program word \
(pharmacy, cafe, apartments). `style` is a letter A to F only when the notes give one; the letters \
are opaque labels, never guess one from a material word. `floors` only when stated. `size_hint` \
keeps any stated size ("40 x 60 ft", "small"). `where` keeps the position phrase in the designer's \
own words ("north end", "corner on the avenue", "the long block"). `group` links buildings that share \
one drawn block (see splits).
- splits: when the notes divide one drawn block into several buildings ("the long block is three \
shops: a cafe, Rite Aid, a bakery"), make one split with a group id ("g1"), the block's position \
phrase, and the count, and give every member building that `group`, listed in the order the notes \
give them. Count is at least the number of members.
- program_totals: quantities to spread over the drawn blocks ("40 units of housing", "3 retail \
bays"). `across` lists the group ids or building refs they apply to, empty for all blocks.
- paths and fences: one entry per instruction about a walkway, sidewalk, road, trail, fence, railing \
or wall. `where` is the position phrase. Fill `material` / `type` only from the allowed values; \
translate words ("concrete sidewalk" is pavement_light, "chain link" is chain_link). \
`exclude` is true for "no fences", "remove the path".
- props: trees, benches, lamps and other small repeated objects. `type` is a plain singular noun. \
`arrangement` is row, cluster or scatter when the notes say so. `exclude` is true for "no trees".
- ignored: sentences about ground surfaces, grass, plazas, paving areas, water or ponds. Those are \
outside this brief's scope; list them here so the report can say so.
- unparsed: anything else you could not map, including comparisons and context ("like the bakery \
downtown", "next to the old church").
- Never invent a name, letter, count or size that the notes do not state. Empty arrays are fine.
"""


def build_site_context_text(site_ctx) -> str:
    """Short site facts the parser needs to read position words and sizes."""
    if not isinstance(site_ctx, dict):
        site_ctx = {}
    w = site_ctx.get("site_width_ft")
    h = site_ctx.get("site_height_ft")
    lines = ["Site facts:"]
    if w and h:
        long_axis = "up and down the sketch" if w >= h else "across the sketch"
        lines.append(f"- The parcel is {w:g} ft along the sketch's vertical axis and {h:g} ft across. "
                     f"Its long side runs {long_axis}.")
    else:
        lines.append("- Parcel dimensions are not known; the layout model estimates them from the sketch.")
    lines.append("- North is the top of the sketch as the designer drew it; left is west, right is east.")
    lines.append(f"- Allowed path materials: {', '.join(PATH_MATERIALS)}.")
    lines.append(f"- Allowed fence types: {', '.join(FENCE_TYPES)}.")
    lines.append(f"- Style letters: {', '.join(STYLE_IDS)}.")
    return "\n".join(lines)


def _client():
    key = os.getenv("ANTHROPIC_API_KEY")
    if not key:
        return None
    import anthropic
    return anthropic.Anthropic(api_key=key)


def _transform(schema):
    """Run the SDK's schema transform when it is available (adds string formats, strips
    unsupported keywords into descriptions). Falls back to the raw schema."""
    try:
        from anthropic.lib._parse._transform import transform_schema
        return transform_schema(schema)
    except Exception:
        return schema


def normalize_brief(raw) -> dict:
    """Make a parsed brief safe for the reconcile step: one shape, unique refs, valid letters,
    split counts at least the member count, blank names filled from the use word."""
    brief = {k: (raw.get(k) if isinstance(raw, dict) else None) for k in _EMPTY_BRIEF}
    for k, v in brief.items():
        if not isinstance(v, list):
            brief[k] = []

    seen = set()
    buildings = []
    for i, b in enumerate(brief["buildings"]):
        if not isinstance(b, dict):
            continue
        name = (b.get("name") or "").strip()
        use = (b.get("use") or "").strip() or None
        if not name and use:
            name = use[:1].upper() + use[1:]
        if not name:
            continue
        ref = (b.get("ref") or "").strip() or f"b{i + 1}"
        while ref in seen:
            ref = f"{ref}_"
        seen.add(ref)
        style = b.get("style")
        style = style.strip().upper() if isinstance(style, str) else None
        if style not in STYLE_IDS:
            style = None
        floors = b.get("floors")
        floors = int(floors) if isinstance(floors, (int, float)) and floors > 0 else None
        buildings.append({
            "ref": ref, "name": name, "use": use, "style": style, "floors": floors,
            "size_hint": (b.get("size_hint") or None),
            "where": (b.get("where") or None),
            "group": ((b.get("group") or "").strip() or None),
        })
    brief["buildings"] = buildings

    members = {}
    for b in buildings:
        if b["group"]:
            members.setdefault(b["group"], []).append(b["ref"])
    splits = []
    seen_groups = set()
    for s in brief["splits"]:
        if not isinstance(s, dict):
            continue
        group = (s.get("group") or "").strip()
        if not group or group in seen_groups:
            continue
        seen_groups.add(group)
        count = s.get("count")
        count = int(count) if isinstance(count, (int, float)) else 0
        count = max(count, len(members.get(group, [])), 1)
        splits.append({"group": group, "where": (s.get("where") or None), "count": count})
    # A group referenced by buildings but missing its split row still counts as a split.
    for group, refs in members.items():
        if group not in seen_groups and len(refs) > 1:
            splits.append({"group": group, "where": None, "count": len(refs)})
    brief["splits"] = splits

    def _bool(v):
        return bool(v) if isinstance(v, bool) else False

    brief["program_totals"] = [
        {"use": (p.get("use") or "").strip(), "quantity": p.get("quantity"),
         "unit": p.get("unit") if p.get("unit") in PROGRAM_UNITS else "units",
         "across": [a for a in (p.get("across") or []) if isinstance(a, str)]}
        for p in brief["program_totals"] if isinstance(p, dict) and (p.get("use") or "").strip()
    ]
    # Paths, fences and props get refs too ("p1", "f1", "x1") so the layout model can tag the
    # entry that satisfies each and the reconcile step can find it again.
    brief["paths"] = [
        {"ref": f"p{i + 1}", "where": (p.get("where") or "").strip(),
         "material": p.get("material") if p.get("material") in PATH_MATERIALS else None,
         "width_ft": p.get("width_ft") if isinstance(p.get("width_ft"), (int, float)) else None,
         "exclude": _bool(p.get("exclude"))}
        for i, p in enumerate(p for p in brief["paths"] if isinstance(p, dict))
    ]
    brief["fences"] = [
        {"ref": f"f{i + 1}", "where": (f.get("where") or "").strip(),
         "type": f.get("type") if f.get("type") in FENCE_TYPES else None,
         "height_ft": f.get("height_ft") if isinstance(f.get("height_ft"), (int, float)) else None,
         "exclude": _bool(f.get("exclude"))}
        for i, f in enumerate(f for f in brief["fences"] if isinstance(f, dict))
    ]
    brief["props"] = [
        {"ref": f"x{i + 1}", "type": (p.get("type") or "").strip().lower(),
         "where": (p.get("where") or None),
         "arrangement": p.get("arrangement") if p.get("arrangement") in PROP_ARRANGEMENTS else None,
         "count": int(p["count"]) if isinstance(p.get("count"), (int, float)) and p["count"] > 0 else None,
         "exclude": _bool(p.get("exclude"))}
        for i, p in enumerate(p for p in brief["props"] if isinstance(p, dict) and (p.get("type") or "").strip())
    ]
    brief["ignored"] = [s for s in brief["ignored"] if isinstance(s, str) and s.strip()]
    brief["unparsed"] = [s for s in brief["unparsed"] if isinstance(s, str) and s.strip()]
    return brief


def is_empty_brief(brief) -> bool:
    if not isinstance(brief, dict):
        return True
    return not any(brief.get(k) for k in ("buildings", "splits", "program_totals", "paths", "fences", "props"))


def parse_brief(notes, site_ctx=None, client=None) -> dict:
    """Returns {"brief": dict | None, "error": str | None, "model": str}.

    Empty notes: no call, brief None, no error. Any failure: brief None plus the error text.
    """
    result = {"brief": None, "error": None, "model": BRIEF_MODEL_ID}
    text = notes.strip() if isinstance(notes, str) else ""
    if not text:
        return result

    client = client or _client()
    if client is None:
        result["error"] = "ANTHROPIC_API_KEY is not set; brief pass skipped."
        return result

    user_text = f"{build_site_context_text(site_ctx)}\n\nDesigner notes:\n{text}"
    schema = _transform(BRIEF_SCHEMA)
    last_error = None
    for attempt in range(BRIEF_MAX_RETRIES):
        try:
            response = client.messages.create(
                model=BRIEF_MODEL_ID,
                max_tokens=BRIEF_MAX_TOKENS,
                thinking={"type": "adaptive"},
                output_config={"effort": "low", "format": {"type": "json_schema", "schema": schema}},
                system=BRIEF_INSTRUCTIONS,
                messages=[{"role": "user", "content": user_text}],
            )
            if response.stop_reason == "refusal":
                result["error"] = "Brief pass refused by the model."
                return result
            if response.stop_reason == "max_tokens":
                result["error"] = "Brief pass output was truncated."
                return result
            raw = "".join(b.text for b in response.content if getattr(b, "type", None) == "text")
            start, end = raw.find("{"), raw.rfind("}")
            if start < 0 or end < 0:
                result["error"] = "Brief pass returned no JSON."
                return result
            result["brief"] = normalize_brief(json.loads(raw[start:end + 1]))
            return result
        except json.JSONDecodeError as exc:
            result["error"] = f"Brief pass returned invalid JSON: {exc.msg}"
            return result
        except Exception as exc:  # API errors: retry the transient ones, give up on the rest
            last_error = str(exc)
            if any(code in last_error for code in ("429", "503", "529")):
                time.sleep(20 + random.uniform(2, 8))
                continue
            break
    result["error"] = f"Brief pass failed: {last_error}"
    return result
