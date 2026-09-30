#!/usr/bin/env bash
# Serves the product on its pinned pair and proves it runs: the bootstrap names
# the product, the runtime stays Running while the player's update count
# advances, and the generator's live fingerprint is the one recorded for its
# version.
#   scripts/ci-serve-product.sh <product.csproj> <port>
set -euo pipefail

project=$1
port=$2
origin=http://127.0.0.1:$port
log=${RUNNER_TEMP:-/tmp}/craftsurvive-serve.log

rusty dev --project "$project" --live-debug --bind-host 127.0.0.1 --port "$port" > "$log" 2>&1 &
dev=$!
stop() {
    kill -INT "$dev" 2>/dev/null || true
    wait "$dev" 2>/dev/null || true
}
trap stop EXIT

execute() {
    curl -sf -X POST -H 'content-type: text/plain; charset=utf-8' --data "$1" "$origin/__rusty/product/runtime/debug/execute"
}

served=false
for _ in $(seq 1 180); do
    if curl -sf "$origin/product-bootstrap.json" | grep -q '"id":"rusty-craftsurvive"'; then
        served=true
        break
    fi
    kill -0 "$dev" 2>/dev/null || break
    # The supervisor stays up after its restart budget is spent; stop waiting then.
    grep -q '"event":"paused-fault"' "$log" && break
    sleep 1
done
if [[ "$served" != true ]]; then
    echo "The product did not serve." >&2
    cat "$log" >&2
    exit 1
fi

updates() {
    local readout
    readout=$(execute craft.player.readout) || return 1
    [[ "$readout" =~ ^updates=([0-9]+) ]] || return 1
    echo "${BASH_REMATCH[1]}"
}

first=""
for _ in $(seq 1 60); do
    if first=$(updates) && ((first > 0)); then
        break
    fi
    sleep 1
done
sleep 2
second=$(updates || echo 0)
runtime=$(execute craft.runtime || echo unavailable)
generation=$(execute craft.terrain.generation || echo unavailable)
echo "updates ${first:-0} -> ${second}; ${runtime}; ${generation}"
if [[ "$runtime" != state=Running* ]] || ((second <= ${first:-0})); then
    echo "The product did not keep updating." >&2
    cat "$log" >&2
    exit 1
fi
if [[ "$generation" != *golden=match* ]]; then
    echo "The generator's live fingerprint is not the one recorded for its version." >&2
    exit 1
fi
