#!/usr/bin/env bash
# Serves the product on its pinned pair and proves it started and updates:
# the bootstrap names the product and the player readout counts updates.
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
    sleep 1
done
if [[ "$served" != true ]]; then
    echo "The product did not serve." >&2
    cat "$log" >&2
    exit 1
fi

for _ in $(seq 1 60); do
    if readout=$(execute craft.player.readout) && [[ "$readout" =~ ^updates=([0-9]+) ]] && ((BASH_REMATCH[1] > 0)); then
        echo "Served and updating: ${readout%%;*}"
        exit 0
    fi
    sleep 1
done
echo "The product served but never reported an update." >&2
cat "$log" >&2
exit 1
