#!/usr/bin/env bash
# Runs GitHub CI's `product` job on this machine, from a clean checkout of one commit:
# the Release build, `rusty build`, the texture audit, and scripts/ci-serve-product.sh
# rendering on llvmpipe (Mesa's software Vulkan driver), as the GPU-less runner does.
#
#   scripts/check-product.sh [--ref <commit>] [--managed] [--propose [<version>]] [--keep]
#
#   --ref       the commit to check (default HEAD); uncommitted work is never included
#   --managed   also run every tests/ lane and build MapLab, as the engine-pair workflow does
#   --propose   first move the checkout's pin with `rusty update` (to <version>, else the newest
#               published pair), as the engine-pair workflow does; the pin is never committed here
#   --keep      keep the temporary checkout and its logs
#
# LP_NUM_THREADS (default 4, a GitHub runner's vCPU count) bounds llvmpipe's threads, since the
# machine running this is usually shared. The host is stopped on exit whatever happens.
# Temporary checkouts go under CHECK_PRODUCT_DIR (default ~/.cache/craftsurvive-check-product).
set -euo pipefail

ref=HEAD
managed=false
propose=false
propose_version=""
keep=false
while (($#)); do
    case $1 in
        --ref) ref=$2; shift 2 ;;
        --managed) managed=true; shift ;;
        --propose)
            propose=true
            if [[ ${2:-} && $2 != --* ]]; then propose_version=$2; shift; fi
            shift ;;
        --keep) keep=true; shift ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

readonly PROJECT=src/CraftSurvive.Game/CraftSurvive.Game.csproj
readonly LAVAPIPE_ICD=/usr/share/vulkan/icd.d/lvp_icd.json
readonly DEFAULT_LLVMPIPE_THREADS=4

repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
commit=$(git -C "$repo" rev-parse --verify "$ref^{commit}")
for tool in rusty dotnet pnpm node curl; do
    command -v "$tool" >/dev/null || { echo "$tool is not on PATH." >&2; exit 1; }
done
[[ -f $LAVAPIPE_ICD ]] || { echo "No llvmpipe Vulkan driver at $LAVAPIPE_ICD (install mesa-vulkan-drivers)." >&2; exit 1; }

# On the home filesystem: pnpm keeps a store per filesystem, and one in /tmp cannot switch pnpm versions.
work_root=${CHECK_PRODUCT_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/craftsurvive-check-product}
mkdir -p "$work_root"
work=$(mktemp -d "$work_root/run.XXXXXX")
checkout=$work/checkout
logs=$work/logs
mkdir -p "$logs"

cleanup() {
    local status=$?
    # ci-serve-product.sh stops its own host; make sure no session of this checkout outlives it.
    if [[ -d $checkout ]]; then
        rusty dev list 2>/dev/null | awk -v dir="$checkout/" 'index($0, dir) { print $1 }' |
            while read -r id; do rusty dev stop "$id" >/dev/null 2>&1 || true; done
    fi
    if [[ $keep == true || $status -ne 0 ]]; then
        echo "Checkout and logs kept in $work"
    else
        git -C "$repo" worktree remove --force "$checkout" 2>/dev/null || true
        rm -rf "$work"
    fi
}
trap cleanup EXIT

step() {
    local name=$1; shift
    local started=$SECONDS
    echo "== $name"
    if ! "$@" > "$logs/$name.log" 2>&1; then
        echo "FAILED: $name ($((SECONDS - started)) s); last lines of $logs/$name.log:" >&2
        tail -n 40 "$logs/$name.log" >&2
        exit 1
    fi
    echo "   ok ($((SECONDS - started)) s)"
}

pin() {
    sed -n 's|.*<RustyEnginePackageVersion>\([^<]*\)</RustyEnginePackageVersion>.*|\1|p' Directory.Build.props
}

managed_checks() {
    local check
    for check in tests/*/*.csproj; do
        echo "-- $check"
        dotnet run --project "$check" -c Release
    done
    dotnet build authoring/tools/MapLab -c Release
}

# rusty dev refuses ports in the ephemeral range (32768 and up) and 30300-30450 is den-serve's, so
# take a free one from the range between.
readonly PORT_LOW=31000 PORT_HIGH=32767
free_port() {
    node -e '
        const net = require("net");
        const [low, high] = process.argv.slice(1).map(Number);
        const order = Array.from({ length: high - low + 1 }, (_, i) => low + i).sort(() => Math.random() - 0.5);
        (function next(i) {
            if (i >= order.length) { console.error("no free port"); process.exit(1); }
            const server = net.createServer();
            server.once("error", () => next(i + 1));
            server.listen(order[i], "127.0.0.1", () => { console.log(order[i]); server.close(); });
        })(0);' "$PORT_LOW" "$PORT_HIGH"
}

echo "Checking $commit in $checkout"
git -C "$repo" worktree add --detach --quiet "$checkout" "$commit"
cd "$checkout"
step checkout git submodule update --init --recursive

if [[ $propose == true ]]; then
    from=$(pin)
    step propose rusty update ${propose_version:+--to "$propose_version"}
    echo "   pin $from -> $(pin)"
fi
echo "   pair $(pin)"

step install rusty install
step ui-dependencies pnpm install --frozen-lockfile --ignore-scripts
step release-build dotnet build src/CraftSurvive.Game -c Release
step rusty-build rusty build --project "$PROJECT"
step audit-textures pnpm run audit:textures
if [[ $managed == true ]]; then
    step managed-checks managed_checks
fi

port=$(free_port)
export RUNNER_TEMP=$logs
export WGPU_BACKEND=vulkan
export VK_DRIVER_FILES=$LAVAPIPE_ICD VK_ICD_FILENAMES=$LAVAPIPE_ICD
export LP_NUM_THREADS=${LP_NUM_THREADS:-$DEFAULT_LLVMPIPE_THREADS}
step serve scripts/ci-serve-product.sh "$PROJECT" "$port"
grep -E '^updates |^\{' "$logs/serve.log" | sed 's/^/   /' || true

echo "PASS: product check of $commit on pair $(pin)"
