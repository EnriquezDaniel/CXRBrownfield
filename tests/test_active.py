"""Contract tests for GET/POST /api/active, the pointer the VR viewer polls (docs/vr-live-sync.md).

Runs against Flask's test client with every data path redirected to a temp directory, so
server/data/ is never read or written. Importing the server module does run its own startup
(_ensure_data_dirs), which is the same no-op as starting the server.

    .venv/Scripts/python.exe -m unittest tests.test_active
"""
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "server"))   # server.py is a script, not a package

import server as srv  # noqa: E402


class ActivePointerTests(unittest.TestCase):
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

        self.data = data
        self.client = srv.app.test_client()

    # -- helpers --

    def make_env(self, name):
        r = self.client.post("/api/environments", json={"name": name, "dedupe": False})
        self.assertEqual(r.status_code, 201, r.get_data(as_text=True))
        return r.get_json()["id"]

    def get_active(self):
        r = self.client.get("/api/active")
        self.assertEqual(r.status_code, 200)
        return r.get_json()

    # -- tests --

    def test_redirected_away_from_real_data(self):
        real = ROOT / "server" / "data"
        self.assertNotEqual(srv.ACTIVE_POINTER_FILE.parent.resolve(), real.resolve())

    def test_nothing_published_is_200_with_null_env(self):
        body = self.get_active()
        self.assertEqual(body["status"], "success")
        self.assertIsNone(body["envId"])
        self.assertEqual(body["version"], 0)
        self.assertEqual(body["loaded"], [])

    def test_publish_returns_the_same_shape_as_get(self):
        a = self.make_env("Alpha")
        r = self.client.post("/api/active", json={"envId": a})
        self.assertEqual(r.status_code, 200)
        posted = r.get_json()
        self.assertEqual(posted, self.get_active())
        self.assertEqual(posted["envId"], a)
        self.assertEqual(posted["name"], "Alpha")
        self.assertEqual(posted["version"], 1)
        self.assertEqual([e["envId"] for e in posted["loaded"]], [a])
        for key in ("envId", "version", "name"):
            self.assertIn(key, posted["loaded"][0])

    def test_unknown_active_env_is_404_and_changes_nothing(self):
        a = self.make_env("Alpha")
        self.client.post("/api/active", json={"envId": a})
        r = self.client.post("/api/active", json={"envId": "no-such-env"})
        self.assertEqual(r.status_code, 404)
        self.assertEqual(self.get_active()["envId"], a)

    def test_loaded_ids_drop_unknowns_and_always_include_the_active_env(self):
        a, b = self.make_env("Alpha"), self.make_env("Beta")
        r = self.client.post("/api/active", json={"envId": a, "loadedIds": [b, "ghost"]})
        self.assertEqual(r.status_code, 200)
        ids = [e["envId"] for e in r.get_json()["loaded"]]
        self.assertCountEqual(ids, [a, b])

    def test_version_follows_an_environment_save(self):
        a = self.make_env("Alpha")
        self.client.post("/api/active", json={"envId": a})
        r = self.client.put(f"/api/environments/{a}", json={"name": "Alpha"})
        self.assertEqual(r.status_code, 200)
        body = self.get_active()
        self.assertEqual(body["version"], 2)               # read live, no republish needed
        self.assertEqual(body["loaded"][0]["version"], 2)

    def test_empty_publish_clears_the_pointer(self):
        a = self.make_env("Alpha")
        self.client.post("/api/active", json={"envId": a})
        r = self.client.post("/api/active", json={"envId": None, "loadedIds": []})
        self.assertEqual(r.status_code, 200)
        self.assertIsNone(self.get_active()["envId"])
        self.assertFalse((self.data / "active.json").exists())

    def test_archived_env_drops_out_of_the_published_set(self):
        a, b = self.make_env("Alpha"), self.make_env("Beta")
        self.client.post("/api/active", json={"envId": a, "loadedIds": [a, b]})
        self.assertEqual(self.client.post(f"/api/environments/{b}/archive").status_code, 200)
        self.assertEqual([e["envId"] for e in self.get_active()["loaded"]], [a])


if __name__ == "__main__":
    unittest.main()
