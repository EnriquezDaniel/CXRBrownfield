"""Contract tests for POST/GET /api/buildings as used by pasted building copies.

A paste in Unity clones the BuildingDef client-side (BuildingCopies) and posts it with the next
environment save: its own client id, dedupe off, hiddenCopy set. The server's spaced name rule is
the backstop when another client took the name meanwhile.

Same harness as tests.test_active: Flask's test client against a temp directory, never server/data/.

    .venv/Scripts/python.exe -m unittest tests.test_buildings
"""
import sys
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "server"))   # server.py is a script, not a package

import server as srv  # noqa: E402


class BuildingCopyTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        data = Path(self._tmp.name)
        env_dir = data / "environments"
        bld_dir = data / "buildings"
        env_kinds = {"user": env_dir / "user", "generated": env_dir / "generated"}
        bld_kinds = {"static": bld_dir / "static", "cached": bld_dir / "cached"}
        for d in [*env_kinds.values(), *bld_kinds.values(),
                  data / "_archive" / "environments", data / "_archive" / "buildings"]:
            d.mkdir(parents=True)

        patches = [
            mock.patch.object(srv, "DATA_DIR", data),
            mock.patch.object(srv, "ENVIRONMENTS_DIR", env_dir),
            mock.patch.object(srv, "BUILDINGS_DIR", bld_dir),
            mock.patch.object(srv, "ARCHIVE_DIR", data / "_archive"),
            mock.patch.object(srv, "ACTIVE_POINTER_FILE", data / "active.json"),
            mock.patch.dict(srv.ENV_KINDS, env_kinds, clear=True),
            mock.patch.dict(srv.BLDG_KINDS, bld_kinds, clear=True),
        ]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        self.addCleanup(self._tmp.cleanup)

        self.client = srv.app.test_client()

    # -- helpers --

    def post(self, name, **extra):
        body = {"name": name, "tiles": [{"gridX": 0, "gridZ": 0, "floor": 0}], **extra}
        r = self.client.post("/api/buildings", json=body)
        self.assertIn(r.status_code, (200, 201), r.get_data(as_text=True))
        return r.get_json()

    def summaries(self):
        r = self.client.get("/api/buildings")
        self.assertEqual(r.status_code, 200)
        return {s["name"]: s for s in r.get_json()["buildings"]}

    # -- tests --

    def test_redirected_away_from_real_data(self):
        real = ROOT / "server" / "data"
        self.assertNotEqual(srv.BUILDINGS_DIR.resolve(), (real / "buildings").resolve())

    def test_first_record_keeps_its_name(self):
        self.assertEqual(self.post("Coffee Shop")["name"], "Coffee Shop")

    def test_taken_name_gets_a_spaced_number(self):
        self.post("Coffee Shop")
        self.assertEqual(self.post("Coffee Shop", dedupe=False)["name"], "Coffee Shop 2")
        self.assertEqual(self.post("Coffee Shop", dedupe=False)["name"], "Coffee Shop 3")

    def test_taken_numbered_name_counts_on_from_its_number(self):
        self.post("Coffee Shop 2")
        self.assertEqual(self.post("Coffee Shop 2", dedupe=False)["name"], "Coffee Shop 3")

    def test_name_match_ignores_case(self):
        self.post("Coffee Shop")
        self.assertEqual(self.post("coffee shop", dedupe=False)["name"], "coffee shop 2")

    def test_identical_clone_dedupes_unless_told_not_to(self):
        first = self.post("Shed")
        again = self.post("Shed")
        self.assertEqual(again["id"], first["id"])
        self.assertTrue(again.get("deduped"))

    def test_clone_keeps_its_client_id(self):
        self.post("Shed")
        cid = str(uuid.uuid4())
        made = self.post("Shed 2", id=cid, dedupe=False, hiddenCopy=True)
        self.assertEqual(made["id"], cid)
        self.assertEqual(made["name"], "Shed 2")
        r = self.client.get(f"/api/buildings/{cid}")
        self.assertEqual(r.status_code, 200)
        self.assertTrue(r.get_json()["hiddenCopy"])

    def test_summary_carries_hidden_copy(self):
        self.post("Shed")
        self.post("Shed 2", dedupe=False, hiddenCopy=True)
        rows = self.summaries()
        self.assertFalse(rows["Shed"]["hiddenCopy"])
        self.assertTrue(rows["Shed 2"]["hiddenCopy"])

    def test_rename_put_clears_hidden_copy(self):
        made = self.post("Shed 2", dedupe=False, hiddenCopy=True)
        r = self.client.put(f"/api/buildings/{made['id']}",
                            json={"name": "Bike Store", "hiddenCopy": False, "tiles": []})
        self.assertEqual(r.status_code, 200, r.get_data(as_text=True))
        self.assertFalse(self.summaries()["Bike Store"]["hiddenCopy"])

    def test_environment_names_keep_the_bare_number(self):
        self.client.post("/api/environments", json={"name": "Site", "dedupe": False})
        r = self.client.post("/api/environments", json={"name": "Site", "dedupe": False})
        self.assertEqual(r.get_json()["name"], "Site2")


if __name__ == "__main__":
    unittest.main()
