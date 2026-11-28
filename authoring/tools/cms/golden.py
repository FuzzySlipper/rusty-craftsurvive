"""The golden-seed export (authoring/tools/golden-seed): served to the atlas, with how far the game has moved since."""

from __future__ import annotations

import gzip
import json
import re
import subprocess
import threading

from cache import file_stamp
from library import GOLDEN_CONFIG, GOLDEN_MOUNT
from router import ApiError, NotFoundError, Response

EXPORT_COMMAND = "dotnet run --project authoring/tools/golden-seed -c Release"
UNDERGROUND_COMMAND = "dotnet run --project authoring/tools/golden-seed -c Release -- underground"
BUILD_COMMAND = "dotnet build authoring/tools/golden-seed -c Release"
EXPORTER = "authoring/tools/golden-seed/bin/Release/net10.0/GoldenSeed.dll"
# A cave is generated on its first view (a couple of seconds); later views read the exporter's cache.
CAVE_TIMEOUT_S = 180
SYSTEM_TIMEOUT_S = 300
CAVE_ID = re.compile(r"^poi:-?\d+:-?\d+$")
UNDERGROUND_LEVEL = re.compile(r"^[a-z0-9_-]+$")
UNDERGROUND_SYSTEM_ID = re.compile(r"^ug:([a-z0-9_-]+):(-?\d+):(-?\d+):(\d+)$")
# What a change to can move the export: the product's code and content, and the Engine pair (its keyed draws).
GENERATOR_PATHS = ("src/CraftSurvive.Game", "content/game", "Directory.Build.props")
GIT_TIMEOUT_S = 5


class GoldenStore:
    def __init__(self, library, cache, exporter=None):
        self.library = library
        self.cache = cache
        self.exporter = exporter
        # One cave generation at a time: each fills several cores.
        self.cave_lock = threading.Lock()

    @property
    def root(self):
        return self.library.golden

    def file(self, name):
        return self.root / name

    def manifest(self):
        return self.cache.json(self.file("manifest.json"))

    def config(self):
        return self.cache.json(GOLDEN_CONFIG)

    def git(self, *args):
        try:
            process = subprocess.run(["git", *args], cwd=self.library.repo, capture_output=True, text=True, timeout=GIT_TIMEOUT_S)
        except (OSError, subprocess.SubprocessError):
            return None
        return process.stdout.strip() if process.returncode == 0 else None

    def drift(self, manifest):
        """How far the repository has moved since the export: commits, and those that touch what generates the world."""
        commit = (manifest.get("source") or {}).get("commit")
        if not commit:
            return None
        head = self.git("rev-parse", "HEAD")
        since = self.git("rev-list", "--count", f"{commit}..HEAD")
        touching = self.git("rev-list", "--count", f"{commit}..HEAD", "--", *GENERATOR_PATHS)
        dirty = self.git("status", "--porcelain", "--", *GENERATOR_PATHS)
        return {"head": head, "commitsSince": int(since) if since else None,
                "generatorCommitsSince": int(touching) if touching else None,
                "dirtyNow": bool(dirty), "dirtyAtExport": (manifest.get("source") or {}).get("dirty")}

    def overview(self):
        manifest = self.manifest()
        config = self.config()
        if not manifest:
            return {"available": False, "config": config, "command": EXPORT_COMMAND}
        exported = manifest.get("config") or {}
        return {"available": True, "manifest": manifest, "config": config, "command": EXPORT_COMMAND,
                "exporterBuilt": (self.exporter or (self.library.repo / EXPORTER)).is_file(),
                "configChanged": bool(config) and any(config.get(key) != exported.get(key) for key in ("seed", "size", "pixels", "windows")),
                "drift": self.drift(manifest), "mediaBase": GOLDEN_MOUNT}

    def json_file(self, name):
        data = self.cache.json(self.file(name))
        if data is None:
            raise NotFoundError(f"The export has no {name}; run {EXPORT_COMMAND}.")
        return data

    def drainage(self, min_flow):
        """Walked reaches at or above a flow, cached per threshold: the full network is tens of thousands of reaches."""
        path = self.file("drainage.json")
        stamp = file_stamp(path)
        if stamp is None:
            raise NotFoundError(f"The export has no drainage.json; run {EXPORT_COMMAND}.")

        def load():
            reaches = self.json_file("drainage.json")["reaches"]
            return {"minFlow": min_flow, "total": len(reaches),
                    "reaches": [reach for reach in reaches if reach["flow"] >= min_flow]}

        return self.cache.get(("golden-drainage", min_flow), stamp, load)

    def window(self, id):
        if not id.replace("-", "").isalnum():
            raise NotFoundError("No such window.")
        detail = self.json_file(f"windows/{id}.json")
        return detail | {"heightUrl": f"{GOLDEN_MOUNT}windows/{id}.height.i16", "biomeUrl": f"{GOLDEN_MOUNT}windows/{id}.biome.u8"}


    def tile(self, x, z):
        """A region tile's places, cave ways and crossings, as the exporter wrote them (gzip JSON, sent as is)."""
        path = self.file(f"tiles/{int(x)}_{int(z)}.json.gz")
        if not path.is_file():
            raise NotFoundError("The export has no such tile.")
        return Response(path.read_bytes(), content_type="application/json; charset=utf-8", headers={"Content-Encoding": "gzip"})

    def cave(self, id):
        """A cave's simplified view: generated by the exporter on first asking, from its cache afterwards."""
        if not CAVE_ID.match(id):
            raise NotFoundError("A cave is named by its mouth, poi:CX:CZ.")
        exporter = self.exporter or (self.library.repo / EXPORTER)
        if not exporter.is_file():
            raise ApiError(503, f"The exporter is not built: run {BUILD_COMMAND}.")
        with self.cave_lock:
            try:
                process = subprocess.run(["dotnet", str(exporter), "cave", id, "--out", str(self.root)], cwd=self.library.repo,
                                         capture_output=True, text=True, timeout=CAVE_TIMEOUT_S)
            except subprocess.TimeoutExpired as error:
                raise ApiError(504, f"Generating the cave under {id} took longer than {CAVE_TIMEOUT_S} s.") from error
        if process.returncode:
            message = (process.stderr or process.stdout).strip().splitlines()
            raise ApiError(500, f"The exporter could not make the cave under {id}: {message[-1] if message else 'no output'}")
        path = process.stdout.strip().splitlines()[-1]
        with gzip.open(path, "rb") as file:
            return Response(file.read(), content_type="application/json; charset=utf-8")

    def underground_manifest(self):
        """Underground plan manifest across coarseness levels (#11209), if exported."""
        manifest = self.cache.json(self.file("underground/manifest.json"))
        if not manifest:
            return {"available": False, "command": UNDERGROUND_COMMAND}
        solubility = manifest.get("solubilityRaster") or {}
        return {
            "available": True,
            "command": UNDERGROUND_COMMAND,
            "manifest": manifest,
            "solubilityUrl": f"{GOLDEN_MOUNT}{solubility['file']}" if solubility.get("file") else None,
            "exporterBuilt": (self.exporter or (self.library.repo / EXPORTER)).is_file(),
        }

    def underground_summary(self, level):
        if not UNDERGROUND_LEVEL.match(level):
            raise NotFoundError("Unknown underground coarseness level.")
        path = self.file(f"underground/{level}/summary.json.gz")
        if not path.is_file():
            raise NotFoundError(f"No underground summary for level '{level}'; run {UNDERGROUND_COMMAND}.")
        return Response(path.read_bytes(), content_type="application/json; charset=utf-8", headers={"Content-Encoding": "gzip"})

    def underground_tile(self, level, x, z):
        if not UNDERGROUND_LEVEL.match(level):
            raise NotFoundError("Unknown underground coarseness level.")
        path = self.file(f"underground/{level}/tiles/{int(x)}_{int(z)}.json.gz")
        if not path.is_file():
            raise NotFoundError("No underground plan for that tile.")
        return Response(path.read_bytes(), content_type="application/json; charset=utf-8", headers={"Content-Encoding": "gzip"})

    def underground_system(self, level, system_id):
        match = UNDERGROUND_SYSTEM_ID.match(system_id)
        if not UNDERGROUND_LEVEL.match(level) or not match or match.group(1) != level:
            raise NotFoundError("An underground system is named ug:<level>:<tx>:<tz>:<index>.")
        tx, tz = int(match.group(2)), int(match.group(3))
        path = self.file(f"underground/{level}/tiles/{tx}_{tz}.json.gz")
        if not path.is_file():
            raise NotFoundError("No underground tile plan for that system.")
        with gzip.open(path, "rt", encoding="utf-8") as file:
            tile = json.load(file)
        for sys in tile.get("systems", []):
            if sys.get("id") == system_id:
                return {"level": level, "system": sys}
        raise NotFoundError(f"Underground system {system_id} was not found.")

    def underground_detail(self, level, system_id):
        """Multi-box KarstCave detail generated from the plan's boundary ports (#11209, CMS-only)."""
        match = UNDERGROUND_SYSTEM_ID.match(system_id)
        if not UNDERGROUND_LEVEL.match(level) or not match or match.group(1) != level:
            raise NotFoundError("An underground system is named ug:<level>:<tx>:<tz>:<index>.")
        exporter = self.exporter or (self.library.repo / EXPORTER)
        if not exporter.is_file():
            raise ApiError(503, f"The exporter is not built: run {BUILD_COMMAND}.")
        with self.cave_lock:
            try:
                process = subprocess.run(
                    ["dotnet", str(exporter), "system", level, system_id, "--out", str(self.root)],
                    cwd=self.library.repo, capture_output=True, text=True, timeout=SYSTEM_TIMEOUT_S,
                )
            except subprocess.TimeoutExpired as error:
                raise ApiError(504, f"Generating multi-box detail for {system_id} took longer than {SYSTEM_TIMEOUT_S} s.") from error
        if process.returncode:
            message = (process.stderr or process.stdout).strip().splitlines()
            raise ApiError(500, f"The exporter could not build system detail for {system_id}: {message[-1] if message else 'no output'}")
        path = process.stdout.strip().splitlines()[-1]
        with gzip.open(path, "rb") as file:
            return Response(file.read(), content_type="application/json; charset=utf-8")


def register_routes(router, workspace):
    golden = workspace.golden

    def min_flow(req):
        try:
            return max(0.0, float(req.arg("minFlow", "0")))
        except ValueError:
            return 0.0

    router.get("/api/golden", lambda _req: golden.overview())
    router.get("/api/golden/places", lambda _req: golden.json_file("places.json"))
    router.get("/api/golden/rivers", lambda _req: golden.json_file("map-rivers.json"))
    router.get("/api/golden/drainage", lambda req: golden.drainage(min_flow(req)))
    router.get("/api/golden/windows/{id}", lambda req: golden.window(req.params["id"]))
    router.get("/api/golden/tiles", lambda _req: golden.json_file("tiles/index.json"))
    router.get("/api/golden/tiles/{x}/{z}", lambda req: golden.tile(req.params["x"], req.params["z"]))
    router.get("/api/golden/caves", lambda _req: golden.json_file("caves.json"))
    router.get("/api/golden/caves/{id}", lambda req: golden.cave(req.params["id"]))
    router.get("/api/golden/underground", lambda _req: golden.underground_manifest())
    router.get("/api/golden/underground/{level}/summary", lambda req: golden.underground_summary(req.params["level"]))
    router.get("/api/golden/underground/{level}/tiles/{x}/{z}", lambda req: golden.underground_tile(req.params["level"], req.params["x"], req.params["z"]))
    router.get("/api/golden/underground/{level}/systems/{id}", lambda req: golden.underground_system(req.params["level"], req.params["id"]))
    router.get("/api/golden/underground/{level}/detail/{id}", lambda req: golden.underground_detail(req.params["level"], req.params["id"]))

