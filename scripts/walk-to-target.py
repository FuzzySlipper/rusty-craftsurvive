#!/usr/bin/env python3
"""Walk the live player to a target, closing on the readout's own distance.

Two open-loop attempts failed before this: aiming by a target's coordinates and holding keys
for distance/speed overshoots, because the player's exact position and walking speed are not
what the plan assumes. This closes the loop instead - read the distance, take a bounded step,
read again - and it damps the step as the target nears, because a fifty-metre stride is larger
than the distance remaining once you are thirty metres away.

Usage: walk-to-target.py <origin> <crossing|entrance|site> [stop-distance]
"""
import os, re, subprocess, sys, time

ORIGIN = sys.argv[1]
KIND = sys.argv[2] if len(sys.argv) > 2 else "site"
STOP = float(sys.argv[3]) if len(sys.argv) > 3 else (3.0 if KIND == "crossing" else 20.0)
SPEED = 5.5
STEP_FAR, STEP_NEAR = 9.0, 2.5
NEAR = 30.0
KIND_NAMES = {"entrance": "DungeonEntrance", "site": "", "crossing": ""}
KINDS = {"crossing": "crossing:[\\d-]+:[\\d-]+", "entrance": "DungeonEntrance", "site": "\\w+"}


def exec_(command):
    return subprocess.run(
        ["curl", "-s", "-m", "12", "-X", "POST", "-H", "content-type: text/plain; charset=utf-8",
         "--data", command, f"{ORIGIN}/__rusty/product/runtime/debug/execute"],
        capture_output=True, text=True).stdout


def rows():
    """(id, x, z, distance) for the nearest target of the wanted kind."""
    if KIND == "crossing":
        out = exec_("craft.discovery.crossings 400").split(":", 1)[-1]
        best = None
        for row in out.split(";"):
            m = re.search(r"(crossing:[\d-]+:[\d-]+) span=\d+ deck=\d+ from=(-?\d+),(-?\d+) to=(-?\d+),(-?\d+) d=([\d.]+)", row)
            if m:
                cx, cz, d = (int(m.group(2)) + int(m.group(4))) / 2, (int(m.group(3)) + int(m.group(5))) / 2, float(m.group(6))
                if best is None or d < best[3]:
                    best = (m.group(1), cx, cz, d)
        return best
    out = exec_(f"craft.discovery.find {KIND_NAMES.get(KIND, KIND)} 3000").split(":", 1)[-1] if KIND == "entrance" else exec_("craft.discovery.near 3000").split(":", 1)[-1]
    best = None
    for row in out.split(";"):
        m = re.search(r"(\w+)@(-?\d+),(-?\d+) d=([\d.]+)", row)
        if m and (KIND == "site" or m.group(1) == KIND_NAMES.get(KIND, KIND)):
            d = float(m.group(4))
            if best is None or d < best[3]:
                best = (m.group(1), float(m.group(2)), float(m.group(3)), d)
    return best


def hold(key, seconds):
    """Press one movement key, and report what the lane actually did.

    The result is parsed and returned rather than discarded. Five increments of walking failures
    were made harder to diagnose by throwing this away: the loop would compute a step, the
    distance would not move, and nothing said whether the key press ever happened.
    """
    if seconds < 0.4:
        return None
    presses = max(2, int(seconds * 4))
    run = subprocess.run(
        ["node", "scripts/playwright-attack-key.cjs"],
        env=dict(os.environ,
                 PLAYWRIGHT_MODULE="/home/agent/.local/share/crew-playtest/browser/node_modules/playwright",
                 PRODUCT_ORIGIN=ORIGIN, CHROMIUM_PATH="/home/agent/.local/share/crew-playtest/bin/chromium-local",
                 HOLD_KEYS=key, ATTACK_KEY="j", PRESSES=str(presses), GAP_MS="250"),
        capture_output=True, text=True, timeout=150)
    report = (run.stdout or "").strip().split("\n")[-1] if run.stdout else ""
    match = re.search(r'"presses":\s*(\d+)', report)
    issued = int(match.group(1)) if match else 0
    errors = re.search(r'"pageErrors":\s*\[([^\]]*)\]', report)
    if issued == 0:
        print(f"    HOLD FAILED key={key} wanted={presses} exit={run.returncode} report={report[:120]!r} stderr={(run.stderr or '').strip()[-120:]!r}")
    elif errors and errors.group(1).strip():
        print(f"    hold key={key} presses={issued} pageErrors={errors.group(1)[:80]}")
    return issued


def main():
    time.sleep(16)
    sign = [1, 1]
    print("start journal:", exec_("craft.discovery.readout").strip()[:120])
    for attempt in range(18):
        target = rows()
        if target is None:
            print("  no target of that kind in range")
            return 1
        ident, tx, tz, distance = target
        print(f"  attempt {attempt}: {ident} at ({tx:.0f},{tz:.0f}) d={distance}")
        if distance <= STOP:
            print("  arrived")
            return 0
        step = min(STEP_FAR if distance > NEAR else STEP_NEAR, distance / SPEED)
        for axis, (positive, negative) in enumerate((("d", "a"), ("w", "s"))):
            target_coordinate = tx if axis == 0 else tz
            if abs(target_coordinate) < 3:
                continue
            key = positive if (target_coordinate > 0) == (sign[axis] > 0) else negative
            before = distance
            hold(key, step)
            after = rows()
            if after is not None and after[3] > before + 2.0:
                # The step went the wrong way, so the guess about which side the player is on
                # was wrong: undo it and remember the correction for the rest of the walk.
                hold(negative if key == positive else positive, step * 2)
                sign[axis] = -sign[axis]
    return 1


if __name__ == "__main__":
    sys.exit(main())
