"""
Named site presets — the single source of truth for real-world parcels.

Each preset carries the authoritative lot boundary (a polygon in normalized
[y, x] canvas space, 0-1000) plus the parcel's real-world dimensions in feet.
These feed `layout_prompt.process_sketch` as the placement constraint + scale,
and the same lot_boundary is echoed into the generated JSON's `site_scale` so
Unity can shape the terrain to the parcel (see LayoutConverter / WorldRenderer).

`get_site_preset(name)` returns None for the reserved name "auto" (and for any
unknown name): "auto" means no preset boundary — the LLM traces the parcel
outline from the sketch itself. Callers pass the resulting `lot_boundary=None`
straight through; layout_prompt's runtime context switches to auto-derive mode.

`lot_boundary` follows the same convention every other coordinate array uses
(see prompts/site_parsing.md): an ordered list of [y, x] vertices, 0-1000.
"""

# Reserved name: no boundary, let the model trace the parcel from the sketch.
AUTO = "auto"

SITE_PRESETS = {
    # Site A — pentagonal waterfront parcel along the Bronx River, wider at the
    # north and tapering southeast toward the river bank (~220 x 180 ft).
    "site_a_bronx": {
        "label": "Site A — Bronx River waterfront",
        "lot_boundary": [
            [50, 150],   # northwest corner
            [80, 700],   # northeast corner
            [320, 620],  # east side mid (angles inward)
            [420, 350],  # southeast tip
            [280, 80],   # west side mid (river bank)
        ],
        "site_width_ft": 220,
        "site_height_ft": 180,
        "scale_note": (
            "Pentagonal Bronx River waterfront parcel. All placements must fall "
            "within lot_boundary; area outside the parcel is water."
        ),
    },

    # Roosevelt Island Steam Plant — parcel at the island's southern tip, long
    # axis running north-south, water on three sides (~30,000 sq ft). The polygon
    # is an APPROXIMATION of the peninsula tip (narrowing toward the south); refine
    # against the survey/map if a precise boundary becomes available.
    "roosevelt_island": {
        "label": "Roosevelt Island Steam Plant (southern tip)",
        "lot_boundary": [
            [100, 350],  # northwest
            [100, 650],  # northeast
            [500, 700],  # east mid
            [900, 580],  # southeast tip
            [900, 420],  # southwest tip
            [500, 300],  # west mid
        ],
        "site_width_ft": 120,    # east-west extent
        "site_height_ft": 250,   # north-south extent (120 x 250 ~= 30,000 sq ft)
        "scale_note": (
            "Roosevelt Island Steam Plant parcel at the island's southern tip, "
            "water on three sides. All placements must fall within lot_boundary; "
            "area outside the parcel is water."
        ),
    },

    # Westchester Avenue — a long, thin street-front strip, about 140 x 20 m.
    # The full canvas is the parcel: the long side runs down the sketch (north
    # at the top), so site_width_ft carries the 140 m and site_height_ft the 20 m.
    "westchester_avenue": {
        "label": "Westchester Avenue — 140 x 20 m strip",
        "lot_boundary": [
            [0, 0],        # northwest corner
            [0, 1000],     # northeast corner
            [1000, 1000],  # southeast corner
            [1000, 0],     # southwest corner
        ],
        "site_width_ft": 459,    # long side: sketch vertical / Unity X (140 m)
        "site_height_ft": 66,    # short side: sketch across / Unity Z (20 m)
        "scale_note": (
            "Westchester Avenue strip, 459 x 66 ft, north at the top of the sketch. "
            "All placements must fall within lot_boundary."
        ),
    },
}


def list_site_presets():
    """Return preset metadata for the UI: [{name, label, site_width_ft, site_height_ft}, ...].

    The reserved "auto" entry is included first so the dropdown can offer
    sketch-traced generation without a hard-coded boundary.
    """
    rows = [{
        "name": AUTO,
        "label": "Auto — trace boundary from sketch",
        "site_width_ft": None,
        "site_height_ft": None,
    }]
    for name, preset in SITE_PRESETS.items():
        rows.append({
            "name": name,
            "label": preset.get("label", name),
            "site_width_ft": preset.get("site_width_ft"),
            "site_height_ft": preset.get("site_height_ft"),
        })
    return rows


def get_site_preset(name):
    """Return the preset dict for `name`, or None for "auto"/unknown/empty names.

    None signals auto-derive mode (no authoritative boundary) to the caller.
    """
    if not name or name == AUTO:
        return None
    return SITE_PRESETS.get(name)
