"""The golden-seed export (authoring/tools/golden-seed): served to the atlas, with how far the game has moved since."""

from __future__ import annotations

import subprocess

from cache import file_stamp
from library import GOLDEN_CONFIG, GOLDEN_MOUNT
from router import NotFoundError

EXPORT_COMMAND = "python3 authoring/tools/golden-seed/export.py"
# What a change to can move the export: the product's code and content, and the Engine pair (its keyed draws).
GENERATOR_PATHS = ("src/CraftSurvive.Game", "content/game", "Directory.Build.props")
GIT_TIMEOUT_S = 5


class GoldenStore:
    def __init__(self, library, cache):
        self.library = library
        self.cache = cache

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

