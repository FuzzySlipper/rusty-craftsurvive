"""Thread-safe mtime/size cache for read-heavy CMS files and derived indexes."""

import csv
import json
from pathlib import Path
from threading import Lock


def file_stamp(path: Path):
    """Return (mtime_ns, size) for an existing file, or None when absent."""
    try:
        stat = path.stat()
        return (stat.st_mtime_ns, stat.st_size) if path.is_file() else None
    except OSError:
        return None


def dir_stamp(root: Path, pattern: str):
    """Return a deterministic stamp tuple for every file under root matching pattern."""
    if not root.is_dir():
        return ()
    entries = []
    for path in sorted(root.glob(pattern)):
        stamp = file_stamp(path)
        if stamp is not None:
            entries.append((path.relative_to(root).as_posix(), stamp[0], stamp[1]))
    return tuple(entries)


class FileCache:
    """Cache parsed files and derived views, invalidating automatically when stamps change.

    Cached JSON/CSV structures are shared across readers; treat returned objects as read-only.
    """

    def __init__(self):
        self._lock = Lock()
        self._entries = {}

    def get(self, key, stamp, loader):
        with self._lock:
            cached = self._entries.get(key)
            if cached is not None and cached[0] == stamp:
                return cached[1]
        value = loader()
        with self._lock:
            self._entries[key] = (stamp, value)
        return value

    def json(self, path: Path, default=None):
        resolved = Path(path)
        stamp = file_stamp(resolved)
        if stamp is None:
            return default() if callable(default) else default
        return self.get(("json", resolved), stamp, lambda: json.loads(resolved.read_text(encoding="utf-8")))

    def csv(self, path: Path):
        resolved = Path(path)
        stamp = file_stamp(resolved)
        if stamp is None:
            return ()

        def load():
            with open(resolved, encoding="utf-8") as file:
                return tuple(csv.DictReader(file))

        return self.get(("csv", resolved), stamp, load)

    def invalidate(self, key=None):
        with self._lock:
            if key is None:
                self._entries.clear()
            else:
                self._entries.pop(key, None)

    def invalidate_path(self, path: Path):
        resolved = Path(path)
        with self._lock:
            for kind in ("json", "csv"):
                self._entries.pop((kind, resolved), None)
