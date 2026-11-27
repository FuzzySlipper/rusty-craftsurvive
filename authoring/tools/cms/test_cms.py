"""The CMS against a scratch repository: the catalog, provenance, reviews, the golden-seed export and the server."""

import json
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from pathlib import Path

from cache import FileCache
from catalog import Catalog
from golden import GoldenStore
from library import Library
from reviews import ReviewStore
from server import Workspace, make_server


def write(path: Path, data) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(data, bytes):
        path.write_bytes(data)
    else:
        path.write_text(data if isinstance(data, str) else json.dumps(data), encoding="utf-8")
    return path


def scratch_repo(root: Path) -> Path:
    game = root / "content/game"
    write(game / "creatures/mole/mole.glb", b"glTF")
    write(game / "creatures/mole/mole.manifest.json", {"species": "mole", "clips": {"walk": {}, "idle": {}}})
    write(game / "wildlife/kinds.json", {"kinds": [{"id": "mole", "name": "Giant mole", "habitat": {"Grassland": 1},
                                                    "mesh": {"file": "creatures/mole/mole.glb"}}]})
    write(game / "flora/catalog.json", {"species": {"oak": {"role": "tree", "bark": "oak", "leaves": "oak"}},
                                        "barks": {"oak": {"pattern": "fissured"}}, "foliage": {"oak": {"ramp": "foliage"}}})
    write(game / "models/trees/oak-p1.prop-mesh.json", {"name": "oak", "parts": [], "textures": {"bark": "textures/trees/oak-bark.png"}})
    write(game / "textures/trees/oak-bark.png", b"\x89PNG")
    write(game / "textures/sky/lonely.png", b"\x89PNG")
    write(game / "audio/wind.wav", b"RIFF")
    write(game / "sky/sky.stars.json", {"purpose": "The stars."})
    write(root / "content/creatures.sources.json", {"purpose": "creatures", "items": [
        {"mesh": "content/game/creatures/mole/mole.glb", "built": "from authoring/assets/creatures/mole/species.json"}]})
    write(root / "content/trees.sources.json", {"purpose": "trees", "items": [{"texture": "content/game/textures/trees/oak-bark.png"},
                                                                         {"model": "content/game/models/trees/oak-p1.prop-mesh.json"},
                                                                         {"gone": "content/game/textures/trees/gone.png"}]})
    write(root / "authoring/assets/creatures/mole/species.json", {"species": "mole"})
    write(root / "authoring/assets/creatures/mole/concept/option-a.png", b"\x89PNG")
    return root


def golden_export(root: Path) -> Path:
    folder = root / "authoring/assets/golden-seed"
    write(folder / "manifest.json", {"format": 1, "world": {"seed": 7, "size": 4096, "radius": 2048, "fingerprint": "abc"},
                                     "config": {"seed": 7, "size": 4096, "pixels": 2, "windows": {}},
                                     "source": {"commit": None, "dirty": False}, "windows": []})
    write(folder / "drainage.json", {"reaches": [{"id": 0, "flow": 0.5, "points": []}, {"id": 1, "flow": 9, "points": []}]})
    write(folder / "places.json", {"towns": [], "lakes": [], "sites": []})
    write(folder / "windows/home.json", {"x": 0, "z": 0, "places": []})
    return folder


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.library = Library(repo=scratch_repo(Path(self.temp.name)))
        self.catalog = Catalog(self.library)

    def tearDown(self):
        self.temp.cleanup()

    def test_every_content_file_is_in_a_record(self):
        self.assertEqual(self.catalog.summary()["unclaimed"], [])

    def test_creature_joins_kind_manifest_working_files_and_provenance(self):
        mole = self.catalog.records["creatures-mole"]
        self.assertEqual(mole["name"], "Giant mole")
        self.assertEqual(mole["kind"], "wildlife kind")
        self.assertEqual(mole["data"]["manifest"]["clips"], ["idle", "walk"])
        self.assertEqual(mole["model"]["type"], "glb")
        self.assertTrue(mole["preview"].endswith("concept/option-a.png"), "a creature's card shows its concept art first")
        self.assertEqual([entry["source"] for entry in mole["provenance"]], ["content/creatures.sources.json"])
        self.assertEqual(mole["unprovenanced"], [])

    def test_flora_species_claims_its_meshes_and_textures(self):
        oak = self.catalog.records["flora-oak"]
        self.assertEqual(sorted(row["path"] for row in oak["files"]),
                         ["content/game/models/trees/oak-p1.prop-mesh.json", "content/game/textures/trees/oak-bark.png"])
        self.assertEqual(oak["data"]["bark"], {"pattern": "fissured"})
        self.assertNotIn("textures-trees-oak-bark", self.catalog.records)

    def test_unprovenanced_and_dangling(self):
        self.assertEqual(self.catalog.records["textures-sky-lonely"]["unprovenanced"], ["content/game/textures/sky/lonely.png"])
        self.assertEqual(self.catalog.records["audio-wind"]["unprovenanced"], ["content/game/audio/wind.wav"])
        self.assertEqual(self.catalog.records["data-sky-sky-stars"]["unprovenanced"], [], "data is hand-authored and needs no provenance")
        self.assertIn("content/game/textures/trees/gone.png", self.catalog.provenance.by_path)


class GoldenTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = scratch_repo(Path(self.temp.name))
        self.library = Library(repo=self.root)

    def tearDown(self):
        self.temp.cleanup()

    def test_without_an_export_the_command_is_offered(self):
        overview = GoldenStore(self.library, FileCache()).overview()
        self.assertFalse(overview["available"])
        self.assertIn("export.py", overview["command"])

    def test_drainage_is_filtered_by_flow_and_windows_by_name(self):
        golden_export(self.root)
        store = GoldenStore(self.library, FileCache())
        self.assertTrue(store.overview()["available"])
        self.assertEqual([reach["id"] for reach in store.drainage(1)["reaches"]], [1])
        self.assertEqual(store.drainage(0)["total"], 2)
        self.assertTrue(store.window("home")["heightUrl"].endswith("windows/home.height.i16"))
        with self.assertRaises(Exception):
            store.window("../manifest")


class ServerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = scratch_repo(Path(self.temp.name))
        golden_export(root)
        workspace = Workspace(Library(repo=root), ReviewStore(root / "reviews.sqlite3"))
        self.server = make_server("127.0.0.1", 0, workspace)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.server.server_port}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.temp.cleanup()

    def get(self, path):
        with urllib.request.urlopen(self.base + path) as response:
            return response.status, response.read()

    def post(self, path, body):
        request = urllib.request.Request(self.base + path, data=json.dumps(body).encode(), method="POST",
                                         headers={"content-type": "application/json"})
        with urllib.request.urlopen(request) as response:
            return json.loads(response.read())

    def test_catalog_record_and_media(self):
        catalog = json.loads(self.get("/api/catalog")[1])
        self.assertIn("creatures-mole", [record["id"] for record in catalog["records"]])
        record = json.loads(self.get("/api/records/creatures-mole")[1])
        status, body = self.get(record["model"]["url"])
        self.assertEqual((status, body), (200, b"glTF"))

    def test_media_cannot_leave_its_root(self):
        with self.assertRaises(urllib.error.HTTPError) as raised:
            self.get("/media/content/../../content/creatures.sources.json")
        self.assertEqual(raised.exception.code, 404)
        raised.exception.close()

    def test_a_rating_goes_stale_when_the_files_change(self):
        record = json.loads(self.get("/api/records/creatures-mole")[1])
        saved = self.post("/api/reviews/creatures/creatures-mole", {"rating": "good", "expectedRevision": record["revision"]})
        self.assertEqual(saved["rating"], "good")
        write(Path(self.temp.name) / "content/game/creatures/mole/mole.glb", b"glTF, rebuilt")
        review = json.loads(self.get("/api/reviews/creatures/creatures-mole")[1])
        self.assertIsNone(review["rating"])
        self.assertEqual(review["previousRating"], "good")

    def test_golden_element_notes(self):
        saved = self.post("/api/reviews/golden/town:Kelpmouth", {"note": "check the quay", "marked": True})
        self.assertEqual((saved["note"], saved["marked"]), ("check the quay", True))
        drainage = json.loads(self.get("/api/golden/drainage?minFlow=5")[1])
        self.assertEqual(len(drainage["reaches"]), 1)


if __name__ == "__main__":
    unittest.main()
