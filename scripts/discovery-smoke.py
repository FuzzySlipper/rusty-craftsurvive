#!/usr/bin/env python3
"""Exercise the discovery module end to end against a live product.

The lane of doubles proves the pieces - state, rules, codec - but not the wiring: Update
collecting sites, noticing, saving through the Engine's store, and publishing to the UI stream.
That path only exists in a running product, so it is checked here rather than in a lane, and the
reviewer who asked for module-level coverage named exactly this gap.

Usage: discovery-smoke.py [port]
"""
import os, re, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORT = sys.argv[1] if len(sys.argv) > 1 else "37561"
ORIGIN = f"http://127.0.0.1:{PORT}"
PACK = f"{ROOT}/.runtime/pair-1aecde636cd3/runtime-pack"
PLAYWRIGHT = "/home/agent/.local/share/crew-playtest/browser/node_modules/playwright"
CHROMIUM = "/home/agent/.local/share/crew-playtest/bin/chromium-local"


def exec_(command):
    return subprocess.run(
        ["curl", "-s", "-m", "12", "-X", "POST", "-H", "content-type: text/plain; charset=utf-8",
         "--data", command, f"{ORIGIN}/__rusty/product/runtime/debug/execute"],
        capture_output=True, text=True).stdout


def hold(keys, seconds):
    subprocess.run(
        ["node", f"{ROOT}/scripts/playwright-attack-key.cjs"],
        env=dict(os.environ, PLAYWRIGHT_MODULE=PLAYWRIGHT, PRODUCT_ORIGIN=ORIGIN,
                 CHROMIUM_PATH=CHROMIUM, HOLD_KEYS=keys, ATTACK_KEY="j",
                 PRESSES=str(max(2, int(seconds * 4))), GAP_MS="250"),
        capture_output=True, text=True, timeout=150, cwd=ROOT)


def journal():
    return exec_("craft.discovery.readout").strip()


def stored_bytes(line):
    m = re.search(r"stored=\w+/(\d+)", line)
    return int(m.group(1)) if m else None


def main():
    server = subprocess.Popen(
        [f"{PACK}/bin/rusty", "dev", "--runtime", PACK,
         "--project", f"{ROOT}/src/CraftSurvive.Game/CraftSurvive.Game.csproj",
         "--live-debug", "--debugger", "--bind-host", "127.0.0.1", "--port", PORT],
        cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        for _ in range(45):
            time.sleep(2)
            if subprocess.run(["curl", "-sf", "-o", "/dev/null", f"{ORIGIN}/product-ui/main.js"]).returncode == 0:
                break
        else:
            print("FAIL: the product did not come up")
            return 1

        time.sleep(14)
        before = journal()
        print("before:", before[:130])

        # Stand a few metres from a real site and walk onto it. The teleport is a debug command to
        # a stand-off, not onto the target: the walk is what earns the fact.
        row = exec_("craft.discovery.find StandingStones 3000")
        m = re.search(r"StandingStones@(-?\d+),(-?\d+) d=[\d.]+ ground=(\d+)", row)
        if not m:
            print("FAIL: no standing stones listed within 3 km")
            return 1
        x, z, ground = int(m.group(1)), int(m.group(2)), int(m.group(3))
        exec_(f"craft.player.teleport {x} {ground + 1} {z - 7}")
        hold("w", 2.5)
        after = journal()
        print("after: ", after[:130])

        places_before = int(re.search(r"places=(\d+)", before).group(1))
        places_after = int(re.search(r"places=(\d+)", after).group(1))
        size = stored_bytes(after)
        checks = [
            (places_after > places_before, f"walking must discover the place: {places_before} -> {places_after}"),
            (size is not None and (size - 32) % 51 == 0, f"the stored journal must be 32 + 51n bytes, was {size}"),
            ("restore=" in after, "the readout must report what restore did"),
        ]
        ok = True
        for passed, message in checks:
            print(("  ok   " if passed else "  FAIL ") + message)
            ok &= passed
        print("discovery smoke: " + ("passed" if ok else "FAILED"))
        return 0 if ok else 1
    finally:
        server.terminate()
        try:
            server.wait(timeout=20)
        except subprocess.TimeoutExpired:
            server.kill()


if __name__ == "__main__":
    sys.exit(main())
