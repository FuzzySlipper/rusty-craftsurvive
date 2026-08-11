#!/usr/bin/env bash
set -euo pipefail

CRAFT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CRAFT_HOST=""
CRAFT_PORT=""
CRAFT_SURFACE="${RUSTY_CRAFTSURVIVE_SURFACE:-box}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --) shift ;;
    --host) CRAFT_HOST="${2:-}"; shift 2 ;;
    --port) CRAFT_PORT="${2:-}"; shift 2 ;;
    *) echo "unknown serve-den argument: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$CRAFT_HOST" ]] || { echo "--host is required" >&2; exit 2; }
[[ "$CRAFT_PORT" =~ ^[0-9]+$ ]] && (( CRAFT_PORT >= 1 && CRAFT_PORT <= 65535 )) \
  || { echo "--port must be an integer from 1 through 65535" >&2; exit 2; }

cd "$CRAFT_ROOT"
[[ -x node_modules/.bin/vite ]] \
  || { echo "browser dependencies are missing; run pnpm install --frozen-lockfile" >&2; exit 1; }
pnpm build
exec cargo run --release --locked --bin browser-host -- \
  --host "$CRAFT_HOST" --port "$CRAFT_PORT" --surface "$CRAFT_SURFACE" --web-root web/dist
