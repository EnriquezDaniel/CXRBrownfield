"""
Pins layout_reconcile.py and brief_prompt.normalize_brief. Standard-library unittest, no API key:

    .venv/Scripts/python.exe -m unittest tests.test_reconcile -v      (repo root)
"""
import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from layout_reconcile import (reconcile_brief, split_box, tiles_for, strip_null_fields,   # noqa: E402
                              is_quarter_turn)
from brief_prompt import normalize_brief, is_empty_brief                                 # noqa: E402

# Westchester strip: 459 ft down the sketch (index 0), 66 ft across (index 1).
W_FT, H_FT = 459.0, 66.0


def building(name, box, **extra):
    ymin, xmin, ymax, xmax = box
    b = {
        "area_name": name, "semantic_tag": "mixed_use", "bounding_box": list(box),
        "center_point": [(ymin + ymax) // 2, (xmin + xmax) // 2], "rotation_y_deg": 0,
        "floors": 2, "approx_sq_ft": 1000, "unity_strategy": "modular_prefab",
    }
    b.update(extra)
    return b


def layout(buildings=(), paths=(), fences=(), prefabs=(), objects=()):
    return {
        "site_scale": {"site_width_ft": W_FT, "site_height_ft": H_FT,
                       "normalized_canvas": [0, 0, 1000, 1000],
                       "lot_boundary": [[0, 0], [0, 1000], [1000, 1000], [1000, 0]], "scale_note": ""},
        "terrain_zones": [], "paths": list(paths), "fences": list(fences),
        "generated_buildings": list(buildings), "generated_objects": list(objects),
        "prefab_instances": list(prefabs),
    }


def brief(**kw):
    return normalize_brief({"buildings": [], "splits": [], "program_totals": [], "paths": [],
                            "fences": [], "props": [], "ignored": [], "unparsed": [], **kw})


class SplitMath(unittest.TestCase):
    def test_tiles_match_unity_rounding(self):
        # 12 tiles = 48 m = 157.48 ft along index 0 -> 157.48 / 0.459 = 343.1 units
        self.assertEqual(tiles_for(343, W_FT / 1000), 12)
        self.assertEqual(tiles_for(1, W_FT / 1000), 1)          # never below one tile

    def test_twelve_tiles_into_three_and_five(self):
        box = [100, 100, 443, 900]                                 # 343 units tall = 12 tiles
        pieces, info = split_box(box, 3, W_FT, H_FT)
        self.assertEqual(info["axis"], 0)
        self.assertEqual(info["tiles"], [4, 4, 4])
        self.assertEqual(len(pieces), 3)
        pieces5, info5 = split_box(box, 5, W_FT, H_FT)
        self.assertEqual(info5["tiles"], [3, 3, 2, 2, 2])
        # touching, in order, and covering exactly the block
        for a, b in zip(pieces5, pieces5[1:]):
            self.assertEqual(a[2], b[0])
        self.assertEqual(pieces5[0][0], 100)
        self.assertEqual(pieces5[-1][2], 443)
        # each piece recovers its tile count under Unity's rounding
        for p, t in zip(pieces5, info5["tiles"]):
            self.assertEqual(tiles_for(p[2] - p[0], W_FT / 1000), t)

    def test_long_axis_is_chosen_in_feet_not_units(self):
        # 300 units across (19.8 ft) vs 100 units down (45.9 ft): down is longer in feet
        pieces, info = split_box([0, 0, 100, 300], 2, W_FT, H_FT)
        self.assertEqual(info["axis"], 0)

    def test_block_shorter_than_count_is_reduced(self):
        # 57 units down = 26 ft = 2 tiles; 200 units across = 13 ft = 1 tile, so down is the long side
        pieces, info = split_box([100, 100, 157, 300], 4, W_FT, H_FT)
        self.assertEqual(info["n_eff"], 2)
        self.assertEqual(len(pieces), 2)

    def test_bad_input(self):
        self.assertIsNone(split_box([0, 0, 0, 0], 2, W_FT, H_FT)[0])
        self.assertIsNone(split_box([0, 0, 100, 100], 2, None, H_FT)[0])

    def test_quarter_turn(self):
        for d in (0, 90, 180, 270, 359.5, 89.5):
            self.assertTrue(is_quarter_turn(d), d)
        for d in (30, 45, 88):
            self.assertFalse(is_quarter_turn(d), d)


class Linking(unittest.TestCase):
    def test_link_by_ref_name_and_use(self):
        data = layout([
            building("Block A", [0, 0, 100, 900], brief_ref="b1"),
            building("Rite Aid", [100, 0, 200, 900]),
            building("Corner Cafe", [200, 0, 300, 900], semantic_tag="cafe"),
            building("Something", [300, 0, 400, 900], style="D"),
        ])
        b = brief(buildings=[
            {"ref": "b1", "name": "Theater", "style": "a", "floors": 5},
            {"ref": "b2", "name": "rite aid", "style": None},
            {"ref": "b3", "name": "The Cafe", "use": "cafe"},
        ])
        rep = reconcile_brief(data, b)
        self.assertEqual(rep["satisfied"], ["b1", "b2", "b3"])
        self.assertEqual(rep["missing"], [])
        names = [x["area_name"] for x in data["generated_buildings"]]
        self.assertEqual(names, ["Theater", "rite aid", "The Cafe", "Something"])
        self.assertEqual(data["generated_buildings"][0]["style"], "A")
        self.assertEqual(data["generated_buildings"][0]["floors"], 5)
        self.assertEqual(data["generated_buildings"][2]["brief_ref"], "b3")
        # invented letter on an unmatched building is dropped
        self.assertNotIn("style", data["generated_buildings"][3])
        self.assertEqual(rep["extra_unmatched"], ["Something"])

    def test_missing_building_is_reported_not_invented(self):
        data = layout([building("Only One", [0, 0, 100, 900])])
        b = brief(buildings=[{"ref": "b1", "name": "Only One"}, {"ref": "b2", "name": "Ghost Kiosk"}])
        rep = reconcile_brief(data, b)
        self.assertEqual(len(data["generated_buildings"]), 1)
        self.assertEqual(rep["missing"][0]["ref"], "b2")
        self.assertTrue(any("Ghost Kiosk" in w for w in rep["warnings"]))

    def test_ambiguous_use_does_not_link(self):
        data = layout([building("Cafe North", [0, 0, 100, 900]), building("Cafe South", [100, 0, 200, 900])])
        b = brief(buildings=[{"ref": "b1", "name": "Cafe", "use": "cafe"}])
        rep = reconcile_brief(data, b)
        self.assertEqual(rep["missing"][0]["ref"], "b1")

    def test_named_object_links_too(self):
        obj = {"area_name": "Kiosk", "semantic_tag": "kiosk", "object_type": "kiosk",
               "bounding_box": [0, 0, 50, 50], "center_point": [25, 25], "rotation_y_deg": 0,
               "approx_sq_ft": 100, "target_dimensions_ft": {"width_ft": 10, "depth_ft": 10, "height_ft": 12},
               "unity_strategy": "box_primitive"}
        data = layout(objects=[obj])
        b = brief(buildings=[{"ref": "b1", "name": "Coffee Kiosk", "use": "kiosk", "style": "B"}])
        rep = reconcile_brief(data, b)
        self.assertEqual(rep["satisfied"], ["b1"])
        self.assertEqual(data["generated_objects"][0]["area_name"], "Coffee Kiosk")
        self.assertNotIn("style", data["generated_objects"][0])


class Splits(unittest.TestCase):
    def members(self):
        return [
            {"ref": "b1", "name": "Cafe", "group": "g1"},
            {"ref": "b2", "name": "Rite Aid", "group": "g1", "style": "C", "floors": 4},
            {"ref": "b3", "name": "Bakery", "group": "g1"},
        ]

    def test_model_kept_one_block_server_splits_it(self):
        data = layout([building("Long Block", [100, 100, 443, 900], floors=3, sign="SHOPS", corner_angles=[0, 0, 30, 0]),
                       building("Other", [500, 100, 600, 900])])
        b = brief(buildings=self.members(), splits=[{"group": "g1", "count": 3}])
        # nothing links by name; the model's block has to be found through the group's use words
        data["generated_buildings"][0]["area_name"] = "Bakery"
        rep = reconcile_brief(data, b)
        bl = data["generated_buildings"]
        self.assertEqual([x["area_name"] for x in bl], ["Cafe", "Rite Aid", "Bakery", "Other"])
        self.assertEqual(rep["splits"][0]["outcome"], "split")
        self.assertEqual(rep["splits"][0]["tiles"], [4, 4, 4])
        self.assertEqual(bl[0]["bounding_box"][0], 100)
        self.assertEqual(bl[2]["bounding_box"][2], 443)
        self.assertEqual(bl[0]["bounding_box"][2], bl[1]["bounding_box"][0])
        self.assertEqual(bl[1]["style"], "C")
        self.assertEqual(bl[1]["floors"], 4)
        self.assertEqual(bl[0]["floors"], 3, "unspecified floors inherit the drawn block")
        for x in bl[:3]:
            self.assertNotIn("sign", x)
            self.assertNotIn("corner_angles", x)
            self.assertEqual(x["center_point"][0], (x["bounding_box"][0] + x["bounding_box"][2]) // 2)
        self.assertTrue(rep["splits"][0].get("corner_angles_stripped"))
        self.assertEqual(rep["satisfied"], ["b1", "b2", "b3"])

    def test_partial_split_uses_union_box(self):
        data = layout([building("Cafe", [100, 100, 214, 900]), building("Rite Aid", [214, 100, 443, 900])])
        b = brief(buildings=self.members(), splits=[{"group": "g1", "count": 3}])
        rep = reconcile_brief(data, b)
        bl = data["generated_buildings"]
        self.assertEqual(len(bl), 3)
        self.assertEqual([x["area_name"] for x in bl], ["Cafe", "Rite Aid", "Bakery"])
        self.assertEqual(bl[0]["bounding_box"][0], 100)
        self.assertEqual(bl[2]["bounding_box"][2], 443)

    def test_model_already_split_is_left_alone(self):
        data = layout([building("Cafe", [100, 100, 214, 900]), building("Rite Aid", [214, 100, 328, 900]),
                       building("Bakery", [328, 100, 443, 900])])
        before = copy.deepcopy(data)
        b = brief(buildings=self.members(), splits=[{"group": "g1", "count": 3}])
        rep = reconcile_brief(data, b)
        self.assertEqual(rep["splits"][0]["outcome"], "satisfied")
        for x, y in zip(before["generated_buildings"], data["generated_buildings"]):
            self.assertEqual(x["bounding_box"], y["bounding_box"])

    def test_rotated_block_is_not_split(self):
        data = layout([building("Cafe", [100, 100, 443, 900], rotation_y_deg=30)])
        b = brief(buildings=self.members(), splits=[{"group": "g1", "count": 3}])
        rep = reconcile_brief(data, b)
        self.assertEqual(rep["splits"][0]["outcome"], "skipped_rotated")
        self.assertEqual(len(data["generated_buildings"]), 1)

    def test_short_block_reduces_and_reports(self):
        data = layout([building("Cafe", [100, 100, 157, 300])])            # 2 tiles down, 1 across
        b = brief(buildings=self.members(), splits=[{"group": "g1", "count": 3}])
        rep = reconcile_brief(data, b)
        self.assertEqual(rep["splits"][0]["outcome"], "split_reduced")
        self.assertEqual(len(data["generated_buildings"]), 2)
        self.assertEqual(rep["missing"][0], {"ref": "b3", "name": "Bakery", "reason": "split_reduced"})

    def test_quarter_turn_block_keeps_rotation(self):
        data = layout([building("Cafe", [100, 100, 443, 900], rotation_y_deg=90)])
        b = brief(buildings=self.members(), splits=[{"group": "g1", "count": 3}])
        reconcile_brief(data, b)
        self.assertTrue(all(x["rotation_y_deg"] == 90 for x in data["generated_buildings"]))


class LinearAndProps(unittest.TestCase):
    def path(self, name, mat, width=6, **extra):
        p = {"area_name": name, "semantic_tag": "walk", "path_material": mat, "width_ft": width,
             "points": [[0, 50], [900, 50]]}
        p.update(extra)
        return p

    def fence(self, name, kind, **extra):
        f = {"area_name": name, "semantic_tag": "fence", "fence_type": kind, "points": [[0, 0], [900, 0]]}
        f.update(extra)
        return f

    def prefab(self, name, ptype):
        return {"area_name": name, "semantic_tag": ptype, "prefab_type": ptype, "center_point": [10, 10],
                "footprint_box": [0, 0, 20, 20], "rotation_deg": 0, "scale_multiplier": 1.0,
                "unity_strategy": "place_prefab"}

    def test_path_material_and_width_enforced(self):
        data = layout(paths=[self.path("Walk", "pavement_light", 6)])
        b = brief(paths=[{"where": "along the avenue", "material": "brick", "width_ft": 8}])
        rep = reconcile_brief(data, b)
        self.assertEqual(rep["paths"][0]["outcome"], "satisfied")
        self.assertEqual(data["paths"][0]["path_material"], "brick")
        self.assertEqual(data["paths"][0]["width_ft"], 8)
        self.assertEqual(data["paths"][0]["brief_ref"], "p1")

    def test_fence_linked_by_ref_and_type_enforced(self):
        data = layout(fences=[self.fence("East", "picket", brief_ref="f1"), self.fence("West", "picket")])
        b = brief(fences=[{"where": "east edge", "type": "chain_link", "height_ft": 8}])
        rep = reconcile_brief(data, b)
        self.assertEqual(data["fences"][0]["fence_type"], "chain_link")
        self.assertEqual(data["fences"][0]["height_ft"], 8)
        self.assertEqual(data["fences"][1]["fence_type"], "picket")
        self.assertEqual(rep["fences"][0]["outcome"], "satisfied")

    def test_exclusions(self):
        data = layout(paths=[self.path("A", "dirt"), self.path("B", "brick")],
                      fences=[self.fence("F", "picket")],
                      prefabs=[self.prefab("Tree 1", "oak_tree"), self.prefab("Bench", "bench")])
        b = brief(paths=[{"where": "", "material": "dirt", "exclude": True}],
                  fences=[{"where": "", "type": None, "exclude": True}],
                  props=[{"type": "tree", "exclude": True}, {"type": "bench"}])
        rep = reconcile_brief(data, b)
        self.assertEqual([p["area_name"] for p in data["paths"]], ["B"])
        self.assertEqual(data["fences"], [])
        self.assertEqual([p["prefab_type"] for p in data["prefab_instances"]], ["bench"])
        self.assertEqual(rep["props"][1]["outcome"], "satisfied")
        self.assertTrue(rep["changed"])

    def test_placed_exclusion_without_kind_removes_nothing(self):
        data = layout(fences=[self.fence("F", "picket")])
        b = brief(fences=[{"where": "east side", "type": None, "exclude": True}])
        reconcile_brief(data, b)
        self.assertEqual(len(data["fences"]), 1)

    def test_prop_count_shortfall_and_missing(self):
        data = layout(prefabs=[self.prefab("Tree 1", "tree")])
        b = brief(props=[{"type": "tree", "count": 4, "arrangement": "row"}, {"type": "lamp"}])
        rep = reconcile_brief(data, b)
        self.assertEqual(rep["props"][0]["outcome"], "fewer")
        self.assertEqual(rep["props"][1]["outcome"], "missing")


class Normalize(unittest.TestCase):
    def test_normalize_fills_defaults_and_refs(self):
        raw = {"buildings": [{"name": "", "use": "cafe", "style": "c", "floors": 2.0, "group": "g1"},
                             {"ref": "b1", "name": "Rite Aid", "style": "Z", "group": "g1"}],
               "splits": [{"group": "g1", "count": 1}],
               "paths": [{"where": "avenue", "material": "brick", "exclude": None}],
               "props": [{"type": " Tree ", "count": 3.0}]}
        b = normalize_brief(raw)
        self.assertEqual([x["ref"] for x in b["buildings"]], ["b1", "b1_"])
        self.assertEqual(b["buildings"][0]["name"], "Cafe")
        self.assertEqual(b["buildings"][0]["style"], "C")
        self.assertIsNone(b["buildings"][1]["style"])
        self.assertEqual(b["splits"][0]["count"], 2, "count is at least the member count")
        self.assertEqual(b["paths"][0]["ref"], "p1")
        self.assertFalse(b["paths"][0]["exclude"])
        self.assertEqual(b["props"][0]["type"], "tree")
        self.assertEqual(b["props"][0]["count"], 3)
        self.assertFalse(is_empty_brief(b))
        self.assertTrue(is_empty_brief(normalize_brief({})))

    def test_group_without_split_row_becomes_one(self):
        b = normalize_brief({"buildings": [{"ref": "b1", "name": "A", "group": "g"},
                                           {"ref": "b2", "name": "B", "group": "g"}]})
        self.assertEqual(b["splits"], [{"group": "g", "where": None, "count": 2}])


class Nulls(unittest.TestCase):
    def test_strip_null_fields(self):
        data = layout([building("A", [0, 0, 10, 10], style=None, sign=None)])
        data["site_scale"]["scale_note"] = None
        strip_null_fields(data)
        self.assertNotIn("style", data["generated_buildings"][0])
        self.assertNotIn("scale_note", data["site_scale"])


if __name__ == "__main__":
    unittest.main()
