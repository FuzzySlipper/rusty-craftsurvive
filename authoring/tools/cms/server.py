#!/usr/bin/env python3
"""Serve the Rusty Wandering authoring CMS: the content catalog, provenance, reviews and the golden-seed atlas."""

import argparse
from http import HTTPStatus
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
import sqlite3
import subprocess
from pathlib import Path
from urllib.parse import unquote, urlsplit

from cache import FileCache
import catalog as catalog_module
from catalog import CatalogStore
import golden as golden_module
from golden import GoldenStore
from library import CMS, DEFAULT_REVIEWS, REPO, Library
import reviews as reviews_module
from reviews import ReviewStore, Reviews
from router import Response, Router

WEB = CMS / "web"
SERVICE = "Rusty Wandering Authoring"
DEFAULT_PORT = 8790
GIT_TIMEOUT_S = 5
# The CMS edits nothing in git; it reports what is uncommitted under the content it shows.
WATCHED = ("content/game", "content", "authoring/tools", "authoring/assets")


class Workspace:
    """Composition root: the library's roots, one file cache, and the domain stores."""

    def __init__(self, library: Library, review_store: ReviewStore | None = None, cache: FileCache | None = None):
        self.library = library
        self.review_store = review_store
        self.cache = cache if cache is not None else FileCache()
        self.catalog_store = CatalogStore(library, self.cache)
        self.golden = GoldenStore(library, self.cache)

    def catalog(self):
        return self.catalog_store.catalog()

    def revision(self, category, id):
        record = self.catalog().records.get(id)
        if record is not None:
            return record["revision"]
        if category == "golden":
            manifest = self.golden.manifest()
            return manifest["world"]["fingerprint"] if manifest else None
        return None

    def reviews(self):
        return Reviews(self.review_store, self.revision)

    def git_status(self):
        repo = self.library.repo
        try:
            process = subprocess.run(["git", "status", "--porcelain", "--untracked-files=all", "--", *WATCHED],
                                     cwd=repo, capture_output=True, text=True, timeout=GIT_TIMEOUT_S)
        except (OSError, subprocess.SubprocessError):
            return {"gitAvailable": False, "modified": []}
        if process.returncode:
            return {"gitAvailable": False, "modified": []}
        modified = [{"status": line[:2].strip() or "M", "path": line[3:].strip()} for line in process.stdout.splitlines() if len(line) > 3]
        return {"gitAvailable": True, "modified": modified}


def create_router(workspace: Workspace) -> Router:
    router = Router()
    router.get("/health", lambda _req: {"service": SERVICE})
    router.get("/api/workspace/status", lambda _req: workspace.git_status())
    catalog_module.register_routes(router, workspace)
    reviews_module.register_routes(router, workspace)
    golden_module.register_routes(router, workspace)
    return router


class Handler(SimpleHTTPRequestHandler):
    workspace: Workspace
    router: Router

    def log_message(self, format, *args):
        pass

    def end_headers(self):
        self.send_header("Cache-Control", "no-cache")
        super().end_headers()

    def send_data(self, data, content_type, status=HTTPStatus.OK, headers=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(data)

    def json_response(self, data, status=HTTPStatus.OK):
        self.send_data(json.dumps(data).encode("utf-8"), "application/json; charset=utf-8", status)

    def translate_path(self, path):
        """Media mounts serve the repository's content roots; everything else is the web app."""
        path = unquote(urlsplit(path).path)
        for mount, root in self.workspace.library.mounts.items():
            if path.startswith(mount):
                root = root.resolve()
                file = (root / path.removeprefix(mount)).resolve()
                return str(file if file.is_relative_to(root) else WEB / "missing")
        file = (WEB / path.lstrip("/")).resolve()
        return str(file if file.is_relative_to(WEB) else WEB / "missing")

    def guess_type(self, path):
        if str(path).endswith((".glb",)):
            return "model/gltf-binary"
        if str(path).endswith((".f32", ".u8", ".i16")):
            return "application/octet-stream"
        return super().guess_type(path)

    def _read_body(self):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            return b""
        return self.rfile.read(max(0, length)) if length else b""

    def _handle(self, method: str, allow_static: bool = False):
        try:
            body = None if method == "GET" else self._read_body()
            response = self.router.dispatch(method, self.path, body)
            if response is None and allow_static:
                super().do_GET()
                return
            if response is None:
                self.json_response({"error": "Not found."}, HTTPStatus.NOT_FOUND)
                return
            payload, content_type = response.encode()
            self.send_data(payload, content_type, response.status, response.headers)
        except FileNotFoundError:
            self.send_error(HTTPStatus.NOT_FOUND)
        except (OSError, sqlite3.Error) as error:
            self.json_response({"error": str(error)}, HTTPStatus.INTERNAL_SERVER_ERROR)

    def do_GET(self):
        self._handle("GET", allow_static=True)

    def do_POST(self):
        self._handle("POST")


def make_server(host: str, port: int, workspace: Workspace) -> ThreadingHTTPServer:
    class BoundHandler(Handler):
        pass

    BoundHandler.workspace = workspace
    BoundHandler.router = create_router(workspace)
    return ThreadingHTTPServer((host, port), BoundHandler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--repo", type=Path, default=REPO)
    parser.add_argument("--reviews", type=Path, default=None, help="the review database (default authoring/assets/cms/reviews.sqlite3)")
    parser.add_argument("--golden", type=Path, default=None, help="the golden-seed export folder (default authoring/assets/golden-seed)")
    args = parser.parse_args()
    repo = args.repo.resolve()
    library = Library(repo=repo, golden_dir=args.golden.resolve() if args.golden else None)
    reviews = (args.reviews or (repo / DEFAULT_REVIEWS.relative_to(REPO))).resolve()
    workspace = Workspace(library, ReviewStore(reviews))
    with make_server(args.host, args.port, workspace) as server:
        print(f"{SERVICE} at http://{args.host}:{server.server_port}", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
