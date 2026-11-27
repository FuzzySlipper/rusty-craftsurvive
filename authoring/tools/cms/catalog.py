"""The content catalog: every file the game loads from content/game, grouped into records by what it is.

A record id is `<category>-<key>`, stable while the files keep their names. Each record lists its files,
the provenance entries in content/*.sources.json that name them, the working files under authoring/assets
behind them, and the game data that describes them (a wildlife kind, a flora species, a town kit piece).
"""

from __future__ import annotations

from collections import defaultdict
import hashlib
import json
import os
import re
from pathlib import Path

from cache import FileCache
from library import Library
from router import NotFoundError

CATEGORIES = (
    {"id": "creatures", "label": "Creatures", "about": "Wildlife and cave creatures: meshes, manifests, ragdolls, concept and reference art."},
    {"id": "flora", "label": "Flora", "about": "The flora catalog's species: their tree and scatter meshes and bark and foliage textures."},
    {"id": "towns", "label": "Towns", "about": "Town layouts, the kit's pieces, palettes and the people who keep the services."},
    {"id": "characters", "label": "Characters", "about": "Character bodies, outfits, clips and faces."},
    {"id": "models", "label": "Models", "about": "Prop meshes: places, dungeon kit and formations, map models, boats and scatter."},
    {"id": "textures", "label": "Textures", "about": "Every texture the game loads that no other record claims."},
    {"id": "audio", "label": "Audio", "about": "Sound clips."},
    {"id": "data", "label": "Data", "about": "Game data files: catalogs, kinds, sky, chemistry, shaders."},
)

IMAGE = {".png", ".jpg", ".jpeg", ".webp"}
MODEL_GLB = ".glb"
PROP_MESH = ".prop-mesh.json"
AUDIO = {".wav", ".ogg", ".mp3"}
DATA = {".json", ".wgsl"}
# Binary assets made by a tool or a generator are the files provenance should name; data is hand-authored.
PROVENANCE_EXPECTED = IMAGE | AUDIO | {MODEL_GLB, PROP_MESH}
PROVENANCE_GLOB = "*.sources.json"
EXTRA_PROVENANCE = ("game/towns/audio-provenance.json",)
PATH_PATTERN = re.compile(r"(?:content/game/|content/|authoring/)?[\w.\-/]+\.[a-z0-9]{2,5}|(?:content|authoring)/[\w.\-/]+/?")
# Display caps: a provenance entry or a data block is summarised, not dumped whole.
MAX_ENTRY_CHARS = 4000
MAX_DATA_CHARS = 60_000
SPECIES_MODEL = r"^{species}-(?:p?\d+)(?:-far)?\.prop-mesh\.json$"


def suffix(path: Path) -> str:
    name = path.name.lower()
    return PROP_MESH if name.endswith(PROP_MESH) else path.suffix.lower()


def title(key: str) -> str:
    return key.replace("-", " ").replace("_", " ").strip().capitalize()


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def local(folder: str, category: str) -> str:
    """A folder under content/game without the category's own folder, which the id's prefix already says."""
    return "" if folder in (category, ".") else folder.removeprefix(category + "/")


def tree_stamp(*roots: Path):
    """(mtime, size) of every file under the roots: the catalog is rebuilt when any of them changes."""
    entries = []
    for root in roots:
        if not root.is_dir():
            continue
        for directory, _, files in os.walk(root):
            for name in files:
                try:
                    stat = os.stat(os.path.join(directory, name))
                except OSError:
                    continue
                entries.append((directory, name, stat.st_mtime_ns, stat.st_size))
    entries.sort()
    return hashlib.sha1(repr(entries).encode()).hexdigest()


def compact(value, limit=MAX_ENTRY_CHARS):
    text = json.dumps(value)
    return value if len(text) <= limit else {"summary": text[:limit] + "…"}


class ProvenanceIndex:
    """Every file path the provenance files name, with the entry that names it."""

    def __init__(self, library: Library):
        self.library = library
        self.by_path: dict[str, list[dict]] = defaultdict(list)
        self.sources: list[dict] = []
        meta = library.content_meta
        files = sorted(meta.glob(PROVENANCE_GLOB)) + [meta / extra for extra in EXTRA_PROVENANCE if (meta / extra).is_file()]
        for file in files:
            rel = library.rel(file)
            try:
                data = json.loads(file.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as error:
                self.sources.append({"path": rel, "error": str(error), "purpose": None, "paths": 0})
                continue
            before = sum(len(v) for v in self.by_path.values())
            self.walk(rel, data, "", file.parent)
            self.sources.append({"path": rel, "purpose": data.get("purpose") if isinstance(data, dict) else None,
                                 "generated": data.get("generated") if isinstance(data, dict) else None,
                                 "paths": sum(len(v) for v in self.by_path.values()) - before})

    def normalise(self, text: str, base: Path) -> str | None:
        text = text.strip().split(" ")[0].rstrip(",;:)")
        if text.startswith(("content/", "authoring/")):
            return text.rstrip("/")
        if text.startswith("/") or ".." in text:
            return None
        # Kinds and catalogs name runtime files relative to content/game, a provenance file beside them relative to itself.
        for root in (self.library.content, base):
            candidate = root / text
            if "/" in text and candidate.exists():
                return self.library.rel(candidate)
        return None

    def walk(self, source: str, node, pointer: str, base: Path):
        if isinstance(node, dict):
            named = set()
            for key, value in node.items():
                strings = [value] if isinstance(value, str) else [v for v in value if isinstance(v, str)] if isinstance(value, list) else []
                for text in strings:
                    for match in PATH_PATTERN.findall(text):
                        path = self.normalise(match, base)
                        if path and path not in named:
                            named.add(path)
                            self.by_path[path].append({"source": source, "pointer": pointer or "/", "field": key,
                                                       "entry": compact({k: v for k, v in node.items() if not isinstance(v, (dict, list)) or len(json.dumps(v)) < 400})})
            for key, value in node.items():
                if isinstance(value, (dict, list)):
                    self.walk(source, value, f"{pointer}/{key}", base)
        elif isinstance(node, list):
            for index, value in enumerate(node):
                self.walk(source, value, f"{pointer}/{index}", base)

    def for_path(self, rel: str) -> list[dict]:
        """Entries naming the file itself or a folder it is in."""
        found, parts = [], rel.split("/")
        for end in range(len(parts), 1, -1):
            found.extend(self.by_path.get("/".join(parts[:end]), ()))
        return found


class Catalog:
    def __init__(self, library: Library):
        self.library = library
        self.provenance = ProvenanceIndex(library)
        self.records: dict[str, dict] = {}
        self.claimed: set[str] = set()
        content = library.content
        self.files = sorted(p for p in content.rglob("*") if p.is_file()) if content.is_dir() else []
        self.build()

    # ---- helpers

    def file_row(self, path: Path) -> dict:
        stat = path.stat()
        return {"path": self.library.rel(path), "url": self.library.url(path), "size": stat.st_size, "type": suffix(path),
                "mtime": stat.st_mtime_ns}

    def json(self, path: Path, default=None):
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return default

    def add(self, category: str, key: str, kind: str, name: str, files: list[Path], *, authoring: list[Path] = (),
            data: dict | None = None, summary: str = "", tags=()):
        id = f"{category}-{slug(key)}"
        files = sorted(set(files))
        rows = [self.file_row(file) for file in files]
        self.claimed.update(row["path"] for row in rows)
        authored = [self.file_row(file) for file in sorted(set(authoring)) if file.is_file()]
        images = [row for row in rows if row["type"] in IMAGE] + [row for row in authored if row["type"] in IMAGE]
        if category == "creatures":
            images = [row for row in authored if row["type"] in IMAGE] + [row for row in rows if row["type"] in IMAGE]
        models = [row for row in rows if row["type"] in (MODEL_GLB, PROP_MESH)]
        sounds = [row for row in rows if row["type"] in AUDIO]
        provenance, seen = [], set()
        for row in rows:
            for entry in self.provenance.for_path(row["path"]):
                marker = (entry["source"], entry["pointer"])
                if marker not in seen:
                    seen.add(marker)
                    provenance.append(entry)
        missing = [row["path"] for row in rows if row["type"] in PROVENANCE_EXPECTED and not self.provenance.for_path(row["path"])]
        revision = hashlib.sha1(repr([(row["path"], row["mtime"], row["size"]) for row in rows]).encode()).hexdigest()[:12]
        self.records[id] = {
            "id": id, "category": category, "kind": kind, "name": name, "summary": summary, "tags": list(tags),
            "files": rows, "authoring": authored, "data": compact(data, MAX_DATA_CHARS) if data is not None else None,
            "preview": images[0]["url"] if images else None, "images": [row["url"] for row in images],
            "model": {"type": "glb" if models[0]["type"] == MODEL_GLB else "prop-mesh", "url": models[0]["url"]} if models else None,
            "models": [{"type": "glb" if row["type"] == MODEL_GLB else "prop-mesh", "url": row["url"], "path": row["path"]} for row in models],
            "audio": sounds[0]["url"] if sounds else None,
            "provenance": provenance, "unprovenanced": missing, "revision": revision,
            "bytes": sum(row["size"] for row in rows),
        }

    # ---- categories

    def build(self):
        content = self.library.content
        self.creatures(content)
        self.flora(content)
        self.towns(content)
        self.characters(content)
        self.models(content)
        self.textures(content)
        self.audio()
        self.data()

    def creatures(self, content: Path):
        kinds_file = content / "wildlife/kinds.json"
        kinds = {kind["id"]: kind for kind in (self.json(kinds_file, {}) or {}).get("kinds", [])}
        style = {kind.get("id") or kind.get("species"): kind for kind in (self.json(content / "creatures/style-kinds.json", {}) or {}).get("kinds", []) if isinstance(kind, dict)}
        authored_root = self.library.authoring_assets / "creatures"
        folders = {p.name for p in (content / "creatures").iterdir() if p.is_dir()} if (content / "creatures").is_dir() else set()
        authored = {p.name for p in authored_root.iterdir() if p.is_dir()} if authored_root.is_dir() else set()
        for key in sorted(folders | authored | set(kinds)):
            folder = content / "creatures" / key
            files = [p for p in folder.rglob("*") if p.is_file()] if folder.is_dir() else []
            working = [p for p in (authored_root / key).rglob("*") if p.is_file()] if (authored_root / key).is_dir() else []
            # Concept and reference art first, so the card shows the drawing the creature was made from.
            working.sort(key=lambda p: (0 if "concept" in p.parts else 1 if "mesh" in p.parts else 2, p.as_posix()))
            manifest = next((self.json(p) for p in files if p.name.endswith(".manifest.json")), None)
            kind = kinds.get(key)
            data = {}
            if kind:
                data["wildlifeKind"] = kind
            if key in style:
                data["styleKind"] = style[key]
            if manifest:
                data["manifest"] = {"species": manifest.get("species"), "family": manifest.get("family"),
                                    "clips": sorted((manifest.get("clips") or {}).keys())}
            label = "wildlife kind" if kind else "style test" if key in style else "creature" if files else "authoring only"
            habitat = ", ".join(sorted((kind or {}).get("habitat", {}))) if kind else ""
            self.add("creatures", key, label, (kind or {}).get("name") or title(key), files, authoring=working,
                     data=data or None, summary=habitat, tags=[t for t in (kind and "kind", files and "mesh", working and "authoring") if t])

    def flora(self, content: Path):
        catalog = self.json(content / "flora/catalog.json", {}) or {}
        species = catalog.get("species", {})
        barks, foliage = catalog.get("barks", {}), catalog.get("foliage", {})
        trees, scatter, textures = content / "models/trees", content / "models/scatter", content / "textures/trees"
        for key, definition in species.items():
            pattern = re.compile(SPECIES_MODEL.format(species=re.escape(key)))
            files = [p for folder in (trees, scatter) if folder.is_dir() for p in folder.iterdir() if pattern.match(p.name)]
            for texture in (f"{definition.get('bark')}-bark.png", f"{definition.get('leaves')}-leaves.png"):
                if (textures / texture).is_file():
                    files.append(textures / texture)
            # A scatter mesh names its painted sheet; claim it with the species.
            for mesh in [p for p in files if p.name.endswith(PROP_MESH)]:
                for texture in ((self.json(mesh, {}) or {}).get("textures") or {}).values():
                    if isinstance(texture, str) and (content / texture).is_file():
                        files.append(content / texture)
            data = {"species": definition}
            if definition.get("bark") in barks:
                data["bark"] = barks[definition["bark"]]
            if definition.get("leaves") in foliage:
                data["foliage"] = foliage[definition["leaves"]]
            self.add("flora", key, definition.get("role", "species"), title(key), files, data=data,
                     summary=f"{definition.get('role', '')} · {len([p for p in files if p.name.endswith(PROP_MESH)])} meshes")

    def towns(self, content: Path):
        towns = content / "towns"
        if not towns.is_dir():
            return
        for layout in sorted((towns / "layouts").glob("*.json")):
            self.add("towns", f"layout-{layout.stem}", "layout", f"{title(layout.stem)} layout", [layout], data=self.json(layout))
        kit = self.json(towns / "kit.json", {}) or {}
        for key, piece in (kit.get("pieces") or {}).items():
            files = [p for p in (towns / "kit").glob(f"{key}*.glb")]
            for texture in re.findall(r"[\w\-]+\.png", json.dumps(piece)):
                if (towns / "kit/textures" / texture).is_file():
                    files.append(towns / "kit/textures" / texture)
            self.add("towns", f"kit-{key}", "kit piece", title(key), files, data=piece)
        for key, palette in ((self.json(towns / "palettes.json", {}) or {}).get("palettes") or {}).items():
            self.add("towns", f"palette-{key}", "palette", f"{title(key)} palette", [], data=palette)
        people = towns / "people"
        if people.is_dir():
            for person in sorted(p for p in people.iterdir() if p.is_dir()):
                files = sorted((p for p in person.rglob("*") if p.is_file()), key=lambda p: (p.name != "face.png", p.as_posix()))
                self.add("towns", f"person-{person.name}", "person", title(person.name), files,
                         summary=", ".join(sorted({p.parent.name for p in files})))

    def characters(self, content: Path):
        root = content / "characters"
        if not root.is_dir():
            return
        for group in sorted(p for p in root.iterdir() if p.is_dir()):
            members = defaultdict(list)
            for file in sorted(p for p in group.rglob("*") if p.is_file()):
                members[file.name.split(".")[0].split("-")[0]].append(file)
            for key, files in members.items():
                files.sort(key=lambda p: (suffix(p) != MODEL_GLB, "clips" in p.name, p.name))
                self.add("characters", f"{group.name}-{key}", group.name, title(key), files,
                         summary=f"{sum(1 for p in files if suffix(p) == MODEL_GLB)} meshes, {sum(1 for p in files if suffix(p) in IMAGE)} images")

    def models(self, content: Path):
        groups = defaultdict(list)
        for file in self.files:
            rel = self.library.rel(file)
            if rel in self.claimed or suffix(file) != PROP_MESH:
                continue
            folder = file.parent.relative_to(content / "models").as_posix() if file.is_relative_to(content / "models") else file.parent.name
            base = re.sub(r"-far$", "", file.name[: -len(PROP_MESH)])
            groups[(folder, base)].append(file)
        for (folder, base), files in sorted(groups.items()):
            for mesh in list(files):
                for texture in ((self.json(mesh, {}) or {}).get("textures") or {}).values():
                    if isinstance(texture, str) and (content / texture).is_file() and self.library.rel(content / texture) not in self.claimed:
                        files.append(content / texture)
            key = f"{folder}-{base}" if folder != "." else base
            self.add("models", key, folder if folder != "." else "prop", title(base), files)

    def textures(self, content: Path):
        for file in self.files:
            rel = self.library.rel(file)
            if rel in self.claimed or suffix(file) not in IMAGE:
                continue
            folder = file.parent.relative_to(content).as_posix()
            place = local(folder, "textures")
            self.add("textures", f"{place}-{file.stem}" if place else file.stem, place or "texture", file.stem, [file])

    def audio(self):
        for file in self.files:
            if self.library.rel(file) in self.claimed or suffix(file) not in AUDIO:
                continue
            place = local(file.parent.relative_to(self.library.content).as_posix(), "audio")
            self.add("audio", f"{place}-{file.stem}" if place else file.stem, place or "audio", title(file.stem), [file])

    def data(self):
        for file in self.files:
            rel = self.library.rel(file)
            if rel in self.claimed:
                continue
            folder = file.parent.relative_to(self.library.content).as_posix()
            data = self.json(file) if suffix(file) == ".json" else {"text": file.read_text(encoding="utf-8", errors="replace")[:MAX_DATA_CHARS]} if suffix(file) in DATA else None
            purpose = data.get("purpose") or data.get("about") or data.get("$comment") if isinstance(data, dict) else None
            self.add("data", f"{folder}-{file.stem}", folder, file.name, [file], data=data,
                     summary=(purpose or "")[:200] if isinstance(purpose, str) else "")

    # ---- views

    def card(self, record: dict) -> dict:
        keys = ("id", "category", "kind", "name", "summary", "tags", "preview", "model", "audio", "revision", "bytes")
        return {key: record[key] for key in keys} | {"files": len(record["files"]), "authoring": len(record["authoring"]),
                                                    "provenance": len(record["provenance"]), "unprovenanced": len(record["unprovenanced"])}

    def summary(self) -> dict:
        counts = defaultdict(lambda: {"records": 0, "files": 0, "bytes": 0, "unprovenanced": 0})
        for record in self.records.values():
            row = counts[record["category"]]
            row["records"] += 1
            row["files"] += len(record["files"])
            row["bytes"] += record["bytes"]
            row["unprovenanced"] += len(record["unprovenanced"])
        return {"categories": [category | counts[category["id"]] for category in CATEGORIES],
                "contentFiles": len(self.files), "unclaimed": [self.library.rel(f) for f in self.files if self.library.rel(f) not in self.claimed]}


class CatalogStore:
    """The catalog, rebuilt when any file under content/ or authoring/assets/ changes."""

    def __init__(self, library: Library, cache: FileCache):
        self.library = library
        self.cache = cache

    def catalog(self) -> Catalog:
        stamp = tree_stamp(self.library.content_meta, self.library.authoring_assets / "creatures")
        return self.cache.get(("catalog",), stamp, lambda: Catalog(self.library))


def register_routes(router, workspace):
    def catalog(_req):
        current = workspace.catalog()
        return {"categories": list(CATEGORIES), "records": [current.card(r) for r in current.records.values()]} | current.summary()

    def record(req):
        current = workspace.catalog()
        found = current.records.get(req.params["id"])
        if not found:
            raise NotFoundError(f"No record {req.params['id']}.")
        return found

    def provenance(_req):
        current = workspace.catalog()
        missing = defaultdict(list)
        for found in current.records.values():
            for path in found["unprovenanced"]:
                missing[found["category"]].append({"id": found["id"], "name": found["name"], "path": path})
        return {"sources": current.provenance.sources, "missing": missing,
                "dangling": sorted(path for path in current.provenance.by_path
                                   if (p := workspace.library.repo_path(path)) is not None and not p.exists())}

    router.get("/api/catalog", catalog)
    router.get("/api/records/{id}", record)
    router.get("/api/provenance", provenance)
