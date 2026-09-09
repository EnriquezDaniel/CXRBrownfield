"""
JSON schema for the layout the model returns (prompts/site_parsing.md, "Required Output Shape").

Used twice: as the structured-output format on the Claude layout call (layout_prompt.py), so the
response is grammar-constrained to this shape, and as a warn-only jsonschema check on the server
(server.py) for whichever path produced the JSON. Optional fields are left out of `required`; the
server also strips null-valued keys, so a nulled optional never reaches Unity's typed parse.

Pure data, no imports beyond the standard library, so the server can load it without the model SDKs.
"""

PATH_MATERIALS = ["pavement_dark", "pavement_light", "brick", "dirt", "asphalt"]
FENCE_TYPES = ["picket", "lattice", "chain_link", "wood_privacy", "wrought_iron"]
TERRAIN_TYPES = ["grass", "pavement", "asphalt", "plaza", "water", "planting_bed", "sand"]
OBJECT_TYPES = ["building", "structure", "pavilion", "kiosk"]
STYLE_IDS = ["A", "B", "C", "D", "E", "F"]


def _obj(props, required):
    return {"type": "object", "properties": props, "required": required, "additionalProperties": False}


_INT_BOX = {"type": "array", "items": {"type": "integer"}}          # [ymin, xmin, ymax, xmax]
_INT_PT = {"type": "array", "items": {"type": "integer"}}           # [y, x]
_INT_PTS = {"type": "array", "items": _INT_PT}                      # [[y, x], ...]
_NUM_PTS = {"type": "array", "items": {"type": "array", "items": {"type": "number"}}}

SITE_SCALE_SCHEMA = _obj({
    "site_width_ft":     {"type": "number"},
    "site_height_ft":    {"type": "number"},
    "normalized_canvas": {"type": "array", "items": {"type": "integer"}},
    "lot_boundary":      _NUM_PTS,
    "scale_note":        {"type": "string"},
}, ["site_width_ft", "site_height_ft", "normalized_canvas", "lot_boundary", "scale_note"])

TERRAIN_ZONE_SCHEMA = _obj({
    "area_name":      {"type": "string"},
    "semantic_tag":   {"type": "string"},
    "terrain_type":   {"type": "string", "enum": TERRAIN_TYPES},
    "bounding_box":   _INT_BOX,
    "approx_sq_ft":   {"type": "number"},
    "unity_strategy": {"type": "string", "enum": ["paint_terrain"]},
}, ["area_name", "semantic_tag", "terrain_type", "bounding_box", "approx_sq_ft", "unity_strategy"])

PATH_SCHEMA = _obj({
    "area_name":     {"type": "string"},
    "semantic_tag":  {"type": "string"},
    "path_material": {"type": "string", "enum": PATH_MATERIALS},
    "width_ft":      {"type": "number"},
    "points":        _INT_PTS,
    "smoothing":     {"type": "number"},
    "brief_ref":     {"type": "string"},
}, ["area_name", "semantic_tag", "path_material", "width_ft", "points"])

FENCE_SCHEMA = _obj({
    "area_name":    {"type": "string"},
    "semantic_tag": {"type": "string"},
    "fence_type":   {"type": "string", "enum": FENCE_TYPES},
    "points":       _INT_PTS,
    "height_ft":    {"type": "number"},
    "smoothing":    {"type": "number"},
    "brief_ref":    {"type": "string"},
}, ["area_name", "semantic_tag", "fence_type", "points"])

GENERATED_BUILDING_SCHEMA = _obj({
    "area_name":      {"type": "string"},
    "semantic_tag":   {"type": "string"},
    "bounding_box":   _INT_BOX,
    "center_point":   _INT_PT,
    "rotation_y_deg": {"type": "number"},
    "floors":         {"type": "integer"},
    "approx_sq_ft":   {"type": "number"},
    "unity_strategy": {"type": "string", "enum": ["modular_prefab"]},
    "style":          {"type": "string", "enum": STYLE_IDS},
    "sign":           {"type": "string"},
    "corner_angles":  {"type": "array", "items": {"type": "number"}},
    "brief_ref":      {"type": "string"},
}, ["area_name", "semantic_tag", "bounding_box", "center_point", "rotation_y_deg", "floors",
    "approx_sq_ft", "unity_strategy"])

GENERATED_OBJECT_SCHEMA = _obj({
    "area_name":      {"type": "string"},
    "semantic_tag":   {"type": "string"},
    "object_type":    {"type": "string", "enum": OBJECT_TYPES},
    "bounding_box":   _INT_BOX,
    "center_point":   _INT_PT,
    "rotation_y_deg": {"type": "number"},
    "approx_sq_ft":   {"type": "number"},
    "target_dimensions_ft": _obj({
        "width_ft":  {"type": "number"},
        "depth_ft":  {"type": "number"},
        "height_ft": {"type": "number"},
    }, ["width_ft", "depth_ft", "height_ft"]),
    "unity_strategy": {"type": "string", "enum": ["box_primitive"]},
    "brief_ref":      {"type": "string"},
}, ["area_name", "semantic_tag", "object_type", "bounding_box", "center_point", "rotation_y_deg",
    "approx_sq_ft", "target_dimensions_ft", "unity_strategy"])

PREFAB_INSTANCE_SCHEMA = _obj({
    "area_name":        {"type": "string"},
    "semantic_tag":     {"type": "string"},
    "prefab_type":      {"type": "string"},
    "center_point":     _INT_PT,
    "footprint_box":    _INT_BOX,
    "rotation_deg":     {"type": "number"},
    "scale_multiplier": {"type": "number"},
    "unity_strategy":   {"type": "string", "enum": ["place_prefab"]},
    "brief_ref":        {"type": "string"},
}, ["area_name", "semantic_tag", "prefab_type", "center_point", "footprint_box", "rotation_deg",
    "scale_multiplier", "unity_strategy"])

LAYOUT_SCHEMA = _obj({
    "site_scale":          SITE_SCALE_SCHEMA,
    "terrain_zones":       {"type": "array", "items": TERRAIN_ZONE_SCHEMA},
    "paths":               {"type": "array", "items": PATH_SCHEMA},
    "fences":              {"type": "array", "items": FENCE_SCHEMA},
    "generated_buildings": {"type": "array", "items": GENERATED_BUILDING_SCHEMA},
    "generated_objects":   {"type": "array", "items": GENERATED_OBJECT_SCHEMA},
    "prefab_instances":    {"type": "array", "items": PREFAB_INSTANCE_SCHEMA},
}, ["site_scale", "terrain_zones", "paths", "fences", "generated_buildings", "generated_objects",
    "prefab_instances"])

# What the Claude layout call actually sends as its structured-output format. The full
# LAYOUT_SCHEMA (about 4.8 KB) is rejected by the API's grammar compiler ("compiled grammar is too
# large"; every trimmed variant above ~4 KB was too, probed 2026-09-09), so only the two parts the
# designer brief depends on are typed: site_scale and generated_buildings (names, style enum,
# floors, brief_ref). The other categories stay free objects governed by the prompt; the server
# still validates the whole layout against LAYOUT_SCHEMA afterwards.
LAYOUT_OUTPUT_SCHEMA = _obj({
    "site_scale":          SITE_SCALE_SCHEMA,
    "terrain_zones":       {"type": "array", "items": {"type": "object"}},
    "paths":               {"type": "array", "items": {"type": "object"}},
    "fences":              {"type": "array", "items": {"type": "object"}},
    "generated_buildings": {"type": "array", "items": GENERATED_BUILDING_SCHEMA},
    "generated_objects":   {"type": "array", "items": {"type": "object"}},
    "prefab_instances":    {"type": "array", "items": {"type": "object"}},
}, ["site_scale", "terrain_zones", "paths", "fences", "generated_buildings", "generated_objects",
    "prefab_instances"])

# Top-level lists that may carry brief_ref on their entries.
BRIEF_REF_CATEGORIES = ("generated_buildings", "generated_objects", "paths", "fences", "prefab_instances")
