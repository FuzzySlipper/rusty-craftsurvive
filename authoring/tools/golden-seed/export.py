#!/usr/bin/env python3
"""Export the golden seed's generated world from the running game (#11125).

Starts a headless host of the current product (its own `rusty dev` instance, beside any other), has it
generate the world golden-seed.json names, reads every layer through the product's craft.export.* debug
commands, and writes them to the export folder (default authoring/assets/golden-seed/, ignored and
regenerable). The old export is replaced only once the new one is complete. The CMS atlas reads it.

    python3 authoring/tools/golden-seed/export.py [--config FILE] [--out DIR] [--origin URL] [--keep-host]

--origin uses a host that is already running with --live-debug instead of starting one (the world is still
regenerated in it). Every value comes from the product's own generator in that host: map fields, the
walked drainage, lakes, the towns as the game builds them, and places, crossings and caves decided by the
Engine's keyed draws. The host's dev world becomes the golden world (dev saves are disposable).
"""

from __future__ import annotations

import argparse
import base64
import json
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

TOOL = Path(__file__).resolve().parent
REPO = TOOL.parents[2]
PROJECT = REPO / "src/CraftSurvive.Game/CraftSurvive.Game.csproj"
DEFAULT_CONFIG = TOOL / "golden-seed.json"
DEFAULT_OUT = REPO / "authoring/assets/golden-seed"
INSTANCE = "golden-seed"
LABEL = "golden-seed-export"
EXECUTE = "/__rusty/product/runtime/debug/execute"
# The host stops itself after this many idle minutes if the export dies without stopping it.
IDLE_MINUTES = 15
WORLD_TIMEOUT_S = 600
TOWNS_TIMEOUT_S = 60
POLL_S = 2.0
# A command answer can take a region tile's build (about 30 s on den-agents) or more on a loaded machine.
COMMAND_TIMEOUT_S = 600
RASTER_ROWS = 64
RASTER_LAYERS = {"elevation": "f32", "temperature": "f32", "moisture": "f32", "rock": "f32", "biome": "u8", "water": "u8"}
FORMAT_VERSION = 1


def log(message: str) -> None:
    print(message, flush=True)


class Host:
    """A running product host with live debug: started here (and stopped here), or one given by --origin."""

    def __init__(self, origin: str | None, keep: bool):
        self.started = origin is None
        self.keep = keep
        self.origin = origin.rstrip("/") if origin else self.start()

    def start(self) -> str:
        log(f"starting a headless host (rusty dev instance {INSTANCE})")
        command = ["rusty", "dev", "start", "--project", str(PROJECT), "--instance", INSTANCE, "--live-debug",
                   "--headless", "--bind-host", "127.0.0.1", "--label", LABEL, "--idle-timeout", str(IDLE_MINUTES)]
        process = subprocess.run(command, cwd=REPO, capture_output=True, text=True)
        if process.returncode:
            raise SystemExit(f"rusty dev start failed:\n{process.stderr or process.stdout}")
        record = json.loads(process.stdout.strip().splitlines()[-1])
        return record["url"].rstrip("/")

    def stop(self) -> None:
        if self.started and not self.keep:
            subprocess.run(["rusty", "dev", "stop", "--project", str(PROJECT), "--instance", INSTANCE],
                           cwd=REPO, capture_output=True, text=True)
            log("stopped the host")

    def exec(self, command: str) -> str:
        request = urllib.request.Request(self.origin + EXECUTE, data=command.encode("utf-8"), method="POST",
                                         headers={"content-type": "text/plain; charset=utf-8"})
        try:
            with urllib.request.urlopen(request, timeout=COMMAND_TIMEOUT_S) as response:
                text = response.read().decode("utf-8").strip()
        except urllib.error.HTTPError as error:
            raise SystemExit(f"{command} answered HTTP {error.code}: {error.read()[:300]!r} (is the host running with --live-debug?)")
        if text.startswith("refused"):
            raise SystemExit(f"{command}: {text}")
        return text

    def json(self, command: str) -> dict:
        return json.loads(self.exec(command))


def field(readout: str, name: str) -> str | None:
    match = re.search(rf"(?:^|;){name}=([^;]*)", readout)
    return match.group(1) if match else None


def generate(host: Host, seed: int, size: int) -> None:
    """Has the host generate the golden world afresh (never a restored copy, which an older generator made)."""
    # A host just started is still generating or restoring its first world, and refuses a new one until it has it.
    deadline = time.monotonic() + WORLD_TIMEOUT_S
    before = host.exec("craft.world.map")
    while field(before, "seed") is None:
        if time.monotonic() >= deadline:
            raise SystemExit(f"the host had no world within {WORLD_TIMEOUT_S} s: {before}")
        time.sleep(POLL_S)
        before = host.exec("craft.world.map")
    generation = field(before, "generation")
    log(f"generating seed {seed} at {size} m (host had {field(before, 'seed')} at {field(before, 'size')} m)")
    host.exec(f"craft.world.create {seed} {size}")
    while time.monotonic() < deadline:
        time.sleep(POLL_S)
        readout = host.exec("craft.world.map")
        if field(readout, "seed") == str(seed) and field(readout, "size") == str(size) and field(readout, "generation") != generation:
            return
    raise SystemExit(f"the world was not generated within {WORLD_TIMEOUT_S} s")


def wait_for_towns(host: Host, continental: bool) -> dict:
    """The towns are placed once the world is admitted; a continent always has some, a regional world none."""
    deadline = time.monotonic() + (TOWNS_TIMEOUT_S if continental else 0)
    while True:
        places = host.json("craft.export.places")
        if places["towns"] or time.monotonic() >= deadline:
            return places
        time.sleep(POLL_S)


def paged(host: Host, command: str) -> list:
    first = host.json(f"{command} 0")
    items = list(first["reaches"])
    for page in range(1, first["pages"]):
        items.extend(host.json(f"{command} {page}")["reaches"])
    return items


def slug(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


def git(*args: str) -> str:
    process = subprocess.run(["git", *args], cwd=REPO, capture_output=True, text=True)
    return process.stdout.strip()


def engine_pair() -> str | None:
    match = re.search(r"<RustyEnginePackageVersion>([^<]+)<", (REPO / "Directory.Build.props").read_text(encoding="utf-8"))
    return match.group(1) if match else None


def export(host: Host, config: dict, out: Path) -> Path:
    seed, size, pixels = int(config["seed"]), int(config["size"]), int(config.get("pixels", 1024))
    timings: dict[str, float] = {}
    clock = time.monotonic()
    generate(host, seed, size)
    timings["generate"] = time.monotonic() - clock

    work = out.with_name(out.name + ".partial")
    shutil.rmtree(work, ignore_errors=True)
    (work / "continent").mkdir(parents=True)
    (work / "windows").mkdir()

    header = host.json("craft.export.header")
    if header["seed"] != seed or header["size"] != size:
        raise SystemExit(f"the host's world is {header['seed']} at {header['size']} m, not the golden seed")

    clock = time.monotonic()
    places = wait_for_towns(host, header["continental"])
    (work / "places.json").write_text(json.dumps(places), encoding="utf-8")
    timings["places"] = time.monotonic() - clock

    clock = time.monotonic()
    layers = {name: bytearray() for name in RASTER_LAYERS}
    for first in range(0, pixels, RASTER_ROWS):
        page = host.json(f"craft.export.raster {pixels} {first} {min(RASTER_ROWS, pixels - first)}")
        for name in RASTER_LAYERS:
            layers[name] += base64.b64decode(page[name])
        log(f"continent raster rows {first + page['rows']}/{pixels}")
    for name, kind in RASTER_LAYERS.items():
        (work / "continent" / f"{name}.{kind}").write_bytes(layers[name])
    timings["raster"] = time.monotonic() - clock

    clock = time.monotonic()
    (work / "map-rivers.json").write_text(json.dumps({"reaches": paged(host, "craft.export.rivers")}), encoding="utf-8")
    drainage = paged(host, "craft.export.drainage")
    (work / "drainage.json").write_text(json.dumps({"reaches": drainage}), encoding="utf-8")
    log(f"rivers: {header['mapRivers']} map reaches, {len(drainage)} walked reaches")
    timings["rivers"] = time.monotonic() - clock

    spec = config.get("windows", {})
    half, step = int(spec.get("half", 4096)), int(spec.get("step", 16))
    wanted = [{"name": town["name"], "x": town["x"], "z": town["z"], "town": town["key"]}
              for town in places["towns"]] if spec.get("towns", True) else []
    wanted += [dict(window) for window in spec.get("extra", [])]
    windows = []
    clock = time.monotonic()
    for window in wanted:
        name = slug(window["name"])
        started = time.monotonic()
        detail = host.json(f"craft.export.window {round(window['x'])} {round(window['z'])} {half} {step}")
        (work / "windows" / f"{name}.height.i16").write_bytes(base64.b64decode(detail.pop("height")))
        (work / "windows" / f"{name}.biome.u8").write_bytes(base64.b64decode(detail.pop("biome")))
        detail |= {"id": name, "name": window["name"], "town": window.get("town")}
        (work / "windows" / f"{name}.json").write_text(json.dumps(detail), encoding="utf-8")
        windows.append({key: detail[key] for key in ("id", "name", "town", "x", "z", "half", "step", "side")}
                       | {"places": len(detail["places"]), "crossings": len(detail["crossings"]),
                          "caves": sum(1 for place in detail["places"] if "cave" in place)})
        log(f"window {window['name']}: {len(detail['places'])} places, {time.monotonic() - started:.1f} s")
    timings["windows"] = time.monotonic() - clock

    manifest = {
        "format": FORMAT_VERSION,
        "world": header,
        "config": config,
        "exportedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "source": {"commit": git("rev-parse", "HEAD"), "dirty": bool(git("status", "--porcelain", "--", "src", "content/game")),
                   "enginePair": engine_pair()},
        "continent": {"pixels": pixels, "metresPerPixel": header["radius"] * 2 / pixels, "layers": RASTER_LAYERS,
                      "origin": "north-west corner (-radius, -radius); rows run +Z (south), columns +X (east); little-endian"},
        "windows": windows,
        "timings": {name: round(seconds, 1) for name, seconds in timings.items()},
    }
    (work / "manifest.json").write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")

    previous = out.with_name(out.name + ".previous")
    shutil.rmtree(previous, ignore_errors=True)
    if out.exists():
        out.rename(previous)
    work.rename(out)
    shutil.rmtree(previous, ignore_errors=True)
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    parser.add_argument("--origin", help="a running host with live debug to use instead of starting one")
    parser.add_argument("--keep-host", action="store_true", help="leave the started host running afterwards")
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding="utf-8"))
    started = time.monotonic()
    host = Host(args.origin, args.keep_host)
    try:
        out = export(host, config, args.out.resolve())
    finally:
        host.stop()
    log(f"exported seed {config['seed']} at {config['size']} m to {out} in {time.monotonic() - started:.0f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
